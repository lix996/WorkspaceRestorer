using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 把当前桌面截成一张 PNG（用户 2026-10-20：图标布局文件夹里要能展开看桌面图片）。
    ///
    /// 优先用 `PrintWindow(Progman, PW_RENDERFULLCONTENT)`：它只画桌面（壁纸 + 图标），
    /// 不受本程序/浏览器窗口遮挡，也不用把窗口最小化、不会闪。
    /// 只有在拿不到 Progman 或它覆盖不到整个虚拟桌面（多屏）时，才退回屏幕拷贝；
    /// 退路里会先尝试“显示桌面”，截完立即恢复。
    /// </summary>
    internal static class DesktopScreenCapture
    {
        private const uint PwRenderFullContent = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder buffer, int count);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static bool TryCapture(string path, bool showDesktop, out string error)
        {
            error = string.Empty;
            if (TryCaptureDesktopWindow(path, out error))
            {
                return true;
            }

            bool desktopToggled = false;
            try
            {
                if (showDesktop)
                {
                    desktopToggled = TryToggleDesktop();
                    if (desktopToggled)
                    {
                        Thread.Sleep(220);
                    }
                }
                return TryCaptureScreen(path, out error);
            }
            finally
            {
                if (desktopToggled)
                {
                    TryToggleDesktop();
                }
            }
        }

        /// <summary>只画 Progman（桌面壁纸+图标）；覆盖不到整个虚拟桌面就返回 false。</summary>
        private static bool TryCaptureDesktopWindow(string path, out string error)
        {
            error = string.Empty;
            IntPtr progman = FindDesktopWindow();
            if (progman == IntPtr.Zero)
            {
                error = "没有找到桌面窗口";
                return false;
            }
            NativeRect rect;
            if (!GetWindowRect(progman, out rect))
            {
                error = "读不到桌面窗口尺寸";
                return false;
            }
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            if (width <= 0 || height <= 0)
            {
                error = "桌面窗口尺寸无效";
                return false;
            }
            // Progman 通常只覆盖主屏；多屏时退回屏幕拷贝，避免图片只有半张。
            if (width < virtualScreen.Width || height < virtualScreen.Height)
            {
                error = "桌面窗口没有覆盖整个虚拟桌面";
                return false;
            }
            try
            {
                EnsureDirectory(path);
                string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        IntPtr deviceContext = graphics.GetHdc();
                        bool drawn;
                        try
                        {
                            drawn = PrintWindow(progman, deviceContext, PwRenderFullContent);
                        }
                        finally
                        {
                            graphics.ReleaseHdc(deviceContext);
                        }
                        if (!drawn)
                        {
                            error = "PrintWindow 没有画出桌面";
                            return false;
                        }
                        bitmap.Save(temporary, ImageFormat.Png);
                    }
                    ReplaceFile(temporary, path);
                }
                finally
                {
                    TryDelete(temporary);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool TryCaptureScreen(string path, out string error)
        {
            error = string.Empty;
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    error = "当前虚拟桌面尺寸无效";
                    return false;
                }
                EnsureDirectory(path);
                string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb))
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                        bitmap.Save(temporary, ImageFormat.Png);
                    }
                    ReplaceFile(temporary, path);
                }
                finally
                {
                    TryDelete(temporary);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static IntPtr FindDesktopWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows(delegate(IntPtr window, IntPtr parameter)
                {
                    StringBuilder builder = new StringBuilder(64);
                    if (GetClassName(window, builder, builder.Capacity) > 0
                        && string.Equals(builder.ToString(), "Progman", StringComparison.OrdinalIgnoreCase))
                    {
                        found = window;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                return IntPtr.Zero;
            }
            return found;
        }

        private static bool TryToggleDesktop()
        {
            object shell = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return false;
                }
                shell = Activator.CreateInstance(shellType);
                shellType.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    try { Marshal.ReleaseComObject(shell); } catch { }
                }
            }
        }

        private static void EnsureDirectory(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void ReplaceFile(string temporary, string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
