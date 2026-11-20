using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
namespace WorkspaceRestorer.UI
{
    // 设置页：注册式分区（总设置 / 启动与恢复 / 自启动应用 / 图标位置保存 /
    // 快捷键 / 通用 / 关于）。关于固定画在导航最下面，见 SettingsSection.PinnedToBottom。
    // （从 MainWindow.cs 拆分出来，便于各自维护；同为 partial class MainWindow。）
    public sealed partial class MainWindow
    {
        /// <summary>One row of the settings navigation, plus how to build its page.</summary>
        private sealed class SettingsSection
        {
            public string Id;
            public string Label;
            public string Icon;
            public string Description;
            public Func<UIElement> Build;
            /// <summary>Resets only this section's options; null when it has none.</summary>
            public System.Action Reset;
            /// <summary>true = 在导航里这一项前面加一条分隔线，用来分组“应用级设置”。</summary>
            public bool SeparatorBefore;
            /// <summary>
            /// true = 固定在导航最下面。渲染时先画普通分区，再补一条分隔线，最后才画这一组；
            /// 所以以后在列表中间或上面新增普通分区，都不会排到它下面。
            /// </summary>
            public bool PinnedToBottom;
        }

        /// <summary>
        /// Declarative list of settings sections, in the order they appear.
        /// </summary>
        private List<SettingsSection> CreateSettingsSections()
        {
            List<SettingsSection> sections = new List<SettingsSection>();
            sections.Add(new SettingsSection
            {
                Id = "general",
                Label = AppText.T(SettingsTextKeys.NavGeneral),
                Icon = "\uE713",
                Description = AppText.T(SettingsTextKeys.NavGeneralDescription),
                Build = BuildGeneralSettingsSection,
                Reset = controller.ResetGeneralSettings
            });
            sections.Add(new SettingsSection
            {
                Id = "startup",
                Label = AppText.T(SettingsTextKeys.NavStartup),
                Icon = "\uE7E8",
                Description = AppText.T(SettingsTextKeys.NavStartupDescription),
                Build = BuildStartupSettingsSection,
                Reset = controller.ResetStartupSettings
            });
            sections.Add(new SettingsSection
            {
                Id = "applications",
                Label = AppText.T(SettingsTextKeys.NavApplications),
                Icon = "\uE721",
                Description = AppText.T(SettingsTextKeys.NavApplicationsDescription),
                Build = BuildApplicationsSettingsSection,
                Reset = controller.ResetApplicationsSettings
            });
            // 用户要求（2026-10-23）：设置分区也跟随左栏顺序，自启动应用放在图标位置保存前面。
            sections.Add(new SettingsSection
            {
                Id = "layout",
                Label = AppText.T(SettingsTextKeys.NavLayout),
                Icon = "\uE8B9",
                Description = AppText.T(SettingsTextKeys.NavLayoutDescription),
                Build = BuildLayoutSettingsSection,
                Reset = controller.ResetLayoutSettings
            });
            // 应用级设置放在最下面，和三个工作区设置用一条分隔线隔开。
            sections.Add(new SettingsSection
            {
                Id = "shortcuts",
                Label = AppText.T(SettingsTextKeys.NavShortcuts),
                Icon = "\uE765",
                Description = AppText.T(SettingsTextKeys.NavShortcutsDescription),
                Build = BuildShortcutSettingsSection,
                Reset = null,
                SeparatorBefore = true
            });
            sections.Add(new SettingsSection
            {
                Id = "general-app",
                Label = AppText.T(SettingsTextKeys.NavPreferences),
                Icon = "\uE713",
                Description = AppText.T(SettingsTextKeys.NavPreferencesDescription),
                Build = BuildGeneralAppSettingsSection,
                Reset = controller.ResetAppearanceSettings
            });
            // 关于由 AboutSection.cs 自己提供分区配置；这里只把它登记进来，
            // PinnedToBottom 让它在导航里永远画在最后，和列表位置无关。
            sections.Add(CreateAboutSettingsSection());
            return sections;
        }

        /// <summary>一行纯说明文字（有些设置只在别处能改，这里指个路）。</summary>
        private static UIElement BuildSupportingHint(string text)
        {
            SelectableText hint = UiFactory.SupportingText(text);
            hint.VerticalAlignment = VerticalAlignment.Center;
            return hint;
        }

        private static UIElement BuildSupportingHintKey(string key, params object[] args)
        {
            SelectableText hint = UiFactory.SupportingText(string.Empty);
            LocalizedText.Text(hint, key, args);
            hint.VerticalAlignment = VerticalAlignment.Center;
            return hint;
        }

