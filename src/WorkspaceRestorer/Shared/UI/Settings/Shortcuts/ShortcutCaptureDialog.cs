using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 快捷键录键弹窗（只服务设置页的“快捷键”分区）。
    ///
    /// 2026-11-16 用户要求按 ComfyUI 的编辑窗口做：
    ///   标题 + “为以下操作设置快捷键：” + 命令名 + 捕获框 + 取消/保存。
    /// 只负责捕获和显示；键位合法性、冲突判断仍走 ShortcutCatalog / ShortcutKeyText，
    /// 保存和落盘仍由调用方 ApplicationController 处理。
    ///
    /// 2026-11-17 用户要求弹窗自己带撤销/重做：
    ///   · 打开弹窗时的原键位是历史第 0 条，之后每录成一个完整组合键追加一条；
    ///   · 左下角两个小图标按钮，到头时置灰；撤销后重新录键会清空可重做的那一段；
    ///   · 只按修饰键不产生历史；保存/取消/Enter/Esc 行为不变，
    ///     不占用 Ctrl+Z / Ctrl+Shift+Z（这两个键仍可被录成快捷键）。
    /// </summary>
    internal sealed class ShortcutCaptureDialog : Window
    {
        private readonly string commandId;
        private readonly int bindingIndex;
        private readonly Border captureBox;
        private readonly SelectableText captureText;
        private readonly TextBlock warningText;
        private readonly TextBlock errorText;
        private readonly Button saveButton;
        private readonly ShortcutKeyCaptureHook captureHook;
        private readonly List<ShortcutStep> history = new List<ShortcutStep>();
        private int historyIndex;
        private Button undoButton;
        private Button redoButton;

        private Key pendingKey = Key.None;
        private ModifierKeys pendingModifiers = ModifierKeys.None;
        private bool candidateValid;
        private string candidateError;

        internal ShortcutCaptureDialog(
            Window owner,
            string commandId,
            string commandTitle,
            int bindingIndex,
            bool adding,
            Key initialKey,
            ModifierKeys initialModifiers)
        {
            this.commandId = commandId;
            this.bindingIndex = bindingIndex;
            pendingKey = initialKey == Key.None ? Key.None : initialKey;
            pendingModifiers = ShortcutKeyText.NormalizeModifiers(initialModifiers);
            // 历史第 0 条永远是打开弹窗时的原键位（新增时是“未设置”）。
            history.Add(new ShortcutStep(pendingKey, pendingModifiers));
            historyIndex = 0;

            // 无边框卡片：和参考图一样是“页面中间弹出的一块卡片”，不是系统标题栏窗口。
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 540;
            ShowInTaskbar = false;
            FontFamily = AppTheme.UiFont;
            Title = AppText.T(adding
                ? SettingsTextKeys.ShortcutAddTitle
                : SettingsTextKeys.ShortcutEditTitle);
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
                Text = AppText.T(adding
                    ? SettingsTextKeys.ShortcutAddTitle
                    : SettingsTextKeys.ShortcutEditTitle),
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
            AutomationProperties.SetAutomationId(pressHint, "ShortcutCaptureHint");
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
            AutomationProperties.SetAutomationId(close, "ShortcutCaptureClose");
            Grid.SetColumn(close, 2);
            header.Children.Add(close);
            root.Children.Add(header);

            root.Children.Add(new TextBlock
            {
                Text = AppText.T(adding
                    ? SettingsTextKeys.ShortcutAddBody
                    : SettingsTextKeys.ShortcutEditBody),
                FontSize = AppFonts.Body,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 16, 0, 0)
            });
            root.Children.Add(new TextBlock
            {
                Text = commandTitle ?? string.Empty,
                FontSize = AppFonts.DialogTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });

            captureText = new SelectableText
            {
                Text = string.Empty,
                FontSize = AppFonts.DialogTitle,
                Foreground = AppTheme.TextSecondary,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                // 只做鼠标拖选 + 右键复制，不抢键盘焦点：键盘始终用于录键。
                Focusable = false
            };
            AutomationProperties.SetAutomationId(captureText, "ShortcutCaptureText");
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
            AutomationProperties.SetAutomationId(captureBox, "ShortcutCaptureBox");
            root.Children.Add(captureBox);

            errorText = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.Danger,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(errorText, "ShortcutCaptureError");
            root.Children.Add(errorText);

            warningText = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.Warning,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetAutomationId(warningText, "ShortcutCaptureWarning");
            root.Children.Add(warningText);

            // 底部：左边撤销/重做两个小图标按钮，右边取消/保存；两端各占一列，中间留空。
            Grid buttonRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel historyButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            undoButton = BuildHistoryButton(
                "\uE7A7", SettingsTextKeys.ShortcutUndo, "ShortcutCaptureUndo", UndoHistory);
            redoButton = BuildHistoryButton(
                "\uE7A6", SettingsTextKeys.ShortcutRedo, "ShortcutCaptureRedo", RedoHistory);
            redoButton.Margin = new Thickness(6, 0, 0, 0);
            historyButtons.Children.Add(undoButton);
            historyButtons.Children.Add(redoButton);
            Grid.SetColumn(historyButtons, 0);
            buttonRow.Children.Add(historyButtons);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button cancel = UiFactory.CommandButtonKey(
                "\uE711", SettingsTextKeys.ShortcutCancel, false);
            cancel.MinWidth = 96;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += delegate { CancelDialog(); };
            AutomationProperties.SetAutomationId(cancel, "ShortcutCaptureCancel");
            buttons.Children.Add(cancel);
            saveButton = UiFactory.CommandButtonKey(
                "\uE73E", SettingsTextKeys.ShortcutSave, true);
            saveButton.MinWidth = 96;
            saveButton.IsEnabled = false;
            saveButton.Click += delegate { CommitDialog(); };
            AutomationProperties.SetAutomationId(saveButton, "ShortcutCaptureSave");
            buttons.Children.Add(saveButton);
            Grid.SetColumn(buttons, 2);
            buttonRow.Children.Add(buttons);
            root.Children.Add(buttonRow);

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
            AutomationProperties.SetAutomationId(card, "ShortcutCaptureDialog");
            Content = card;

            captureHook = new ShortcutKeyCaptureHook(this, OnHookCaptured);
            PreviewKeyDown += OnCapturePreviewKeyDown;
            Loaded += delegate
            {
                // 钩子只在弹窗打开期间存在；关窗/取消/保存都会在 Closed 里卸载。
                captureHook.Start();
                Activate();
                // Enter/Esc 走 WPF 路由；把焦点放在卡片上，避免焦点留在被禁用的主窗口。
                card.Focus();
                Keyboard.Focus(card);
            };
            Closed += delegate
            {
                captureHook.Stop();
                captureHook.Dispose();
            };

            // 打开时就把原键位显示在捕获框里（新增时为“未设置”），别让用户以为要重新按。
            Revalidate();
        }

        /// <summary>用户确认后按下的主键。</summary>
        internal Key SelectedKey { get; private set; }

        /// <summary>用户确认后按下的修饰键。</summary>
        internal ModifierKeys SelectedModifiers { get; private set; }

        private void OnHookCaptured(Key key, ModifierKeys modifiers)
        {
            // 单独按 Alt 时窗口可能短暂失活（系统菜单模式），钩子仍是在本弹窗上按的。
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
                if (candidateValid)
                {
                    CommitDialog();
                }
                else
                {
                    candidateError = AppText.T(SettingsTextKeys.ShortcutErrorNoKey);
                    RefreshDisplay();
                }
                return;
            }
            if (ShortcutKeyText.IsModifierKey(key))
            {
                // 和参考图一致：只按住 Ctrl 时，框里就显示 Ctrl，不显示“…”。
                pendingKey = Key.None;
                pendingModifiers = ShortcutKeyText.NormalizeModifiers(Keyboard.Modifiers);
                candidateValid = false;
                candidateError = null;
                RefreshDisplay();
                e.Handled = true;
                return;
            }
            if (key == Key.None)
            {
                e.Handled = true;
                return;
            }
            // Alt+字母走 Key.System，WPF 的 Keyboard.Modifiers 有时还没把 Alt 算进去。
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (e.Key == Key.System)
            {
                modifiers |= ModifierKeys.Alt;
            }
            SetCandidate(key, modifiers);
            e.Handled = true;
        }

        private void SetCandidate(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None || ShortcutKeyText.IsModifierKey(key))
            {
                return;
            }
            pendingKey = key;
            pendingModifiers = ShortcutKeyText.NormalizeModifiers(modifiers);
            RecordHistory(pendingKey, pendingModifiers);
            Revalidate();
        }

        /// <summary>
        /// 记一条历史：只记完整组合键（带主键）。连续按同一个组合不重复记；
        /// 撤销到中间后再录新键，会先丢掉后面的重做分支，保证“撤销后重录 = 清空重做”。
        /// </summary>
        private void RecordHistory(Key key, ModifierKeys modifiers)
        {
            if (key == Key.None || ShortcutKeyText.IsModifierKey(key))
            {
                return;
            }
            ShortcutStep current = history[historyIndex];
            if (current.Key == key && current.Modifiers == modifiers)
            {
                return;
            }
            if (historyIndex < history.Count - 1)
            {
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            }
            history.Add(new ShortcutStep(key, modifiers));
            historyIndex = history.Count - 1;
        }

        private void UndoHistory()
        {
            if (historyIndex <= 0)
            {
                return;
            }
            historyIndex--;
            ApplyHistoryStep();
        }

        private void RedoHistory()
        {
            if (historyIndex >= history.Count - 1)
            {
                return;
            }
            historyIndex++;
            ApplyHistoryStep();
        }

        /// <summary>切到历史里的某一条，并像重新录键一样重新校验冲突/错误/警告/保存按钮。</summary>
        private void ApplyHistoryStep()
        {
            ShortcutStep step = history[historyIndex];
            pendingKey = step.Key;
            pendingModifiers = step.Modifiers;
            Revalidate();
        }

        /// <summary>按当前 pendingKey/pendingModifiers 重算合法性，并刷新提示与按钮状态。</summary>
        private void Revalidate()
        {
            string error = null;
            bool valid = false;
            if (pendingKey != Key.None)
            {
                if (ShortcutKeyText.IsReservedKey(pendingKey))
                {
                    error = AppText.F(
                        SettingsTextKeys.ShortcutErrorReservedFormat,
                        ShortcutKeyText.KeyName(pendingKey));
                }
                else
                {
                    valid = ShortcutCatalog.ValidateCandidate(
                        commandId, pendingKey, pendingModifiers, bindingIndex, out error);
                }
            }
            candidateValid = valid;
            candidateError = error;
            RefreshDisplay();
        }

        private void RefreshDisplay()
        {
            if (pendingKey != Key.None)
            {
                captureText.Text = ShortcutKeyText.Format(pendingModifiers, pendingKey);
                captureText.Foreground = AppTheme.TextPrimary;
            }
            else if (pendingModifiers != ModifierKeys.None)
            {
                captureText.Text = ShortcutKeyText.FormatModifiers(pendingModifiers);
                captureText.Foreground = AppTheme.TextPrimary;
            }
            else
            {
                captureText.Text = AppText.T(SettingsTextKeys.ShortcutUnset);
                captureText.Foreground = AppTheme.TextSecondary;
            }
            bool hasError = !string.IsNullOrWhiteSpace(candidateError);
            captureBox.BorderBrush = hasError
                ? AppTheme.Danger
                : (candidateValid ? AppTheme.Accent : AppTheme.Border);
            errorText.Text = hasError ? candidateError : string.Empty;
            errorText.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

            // 只有一个字母/数字主键：合法但不拦截，只提示打字时可能误触（用户 2026-11-16 拍板）。
            bool warnsTyping = candidateValid
                && ShortcutKeyText.NeedsTypingWarning(pendingKey, pendingModifiers);
            warningText.Text = warnsTyping
                ? AppText.T(SettingsTextKeys.ShortcutSingleKeyWarning)
                : string.Empty;
            warningText.Visibility = warnsTyping ? Visibility.Visible : Visibility.Collapsed;
            saveButton.IsEnabled = candidateValid;
            // 历史到头就置灰：撤销到原键位后不能再撤，重做到最新一条后不能再重做。
            if (undoButton != null)
            {
                undoButton.IsEnabled = historyIndex > 0;
            }
            if (redoButton != null)
            {
                redoButton.IsEnabled = historyIndex < history.Count - 1;
            }
        }

        private void CommitDialog()
        {
            if (!candidateValid || pendingKey == Key.None)
            {
                if (string.IsNullOrWhiteSpace(candidateError))
                {
                    candidateError = AppText.T(SettingsTextKeys.ShortcutErrorNoKey);
                    RefreshDisplay();
                }
                return;
            }
            SelectedKey = pendingKey;
            SelectedModifiers = pendingModifiers;
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

        /// <summary>底部左侧的撤销/重做小图标按钮：32×32，与取消/保存分开一列。</summary>
        private static Button BuildHistoryButton(
            string icon,
            string tooltipKey,
            string automationId,
            Action click)
        {
            Button button = UiFactory.IconButton(
                icon,
                TooltipDefinition.Plain(tooltipKey, null));
            button.Width = 32;
            button.Height = 32;
            button.MinWidth = 32;
            button.MinHeight = 32;
            button.Padding = new Thickness(0);
            button.IsEnabled = false;
            button.Click += delegate { click(); };
            AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        /// <summary>历史里的一条：完整组合键（主键 + 修饰键）；合法性在切换时现算。</summary>
        private struct ShortcutStep
        {
            internal readonly Key Key;
            internal readonly ModifierKeys Modifiers;

            internal ShortcutStep(Key key, ModifierKeys modifiers)
            {
                Key = key;
                Modifiers = modifiers;
            }
        }
    }
}
