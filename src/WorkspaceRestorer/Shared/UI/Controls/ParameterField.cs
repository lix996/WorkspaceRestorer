using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 设置页里“参数框”的公共交互（用户 2026-10-20 要求）。
    ///
    /// 用户原话：“在调试设置内的参数时，全部都要像刚刚修改‘拖动时的滚动速度’时
    /// （双击改为单击……点击一次开始主动修改参数，点击其他地方，则是完成修改）……
    /// 点击空白区域就是完成修改，现在点击空白区域时，还在修改参数的操作上。”
    ///
    /// 之前只有 `SettingsPage.cs` 里的拖动倍率单独挂了“点空白提交”的窗口级钩子，
    /// 启动/自启动应用里的 8 个数字框仍靠 `LostFocus`；而 WPF 里点在 Border/TextBlock
    /// 这类**不接收焦点**的空白处根本不会让 TextBox 失焦 —— 于是数字框一直保留焦点、
    /// 边框一直亮着，看起来像“还在改”。这里把交互集中成一份，所有参数框共用：
    ///
    ///   · 双击参数框才开始改（用户 2026-11-16 要求：其余文字修改也都是双击）；
    ///   · 点空白 / 点别的控件 = 提交，并把焦点从参数框上拿走（边框恢复普通颜色）；
    ///   · 回车 = 提交并结束；Esc = 取消（调用方给了取消回调时）。
    ///
    /// 只做界面行为，不碰业务数据：提交时由调用方决定怎么解析、怎么保存。
    /// </summary>
    internal static class ParameterField
    {
        /// <summary>一个登记过的参数输入框。</summary>
        private sealed class Entry
        {
            public TextBox Editor;
            public Action Commit;
            public Action Cancel;
            public string LastCommittedText;
            public bool Committing;
        }

        /// <summary>一个窗口上的登记表与窗口级鼠标钩子。</summary>
        private sealed class Host
        {
            public readonly List<Entry> Entries = new List<Entry>();
            public bool MouseHooked;
        }

        // 用弱表挂窗口：窗口关掉后登记表跟着释放，不会因为静态字段留住整窗控件。
        private static readonly ConditionalWeakTable<Window, Host> Hosts =
            new ConditionalWeakTable<Window, Host>();

        /// <summary>
        /// 让一段文字**双击**才开始编辑（拖动倍率那种“文字 ↔ 输入框”的控件用）。
        /// </summary>
        public static void BeginEditOnDoubleClick(UIElement surface, Action beginEdit)
        {
            if (surface == null || beginEdit == null)
            {
                return;
            }
            surface.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ChangedButton != MouseButton.Left || args.ClickCount != 2)
                {
                    return;
                }
                args.Handled = true;
                beginEdit();
            };
        }

        /// <summary>登记一个参数输入框（回车提交、失焦提交、点空白提交）。</summary>
        public static void Register(Window owner, TextBox editor, Action commit)
        {
            Register(owner, editor, commit, null);
        }

        /// <summary>同上，并允许 Esc 调用调用方给的取消动作。</summary>
        public static void Register(Window owner, TextBox editor, Action commit, Action cancel)
        {
            if (owner == null || editor == null || commit == null)
            {
                return;
            }
            Host host = GetHost(owner);
            for (int index = 0; index < host.Entries.Count; index++)
            {
                if (object.ReferenceEquals(host.Entries[index].Editor, editor))
                {
                    // 设置页会缓存控件、重新挂到页面上；同一个框只登记一次。
                    return;
                }
            }

            Entry entry = new Entry
            {
                Editor = editor,
                Commit = commit,
                Cancel = cancel,
                LastCommittedText = editor.Text
            };
            host.Entries.Add(entry);

            // 参数框平时只读：双击才进入编辑（用户 2026-11-16 要求与其它文字修改一致）。
            editor.IsReadOnly = true;
            editor.Cursor = Cursors.Arrow;
            editor.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                OnEditorPreviewMouseDown(entry, args);
            };
            editor.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                OnEditorPreviewKeyDown(entry, args);
            };
            editor.LostKeyboardFocus += delegate
            {
                OnEditorLostKeyboardFocus(entry);
            };

            if (!host.MouseHooked)
            {
                host.MouseHooked = true;
                // handledEventsToo=true：控件自己处理掉的点击也要看一眼，
                // 否则点到按钮/卡片上时，参数框不会退出编辑。
                owner.AddHandler(UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnOwnerPreviewMouseDown), true);
            }
        }

        private static Host GetHost(Window owner)
        {
            Host host;
            if (!Hosts.TryGetValue(owner, out host))
            {
                host = new Host();
                Hosts.Add(owner, host);
            }
            return host;
        }

        /// <summary>
        /// 窗口里任何一处按下左键：如果按在参数框外面，就把正在编辑的那个提交掉。
        /// WPF 的 TextBox 不会因为点在不可获焦的空白上而失焦，所以必须在这里主动判断。
        /// </summary>
        private static void OnOwnerPreviewMouseDown(object sender, MouseButtonEventArgs args)
        {
            Window owner = sender as Window;
            if (owner == null || args.ChangedButton != MouseButton.Left)
            {
                return;
            }
            Host host;
            if (!Hosts.TryGetValue(owner, out host))
            {
                return;
            }

            bool clickedRegisteredEditor = false;
            for (int index = 0; index < host.Entries.Count; index++)
            {
                if (host.Entries[index].Editor.IsMouseOver)
                {
                    clickedRegisteredEditor = true;
                    break;
                }
            }

            Entry focused = FindFocused(host);
            if (focused != null && !focused.Editor.IsMouseOver)
            {
                CommitEntry(focused);
            }

            if (clickedRegisteredEditor || focused == null)
            {
                return;
            }
            if (focused.Editor.IsMouseOver)
            {
                // 点的是正在编辑的这个框本身：只是移动光标，不结束编辑。
                return;
            }

            // 等这一轮鼠标处理完再收焦点：
            //   · 点按钮 / 列表时焦点已经自己移走 → 这里什么都不做；
            //   · 点的是不可获焦的空白 → 焦点还留在参数框上，交给窗口，
            //     输入框的强调色边框与光标才会真正消失，看起来才是“改完了”。
            // 只针对按下时正在编辑的那个框：如果这一下打开了另一个参数框（例如
            // 点倍率文字进入编辑），新框会拿到焦点，不能被这里抢走。
            owner.Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    if (focused.Editor.IsKeyboardFocusWithin)
                    {
                        FocusOwner(owner);
                    }
                }),
                DispatcherPriority.Input);
        }

        /// <summary>双击只读参数框才进入编辑；单击只选中/移动光标，不改文字。</summary>
        private static void OnEditorPreviewMouseDown(Entry entry, MouseButtonEventArgs args)
        {
            if (entry == null || args == null
                || args.ChangedButton != MouseButton.Left
                || !entry.Editor.IsReadOnly
                || args.ClickCount < 2)
            {
                return;
            }
            entry.Editor.IsReadOnly = false;
            entry.Editor.Cursor = Cursors.IBeam;
            entry.Editor.Focus();
            entry.Editor.SelectAll();
            args.Handled = true;
        }

        /// <summary>提交/取消/失焦后回到“只读等双击”的状态。</summary>
        private static void ResetEditorReadOnly(Entry entry)
        {
            if (entry == null || entry.Editor == null)
            {
                return;
            }
            entry.Editor.IsReadOnly = true;
            entry.Editor.Cursor = Cursors.Arrow;
        }

        private static Entry FindFocused(Host host)
        {
            for (int index = 0; index < host.Entries.Count; index++)
            {
                if (host.Entries[index].Editor.IsKeyboardFocusWithin)
                {
                    return host.Entries[index];
                }
            }
            return null;
        }

        private static void OnEditorLostKeyboardFocus(Entry entry)
        {
            if (entry == null || entry.Committing)
            {
                return;
            }
            if (entry.Editor.IsKeyboardFocusWithin || !entry.Editor.IsVisible)
            {
                ResetEditorReadOnly(entry);
                return;
            }
            // 刚提交过（点空白、回车、Esc）不要再提交一次；
            // 文本没变也不用重复写盘。
            if (string.Equals(entry.Editor.Text, entry.LastCommittedText, StringComparison.Ordinal))
            {
                ResetEditorReadOnly(entry);
                return;
            }
            CommitEntry(entry);
        }

        private static void OnEditorPreviewKeyDown(Entry entry, KeyEventArgs args)
        {
            if (entry == null || args == null)
            {
                return;
            }
            if (args.Key == Key.Enter)
            {
                // 用隧道事件拦：不然输入框没拿焦点时，回车会冒泡去触发下面的按钮。
                args.Handled = true;
                CommitEntry(entry);
                Window owner = Window.GetWindow(entry.Editor);
                if (owner != null)
                {
                    FocusOwner(owner);
                }
            }
            else if (args.Key == Key.Escape && entry.Cancel != null)
            {
                args.Handled = true;
                entry.Committing = true;
                try
                {
                    entry.Cancel();
                }
                finally
                {
                    entry.Committing = false;
                    entry.LastCommittedText = entry.Editor.Text;
                    ResetEditorReadOnly(entry);
                }
                Window owner = Window.GetWindow(entry.Editor);
                if (owner != null)
                {
                    FocusOwner(owner);
                }
            }
        }

        private static void CommitEntry(Entry entry)
        {
            if (entry == null || entry.Committing)
            {
                return;
            }
            entry.Committing = true;
            try
            {
                entry.Commit();
            }
            finally
            {
                entry.Committing = false;
                // 提交回调可能把文本规范化（例如 007 → 7、超范围夹到上限），
                // 以回调之后的文本为准，后面的 LostFocus 才不会又提交一次。
                entry.LastCommittedText = entry.Editor.Text;
                ResetEditorReadOnly(entry);
            }
        }

        /// <summary>把键盘焦点交回窗口（输入框的强调边框随之消失）。</summary>
        private static void FocusOwner(Window owner)
        {
            try
            {
                if (owner == null || !owner.IsVisible)
                {
                    return;
                }
                if (!owner.Focus())
                {
                    Keyboard.ClearFocus();
                }
            }
            catch
            {
                try
                {
                    Keyboard.ClearFocus();
                }
                catch
                {
                }
            }
        }
    }
}
