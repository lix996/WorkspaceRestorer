using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Services
{
    /// <summary>一块显示器的工作区（物理像素，已扣任务栏）。纯数据，方便单测塞“假的多屏”。</summary>
    internal sealed class MonitorWorkArea
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        /// <summary>Windows 标的主显示器（MONITORINFOF_PRIMARY）。</summary>
        public bool IsPrimary;

        internal int Width
        {
            get { return Right - Left; }
        }

        internal int Height
        {
            get { return Bottom - Top; }
        }

        internal bool HasArea
        {
            get { return Width > 0 && Height > 0; }
        }

        internal bool Contains(int x, int y)
        {
            return x >= Left && x <= Right && y >= Top && y <= Bottom;
        }

        /// <summary>点到这块矩形的欧氏距离平方（点在矩形内＝0）。多屏时用它挑“最近的那块”。</summary>
        internal long DistanceSquared(int x, int y)
        {
            long dx = x < Left ? (long)Left - x : (x > Right ? (long)x - Right : 0L);
            long dy = y < Top ? (long)Top - y : (y > Bottom ? (long)y - Bottom : 0L);
            return dx * dx + dy * dy;
        }

        internal string Key
        {
            get { return Left + "," + Top + "," + Right + "," + Bottom; }
        }
    }

    /// <summary>一次“这个窗口该放到哪”的结论。</summary>
    internal sealed class MonitorFitResult
    {
        public bool HasTarget;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int MonitorIndex = -1;
        public bool TopologyChanged;
        public bool PlacedOnPrimary;
        public string Reason;
    }

    /// <summary>
    /// 【启动与恢复】多屏拓扑适配（用户 2026-10-18）：
    /// “要兼顾屏幕变小/变大（笔记本外接显示器，外接显示器断掉转笔记本，或者显示器大小变化），
    ///  以及双屏、三屏幕、多屏的应用存放位置（扩展或复制），还有他们相互转换例如三屏变单屏等，
    ///  但是单屏变双屏，那么就在主屏幕上。”
    ///
    /// 原来的做法：哪个屏离保存的坐标最近就放哪块，然后 `WindowScreenFit` 夹回可见区。
    /// 换屏变小能兜住，但**多屏变少屏**时所有落在已消失显示器上的窗口会被夹到同一块屏的同一侧，
    /// 彼此完全重叠；也**不知道自己保存时是几块屏**，没法按“保存时第几块屏”去对应。
    ///
    /// 现在的规则（纯算术，全部可单测）：
    ///  1. 保存时和现在**屏数相同** → 按“保存时第几块屏”对应到现在的同序号屏（排列换了也跟着走）；
    ///     序号不可用时退回“离保存坐标最近的那块”；
    ///  2. **多了屏**（单屏 → 双屏/三屏）→ 放到**主屏**（用户原话：“单屏变双屏，那么就在主屏幕上”）；
    ///  3. **少了屏**（三屏 → 单屏、拔掉外接）→ 如果保存时那块屏还在（坐标仍落在某块屏里）就留它，
    ///     否则放到主屏；
    ///  4. 只要是“拓扑变了”才把窗口**阶梯错开**（每多一个 +32 像素，越界就回到工作区左上角），
    ///     免得一堆窗口全叠在同一点；拓扑没变时一个像素都不动（不打扰正常恢复）；
    ///  5. 大小/越界一律交给 `WindowScreenFit.Fit`（已有单测）。
    /// 复制模式（两块屏坐标完全一样）在 <see cref="Normalize"/> 里按矩形去重，只算一块屏。
    /// </summary>
    internal static class MonitorTopologyFit
    {
        /// <summary>拓扑变了、多个窗口挤到同一块屏时，每个窗口错开的像素数。</summary>
        internal const int CascadeStep = 32;

        /// <summary>
        /// 整理显示器列表：按矩形去重（复制模式）、按左/上排序、保证有且只有一个“主屏”。
        /// </summary>
        internal static List<MonitorWorkArea> Normalize(IEnumerable<MonitorWorkArea> monitors)
        {
            List<MonitorWorkArea> list = new List<MonitorWorkArea>();
            if (monitors != null)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (MonitorWorkArea monitor in monitors)
                {
                    if (monitor == null || !monitor.HasArea)
                    {
                        continue;
                    }
                    if (!seen.Add(monitor.Key))
                    {
                        continue;   // 复制模式：同一块坐标只算一次
                    }
                    list.Add(monitor);
                }
            }
            list.Sort(delegate(MonitorWorkArea left, MonitorWorkArea right)
            {
                int byLeft = left.Left.CompareTo(right.Left);
                return byLeft != 0 ? byLeft : left.Top.CompareTo(right.Top);
            });
            if (list.Count == 0)
            {
                return list;
            }
            int primaryIndex = -1;
            for (int index = 0; index < list.Count; index++)
            {
                if (list[index].IsPrimary)
                {
                    primaryIndex = index;
                    break;
                }
            }
            if (primaryIndex < 0)
            {
                // 没有标主屏（例如测试塞的假数据）：按 Windows 的习惯把最左上的当主屏。
                list[0].IsPrimary = true;
                primaryIndex = 0;
            }
            for (int index = 0; index < list.Count; index++)
            {
                list[index].IsPrimary = index == primaryIndex;
            }
            return list;
        }

        /// <summary>
        /// 算出这个窗口现在该落在哪、多大。返回 null ＝ 没有可用显示器（调用方保持原样）。
        /// </summary>
        /// <param name="savedLeft">保存的左边缘（物理像素）。</param>
        /// <param name="savedMonitorIndex">保存时它落在第几块屏（按左/上排序；旧数据 -1＝不知道）。</param>
        /// <param name="savedMonitorCount">保存时一共几块屏（旧数据 0＝不知道）。</param>
        /// <param name="alreadyPlacedOnTarget">这块屏上本次恢复已经放了几个窗口（用于阶梯错开）。</param>
        internal static MonitorFitResult Decide(
            IList<MonitorWorkArea> monitors,
            int savedLeft,
            int savedTop,
            int savedWidth,
            int savedHeight,
            int savedMonitorIndex,
            int savedMonitorCount,
            int alreadyPlacedOnTarget)
        {
            List<MonitorWorkArea> list = Normalize(monitors);
            if (list.Count == 0)
            {
                return null;
            }

            int currentCount = list.Count;
            int primaryIndex = 0;
            for (int index = 0; index < list.Count; index++)
            {
                if (list[index].IsPrimary)
                {
                    primaryIndex = index;
                    break;
                }
            }
            bool savedKnown = savedMonitorCount > 0 && savedMonitorIndex >= 0 && savedMonitorIndex < savedMonitorCount;
            bool topologyChanged = savedKnown && currentCount != savedMonitorCount;
            int targetIndex;
            string reason;

            if (!savedKnown)
            {
                // 旧存档：只知道坐标，沿用“离它最近的那块屏”。
                targetIndex = NearestIndex(list, savedLeft, savedTop);
                reason = "旧存档没有屏数信息 → 离保存坐标最近的第 " + (targetIndex + 1) + " 块屏";
            }
            else if (currentCount > savedMonitorCount)
            {
                targetIndex = primaryIndex;
                reason = "屏变多了（保存时 " + savedMonitorCount + " 块 → 现在 " + currentCount
                    + " 块）→ 放主屏";
            }
            else if (currentCount < savedMonitorCount)
            {
                int containing = IndexContaining(list, savedLeft, savedTop);
                if (containing >= 0)
                {
                    targetIndex = containing;
                    reason = "屏变少了（" + savedMonitorCount + " → " + currentCount
                        + " 块），但保存时那块屏还在 → 原地保留";
                }
                else
                {
                    targetIndex = primaryIndex;
                    reason = "屏变少了（保存时 " + savedMonitorCount + " 块 → 现在 " + currentCount
                        + " 块，原来那块已拔掉）→ 放主屏";
                }
            }
            else
            {
                // 屏数没变：**先看保存的坐标现在是否还落在某块屏里** —— 是就原地不动（最常见，
                // 什么都不改最不打扰）。不在任何一块屏里（排列换了/分辨率变了）才按保存时的屏序号对应。
                int containing = IndexContaining(list, savedLeft, savedTop);
                if (containing >= 0)
                {
                    targetIndex = containing;
                    reason = "屏数没变（" + currentCount + " 块），保存位置仍在第 "
                        + (containing + 1) + " 块屏里 → 原地";
                }
                else if (savedMonitorIndex >= 0 && savedMonitorIndex < currentCount)
                {
                    targetIndex = savedMonitorIndex;
                    reason = "屏数没变（" + currentCount + " 块）但原位置已不在任何屏里 → 按保存时第 "
                        + (savedMonitorIndex + 1) + " 块屏";
                }
                else
                {
                    targetIndex = NearestIndex(list, savedLeft, savedTop);
                    reason = "屏数没变、保存序号无效 → 离保存坐标最近的第 " + (targetIndex + 1) + " 块屏";
                }
            }

            MonitorWorkArea area = list[targetIndex];
            int[] fitted = WindowScreenFit.Fit(
                savedLeft,
                savedTop,
                savedWidth,
                savedHeight,
                new[] { area.Left, area.Top, area.Right, area.Bottom });

            MonitorFitResult result = new MonitorFitResult();
            result.HasTarget = true;
            result.MonitorIndex = targetIndex;
            result.TopologyChanged = topologyChanged;
            result.PlacedOnPrimary = targetIndex == primaryIndex;
            result.Reason = reason;
            result.Left = fitted[0];
            result.Top = fitted[1];
            result.Width = fitted[2];
            result.Height = fitted[3];

            // 拓扑变了才错开：一堆本来在别的屏上的窗口挤过来，不错开会全叠在最左边。
            if (topologyChanged && alreadyPlacedOnTarget > 0)
            {
                Cascade(result, area, alreadyPlacedOnTarget);
            }
            return result;
        }

        /// <summary>把窗口沿右下方向阶梯错开，越过工作区就回卷到左上角（仍然保证整块在工作区里）。</summary>
        internal static void Cascade(MonitorFitResult result, MonitorWorkArea area, int alreadyPlaced)
        {
            if (result == null || area == null || alreadyPlaced <= 0)
            {
                return;
            }
            int offset = alreadyPlaced * CascadeStep;
            int width = result.Width > 0 ? result.Width : WindowScreenFit.UnknownSizeFallback;
            int height = result.Height > 0 ? result.Height : WindowScreenFit.UnknownSizeFallback;
            int maxLeft = Math.Max(area.Left, area.Right - width);
            int maxTop = Math.Max(area.Top, area.Bottom - height);
            int left = result.Left + offset;
            int top = result.Top + offset;
            if (left > maxLeft)
            {
                left = area.Left + (left - maxLeft) % Math.Max(1, maxLeft - area.Left + 1);
            }
            if (top > maxTop)
            {
                top = area.Top + (top - maxTop) % Math.Max(1, maxTop - area.Top + 1);
            }
            result.Left = Math.Min(Math.Max(left, area.Left), maxLeft);
            result.Top = Math.Min(Math.Max(top, area.Top), maxTop);
        }

        private static int NearestIndex(IList<MonitorWorkArea> monitors, int x, int y)
        {
            int best = 0;
            long bestDistance = long.MaxValue;
            for (int index = 0; index < monitors.Count; index++)
            {
                long distance = monitors[index].DistanceSquared(x, y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = index;
                }
            }
            return best;
        }

        private static int IndexContaining(IList<MonitorWorkArea> monitors, int x, int y)
        {
            for (int index = 0; index < monitors.Count; index++)
            {
                if (monitors[index].Contains(x, y))
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
