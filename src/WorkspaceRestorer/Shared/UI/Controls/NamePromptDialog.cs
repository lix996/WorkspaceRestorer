using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共组件】输入一个名字的小窗口（新建子方案/子文件夹、重命名时用）。
    ///
    /// 和 ConfirmDialog 一样自己画，保持按钮顺序「确定在左、取消在右」，
    /// 右上角 × / Esc 都等于取消。回车等于确定。
    /// </summary>
    internal static class NamePromptDialog
    {
        public static string Ask(Window owner, string title, string prompt, string initialValue)
        {
            NamePromptWindow window = new NamePromptWindow(title, prompt, initialValue);
            if (owner != null && owner.IsVisible)
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            return window.ShowDialog() == true ? window.Value : null;
        }

        private sealed class NamePromptWindow : Window
        {
            private readonly TextBox input;

            public NamePromptWindow(string title, string prompt, string initialValue)
            {
                Title = string.IsNullOrWhiteSpace(title)
                    ? AppText.T(ShellTextKeys.DialogNameTitle)
                    : title;
                Width = 480;
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
                    Margin = new Thickness(0, 0, 0, 8)
                });
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    root.Children.Add(new TextBlock
                    {
                        Text = prompt,
                        FontSize = 12,
                        Foreground = AppTheme.TextSecondary,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 19,
                        Margin = new Thickness(0, 0, 0, 10)
                    });
                }

                input = new TextBox
                {
                    Text = initialValue ?? string.Empty,
                    FontSize = 13,
                    MinHeight = 32,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 16)
                };
                AccessibleToolTip.Attach(input, ShellToolTips.NameInput);
                root.Children.Add(input);

                StackPanel buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Button ok = UiFactory.CommandButtonKey(
                    "\uE73E", ShellTextKeys.DialogOk, true);
                ok.MinWidth = 96;
                ok.Margin = new Thickness(0, 0, 8, 0);
                AccessibleToolTip.Attach(ok, ShellToolTips.NameOk);
                ok.Click += delegate { Accept(); };
                Button cancel = UiFactory.CommandButtonKey(
                    "\uE711", ShellTextKeys.DialogCancel, false);
                cancel.MinWidth = 96;
                AccessibleToolTip.Attach(cancel, ShellToolTips.NameCancel);
                cancel.Click += delegate
                {
                    DialogResult = false;
                    Close();
                };
                buttons.Children.Add(ok);
                buttons.Children.Add(cancel);
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

                PreviewKeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if (args.Key == Key.Escape)
                    {
                        args.Handled = true;
                        DialogResult = false;
                        Close();
                    }
                    else if (args.Key == Key.Enter)
                    {
                        args.Handled = true;
                        Accept();
                    }
                };
                Loaded += delegate
                {
                    input.Focus();
                    input.SelectAll();
                };
            }

            public string Value
            {
                get { return input == null ? string.Empty : (input.Text ?? string.Empty).Trim(); }
            }

            private void Accept()
            {
                if (string.IsNullOrWhiteSpace(Value))
                {
                    input.Focus();
                    return;
                }
                DialogResult = true;
                Close();
            }
        }
    }
}
