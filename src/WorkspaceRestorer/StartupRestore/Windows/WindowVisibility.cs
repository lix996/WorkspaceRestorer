using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 判断窗口/保存位置是不是“在屏幕外”。
    ///
    /// 为什么单独成类：保存、恢复前体检、恢复后巡检三处都要用同一套规则。
    /// 如果每处各写一份 `-32000` / 16 像素判断，迟早会出现“保存时过滤了、
    /// 恢复时又放出来”的不一致。
    /// </summary>
    internal static class WindowVisibility
    {
        /// <summary>
        /// Windows 把“关闭到托盘/隐藏”的窗口放在 -32000 附近；最小化窗口的
        /// GetWindowRect 也会返回这个值。只认“整块窗口都在 -30000 以外”的矩形。
        /// </summary>
        internal const int HiddenSentinel = -30000;

        /// <summary>巡检时至少要有这么多可见面积，才认为窗口“在屏幕上”。</summary>
        internal const int MinVisibleWidth = 80;
        internal const int MinVisibleHeight = 40;

        /// <summary>这个矩形是不是“隐藏到屏幕外”的哨兵值。</summary>
        internal static bool IsHiddenRect(int left, int top, int right, int bottom)
        {
            return right <= HiddenSentinel || bottom <= HiddenSentinel;
        }

        /// <summary>当前窗口是不是“非最小化、但整块在屏幕外”的托盘隐藏窗口。</summary>
        internal static bool IsHiddenToTrayWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || NativeMethods.IsIconic(windowHandle))
            {
                return false;
            }
            NativeMethods.NativeRect rectangle;
            if (!NativeMethods.GetWindowRect(windowHandle, out rectangle))
            {
                return false;
            }
            return IsHiddenRect(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
        }

        /// <summary>保存记录是不是“非最小化、整块在屏幕外”的托盘隐藏条目。</summary>
        internal static bool IsHiddenPlacement(SavedWindowPlacement placement)
        {
            if (placement == null
                || !placement.HasBounds
                || IsMinimized(placement.WindowState))
            {
                return false;
            }
            return IsHiddenRect(
                placement.Left,
                placement.Top,
                placement.Left + Math.Max(placement.Width, 1),
                placement.Top + Math.Max(placement.Height, 1));
        }

        internal static bool IsMinimized(string windowState)
        {
            return string.Equals(windowState, "Minimized", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 保存位置是否和任意一块屏幕有“够用”的可见交集。
        /// 只碰到屏幕一条边不算可见；宽高未知时按 220 估算。
        /// </summary>
        internal static bool HasUsableVisibleArea(
            SavedWindowPlacement placement,
            IList<MonitorWorkArea> monitors)
        {
            if (placement == null || !placement.HasBounds)
            {
                return true;
            }
            int width = placement.Width > 0 ? placement.Width : 220;
            int height = placement.Height > 0 ? placement.Height : 220;
            return HasUsableVisibleArea(
                placement.Left,
                placement.Top,
                placement.Left + width,
                placement.Top + height,
                monitors);
        }

        /// <summary>矩形和任意一块屏幕工作区是否有够用的可见交集。</summary>
        internal static bool HasUsableVisibleArea(
            int left,
            int top,
            int right,
            int bottom,
            IList<MonitorWorkArea> monitors)
        {
            if (monitors == null || monitors.Count == 0)
            {
                return true;
            }
            int width = Math.Max(1, right - left);
            int height = Math.Max(1, bottom - top);
            int needWidth = Math.Min(MinVisibleWidth, width);
            int needHeight = Math.Min(MinVisibleHeight, height);
            for (int index = 0; index < monitors.Count; index++)
            {
                MonitorWorkArea area = monitors[index];
                if (area == null)
                {
                    continue;
                }
                int overlapWidth = Math.Min(right, area.Right) - Math.Max(left, area.Left);
                int overlapHeight = Math.Min(bottom, area.Bottom) - Math.Max(top, area.Top);
                if (overlapWidth >= needWidth && overlapHeight >= needHeight)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>DWM cloaked 窗口（例如被挂起的 UWP/虚拟桌面窗口）不算可见。</summary>
        internal static bool IsCloaked(IntPtr windowHandle)
        {
            try
            {
                int cloaked;
                int result = NativeMethods.DwmGetWindowAttribute(
                    windowHandle,
                    NativeMethods.DwmwaCloaked,
                    out cloaked,
                    sizeof(int));
                return result == 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
