using System;
using System.Collections.Generic;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器恢复的“窗口认领器”。
    ///
    /// 只做一件事：在本轮新建的、同进程的可见顶层窗口里，找出唯一一个
    /// 浏览器主窗口并把它登记到 BrowserWindowTracker。
    /// 不缩窗、不摆位置、不激活窗口。
    ///
    /// 空白引导窗口不能只靠标题：Edge 新窗口标题常常先是空/无标题。
    /// 这里允许“无 owner + Chromium 主窗口形状 + 正常尺寸”的空白窗口，
    /// 但只在候选唯一时认领；多个候选就继续等标题，绝不猜。
    /// </summary>
    internal static class BrowserWindowClaimer
    {
        internal static IntPtr TryClaim(
            string restoreKey,
            string executablePath,
            IList<string> expectedTitles,
            ISet<IntPtr> existingWindows,
            int timeoutMilliseconds,
            Func<bool> userClosed,
            Func<bool> userMinimized)
        {
            if (string.IsNullOrWhiteSpace(restoreKey)
                || string.IsNullOrWhiteSpace(executablePath)
                || OsMutationGuard.IsDisabled
                || ExternalEffectsDisabled())
            {
                return IntPtr.Zero;
            }

            string normalizedExecutable = PathText.Normalize(executablePath);
            if (string.IsNullOrWhiteSpace(normalizedExecutable))
            {
                return IntPtr.Zero;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
            while (DateTime.UtcNow <= deadline)
            {
                if (userClosed != null && userClosed())
                {
                    return IntPtr.Zero;
                }
                if (userMinimized != null && userMinimized())
                {
                    return IntPtr.Zero;
                }

                IntPtr remembered = BrowserWindowTracker.Get(restoreKey);
                if (remembered != IntPtr.Zero && NativeMethods.IsWindow(remembered))
                {
                    return remembered;
                }

                IntPtr found = FindUniqueCandidate(
                    normalizedExecutable,
                    expectedTitles,
                    existingWindows);
                if (found != IntPtr.Zero
                    && BrowserWindowTracker.ClaimForRestore(restoreKey, found))
                {
                    AppLog.Write("[窗口恢复] 认领浏览器窗口：key=" + restoreKey
                        + " HWND=" + found
                        + " 标题=" + (BrowserWindowLocator.ReadTitle(found) ?? string.Empty));
                    return found;
                }

                Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }

        private static IntPtr FindUniqueCandidate(
            string normalizedExecutable,
            IList<string> expectedTitles,
            ISet<IntPtr> existingWindows)
        {
            IntPtr bestHandle = IntPtr.Zero;
            int bestScore = -1;
            bool tied = false;

            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || BrowserWindowLocator.IsShellWindow(windowHandle)
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
                if (!BrowserWindowLocator.PathsMatch(processId, normalizedExecutable))
                {
                    return true;
                }

                string title = BrowserWindowLocator.ReadTitle(windowHandle);
                int score = 0;
                bool titleMatches = !string.IsNullOrWhiteSpace(title)
                    && BrowserTitleProfile.BrowserWindowTitleMatches(title, expectedTitles);
                if (titleMatches)
                {
                    score = 100;
                }
                else if (BrowserTitleProfile.IsBlankWindowTitle(title)
                    && BrowserWindowNative.IsLikelyChromiumMainWindow(windowHandle))
                {
                    score = 60;
                }
                else
                {
                    return true;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHandle = windowHandle;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
                return true;
            };

            try
            {
                NativeMethods.EnumWindows(callback, IntPtr.Zero);
            }
            catch
            {
                return IntPtr.Zero;
            }
            finally
            {
                GC.KeepAlive(callback);
            }

            return tied ? IntPtr.Zero : bestHandle;
        }

        private static bool ExternalEffectsDisabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
