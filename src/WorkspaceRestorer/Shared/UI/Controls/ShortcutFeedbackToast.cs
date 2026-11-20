using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】全局快捷键的浮窗提示。
    ///
    /// 用户要求（2026-10-31）：
    ///   “我按复制粘贴快捷键时，会有显示（参考图1，但是是黑色字体），
    ///    我得知道我到底有没有按快捷键……防止用户一次性按多次相同快捷键，浮窗提示乱跳，
    ///    浮窗提示只显示第一次（点击任意地方浮窗提示就消失），
    ///    时间和恢复时的时间一样……在这个时间段按的相同快捷键，浮窗提示只显示第一次，
    ///    若超出这个时间，那么会继续显示，如果更换了其他快捷键，那么浮窗提示要改变。”
    ///
    /// 规则：
    ///   · 同一个快捷键在浮窗还亮着时再按：忽略，不重置计时、不跳位置；
    ///   · 换一个快捷键：立即换成新的文字，重新计时；
    ///   · 到时自动消失；用户点任意地方也消失；
    ///   · 消失后再按同一个快捷键：重新显示。
    ///
    /// 本类只管“显示/去重/计时”，不判断键位；键位与命令仍在
    /// <see cref="ShortcutCatalog"/> / <see cref="GlobalShortcutRouter"/>。
    /// </summary>
    internal sealed class ShortcutFeedbackToast : Border
    {
        /// <summary>
        /// 与恢复完成横幅相同的显示时长（MainWindow 的 restoreNoticeTimer 也用它）。
        /// 用户明确要求“时间和恢复时的时间一样”。
        /// </summary>
        internal const int DisplayDurationMilliseconds = 6000;

        private readonly TextBlock text;
        private readonly DispatcherTimer timer;
        private string currentShortcutId;

        internal ShortcutFeedbackToast()
        {
            Visibility = Visibility.Collapsed;
            Background = AppTheme.Surface;
            BorderBrush = AppTheme.Border;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(16, 9, 16, 9);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(0);
            IsHitTestVisible = false;
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 1,
                Opacity = 0.18,
                Color = Colors.Black
            };

            text = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                // 用户明确要黑色字体；AppTheme.TextPrimary 就是界面正文的黑色。
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 620
            };
            AutomationProperties.SetAutomationId(text, "ShortcutFeedbackText");
            LocalizedText.AccessibleName(text, ShellTextKeys.ShortcutFeedbackName);
            Child = text;

            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(DisplayDurationMilliseconds)
            };
            timer.Tick += delegate { Hide(); };
        }

        /// <summary>显示一条快捷键；同一个 Id 还亮着时直接忽略。</summary>
        internal void Show(ShortcutCatalog.Shortcut shortcut)
        {
            if (shortcut == null)
            {
                return;
            }
            string id = !string.IsNullOrWhiteSpace(shortcut.Id) ? shortcut.Id : shortcut.Keys;
            // 同一命令可能挂了多条快捷键：去重看“命令 + 实际键位”，
            // 换一条绑定按下去时浮窗要立即改文字。
            if (!string.IsNullOrWhiteSpace(shortcut.Keys))
            {
                id = id + "|" + shortcut.Keys;
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }
            if (Visibility == Visibility.Visible
                && string.Equals(currentShortcutId, id, StringComparison.OrdinalIgnoreCase))
            {
                // 同一个快捷键连按：第一次已经显示过，保持原位置/原计时。
                AppLog.Write("[快捷键提示] 同一快捷键仍在显示，忽略重复：" + shortcut.Keys);
                return;
            }

            currentShortcutId = id;
            string keys = string.IsNullOrWhiteSpace(shortcut.Keys)
                ? AppText.T(ShellTextKeys.ShortcutFallback)
                : shortcut.Keys.Trim();
            string summary = string.IsNullOrWhiteSpace(shortcut.Summary)
                ? shortcut.Title
                : shortcut.Summary;
            text.Text = string.IsNullOrWhiteSpace(summary)
                ? keys
                : AppText.F(ShellTextKeys.ShortcutFeedbackFormat, keys, summary.Trim());
            // 让外部自检/读屏读到实际内容，而不是固定标题。
            AutomationProperties.SetName(text, text.Text);
            Visibility = Visibility.Visible;
            timer.Stop();
            timer.Start();
            AppLog.Write("[快捷键提示] 显示：" + text.Text);
        }

        /// <summary>立即隐藏；下次同一个快捷键再按会重新显示。</summary>
        internal void Hide()
        {
            bool wasVisible = Visibility == Visibility.Visible;
            timer.Stop();
            Visibility = Visibility.Collapsed;
            currentShortcutId = null;
            if (wasVisible)
            {
                AppLog.Write("[快捷键提示] 已隐藏");
            }
        }
    }
}
