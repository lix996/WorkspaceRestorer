using System;
using System.Collections.Generic;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 恢复后兜底巡检：窗口已经按保存位置摆好之后，再延迟检查一次。
    ///
    /// 为什么要这一层：有些应用启动后会**自己**把窗口挪走（恢复上次位置、
    /// 自建启动窗口、Electron 延迟布局），前面的“保存过滤 / 恢复前体检”
    /// 都发生在它挪走之前，管不到。
    ///
    /// 只检查本次确实由 WindowPlacementService 摆过的 HWND，且：
    ///   · 最小化 / 不可见 / cloaked / 托盘隐藏窗口不碰；
    ///   · 只有在可见面积明显不够时才拉回；
    ///   · 只纠正一次，第二次发现又被挪走只记日志，不无限拉扯。
    /// </summary>
    internal static class WindowRestoreAudit
    {
        private const int FirstCheckDelayMilliseconds = 1500;
        private const int SecondCheckDelayMilliseconds = 2500;
        private const int CascadeStep = 32;

        private static readonly object Gate = new object();
        private static readonly HashSet<IntPtr> Scheduled = new HashSet<IntPtr>();
        private static int cascadeSlot;
        private static int generation;

        /// <summary>每轮恢复开始时清空，避免上一轮的 HWND/阶梯计数影响下一轮。</summary>
        internal static void Reset()
        {
            lock (Gate)
            {
                Scheduled.Clear();
                cascadeSlot = 0;
                generation++;
            }
        }

        internal static void Schedule(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            string label)
        {
            if (windowHandle == IntPtr.Zero
                || placement == null
                || !placement.HasBounds
                || WindowVisibility.IsMinimized(placement.WindowState)
                || WindowVisibility.IsHiddenPlacement(placement))
            {
                return;
            }
            int scheduledGeneration;
            lock (Gate)
            {
                if (!Scheduled.Add(windowHandle))
                {
                    return;
                }
                scheduledGeneration = generation;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Run(windowHandle, placement, label, scheduledGeneration);
            });
        }

        private static void Run(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            string label,
            int scheduledGeneration)
        {
            bool corrected = false;
            Thread.Sleep(FirstCheckDelayMilliseconds);
            if (!IsCurrent(scheduledGeneration))
            {
                return;
            }
            Audit(windowHandle, placement, label, ref corrected);
            Thread.Sleep(SecondCheckDelayMilliseconds);
            if (!IsCurrent(scheduledGeneration))
            {
                return;
            }
            Audit(windowHandle, placement, label, ref corrected);
        }

        private static bool IsCurrent(int scheduledGeneration)
        {
            lock (Gate)
            {
                return scheduledGeneration == generation;
            }
        }

        private static void Audit(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            string label,
            ref bool corrected)
        {
            if (!NativeMethods.IsWindow(windowHandle)
                || !NativeMethods.IsWindowVisible(windowHandle)
                || NativeMethods.IsIconic(windowHandle)
                || WindowVisibility.IsCloaked(windowHandle))
            {
                return;
            }

            NativeMethods.NativeRect rectangle;
            if (!NativeMethods.GetWindowRect(windowHandle, out rectangle))
            {
                return;
            }
            // 应用把自己藏到托盘（-32000）不是“卡出屏幕”，巡检不能把它拉出来。
            if (WindowVisibility.IsHiddenRect(
                rectangle.Left,
                rectangle.Top,
                rectangle.Right,
                rectangle.Bottom))
            {
                return;
            }

            List<MonitorWorkArea> monitors = WindowPlacementService.EnumerateCurrentMonitors();
            if (monitors.Count == 0
                || WindowVisibility.HasUsableVisibleArea(
                    rectangle.Left,
                    rectangle.Top,
                    rectangle.Right,
                    rectangle.Bottom,
                    monitors))
            {
                return;
            }

            if (corrected)
            {
                AppLog.Write("[窗口恢复] 兜底巡检：窗口又被应用挪到屏幕外，本次不再拉扯："
                    + Describe(label));
                return;
            }

            if (TryMoveToVisible(windowHandle, rectangle, monitors, label))
            {
                corrected = true;
            }
        }

        private static bool TryMoveToVisible(
            IntPtr windowHandle,
            NativeMethods.NativeRect rectangle,
            List<MonitorWorkArea> monitors,
            string label)
        {
            SavedWindowPlacement current = new SavedWindowPlacement
            {
                HasBounds = true,
                Left = rectangle.Left,
                Top = rectangle.Top,
                Width = rectangle.Right - rectangle.Left,
                Height = rectangle.Bottom - rectangle.Top,
                WindowState = "Normal"
            };
            MonitorWorkArea target = WindowPlacementSanitizer.FindNearestMonitor(current, monitors);
            if (target == null)
            {
                return false;
            }
            int[] fitted = WindowScreenFit.Fit(
                rectangle.Left,
                rectangle.Top,
                current.Width,
                current.Height,
                new[] { target.Left, target.Top, target.Right, target.Bottom });
            int left = fitted[0];
            int top = fitted[1];
            int offset = NextCascadeOffset();
            left += offset;
            top += offset;
            if (left + fitted[2] > target.Right)
            {
                left = target.Left;
            }
            if (top + fitted[3] > target.Bottom)
            {
                top = target.Top;
            }

            bool moved = NativeMethods.SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                left,
                top,
                fitted[2],
                fitted[3],
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
            if (moved)
            {
                AppLog.Write("[窗口恢复] 兜底巡检：窗口在屏幕外，已拉回。"
                    + Describe(label)
                    + " 原=(" + rectangle.Left + "," + rectangle.Top + " "
                    + current.Width + "x" + current.Height + ")"
                    + " → (" + left + "," + top + " " + fitted[2] + "x" + fitted[3] + ")");
            }
            return moved;
        }

        private static int NextCascadeOffset()
        {
            lock (Gate)
            {
                int slot = cascadeSlot++;
                return (slot % 8) * CascadeStep;
            }
        }

        private static string Describe(string label)
        {
            return string.IsNullOrWhiteSpace(label) ? "未知窗口" : label;
        }
    }
}
