using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using Microsoft.Win32;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 图标布局编辑器（弹出页）。
    ///
    /// 【2026-10-20 用户要求】“编辑后，布局要和截图的小一致，图标也是（就是等比例缩放）”。
    /// 所以这里不再把截图按格子裁成一块块壁纸方块，而是：
    ///   · 画布 = 桌面的真实像素坐标（virtualBounds），桌面上每个格子的位置就是截图里的位置；
    ///   · 背景 = 当前壁纸（没有壁纸时用截图模糊版），不放带图标的截图，避免和编辑后的图标重影；
    ///   · 图标 = 从 Windows 取透明背景的大图标，按网格宽度等比缩放，文字也按同一比例缩放；
    ///   · 默认“适应窗口”，一打开就能看到整张桌面，和卡片截图是同一个比例。
    /// </summary>
    internal sealed partial class DesktopLayoutEditorWindow : Window
    {
        private readonly ApplicationController controller;
        private readonly DesktopShellSnapshot snapshot;
        private readonly DesktopTopologyLayout layout;
        private readonly Canvas desktopCanvas;
        private readonly Border desktopFrame;
        private readonly TextBlock zoomText;
        private readonly TextBlock statusText;
        private TextBlock layoutSummaryText;
        private string layoutSummaryBase;
        private Button scanDeletedButton;
        private Border scanToast;
        private TextBlock scanToastText;
        private DispatcherTimer scanToastTimer;
        private DispatcherTimer missingScanHighlightTimer;
        private bool missingScanHighlightActive;
        private bool scanningDeleted;
        private bool resizeFitPending;
        private readonly HashSet<string> scannedMissingIdentities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ScrollViewer scroll;
        private readonly Grid zoomHost;
        private readonly ScaleTransform canvasScale;
        private readonly string screenshotPath;
        // 编辑页生命周期状态（保存中/已关闭/有未保存改动等）集中在这里。
        private readonly DesktopLayoutEditorState state = new DesktopLayoutEditorState();
        private readonly HashSet<string> newIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<DesktopIconPlacement> unplacedNewItems =
            new HashSet<DesktopIconPlacement>();
        private readonly DesktopLayoutIconImageCache imageCache =
            new DesktopLayoutIconImageCache();
        private readonly Dictionary<DesktopIconPlacement, Border> iconSkeletons =
            new Dictionary<DesktopIconPlacement, Border>();
        private readonly Dictionary<DesktopIconPlacement, Border> tileByItem =
            new Dictionary<DesktopIconPlacement, Border>();
        private readonly List<TrayEntry> trayEntries = new List<TrayEntry>();
        private readonly List<DesktopIconPositionsState> undoStates = new List<DesktopIconPositionsState>();
        private readonly List<DesktopIconPositionsState> redoStates = new List<DesktopIconPositionsState>();
        private readonly DesktopRect virtualBounds;
        private readonly double canvasWidth;
        private readonly double canvasHeight;
        private readonly double gridWidth;
        private readonly double gridHeight;
        private readonly double gridOriginX;
        private readonly double gridOriginY;
        private readonly double iconSize;
        private readonly double labelFontSize;
        private readonly double iconTop;
        private double dpiScale = 1.0;
        private double zoom = 1.0;
        private bool fitMode = true;
        private bool trayDragging;
        private Point trayDragStart;
        private DesktopIconPlacement trayDragItem;
        private Canvas dragOverlay;
        private Border dragGhost;
        // 编辑页所有自由拖拽物体共用的移动边界；标签和浮卡各持一个实例，
        // 边界矩形在每次 MoveTo 时传入（标签/卡片=编辑页，画布图标=可见预桌面）。
        private EditorDragBounds newIconTagDragBounds;
        private EditorDragBounds dragGhostBounds;
        // 拖动开始缓存：边界/页面原点/轴心。拖动期间不再每帧 TransformToAncestor。
        private Rect newIconTagDragPageBounds;
        private Point newIconTagDragPageOrigin;
        private bool newIconTagDragPageOriginValid;
        private bool newIconTagDragAxisValid;
        private double newIconTagDragAxisX;
        private double newIconTagDragAxisY;
        // MouseMove 与定时器合并成“每帧最多一次”。
        private bool newIconTagDragUpdateQueued;
        private bool newIconTagCaptureRestoreQueued;
        private int newIconTagLeftUpTicks;
        // 捕获丢失后 WPF 坐标可能停住；此时才切物理光标。
        private bool dragPointerPhysicalFallback;
        private TextBox newIconFilter;
        private Canvas newIconDock;
        private Border newIconTag;
        private Grid newIconTagHost;
        private Border newIconDockContent;
        private Image newIconDirectionGhost;
        private Image newIconDirectionNewGhost;
        private TranslateTransform newIconDirectionGhostShift;
        private RectangleGeometry newIconDirectionGhostClip;
        private TranslateTransform newIconDirectionContentShift;
        private RectangleGeometry newIconDirectionContentClip;
        private double newIconDirectionNewGhostOffsetX;
        private double newIconDirectionNewGhostOffsetY;
        private double newIconDirectionGhostOffsetX;
        private double newIconDirectionGhostOffsetY;
        private bool newIconDirectionGhostIsComposite;
        private bool newIconDirectionTransitionActive;
        private bool newIconDirectionTransitionPending;
        private bool newIconDirectionTransitionCompact;
        private bool newIconDirectionTransitionRetarget;
        private int newIconDirectionTransitionVersion;
        private NewIconDockDirection newIconDirectionTransitionFrom;
        private TextBlock newIconChevron;
        private Border newIconCountBadge;
        private TextBlock newIconCountText;
        private StackPanel newIconCardsPanel;
        private ScrollViewer newIconBelt;
        private Button newIconLeftButton;
        private Button newIconRightButton;
        private Slider newIconSlider;
        private TextBlock newIconBeltHint;
        private bool verticalNewIconContent;
        private bool syncingBelt;
        private Grid pageOverlay;
        private TextBlock layoutTitleText;
        private StackPanel layoutTitleLine;
        private NewIconDockDirection newIconDockDirection = NewIconDockDirection.Down;
        private Canvas newIconBoundaryGuides;
        private Rectangle newIconBoundaryBar;
        private LinearGradientBrush newIconBoundaryBrush;
        private Border newIconBoundaryPill;
        private TextBlock newIconBoundaryPillIcon;
        private TextBlock newIconBoundaryPillText;
        private Grid desktopLoadingCover;
        private TextBlock desktopLoadingText;
        private NewIconBoundary activeNewIconBoundary;
        private string lastNewIconBoundaryStatus;
        // 隐藏十字轴的轴侧状态。第一次拿到有效页面坐标时按当前位置初始化；
        // 之后持续维护，不因单击或重新拖动而重置，避免在轴附近重新起拖时翻面。
        private bool newIconAxisSidesInitialized;
        private bool newIconAxisLeftOfCenter;
        private bool newIconAxisAboveCenter;
        private double newIconTagX;
        private double newIconTagY;
        private bool newIconDockPositionInitialized;
        private DispatcherTimer newIconHoldTimer;
        private DispatcherTimer newIconDragFollowTimer;
        private DispatcherTimer newIconDirectionTransitionTimer;
        private bool newIconPointerDown;
        private bool newIconLongPress;
        private bool newIconDragging;
        private Point newIconPressPoint;
        private DateTime newIconLastDragTimeUtc;
        private double newIconLastDragX;
        private double newIconLastDragY;
        private double newIconDragSpeed;
        // 超过这个速度仍然播 B，只把 110/170ms 压成 70/100ms。
        private const double FastNewIconDragSpeed = 900.0;
        private Grid canvasLayer;

        private enum NewIconDockDirection
        {
            Right,
            Left,
            Down,
            Up
        }

        private enum NewIconBoundary
        {
            None,
            Top,
            Bottom,
            Left,
            Right
        }

        private sealed class NewIconDockPanel : Border
        {
            protected override AutomationPeer OnCreateAutomationPeer()
            {
                return new NewIconDockPanelPeer(this);
            }
        }

        private sealed class NewIconDockPanelPeer : FrameworkElementAutomationPeer
        {
            public NewIconDockPanelPeer(FrameworkElement owner)
                : base(owner)
            {
            }

            protected override string GetClassNameCore()
            {
                return "NewIconDockPanel";
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Pane;
            }
        }

        private bool beltDragging;
        private Point beltDragStart;
        private double beltStartOffset;
        private bool canvasPanning;
        private Point canvasPanStart;
        private double canvasPanHorizontalOffset;
        private double canvasPanVerticalOffset;


        internal bool SavedLayout
        {
            get { return state.SavedLayout; }
        }

        internal DesktopTopologyLayout Layout
        {
            get { return layout; }
        }

        /// <summary>
        /// 取消按钮/Esc 都走这里：有未保存的拖动时先确认，别让一次误按直接丢掉调整。
        /// 真正的确认在 Closing 里统一处理，右上角 × 和 Alt+F4 也会经过同一道拦截。
        /// </summary>
        private void RequestClose()
        {
            if (state.Saving)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorSavingPleaseWait);
                return;
            }
            Close();
        }

        /// <summary>程序主动关闭（例如读取当前桌面失败）：绕过“未保存”确认。</summary>
        internal void CloseWithoutPrompt()
        {
            state.CloseWithoutPrompt = true;
            Close();
        }

        internal DesktopLayoutEditorWindow(
            Window owner,
            ApplicationController controller,
            DesktopShellSnapshot snapshot,
            DesktopTopologyLayout layout,
            int newIconCount,
            int missingIconCount,
            IList<string> missingIdentities,
            string screenshotPath)
        {
            FocusVisualStyle = null;
            this.controller = controller;
            this.snapshot = snapshot;
            this.layout = layout;
            missingIconCount = Math.Max(0, missingIconCount);
            if (missingIdentities != null)
            {
                for (int index = 0; index < missingIdentities.Count; index++)
                {
                    string identity = DesktopIdentity.Normalize(missingIdentities[index]);
                    if (!string.IsNullOrEmpty(identity))
                    {
                        scannedMissingIdentities.Add(identity);
                    }
                }
            }
            // 截图只作为“没有壁纸时”的模糊底图，不能在这里解码 6MB PNG。
            // 打开编辑页卡顿的主要来源之一就是首帧同步解这张图。
            this.screenshotPath = screenshotPath;

            DisplayTopology topology = snapshot == null ? null : snapshot.Topology;
            if (topology == null)
            {
                topology = new DisplayTopology();
            }
            this.virtualBounds = ResolveVirtualBounds(topology);
            this.canvasWidth = Math.Max(320.0, this.virtualBounds.Width);
            this.canvasHeight = Math.Max(240.0, this.virtualBounds.Height);
            int snapshotGridWidth = snapshot == null || snapshot.GridWidth <= 0 ? 75 : snapshot.GridWidth;
            int snapshotGridHeight = snapshot == null || snapshot.GridHeight <= 0 ? 75 : snapshot.GridHeight;
            this.gridWidth = Math.Max(24.0, snapshotGridWidth);
            this.gridHeight = Math.Max(24.0, snapshotGridHeight);
            // Windows 桌面网格不是从 (0,0) 开始的：常见原点是 (20,2)。
            // 只看“能被 112/147 整除”会把图标放到错误的网格线上，
            // 必须从当前图标位置统计出真正的网格原点。
            double originX;
            double originY;
            ResolveGridOrigin(snapshot, this.virtualBounds,
                this.gridWidth, this.gridHeight, out originX, out originY);
            this.gridOriginX = originX;
            this.gridOriginY = originY;
            this.iconSize = Math.Max(16.0, this.gridWidth * 0.70);
            this.labelFontSize = Math.Max(8.0, this.gridWidth * 0.18);
            this.iconTop = Math.Max(3.0, this.gridHeight * 0.055);

            // 新建的空布局（例如第一次点卡片上的“编辑图标布局”）没有基准，
            // 里面的图标全部算“已存在”，否则满屏都是绿色“新”标记，反而看不出哪些是新增的。
            bool hasSavedIcons = layout.Icons != null && layout.Icons.Count > 0;
            if (!hasSavedIcons)
            {
                newIconCount = 0;
            }
            // 切换显示器时为了不让新图标消失，coordinator 会把它们自动并进其他布局；
            // 这些身份虽然已经在 layout.Icons 里，但不是用户在这份布局里明确保存的，
            // 编辑页仍要放进“新图标”，绝不能直接铺到预桌面。
            HashSet<string> autoMergedIdentities = ReadAutoMergedIdentities(layout);
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (hasSavedIcons && item != null && !string.IsNullOrEmpty(item.Identity))
                {
                    bool isNew = true;
                    bool savedUnplaced = false;
                    for (int savedIndex = 0; savedIndex < layout.Icons.Count; savedIndex++)
                    {
                        DesktopIconPlacement saved = layout.Icons[savedIndex];
                        if (saved != null && DesktopIdentity.Equals(saved.Identity, item.Identity))
                        {
                            isNew = false;
                            savedUnplaced = IsUnplacedPlacement(saved);
                            break;
                        }
                    }
                    if (autoMergedIdentities.Contains(DesktopIdentity.Normalize(item.Identity)))
                    {
                        isNew = true;
                        // 自动并集时给的是“空位”坐标，不能拿来当用户保存的位置；
                        // 先放哨兵，保存时仍保持未放置。
                        item.X = -1000000;
                        item.Y = -1000000;
                    }
                    else if (savedUnplaced)
                    {
                        // 用户上次把图标拖进“新图标”、保存时留下的哨兵。
                        // 当前桌面上图标还在原处，但编辑页必须继续把它放回“新图标”，
                        // 不能再铺回预桌面。
                        isNew = true;
                        item.X = -1000000;
                        item.Y = -1000000;
                    }
                    if (isNew)
                    {
                        newIdentities.Add(item.Identity);
                    }
                }
            }
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item != null && newIdentities.Contains(item.Identity ?? string.Empty))
                {
                    unplacedNewItems.Add(item);
                }
            }

            LocalizedText.Title(
                this,
                DesktopLayoutTextKeys.EditorTitleFormat,
                layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback));
            // 不设置 Owner：Owner 窗口最小化/还原时会把编辑窗一起带着动，
            // 用户要求主窗和编辑窗的尺寸/最小化互相独立。
            this.WindowStartupLocation = WindowStartupLocation.Manual;
            // 编辑 4K 桌面的 68 个图标，窗口太小会导致“适应窗口”只有 30% 多，
            // 标签看不清。在不超出工作区的前提下尽量给大一点；用户仍可手动缩放窗口。
            Rect workArea = SystemParameters.WorkArea;
            this.Width = Math.Max(900.0, Math.Min(1600.0, workArea.Width - 80.0));
            this.Height = Math.Max(600.0, Math.Min(1000.0, workArea.Height - 80.0));
            this.MinWidth = 900;
            this.MinHeight = 600;
            double initialLeft = owner == null
                ? workArea.Left + (workArea.Width - this.Width) / 2.0
                : owner.Left + (owner.Width - this.Width) / 2.0;
            double initialTop = owner == null
                ? workArea.Top + (workArea.Height - this.Height) / 2.0
                : owner.Top + (owner.Height - this.Height) / 2.0;
            this.Left = Math.Max(workArea.Left, Math.Min(initialLeft,
                workArea.Right - this.Width));
            this.Top = Math.Max(workArea.Top, Math.Min(initialTop,
                workArea.Bottom - this.Height));
            // 和普通应用窗口一样出现在任务栏；最小化后可以正常从任务栏还原，
            // 也可以先切回主窗口继续操作（编辑页改成非模态 Show）。
            this.ShowInTaskbar = true;
            this.Background = AppTheme.WindowBackground;
            this.FontFamily = AppFonts.Ui;

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid { Margin = new Thickness(20, 16, 20, 10) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel();
            layoutTitleLine = new StackPanel { Orientation = Orientation.Horizontal };
            layoutTitleText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(layout.Name)
                    ? AppText.T(DesktopLayoutTextKeys.LayoutFallback)
                    : layout.Name,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            layoutTitleLine.Children.Add(layoutTitleText);
            TextBlock editingBadge = new TextBlock
            {
                FontSize = 11,
                Foreground = AppTheme.Accent
            };
            LocalizedText.Text(editingBadge, DesktopLayoutTextKeys.EditorEditingBadge);
            layoutTitleLine.Children.Add(new Border
            {
                Background = AppTheme.ActiveSurface,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(9, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = editingBadge
            });
            heading.Children.Add(layoutTitleLine);

            layoutSummaryBase = DesktopLayoutFolderStore.DescribeDisplay(layout)
                + AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryIconsFormat,
                    snapshot.Icons.Count);
            if (missingIconCount > 0)
            {
                layoutSummaryBase += AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryMissingFormat,
                    missingIconCount);
            }
            string summary = layoutSummaryBase;
            if (newIconCount > 0)
            {
                summary += AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryNewFormat,
                    newIconCount);
            }
            layoutSummaryText = new TextBlock
            {
                Text = summary,
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0)
            };
            heading.Children.Add(layoutSummaryText);
            heading.Children.Add(HelpHint.BuildHintLine(DesktopLayoutToolTips.EditorHelp));
            header.Children.Add(heading);

            StackPanel zoomPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(18, 0, 0, 0)
            };
            // 放在“缩小”左边；不再占用标题下方的独立一行。
            scanDeletedButton = UiFactory.CommandButtonKey(
                "\uE721", DesktopLayoutTextKeys.EditorScanDeleted, false);
            scanDeletedButton.Margin = new Thickness(0, 0, 12, 0);
            scanDeletedButton.Click += delegate { ScanDeletedIconsAsync(); };
            AccessibleToolTip.Attach(scanDeletedButton, DesktopLayoutToolTips.ScanDeleted);
            zoomPanel.Children.Add(scanDeletedButton);
            Button zoomOut = UiFactory.CommandButtonKey(
                "\uE71F", DesktopLayoutTextKeys.EditorZoomOut, false);
            zoomText = new TextBlock
            {
                Text = "100%",
                Width = 52,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = AppTheme.TextSecondary,
                FontSize = 12
            };
            Button zoomIn = UiFactory.CommandButtonKey(
                "\uE8A3", DesktopLayoutTextKeys.EditorZoomIn, false);
            Button fit = UiFactory.CommandButtonKey(
                "\uE9A6", DesktopLayoutTextKeys.EditorFitWindow, false);
            AccessibleToolTip.Attach(zoomOut, DesktopLayoutToolTips.ZoomOut);
            AccessibleToolTip.Attach(zoomIn, DesktopLayoutToolTips.ZoomIn);
            AccessibleToolTip.Attach(fit, DesktopLayoutToolTips.FitWindow);
            zoomOut.Click += delegate
            {
                fitMode = false;
                SetZoom(zoom / 1.25, true);
            };
            zoomIn.Click += delegate
            {
                fitMode = false;
                // 以视口左上角为锚点，而不是桌面中心：桌面中心通常是大片壁纸，
                // 从“适应窗口”放大后如果锚在中心，第一眼会看不到图标。
                SetZoom(zoom * 1.25, true);
            };
            fit.Click += delegate { FitToWindow(); };
            zoomOut.Margin = new Thickness(0, 0, 4, 0);
            zoomIn.Margin = new Thickness(6, 0, 4, 0);
            fit.Margin = new Thickness(6, 0, 0, 0);
            zoomText.Margin = new Thickness(6, 0, 0, 0);
            zoomPanel.Children.Add(zoomOut);
            zoomPanel.Children.Add(zoomText);
            zoomPanel.Children.Add(zoomIn);
            zoomPanel.Children.Add(fit);
            Grid.SetColumn(zoomPanel, 1);
            header.Children.Add(zoomPanel);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                PanningMode = PanningMode.Both,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null
            };
            canvasScale = new ScaleTransform(1.0, 1.0);
            desktopCanvas = new Canvas
            {
                Width = this.canvasWidth,
                Height = this.canvasHeight,
                Background = EditorPalette.CanvasBackground,
                ClipToBounds = true,
                SnapsToDevicePixels = true,
                // 保留键盘焦点（Ctrl+Z/回车要发给编辑页），但把焦点虚线框关掉；
                // 用户之前看到灰色区域边缘的虚线就是 WPF 的 FocusVisualStyle。
                Focusable = true,
                FocusVisualStyle = null
            };
            LocalizedText.AccessibleName(
                desktopCanvas, DesktopLayoutTextKeys.EditorCanvasAutomation);
            desktopFrame = new Border
            {
                // 去掉旧版深色描边和 6px 黑边：它们就是预桌面左上角那两条
                // 相交黑线的来源。现在画布直接贴边，外面由灰色画布区留白。
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                Margin = new Thickness(24),
                Child = desktopCanvas,
                // 缩放用 RenderTransform，而不是 LayoutTransform：
                // LayoutTransform 每次滚轮都会让 4K 画布和几十个图标重新 Measure/Arrange，
                // 滚动条和居中位置在中间帧跳一下，看起来就是闪烁。
                // RenderTransform 只重绘，不重排；外层 zoomHost 负责给 ScrollViewer
                // 一个“放大后的尺寸”，所以滚动条/滚动范围仍然正确。
                RenderTransform = canvasScale,
                RenderTransformOrigin = new Point(0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            AddBackground(topology);
            // 自检/读屏用：把预桌面的范围暴露成一个有名字的文本块。
            // 不参与绘制和命中测试，真实鼠标探针靠它量图标浮卡有没有越出预桌面。
            TextBlock desktopBoundsProbe = new TextBlock
            {
                Width = canvasWidth,
                Height = canvasHeight,
                Opacity = 0.0,
                IsHitTestVisible = false,
                Focusable = false
            };
            LocalizedText.AccessibleName(
                desktopBoundsProbe, DesktopLayoutTextKeys.EditorCanvasBoundsAutomation);
            Panel.SetZIndex(desktopBoundsProbe, -10);
            desktopCanvas.Children.Add(desktopBoundsProbe);
            // 图标 tile 不在构造函数里一次性建完；ContentRendered 后每帧建一小批，
            // 窗口先出来，不再“点开编辑布局先卡一两秒”。
            zoomHost = new Grid
            {
                // ScrollViewer 的 HorizontalContentAlignment=Center 只会把宿主放在中间；
                // 宿主自己如果 Left/Top，就会把画面顶到左上角（实测会偏到左边）。
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            zoomHost.Children.Add(desktopFrame);
            scroll.Content = zoomHost;
            UpdateZoomHostSize(zoom / dpiScale);
            // Shift+拖空白画布＝平移视图。放大后要拖到屏幕外的位置时，
            // 不用先松手去拖滚动条；图1里的“有些地方移动不过去”就是这个问题。
            desktopCanvas.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    return;
                }
                canvasPanning = true;
                canvasPanStart = args.GetPosition(scroll);
                canvasPanHorizontalOffset = scroll.HorizontalOffset;
                canvasPanVerticalOffset = scroll.VerticalOffset;
                desktopCanvas.CaptureMouse();
                desktopCanvas.Cursor = Cursors.SizeAll;
                args.Handled = true;
            };
            desktopCanvas.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!canvasPanning || !desktopCanvas.IsMouseCaptured
                    || args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }
                Point point = args.GetPosition(scroll);
                scroll.ScrollToHorizontalOffset(
                    Math.Max(0.0, canvasPanHorizontalOffset - (point.X - canvasPanStart.X)));
                scroll.ScrollToVerticalOffset(
                    Math.Max(0.0, canvasPanVerticalOffset - (point.Y - canvasPanStart.Y)));
            };
            desktopCanvas.MouseLeftButtonUp += delegate
            {
                if (!canvasPanning)
                {
                    return;
                }
                canvasPanning = false;
                if (desktopCanvas.IsMouseCaptured)
                {
                    desktopCanvas.ReleaseMouseCapture();
                }
                desktopCanvas.Cursor = Cursors.Arrow;
            };
            desktopCanvas.LostMouseCapture += delegate
            {
                canvasPanning = false;
                desktopCanvas.Cursor = Cursors.Arrow;
            };
            // 鼠标滚轮直接缩放画布（按鼠标位置为锚点）。原来只支持 Ctrl+滚轮，
            // 用户要求“滚轮是放大缩小画布”，所以不再要求按 Ctrl。
            scroll.PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs args)
            {
                args.Handled = true;
                fitMode = false;
                SetZoomAt(
                    zoom * (args.Delta > 0 ? 1.1 : (1.0 / 1.1)),
                    args.GetPosition(scroll));
            };

            canvasLayer = new Grid();
            canvasLayer.Children.Add(scroll);

            Border canvasHost = new Border
            {
                Background = EditorPalette.Panel,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(20, 0, 20, 0),
                ClipToBounds = true,
                Child = canvasLayer
            };
            Grid.SetRow(canvasHost, 1);
            root.Children.Add(canvasHost);

            Grid footer = new Grid { Margin = new Thickness(20, 10, 20, 16) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusText = new TextBlock
            {
                Text = AppText.T(DesktopLayoutTextKeys.EditorGeneratingPreview),
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0)
            };
            footer.Children.Add(statusText);
            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
            Button cancel = UiFactory.CommandButtonKey(
                "\uE711", DesktopLayoutTextKeys.EditorCancel, false);
            Button finish = UiFactory.CommandButtonKey(
                "\uE74E", DesktopLayoutTextKeys.EditorFinish, true);
            finish.Margin = new Thickness(8, 0, 0, 0);
            finish.IsDefault = true;
            AccessibleToolTip.Attach(cancel, DesktopLayoutToolTips.CancelEdit);
            AccessibleToolTip.Attach(finish, DesktopLayoutToolTips.FinishSave);
            cancel.Click += delegate { RequestClose(); };
            finish.Click += async delegate { await FinishAsync(); };
            actions.Children.Add(cancel);
            actions.Children.Add(finish);
            Grid.SetColumn(actions, 1);
            footer.Children.Add(actions);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            // 「新图标」浮层覆盖整个编辑页，而不是只覆盖预桌面画布：
            // 用户可以把它拖到标题、画布、甚至底部状态栏附近。
            // 不裁剪：标签/展开面板拖动时要能跟到页面外；窗口本身仍会裁剪。
            pageOverlay = new Grid { ClipToBounds = false };
            Grid.SetRow(pageOverlay, 0);
            Grid.SetRowSpan(pageOverlay, 3);
            Panel.SetZIndex(pageOverlay, 60);
            root.Children.Add(pageOverlay);
            Canvas dock = BuildNewIconDock();
            if (dock != null)
            {
                pageOverlay.Children.Add(dock);
                Panel.SetZIndex(dock, 50);
            }

            dragOverlay = new Canvas
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Grid.SetRow(dragOverlay, 0);
            Grid.SetRowSpan(dragOverlay, 3);
            Panel.SetZIndex(dragOverlay, 5000);
            root.Children.Add(dragOverlay);

            // 扫描结果用居中浮层提示，不再在标题下面突然占一行；点空白立刻消失。
            scanToast = new Border
            {
                Background = EditorPalette.Sidebar,
                BorderBrush = EditorPalette.SidebarBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 12, 18, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 720,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = true
            };
            try
            {
                scanToast.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 3,
                    Opacity = 0.20,
                    Color = Colors.Black
                };
            }
            catch
            {
            }
            scanToastText = new TextBlock
            {
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            scanToast.Child = scanToastText;
            LocalizedText.AccessibleName(
                scanToast, DesktopLayoutTextKeys.EditorToastAutomation);
            Grid.SetRow(scanToast, 0);
            Grid.SetRowSpan(scanToast, 3);
            Panel.SetZIndex(scanToast, 6000);
            root.Children.Add(scanToast);

            // 编辑窗口先用已保存的布局打开，后台再读当前桌面；读取期间盖一层
            // 半透明加载层，既避免用户先改到一半又被补数据打断，也不让首开干等。
            desktopLoadingCover = new Grid
            {
                Background = EditorPalette.OverlayMask,
                IsHitTestVisible = true
            };
            Grid.SetRow(desktopLoadingCover, 0);
            Grid.SetRowSpan(desktopLoadingCover, 3);
            Panel.SetZIndex(desktopLoadingCover, 7000);
            Border loadingCard = new Border
            {
                Background = EditorPalette.OverlayWindow,
                BorderBrush = EditorPalette.OverlayAccentBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(26, 22, 26, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 330
            };
            try
            {
                loadingCard.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 4,
                    Opacity = 0.30,
                    Color = Colors.Black
                };
            }
            catch
            {
            }
            StackPanel loadingStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center
            };
            desktopLoadingText = new TextBlock
            {
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            LocalizedText.Text(
                desktopLoadingText, DesktopLayoutTextKeys.EditorLoadingTitle);
            loadingStack.Children.Add(desktopLoadingText);
            TextBlock loadingDetail = new TextBlock
            {
                FontSize = 11,
                Foreground = EditorPalette.MutedText,
                Margin = new Thickness(0, 6, 0, 14),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            LocalizedText.Text(
                loadingDetail, DesktopLayoutTextKeys.EditorLoadingDetail);
            loadingStack.Children.Add(loadingDetail);
            ProgressBar loadingBar = new ProgressBar
            {
                IsIndeterminate = true,
                Width = 220,
                Height = 3,
                Background = EditorPalette.TileHover,
                Foreground = AppTheme.Accent,
                BorderThickness = new Thickness(0)
            };
            loadingStack.Children.Add(loadingBar);
            loadingCard.Child = loadingStack;
            desktopLoadingCover.Children.Add(loadingCard);
            LocalizedText.AccessibleName(
                desktopLoadingCover, DesktopLayoutTextKeys.EditorLoadingTitle);
            root.Children.Add(desktopLoadingCover);
            root.PreviewMouseLeftButtonDown += delegate
            {
                HideScanToast();
                HideMissingIconHighlight();
            };
            scanToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.0) };
            scanToastTimer.Tick += delegate
            {
                scanToastTimer.Stop();
                HideScanToast();
            };

            Content = root;

            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (state.Saving)
                {
                    return;
                }
                if (!state.DesktopLoadCompleted)
                {
                    if (args.Key == Key.Escape)
                    {
                        RequestClose();
                        args.Handled = true;
                    }
                    else
                    {
                        args.Handled = true;
                    }
                    return;
                }
                bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                bool textEditing = IsTextEditing();
                if (control && args.Key == Key.Z && !textEditing)
                {
                    if (shift)
                    {
                        RedoLayoutEdit();
                    }
                    else
                    {
                        UndoLayoutEdit();
                    }
                    args.Handled = true;
                    return;
                }
                if (args.Key == Key.Enter && !textEditing
                    && !(Keyboard.FocusedElement is Button))
                {
                    // 编辑区按回车＝完成并保存；搜索框里按回车仍只用于检索。
                    args.Handled = true;
                    System.Threading.Tasks.Task ignored = FinishAsync();
                    return;
                }
                if (args.Key == Key.Escape)
                {
                    RequestClose();
                    args.Handled = true;
                }
            };
            SizeChanged += delegate
            {
                if (!IsLoaded)
                {
                    return;
                }
                if (fitMode && !resizeFitPending)
                {
                    // 窗口拖动/缩放会连续触发 SizeChanged；每个事件都重排 4K 画布
                    // 会让编辑页明显卡顿，合并到一次 Background 里做。
                    resizeFitPending = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        resizeFitPending = false;
                        if (IsLoaded && fitMode)
                        {
                            FitToWindow();
                        }
                    }), DispatcherPriority.Background);
                }
                if (newIconTag != null && newIconDockPositionInitialized)
                {
                    SettleNewIconDirectionTransitionForViewportChange();
                    ClampNewIconTagToPage();
                    // 窗口尺寸变化不主动换向；等下一次真正拖动时再按轴判断，
                    // 避免用户只是缩放窗口时面板在两侧之间跳。
                    ApplyNewIconDockLayout(false);
                }
            };
            ContentRendered += delegate
            {
                dpiScale = GetDpiScale();
                FitToWindow();
                PlaceNewIconDockBesideTitle();
                BeginIconTileBuild();
                // 非模态窗口刚显示时键盘焦点可能还留在主窗口的输入框里，
                // 这样 Ctrl+Z / 回车会发给主页面而不是编辑页。把焦点放到画布上。
                desktopCanvas.Focus();
            };
            Closing += delegate(object sender, CancelEventArgs args)
            {
                // 保存流程自己会 Close；程序主动收尾（读取桌面失败）也不该再问。
                if (state.SavedLayout || state.CloseWithoutPrompt)
                {
                    return;
                }
                if (state.Saving)
                {
                    // 保存是异步的，期间关闭会把“保存结果写回/关窗”顺序打乱。
                    args.Cancel = true;
                    statusText.Text = AppText.T(
                        DesktopLayoutTextKeys.EditorSavingPleaseWait);
                    return;
                }
                // 只有布局内容（图标位置/增删）真的变了才问；纯打开看一眼、或只挪了“新图标”面板位置，直接关。
                if (!state.LayoutDirty)
                {
                    return;
                }
                bool discard = WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                    this,
                    AppText.T(DesktopLayoutTextKeys.EditorCloseTitle),
                    AppText.T(DesktopLayoutTextKeys.EditorClosePrompt),
                    AppText.T(DesktopLayoutTextKeys.EditorCloseHelp));
                if (!discard)
                {
                    args.Cancel = true;
                }
            };
            Closed += delegate
            {
                state.Closed = true;
                CancelNewIconDirectionTransition();
                if (scanToastTimer != null)
                {
                    scanToastTimer.Stop();
                }
                if (newIconHoldTimer != null)
                {
                    newIconHoldTimer.Stop();
                }
                if (newIconDragFollowTimer != null)
                {
                    newIconDragFollowTimer.Stop();
                }
                if (newIconDirectionTransitionTimer != null)
                {
                    newIconDirectionTransitionTimer.Stop();
                }
                if (missingScanHighlightTimer != null)
                {
                    missingScanHighlightTimer.Stop();
                }
            };
        }

        private sealed class TrayEntry
        {
            internal TrayEntry(DesktopIconPlacement item, Border card)
            {
                Item = item;
                Card = card;
            }

            internal DesktopIconPlacement Item { get; private set; }

            internal Border Card { get; private set; }
        }

        private sealed class DesktopIconPositionsState
        {
            internal DesktopIconPlacement[] Items;
            internal int[] X;
            internal int[] Y;
            internal HashSet<DesktopIconPlacement> UnplacedNewItems;
        }

    }
}
