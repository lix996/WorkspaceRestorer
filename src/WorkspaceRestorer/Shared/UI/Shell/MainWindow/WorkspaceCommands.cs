using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>
        /// 空白处的右键菜单：“新建空白方案 ＋ 往当前方案粘贴路径/网址新增…”。
        /// 用户要求（2026-10-09 18:36）「自启动应用」也要有这两个功能，所以抽成公共方法，
        /// 两个工作区共用（各自的工作区内容区都挂这一份菜单）。
        /// </summary>
        private ContextMenu CreateBlankContextMenu()
        {
            // 【2026-10-19】菜单装配走公共件；动作仍是本工作区自己的方法。
            return Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(
                    AppText.T(CommonTextKeys.MenuNewBlankProfile),
                    delegate { CreateBlankStartupProfile(); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(
                    AppText.T(CommonTextKeys.MenuPasteAddToCurrentProfile),
                    delegate { AddItemByClipboard(controller.ActiveProfile); }));
        }

        /// <summary>新建一份空白工作方案（右键空白处用；重名自动顺延）。</summary>
        private void CreateBlankStartupProfile()
        {
            StartupProfile created = controller.CreateProfile(
                MakeUniqueProfileName(GetNewStartupProfileName()));
            if (created == null)
            {
                return;
            }
            RefreshProfileList();
            statusText.Text = AppText.F(CommonTextKeys.BlankProfileCreatedFormat, created.Name);
        }

        private static SessionItem CreatePastedSessionItem(string input, bool minimized, out string error)
        {
            error = null;
            // 【2026-10-18 用户反馈】“复制的网址带有双引号都无法检测到，并添加到方案内”。
            // 现在先把外层引号/书名号/Markdown 链接剥掉再判定（规则在 WebAddressTools 里，只有一份）。
            if (WebAddressTools.LooksLikeCopiedProfile(input))
            {
                // 剪贴板里是「启动与恢复」Ctrl+C 复制的工作方案文本（CSK1|…），不是路径/网址。
                error = AppText.T(CommonTextKeys.PasteErrorProfileClipboard);
                return null;
            }
            string text = WebAddressTools.Normalize(input);

            string preparedUrl;
            if (WebAddressTools.TryPrepareWebAddress(text, out preparedUrl))
            {
                Uri uri;
                string displayName = preparedUrl;
                if (Uri.TryCreate(preparedUrl, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
                {
                    displayName = uri.Host;
                }
                return new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = displayName,
                    Url = preparedUrl,
                    TargetPath = preparedUrl,
                    IsEnabled = true
                };
            }

            if (Directory.Exists(text))
            {
                string folderName = Path.GetFileName(text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    folderName = text;
                }
                return new SessionItem
                {
                    Kind = SessionItemKind.ExplorerFolder,
                    DisplayName = folderName,
                    WindowTitle = folderName,
                    ExecutablePath = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe"),
                    TargetPath = text,
                    WindowPlacement = BuildPastedPlacement(minimized),
                    IsEnabled = true
                };
            }

            if (File.Exists(text))
            {
                string extension = (Path.GetExtension(text) ?? string.Empty).ToLowerInvariant();
                if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    return new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        DisplayName = Path.GetFileName(text),
                        WindowTitle = Path.GetFileName(text),
                        ExecutablePath = text,
                        TargetPath = text,
                        WindowPlacement = BuildPastedPlacement(minimized),
                        IsEnabled = true
                    };
                }
                return new SessionItem
                {
                    Kind = SessionItemKind.Document,
                    DisplayName = Path.GetFileName(text),
                    TargetPath = text,
                    IsEnabled = true
                };
            }

            error = AppText.T(CommonTextKeys.PasteErrorInvalidTarget);
            return null;
        }

        private static SavedWindowPlacement BuildPastedPlacement(bool minimized)
        {
            return new SavedWindowPlacement
            {
                HasBounds = false,
                WindowState = minimized ? "Minimized" : "Normal",
                WindowStateBeforeMinimize = minimized ? "Normal" : null,
                MinimizeChoiceExplicit = minimized
            };
        }

        private sealed class PasteItemDialog : Window
        {
            private readonly TextBox valueBox;
            private readonly RadioButton minimizeRadio;

            public string Value { get { return valueBox == null ? string.Empty : valueBox.Text; } }

            public bool OpenMinimized { get { return minimizeRadio != null && minimizeRadio.IsChecked == true; } }

            public PasteItemDialog()
            {
                LocalizedText.Title(this, CommonTextKeys.PasteTitle);
                Width = 560;
                SizeToContent = SizeToContent.Height;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;
                Background = AppTheme.WindowBackground;
                FontFamily = AppTheme.UiFont;

                StackPanel root = new StackPanel { Margin = new Thickness(18) };
                TextBlock prompt = new TextBlock
                {
                    FontSize = 13,
                    Foreground = AppTheme.TextPrimary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                LocalizedText.Text(prompt, CommonTextKeys.PastePrompt);
                root.Children.Add(prompt);
                valueBox = new TextBox
                {
                    Height = 30,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                // 【2026-10-18 用户反馈】“再次打开，还保留上一次的内容；搜索、添加类不需要保留上一次的内容。”
                // 以前这里会把**当前剪贴板**预填进来 —— 剪贴板没变（比如刚复制过一个桌面快捷方式），
                // 下次打开又看到同一串东西，像是“记住了上次的输入”。新增类对话框现在一律从空白开始；
                // 要粘贴就按 Ctrl+V（这个文本框本来就有系统右键菜单：剪切/复制/粘贴/全选）。
                valueBox.Text = string.Empty;
                valueBox.SelectAll();
                root.Children.Add(valueBox);

                minimizeRadio = new RadioButton
                {
                    IsChecked = true,
                    Margin = new Thickness(0, 14, 0, 0)
                };
                LocalizedText.Content(minimizeRadio, CommonTextKeys.PasteMinimized);
                RadioButton normalRadio = new RadioButton
                {
                    IsChecked = false,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                LocalizedText.Content(normalRadio, CommonTextKeys.PasteNormal);
                root.Children.Add(minimizeRadio);
                root.Children.Add(normalRadio);

                StackPanel buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 18, 0, 0)
                };
                Button addButton = new Button
                {
                    Width = 92,
                    Margin = new Thickness(0, 0, 8, 0),
                    Padding = new Thickness(0, 6, 0, 6),
                    Cursor = Cursors.Hand
                };
                LocalizedText.Content(addButton, CommonTextKeys.PasteAdd);
                addButton.Click += delegate { DialogResult = true; };
                Button cancelButton = new Button
                {
                    Width = 92,
                    Padding = new Thickness(0, 6, 0, 6),
                    Cursor = Cursors.Hand
                };
                LocalizedText.Content(cancelButton, ShellTextKeys.DialogCancel);
                cancelButton.Click += delegate { DialogResult = false; };
                buttons.Children.Add(addButton);
                buttons.Children.Add(cancelButton);
                root.Children.Add(buttons);
                Content = root;
            }
        }

        /// <summary>
        /// Brings the keeper window to the front reliably.  A plain WPF
        /// Activate can be rejected by the Windows foreground lock after the
        /// capture helper or a browser took the foreground, so the window is
        /// raised once. Operation cards own topmost until dismissed, so no
        /// delayed pulse can clear a newer operation's topmost state.
        /// </summary>
        private static void BringToFront(Window window)
        {
            if (window == null)
            {
                return;
            }

            try
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                window.Show();

                IntPtr windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (windowHandle != IntPtr.Zero)
                {
                    WorkspaceRestorer.Services.NativeMethods.SetForegroundWindow(windowHandle);
                }
                window.Activate();
                window.Focus();
            }
            catch
            {
                // Best effort; the banner still appears if activation fails.
            }
        }

        private void RefreshSettings()
        {
            loadingSettings = true;
            try
            {
                bool autoStart = controller.Settings.AutoStart;
                StartupRegistrationInfo startupStatus = controller.StartupStatus;
                if (startupStatus != null
                    && !startupStatus.WasSuppressed
                    && string.IsNullOrWhiteSpace(startupStatus.Error))
                {
                    autoStart = startupStatus.IsEnabled;
                }
                autoStartCheckBox.IsChecked = autoStart;
                autoRestoreCheckBox.IsChecked = controller.Settings.AutoRestore;
                // 开机自启动与自动恢复彼此独立，这里不再因为自动恢复未开而置灰。
                autoStartCheckBox.IsEnabled = true;
                loadGateCheckBox.IsChecked = controller.Settings.WaitForLoad;
                scanTerminalsCheckBox.IsChecked = controller.Settings.ScanTerminalsAndLocalUrls;
                initialDelayBox.Text = controller.Settings.InitialDelaySeconds.ToString();
                itemDelayBox.Text = controller.Settings.ItemDelaySeconds.ToString();
                webPageDelayBox.Text = controller.Settings.WebPageDelaySeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                RefreshCommonCpuSettings();
                RefreshCommonMemorySettings();
                RefreshAppearanceSettings();
                applicationsWaitForLoadCheckBox.IsChecked = controller.Settings.ApplicationsWaitForLoad;
                applicationsInitialDelayBox.Text = controller.Settings.ApplicationsInitialDelaySeconds.ToString();
                applicationsItemDelayBox.Text = controller.Settings.ApplicationsItemDelaySeconds.ToString();
                applicationsWebPageDelayBox.Text = controller.Settings.ApplicationsWebPageDelaySeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                RefreshLayoutSettings();
                // 命名框已搬进三个设置分区：这里统一回填，页面缓存复用后也能看到最新值。
                RefreshStartupNamingRow();
                RefreshApplicationsNamingRow();
                double speed = Math.Max(0.5, Math.Min(3.0, controller.Settings.DragScrollSpeed));
                if (dragScrollSpeedSlider != null)
                {
                    dragScrollSpeedSlider.Value = speed;
                }
                if (dragScrollSpeedValueText != null)
                {
                    // 和提交/拖动时同一个格式：1.0× / 1.26×（最多两位小数），
                    // 刷新设置不会把 1.26× 显示回 1.3×。
                    dragScrollSpeedValueText.Text = speed.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "×";
                }
                // 【2026-10-17 修】「自启动应用」分区那个勾选框以前**从来没有回填过**：
                // 它只在用户点击时才写设置，刷新时没人把 settings.json 里的值写回界面，
                // 于是不管实际开没开，它永远显示“未勾选”（点一下“没反应”、再点一下反而关掉）。
                // 现在按设置回填；回填期间把 suppression 标记立起来，避免“设置自己改自己”。
                suppressApplicationsAutoRestoreChange = true;
                suppressApplicationsWaitForLoadChange = true;
                suppressStartupPriorityChange = true;
                try
                {
                    if (applicationsAutoRestoreCheckBox != null)
                    {
                        applicationsAutoRestoreCheckBox.IsChecked = controller.Settings.ApplicationsAutoRestore;
                    }
                    if (applicationsWaitForLoadCheckBox != null)
                    {
                        applicationsWaitForLoadCheckBox.IsChecked = controller.Settings.ApplicationsWaitForLoad;
                    }
                    if (startupPriorityBox != null)
                    {
                        startupPriorityBox.SelectedIndex = controller.Settings.ApplicationsFirst ? 1 : 0;
                    }
                }
                finally
                {
                    suppressApplicationsAutoRestoreChange = false;
                    suppressApplicationsWaitForLoadChange = false;
                    suppressStartupPriorityChange = false;
                }
            }
            finally
            {
                loadingSettings = false;
            }
        }

        private void RefreshLayoutSummary()
        {
            layoutSummary.Text = AppText.T(ShellTextKeys.LayoutSummary);
            RefreshDesktopLayoutCards();
        }

        private async void SaveCurrentWork()
        {
            if (saveOperationInProgress)
            {
                return;
            }
            // Every explicit "save current work" creates a new named profile so
            // earlier profiles are never overwritten; the newest one becomes
            // the active profile used for logon auto-restore.  The name comes
            // from 设置 → 启动与恢复 → 新工作方案名（没自定义时用默认名）。
            await CreateStartupProfileAsync();
        }

        private static bool IsLayoutTestMode()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"),
                "1",
                StringComparison.Ordinal);
        }

        private async void RestoreDesktopLayoutFromButtonAsync()
        {
            // 和卡片“恢复此布局”共用同一个单入口：先互斥检查、再截快照、成功后才记撤销。
            await RestoreDesktopLayoutCoreAsync(null);
        }

        // 【2026-10-17 删除】这里原来是 MainWindow.AddSelectedDesktopEntries()：
        // 把扫描结果里勾选的项目加进 **「启动与恢复」的工作方案**（controller.ActiveProfile），
        // 并且一条都没勾时弹系统 MessageBox。它唯一的调用方是「自启动应用」页工具栏上的
        // 「加入启动清单」按钮 —— 也就是说那个按钮一直在改错工作区的数据。
        // 自启动应用已经有自己的实现（AddCheckedDesktopEntriesToActiveProfile →
        // AddDesktopEntriesToCurrentApplicationPlan，并进当前自启动方案），
        // 按钮改接那个实现之后，这个方法就没人用了，按“不留僵尸代码”删掉。

        private void CommitTiming()
        {
            int initial = ParseNumber(initialDelayBox.Text, controller.Settings.InitialDelaySeconds,
                ApplicationController.ControllerSettings.InitialDelayMaximumSeconds);
            int item = ParseNumber(itemDelayBox.Text, controller.Settings.ItemDelaySeconds);
            double webPage = ParseDecimal(webPageDelayBox.Text, controller.Settings.WebPageDelaySeconds);
            controller.UpdateTiming(initial, item, webPage);
            RefreshSettings();
        }

        private void CommitApplicationsTiming()
        {
            int initial = ParseNumber(applicationsInitialDelayBox.Text, controller.Settings.ApplicationsInitialDelaySeconds,
                ApplicationController.ControllerSettings.InitialDelayMaximumSeconds);
            int item = ParseNumber(applicationsItemDelayBox.Text, controller.Settings.ApplicationsItemDelaySeconds);
            double webPage = ParseDecimal(applicationsWebPageDelayBox.Text, controller.Settings.ApplicationsWebPageDelaySeconds);
            controller.UpdateApplicationsTiming(initial, item, webPage);
            RefreshSettings();
        }

        /// <summary>焦点是否在一个正在编辑的文本框里（这时 Ctrl+C/V 应交给文本编辑）。</summary>
        /// <summary>
        /// 从交付文件 `data\图标.ico` 给窗口装图标（标题栏 / 任务栏 / Alt-Tab 都用它）。
        /// 文件缺失或读不出来就安静地跳过（exe 里还嵌着同一个图标，Window.Icon 为空时 WPF 会用 exe 的）。
        /// </summary>
        private void TryApplyWindowIcon()
        {
            try
            {
                string path = WorkspaceRestorer.Infrastructure.AppPaths.AppIconFile;
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                {
                    return;
                }
                System.Windows.Media.Imaging.BitmapImage image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                Icon = image;
            }
            catch (Exception exception)
            {
                AppLog.Write("读取程序图标失败（会用 exe 里嵌的图标）。", exception);
            }
        }

        private bool IsTextEditingFocus()
        {
            try
            {
                // 【2026-11-15 P1】SelectableText 已不是 TextBox；没有选中任何字时
                // 不算“在输入框里”，否则点一下说明文字后全局 Ctrl+C / Ctrl+V 会被它吃掉。
                SelectableText selectable = Keyboard.FocusedElement as SelectableText;
                if (selectable != null)
                {
                    return selectable.SelectionLength > 0;
                }
                TextBox box = Keyboard.FocusedElement as TextBox;
                if (box != null)
                {
                    return true;
                }
                // 就地改名用的编辑器也是 TextBox；再兜一层按类型名判断，避免别的输入控件漏掉。
                DependencyObject focused = Keyboard.FocusedElement as DependencyObject;
                return focused != null && focused is System.Windows.Controls.Primitives.TextBoxBase;
            }
            catch
            {
                return false;
            }
        }

    }
}
