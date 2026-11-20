using System;
using System.Windows;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 【纯逻辑】“指针在不在可放区”的矩形判定（不碰控件，可单测）。
    ///
    /// 可放区只有三块：列表本身、列表上方的滚动/放置条、列表下方的滚动/放置条。
    /// 列表里的空白不算可放——那里松手是取消；真正“能不能放”还要看是否有落点行/卡片，
    /// 由各工作区的状态机决定。
    ///
    /// 边线容差 6 像素：松手时指针压在列表/放置条的边线上（差一两个像素）不能被判成外面。
    /// 【2026-11-01 用户拍板】滚动带取消原来的 8 像素内缩：亮起＝能放＝能滚。
    /// </summary>
    public static class DragDropArea
    {
        /// <summary>列表/放置条边线的容差（像素）。</summary>
        public const double Tolerance = 6.0;

        /// <summary>把一个矩形向外扩 <paramref name="amount"/> 像素。</summary>
        public static Rect Inflate(Rect rect, double amount)
        {
            if (rect.IsEmpty || amount == 0.0)
            {
                return rect;
            }
            Rect grown = rect;
            grown.Inflate(amount, amount);
            return grown;
        }

        /// <summary>指针是否在“列表 + 上下放置条”这三块区域的容差范围内。</summary>
        public static bool Contains(Rect list, Rect topZone, Rect bottomZone, Point point)
        {
            if (!list.IsEmpty && Inflate(list, Tolerance).Contains(point))
            {
                return true;
            }
            if (!topZone.IsEmpty && Inflate(topZone, Tolerance).Contains(point))
            {
                return true;
            }
            return !bottomZone.IsEmpty && Inflate(bottomZone, Tolerance).Contains(point);
        }

        /// <summary>指针是否在上下放置条的容差范围内（不含列表本身）。</summary>
        public static bool IsInZone(Rect topZone, Rect bottomZone, Point point)
        {
            if (!topZone.IsEmpty && Inflate(topZone, Tolerance).Contains(point))
            {
                return true;
            }
            return !bottomZone.IsEmpty && Inflate(bottomZone, Tolerance).Contains(point);
        }
    }
}
