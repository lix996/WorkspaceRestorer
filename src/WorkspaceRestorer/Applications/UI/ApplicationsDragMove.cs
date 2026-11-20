using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.Core.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Drag;

namespace WorkspaceRestorer.UI
{
    // 工作区：自启动应用 —— **拖动移动**。
    //
    // 拖动机制本身在公共区域：`Shared/UI/Cards/Drag/Engine/DragMoveSession.cs`。
    // 2026-11-01 起「启动与恢复」也改成走同一个引擎；三个工作区只保留各自的业务回调，
    // 不再各存一套“启动/跟手/放置条/看门狗”的流程。
    // 这个文件只做“自启动应用 ↔ 引擎”的接线：谁是行、谁是卡片、落点之后数据怎么改。
    //
    // 【修掉的 bug（2026-10-17 查出来的）】
    //  · 旧代码 `EnsureApplicationItemNativeHooks()` **从来没被调用** → 项目行起拖后捕获了鼠标、
    //    显示了浮动方框，但**没有任何地方收移动与松手**：方框一直亮着、光标一直是“移动”、
    //    行一直抓着鼠标，拖动永远结束不了（用户说的“移动时有bug”）。
    //  · 没有看门狗：截屏工具一抢鼠标，连正常松手都收不到，只能一直卡在“移动中”。
    //  · 光标只在移动那一帧设一次 → 一停手就被系统刷回箭头。
    //  · 上/下两块放置条在项目行拖动时**既不亮也不滚**（驱动它的代码只在已经废弃的 OLE 那条路上）。
    public sealed partial class MainWindow
    {
        /// <summary>项目行拖动引擎（行模式：细插入线 + 放置条 + 自动滚动）。</summary>
        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession applicationRowDragSession;

        /// <summary>方案卡片拖动引擎（卡片模式：边缘＝调顺序，中间＝并入）。</summary>
        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession applicationCardDragSession;

        /// <summary>正在拖的项目行（数据）。</summary>
        private ApplicationPlanItem applicationRowDragItem;
        /// <summary>它原来属于哪份方案。</summary>
        private ApplicationPlan applicationRowDragPlan;
        /// <summary>正在拖的卡片（方案）。</summary>
        private ApplicationPlan applicationCardDragPlan;

        /// <summary>“刚刚拖完一张卡片”的标记：别把松手当成“点击标题切换当前方案”。</summary>
        private bool applicationCardDragJustEnded;

        /// <summary>拖动落点表（项目行）：每次重建卡片时重新登记。</summary>
        private readonly List<WorkspaceRestorer.UI.Cards.Drag.DragMoveRow> applicationRowDragRows =
            new List<WorkspaceRestorer.UI.Cards.Drag.DragMoveRow>();

        /// <summary>拖动落点表（卡片）：方案 Id → 标题条 Header / 外层整块 Block。</summary>
        private readonly WorkspaceRestorer.UI.Cards.Drag.DragCardElementRegistry applicationCardElements =
            new WorkspaceRestorer.UI.Cards.Drag.DragCardElementRegistry();

        /// <summary>被拖方案自己的后代卡片（窗口坐标）：这些区域是禁止落点。</summary>
        private readonly List<Rect> applicationCardForbiddenRects = new List<Rect>();

