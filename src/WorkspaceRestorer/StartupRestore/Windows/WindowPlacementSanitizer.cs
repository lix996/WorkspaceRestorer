using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    internal enum WindowPlacementSanitizeAction
    {
        Keep,
        Clamp,
        Skip
    }

    internal sealed class WindowPlacementSanitizeResult
    {
        internal WindowPlacementSanitizeAction Action { get; set; }
        internal SavedWindowPlacement Placement { get; set; }
        internal string Reason { get; set; }
    }

    /// <summary>
    /// 恢复前体检：在真正启动程序之前，先看保存的窗口坐标在**当前**屏幕上是否还有
    /// 够用的可见区域。
    ///
    /// 这一层解决的是“保存坐标本身就不能用”：
    ///   · 夸克关闭到托盘留下的 -32000,-32000（状态却是 Normal）→ 直接跳过，不启动；
    ///   · 拔掉外接屏后，原来在 x=5000 的窗口 → 先夹到最近的屏幕，再启动；
    ///   · 真正最小化、NormalPosition 却是 -32000 的窗口 → 保留最小化，但把
    ///     NormalPosition 修到可见位置，避免以后从任务栏恢复时又跑到屏幕外。
    ///
    /// 它只做“启动前”的决定；启动后应用自己又把窗口挪走的情况，由
    /// <see cref="WindowRestoreAudit"/> 兜底。
    /// </summary>
    internal static class WindowPlacementSanitizer
    {
        internal static WindowPlacementSanitizeResult Sanitize(SavedWindowPlacement placement)
        {
            if (placement == null || !placement.HasBounds)
            {
                return Keep(placement);
            }

            if (WindowVisibility.IsHiddenPlacement(placement))
            {
                return new WindowPlacementSanitizeResult
                {
                    Action = WindowPlacementSanitizeAction.Skip,
                    Placement = placement,
                    Reason = "保存的是关闭到托盘的隐藏窗口（-32000）"
                };
            }

            List<MonitorWorkArea> monitors = WindowPlacementService.EnumerateCurrentMonitors();
            if (monitors.Count == 0
                || WindowVisibility.HasUsableVisibleArea(placement, monitors))
            {
                return Keep(placement);
            }

            MonitorWorkArea target = FindNearestMonitor(placement, monitors);
            if (target == null)
            {
                return Keep(placement);
            }
            int[] fitted = WindowScreenFit.Fit(
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height,
                new[] { target.Left, target.Top, target.Right, target.Bottom });
            SavedWindowPlacement repaired = placement.Clone();
            repaired.Left = fitted[0];
            repaired.Top = fitted[1];
            if (placement.Width > 0)
            {
                repaired.Width = fitted[2];
            }
            if (placement.Height > 0)
            {
                repaired.Height = fitted[3];
            }
            return new WindowPlacementSanitizeResult
            {
                Action = WindowPlacementSanitizeAction.Clamp,
                Placement = repaired,
                Reason = "保存位置不在任何屏幕的可见区内"
            };
        }

        /// <summary>找离保存窗口中心最近的一块屏；没有屏幕时返回 null。</summary>
        internal static MonitorWorkArea FindNearestMonitor(
            SavedWindowPlacement placement,
            IList<MonitorWorkArea> monitors)
        {
            if (placement == null || monitors == null || monitors.Count == 0)
            {
                return null;
            }
            int width = placement.Width > 0 ? placement.Width : 220;
            int height = placement.Height > 0 ? placement.Height : 220;
            int centerX = placement.Left + width / 2;
            int centerY = placement.Top + height / 2;
            MonitorWorkArea best = null;
            long bestDistance = long.MaxValue;
            for (int index = 0; index < monitors.Count; index++)
            {
                MonitorWorkArea area = monitors[index];
                if (area == null)
                {
                    continue;
                }
                long distance = area.DistanceSquared(centerX, centerY);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = area;
                }
            }
            return best;
        }

        private static WindowPlacementSanitizeResult Keep(SavedWindowPlacement placement)
        {
            return new WindowPlacementSanitizeResult
            {
                Action = WindowPlacementSanitizeAction.Keep,
                Placement = placement,
                Reason = null
            };
        }
    }
}
