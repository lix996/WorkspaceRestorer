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
using WorkspaceRestorer.UI.Cards.Drag;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        private readonly ApplicationController controller;
        private readonly ListView sessionList;
        private readonly ListView desktopList;
        private readonly TextBox desktopSearch;
        private Microsoft.Win32.OpenFileDialog browseFileDialog;
        private readonly SelectableText statusText;
        private readonly TextBlock countdownText;
        private readonly TextBlock sessionSummary;
        private readonly SelectableText layoutSummary;
        private readonly ProgressBar progressBar;
        private readonly CheckBox autoStartCheckBox;
        private readonly CheckBox autoRestoreCheckBox;

        /// <summary>「启动与恢复」：保存方案时是否也扫描终端窗口 / 本地网址（默认不勾）。</summary>
        private readonly CheckBox scanTerminalsCheckBox = new CheckBox();
        private readonly CheckBox loadGateCheckBox;
        private readonly TextBox initialDelayBox;
        private readonly TextBox itemDelayBox;
        private readonly TextBox webPageDelayBox;
        // CPU 上限 / 等待上限的控件在公共 Settings 文件夹（CommonCpuSettingsSection.cs）。
        // 【2026-10-19】「自启动应用」自己的等待/间隔（代码放在 Applications/Settings 分区里用）。
        private readonly CheckBox applicationsWaitForLoadCheckBox;
        private readonly TextBox applicationsInitialDelayBox;
        private readonly TextBox applicationsItemDelayBox;
        private readonly TextBox applicationsWebPageDelayBox;
        private readonly StackPanel profileHost;
        private readonly Dictionary<string, bool> profileExpansionStates;
        private readonly Dictionary<string, bool> browserExpansionStates;
        private readonly DispatcherTimer displayChangeTimer;
        private readonly Border restoreNotice;
        private readonly TextBlock restoreNoticeText;
        private readonly DispatcherTimer restoreNoticeTimer;
        private bool restoreNoticePersistent;
        private readonly Shared.ShortcutFeedbackToast shortcutFeedbackToast;
        private readonly Shared.LoadLimitPrompt loadLimitPrompt;
        private readonly Border savingOverlay;
        private readonly Border restoreOverlay;
        private readonly TextBlock restoreProgressText;
        /// <summary>恢复卡片上的标题与说明（两个流程共用：工作会话 / 自启动方案）。</summary>
        private readonly TextBlock restoreTitleText;
        private readonly TextBlock restoreDetailText;
        private readonly Button immediateRestoreButton;
        private readonly DispatcherTimer windowStateSaveTimer;
        private readonly AtomicJsonStore<MainWindowState> windowStateStore;
        private bool restoreWindowMaximized;
        private bool allowClose;
        private bool loadingSettings;
        private bool saveOperationInProgress;
        private SessionItem dragSourceItem;
        /// <summary>
        /// 被拖动的启动项来自哪份方案（用户第 86 条：允许拖到**别的方案**里去）。
        /// 项目行 Drop 时如果这份和落点方案不同，就走“搬到另一份方案”的逻辑。
        /// </summary>
        private StartupProfile dragSourceProfile;
        private Point dragStartPoint;
        private bool dragActive;
        // 【公共区域】全局撤销栈 + 全局快捷键路由（用户 2026-10-17：
        // “快捷键的代码要放到公共文件夹内…是作用于软件的全局”）。
        //   · UndoStack：四个工作区共用同一份撤销记录，Ctrl+Z 撤销最近那一条；
        //   · GlobalShortcutRouter：唯一判断按键的地方，键位表在 Shared/UI/Shell/ShortcutCatalog.cs。
        // 本文件里的 PushUndo / UndoLastAction / RedoLastAction 只是转发，各工作区照旧调用它们。
        private readonly WorkspaceRestorer.UI.Shared.UndoStack undoStack =
            new WorkspaceRestorer.UI.Shared.UndoStack();
        private readonly WorkspaceRestorer.UI.Shared.GlobalShortcutRouter shortcuts =
            new WorkspaceRestorer.UI.Shared.GlobalShortcutRouter();
        /// <summary>当前工作区的文案 key（只用于判断，不随语言变化）。</summary>
        private static string activeWorkspaceKey = ShellTextKeys.NavStartupTitle;
        /// <summary>当前在哪个工作区（只用于撤销记录与日志：“已撤销（在「自启动应用」里）：…”）。</summary>
        private static string activeWorkspaceName = AppText.T(ShellTextKeys.NavStartupTitle);
        /// <summary>
        /// 当前可见的工作区：startup / applications / layout / settings / recycle。
        /// 语言切换只立即刷新这一块，其余工作区标记为 pending，切过去时再刷新。
        /// </summary>
        private string activeSurfaceId = "startup";
        private bool startupWorkspaceLanguageDirty;
        private bool applicationsWorkspaceLanguageDirty;
        private bool layoutWorkspaceLanguageDirty;
        /// <summary>排查快捷键时把每一个按键都写进日志（环境变量 WORKSPACE_RESTORER_LOG_SHORTCUTS=1）。</summary>
        private static readonly bool LogShortcutDiagnostics = string.Equals(
            Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_LOG_SHORTCUTS"),
            "1",
            StringComparison.Ordinal);
        private Canvas dragInsertionLayer;
        private readonly DragGhostVisual dragGhost;
        private ScrollViewer profileScrollViewer;
        private bool suppressProfileSelectOnClickUp;
        private bool inlineRenameActive;
        private bool profileRefreshActive;
        // 重建过程中又有刷新请求：合并成一次，等当前重建结束后补跑，不能静默丢掉。
        private bool profileRefreshQueued;
        private DispatcherTimer pendingProfileSelectTimer;
        private Point profileDragStartPoint;
        private StartupProfile profileDragSource;
        private Slider dragScrollSpeedSlider;
        private TextBlock dragScrollSpeedValueText;
        // 设置面板（弹层）的骨架与状态：分区列表是注册式的，见 CreateSettingsSections()。
        private WrapPanel settingsNavHost;
        private ScrollViewer settingsContentScroll;
        private StackPanel settingsContentHost;
        private Grid shortcutPagerHost;
        private AdaptiveTwoPane settingsPane;
        private List<SettingsSection> settingsSections;
        private string activeSettingsSectionId;
        private string pendingLanguageCode;
        private bool languageChangeScheduled;
        // Section pages are built once and then reused: the settings controls are
        // single instances (check boxes, number boxes, the speed slider), so
        // rebuilding a page would try to re-parent them.
        private Dictionary<string, UIElement> settingsPageCache;
        /// <summary>Highlights the bottom-left 设置 entry while the panel is open.</summary>


        /// <summary>Nav tile that is currently selected (its hover shade differs).</summary>
        private static Button activeNavigationButton;
        private bool rowDragArmed;
        /// <summary>本次“整行拖动”是按哪个鼠标键武装的（≡ 手柄固定左键）。</summary>
        private MouseButton rowDragMouseButton = MouseButton.Left;
        /// <summary>清单下方那块白色空白区：按住可滚动，松手＝移到最末尾。</summary>
        private Border profileDropPad;
        private Border profileDropPadTop;
        private bool operationTopmostOwned;
        private bool topmostBeforeOperation;

        /// <summary>
        /// 左侧导航栏占的总宽度（栏宽 220 + 右外边距 24）。提示横幅要"在项目页面中间"，
        /// 就靠它整体右移这么多（半个左栏宽度还多一点点，视觉上正好落在右侧工作区中心）。
        /// </summary>
        private const double LeftRailWidth = 244;

        /// <summary>
        /// Ctrl+C 复制下来的"工作方案"文本（自包含 JSON）。
        /// 不只用系统剪贴板：系统剪贴板随时会被别的程序覆盖，自己留一份更可靠。
        /// </summary>
        private string copiedProfileText;
        // 图标布局的复制文本（带 CSK1|图标位置保存 标记；系统剪贴板被占用时也能在本进程内粘贴）。
        private string copiedLayoutText;

        /// <summary>“浏览”用的系统文件选择对话框（可多选）。</summary>


        public MainWindow(ApplicationController controller)
        {
            if (controller == null)
            {
                throw new ArgumentNullException("controller");
            }

            this.controller = controller;
            Title = AppText.T(ShellTextKeys.WindowTitle);
            LocalizedText.Title(this, ShellTextKeys.WindowTitle);
            Width = 1080;
            Height = 720;
            MinWidth = 720;
            MinHeight = 480;
            Background = AppTheme.WindowBackground;
            FontFamily = AppTheme.UiFont;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            // 【2026-10-19 用户要求】软件要有图标：从 `data\图标.ico`（交付文件）读，
            // 标题栏、任务栏、Alt-Tab 都用它；文件不在就退回 exe 里嵌的图标（Build.ps1 嵌的是同一个文件）。
            TryApplyWindowIcon();
            DpiWindowBehavior.Attach(this);


            // Remember the window size/position like Explorer or a browser:
            // resize it once and the next launch reopens at that size.
            AppPaths.EnsureDataDirectories();
            windowStateStore = new AtomicJsonStore<MainWindowState>(
                AppPaths.WindowStateFile,
                delegate { return new MainWindowState(); });
            MainWindowState savedState = LoadWindowState();
            // 【2026-10-19 用户要求】“要不要适应屏幕。”——窗口再大也不能大过工作区：
            // 小屏幕 / 高缩放（例如 1366×768@150%）上先按工作区缩小，最小尺寸也跟着让一步；
            // 记住的位置如果跑到屏幕外，拉回可见范围，避免打开就看不见标题栏。
            Rect workArea = SystemParameters.WorkArea;
            double fitWidth = Math.Max(600, workArea.Width - 40);
            double fitHeight = Math.Max(420, workArea.Height - 40);
            MinWidth = Math.Min(MinWidth, fitWidth);
            MinHeight = Math.Min(MinHeight, fitHeight);
            if (savedState != null)
            {
                double savedWidth = Math.Max(MinWidth, savedState.Width);
                double savedHeight = Math.Max(MinHeight, savedState.Height);
                if (savedWidth > 0 && savedWidth <= 8000 && savedHeight > 0 && savedHeight <= 8000)
                {
                    Width = Math.Min(savedWidth, fitWidth);
                    Height = Math.Min(savedHeight, fitHeight);
                }
                if (savedState.HasPosition)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = Math.Min(Math.Max(savedState.Left, workArea.Left - 8), workArea.Right - 200);
                    Top = Math.Min(Math.Max(savedState.Top, workArea.Top), workArea.Bottom - 120);
                }
                restoreWindowMaximized = savedState.Maximized;
            }
            else
            {
                Width = Math.Min(Width, fitWidth);
                Height = Math.Min(Height, fitHeight);
            }
            windowStateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            windowStateSaveTimer.Tick += delegate { SaveWindowState(); };
            SizeChanged += delegate { RestartWindowStateSave(); };
            LocationChanged += delegate { RestartWindowStateSave(); };
            StateChanged += delegate { RestartWindowStateSave(); UpdateOperationTopmost(); };
            IsVisibleChanged += delegate { UpdateOperationTopmost(); };

            sessionList = new ListView { Margin = new Thickness(0, 12, 0, 0) };
            sessionList.View = CreateSessionView();
            desktopList = new ListView { Margin = new Thickness(0, 10, 0, 0) };
            desktopList.View = CreateDesktopView();
            desktopList.MouseDoubleClick += OnDesktopDoubleClick;
            desktopList.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnDesktopActionButtonClick), true);
            desktopSearch = new TextBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Right };
            desktopSearch.TextChanged += delegate { RefreshDesktopList(); };
            // 【2026-10-19】状态栏文字改成可选中复制的只读文字（用户要求“现有的字体都可以按住左键选取复制”）：
            // 出错时用户可以直接拖选那一句复制出来。下面所有 `statusText.Text = …` 的写法都不用改。
            statusText = new SelectableText { FontSize = 12, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
            countdownText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = AppTheme.Warning, VerticalAlignment = VerticalAlignment.Center };
            sessionSummary = new TextBlock { FontSize = 12, Foreground = AppTheme.TextSecondary };
            // 图标位置保存的说明与「启动与恢复」同位置，并且走公共 SelectableText：
            // 可以像其它说明一样按住左键选中复制。
            layoutSummary = new SelectableText
            {
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            LocalizedText.Text(layoutSummary, ShellTextKeys.LayoutSummary);
            // 尺寸、对齐和“画在哪个卡片上”统一在 BuildContent 里设置；
            // 这里只建控件和主题，避免两处各写一份位置。
            progressBar = new ProgressBar { Height = 6, Minimum = 0, Foreground = AppTheme.Accent, Background = AppTheme.Border, Visibility = Visibility.Collapsed };
            // 这四个勾选框的文字由设置行提供：PrepareSettingsCheckBox 会把 Content 清空，
            // 避免设置行里重复显示同一个开关名字（无障碍名字仍由 AccessibleToolTip 提供）。
            autoStartCheckBox = new CheckBox { Margin = new Thickness(0, 10, 18, 0) };
            autoRestoreCheckBox = new CheckBox { Margin = new Thickness(0, 10, 18, 0) };
            loadGateCheckBox = new CheckBox { Margin = new Thickness(0, 10, 18, 0) };
            initialDelayBox = CreateNumberBox();
            AccessibleToolTip.Attach(initialDelayBox, SettingsToolTips.StartupInitialDelay);
            itemDelayBox = CreateNumberBox();
            AccessibleToolTip.Attach(itemDelayBox, SettingsToolTips.StartupItemDelay);
            webPageDelayBox = CreateDecimalBox();
            AccessibleToolTip.Attach(webPageDelayBox, SettingsToolTips.StartupWebDelay);
            InitializeCommonCpuSettings();
            InitializeCommonMemorySettings();
            // 回收站保留天数只在「回收站」页面里改（用户第 67 条要求把总设置里那一份删掉，
            // 对应的控件与提交方法一并删除，避免僵尸代码）。
            // 【2026-10-20】参数框统一走公共的 ParameterField：
            // 回车提交、失焦提交、点空白提交并收走焦点（见 Shared/UI/Controls/ParameterField.cs）。
            applicationsWaitForLoadCheckBox = new CheckBox { Margin = new Thickness(0, 10, 18, 0) };
            applicationsInitialDelayBox = CreateNumberBox();
            AccessibleToolTip.Attach(applicationsInitialDelayBox, SettingsToolTips.ApplicationsInitialDelay);
            applicationsItemDelayBox = CreateNumberBox();
            AccessibleToolTip.Attach(applicationsItemDelayBox, SettingsToolTips.ApplicationsItemDelay);
            applicationsWebPageDelayBox = CreateDecimalBox();
            AccessibleToolTip.Attach(applicationsWebPageDelayBox, SettingsToolTips.ApplicationsWebDelay);
            // 【2026-10-20】参数框统一走公共的 ParameterField（见 Shared/UI/Controls/ParameterField.cs）：
            // 点一下开始改；点空白 / 别的控件就提交，并把焦点收走。
            ParameterField.Register(this, applicationsInitialDelayBox, CommitApplicationsTiming);
            ParameterField.Register(this, applicationsItemDelayBox, CommitApplicationsTiming);
            ParameterField.Register(this, applicationsWebPageDelayBox, CommitApplicationsTiming);
            profileHost = new StackPanel();
            profileExpansionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            browserExpansionStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            displayChangeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            displayChangeTimer.Tick += OnDisplayChangeTimerTick;

            // Transient centered toast that reports how a save/restore ended.
            // It disappears when the user clicks anywhere, or on its own after
            // the interval when nothing is clicked.
            restoreNoticeText = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 640
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(restoreNoticeText, "CompletionBanner");
            restoreNotice = new Border
            {
                Child = restoreNoticeText,
                Visibility = Visibility.Collapsed,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(18, 10, 18, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 1,
                    Opacity = 0.18,
                    Color = Colors.Black
                }
            };
            restoreNoticeTimer = new DispatcherTimer
            {
                // 与快捷键浮窗共用同一个时长，用户要求“时间和恢复时的时间一样”。
                Interval = TimeSpan.FromMilliseconds(Shared.ShortcutFeedbackToast.DisplayDurationMilliseconds)
            };
            restoreNoticeTimer.Tick += delegate
            {
                if (!restoreNoticePersistent)
                {
                    HideRestoreNotice();
                }
            };

            shortcutFeedbackToast = new Shared.ShortcutFeedbackToast();
            loadLimitPrompt = new Shared.LoadLimitPrompt();
            loadLimitPrompt.ContinueRequested += delegate
            {
                controller.ContinueRestoreDespiteLoad();
            };
            loadLimitPrompt.CancelRequested += delegate
            {
                controller.CancelRestore();
                controller.CancelApplicationsStart();
            };

            // Centered "saving in progress" card shown while an explicit save
            // runs: title, explanatory detail, hover tooltip and a cancel
            // button at its bottom-right.
            Grid savingCardContent = new Grid { Width = 460 };
            savingCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            savingCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            savingCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock savingTitleText = new TextBlock
            {
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            LocalizedText.Text(savingTitleText, ShellTextKeys.SavingTitle);
            System.Windows.Automation.AutomationProperties.SetAutomationId(savingTitleText, "SavingTitle");
            savingCardContent.Children.Add(savingTitleText);

            TextBlock savingDetailText = new TextBlock
            {
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            LocalizedText.Text(savingDetailText, ShellTextKeys.SavingDetail);
            Grid.SetRow(savingDetailText, 1);
            savingCardContent.Children.Add(savingDetailText);

            Button cancelSaveButton = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand
            };
            LocalizedText.Content(cancelSaveButton, ShellTextKeys.SavingCancel);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelSaveButton, "CancelSaveButton");
            cancelSaveButton.Click += delegate { controller.CancelSessionSave(); };
            Grid.SetRow(cancelSaveButton, 2);
            savingCardContent.Children.Add(cancelSaveButton);

            savingOverlay = new Border
            {
                Child = savingCardContent,
                Visibility = Visibility.Collapsed,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 16, 20, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 1,
                    Opacity = 0.2,
                    Color = Colors.Black
                }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(savingOverlay, "SavingOverlay");
            AccessibleToolTip.AttachDescription(savingOverlay, ShellToolTips.SavingOverlay);

            // Centered "restoring in progress" card, mirroring the save card:
            // title, detail, hover tooltip and a cancel button at its bottom
            // right that stops the running restore.
            Grid restoreCardContent = new Grid { Width = 440 };
            restoreCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            restoreCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            restoreCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            restoreCardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            restoreTitleText = new TextBlock
            {
                Text = AppText.T(ShellTextKeys.RestoreTitleProfile),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(restoreTitleText, "RestoreTitle");
            restoreCardContent.Children.Add(restoreTitleText);

            // Live progress line: shows exactly which item is being restored and
            // how far along the restore is, so the user can see where it is.
            TextBlock progressText = new TextBlock
            {
                Text = AppText.T(ShellTextKeys.RestoreProgressPreparing),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Accent,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            };
            restoreProgressText = progressText;
            System.Windows.Automation.AutomationProperties.SetAutomationId(progressText, "RestoreProgress");
            Grid.SetRow(progressText, 1);
            restoreCardContent.Children.Add(progressText);

            restoreDetailText = new TextBlock
            {
                Text = AppText.T(ShellTextKeys.RestoreDetailSummary),
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(restoreDetailText, 2);
            restoreCardContent.Children.Add(restoreDetailText);

            immediateRestoreButton = new Button
            {
                Visibility = Visibility.Collapsed,
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand
            };
            LocalizedText.Content(immediateRestoreButton, ShellTextKeys.RestoreImmediate);
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                immediateRestoreButton,
                "ImmediateRestoreButton");
            AccessibleToolTip.Attach(immediateRestoreButton, ShellToolTips.ImmediateRestore);
            immediateRestoreButton.Click += delegate
            {
                controller.SkipRestoreCountdown();
            };

            Button cancelRestoreButton = new Button
            {
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                Cursor = Cursors.Hand
            };
            LocalizedText.Content(cancelRestoreButton, ShellTextKeys.RestoreCancel);
            System.Windows.Automation.AutomationProperties.SetAutomationId(cancelRestoreButton, "CancelRestoreButton");
            // 【2026-10-19】这张卡片现在两个流程共用；取消要把两边都停掉，
            // 否则“自启动方案”那一半会继续把后面的项目一个个打开（用户要的是点了就停）。
            cancelRestoreButton.Click += delegate
            {
                controller.CancelRestore();
                controller.CancelApplicationsStart();
            };

            StackPanel restoreButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            restoreButtons.Children.Add(immediateRestoreButton);
            restoreButtons.Children.Add(cancelRestoreButton);
            Grid.SetRow(restoreButtons, 3);
            restoreCardContent.Children.Add(restoreButtons);

            restoreOverlay = new Border
            {
                Child = restoreCardContent,
                Visibility = Visibility.Collapsed,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 16, 20, 16),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 540,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 1,
                    Opacity = 0.2,
                    Color = Colors.Black
                }
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(restoreOverlay, "RestoreOverlay");
            AccessibleToolTip.AttachDescription(restoreOverlay, ShellToolTips.RestoreOverlay);

            dragGhost = DragGhostVisual.Create();
            // 起拖、窗口级鼠标钩子、放置条/自动滚动全部由公共 `DragMoveSession` 接管。
            // 这里不再注册 OLE 的 DragOver/Drop/GiveFeedback，也不再把窗口设成 DropTarget。
            // 【公共快捷键】窗口级接管按键：具体键位在 Shared/UI/Shell/ShortcutCatalog.cs，
            // 判断与分派在 GlobalShortcutRouter，四个工作区共用这一份（用户 2026-10-17 要求）。
            WireGlobalShortcuts();
            KeyDown += OnMainWindowKeyDown;
            // PreviewKeyDown 只用来记日志：输入框会自己吃掉 Ctrl+C/V/Z，日志里写清“交给输入框”，
            // 以后排查“Ctrl+Z 没反应”就能分清是程序没收到、还是焦点在输入框里。
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                // 恢复卡片的 Enter/Esc 放在 Preview 层：卡片上的按钮即使有焦点，
                // 也不会先把 Enter 当成“点这个按钮”（例如误点取消）。
                if (HandleRestoreCardKey(args))
                {
                    return;
                }
                if (shortcuts.HandlePreview(args))
                {
                    return;
                }
                shortcuts.ObservePreview(args);
            };
            // 【2026-10-17 关键一层】WPF 里 TextBox 的类级输入绑定（Ctrl+Z → ApplicationCommands.Undo）
            // 是在**命令翻译阶段**把这个键吃掉的，比 KeyDown / PreviewKeyDown 都早：
            // 只要光标落在任何一个输入框里，Ctrl+Z 就根本到不了窗口的 KeyDown。
            // 于是先点进设置里的命名框 / 搜索框、再按 Ctrl+Z，永远没反应（用户报的“无法撤销”里就有这一半）。
            // 这里在 InputManager 最早的入口先看一眼：输入框没本事撤销时，替它执行全局撤销。
            System.Windows.Input.InputManager.Current.PreProcessInput += OnWindowPreProcessInputKeys;
            // 文件浏览对话框：用户要求（2026-10-09 19:04 图1）点“浏览”要跳到系统那种文件选择界面，
            // 可以自己跑到 C 盘 / D 盘挑，而不是只能挑桌面上的东西。用系统的“打开”对话框：它支持多选；
            // 第一次打开默认在桌面。
            // 【2026-10-17 第 53 批】用户要求把“浏览”恢复成这个（可多选）＋“挑文件夹”两个入口。
            browseFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = AppText.T(ShellTextKeys.DialogOpenItemsTitle),
                Filter = AppText.T(ShellTextKeys.DialogAllFilesFilter),
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = true
            };
            string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktopFolder) && System.IO.Directory.Exists(desktopFolder))
            {
                browseFileDialog.InitialDirectory = desktopFolder;
            }
            Content = BuildContent();
            Loaded += OnLoaded;
            SourceInitialized += OnSourceInitialized;
            Closing += OnClosing;
            controller.PropertyChanged += OnControllerPropertyChanged;
            controller.RestoreCompleted += OnRestoreCompleted;
            controller.SaveCompleted += OnSaveCompleted;
            controller.SaveStarted += OnSaveStarted;
            controller.RestoreStarted += OnRestoreStarted;
            AppText.LanguageChanged += OnLanguageChanged;
            PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                HideRestoreNotice();
                shortcutFeedbackToast.Hide();
            };
            autoStartCheckBox.Checked += delegate { if (!loadingSettings) controller.SetAutoStart(true); };
            autoStartCheckBox.Unchecked += delegate { if (!loadingSettings) controller.SetAutoStart(false); };
            autoRestoreCheckBox.Checked += delegate { if (!loadingSettings) { controller.SetAutoRestore(true); RefreshSettings(); } };
            autoRestoreCheckBox.Unchecked += delegate { if (!loadingSettings) { controller.SetAutoRestore(false); RefreshSettings(); } };
        // 「启动与恢复」：保存方案时是否也扫描终端窗口 / 本地网址（默认不勾；勾了会在提示里说明哪些恢复不了）。
        scanTerminalsCheckBox.Checked += delegate { if (!loadingSettings) { controller.SetScanTerminalsAndLocalUrls(true); RefreshSettings(); } };
        scanTerminalsCheckBox.Unchecked += delegate { if (!loadingSettings) { controller.SetScanTerminalsAndLocalUrls(false); RefreshSettings(); } };
            loadGateCheckBox.Checked += delegate { if (!loadingSettings) { controller.Settings.WaitForLoad = true; controller.SaveSettings(); } };
            loadGateCheckBox.Unchecked += delegate { if (!loadingSettings) { controller.Settings.WaitForLoad = false; controller.SaveSettings(); } };
            // 【2026-10-20】启动与恢复的三个等待/间隔参数框也走同一个公共交互。
            ParameterField.Register(this, initialDelayBox, CommitTiming);
            ParameterField.Register(this, itemDelayBox, CommitTiming);
            ParameterField.Register(this, webPageDelayBox, CommitTiming);
            AccessibleToolTip.Attach(autoStartCheckBox, SettingsToolTips.AutoStart);
            AccessibleToolTip.Attach(autoRestoreCheckBox, SettingsToolTips.StartupAutoRestore);
            AccessibleToolTip.Attach(loadGateCheckBox, SettingsToolTips.LoadGate);
            AccessibleToolTip.Attach(scanTerminalsCheckBox, SettingsToolTips.ScanTerminals);
            // 【2026-10-17 补】这个勾选框一直没有无障碍名字（其它几个都有）：
            // 读屏软件只会念“复选框”，自检也按名字找不到它（Round86 因此报“状态=空”）。
            AccessibleToolTip.Attach(applicationsAutoRestoreCheckBox, SettingsToolTips.ApplicationsAutoStart);
        }

        public MainWindow(string[] args)
            : this(new ApplicationController())
        {
        }

    }
}
