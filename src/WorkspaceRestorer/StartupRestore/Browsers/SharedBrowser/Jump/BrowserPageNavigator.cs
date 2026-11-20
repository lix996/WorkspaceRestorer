using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 【Browsers/SharedBrowser/Jump】浏览器页面跳转需要的窗口能力。
    /// Website 层只依赖这个接口，不直接写 Win32 / 不出现 Edge 路径。
    /// 当前实现只覆盖 Chromium 系（Edge / Chrome / Brave 等）的顶层窗口；
    /// 如果以后验证 Firefox，再新增实现，不要在 Website 里写 if(edge)。
    /// </summary>
    internal interface IBrowserPageNavigator
    {
        List<IntPtr> EnumerateWindows(string executable);

        string ReadTitle(IntPtr windowHandle);

        bool IsMinimized(IntPtr windowHandle);

        bool Activate(IntPtr windowHandle);
    }

    /// <summary>
    /// Chromium 系窗口导航实现。这里没有 msedge.exe、没有 Edge 安装路径；
    /// Edge 只由 MicrosoftEdge/Definitions 描述。
    /// </summary>
    internal sealed class ChromiumBrowserPageNavigator : IBrowserPageNavigator
    {
        public List<IntPtr> EnumerateWindows(string executable)
        {
            List<IntPtr> windows = new List<IntPtr>();
            string expected = PathText.Normalize(executable);
            if (string.IsNullOrWhiteSpace(expected))
            {
                return windows;
            }
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }
                uint processId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
                if (processId != 0
                    && processId <= int.MaxValue
                    && ProcessExecutableMatches(windowHandle, expected))
                {
                    windows.Add(windowHandle);
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return windows;
        }

        public string ReadTitle(IntPtr windowHandle)
        {
            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return null;
                }
                StringBuilder text = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(windowHandle, text, text.Capacity);
                string value = text.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch
            {
                return null;
            }
        }

        public bool IsMinimized(IntPtr windowHandle)
        {
            try
            {
                return windowHandle != IntPtr.Zero && NativeMethods.IsIconic(windowHandle);
            }
            catch
            {
                return false;
            }
        }

        public bool Activate(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (NativeMethods.IsIconic(windowHandle))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
                }

                uint targetThread;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out targetThread);
                uint currentThread = NativeMethods.GetCurrentThreadId();
                bool attached = targetThread != 0
                    && currentThread != 0
                    && targetThread != currentThread
                    && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
                try
                {
                    NativeMethods.SetForegroundWindow(windowHandle);
                    NativeMethods.BringWindowToTop(windowHandle);
                }
                finally
                {
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                    }
                }
                return !NativeMethods.IsIconic(windowHandle);
            }
            catch
            {
                return false;
            }
        }

        private static bool ProcessExecutableMatches(IntPtr windowHandle, string expected)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return false;
            }
            string actual = GetProcessExecutablePath((int)processId);
            if (!string.IsNullOrWhiteSpace(actual))
            {
                return string.Equals(PathText.Normalize(actual), expected, StringComparison.OrdinalIgnoreCase);
            }

            string expectedName;
            try
            {
                expectedName = System.IO.Path.GetFileNameWithoutExtension(expected);
            }
            catch
            {
                expectedName = null;
            }
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetProcessExecutablePath(int processId)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                false,
                (uint)processId);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    StringBuilder path = new StringBuilder(32768);
                    int pathLength = path.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref pathLength))
                    {
                        return path.ToString();
                    }
                }
                catch
                {
                }
                finally
                {
                    try { NativeMethods.CloseHandle(handle); } catch { }
                }
            }
            return null;
        }
    }

    internal static class BrowserPageNavigatorFactory
    {
        internal static IBrowserPageNavigator Create(string executable)
        {
            // 目前只实现 Chromium 系；不根据 msedge 名字分支。
            // 将来 Firefox 验证通过后，在这里按 BrowserCatalog 返回对应实现。
            return new ChromiumBrowserPageNavigator();
        }
    }
}
