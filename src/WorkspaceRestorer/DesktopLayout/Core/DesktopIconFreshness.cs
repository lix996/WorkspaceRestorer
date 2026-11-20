using System;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 判断桌面上的某个图标是不是“删除后又新建”的同一个名字。
    ///
    /// Windows 的图标身份只看路径/名称；把“123.txt”删掉再新建一个同名文件，
    /// 身份仍然是“name:123.TXT”。如果只按身份对比，编辑布局时会继续沿用
    /// 旧布局里的位置，看起来就像新文件没有出现在“新图标”里（2026-10-23 用户报的严重问题）。
    /// 这里用文件创建时间和布局第一次创建时间比较；如果 sidecar 里记录了
    /// 这个图标上次保存时的创建时间，就以那个时间为准。布局的“最后保存时间”
    /// 不能当基准：用户可能刚点了保存，时间会晚于文件创建，反而把重建文件盖过去。
    /// </summary>
    public static class DesktopIconFreshness
    {
        private const double TimestampToleranceSeconds = 2.0;

        public static bool RecreatedAfterLayout(
            DesktopIconPlacement live,
            DesktopIconPlacement saved,
            DateTime layoutCreatedUtc,
            DateTime layoutUpdatedUtc)
        {
            if (live == null || live.CreatedUtc == DateTime.MinValue)
            {
                return false;
            }
            DateTime baseline = DateTime.MinValue;
            if (saved != null && saved.CreatedUtc != DateTime.MinValue)
            {
                baseline = saved.CreatedUtc;
            }
            else if (layoutCreatedUtc != DateTime.MinValue)
            {
                // 旧档案没有每个图标的创建时间。用布局的创建时间做保守兜底；
                // 但如果布局是刚建好就保存的（例如“新建布局→马上保存当前桌面”），
                // 用创建时间会把刚保存的图标全判成新图标，这时改用保存时间。
                baseline = layoutCreatedUtc;
                if (layoutUpdatedUtc != DateTime.MinValue
                    && layoutUpdatedUtc >= layoutCreatedUtc
                    && layoutUpdatedUtc - layoutCreatedUtc < TimeSpan.FromHours(1.0))
                {
                    baseline = layoutUpdatedUtc;
                }
            }
            if (baseline == DateTime.MinValue)
            {
                return false;
            }
            return live.CreatedUtc > baseline.AddSeconds(TimestampToleranceSeconds);
        }
    }
}
