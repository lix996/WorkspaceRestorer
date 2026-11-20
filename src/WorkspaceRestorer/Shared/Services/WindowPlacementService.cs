using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// Applies a saved top-level window placement after an application has
    /// started.  Electron applications often create their native window after
    /// Process.Start returns, so the operation is bounded and asynchronous.
    /// </summary>
    internal static class WindowPlacementService
    {
        private const int StrongIdentityWaitAttempts = 50;
        // Electron windows can publish a bootstrap frame first and apply
        // their persisted layout later. Keep a short bounded correction
        // window so that late app initialization cannot overwrite the saved
        // position immediately after we restore it.
        private const int PlacementCorrectionAttempts = 12;
        private const int PlacementCorrectionDelayMilliseconds = 250;
        // Chromium 在恢复多个标签时可能先把窗口显示出来，等标签加载完再
        // 触发一次“显示/激活”。普通窗口 3 秒的复检不够，最小化的浏览器
        // 单独多盯一会儿，避免刚最小化完又被浏览器自己弹出来。
        // B1：恢复完成前只做一轮短复检（约 1 秒），不再把完成硬拖到 8 秒。
        // 最后一页已经有 3 秒 settle；这里只负责抓住“缩完马上又被弹回来”。
        private const int BrowserMinimizeCorrectionAttempts = 4;
        private const int BrowserNormalPositionTolerance = 4;
        // Explorer folder windows need to be visible/created before they can
        // be minimized reliably. Keep this short enough that the user sees
        // the restore finish quickly, but long enough for the shell window to
        // settle.
        private const int FolderMinimizeDelayMilliseconds = 600;
        private const int FolderMinimizeAttempts = 20;
        private static readonly object PendingFolderMinimizeLock = new object();
        private static readonly HashSet<IntPtr> PendingFolderMinimize = new HashSet<IntPtr>();

        /// <summary>浏览器最小化动作的真实结果；调用方不能再无条件写“成功”。</summary>
        internal enum BrowserMinimizeOutcome
        {
            Applied,
            AlreadyMinimized,
            WindowLost,
            TitleMismatch,
            Skipped
        }

        /// <summary>新一轮恢复开始前清掉上一轮记住的浏览器句柄和认领状态。</summary>
        internal static void ClearBrowserWindowHandles()
        {
            BrowserWindowTracker.Clear();
        }

        internal static ISet<IntPtr> CaptureExistingWindows(string executablePath)
        {
            return BrowserWindowLocator.CaptureExistingWindows(executablePath);
        }

        internal static void Schedule(
            int processId,
            string executablePath,
            string windowTitle,
            string windowIdentityPath,
            SavedWindowPlacement placement,
            ISet<IntPtr> existingWindows,
            bool browserWindow = false,
            IList<string> browserWindowTitles = null,
            string restoreKey = null,
            string taskbarAppId = null)
        {
            bool minimizedOnly = placement != null
                && !placement.HasBounds
                && string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase);
            if (placement == null
                || (!placement.HasBounds && !minimizedOnly)
                || OsMutationGuard.IsDisabled
                || ExternalEffectsDisabled())
            {
                return;
            }

            string normalizedExecutable = PathText.NormalizeExpanded(executablePath);
            HashSet<IntPtr> excludedWindows = existingWindows == null
                ? new HashSet<IntPtr>()
                : new HashSet<IntPtr>(existingWindows);
            ThreadPool.QueueUserWorkItem(delegate
            {
                ApplyWhenReady(
                    processId,
                    normalizedExecutable,
                    windowTitle,
                    windowIdentityPath,
                    placement,
                    excludedWindows,
                    browserWindow,
                    browserWindowTitles,
                    restoreKey,
                    taskbarAppId);
            });
        }

        private static void ApplyWhenReady(
            int processId,
            string executablePath,
            string windowTitle,
            string windowIdentityPath,
            SavedWindowPlacement placement,
            ISet<IntPtr> excludedWindows,
            bool browserWindow,
            IList<string> browserWindowTitles,
            string restoreKey,
            string taskbarAppId)
        {
            // 浏览器窗口认领器可能在标题还没发布时就先认领了空白引导窗口。
            // 这时不要再等标题匹配，否则摆位 worker 会一直找不到它，
            // 保存的最小化前位置就丢了。
            IntPtr claimed = BrowserWindowTracker.Get(restoreKey);
            if (claimed != IntPtr.Zero && NativeMethods.IsWindow(claimed))
            {
                ApplyAndCorrect(
                    claimed,
                    processId,
                    executablePath,
                    windowTitle,
                    windowIdentityPath,
                    placement,
                    excludedWindows,
                    browserWindow,
                    browserWindowTitles,
                    restoreKey,
                    taskbarAppId);
                return;
            }

            for (int attempt = 0; attempt < 150; attempt++)
            {
                // 认领器可能在摆位 worker 等待标题的期间先认领空白窗口。
                // 每轮都先看一次“本轮 key 已认领的句柄”，否则 FindWindow
                // 会因为它已被认领而跳过，摆位永远不生效。
                IntPtr claimedNow = BrowserWindowTracker.Get(restoreKey);
                if (claimedNow != IntPtr.Zero && NativeMethods.IsWindow(claimedNow))
                {
                    ApplyAndCorrect(
                        claimedNow,
                        processId,
                        executablePath,
                        windowTitle,
                        windowIdentityPath,
                        placement,
                        excludedWindows,
                        browserWindow,
                        browserWindowTitles,
                        restoreKey,
                        taskbarAppId);
                    return;
                }

                IntPtr window = FindWindow(
                    processId,
                    executablePath,
                    windowTitle,
                    windowIdentityPath,
                    excludedWindows,
                    ShouldAllowWeakWindowMatch(windowIdentityPath, attempt),
                    browserWindow,
                    browserWindowTitles);
                if (window != IntPtr.Zero)
                {
                    RememberBrowserWindow(restoreKey, window);
                    if (!string.IsNullOrWhiteSpace(taskbarAppId))
                    {
                        TaskbarIdentityService.ApplyToWindow(window, taskbarAppId);
                    }
                    ApplyAndCorrect(
                        window,
                        processId,
                        executablePath,
                        windowTitle,
                        windowIdentityPath,
                        placement,
                        excludedWindows,
                        browserWindow,
                        browserWindowTitles,
                        restoreKey,
                        taskbarAppId);
                    return;
                }

                Thread.Sleep(100);
            }
        }

        private static void RememberBrowserWindow(string restoreKey, IntPtr windowHandle)
        {
            if (string.IsNullOrWhiteSpace(restoreKey) || windowHandle == IntPtr.Zero)
            {
                return;
            }
            IntPtr previous = BrowserWindowTracker.Get(restoreKey);
            if (previous != IntPtr.Zero
                && previous != windowHandle
                && !BrowserWindowTracker.GetExcept(restoreKey).Contains(previous))
            {
                BrowserWindowTracker.RemoveClaim(previous);
            }
            BrowserWindowTracker.Set(restoreKey, windowHandle);
        }

        private static IntPtr GetRememberedBrowserWindow(string restoreKey)
        {
            return BrowserWindowTracker.Get(restoreKey);
        }

        private static HashSet<IntPtr> GetRememberedBrowserWindowsExcept(string restoreKey)
        {
            return BrowserWindowTracker.GetExcept(restoreKey);
        }

        /// <summary>
        /// 等第一页认领到的浏览器窗口。句柄失效、或标题已经不再像这个
        /// 窗口的任一保存页时，按整窗标题重找；不再“只要 IsWindow 就信”。
        /// 只找不缩；缩由 <see cref="ApplyMinimizedToBrowserWindow"/> 在最后一页之后做。
        /// </summary>
        internal static IntPtr WaitForBrowserWindow(
            string restoreKey,
            string executablePath,
            IList<string> browserWindowTitles,
            ISet<IntPtr> existingWindows,
            int timeoutMilliseconds)
        {
            return BrowserWindowLocator.WaitForBrowserWindow(
                restoreKey,
                executablePath,
                browserWindowTitles,
                existingWindows,
                timeoutMilliseconds);
        }

        internal static string ReadWindowTitle(IntPtr windowHandle)
        {
            return ReadWindowTitleCore(windowHandle);
        }

        internal static bool BrowserWindowStillPresentAfterMinimize(
            IntPtr windowHandle,
            out string title,
            out int pageCount)
        {
            return BrowserWindowVerifier.StillPresentAfterMinimize(
                windowHandle,
                out title,
                out pageCount);
        }

        /// <summary>
        /// 浏览器窗口的“最后一页已经启动完”之后，只在这里缩一次。
        /// 只有标题/页数都验证通过才动；窗口的普通位置已经正确时不再重复
        /// SetWindowPlacement（重复摆位可能让 Edge 重建/替换窗口），直接
        /// ShowWindow 最小化。缩完再做有界复检，防止 Chromium 又弹出来。
        /// </summary>
        internal static BrowserMinimizeOutcome ApplyMinimizedToBrowserWindow(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            Func<bool> stillCurrent = null)
        {
            return BrowserWindowMinimizer.Apply(
                windowHandle,
                placement,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                BrowserMinimizeCorrectionAttempts,
                PlacementCorrectionDelayMilliseconds,
                NormalPositionMatches,
                delegate(IntPtr handle, SavedWindowPlacement target)
                {
                    Apply(handle, target, null);
                },
                stillCurrent);
        }

        /// <summary>
        /// C：标题/页数校验失败时的最后兜底。只允许对**仍然存在的精确
        /// HWND**、且仍属于同一个浏览器进程的窗口执行一次最小化；
        /// 找不到窗口或进程对不上就放弃，绝不按标题找替代窗口。
        /// </summary>
        internal static bool TryForceMinimizeBrowserWindow(
            IntPtr windowHandle,
            string executablePath,
            Func<bool> stillCurrent = null)
        {
            if (windowHandle == IntPtr.Zero
                || string.IsNullOrWhiteSpace(executablePath)
                || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || !PathsMatch(processId, executablePath))
            {
                return false;
            }

            if (NativeMethods.IsIconic(windowHandle))
            {
                return true;
            }
            if (stillCurrent != null && !stillCurrent())
            {
                return false;
            }

            try
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
            }
            catch
            {
                return false;
            }
            Thread.Sleep(150);
            if (stillCurrent != null && !stillCurrent())
            {
                return false;
            }
            return NativeMethods.IsWindow(windowHandle)
                && NativeMethods.IsIconic(windowHandle);
        }

        /// <summary>
        /// 当前普通位置是否已经和保存值一致。用于决定最小化前要不要再
        /// 调 SetWindowPlacement：第一页已经按 DisplayPlacement 摆过，
        /// 大多数情况下这里应该直接返回 true。
        /// </summary>
        private static bool NormalPositionMatches(IntPtr windowHandle, SavedWindowPlacement placement)
        {
            if (placement == null || !placement.HasBounds)
            {
                return true;
            }
            try
            {
                NativeMethods.WindowPlacement current = NativeMethods.WindowPlacement.Create();
                if (!NativeMethods.GetWindowPlacement(windowHandle, ref current))
                {
                    return false;
                }
                int width = current.NormalPosition.Right - current.NormalPosition.Left;
                int height = current.NormalPosition.Bottom - current.NormalPosition.Top;
                return Math.Abs(current.NormalPosition.Left - placement.Left) <= BrowserNormalPositionTolerance
                    && Math.Abs(current.NormalPosition.Top - placement.Top) <= BrowserNormalPositionTolerance
                    && (placement.Width <= 0
                        || Math.Abs(width - placement.Width) <= BrowserNormalPositionTolerance)
                    && (placement.Height <= 0
                        || Math.Abs(height - placement.Height) <= BrowserNormalPositionTolerance);
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyAndCorrect(
            IntPtr window,
            int processId,
            string executablePath,
            string windowTitle,
            string windowIdentityPath,
            SavedWindowPlacement placement,
            ISet<IntPtr> excludedWindows,
            bool browserWindow,
            IList<string> browserWindowTitles,
            string restoreKey,
            string taskbarAppId)
        {
            Apply(window, placement, windowIdentityPath);

            // Some applications replace the bootstrap HWND while completing
            // startup. Re-query when that happens and keep all corrections
            // bounded; this worker must never remain alive indefinitely.
            bool minimized = placement != null
                && string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase);
            int correctionAttempts = browserWindow && minimized
                ? BrowserMinimizeCorrectionAttempts
                : PlacementCorrectionAttempts;
            for (int correction = 0; correction < correctionAttempts; correction++)
            {
                Thread.Sleep(PlacementCorrectionDelayMilliseconds);
                if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
                {
                    window = FindWindow(
                        processId,
                        executablePath,
                        windowTitle,
                        windowIdentityPath,
                        excludedWindows,
                        true,
                        browserWindow,
                        browserWindowTitles);
                    if (window == IntPtr.Zero)
                    {
                        continue;
                    }
                    // Edge/Electron 可能用新 HWND 替换引导窗口。记住替换后
                    // 的句柄，否则最后一页之后还会去缩那个已经失效的旧句柄。
                    if (browserWindow)
                    {
                        RememberBrowserWindow(restoreKey, window);
                    }
                    if (!string.IsNullOrWhiteSpace(taskbarAppId))
                    {
                        TaskbarIdentityService.ApplyToWindow(window, taskbarAppId);
                    }
                }

                // 【2026-11-05 用户报“缩→展→缩”】窗口只要已经最小化，就不再摆位：
                // 这个 worker 拿的是“最小化前的位置”（WindowState=Normal），
                // SetWindowPlacement 会带 SW_SHOWNORMAL，把刚缩下去的窗口重新展开；
                // 最后一页之后的缩窗再把它缩回去，就成了“缩→展→缩”循环
                // （Round201 实测缩/展间隔正好是这里的 250ms 校正节拍）。
                // 被 Edge 换过的替代窗口还没最小化，仍然会被摆位。
                if (NativeMethods.IsIconic(window))
                {
                    continue;
                }

                Apply(window, placement, windowIdentityPath);
            }

            // 应用可能在我们摆好之后又自己挪窗口（恢复自己的旧位置、延迟布局等）。
            // 这里只把“本次确实摆过的 HWND”交给当前窗口恢复策略；具体检查由
            // StartupRestore/Windows/WindowRestoreAudit 做，且只纠一次。
            WindowPlacementPolicy.Current.OnWindowPlaced(
                window,
                placement,
                string.IsNullOrWhiteSpace(windowIdentityPath) ? windowTitle : windowIdentityPath);
        }

        /// <summary>
        /// 标题、页数是否仍然像这个恢复窗口。最小化前后都用它做最后一道
        /// 检查：空白/初始化窗口、页数明显少于保存页数的窗口一律不算。
        /// </summary>
        internal static bool BrowserWindowMatchesExpectation(
            IntPtr windowHandle,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            out string title,
            out int pageCount)
        {
            return BrowserWindowVerifier.MatchesExpectation(
                windowHandle,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                out title,
                out pageCount);
        }

        /// <summary>
        /// Edge/Chrome 窗口标题里的页数：
        ///   “当前页 和另外 N 个页面 …” → N+1
        ///   单页窗口没有“和另外” → 1
        /// 标题本身（用户网页标题）也可能含“和另外”，所以从最后一个
        /// 标记往后解析；解析不出数字就当未知，不瞎猜。
        /// </summary>
        internal static bool TryReadBrowserPageCount(string title, out int pageCount)
        {
            return BrowserTitleProfile.TryReadPageCount(title, out pageCount);
        }

        /// <summary>取窗口标题里的“当前标签标题”，去掉“和另外/用户配置/浏览器名”等后缀。</summary>
        internal static string GetBrowserActiveTitle(string title)
        {
            return BrowserTitleProfile.GetActiveTitle(title);
        }

        internal static bool IsBlankBrowserWindowTitle(string title)
        {
            return BrowserTitleProfile.IsBlankWindowTitle(title);
        }

        private static bool TryReadBrowserTabCount(IntPtr windowHandle, out int tabCount)
        {
            return BrowserWindowVerifier.TryReadTabCount(windowHandle, out tabCount);
        }

        private static bool BrowserTitleFragmentsMatch(string actual, string expected)
        {
            return BrowserTitleProfile.BrowserTitleFragmentsMatch(actual, expected);
        }

        private static bool BrowserTitleMatchesAny(string actual, IList<string> expectedTitles)
        {
            return BrowserTitleProfile.BrowserTitleMatchesAny(actual, expectedTitles);
        }

        /// <summary>
        /// 恢复前已经存在的窗口里，有没有已经装着这一组页面的。
        /// 有的话说明页面被 Edge 合并进旧窗口了，本次不该再缩新窗口。
        /// </summary>
        internal static IntPtr FindExistingBrowserWindowWithExpectedPages(
            string executablePath,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            ISet<IntPtr> existingWindows,
            out string title,
            out int pageCount)
        {
            return BrowserFinalWindowResolver.FindExisting(
                executablePath,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                existingWindows,
                out title,
                out pageCount);
        }

        internal static IntPtr ResolveFinalBrowserWindow(
            string restoreKey,
            string executablePath,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            ISet<IntPtr> existingWindows,
            int timeoutMilliseconds,
            out string reason)
        {
            return BrowserFinalWindowResolver.Resolve(
                restoreKey,
                executablePath,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                existingWindows,
                timeoutMilliseconds,
                out reason);
        }

        /// <summary>
        /// 把已经验证过的浏览器窗口切到前台。只在确实最小化时 SW_RESTORE；
        /// 已经最大化/普通时不能乱 RESTORE，否则会把最大化窗口变回普通。
        /// </summary>
        internal static bool TryActivateBrowserWindow(IntPtr windowHandle)
        {
            return BrowserWindowActivator.TryActivate(windowHandle);
        }


        /// <summary>
        /// 判断一个顶层窗口标题是不是本批网页所在的浏览器窗口。
        /// Edge/Chrome 的窗口标题是“当前标签标题 + 和另外 N 个页面 +
        /// 用户配置 + 浏览器名”，而保存下来的网页标题只是标签标题，还可
        /// 能带“ - 内存使用率 - 100 MB”这种采集后缀；不能做精确相等。
        /// 同时排除 Edge 的“还原页面”这类同 exe 辅助窗口。
        /// </summary>
        internal static bool BrowserWindowTitleMatches(string actualTitle, IList<string> expectedTitles)
        {
            return BrowserTitleProfile.BrowserWindowTitleMatches(actualTitle, expectedTitles);
        }

        private static IntPtr FindWindow(
            int processId,
            string executablePath,
            string windowTitle,
            string windowIdentityPath,
            ISet<IntPtr> excludedWindows,
            bool allowWeakMatch,
            bool browserWindow,
            IList<string> browserWindowTitles)
        {
            IntPtr processMatch = IntPtr.Zero;
            IntPtr titleMatch = IntPtr.Zero;
            IntPtr identityMatch = IntPtr.Zero;
            IntPtr executableMatch = IntPtr.Zero;
            IntPtr browserTitleMatch = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle) || IsShellWindow(windowHandle))
                {
                    return true;
                }
                if ((excludedWindows != null && excludedWindows.Contains(windowHandle))
                    || IsWindowClaimed(windowHandle))
                {
                    return true;
                }

                uint ownerProcessId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out ownerProcessId);
                bool pathMatches = !string.IsNullOrWhiteSpace(executablePath)
                    && PathsMatch(ownerProcessId, executablePath);
                if (processMatch == IntPtr.Zero
                    && processId > 0
                    && ownerProcessId == (uint)processId)
                {
                    processMatch = windowHandle;
                }

                string currentTitle = null;
                if (browserWindow
                    && browserTitleMatch == IntPtr.Zero
                    && pathMatches)
                {
                    currentTitle = ReadWindowTitleCore(windowHandle);
                    if (BrowserWindowTitleMatches(currentTitle, browserWindowTitles))
                    {
                        browserTitleMatch = windowHandle;
                    }
                }
                if (titleMatch == IntPtr.Zero
                    && !string.IsNullOrWhiteSpace(windowTitle)
                    && (string.IsNullOrWhiteSpace(executablePath) || pathMatches))
                {
                    if (currentTitle == null)
                    {
                        currentTitle = ReadWindowTitleCore(windowHandle);
                    }
                }
                if (titleMatch == IntPtr.Zero
                    && !string.IsNullOrWhiteSpace(windowTitle)
                    && string.Equals(currentTitle, windowTitle.Trim(), StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(executablePath) || pathMatches))
                {
                    titleMatch = windowHandle;
                }

                if (identityMatch == IntPtr.Zero
                    && !string.IsNullOrWhiteSpace(windowIdentityPath)
                    && (string.IsNullOrWhiteSpace(executablePath) || pathMatches))
                {
                    if (currentTitle == null)
                    {
                        currentTitle = ReadWindowTitleCore(windowHandle);
                    }
                    if (TitleContainsPathIdentity(currentTitle, windowIdentityPath))
                    {
                        identityMatch = windowHandle;
                    }
                }

                if (executableMatch == IntPtr.Zero
                    && pathMatches)
                {
                    executableMatch = windowHandle;
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

            // A workspace-bearing title is a stronger identity than the PID.
            // Electron can hand several --new-window requests to one process,
            // so accepting any matching executable while titles are loading
            // can swap the saved positions of two VS Code windows.
            //
            // 浏览器不能走 executableMatch 兜底：Edge/Chrome 的“还原页面”
            // 辅助窗口也是同一个 exe。旧逻辑精确比较标签标题失败后，会抓
            // 第一个同 exe 的可见窗口，结果把“还原页面”最小化/搬到浏览器
            // 保存位置，真正的浏览器窗口反而留在桌面上。浏览器只认标签
            // 标题命中的窗口；标题还没加载出来就继续等，宁可不摆位置，
            // 也不能认错窗口。
            bool hasPathIdentity = !string.IsNullOrWhiteSpace(windowIdentityPath);
            IntPtr selected;
            if (browserWindow)
            {
                selected = browserTitleMatch;
                if (selected == IntPtr.Zero
                    && (browserWindowTitles == null || browserWindowTitles.Count == 0))
                {
                    selected = titleMatch;
                }
            }
            else
            {
                selected = hasPathIdentity ? identityMatch : titleMatch;
                if (selected == IntPtr.Zero && allowWeakMatch)
                {
                    selected = titleMatch != IntPtr.Zero
                        ? titleMatch
                        : (processMatch != IntPtr.Zero ? processMatch : executableMatch);
                }
            }
            return TryClaimWindow(selected) ? selected : IntPtr.Zero;
        }

        private static bool ShouldAllowWeakWindowMatch(string windowIdentityPath, int attempt)
        {
            return string.IsNullOrWhiteSpace(windowIdentityPath)
                || attempt >= StrongIdentityWaitAttempts;
        }

        private static bool TitleContainsPathIdentity(string windowTitle, string identityPath)
        {
            if (string.IsNullOrWhiteSpace(windowTitle) || string.IsNullOrWhiteSpace(identityPath))
            {
                return false;
            }

            string path = identityPath.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name;
            try
            {
                name = Path.GetFileName(path);
                if (File.Exists(path))
                {
                    string withoutExtension = Path.GetFileNameWithoutExtension(path);
                    if (!string.IsNullOrWhiteSpace(withoutExtension)
                        && windowTitle.IndexOf(withoutExtension, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
                name = null;
            }
            return !string.IsNullOrWhiteSpace(name)
                && windowTitle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsWindowClaimed(IntPtr windowHandle)
        {
            return BrowserWindowTracker.IsClaimed(windowHandle);
        }

        private static bool TryClaimWindow(IntPtr windowHandle)
        {
            return BrowserWindowTracker.TryClaim(windowHandle);
        }


        private static bool IsShellWindow(IntPtr windowHandle)
        {
            return windowHandle == NativeMethods.GetShellWindow()
                || windowHandle == NativeMethods.GetDesktopWindow();
        }

        private static string ReadWindowTitleCore(IntPtr windowHandle)
        {
            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return string.Empty;
                }

                System.Text.StringBuilder value = new System.Text.StringBuilder(length + 1);
                NativeMethods.GetWindowText(windowHandle, value, value.Capacity);
                return value.ToString().Trim();
            }
            catch
            {
                return string.Empty;
            }
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
                        actualPath = process.MainModule == null
                            ? null
                            : process.MainModule.FileName;
                    }
                    catch
                    {
                        // Elevated or protected processes can deny access to
                        // MainModule even when ProcessName is available.
                    }

                    if (!string.IsNullOrWhiteSpace(actualPath))
                    {
                        return string.Equals(
                            PathText.NormalizeExpanded(actualPath),
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase);
                    }

                    // Single-instance browsers and Explorer may return an
                    // existing process for a new-window request. If Windows
                    // withholds its full path, the executable name is the
                    // remaining safe identity available to us.
                    return ProcessNameMatches(process, expectedPath);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool ProcessNameMatches(Process process, string expectedPath)
        {
            if (process == null || string.IsNullOrWhiteSpace(expectedPath))
            {
                return false;
            }

            string expectedName;
            try
            {
                expectedName = Path.GetFileNameWithoutExtension(expectedPath.Trim());
            }
            catch
            {
                expectedName = null;
            }

            return !string.IsNullOrWhiteSpace(expectedName)
                && string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Apply(IntPtr windowHandle, SavedWindowPlacement placement, string windowIdentityPath)
        {
            if (windowHandle == IntPtr.Zero || placement == null)
            {
                return;
            }

            // Entries added by the user (no saved coordinates) may only want
            // the window opened minimized.  Apply that state without moving it.
            if (!placement.HasBounds)
            {
                if (string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase)
                    && !NativeMethods.IsIconic(windowHandle))
                {
                    bool folderWindow = !string.IsNullOrWhiteSpace(windowIdentityPath)
                        && Directory.Exists(windowIdentityPath);
                    if (folderWindow)
                    {
                        // Explorer folders have the same "not ready yet" issue
                        // even when there is no saved position: create it as a
                        // normal window first, then minimize after it settles.
                        ScheduleFolderMinimize(windowHandle);
                    }
                    else
                    {
                        try
                        {
                            NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                        }
                        catch
                        {
                        }
                    }
                }
                return;
            }

            // A folder saved minimized used to be forced to a normal window
            // because Explorer could leave it stuck on the taskbar.  We now
            // create/position it normally first and minimize it only after the
            // shell window has settled, so the saved minimized state is kept
            // without reintroducing the old "cannot reopen" problem.
            bool folderMinimized = IsMinimizedFolder(placement, windowIdentityPath);
            if (folderMinimized && NativeMethods.IsIconic(windowHandle))
            {
                // Explorer may already have restored it minimized; do not
                // flash it to normal just to minimize it again.
                return;
            }

            // Use the exact screen coordinates captured before shutdown and
            // the saved width/height so the restored window matches its saved
            // size, not the application's default.
            int[] savedPosition = ResolveSavedScreenPosition(placement);
            int screenX = savedPosition[0];
            int screenY = savedPosition[1];
            int showCommand = folderMinimized
                ? NativeMethods.SwShowNormal
                : ResolveShowCommand(placement.WindowState);

            int winWidth = placement.Width > 0 ? placement.Width : 0;
            int winHeight = placement.Height > 0 ? placement.Height : 0;

            // 【2026-10-18 用户要求】多屏拓扑适配：
            //   · 屏数没变 → 按“保存时第几块屏”对应（排列换了也跟着走）；
            //   · 单屏变多屏 → 放主屏；
            //   · 多屏变少屏（三屏→单屏、拔掉外接）→ 原来那块还在就留、否则放主屏，并且阶梯错开防重叠；
            //   · 大小/越界照旧交给 WindowScreenFit（已有单测）。
            // 规则与算法全在 `MonitorTopologyFit`（纯算术，有单测）。
            MonitorFitResult topology = null;
            try
            {
                List<MonitorWorkArea> monitors = EnumerateCurrentMonitors();
                if (monitors.Count > 0)
                {
                    string monitorKey = monitors.Count + "|" + (placement.MonitorIndex >= 0 ? placement.MonitorIndex : -1);
                    int alreadyPlaced = TakeRestoreSlot(monitorKey);
                    topology = MonitorTopologyFit.Decide(
                        monitors,
                        screenX,
                        screenY,
                        winWidth,
                        winHeight,
                        placement.MonitorIndex,
                        placement.MonitorCount,
                        alreadyPlaced);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 拓扑适配失败，退回原来的“最近屏幕”逻辑", exception);
                topology = null;
            }
            if (topology != null && topology.HasTarget)
            {
                if (topology.TopologyChanged
                    || topology.Left != screenX
                    || topology.Top != screenY
                    || (winWidth > 0 && topology.Width != winWidth)
                    || (winHeight > 0 && topology.Height != winHeight))
                {
                    AppLog.Write("[窗口恢复] 多屏拓扑：" + topology.Reason + "；" + windowIdentityPath
                        + " 保存=(" + screenX + "," + screenY + " " + winWidth + "x" + winHeight + ")"
                        + " → 恢复=(" + topology.Left + "," + topology.Top + " " + topology.Width + "x" + topology.Height + ")"
                        + " 第 " + (topology.MonitorIndex + 1) + " 块屏"
                        + (topology.TopologyChanged ? "（拓扑变了）" : string.Empty));
                }
                screenX = topology.Left;
                screenY = topology.Top;
                if (winWidth > 0)
                {
                    winWidth = topology.Width;
                }
                if (winHeight > 0)
                {
                    winHeight = topology.Height;
                }
            }
            else
            {
                // 拿不到显示器列表（接口失败）时的老路：取“离保存坐标最近的那块屏”再拟合。
                NativeMethods.NativeRect workArea;
                if (TryGetWorkArea(screenX, screenY, winWidth, winHeight, out workArea))
                {
                    int[] fitted = WindowScreenFit.Fit(
                        screenX,
                        screenY,
                        winWidth,
                        winHeight,
                        new[] { workArea.Left, workArea.Top, workArea.Right, workArea.Bottom });
                    if (fitted[0] != screenX || fitted[1] != screenY
                        || (winWidth > 0 && fitted[2] != winWidth)
                        || (winHeight > 0 && fitted[3] != winHeight))
                    {
                        AppLog.Write("[窗口恢复] 屏幕和保存时不一样，已把窗口拉回可见区：" + windowIdentityPath
                            + " 保存=(" + screenX + "," + screenY + " " + winWidth + "x" + winHeight + ")"
                            + " → 恢复=(" + fitted[0] + "," + fitted[1] + " " + fitted[2] + "x" + fitted[3] + ")"
                            + " 工作区=(" + workArea.Left + "," + workArea.Top + ")-(" + workArea.Right + "," + workArea.Bottom + ")");
                    }
                    screenX = fitted[0];
                    screenY = fitted[1];
                    // 只有原本知道大小才改大小：不知道大小时调用方传的是 0，
                    // 恢复时保持程序自己的窗口尺寸（见下面 `winWidth > 0 && winHeight > 0`）。
                    if (winWidth > 0)
                    {
                        winWidth = fitted[2];
                    }
                    if (winHeight > 0)
                    {
                        winHeight = fitted[3];
                    }
                }
            }
            bool applied = false;
            try
            {
                NativeMethods.WindowPlacement windowPlacement = NativeMethods.WindowPlacement.Create();
                if (NativeMethods.GetWindowPlacement(windowHandle, ref windowPlacement))
                {
                    // Set the saved update bounds. SetWindowPlacement applies
                    // the normal/maximized/minimized state atomically, so a
                    // restored window opens at the saved location/size.
                    windowPlacement.NormalPosition.Left = screenX;
                    windowPlacement.NormalPosition.Top = screenY;
                    if (winWidth > 0 && winHeight > 0)
                    {
                        windowPlacement.NormalPosition.Right = screenX + winWidth;
                        windowPlacement.NormalPosition.Bottom = screenY + winHeight;
                    }
                    windowPlacement.ShowCommand = showCommand;
                    applied = NativeMethods.SetWindowPlacement(windowHandle, ref windowPlacement);
                }
            }
            catch
            {
                // Fall through to the legacy best-effort path below.
            }

            if (!applied)
            {
                ApplyLegacy(windowHandle, screenX, screenY, winWidth, winHeight,
                    folderMinimized ? "Normal" : placement.WindowState);
            }
            if (folderMinimized)
            {
                // The window is now created and positioned; minimize after a
                // short settle delay so Explorer cannot leave it stuck.
                ScheduleFolderMinimize(windowHandle);
            }
            else if (string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase)
                && !NativeMethods.IsIconic(windowHandle))
            {
                // 有些程序（浏览器/Electron）不认 SetWindowPlacement 的
                // SW_SHOWMINIMIZED；补一次 ShowWindow，保证“勾了最小化”真的缩。
                try
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 文件夹保存时是最小化：先正常创建/定位，等 Shell 窗口稳定后再最小化。
        /// 直接一开始就 SW_SHOWMINIMIZED 会在 Explorer 尚未创建好窗口时
        /// 只留下一个点不开的任务栏按钮（旧版强制改回正常窗口的原因）。
        /// </summary>
        private static void ScheduleFolderMinimize(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }
            lock (PendingFolderMinimizeLock)
            {
                if (!PendingFolderMinimize.Add(windowHandle))
                {
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    for (int attempt = 0; attempt < FolderMinimizeAttempts; attempt++)
                    {
                        Thread.Sleep(attempt == 0
                            ? FolderMinimizeDelayMilliseconds
                            : PlacementCorrectionDelayMilliseconds);
                        if (!NativeMethods.IsWindow(windowHandle))
                        {
                            return;
                        }
                        if (NativeMethods.IsIconic(windowHandle))
                        {
                            return;
                        }
                        try
                        {
                            NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                        }
                        catch
                        {
                        }
                        if (NativeMethods.IsIconic(windowHandle))
                        {
                            return;
                        }
                    }
                    AppLog.Write("[窗口恢复] 文件夹窗口延迟最小化没有成功：" + windowHandle);
                }
                finally
                {
                    lock (PendingFolderMinimizeLock)
                    {
                        PendingFolderMinimize.Remove(windowHandle);
                    }
                }
            });
        }

        /// <summary>
        /// 恢复前文件夹已经开着时，不再启动第二个窗口，只把已存在的窗口
        /// 按保存状态最小化（窗口已经最小化时不动它）。
        /// </summary>
        internal static void MinimizeExistingWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero
                || !NativeMethods.IsWindow(windowHandle)
                || NativeMethods.IsIconic(windowHandle))
            {
                return;
            }
            try
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 枚举当前所有显示器的工作区（物理像素、已扣任务栏）。
        /// 【2026-10-18】多屏拓扑适配要用：只靠 `MonitorFromRect(最近)` 没法知道“现在一共几块屏”。
        /// 复制模式下两块屏的坐标完全一样，`MonitorTopologyFit.Normalize` 会按矩形去重。
        /// </summary>
        internal static List<MonitorWorkArea> EnumerateCurrentMonitors()
        {
            List<MonitorWorkArea> monitors = new List<MonitorWorkArea>();
            try
            {
                NativeMethods.EnumMonitorsProc callback = delegate(
                    IntPtr monitorHandle, IntPtr deviceContext, ref NativeMethods.NativeRect rectangle, IntPtr data)
                {
                    try
                    {
                        NativeMethods.MonitorInfo info = NativeMethods.MonitorInfo.Create();
                        if (NativeMethods.GetMonitorInfo(monitorHandle, ref info))
                        {
                            monitors.Add(new MonitorWorkArea
                            {
                                Left = info.rcWork.Left,
                                Top = info.rcWork.Top,
                                Right = info.rcWork.Right,
                                Bottom = info.rcWork.Bottom,
                                IsPrimary = (info.dwFlags & NativeMethods.MonitorInfoPrimary) != 0
                            });
                        }
                    }
                    catch
                    {
                    }
                    return true;
                };
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 枚举显示器失败", exception);
            }
            return monitors;
        }

        /// <summary>
        /// 保存时记下“这个窗口在第几块屏、当时一共几块屏”。
        /// 调用方已经拿到了窗口矩形；这里只做一次枚举 + 判断中心点落在哪块。
        /// 枚举失败就保持 -1 / 0（＝不知道），恢复时退回老逻辑。
        /// </summary>
        internal static void FillMonitorInfo(SavedWindowPlacement placement)
        {
            if (placement == null || !placement.HasBounds)
            {
                return;
            }
            try
            {
                List<MonitorWorkArea> monitors = MonitorTopologyFit.Normalize(EnumerateCurrentMonitors());
                if (monitors.Count == 0)
                {
                    return;
                }
                int centerX = placement.Left + Math.Max(1, placement.Width) / 2;
                int centerY = placement.Top + Math.Max(1, placement.Height) / 2;
                int index = -1;
                for (int cursor = 0; cursor < monitors.Count; cursor++)
                {
                    if (monitors[cursor].Contains(centerX, centerY))
                    {
                        index = cursor;
                        break;
                    }
                }
                if (index < 0)
                {
                    // 中心点不在任何一块屏里（窗口大半在屏幕外）：用最近的那块。
                    long best = long.MaxValue;
                    for (int cursor = 0; cursor < monitors.Count; cursor++)
                    {
                        long distance = monitors[cursor].DistanceSquared(centerX, centerY);
                        if (distance < best)
                        {
                            best = distance;
                            index = cursor;
                        }
                    }
                }
                if (index >= 0)
                {
                    placement.MonitorIndex = index;
                    placement.MonitorCount = monitors.Count;
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 记录显示器序号失败（恢复时退回按坐标找屏）", exception);
            }
        }

        private static readonly object RestoreBatchLock = new object();
        private static readonly Dictionary<string, int> RestoreBatchCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>每次“立即恢复/开机恢复”开始时清空计数（拓扑变了才用它做阶梯错开）。</summary>
        internal static void ResetRestoreBatch()
        {
            lock (RestoreBatchLock)
            {
                RestoreBatchCounts.Clear();
            }
            // 清 map 的同时必须释放浏览器窗口的认领状态；只 Clear map
            // 会把旧 HWND 永远留在 ClaimedWindows 里，下一轮如果句柄被
            // 复用就会被误判成“别的恢复已经认领”。
            ClearBrowserWindowHandles();
            WindowPlacementPolicy.Current.Reset();
        }

        /// <summary>取这块屏上本次恢复已经放过的窗口数，并把它 +1（用于阶梯错开）。</summary>
        private static int TakeRestoreSlot(string key)
        {
            lock (RestoreBatchLock)
            {
                int count;
                RestoreBatchCounts.TryGetValue(key ?? string.Empty, out count);
                RestoreBatchCounts[key ?? string.Empty] = count + 1;
                return count;
            }
        }

        /// <summary>
        /// 取“保存位置所在（或离它最近）的那块显示器”的工作区。
        /// 工作区已经扣掉任务栏，所以贴边恢复不会钻到任务栏底下。
        /// 取不到（没有显示器、接口失败）时返回 false，调用方保持原坐标不动。
        /// </summary>
        private static bool TryGetWorkArea(int screenX, int screenY, int width, int height, out NativeMethods.NativeRect workArea)
        {
            workArea = default(NativeMethods.NativeRect);
            try
            {
                int w = width > 0 ? width : WindowScreenFit.UnknownSizeFallback;
                int h = height > 0 ? height : WindowScreenFit.UnknownSizeFallback;
                NativeMethods.NativeRect rect = new NativeMethods.NativeRect
                {
                    Left = screenX,
                    Top = screenY,
                    Right = screenX + w,
                    Bottom = screenY + h
                };
                IntPtr monitor = NativeMethods.MonitorFromRect(
                    ref rect,
                    (uint)NativeMethods.MonitorDefaultToNearest.ToInt64());
                if (monitor == IntPtr.Zero)
                {
                    return false;
                }
                NativeMethods.MonitorInfo info = NativeMethods.MonitorInfo.Create();
                if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                {
                    return false;
                }
                workArea = info.rcWork;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// `SetWindowPlacement` 失败时的兜底路径：用 SetWindowPos 摆位置。
        /// 宽高必须用**调用方算好的**（可能已经被 WindowScreenFit 缩过）：
        /// 这里以前自己从 placement 里重新读一遍，于是“屏幕变小后拉回来”的效果
        /// 在这个兜底分支上会整段丢掉（窗口又被撑回 2560 宽）。
        /// </summary>
        private static void ApplyLegacy(IntPtr windowHandle, int screenX, int screenY, int width, int height, string windowState)
        {
            try
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowNormal);
                NativeMethods.SetWindowPos(
                    windowHandle,
                    IntPtr.Zero,
                    screenX,
                    screenY,
                    width > 0 ? width : 0,
                    height > 0 ? height : 0,
                    NativeMethods.SwpNoActivate
                        | NativeMethods.SwpNoZOrder
                        | NativeMethods.SwpShowWindow);
                if (string.Equals(windowState, "Maximized", StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMaximized);
                }
                else if (string.Equals(windowState, "Minimized", StringComparison.OrdinalIgnoreCase))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 判断这条保存记录是不是“有坐标的文件夹，且保存时最小化”。
        /// 有坐标才需要先正常摆放再延迟最小化；没有坐标的用户新增项
        /// 仍可用原来的立即最小化路径。
        /// </summary>
        internal static bool IsMinimizedFolder(
            SavedWindowPlacement placement,
            string windowIdentityPath)
        {
            return placement != null
                && placement.HasBounds
                && string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(windowIdentityPath)
                && Directory.Exists(windowIdentityPath);
        }

        private static int ResolveShowCommand(string windowState)
        {
            if (string.Equals(windowState, "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                return NativeMethods.SwShowMaximized;
            }

            if (string.Equals(windowState, "Minimized", StringComparison.OrdinalIgnoreCase))
            {
                return NativeMethods.SwShowMinimized;
            }

            return NativeMethods.SwShowNormal;
        }

        private static int[] ResolveSavedScreenPosition(SavedWindowPlacement placement)
        {
            if (placement == null)
            {
                return new[] { 0, 0 };
            }

            // Keep the saved screen coordinates verbatim.  Monitor-relative
            // recalculation is intentionally deferred until a future,
            // explicit multi-display mode is designed and tested.
            return new[] { placement.Left, placement.Top };
        }

        private static bool ExternalEffectsDisabled()
        {
            return IsTruthy("WORKSPACE_RESTORER_TEST_MODE")
                || IsTruthy("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS");
        }

        private static bool IsTruthy(string variableName)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
