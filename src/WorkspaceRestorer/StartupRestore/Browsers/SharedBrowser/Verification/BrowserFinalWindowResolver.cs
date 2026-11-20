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
    /// 浏览器最后一页之后的最终窗口解析：排除旧窗口/别的窗口认领过的句柄，
    /// 按标题、精确页数、当前标签和前台状态评分，并连续稳定后才认领。
    /// </summary>
    internal static class BrowserFinalWindowResolver
    {
        private const int FinalWindowStableMilliseconds = 1000;
        private const int FinalWindowPollMilliseconds = 200;

        private sealed class Candidate
        {
            internal IntPtr Handle;
            internal string Title;
            internal int PageCount;
            internal int Order;
            internal int Score;
        }

        internal static IntPtr FindExisting(
            string executablePath,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            ISet<IntPtr> existingWindows,
            out string title,
            out int pageCount)
        {
            title = null;
            pageCount = 0;
            if (existingWindows == null || existingWindows.Count == 0)
            {
                return IntPtr.Zero;
            }
            string normalizedExecutable = PathText.Normalize(executablePath);
            foreach (IntPtr handle in existingWindows)
            {
                if (!NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle))
                {
                    continue;
                }
                uint processId;
                NativeMethods.GetWindowThreadProcessId(handle, out processId);
                if (!string.IsNullOrWhiteSpace(normalizedExecutable)
                    && !PathsMatch(processId, normalizedExecutable))
                {
                    continue;
                }
                string currentTitle;
                int currentCount;
                if (BrowserWindowVerifier.MatchesExpectation(
                    handle,
                    expectedTitles,
                    lastPageTitle,
                    expectedPageCount,
                    out currentTitle,
                    out currentCount))
                {
                    title = currentTitle;
                    pageCount = currentCount;
                    return handle;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 最后一页启动完、settle 之后，重新找真正的最终窗口。
        /// 候选必须：不是恢复前已有的窗口、不是别的浏览器窗口认领过的、
        /// 标题像这一组页面、页数不少于保存页数，并连续稳定约 1 秒。
        /// 找不到就返回 0，调用方不应再缩任何“记得的旧句柄”。
        /// </summary>
        internal static IntPtr Resolve(
            string restoreKey,
            string executablePath,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            ISet<IntPtr> existingWindows,
            int timeoutMilliseconds,
            out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(restoreKey))
            {
                reason = "缺少浏览器窗口恢复 key";
                return IntPtr.Zero;
            }

            string normalizedExecutable = PathText.Normalize(executablePath);
            IntPtr remembered = BrowserWindowTracker.Get(restoreKey);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMilliseconds));
            IntPtr stableHandle = IntPtr.Zero;
            string stableTitle = null;
            int stablePageCount = -1;
            DateTime stableSince = DateTime.UtcNow;
            bool sawCandidate = false;

            while (DateTime.UtcNow <= deadline)
            {
                Candidate candidate = FindBestCandidate(
                    restoreKey,
                    normalizedExecutable,
                    expectedTitles,
                    lastPageTitle,
                    expectedPageCount,
                    existingWindows,
                    remembered);
                if (candidate != null)
                {
                    sawCandidate = true;
                    if (candidate.Handle == stableHandle
                        && string.Equals(candidate.Title, stableTitle, StringComparison.Ordinal)
                        && candidate.PageCount == stablePageCount)
                    {
                        if ((DateTime.UtcNow - stableSince).TotalMilliseconds
                            >= FinalWindowStableMilliseconds)
                        {
                            if (BrowserWindowTracker.ClaimForRestore(restoreKey, candidate.Handle))
                            {
                                reason = "最终窗口已稳定：页数=" + candidate.PageCount
                                    + "/" + expectedPageCount
                                    + " 标题=" + candidate.Title;
                                return candidate.Handle;
                            }
                        }
                    }
                    else
                    {
                        stableHandle = candidate.Handle;
                        stableTitle = candidate.Title;
                        stablePageCount = candidate.PageCount;
                        stableSince = DateTime.UtcNow;
                    }
                }
                else
                {
                    stableHandle = IntPtr.Zero;
                    stableTitle = null;
                    stablePageCount = -1;
                }
                Thread.Sleep(FinalWindowPollMilliseconds);
            }

            reason = sawCandidate
                ? "候选窗口标题/页数在超时内没有稳定"
                : "没有找到标题/页数匹配的新浏览器窗口";
            return IntPtr.Zero;
        }

        private static Candidate FindBestCandidate(
            string restoreKey,
            string normalizedExecutable,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            ISet<IntPtr> existingWindows,
            IntPtr remembered)
        {
            List<Candidate> candidates = new List<Candidate>();
            HashSet<IntPtr> otherKeyWindows = BrowserWindowTracker.GetExcept(restoreKey);
            int order = 0;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle) || IsShellWindow(windowHandle))
                {
                    return true;
                }
                if (existingWindows != null && existingWindows.Contains(windowHandle))
                {
                    return true;
                }
                if (otherKeyWindows.Contains(windowHandle))
                {
                    return true;
                }

                uint processId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
                if (!string.IsNullOrWhiteSpace(normalizedExecutable)
                    && !PathsMatch(processId, normalizedExecutable))
                {
                    return true;
                }

                string title = ReadTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                int pageCount;
                bool hasPageCount = BrowserTitleProfile.TryReadPageCount(title, out pageCount);
                if (expectedPageCount > 1
                    && (!hasPageCount || pageCount != expectedPageCount)
                    && !NativeMethods.IsIconic(windowHandle))
                {
                    int uiTabCount;
                    if (BrowserWindowVerifier.TryReadTabCount(windowHandle, out uiTabCount))
                    {
                        pageCount = uiTabCount;
                        hasPageCount = true;
                    }
                }
                bool exactKnownCount = expectedPageCount > 1
                    && hasPageCount
                    && pageCount == expectedPageCount;
                if (BrowserTitleProfile.IsBlankWindowTitle(title) && !exactKnownCount)
                {
                    return true;
                }
                if (!BrowserTitleProfile.BrowserWindowTitleMatches(title, expectedTitles) && !exactKnownCount)
                {
                    return true;
                }
                if (expectedPageCount > 0
                    && hasPageCount
                    && pageCount != expectedPageCount)
                {
                    return true;
                }

                string activeTitle = BrowserTitleProfile.GetActiveTitle(title);
                int score = 0;
                if (!string.IsNullOrWhiteSpace(lastPageTitle)
                    && BrowserTitleProfile.BrowserTitleFragmentsMatch(activeTitle, lastPageTitle))
                {
                    score += 100;
                }
                if (expectedPageCount > 0 && hasPageCount && pageCount == expectedPageCount)
                {
                    score += 80;
                }
                else if (expectedPageCount > 0 && hasPageCount && pageCount > expectedPageCount)
                {
                    score += 30;
                }
                else if (!hasPageCount)
                {
                    score += 10;
                }
                if (BrowserTitleProfile.BrowserTitleMatchesAny(activeTitle, expectedTitles))
                {
                    score += 20;
                }
                if (windowHandle == remembered)
                {
                    score += 15;
                }
                if (NativeMethods.GetForegroundWindow() == windowHandle)
                {
                    score += 5;
                }
                if (!NativeMethods.IsIconic(windowHandle))
                {
                    score += 2;
                }

                candidates.Add(new Candidate
                {
                    Handle = windowHandle,
                    Title = title,
                    PageCount = hasPageCount ? pageCount : 0,
                    Order = order++,
                    Score = score
                });
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

            if (candidates.Count == 0)
            {
                return null;
            }
            candidates.Sort(delegate(Candidate left, Candidate right)
            {
                int byScore = right.Score.CompareTo(left.Score);
                if (byScore != 0)
                {
                    return byScore;
                }
                return left.Order.CompareTo(right.Order);
            });
            return candidates[0];
        }

        private static bool ClaimBrowserWindowForRestore(string restoreKey, IntPtr windowHandle)
        {
            return BrowserWindowTracker.ClaimForRestore(restoreKey, windowHandle);
        }

        private static bool PathsMatch(uint processId, string expectedPath)
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

        private static bool IsShellWindow(IntPtr windowHandle)
        {
            return windowHandle == NativeMethods.GetShellWindow()
                || windowHandle == NativeMethods.GetDesktopWindow();
        }

        private static string ReadTitle(IntPtr windowHandle)
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
