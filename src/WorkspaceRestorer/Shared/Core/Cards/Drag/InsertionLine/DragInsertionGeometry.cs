using System;
using System.Windows;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 【纯逻辑】分割辅助线的几何：行线矩形、卡片缝位置、按列表可视区裁剪。
    ///
    /// 口径与「启动与恢复」的 `ShowInsertionLine` / `ShowProfileDropInsert` 一致：
    ///  · 行线 2 像素，画在行的上沿或下沿（中心压在边线上，所以 Y 减 1）；
    ///  · 卡片缝线 3 像素，以缝为中心（Y 减 1.5）；
    ///  · 第一张之上 = 上沿再往外 4；最后一张之下 = 下沿再往外 4；两张之间 = 正中间；
    ///  · 两张邻居之间夹着被拖的那张卡片时，线贴指针那一侧的缝，并告诉调用方“不要腾位”。
    /// </summary>
    public static class DragInsertionGeometry
    {
        /// <summary>行线高度。</summary>
        public const double RowLineHeight = 2.0;

        /// <summary>卡片缝线高度。</summary>
        public const double CardLineHeight = 3.0;

        /// <summary>首/末卡片外侧的额外间距。</summary>
        public const double EdgeGap = 4.0;

        /// <summary>裁剪后短于这个宽度就不画（防止在窗口边缘出现一截线头）。</summary>
        public const double MinVisibleWidth = 40.0;

        /// <summary>行线的矩形：锚点元素的左上角 + 宽高，插到它前面/后面。</summary>
        public static Rect RowLine(Rect anchor, bool insertAfter)
        {
            double y = anchor.Y + (insertAfter ? anchor.Height : 0.0) - (RowLineHeight / 2.0);
            return new Rect(anchor.X, y, anchor.Width, RowLineHeight);
        }

        /// <summary>第一张卡片之上：上沿再往外 4 像素。</summary>
        public static double FirstSeam(double firstTop)
        {
            return firstTop - EdgeGap;
        }

        /// <summary>最后一张卡片之下：下沿再往外 4 像素。</summary>
        public static double LastSeam(double lastBottom)
        {
            return lastBottom + EdgeGap;
        }

        /// <summary>
        /// 两张邻居之间的缝。
        /// <paramref name="draggedTop"/> / <paramref name="draggedBottom"/> 只在
        /// <paramref name="hasDragged"/> 为真时有效；被拖卡片夹在两邻居之间时，
        /// 线贴指针那一侧（上半贴上邻居下沿、下半贴下邻居上沿），并返回 straddled=true。
        /// </summary>
        public static double BetweenSeam(
            double upperBottom,
            double lowerTop,
            double pointerY,
            double draggedTop,
            double draggedBottom,
            bool hasDragged,
            out bool straddled)
        {
            straddled = false;
            double gap = lowerTop - upperBottom;
            if (hasDragged
                && draggedTop >= upperBottom - 1.0
                && draggedBottom <= lowerTop + 1.0
                && gap > (draggedBottom - draggedTop))
            {
                straddled = true;
                double middle = (draggedTop + draggedBottom) / 2.0;
                return pointerY < middle ? upperBottom + EdgeGap : lowerTop - EdgeGap;
            }
            return (upperBottom + lowerTop) / 2.0;
        }

        /// <summary>把线按列表可视区横向裁掉；纵向完全在外面时返回 false。</summary>
        public static bool Clip(ref Rect line, Rect clip)
        {
            if (clip.IsEmpty || clip.Width <= 0.0 || clip.Height <= 0.0)
            {
                return true;
            }
            if (line.Bottom < clip.Top - 2.0 || line.Top > clip.Bottom + 2.0)
            {
                return false;
            }
            double left = Math.Max(line.Left, clip.Left);
            double right = Math.Min(line.Right, clip.Right);
            if (right - left < MinVisibleWidth)
            {
                return false;
            }
            line = new Rect(left, line.Y, right - left, line.Height);
            return true;
        }
    }
}
