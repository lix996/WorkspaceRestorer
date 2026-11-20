using System;
using System.Collections.Generic;
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
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Surface;
using WorkspaceRestorer.UI.Cards.Interaction;
namespace WorkspaceRestorer.UI
{
    // 工作区：图标位置保存
    // （从 MainWindow.cs 拆分出来，便于各自维护；同为 partial class MainWindow。）
    public sealed partial class MainWindow
    {
        // 【2026-10-20】每个命名布局一份“文件夹”卡片；展开看保存时的桌面图片。
        private StackPanel layoutCardsHost;
        private ScrollViewer layoutCardsScroll;
        private readonly HashSet<string> expandedLayoutSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>布局卡片标题条/外层整块登记（拖动专用；由 BuildLayoutCard 登记）。</summary>
        private readonly WorkspaceRestorer.UI.Cards.Drag.DragCardElementRegistry layoutCardVisuals =
            new WorkspaceRestorer.UI.Cards.Drag.DragCardElementRegistry();
        /// <summary>被拖布局自己的后代卡片（窗口坐标）：这些区域是禁止落点。</summary>
        private readonly List<Rect> layoutCardForbiddenRects = new List<Rect>();
        // 【2026-10-24】整张卡的外层只负责定位/拖动几何；active/hover 的颜色只画在标题条上，
        // 和「启动与恢复」「自启动应用」保持一致。以前 CardSurface 套在整张卡上，
        // 展开后的预览区和并入反馈都会跟着泛蓝（用户截图里的“卡片串色”）。
        /// <summary>当前列表里可见卡片的显示名：拖动提示直接查它，鼠标移动期间不再读布局档案。</summary>
        private readonly Dictionary<string, string> layoutCardNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>当前列表里可见的布局文件夹 Id：起拖时用它算“谁可以接收布局”，不再临时读盘。</summary>
        private readonly HashSet<string> layoutFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Border layoutTopPad;
        private Border layoutBottomPad;
        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession layoutCardDragSession;
        private DesktopTopologyLayout layoutCardDrag;
        /// <summary>刚拖完一张布局卡片：松手那一下不能再当成“点击选中/展开”。</summary>
        private bool layoutCardDragJustEnded;
        private DateTime layoutCardDragEndedUtc;
        /// <summary>本次拖动允许接收“并入”的卡片 Id（布局文件夹）。起拖时算一次，DragOver 不反复读盘。</summary>
        private readonly HashSet<string> layoutMergeTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool layoutSaveInProgress;
        // 【2026-10-24】恢复图标位置是“先截快照、再异步摆图标”，必须单入口：
        // 双击卡片/顶部按钮不能各发起一次，也不能在保存进行中抓一份半截桌面。
        private bool layoutRestoreInProgress;
        // 允许多个编辑窗并存：每个布局一份，关掉一个不影响其他窗口。
        private readonly List<DesktopLayoutEditorWindow> openLayoutEditors =
            new List<DesktopLayoutEditorWindow>();
        private bool layoutEditInProgress
        {
            get { return openLayoutEditors.Count > 0; }
        }
        private Task<DesktopShellSnapshot> sharedLayoutCaptureTask;
        private DateTime sharedLayoutCaptureStartedUtc;
        private Point layoutCardDragPress;
        private DesktopTopologyLayout layoutCardDragCandidate;
        /// <summary>本次整卡拖动按下的鼠标键（≡ 手柄固定左键）。</summary>
        private MouseButton layoutCardDragMouseButton = MouseButton.Left;
        private DispatcherTimer pendingLayoutSelectTimer;
        private bool suppressLayoutSelectOnClickUp;
        private int layoutAutoRestoreGeneration;
        /// <summary>当前选中的布局文件夹；新建布局时放进去。选中叶子时记它的父文件夹。</summary>
        private string selectedLayoutFolderId;
        /// <summary>
        /// 最近一次刷新时读到的“当前布局”Id。拖动开始/结束只改标题条颜色，
        /// 用它即可，不再为这一项单独读一遍 `布局档案.xml`。
        /// </summary>
        private string layoutActiveId = string.Empty;

        /// <summary>布局保存完成；窗口隐藏时 App 用它发托盘通知。</summary>
        internal event Action<DesktopTopologyLayout> LayoutSaved;


