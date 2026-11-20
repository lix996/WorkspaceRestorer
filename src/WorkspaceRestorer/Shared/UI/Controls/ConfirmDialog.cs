using System;
using System.Windows;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共组件】确认框：**「是」在左、「否」在右**，右上角 × 等于「否」（取消）。
    ///
    /// 为什么不用系统的 <c>MessageBox</c>（用户要求 2026-10-10 15:56 / 16:06）：
    ///  · 用户第 53 条：“是和否要交换位置，要适应现在大部分软件的操作习惯，大部分不都是是在左，否在右吗”
    ///    —— 系统 MessageBox 的按钮顺序由 Windows 决定，程序改不了；
    ///  · 用户第 52 条：“检查所有弹窗，选择打叉，的意思是取消（也就是否），现在无法打叉”
    ///    —— 系统 MessageBox 在只有“是/否”时没有关闭按钮，用户没法用 × 表示否。
    /// 自己画的窗口这两点都能满足，而且外观和程序其它窗口一致（圆角、字体、配色统一）。
    ///
    /// 用法：
    /// <code>
    /// if (ConfirmDialog.Ask(this, "删除自启动方案", "确定删除…吗？", "只会删掉清单，不会删除磁盘上的文件。"))
    /// {
    ///     // 用户点了「是」
    /// }
    /// </code>
    /// </summary>
    internal static class ConfirmDialog
    {
        /// <summary>弹出确认框；返回 true = 用户点了「是」，false = 点了「否」或直接关掉窗口。</summary>
        public static bool Ask(Window owner, string title, string message, string detail)
        {
            // 自检用：合并方案这类流程要连点好几步确认框，脚本很难稳定点中，
            // 所以测试模式下可以用环境变量让确认框直接按「是」返回。
            // **只在设置了 WORKSPACE_RESTORER_TEST_MODE 时才认这个变量**，
            // 正常使用时完全没有影响。
            if (string.Equals(Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"), "1", StringComparison.Ordinal))
            {
                string autoYes = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_AUTO_CONFIRM");
                if (string.Equals(autoYes, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (string.Equals(autoYes, "no", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            ConfirmWindow window = new ConfirmWindow(title, message, detail);
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return window.ShowDialog() == true;
        }

        /// <summary>只要一段正文的简写。</summary>
        public static bool Ask(Window owner, string title, string message)
        {
            return Ask(owner, title, message, null);
        }

        private sealed class ConfirmWindow : Window
        {
            public ConfirmWindow(string title, string message, string detail)
            {
                Title = string.IsNullOrWhiteSpace(title)
                    ? AppText.T(ShellTextKeys.DialogConfirmTitle)
                    : title;
                Width = 480;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Background = AppTheme.WindowBackground;
                FontFamily = AppTheme.UiFont;
                // 关掉窗口（含右上角 ×、Alt+F4、Esc）= 否。
                // 用户第 52 条：“选择打叉，的意思是取消（也就是否）。”
                // 关掉窗口（含右上角 ×、Alt+F4）= 否：DialogResult 保持不是 true，Ask 自然返回 false。

                StackPanel root = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
                root.Children.Add(new TextBlock
                {
                    Text = Title,
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = AppTheme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });
                root.Children.Add(new TextBlock
                {
                    Text = message ?? string.Empty,
                    FontSize = 13,
                    Foreground = AppTheme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                });
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontSize = 12,
                        Foreground = AppTheme.TextSecondary,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 19,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }

                // 按钮排：**「是」在左、「否」在右**（用户第 53 条要求，符合现在多数软件的习惯）。
                StackPanel buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0)
                };
                Button yes = UiFactory.CommandButtonKey(
                    "\uE73E", ShellTextKeys.DialogYes, true);
                yes.MinWidth = 96;
                yes.Margin = new Thickness(0, 0, 8, 0);
                // 【2026-10-17 补】这两个按钮的内容是“图标 + 文字”的 StackPanel，
                // WPF 的 UI Automation 推不出名字 —— 读屏软件只会念“按钮”，
                // 自检也点不中它们（Round23 一直在报“确认框里有「是」「否」按钮：是 0 个；否 0 个”）。
                // 程序里其它按钮都挂了 AccessibleToolTip，这里补齐。
                AccessibleToolTip.Attach(yes, ShellToolTips.ConfirmYes);
                yes.Click += delegate
                {
                    // 【2026-10-17 清理】这里原来还给一个 `confirmed` 字段赋值，
                    // 但那个字段从头到尾没人读过（编译警告 CS0414），已删掉。
                    try
                    {
                        DialogResult = true;
                    }
                    catch
                    {
                    }
                    Close();
                };
                Button no = UiFactory.CommandButtonKey(
                    "\uE711", ShellTextKeys.DialogNo, false);
                no.MinWidth = 96;
                AccessibleToolTip.Attach(no, ShellToolTips.ConfirmNo);
                no.Click += delegate
                {
                    try
                    {
                        DialogResult = false;
                    }
                    catch
                    {
                    }
                    Close();
                };
                buttons.Children.Add(yes);
                buttons.Children.Add(no);
                root.Children.Add(buttons);
                Content = new Border
                {
                    Background = AppTheme.Surface,
                    BorderBrush = AppTheme.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(12),
                    Child = root
                };

                // Esc = 否（与 × 一致）。
                PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
                {
                    if (args.Key == System.Windows.Input.Key.Escape)
                    {
                        args.Handled = true;
                        no.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    }
                };
                Loaded += delegate { no.Focus(); };   // 默认焦点给「否」，避免误按回车就确认
            }
        }
    }
}
