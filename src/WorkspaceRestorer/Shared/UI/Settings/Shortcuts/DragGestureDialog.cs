using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 拖动起手手势的编辑弹窗（只服务设置页“快捷键”分区里的“拖动”行）。
    ///
    /// 用户 2026-11-16 要求：
    ///   · 先按组合键，再点“左键拖动 / 右键拖动”决定用哪个鼠标键；
    ///   · 只按组合键、没点鼠标键按钮时，鼠标键保持原来那个，不能写死左键；
    ///   · 组合键可以只按修饰键（纯修饰键不提示），也可以带字母主键（提示可能和打字冲突）。
    ///
    /// 只负责捕获与显示；合法性判断走 DragGestureSettings，落盘仍由 ApplicationController。
    /// </summary>
    internal sealed class DragGestureDialog : Window
    {
        private readonly Border captureBox;
        private readonly SelectableText captureText;
        private readonly TextBlock errorText;
        private readonly TextBlock warningText;
        private readonly Button saveButton;
        private readonly ShortcutKeyCaptureHook captureHook;

        private Key pendingKey = Key.None;
        private ModifierKeys pendingModifiers = ModifierKeys.Shift;
        private bool candidateValid;
        private string candidateError;
        private MouseButton selectedMouseButton;

        internal DragGestureDialog(
            Window owner,
            Key initialKey,
            ModifierKeys initialModifiers,
            MouseButton mouseButton)
        {
            // 当前选择从原鼠标键开始：不点左/右键按钮直接保存时，保持原来的鼠标键。
            selectedMouseButton = mouseButton == MouseButton.Right ? MouseButton.Right : MouseButton.Left;
            pendingKey = initialKey == Key.None ? Key.None : initialKey;
            pendingModifiers = ShortcutKeyText.NormalizeModifiers(initialModifiers);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 560;
            ShowInTaskbar = false;
            FontFamily = AppTheme.UiFont;
            Title = AppText.T(SettingsTextKeys.DragGestureEditTitle);
            if (owner != null)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            StackPanel root = new StackPanel();

            Grid header = new Grid();
            // 左右两列都占 Star：中间 Auto 列的“请按下组合键”正好落在卡片水平中心，不再贴着 ×。
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock title = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.DragGestureEditTitle),
                FontSize = AppFonts.DialogTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            header.Children.Add(title);
            // “请按下组合键修改”放在卡片上面中间，和左边标题同字号、同粗细。
            TextBlock pressHint = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutPressHint),
                FontSize = AppFonts.DialogTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pressHint, 1);
            AutomationProperties.SetAutomationId(pressHint, "DragGestureHint");
            header.Children.Add(pressHint);
            Button close = UiFactory.IconButton(
                "\uE711",
                TooltipDefinition.Plain(SettingsTextKeys.ShortcutCancel, null));
            close.Width = 30;
            close.Height = 30;
            close.Padding = new Thickness(0);
            close.FontSize = 12;
            // 右侧改成 Star 列后要显式靠右，否则按钮会拉伸整列。
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Click += delegate { CancelDialog(); };
            AutomationProperties.SetAutomationId(close, "DragGestureClose");
            Grid.SetColumn(close, 2);
            header.Children.Add(close);
            root.Children.Add(header);

            TextBlock body = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.DragGestureBody),
                FontSize = AppFonts.Body,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 0)
            };
            AutomationProperties.SetAutomationId(body, "DragGestureBody");
            root.Children.Add(body);

            captureText = new SelectableText
            {
                FontSize = AppFonts.DialogTitle,
                Foreground = AppTheme.TextPrimary,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                // 只做鼠标拖选 + 右键复制，不抢键盘焦点：键盘始终用于录键。
                Focusable = false
            };
            AutomationProperties.SetAutomationId(captureText, "DragGestureCaptureText");
            captureBox = new Border
            {
                Margin = new Thickness(0, 16, 0, 0),
                MinHeight = 56,
                Padding = new Thickness(12, 10, 12, 10),
                Background = AppTheme.PanelSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Child = captureText
            };
            AutomationProperties.SetAutomationId(captureBox, "DragGestureCaptureBox");
            root.Children.Add(captureBox);

            warningText = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.Warning,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(warningText, "DragGestureWarning");
            root.Children.Add(warningText);

            errorText = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.Danger,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(errorText, "DragGestureError");
            root.Children.Add(errorText);

            Grid footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel mouseButtons = new StackPanel { Orientation = Orientation.Horizontal };
            Button leftButton = UiFactory.CommandButtonKey(
                "\uE7C9", SettingsTextKeys.DragGestureLeftButton, false);
            leftButton.MinWidth = 108;
            leftButton.Margin = new Thickness(0, 0, 8, 0);
            // 只切换当前选择的鼠标键，不确认、不关窗；确认统一走“保存”或 Enter。
            leftButton.Click += delegate { SelectMouseButton(MouseButton.Left); };
            AutomationProperties.SetAutomationId(leftButton, "DragGestureLeftButton");
            mouseButtons.Children.Add(leftButton);
            Button rightButton = UiFactory.CommandButtonKey(
                "\uE7C9", SettingsTextKeys.DragGestureRightButton, false);
            rightButton.MinWidth = 108;
            rightButton.Click += delegate { SelectMouseButton(MouseButton.Right); };
            AutomationProperties.SetAutomationId(rightButton, "DragGestureRightButton");
            mouseButtons.Children.Add(rightButton);
            Grid.SetColumn(mouseButtons, 0);
            footer.Children.Add(mouseButtons);

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Button cancel = UiFactory.CommandButtonKey(
                "\uE711", SettingsTextKeys.ShortcutCancel, false);
            cancel.MinWidth = 92;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += delegate { CancelDialog(); };
            AutomationProperties.SetAutomationId(cancel, "DragGestureCancel");
            buttons.Children.Add(cancel);
            saveButton = UiFactory.CommandButtonKey(
                "\uE73E", SettingsTextKeys.ShortcutSave, true);
            saveButton.MinWidth = 92;
            saveButton.Click += delegate { CommitDialog(selectedMouseButton); };
            AutomationProperties.SetAutomationId(saveButton, "DragGestureSave");
            buttons.Children.Add(saveButton);
            Grid.SetColumn(buttons, 1);
            footer.Children.Add(buttons);
            root.Children.Add(footer);

            Border card = new Border
            {
                Focusable = true,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(22, 18, 22, 18),
                Margin = new Thickness(10),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Opacity = 0.22,
                    Color = Colors.Black
                },
                Child = root
            };
            AutomationProperties.SetAutomationId(card, "DragGestureDialog");
            Content = card;

            captureHook = new ShortcutKeyCaptureHook(this, OnHookCaptured, true);
            PreviewKeyDown += OnCapturePreviewKeyDown;
            Loaded += delegate
            {
                captureHook.Start();
                Activate();
                card.Focus();
                Keyboard.Focus(card);
            };
            Closed += delegate
            {
                captureHook.Stop();
                captureHook.Dispose();
            };

            SetCandidate(pendingKey, pendingModifiers);
        }

        /// <summary>确认后的前缀主键；None = 只有修饰键。</summary>
        internal Key SelectedKey { get; private set; }

        /// <summary>确认后的修饰键。</summary>
        internal ModifierKeys SelectedModifiers { get; private set; }

        /// <summary>用户点了“左键拖动/右键拖动”才有值；null = 保持原来的鼠标键。</summary>
        internal MouseButton? SelectedMouseButton { get; private set; }

        private void OnHookCaptured(Key key, ModifierKeys modifiers)
        {
            // 单独按 Alt 会让窗口短暂失去“活动”状态（系统菜单模式），但钩子确实是在本弹窗上按的：
            // 只看 IsLoaded，别把这次 Alt 丢掉。
            if (!IsLoaded)
            {
                return;
            }
            SetCandidate(key, modifiers);
        }

        private void OnCapturePreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                e.Handled = true;
                CancelDialog();
                return;
            }
            if (key == Key.Return)
            {
                e.Handled = true;
                CommitDialog(selectedMouseButton);
                return;
            }
            if (key == Key.LeftAlt || key == Key.RightAlt)
            {
                // WPF 在 Alt 刚按下时 Keyboard.Modifiers 可能还没更新，手动补上 Alt。
                SetCandidate(Key.None, Keyboard.Modifiers | ModifierKeys.Alt);
                e.Handled = true;
                return;
            }
            if (ShortcutKeyText.IsModifierKey(key))
            {
                // 只按修饰键也合法（默认 Shift 就是这样）；框里显示 Ctrl / Ctrl + Alt。
                // 松开 Alt 时 WPF 可能再送一次 LeftAlt + Modifiers=None，不能把刚按下的 Alt 清掉。
                ModifierKeys modifiers = Keyboard.Modifiers;
                if (key == Key.LeftAlt || key == Key.RightAlt)
                {
                    modifiers |= ModifierKeys.Alt;
                }
                else if (key == Key.LeftCtrl || key == Key.RightCtrl)
                {
                    modifiers |= ModifierKeys.Control;
                }
                else if (key == Key.LeftShift || key == Key.RightShift)
                {
                    modifiers |= ModifierKeys.Shift;
                }
                SetCandidate(Key.None, modifiers);
                e.Handled = true;
                return;
            }
            if (key == Key.None)
            {
                e.Handled = true;
                return;
            }
            SetCandidate(key, Keyboard.Modifiers);
            e.Handled = true;
        }

        private void SetCandidate(Key key, ModifierKeys modifiers)
        {
            ModifierKeys normalizedModifiers = ShortcutKeyText.NormalizeModifiers(modifiers);
            if (key == Key.None && normalizedModifiers == ModifierKeys.None)
            {
                // 松开修饰键/窗口短暂失活时系统可能补送一个“空候选”，
                // 不能把刚按下的 Alt/Shift 清成“未设置”。
                return;
            }
            Key normalizedKey = key == Key.None || ShortcutKeyText.IsModifierKey(key)
                ? Key.None
                : key;
            string error;
            bool valid = DragGestureSettings.TryValidatePrefix(
                normalizedKey, normalizedModifiers, out error);
            pendingKey = normalizedKey;
            pendingModifiers = normalizedModifiers;
            candidateValid = valid;
            candidateError = error;
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            string prefix;
            if (pendingKey != Key.None)
            {
                prefix = ShortcutKeyText.Format(pendingModifiers, pendingKey);
                captureText.Foreground = AppTheme.TextPrimary;
            }
            else if (pendingModifiers != ModifierKeys.None)
            {
                prefix = ShortcutKeyText.FormatModifiers(pendingModifiers);
                captureText.Foreground = AppTheme.TextPrimary;
            }
            else
            {
                prefix = AppText.T(SettingsTextKeys.ShortcutUnset);
                captureText.Foreground = AppTheme.TextSecondary;
            }

            // 捕获框直接显示保存后的完整手势：组合键 + 当前选中的鼠标键。
            // 没点左/右键按钮时，selectedMouseButton 仍是原鼠标键，所以这里显示的就是保存结果。
            string mouseName = selectedMouseButton == MouseButton.Right
                ? AppText.T(SettingsTextKeys.DragGestureMouseRightName)
                : AppText.T(SettingsTextKeys.DragGestureMouseLeftName);
            captureText.Text = prefix + " + " + mouseName;

            bool hasError = !string.IsNullOrWhiteSpace(candidateError);
            captureBox.BorderBrush = hasError
                ? AppTheme.Danger
                : (candidateValid ? AppTheme.Accent : AppTheme.Border);
            errorText.Text = hasError ? candidateError : string.Empty;
            errorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

            bool warnsTyping = DragGestureSettings.PrefixHasMainKey(pendingKey);
            warningText.Text = warnsTyping
                ? AppText.T(SettingsTextKeys.DragGestureWarningTyping)
                : string.Empty;
            warningText.Visibility = warnsTyping ? Visibility.Visible : Visibility.Collapsed;

            saveButton.IsEnabled = candidateValid;
        }

        /// <summary>只切换鼠标键选择：刷新捕获框文字，不确认、不关窗、不写盘。</summary>
        private void SelectMouseButton(MouseButton mouseButton)
        {
            selectedMouseButton = mouseButton == MouseButton.Right
                ? MouseButton.Right
                : MouseButton.Left;
            RefreshDisplay();
        }

        private void CommitDialog(MouseButton? mouseButton)
        {
            if (!candidateValid)
            {
                if (string.IsNullOrWhiteSpace(candidateError))
                {
                    candidateError = AppText.T(SettingsTextKeys.DragGestureErrorNoPrefix);
                    RefreshDisplay();
                }
                return;
            }
            SelectedKey = pendingKey;
            SelectedModifiers = pendingModifiers;
            SelectedMouseButton = mouseButton;
            try
            {
                DialogResult = true;
            }
            catch
            {
            }
            Close();
        }

        private void CancelDialog()
        {
            try
            {
                DialogResult = false;
            }
            catch
            {
            }
            Close();
        }
    }
}
