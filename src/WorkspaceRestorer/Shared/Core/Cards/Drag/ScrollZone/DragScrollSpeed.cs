using System;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 拖动到上/下滚动带时的滚动速度（三个工作区共用）。
    ///
    /// 【2026-10-20 用户报】“在滚动区域内靠近方案的一侧会慢，远离方案的一侧会快。”
    /// 以前的公式是 170～850 像素/秒（越靠带子外沿越快），所以同一个带子里，
    /// 靠近列表的内侧很慢、靠外侧很快，速度不受总设置单独控制。
    /// 现在整个带子用同一个速度：只由总设置里的倍率决定，不再随指针深浅变化。
    ///
    /// 1.0× 时约 600 像素/秒；倍率范围仍是 0.5–3.0。
    /// </summary>
    internal static class DragScrollSpeed
    {
        /// <summary>倍率 1.0 时每秒滚动的像素数。</summary>
        public const double BasePixelsPerSecond = 600.0;

        /// <summary>把总设置里的倍率换算成像素/秒；超出范围按 0.5–3.0 夹。</summary>
        public static double PixelsPerSecond(double factor)
        {
            double clamped = factor;
            if (double.IsNaN(clamped) || double.IsInfinity(clamped))
            {
                clamped = 1.0;
            }
            clamped = Math.Max(0.5, Math.Min(3.0, clamped));
            return BasePixelsPerSecond * clamped;
        }
    }
}
