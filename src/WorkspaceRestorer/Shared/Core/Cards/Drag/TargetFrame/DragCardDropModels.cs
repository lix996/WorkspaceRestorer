using System;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 一张卡片在**内容坐标**里占的纵向范围（不是窗口坐标）。
    /// 用内容坐标是为了“滚动不影响判定”：卡片能跟着列表滚，但它在自己容器里的位置不变，
    /// 于是“指针落在哪张卡片的哪个带子里”不会因为滚动而反复翻。
    /// </summary>
    internal sealed class DragMoveCardSlot
    {
        public string Id;
        public double Left;
        public double Width;
        public double Top;
        public double Height;

        public double Bottom
        {
            get { return Top + Height; }
        }
    }

    /// <summary>
    /// 卡片缝隙的落点口径。
    ///
    /// 【为什么要有两种】公共引擎是从「启动与恢复」复制出来的，但「自启动应用」
    /// 的卡片列表经常只有一两张，被拖的那张又不在落点表里 —— 如果照「启动与恢复」
    /// 的“缝隙太大＝不能放”，拖第二张往上移时松手几乎永远落在空档里，数据一个字不改。
    /// 所以自启动应用改成“列表里面一定能放下（空档＝最近那道缝）”。
    /// 「启动与恢复」仍保持原来的严格口径：只有边缘插入带/卡缝里才算插入，太远＝取消。
    /// </summary>
    internal enum DragMoveCardGapMode
    {
        /// <summary>「启动与恢复」：缝隙超过插入带、或离最后一张太远 → 没有落点（取消）。</summary>
        StrictNone = 0,

        /// <summary>「自启动应用」：列表里面一定能放，空档算最近的那道缝。</summary>
        NearestSeam = 1
    }

    /// <summary>卡片落点类型。</summary>
    internal enum DragMoveCardDropKind
    {
        /// <summary>没有合法落点（指针在列表外，或两张卡片之间那片空档太大）。</summary>
        None = 0,
        /// <summary>插到某张卡片之前或之后（调整顺序）。</summary>
        Insert = 1,
        /// <summary>并入某张卡片（合并成一份）。</summary>
        Merge = 2
    }

    /// <summary>一次卡片落点判定（Insert 时 Index 表示“插到第 Index 张之前”，Index == Count 表示插到最后）。</summary>
    internal sealed class DragMoveCardDropDecision
    {
        public DragMoveCardDropKind Kind;
        public int Index;
        public string TargetId;

        public static DragMoveCardDropDecision None()
        {
            return new DragMoveCardDropDecision { Kind = DragMoveCardDropKind.None, Index = -1, TargetId = null };
        }

        public bool SameTarget(DragMoveCardDropDecision other)
        {
            if (other == null)
            {
                return Kind == DragMoveCardDropKind.None;
            }
            return Kind == other.Kind && Index == other.Index;
        }
    }
}
