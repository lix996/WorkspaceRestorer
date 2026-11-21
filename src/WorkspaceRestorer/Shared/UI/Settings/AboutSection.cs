using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.Services.Updates;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 设置页的“关于”分区，固定在导航最下面（见 SettingsSection.PinnedToBottom）。
    /// 内容：程序身份、项目信息、检查更新；不显示数据目录（用户 2026-11-15 要求）。
    /// 只负责展示和点击接线；版本/作者/GitHub 在 AppInfo，检查更新在 Services/Updates。
    /// </summary>
    public sealed partial class MainWindow
    {
        private Button checkUpdateButton;
        private TextBlock updateStatusText;
        private Border updateStatusDot;

        /// <summary>关于的分区配置放在这里，设置页外壳只留通用规则和一行登记。</summary>
        private SettingsSection CreateAboutSettingsSection()
        {
            return new SettingsSection
            {
                Id = "about",
                Label = AppText.T(AboutTextKeys.Title),
                Icon = "\uE946",
                Description = AppText.T(AboutTextKeys.Description),
                Build = BuildAboutSettingsSection,
                Reset = null,
                PinnedToBottom = true
            };
        }

        private UIElement BuildAboutSettingsSection()
        {
            StackPanel section = new StackPanel();
            // 【2026-11-15 用户要求】关于标题下一句话 + 问号；这句本身就是一句话，问号里放同一句。
            section.Children.Add(BuildSettingsSectionHeaderKey(
                AboutTextKeys.Title,
                AboutTextKeys.Description,
                AboutTextKeys.Description));
            section.Children.Add(BuildAboutIdentityCard());
            section.Children.Add(BuildAboutInfoCard());
            section.Children.Add(BuildAboutUpdateCard());
            if (!AppInfo.IsGitHubConfigured && !AppInfo.IsEmailConfigured)
            {
                section.Children.Add(BuildAboutFooterHint());
            }
            return section;
        }

        /// <summary>程序身份：真实程序图标（窗口已经加载好的 data\图标.ico）、名称、版本行和一句话说明。</summary>
        private UIElement BuildAboutIdentityCard()
        {
            Grid card = new Grid();
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border iconHost = new Border
            {
                Width = 56,
                Height = 56,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                VerticalAlignment = VerticalAlignment.Center
            };
            ImageSource iconSource = Icon;
            if (iconSource != null)
            {
                iconHost.Child = new Image
                {
                    Source = iconSource,
                    Width = 44,
                    Height = 44,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else
            {
                iconHost.Child = new TextBlock
                {
                    Text = UiFactory.IconGlyph("\uE7E8"),
                    FontFamily = AppTheme.IconFont,
                    FontSize = 28,
                    Foreground = AppTheme.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            Grid.SetColumn(iconHost, 0);
            card.Children.Add(iconHost);

            StackPanel text = new StackPanel
            {
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            SelectableText name = new SelectableText
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            LocalizedText.Text(name, AboutTextKeys.ProductName);
            text.Children.Add(name);

            SelectableText version = new SelectableText
            {
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(0, 4, 0, 0)
            };
            LocalizedText.Text(
                version,
                AboutTextKeys.VersionLineFormat,
                AppInfo.Version,
                Environment.Is64BitProcess ? 64 : 32);
            text.Children.Add(version);

            SelectableText tagline = new SelectableText
            {
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            LocalizedText.Text(tagline, AboutTextKeys.Tagline);
            text.Children.Add(tagline);
            Grid.SetColumn(text, 1);
            card.Children.Add(text);

            return new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.SoftBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 12, 0, 0),
                Child = card
            };
        }

        /// <summary>
        /// 项目信息：两列两行（作者/邮箱 ｜ 版本/运行环境），第三行只保留左列的“项目主页”。
        /// 数据目录已去掉，所以右列第三格留空；这是用户 2026-11-15 明确要求的。
        /// </summary>
        private UIElement BuildAboutInfoCard()
        {
            Grid grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddAboutInfoCell(grid, 0, 0, AboutTextKeys.Author,
                BuildAboutInfoValue(
                    AppInfo.IsAuthorConfigured ? AppInfo.AuthorName : null,
                    AppInfo.IsAuthorConfigured ? null : AboutTextKeys.NotConfigured));
            AddAboutInfoCell(grid, 3, 0, AboutTextKeys.VersionLabel,
                BuildAboutInfoValue(AppInfo.Version, null));
            AddAboutInfoCell(grid, 0, 1, AboutTextKeys.Email,
                BuildAboutInfoValue(
                    AppInfo.IsEmailConfigured ? AppInfo.AuthorEmail : null,
                    AppInfo.IsEmailConfigured ? null : AboutTextKeys.NotConfigured));
            AddAboutInfoCell(grid, 3, 1, AboutTextKeys.EnvironmentLabel,
                BuildAboutInfoValueFormat(
                    AboutTextKeys.EnvironmentFormat,
                    Environment.Is64BitProcess ? 64 : 32));
            AddAboutInfoCell(grid, 0, 2, AboutTextKeys.ProjectHomeLabel,
                BuildAboutInfoValue(
                    AppInfo.IsGitHubConfigured ? AppInfo.GitHubRepositoryUrl : null,
                    AppInfo.IsGitHubConfigured ? null : AboutTextKeys.NotConfigured));

            // 两列之间的细线只覆盖前两行；第三行只有左列的项目主页。
            Border divider = new Border
            {
                Width = 1,
                Background = AppTheme.SoftBorder,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetColumn(divider, 2);
            Grid.SetRow(divider, 0);
            Grid.SetRowSpan(divider, 2);
            grid.Children.Add(divider);

            StackPanel content = new StackPanel();
            TextBlock title = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            LocalizedText.Text(title, AboutTextKeys.InfoTitle);
            content.Children.Add(title);
            content.Children.Add(grid);

            return new Border
            {
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 16, 0, 0),
                Child = content
            };
        }

        private static void AddAboutInfoCell(
            Grid grid,
            int labelColumn,
            int row,
            string labelKey,
            UIElement value,
            int valueColumnSpan = 1)
        {
            TextBlock label = new TextBlock
            {
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 10, 5)
            };
            LocalizedText.Text(label, labelKey);
            Grid.SetColumn(label, labelColumn);
            Grid.SetRow(label, row);
            grid.Children.Add(label);

            if (value == null)
            {
                return;
            }
            FrameworkElement element = value as FrameworkElement;
            if (element != null)
            {
                element.Margin = new Thickness(0, 5, 0, 5);
            }
            Grid.SetColumn(value, labelColumn + 1);
            Grid.SetRow(value, row);
            if (valueColumnSpan > 1)
            {
                Grid.SetColumnSpan(value, valueColumnSpan);
            }
            grid.Children.Add(value);
        }

        private static SelectableText CreateAboutInfoValue()
        {
            return new SelectableText
            {
                FontSize = 12,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
        }

        private static SelectableText BuildAboutInfoValue(string value, string valueKey)
        {
            SelectableText text = CreateAboutInfoValue();
            if (!string.IsNullOrEmpty(valueKey))
            {
                LocalizedText.Text(text, valueKey);
                text.Foreground = AppTheme.TextSecondary;
            }
            else
            {
                text.Text = value ?? string.Empty;
            }
            return text;
        }

        private static SelectableText BuildAboutInfoValueFormat(string key, params object[] args)
        {
            SelectableText text = CreateAboutInfoValue();
            LocalizedText.Text(text, key, args);
            return text;
        }

        /// <summary>检查更新：左边说明和状态，右边一个描边按钮；只有配置了入口才显示 GitHub/写邮件。</summary>
        private UIElement BuildAboutUpdateCard()
        {
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel();
            SelectableText title = new SelectableText
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            LocalizedText.Text(title, AboutTextKeys.CheckUpdates);
            text.Children.Add(title);

            SelectableText hint = new SelectableText
            {
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            LocalizedText.Text(hint, AboutTextKeys.UpdateHint);
            text.Children.Add(hint);

            StackPanel status = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Brush statusColor = UpdateChecker.IsConfigured ? AppTheme.TextSecondary : AppTheme.Warning;
            updateStatusDot = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = statusColor,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            status.Children.Add(updateStatusDot);
            updateStatusText = new TextBlock
            {
                FontSize = 11,
                Foreground = statusColor,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            LocalizedText.Text(
                updateStatusText,
                UpdateChecker.IsConfigured
                    ? AboutTextKeys.NotChecked
                    : AboutTextKeys.UpdateNotConfigured);
            status.Children.Add(updateStatusText);
            text.Children.Add(status);
            Grid.SetColumn(text, 0);
            head.Children.Add(text);

            checkUpdateButton = UiFactory.CommandButtonKey(
                "\uE72C", AboutTextKeys.CheckUpdates, false);
            checkUpdateButton.IsEnabled = UpdateChecker.IsConfigured;
            checkUpdateButton.VerticalAlignment = VerticalAlignment.Center;
            checkUpdateButton.Margin = new Thickness(16, 0, 0, 0);
            LocalizedText.AccessibleName(checkUpdateButton, AboutTextKeys.CheckUpdates);
            AccessibleToolTip.Attach(
                checkUpdateButton,
                TooltipDefinition.Named(
                    AboutTextKeys.CheckUpdates,
                    AboutTextKeys.CheckUpdates,
                    AboutTextKeys.UpdateHint,
                    null));
            checkUpdateButton.Click += delegate { CheckForUpdatesAsync(); };
            Grid.SetColumn(checkUpdateButton, 1);
            head.Children.Add(checkUpdateButton);

            StackPanel content = new StackPanel();
            content.Children.Add(head);

            StackPanel links = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };
            if (AppInfo.IsGitHubConfigured)
            {
                Button github = UiFactory.CommandButtonKey(
                    "\uE774", AboutTextKeys.GitHub, false);
                github.Margin = new Thickness(0, 0, 8, 0);
                LocalizedText.AccessibleName(github, AboutTextKeys.GitHub);
                AccessibleToolTip.Attach(
                    github,
                    TooltipDefinition.Named(
                        AboutTextKeys.GitHub,
                        AboutTextKeys.GitHub,
                        AboutTextKeys.GitHubHint,
                        null));
                github.Click += delegate { OpenGitHub(); };
                links.Children.Add(github);
            }
            if (AppInfo.IsEmailConfigured)
            {
                Button email = UiFactory.CommandButtonKey(
                    "\uE715", AboutTextKeys.WriteEmail, false);
                LocalizedText.AccessibleName(email, AboutTextKeys.WriteEmail);
                AccessibleToolTip.Attach(
                    email,
                    TooltipDefinition.Named(
                        AboutTextKeys.WriteEmail,
                        AboutTextKeys.WriteEmail,
                        AboutTextKeys.WriteEmailHint,
                        null));
                email.Click += delegate { OpenEmail(); };
                links.Children.Add(email);
            }
            if (links.Children.Count > 0)
            {
                content.Children.Add(links);
            }

            return new Border
            {
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 16, 0, 0),
                Child = content
            };
        }

        private static UIElement BuildAboutFooterHint()
        {
            SelectableText hint = UiFactory.SupportingTextKey(AboutTextKeys.FooterHint);
            hint.Margin = new Thickness(0, 12, 0, 0);
            return hint;
        }

        /// <summary>状态文字和左边小圆点一起换色，避免“绿字配橙点”。</summary>
        private void SetUpdateStatus(string text, Brush color)
        {
            if (updateStatusText != null)
            {
                updateStatusText.Text = text;
                updateStatusText.Foreground = color;
            }
            if (updateStatusDot != null)
            {
                updateStatusDot.Background = color;
            }
        }

        private void OpenGitHub()
        {
            if (!AppInfo.IsGitHubConfigured)
            {
                return;
            }
            ShellOpenResult result = ShellOpener.Open(AppInfo.GitHubRepositoryUrl);
            if (result.WasSuppressed)
            {
                return;
            }
            if (result.Success)
            {
                SetUpdateStatus(
                    AppText.T(AboutTextKeys.GitHubOpened),
                    AppTheme.Positive);
                return;
            }
            SetUpdateStatus(
                AppText.F(AboutTextKeys.OpenFailedFormat, result.Error),
                AppTheme.Danger);
        }

        private void OpenEmail()
        {
            if (!AppInfo.IsEmailConfigured)
            {
                return;
            }
            ShellOpenResult result = ShellOpener.Open("mailto:" + AppInfo.AuthorEmail);
            if (result.WasSuppressed)
            {
                return;
            }
            if (result.Success)
            {
                SetUpdateStatus(
                    AppText.T(AboutTextKeys.EmailOpened),
                    AppTheme.Positive);
                return;
            }
            SetUpdateStatus(
                AppText.F(AboutTextKeys.OpenFailedFormat, result.Error),
                AppTheme.Danger);
        }

        private async void CheckForUpdatesAsync()
        {
            if (checkUpdateButton == null || !UpdateChecker.IsConfigured)
            {
                return;
            }
            checkUpdateButton.IsEnabled = false;
            SetUpdateStatus(AppText.T(AboutTextKeys.Checking), AppTheme.TextSecondary);
            try
            {
                UpdateResult result = await Task.Run(delegate
                {
                    return UpdateChecker.Check(AppInfo.Version);
                });
                if (result.Outcome == UpdateCheckOutcome.NotConfigured)
                {
                    SetUpdateStatus(
                        AppText.T(AboutTextKeys.UpdateNotConfigured),
                        AppTheme.Warning);
                }
                else if (result.Outcome == UpdateCheckOutcome.Suppressed)
                {
                    SetUpdateStatus(
                        AppText.T(AboutTextKeys.Suppressed),
                        AppTheme.TextSecondary);
                }
                else if (result.Outcome == UpdateCheckOutcome.UpToDate)
                {
                    SetUpdateStatus(
                        AppText.F(AboutTextKeys.UpToDate, result.LatestVersion),
                        AppTheme.Positive);
                }
                else if (result.Outcome == UpdateCheckOutcome.UpdateAvailable)
                {
                    SetUpdateStatus(
                        AppText.F(
                            AboutTextKeys.UpdateAvailableFormat,
                            result.LatestVersion),
                        AppTheme.Accent);
                    bool open = WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                        Window.GetWindow(this),
                        AppText.T(AboutTextKeys.UpdateDialogTitle),
                        AppText.F(
                            AboutTextKeys.UpdateDialogMessageFormat,
                            result.CurrentVersion,
                            result.LatestVersion),
                        TrimReleaseNotes(result.ReleaseNotes));
                    if (open && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                    {
                        ShellOpenResult openResult = ShellOpener.Open(result.ReleaseUrl);
                        if (!openResult.Success)
                        {
                            SetUpdateStatus(
                                AppText.F(
                                    AboutTextKeys.CheckFailedFormat,
                                    openResult.Error),
                                AppTheme.Danger);
                        }
                    }
                }
                else
                {
                    SetUpdateStatus(
                        AppText.F(AboutTextKeys.CheckFailedFormat, result.Error),
                        AppTheme.Danger);
                }
            }
            finally
            {
                checkUpdateButton.IsEnabled = UpdateChecker.IsConfigured;
            }
        }

        private static string TrimReleaseNotes(string notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return string.Empty;
            }
            string text = notes.Trim();
            const int limit = 600;
            if (text.Length > limit)
            {
                return text.Substring(0, limit) + "…";
            }
            return text;
        }
    }
}
