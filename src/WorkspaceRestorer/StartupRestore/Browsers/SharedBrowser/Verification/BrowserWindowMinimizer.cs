using System;
using System.Collections.Generic;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器最后一页之后的唯一缩窗流程：先校验标题/页数，再只缩一次，
    /// 缩后只检查是否被浏览器自己弹回来。窗口摆放本身仍用通用回调。
    /// </summary>
    internal static class BrowserWindowMinimizer
    {
        internal static WindowPlacementService.BrowserMinimizeOutcome Apply(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            int correctionAttempts,
            int correctionDelayMilliseconds,
            Func<IntPtr, SavedWindowPlacement, bool> normalPositionMatches,
            Action<IntPtr, SavedWindowPlacement> applyPlacement,
            Func<bool> stillCurrent = null)
        {
            if (placement == null)
            {
                return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
            }
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.WindowLost;
            }
            if (stillCurrent != null && !stillCurrent())
            {
                return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
            }

            string title;
            int pageCount;
            if (!BrowserWindowVerifier.MatchesExpectation(
                windowHandle,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                out title,
                out pageCount))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.TitleMismatch;
            }
            if (NativeMethods.IsIconic(windowHandle))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.AlreadyMinimized;
            }

            if (placement.HasBounds
                && normalPositionMatches != null
                && !normalPositionMatches(windowHandle, placement))
            {
                applyPlacement(windowHandle, placement);
            }
            if (!IsCurrent(stillCurrent))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
            }
            if (!NativeMethods.IsIconic(windowHandle))
            {
                try
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                }
                catch
                {
                }
            }

            for (int attempt = 0; attempt < correctionAttempts; attempt++)
            {
                if (!IsCurrent(stillCurrent))
                {
                    return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
                }
                Thread.Sleep(correctionDelayMilliseconds);
                if (!IsCurrent(stillCurrent))
                {
                    return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
                }
                if (!NativeMethods.IsWindow(windowHandle))
                {
                    return WindowPlacementService.BrowserMinimizeOutcome.WindowLost;
                }
                if (NativeMethods.IsIconic(windowHandle))
                {
                    continue;
                }
                if (!BrowserWindowVerifier.MatchesExpectation(
                    windowHandle,
                    expectedTitles,
                    lastPageTitle,
                    expectedPageCount,
                    out title,
                    out pageCount))
                {
                    return WindowPlacementService.BrowserMinimizeOutcome.TitleMismatch;
                }
                applyPlacement(windowHandle, placement);
                if (!IsCurrent(stillCurrent))
                {
                    return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
                }
                if (!NativeMethods.IsIconic(windowHandle))
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

            if (!IsCurrent(stillCurrent))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
            }
            if (!NativeMethods.IsWindow(windowHandle))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.WindowLost;
            }
            if (NativeMethods.IsIconic(windowHandle))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.Applied;
            }
            if (!BrowserWindowVerifier.MatchesExpectation(
                windowHandle,
                expectedTitles,
                lastPageTitle,
                expectedPageCount,
                out title,
                out pageCount))
            {
                return WindowPlacementService.BrowserMinimizeOutcome.TitleMismatch;
            }
            return WindowPlacementService.BrowserMinimizeOutcome.Skipped;
        }

        private static bool IsCurrent(Func<bool> stillCurrent)
        {
            return stillCurrent == null || stillCurrent();
        }
    }
}