        /// <summary>
        /// The 设置 page.  It fills the workspace area (no overlay, nothing to
        /// dismiss) and keeps the section rows the same height each time: the
        /// nav list never moves, so switching sections does not make the page
        /// jump.  Sections come from CreateSettingsSections().
        /// </summary>
        private Border BuildSettingsWorkspace()
        {
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            // 【2026-11-15 用户要求】设置页标题也按“一句话 + 问号”统一；
            // 这句本身就是一句话，问号里放同一句，下面的分区导航紧跟着上移。
            heading.Children.Add(UiFactory.HeadingWithHintKey(
                SettingsTextKeys.PageTitle,
                SettingsTextKeys.PageSubtitle,
                SettingsTextKeys.PageSubtitle,
                18));
            Grid.SetRow(heading, 0);
            content.Children.Add(heading);

            // 【2026-10-19 用户要求】窗口缩小/放大时不能把内容挤扁：用自适应两栏 ——
            // 宽的时候左导航 + 右内容（原样），窄的时候导航变成顶部一行、内容占满整行。
            settingsNavHost = new WrapPanel { Orientation = Orientation.Vertical };

            // 【2026-10-19 用户要求】“重置全部放到总设置内（其他的格式和总设置内的格式一致）。”
            // 所以导航里不再放“重置全部”；这个动作只保留总设置里那一条设置行
            // （标题 + 一句话小字 + 问号，和别的设置行完全一样，见 BuildGeneralSettingsSection）。

            // A fixed-height host: the section content scrolls inside it instead of
            // resizing the page.
            ScrollViewer contentHost = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            settingsContentScroll = contentHost;
            settingsContentHost = new StackPanel();
            contentHost.Content = settingsContentHost;

            // 【2026-11-19】分页条固定在右侧内容区底部，不跟表格内容一起滚动。
            Grid rightPane = new Grid();
            rightPane.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            rightPane.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            Grid.SetRow(contentHost, 0);
            rightPane.Children.Add(contentHost);
            shortcutPagerHost = new Grid
            {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            Grid.SetRow(shortcutPagerHost, 1);
            rightPane.Children.Add(shortcutPagerHost);

            settingsPane = new AdaptiveTwoPane(settingsNavHost, rightPane, 540)
            {
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(settingsPane, 1);
            content.Children.Add(settingsPane);
            // 语言切换统一由 MainWindow.OnLanguageChanged / ApplyLanguageChanged 处理：
            // 左侧分区名重排，右侧缓存页复用；这里不再单独订阅，避免一次切换重建两遍。
            return WorkspaceBorder(content);
        }

        /// <summary>
        /// Rebuilds the nav list and shows the active (or first) section.  Section
        /// pages are built once and cached: the settings controls are single
        /// instances (check boxes, number boxes, the speed slider), so rebuilding a
        /// page would try to re-parent them.
        /// </summary>
        private void RefreshSettingsPanel()
        {
            RefreshSettingsPanelCore(true);
        }

        /// <summary>
        /// 语言切换专用：只重建左侧分区名，右侧缓存页原地保留。
        /// 页内文字已经由 LocalizedText 更新；不再把整页控件重新创建一遍。
        /// 不叫 RefreshSettingsPanel 的重载，避免反射按方法名查找时出现二义性。
        /// </summary>
        private void RefreshSettingsPanelNavigationOnly()
        {
            RefreshSettingsPanelCore(false);
        }

        private void RefreshSettingsPanelCore(bool rebuildContent)
        {
            if (settingsNavHost == null || settingsContentHost == null)
            {
                return;
            }
            settingsContentHost.Visibility = Visibility.Visible;
            settingsSections = CreateSettingsSections();
            if (settingsPageCache == null)
            {
                settingsPageCache = new Dictionary<string, UIElement>(StringComparer.Ordinal);
            }
            settingsNavHost.Children.Clear();
            if (rebuildContent)
            {
                settingsContentHost.Children.Clear();
            }

            SettingsSection active = null;
            foreach (SettingsSection section in settingsSections)
            {
                if (active == null && string.Equals(section.Id, activeSettingsSectionId, StringComparison.Ordinal))
                {
                    active = section;
                }
            }
            if (active == null)
            {
                active = settingsSections.Count > 0 ? settingsSections[0] : null;
            }
            if (active == null)
            {
                return;
            }
            activeSettingsSectionId = active.Id;
            bool shortcutsActive = string.Equals(active.Id, "shortcuts", StringComparison.Ordinal);
            if (shortcutPagerHost != null)
            {
                shortcutPagerHost.Visibility = shortcutsActive
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            // 快捷键页自己滚行区域；外层再滚会把标题和表头一起带走。
            if (settingsContentScroll != null)
            {
                settingsContentScroll.VerticalScrollBarVisibility = shortcutsActive
                    ? ScrollBarVisibility.Disabled
                    : ScrollBarVisibility.Auto;
            }

            foreach (SettingsSection section in settingsSections)
            {
                if (!section.PinnedToBottom)
                {
                    AddSettingsNavigationEntry(section);
                }
            }
            bool hasPinnedSection = false;
            foreach (SettingsSection section in settingsSections)
            {
                if (section.PinnedToBottom)
                {
                    hasPinnedSection = true;
                    break;
                }
            }
            if (hasPinnedSection)
            {
                // 固定组永远最后画：先补一条分隔线，再按列表顺序画固定分区。
                // 新增普通分区不要加 PinnedToBottom，就会自动排在固定组上面。
                Border pinnedSeparator = UiFactory.Separator();
                pinnedSeparator.HorizontalAlignment = HorizontalAlignment.Stretch;
                pinnedSeparator.Margin = new Thickness(8, 8, 8, 8);
                settingsNavHost.Children.Add(pinnedSeparator);
                foreach (SettingsSection section in settingsSections)
                {
                    if (section.PinnedToBottom)
                    {
                        AddSettingsNavigationEntry(section);
                    }
                }
            }
            if (settingsPane != null)
            {
                settingsPane.RefreshLayout();
            }

            UIElement page;
            if (!settingsPageCache.TryGetValue(active.Id, out page))
            {
                page = active.Build == null ? null : active.Build();
                if (page != null)
                {
                    settingsPageCache[active.Id] = page;
                }
            }
            if (page != null && !settingsContentHost.Children.Contains(page))
            {
                settingsContentHost.Children.Add(page);
            }
            // 【2026-10-20 用户报“滑块指针在最左（0.5），右边却显示 1.0×”】
            // 真因：拖动速度的 Slider / 倍数文字是在**这个页面第一次打开时**才创建的；
            // 启动时的 RefreshSettings() 跑在页面创建之前（那时这两个控件还是 null），
            // 于是滑块停在默认最小 0.5、文字停在写死的 1.0×，没有按已保存的设置回填。
            // 设置页每次建好/切换分区后都补一次回填，所有延迟创建的控件都能拿到真实值。
            RefreshSettings();
            // 快捷键页可能因为“重置所有设置”或其它入口改了绑定；页面本身是缓存的，
            // 这里主动按当前目录重画键帽，避免缓存页继续显示旧键位。
            if (shortcutSettingsList != null)
            {
                RefreshShortcutSettingsSection();
            }
        }

        /// <summary>
        /// 画一行设置导航（可选前置分隔线）。普通分区和固定组都走这里，
        /// “快捷键”这类 SeparatorBefore 的行为保持不变。
        /// </summary>
        private void AddSettingsNavigationEntry(SettingsSection section)
        {
            if (section == null)
            {
                return;
            }
            if (section.SeparatorBefore)
            {
                Border separator = UiFactory.Separator();
                separator.HorizontalAlignment = HorizontalAlignment.Stretch;
                separator.Margin = new Thickness(8, 8, 8, 8);
                settingsNavHost.Children.Add(separator);
            }
            SettingsSection captured = section;
            Button button = CreateSettingsNavButton(
                captured.Icon,
                captured.Label,
                captured.Description,
                delegate { return string.Equals(captured.Id, activeSettingsSectionId, StringComparison.Ordinal); });
            button.Click += delegate
            {
                activeSettingsSectionId = captured.Id;
                RefreshSettingsPanel();
            };
            settingsNavHost.Children.Add(button);
        }

        /// <summary>Runs one section's own reset, after confirming with the user.</summary>
        private void ResetSettingsSection(SettingsSection section)
        {
            if (section == null || section.Reset == null)
            {
                return;
            }
            // 页面在语言切换后会原地复用，旧页面里的 SettingsSection 捕获的是旧语言标题；
            // 用 Id 取当前这一份，确认框才不会出现“英文界面 + 中文分区名”。
            SettingsSection current = FindSettingsSection(section.Id);
            if (current == null)
            {
                current = section;
            }
            if (current.Reset == null)
            {
                return;
            }
            // 公共确认框：「是」在左、「否」在右，右上角 × = 否（用户第 52、53 条）。
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.F(SettingsTextKeys.ResetSectionConfirmTitleFormat, current.Label),
                AppText.F(SettingsTextKeys.ResetSectionConfirmMessageFormat, current.Label)))
            {
                return;
            }
            current.Reset();
            RefreshSettings();
            RefreshSettingsPanel();
        }
        private static Button CreateSettingsNavButton(string icon, string label, string description)
        {
            return CreateSettingsNavButton(icon, label, description, null);
        }

        /// <summary>
        /// 左侧分区导航的一行（设置页、回收站共用）。
        ///
        /// 【2026-10-19 用户要求】“小侧边栏要像旁边那样子亮起……常驻亮起。”
        /// 真 bug：原来这里挂的是 `AttachTileHover(button, delegate { return false; })`，
        /// 鼠标一移开就按“不亮”处理，把调用方刚设好的选中底色又擦回透明 —— 选中的分区只有
        /// 鼠标停在它上面的那一瞬间是亮的。现在把“这一行是不是当前分区”做成回调传进来：
        /// 鼠标移开时恢复成选中色，文字/图标也跟着变成主题色，所以它会一直亮着。
        /// </summary>
        private static Button CreateSettingsNavButton(
            string icon,
            string label,
            string description,
            Func<bool> isActive)
        {
            Button button = new Button
            {
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 0, 2)
            };
            TextBlock iconBlock = new TextBlock
            {
                Text = UiFactory.IconGlyph(icon),
                FontFamily = AppTheme.IconFont,
                FontSize = 15,
                Foreground = AppTheme.TextSecondary,
                Width = 24,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            TextBlock labelBlock = new TextBlock
            {
                Text = label,
                FontFamily = AppTheme.UiFont,
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(iconBlock);
            content.Children.Add(labelBlock);
            button.Content = content;
            Func<bool> activeCheck = isActive;
            if (activeCheck == null)
            {
                activeCheck = delegate { return false; };
            }
            Action applyState = delegate
            {
                bool active = activeCheck();
                button.Background = active ? AppTheme.ActiveSurface : System.Windows.Media.Brushes.Transparent;
                iconBlock.Foreground = active ? AppTheme.Accent : AppTheme.TextSecondary;
                labelBlock.Foreground = active ? AppTheme.Accent : AppTheme.TextPrimary;
                labelBlock.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            };
            button.MouseEnter += delegate
            {
                button.Background = activeCheck() ? AppTheme.NavigationActiveSurface : AppTheme.HoverSurface;
            };
            button.MouseLeave += delegate { applyState(); };
            button.Loaded += delegate { applyState(); };
            applyState();
            AccessibleToolTip.Attach(button, SettingsToolTips.Navigation(label, description));
            return button;
        }

        /// <summary>
        /// 【2026-10-19 用户要求】设置行的新排版：
        ///
        /// <code>
        /// CPU 上限（%）                     [ 75 ]
        /// 启动项目前 CPU 高于这个数就先等一等。 (?)   ← 小字 + 问号（悬停看详细，点击弹可复制的说明框）
        /// </code>
        ///
        /// 用户原话：“CPU上限（%）大字不变，下行的介绍小字……要精简，尽量一两句话……
        /// 鼠标放过去（放到问号）后，出现悬浮提示，就是详细介绍。”
        /// 所以这里只放**一两句精简说明**；完整说明传 `detail`，由问号承载（悬浮 + 点击弹框）。
        ///
        /// `layout = Stacked` 用于宽控件（滑块、下拉）：控件放到说明下面一整行，
        /// 否则小字会被挤成一条竖排（用户第 3 张截图里「总设置」那种惨状）。
        /// </summary>
        private static Border BuildSettingsRow(string title, string hint, string detail, UIElement control)
        {
            return BuildSettingsRow(title, hint, detail, control, SettingsRowLayout.Inline);
        }

        /// <summary>
        /// 公共定义版本：标题、小字、问号提示和点击弹框都直接引用 key。
        /// 切换语言后 LocalizedText 更新文字，HelpHint 重新解析提示，不重建设置页。
        /// </summary>
        private static Border BuildSettingsRow(HelpDefinition definition, UIElement control)
        {
            return BuildSettingsRow(definition, control, SettingsRowLayout.Inline);
        }

        private static Border BuildSettingsRow(
            HelpDefinition definition,
            UIElement control,
            SettingsRowLayout layout)
        {
            if (definition == null)
            {
                return BuildSettingsRow(string.Empty, string.Empty, null, control, layout);
            }

            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (layout == SettingsRowLayout.Stacked)
            {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            SelectableText titleBlock = new SelectableText
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.NoWrap
            };
            LocalizedText.Text(titleBlock, definition.TitleKey);
            Grid.SetRow(titleBlock, 0);
            row.Children.Add(titleBlock);

            Grid hintLine = HelpHint.BuildHintLine(definition);
            if (hintLine != null)
            {
                Grid.SetRow(hintLine, 1);
                row.Children.Add(hintLine);
            }

            if (control != null)
            {
                control.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                if (layout == SettingsRowLayout.Stacked)
                {
                    Grid.SetRow(control, 2);
                    Grid.SetColumnSpan(control, 2);
                    control.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 0));
                    control.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                }
                else
                {
                    Grid.SetColumn(control, 1);
                    Grid.SetRowSpan(control, 2);
                    control.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0));
                    control.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
                }
                row.Children.Add(control);
            }

