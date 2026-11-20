using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Cards.Actions;
using WorkspaceRestorer.UI.Cards.Surface;
using WorkspaceRestorer.UI.Cards.Interaction;
using WorkspaceRestorer.UI.Cards.Folders;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 重画「自启动应用」的方案卡片（用户要求：要和「启动与恢复」那样是卡片，
        /// 卡片上有“N 个自启动项目 · 时间”，右侧有移动/添加/删除按钮，能展开看到项目）。
        /// </summary>
        private void RefreshApplicationPlanList()
        {
            CancelPendingApplicationPlanSelect();
            if (applicationPlanHost == null)
            {
                return;
            }
            applicationPlanHost.Children.Clear();
            // 【2026-10-17】重建卡片会把旧的行/卡片元素全部换掉，所以拖动引擎的两张落点表要清空
            // （每一行、每张卡片都会在下文重新登记）。不清的话旧元素会带着**旧的方案/项目对象**
            // 留在表里；撤销改成“按快照整体替换”之后那些就是已废弃的副本，
            // 拖动迟早会把东西落进不存在的数据里。
            applicationRowDragRows.Clear();
            applicationCardElements.Clear();
            // 顺带把还挂在旧行上的插入线收掉（重建后再画会落到已废弃的元素里）。
            ClearDesktopInsertLine();
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (desktopPlanScroll != null)
            {
                desktopPlanScroll.Visibility = plans == null || plans.Count == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            if (plans == null || plans.Count == 0)
            {
                applicationPlanHost.Children.Add(UiFactory.SupportingText(
                    // 【2026-10-19 用户要求】工具栏的「新建空白方案」按钮删掉了（右键菜单保留）。
                    // 这行提示必须跟着改，不能再指着一个不存在的按钮。
                    AppText.T(ApplicationsTextKeys.EmptyPlansHint)));
                return;
            }
            ApplicationPlan active = controller.ActiveApplicationPlan;
            IList<ApplicationPlan> roots = controller.RootApplicationPlans;
            for (int index = 0; index < roots.Count; index++)
            {
                ApplicationPlan plan = roots[index];
                if (plan != null)
                {
                    applicationPlanHost.Children.Add(BuildApplicationPlanCard(
                        plan,
                        active != null && string.Equals(active.Id, plan.Id, StringComparison.OrdinalIgnoreCase),
                        0));
                }
            }
        }


        /// <summary>单击延迟 260ms：双击改名时不要先把这张方案切成当前方案（对齐启动与恢复）。</summary>
        private void ScheduleApplicationPlanSelect(ApplicationPlan plan)
        {
            CancelPendingApplicationPlanSelect();
            if (suppressApplicationSelectOnClickUp)
            {
                suppressApplicationSelectOnClickUp = false;
                return;
            }
            if (plan == null)
            {
                return;
            }
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            pendingApplicationSelectTimer = timer;
            timer.Tick += delegate
            {
                timer.Stop();
                if (object.ReferenceEquals(pendingApplicationSelectTimer, timer))
                {
                    pendingApplicationSelectTimer = null;
                }
                SelectApplicationPlan(plan);
            };
            timer.Start();
        }

        private void CancelPendingApplicationPlanSelect()
        {
            if (pendingApplicationSelectTimer != null)
            {
                pendingApplicationSelectTimer.Stop();
                pendingApplicationSelectTimer = null;
            }
        }

        private void SelectApplicationPlan(ApplicationPlan plan)
        {
            ApplicationPlan previousActive = controller.ActiveApplicationPlan;
            if (previousActive != null && string.Equals(previousActive.Id, plan.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            IList<ApplicationPlan> ancestors = PlanFolderTree.Ancestors(controller.ApplicationPlans, plan);
            for (int index = 0; index < ancestors.Count; index++)
            {
                if (ancestors[index] != null && !string.IsNullOrWhiteSpace(ancestors[index].Id))
                {
                    applicationPlanExpansionStates[ancestors[index].Id] = true;
                    controller.SetApplicationPlanExpanded(ancestors[index].Id, true);
                }
            }
            controller.SetActiveApplicationPlan(plan);
            RefreshApplicationPlanList();
            PushUndo(
                AppText.T(ApplicationsTextKeys.DragSwitchTip),
                delegate
                {
                    if (previousActive != null)
                    {
                        controller.SetActiveApplicationPlan(previousActive);
                    }
                    RefreshApplicationPlanList();
                },
                delegate
                {
                    controller.SetActiveApplicationPlan(plan);
                    RefreshApplicationPlanList();
                });
        }


        private void MergeApplicationPlan(ApplicationPlan source, ApplicationPlan target)
        {
            if (source == null || target == null || object.ReferenceEquals(source, target))
            {
                return;
            }
            string sourceName = source.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
            string targetName = target.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
            string oldParentId = source.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, source);
            if (!controller.MoveApplicationPlanInto(source, target))
            {
                return;
            }
            string newParentId = source.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, source);
            ApplicationPlan sourceForUndo = source;
            applicationPlanExpansionStates[target.Id] = true;
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.StatusMovedIntoFormat, sourceName, targetName);
            PushUndo(
                AppText.F(ApplicationsTextKeys.UndoMovedIntoFormat, sourceName, targetName),
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(sourceForUndo, oldParentId, oldIndex);
                    RefreshApplicationPlanList();
                },
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(sourceForUndo, newParentId, newIndex);
                    RefreshApplicationPlanList();
                });
        }

        private Border BuildApplicationPlanCard(ApplicationPlan plan, bool active)
        {
            return BuildApplicationPlanCard(plan, active, 0);
        }

        private Border BuildApplicationPlanCard(ApplicationPlan plan, bool active, int planDepth)
        {
            bool expanded;
            if (string.IsNullOrWhiteSpace(plan.Id) || !applicationPlanExpansionStates.TryGetValue(plan.Id, out expanded))
            {
                // 用户第 81 条：“自启动应用，默认也像是启动与恢复的一样，默认为收起这个方案，
                // 而不是展开，然后，如果用户操作了展开……就按照用户的来。”
                // → 默认收起；用户展开过的方案 Id 记在设置里（`ExpandedApplicationPlanIds`），
                //   所以**关掉程序再打开也还是用户上次的样子**。
                expanded = controller.IsApplicationPlanExpanded(plan.Id);
                if (!string.IsNullOrWhiteSpace(plan.Id))
                {
                    applicationPlanExpansionStates[plan.Id] = expanded;
                }
            }

            StackPanel container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            // 与「启动与恢复」共用同一套卡片外框（颜色/粗细/圆角/悬停）。
            // MinHeight 仍保留这边的 72，以和启动方案卡片等高。
            Border header = WorkspaceRestorer.UI.Cards.Surface.CardSurface.Create(active, true, 72.0);
            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel expandedHost = new StackPanel
            {
                Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
            };
            StackPanel itemHost = new StackPanel
            {
                Margin = new Thickness(42, 4, 0, 0)
            };
            List<ApplicationPlan> childPlans = new List<ApplicationPlan>(controller.ChildApplicationPlans(plan));
            // 【性能】收起的卡片只建卡片头；子方案和项目行在第一次展开时才建。
            bool contentBuilt = false;
            Action ensureExpandedContent = null;

            Button expandButton = UiFactory.IconButton(expanded ? "\uE70D" : "\uE76C", ApplicationsToolTips.PlanExpand);
            expandButton.Width = 30;
            expandButton.Height = 30;
            // 【2026-10-18】展开/收起抽成 Action：卡片上的箭头和右键菜单里的“展开/收起此方案的清单”
            // 走的是同一套逻辑（状态记进设置、落盘、刷新箭头和清单）。
            Action toggleApplicationPlanExpansion = delegate
            {
                expanded = !expanded;
                if (!string.IsNullOrWhiteSpace(plan.Id))
                {
                    applicationPlanExpansionStates[plan.Id] = expanded;
                    // 同时写进设置并落盘：**关掉程序再打开仍然按用户的来**（用户第 81 条）。
                    controller.SetApplicationPlanExpanded(plan.Id, expanded);
                }
                if (expanded)
                {
                    if (ensureExpandedContent != null)
                    {
                        ensureExpandedContent();
                    }
                }
                expandButton.Content = UiFactory.IconContent(expanded ? "\uE70D" : "\uE76C");
                expandButton.FontFamily = AppTheme.IconFont;
                expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            };
            expandButton.Click += delegate { toggleApplicationPlanExpansion(); };
            Grid.SetColumn(expandButton, 0);
            headerGrid.Children.Add(expandButton);

            // 用户要求（2026-10-10 02:41）：“图标不要书本的样式，还是按照‘启动与恢复’那种文件夹的样式。”
            // 「启动与恢复」的方案卡片用的就是同一个字形 \uE8B7（蓝色文件夹），这里照抄，两页保持一致。
            // 【2026-10-19 用户要求】标题用 Grid（图标 + 星号列），不再用横向 StackPanel：
            // 窗口一窄，横向 StackPanel 不会把文字截断，会直接顶到右边的移动/并入/恢复/删除按钮下面。
            Grid title = new Grid { VerticalAlignment = VerticalAlignment.Center };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock planIconBlock = new TextBlock
            {
                Text = UiFactory.IconGlyph("\uE8B7"),
                FontFamily = AppTheme.IconFont,
                FontSize = 20,
                Foreground = AppTheme.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(planIconBlock, 0);
            title.Children.Add(planIconBlock);
            StackPanel titleText = new StackPanel { MinWidth = 0 };
            Grid.SetColumn(titleText, 1);
            title.Children.Add(titleText);
            TextBlock planNameBlock = new TextBlock
            {
                Text = plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent
            };
            // 用户要求（2026-10-10 01:41）：自启动应用的方案名也要能**双击就地改名**
            // （改名行为已抽到公共区域 `Shared/UI/Cards/Interaction/InlineRename.cs`，以后“图标位置保存”也用它）。
            TextBox planNameEditor = new TextBox
            {
                Text = plan.Name ?? string.Empty,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 200,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid nameHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            nameHost.Children.Add(planNameBlock);
            nameHost.Children.Add(planNameEditor);
            Action beginApplicationRename = InlineRename.Attach(planNameBlock, planNameEditor, 120, delegate(string newName)
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    statusText.Text = AppText.T(ApplicationsTextKeys.StatusNameRequired);
                    return false;
                }
                // 【2026-10-17】改名也要能 Ctrl+Z（以前只有「追加当前时间」进撤销栈，双击改名没进）。
                ApplicationPlansSnapshot beforeRename = CaptureApplicationPlansSnapshot();
                string oldName = plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
                if (controller.RenameApplicationPlan(plan, newName))
                {
                    statusText.Text = AppText.F(ApplicationsTextKeys.StatusRenamedFormat, newName);
                    RefreshApplicationPlanList();
                    PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoRenameFormat, oldName, newName),
                beforeRename);
                    return true;
                }
                return false;
            }, header, header);
            // 用户第 48 条（“双击命名也要向启动与恢复统一”）：提示文字与启动与恢复用同一句话，
            // 并且**不能抢走控件的无障碍名字**（只当帮助文本），否则自检/读屏都找不到方案名。
            AccessibleToolTip.AttachDescription(planNameBlock, ApplicationsToolTips.PlanName);
            titleText.Children.Add(nameHost);
            string savedAt = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                plan.UpdatedAtUtc, AppText.CurrentLanguageTag);
            TextBlock planCountBlock = new TextBlock
            {
                Text = (childPlans.Count == 0 && !plan.IsFolder
                        ? AppText.F(ApplicationsTextKeys.ItemCountFormat, plan.Items == null ? 0 : plan.Items.Count)
                        : PlanFolderPresenter.BuildCountText(
                            childPlans.Count,
                            plan.Items == null ? 0 : plan.Items.Count,
                            plan.IsFolder))
                    + (savedAt.Length == 0 ? string.Empty : " · " + savedAt),
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.TextSecondary
            };
            AccessibleToolTip.AttachDescription(
                planCountBlock,
                ApplicationsToolTips.PlanSavedAt(savedAt));
            titleText.Children.Add(planCountBlock);
            titleText.Cursor = Cursors.Hand;
            title.Cursor = Cursors.Hand;
            Grid.SetColumn(title, 1);
            headerGrid.Children.Add(title);

            // 顺序统一由 Cards/Actions 决定：移动 ≡ → 并入 → 恢复 → 删除。
            ActionBar actionBar = ActionBar.Create(ActionBarKind.ApplicationCard);
            Button move = actionBar.Add(
                ActionSlot.Move,
                ActionButtons.Move(ApplicationsToolTips.PlanMove));
            move.Cursor = Cursors.SizeAll;
            Button addItem = actionBar.Add(
                ActionSlot.Add,
                ActionButtons.Add(ApplicationsToolTips.PlanCopyToStartup));
            Button restoreThis = actionBar.Add(
                ActionSlot.Restore,
                ActionButtons.Restore(ApplicationsToolTips.PlanRestoreThis));
            Button remove = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(ApplicationsToolTips.PlanDelete));
            if (plan.IsFolder && childPlans.Count == 0)
            {
                restoreThis.IsEnabled = false;
            }
            addItem.Click += delegate
            {
                // 点这张卡片的 ＋ ＝ 把这张方案整份复制成一份新工作方案；
                // 先把这张卡切成当前方案，用户随后在「启动与恢复」里看到的就是它。
                controller.SetActiveApplicationPlan(plan);
                CopyApplicationPlanToStartupWorkspace(plan);
                RefreshApplicationPlanList();
            };
            restoreThis.Click += delegate
            {
                controller.SetActiveApplicationPlan(plan);
                RefreshApplicationPlanList();
                RestoreApplicationPlanNow();
            };
            remove.Click += delegate { DeleteApplicationPlanWithConfirm(plan); };
            // “长按移动”手柄：与卡片标题同一套拖动（按下→超过阈值才起拖→松手按落点并入/调顺序）。
            // 【2026-10-17】拖动机制换成公共引擎（`Shared/UI/Cards/Drag/`，从「启动与恢复」复制）：
            // 旧实现没有看门狗/光标保持，松手事件一丢就永远停在“移动中”。
            move.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                // 【2026-10-17 深夜 用户报“移动时居然会这样”】以前这里传的是 `move`（≡ 手柄，只有 30 像素宽）：
                // 浮动方框的左边锚在**手柄**上、宽度也按手柄算，于是按住 ≡ 拖的时候，
                // 那块白框会跑到卡片**最右边**、还压着卡片右侧的按钮（截图里就是这样）。
                // 传 `header`（整条标题行）就对了：方框贴卡片左边、宽度＝卡片宽度（与「启动与恢复」一致）。
                ArmApplicationCardDrag(plan, header, MouseButton.Left);
            };
            move.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                EndApplicationCardDrag();
            };
            Grid.SetColumn(actionBar.Build(), 2);
            headerGrid.Children.Add(actionBar.Build());
            header.Child = headerGrid;

            header.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (IsActionControl(args.OriginalSource as DependencyObject))
                {
                    return;
                }
                // 【拖动结束不能当成点击】拖完一张卡片，这次松手就不能再去“切换当前方案”
                //（与「启动与恢复」的 nativeItemDragJustEnded 同一个道理）。
                if (applicationCardDragJustEnded || ApplicationDragInProgress)
                {
                    applicationCardDragJustEnded = false;
                    return;
                }
                ScheduleApplicationPlanSelect(plan);
                args.Handled = true;
            };

            header.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2 && !IsActionControl(args.OriginalSource as DependencyObject))
                {
                    suppressApplicationSelectOnClickUp = true;
                    CancelPendingApplicationPlanSelect();
                }
                ArmApplicationCardSurfaceDrag(plan, header, MouseButton.Left, args);
                applicationCardDragJustEnded = false;
            };
            header.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmApplicationCardSurfaceDrag(plan, header, MouseButton.Right, args);
                applicationCardDragJustEnded = false;
            };
            header.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                EndApplicationCardDrag();
            };
            container.Children.Add(header);
            // 展开后的项目行必须被卡片区域裁掉：否则会有一小块白色圆角矩形露在浅蓝卡片下面，
            // 看着像“多出来一个方块”（截图里抓到过，用户第 47 条说的“怪怪的”就有这一块）。
            container.ClipToBounds = true;
            container.SizeChanged += delegate
            {
                container.Clip = new System.Windows.Media.RectangleGeometry(
                    new Rect(0.0, 0.0, container.ActualWidth, container.ActualHeight));
            };
            ensureExpandedContent = delegate
            {
                if (contentBuilt)
                {
                    return;
                }
                contentBuilt = true;
                if (childPlans.Count > 0)
                {
                    StackPanel childHost;
                    Grid childIndent = PlanFolderPresenter.CreateChildHost(out childHost);
                    childIndent.Margin = new Thickness(42, 4, 0, 2);
                    for (int childIndex = 0; childIndex < childPlans.Count; childIndex++)
                    {
                        ApplicationPlan child = childPlans[childIndex];
                        childHost.Children.Add(BuildApplicationPlanCard(
                            child,
                            controller.ActiveApplicationPlan != null
                                && string.Equals(controller.ActiveApplicationPlan.Id, child.Id, StringComparison.OrdinalIgnoreCase),
                            planDepth + 1));
                    }
                    expandedHost.Children.Add(childIndent);
                }
                expandedHost.Children.Add(itemHost);

            if (plan.Items == null || plan.Items.Count == 0)
            {
                if (childPlans.Count == 0)
                {
                    itemHost.Children.Add(UiFactory.SupportingText(plan.IsFolder
                        ? AppText.T(ApplicationsTextKeys.FolderEmptyHint)
                        : AppText.T(ApplicationsTextKeys.PlanEmptyHint)));
                }
            }
            else
            {
                List<ApplicationBrowserPlanGroup> browserGroups =
                    ApplicationBrowserPlanGrouping.Collect(plan.Items);
                Dictionary<string, ApplicationBrowserPlanGroup> browserGroupByKey =
                    new Dictionary<string, ApplicationBrowserPlanGroup>(StringComparer.OrdinalIgnoreCase);
                for (int groupIndex = 0; groupIndex < browserGroups.Count; groupIndex++)
                {
                    browserGroupByKey[browserGroups[groupIndex].Key] = browserGroups[groupIndex];
                }
                Dictionary<string, ApplicationPlanItem> itemById =
                    new Dictionary<string, ApplicationPlanItem>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<ApplicationPlanItem>> childrenByParent =
                    new Dictionary<string, List<ApplicationPlanItem>>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item == null || string.IsNullOrWhiteSpace(item.Id))
                    {
                        continue;
                    }
                    itemById[item.Id] = item;
                }
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item == null || string.IsNullOrWhiteSpace(item.ParentId))
                    {
                        continue;
                    }
                    List<ApplicationPlanItem> children;
                    if (!childrenByParent.TryGetValue(item.ParentId, out children))
                    {
                        children = new List<ApplicationPlanItem>();
                        childrenByParent[item.ParentId] = children;
                    }
                    children.Add(item);
                }
                HashSet<string> renderedItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> renderedBrowserGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, StackPanel> browserGroupChildren =
                    new Dictionary<string, StackPanel>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, StackPanel> browserGroupChildStacks =
                    new Dictionary<string, StackPanel>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> browserChildOrdinals =
                    new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Action<ApplicationPlanItem, int, int, Panel> renderItem = null;
                renderItem = delegate(ApplicationPlanItem item, int depth, int ordinal, Panel host)
                {
                    if (host == null || item == null || renderedItems.Contains(item.Id ?? string.Empty))
                    {
                        return;
                    }
                    List<ApplicationPlanItem> children;
                    if (!ApplicationBrowserPlanAdapter.IsBrowserPage(item)
                        && childrenByParent.TryGetValue(item.Id ?? string.Empty, out children)
                        && children.Count > 0)
                    {
                        renderedItems.Add(item.Id);
                        string stateKey = "application-group|" + (plan.Id ?? string.Empty)
                            + "|" + (item.Id ?? string.Empty);
                        bool groupExpanded;
                        if (!applicationGroupExpansionStates.TryGetValue(stateKey, out groupExpanded))
                        {
                            groupExpanded = true;
                            applicationGroupExpansionStates[stateKey] = true;
                        }
                        StackPanel groupChildren = new StackPanel
                        {
                            Visibility = groupExpanded ? Visibility.Visible : Visibility.Collapsed
                        };
                        // 和启动与恢复一样：左边用固定缩进列，子行右边缘仍和父行对齐，
                        // 不靠给每一行加 Margin（那样越深越窄）。
                        Grid groupIndent = new Grid();
                        groupIndent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                        groupIndent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        StackPanel groupChildStack = new StackPanel();
                        Grid.SetColumn(groupChildStack, 1);
                        groupIndent.Children.Add(groupChildStack);
                        groupChildren.Children.Add(groupIndent);
                        if (item.IsBrowserWindow)
                        {
                            host.Children.Add(BuildApplicationBrowserGroupHeader(
                                plan, item, children, ordinal, groupChildren, groupExpanded, stateKey));
                            renderedBrowserGroups.Add(ApplicationBrowserPlanGrouping.KeyOf(item));
                        }
                        else
                        {
                            host.Children.Add(BuildApplicationFolderGroupHeader(
                                plan, item, children, ordinal, groupChildren, groupExpanded, stateKey));
                        }
                        host.Children.Add(groupChildren);
                        int childOrdinal = 0;
                        for (int childIndex = 0; childIndex < children.Count; childIndex++)
                        {
                            childOrdinal++;
                            renderItem(children[childIndex], depth + 1, childOrdinal, groupChildStack);
                        }
                        return;
                    }

                    if (ApplicationBrowserPlanAdapter.IsBrowserPage(item))
                    {
                        string groupKey = ApplicationBrowserPlanGrouping.KeyOf(item);
                        ApplicationPlanItem explicitParent = null;
                        bool hasExplicitParent = !string.IsNullOrWhiteSpace(item.ParentId)
                            && itemById.TryGetValue(item.ParentId, out explicitParent);
                        if (hasExplicitParent && explicitParent != null)
                        {
                            if (!renderedItems.Contains(explicitParent.Id ?? string.Empty))
                            {
                                renderItem(explicitParent, depth, ordinal, host);
                                return;
                            }
                            // 父项已经作为分组画出来了：这一行直接放进父项的子行里。
                            // 不能继续走下面的“合成分组”，否则会出现
                            // Microsoft Edge (2) 里面再套一个 Microsoft Edge (2)。
                            Border groupedPage = BuildApplicationPlanItemRow(plan, item, ordinal);
                            renderedItems.Add(item.Id ?? string.Empty);
                            host.Children.Add(groupedPage);
                            return;
                        }
                        StackPanel browserChildren;
                        if (!browserGroupChildren.TryGetValue(groupKey, out browserChildren))
                        {
                            string stateKey = "application-synthetic|" + (plan.Id ?? string.Empty)
                                + "|" + groupKey;
                            bool browserExpanded;
                            if (!applicationGroupExpansionStates.TryGetValue(stateKey, out browserExpanded))
                            {
                                browserExpanded = true;
                                applicationGroupExpansionStates[stateKey] = true;
                            }
                            browserChildren = new StackPanel
                            {
                                Visibility = browserExpanded ? Visibility.Visible : Visibility.Collapsed
                            };
                            Grid browserIndent = new Grid();
                            browserIndent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                            browserIndent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            StackPanel browserChildStack = new StackPanel();
                            Grid.SetColumn(browserChildStack, 1);
                            browserIndent.Children.Add(browserChildStack);
                            browserChildren.Children.Add(browserIndent);
                            browserGroupChildStacks[groupKey] = browserChildStack;
                            ApplicationBrowserPlanGroup group;
                            if (browserGroupByKey.TryGetValue(groupKey, out group))
                            {
                                host.Children.Add(BuildApplicationBrowserGroupHeader(
                                    plan, group, ordinal, browserChildren, browserExpanded, stateKey));
                            }
                            host.Children.Add(browserChildren);
                            browserGroupChildren[groupKey] = browserChildren;
                            renderedBrowserGroups.Add(groupKey);
                        }
                        int browserOrdinal;
                        browserChildOrdinals.TryGetValue(groupKey, out browserOrdinal);
                        browserOrdinal++;
                        browserChildOrdinals[groupKey] = browserOrdinal;
                        Border browserChild = BuildApplicationPlanItemRow(plan, item, browserOrdinal);
                        StackPanel browserChildHost;
                        if (!browserGroupChildStacks.TryGetValue(groupKey, out browserChildHost))
                        {
                            browserChildHost = browserChildren;
                        }
                        renderedItems.Add(item.Id ?? string.Empty);
                        browserChildHost.Children.Add(browserChild);
                        return;
                    }

                    Border row = BuildApplicationPlanItemRow(plan, item, ordinal);
                    renderedItems.Add(item.Id ?? string.Empty);
                    host.Children.Add(row);
                };
                int topOrdinal = 0;
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item == null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(item.ParentId)
                        || !itemById.ContainsKey(item.ParentId))
                    {
                        topOrdinal++;
                        renderItem(item, 0, topOrdinal, itemHost);
                    }
                }
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem remaining = plan.Items[index];
                    if (remaining != null && !renderedItems.Contains(remaining.Id ?? string.Empty))
                    {
                        topOrdinal++;
                        renderItem(remaining, 0, topOrdinal, itemHost);
                    }
                }
            }
            };
            if (expanded)
            {
                ensureExpandedContent();
            }
            expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            container.Children.Add(expandedHost);
            // Tag = 这份方案：点击归属仍按外层块判断；拖动槽位另用 header 登记。
            Border cardBorder = new Border
            {
                Child = container,
                Tag = plan,
                Background = System.Windows.Media.Brushes.Transparent
            };
            cardBorder.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (IsActionControl(args.OriginalSource as DependencyObject)
                    || applicationCardDragJustEnded
                    || ApplicationDragInProgress)
                {
                    applicationCardDragJustEnded = false;
                    return;
                }
                object owner = PlanFolderPresenter.FindOwningCardTag(args.OriginalSource as DependencyObject);
                if (object.ReferenceEquals(owner, plan))
                {
                    ScheduleApplicationPlanSelect(plan);
                }
            };
            // 【2026-10-18 用户反馈】“自启动应用右键后，无法添加网址”。
            // 以前只有下方空白处挂了右键菜单，点在方案卡片上什么都没有。
            // 现在卡片上也挂同一份菜单（新建/粘贴/挑文件夹/扫描桌面），右键点哪儿都能用。
            // 【2026-10-18 用户要求】卡片右键要把“所有选项”弹出来，和「启动与恢复」的卡片菜单一一对应。
            cardBorder.ContextMenu = CreateApplicationsPlanContextMenu(
                plan, expanded, toggleApplicationPlanExpansion, beginApplicationRename);
            // Header 算槽位/目标框；Block 算禁止区/腾空间。两者必须分开登记。
            if (!string.IsNullOrWhiteSpace(plan.Id))
            {
                applicationCardElements.Register(plan.Id, header, cardBorder);
            }
            return cardBorder;
        }

    }
}
