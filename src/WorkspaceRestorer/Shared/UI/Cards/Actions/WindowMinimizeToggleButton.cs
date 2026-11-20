using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 「启动与恢复」和「自启动应用」共用的“恢复时最小化”开关。
    ///
    /// 这里只负责按钮外观、状态和无障碍名字，不碰业务数据：
    ///   · 调用方决定当前是否最小化、点击后要改哪个方案/项目；
    ///   · 调用方改完数据后调用 <see cref="ApplyState"/> 就地刷新。
    /// </summary>
    internal static class WindowMinimizeToggleButton
    {
        internal const string NameOn = "最小化：已开启";
        internal const string NameOff = "最小化：未开启";

        internal static Button Create(bool minimized, Action onClick)
        {
            Button button = new Button
            {
                Style = Application.Current == null
                    ? null
                    : (Style)Application.Current.Resources["IconButton"],
                FontFamily = AppTheme.IconFont,
                Cursor = Cursors.Hand
            };
            if (onClick != null)
            {
                button.Click += delegate { onClick(); };
            }
            ApplyState(button, minimized);
            return button;
        }

        internal static void ApplyState(Button button, bool minimized)
        {
            if (button == null)
            {
                return;
            }
            Grid content = new Grid { Width = 22, Height = 22 };
            TextBlock glyph = new TextBlock
            {
                Text = UiFactory.IconGlyph("\uE921"),
                FontFamily = AppTheme.IconFont,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = minimized ? AppTheme.Accent : AppTheme.TextSecondary
            };
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock
            {
                Text = UiFactory.IconGlyph("\uE73E"),
                FontFamily = AppTheme.IconFont,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -1, -1),
                Foreground = AppTheme.Accent,
                Visibility = minimized ? Visibility.Visible : Visibility.Collapsed
            });
            button.Content = new Border
            {
                Background = minimized ? AppTheme.ActiveSurface : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(2),
                Child = content
            };
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Width = 32;
            button.Height = 32;
            button.MinHeight = 32;
            button.Padding = new Thickness(0);

            string stateName = minimized ? NameOn : NameOff;
            string description = minimized
                ? "当前设置为恢复时最小化；点击取消最小化，恢复为最小化前的状态。"
                : "当前设置为正常显示；点击设为恢复时最小化。";
            AccessibleToolTip.Attach(button, stateName, description);
        }
    }
}
