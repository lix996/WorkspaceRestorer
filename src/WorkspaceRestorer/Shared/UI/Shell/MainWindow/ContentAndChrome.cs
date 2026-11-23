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

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        private UIElement BuildContent()
        {
            // Keep the three workflows in separate workspaces.  The left rail
            // stays stable while the right side only measures the active list,
            // so the window does not become a single crowded command console.
            Grid root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220), MinWidth = 180 });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
            // 【2026-11-15 用户要求】左栏和右侧工作区同处第 0 行，底边自然齐平；
            // 状态文字/进度条单独占右列第 1 行（高度 Auto），位置仍是最下面，内容不动。
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Border navigation = new Border
            {
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14)
            };
            Grid.SetColumn(navigation, 0);
            root.Children.Add(navigation);

            // The rail puts the three workflows on top and pins "设置" to the
            // bottom-left corner.
            Grid navigationContent = new Grid();
            // 上面的导航放进 ScrollViewer（星号行）后，窗口矮的时候它会自己滚动，
            // 底部“设置”那一行（Auto）永远留在可见范围内。
            navigationContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            navigationContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            navigationContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel navigationTop = new StackPanel();
            navigationTop.Children.Add(UiFactory.HeadingKey(ShellTextKeys.WindowTitle, 19));
            TextBlock chooseWorkspace = new TextBlock
            {
                Foreground = AppTheme.TextSecondary,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 18)
            };
            LocalizedText.Text(chooseWorkspace, ShellTextKeys.ChooseWorkspace);
            navigationTop.Children.Add(chooseWorkspace);

            Button startupNavigation = CreateNavigationButton(
                "\uE7E8", ShellTextKeys.NavStartupTitle, ShellTextKeys.NavStartupDescription);
            Button layoutNavigation = CreateNavigationButton(
                "\uE8B9", ShellTextKeys.NavLayoutTitle, ShellTextKeys.NavLayoutDescription);
            Button applicationsNavigation = CreateNavigationButton(
                "\uE721", ShellTextKeys.NavApplicationsTitle, ShellTextKeys.NavApplicationsDescription);
            // Bottom-left entry, styled like a chat app's rail: a small gear plus
            // the word "设置", with a soft rounded highlight on hover instead of
            // a permanent frame.
            // Real Segoe Fluent/MDL2 gear (E713) drawn with the icon font, so it
            // matches the familiar settings glyph instead of the thin Unicode
            // "⚙" that font fallback used to draw.
            TextBlock settingsGlyph = new TextBlock
            {
                Text = "\uE713",
                FontFamily = AppTheme.IconFont,
                FontSize = 15,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            TextBlock settingsLabel = new TextBlock
            {
                FontFamily = AppTheme.UiFont,
                FontSize = 13,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            LocalizedText.Text(settingsLabel, ShellTextKeys.NavSettings);
            StackPanel settingsRow = new StackPanel { Orientation = Orientation.Horizontal };
            settingsRow.Children.Add(settingsGlyph);
            settingsRow.Children.Add(settingsLabel);
            // A real Button (keyboard and screen-reader friendly) with a flat
            // template: rounded hover highlight, no permanent frame.
            Button shortcutsNavigation = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 6, 14, 6),
                Cursor = Cursors.Hand,
                Content = settingsRow
            };
            Style settingsEntryStyle = new Style(typeof(Button));
            ControlTemplate settingsEntryTemplate = new ControlTemplate(typeof(Button));
            FrameworkElementFactory settingsBackground = new FrameworkElementFactory(typeof(Border));
            settingsBackground.Name = "settingsEntryBackground";
            settingsBackground.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            settingsBackground.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            settingsBackground.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            FrameworkElementFactory settingsPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            settingsPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            settingsPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            settingsBackground.AppendChild(settingsPresenter);
            settingsEntryTemplate.VisualTree = settingsBackground;
            Trigger settingsHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            settingsHover.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.RailSurface),
                "settingsEntryBackground"));
            settingsEntryTemplate.Triggers.Add(settingsHover);
            settingsEntryStyle.Setters.Add(new Setter(Button.TemplateProperty, settingsEntryTemplate));
            settingsEntryStyle.Setters.Add(new Setter(Button.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            settingsEntryStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            shortcutsNavigation.Style = settingsEntryStyle;
            // Give the rail entry a name so screen readers (and UI tests) can find
            // it: its content is a panel, so WPF cannot derive one automatically.
            AccessibleToolTip.Attach(shortcutsNavigation, ShellToolTips.SettingsNavigation);

            System.Action<bool> setSettingsActive = delegate(bool active)
            {
                settingsGlyph.Foreground = active ? AppTheme.Accent : AppTheme.TextSecondary;
                settingsLabel.Foreground = active ? AppTheme.Accent : AppTheme.TextSecondary;
                shortcutsNavigation.Background = active
                    ? AppTheme.ActiveSurface
                    : System.Windows.Media.Brushes.Transparent;
            };
            AccessibleToolTip.Attach(startupNavigation, ShellToolTips.StartupNavigation);
            AccessibleToolTip.Attach(layoutNavigation, ShellToolTips.LayoutNavigation);
            AccessibleToolTip.Attach(applicationsNavigation, ShellToolTips.ApplicationsNavigation);
            AccessibleToolTip.Attach(shortcutsNavigation, ShellToolTips.SettingsNavigationFull);
            navigationTop.Children.Add(startupNavigation);
            // 用户要求（2026-10-23）：左栏顺序改成 启动与恢复 → 自启动应用 → 图标位置保存；
            // 只换按钮位置，Workspace 的枚举/切换逻辑不动。
            navigationTop.Children.Add(applicationsNavigation);
            navigationTop.Children.Add(layoutNavigation);
            // 回收站是全局功能（所有工作区共用一个），放在三个工作区下面。
            Button recycleBinNavigation = CreateNavigationButton(
                "\uE74D", ShellTextKeys.NavRecycleBinTitle, ShellTextKeys.NavRecycleBinDescription);
            // 与另外四个导航项保持一致：补上无障碍名称，读屏软件和 UI 自动化才能识别这一项。
            AccessibleToolTip.Attach(recycleBinNavigation, ShellToolTips.RecycleBinNavigation);
            navigationTop.Children.Add(recycleBinNavigation);
            TextBlock navigationHint = new TextBlock
            {
                Foreground = AppTheme.TextSecondary,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 18, 0, 0)
            };
            LocalizedText.Text(navigationHint, ShellTextKeys.NavigationHint);
            navigationTop.Children.Add(navigationHint);
            // 【2026-10-19 用户要求】窗口高度不够时，左栏内容不能顶到窗口外面：
            // 上半部分放进 ScrollViewer（星号行，内容长了自己滚），底部“设置”按钮始终钉在可见位置。
            ScrollViewer navigationScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = navigationTop
            };
            Grid.SetRow(navigationScroll, 0);
            navigationContent.Children.Add(navigationScroll);
            // Pinned to the bottom of the rail, hard against its left edge.
            shortcutsNavigation.Margin = new Thickness(-8, 0, 0, 0);
            Grid.SetRow(shortcutsNavigation, 2);
            navigationContent.Children.Add(shortcutsNavigation);
            navigation.Child = navigationContent;

            // 【2026-11-15 用户要求】两张卡片按临时绘图脚本的坐标紧贴：间距 0，
            // 左栏宽 220 不变；状态条左边距同步为 0。
            // 【2026-11-23 用户要求】恢复进度条必须画在工作区白卡内部，而且显示/隐藏
            // 不能把页面挤矮。这里不再给进度条单独留一行，只保留工作区这一层。
            Grid main = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            Grid.SetColumn(main, 1);
            root.Children.Add(main);

            // 【2026-11-15 用户要求】去掉顶部“工作空间”公共标题：它不产生任何操作，
            // 只占画面。状态文字仍在最下面；恢复进度条改到卡片内部（见下方 workspaces 覆盖层）。
            StackPanel statusDock = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            Grid statusRow = new Grid { VerticalAlignment = VerticalAlignment.Center };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            countdownText.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(countdownText, 0);
            statusRow.Children.Add(countdownText);
            statusText.TextWrapping = TextWrapping.Wrap;
            statusText.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(statusText, 1);
            statusRow.Children.Add(statusText);
            statusDock.Children.Add(statusRow);
            // 状态条只占右列底部，左栏不跟着变矮。
            Grid.SetColumn(statusDock, 1);
            Grid.SetRow(statusDock, 1);
            root.Children.Add(statusDock);

            Grid workspaces = new Grid { Margin = new Thickness(0) };
            main.Children.Add(workspaces);

            Border startupWorkspace = BuildStartupWorkspace();
            Border layoutWorkspace = BuildLayoutWorkspace();
            Border applicationsWorkspace = BuildApplicationsWorkspace();
            Border settingsWorkspace = BuildSettingsWorkspace();
            Border recycleBinWorkspace = BuildRecycleBinWorkspace();
            workspaces.Children.Add(startupWorkspace);
            workspaces.Children.Add(layoutWorkspace);
            workspaces.Children.Add(applicationsWorkspace);
            workspaces.Children.Add(settingsWorkspace);
            workspaces.Children.Add(recycleBinWorkspace);

            // 【2026-11-23 用户要求】恢复进度条只保留现在这一条，不新建、不给别的页面补。
            // 它作为 workspaces 的覆盖层画在当前白卡内部顶部：6px 高 + 6px 顶距，
            // 落在卡片 18px 的内边距里，不会盖住标题；右边留 18px 避开圆角。
            // 覆盖层不参与布局，所以显示/隐藏都不会再压缩页面高度。
            progressBar.Width = 260;
            progressBar.Height = 6;
            progressBar.Margin = new Thickness(0, 6, 18, 0);
            progressBar.HorizontalAlignment = HorizontalAlignment.Right;
            progressBar.VerticalAlignment = VerticalAlignment.Top;
            progressBar.IsHitTestVisible = false;
            Panel.SetZIndex(progressBar, 10);
            workspaces.Children.Add(progressBar);

            // 用户要求（2026-10-10 19:52）：“点击到回收站时，也要像其他三个的代码一样常驻亮。”
            // 真因：回收站按钮原来**没进这个数组**，点它时 ShowWorkspace 拿到的 selectedButton 是 null，
            // 于是三个按钮全被清成透明、回收站自己也没被点亮（看着就是“点了不亮”）。
            Button[] navigationButtons = new[] { startupNavigation, layoutNavigation, applicationsNavigation, recycleBinNavigation };
            UIElement[] workspaceElements = new UIElement[] { startupWorkspace, layoutWorkspace, applicationsWorkspace, settingsWorkspace, recycleBinWorkspace };
            startupNavigation.Click += delegate { setSettingsActive(false); PrepareWorkspaceForDisplay("startup"); ShowWorkspace(startupWorkspace, workspaceElements, startupNavigation, navigationButtons); };
            layoutNavigation.Click += delegate { setSettingsActive(false); PrepareWorkspaceForDisplay("layout"); ShowWorkspace(layoutWorkspace, workspaceElements, layoutNavigation, navigationButtons); };
            applicationsNavigation.Click += delegate { setSettingsActive(false); PrepareWorkspaceForDisplay("applications"); ShowWorkspace(applicationsWorkspace, workspaceElements, applicationsNavigation, navigationButtons); };
            // 设置 shows a page of its own: a nav list of sections on the left, the
            // active section on the right, filling the workspace area.
            recycleBinNavigation.Click += delegate
            {
                setSettingsActive(false);
                PrepareWorkspaceForDisplay("recycle");
                RefreshRecycleBinPage();
                ShowWorkspace(recycleBinWorkspace, workspaceElements, recycleBinNavigation, navigationButtons);
            };
            shortcutsNavigation.Click += delegate
            {
                setSettingsActive(true);
                PrepareWorkspaceForDisplay("settings");
                RefreshSettingsPanel();
                ShowWorkspace(settingsWorkspace, workspaceElements, null, navigationButtons);
            };
            setSettingsActive(false);
            ShowWorkspace(startupWorkspace, workspaceElements, startupNavigation, navigationButtons);



            // The restore toast floats above both columns and the workspace
            // content while it is visible.
            // 用户 2026-10-31 21:52：恢复完成/取消提示要放在**整个程序窗口正中**。
            // 原来右移了半个左栏（Margin=LeftRailWidth），看起来偏右；逻辑不变，只改位置。
            restoreNotice.Margin = new Thickness(0);
            Grid.SetColumn(restoreNotice, 0);
            Grid.SetColumnSpan(restoreNotice, 2);
            // 浮层跨“内容 + 状态”两行，仍然以整窗为参照居中。
            Grid.SetRowSpan(restoreNotice, 2);
            Panel.SetZIndex(restoreNotice, 100);
            root.Children.Add(restoreNotice);

            // 快捷键浮窗：跨两列，水平居中于**整个程序窗口**（用户要求“程序页面中间”），
            // 垂直也居中；不加 LeftRailWidth 边距，避免偏到右侧。
            Grid.SetColumn(shortcutFeedbackToast, 0);
            Grid.SetColumnSpan(shortcutFeedbackToast, 2);
            Grid.SetRowSpan(shortcutFeedbackToast, 2);
            shortcutFeedbackToast.Margin = new Thickness(0);
            Panel.SetZIndex(shortcutFeedbackToast, 150);
            root.Children.Add(shortcutFeedbackToast);

            // CPU/内存超限等待浮窗：窗口正中，压在恢复卡片之上；
            // 是否显示、文字实时内容由 ApplicationController 决定。
            Grid.SetColumn(loadLimitPrompt, 0);
            Grid.SetColumnSpan(loadLimitPrompt, 2);
            Grid.SetRowSpan(loadLimitPrompt, 2);
            loadLimitPrompt.Margin = new Thickness(0);
            Panel.SetZIndex(loadLimitPrompt, 160);
            root.Children.Add(loadLimitPrompt);

            // The saving-in-progress card sits below the completion banner.
            Grid.SetColumnSpan(savingOverlay, 2);
            Grid.SetRowSpan(savingOverlay, 2);
            Panel.SetZIndex(savingOverlay, 90);
            root.Children.Add(savingOverlay);

            // The restoring-in-progress card sits at the same level.
            Grid.SetColumnSpan(restoreOverlay, 2);
            Grid.SetRowSpan(restoreOverlay, 2);
            Panel.SetZIndex(restoreOverlay, 90);
            root.Children.Add(restoreOverlay);

            // The floating "dragged card" shown while reordering, above all.
            Canvas dragLayer = new Canvas { IsHitTestVisible = false };
            Grid.SetColumnSpan(dragLayer, 2);
            Grid.SetRowSpan(dragLayer, 2);
            Panel.SetZIndex(dragLayer, 200);
            // 【2026-10-17 用户要求】“跟随鼠标移动的卡片只能透过颜色，不能透过线条。”
            // 浮层里除了这张浮动卡片，还画着插入线、并入高亮框、“松手：并入…”提示这些**落点反馈**。
            // Canvas 的绘制顺序默认就是子元素加入顺序 —— 反馈是拖动过程中才加的，会盖在浮动卡片**上面**，
            // 于是卡片和落点框重叠的地方能看见框线“穿过去”，很难看。
            // 这里给它们定死层级：落点反馈在下（10），浮动卡片在上（50），提示气泡再上（60）。
            // 浮动卡片本身是**不透明**的（背景 Surface 白），所以重叠处的框线被它整块挡住；
            // 框的颜色仍然在卡片四周露出来 —— 正好是用户要的“只透颜色、不透线条”。
            Panel.SetZIndex(dragGhost.Element, 50);
            dragLayer.Children.Add(dragGhost.Element);

            // 清单下方那块"白色空白区域"是页面里真实的一块地方（见
            // StartupWorkspacePage.cs 的 profileDropPad），不再用浮层画。
            // The insertion hint is drawn on this same proven surface, in window
            // coordinates at the seam between two rows.
            dragInsertionLayer = dragLayer;

            root.Children.Add(dragLayer);
            return root;
        }

        /// <summary>
        /// Left-hand "设置" workspace: documents the keyboard shortcuts the
        /// window currently supports.
        /// </summary>
        /// <summary>
        /// The 设置 workspace.  It is divided into sections exactly like the left
        /// rail is divided into workspaces: 总设置 holds the options that apply
        /// everywhere, and each workspace gets its own section.  Sections without
        /// options yet are kept as empty shells on purpose, so new settings have
        /// an obvious home and nothing has to be reshuffled later.
        /// </summary>
        // ------------------------------------------------------------------
        // 设置面板（照 DSH 设置弹层的逻辑）
        //
        // 结构：标题 + 关闭按钮 / 左侧分区列表 / 右侧当前分区内容 / 左下角"重置"。
        // 分区是**注册式**的：CreateSettingsSections() 里按顺序列出来，
        // 每个分区自带 id、标题、图标和内容构建方法；以后加设置只需往这个列表里
        // 加一条，菜单、切换、重置入口都会自动跟着走，不必改结构代码。
        // ------------------------------------------------------------------

        /// <summary>
        /// Declarative list of settings sections, in the order they appear.
        /// </summary>
        private static StackPanel CreateTimingSetting(string label, TextBox valueBox, string help)
        {
            StackPanel setting = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 4, 12, 0) };
            TextBlock labelText = new TextBlock { Text = label, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            setting.Children.Add(labelText);
            setting.Children.Add(valueBox);
            if (!string.IsNullOrWhiteSpace(help))
            {
                // Hovering either the label or the box explains the setting.
                AccessibleToolTip.Attach(labelText, label, help);
                AccessibleToolTip.Attach(valueBox, label, help);
                AccessibleToolTip.AttachDescription(setting, help, null);
            }
            return setting;
        }

        private static Border WorkspaceBorder(UIElement child)
        {
            return new Border
            {
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18),
                Child = child
            };
        }

        /// <summary>
        /// Hover highlight for the rail tiles and the settings nav rows.  Their
        /// background is set locally (transparent or active) and a local value
        /// outranks a style trigger in WPF, so the highlight is applied directly.
        /// </summary>
        private static void AttachTileHover(Button button, Func<bool> isActive)
        {
            button.MouseEnter += delegate
            {
                button.Background = isActive() ? AppTheme.NavigationActiveSurface : AppTheme.HoverSurface;
            };
            button.MouseLeave += delegate
            {
                button.Background = isActive() ? AppTheme.ActiveSurface : System.Windows.Media.Brushes.Transparent;
            };
        }

        private static Button CreateNavigationButton(string icon, string titleKey, string descriptionKey)
        {
            Button button = new Button
            {
                Height = 68,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4),
                // 工作区身份记在 Tag 里（key 不随语言变化），显示名另外从当前语言取。
                Tag = titleKey
            };
            AttachTileHover(button, delegate { return object.ReferenceEquals(button, activeNavigationButton); });
            // 图标 + 文字用 Grid（图标 Auto、文字星号）：窗口变窄时说明文字会**换行**，
            // 不会像横向 StackPanel 那样超出按钮、被右边缘裁掉。
            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock navIcon = new TextBlock
            {
                Text = UiFactory.IconGlyph(icon),
                FontFamily = AppTheme.IconFont,
                FontSize = 22,
                Foreground = AppTheme.Accent,
                Width = 34,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(navIcon, 0);
            content.Children.Add(navIcon);
            StackPanel labels = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            TextBlock titleText = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis };
            LocalizedText.Text(titleText, titleKey);
            labels.Children.Add(titleText);
            TextBlock descriptionText = new TextBlock { FontSize = 11, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap };
            LocalizedText.Text(descriptionText, descriptionKey);
            labels.Children.Add(descriptionText);
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);
            button.Content = content;
            return button;
        }

        private static void ShowWorkspace(UIElement selected, UIElement[] workspaces, Button selectedButton, Button[] navigationButtons)
        {
            for (int index = 0; index < workspaces.Length; index++)
            {
                workspaces[index].Visibility = workspaces[index] == selected ? Visibility.Visible : Visibility.Collapsed;
            }
            activeNavigationButton = selectedButton;
            // 记住“现在在哪个工作区”：撤销记录、快捷键日志都要写上它
            // （用户 2026-10-17 的原话：“能够让：启动与恢复，图标位置保存，自启动应用，回收站等都能用上”）。
            // 导航按钮的无障碍名称就是工作区名（见上方 AccessibleToolTip.Attach(…_Navigation, "启动与恢复", …)），
            // 所以这里不用再单独维护一张对照表。
            if (selectedButton != null)
            {
                string workspaceKey = selectedButton.Tag as string;
                if (!string.IsNullOrEmpty(workspaceKey))
                {
                    activeWorkspaceKey = workspaceKey;
                }
                string name = System.Windows.Automation.AutomationProperties.GetName(selectedButton);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    activeWorkspaceName = name.Trim();
                }
            }
            for (int index = 0; index < navigationButtons.Length; index++)
            {
                bool active = navigationButtons[index] == selectedButton;
                navigationButtons[index].Background = active
                    ? AppTheme.ActiveSurface
                    : System.Windows.Media.Brushes.Transparent;
                navigationButtons[index].BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
        }

        private static Border BuildPanel(string heading, TextBlock summary, UIElement content)
        {
            StackPanel panel = new StackPanel();
            panel.Children.Add(UiFactory.Heading(heading, 16));
            panel.Children.Add(summary);
            panel.Children.Add(content);
            return new Border { Background = AppTheme.Surface, BorderBrush = AppTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = panel };
        }

        private static TextBox CreateNumberBox()
        {
            return new TextBox { Width = 54, Height = 32, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 4 };
        }

        private static TextBox CreateDecimalBox()
        {
            return new TextBox { Width = 60, Height = 32, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 6 };
        }





    }
}
