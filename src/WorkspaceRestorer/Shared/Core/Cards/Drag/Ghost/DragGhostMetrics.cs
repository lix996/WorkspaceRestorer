using System;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 【纯逻辑】浮动方框的尺寸口径（不碰任何控件，可单测）。
    ///
    /// 用户 2026-10-16 定稿：方框要像“那一行的复制品”，宽度＝来源宽减 16，
    /// 但不小于 240；文字最多 640 宽（超过用省略号）。
    /// 三个工作区、条目/卡片两种拖动共用这一份，不再各写一遍 Math.Max。
    /// </summary>
    public static class DragGhostMetrics
    {
        /// <summary>方框最小宽度（DIP）。</summary>
        public const double MinWidth = 240.0;

        /// <summary>比来源控件窄多少：留出一点边，看起来像“从行里拿起来”。</summary>
        public const double WidthInset = 16.0;

        /// <summary>方框文字的最大宽度（DIP）。</summary>
        public const double MaxTextWidth = 640.0;

        /// <summary>来源宽未知时的兜底宽度（与旧代码 PositionGhost 的 320 一致）。</summary>
        public const double FallbackWidth = 320.0;

        /// <summary>按来源控件宽度算方框宽度。</summary>
        public static double WidthFor(double sourceWidth)
        {
            if (sourceWidth <= 0.0 || double.IsNaN(sourceWidth) || double.IsInfinity(sourceWidth))
            {
                return MinWidth;
            }
            double width = sourceWidth - WidthInset;
            if (width < MinWidth)
            {
                width = MinWidth;
            }
            return width;
        }
    }
}
