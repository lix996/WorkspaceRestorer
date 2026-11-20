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
using WorkspaceRestorer.UI.Cards.Actions;
using WorkspaceRestorer.UI.Cards.Surface;
using WorkspaceRestorer.UI.Cards.Interaction;
using WorkspaceRestorer.UI.Cards.Folders;
namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        private UIElement BuildLayoutCard(
            DesktopTopologyLayout layout,
            IList<DesktopTopologyLayout> allLayouts,
            int depth,
            string activeId)
        {
            // 同一拓扑可以有多份布局，展开状态必须按 Id 区分，不能按签名。
            string expandKey = layout.Id ?? string.Empty;
            bool isFolder = layout.IsFolder;
            bool expanded = expandedLayoutSignatures.Contains(expandKey);
            bool active = !isFolder
                && string.Equals(activeId, layout.Id, StringComparison.OrdinalIgnoreCase);
            List<DesktopTopologyLayout> childLayouts = new List<DesktopTopologyLayout>(
                PlanFolderTree.ChildrenOf(allLayouts, layout.Id));
            childLayouts.Sort(delegate(DesktopTopologyLayout left, DesktopTopologyLayout right)
            {
                return (left == null ? 0 : left.SortOrder).CompareTo(right == null ? 0 : right.SortOrder);
            });
            Border card = null;
            TextBox nameEditor = null;
            // 和「启动与恢复」「自启动应用」一致：只有点箭头按钮才展开/收起。
            // 单击卡片标题只选中，不展开；双击名字/图标仍然进入重命名。
            Action toggleCard = delegate
            {
                if (expandedLayoutSignatures.Contains(expandKey))
                {
                    expandedLayoutSignatures.Remove(expandKey);
                }
                else
                {
                    expandedLayoutSignatures.Add(expandKey);
                }
                RefreshDesktopLayoutCards();
            };
            int iconCount = layout.Icons == null ? 0 : layout.Icons.Count;
            string savedAt = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                layout.UpdatedUtc, AppText.CurrentLanguageTag);
            string topology = DesktopLayoutFolderStore.DescribeDisplay(layout);
            string countText;
            if (isFolder)
            {
                countText = childLayouts.Count == 0
                ? AppText.T(DesktopLayoutTextKeys.EmptyFolder)
                : AppText.F(DesktopLayoutTextKeys.ChildLayoutsFormat, childLayouts.Count);
            }
            else
            {
                countText = AppText.F(DesktopLayoutTextKeys.IconsFormat, iconCount);
                if (!string.IsNullOrWhiteSpace(topology))
                {
                    countText += " · " + topology;
                }
                if (!string.IsNullOrWhiteSpace(savedAt))
                {
                    countText += " · " + savedAt;
                }
            }

            // 外层只负责整张卡的几何（拖动落点/“腾开空间”/并入框）；
            // 颜色只画在下面的 headerBorder 上，展开后的桌面预览不会跟着 active/hover 变色。
            card = new Border
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 8)
            };
            Border headerBorder = WorkspaceRestorer.UI.Cards.Surface.CardSurface.Create(active, true);
            StackPanel body = new StackPanel();

            Grid header = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Button expandButton = UiFactory.IconButton(
                expanded ? "\uE70E" : "\uE70D",
                DesktopLayoutToolTips.CardExpand);
            expandButton.Width = 30;
            expandButton.Height = 30;
            expandButton.Margin = new Thickness(0, 0, 8, 0);
            expandButton.VerticalAlignment = VerticalAlignment.Center;
            // 与卡片上的其它图标按钮一致：展开箭头只响应鼠标，点击后不要在文件夹区域留下系统虚线焦点框。
            expandButton.FocusVisualStyle = null;
            AutomationProperties.SetName(
                expandButton,
                AppText.T(DesktopLayoutTextKeys.ExpandCollapseAutomation));
            expandButton.Click += delegate { toggleCard(); };
            Grid.SetColumn(expandButton, 0);
            header.Children.Add(expandButton);

            // 文件夹和普通布局必须一眼能分开：以前两种卡片都画文件夹图标，
            // 用户根本看不出哪张能“装”别的布局。
            TextBlock folderIcon = new TextBlock
            {
                Text = isFolder ? "\uE8B7" : "\uE80A",
                FontFamily = AppTheme.IconFont,
                FontSize = 19,
                Foreground = isFolder ? AppTheme.Accent : AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            AutomationProperties.SetName(
                folderIcon,
                AppText.T(isFolder
                    ? DesktopLayoutTextKeys.FolderAutomation
                    : DesktopLayoutTextKeys.LayoutAutomation));
            Grid.SetColumn(folderIcon, 1);
            header.Children.Add(folderIcon);

            Grid nameHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            TextBlock nameBlock = new TextBlock
            {
                Text = layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameEditor = new TextBox
            {
                Text = layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Height = 28,
                Visibility = Visibility.Collapsed,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            nameHost.Children.Add(nameBlock);
            nameHost.Children.Add(nameEditor);
            StackPanel nameAndDetail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameAndDetail.Children.Add(nameHost);
            nameAndDetail.Children.Add(new TextBlock
            {
                Text = countText,
                FontSize = 11.5,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(nameAndDetail, 2);
            header.Children.Add(nameAndDetail);
            Action beginLayoutRename = InlineRename.Attach(nameBlock, nameEditor, 120, delegate(string newName)
            {
                return RenameLayoutWithUndo(layout, newName);
            }, header);
            // 双击文件夹图标/名字/详情仍然重命名；单击只选中，不展开。
            // 展开只由上面的箭头按钮负责，和「启动与恢复」「自启动应用」一致。
            nameBlock.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2)
                {
                    suppressLayoutSelectOnClickUp = true;
                    CancelPendingLayoutSelect();
                }
            };
            nameBlock.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                if (args.ClickCount == 2)
                {
                    CancelPendingLayoutSelect();
                    suppressLayoutSelectOnClickUp = false;
                    return;
                }
                ScheduleLayoutSelect(layout);
            };
            nameAndDetail.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2)
                {
                    args.Handled = true;
                    suppressLayoutSelectOnClickUp = true;
                    CancelPendingLayoutSelect();
                    if (beginLayoutRename != null) beginLayoutRename();
                }
            };
            nameAndDetail.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                ScheduleLayoutSelect(layout);
            };
            folderIcon.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2)
                {
                    args.Handled = true;
                    suppressLayoutSelectOnClickUp = true;
                    CancelPendingLayoutSelect();
                    if (beginLayoutRename != null) beginLayoutRename();
                }
            };
            folderIcon.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                ScheduleLayoutSelect(layout);
            };

            ActionBar actionBar = ActionBar.Create(
                isFolder ? ActionBarKind.LayoutFolderCard : ActionBarKind.LayoutCard);
            // 和「启动与恢复」「自启动应用」一样：≡ 只是长按拖动的手柄，不弹“上移/下移”菜单。
            Button move = actionBar.Add(
                ActionSlot.Move,
                ActionButtons.Move(DesktopLayoutToolTips.CardMove));
            move.Cursor = Cursors.SizeAll;
            move.Tag = "layout-drag-handle";
            move.Focusable = false;
            move.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                layoutCardDragPress = args.GetPosition(this);
                layoutCardDragCandidate = layout;
                layoutCardDragMouseButton = MouseButton.Left;   // ≡ 手柄始终左键直接拖
            };
            move.MouseLeftButtonUp += delegate { layoutCardDragCandidate = null; };
            move.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (args.LeftButton != MouseButtonState.Pressed || layoutCardDragCandidate == null)
                {
                    return;
                }
                Point now = args.GetPosition(this);
                if (Math.Abs(now.X - layoutCardDragPress.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(now.Y - layoutCardDragPress.Y) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }
                DesktopTopologyLayout candidate = layoutCardDragCandidate;
                layoutCardDragCandidate = null;
                BeginLayoutCardDrag(candidate, MouseButton.Left);
                args.Handled = true;
            };
            if (isFolder)
            {
                Button addChild = actionBar.Add(
                    ActionSlot.Add,
                    ActionButtons.Add(DesktopLayoutToolTips.AddChild));
                addChild.Click += delegate { CreateLayoutInFolder(layout, false); };
                Button addChildFolder = actionBar.Add(
                    ActionSlot.AddFolder,
                    ActionButtons.AddFolder(DesktopLayoutToolTips.AddChildFolder));
                addChildFolder.Click += delegate { CreateLayoutInFolder(layout, true); };
            }
            else
            {
                // 用“网格/布局”图标，避免和「启动与恢复」「自启动应用」的重命名铅笔图标撞脸。
                Button edit = actionBar.Add(
                    ActionSlot.Edit,
                    ActionButtons.Edit(DesktopLayoutToolTips.Edit));
                edit.Click += delegate { BeginLayoutEditAsync(layout); };
                Button save = actionBar.Add(
                    ActionSlot.Save,
                    ActionButtons.Save(DesktopLayoutToolTips.SaveHere));
                save.Click += delegate { SaveLayoutProfileAsync(layout); };
                Button restore = actionBar.Add(
                    ActionSlot.Restore,
                    ActionButtons.Restore(DesktopLayoutToolTips.RestoreHere));
                restore.Click += delegate { RestoreLayoutProfileAsync(layout); };
            }
            Button delete = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(isFolder
                    ? DesktopLayoutToolTips.DeleteFolder
                    : DesktopLayoutToolTips.DeleteHere));
            delete.Click += delegate { DeleteLayoutProfile(layout); };
            Grid.SetColumn(actionBar.Build(), 3);
            header.Children.Add(actionBar.Build());

            ContextMenu layoutMenu;
            if (isFolder)
            {
                layoutMenu = WorkspaceRestorer.UI.Shared.ContextMenus.Create(
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuNewChildLayout), delegate { CreateLayoutInFolder(layout, false); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuNewChildFolder), delegate { CreateLayoutInFolder(layout, true); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(expanded
                ? DesktopLayoutTextKeys.MenuCollapseFolder
                : DesktopLayoutTextKeys.MenuExpandFolder), delegate
                    {
                        if (expandedLayoutSignatures.Contains(expandKey)) { expandedLayoutSignatures.Remove(expandKey); }
                        else { expandedLayoutSignatures.Add(expandKey); }
                        RefreshDesktopLayoutCards();
                    }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(
                AppText.T(DesktopLayoutTextKeys.MenuRenameFolder),
                delegate { if (beginLayoutRename != null) beginLayoutRename(); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Separator(),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(
                AppText.T(DesktopLayoutTextKeys.MenuDeleteFolder),
                delegate { DeleteLayoutProfile(layout); }));
            }
            else
            {
                layoutMenu = WorkspaceRestorer.UI.Shared.ContextMenus.Create(
                    // 用户要求：右键菜单第一项必须是“编辑图标布局”。
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuEditLayout), delegate { BeginLayoutEditAsync(layout); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(expanded
                ? DesktopLayoutTextKeys.MenuCollapseLayout
                : DesktopLayoutTextKeys.MenuExpandLayout), delegate
                    {
                        if (expandedLayoutSignatures.Contains(expandKey)) { expandedLayoutSignatures.Remove(expandKey); }
                        else { expandedLayoutSignatures.Add(expandKey); }
                        RefreshDesktopLayoutCards();
                    }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuRestoreNow), delegate { RestoreLayoutProfileAsync(layout); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuSaveCurrentHere), delegate { SaveLayoutProfileAsync(layout); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuSetCurrent), delegate { SelectLayout(layout); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(AppText.T(DesktopLayoutTextKeys.MenuRenameLayout), delegate { if (beginLayoutRename != null) beginLayoutRename(); }),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Separator(),
                    WorkspaceRestorer.UI.Shared.ContextMenuItem.Item(
                AppText.T(DesktopLayoutTextKeys.MenuDeleteLayout),
                delegate { DeleteLayoutProfile(layout); }));
            }
            AddMoveIntoFolderMenu(layoutMenu, layout, allLayouts);
            WorkspaceRestorer.UI.Shared.ContextMenus.Attach(card, layoutMenu, header, nameBlock, nameAndDetail);
            header.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2 && !IsInsideButton(args.OriginalSource as DependencyObject))
                {
                    suppressLayoutSelectOnClickUp = true;
                    CancelPendingLayoutSelect();
                }
                ArmLayoutCardSurfaceDrag(layout, MouseButton.Left, args);
            };
            header.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmLayoutCardSurfaceDrag(layout, MouseButton.Right, args);
            };
            header.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                ContinueLayoutCardSurfaceDrag(layout, args);
            };
            header.PreviewMouseLeftButtonUp += delegate { layoutCardDragCandidate = null; };
            header.PreviewMouseRightButtonUp += delegate { layoutCardDragCandidate = null; };
            header.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (IsInsideButton(args.OriginalSource as DependencyObject))
                {
                    return;
                }
                // 和「启动与恢复」一致：单击延迟切换当前布局；双击改名时不切换。
                ScheduleLayoutSelect(layout);
            };
            headerBorder.Child = header;
            body.Children.Add(headerBorder);

            if (isFolder)
            {
                if (expanded && childLayouts.Count > 0)
                {
                    StackPanel childHost;
                    Grid childIndent = PlanFolderPresenter.CreateChildHost(out childHost);
                    childIndent.Margin = new Thickness(42, 4, 0, 2);
                    for (int childIndex = 0; childIndex < childLayouts.Count; childIndex++)
                    {
                        childHost.Children.Add(BuildLayoutCard(childLayouts[childIndex], allLayouts, depth + 1, activeId));
                    }
                    body.Children.Add(childIndent);
                }
                else if (expanded)
                {
                    body.Children.Add(UiFactory.SupportingText(
                        AppText.T(DesktopLayoutTextKeys.FolderEmptyHint)));
                }
            }
            else if (expanded)
            {
            Border preview = new Border
            {
                Background = AppTheme.PreviewSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0, 0, 10, 10),
                // 标题条自己有 10 像素内边距；预览区保持同样的左右/底部内缩，
                // 避免展开后从标题卡的两侧“溢出来”。
                Margin = new Thickness(10, 0, 10, 10),
                Padding = new Thickness(12),
                Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
            };
            StackPanel previewContent = new StackPanel();
            string previewPath = DesktopLayoutFolderStore.PreviewPath(layout);
            BitmapImage bitmap = TryLoadLayoutPreview(previewPath);
            if (bitmap != null)
            {
                Image image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 300,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                image.Cursor = Cursors.Hand;
                AccessibleToolTip.Attach(image, DesktopLayoutToolTips.DesktopScreenshot);
                image.MouseLeftButtonUp += delegate
                {
                    ShellOpenResult openResult = ShellOpener.OpenFile(previewPath);
                    if (openResult.WasSuppressed)
                    {
                        statusText.Text = AppText.T(DesktopLayoutTextKeys.TestModeOpen);
                    }
                    else if (!openResult.Success)
                    {
                        statusText.Text = AppText.F(DesktopLayoutTextKeys.OpenScreenshotFailedFormat, openResult.Error);
                    }
                };
                previewContent.Children.Add(image);
                previewContent.Children.Add(new TextBlock
                {
                    Text = AppText.F(
                    DesktopLayoutTextKeys.ScreenshotCaptionFormat,
                    Path.GetFileName(DesktopLayoutFolderStore.FolderPath(layout))),
                    FontSize = 11,
                    Foreground = AppTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                previewContent.Children.Add(new TextBlock
                {
                    Text = AppText.T(DesktopLayoutTextKeys.NoScreenshotHint),
                    FontSize = 12,
                    Foreground = AppTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            preview.Child = previewContent;
            body.Children.Add(preview);
            }
            card.Child = body;
            card.Tag = layout.Id ?? string.Empty;
            layoutCardVisuals.Register(layout.Id ?? string.Empty, headerBorder, card);
            layoutCardNames[layout.Id ?? string.Empty] = layout.Name
                ?? AppText.T(isFolder
                        ? DesktopLayoutTextKeys.FolderAutomation
                        : DesktopLayoutTextKeys.LayoutAutomation);
            if (isFolder)
            {
                layoutFolderIds.Add(layout.Id ?? string.Empty);
            }
            return card;
        }

        /// <summary>
        /// 右键菜单里的“移入布局文件夹”：不依赖拖动，给不想拖/拖不动的人一个明确入口。
        /// 只列合法目标（文件夹、不是自己、不是当前父级、不会形成环）。
        /// </summary>
        private void AddMoveIntoFolderMenu(
            ContextMenu menu,
            DesktopTopologyLayout layout,
            IList<DesktopTopologyLayout> allLayouts)
        {
            if (menu == null || layout == null || allLayouts == null)
            {
                return;
            }
            string moveIntoText = AppText.T(DesktopLayoutTextKeys.MenuMoveIntoFolder);
            MenuItem moveInto = new MenuItem { Header = moveIntoText };
            AutomationProperties.SetName(moveInto, moveIntoText);
            List<DesktopTopologyLayout> folders = new List<DesktopTopologyLayout>();
            for (int index = 0; index < allLayouts.Count; index++)
            {
                DesktopTopologyLayout folder = allLayouts[index];
                if (folder == null || !folder.IsFolder)
                {
                    continue;
                }
                if (string.Equals(folder.Id, layout.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(folder.Id, layout.ParentId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!PlanFolderTree.CanMove(allLayouts, layout, folder))
                {
                    continue;
                }
                folders.Add(folder);
            }
            folders.Sort(delegate(DesktopTopologyLayout left, DesktopTopologyLayout right)
            {
                return string.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty,
                    StringComparison.CurrentCultureIgnoreCase);
            });
            if (folders.Count == 0)
            {
                MenuItem none = new MenuItem { Header = AppText.T(DesktopLayoutTextKeys.MenuNoAvailableFolders) };
                none.IsEnabled = false;
                moveInto.Items.Add(none);
            }
            else
            {
                for (int index = 0; index < folders.Count; index++)
                {
                    DesktopTopologyLayout folder = folders[index];
                    MenuItem item = new MenuItem { Header = folder.Name ?? AppText.T(DesktopLayoutTextKeys.FolderAutomation) };
                    item.Click += delegate { MoveLayoutIntoWithUndo(layout, folder); };
                    moveInto.Items.Add(item);
                }
            }
            // 放在“编辑/新建子布局”之后，不抢原来的第一项。
            menu.Items.Insert(Math.Min(1, menu.Items.Count), moveInto);
        }

    }
}
