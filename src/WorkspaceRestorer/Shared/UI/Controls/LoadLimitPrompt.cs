using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】CPU/内存超限时的等待浮窗。
    ///
    /// 只负责显示 + 两个按钮；显示时长、当前值刷新、超时停止都由恢复控制器
    /// / SessionRestoreService 决定。浮窗不会自己几秒后消失：
    ///   · 负载恢复 → 控制器隐藏它并继续恢复；
    ///   · 用户点“立即恢复” → 控制器隐藏并继续；
    ///   · 用户点“取消恢复” → 控制器取消；
    ///   · 到 y 秒仍超限 → 控制器隐藏它并显示常驻暂停提示。
    /// </summary>
    internal sealed class LoadLimitPrompt : Border
    {
        private readonly TextBlock text;

        internal event Action ContinueRequested;
        internal event Action CancelRequested;

        internal LoadLimitPrompt()
        {
            Visibility = Visibility.Collapsed;
            Background = AppTheme.Surface;
            BorderBrush = AppTheme.Border;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(20, 16, 20, 14);
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            // 固定宽度/最小高度：CPU/内存行始终存在，数字从 9% 变 100% 也不改变布局，
            // 不会出现浮窗“变宽/变高→重新居中”的闪烁。
            Width = 560;
            MinHeight = 250;
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 1,
                Opacity = 0.2,
                Color = Colors.Black
            };
            AutomationProperties.SetAutomationId(this, "LoadLimitPrompt");

            StackPanel content = new StackPanel();
            TextBlock title = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            LocalizedText.Text(title, ShellTextKeys.LoadLimitTitle);
            content.Children.Add(title);

            text = new TextBlock
            {
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.NoWrap,
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 10, 0, 0),
                Width = 520
            };
            AutomationProperties.SetAutomationId(text, "LoadLimitText");
            content.Children.Add(text);

            Button continueButton = new Button
            {
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            LocalizedText.Content(continueButton, ShellTextKeys.LoadLimitContinue);
            AutomationProperties.SetAutomationId(continueButton, "LoadLimitContinueButton");
            LocalizedText.AccessibleName(continueButton, ShellTextKeys.LoadLimitContinue);
            continueButton.Click += delegate
            {
                Action handler = ContinueRequested;
                if (handler != null)
                {
                    handler();
                }
            };

            Button cancelButton = new Button
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 7, 16, 7),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            LocalizedText.Content(cancelButton, ShellTextKeys.LoadLimitCancel);
            AutomationProperties.SetAutomationId(cancelButton, "LoadLimitCancelButton");
            LocalizedText.AccessibleName(cancelButton, ShellTextKeys.LoadLimitCancel);
            cancelButton.Click += delegate
            {
                Action handler = CancelRequested;
                if (handler != null)
                {
                    handler();
                }
            };

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            buttons.Children.Add(continueButton);
            buttons.Children.Add(cancelButton);
            content.Children.Add(buttons);
            Child = content;
        }

        internal void Show(string message)
        {
            text.Text = message ?? string.Empty;
            AutomationProperties.SetName(text, text.Text);
            Visibility = Visibility.Visible;
        }

        internal void Hide()
        {
            Visibility = Visibility.Collapsed;
        }
    }
}