        /// <summary>拖动中吗（页面用来挡掉“松手当点击”、快捷键里的撤销也要让路）。</summary>
        private bool ApplicationDragInProgress
        {
            get
            {
                return (applicationRowDragSession != null && applicationRowDragSession.IsActive)
                    || (applicationCardDragSession != null && applicationCardDragSession.IsActive);
            }
        }

        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession RowDragEngine()
        {
            if (applicationRowDragSession != null)
            {
                return applicationRowDragSession;
            }
            WorkspaceRestorer.UI.Cards.Drag.DragMoveHost host =
                new WorkspaceRestorer.UI.Cards.Drag.DragMoveHost();
            host.Window = this;
            host.Layer = dragInsertionLayer;
            host.Ghost = dragGhost;
            host.List = desktopPlanScroll;
            host.TopPad = desktopTopPad;
            host.BottomPad = desktopBottomPad;
            host.Mode = WorkspaceRestorer.UI.Cards.Drag.DragMoveMode.InsertRow;
            host.LogTag = "[拖动]";
            host.DescribeMoving = delegate
            {
                return ApplicationItemLabel(applicationRowDragItem);
            };
            host.FindRowAt = FindApplicationRowAt;
            host.ApplyDrop = delegate(WorkspaceRestorer.UI.Cards.Drag.DragMoveRow target, bool insertAfter)
            {
                ApplyApplicationRowDrop(target, insertAfter);
            };
            host.ApplyEdgeDrop = delegate(int direction)
            {
                ApplicationPlan plan = applicationRowDragPlan;
                ApplicationPlanItem item = applicationRowDragItem;
                applicationRowDragItem = null;
                applicationRowDragPlan = null;
                if (plan != null && item != null)
                {
                    MoveApplicationItemToEdge(plan, item, direction);
                }
            };
            host.OnCancelled = delegate
            {
                applicationRowDragItem = null;
                applicationRowDragPlan = null;
            };
            host.OnFinished = delegate
            {
                // 不管放下了、落在空档里（＝没改动）、还是自动取消，收尾都要把“正在拖什么”清掉，
                // 否则松手后第一次点击会被“刚拖完”那条判断吞掉（用户看到的就是“点了没反应”）。
                applicationRowDragItem = null;
                applicationRowDragPlan = null;
                applicationRowDragPressActive = false;
                applicationRowDragArmedItem = null;
                applicationRowDragArmedPlan = null;
                applicationRowDragArmedRow = null;
                // 【2026-10-18】这一行现在“点一下就打开”，所以拖完那一下要标记住，
                // 否则松手会被当成点击，顺手把项目打开了（与「启动与恢复」的 nativeItemDragJustEnded 同一个道理）。
                // 【2026-10-24】改成时间戳：原来越在空白处松手，这一行收不到 MouseUp，
                // 标记会一直留到下一次点击，把下一次正常点击吞掉。现在只在松手后很短的窗口内有效。
                applicationRowDragEndedUtc = DateTime.UtcNow;
                SyncApplicationDragStatusDescription();
            };
            host.ShowHint = ShowApplicationDragHint;
            // 【2026-10-18 用户：“悬停提示要一致”】落点提示交给页面写，口径与「启动与恢复」的
            // `ShowDropHint` 一字不差：报到“第几位（在“X”之前/之后）”，而不是只说“插到 X 之前”。
            host.DescribeRowHint = DescribeApplicationRowDropHint;
            // 2026-11-01：拖到“别的方案卡片”上＝放进那份方案（收起/空方案＝末尾）。
            // 卡片落点用同一个公共引擎，页面只提供几何与最近项。
            host.DescribeCard = delegate(string id)
            {
                ApplicationPlan plan = FindApplicationPlanById(id);
                return plan == null ? "?" : (plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            };
            host.FindRowCardAt = FindApplicationRowCardAt;
            host.DescribeRowCardHint = DescribeApplicationRowCardDropHint;
            host.ApplyRowCardDrop = ApplyApplicationRowCardDrop;
            // 行拖到方案卡片：目标框框标题条；卡片归属和禁止区才用外层整块。
            host.FindCardElement = delegate(string id)
            {
                return applicationCardElements.FindHeader(id);
            };
            host.ScrollSpeedFactor = delegate { return controller.Settings.DragScrollSpeed; };
            applicationRowDragSession = new WorkspaceRestorer.UI.Cards.Drag.DragMoveSession(host);
            return applicationRowDragSession;
        }

        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession CardDragEngine()
        {
            if (applicationCardDragSession != null)
            {
                return applicationCardDragSession;
            }
            WorkspaceRestorer.UI.Cards.Drag.DragMoveHost host =
                new WorkspaceRestorer.UI.Cards.Drag.DragMoveHost();
            host.Window = this;
            host.Layer = dragInsertionLayer;
            host.Ghost = dragGhost;
            host.List = desktopPlanScroll;
            host.TopPad = desktopTopPad;
            host.BottomPad = desktopBottomPad;
            host.CardHost = applicationPlanHost;
            host.Mode = WorkspaceRestorer.UI.Cards.Drag.DragMoveMode.CardZones;
            host.LogTag = "[卡片拖动]";
            host.DescribeMoving = delegate
            {
                return applicationCardDragPlan == null
                ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                : (applicationCardDragPlan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            };
            host.GetCardSlots = CaptureApplicationCardSlots;
            host.FindCardElement = delegate(string id)
            {
                return applicationCardElements.FindHeader(id);
            };
            host.FindCardShiftElement = delegate(string id)
            {
                return applicationCardElements.FindBlock(id);
            };
            host.DescribeCard = delegate(string id)
            {
                ApplicationPlan plan = FindApplicationPlanById(id);
                return plan == null ? "?" : (plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            };
            host.IsDropPointForbidden = IsApplicationCardDropForbidden;
            host.ApplyCardDrop = ApplyApplicationCardDrop;
            host.ApplyEdgeDrop = delegate(int direction)
            {
                ApplicationPlan plan = applicationCardDragPlan;
                applicationCardDragPlan = null;
                if (plan != null)
                {
                    MoveApplicationCardToEdge(plan, direction);
                }
            };
            host.OnCancelled = delegate
            {
                applicationCardDragPlan = null;
                applicationCardDragJustEnded = true;
                SyncApplicationDragStatusDescription();
            };
            host.OnFinished = delegate
            {
                applicationCardDragPlan = null;
                // 松手之后那一下**不能再当成“点击标题切换方案”**（否则拖完会顺手把当前方案换掉）。
                applicationCardDragJustEnded = true;
                applicationCardDragPressActive = false;
                applicationCardDragArmedPlan = null;
                applicationCardDragArmedElement = null;
                SyncApplicationDragStatusDescription();
            };
            host.ShowHint = ShowApplicationDragHint;
            host.ScrollSpeedFactor = delegate { return controller.Settings.DragScrollSpeed; };
            applicationCardDragSession = new WorkspaceRestorer.UI.Cards.Drag.DragMoveSession(host);
            return applicationCardDragSession;
        }

        // ================================================================ 项目行拖动

        // 【为什么要“先武装、再起拖”】
        // 真实拖动时，按下之后指针会先动几个像素；如果第一步就跨出了那 31 像素宽的手柄，
        // 手柄/行就再也收不到 MouseMove，拖动永远起不来（合成鼠标一次跳 40 像素时必然如此）。
        // 所以按下时只“武装”（记下要拖的是哪一项、按在哪），之后只要有**设备级**的指针移动
        // 超过系统阈值就起拖——不管那一刻指针停在哪个控件上。窗口级钩子只挂一次。
        private ApplicationPlan applicationRowDragArmedPlan;
        private ApplicationPlanItem applicationRowDragArmedItem;
        private Border applicationRowDragArmedRow;
        private Point applicationRowDragPressPoint;
        private bool applicationRowDragPressActive;
        private MouseButton applicationRowDragMouseButton = MouseButton.Left;

        private ApplicationPlan applicationCardDragArmedPlan;
        private FrameworkElement applicationCardDragArmedElement;
        private Point applicationCardDragPressPoint;
        private bool applicationCardDragPressActive;
        private MouseButton applicationCardDragMouseButton = MouseButton.Left;

        private bool applicationRawPointerHooked;

        /// <summary>按在手柄 / 拖动前缀整行上：先武装（真正起拖看下一次指针移动是否超过阈值）。</summary>
        private void ArmApplicationRowDrag(
            ApplicationPlan plan,
            ApplicationPlanItem item,
            Border row,
            MouseButton mouseButton)
        {
            if (plan == null || item == null || row == null || ApplicationDragInProgress)
            {
                return;
            }
            applicationRowDragArmedPlan = plan;
            applicationRowDragArmedItem = item;
            applicationRowDragArmedRow = row;
            applicationRowDragMouseButton = mouseButton;
            applicationRowDragPressPoint = GetApplicationPointer();
            applicationRowDragPressActive = true;
            EnsureApplicationRawPointerHook();
        }

        /// <summary>按在卡片标题/“长按移动”上：先武装。</summary>
        private void ArmApplicationCardDrag(
            ApplicationPlan plan,
            FrameworkElement card,
            MouseButton mouseButton)
        {
            if (plan == null || card == null || ApplicationDragInProgress)
            {
                return;
            }
            applicationCardDragArmedPlan = plan;
            applicationCardDragArmedElement = card;
            applicationCardDragMouseButton = mouseButton;
            applicationCardDragPressPoint = GetApplicationPointer();
            applicationCardDragPressActive = true;
            EnsureApplicationRawPointerHook();
        }

        /// <summary>
        /// 窗口级“设备级指针移动”：武装过一次之后，指针一动就在**任何控件上面**判断是否该起拖。
        /// 用 `InputManager.PreProcessInput`：它不受鼠标捕获影响（与公共拖动引擎同一层）。
        /// </summary>
        private void EnsureApplicationRawPointerHook()
        {
            if (applicationRawPointerHooked)
            {
                return;
            }
            applicationRawPointerHooked = true;
            System.Windows.Input.InputManager.Current.PreProcessInput +=
                delegate(object sender, System.Windows.Input.PreProcessInputEventArgs e)
                {
                    if (e == null || e.StagingItem == null)
                    {
                        return;
                    }
                    System.Windows.Input.MouseEventArgs args =
                        e.StagingItem.Input as System.Windows.Input.MouseEventArgs;
                    if (args == null)
                    {
                        return;
                    }
                    if (args.RoutedEvent != System.Windows.Input.Mouse.PreviewMouseMoveEvent
                        && args.RoutedEvent != System.Windows.Input.Mouse.MouseMoveEvent)
                    {
                        if (args.RoutedEvent == System.Windows.Input.Mouse.PreviewMouseUpEvent
                            || args.RoutedEvent == System.Windows.Input.Mouse.MouseUpEvent)
                        {
                            // 【2026-10-17】设备级松手：**任何地方**松手都把“武装”清掉。
                            // 以前只靠卡片标题/手柄自己的 MouseUp 清，指针一离开那个控件就清不掉；
                            // 于是“按在卡片上点了两下、又没拖成”之后，再按别的地方会莫名其妙起拖。
                            OnApplicationRawPointerUp();
                        }
                        return;
                    }
                    OnApplicationRawPointerMove();
                };
        }

        /// <summary>设备级松手：清掉“武装”状态（还没起拖就松手＝那只是一次点击）。</summary>
        private void OnApplicationRawPointerUp()
        {
            applicationRowDragPressActive = false;
            applicationCardDragPressActive = false;
            applicationRowDragArmedPlan = null;
            applicationRowDragArmedItem = null;
            applicationRowDragArmedRow = null;
            applicationCardDragArmedPlan = null;
            applicationCardDragArmedElement = null;
        }

        private void OnApplicationRawPointerMove()
        {
            if (!applicationRowDragPressActive && !applicationCardDragPressActive)
            {
                return;
            }
            MouseButton armedButton = applicationRowDragPressActive
                ? applicationRowDragMouseButton
                : applicationCardDragMouseButton;
            if (!DragGestureSettings.IsMouseButtonDownPhysical(armedButton))
            {
                // 松手了却还没起拖 → 这只是一次点击，清掉武装状态（别让它留到下一次）。
                applicationRowDragPressActive = false;
                applicationCardDragPressActive = false;
                return;
            }
            Point now = GetApplicationPointer();
            if (applicationRowDragPressActive)
            {
                if (!ExceedsDragThreshold(now, applicationRowDragPressPoint))
                {
                    return;
                }
                ApplicationPlan plan = applicationRowDragArmedPlan;
                ApplicationPlanItem item = applicationRowDragArmedItem;
                Border row = applicationRowDragArmedRow;
                MouseButton mouseButton = applicationRowDragMouseButton;
                applicationRowDragPressActive = false;
                applicationRowDragArmedPlan = null;
                applicationRowDragArmedItem = null;
                applicationRowDragArmedRow = null;
                BeginApplicationRowDrag(plan, item, row, mouseButton);
                return;
            }
            if (applicationCardDragPressActive)
            {
                if (!ExceedsDragThreshold(now, applicationCardDragPressPoint))
                {
                    return;
                }
                ApplicationPlan plan = applicationCardDragArmedPlan;
                FrameworkElement card = applicationCardDragArmedElement;
                MouseButton mouseButton = applicationCardDragMouseButton;
                applicationCardDragPressActive = false;
                applicationCardDragArmedPlan = null;
                applicationCardDragArmedElement = null;
                if (plan != null && card != null)
                {
                    applicationCardDragPlan = plan;
                    applicationCardDragJustEnded = false;
                    CardDragEngine().Start(
                        card,
                        plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                        mouseButton);
                }
            }
        }

        private static bool ExceedsDragThreshold(Point now, Point press)
        {
            return Math.Abs(now.X - press.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(now.Y - press.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }

        /// <summary>当前指针的窗口内坐标（设备级取点，150% 缩放下也对）。</summary>
        private Point GetApplicationPointer()
        {
            WorkspaceRestorer.Services.NativeMethods.NativePoint cursor;
            if (WorkspaceRestorer.Services.NativeMethods.GetCursorPos(out cursor))
            {
                try
                {
                    return PointFromScreen(new Point(cursor.X, cursor.Y));
                }
                catch
                {
                }
            }
            return new Point(0.0, 0.0);
        }

        /// <summary>真正起拖：交给公共拖动引擎。</summary>
        private void BeginApplicationRowDrag(
            ApplicationPlan plan,
            ApplicationPlanItem item,
            Border row,
            MouseButton mouseButton)
        {
            if (plan == null || item == null || row == null || ApplicationDragInProgress)
            {
                return;
            }
            applicationRowDragPlan = plan;
            applicationRowDragItem = item;
            RowDragEngine().Start(row, ApplicationItemLabel(item), mouseButton);
        }

        /// <summary>拖动中（行自己的 MouseMove 也调一次，保证行内移动即时）。</summary>
        private void ContinueApplicationRowDrag()
        {
            if (applicationRowDragSession != null && applicationRowDragSession.IsActive)
            {
                applicationRowDragSession.Continue();
            }
        }
        /// <summary>按窗口坐标找落点行：取**面积最小**的包含指针的行（最内层），并跳过被拖的那一行。</summary>
        private WorkspaceRestorer.UI.Cards.Drag.DragMoveRow FindApplicationRowAt(Point windowPoint)
        {
            ApplicationPlanItem moving = applicationRowDragItem;
            // 用户 2026-11-01：子项只能整组走。拖子项时，别的方案的行不再算落点，
            // 光标显示禁止符号；不写“请拖整组”的提示。
            bool movingChild = moving != null && !string.IsNullOrWhiteSpace(moving.ParentId);
            WorkspaceRestorer.UI.Cards.Drag.DragMoveRow best = null;
            double bestArea = double.MaxValue;
            for (int index = 0; index < applicationRowDragRows.Count; index++)
            {
                WorkspaceRestorer.UI.Cards.Drag.DragMoveRow candidate = applicationRowDragRows[index];
                if (candidate == null || candidate.Element == null || !candidate.Element.IsVisible)
                {
                    continue;
                }
                if (movingChild && !object.ReferenceEquals(candidate.Owner, applicationRowDragPlan))
                {
                    continue;
                }
                if (candidate.Payload != null && object.ReferenceEquals(candidate.Payload, applicationRowDragItem))
                {
                    continue;
                }
                Point topLeft;
                try
                {
                    topLeft = candidate.Element.TranslatePoint(new Point(0.0, 0.0), this);
                }
                catch
                {
                    continue;
                }
                double width = Math.Max(1.0, candidate.Element.ActualWidth);
                double height = Math.Max(1.0, candidate.Element.ActualHeight);
                Rect bounds = new Rect(topLeft.X, topLeft.Y, width, height);
                if (!bounds.Contains(windowPoint))
                {
                    continue;
                }
                double area = width * height;
                if (best == null || area < bestArea)
                {
                    best = candidate;
                    bestArea = area;
                    candidate.Top = topLeft.Y;
                    candidate.Height = height;
                }
            }
            return best;
        }

        /// <summary>松手：同方案里调顺序，跨方案就搬过去（两种都进撤销栈）。</summary>
        private void ApplyApplicationRowDrop(WorkspaceRestorer.UI.Cards.Drag.DragMoveRow target, bool insertAfter)
        {
            ApplicationPlanItem moving = applicationRowDragItem;
            ApplicationPlan from = applicationRowDragPlan;
            applicationRowDragItem = null;
            applicationRowDragPlan = null;
            if (moving == null || from == null || target == null)
            {
                return;
            }
            ApplicationPlan targetPlan = target.Owner as ApplicationPlan;
            ApplicationPlanItem targetItem = target.Payload as ApplicationPlanItem;
            if (targetPlan == null || targetItem == null)
            {
                return;
            }
            if (!object.ReferenceEquals(targetPlan, from))
            {
                // 跨方案搬移（与「启动与恢复」同一语义：搬过去，原方案就没有这一项了）。
                ApplicationPlansSnapshot beforeCrossMove =
                    CaptureApplicationPlansSnapshot();
                if (controller.MoveApplicationItemBetweenPlans(from, targetPlan, moving, targetItem, insertAfter))
                {
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusMovedItemToPlanFormat,
                        ApplicationItemLabel(moving),
                        targetPlan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                    PushApplicationPlansUndo(
                        AppText.F(
                    ApplicationsTextKeys.UndoMovedItemToPlanFormat,
                    ApplicationItemLabel(moving),
                    targetPlan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback)),
                        beforeCrossMove);
                }
                else
                {
                    statusText.Text = AppText.T(ApplicationsTextKeys.StatusAlreadyInTarget);
                }
                RefreshApplicationPlanList();
                return;
            }
            // 同方案内部：复用原来的重排实现（它自己会落盘 + 记撤销 + 刷新）。
            ApplyApplicationItemReorder(from, moving, targetPlan, targetItem, insertAfter);
        }

        // ================================================================ 卡片落点（2026-11-01）

        /// <summary>
        /// 行模式：指针下的方案卡片。只认**别的方案**的卡片；自己的卡片、方案文件夹不算。
        /// 子项只能整组走，所以拖子项时返回 null（引擎显示禁止符号）。
        /// </summary>
        private WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot FindApplicationRowCardAt(Point windowPoint)
        {
            ApplicationPlan from = applicationRowDragPlan;
            if (from == null || applicationPlanHost == null)
            {
                return null;
            }
            if (applicationRowDragItem != null
                && !string.IsNullOrWhiteSpace(applicationRowDragItem.ParentId))
            {
                return null;
            }
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (plans == null)
            {
                return null;
            }
            WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot best = null;
            double bestArea = double.MaxValue;
            for (int index = 0; index < plans.Count; index++)
            {
                ApplicationPlan plan = plans[index];
                if (plan == null || string.IsNullOrWhiteSpace(plan.Id) || plan.IsFolder)
                {
                    continue;
                }
                if (object.ReferenceEquals(plan, from))
                {
                    continue;
                }
                FrameworkElement card = applicationCardElements.FindBlock(plan.Id);
                if (card == null || !card.IsVisible)
                {
                    continue;
                }
                Point topLeft;
                try
                {
                    topLeft = card.TranslatePoint(new Point(0.0, 0.0), this);
                }
                catch
                {
                    continue;
                }
                double width = Math.Max(1.0, card.ActualWidth);
                double height = Math.Max(1.0, card.ActualHeight);
                Rect bounds = new Rect(topLeft.X, topLeft.Y, width, height);
                if (!bounds.Contains(windowPoint))
                {
                    continue;
                }
                double area = width * height;
                if (best != null && area >= bestArea)
                {
                    continue;
                }
                WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot slot =
                    new WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot();
                slot.Id = plan.Id;
                slot.Left = topLeft.X;
                slot.Width = width;
                slot.Top = topLeft.Y;
                slot.Height = height;
                best = slot;
                bestArea = area;
            }
            return best;
        }

        /// <summary>
        /// 卡片落点：取目标方案里**离指针最近的那一项**做定位项（用户 2026-11-01 的决定）；
        /// 目标方案收起/为空、一项都看不见时返回 null＝追加到末尾。
        /// 最近项是子项时提升到它所属的整组父行，并插到整组之后（不会插进组内部）。
        /// </summary>
        private void ResolveApplicationCardDropAnchor(
            ApplicationPlan plan,
            Point pointer,
            out ApplicationPlanItem anchor,
            out bool insertAfter)
        {
            anchor = null;
            insertAfter = false;
            if (plan == null)
            {
                return;
            }
            double bestDistance = double.MaxValue;
            for (int index = 0; index < applicationRowDragRows.Count; index++)
            {
                WorkspaceRestorer.UI.Cards.Drag.DragMoveRow row = applicationRowDragRows[index];
                if (row == null || row.Element == null || !row.Element.IsVisible)
                {
                    continue;
                }
                if (!object.ReferenceEquals(row.Owner, plan))
                {
                    continue;
                }
                ApplicationPlanItem item = row.Payload as ApplicationPlanItem;
                if (item == null)
                {
                    continue;
                }
                Point topLeft;
                try
                {
                    topLeft = row.Element.TranslatePoint(new Point(0.0, 0.0), this);
                }
                catch
                {
                    continue;
                }
                double height = Math.Max(1.0, row.Element.ActualHeight);
                double center = topLeft.Y + height / 2.0;
                bool overRow = pointer.Y >= topLeft.Y && pointer.Y <= topLeft.Y + height;
                double distance = overRow ? 0.0 : Math.Abs(pointer.Y - center);
                if (distance >= bestDistance)
                {
                    continue;
                }
                bestDistance = distance;
                ApplicationPlanItem resolved = item;
                bool afterItem = pointer.Y > center;
                if (!string.IsNullOrWhiteSpace(item.ParentId))
                {
                    ApplicationPlanItem root = FindApplicationGroupRoot(plan, item);
                    if (root != null)
                    {
                        resolved = root;
                        afterItem = true;
                    }
                }
                anchor = resolved;
                insertAfter = afterItem;
            }
        }

        private string DescribeApplicationRowCardDropHint(
            WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot card,
            Point pointer)
        {
            ApplicationPlan plan = card == null ? null : FindApplicationPlanById(card.Id);
            if (plan == null)
            {
                return null;
            }
            ApplicationPlanItem anchor;
            bool insertAfter;
            ResolveApplicationCardDropAnchor(plan, pointer, out anchor, out insertAfter);
            string name = plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
            if (anchor == null)
            {
                return AppText.F(ApplicationsTextKeys.DropIntoPlanEndFormat, name);
            }
            return AppText.F(
                    ApplicationsTextKeys.DropIntoPlanItemFormat,
                    name,
                    ApplicationItemLabel(anchor),
                    AppText.T(insertAfter
                        ? ApplicationsTextKeys.InsertAfter
                        : ApplicationsTextKeys.InsertBefore));
        }

        /// <summary>松手落在方案卡片上：整组（父项+全部子项）或单项搬到该方案的最近项旁边/末尾。</summary>
        private void ApplyApplicationRowCardDrop(
            WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot card,
            Point pointer)
        {
            ApplicationPlanItem moving = applicationRowDragItem;
            ApplicationPlan from = applicationRowDragPlan;
            applicationRowDragItem = null;
            applicationRowDragPlan = null;
            if (moving == null || from == null || card == null)
            {
                return;
            }
            ApplicationPlan plan = FindApplicationPlanById(card.Id);
            if (plan == null || object.ReferenceEquals(plan, from))
            {
                return;
            }
            ApplicationPlanItem anchor;
            bool insertAfter;
            ResolveApplicationCardDropAnchor(plan, pointer, out anchor, out insertAfter);
            ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
            bool group = moving.IsGroupParent;
            if (controller.MoveApplicationItemBetweenPlans(from, plan, moving, anchor, insertAfter))
            {
                string label = ApplicationItemLabel(moving);
                statusText.Text = AppText.F(
                group
                    ? ApplicationsTextKeys.StatusMovedGroupToPlanFormat
                    : ApplicationsTextKeys.StatusMovedItemToPlanFormat,
                label,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                PushApplicationPlansUndo(
                    AppText.F(
                    group
                        ? ApplicationsTextKeys.UndoMovedGroupToPlanFormat
                        : ApplicationsTextKeys.UndoMovedItemToPlanFormat,
                    plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                    label),
                    before);
            }
            else
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusAlreadyInTarget);
            }
            RefreshApplicationPlanList();
        }

        /// <summary>把一项提升到它所属的整组父项（找不到父项就返回它自己）。</summary>
        private static ApplicationPlanItem FindApplicationGroupRoot(ApplicationPlan plan, ApplicationPlanItem item)
        {
            if (plan == null || plan.Items == null || item == null)
            {
                return item;
            }
            ApplicationPlanItem current = item;
            int guard = 0;
            while (current != null && !string.IsNullOrWhiteSpace(current.ParentId) && guard < 32)
            {
                guard++;
                string parentId = current.ParentId;
                ApplicationPlanItem parent = null;
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem candidate = plan.Items[index];
                    if (candidate != null
                        && string.Equals(candidate.Id, parentId, StringComparison.OrdinalIgnoreCase))
                    {
                        parent = candidate;
                        break;
                    }
                }
                if (parent == null)
                {
                    return current;
                }
                current = parent;
            }
            return current;
        }

        private void ShowApplicationDragHint(string text)
        {
            if (statusText == null)
            {
                return;
            }
            statusText.Text = text;
            // 状态栏自己的 Text 不在无障碍树里，自检读不到；同一句话也挂到说明上（与「启动与恢复」一致）。
            AccessibleToolTip.Attach(statusText, ApplicationsToolTips.DragHint(text));
        }

        /// <summary>
        /// 拖动收尾：把状态栏的无障碍名字/说明换回**当前那句结论**。
        ///
        /// 【为什么必须做】拖动过程中 `ShowApplicationDragHint` 把状态栏的**名字**设成“落点提示”、
        /// 说明设成落点那句话；松手后页面只改 `Text`，名字与说明还停在上一句落点上 ——
        /// 用户看到的（屏幕上的字）是“位置没有变化”，读屏软件和自检读到的却是“松手：把「X」移到最后面”
        /// （Round106 的 T1 就是在这里红的：同一块文字，两个来源不一致）。
        /// </summary>
        private void SyncApplicationDragStatusDescription()
        {
            if (statusText == null)
            {
                return;
            }
            string text = statusText.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            AccessibleToolTip.Attach(statusText, ApplicationsToolTips.SyncStatus(text));
        }

        /// <summary>
        /// 行拖动的落点提示（与「启动与恢复」的 `ShowDropHint` / `ItemPositionOf` 同一算法、同一文案）：
        /// `松手：把“甲一”插到第 2 位（在“甲三”之前）`。
        ///
        /// 为什么是“第 N 位”而不是“插到甲三之前”：被拖的项会先**从原位拿走**再插入，
        /// 所以“插到目标之前”＝目标现在的下标 + 1，“之后”＝下标 + 2
        /// （那边曾经差一位，自检实测抓到过）。
        /// </summary>
        private string DescribeApplicationRowDropHint(
            WorkspaceRestorer.UI.Cards.Drag.DragMoveRow target, bool insertAfter)
        {
            string moving = ApplicationItemLabel(applicationRowDragItem);
            if (target == null)
            {
                return AppText.F(ApplicationsTextKeys.DropCancelFormat, moving);
            }
            ApplicationPlan plan = target.Owner as ApplicationPlan;
            ApplicationPlanItem item = target.Payload as ApplicationPlanItem;
            string targetName = item == null ? (target.Label ?? "?") : ApplicationItemLabel(item);
            int position = 1;
            if (plan != null && plan.Items != null && item != null)
            {
                int index = plan.Items.IndexOf(item);
                if (index >= 0)
                {
                    position = insertAfter ? index + 2 : index + 1;
                }
            }
            return AppText.F(
                    ApplicationsTextKeys.DropInsertFormat,
                    moving, position, targetName,
                    AppText.T(insertAfter
                        ? ApplicationsTextKeys.InsertAfter
                        : ApplicationsTextKeys.InsertBefore));
        }

        /// <summary>清掉拖动插入线（公共拖动引擎里那一份；页面重建列表时也要调）。</summary>
        private void ClearDesktopInsertLine()
        {
            if (applicationRowDragSession != null)
            {
                applicationRowDragSession.ClearInsertionLine();
            }
        }

        // ================================================================ 方案卡片拖动

        /// <summary>卡片松手：按落点并入或调顺序（引擎的窗口级钩子也会调，重复调用是空操作）。</summary>
        private void EndApplicationCardDrag()
        {
            WorkspaceRestorer.UI.Cards.Drag.DragMoveSession session = applicationCardDragSession;
            if (session != null && session.IsActive)
            {
                applicationCardDragJustEnded = true;
                session.Finish();
                applicationCardDragPlan = null;
            }
            applicationCardDragPressActive = false;
            applicationCardDragArmedPlan = null;
            applicationCardDragArmedElement = null;
        }
        /// <summary>卡片模式：Header 算槽位、Block 算禁止区，排除被拖的那张。</summary>
        private IList<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> CaptureApplicationCardSlots()
        {
            List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> slots =
                new List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot>();
            applicationCardForbiddenRects.Clear();
            if (applicationPlanHost == null)
            {
                return slots;
            }

            ApplicationPlan dragged = applicationCardDragPlan;
            WorkspaceRestorer.UI.Cards.Drag.DragCardSlotCapture capture =
                WorkspaceRestorer.UI.Cards.Drag.DragCardSlotCollector.Capture(
                    applicationCardElements,
                    applicationPlanHost,
                    this,
                    dragged == null ? null : dragged.Id,
                    delegate(string id)
                    {
                        return dragged != null
                            && PlanFolderTree.IsDescendantId(controller.ApplicationPlans, id, dragged.Id);
                    },
                    false);

            applicationCardForbiddenRects.AddRange(capture.ForbiddenRects);
            return capture.Slots;
        }

        /// <summary>指针是否落在被拖方案自己的后代卡片上（禁止落点）。坐标是窗口坐标。</summary>
        private bool IsApplicationCardDropForbidden(Point pointer)
        {
            for (int index = 0; index < applicationCardForbiddenRects.Count; index++)
            {
                if (applicationCardForbiddenRects[index].Contains(pointer))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>卡片落点：插入＝调整方案顺序；并入＝合并（会弹确认框）。</summary>
        private void ApplyApplicationCardDrop(WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropDecision decision)
        {
            ApplicationPlan dragged = applicationCardDragPlan;
            applicationCardDragPlan = null;
            if (dragged == null || decision == null)
            {
                return;
            }
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (decision.Kind == WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropKind.Merge)
            {
                ApplicationPlan mergeTarget = null;
                if (plans != null && decision.Index >= 0 && decision.Index < plans.Count)
                {
                    // 落点表里排除了自己，所以这里要按 Id 找回那张卡片对应的方案。
                    mergeTarget = FindApplicationPlanById(decision.TargetId);
                }
                if (mergeTarget != null)
                {
                    MergeApplicationPlan(dragged, mergeTarget);
                }
                return;
            }
            if (decision.Kind == WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropKind.Insert)
            {
                MoveApplicationCardToIndex(dragged, decision);
            }
        }

        private ApplicationPlan FindApplicationPlanById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (plans == null)
            {
                return null;
            }
            for (int index = 0; index < plans.Count; index++)
            {
                ApplicationPlan plan = plans[index];
                if (plan != null && string.Equals(plan.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return plan;
                }
            }
            return null;
        }

        /// <summary>插到第 Index 道缝（落点表已排除自己，所以要把下标换算回真实位置）。</summary>
        private void MoveApplicationCardToIndex(ApplicationPlan plan,
            WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropDecision decision)
        {
            if (plan == null || decision == null)
            {
                return;
            }
            ApplicationPlan target = FindApplicationPlanById(decision.TargetId);
            if (target == null || object.ReferenceEquals(plan, target))
            {
                return;
            }
            // 落点表按“除被拖项之外的当前清单顺序”排列；用它换算插到目标之前/之后。
            List<ApplicationPlan> others = new List<ApplicationPlan>();
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            for (int index = 0; index < (plans == null ? 0 : plans.Count); index++)
            {
                ApplicationPlan candidate = plans[index];
                if (candidate != null
                    && !string.Equals(candidate.Id, plan.Id, StringComparison.OrdinalIgnoreCase)
                    && !PlanFolderTree.IsDescendantId(plans, candidate.Id, plan.Id))
                {
                    others.Add(candidate);
                }
            }
            int targetIndex = others.IndexOf(target);
            bool insertAfter = targetIndex >= 0 && decision.Index > targetIndex;
            if (decision.Index >= others.Count)
            {
                insertAfter = true;
            }
            string oldParentId = plan.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, plan);
            if (!controller.MoveApplicationPlanRelative(plan, target, insertAfter))
            {
                return;
            }
            string newParentId = plan.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, plan);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.StatusReorderedFormat,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            PushUndo(
                AppText.T(ApplicationsTextKeys.UndoReorder),
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(plan, oldParentId, oldIndex);
                    RefreshApplicationPlanList();
                },
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(plan, newParentId, newIndex);
                    RefreshApplicationPlanList();
                });
        }

        /// <summary>松手落在上/下放置条上：把这份方案移到最前/最后。</summary>
        private void MoveApplicationCardToEdge(ApplicationPlan plan, int direction)
        {
            if (plan == null)
            {
                return;
            }
            string oldParentId = plan.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, plan);
            if (!controller.MoveApplicationPlanToRoot(plan))
            {
                return;
            }
            string newParentId = plan.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.ApplicationPlans, plan);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.StatusMovedRootFormat,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            PushUndo(
                AppText.F(
                    ApplicationsTextKeys.UndoMovedRootFormat,
                    plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback)),
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(plan, oldParentId, oldIndex);
                    RefreshApplicationPlanList();
                },
                delegate
                {
                    controller.MoveApplicationPlanToPlacement(plan, newParentId, newIndex);
                    RefreshApplicationPlanList();
                });
        }

    }
}
