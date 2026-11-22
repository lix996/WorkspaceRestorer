using System;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    /// <summary>收编未登记方案时的三种处理方式。</summary>
    internal enum MirrorIntakeChoice
    {
        Import,
        Defer,
        Quarantine
    }

    /// <summary>
    /// 【收编流程专用】三选一弹框：导入 / 暂不导入 / 移到“无法识别”。
    ///
    /// 为什么不做成公共三选一控件：目前只有“未登记方案收编”一个真实调用者，
    /// 而且三个选项直接对应方案导入和文件移动，不是通用确认语义；
    /// 公共确认框 ConfirmDialog 仍然只负责“是/否”。
    ///
    /// 关闭窗口（×、Alt+F4、Esc）等于「暂不导入」，不会默认移走或导入。
    /// </summary>
    internal static class MirrorIntakeDialog
    {
        public static MirrorIntakeChoice Ask(Window owner, int count)
        {
            MirrorIntakeWindow window = new MirrorIntakeWindow(count);
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            window.ShowDialog();
            return window.Choice;
        }

        private sealed class MirrorIntakeWindow : Window
        {
            private MirrorIntakeChoice choice;

            public MirrorIntakeChoice Choice
            {
                get { return choice; }
            }

            public MirrorIntakeWindow(int count)
            {
                choice = MirrorIntakeChoice.Defer;
                Title = AppText.T(ShellTextKeys.DialogIntakeTitle);
                Width = 560;
                SizeToContent = SizeToContent.Height;
                ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false;
                Background = AppTheme.WindowBackground;
                FontFamily = AppTheme.UiFont;

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
                    Text = AppText.F(ShellTextKeys.DialogIntakeMessageFormat, count),
                    FontSize = 13,
                    Foreground = AppTheme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20
                });
                root.Children.Add(new TextBlock
                {
                    Text = AppText.T(ShellTextKeys.DialogIntakeDetail),
                    FontSize = 12,
                    Foreground = AppTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19,
                    Margin = new Thickness(0, 8, 0, 0)
                });

                StackPanel buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0)
                };

                Button import = UiFactory.CommandButtonKey(
                    "\uE73E", ShellTextKeys.DialogIntakeImport, true);
                import.MinWidth = 112;
                import.Margin = new Thickness(0, 0, 8, 0);
                AccessibleToolTip.Attach(import, ShellToolTips.IntakeImport);
                import.Click += delegate { Choose(MirrorIntakeChoice.Import); };

                Button defer = UiFactory.CommandButtonKey(
                    "\uE823", ShellTextKeys.DialogIntakeDefer, false);
                defer.MinWidth = 112;
                defer.Margin = new Thickness(0, 0, 8, 0);
                AccessibleToolTip.Attach(defer, ShellToolTips.IntakeDefer);
                defer.Click += delegate { Choose(MirrorIntakeChoice.Defer); };

                Button quarantine = UiFactory.CommandButtonKey(
                    "\uE8B7", ShellTextKeys.DialogIntakeQuarantine, false);
                quarantine.MinWidth = 112;
                AccessibleToolTip.Attach(quarantine, ShellToolTips.IntakeQuarantine);
                quarantine.Click += delegate { Choose(MirrorIntakeChoice.Quarantine); };

                buttons.Children.Add(import);
                buttons.Children.Add(defer);
                buttons.Children.Add(quarantine);
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

                PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs args)
                {
                    if (args.Key == System.Windows.Input.Key.Escape)
                    {
                        args.Handled = true;
                        Choose(MirrorIntakeChoice.Defer);
                    }
                };
                Loaded += delegate { defer.Focus(); };
            }

            private void Choose(MirrorIntakeChoice value)
            {
                choice = value;
                try
                {
                    DialogResult = true;
                }
                catch
                {
                }
                Close();
            }
        }
    }
}
