using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共区域】三个工作区唯一的拖动引擎。
    ///
    /// 2026-11-01 起「启动与恢复」也改用它；启动只通过
    /// `StartupWorkspacePage.DragEngine.cs` 注入“嵌套方案”的业务回调。
    /// 引擎负责：
    ///  · **纯 WPF 原生拖动**（不走 OLE 的 DoDragDrop：它会整个接管鼠标输入，合成拖动下还不返回）；
    ///  · 窗口级钩子 + `InputManager.PreProcessInput`：起拖后鼠标被行捕获，窗口的 PreviewMouseMove
    ///    只能在被捕获元素的子树里派发，指针一移出列表就收不到 —— 用原始输入（设备级）兜住；
    ///  · **浮动方框**：X 固定在来源行左边缘、只有 Y 跟手（像那一行的复制品），宽度＝行宽；
    ///  · **光标持续持有**（80ms 定时器反复设 OverrideCursor）：只在移动那一帧设一次会被系统刷回箭头；
    ///  · **上/下放置条**：拖进去淡淡亮起并自动滚动（速度取总设置里的“拖动时的滚动速度”），
    ///    松手＝移到最前/最后；
    ///  · **禁止光标**：指针不在列表/放置条上就显示禁止符号，松手＝取消（绝不扔到猜出来的位置）；
    ///  · **落点提示线**：行模式一行只插一条 2 像素的主题色线（真元素插进列表，不重画、不闪）；
    ///    卡片模式是 3 像素圆角线画在浮层上，**缝上/下两张卡片各让 5 像素**（140ms 缓动）——
    ///    用户 2026-10-18 原话：“插入时，上下的方案会微微上/下移动，有交互感”；
    ///  · **落点提示文字**与「启动与恢复」同一口径（行模式报到第几位、卡片模式报到“最前/最后/之间/并入”）；
    ///  · **看门狗**：物理左键已松开却收不到 Up（截屏工具抢走输入）时，≈200ms 内自动“取消移动”；
    ///  · **日志**：起拖 / 移动中（每 15 帧）/ 松手 / 自动取消 / 落点合法与否，全部带前缀 `[拖动]`。
    ///
    /// 它**不认识**任何业务数据：落点怎么判、松手改什么，全由 <see cref="DragMoveHost"/> 的回调交给页面。
    /// 三个工作区共用同一套手感；数据改动仍留在各自文件夹里。
    /// </summary>
    internal sealed class DragMoveSession : IDisposable
    {
        // ---------------- 常量：与「启动与恢复」一致的部分 ----------------
        private const int ScrollIntervalMilliseconds = 33;
        private const double FallbackZoneHeight = 64.0;
        /// <summary>卡片插入线：3 像素高、圆角 1.5（与「启动与恢复」的 `ShowProfileDropInsert` 一致）。</summary>
        private const double CardInsertLineHeight = 3.0;
        private const double CardInsertLineRadius = 1.5;
        /// <summary>卡片“腾开空间”：缝上、下那张卡片各让 5 像素（与「启动与恢复」的 `ProfileDropRoomShift` 一致）。</summary>
        private const double CardDropRoomShift = 5.0;
        private const double CardDropRoomShiftMilliseconds = 140.0;

        private readonly DragMoveHost host;
        private bool disposed;

        // ---------------- 五个公共零件（样式/几何/光标/滚动各只有一份） ----------------
        private readonly DragInsertionLine insertionLine;
        private readonly DragTargetFrame targetFrame;
        private readonly DragForbiddenCursor forbiddenCursor;
        private readonly DragScrollZone scrollZone;

        // ---------------- 拖动状态 ----------------
        private bool active;
        /// <summary>本次拖动实际按下的鼠标键（≡ 手柄固定左键，整块拖动取设置）。</summary>
        private MouseButton activeMouseButton = MouseButton.Left;
        private string movingLabel;
        private FrameworkElement sourceElement;
        private bool? loggedInside;
        private int moveLogCount;

        // 行模式
        private DragMoveRow dropRow;
        private bool dropAfter;

        // 行模式：拖到方案卡片上（2026-11-01；指针不在任何行上时才用）
        private DragMoveCardSlot dropCard;

        // 卡片模式
        private readonly List<DragMoveCardSlot> cardSlots = new List<DragMoveCardSlot>();
        private readonly Dictionary<string, FrameworkElement> cardElements =
            new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        private DragMoveCardDropDecision cardDecision;

        // 看门狗（公共原语）
        private DragWatchdog watchdog;

        internal DragMoveSession(DragMoveHost host)
        {
            if (host == null || host.Window == null)
            {
                throw new ArgumentException("拖动引擎需要窗口与宿主配置。", "host");
            }
            this.host = host;
            Window window = host.Window;
            insertionLine = new DragInsertionLine(host.Layer);
            targetFrame = new DragTargetFrame(host.Layer, host.Ghost);
            forbiddenCursor = new DragForbiddenCursor(delegate { return active; });
            scrollZone = new DragScrollZone(
                host.Window, host.List, host.TopPad, host.BottomPad, host.ScrollSpeedFactor);
            window.AddHandler(UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(OnWindowMouseMove), true);
            window.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OnWindowMouseUp), true);
            // 拖动可以配置成右键起拖：右键 Up 只收本次拖动的收尾，不拖时右键菜单照常。
            window.AddHandler(UIElement.PreviewMouseRightButtonUpEvent,
                new MouseButtonEventHandler(OnWindowMouseUp), true);
            InputManager.Current.PreProcessInput += OnPreProcessInput;
            window.Closed += delegate { Dispose(); };
        }

        /// <summary>正在拖动吗（页面用它挡掉“松手当点击”和 Ctrl+Z）。</summary>
        internal bool IsActive
        {
            get { return active; }
        }

        /// <summary>拖动模式。</summary>
        internal DragMoveMode Mode
        {
            get { return host.Mode; }
        }

        // ================================================================ 起拖

        /// <summary>
        /// 开始一次拖动。<paramref name="element"/> 是来源控件（行/卡片，用来捕获鼠标与取宽度），
        /// <paramref name="label"/> 是日志与方框上的名字；
        /// <paramref name="mouseButton"/> 是本次按下的鼠标键（≡ 手柄固定左键，整块拖动取设置）。
        /// </summary>
        internal bool Start(FrameworkElement element, string label, MouseButton mouseButton)
        {
            if (active || element == null)
            {
                return false;
            }
            active = true;
            activeMouseButton = mouseButton == MouseButton.Right ? MouseButton.Right : MouseButton.Left;
            loggedInside = null;
            moveLogCount = 0;
            dropRow = null;
            dropAfter = false;
            dropCard = null;
            cardDecision = null;
            sourceElement = element;
            movingLabel = string.IsNullOrWhiteSpace(label)
                ? AppText.T(CommonTextKeys.ItemFallback)
                : label;
            AppLog.Write(host.LogTag + " 起拖：" + movingLabel);
            scrollZone.Refresh();
            scrollZone.Reset();
            insertionLine.Hide();
            targetFrame.Hide();
            if (host.Mode == DragMoveMode.CardZones)
            {
                CaptureCardSlots();
            }
            try { element.CaptureMouse(); } catch { }
            try { Mouse.OverrideCursor = Cursors.SizeAll; } catch { }
            forbiddenCursor.Start();
            StartWatchdog();
            if (host.Ghost != null)
            {
                host.Ghost.ShowFrom(element, movingLabel, host.Layer);
                Point pointer;
                if (TryGetPointer(out pointer))
                {
                    host.Ghost.Move(host.Window, host.Layer, pointer);
                }
            }
            return true;
        }

        // ================================================================ 移动中

        /// <summary>
        /// 每帧调一次：摆浮动方框、更新放置条/自动滚动、算落点、画提示线。
        /// 窗口级钩子会自动调它；页面在行自己的 MouseMove 里也调一次，保证行内移动也即时。
        /// </summary>
        internal void Continue()
        {
            if (!active)
            {
                return;
            }
            Point pointer;
            if (!TryGetPointer(out pointer))
            {
                return;
            }
            if (host.Ghost != null)
            {
                host.Ghost.Move(host.Window, host.Layer, pointer);
            }
            scrollZone.Update(pointer);
            if (host.Mode == DragMoveMode.CardZones)
            {
                ContinueCard(pointer);
            }
            else
            {
                ContinueRow(pointer);
            }
            UpdateForbiddenCursor(pointer);
        }

        private void ContinueRow(Point pointer)
        {
            // 上/下放置条优先：在带子里就是“移到最前/最后”，不要再拿行几何算一条插入线。
            if (scrollZone.IsInZone(pointer))
            {
                dropRow = null;
                dropAfter = false;
                dropCard = null;
                ClearInsertionLine();
                targetFrame.Hide();
                Hint(scrollZone.TopZoneRect.Contains(pointer)
                    ? AppText.T(CommonTextKeys.DragHintMoveToFront)
                    : AppText.T(CommonTextKeys.DragHintMoveToBack), null);
                return;
            }
            DragMoveRow target = host.FindRowAt == null ? null : host.FindRowAt(pointer);
            if (target == null)
            {
                // 【2026-11-01】不在任何行上时，先看是不是落在某张方案卡片上：
                // 拖到别的方案卡片＝放进那份方案（页面自己算“最近的一项”/末尾）；自己的卡片页面返回 null。
                DragMoveCardSlot card = ResolveRowCardTarget(pointer);
                if (card != null)
                {
                    dropRow = null;
                    dropAfter = false;
                    dropCard = card;
                    ClearInsertionLine();
                    NotifyRowCardHighlight(card);
                    Hint(DescribeRowCardTargetHint(card, pointer), null);
                    return;
                }
                // 【必须清掉落点】否则松手会用“最后一个悬停过的行”当落点，
                // 表现就是“在空白处松手却跳到了最上/最下面”（「启动与恢复」踩过这个坑）。
                dropRow = null;
                dropAfter = false;
                if (dropCard != null || targetFrame.IsVisible)
                {
                    dropCard = null;
                    NotifyRowCardHighlight(null);
                }
                ClearInsertionLine();
                Hint(AppText.F(CommonTextKeys.DragHintCancelMoveFormat, movingLabel), null);
                return;
            }
            if (dropCard != null)
            {
                dropCard = null;
                NotifyRowCardHighlight(null);
            }
            double rowTop = target.Top;
            double rowBottom = target.Top + target.Height;
            bool insertAfter = (pointer.Y - rowTop) > (rowBottom - pointer.Y);
            dropRow = target;
            dropAfter = insertAfter;
            moveLogCount++;
            if (moveLogCount % 15 == 1)
            {
                AppLog.Write(host.LogTag + " 移动中：鼠标=(" + (int)pointer.X + "," + (int)pointer.Y
                    + ") 落点行=" + (target.Label ?? "?")
                    + " 到上缝=" + (int)(pointer.Y - rowTop) + " 到下缝=" + (int)(rowBottom - pointer.Y)
                    + " 插到后面=" + insertAfter);
            }
            ShowRowInsertionLine(target, insertAfter);
            Hint(BuildRowHint(target, insertAfter), target);
        }

        private string BuildRowHint(DragMoveRow target, bool insertAfter)
        {
            // 页面更懂“这一项在方案里是第几位”，所以落点提示交给它写（与「启动与恢复」的
            // `ShowDropHint` 一致：“松手：把“甲一”插到第 2 位（在“甲三”之前）”）。
            if (target != null && host.DescribeRowHint != null)
            {
                try
                {
                    string described = host.DescribeRowHint(target, insertAfter);
                    if (!string.IsNullOrWhiteSpace(described))
                    {
                        return described;
                    }
                }
                catch
                {
                }
            }
            if (target == null)
            {
                return AppText.F(CommonTextKeys.DragHintCancelMoveFormat, movingLabel);
            }
            return AppText.F(
                insertAfter ? CommonTextKeys.DragHintInsertAfterFormat : CommonTextKeys.DragHintInsertBeforeFormat,
                movingLabel, target.Label ?? "?");
        }

        /// <summary>行模式：指针下的方案卡片（页面负责排除自己的卡片/方案文件夹）。</summary>
        private DragMoveCardSlot ResolveRowCardTarget(Point pointer)
        {
            if (host.FindRowCardAt == null)
            {
                return null;
            }
            try
            {
                return host.FindRowCardAt(pointer);
            }
            catch
            {
                return null;
            }
        }

        private void NotifyRowCardHighlight(DragMoveCardSlot card)
        {
            if (card == null)
            {
                targetFrame.Hide();
                return;
            }
            FrameworkElement element = FindCard(card.Id);
            if (element == null)
            {
                targetFrame.Hide();
                return;
            }
            // 用户 2026-11-01 拍板：行拖到方案卡片＝浮层框 + 气泡；
            // 气泡用短句，完整位次（在“Y”之前/之后）仍由状态栏报。
            targetFrame.Show(element, AppText.F(
                CommonTextKeys.DragHintDropIntoTargetFormat, DescribeCardName(card.Id)));
        }

        private string DescribeRowCardTargetHint(DragMoveCardSlot card, Point pointer)
        {
            if (host.DescribeRowCardHint != null)
            {
                try
                {
                    string described = host.DescribeRowCardHint(card, pointer);
                    if (!string.IsNullOrWhiteSpace(described))
                    {
                        return described;
                    }
                }
                catch
                {
                }
            }
            return AppText.F(
                CommonTextKeys.DragHintDropIntoPlanFormat,
                DescribeCardName(card == null ? null : card.Id));
        }

        private string DescribeCardName(string id)
        {
            if (host.DescribeCard != null)
            {
                try
                {
                    string name = host.DescribeCard(id);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                catch
                {
                }
            }
            return string.IsNullOrWhiteSpace(id) ? AppText.T(CommonTextKeys.PlanFallback) : id;
        }

        private void ContinueCard(Point pointer)
        {
            if (cardSlots.Count == 0)
            {
                // 只有这一张卡片，没地方可放（松手＝什么都不改）。
                ClearCardFeedback();
                cardDecision = null;
                Hint(AppText.T(CommonTextKeys.DragHintCancelDrop), null);
                return;
            }
            // 上/下放置条优先：在带子里就是“移到最前/最后”，不要再用卡片几何算插入线。
            if (scrollZone.IsInZone(pointer))
            {
                ClearCardFeedback();
                cardDecision = null;
                Hint(scrollZone.TopZoneRect.Contains(pointer)
                    ? AppText.F(CommonTextKeys.DragHintMoveItemToFrontFormat, movingLabel)
                    : AppText.F(CommonTextKeys.DragHintMoveItemToBackFormat, movingLabel), null);
                return;
            }
            // 指针不在列表/放置条上＝不能放（显示禁止符号，松手什么都不改）。
            if (scrollZone.IsReady && !IsInsideDropArea(pointer))
            {
                ClearCardFeedback();
                cardDecision = null;
                if (scrollZone.TopZoneRect.Contains(pointer))
                {
                    Hint(AppText.F(CommonTextKeys.DragHintMoveItemToFrontFormat, movingLabel), null);
                }
                else if (scrollZone.BottomZoneRect.Contains(pointer))
                {
                    Hint(AppText.F(CommonTextKeys.DragHintMoveItemToBackFormat, movingLabel), null);
                }
                else
                {
                    Hint(AppText.T(CommonTextKeys.DragHintCancelDrop), null);
                }
                return;
            }
            if (IsPointerForbidden(pointer))
            {
                // 例如拖一张方案到它自己的子方案上：提前判禁止，不画线/高亮。
                ClearCardFeedback();
                cardDecision = null;
                Hint(AppText.T(CommonTextKeys.DragHintForbiddenDescendant), null);
                return;
            }
            if (IsPointerBelowCards(pointer))
            {
                // 用户 2026-11-01：列表最后一张卡片下方的空白＝移到根目录末尾；其他空白仍取消。
                ClearCardFeedback();
                cardDecision = null;
                Hint(AppText.F(CommonTextKeys.DragHintMoveToRootEndFormat, movingLabel), null);
                return;
            }
            Point content;
            if (!TryContentPoint(pointer, out content))
            {
                ClearCardFeedback();
                return;
            }
            DragMoveCardDropDecision decision = ApplyCardMergePolicy(
                DragMoveCardZones.Resolve(cardSlots, content.Y, cardDecision, host.CardGapMode), content.Y);
            cardDecision = decision;
            if (decision.Kind == DragMoveCardDropKind.Insert)
            {
                HideCardMerge();
                ShowCardInsert(decision);
                Hint(BuildCardInsertHint(decision), null);
            }
            else if (decision.Kind == DragMoveCardDropKind.Merge)
            {
                HideCardInsert();
                ShowCardMerge(decision);
                string targetName = CardLabel(decision.Index);
                string mergeHint = host.DescribeMergeHint == null
                    ? null
                    : host.DescribeMergeHint(movingLabel, targetName);
                if (string.IsNullOrWhiteSpace(mergeHint))
                {
                    mergeHint = AppText.F(
                        CommonTextKeys.DragHintMergeIntoFormat, movingLabel, targetName);
                }
                Hint(mergeHint, null);
            }
            else
            {
                ClearCardFeedback();
                Hint(AppText.T(CommonTextKeys.DragHintCancelDrop), null);
            }
        }

        /// <summary>
        /// 卡片插入时的落点提示。文字口径与「启动与恢复」的 `UpdateProfileDropStatusText` 一字不差：
        /// 最前 / 最后 / 插到「前一张」和「后一张」之间，引号用「」。
        /// </summary>
        private string BuildCardInsertHint(DragMoveCardDropDecision decision)
        {
            if (decision == null)
            {
                return AppText.T(CommonTextKeys.DragHintCancelDrop);
            }
            if (decision.Index <= 0)
            {
                return AppText.F(CommonTextKeys.DragHintMoveItemToFrontFormat, movingLabel);
            }
            if (decision.Index >= cardSlots.Count)
            {
                return AppText.F(CommonTextKeys.DragHintMoveItemToBackFormat, movingLabel);
            }
            return AppText.F(
                CommonTextKeys.DragHintInsertBetweenFormat,
                movingLabel,
                CardLabel(decision.Index - 1),
                CardLabel(decision.Index));
        }

        private string CardLabel(int index)
        {
            if (index < 0)
            {
                return "?";
            }
            // 插到**最后面**时 Index == Count：提示里要说“插到「最后一张」之后”，
            // 以前这里直接返回 "?"，用户看到的是「松手：把“X”插到“?”之后」（Round106 抓到的）。
            if (index >= cardSlots.Count)
            {
                index = cardSlots.Count - 1;
            }
            if (index < 0)
            {
                return "?";
            }
            string id = cardSlots[index].Id;
            // 卡片元素本身没有无障碍名字（名字在卡片里的标题 TextBlock 上），
            // 所以“这张卡片叫什么”要由页面通过 DescribeCard 告诉我们。
            if (host.DescribeCard != null && !string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    string described = host.DescribeCard(id);
                    if (!string.IsNullOrWhiteSpace(described))
                    {
                        return described;
                    }
                }
                catch
                {
                }
            }
            FrameworkElement card = FindCard(id);
            if (card == null)
            {
                return "?";
            }
            try
            {
                string name = System.Windows.Automation.AutomationProperties.GetName(card);
                return string.IsNullOrWhiteSpace(name) ? "?" : name;
            }
            catch
            {
                return "?";
            }
        }
        // ================================================================ 松手 / 取消

        /// <summary>松手：按当前落点执行（落在放置条上＝移到最前/最后，落在行/卡片上＝插入或并入）。</summary>
        internal void Finish()
        {
            FinishInternal(false);
        }

        /// <summary>被打断（物理左键松了但收不到 Up、或窗口失活）：与松手同样的清理，但**不执行任何落点**。</summary>
        internal void CancelInterrupted(string reason)
        {
            AppLog.Write(host.LogTag + " 自动取消：还停在“移动中”，但这次拖动已经被打断了 → 按“取消移动”收尾，"
                + "不执行任何落点。原因：" + reason);
            FinishInternal(true);
        }

        private void FinishInternal(bool cancelled)
        {
            if (!active)
            {
                return;
            }
            active = false;
            DragMoveRow target = dropRow;
            bool insertAfter = dropAfter;
            DragMoveCardDropDecision decision = cardDecision;
            DragMoveCardSlot cardTarget = dropCard;
            dropRow = null;
            dropAfter = false;
            dropCard = null;
            cardDecision = null;
            insertionLine.Hide();
            targetFrame.Hide();
            // 【2026-10-17 修“松开鼠标后完全卡住了”】收浮动方框统一由 DragGhostVisual 负责。
            if (host.Ghost != null)
            {
                host.Ghost.Hide();
            }
            scrollZone.Reset();
            forbiddenCursor.Stop();
            StopWatchdog();
            loggedInside = null;
            try { Mouse.Capture(null); } catch { }
            try { Mouse.OverrideCursor = null; } catch { }
            try { Mouse.SetCursor(Cursors.Arrow); } catch { }
            try
            {
                if (cancelled)
                {
                    Hint(AppText.T(CommonTextKeys.DragHintCancelledInterrupted), null);
                    if (host.OnCancelled != null)
                    {
                        try { host.OnCancelled(); } catch (Exception exception) { AppLog.Write(host.LogTag + " 取消回调出错", exception); }
                    }
                    return;
                }
                // 松手这一刻**重新取一次指针**：不能用上一帧的残留（「启动与恢复」吃过“指针已经在边栏却还想并入”的亏）。
                Point pointer;
                bool havePointer = TryGetPointer(out pointer);
                int edgeDirection = 0;
                if (havePointer && !scrollZone.IsReady)
                {
                    scrollZone.Refresh();
                }
                if (havePointer && scrollZone.IsReady && !scrollZone.ListRect.Contains(pointer))
                {
                    if (scrollZone.TopZoneRect.Contains(pointer))
                    {
                        edgeDirection = -1;
                    }
                    else if (scrollZone.BottomZoneRect.Contains(pointer))
                    {
                        edgeDirection = 1;
                    }
                }
                if (havePointer && edgeDirection == 0 && IsPointerBelowCards(pointer))
                {
                    // 列表最后一张卡片下方的空白＝移到根目录末尾（用户 2026-11-01 拍板）。
                    edgeDirection = 1;
                }
                // 落点也用“松手这一刻”的指针重算一次：最后一帧如果没赶上移动事件，
                // 用旧落点就会“松手在空白处却插到上一行”（「启动与恢复」踩过同样的坑）。
                if (havePointer && host.Mode == DragMoveMode.InsertRow && host.FindRowAt != null)
                {
                    DragMoveRow fresh = host.FindRowAt(pointer);
                    target = fresh;
                    insertAfter = false;
                    if (fresh != null)
                    {
                        double rowTop = fresh.Top;
                        double rowBottom = fresh.Top + fresh.Height;
                        insertAfter = (pointer.Y - rowTop) > (rowBottom - pointer.Y);
                    }
                }
                if (havePointer && host.Mode == DragMoveMode.CardZones)
                {
                    decision = ResolveCardDecisionAtRelease(pointer);
                }
                if (havePointer && host.Mode == DragMoveMode.InsertRow && host.FindRowCardAt != null)
                {
                    // 行模式松手时也重算一次“卡片落点”：只有不在任何行上才算卡片。
                    cardTarget = target == null ? ResolveRowCardTarget(pointer) : null;
                }
                AppLog.Write(host.LogTag + " 松手：拖动项=" + movingLabel
                    + " 指针=" + (havePointer ? ("(" + (int)pointer.X + "," + (int)pointer.Y + ")") : "?")
                    + (host.Mode == DragMoveMode.CardZones
                        ? (" 落点=" + DescribeCardDecision(decision))
                        : (" 落点行=" + (target == null ? "无" : (target.Label ?? "?")) + " 插到后面=" + insertAfter))
                    + (target == null && cardTarget != null
                        ? (" 落点卡片=" + DescribeCardName(cardTarget.Id))
                        : string.Empty)
                    + (edgeDirection == 0 ? string.Empty : (" 落点=放置条(" + (edgeDirection < 0 ? "最前" : "最后") + ")")));
                if (edgeDirection != 0 && host.ApplyEdgeDrop != null)
                {
                    host.ApplyEdgeDrop(edgeDirection);
                    return;
                }
                if (host.Mode == DragMoveMode.CardZones)
                {
                    if (decision != null && decision.Kind != DragMoveCardDropKind.None && host.ApplyCardDrop != null)
                    {
                        host.ApplyCardDrop(decision);
                    }
                    else
                    {
                        // 松手在列表外面/列表里没有别的卡片：明确说“什么都没改”，
                        // 别让状态栏还挂着“松手：把…插到…”那句（以前就是这样，看着像卡住了）。
                        Hint(AppText.T(CommonTextKeys.DragHintNoDrop), null);
                    }
                    return;
                }
                if (target != null && host.ApplyDrop != null)
                {
                    host.ApplyDrop(target, insertAfter);
                    return;
                }
                if (target == null && cardTarget != null && host.ApplyRowCardDrop != null)
                {
                    host.ApplyRowCardDrop(cardTarget, pointer);
                    return;
                }
                Hint(AppText.T(CommonTextKeys.DragHintNoDrop), null);
            }
            finally
            {
                // 页面在这里清掉自己的“正在拖什么”（一定在落点回调之后，落点回调里还要用它）。
                if (host.OnFinished != null)
                {
                    try { host.OnFinished(); } catch (Exception exception) { AppLog.Write(host.LogTag + " 收尾回调出错", exception); }
                }
            }
        }

        /// <summary>
        /// 卡片模式：松手那一刻的落点。指针**不在列表/放置条上**＝不能放（返回 None，绝不扔到猜出来的位置）；
        /// 在列表里就一定能放下（空档算最近的那道缝，见 `DragMoveCardZones.ResolveRaw`）。
        /// </summary>
        private DragMoveCardDropDecision ResolveCardDecisionAtRelease(Point pointer)
        {
            if (cardSlots.Count == 0)
            {
                return null;
            }
            if (scrollZone.IsReady && !IsInsideDropArea(pointer))
            {
                return null;
            }
            if (IsPointerForbidden(pointer))
            {
                return null;
            }
            Point content;
            if (!TryContentPoint(pointer, out content))
            {
                return null;
            }
            return ApplyCardMergePolicy(
                DragMoveCardZones.Resolve(cardSlots, content.Y, cardDecision, host.CardGapMode), content.Y);
        }

        /// <summary>
        /// 有些工作区只允许插入、不允许并入（「图标位置保存」）。
        /// 把落在卡片中间的 Merge 改判成离指针最近的上/下接缝；这样拖动中间也会画插入线，
        /// 不会出现“松手：并入…”的气泡或整卡高亮。
        /// </summary>
        private DragMoveCardDropDecision ApplyCardMergePolicy(DragMoveCardDropDecision decision, double pointerY)
        {
            bool mergeAllowed = host.AllowCardMerge;
            if (mergeAllowed && host.CanMergeCard != null && decision != null
                && !string.IsNullOrWhiteSpace(decision.TargetId))
            {
                try
                {
                    mergeAllowed = host.CanMergeCard(decision.TargetId);
                }
                catch
                {
                    mergeAllowed = false;
                }
            }
            if (mergeAllowed
                || decision == null
                || decision.Kind != DragMoveCardDropKind.Merge
                || decision.Index < 0
                || decision.Index >= cardSlots.Count)
            {
                return decision;
            }
            int index = decision.Index;
            DragMoveCardSlot slot = cardSlots[index];
            if (pointerY > slot.Top + slot.Height / 2.0)
            {
                index++;
            }
            if (index < 0)
            {
                index = 0;
            }
            if (index > cardSlots.Count)
            {
                index = cardSlots.Count;
            }
            string anchor = index < cardSlots.Count
                ? cardSlots[index].Id
                : (cardSlots.Count > 0 ? cardSlots[cardSlots.Count - 1].Id : null);
            return new DragMoveCardDropDecision
            {
                Kind = DragMoveCardDropKind.Insert,
                Index = index,
                TargetId = anchor
            };
        }

        /// <summary>收起浮动方框（收尾/被打断都要收：留着就是一块白框飘在界面上）。</summary>
        private void HideGhost()
        {
            if (host.Ghost != null)
            {
                host.Ghost.Hide();
            }
        }

        private static string DescribeCardDecision(DragMoveCardDropDecision decision)
        {
            if (decision == null || decision.Kind == DragMoveCardDropKind.None)
            {
                return "无（取消）";
            }
            return decision.Kind == DragMoveCardDropKind.Merge
                ? ("并入第 " + (decision.Index + 1) + " 张")
                : ("插到第 " + (decision.Index + 1) + " 道缝");
        }

        // ================================================================ 窗口钩子

        private void OnWindowMouseMove(object sender, MouseEventArgs e)
        {
            if (!active || !DragGestureSettings.IsMouseButtonPressed(e, activeMouseButton))
            {
                return;
            }
            Continue();
        }

        private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!active || e.ChangedButton != activeMouseButton)
            {
                return;
            }
            if (activeMouseButton == MouseButton.Right)
            {
                // 右键拖动结束：把这次右键 Up 吃掉，别紧接着弹出卡片/行的右键菜单。
                e.Handled = true;
            }
            Finish();
        }

        /// <summary>
        /// 原始鼠标输入（设备级）：起拖后鼠标被行捕获，窗口的 PreviewMouseMove 只在被捕获元素的子树里派发，
        /// 指针一移出列表（例如移到左边工作区边栏）就收不到 —— 那样“禁止符号”永远不生效。
        /// </summary>
        private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            if (!active || e == null || e.StagingItem == null)
            {
                return;
            }
            MouseEventArgs args = e.StagingItem.Input as MouseEventArgs;
            if (args == null)
            {
                return;
            }
            if (args.RoutedEvent != Mouse.PreviewMouseMoveEvent && args.RoutedEvent != Mouse.MouseMoveEvent)
            {
                return;
            }
            if (!DragGestureSettings.IsMouseButtonPressed(args, activeMouseButton))
            {
                return;
            }
            Continue();
        }

        // ================================================================ 光标

        private void UpdateForbiddenCursor(Point pointer)
        {
            // 光标跟着**真实落点**走：有行/卡片/放置条才允许，其余都显示禁止。
            bool inside = !scrollZone.IsReady || IsDropPointAllowed(pointer);
            forbiddenCursor.Set(!inside);
            if (loggedInside != inside)
            {
                loggedInside = inside;
                AppLog.Write(host.LogTag + "落点 " + (inside ? "合法（可放置）" : "不合法（显示禁止符号）")
                    + " 指针=(" + (int)pointer.X + "," + (int)pointer.Y + ")");
            }
        }

        /// <summary>
        /// 光标语义用的“这里真的能放吗”：
        ///  · 上/下两条放置带永远算可放（松手会移到最前/最后）；
        ///  · 行模式：指针必须压在某个落点行/方案卡片上；
        ///  · 卡片模式：必须落在“插入/并入”的判定里（卡片之间的缝也算）。
        /// 列表自己的空白背景不算可放 —— 那里松手是取消，光标必须与结果一致。
        /// </summary>
        private bool IsDropPointAllowed(Point pointer)
        {
            if (scrollZone.IsInZone(pointer))
            {
                return true;
            }
            if (IsPointerForbidden(pointer))
            {
                return false;
            }
            // 最后一张卡片下方的空白不是“无落点”：松手会走 ApplyEdgeDrop(1)
            // 移到根目录末尾。光标必须和松手用同一个判定，否则会出现
            // “显示禁止符号，但松手又真的移动”的矛盾。
            if (IsPointerBelowCards(pointer))
            {
                return true;
            }
            if (host.Mode == DragMoveMode.InsertRow)
            {
                return dropRow != null || dropCard != null;
            }
            return cardDecision != null && cardDecision.Kind != DragMoveCardDropKind.None;
        }

        private bool IsInsideDropArea(Point pointer)
        {
            if (!scrollZone.IsReady)
            {
                return true;
            }
            return scrollZone.Contains(pointer);
        }

        /// <summary>页面给的“禁止区”判定（例如被拖方案自己的后代卡片）。</summary>
        private bool IsPointerForbidden(Point pointer)
        {
            if (host.IsDropPointForbidden == null)
            {
                return false;
            }
            try
            {
                return host.IsDropPointForbidden(pointer);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>指针是否在列表可视区里、最后一张卡片下方的空白处。</summary>
        private bool IsPointerBelowCards(Point pointer)
        {
            if (host.Mode != DragMoveMode.CardZones || cardSlots.Count == 0 || host.CardHost == null)
            {
                return false;
            }
            if (IsPointerForbidden(pointer))
            {
                return false;
            }
            if (!scrollZone.IsInList(pointer))
            {
                return false;
            }
            Point content;
            if (!TryContentPoint(pointer, out content))
            {
                return false;
            }
            return content.Y > cardSlots[cardSlots.Count - 1].Bottom + 2.0;
        }

        // ================================================================ 看门狗

        private void StartWatchdog()
        {
            if (watchdog == null)
            {
                watchdog = new DragWatchdog(
                    host.Window,
                    delegate { return active; },
                    delegate { return DragGestureSettings.IsMouseButtonDownPhysical(activeMouseButton); },
                    CancelInterrupted);
            }
            watchdog.Start();
        }

        private void StopWatchdog()
        {
            if (watchdog != null)
            {
                watchdog.Stop();
            }
        }


        // ================================================================ 提示线（行模式）

        /// <summary>清掉插入提示线（页面重建列表时也要调，否则线会留在已废弃的元素里）。</summary>
        internal void ClearInsertionLine()
        {
            insertionLine.Hide();
        }

        private void ShowRowInsertionLine(DragMoveRow target, bool insertAfter)
        {
            FrameworkElement rowElement = target == null ? null : target.Element;
            if (rowElement == null)
            {
                insertionLine.Hide();
                return;
            }
            Rect clip;
            if (!TryGetListClip(out clip))
            {
                clip = Rect.Empty;
            }
            insertionLine.ShowRow(rowElement, insertAfter, clip);
        }

        /// <summary>列表可视区在浮层里的矩形（把线裁在列表里，别画到工具栏上）。</summary>
        private bool TryGetListClip(out Rect clip)
        {
            clip = Rect.Empty;
            if (host.List == null || host.Layer == null)
            {
                return false;
            }
            try
            {
                Point origin = host.List.TranslatePoint(new Point(0.0, 0.0), host.Layer);
                if (host.List.ActualWidth <= 1.0 || host.List.ActualHeight <= 1.0)
                {
                    return false;
                }
                clip = new Rect(origin.X, origin.Y, host.List.ActualWidth, host.List.ActualHeight);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ================================================================ 卡片反馈（卡片模式）

        private void CaptureCardSlots()
        {
            cardSlots.Clear();
            cardElements.Clear();
            if (host.GetCardSlots == null)
            {
                return;
            }
            IList<DragMoveCardSlot> slots = host.GetCardSlots();
            if (slots == null)
            {
                return;
            }
            for (int index = 0; index < slots.Count; index++)
            {
                if (slots[index] != null)
                {
                    cardSlots.Add(slots[index]);
                }
            }
            cardSlots.Sort(delegate(DragMoveCardSlot left, DragMoveCardSlot right)
            {
                return left.Top.CompareTo(right.Top);
            });
            for (int index = 0; index < cardSlots.Count; index++)
            {
                FrameworkElement card = FindCard(cardSlots[index].Id);
                if (card != null)
                {
                    cardElements[cardSlots[index].Id] = card;
                }
            }
        }

        /// <summary>“腾开空间”要位移的元素；宿主没配时退回卡片本体。</summary>
        private FrameworkElement FindCardShift(string id)
        {
            if (host.FindCardShiftElement != null && !string.IsNullOrWhiteSpace(id))
            {
                try
                {
                    FrameworkElement shift = host.FindCardShiftElement(id);
                    if (shift != null)
                    {
                        return shift;
                    }
                }
                catch
                {
                }
            }
            return FindCard(id);
        }

        private FrameworkElement FindCard(string id)
        {
            if (host.FindCardElement == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            FrameworkElement cached;
            if (cardElements.TryGetValue(id, out cached) && cached != null)
            {
                return cached;
            }
            try
            {
                return host.FindCardElement(id);
            }
            catch
            {
                return null;
            }
        }

        private void ShowCardInsert(DragMoveCardDropDecision decision)
        {
            if (host.Layer == null || decision == null || cardSlots.Count == 0)
            {
                return;
            }
            // 缝的位置：插到第 Index 张之前（Index==Count 时插到最后一张之后）。
            // 口径与「启动与恢复」的 `TryGetProfileDropSeamRect` 一致：
            // 第一张之上 = 上沿再往外 4 像素；最后一张之下 = 下沿再往外 4 像素；
            // 两张之间 = 两张的**正中间**；夹着被拖卡片时贴指针那一侧且不腾位。
            double seamY;
            bool straddled = false;
            if (decision.Index <= 0)
            {
                seamY = DragInsertionGeometry.FirstSeam(cardSlots[0].Top);
            }
            else if (decision.Index >= cardSlots.Count)
            {
                seamY = DragInsertionGeometry.LastSeam(cardSlots[cardSlots.Count - 1].Bottom);
            }
            else
            {
                double draggedTop;
                double draggedBottom;
                bool hasDragged = TryGetDraggedCardBounds(out draggedTop, out draggedBottom);
                seamY = DragInsertionGeometry.BetweenSeam(
                    cardSlots[decision.Index - 1].Bottom,
                    cardSlots[decision.Index].Top,
                    GetContentPointerY(),
                    draggedTop,
                    draggedBottom,
                    hasDragged,
                    out straddled);
            }
            double left = cardSlots[0].Left;
            double width = cardSlots[0].Width;
            Point windowPoint;
            try
            {
                windowPoint = host.CardHost.TranslatePoint(new Point(left, seamY), host.Window);
            }
            catch
            {
                return;
            }
            Point layerPoint = host.Window.TranslatePoint(windowPoint, host.Layer);
            Rect clip;
            if (!TryGetListClip(out clip))
            {
                clip = Rect.Empty;
            }
            if (!insertionLine.ShowSeam(layerPoint.X, layerPoint.Y, width, clip))
            {
                // 线被列表可视区裁没了：不要再让卡片“腾开空间”，否则会出现“卡片让位但没有线”。
                insertionLine.ClearRoom();
                return;
            }
            if (straddled)
            {
                insertionLine.ClearRoom();
                return;
            }
            FrameworkElement upper = decision.Index > 0 && decision.Index - 1 < cardSlots.Count
                ? FindCardShift(cardSlots[decision.Index - 1].Id)
                : null;
            FrameworkElement lower = decision.Index < cardSlots.Count
                ? FindCardShift(cardSlots[decision.Index].Id)
                : null;
            insertionLine.SetRoom(upper, lower);
        }

        /// <summary>被拖那张卡片此刻在卡片容器内容坐标里的上下沿（用来判断“缝夹着它”）。</summary>
        private bool TryGetDraggedCardBounds(out double top, out double bottom)
        {
            top = 0.0;
            bottom = 0.0;
            if (sourceElement == null || host.CardHost == null)
            {
                return false;
            }
            try
            {
                Point origin = sourceElement.TranslatePoint(new Point(0.0, 0.0), host.CardHost);
                double height = sourceElement.ActualHeight;
                if (height <= 1.0)
                {
                    return false;
                }
                top = origin.Y;
                bottom = origin.Y + height;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private double GetContentPointerY()
        {
            Point pointer;
            if (!TryGetPointer(out pointer))
            {
                return 0.0;
            }
            Point content;
            if (!TryContentPoint(pointer, out content))
            {
                return 0.0;
            }
            return content.Y;
        }

        private void HideCardInsert()
        {
            insertionLine.Hide();
        }

        private void ShowCardMerge(DragMoveCardDropDecision decision)
        {
            if (decision == null || decision.Index < 0 || decision.Index >= cardSlots.Count)
            {
                return;
            }
            FrameworkElement card = FindCard(cardSlots[decision.Index].Id);
            if (card == null)
            {
                return;
            }
            string targetLabel = CardLabel(decision.Index);
            string mergeText = host.DescribeMergeHint == null
                ? null
                : host.DescribeMergeHint(movingLabel, targetLabel);
            if (string.IsNullOrWhiteSpace(mergeText))
            {
                mergeText = AppText.F(CommonTextKeys.DragHintMergeIntoOnlyFormat, targetLabel);
            }
            targetFrame.Show(card, mergeText);
        }

        private void HideCardMerge()
        {
            targetFrame.Hide();
        }

        private void ClearCardFeedback()
        {
            HideCardInsert();
            HideCardMerge();
        }
        // ================================================================ 工具

        private bool TryGetPointer(out Point windowPoint)
        {
            windowPoint = new Point(0.0, 0.0);
            NativeMethods.NativePoint cursor;
            if (!NativeMethods.GetCursorPos(out cursor))
            {
                return false;
            }
            try
            {
                windowPoint = host.Window.PointFromScreen(new Point(cursor.X, cursor.Y));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryContentPoint(Point windowPoint, out Point content)
        {
            content = new Point(0.0, 0.0);
            if (host.CardHost == null)
            {
                return false;
            }
            try
            {
                content = host.Window.TranslatePoint(windowPoint, host.CardHost);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void Hint(string text, DragMoveRow target)
        {
            if (host.ShowHint == null)
            {
                return;
            }
            try
            {
                host.ShowHint(text);
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (active)
            {
                FinishInternal(true);
            }
            // 页面重建列表/换工作区时可能还有残留的落点反馈（插入线、“腾开”位移、并入框），一并收干净。
            insertionLine.Hide();
            targetFrame.Dispose();
            forbiddenCursor.Dispose();
            scrollZone.Dispose();
            StopWatchdog();
            if (host.Ghost != null)
            {
                host.Ghost.Hide();
            }
            try { InputManager.Current.PreProcessInput -= OnPreProcessInput; } catch { }
            if (host.Window != null)
            {
                try
                {
                    host.Window.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnWindowMouseMove));
                    host.Window.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnWindowMouseUp));
                    host.Window.RemoveHandler(UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnWindowMouseUp));
                }
                catch
                {
                }
            }
        }
    }
}
