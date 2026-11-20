using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.UI.Cards.Interaction
{
    /// <summary>
    /// 【公共区域】“点这一行就跳/打开”的**交互层**（用户 2026-10-18 要求）。
    ///
    /// 用户原话：“移动‘启动与恢复’的这部分代码，点击后可以跳转链接，要求功能一致，鼠标放过去有显示，
    /// 放到公共文件夹内，启动与恢复和自启动应用能用上，后续的图标位置保存则也要显示，
    /// 但是点击确是显示图片。”
    /// 后半句又交代了边界：“只搬大概的，点击跳转和交互部分，但深层次的窗口激活、浏览器标签定位、
    /// Explorer 子项等就不用搬，这样后续做分支时也容易。”
    ///
    /// 所以这里只放**三者共用的那一层**：
    ///  · 什么时候算“点击”（拖动刚结束不算、点在行里的小按钮/勾选框/滚动条上不算）；
    ///  · 鼠标放过去显示什么（统一提示文案 + 手型光标 + 空白区域可命中）；
    ///  · 测试模式下**不真的打开/跳转**（只写日志与一句提示，自检可以随便点）。
    /// **“跳到哪”由各工作区自己给**（`Action jump`）：
    ///  启动与恢复 → 找已开窗口/标签页那一套（留在 `StartupRestore/UI/`）；
    ///  自启动应用 → 打开这一项（留在 `Applications/UI/`）；
    ///  图标位置保存（以后）→ 显示图片。
    /// </summary>
    internal static class ClickToJump
    {
        /// <summary>“点了会跳到已经开着的窗口”的提示（与「启动与恢复」原来那句一字不差）。
        /// 自启动应用现在也用这一句：它点整行是**真跳转**（复用「启动与恢复」的 JumpToSavedItemWindow，
        /// 代码没有搬过来，只调用），所以两边提示完全一致。</summary>
        internal static string JumpHint
        {
            get { return AppText.T(CommonTextKeys.JumpHint); }
        }

        /// <summary>排序提示（两边一模一样：都是 ≡ 手柄或 Shift 整行拖）。</summary>
        internal static string DragHintSuffix
        {
            get { return AppText.T(CommonTextKeys.JumpDragHintSuffix); }
        }

        internal static string BuildRowToolTip(string actionHint)
        {
            return string.IsNullOrWhiteSpace(actionHint) ? DragHintSuffix : (actionHint + DragHintSuffix);
        }

        /// <summary>点到行里的这些小控件时，不算“点击整行”（它们有自己的动作）。</summary>
        internal static bool IsActionControl(DependencyObject origin)
        {
            DependencyObject current = origin;
            while (current != null)
            {
                if (current is ButtonBase
                    || current is System.Windows.Controls.Primitives.TextBoxBase
                    || current is PasswordBox
                    || current is System.Windows.Controls.Primitives.ScrollBar
                    || current is System.Windows.Controls.Primitives.Thumb)
                {
                    return true;
                }
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// “点在某个元素（或它的子元素）上就忽略”——用来排除 ≡ 拖动柄这类只给拖动用的东西。
        /// </summary>
        internal static Func<DependencyObject, bool> IgnoreInside(FrameworkElement element)
        {
            return delegate(DependencyObject origin)
            {
                if (element == null || origin == null)
                {
                    return false;
                }
                DependencyObject current = origin;
                while (current != null)
                {
                    if (object.ReferenceEquals(current, element))
                    {
                        return true;
                    }
                    current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                }
                return false;
            };
        }

        /// <summary>
        /// 把一个“行”变成可点击跳转/打开的行。
        /// </summary>
        /// <param name="row">整行（用来挂鼠标事件与手型光标）。</param>
        /// <param name="tooltipTarget">提示挂在哪个元素上（一般是行里的名字文字，别挂在整行上，否则提示会很大一块）。</param>
        /// <param name="tooltip">悬停提示文字（用 <see cref="BuildRowToolTip"/> 拼）。</param>
        /// <param name="consumeJustDragged">返回 true＝这次点击是“刚拖完”那一下，消费掉、不执行动作。</param>
        /// <param name="subject">日志/提示里这句叫什么（例如项目名）。</param>
        /// <param name="jump">真正要做什么（各工作区自己的深层实现）。</param>
        /// <param name="report">把一句话写到状态栏（测试模式提示用）。</param>
        /// <param name="alsoIgnore">
        /// 还要忽略哪些元素（可选）。用来排除“≡ 手柄”这类**只给拖动用**的东西：
        /// 它们不是 Button，`IsActionControl` 认不出来，点一下会冒泡成“点击整行”。
        /// </param>
        internal static void AttachRow(
            FrameworkElement row,
            FrameworkElement tooltipTarget,
            string tooltip,
            Func<bool> consumeJustDragged,
            string subject,
            Action jump,
            Action<string> report,
            Func<DependencyObject, bool> alsoIgnore = null,
            Border hoverHost = null,
            TextBlock heading = null,
            Brush normalBackground = null,
            Brush hoverBackground = null,
            Brush normalForeground = null,
            Brush hoverForeground = null,
            Func<bool> isDragging = null)
        {
            if (row == null || jump == null)
            {
                return;
            }
            FrameworkElement hintHost = tooltipTarget ?? row;
            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                // 公共件同时负责提示、ToolTipService 默认值和内部文字块的 HelpText。
                AccessibleToolTip.AttachDescriptionDeep(hintHost, tooltip, null);
            }
            row.Cursor = Cursors.Hand;
            // 空白处也要能点到：Grid/StackPanel 默认没有背景，命中测试会漏。
            Panel panel = row as Panel;
            if (panel != null && panel.Background == null)
            {
                panel.Background = Brushes.Transparent;
            }
            // 悬停高亮（用户 2026-10-18：“要像‘启动与恢复’那样有显示，参考交互”）：
            // 整行背景变浅蓝 + 名字变主题色并加下划线；拖动中不亮（只让插入线提示落点）。
            if (hoverHost != null && hoverBackground != null)
            {
                Brush restBackground = normalBackground ?? hoverHost.Background;
                Brush restForeground = normalForeground ?? (heading == null ? null : heading.Foreground);
                row.MouseEnter += delegate
                {
                    if (isDragging != null)
                    {
                        try
                        {
                            if (isDragging())
                            {
                                return;
                            }
                        }
                        catch
                        {
                        }
                    }
                    hoverHost.Background = hoverBackground;
                    if (heading != null)
                    {
                        if (hoverForeground != null) { heading.Foreground = hoverForeground; }
                        heading.TextDecorations = TextDecorations.Underline;
                    }
                };
                row.MouseLeave += delegate
                {
                    if (restBackground != null) { hoverHost.Background = restBackground; }
                    if (heading != null)
                    {
                        if (restForeground != null) { heading.Foreground = restForeground; }
                        heading.TextDecorations = null;
                    }
                };
            }
            row.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (consumeJustDragged != null && consumeJustDragged())
                {
                    return;
                }
                if (IsActionControl(args.OriginalSource as DependencyObject))
                {
                    return;
                }
                if (alsoIgnore != null)
                {
                    try
                    {
                        if (alsoIgnore(args.OriginalSource as DependencyObject))
                        {
                            return;
                        }
                    }
                    catch
                    {
                    }
                }
                // 这次点击是“跳到这一项”，不能再冒泡给外面的方案卡：
                // 否则方案卡的“单击选中”会在跳转之后把状态栏改成“当前工作方案…”，
                // 用户会以为跳转没生效。拖动结束/点行内按钮的路径在上面已经返回。
                args.Handled = true;
                if (JumpDisabled())
                {
                    string skipped = AppText.F(
                        CommonTextKeys.JumpTestModeSkippedFormat,
                        subject ?? AppText.T(CommonTextKeys.ThisItemFallback));
                    AppLog.Write("[跳转] " + skipped);
                    if (report != null)
                    {
                        try { report(skipped); } catch { }
                    }
                    return;
                }
                try
                {
                    jump();
                }
                catch (Exception exception)
                {
                    AppLog.Write("[跳转] 执行失败：" + (subject ?? "?") + " → " + exception.Message, exception);
                    if (report != null)
                    {
                        try { report(AppText.F(CommonTextKeys.JumpFailedFormat, exception.Message)); } catch { }
                    }
                }
            };
        }

        /// <summary>测试/自检模式：只记日志，不激活窗口、不启动程序（自检会点这些行）。</summary>
        internal static bool JumpDisabled()
        {
            return IsTruthy("WORKSPACE_RESTORER_TEST_MODE")
                || IsTruthy("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS")
                || OsMutationGuard.IsDisabled;
        }

        private static bool IsTruthy(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
