using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI.Cards.Actions;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 设置页的“快捷键”独立分区。
    ///
    /// 2026-11-16 用户要求按 ComfyUI 的排版：
    ///   · 表头“按键绑定 | 指令 | 操作”，键位在左、指令在右；
    ///   · 键位拆成独立键帽，中间用一个小号“+”连接；
    ///   · 搜索用公共的“搜索方案”按钮外观 + 公共搜索弹窗（不另写行内搜索框）；
    ///   · 一条命令固定一行；多条绑定用右侧向右箭头 + “+N” 收进浮窗逐条管理，
    ///     浮窗里每条绑定一行（键位 / 修改 / 删除），表格本身不再往下多出行。
    /// 外观复用 Cards/Actions 的 32×32 图标按钮，不另写按钮样式。
    ///
    /// 2026-11-17 用户拍板：按键绑定 / 指令按 1:1 分宽度，操作列由四个按钮自然撑开；
    /// 绑定再多也只在自己的列里收纳（右侧向右箭头 + “+N” 徽标），不挤压其他列。
    /// 表格整体放在局部横向滚动里：窗口缩到三列放不下时，表头和行一起左右滚动，
    /// 操作列不会被挤出窗口，页面其他部分也不会横向漂移。
    /// </summary>
    public sealed partial class MainWindow
    {
        // 三列都放得下的最小内容宽（DIP）：绑定 + 指令各约 170，操作四格 140。
        // 低于这个宽度才让表格局部横向滚动；常见 125% 缩放 + 1400 宽窗口仍能完整显示。
        private const double ShortcutTableMinContentWidth = 500.0;

        // 键帽长按复制：按住左键约 0.5 秒直接复制键位文字，移动超过 4 DIP 视为拖动而取消。
        private const int ShortcutChipHoldCopyMilliseconds = 500;
        private const double ShortcutChipHoldCopyMoveTolerance = 4.0;
        // 长按复制后压住双击的时间：覆盖系统默认双击间隔（500ms）再留一点余量。
        private const int ShortcutChipHoldCopyDoubleClickGuardMilliseconds = 600;

        private StackPanel shortcutSettingsList;
        private ScrollViewer shortcutTableScroll;
        private ScrollViewer shortcutHeaderScroll;
        private FrameworkElement shortcutTableHeader;
        private ShortcutSettingsPager shortcutPager;
        // 只缓存当前生效值；真正的持久化来源是 controller.Settings.ShortcutPageSize。
        private int shortcutPageSize = ShortcutPageSettings.DefaultPageSize;
        private int shortcutCurrentPage = 1;
        private SelectableText shortcutSettingsCount;
        private TextBlock shortcutSettingsError;
        private readonly Dictionary<string, Border> shortcutSettingsRowHosts =
            new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private int shortcutRowOrdinal;

        private DispatcherTimer shortcutHighlightTimer;
        private Border shortcutHighlightRow;
        private Brush shortcutHighlightBaseBrush;

        private UIElement BuildShortcutSettingsSection()
        {
            Grid section = new Grid();
            section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            section.VerticalAlignment = VerticalAlignment.Top;
            BindShortcutSectionHeight(section);

            StackPanel headerArea = new StackPanel();
            headerArea.Children.Add(BuildSettingsSectionHeaderKey(
                SettingsTextKeys.ShortcutsTitle,
                SettingsTextKeys.ShortcutsShort,
                SettingsTextKeys.ShortcutsSubtitle));

            shortcutSettingsCount = UiFactory.SupportingText(string.Empty);
            shortcutSettingsCount.VerticalAlignment = VerticalAlignment.Center;
            shortcutSettingsCount.Margin = new Thickness(0, 2, 0, 0);
            shortcutSettingsCount.Visibility = Visibility.Collapsed;
            headerArea.Children.Add(shortcutSettingsCount);

            shortcutSettingsError = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.Danger,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };
            headerArea.Children.Add(shortcutSettingsError);
            headerArea.Children.Add(BuildShortcutToolbar());
            headerArea.PreviewMouseWheel += ForwardShortcutWheelToRows;
            Grid.SetRow(headerArea, 0);
            section.Children.Add(headerArea);

            // 表头固定；只有下面的行列表上下滚动。横向滚动时表头和行同步偏移。
            Grid tableHost = new Grid();
            tableHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            tableHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            shortcutTableHeader = BuildShortcutTableHeader() as FrameworkElement;
            shortcutHeaderScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                CanContentScroll = false,
                Focusable = false,
                Content = shortcutTableHeader
            };
            shortcutHeaderScroll.PreviewMouseWheel += ForwardShortcutWheelToRows;
            Grid.SetRow(shortcutHeaderScroll, 0);
            tableHost.Children.Add(shortcutHeaderScroll);

            shortcutSettingsList = new StackPanel();
            shortcutTableScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false,
                Content = shortcutSettingsList
            };
            AutomationProperties.SetAutomationId(shortcutTableScroll, "ShortcutTableScroll");
            // 横向滚动开启后 ScrollViewer 会用无限宽测量内容，星号列会退化成 Auto；
            // 因此显式把内容宽度钉在 max(视口宽, 最小内容宽)，保证 1:1 分列且可整体滚动。
            shortcutTableScroll.SizeChanged += delegate { UpdateShortcutTableViewport(); };
            shortcutTableScroll.Loaded += delegate { UpdateShortcutTableViewport(); };
            shortcutTableScroll.LayoutUpdated += delegate
            {
                if (shortcutSettingsList == null)
                {
                    return;
                }
                double viewportWidth = shortcutTableScroll.ViewportWidth;
                if (viewportWidth <= 0)
                {
                    return;
                }
                double targetWidth = Math.Max(ShortcutTableMinContentWidth, viewportWidth);
                if (Math.Abs(shortcutSettingsList.Width - targetWidth) > 0.5)
                {
                    UpdateShortcutTableViewport();
                }
            };
            shortcutTableScroll.ScrollChanged += delegate(object sender, ScrollChangedEventArgs args)
            {
                // 竖向滚动条出现/消失会改变 ViewportWidth；重新钉一次内容宽度，
                // 避免差出一条滚动条宽度而凭空出现横向滚动条。
                if (Math.Abs(args.ViewportWidthChange) > 0.1)
                {
                    UpdateShortcutTableViewport();
                }
                if (shortcutHeaderScroll != null
                    && Math.Abs(shortcutHeaderScroll.HorizontalOffset
                        - shortcutTableScroll.HorizontalOffset) > 0.1)
                {
                    shortcutHeaderScroll.ScrollToHorizontalOffset(
                        shortcutTableScroll.HorizontalOffset);
                }
            };
            Grid.SetRow(shortcutTableScroll, 1);
            tableHost.Children.Add(shortcutTableScroll);

            Grid.SetRow(tableHost, 1);
            section.Children.Add(tableHost);

            // 2026-11-19 按 ComfyUI KeybindingPanel 加分页：只有一页也显示。
            // 【2026-11-19】分页条挂在设置页右侧的固定底栏，不再跟着表格内容滚动。
            shortcutPager = new ShortcutSettingsPager();
            shortcutPager.PageChanged += delegate
            {
                shortcutCurrentPage = shortcutPager.CurrentPage;
                ResetShortcutTableScroll();
                RefreshShortcutSettingsSection();
            };
            shortcutPager.PageSizeChanged += delegate
            {
                shortcutPageSize = shortcutPager.PageSize;
                // 每页条数跟其它设置一样落 settings.json；只在用户改下拉时写，初始化 SetState 不写。
                controller.UpdateShortcutPageSize(shortcutPageSize);
                ResetShortcutTableScroll();
                RefreshShortcutSettingsSection();
            };
            if (shortcutPagerHost != null)
            {
                shortcutPagerHost.Children.Clear();
                shortcutPagerHost.Children.Add(shortcutPager);
            }
            else
            {
                // 兜底：没有设置页骨架时仍把分页条放在表格下方。
                Grid.SetRow(shortcutPager, 2);
                section.Children.Add(shortcutPager);
            }

            RefreshShortcutSettingsSection();
            return section;
        }

        private void UpdateShortcutTableViewport()
        {
            if (shortcutTableScroll == null || shortcutSettingsList == null)
            {
                return;
            }
            // 行滚动区有竖向滚动条时 ViewportWidth 才是真正可用的列宽；
            // 首次布局取不到 ViewportWidth 时退回 ActualWidth。
            double width = shortcutTableScroll.ViewportWidth;
            if (width <= 0)
            {
                width = shortcutTableScroll.ActualWidth;
            }
            if (width <= 0)
            {
                return;
            }
            // 留 1 DIP 余量，避免 DPI 取整后比 ViewportWidth 多出零点几像素，
            // WPF 会因此常驻一条没用的横向滚动条。
            double contentWidth = Math.Max(ShortcutTableMinContentWidth, width - 1.0);
            shortcutSettingsList.Width = contentWidth;
            if (shortcutTableHeader != null)
            {
                shortcutTableHeader.Width = contentWidth;
            }
            if (shortcutHeaderScroll != null
                && Math.Abs(shortcutHeaderScroll.HorizontalOffset
                    - shortcutTableScroll.HorizontalOffset) > 0.1)
            {
                shortcutHeaderScroll.ScrollToHorizontalOffset(
                    shortcutTableScroll.HorizontalOffset);
            }
        }

        private void BindShortcutSectionHeight(FrameworkElement section)
        {
            if (section == null || settingsContentScroll == null)
            {
                return;
            }
            section.SetBinding(
                FrameworkElement.HeightProperty,
                new Binding("ViewportHeight")
                {
                    Source = settingsContentScroll,
                    Mode = BindingMode.OneWay
                });
        }

        private void ResetShortcutTableScroll()
        {
            if (shortcutTableScroll != null)
            {
                shortcutTableScroll.ScrollToVerticalOffset(0);
            }
        }

        private void ForwardShortcutWheelToRows(object sender, MouseWheelEventArgs args)
        {
            if (shortcutTableScroll == null || args == null)
            {
                return;
            }
            shortcutTableScroll.ScrollToVerticalOffset(
                shortcutTableScroll.VerticalOffset - args.Delta / 3.0);
            args.Handled = true;
        }

        /// <summary>顶部工具栏：左边公共搜索按钮，右边重置所有快捷键。</summary>
        private UIElement BuildShortcutToolbar()
        {
            Grid toolbar = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 和启动与恢复/自启动应用/图标位置保存/回收站的“搜索方案”同一个公共按钮外观。
            Button search = UiFactory.CommandButtonKey(
                "\uE721", SettingsTextKeys.ShortcutSearchButton, false);
            search.HorizontalAlignment = HorizontalAlignment.Left;
            AccessibleToolTip.Attach(
                search,
                TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutSearchButton,
                    SettingsTextKeys.ShortcutSearchTitle,
                    SettingsTextKeys.ShortcutSearchDescription,
                    null));
            AutomationProperties.SetAutomationId(search, "ShortcutSearch");
            search.Click += delegate { OpenShortcutSearch(); };
            Grid.SetColumn(search, 0);
            toolbar.Children.Add(search);

            Button reset = UiFactory.CommandButtonKey(
                "\uE777", SettingsTextKeys.ShortcutResetAllLabel, false);
            reset.HorizontalAlignment = HorizontalAlignment.Right;
            AccessibleToolTip.Attach(
                reset,
                TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutResetAllLabel,
                    SettingsTextKeys.ShortcutResetAllLabel,
                    SettingsTextKeys.ShortcutResetAllShort,
                    SettingsTextKeys.ShortcutResetAllDetail));
            AutomationProperties.SetAutomationId(reset, "ShortcutResetAll");
            reset.Click += delegate { ResetAllShortcuts(); };
            Grid.SetColumn(reset, 1);
            toolbar.Children.Add(reset);
            return toolbar;
        }

        /// <summary>
        /// 按键绑定 / 指令 / 操作三段共用的列定义。
        /// 绑定和指令各占 1 份剩余宽度（1:1），操作列由四个按钮自然撑开；
        /// 绑定再多也只会收进自己的列，不会把操作列挤出窗口。
        /// </summary>
        private static void ConfigureShortcutTableColumns(Grid grid)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                // 操作列固定容下四格按钮，表头和每行都按这个下限算，标题才能和按钮起点对齐。
                Width = GridLength.Auto,
                MinWidth = 4 * ActionMetrics.ButtonSize + 3 * ActionMetrics.Gap
            });
        }

        /// <summary>表头：按键绑定 | 指令 | 操作。</summary>
        private UIElement BuildShortcutTableHeader()
        {
            Grid grid = new Grid();
            ConfigureShortcutTableColumns(grid);

            TextBlock binding = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutTableBinding),
                FontSize = AppFonts.Caption,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(binding, 0);
            grid.Children.Add(binding);

            TextBlock command = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutTableCommand),
                FontSize = AppFonts.Caption,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            Grid.SetColumn(command, 1);
            grid.Children.Add(command);

            TextBlock actions = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutTableActions),
                FontSize = AppFonts.Caption,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            Border header = new Border
            {
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(10, 6, 10, 6),
                Child = grid
            };
            AutomationProperties.SetAutomationId(header, "ShortcutTableHeader");
            return header;
        }

        /// <summary>
        /// 按当前目录重建表格。修改/新增/删除/恢复后都走这里；
        /// 行数会随绑定增减，直接重建最稳。
        /// </summary>
        private void RefreshShortcutSettingsSection()
        {
            if (shortcutSettingsList == null)
            {
                return;
            }
            StopShortcutHighlight();
            ClearShortcutPageError();
            shortcutSettingsList.Children.Clear();
            shortcutSettingsRowHosts.Clear();

            // 以持久化设置为准：首次打开、重置全部设置、从别的入口改了设置，都按磁盘里的值重建。
            shortcutPageSize = ShortcutPageSettings.NormalizePageSize(
                controller.Settings.ShortcutPageSize);

            IList<ShortcutCatalog.Shortcut> shortcuts = ShortcutCatalog.All();
            int commandCount = 0;
            int bindingCount = 0;
            for (int index = 0; index < shortcuts.Count; index++)
            {
                ShortcutCatalog.Shortcut shortcut = shortcuts[index];
                if (shortcut == null)
                {
                    continue;
                }
                if (shortcut.IsEditable)
                {
                    commandCount++;
                    bindingCount += shortcut.Bindings == null ? 0 : shortcut.Bindings.Count;
                }
            }

            int totalRowCount = shortcuts.Count;

            int pageCount = ShortcutSettingsPager.ComputePageCount(totalRowCount, shortcutPageSize);
            shortcutCurrentPage = ShortcutSettingsPager.ClampPage(shortcutCurrentPage, pageCount);
            int pageStart = ShortcutSettingsPager.GetPageStart(
                shortcutCurrentPage, shortcutPageSize, totalRowCount);
            int pageLength = ShortcutSettingsPager.GetPageLength(
                shortcutCurrentPage, shortcutPageSize, totalRowCount);

            shortcutRowOrdinal = 0;
            for (int offset = 0; offset < pageLength; offset++)
            {
                int rowIndex = pageStart + offset;
                ShortcutCatalog.Shortcut shortcut = shortcuts[rowIndex];
                if (shortcut == null)
                {
                    continue;
                }
                try
                {
                    shortcutSettingsList.Children.Add(BuildShortcutRow(shortcut));
                }
                catch (Exception exception)
                {
                    // 设置页重建失败不能留一整块空白，也不能让一个坏条目把整个应用带崩：
                    // 记日志 + 画一行错误提示，其余命令继续显示。
                    AppLog.Write("[快捷键页] 重建行失败："
                        + (shortcut.Id ?? shortcut.TitleKey ?? "?"), exception);
                    TextBlock error = new TextBlock
                    {
                        Text = AppText.T(SettingsTextKeys.ShortcutPageBuildFailed),
                        FontSize = AppFonts.Caption,
                        Foreground = AppTheme.Danger,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 6)
                    };
                    shortcutSettingsList.Children.Add(error);
                }
            }

            if (shortcutPager != null)
            {
                shortcutPager.SetState(totalRowCount, shortcutPageSize, shortcutCurrentPage);
                shortcutPageSize = shortcutPager.PageSize;
                shortcutCurrentPage = shortcutPager.CurrentPage;
            }

            if (shortcutSettingsCount != null)
            {
                LocalizedText.Text(
                    shortcutSettingsCount,
                    SettingsTextKeys.ShortcutsCountFormat,
                    commandCount,
                    bindingCount);
                shortcutSettingsCount.Visibility = commandCount > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private UIElement BuildShortcutRow(ShortcutCatalog.Shortcut shortcut)
        {
            if (string.Equals(shortcut.Id, ShortcutCatalog.DragGestureId, StringComparison.Ordinal))
            {
                return BuildDragGestureRow(shortcut);
            }
            if (!shortcut.IsEditable)
            {
                return BuildFixedShortcutRow(shortcut);
            }

            StackPanel block = new StackPanel();
            if (shortcut.Bindings == null || shortcut.Bindings.Count == 0)
            {
                Border row = BuildShortcutTableRow(
                    BuildEmptyBindingCell(shortcut),
                    BuildShortcutSummaryLine(
                        shortcut,
                        string.IsNullOrWhiteSpace(shortcut.SummaryKey) ? shortcut.TitleKey : shortcut.SummaryKey),
                    BuildCommandActionsCell(shortcut));
                AutomationProperties.SetAutomationId(row, "ShortcutRow_" + shortcut.Id);
                shortcutSettingsRowHosts[shortcut.Id] = row;
                block.Children.Add(row);
                return block;
            }

            // 一条命令固定一行：只显示第一条绑定，其余收进右侧箭头（“+N” 徽标），
            // 点箭头弹出浮窗管理全部绑定，不在表格下面新增行。
            Border oneRow = BuildShortcutTableRow(
                BuildBindingPreview(shortcut),
                BuildShortcutSummaryLine(
                    shortcut,
                    string.IsNullOrWhiteSpace(shortcut.SummaryKey) ? shortcut.TitleKey : shortcut.SummaryKey),
                BuildBindingActionsCell(shortcut, 0, true));
            AutomationProperties.SetAutomationId(oneRow, "ShortcutRow_" + shortcut.Id);
            shortcutSettingsRowHosts[shortcut.Id] = oneRow;
            block.Children.Add(oneRow);
            return block;
        }

        /// <summary>
        /// 一行式绑定预览：只显示第一条绑定；有多条时右侧出现向右箭头，
        /// 箭头内的小数字（+N）和悬浮提示都表示还有几条没显示。
        /// </summary>
        private FrameworkElement BuildBindingPreview(ShortcutCatalog.Shortcut shortcut)
        {
            if (shortcut.Bindings == null || shortcut.Bindings.Count == 0)
            {
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            ShortcutBinding firstBinding = shortcut.Bindings[0];
            FrameworkElement chips = BuildBindingChips(
                shortcut, 0, firstBinding == null ? string.Empty : firstBinding.Keys);
            if (shortcut.Bindings.Count <= 1)
            {
                return chips;
            }
            return BuildBindingCellWithToggle(shortcut, chips, shortcut.Bindings.Count - 1);
        }

        /// <summary>
        /// 绑定列内容：左侧键位（超出列宽时裁掉）+ 右侧固定的向右箭头。
        /// 箭头不会跟着键位长短移动，多条绑定的入口永远在同一列的最右边。
        /// </summary>
        private FrameworkElement BuildBindingCellWithToggle(
            ShortcutCatalog.Shortcut shortcut,
            FrameworkElement chips,
            int hiddenCount)
        {
            Grid host = new Grid { VerticalAlignment = VerticalAlignment.Center };
            host.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            host.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            if (chips != null)
            {
                // 单独一层裁剪容器：键位再长也只裁在“键位列”里，不会盖住右侧箭头。
                Grid clipHost = new Grid { ClipToBounds = true };
                chips.HorizontalAlignment = HorizontalAlignment.Left;
                chips.VerticalAlignment = VerticalAlignment.Center;
                clipHost.Children.Add(chips);
                Grid.SetColumn(clipHost, 0);
                host.Children.Add(clipHost);
            }

            // 浮窗锚定在整个绑定格上，向下弹出时不会因为箭头贴右而溢出窗口。
            Button toggle = BuildShortcutExpandToggle(shortcut, hiddenCount, host);
            Grid.SetColumn(toggle, 1);
            host.Children.Add(toggle);
            return host;
        }

        /// <summary>
        /// 多条绑定入口：图标前带“+N”小徽标，图标始终是向右箭头；
        /// 点它弹出“全部快捷键”浮窗，不改变表格行数。
        /// </summary>
        private Button BuildShortcutExpandToggle(
            ShortcutCatalog.Shortcut shortcut,
            int hiddenCount,
            FrameworkElement anchor)
        {
            TooltipDefinition tip = TooltipDefinition.Plain(
                SettingsTextKeys.ShortcutExpandMoreFormat, null);
            tip.WithArguments(hiddenCount);

            Button button = UiFactory.IconButton("\uE76C", tip);
            // IconButton 样式默认 38×38；这里改成紧凑的 24 高，宽度由“徽标 + 箭头”决定。
            button.Width = double.NaN;
            button.Height = 24;
            button.MinHeight = 24;
            button.MinWidth = 24;
            button.Padding = new Thickness(2, 0, 2, 0);
            button.Margin = new Thickness(6, 0, 0, 0);

            StackPanel content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(new TextBlock
            {
                Text = "+" + hiddenCount,
                FontSize = AppFonts.Caption,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = UiFactory.IconGlyph("\uE76C"),
                FontFamily = AppFonts.Icon,
                FontSize = 12,
                LineHeight = 18,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Center
            });
            button.Content = content;

            AutomationProperties.SetAutomationId(button, "ShortcutExpand_" + shortcut.Id);
            // 按钮内容不是字符串，读屏不会自动取到“+N”；显式给一个和悬浮提示一致的名字。
            AutomationProperties.SetName(
                button,
                AppText.F(SettingsTextKeys.ShortcutExpandMoreFormat, hiddenCount));
            button.Click += delegate { OpenMoreBindingsPopup(shortcut, anchor); };
            return button;
        }

        private static void ClearButtonTooltip(Button button)
        {
            if (button == null)
            {
                return;
            }
            button.ToolTip = null;
            ToolTipService.SetIsEnabled(button, false);
        }

        /// <summary>
        /// “全部快捷键”浮窗：点绑定列右侧箭头时弹出，列出这条命令的所有绑定。
        /// 可双击键帽修改，也可用每行的铅笔/垃圾桶修改或删除；
        /// 不提供“展开全部”，关闭后表格仍是原来的一行。
        /// </summary>
        private void OpenMoreBindingsPopup(
            ShortcutCatalog.Shortcut shortcut,
            FrameworkElement anchor)
        {
            if (shortcut == null || anchor == null || shortcut.Bindings == null)
            {
                return;
            }
            int count = shortcut.Bindings.Count;
            if (count == 0)
            {
                return;
            }

            Popup popup = null;
            StackPanel content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutOtherBindings),
                FontSize = AppFonts.Caption,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(2, 0, 2, 6)
            });

            for (int index = 0; index < count; index++)
            {
                int bindingIndex = index;
                ShortcutBinding binding = shortcut.Bindings[index];
                string keys = binding == null ? string.Empty : binding.Keys;

                Grid line = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                FrameworkElement chips = BuildKeyChips(
                    keys,
                    delegate
                    {
                        if (popup != null)
                        {
                            popup.IsOpen = false;
                        }
                        BeginEditShortcutBinding(shortcut, bindingIndex);
                    },
                    null,
                    null,
                    null);
                Grid.SetColumn(chips, 0);
                line.Children.Add(chips);

                Button edit = CreateSmallActionButton(ActionSlot.Edit);
                edit.Margin = new Thickness(6, 0, 0, 0);
                AutomationProperties.SetAutomationId(
                    edit, "ShortcutPopupEdit_" + shortcut.Id + "_" + bindingIndex);
                edit.Click += delegate
                {
                    if (popup != null)
                    {
                        popup.IsOpen = false;
                    }
                    BeginEditShortcutBinding(shortcut, bindingIndex);
                };
                Grid.SetColumn(edit, 1);
                line.Children.Add(edit);

                Button delete = CreateSmallActionButton(ActionSlot.Delete);
                delete.Margin = new Thickness(2, 0, 0, 0);
                AutomationProperties.SetAutomationId(
                    delete, "ShortcutPopupDelete_" + shortcut.Id + "_" + bindingIndex);
                delete.Click += delegate
                {
                    if (popup != null)
                    {
                        popup.IsOpen = false;
                    }
                    DeleteShortcutBinding(shortcut, bindingIndex);
                };
                Grid.SetColumn(delete, 2);
                line.Children.Add(delete);

                ContextMenus.Attach(line, BuildShortcutCopyMenu(keys));
                content.Children.Add(line);
            }

            Border card = new Border
            {
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                MinWidth = 240,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.18,
                    Color = Colors.Black
                },
                Child = content
            };

            popup = new Popup
            {
                AllowsTransparency = true,
                StaysOpen = false,
                PlacementTarget = anchor,
                Placement = PlacementMode.Bottom,
                VerticalOffset = 4,
                PopupAnimation = PopupAnimation.Fade,
                Child = card
            };
            popup.IsOpen = true;
        }

        private static Button CreateSmallActionButton(ActionSlot slot)
        {
            Button button = slot == ActionSlot.Delete
                ? ActionButtons.Delete((TooltipDefinition)null)
                : ActionButtons.EditPencil(null);
            button.Width = 28;
            button.Height = 28;
            button.MinHeight = 28;
            button.Padding = new Thickness(0);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.VerticalAlignment = VerticalAlignment.Center;
            ClearButtonTooltip(button);
            return button;
        }

        private UIElement BuildFixedShortcutRow(ShortcutCatalog.Shortcut shortcut)
        {
            FrameworkElement bindingCell = BuildStaticBindingChips(shortcut);
            Grid commandCell = BuildShortcutSummaryLine(
                shortcut,
                string.IsNullOrWhiteSpace(shortcut.SummaryKey) ? shortcut.TitleKey : shortcut.SummaryKey);
            if (commandCell != null)
            {
                commandCell.Opacity = 0.72;
            }
            Border row = BuildShortcutTableRow(bindingCell, commandCell, null);
            if (!string.IsNullOrWhiteSpace(shortcut.KeysKey))
            {
                AutomationProperties.SetAutomationId(row, "ShortcutFixed_" + shortcut.KeysKey);
            }
            return row;
        }

        private Border BuildShortcutTableRow(
            FrameworkElement bindingCell,
            FrameworkElement commandCell,
            FrameworkElement actionsCell)
        {
            int ordinal = shortcutRowOrdinal++;
            Border row = new Border
            {
                MinHeight = 46,
                Padding = new Thickness(10, 6, 10, 6),
                Background = (ordinal % 2 == 0) ? Brushes.Transparent : AppTheme.RowSurface,
                BorderBrush = AppTheme.SoftBorder,
                BorderThickness = new Thickness(0, 0, 0, 1)
            };

            Grid grid = new Grid();
            ConfigureShortcutTableColumns(grid);

            if (bindingCell != null)
            {
                // 绑定列只占自己的 1 份宽度：内容超出就在列内裁掉（箭头仍在列尾）。
                bindingCell.ClipToBounds = true;
                bindingCell.Margin = new Thickness(0, 0, 6, 0);
                bindingCell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(bindingCell, 0);
                grid.Children.Add(bindingCell);
            }
            if (commandCell != null)
            {
                commandCell.Margin = new Thickness(0, 0, 6, 0);
                commandCell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(commandCell, 1);
                grid.Children.Add(commandCell);
            }
            if (actionsCell != null)
            {
                // 操作列按钮组靠左，和表头“操作”标题同一个起点。
                actionsCell.HorizontalAlignment = HorizontalAlignment.Left;
                actionsCell.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(actionsCell, 2);
                grid.Children.Add(actionsCell);
            }

            row.Child = grid;
            return row;
        }

        /// <summary>可编辑键位：拆成独立小键帽，中间用“+”连接；双击键帽进入修改。</summary>
        private FrameworkElement BuildBindingChips(
            ShortcutCatalog.Shortcut shortcut,
            int bindingIndex,
            string keys)
        {
            return BuildKeyChips(
                keys,
                delegate { BeginEditShortcutBinding(shortcut, bindingIndex); },
                "ShortcutCap_" + shortcut.Id + "_" + bindingIndex,
                SettingsTextKeys.ShortcutCapAccessibleFormat,
                keys);
        }

        /// <summary>固定说明的键位：只读键帽，仍然按“+”拆开显示；右键仍可复制键位文字。</summary>
        private FrameworkElement BuildStaticBindingChips(ShortcutCatalog.Shortcut shortcut)
        {
            string keys = string.IsNullOrWhiteSpace(shortcut.KeysKey)
                ? shortcut.Keys
                : AppText.T(shortcut.KeysKey);
            return BuildKeyChips(keys, null, null, null, null);
        }

        /// <summary>
        /// 键位键帽的唯一装配处：按“ + ”拆开，中间插一个小号“+”。
        /// <paramref name="edit"/> 非空时双击键帽进入编辑；所有键帽右键都能复制整条键位文字。
        /// </summary>
        private FrameworkElement BuildKeyChips(
            string keys,
            Action edit,
            string automationId,
            string accessibleKey,
            string accessibleArgument)
        {
            StackPanel host = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            string[] parts = SplitShortcutKeys(keys);
            if (parts.Length == 0)
            {
                Button empty = BuildShortcutKeyChip(AppText.T(SettingsTextKeys.ShortcutUnset), false);
                AttachShortcutKeyInteractions(empty, edit, keys);
                if (!string.IsNullOrWhiteSpace(automationId))
                {
                    AutomationProperties.SetAutomationId(empty, automationId);
                }
                host.Children.Add(empty);
                return host;
            }
            for (int index = 0; index < parts.Length; index++)
            {
                if (index > 0)
                {
                    host.Children.Add(BuildShortcutKeyPlus());
                }
                Button chip = BuildShortcutKeyChip(parts[index], false);
                AttachShortcutKeyInteractions(chip, edit, keys);
                if (index == 0 && !string.IsNullOrWhiteSpace(automationId))
                {
                    AutomationProperties.SetAutomationId(chip, automationId);
                    if (!string.IsNullOrWhiteSpace(accessibleKey))
                    {
                        AutomationProperties.SetName(
                            chip,
                            AppText.F(accessibleKey, accessibleArgument));
                    }
                }
                host.Children.Add(chip);
            }
            return host;
        }

        /// <summary>
        /// 键帽交互：双击修改、右键复制整条键位；按住左键约 0.5 秒也直接复制（不弹菜单）。
        /// 长按复制后本次不再触发双击，避免“复制完又打开修改弹窗”。
        /// </summary>
        private void AttachShortcutKeyInteractions(Button chip, Action edit, string keys)
        {
            if (chip == null)
            {
                return;
            }

            bool holding = false;
            DateTime suppressDoubleClickUntil = DateTime.MinValue;
            Point pressPoint = new Point();
            DispatcherTimer holdTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ShortcutChipHoldCopyMilliseconds)
            };
            Action stopHold = delegate
            {
                holding = false;
                holdTimer.Stop();
            };
            holdTimer.Tick += delegate
            {
                holdTimer.Stop();
                if (!holding)
                {
                    return;
                }
                holding = false;
                // 复制后压住双击一段时间：长按结束时的那次抬手+点击不能当成双击。
                suppressDoubleClickUntil = DateTime.UtcNow.AddMilliseconds(
                    ShortcutChipHoldCopyDoubleClickGuardMilliseconds);
                CopyShortcutKeys(keys);
            };

            // 用 Preview 事件：ButtonBase 会在自己的类处理里把 MouseLeftButtonDown 标记为已处理，
            // 普通 += 收不到；预览事件先于按钮的类处理，长按计时才能启动。
            chip.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e == null || e.ClickCount > 1)
                {
                    // 双击的第二次按下不再启动长按计时，避免复制盖过修改。
                    stopHold();
                    return;
                }
                holding = true;
                pressPoint = e.GetPosition(chip);
                holdTimer.Stop();
                holdTimer.Start();
            };
            chip.PreviewMouseMove += delegate(object sender, MouseEventArgs e)
            {
                if (!holding)
                {
                    return;
                }
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    stopHold();
                    return;
                }
                Point current = e.GetPosition(chip);
                if (Math.Abs(current.X - pressPoint.X) > ShortcutChipHoldCopyMoveTolerance
                    || Math.Abs(current.Y - pressPoint.Y) > ShortcutChipHoldCopyMoveTolerance)
                {
                    stopHold();
                }
            };
            chip.PreviewMouseLeftButtonUp += delegate { stopHold(); };
            chip.LostMouseCapture += delegate { stopHold(); };
            chip.Unloaded += delegate { holdTimer.Stop(); };

            if (edit != null)
            {
                chip.MouseDoubleClick += delegate
                {
                    if (DateTime.UtcNow < suppressDoubleClickUntil)
                    {
                        return;
                    }
                    edit();
                };
                AccessibleToolTip.AttachDescription(
                    chip,
                    TooltipDefinition.Plain(SettingsTextKeys.ShortcutDoubleClickHint, null));
            }
            ContextMenus.Attach(chip, BuildShortcutCopyMenu(keys));
        }

        private ContextMenu BuildShortcutCopyMenu(string keys)
        {
            return ContextMenus.Create(ContextMenuItem.Item(
                AppText.T(SettingsTextKeys.ShortcutCopyMenu),
                delegate { CopyShortcutKeys(keys); }));
        }

        private void CopyShortcutKeys(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys))
            {
                return;
            }
            string error;
            if (!ClipboardText.TrySetText(keys, out error))
            {
                ShowShortcutPageError(string.IsNullOrWhiteSpace(error)
                    ? AppText.T(SettingsTextKeys.ShortcutCopyFailed)
                    : error);
                return;
            }
            ClearShortcutPageError();
            if (statusText != null)
            {
                statusText.Text = AppText.F(SettingsTextKeys.ShortcutCopiedFormat, keys);
            }
        }

        /// <summary>快捷键页里的“拖动”行：显示当前前缀 + 拖动，双击/铅笔编辑，恢复回到 Shift + 左键。</summary>
        private UIElement BuildDragGestureRow(ShortcutCatalog.Shortcut shortcut)
        {
            string keys = DragGestureSettings.DisplayKeys(
                AppText.T(SettingsTextKeys.DragGestureWord));
            FrameworkElement bindingCell = BuildKeyChips(
                keys,
                delegate { BeginEditDragGesture(); },
                "ShortcutCap_" + ShortcutCatalog.DragGestureId,
                SettingsTextKeys.DragGestureAccessibleFormat,
                keys);
            Grid commandCell = BuildShortcutSummaryLine(
                shortcut,
                string.IsNullOrWhiteSpace(shortcut.SummaryKey)
                    ? shortcut.TitleKey
                    : shortcut.SummaryKey);

            ActionBar actionBar = ActionBar.Create(ActionBarKind.DragGestureRow);
            Button disabledAdd = AddDisabledAction(actionBar, ActionSlot.Add);
            if (disabledAdd != null)
            {
                AutomationProperties.SetAutomationId(disabledAdd, "DragGestureDisabledAdd");
            }
            Button edit = actionBar.Add(
                ActionSlot.Edit,
                ActionButtons.EditPencil(TooltipDefinition.Named(
                    SettingsTextKeys.DragGestureEditTipName,
                    SettingsTextKeys.DragGestureEditTipTitle,
                    SettingsTextKeys.DragGestureEditTipDescription,
                    null)));
            edit.Click += delegate { BeginEditDragGesture(); };
            AutomationProperties.SetAutomationId(edit, "DragGestureEdit");
            Button restore = actionBar.Add(
                ActionSlot.Restore,
                ActionButtons.Restore(TooltipDefinition.Named(
                    SettingsTextKeys.DragGestureRestoreTipName,
                    SettingsTextKeys.DragGestureRestoreTipTitle,
                    SettingsTextKeys.DragGestureRestoreTipDescription,
                    null)));
            // 已经默认 Shift + 左键时没有可恢复的内容：灰色、不可点、也不弹空白提示。
            if (DragGestureSettings.IsDefault)
            {
                restore.IsEnabled = false;
                ClearButtonTooltip(restore);
            }
            else
            {
                restore.Click += delegate { ResetDragGesture(); };
            }
            AutomationProperties.SetAutomationId(restore, "DragGestureRestore");
            Button disabledDelete = AddDisabledAction(actionBar, ActionSlot.Delete);
            if (disabledDelete != null)
            {
                AutomationProperties.SetAutomationId(disabledDelete, "DragGestureDisabledDelete");
            }

            Border row = BuildShortcutTableRow(bindingCell, commandCell, actionBar.Build());
            AutomationProperties.SetAutomationId(
                row, "ShortcutRow_" + ShortcutCatalog.DragGestureId);
            shortcutSettingsRowHosts[ShortcutCatalog.DragGestureId] = row;
            return row;
        }

        private static string[] SplitShortcutKeys(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys))
            {
                return new string[0];
            }
            return keys.Split(new[] { " + " }, StringSplitOptions.None);
        }

        private static TextBlock BuildShortcutKeyPlus()
        {
            return new TextBlock
            {
                Text = "+",
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static Button BuildShortcutKeyChip(string text, bool muted)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                FontFamily = AppTheme.UiFont,
                FontSize = AppFonts.Small,
                FontWeight = FontWeights.SemiBold,
                Foreground = muted ? AppTheme.TextSecondary : AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button chip = new Button
            {
                MinWidth = 24,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0),
                FontSize = AppFonts.Small,
                FontWeight = FontWeights.SemiBold,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = AppTheme.PanelSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                Content = label
            };
            return chip;
        }

        private FrameworkElement BuildEmptyBindingCell(ShortcutCatalog.Shortcut shortcut)
        {
            StackPanel host = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Button empty = new Button
            {
                MinWidth = 92,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(9, 2, 9, 2),
                Background = AppTheme.PanelSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = AppText.T(SettingsTextKeys.ShortcutUnset),
                    FontFamily = AppTheme.UiFont,
                    FontSize = AppFonts.Small,
                    Foreground = AppTheme.TextSecondary,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AccessibleToolTip.AttachDescription(
                empty,
                TooltipDefinition.Plain(SettingsTextKeys.ShortcutEmptyHint, null));
            AutomationProperties.SetAutomationId(empty, "ShortcutEmpty_" + shortcut.Id);
            empty.Click += delegate { BeginAddShortcutBinding(shortcut); };
            host.Children.Add(empty);

            TextBlock hint = new TextBlock
            {
                Text = AppText.T(SettingsTextKeys.ShortcutEmptyHint),
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            host.Children.Add(hint);
            return host;
        }

        private FrameworkElement BuildBindingActionsCell(
            ShortcutCatalog.Shortcut shortcut,
            int bindingIndex,
            bool includeCommandActions)
        {
            ActionBar actionBar = includeCommandActions
                ? ActionBar.Create(ActionBarKind.ShortcutBindingWithCommandRow)
                : ActionBar.Create(ActionBarKind.ShortcutBindingRow);
            if (includeCommandActions)
            {
                AddCommandActions(actionBar, shortcut);
            }
            else
            {
                // 后续绑定行没有命令级的加号/恢复，用灰色禁用位占住前两格。
                AddDisabledAction(actionBar, ActionSlot.Add);
                AddDisabledAction(actionBar, ActionSlot.Restore);
            }

            Button edit = actionBar.Add(
                ActionSlot.Edit,
                ActionButtons.EditPencil(TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutEditTipName,
                    SettingsTextKeys.ShortcutEditTipTitle,
                    SettingsTextKeys.ShortcutEditTipDescription,
                    null)));
            edit.Click += delegate { BeginEditShortcutBinding(shortcut, bindingIndex); };
            SetShortcutActionId(edit, "ShortcutEdit_" + shortcut.Id + "_", bindingIndex);

            Button delete = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutDeleteTipName,
                    SettingsTextKeys.ShortcutDeleteTipTitle,
                    SettingsTextKeys.ShortcutDeleteTipDescription,
                    null)));
            delete.Click += delegate { DeleteShortcutBinding(shortcut, bindingIndex); };
            SetShortcutActionId(delete, "ShortcutDelete_" + shortcut.Id + "_", bindingIndex);

            return actionBar.Build();
        }

        private FrameworkElement BuildCommandActionsCell(ShortcutCatalog.Shortcut shortcut)
        {
            ActionBar actionBar = ActionBar.Create(ActionBarKind.ShortcutCommandRow);
            AddCommandActions(actionBar, shortcut);
            // 未设置绑定时没有可编辑/可删除的具体绑定，后两格补灰色禁用位。
            AddDisabledAction(actionBar, ActionSlot.Edit);
            AddDisabledAction(actionBar, ActionSlot.Delete);
            return actionBar.Build();
        }

        /// <summary>
        /// 命令级动作：加号新增一条绑定，恢复把这条命令回默认。
        /// 只在第一条绑定行或“未设置”行出现一次。
        /// </summary>
        private void AddCommandActions(ActionBar actionBar, ShortcutCatalog.Shortcut shortcut)
        {
            if (actionBar == null || shortcut == null)
            {
                return;
            }
            Button add = actionBar.Add(
                ActionSlot.Add,
                ActionButtons.Add(TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutAddTipName,
                    SettingsTextKeys.ShortcutAddTipTitle,
                    SettingsTextKeys.ShortcutAddTipDescription,
                    null)));
            add.Click += delegate { BeginAddShortcutBinding(shortcut); };
            SetShortcutActionId(add, "ShortcutAdd_", shortcut.Id);

            Button restore = actionBar.Add(
                ActionSlot.Restore,
                ActionButtons.Restore(TooltipDefinition.Named(
                    SettingsTextKeys.ShortcutRestoreTipName,
                    SettingsTextKeys.ShortcutRestoreTipTitle,
                    SettingsTextKeys.ShortcutRestoreTipDescription,
                    null)));
            // 当前绑定已经和默认一致时没有可恢复的内容：灰色、不可点、也不弹空白提示。
            if (ShortcutCatalog.IsAtDefaultBindings(shortcut))
            {
                restore.IsEnabled = false;
                ClearButtonTooltip(restore);
            }
            else
            {
                restore.Click += delegate { ResetShortcutCommand(shortcut); };
            }
            SetShortcutActionId(restore, "ShortcutRestore_", shortcut.Id);
        }

        /// <summary>
        /// 快捷键表格固定四格：这一行没有的动作放一个同图标的灰色禁用按钮。
        /// 不绑 Click；默认不设 AutomationId，需要探针定位时由调用方补一个唯一 ID。
        /// 这样加号/恢复/铅笔/垃圾桶在所有行都对齐，缺功能的按钮无法点击。
        /// </summary>
        private static Button AddDisabledAction(ActionBar actionBar, ActionSlot slot)
        {
            if (actionBar == null)
            {
                return null;
            }
            Button button;
            switch (slot)
            {
                case ActionSlot.Add:
                    button = ActionButtons.Add((TooltipDefinition)null);
                    break;
                case ActionSlot.Restore:
                    button = ActionButtons.Restore((TooltipDefinition)null);
                    break;
                case ActionSlot.Edit:
                    button = ActionButtons.EditPencil(null);
                    break;
                case ActionSlot.Delete:
                    button = ActionButtons.Delete((TooltipDefinition)null);
                    break;
                default:
                    return null;
            }
            button.IsEnabled = false;
            ClearButtonTooltip(button);
            return actionBar.Add(slot, button);
        }

        private Grid BuildShortcutSummaryLine(ShortcutCatalog.Shortcut shortcut, string summaryKey)
        {
            // 问号里放完整说明；适用范围也写进说明正文，不再单独占右半部分。
            string detailKey = shortcut.DescriptionKey;
            Grid line = HelpHint.BuildHintLine(HelpDefinition.Create(
                shortcut.TitleKey,
                summaryKey,
                detailKey));
            if (line != null)
            {
                // 填满指令列，HelpHint 会按实际宽度换行；不要用 Left，否则文字会溢出列被硬裁。
                line.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
            return line;
        }

        /// <summary>公共“搜索方案”按钮：点开公共搜索弹窗，按指令名/键位/说明找快捷键。</summary>
        private void OpenShortcutSearch()
        {
            ShowSearchDialog(
                AppText.T(SettingsTextKeys.ShortcutSearchTitle),
                AppText.T(SettingsTextKeys.ShortcutSearchDescription),
                delegate(string keyword, bool includeTime) { return SearchShortcutCommands(keyword); },
                delegate(SearchHit hit)
                {
                    if (hit != null)
                    {
                        HighlightShortcutCommand(hit.Tag as string);
                    }
                },
                AppText.T(SettingsTextKeys.ShortcutSearchEmpty),
                false,
                SettingsTextKeys.ShortcutSearchAction);
        }

        private List<SearchHit> SearchShortcutCommands(string keyword)
        {
            List<SearchHit> hits = new List<SearchHit>();
            IList<ShortcutCatalog.Shortcut> shortcuts = ShortcutCatalog.All();
            for (int index = 0; index < shortcuts.Count; index++)
            {
                ShortcutCatalog.Shortcut shortcut = shortcuts[index];
                if (shortcut == null)
                {
                    continue;
                }
                bool dragGesture = string.Equals(
                    shortcut.Id, ShortcutCatalog.DragGestureId, StringComparison.Ordinal);
                if (!shortcut.IsEditable && !dragGesture)
                {
                    continue;
                }
                string bindingKeys = dragGesture
                    ? DragGestureSettings.DisplayKeys(AppText.T(SettingsTextKeys.DragGestureWord))
                    : JoinShortcutBindingKeys(shortcut.Bindings);
                SearchTarget target = new SearchTarget
                {
                    Name = shortcut.Title,
                    // 不搜完整说明：复制说明里提到“粘贴”，会把复制命令也搜出来，结果不精确。
                    // 指令名 + 一句话概括 + 范围 + 键位已经够找快捷键了。
                    ExtraText = new[]
                    {
                        shortcut.Summary,
                        shortcut.Scope,
                        bindingKeys
                    },
                    TimeText = string.Empty
                };
                if (!SearchMatcher.Matches(target, keyword, false))
                {
                    continue;
                }

                string detail = (string.IsNullOrWhiteSpace(bindingKeys)
                        ? AppText.T(SettingsTextKeys.ShortcutUnset)
                        : bindingKeys)
                    + " · " + shortcut.Scope;
                SearchHit hit = new SearchHit
                {
                    Title = shortcut.Title,
                    Detail = detail,
                    Tag = shortcut.Id
                };
                List<SearchTextRange> nameRanges = new List<SearchTextRange>();
                List<SearchTextRange> timeRanges = new List<SearchTextRange>();
                SearchMatcher.Highlight(target, keyword, false, nameRanges, timeRanges);
                hit.TitleSegments = BuildHighlightSegments(shortcut.Title, nameRanges, 0);
                // 结果行要显示第二行键位，必须给一个非空分段；键位本身不额外高亮。
                hit.DetailSegments = BuildHighlightSegments(
                    detail, new List<SearchTextRange>(), 0);
                hits.Add(hit);
            }
            return hits;
        }

        private static string JoinShortcutBindingKeys(IList<ShortcutBinding> bindings)
        {
            List<string> keys = new List<string>();
            if (bindings != null)
            {
                for (int index = 0; index < bindings.Count; index++)
                {
                    ShortcutBinding binding = bindings[index];
                    if (binding != null && !string.IsNullOrWhiteSpace(binding.Keys))
                    {
                        keys.Add(binding.Keys);
                    }
                }
            }
            return string.Join(" / ", keys.ToArray());
        }

        private static int FindShortcutRowIndex(string commandId)
        {
            IList<ShortcutCatalog.Shortcut> shortcuts = ShortcutCatalog.All();
            for (int index = 0; index < shortcuts.Count; index++)
            {
                ShortcutCatalog.Shortcut shortcut = shortcuts[index];
                if (shortcut != null
                    && !string.IsNullOrEmpty(shortcut.Id)
                    && string.Equals(shortcut.Id, commandId, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>搜索结果点中后：先跳到对应页，再滚到这一行并短暂高亮。</summary>
        private void HighlightShortcutCommand(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return;
            }
            int itemIndex = FindShortcutRowIndex(commandId);
            if (itemIndex < 0)
            {
                return;
            }
            int targetPage = ShortcutSettingsPager.ComputePageForIndex(itemIndex, shortcutPageSize);
            if (targetPage != shortcutCurrentPage)
            {
                shortcutCurrentPage = targetPage;
                RefreshShortcutSettingsSection();
            }
            Border row;
            if (!shortcutSettingsRowHosts.TryGetValue(commandId, out row) || row == null)
            {
                return;
            }
            StopShortcutHighlight();
            row.BringIntoView();
            shortcutHighlightRow = row;
            shortcutHighlightBaseBrush = row.Background;
            row.Background = AppTheme.ActiveSurface;
            shortcutHighlightTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1600)
            };
            shortcutHighlightTimer.Tick += delegate { StopShortcutHighlight(); };
            shortcutHighlightTimer.Start();
        }

        private void StopShortcutHighlight()
        {
            if (shortcutHighlightTimer != null)
            {
                shortcutHighlightTimer.Stop();
                shortcutHighlightTimer = null;
            }
            if (shortcutHighlightRow != null)
            {
                shortcutHighlightRow.Background = shortcutHighlightBaseBrush;
                shortcutHighlightRow = null;
                shortcutHighlightBaseBrush = null;
            }
        }

        private void ShowShortcutPageError(string error)
        {
            if (shortcutSettingsError == null)
            {
                return;
            }
            shortcutSettingsError.Text = error ?? string.Empty;
            shortcutSettingsError.Visibility = string.IsNullOrWhiteSpace(error)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ClearShortcutPageError()
        {
            if (shortcutSettingsError == null)
            {
                return;
            }
            shortcutSettingsError.Text = string.Empty;
            shortcutSettingsError.Visibility = Visibility.Collapsed;
        }

        private static void SetShortcutActionId(Button button, string prefix, string shortcutId)
        {
            if (button != null && !string.IsNullOrWhiteSpace(shortcutId))
            {
                AutomationProperties.SetAutomationId(button, prefix + shortcutId);
            }
        }

        private static void SetShortcutActionId(Button button, string prefix, int bindingIndex)
        {
            if (button != null)
            {
                AutomationProperties.SetAutomationId(button, prefix + bindingIndex);
            }
        }
    }
}
