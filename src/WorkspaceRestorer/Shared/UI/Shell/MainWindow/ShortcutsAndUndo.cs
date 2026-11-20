using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>
        /// 【公共快捷键】把“键位”和“动作”接起来 —— 这里是**唯一**的绑定处。
        ///
        /// 用户要求（2026-10-17）：“快捷键的代码要放到公共文件夹内，能够让：启动与恢复，图标位置保存，
        /// 自启动应用，回收站等都能用上，是作用于软件的全局，……命名好，后续修改的时候也好修改。”
        ///
        /// 所以：键位表（按哪个键、设置页显示什么）在 `Shared/UI/Shell/ShortcutCatalog.cs`；
        /// 这里只写“这个命令做哪件事”。**加一个新快捷键**＝在目录里加一条 + 在这里 Bind 一行。
        /// 四个工作区共用同一份撤销栈，所以 Ctrl+Z 在哪个页面都能撤（撤销栈见 `Shared/UI/Shell/UndoStack.cs`）。
        /// </summary>
        private void WireGlobalShortcuts()
        {
            // 撤销/重做的结果统一写到最上面那行状态文字里（包含“哪一步 / 哪个工作区”）。
            // 【为什么要再排一拍】控制器自己的状态消息是用 `Dispatcher.BeginInvoke`（Normal 优先级）
            // **稍后**才写进状态栏的（见 OnControllerPropertyChanged → RefreshProgress）。
            // 只改界面上那行字的话，撤销动作里控制器留下的旧消息会在下一拍把结论盖掉
            //（实测：撤销“追加当前时间”后状态栏显示的是“工作方案已重命名为：甲”，
            // 用户看到的就像“按了没反应”）。这里立刻写一次，再用**更低优先级**补写一次，
            // 保证最后停在状态栏上的一定是撤销/重做的结论。
            undoStack.StatusSink = delegate(string text)
            {
                statusText.Text = text;
                try
                {
                    if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    {
                        Dispatcher.BeginInvoke(
                            DispatcherPriority.Background,
                            new Action(delegate
                            {
                                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                                {
                                    statusText.Text = text;
                                }
                            }));
                    }
                }
                catch
                {
                    // 关闭过程中排队失败无所谓，界面马上就不在了。
                }
            };
            shortcuts.DescribeContext = delegate { return activeWorkspaceName; };
            shortcuts.IsTextEditingFocus = IsTextEditingFocus;
            // 拖动列表项的时候不允许撤销/重做：那会在拖动状态机里把数据和界面撕开。
            // 三个工作区现在都走公共引擎，直接问引擎“还活着吗”。
            shortcuts.IsDragInProgress = delegate
            {
                return (startupItemDragSession != null && startupItemDragSession.IsActive)
                    || (startupCardDragSession != null && startupCardDragSession.IsActive)
                    || ApplicationDragInProgress
                    || (layoutCardDragSession != null && layoutCardDragSession.IsActive);
            };
            // 快捷键真正执行后显示公共浮窗；同一个键连按只显示第一次，
            // 换键立即改文字，点击任意处/6 秒后消失。
            shortcuts.Executed = delegate(Shared.ShortcutCatalog.Shortcut shortcut)
            {
                shortcutFeedbackToast.Show(shortcut);
            };
            shortcuts.Bind("copy-profile", CopyActiveProfileToClipboard);
            shortcuts.Bind("paste-profile", PasteProfileFromClipboard);
            shortcuts.Bind("undo", UndoLastAction);
            shortcuts.Bind("redo", RedoLastAction);

        }

        private void OnMainWindowKeyDown(object sender, KeyEventArgs e)
        {
            // 恢复卡片上的 Enter / Esc 是“卡片按钮”的键盘等价物，不进入可配置快捷键表：
            //   Enter = 立即恢复（倒计时中）/ 继续恢复（CPU、内存超限提示中）
            //   Esc   = 取消恢复（两个流程一起停，和卡片上的“取消恢复”完全一样）
            // 只认没有 Ctrl/Alt/Shift 的按键，避免和用户自定义快捷键抢。
            if (HandleRestoreCardKey(e))
            {
                return;
            }
            // 所有全局快捷键都由公共路由处理（键位表见 ShortcutCatalog，判断见 GlobalShortcutRouter）。
            // 这里不再写任何 `e.Key == Key.Z` 之类的判断：加/改快捷键只改公共区域那一处。
            shortcuts.Handle(e);
        }

        /// <summary>
        /// 恢复卡片（含 CPU/内存超限浮窗）的 Enter / Esc。
        /// 只在卡片确实显示、并且按键没有修饰键时生效；输入框里有焦点时让给输入框。
        /// 这不是全局键盘钩子，焦点不在主窗口时不会触发。
        /// </summary>
        private bool HandleRestoreCardKey(KeyEventArgs e)
        {
            if (e == null || e.Handled || IsTextEditingFocus())
            {
                return false;
            }
            if (Keyboard.Modifiers != ModifierKeys.None)
            {
                return false;
            }
            bool restoreVisible = restoreOverlay != null
                && restoreOverlay.Visibility == Visibility.Visible;
            bool loadPromptVisible = loadLimitPrompt != null
                && loadLimitPrompt.Visibility == Visibility.Visible;
            if (!restoreVisible && !loadPromptVisible)
            {
                return false;
            }
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                controller.CancelRestore();
                controller.CancelApplicationsStart();
                e.Handled = true;
                return true;
            }
            if (key == Key.Return)
            {
                if (loadPromptVisible && controller.LoadPromptVisible)
                {
                    controller.ContinueRestoreDespiteLoad();
                    e.Handled = true;
                    return true;
                }
                if (controller.CountdownSeconds > 0)
                {
                    controller.SkipRestoreCountdown();
                    e.Handled = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 【全局快捷键 · 命令翻译之前的那一层】焦点在输入框里时的兜底。
        ///
        /// 为什么必须有它：WPF 的 TextBox 把 Ctrl+Z 注册成了**类级输入绑定**
        /// （ApplicationCommands.Undo），这个键在“命令翻译”阶段就被输入框收走了 ——
        /// 比窗口的 PreviewKeyDown / KeyDown 都早，所以窗口里那份判断根本轮不到执行。
        /// 而本程序的输入框没有开文本撤销（默认的撤销历史在“程序设置文本”之后基本是空的），
        /// 结果就是：**光标停在任何一个输入框里时，Ctrl+Z 完全没反应**。
        /// 用户 2026-10-17 报的“自启动应用使用撤销快捷键却无法撤销”，有一半就是这个
        /// （点进命名框/搜索框之后再也撤不动）。
        ///
        /// 规则（简单、可预期）：
        ///   · 输入框里**还有**可撤销的输入（刚打了字）→ 让给输入框，用户想撤的是自己刚打的字；
        ///   · 输入框里没有可撤销的输入 → 按全局撤销/重做执行（否则这个键就白按了）。
        /// 键位判断仍然只在 <see cref="WorkspaceRestorer.UI.Shared.GlobalShortcutRouter"/> 一处。
        /// </summary>
        private void OnWindowPreProcessInputKeys(object sender, System.Windows.Input.PreProcessInputEventArgs e)
        {
            if (e == null || e.StagingItem == null)
            {
                return;
            }
            KeyEventArgs args = e.StagingItem.Input as KeyEventArgs;
            if (args == null)
            {
                return;
            }
            // 【只在这一拍动手：PreviewKeyDown 那一趟】
            // 一次按键会被 WPF 分两趟处理（PreviewKeyDown 一趟、KeyDown 一趟），两趟都会进 PreProcessInput。
            // 只认第一趟：这里把输入标成已处理之后，第二趟就不会再来，绝不会执行两次。
            // （实测：如果只等 KeyDown 那一趟，输入框会把整趟输入提前吃掉，这一层根本看不到按键。）
            bool previewPhase = args.RoutedEvent == Keyboard.PreviewKeyDownEvent;
            if (LogShortcutDiagnostics && previewPhase)
            {
                // 排查用（设环境变量 WORKSPACE_RESTORER_LOG_SHORTCUTS=1 才会写，平时不刷日志）：
                // “命令翻译之前”这一层到底有没有收到这个键、当时焦点在哪。
                AppLog.Write("[快捷键诊断] 收到按键 " + args.Key + "（PreProcessInput 阶段），修饰键="
                    + Keyboard.Modifiers + "，焦点="
                    + (Keyboard.FocusedElement == null ? "（没有控件）" : Keyboard.FocusedElement.GetType().Name)
                    + "，是输入框=" + IsTextEditingFocus());
            }
            if (!previewPhase)
            {
                return;
            }
            // 只有“焦点在输入框里”才需要这一层；其它情况一律交给正常的 KeyDown 流程。
            if (!IsTextEditingFocus())
            {
                return;
            }
            WorkspaceRestorer.UI.Shared.ShortcutCatalog.Shortcut shortcut =
                WorkspaceRestorer.UI.Shared.GlobalShortcutRouter.Resolve(args.Key, Keyboard.Modifiers);
            if (shortcut == null)
            {
                return;
            }
            // Ctrl+C / Ctrl+V 在输入框里必须是普通复制粘贴（由输入框处理，不拦）。
            if (shortcut.SkipWhenEditingText)
            {
                return;
            }
            if (IsTextEditingUndoAvailable())
            {
                // 实测结论（2026-10-17，Round104）：本程序的输入框虽然 IsUndoEnabled=True、CanUndo=True，
                // 但 WPF 的文本撤销执行后**界面文字并不会变**（这些框都是“程序设置过初始文字”的短名称/搜索框）。
                // 也就是说“让给输入框”的结果 = 用户按了 Ctrl+Z 什么也没发生 —— 正是要修的那个 bug。
                // 所以这里不再让：全局撤销优先（用户要的是“作用于软件的全局”），下面的日志只是留证据。
                shortcuts.LogTextEditorHadOwnUndo(shortcut, DescribeTextEditingUndoState());
            }
            else
            {
                shortcuts.LogTakenFromTextEditor(shortcut, DescribeTextEditingUndoState());
            }
            // 输入框会把这个键吃掉但什么也不会发生 → 在这里替它把全局快捷键执行掉。
            shortcuts.Execute(shortcut);
            args.Handled = true;
        }

        /// <summary>日志用：把“输入框还能不能撤销自己的输入”写成一句，排查时不用猜。</summary>
        private static string DescribeTextEditingUndoState()
        {
            try
            {
                System.Windows.Controls.Primitives.TextBoxBase box =
                    Keyboard.FocusedElement as System.Windows.Controls.Primitives.TextBoxBase;
                if (box == null)
                {
                    return "焦点不是输入框";
                }
                return "IsUndoEnabled=" + box.IsUndoEnabled + "，CanUndo=" + box.CanUndo;
            }
            catch
            {
                return "读不到输入框状态";
            }
        }

        /// <summary>当前焦点所在的输入框自己还有没有可撤销的文本输入。</summary>
        private bool IsTextEditingUndoAvailable()
        {
            try
            {
                System.Windows.Controls.Primitives.TextBoxBase box =
                    Keyboard.FocusedElement as System.Windows.Controls.Primitives.TextBoxBase;
                return box != null && box.IsUndoEnabled && box.CanUndo;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Records an undoable edit. Both directions are captured by the caller
        /// so the change can be undone and then redone.
        /// 【公共区域】真正存进 `UndoStack`（全局只有这一份），这里只是转发，顺带写上当前工作区。
        /// </summary>
        private void PushUndo(string description, System.Action undo, System.Action redo)
        {
            undoStack.Push(activeWorkspaceName, description, undo, redo);
        }

        /// <summary>
        /// Undo helper for the common case of "the profile's item list looked
        /// like this before / like that after": covers reordering, adding and
        /// removing entries.
        /// </summary>
        private void PushItemListUndo(
            StartupProfile profile,
            List<SessionItem> before,
            List<SessionItem> after,
            string description)
        {
            if (profile == null || before == null)
            {
                return;
            }
            System.Action<List<SessionItem>> apply = delegate(List<SessionItem> list)
            {
                controller.SetProfileItemsOrder(profile, list);
                RefreshProfileList();
            };
            PushUndo(
                description,
                delegate { apply(before); },
                after == null ? null : (System.Action)delegate { apply(after); });
        }

        private static List<SessionItem> SnapshotItems(StartupProfile profile)
        {
            return profile == null || profile.Items == null
                ? new List<SessionItem>()
                : new List<SessionItem>(profile.Items);
        }

        private void UndoLastAction()
        {
            undoStack.Undo(activeWorkspaceName);
        }

        private void RedoLastAction()
        {
            undoStack.Redo(activeWorkspaceName);
        }

        private static int ParseNumber(string value, int fallback)
        {
            return ParseNumber(value, fallback, 3600);
        }

        private static int ParseNumber(string value, int fallback, int maximum)
        {
            int parsed;
            return int.TryParse(value, out parsed) ? Math.Max(0, Math.Min(maximum, parsed)) : fallback;
        }

        private static double ParseDecimal(string value, double fallback)
        {
            double parsed;
            if (!double.TryParse(
                (value ?? string.Empty).Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed))
            {
                return fallback;
            }
            if (double.IsNaN(parsed))
            {
                return fallback;
            }
            return Math.Max(0, Math.Min(3600, parsed));
        }

        private void OnDesktopDoubleClick(object sender, MouseButtonEventArgs e)
        {
            DesktopEntry entry = desktopList.SelectedItem as DesktopEntry;
            if (entry != null)
            {
                controller.LaunchDesktopEntry(entry);
            }
        }

        private void OnDesktopActionButtonClick(object sender, RoutedEventArgs e)
        {
            Button button = e.OriginalSource as Button;
            if (button == null || !string.Equals(Convert.ToString(button.Tag), "open", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DesktopEntry entry = button.DataContext as DesktopEntry;
            if (entry != null)
            {
                controller.LaunchDesktopEntry(entry);
                e.Handled = true;
            }
        }
    }
}
