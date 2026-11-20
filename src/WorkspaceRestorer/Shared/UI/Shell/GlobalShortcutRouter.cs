using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】全局快捷键路由 —— 整个软件**只有这一处**判断按键。
    ///
    /// 用户要求（2026-10-17）：“快捷键的代码要放到公共文件夹内，能够让：启动与恢复，图标位置保存，
    /// 自启动应用，回收站等都能用上，是作用于软件的全局，……命名好，后续修改的时候也好修改。”
    ///
    /// 分工：
    ///   · 键位与说明写在 <see cref="ShortcutCatalog"/>（唯一数据源，也是设置页显示的那张表）；
    ///   · 判断按键、找到动作、统一记日志在**这里**；
    ///   · 各工作区只需知道自己要响应哪个命令 Id（<see cref="Bind"/>），不必各写一份 KeyDown。
    ///
    /// 因为挂在**窗口**上（主窗口的 KeyDown/PreviewKeyDown），所以不管当前是哪个工作区、
    /// 焦点在列表还是按钮上，按键都会先经过这里 —— 这就是“作用于软件的全局”。
    /// </summary>
    internal sealed class GlobalShortcutRouter
    {
        private readonly Dictionary<string, Action> handlers =
            new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);

        public GlobalShortcutRouter()
        {
            IsTextEditingFocus = delegate { return false; };
            IsDragInProgress = delegate { return false; };
            DescribeContext = delegate { return string.Empty; };
            Executed = delegate { };
        }

        /// <summary>焦点是不是在文本框里（那种情况下 Ctrl+C/V 属于文本编辑）。</summary>
        public Func<bool> IsTextEditingFocus { get; set; }

        /// <summary>是不是正在拖动列表项（拖动中不允许撤销/重做）。</summary>
        public Func<bool> IsDragInProgress { get; set; }

        /// <summary>当前工作区名（只用于日志与状态栏）。</summary>
        public Func<string> DescribeContext { get; set; }

        /// <summary>
        /// 快捷键真正执行后回调（公共浮窗提示用）。
        /// 只有“确实认领并执行了这个键”才调；输入框里的文本编辑、拖动中被忽略的键不会调。
        /// </summary>
        public Action<ShortcutCatalog.Shortcut> Executed { get; set; }

        /// <summary>把某个命令 Id 接到一个动作上（同一 Id 后绑定的覆盖先绑定的）。</summary>
        public void Bind(string commandId, Action handler)
        {
            if (string.IsNullOrWhiteSpace(commandId) || handler == null)
            {
                return;
            }
            handlers[commandId] = handler;
        }

        /// <summary>这个命令 Id 有没有接上动作（自检/日志用）。</summary>
        public bool IsBound(string commandId)
        {
            return !string.IsNullOrWhiteSpace(commandId) && handlers.ContainsKey(commandId);
        }

        /// <summary>
        /// 窗口 PreviewKeyDown 用：只提前处理“布局移动”这类会被 ScrollViewer 吃掉的箭头键，
        /// 其余快捷键仍走 KeyDown，避免影响输入框/滚动。
        /// </summary>
        public bool HandlePreview(KeyEventArgs e)
        {
            if (e == null)
            {
                return false;
            }
            Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
            ShortcutCatalog.Shortcut shortcut = Resolve(effectiveKey, Keyboard.Modifiers);
            if (shortcut == null || string.IsNullOrEmpty(shortcut.Id)
                || !shortcut.Id.StartsWith("layout-move-", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!TryExecute(shortcut))
            {
                return false;
            }
            e.Handled = true;
            return true;
        }
        /// <summary>
        /// 主窗口 KeyDown（冒泡到窗口那一层）→ 这里。返回 true 表示已经处理掉了。
        /// </summary>
        public bool Handle(KeyEventArgs e)
        {
            if (e == null)
            {
                return false;
            }
            Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
            ShortcutCatalog.Shortcut shortcut = Resolve(effectiveKey, Keyboard.Modifiers);
            if (shortcut == null)
            {
                return false;
            }
            if (!TryExecute(shortcut))
            {
                return false;
            }
            e.Handled = true;
            return true;
        }

        /// <summary>
        /// 直接执行一条快捷键（**不经过按键事件**）。
        /// 用在“命令翻译之前”的那一层：输入框会把 Ctrl+Z 先吃掉（见 MainWindow.OnWindowPreProcessInputKeys），
        /// 那里拿到的是同一条<see cref="ShortcutCatalog.Shortcut"/>，交给这里执行，键位判断仍然只有一处。
        /// </summary>
        public bool Execute(ShortcutCatalog.Shortcut shortcut)
        {
            return shortcut != null && TryExecute(shortcut);
        }

        private bool TryExecute(ShortcutCatalog.Shortcut shortcut)
        {
            // 拖动中：撤销/重做会把界面和数据撕开，直接忽略（日志留一句，方便对时间线）。
            if (shortcut.IgnoreWhileDragging && IsDragInProgress())
            {
                AppLog.Write("[快捷键] " + shortcut.Keys + "（" + shortcut.Id
                    + "）被忽略：正在拖动列表项");
                return false;
            }
            // 文本框里：Ctrl+C / Ctrl+V 属于文本编辑，让给它（其它快捷键不受影响）。
            if (shortcut.SkipWhenEditingText && IsTextEditingFocus())
            {
                return false;
            }
            Action handler;
            if (!handlers.TryGetValue(shortcut.Id, out handler) || handler == null)
            {
                AppLog.Write("[快捷键] " + shortcut.Keys + "（" + shortcut.Id
                    + "）没有绑定动作，什么也没做");
                return false;
            }
            // 先记日志、先弹提示，再执行动作：复制/粘贴大方案可能要几十毫秒，
            // 提示不能等动作做完才出现（用户会感觉“按了很卡、没反应”）。
            AppLog.Write("[快捷键] " + shortcut.Keys + " → " + shortcut.Id
                + "（工作区=" + SafeContext() + "，焦点=" + DescribeFocus() + "）");
            NotifyExecuted(shortcut);
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                AppLog.Write("[快捷键] " + shortcut.Keys + "（" + shortcut.Id + "）执行时出错", exception);
                return true;   // 已经“认领”了这个键，避免再落到别处重复执行
            }
            return true;
        }

        /// <summary>把“这个快捷键已经执行”交给公共提示层；回调出错不能影响快捷键本身。</summary>
        private void NotifyExecuted(ShortcutCatalog.Shortcut shortcut)
        {
            try
            {
                Action<ShortcutCatalog.Shortcut> callback = Executed;
                if (callback != null)
                {
                    callback(shortcut);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[快捷键] 浮窗提示失败", exception);
            }
        }

        /// <summary>
        /// 窗口级 PreviewKeyDown：**只记日志、不改行为**。
        /// 输入框会自己吃掉 Ctrl+C/V（这是对的），但用户从界面上看不出来，
        /// 以后排查“快捷键没反应”时靠这条日志一眼分清：
        ///   · 有“交给输入框”——键被输入框拿走了，先把光标移出输入框再按；
        ///   · 有“[快捷键] … → 撤销上一步”——程序确实收到了；
        ///   · 什么都没有——窗口没拿到这个键（被别的窗口/浮层抢走了，或者被命令翻译提前吃掉）。
        /// </summary>
        public void ObservePreview(KeyEventArgs e)
        {
            if (e == null || !IsTextEditingFocus())
            {
                return;
            }
            Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
            ShortcutCatalog.Shortcut shortcut = Resolve(effectiveKey, Keyboard.Modifiers);
            if (shortcut == null || !shortcut.SkipWhenEditingText)
            {
                return;
            }
            AppLog.Write("[快捷键] " + shortcut.Keys + "：焦点在输入框里，交给输入框自己处理（正常行为）");
        }

        /// <summary>在“命令翻译之前”那一层记录一句：这个键被输入框自己处理掉了。</summary>
        public void LogTextEditorHadOwnUndo(ShortcutCatalog.Shortcut shortcut, string textEditorState)
        {
            if (shortcut == null)
            {
                return;
            }
            AppLog.Write("[快捷键] " + shortcut.Keys + "：输入框自己也有撤销历史，但实测文本撤销不改变文字 → 仍然按全局快捷键执行"
                + (string.IsNullOrWhiteSpace(textEditorState) ? string.Empty : "（" + textEditorState + "）"));
        }

        /// <summary>在“命令翻译之前”那一层记录一句：输入框没本事撤销，于是按全局撤销执行了。</summary>
        public void LogTakenFromTextEditor(ShortcutCatalog.Shortcut shortcut, string textEditorState)
        {
            if (shortcut == null)
            {
                return;
            }
            AppLog.Write("[快捷键] " + shortcut.Keys + "：输入框里没有可撤销的输入 → 按全局快捷键执行"
                + (string.IsNullOrWhiteSpace(textEditorState) ? string.Empty : "（" + textEditorState + "）"));
        }

        /// <summary>
        /// 当前按键是不是某条快捷键（只判断，不执行；自检与界面提示可用）。
        /// 判断仍然只有目录一处：用户修改/删除绑定后，这里下一次按键立即按新列表走。
        /// </summary>
        public static ShortcutCatalog.Shortcut Resolve(Key key, ModifierKeys modifiers)
        {
            return ShortcutCatalog.Resolve(key, modifiers);
        }

        private string SafeContext()
        {
            try
            {
                Func<string> context = DescribeContext;
                return context == null ? string.Empty : (context() ?? string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>日志里写清“这个键是在哪个控件上按的”，排查快捷键问题全靠它。</summary>
        private static string DescribeFocus()
        {
            try
            {
                IInputElement focused = Keyboard.FocusedElement;
                if (focused == null)
                {
                    return "（没有控件有焦点）";
                }
                string name = System.Windows.Automation.AutomationProperties.GetName(
                    focused as System.Windows.DependencyObject);
                string type = focused.GetType().Name;
                return string.IsNullOrWhiteSpace(name) ? type : (type + "「" + name + "」");
            }
            catch
            {
                return "（读不到焦点）";
            }
        }
    }
}