        private Border BuildLayoutWorkspace()
        {
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            // 【2026-11-15 用户要求】标题下一句话 + 问号；这句本身就是一句话，问号里放同一句。
            heading.Children.Add(UiFactory.HeadingWithHintKey(
                DesktopLayoutTextKeys.PageTitle,
                DesktopLayoutTextKeys.PageSubtitle,
                DesktopLayoutTextKeys.PageSubtitle,
                18));
            // 与「启动与恢复」一致：说明摘要放在标题/说明下面、主按钮上面，
            // 并且用公共 SelectableText，支持按住左键选中复制。
            layoutSummary.TextWrapping = TextWrapping.Wrap;
            heading.Children.Add(layoutSummary);
            Grid.SetRow(heading, 0);
            content.Children.Add(heading);

            WrapPanel commands = UiFactory.PrimaryActionRow();
            Button saveLayoutButton = UiFactory.CommandButtonKey("\uE8B9", DesktopLayoutTextKeys.SaveLayout, true);
            Button restoreLayoutButton = UiFactory.CommandButtonKey("\uE8B8", DesktopLayoutTextKeys.RestoreNow, false);
            saveLayoutButton.Click += delegate { SaveDesktopLayoutAsync(); };
            restoreLayoutButton.Click += delegate { RestoreDesktopLayoutFromButtonAsync(); };
            AccessibleToolTip.Attach(saveLayoutButton, DesktopLayoutToolTips.SaveLayout);
            AccessibleToolTip.Attach(restoreLayoutButton, DesktopLayoutToolTips.RestoreNow);
            // 和「启动与恢复」「自启动应用」用同一个公共主操作行：
            // 窗口变窄时 WrapPanel 自动换行，MinWidth/间距也由公共件统一。
            UiFactory.AddPrimaryAction(commands, saveLayoutButton);
            UiFactory.AddPrimaryAction(commands, restoreLayoutButton);
            Grid.SetRow(commands, 1);
            content.Children.Add(commands);

            // 用户要求（2026-11-18）：命名框搬进设置 → 图标位置保存；
            // 搜索布局 / 追加当前时间 / 新建文件夹留在页面，放主按钮下面这一行。
            WrapPanel secondaryCommands = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };

            Button searchLayouts = UiFactory.CommandButtonKey("\uE721", DesktopLayoutTextKeys.SearchLayouts, false);
            searchLayouts.Margin = new Thickness(0, 0, 8, 0);
            searchLayouts.Click += delegate { SearchDesktopLayouts(); };
            AccessibleToolTip.Attach(searchLayouts, DesktopLayoutToolTips.SearchLayouts);
            secondaryCommands.Children.Add(searchLayouts);

            Button appendTime = UiFactory.CommandButtonKey("\uE823", DesktopLayoutTextKeys.AppendTime, false);
            appendTime.Margin = new Thickness(0, 0, 8, 0);
            appendTime.Click += delegate { AppendCurrentTimeToLayoutName(); };
            AccessibleToolTip.Attach(appendTime, DesktopLayoutToolTips.AppendTime);
            secondaryCommands.Children.Add(appendTime);

            // 新建文件夹仍走原来的弹框入口；命名框已搬走，它不再借用页面上的名字。
            Button newFolder = UiFactory.CommandButtonKey("\uE8B7", DesktopLayoutTextKeys.NewFolder, false);
            // 保留原来的无障碍名字：探针/读屏仍按“新建布局文件夹”找。
            LocalizedText.AccessibleName(newFolder, DesktopLayoutTextKeys.NewFolderAutomation);
            newFolder.Margin = new Thickness(0, 0, 8, 0);
            newFolder.VerticalAlignment = VerticalAlignment.Center;
            newFolder.Click += delegate { CreateLayoutFolderInteractive(); };
            AccessibleToolTip.Attach(newFolder, DesktopLayoutToolTips.NewFolder);
            secondaryCommands.Children.Add(newFolder);
            Grid.SetRow(secondaryCommands, 2);
            content.Children.Add(secondaryCommands);