            return new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = AppTheme.SoftBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 13),
                Child = row
            };
        }

        /// <summary>
        /// 公共内容版本：设置行的小字和问号详情从 HelpContent 取，页面不再自己写。
        /// </summary>
        private static Border BuildSettingsRow(HelpContent content, UIElement control)
        {
            return BuildSettingsRow(content, control, SettingsRowLayout.Inline);
        }

        private static Border BuildSettingsRow(
            HelpContent content,
            UIElement control,
            SettingsRowLayout layout)
        {
            if (content == null)
            {
                return BuildSettingsRow(string.Empty, string.Empty, null, control, layout);
            }
            return BuildSettingsRow(content.Title, content.Short, content.Detail, control, layout);
        }

        private static Border BuildSettingsRow(
            string title,
            string hint,
            string detail,
            UIElement control,
            SettingsRowLayout layout)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (layout == SettingsRowLayout.Stacked)
            {
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            SelectableText titleBlock = new SelectableText
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                // 标题保持一行（长标题宁可裁也不折行），和原来看法一致。
                TextWrapping = TextWrapping.NoWrap
            };
            Grid.SetRow(titleBlock, 0);
            row.Children.Add(titleBlock);

            if (!string.IsNullOrWhiteSpace(hint) || !string.IsNullOrWhiteSpace(detail))
            {
                // 公共交互：小字 + 紧跟问号（悬停看详情、点击弹可复制说明框）。
                // 代码在 `Shared/UI/Theme/ToolTips/HelpHint.cs`，全程序的问号都走同一份。
                Grid hintLine = HelpHint.BuildHintLine(title, hint, detail);
                Grid.SetRow(hintLine, 1);
                row.Children.Add(hintLine);
            }

            if (control != null)
            {
                control.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                if (layout == SettingsRowLayout.Stacked)
                {
                    Grid.SetRow(control, 2);
                    Grid.SetColumnSpan(control, 2);
                    control.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 0));
                    control.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
                }
                else
                {
                    Grid.SetColumn(control, 1);
                    Grid.SetRowSpan(control, 2);
                    control.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0));
                    control.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
                }
                row.Children.Add(control);
            }

            return new Border
            {
                Background = System.Windows.Media.Brushes.Transparent,
                BorderBrush = AppTheme.SoftBorder,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 12, 0, 13),
                Child = row
            };
        }

        /// <summary>
        /// <summary>
        /// 兼容旧调用（回收站那种“可点击、有右键菜单”的条目行）：
        /// 不带问号、说明可长可短，`selectable = false` 时用普通 TextBlock（否则右键菜单会被文字控件截走）。
        /// 新的设置行请用上面“标题 + 小字 + 详细说明”的那个重载。
        /// </summary>
        private static Border BuildSettingsRow(string title, string explanation, UIElement control)
        {
            return BuildSettingsRow(title, explanation, control, false, true);
        }

        private static Border BuildSettingsRow(string title, string explanation, UIElement control, bool flat)
        {
            return BuildSettingsRow(title, explanation, control, flat, true);
        }

        private static Border BuildSettingsRow(
            string title,
            string explanation,
            UIElement control,
            bool flat,
            bool selectable)
        {
            Grid row = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (selectable)
            {
                text.Children.Add(new SelectableText
                {
                    Text = title,
                    FontSize = 13,
                    Foreground = AppTheme.TextPrimary,
                    // 标题保持“一行”（和原来的 TextBlock 默认 NoWrap 一致，长标题宁可被裁也不折行）。
                    TextWrapping = TextWrapping.NoWrap
                });
            }
            else
            {
                text.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 13,
                    Foreground = AppTheme.TextPrimary
                });
            }
            if (!string.IsNullOrWhiteSpace(explanation))
            {
                if (selectable)
                {
                    text.Children.Add(new SelectableText
                    {
                        Text = explanation,
                        FontSize = 11,
                        Foreground = AppTheme.TextSecondary,
                        Margin = new Thickness(0, 3, 12, 0)
                    });
                }
                else
                {
                    text.Children.Add(new TextBlock
                    {
                        Text = explanation,
                        FontSize = 11,
                        Foreground = AppTheme.TextSecondary,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 12, 0)
                    });
                }
            }
            Grid.SetColumn(text, 0);
            row.Children.Add(text);
            if (control != null)
            {
                control.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
                Grid.SetColumn(control, 1);
                row.Children.Add(control);
            }
            // 给行一个**实心背景**（原来完全不设背景）：
            //  · 透明背景的 Border 在 WPF 里不参与命中测试，鼠标事件只能靠子控件冒泡上来，
            //    回收站那种“整行悬停微微亮起”期望的行为就不可靠（用户第 76 条：鼠标放过去还是没亮起）；
            //  · 用户第 77 条还说“方方的，太生硬的，你的代码不就挺好看的吗”——
            //    所以这里顺手把它做成和清单行一致的样式：浅色圆角 + 内边距，不再是一条生硬的直线。
            return new Border
            {
                // flat：内层不设背景、不画圆角（外面的容器自己有），
                // 否则这块直角实心背景会把外层的圆角盖掉（用户第 78、79 条反馈的正是这个）。
                Background = flat ? null : AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 0, 0, flat ? 0 : 1),
                CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(8),
                Padding = flat ? new Thickness(0) : new Thickness(10, 8, 10, 8),
                Child = row
            };
        }

        /// <summary>设置行里控件的摆放方式：标题右边（窄控件）/ 说明下面一整行（滑块、下拉等宽控件）。</summary>
        private enum SettingsRowLayout
        {
            Inline,
            Stacked
        }

        /// <summary>
        /// The per-section reset at the bottom of every section page.  It resets
        /// only the options on that page; sections that have nothing to reset get a
        /// disabled button that says so.
        /// </summary>
        private UIElement BuildSectionResetRow(SettingsSection section)
        {
            SettingsSection captured = section;
            bool hasSettings = captured != null && captured.Reset != null;
            Button reset = UiFactory.CommandButtonKey(
                "\uE777", SettingsTextKeys.ResetSectionLabel, hasSettings);
            reset.HorizontalAlignment = HorizontalAlignment.Left;
            AccessibleToolTip.Attach(
                reset,
                TooltipDefinition.Named(
                    SettingsTextKeys.ResetSectionLabel,
                    SettingsTextKeys.ResetSectionLabel,
                    hasSettings
                        ? SettingsTextKeys.ResetSectionShort
                        : SettingsTextKeys.ResetSectionNoneShort,
                    null));
            reset.IsEnabled = hasSettings;
            if (hasSettings)
            {
                reset.Click += delegate { ResetSettingsSection(captured); };
            }
            return BuildSettingsRow(
                HelpDefinition.Create(
                    SettingsTextKeys.ResetSectionTitle,
                    hasSettings
                        ? SettingsTextKeys.ResetSectionShort
                        : SettingsTextKeys.ResetSectionNoneShort,
                    hasSettings
                        ? SettingsTextKeys.ResetSectionDetail
                        : SettingsTextKeys.ResetSectionNoneDetail),
                reset);
        }

        private static UIElement BuildSettingsSectionHeader(string title)
        {
            // 分区标题也可选中复制（不可交互，安全）。
            return new SelectableText
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private static UIElement BuildSettingsSectionHeaderKey(string key)
        {
            SelectableText title = BuildSettingsSectionHeader(string.Empty) as SelectableText;
            if (title != null)
            {
                LocalizedText.Text(title, key);
            }
            return title;
        }

        /// <summary>
        /// 【2026-11-15】分区标题 + 一句话 + 问号：关于、通用设置、快捷键三个分区共用。
        /// 标题仍然是 15 号，下面只多一行小字和问号，旧的长说明进问号弹框。
        /// </summary>
        private static UIElement BuildSettingsSectionHeaderKey(
            string key,
            string shortKey,
            string detailKey)
        {
            StackPanel header = UiFactory.HeadingWithHintKey(key, shortKey, detailKey, 15);
            header.Margin = new Thickness(0, 0, 0, 4);
            return header;
        }

        private UIElement BuildGeneralSettingsSection()
        {
            PrepareSettingsCheckBox(autoStartCheckBox);
            StackPanel section = new StackPanel();
            section.Children.Add(BuildSettingsSectionHeaderKey(SettingsTextKeys.NavGeneral));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.GeneralScrollSpeedRow,
                BuildDragScrollSpeedControl(),
                SettingsRowLayout.Stacked));
            // 总设置只放"对所有工作区都生效"的选项，以及"开机时自动启动"（它只负责开机拉起程序）。
            // 注意：**同一个控件只能出现在一个分区里**——WPF 里一个控件同时属于两个父容器时，
            // 后加的那个父容器会把它从前面那个"抢"走，表现就是"这一页少了这一行"或
            // "两个地方各显示一个同名设置"（用户 2026-10-09 反馈的"突然能两个一起"就是这个）。
            // CPU 上限/等待上限是所有启动类工作区共用的，代码在公共 Settings 文件夹。
            section.Children.Add(BuildCommonCpuSettingsRows());
            // 内存上限/等待上限也是所有启动类工作区共用，代码同样在公共 Settings 文件夹。
            section.Children.Add(BuildCommonMemorySettingsRows());
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.AutoStartRow,
                autoStartCheckBox));
            // 用户要求（2026-10-10 03:12）：“总设置，开机时自动启动下面要有一个优先启动项，
            // 要么是启动与恢复的工作方案启动完后，再启动自启动应用里的方案，要么反过来。”
            // 原因：两个分区都有自己的“启动程序后自动恢复…”，同时打开时会抢顺序，必须有个明确的先后。
            // 这一项是全局行为，所以放在总设置里（代码也放在公共区域：见 Settings 模型里的 StartupPriority）。
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupPriorityRow,
                BuildStartupPriorityControl()));

            // 快捷键已经从“总设置”里拆成独立页面，见 ShortcutSettingsSection.cs。
            // 顺序：重置本节 → 重置所有设置（最后、绿色按钮）。
            section.Children.Add(BuildSectionResetRow(FindSettingsSection("general")));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ResetAllRow,
                BuildResetAllSettingsButton()));
            return section;
        }

        /// <summary>总设置里的“重置所有设置”按钮（点击后弹确认框）。</summary>
        private UIElement BuildResetAllSettingsButton()
        {
            // 用户要求（2026-10-19）：重置所有设置的按钮改成绿色，显得重要（公共样式 GreenButton）。
            Button button = UiFactory.CommandButtonKey(
                "\uE777", SettingsTextKeys.ResetAllLabel, true);
            button.Style = (Style)Application.Current.Resources["GreenButton"];
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Click += delegate { ConfirmResetAllSettings(); };
            AccessibleToolTip.Attach(button, SettingsToolTips.ResetAll);
            return button;
        }

        /// <summary>
        /// 重置全部设置前的确认。文案自己润色：说清“会重置什么、不会动什么、能不能撤销”。
        /// </summary>
        private void ConfirmResetAllSettings()
        {
            // 公共确认框：「是」在左、「否」在右，右上角 × = 否（用户第 52、53 条）。
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(SettingsTextKeys.ResetAllConfirmTitle),
                AppText.T(SettingsTextKeys.ResetAllConfirmBody)))
            {
                return;
            }
            controller.ResetSettingsToDefaults();
            RefreshSettings();
            RefreshSettingsPanel();
            statusText.Text = AppText.T(SettingsTextKeys.ResetAllDone);
        }


        /// <summary>
        /// A settings row already spells out what the switch does, so the check box
        /// itself carries no text (leaving the old label would repeat it twice) -
        /// only its accessible name keeps the wording.
        /// </summary>
        private static void PrepareSettingsCheckBox(CheckBox box)
        {
            if (box != null)
            {
                box.Content = null;
                box.MinWidth = 0;
                box.Padding = new Thickness(0);
            }
        }

        /// <summary>Keeps a number box at its natural width inside a settings row.</summary>
        private static UIElement CreateNumberBoxHost(TextBox box)
        {
            box.HorizontalAlignment = HorizontalAlignment.Right;
            return box;
        }

        /// <summary>命名设置控件：输入框 + 未编辑时的“开头…”显示层。</summary>
        private sealed class NamingSettingField
        {
            public Grid Host;
            public TextBox Editor;
        }

        // 命名框：宽度按名字在 32–240 之间伸缩；13 号字放不下先缩到 11，仍放不下才截断。
        private const double NamingMinWidth = 32.0;
        private const double NamingMaxWidth = 240.0;
        private const double NamingWidthPadding = 26.0;
        private const double NamingWidthSafety = 2.0;
        private const double NamingBaseFontSize = 13.0;
        private const double NamingMinFontSize = 11.0;

        /// <summary>
        /// 设置页“新命名”输入框（启动与恢复 / 自启动应用 / 图标位置保存三个分区共用）。
        /// 回车提交并把焦点移走、点空白提交并移走焦点、失焦提交。
        /// 宽度按名字实际宽度伸缩（32–240）；13 号字放不下先缩字号（下限 11），
        /// 仍放不下才截断——只保留完整字，最后一个字不露半截；截断时悬停显示完整内容。
        /// 名字怎么归一化、存哪里由调用方的 commit 决定。
        /// </summary>
        private NamingSettingField CreateNamingSettingBox(TooltipDefinition tip, System.Action commit)
        {
            NamingSettingField field = new NamingSettingField();
            TextBox box = new TextBox
            {
                Width = 110,
                Height = 30,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            field.Editor = box;
            if (tip != null)
            {
                AccessibleToolTip.Attach(box, tip);
            }
            System.Action run = commit ?? delegate { };
            box.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    run();
                    WorkspaceRestorer.UI.Shared.CommitOnOutsideClick.MoveFocusAfter(box);
                    args.Handled = true;
                }
            };
            box.LostFocus += delegate { run(); };
            WorkspaceRestorer.UI.Shared.CommitOnOutsideClick.Attach(
                this,
                box,
                delegate
                {
                    run();
                    WorkspaceRestorer.UI.Shared.CommitOnOutsideClick.MoveFocusAfter(box);
                });

            // 用户要求（2026-11-18，2026-11-19 方案 A）：宽度按名字在 110–240 之间伸缩；
            // 名字完整放下时不显示省略号；超过 240 才显示“开头 + …”，并悬停显示完整值。
            // 文字始终由输入框自己画，聚焦时不再换一层文字，所以不会位移。
            // 盖块按实际字形量出“还能放下几个完整字”，点紧挨最后一个字，末尾的字不会被压掉。
            Border ellipsis = new Border
            {
                Height = 20,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            ellipsis.SetBinding(Border.BackgroundProperty, new Binding("Background") { Source = box });
            TextBlock dots = new TextBlock
            {
                Text = "\u2026",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ellipsis.Child = dots;

            Grid host = new Grid { Width = 110 };
            host.Children.Add(box);
            host.Children.Add(ellipsis);
            field.Host = host;

            System.Action updateEllipsis = delegate
            {
                if (box.IsKeyboardFocusWithin)
                {
                    ellipsis.Visibility = Visibility.Collapsed;
                    return;
                }
                string text = box.Text ?? string.Empty;
                // 先按正文字号算宽度；放不下再缩字号，最后才截断。
                box.FontSize = NamingBaseFontSize;
                dots.FontSize = NamingBaseFontSize;
                double baseWidth = MeasureTextAdvance(box, text);
                double maxContent = NamingMaxWidth - box.BorderThickness.Left - box.BorderThickness.Right
                    - box.Padding.Left - box.Padding.Right;
                double fitContent = maxContent - NamingWidthSafety;
                double desiredWidth;
                if (baseWidth <= fitContent)
                {
                    desiredWidth = baseWidth + NamingWidthPadding;
                    if (desiredWidth < NamingMinWidth)
                    {
                        desiredWidth = NamingMinWidth;
                    }
                }
                else
                {
                    double targetFont = NamingBaseFontSize * fitContent / baseWidth;
                    if (targetFont < NamingMinFontSize)
                    {
                        targetFont = NamingMinFontSize;
                    }
                    if (targetFont > NamingBaseFontSize)
                    {
                        targetFont = NamingBaseFontSize;
                    }
                    box.FontSize = targetFont;
                    dots.FontSize = targetFont;
                    desiredWidth = NamingMaxWidth;
                    double shrunkWidth = MeasureTextAdvance(box, text);
                    if (shrunkWidth > fitContent && targetFont > NamingMinFontSize + 0.01)
                    {
                        // 比例估算有误差，按实际量到的宽度再收一次。
                        targetFont = Math.Max(NamingMinFontSize, targetFont * fitContent / shrunkWidth);
                        box.FontSize = targetFont;
                        dots.FontSize = targetFont;
                    }
                }
                if (Math.Abs(box.Width - desiredWidth) > 0.1)
                {
                    box.Width = desiredWidth;
                    host.Width = desiredWidth;
                }
                box.UpdateLayout();
                double contentWidth = box.ActualWidth - box.BorderThickness.Left - box.BorderThickness.Right
                    - box.Padding.Left - box.Padding.Right;
                if (contentWidth <= 0)
                {
                    contentWidth = maxContent;
                }
                double textWidth = MeasureTextAdvance(box, text);
                if (textWidth <= contentWidth - 0.5)
                {
                    ellipsis.Visibility = Visibility.Collapsed;
                    // 放得下也挂这一行的短提示（标题 + 一句说明 + 建议），
                    // 和同页其它设置控件一样，悬停有内容；截断时才换成正文字段的完整名字。
                    AccessibleToolTip.Attach(box, tip);
                    return;
                }
                if (contentWidth <= NamingEllipsisInkWidth + NamingEllipsisGap)
                {
                    ellipsis.Visibility = Visibility.Collapsed;
                    AccessibleToolTip.Attach(box, tip);
                    return;
                }
                // 只保留完整文本元素：用 TextBox 自己的排版量（GetRectFromCharacterIndex），
                // 和实际渲染同一套布局；量到的是每个字真正的右边缘，不再用子串宽度估算。
                double hiddenLeft;
                FindNamingTruncation(box, text, contentWidth, out hiddenLeft);
                double left = box.BorderThickness.Left + box.Padding.Left + hiddenLeft;
                // 右边缘多盖 1 DIP：150% 下内容区右边缘落在半像素，少盖会漏出一条缝。
                double width = contentWidth + 1.0 - hiddenLeft;
                if (width < NamingEllipsisInkWidth)
                {
                    width = NamingEllipsisInkWidth;
                }
                ellipsis.Margin = new Thickness(left, 0, 0, 0);
                ellipsis.Width = width;
                ellipsis.Visibility = Visibility.Visible;
                AttachNamingValueTooltip(box, tip, text);
            };
            System.Action resetView = delegate
            {
                AppTheme.ScrollToStart(box);
                box.Dispatcher.BeginInvoke(
                    updateEllipsis,
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            box.Loaded += delegate { resetView(); };
            box.TextChanged += delegate
            {
                if (box.IsKeyboardFocusWithin)
                {
                    ellipsis.Visibility = Visibility.Collapsed;
                    return;
                }
                resetView();
            };
            box.GotKeyboardFocus += delegate
            {
                ellipsis.Visibility = Visibility.Collapsed;
                // 内部横向滚动保持失焦时的位置（0）：点击进入编辑时不再把光标滚进可视区，文字不位移。
                AppTheme.ScrollToStart(box);
            };
            // 点击末尾时 TextBox 会为光标再滚一次，等光标定位/选区变化后再按回 0；
            // 只在文字本来放得下（未截断）时做，截断时保留光标滚到末尾的能力。
            System.Action keepScrollWhenFit = delegate
            {
                if (!box.IsKeyboardFocusWithin)
                {
                    return;
                }
                string current = box.Text ?? string.Empty;
                double content = box.ActualWidth - box.BorderThickness.Left - box.BorderThickness.Right
                    - box.Padding.Left - box.Padding.Right;
                if (content > 0 && MeasureTextAdvance(box, current) <= content - 0.5)
                {
                    AppTheme.ScrollToStart(box);
                }
            };
            box.PreviewMouseLeftButtonDown += delegate
            {
                box.Dispatcher.BeginInvoke(
                    keepScrollWhenFit,
                    System.Windows.Threading.DispatcherPriority.Background);
            };
            box.SelectionChanged += delegate { keepScrollWhenFit(); };
            box.LostFocus += delegate
            {
                ellipsis.Visibility = Visibility.Collapsed;
                resetView();
            };
            return field;
        }

        // 省略号墨迹约 8 DIP；最后一个完整字和省略号之间至少留 1 DIP。
        private const double NamingEllipsisInkWidth = 8.0;
        private const double NamingEllipsisGap = 1.0;

        private static double MeasureTextAdvance(TextBox box, string text)
        {
            if (box == null || string.IsNullOrEmpty(text))
            {
                return 0;
            }
            Typeface typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
            double pixelsPerDip = 1.0;
            try { pixelsPerDip = VisualTreeHelper.GetDpi(box).PixelsPerDip; } catch { }
            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                box.FlowDirection,
                typeface,
                box.FontSize,
                Brushes.Black,
                pixelsPerDip);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        /// <summary>
        /// 用 TextBox 自己的排版（GetRectFromCharacterIndex）找第一个放不下的完整文本元素，
        /// 返回它的左边缘（相对内容区）。这样和实际渲染同一套布局，不会切出半个字。
        /// </summary>
        private static void FindNamingTruncation(TextBox box, string text, double contentRight, out double hiddenLeft)
        {
            hiddenLeft = contentRight;
            if (box == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                int start = enumerator.ElementIndex;
                string element = enumerator.GetTextElement();
                int last = start + element.Length - 1;
                Rect trailing;
                Rect leading;
                try
                {
                    trailing = box.GetRectFromCharacterIndex(last, true);
                    leading = box.GetRectFromCharacterIndex(start, false);
                }
                catch
                {
                    return;
                }
                if (trailing.Right > contentRight - 0.5)
                {
                    hiddenLeft = leading.Left;
                    return;
                }
            }
        }

        /// <summary>
        /// 量出“最多能放下几个完整文本元素”的前缀宽度；按文本元素（不是码元）切，
        /// 所以 emoji / 组合字符不会被切一半。
        /// </summary>
        private static double MeasureFittingPrefixWidth(TextBox box, string text, double maxWidth)
        {
            if (box == null || string.IsNullOrEmpty(text) || maxWidth <= 0)
            {
                return 0;
            }
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            double fitted = 0;
            while (enumerator.MoveNext())
            {
                int next = enumerator.ElementIndex + enumerator.GetTextElement().Length;
                double width = MeasureTextAdvance(box, text.Substring(0, next));
                if (width > maxWidth)
                {
                    break;
                }
                fitted = width;
            }
            return fitted;
        }

        // 截断时悬停显示框里完整的名字（相当于评论区的 Read more）；
        // 定义本身放在公共的 Theme/ToolTips/Content/SettingsToolTips，这里只填当前值。
        private static void AttachNamingValueTooltip(TextBox box, TooltipDefinition tip, string text)
        {
            if (box == null)
            {
                return;
            }
            AccessibleToolTip.Attach(box, SettingsToolTips.NamingValue(tip, text));
        }

        /// <summary>
        /// 命名设置的归一化：空白、或正好等于当前语言的默认名 → null（表示“未自定义”）。
        /// 这样切换语言时，没自定义过的名字会跟着变成对应语言的默认名。
        /// </summary>
        private static string NormalizeNamingSetting(TextBox box, string defaultName)
        {
            string value = box == null || box.Text == null ? string.Empty : box.Text.Trim();
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, defaultName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return value;
        }

        /// <summary>
        /// “优先启动项”控件（用户要求 2026-10-10 03:12）：两个分区都开了自动恢复时先做哪一个。
        /// 这一项是全局行为，所以放在总设置里；数据存在 AppSettings 上（公共区域）。
        /// </summary>
        private UIElement BuildStartupPriorityControl()
        {
            StackPanel host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            startupPriorityBox = new ComboBox
            {
                Width = 260,
                VerticalAlignment = VerticalAlignment.Center
            };
            ComboBoxItem startupFirst = new ComboBoxItem();
            LocalizedText.Content(startupFirst, SettingsTextKeys.PriorityStartupFirst);
            startupPriorityBox.Items.Add(startupFirst);
            ComboBoxItem applicationsFirst = new ComboBoxItem();
            LocalizedText.Content(applicationsFirst, SettingsTextKeys.PriorityApplicationsFirst);
            startupPriorityBox.Items.Add(applicationsFirst);
            AccessibleToolTip.Attach(startupPriorityBox, SettingsToolTips.StartupPriority);
            startupPriorityBox.SelectedIndex = controller.Settings.ApplicationsFirst ? 1 : 0;
            startupPriorityBox.SelectionChanged += delegate
            {
                if (suppressStartupPriorityChange)
                {
                    return;
                }
                controller.UpdateApplicationsFirst(startupPriorityBox.SelectedIndex == 1);
            };
            host.Children.Add(startupPriorityBox);
            return host;
        }

        /// <summary>「自启动应用」分区的“启动程序后自动启动自启动方案”开关。</summary>
        private readonly CheckBox applicationsAutoRestoreCheckBox = new CheckBox();

        private bool suppressStartupPriorityChange;
        /// <summary>回填勾选状态时抑制写回，避免“设置自己改自己”。</summary>
        private bool suppressApplicationsAutoRestoreChange;
        private bool suppressApplicationsWaitForLoadChange;
        private ComboBox startupPriorityBox;
        // 【2026-10-20】拖动速度的“点一下输入倍率”控件与状态。
        // 参数框的单击/点空白提交/回车提交统一走公共的 ParameterField（Shared/UI/Controls/ParameterField.cs）。
        private TextBox dragScrollSpeedEditor;
        private bool updatingDragScrollSpeed;

        private UIElement BuildDragScrollSpeedControl()
        {
            StackPanel host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            dragScrollSpeedSlider = new Slider
            {
                Width = 170,
                Minimum = 0.5,
                Maximum = 3.0,
                // 【2026-10-20 用户要求】倍率要精确到 0.01（例如 1.25×–1.26×），
                // 所以滑块不再吸附到 0.5 的刻度；刻度只是视觉参考。
                SmallChange = 0.01,
                LargeChange = 0.1,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
                TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight
            };
            dragScrollSpeedSlider.ValueChanged += OnDragScrollSpeedChanged;
            AccessibleToolTip.Attach(dragScrollSpeedSlider, SettingsToolTips.DragScrollSpeedSlider);
            host.Children.Add(dragScrollSpeedSlider);

            // 倍数：平时是文字；**双击**变成输入框，点空白处（或回车）确定。
            Grid valueHost = new Grid
            {
                Width = 52,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            dragScrollSpeedValueText = new TextBlock
            {
                Text = "1.0×",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Accent,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.IBeam
            };
            AccessibleToolTip.AttachDescription(dragScrollSpeedValueText, SettingsToolTips.DragScrollSpeedValue);
            ParameterField.BeginEditOnDoubleClick(dragScrollSpeedValueText, BeginDragScrollSpeedEdit);
            valueHost.Children.Add(dragScrollSpeedValueText);

            dragScrollSpeedEditor = new TextBox
            {
                Width = 52,
                Height = 26,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Accent,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(2, 0, 2, 0)
            };
            // 公共参数框交互：回车提交、Esc 取消、点空白提交并收走焦点。
            ParameterField.Register(this, dragScrollSpeedEditor, CommitDragScrollSpeedEdit, CancelDragScrollSpeedEdit);
            valueHost.Children.Add(dragScrollSpeedEditor);
            host.Children.Add(valueHost);

            Button resetSpeedButton = UiFactory.IconButton(
                "\uE777",
                TooltipDefinition.Named(
                    SettingsTextKeys.ResetDragSpeedTipName,
                    SettingsTextKeys.ResetDragSpeedTipTitle,
                    SettingsTextKeys.ResetDragSpeedTipDescription,
                    null));
            resetSpeedButton.Width = 30;
            resetSpeedButton.Height = 30;
            resetSpeedButton.Margin = new Thickness(8, 0, 0, 0);
            resetSpeedButton.Click += delegate
            {
                CancelDragScrollSpeedEdit();
                controller.UpdateDragScrollSpeed(1.0);
                RefreshSettings();
            };
            host.Children.Add(resetSpeedButton);
            return host;
        }

        /// <summary>把倍率显示成 1.0× / 1.25× / 3.0×（最多两位小数，去掉多余的 0）。</summary>
        private static string FormatDragScrollSpeed(double speed)
        {
            return speed.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "×";
        }

        /// <summary>点一下倍数文字：进入编辑，自动全选当前值。</summary>
        private void BeginDragScrollSpeedEdit()
        {
            if (dragScrollSpeedEditor == null || dragScrollSpeedSlider == null)
            {
                return;
            }
            double speed = ClampDragScrollSpeed(dragScrollSpeedSlider.Value);
            dragScrollSpeedEditor.Text = speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            dragScrollSpeedEditor.Visibility = Visibility.Visible;
            dragScrollSpeedValueText.Visibility = Visibility.Collapsed;
            dragScrollSpeedEditor.Focus();
            dragScrollSpeedEditor.SelectAll();
        }

        private void CommitDragScrollSpeedEdit()
        {
            if (dragScrollSpeedEditor == null || dragScrollSpeedEditor.Visibility != Visibility.Visible)
            {
                return;
            }
            string text = (dragScrollSpeedEditor.Text ?? string.Empty).Trim();
            text = text.TrimEnd('x', 'X', '×').Trim();
            double parsed;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out parsed)
                && !double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.CurrentCulture, out parsed))
            {
                CancelDragScrollSpeedEdit();
                return;
            }
            double speed = ClampDragScrollSpeed(Math.Round(parsed, 2, MidpointRounding.AwayFromZero));
            HideDragScrollSpeedEditor();
            updatingDragScrollSpeed = true;
            try
            {
                if (dragScrollSpeedSlider != null)
                {
                    dragScrollSpeedSlider.Value = speed;
                }
            }
            finally
            {
                updatingDragScrollSpeed = false;
            }
            controller.UpdateDragScrollSpeed(speed);
            if (dragScrollSpeedValueText != null)
            {
                dragScrollSpeedValueText.Text = FormatDragScrollSpeed(speed);
            }
        }

        private void CancelDragScrollSpeedEdit()
        {
            HideDragScrollSpeedEditor();
        }

        private void HideDragScrollSpeedEditor()
        {
            if (dragScrollSpeedEditor != null)
            {
                dragScrollSpeedEditor.Visibility = Visibility.Collapsed;
            }
            if (dragScrollSpeedValueText != null)
            {
                dragScrollSpeedValueText.Visibility = Visibility.Visible;
            }
        }

        private static double ClampDragScrollSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed))
            {
                return 1.0;
            }
            return Math.Max(0.5, Math.Min(3.0, Math.Round(speed, 2, MidpointRounding.AwayFromZero)));
        }

        /// <summary>
        /// Placeholder page for a workspace section that has no options yet.  It is
        /// registered like every other section, so adding settings later is a local
        /// change in CreateSettingsSections().
        /// </summary>
        private static StackPanel BuildEmptySettingsSection(string heading, string explanation)
        {
            StackPanel section = new StackPanel();
            section.Children.Add(BuildSettingsSectionHeader(heading));
            section.Children.Add(new TextBlock
            {
                Text = explanation,
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return section;
        }

        /// <summary>Looks up a registered section (used by the page builders).</summary>
        private SettingsSection FindSettingsSection(string id)
        {
            if (settingsSections != null)
            {
                foreach (SettingsSection section in settingsSections)
                {
                    if (string.Equals(section.Id, id, StringComparison.Ordinal))
                    {
                        return section;
                    }
                }
            }
            return null;
        }
        private void OnDragScrollSpeedChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (loadingSettings || updatingDragScrollSpeed)
            {
                return;
            }
            // 倍率精确到 0.01（用户 2026-10-20 要求）：拖动滑块也按两位小数走，
            // 不再像以前那样每格 0.1 地把 1.26 吸回 1.3。
            double speed = ClampDragScrollSpeed(e.NewValue);
            controller.UpdateDragScrollSpeed(speed);
            if (dragScrollSpeedValueText != null)
            {
                dragScrollSpeedValueText.Text = FormatDragScrollSpeed(speed);
            }
        }

    }
}
