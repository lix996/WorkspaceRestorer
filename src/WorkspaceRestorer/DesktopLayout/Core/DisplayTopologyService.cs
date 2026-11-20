using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WorkspaceRestorer.Desktop
{
    public sealed class DisplayTopologyService
    {
        public DisplayTopology Capture()
        {
            List<DisplayMonitor> monitors = new List<DisplayMonitor>();
            NativeMethods.MonitorEnumProc callback = delegate(IntPtr monitorHandle, IntPtr deviceContext, ref NativeMethods.RECT monitorRect, IntPtr data)
            {
                NativeMethods.MONITORINFOEX monitorInfo = new NativeMethods.MONITORINFOEX();
                monitorInfo.Size = Marshal.SizeOf(typeof(NativeMethods.MONITORINFOEX));
                if (!NativeMethods.GetMonitorInfo(monitorHandle, ref monitorInfo))
                {
                    return true;
                }

                NativeMethods.DISPLAY_DEVICE displayDevice = GetDisplayDevice(monitorInfo.DeviceName);
                int dpiX;
                int dpiY;
                GetMonitorDpi(monitorHandle, monitorInfo.DeviceName, out dpiX, out dpiY);

                DisplayMonitor monitor = new DisplayMonitor();
                monitor.DeviceName = monitorInfo.DeviceName ?? string.Empty;
                monitor.DeviceId = displayDevice.DeviceID ?? string.Empty;
                monitor.FriendlyName = displayDevice.DeviceString ?? string.Empty;
                monitor.StableKey = BuildStableMonitorKey(monitor.DeviceName, displayDevice);
                monitor.IsPrimary = (monitorInfo.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                monitor.DpiX = dpiX;
                monitor.DpiY = dpiY;
                monitor.Bounds = ToDesktopRect(monitorInfo.Monitor);
                monitor.WorkingArea = ToDesktopRect(monitorInfo.WorkArea);
                monitors.Add(monitor);
                return true;
            };

            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enumerate display monitors.");
            }

            if (monitors.Count == 0)
            {
                throw new InvalidOperationException("Windows did not report an active display monitor.");
            }

            DisplayTopology topology = new DisplayTopology();
            topology.Monitors = monitors;
            topology.VirtualBounds = CalculateVirtualBounds(monitors);
            topology.Signature = BuildSignature(topology);
            return topology;
        }

        private static NativeMethods.DISPLAY_DEVICE GetDisplayDevice(string deviceName)
        {
            NativeMethods.DISPLAY_DEVICE fallback = new NativeMethods.DISPLAY_DEVICE();
            fallback.Size = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));

            for (uint index = 0; index < 16; index++)
            {
                NativeMethods.DISPLAY_DEVICE candidate = new NativeMethods.DISPLAY_DEVICE();
                candidate.Size = Marshal.SizeOf(typeof(NativeMethods.DISPLAY_DEVICE));
                if (!NativeMethods.EnumDisplayDevices(deviceName, index, ref candidate, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    break;
                }

                if (index == 0)
                {
                    fallback = candidate;
                }

                if ((candidate.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0)
                {
                    return candidate;
                }
            }

            return fallback;
        }

        private static string BuildStableMonitorKey(string deviceName, NativeMethods.DISPLAY_DEVICE device)
        {
            string value = device.DeviceID;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = device.DeviceKey;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = deviceName;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = "UNKNOWN-DISPLAY";
            }

            return value.Trim().ToUpperInvariant();
        }

        private static void GetMonitorDpi(IntPtr monitorHandle, string deviceName, out int dpiX, out int dpiY)
        {
            uint nativeDpiX;
            uint nativeDpiY;
            try
            {
                int result = NativeMethods.GetDpiForMonitor(
                    monitorHandle,
                    NativeMethods.MDT_EFFECTIVE_DPI,
                    out nativeDpiX,
                    out nativeDpiY);
                if (result == 0 && nativeDpiX > 0 && nativeDpiY > 0)
                {
                    dpiX = (int)nativeDpiX;
                    dpiY = (int)nativeDpiY;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            IntPtr displayDc = NativeMethods.CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
            if (displayDc != IntPtr.Zero)
            {
                try
                {
                    dpiX = NativeMethods.GetDeviceCaps(displayDc, NativeMethods.LOGPIXELSX);
                    dpiY = NativeMethods.GetDeviceCaps(displayDc, NativeMethods.LOGPIXELSY);
                    if (dpiX > 0 && dpiY > 0)
                    {
                        return;
                    }
                }
                finally
                {
                    NativeMethods.DeleteDC(displayDc);
                }
            }

            dpiX = 96;
            dpiY = 96;
        }

        private static DesktopRect ToDesktopRect(NativeMethods.RECT value)
        {
            return new DesktopRect(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);
        }

        private static DesktopRect CalculateVirtualBounds(IList<DisplayMonitor> monitors)
        {
            int left = monitors[0].Bounds.Left;
            int top = monitors[0].Bounds.Top;
            int right = monitors[0].Bounds.Right;
            int bottom = monitors[0].Bounds.Bottom;

            for (int index = 1; index < monitors.Count; index++)
            {
                left = Math.Min(left, monitors[index].Bounds.Left);
                top = Math.Min(top, monitors[index].Bounds.Top);
                right = Math.Max(right, monitors[index].Bounds.Right);
                bottom = Math.Max(bottom, monitors[index].Bounds.Bottom);
            }

            return new DesktopRect(left, top, right - left, bottom - top);
        }

        private static string BuildSignature(DisplayTopology topology)
        {
            DisplayMonitor primary = topology.GetPrimaryMonitor();
            List<DisplayMonitor> ordered = new List<DisplayMonitor>(topology.Monitors);
            ordered.Sort(delegate(DisplayMonitor left, DisplayMonitor right)
            {
                return string.Compare(left.StableKey, right.StableKey, StringComparison.OrdinalIgnoreCase);
            });

            StringBuilder source = new StringBuilder();
            for (int index = 0; index < ordered.Count; index++)
            {
                DisplayMonitor monitor = ordered[index];
                if (index > 0)
                {
                    source.Append('|');
                }

                source.Append(monitor.StableKey);
                source.Append(':');
                source.Append((monitor.Bounds.Left - primary.Bounds.Left).ToString(CultureInfo.InvariantCulture));
                source.Append(',');
                source.Append((monitor.Bounds.Top - primary.Bounds.Top).ToString(CultureInfo.InvariantCulture));
                source.Append(',');
                source.Append(monitor.Bounds.Width.ToString(CultureInfo.InvariantCulture));
                source.Append(',');
                source.Append(monitor.Bounds.Height.ToString(CultureInfo.InvariantCulture));
                source.Append(',');
                source.Append(monitor.DpiX.ToString(CultureInfo.InvariantCulture));
                source.Append(',');
                source.Append(monitor.DpiY.ToString(CultureInfo.InvariantCulture));
                source.Append(monitor.IsPrimary ? ",P" : ",S");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(source.ToString());
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(bytes);
                StringBuilder result = new StringBuilder(digest.Length * 2);
                for (int index = 0; index < digest.Length; index++)
                {
                    result.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return result.ToString();
            }
        }

        private static class NativeMethods
        {
            internal const uint MONITORINFOF_PRIMARY = 1;
            internal const uint EDD_GET_DEVICE_INTERFACE_NAME = 1;
            internal const int DISPLAY_DEVICE_ACTIVE = 1;
            internal const int MDT_EFFECTIVE_DPI = 0;
            internal const int LOGPIXELSX = 88;
            internal const int LOGPIXELSY = 90;

            internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, ref RECT monitorRect, IntPtr data);

            [StructLayout(LayoutKind.Sequential)]
            internal struct RECT
            {
                internal int Left;
                internal int Top;
                internal int Right;
                internal int Bottom;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            internal struct MONITORINFOEX
            {
                internal int Size;
                internal RECT Monitor;
                internal RECT WorkArea;
                internal uint Flags;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                internal string DeviceName;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
            internal struct DISPLAY_DEVICE
            {
                internal int Size;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                internal string DeviceName;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                internal string DeviceString;

                internal int StateFlags;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                internal string DeviceID;

                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
                internal string DeviceKey;
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumDisplayMonitors(
                IntPtr deviceContext,
                IntPtr clipRect,
                MonitorEnumProc callback,
                IntPtr data);

            [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX monitorInfo);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumDisplayDevices(
                string device,
                uint deviceIndex,
                ref DISPLAY_DEVICE displayDevice,
                uint flags);

            [DllImport("shcore.dll")]
            internal static extern int GetDpiForMonitor(
                IntPtr monitor,
                int dpiType,
                out uint dpiX,
                out uint dpiY);

            [DllImport("gdi32.dll", CharSet = CharSet.Auto)]
            internal static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);

            [DllImport("gdi32.dll")]
            internal static extern int GetDeviceCaps(IntPtr deviceContext, int index);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteDC(IntPtr deviceContext);
        }
    }
}
