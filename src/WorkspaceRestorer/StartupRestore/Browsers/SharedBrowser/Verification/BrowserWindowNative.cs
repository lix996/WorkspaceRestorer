using System;
using System.Runtime.InteropServices;
using System.Text;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器窗口认领需要的少量通用 Win32 查询。
    /// 只放在浏览器子模块里，不因为“像公共能力”就塞进 Shared：
    /// 这些判断只服务浏览器恢复的空白引导窗口识别。
    /// </summary>
    internal static class BrowserWindowNative
    {
        private const int GwOwner = 4;
        private const int GwlStyle = -16;
        private const int WsCaption = 0x00C00000;
        private const int WsThickFrame = 0x00040000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr windowHandle, uint command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(
            IntPtr windowHandle,
            StringBuilder className,
            int maximumCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr windowHandle, int index);

        internal static bool HasOwner(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                return GetWindow(windowHandle, GwOwner) != IntPtr.Zero;
            }
            catch
            {
                return true;
            }
        }

        internal static string ReadClassName(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return string.Empty;
            }
            try
            {
                StringBuilder className = new StringBuilder(256);
                int length = GetClassName(windowHandle, className, className.Capacity);
                return length > 0 ? className.ToString() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 新 Edge/Chromium 窗口在标题出来之前，只能靠窗口形状判断。
        /// 这里要求“无 owner + Chromium 窗口类 + 有标题栏/可调整边框 + 正常尺寸”，
        /// 用来排除“还原页面”这类同进程辅助窗口。
        /// </summary>
        internal static bool IsLikelyChromiumMainWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero
                || !NativeMethods.IsWindow(windowHandle)
                || !NativeMethods.IsWindowVisible(windowHandle)
                || BrowserWindowLocator.IsShellWindow(windowHandle)
                || HasOwner(windowHandle))
            {
                return false;
            }

            string className = ReadClassName(windowHandle);
            if (string.IsNullOrWhiteSpace(className)
                || className.IndexOf("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }

            int style = GetWindowLong(windowHandle, GwlStyle);
            if ((style & WsCaption) != WsCaption
                || (style & WsThickFrame) != WsThickFrame)
            {
                return false;
            }

            NativeMethods.NativeRect rectangle;
            if (!NativeMethods.GetWindowRect(windowHandle, out rectangle))
            {
                return false;
            }
            int width = rectangle.Right - rectangle.Left;
            int height = rectangle.Bottom - rectangle.Top;
            return width >= 400 && height >= 300;
        }
    }
}
