using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器恢复的窗口定位：启动前抓已有窗口，逐页启动时等第一页认领到的窗口。
    /// 不负责最终评分，最终评分在 BrowserFinalWindowResolver。
    /// </summary>
    internal static class BrowserWindowLocator
    {
        internal static ISet<IntPtr> CaptureExistingWindows(string executablePath)
        {
            HashSet<IntPtr> result = new HashSet<IntPtr>();
            if (OsMutationGuard.IsDisabled || ExternalEffectsDisabled())
            {
                return result;
            }
            string normalizedExecutable = PathText.Normalize(executablePath);
            if (string.IsNullOrWhiteSpace(normalizedExecutable))
            {
                return result;
            }

            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle) || IsShellWindow(windowHandle))
                {
                    return true;
                }
                uint ownerProcessId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out ownerProcessId);
                if (PathsMatch(ownerProcessId, normalizedExecutable))
                {
                    result.Add(windowHandle);
                }
                return true;
            };
            try
            {
                NativeMethods.EnumWindows(callback, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                GC.KeepAlive(callback);
            }
            return result;
        }

        internal static IntPtr WaitForBrowserWindow(
            string restoreKey,
            string executablePath,
            IList<string> browserWindowTitles,
            ISet<IntPtr> existingWindows,
            int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
            string normalizedExecutable = PathText.Normalize(executablePath);
            while (DateTime.UtcNow <= deadline)
            {
                IntPtr remembered = BrowserWindowTracker.Get(restoreKey);
                if (remembered != IntPtr.Zero && NativeMethods.IsWindow(remembered))
                {
                    return remembered;
                }

                IntPtr found = FindWindow(normalizedExecutable, browserWindowTitles, existingWindows);
                if (found != IntPtr.Zero)
                {
                    BrowserWindowTracker.Set(restoreKey, found);
                    return found;
                }
                Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindWindow(
            string normalizedExecutable,
            IList<string> expectedTitles,
            ISet<IntPtr> existingWindows)
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || IsShellWindow(windowHandle)
                    || BrowserWindowTracker.IsClaimed(windowHandle))
                {
                    return true;
                }
                if (existingWindows != null && existingWindows.Contains(windowHandle))
                {
                    return true;
                }
                uint processId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
                if (!PathsMatch(processId, normalizedExecutable))
                {
                    return true;
                }
                string title = ReadTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title)
                    || !BrowserTitleProfile.BrowserWindowTitleMatches(title, expectedTitles))
                {
                    return true;
                }
                found = windowHandle;
                return false;
            };
            try
            {
                NativeMethods.EnumWindows(callback, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                GC.KeepAlive(callback);
            }
            return found;
        }

        private static bool ExternalEffectsDisabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsShellWindow(IntPtr windowHandle)
        {
            return windowHandle == NativeMethods.GetShellWindow()
                || windowHandle == NativeMethods.GetDesktopWindow();
        }

        internal static bool PathsMatch(uint processId, string expectedPath)
        {
            if (processId == 0 || string.IsNullOrWhiteSpace(expectedPath))
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    string actualPath = null;
                    try
                    {
                        actualPath = process.MainModule == null ? null : process.MainModule.FileName;
                    }
                    catch
                    {
                    }
                    if (!string.IsNullOrWhiteSpace(actualPath))
                    {
                        return string.Equals(PathText.Normalize(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase);
                    }
                    string expectedName = Path.GetFileNameWithoutExtension(expectedPath.Trim());
                    return !string.IsNullOrWhiteSpace(expectedName)
                        && string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string ReadTitle(IntPtr windowHandle)
        {
            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return null;
                }
                StringBuilder text = new StringBuilder(length + 1);
                return NativeMethods.GetWindowText(windowHandle, text, text.Capacity) > 0
                    ? text.ToString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
