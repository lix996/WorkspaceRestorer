using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkspaceRestorer.UI
{
    internal static class DpiWindowBehavior
    {
        private const int WmDpiChanged = 0x02E0;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpNoZOrder = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        public static void Attach(Window window)
        {
            window.SourceInitialized += delegate
            {
                HwndSource source = PresentationSource.FromVisual(window) as HwndSource;
                if (source != null)
                {
                    source.AddHook(WindowProcedure);
                }
            };
            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
        }

        private static IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmDpiChanged && lParam != IntPtr.Zero)
            {
                NativeRect suggested = (NativeRect)Marshal.PtrToStructure(lParam, typeof(NativeRect));
                SetWindowPos(
                    window,
                    IntPtr.Zero,
                    suggested.Left,
                    suggested.Top,
                    suggested.Right - suggested.Left,
                    suggested.Bottom - suggested.Top,
                    SwpNoActivate | SwpNoZOrder);
                handled = true;
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
