using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 找“这个文件现在是被哪个窗口打开的”。
    ///
    /// 用户 2026-10-15：“跳转，点击工作方案内对应的跳转地址，却不能准确跳转到对应网址上，
    /// 不单单是网址，后续像图2这种 txt 文件，里面多个打开也是一样。”
    ///
    /// 文件类条目（txt/doc/xlsx…）原来**完全没有**“找已经打开的窗口”这一步：
    /// 它没有 ExecutablePath，按可执行文件匹配必然失败，于是每次点都重新开一个。
    ///
    /// 做法：先问 Windows 这个扩展名默认用什么程序（AssocQueryString），
    /// 再在这个程序的可见窗口里找标题包含文件名的那个；
    /// 拿不到关联程序时退回“任意进程、标题包含完整文件名”。
    /// 「启动与恢复」的跳转和“已经打开就跳过”两处**共用这一份**。
    /// </summary>
    internal static class DocumentWindowFinder
    {
        internal static IntPtr FindWindowShowing(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return IntPtr.Zero;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(filePath.Trim()));
            }
            catch
            {
                return IntPtr.Zero;
            }

            string fileName;
            try
            {
                fileName = Path.GetFileName(fullPath);
            }
            catch
            {
                return IntPtr.Zero;
            }
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return IntPtr.Zero;
            }

            string associated = TryGetAssociatedExecutable(fullPath);
            string normalizedAssociated = string.IsNullOrWhiteSpace(associated)
                ? string.Empty
                : Normalize(associated);

            IntPtr associatedHit = IntPtr.Zero;
            IntPtr anyHit = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }

                string title = ReadTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title)
                    || title.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return true;
                }

                if (anyHit == IntPtr.Zero)
                {
                    anyHit = windowHandle;
                }
                if (associatedHit == IntPtr.Zero
                    && !string.IsNullOrWhiteSpace(normalizedAssociated)
                    && ProcessExecutableMatches(windowHandle, normalizedAssociated))
                {
                    associatedHit = windowHandle;
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return associatedHit != IntPtr.Zero ? associatedHit : anyHit;
        }

        /// <summary>
        /// 问 Windows“这个文件默认用什么程序打开”。
        /// AssocQueryString 比直接读注册表可靠：用户改过默认程序也能拿到正确的。
        /// 失败返回 null，调用方会退回按标题找。
        /// </summary>
        internal static string TryGetAssociatedExecutable(string filePath)
        {
            try
            {
                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    return null;
                }

                StringBuilder buffer = new StringBuilder(1024);
                int length = buffer.Capacity;
                int result = NativeMethods.AssocQueryString(
                    NativeMethods.AssocfNone,
                    NativeMethods.AssocstrExecutable,
                    extension,
                    null,
                    buffer,
                    ref length);
                if (result != 0 || length <= 0)
                {
                    return null;
                }
                string value = buffer.ToString().Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string Normalize(string value)
        {
            try
            {
                return Path.GetFullPath(value.Trim()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return value.Trim();
            }
        }

        private static string ReadTitle(IntPtr windowHandle)
        {
            int length = NativeMethods.GetWindowTextLength(windowHandle);
            if (length <= 0)
            {
                return null;
            }
            StringBuilder text = new StringBuilder(length + 1);
            if (NativeMethods.GetWindowText(windowHandle, text, text.Capacity) <= 0)
            {
                return null;
            }
            return text.ToString();
        }

        private static bool ProcessExecutableMatches(IntPtr windowHandle, string normalizedExecutable)
        {
            if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(normalizedExecutable))
            {
                return false;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return false;
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = NativeMethods.OpenProcess(
                    NativeMethods.ProcessQueryLimitedInformation,
                    false,
                    processId);
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                StringBuilder buffer = new StringBuilder(2048);
                int size = buffer.Capacity;
                if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
                {
                    return false;
                }
                string image = buffer.ToString();
                return !string.IsNullOrWhiteSpace(image)
                    && string.Equals(
                        Normalize(image),
                        normalizedExecutable,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    try { NativeMethods.CloseHandle(handle); } catch { }
                }
            }
        }
    }
}
