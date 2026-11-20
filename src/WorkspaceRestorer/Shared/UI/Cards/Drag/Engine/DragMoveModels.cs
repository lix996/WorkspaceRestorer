using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共区域】拖动移动的数据模型与“卡片落点算法”（纯算术，没有 WPF 事件，可单元测试）。
    ///
    /// 2026-11-01 起三个工作区（启动与恢复、自启动应用、图标位置保存）都走
    /// `DragMoveSession`；这里只保留数据形状和落点口径，不再有“启动与恢复专用副本”。
    /// 启动与恢复通过 `StartupWorkspacePage.DragEngine.cs` 注入嵌套方案的业务回调。
    /// </summary>
    internal enum DragMoveMode
    {
        /// <summary>项目行拖动：插到某一行之前/之后（细插入线）。</summary>
        InsertRow = 0,

        /// <summary>卡片拖动：卡片边缘窄带＝插到那道缝（调顺序），卡片中间＝并入这张卡片。</summary>
        CardZones = 1
    }

    /// <summary>拖动时的“潜在落点行”（窗口坐标，按需刷新几何）。</summary>
    internal sealed class DragMoveRow
    {
        /// <summary>行对应的数据（自启动应用＝方案里的一个项目）。</summary>
        public object Payload;
        /// <summary>它属于谁（自启动应用＝它所在的 ApplicationPlan，用于跨方案搬移）。</summary>
        public object Owner;
        /// <summary>判矩形用的元素。</summary>
        public FrameworkElement Element;
        /// <summary>日志与提示里的名字。</summary>
        public string Label;
        /// <summary>拖动时缓存的行顶边（窗口坐标）。</summary>
        public double Top;
        /// <summary>拖动时缓存的行高。</summary>
        public double Height;
    }


    /// <summary>
    /// 拖动引擎的“宿主配置”：页面把自己这边的元素与回调交给引擎，引擎只负责拖动本身。
    ///
    /// 这样「自启动应用」「图标位置保存」可以共用同一套手感（浮动方框、插入线、禁止光标、
    /// 上/下放置条与自动滚动、被打断自动取消），而**各自的数据怎么改仍然留在自己的文件夹里**。
    /// </summary>
    internal sealed class DragMoveHost
    {
        /// <summary>主窗口（坐标换算、窗口级鼠标钩子）。</summary>
        public Window Window;

        /// <summary>拖动浮层（与「启动与恢复」共用的那块 Canvas）。</summary>
        public Canvas Layer;

        /// <summary>浮动方框（与「启动与恢复」共用同一个实例；样式/宽度/定位在 `DragGhostVisual`）。</summary>
        public DragGhostVisual Ghost;

        /// <summary>
        /// 这一帧指针是不是落在“禁止区”（例如被拖方案自己的后代卡片）。
        /// 返回 true 时引擎直接判 None：不画插入线/高亮，光标显示禁止，松手不改数据。
        /// </summary>
        public Func<Point, bool> IsDropPointForbidden;

        /// <summary>列表可视区（判“在不在列表里”、自动滚动、卡片模式把线裁在列表内）。</summary>
        public ScrollViewer List;

        /// <summary>上/下两块放置条（上＝移到最前，下＝移到最后）。</summary>
        public Border TopPad;
        public Border BottomPad;

        /// <summary>卡片模式下卡片的父容器（卡片矩形在这个容器的内容坐标里冻结）。</summary>
        public FrameworkElement CardHost;

        /// <summary>行模式 or 卡片模式。</summary>
        public DragMoveMode Mode = DragMoveMode.InsertRow;

        /// <summary>日志前缀（默认 “[拖动]”）。</summary>
        public string LogTag = "[拖动]";

        /// <summary>浮动方框上的文字（也用于日志“拖动项=”）。</summary>
        public Func<string> DescribeMoving;

        /// <summary>行模式：按窗口坐标找落点行（要跳过被拖的那一行；找不到返回 null＝取消）。</summary>
        public Func<Point, DragMoveRow> FindRowAt;

        /// <summary>行模式：松手落点。参数是落点行与“插到它后面”。</summary>
        public Action<DragMoveRow, bool> ApplyDrop;

        /// <summary>行模式：落点提示文字（页面写进状态栏/无障碍说明）。</summary>
        public Action<string> ShowHint;

        /// <summary>
        /// 行模式：落点提示的**完整一句话**（可选）。页面最清楚“这一项在方案里是第几位”，
        /// 所以由页面来写，口径与「启动与恢复」的 `ShowDropHint` 一致：
        /// `松手：把“甲一”插到第 2 位（在“甲三”之前）`。返回空字符串＝退回引擎自己那句。
        /// 【2026-10-18 加】用户要求“悬停提示要一致”，两边的位次口径不一样会让用户以为落点不同。
        /// </summary>
        public Func<DragMoveRow, bool, string> DescribeRowHint;

        /// <summary>松手落在上/下放置条：-1＝移到最前，+1＝移到最后。</summary>
        public Action<int> ApplyEdgeDrop;

        /// <summary>拖动被打断（物理左键松了却收不到 Up、或窗口失活）时的收尾回调。</summary>
        public Action OnCancelled;

        /// <summary>
        /// 每次拖动结束都会调一次（不管是放下了、取消了一行、还是自动取消）。
        /// 页面在这里清掉自己的“正在拖什么”。**它一定在 `ApplyDrop`/`ApplyCardDrop`/`ApplyEdgeDrop` 之后**，
        /// 所以落点回调里还能读到“拖的是哪一项”。
        /// 【2026-10-17 加】以前“落在空档里松手（＝取消）”这条路上没有任何回调，
        /// 页面一直以为还在拖某张卡片，松手后的第一次点击还会被当成“刚拖完”吞掉。
        /// </summary>
        public Action OnFinished;

        /// <summary>滚动速度倍率（来自总设置 `Settings.DragScrollSpeed`，三个工作区共用一个值）。</summary>
        public Func<double> ScrollSpeedFactor;

        /// <summary>卡片模式：起拖时调用一次，返回要参与判定的卡片矩形（内容坐标，已排除被拖的那张）。</summary>
        public Func<IList<DragMoveCardSlot>> GetCardSlots;

        /// <summary>卡片模式：按 Id 取卡片元素（画并入高亮用）。</summary>
        public Func<string, FrameworkElement> FindCardElement;

        /// <summary>
        /// 卡片模式：“腾开空间”要位移的元素（可选）。为空时退回 <see cref="FindCardElement"/>。
        /// 「启动与恢复」的方案卡片有展开后的子卡片，必须移动**整块**（含子卡片），
        /// 不能只移动卡片头，否则头会压到子卡片上；其它工作区返回卡片本身即可。
        /// </summary>
        public Func<string, FrameworkElement> FindCardShiftElement;

        /// <summary>卡片模式：按 Id 取这张卡片叫什么（落点提示与日志里用；元素本身没有无障碍名字）。</summary>
        public Func<string, string> DescribeCard;

        /// <summary>
        /// 行模式（2026-11-01）：指针不在任何落点行上时，找“能把它放进去的方案卡片”。
        /// 返回 null＝这里不放（引擎显示禁止符号）。页面负责排除“自己的卡片”和方案文件夹。
        /// 启动与恢复也走这个引擎，它的实现见 `StartupWorkspacePage.DragEngine.cs`。
        /// </summary>
        public Func<Point, DragMoveCardSlot> FindRowCardAt;

        /// <summary>行模式：落在方案卡片上时的提示文字（参数：卡片、松手位置）。</summary>
        public Func<DragMoveCardSlot, Point, string> DescribeRowCardHint;

        /// <summary>行模式：松手落在方案卡片上（页面在这里算“最近的那一项”并搬过去）。</summary>
        public Action<DragMoveCardSlot, Point> ApplyRowCardDrop;

        /// <summary>
        /// 卡片模式：自定义“松手并入”的提示。参数是（被拖卡片名, 目标卡片名）；
        /// 返回空则用默认的“两份方案合成一份”。图标位置保存用它说明“只分组，不合并图标位置”。
        /// </summary>
        public Func<string, string, string> DescribeMergeHint;

        /// <summary>卡片模式：松手落点（Insert＝调顺序，Merge＝并入）。</summary>
        public Action<DragMoveCardDropDecision> ApplyCardDrop;

        /// <summary>
        /// 卡片模式是否允许“拖到卡片中间＝并入”。三个工作区默认 true；
        /// 「图标位置保存」再用 <see cref="CanMergeCard"/> 限定只有布局文件夹能收子布局。
        /// </summary>
        public bool AllowCardMerge = true;

        /// <summary>
        /// 卡片模式：某张卡片是否真的能接收“并入”。返回 false 时引擎把 Merge 改判成最近插入缝。
        /// 为空 = 所有卡片都可并入（启动与恢复、自启动应用保持原行为）。
        /// </summary>
        public Func<string, bool> CanMergeCard;

        /// <summary>
        /// 卡片缝隙的落点口径：
        /// · 「启动与恢复」用 <see cref="DragMoveCardGapMode.StrictNone"/>（缝隙太大＝取消，保持原行为）；
        /// · 「自启动应用」「图标位置保存」用默认的 <see cref="DragMoveCardGapMode.NearestSeam"/>。
        /// 这两个口径在 2026-10-17 就已经分叉，不是同一份算法；不要一律改成同一种。
        /// </summary>
        public DragMoveCardGapMode CardGapMode = DragMoveCardGapMode.NearestSeam;
    }
}