            // 文件夹按钮在上一行末尾，标题行只留标题 + 一行小字，小字可用满整行宽。
            StackPanel listHeader = new StackPanel { Margin = new Thickness(0, 18, 0, 8) };
            listHeader.Children.Add(UiFactory.HeadingKey(DesktopLayoutTextKeys.SavedLayoutsHeader, 14));
            TextBlock dragHint = new TextBlock
            {
                Foreground = AppTheme.TextSecondary,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            LocalizedText.Text(dragHint, DesktopLayoutTextKeys.DragHint);
            listHeader.Children.Add(dragHint);
            Grid.SetRow(listHeader, 3);
            content.Children.Add(listHeader);

            layoutCardsHost = new StackPanel();
            layoutCardsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = layoutCardsHost,
                // 点列表空白时会把焦点交给它，只当快捷键的焦点落点用。
                // 不关掉系统 FocusVisualStyle 的话，文件夹区域会出现一圈虚线（与「启动与恢复」不一致）。
                Focusable = true,
                FocusVisualStyle = null
            };
            layoutTopPad = WorkspaceRestorer.UI.Cards.Drag.DragDropPadFactory.CreateTop(
                AppText.T(DesktopLayoutTextKeys.ScrollTop));
            layoutBottomPad = WorkspaceRestorer.UI.Cards.Drag.DragDropPadFactory.CreateBottom(
                AppText.T(DesktopLayoutTextKeys.ScrollBottom));
            AccessibleToolTip.Attach(layoutTopPad, DesktopLayoutToolTips.ScrollTop);
            AccessibleToolTip.Attach(layoutBottomPad, DesktopLayoutToolTips.ScrollBottom);
            layoutCardsScroll.Background = Brushes.Transparent;
            ContextMenu blankLayoutMenu = WorkspaceRestorer.UI.Shared.ContextMenus.Create(
                WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuNewFolder), delegate { CreateLayoutFolderInteractive(); }),
                WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuNewLayout), delegate { CreateLayoutFromNameBox(); }));
            WorkspaceRestorer.UI.Shared.ContextMenus.Attach(layoutCardsScroll, blankLayoutMenu, layoutCardsHost, layoutBottomPad);
            Grid cardsArea = new Grid();
            cardsArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardsArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(layoutCardsScroll, 0);
            cardsArea.Children.Add(layoutCardsScroll);
            Grid.SetRow(layoutBottomPad, 1);
            cardsArea.Children.Add(layoutBottomPad);
            // 顶部滚动区和其余两个工作区完全一样：64 像素、白底、负边距浮在列表上沿，
            // 平时透明不挡点击，拖进来才亮起来。
            Panel.SetZIndex(layoutTopPad, 3);
            Grid.SetRow(layoutTopPad, 0);
            cardsArea.Children.Add(layoutTopPad);
            Grid.SetRow(cardsArea, 4);
            content.Children.Add(cardsArea);

            RefreshDesktopLayoutCards();
            return WorkspaceBorder(content);
        }

        /// <summary>
        /// 单击切换当前布局：只改数据和卡片外框，不重建整个列表。
        /// 之前这里每次都 RefreshDesktopLayoutCards，文件夹/截图/按钮全部重建，单击明显卡一下。
        /// </summary>
        private void SelectLayout(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return;
            }
            if (layout.IsFolder)
            {
                selectedLayoutFolderId = layout.Id;
                if (expandedLayoutSignatures.Contains(layout.Id ?? string.Empty))
                {
                    expandedLayoutSignatures.Remove(layout.Id ?? string.Empty);
                }
                else
                {
                    expandedLayoutSignatures.Add(layout.Id ?? string.Empty);
                }
                RefreshDesktopLayoutCards();
                statusText.Text = AppText.F(
                DesktopLayoutTextKeys.SelectedFolderFormat,
                layout.Name ?? AppText.T(DesktopLayoutTextKeys.FolderFallback));
                return;
            }
            selectedLayoutFolderId = layout.ParentId;
            string activeId = controller.SetActiveDesktopLayout(layout.Id)
                ? layout.Id
                : controller.ActiveDesktopLayoutId;
            layoutActiveId = activeId;
            // 只看标题条：外层卡片不再承载 active/hover 底色。
            foreach (WorkspaceRestorer.UI.Cards.Drag.DragCardElement element in layoutCardVisuals.Items)
            {
                Border header = element.Header as Border;
                if (header != null)
                {
                    WorkspaceRestorer.UI.Cards.Surface.CardSurface.SetActive(
                        header,
                        string.Equals(element.Id, activeId, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        /// <summary>
        /// 单击选择延迟 260ms：和「启动与恢复」一样，双击改名时不要先把这张卡片切成当前布局。
        /// </summary>
        private void ScheduleLayoutSelect(DesktopTopologyLayout layout)
        {
            CancelPendingLayoutSelect();
            if (layoutCardDragJustEnded)
            {
                bool justEnded = (DateTime.UtcNow - layoutCardDragEndedUtc).TotalMilliseconds < 800;
                layoutCardDragJustEnded = false;
                if (justEnded)
                {
                    return;
                }
            }
            if (suppressLayoutSelectOnClickUp)
            {
                suppressLayoutSelectOnClickUp = false;
                return;
            }
            if (layout == null)
            {
                return;
            }
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            pendingLayoutSelectTimer = timer;
            timer.Tick += delegate
            {
                timer.Stop();
                if (object.ReferenceEquals(pendingLayoutSelectTimer, timer))
                {
                    pendingLayoutSelectTimer = null;
                }
                SelectLayout(layout);
            };
            timer.Start();
        }

        private void CancelPendingLayoutSelect()
        {
            if (pendingLayoutSelectTimer != null)
            {
                pendingLayoutSelectTimer.Stop();
                pendingLayoutSelectTimer = null;
            }
        }

        /// <summary>改名并进撤销栈；空名字/失败返回 false，由 InlineRename 保持编辑框不提交。</summary>
        private bool RenameLayoutWithUndo(DesktopTopologyLayout layout, string newName)
        {
            if (layout == null)
            {
                return false;
            }
            // 编辑窗是非模态的；同一个布局正在编辑时，主页面改名/删除会和编辑窗保存互相覆盖。
            // 先让用户处理编辑窗，避免旧名字被写回、或删除后又被编辑窗重新建出来。
            DesktopLayoutEditorWindow openEditor = FindOpenLayoutEditor(layout);
            if (openEditor != null)
            {
                openEditor.Activate();
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorOpenRenameBlocked);
                return false;
            }
            string name = (newName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.NameRequired);
                return false;
            }
            string oldName = layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback);
            if (string.Equals(oldName, name, StringComparison.Ordinal))
            {
                return true;
            }
            if (!controller.RenameDesktopLayout(layout, name))
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.RenameFailed);
                return false;
            }
            string acceptedName = layout.Name ?? name;
            RefreshDesktopLayoutCards();
            statusText.Text = AppText.F(DesktopLayoutTextKeys.RenamedFormat, acceptedName);
            PushUndo(
                AppText.T(DesktopLayoutTextKeys.UndoRename),
                delegate
                {
                    controller.RenameDesktopLayout(layout, oldName);
                    RefreshDesktopLayoutCards();
                },
                delegate
                {
                    controller.RenameDesktopLayout(layout, acceptedName);
                    RefreshDesktopLayoutCards();
                });
            return true;
        }

        /// <summary>重建“已保存的布局”文件夹卡片。</summary>
        private void RefreshDesktopLayoutCards()
        {
            DesktopLayoutCatalog catalog = null;
            IList<DesktopTopologyLayout> loaded = null;
            try
            {
                catalog = controller.LoadLayoutCatalog();
                loaded = controller.GetDesktopLayouts(catalog);
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 读取布局清单失败", exception);
            }
            RefreshDesktopLayoutCards(loaded, catalog == null ? string.Empty : (catalog.ActiveLayoutId ?? string.Empty));
        }

        /// <summary>
        /// 用调用方已经加载好的档案重建卡片。同一次动作（拖放/撤销/保存）里只加载一次档案，
        /// 这里不再读盘；一次拖放不会再为“取清单”“取当前布局”“重建卡片”各读一遍。
        /// </summary>
        private void RefreshDesktopLayoutCards(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                RefreshDesktopLayoutCards();
                return;
            }
            RefreshDesktopLayoutCards(
                controller.GetDesktopLayouts(catalog),
                catalog.ActiveLayoutId ?? string.Empty);
        }

        private void RefreshDesktopLayoutCards(IList<DesktopTopologyLayout> loaded, string activeId)
        {
            CancelPendingLayoutSelect();
            if (layoutCardsHost == null)
            {
                return;
            }
            layoutCardsHost.Children.Clear();
            layoutCardVisuals.Clear();
            layoutCardNames.Clear();
            layoutFolderIds.Clear();
            layoutActiveId = activeId ?? string.Empty;
            List<DesktopTopologyLayout> layouts = new List<DesktopTopologyLayout>();
            if (loaded != null)
            {
                for (int index = 0; index < loaded.Count; index++)
                {
                    if (loaded[index] != null)
                    {
                        layouts.Add(loaded[index]);
                    }
                }
            }
            layouts.Sort(delegate(DesktopTopologyLayout left, DesktopTopologyLayout right)
            {
                int order = left.SortOrder.CompareTo(right.SortOrder);
                return order != 0 ? order : right.UpdatedUtc.CompareTo(left.UpdatedUtc);
            });

            if (layouts.Count == 0)
            {
                layoutCardsHost.Children.Add(new Border
                {
                    Background = AppTheme.PreviewSurface,
                    BorderBrush = AppTheme.Border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16),
                    Child = new TextBlock
                    {
                        Text = AppText.T(DesktopLayoutTextKeys.EmptyLayoutsHint),
                        Foreground = AppTheme.TextSecondary,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
                return;
            }

            // 根级按 SortOrder；子布局/子文件夹由每张卡片自己递归挂到父卡片下面。
            List<DesktopTopologyLayout> roots = PlanFolderTree.Roots(layouts);
            roots.Sort(delegate(DesktopTopologyLayout left, DesktopTopologyLayout right)
            {
                return (left == null ? 0 : left.SortOrder).CompareTo(right == null ? 0 : right.SortOrder);
            });
            for (int index = 0; index < roots.Count; index++)
            {
                layoutCardsHost.Children.Add(BuildLayoutCard(roots[index], layouts, 0, activeId));
            }
        }

    }
}
