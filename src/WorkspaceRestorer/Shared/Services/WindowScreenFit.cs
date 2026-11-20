using System;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 把“保存下来的窗口位置和大小”换算成“现在这块屏幕上一定看得见的位置和大小”。
    ///
    /// 【为什么需要（用户 2026-10-17 原话）】
    /// “恢复时，如果屏幕（防止用户是外接显示屏，后面改为笔记本）大小变了，那么恢复时的窗口的
    ///   某些部分不能在屏幕之外多少（这个外面是多少你自己取合适的），一定要拉回屏幕内。”
    ///
    /// 典型场景：保存方案时接着 2560×1440 的外接显示器，窗摆在 x=2400；第二天只带笔记本
    ///（1920×1080），屏幕一换，保存的坐标就落在一块**不存在的屏幕**上了。
    /// 原来的做法只把 X/Y 夹进“离它最近的那块屏幕”，看起来没错，但漏了两种情况：
    ///   ① 窗口比屏幕还大（2560 宽摆到 1920 的屏上）——X/Y 夹到 0 之后，右边 640 像素
    ///      仍然在屏幕外，用户**拖不回来**（只能靠快捷键最大化）；
    ///   ② 顶部越界——标题栏跑到屏幕上边以外，连拖都拖不到。
    ///
    /// 【规则（“外面是多少”取 16 像素）】
    ///   · 左 / 右 / 下：最多允许露在工作区外 <see cref="MaxOffScreenMargin"/> 像素；
    ///   · 上：**一像素都不留**在屏幕上边以外（标题栏必须整条看得见，否则没法抓回来）；
    ///   · 窗口比工作区还大：先把宽 / 高缩到工作区大小，再谈位置
    ///     （不缩的话“露在外面”永远超过上限，怎么挪都有一大块够不着）。
    ///
    /// 这里只做**纯算术**：显示器是哪一块、它的工作区是多少，由
    /// <see cref="WindowPlacementService"/> 用 Win32 查好再传进来。
    /// 这样这段最容易算错的几何逻辑可以脱离显示器配置、直接写单元测试。
    ///
    /// 坐标口径：物理像素，与 `SavedWindowPlacement.Left/Top/Width/Height` 完全一致，
    /// 这里不做 DPI 换算（保存和恢复两端都是物理像素，换算是多余且会引入偏差的）。
    /// </summary>
    internal static class WindowScreenFit
    {
        /// <summary>
        /// 允许露在屏幕外的最大像素数（左 / 右 / 下三边）。
        /// 取 16：比一根滚动条还窄，看着就是“贴边”，但仍然保留用户故意把窗口
        /// 稍微推出屏幕一点的习惯，不会被硬掰回来。
        /// </summary>
        internal const int MaxOffScreenMargin = 16;

        /// <summary>
        /// 保存记录里没有宽高（旧数据 `HasBounds = false` 之外的那种“只有坐标”的记录）时，
        /// 按 220×220 估算可见性。与恢复时“不知道大小就不改大小”的策略配套使用。
        /// </summary>
        internal const int UnknownSizeFallback = 220;

        /// <summary>
        /// 把窗口矩形拟合进工作区。
        /// </summary>
        /// <param name="left">保存的窗口左边缘（屏幕物理像素，可以是负数，多屏在左边时就是负数）。</param>
        /// <param name="top">保存的窗口上边缘。</param>
        /// <param name="width">保存的窗口宽度；&lt;= 0 表示不知道，按 <see cref="UnknownSizeFallback"/> 估。</param>
        /// <param name="height">保存的窗口高度；&lt;= 0 表示不知道，按 <see cref="UnknownSizeFallback"/> 估。</param>
        /// <param name="workArea">目标显示器的工作区，四个元素依次是 左、上、右、下。</param>
        /// <returns>四个元素：拟合后的 左、上、宽、高。
        /// 宽高是**可能被缩小过的值**，调用方如果原本不知道大小（传了 0），不要拿它去改窗口大小。</returns>
        internal static int[] Fit(int left, int top, int width, int height, int[] workArea)
        {
            int areaLeft = 0;
            int areaTop = 0;
            int areaRight = 0;
            int areaBottom = 0;
            if (workArea != null && workArea.Length >= 4)
            {
                areaLeft = workArea[0];
                areaTop = workArea[1];
                areaRight = workArea[2];
                areaBottom = workArea[3];
            }
            // 工作区坏掉（右<=左 等）时不能把窗口挪到奇怪的地方去，原样返回。
            if (areaRight <= areaLeft || areaBottom <= areaTop)
            {
                return new[] { left, top, width > 0 ? width : UnknownSizeFallback, height > 0 ? height : UnknownSizeFallback };
            }

            int areaWidth = areaRight - areaLeft;
            int areaHeight = areaBottom - areaTop;

            int fittedWidth = width > 0 ? width : UnknownSizeFallback;
            int fittedHeight = height > 0 ? height : UnknownSizeFallback;
            bool widthShrunk = fittedWidth > areaWidth;
            bool heightShrunk = fittedHeight > areaHeight;
            if (widthShrunk)
            {
                fittedWidth = areaWidth;
            }
            if (heightShrunk)
            {
                fittedHeight = areaHeight;
            }

            // 横向：左边最多露 MaxOffScreenMargin，右边也最多露这么多。
            // 因为 fittedWidth <= areaWidth，这个区间的下界一定 <= 上界，不会夹出反值。
            // 只有“稍微露出去”（16 像素以内）才保留原样；一旦超过 16 像素，就完全贴回
            // 工作区边缘，不能只拉到 -16/右边多 16——那看起来仍然是“卡在屏幕外”。
            int minLeft = areaLeft - MaxOffScreenMargin;
            int maxLeft = areaRight - fittedWidth + MaxOffScreenMargin;
            int fittedLeft = left < minLeft
                ? areaLeft
                : (left > maxLeft ? areaRight - fittedWidth : left);

            // 纵向：上边一像素都不许露（保证标题栏整条可见），下边同样只在 16 像素内保留。
            int minTop = areaTop;
            int maxTop = areaBottom - fittedHeight + MaxOffScreenMargin;
            int fittedTop = top < minTop
                ? areaTop
                : (top > maxTop ? areaBottom - fittedHeight : top);

            // 被缩过的方向直接贴住工作区左上角：这种情况下“保留原坐标再夹”没有意义
            // （窗口本来就比屏幕大，夹完还会一边 16 像素在外），
            // 贴边后就是“整块屏幕都给它”，与用户按 Win+↑ 最大化的观感一致。
            if (widthShrunk)
            {
                fittedLeft = areaLeft;
            }
            if (heightShrunk)
            {
                fittedTop = areaTop;
            }

            return new[] { fittedLeft, fittedTop, fittedWidth, fittedHeight };
        }
    }
}
