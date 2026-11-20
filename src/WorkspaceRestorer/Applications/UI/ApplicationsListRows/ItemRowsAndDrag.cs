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
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Interaction;
using WorkspaceRestorer.UI.Cards.Actions;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        private Border BuildApplicationPlanItemRow(ApplicationPlan plan, ApplicationPlanItem item, int ordinal)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock number = new TextBlock
            {
                Text = ordinal + ".",
                FontFamily = AppTheme.UiFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0)
            };
            Grid.SetColumn(number, 0);
            row.Children.Add(number);

            CheckBox enabled = new CheckBox
            {
                IsChecked = item.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            AccessibleToolTip.Attach(enabled, ApplicationsToolTips.ItemEnable);
            ApplicationPlanItem captured = item;
            // 【2026-10-17 修】这两个勾选框以前只改内存里的 `IsEnabled`，**没有写盘** ——
            // 用户取消勾选后关掉程序，下次打开又变成勾上的（「启动与恢复」那边的同名勾选框
            // 是走 `PushUndo` + 控制器持久化的，这边漏了）。现在每次改动都持久化。
            ApplicationPlan capturedPlan = plan;
            // 【2026-10-17】勾选/取消勾选也要能 Ctrl+Z（用户报“自启动应用无法撤销”的其中一项）。
            // 这里不能在 handler 里直接改完就完事：撤销＝把“勾选前”的整份快照放回去，
            // 所以先拍快照、再改、再 PushUndo，顺序不能反。
            enabled.Checked += delegate
            {
                ApplicationPlansSnapshot beforeEnable = CaptureApplicationPlansSnapshot();
                captured.IsEnabled = true;
                controller.SetApplicationPlanItemsOrder(capturedPlan, capturedPlan.Items);
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemEnabledFormat,
                    captured.DisplayName ?? captured.TargetPath ?? string.Empty);
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoItemEnabledFormat, ApplicationItemLabel(captured)),
                    beforeEnable);
            };
            enabled.Unchecked += delegate
            {
                ApplicationPlansSnapshot beforeDisable = CaptureApplicationPlansSnapshot();
                captured.IsEnabled = false;
                controller.SetApplicationPlanItemsOrder(capturedPlan, capturedPlan.Items);
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemDisabledFormat,
                    captured.DisplayName ?? captured.TargetPath ?? string.Empty);
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoItemDisabledFormat, ApplicationItemLabel(captured)),
                    beforeDisable);
            };
            Grid.SetColumn(enabled, 1);
            row.Children.Add(enabled);

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
            // 名字这一段单独留个引用：悬停高亮要把它变主题色 + 加下划线（与「启动与恢复」一样）。
            TextBlock itemName = new TextBlock
            {
                Text = item.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemUnnamed),
                FontFamily = AppTheme.UiFont,
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            text.Children.Add(itemName);
            text.Children.Add(new TextBlock
            {
                Text = item.TargetPath ?? string.Empty,
                FontFamily = AppTheme.UiFont,
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(text, 2);
            row.Children.Add(text);

            // 右侧动作区统一由 Cards/Actions 排：≡ → 最小化（不适用补空位）→ 删除。
            ActionBar actionBar = ActionBar.Create(ActionBarKind.ItemRow);
            Button grip = actionBar.Add(
                ActionSlot.Move,
                ActionButtons.DragHandle(ApplicationsToolTips.ItemGrip));
            bool canDrag = true;

            if (CanToggleApplicationPlanItemMinimized(captured))
            {
                bool initiallyMinimized = WindowMinimizeState.IsMinimized(captured.WindowPlacement);
                Button minimize = null;
                minimize = WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.Create(
                    initiallyMinimized,
                    delegate
                    {
                        bool wasMinimized = WindowMinimizeState.IsMinimized(captured.WindowPlacement);
                        bool target = !wasMinimized;
                        ApplicationPlansSnapshot beforeMinimize = CaptureApplicationPlansSnapshot();
                        if (!controller.SetApplicationPlanItemWindowMinimized(plan, captured, target))
                        {
                            statusText.Text = AppText.T(ApplicationsTextKeys.ItemGoneMinimize);
                            return;
                        }
                        WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.ApplyState(
                            minimize, target);
                        statusText.Text = target
                            ? AppText.F(ApplicationsTextKeys.ItemMinimizedFormat, ApplicationItemLabel(captured))
                            : AppText.F(ApplicationsTextKeys.ItemUnminimizedFormat, ApplicationItemLabel(captured));
                        PushApplicationPlansUndo(
                            AppText.T(target
                                ? ApplicationsTextKeys.UndoItemMinimized
                                : ApplicationsTextKeys.UndoItemUnminimized),
                            beforeMinimize);
                    });
                actionBar.Add(ActionSlot.Minimize, minimize);
            }

            // 先把外框建出来（拖动需要在 grip 的处理器里引用它），最后再把 row 装进去。
            Border itemRowBorder = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 5, 4, 5)
            };
            // 拖动排序：与「启动与恢复」同一套手感（机制在公共引擎 `Shared/UI/Cards/Drag/`）。
            // 手柄本身按下/移动当然能拖；**整行**也允许起拖，但只限“落在手柄那一小段”时
            // ——这样既好抓（用户第 41～42 条要求两边手感一致），又不会把勾选框、删除按钮的点击抢走。
            // 按下只“武装”：真正起拖由窗口级设备指针钩子判断（指针一动、超过阈值就起拖，
            // 不管那时指针已经跑到哪个控件上面——合成鼠标一次跳 40 像素时也不会漏）。
            grip.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (canDrag)
                {
                    ArmApplicationRowDrag(plan, captured, itemRowBorder, MouseButton.Left);
                }
            };
            itemRowBorder.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmApplicationItemSurfaceDrag(
                    canDrag, plan, captured, itemRowBorder, grip, row, MouseButton.Left, args);
            };
            itemRowBorder.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmApplicationItemSurfaceDrag(
                    canDrag, plan, captured, itemRowBorder, grip, row, MouseButton.Right, args);
            };
            // 行自己的移动也接一下（引擎已经在动时，这里让落点/方框即时跟上）。
            itemRowBorder.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (canDrag)
                {
                    ContinueApplicationRowDrag();
                }
            };

            Button removeItem = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(ApplicationsToolTips.ItemDelete));
            removeItem.Click += delegate
            {
                ApplicationPlanItem before = captured;
                int index = plan.Items == null ? -1 : plan.Items.IndexOf(before);
                // 先把“删除前”的快照记下来，删完再进撤销栈（顺序反了就撤成删除后的样子了）。
                ApplicationPlansSnapshot beforeDelete = CaptureApplicationPlansSnapshot();
                if (!controller.RemoveApplicationPlanItem(plan, captured))
                {
                    statusText.Text = AppText.T(ApplicationsTextKeys.ItemGone);
                    return;
                }
                RefreshApplicationPlanList();
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemDeletedFormat,
                    ApplicationItemLabel(captured));
                // 与「启动与恢复」一样可以 Ctrl+Z 撤销（现在走公共撤销栈 + 整体快照，绝不漏项）。
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoItemDeletedFormat, ApplicationItemLabel(before, index)),
                    beforeDelete);
            };
            Grid.SetColumn(actionBar.Build(), 3);
            row.Children.Add(actionBar.Build());

            // 外框已经在上面建好了（拖动处理器要引用它），这里才把 row 装进去。
            itemRowBorder.Child = row;
            // 【2026-10-18 用户要求】点击整行＝打开这一项；交互层在公共区
            // `Shared/UI/Cards/Interaction/ClickToJump.cs`（与「启动与恢复」同一份，悬停提示同一套骨架）。
            // 这里只给“跳去哪”：目前就是打开这一项；以后要做“跳到已开着的窗口”时只换这个 Action。
            WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.AttachRow(
                itemRowBorder,
                text,
                WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.BuildRowToolTip(
                    BuildApplicationBrowserAwareJumpHint(plan, captured)),
                ConsumeApplicationRowDragJustEnded,
                ApplicationItemLabel(captured),
                delegate { JumpToApplicationItem(plan, captured); },
                ShowApplicationDragHint,
                // ≡ 手柄只给拖动用：点一下它不算“点了整行”（否则轻点手柄会顺手把项目打开）。
                WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.IgnoreInside(grip),
                // 悬停交互也用公共件（用户 2026-10-18：“鼠标放过去，要像‘启动与恢复’那样有显示”）：
                // 整行 #FAFBFC → #E7F1F8、名字变主题色并加下划线；拖动中不亮。
                itemRowBorder,
                itemName,
                AppTheme.RowSurface,
                AppTheme.RowHoverSurface,
                AppTheme.TextPrimary,
                AppTheme.Accent,
                delegate { return ApplicationDragInProgress; });
            // 登记到“拖动落点表”（公共拖动引擎按窗口坐标反查落点行时用这张表）。
            applicationRowDragRows.Add(new WorkspaceRestorer.UI.Cards.Drag.DragMoveRow
            {
                Payload = captured,
                Owner = plan,
                Element = itemRowBorder,
                Label = ApplicationItemLabel(captured)
            });
            // 项目行同样挂上右键菜单：2026-11-01 起统一走 CreateApplicationItemContextMenu
            // （网页行保留“设置网页归属/打开此网页”，所有行都多一项“移动到方案…”）。
            itemRowBorder.ContextMenu = CreateApplicationItemContextMenu(plan, captured);
            return itemRowBorder;
        }

        private Point applicationItemDragStart;
        private DispatcherTimer pendingApplicationSelectTimer;
        private bool suppressApplicationSelectOnClickUp;

        /// <summary>
        /// 整行/≡ 手柄按下：整行要满足设置里的“拖动前缀 + 鼠标键”，
        /// 而 ≡ 手柄那一小段仍然左键直接拖（不受前缀限制）。
        /// </summary>
        private void ArmApplicationItemSurfaceDrag(
            bool canDrag,
            ApplicationPlan plan,
            ApplicationPlanItem item,
            Border itemRowBorder,
            Button grip,
            Grid row,
            MouseButton button,
            MouseButtonEventArgs args)
        {
            if (!canDrag || args == null)
            {
                return;
            }
            if (IsActionControl(args.OriginalSource as DependencyObject))
            {
                return;
            }
            bool gesture = DragGestureSettings.MatchesMouseButton(button)
                && DragGestureSettings.MatchesPrefix();
            bool gripZone = button == MouseButton.Left
                && IsInGripZone(grip, row, args.GetPosition(row));
            if (!gesture && !gripZone)
            {
                return;
            }
            ArmApplicationRowDrag(plan, item, itemRowBorder, button);
        }

        /// <summary>卡片标题区按下：满足“拖动前缀 + 鼠标键”才武装整卡拖动（≡ 手柄仍左键直接拖）。</summary>
        private void ArmApplicationCardSurfaceDrag(
            ApplicationPlan plan,
            FrameworkElement header,
            MouseButton button,
            MouseButtonEventArgs args)
        {
            if (args == null
                || IsActionControl(args.OriginalSource as DependencyObject)
                || !DragGestureSettings.MatchesMouseButton(button)
                || !DragGestureSettings.MatchesPrefix())
            {
                return;
            }
            ArmApplicationCardDrag(plan, header, button);
        }

        /// <summary>是不是一次“按住左键并拖过了系统阈值”的拖动动作。</summary>
        private bool IsGripDragGesture(MouseEventArgs args)
        {
            if (args.LeftButton != MouseButtonState.Pressed || double.IsNaN(applicationItemDragStart.X))
            {
                return false;
            }
            Point now = args.GetPosition(this);
            return Math.Abs(now.X - applicationItemDragStart.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(now.Y - applicationItemDragStart.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }

        /// <summary>指针是不是落在“≡ 手柄”那一小段（横向给 20 像素余量，纵向整行都算）。</summary>
        private static bool IsInGripZone(FrameworkElement grip, FrameworkElement row, Point pointInRow)
        {
            if (grip == null || row == null || row.ActualWidth <= 0.0)
            {
                return false;
            }
            try
            {
                Point gripOrigin = grip.TranslatePoint(new Point(0.0, 0.0), row);
                double left = gripOrigin.X - 10.0;
                double right = gripOrigin.X + (grip.ActualWidth > 0.0 ? grip.ActualWidth : 20.0) + 10.0;
                return pointInRow.X >= left && pointInRow.X <= right;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>把方案里的一项移到最前（-1）或最后（1）。</summary>
        private void MoveApplicationItemToEdge(ApplicationPlan plan, ApplicationPlanItem item, int direction)
        {
            if (plan == null || plan.Items == null || item == null || plan.Items.Count < 2)
            {
                return;
            }
            // 【2026-10-17】调整顺序也要能 Ctrl+Z（以前这里一条撤销记录都不记）。
            ApplicationPlansSnapshot beforeMove = CaptureApplicationPlansSnapshot();
            List<ApplicationPlanItem> ordered = new List<ApplicationPlanItem>(plan.Items);
            if (!ordered.Remove(item))
            {
                return;
            }
            if (direction < 0)
            {
                ordered.Insert(0, item);
            }
            else
            {
                ordered.Add(item);
            }
            controller.SetApplicationPlanItemsOrder(plan, ordered);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                direction < 0
                    ? ApplicationsTextKeys.ItemMovedFirstFormat
                    : ApplicationsTextKeys.ItemMovedLastFormat,
                item.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback));
            PushApplicationPlansUndo(
                AppText.F(
                    direction < 0
                        ? ApplicationsTextKeys.UndoMovedFirstFormat
                        : ApplicationsTextKeys.UndoMovedLastFormat,
                    ApplicationItemLabel(item)),
                beforeMove);
        }

        /// <summary>
        /// 把小项按拖放结果重新排序并落盘（由公共拖动引擎的落点回调调用）。
        /// moving 是被拖的那一项、rom 是它原来的方案（同方案调顺序时两者相同）。
        /// </summary>
        private void ApplyApplicationItemReorder(ApplicationPlan from, ApplicationPlanItem moving, ApplicationPlan plan, ApplicationPlanItem target, bool after)
        {
            if (plan == null || plan.Items == null || target == null || moving == null || from == null)
            {
                return;
            }
            if (object.ReferenceEquals(moving, target))
            {
                return;
            }
            // 子项只能在所属父组内部调顺序；拖到别的父组时按“并入/独立成新组”处理。
            if (!string.IsNullOrWhiteSpace(moving.ParentId))
            {
                ApplyApplicationChildDrop(from, plan, moving, target, after);
                return;
            }
            // 父行代表整个浏览器窗口/文件夹：拖动父行时整组一起移动；子项不允许脱离父组。
            if (moving.IsGroupParent)
            {
                if (target.ParentId != null)
                {
                    ApplicationPlanItem targetParent = plan.Items.Find(delegate(ApplicationPlanItem item)
                    {
                        return item != null && string.Equals(item.Id, target.ParentId, StringComparison.OrdinalIgnoreCase);
                    });
                    if (targetParent != null)
                    {
                        target = targetParent;
                    }
                }
                if (object.ReferenceEquals(moving, target))
                {
                    return;
                }
                if (!object.ReferenceEquals(from, plan))
                {
                    if (from == null || from.Items == null)
                    {
                        return;
                    }
                    // 【2026-11-01】整组跨方案改走控制器里的**同一份规则**
                    // （StartupRestore/Core/PlanItemGroupMove + ApplicationPlanItemMoveAdapter）：
                    // 父项 + 全部子项成块搬过去，一次落盘；这里不再自己拼 crossGroup。
                    ApplicationPlansSnapshot beforeCrossGroup = CaptureApplicationPlansSnapshot();
                    if (controller.MoveApplicationItemBetweenPlans(from, plan, moving, target, after))
                    {
                        RefreshApplicationPlanList();
                        statusText.Text = AppText.F(
                            ApplicationsTextKeys.StatusMovedGroupToPlanFormat,
                            moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback),
                            plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                        PushApplicationPlansUndo(
                            AppText.F(
                                ApplicationsTextKeys.UndoMovedGroupToPlanFormat,
                                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                                ApplicationItemLabel(moving)),
                            beforeCrossGroup);
                    }
                    else
                    {
                        statusText.Text = AppText.T(ApplicationsTextKeys.StatusAlreadyInTarget);
                    }
                    return;
                }
                ApplicationPlansSnapshot beforeGroupReorder = CaptureApplicationPlansSnapshot();
                List<ApplicationPlanItem> group = new List<ApplicationPlanItem>();
                group.Add(moving);
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem child = plan.Items[index];
                    if (child != null
                        && string.Equals(child.ParentId, moving.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        group.Add(child);
                    }
                }
                List<ApplicationPlanItem> orderedGroup = new List<ApplicationPlanItem>(plan.Items);
                for (int index = 0; index < group.Count; index++)
                {
                    orderedGroup.Remove(group[index]);
                }
                int targetIndex = orderedGroup.IndexOf(target);
                if (targetIndex < 0)
                {
                    orderedGroup.AddRange(group);
                }
                else
                {
                    int insertIndex = after ? targetIndex + 1 : targetIndex;
                    orderedGroup.InsertRange(insertIndex, group);
                }
                controller.SetApplicationPlanItemsOrder(plan, orderedGroup);
                RefreshApplicationPlanList();
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemGroupReorderedFormat,
                    moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback));
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoGroupReorderedFormat, ApplicationItemLabel(moving)),
                    beforeGroupReorder);
                return;
            }
            // 用户第 89 条：“我拖动 a 方案的其中一个启动项时，可以加到 b 方案内，
            // 那么 a 方案就没有这个启动项。”→ 来源方案与落点方案不同就是**跨方案搬运**；
            // 插在目标行前面还是后面，仍然由“落在上半还是下半”决定（与原来的拖拽一致）。
            if (!object.ReferenceEquals(from, plan))
            {
                // 【2026-10-17】跨方案搬移也要能 Ctrl+Z：撤销＝两个方案都回到搬之前。
                ApplicationPlansSnapshot beforeCrossMove = CaptureApplicationPlansSnapshot();
                if (controller.MoveApplicationItemBetweenPlans(from, plan, moving, target, after))
                {
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusMovedItemToPlanFormat,
                        moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemThisFallback),
                        plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                    PushApplicationPlansUndo(
                        AppText.F(
                            ApplicationsTextKeys.UndoMovedItemToPlanFormat,
                            ApplicationItemLabel(moving),
                            plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback)),
                        beforeCrossMove);
                }
                else
                {
                    statusText.Text = AppText.T(ApplicationsTextKeys.StatusAlreadyInTarget);
                }
                RefreshApplicationPlanList();
                return;
            }
            // 【2026-10-17】同方案里拖动排序也要能 Ctrl+Z。
            ApplicationPlansSnapshot beforeReorder = CaptureApplicationPlansSnapshot();
            List<ApplicationPlanItem> ordered = new List<ApplicationPlanItem>(plan.Items);
            if (!ordered.Remove(moving))
            {
                return;
            }
            int normalTargetIndex = ordered.IndexOf(target);
            if (normalTargetIndex < 0)
            {
                ordered.Add(moving);
            }
            else
            {
                ordered.Insert(after ? normalTargetIndex + 1 : normalTargetIndex, moving);
            }
            controller.SetApplicationPlanItemsOrder(plan, ordered);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.ItemReorderedFormat,
                moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback));
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoItemReorderedFormat, ApplicationItemLabel(moving)),
                beforeReorder);
        }

        /// <summary>
        /// 网页/文件夹子项的拖放：同父组内排序；拖到兼容的另一个父组就并进去；
        /// 拖到空白处或不同类型的位置就新建一个父组，保证子项始终有归属。
        /// </summary>
        private bool ApplyApplicationChildDrop(
            ApplicationPlan from,
            ApplicationPlan plan,
            ApplicationPlanItem moving,
            ApplicationPlanItem target,
            bool after)
        {
            if (plan == null || plan.Items == null || moving == null || target == null)
            {
                return false;
            }
            if (!object.ReferenceEquals(from, plan))
            {
                // 用户 2026-11-01：“还是继续显示禁止符号，不用提示。”
                // 子项只能整组走；跨方案时 FindApplicationRowAt 已经不把它当落点，
                // 这里只兜底返回（不写状态栏提示）。
                return false;
            }
            ApplicationPlanItem movingParent = FindApplicationParent(plan, moving);
            ApplicationPlanItem targetParent = target.IsGroupParent
                ? target
                : FindApplicationParent(plan, target);
            ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();

            if (movingParent != null && object.ReferenceEquals(movingParent, targetParent))
            {
                List<ApplicationPlanItem> childOrder = new List<ApplicationPlanItem>(plan.Items);
                if (!childOrder.Remove(moving))
                {
                    return false;
                }
                int childTargetIndex = childOrder.IndexOf(target);
                if (childTargetIndex < 0)
                {
                    return false;
                }
                childOrder.Insert(after ? childTargetIndex + 1 : childTargetIndex, moving);
                controller.SetApplicationPlanItemsOrder(plan, childOrder);
                RefreshApplicationPlanList();
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemChildReorderedFormat,
                    moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback));
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoChildReorderedFormat, ApplicationItemLabel(moving)), before);
                return true;
            }

            bool joined = false;
            if (moving.IsBrowserPage && targetParent != null && targetParent.IsBrowserWindow)
            {
                string movingBrowser = moving.BrowserExecutablePath ?? moving.BrowserGroupKey;
                string targetBrowser = targetParent.BrowserExecutablePath ?? targetParent.BrowserGroupKey;
                joined = string.IsNullOrWhiteSpace(movingBrowser)
                    || string.IsNullOrWhiteSpace(targetBrowser)
                    || string.Equals(movingBrowser, targetBrowser, StringComparison.OrdinalIgnoreCase);
            }
            else if (!moving.IsBrowserPage && targetParent != null
                && (targetParent.IsFolderGroup || ApplicationParentHasFolderChildren(plan, targetParent)))
            {
                joined = true;
            }

            List<ApplicationPlanItem> ordered = new List<ApplicationPlanItem>(plan.Items);
            ordered.Remove(moving);
            if (joined)
            {
                int at = ordered.IndexOf(target);
                ordered.Insert(at < 0 ? ordered.Count : (after ? at + 1 : at), moving);
                moving.ParentId = targetParent.Id;
                if (moving.IsBrowserPage)
                {
                    moving.BrowserExecutablePath = targetParent.BrowserExecutablePath;
                    moving.BrowserName = targetParent.BrowserName;
                    moving.BrowserGroupKey = targetParent.BrowserGroupKey;
                    moving.BrowserWindowIndex = targetParent.BrowserWindowIndex;
                    moving.BrowserTabIndex = CountApplicationChildren(plan, targetParent);
                    moving.BrowserTabIdentityCaptured = false;
                    moving.IsActiveBrowserTab = false;
                    moving.BrowserTabs = BrowserTabFingerprint.CloneList(targetParent.BrowserTabs);
                    moving.BrowserWindowTitleSnapshot = targetParent.BrowserWindowTitleSnapshot;
                    moving.BrowserBindingKind = BrowserPageBindingKind.UserBound;
                    moving.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
                }
                controller.SetApplicationPlanItemsOrder(plan, ordered);
                RefreshApplicationPlanList();
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.ItemMergedFormat,
                    moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback),
                    targetParent.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemParentFallback));
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoItemMergedFormat, ApplicationItemLabel(moving)), before);
                return true;
            }

            ApplicationPlanItem newParent = BuildDetachedApplicationGroup(plan, moving);
            if (newParent == null)
            {
                return false;
            }
            int targetIndex = ordered.IndexOf(target);
            if (targetIndex < 0)
            {
                ordered.Add(newParent);
                ordered.Add(moving);
            }
            else
            {
                int insertAt = after ? targetIndex + 1 : targetIndex;
                ordered.Insert(insertAt, newParent);
                ordered.Insert(insertAt + 1, moving);
            }
            moving.ParentId = newParent.Id;
            if (moving.IsBrowserPage)
            {
                moving.BrowserExecutablePath = newParent.BrowserExecutablePath;
                moving.BrowserName = newParent.BrowserName;
                moving.BrowserGroupKey = newParent.BrowserGroupKey;
                moving.BrowserWindowIndex = newParent.BrowserWindowIndex;
                moving.BrowserTabIndex = 0;
                moving.BrowserTabIdentityCaptured = false;
                moving.IsActiveBrowserTab = true;
                moving.BrowserTabs = BrowserTabFingerprint.CloneList(newParent.BrowserTabs);
                moving.BrowserWindowTitleSnapshot = newParent.BrowserWindowTitleSnapshot;
                moving.BrowserBindingKind = BrowserPageBindingKind.UserBound;
                moving.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
            }
            controller.SetApplicationPlanItemsOrder(plan, ordered);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.ItemDetachedFormat,
                moving.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemFallback),
                newParent.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemNewGroupFallback));
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoChildDetachedFormat, ApplicationItemLabel(moving)), before);
            return true;
        }

        private static ApplicationPlanItem FindApplicationParent(ApplicationPlan plan, ApplicationPlanItem item)
        {
            if (plan == null || item == null || string.IsNullOrWhiteSpace(item.ParentId))
            {
                return null;
            }
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem candidate = plan.Items[index];
                if (candidate != null
                    && string.Equals(candidate.Id, item.ParentId, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool ApplicationParentHasFolderChildren(ApplicationPlan plan, ApplicationPlanItem parent)
        {
            if (plan == null || parent == null)
            {
                return false;
            }
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem child = plan.Items[index];
                if (child != null
                    && string.Equals(child.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase)
                    && child.Kind == SessionItemKind.ExplorerFolder)
                {
                    return true;
                }
            }
            return false;
        }

        private static int CountApplicationChildren(ApplicationPlan plan, ApplicationPlanItem parent)
        {
            int count = 0;
            if (plan != null && parent != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem child = plan.Items[index];
                    if (child != null
                        && string.Equals(child.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static ApplicationPlanItem BuildDetachedApplicationGroup(
            ApplicationPlan plan,
            ApplicationPlanItem moving)
        {
            int ordinal = 1;
            if (plan != null && plan.Items != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item != null && item.IsGroupParent)
                    {
                        ordinal++;
                    }
                }
            }
            if (moving.IsBrowserPage)
            {
                string browserName = string.IsNullOrWhiteSpace(moving.BrowserName)
                    ? AppText.T(ApplicationsTextKeys.BrowserFallback)
                    : moving.BrowserName.Trim();
                string executable = moving.BrowserExecutablePath;
                return new ApplicationPlanItem
                {
                    IsGroupParent = true,
                    IsBrowserWindow = true,
                    IsEnabled = true,
                    Kind = SessionItemKind.Application,
                    DisplayName = browserName + " (" + ordinal + ")",
                    TargetPath = executable,
                    BrowserExecutablePath = executable,
                    BrowserName = browserName,
                    BrowserGroupKey = Guid.NewGuid().ToString("N"),
                    BrowserWindowIndex = ordinal - 1,
                    WindowPlacement = moving.WindowPlacement == null
                        ? null
                        : moving.WindowPlacement.Clone()
                };
            }
            return new ApplicationPlanItem
            {
                IsGroupParent = true,
                IsFolderGroup = true,
                IsEnabled = true,
                Kind = SessionItemKind.Application,
                DisplayName = AppText.T(ApplicationsTextKeys.GroupFolderFallback) + " (" + ordinal + ")",
                TargetPath = moving.TargetPath
            };
        }

    }
}
