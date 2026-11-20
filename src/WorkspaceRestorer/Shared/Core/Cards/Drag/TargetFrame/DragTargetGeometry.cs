using System;
using System.Windows;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 【纯逻辑】边缘高亮框 / 气泡的位置与最小尺寸（不碰控件，可单测）。
    ///
    /// 口径与「启动与恢复」原来的 `ShowProfileDropMerge` 一致：
    ///  · 框盖在目标卡片外框上，宽高至少 40×20；
    ///  · 气泡贴在浮动方框右下：右对齐、方框下沿再往下 6 像素。
    /// </summary>
    public static class DragTargetGeometry
    {
        /// <summary>高亮框最小宽度。</summary>
        public const double MinFrameWidth = 40.0;

        /// <summary>高亮框最小高度。</summary>
        public const double MinFrameHeight = 20.0;

        /// <summary>气泡与浮动方框之间的间距。</summary>
        public const double BubbleGap = 6.0;

        /// <summary>高亮框矩形（目标元素在浮层里的左上角 + 实际宽高）。</summary>
        public static Rect FrameRect(Point targetOrigin, double targetWidth, double targetHeight)
        {
            double width = targetWidth > 1.0 ? targetWidth : MinFrameWidth;
            double height = targetHeight > 1.0 ? targetHeight : MinFrameHeight;
            if (width < MinFrameWidth)
            {
                width = MinFrameWidth;
            }
            if (height < MinFrameHeight)
            {
                height = MinFrameHeight;
            }
            return new Rect(targetOrigin.X, targetOrigin.Y, width, height);
        }

        /// <summary>
        /// 气泡左上角：与浮动方框右对齐（放不下时贴左边），纵向挂在方框下沿 + 6。
        /// 返回的点是浮层坐标。
        /// </summary>
        public static Point BubblePosition(
            double ghostLeft,
            double ghostTop,
            double ghostWidth,
            double ghostHeight,
            double bubbleWidth)
        {
            double width = ghostWidth > 1.0 ? ghostWidth : DragGhostMetrics.MinWidth;
            double height = ghostHeight > 1.0 ? ghostHeight : 44.0;
            double hintWidth = bubbleWidth > 1.0 ? bubbleWidth : 160.0;
            double x = ghostLeft + Math.Max(0.0, width - hintWidth);
            double y = ghostTop + height + BubbleGap;
            return new Point(x, y);
        }
    }
}
