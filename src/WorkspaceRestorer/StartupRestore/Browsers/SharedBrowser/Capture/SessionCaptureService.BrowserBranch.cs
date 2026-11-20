using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed partial class SessionCaptureService
    {
        /// <summary>一次浏览器采集里累计的“空白标签/未读窗口”统计，供快照和完成提示使用。</summary>
        internal sealed class BrowserCaptureTotals
        {
            internal int IgnoredBlankTabs;
            internal int UnreadPages;
            internal int UnreadMinimizedWindows;
            internal int UnreadNormalWindows;
            internal List<BrowserUnreadWindowInfo> UnreadWindows =
                new List<BrowserUnreadWindowInfo>();
        }

        /// <summary>
        /// 一个浏览器进程里的所有可见窗口/标签捕获。主循环只负责识别浏览器，
        /// 具体窗口分组、UIA/CDP 读取和最小化窗口临时还原都在这里。
        /// </summary>
        private static void CaptureBrowserWindowGroup(
            IEnumerable<WindowCandidate> processWindows,
            string executablePath,
            string processName,
            IDictionary<string, int> nextBrowserWindowIndexByGroup,
            DateTime startTimeUtc,
            SessionCaptureOptions options,
            List<SessionItem> result,
            ref bool browserCaptureIncomplete,
            ref List<string> unreadSummaries,
            BrowserCaptureTotals totals)
        {
                        {
                            string browserGroupKey = GetBrowserGroupKey(executablePath);
                            string browserName = BrowserCatalog.GetDisplayName(executablePath, processName);
                            int nextBrowserWindowIndex;
                            if (!nextBrowserWindowIndexByGroup.TryGetValue(browserGroupKey, out nextBrowserWindowIndex))
                            {
                                nextBrowserWindowIndex = 0;
                            }

                            List<WindowCandidate> browserWindows = processWindows
                                .OrderByDescending(delegate(WindowCandidate item) { return item.IsForeground; })
                                .ThenBy(delegate(WindowCandidate item) { return item.WindowTitle ?? string.Empty; }, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();
                            for (int localWindowIndex = 0; localWindowIndex < browserWindows.Count; localWindowIndex++)
                            {
                                WindowCandidate browserWindow = browserWindows[localWindowIndex];
                                int browserWindowIndex = nextBrowserWindowIndex++;
                                SessionItem browserWindowItem = new SessionItem
                                {
                                    Kind = SessionItemKind.Application,
                                    DisplayName = BuildBrowserWindowDisplayName(browserName, browserWindowIndex),
                                    BrowserName = browserName,
                                    WindowTitle = browserWindow.WindowTitle,
                                    ExecutablePath = executablePath,
                                    Arguments = null,
                                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                                    CapturedProcessId = browserWindow.ProcessId,
                                    StartTimeUtc = startTimeUtc,
                                    BrowserGroupKey = browserGroupKey,
                                    BrowserWindowIndex = browserWindowIndex,
                                    WindowPlacement = CaptureWindowPlacement(browserWindow.WindowHandle),
                                    CaptureSource = "VisibleBrowserWindow"
                                };

                                if (!options.IncludeBrowserPages)
                                {
                                    result.Add(browserWindowItem);
                                    continue;
                                }

                                // Keep the whole interactive pass well inside
                                // the parent's worker timeout so a helper is
                                // never killed while a restored window is
                                // still waiting to be minimized again.
                                if (options.PageReadDeadlineUtc.HasValue
                                    && DateTime.UtcNow >= options.PageReadDeadlineUtc.Value)
                                {
                                    continue;
                                }

                                // 用户 2026-10-15 反馈：最小化的浏览器整个没被发现，
                                // 保存下来的方案里连这个窗口都没有。
                                //
                                // 原因：Chromium 在窗口最小化时根本不发布标签栏和
                                // 地址栏的无障碍树（实测最小化窗口里 TabItem 数量为
                                // 0），旧代码于是把它的网页清单当成空，并因为
                                // “没有标签就不是真正的浏览窗口”把它整条丢掉。
                                //
                                // 现在的做法：显式保存时把这个窗口安静地还原一下
                                // （SW_SHOWNOACTIVATE，只显示、不激活），并压到最底
                                // 层，读完标签立刻按原状态最小化回去。整个过程不抢
                                // 焦点、不盖住用户正在看的窗口。
                                bool browserWindowMinimized = NativeMethods.IsIconic(browserWindow.WindowHandle);
                                bool restoredForPageRead = false;
                                try
                                {
                                    List<BrowserPageCapture> pages;
                                    int expectedTabCount = 0;
                                    int knownUiTabCount = 0;
                                    int ignoredBlankTabCount = 0;
                                    int unreadTabCount = 0;
                                    bool onlyBlankTabs = false;

                                    if (browserWindowMinimized && options.AllowBrowserTabActivation)
                                    {
                                        // 只有“保存当前方案”这条显式路径会走到
                                        // 这里；被动抓取（AllowBrowserTabActivation
                                        // = false）仍然完全不碰用户窗口。
                                        restoredForPageRead = PrepareMinimizedBrowserWindowForPageRead(
                                            browserWindow.WindowHandle);
                                    }

                                    if (!options.AllowBrowserTabActivation
                                        || !TryCaptureBrowserPages(
                                            browserWindow.WindowHandle,
                                            browserWindow.WindowTitle,
                                            out pages,
                                            out expectedTabCount,
                                            // 被我们还原出来的窗口不是前台窗口，键盘
                                            // 兜底会把 Ctrl+L/Ctrl+Tab 发给用户的当前
                                            // 窗口，所以对它一律不做键盘兜底。
                                            !restoredForPageRead,
                                            out knownUiTabCount,
                                            out ignoredBlankTabCount,
                                            out unreadTabCount,
                                            out onlyBlankTabs))
                                    {
                                        pages = new List<BrowserPageCapture>();
                                    }

                                    // Keep the old active-address-bar path as a
                                    // compatibility fallback for browsers that do
                                    // not expose TabItem controls.
                                    if (pages.Count == 0)
                                    {
                                        string pageUrl;
                                        string pageTitle;
                                        if (TryCaptureBrowserPage(browserWindow.WindowHandle, browserWindow.WindowTitle, out pageUrl, out pageTitle))
                                        {
                                            pages.Add(new BrowserPageCapture
                                            {
                                                Url = pageUrl,
                                                Title = pageTitle,
                                                TabIndex = 0,
                                                IsActive = true,
                                                CaptureSource = "BrowserAddressBar"
                                            });
                                        }
                                    }

                                    // 上面的地址栏兜底可能把空白新标签页（about:blank）又加回来；
                                    // 统一再清一次，保证只有空白标签的窗口不会被保存成幽灵窗口。
                                    RemoveBlankNewTabPages(pages);

                                    // Best-effort Chromium DevTools Protocol read.
                                    // When the browser exposes its debug endpoint it
                                    // lists EVERY tab (including background and
                                    // inactive ones) that UI Automation cannot read.
                                    // Only accept the list when this window's title
                                    // matches a CDP page target, and merge by URL so
                                    // a window gets its full tab set without
                                    // duplicating what UI Automation already read.
                                    if (options.AllowBrowserTabActivation
                                        && !ExternalEffectsDisabled())
                                    {
                                        List<BrowserDebugProtocol.BrowserTabEntry> cdpTabs =
                                            BrowserDebugProtocol.TryReadPageTabs(
                                                BrowserDebugProtocol.DefaultDebugPort);
                                        if (CdpListMatchesWindow(cdpTabs, browserWindow.WindowTitle))
                                        {
                                            for (int c = 0; c < cdpTabs.Count; c++)
                                            {
                                                BrowserDebugProtocol.BrowserTabEntry cdp = cdpTabs[c];
                                                if (string.IsNullOrWhiteSpace(cdp.Url)
                                                    || ContainsPageUrl(pages, cdp.Url))
                                                {
                                                    continue;
                                                }
                                                pages.Add(new BrowserPageCapture
                                                {
                                                    Url = cdp.Url,
                                                    Title = string.IsNullOrWhiteSpace(cdp.Title)
                                                        ? browserWindow.WindowTitle
                                                        : cdp.Title,
                                                    TabIndex = pages.Count,
                                                    IsActive = pages.Count == 0,
                                                    CaptureSource = "BrowserDebugProtocol"
                                                });
                                            }
                                        }
                                    }

                                    // Report unread pages against the window's
                                    // live UI Automation tab count only.  The
                                    // localized title phrase (for example
                                    // "和另外 2 个页面") can be stale or belong
                                    // to another window and previously made a
                                    // one-tab window look like three.
                                    totals.IgnoredBlankTabs += ignoredBlankTabCount;

                                    bool countKnown = knownUiTabCount > 0;
                                    int expectedLimit = countKnown ? knownUiTabCount : expectedTabCount;
                                    if (options.AllowBrowserTabActivation
                                        && expectedLimit > 0
                                        && pages.Count < expectedLimit)
                                    {
                                        browserCaptureIncomplete = true;
                                        int missing = expectedLimit - pages.Count;
                                        totals.UnreadPages += missing;
                                        if (browserWindowMinimized)
                                        {
                                            totals.UnreadMinimizedWindows++;
                                        }
                                        else
                                        {
                                            totals.UnreadNormalWindows++;
                                        }
                                        totals.UnreadWindows.Add(new BrowserUnreadWindowInfo
                                        {
                                            DisplayName = browserWindowItem.DisplayName,
                                            UnreadPageCount = missing,
                                            Minimized = browserWindowMinimized
                                        });
                                        string countNote = countKnown
                                            ? "读到 " + pages.Count + " / " + expectedLimit + " 个标签"
                                            : "读到 " + pages.Count + " 个标签，标签总数未知（可能有未读取）";
                                        unreadSummaries.Add(
                                            (browserWindowItem.DisplayName ?? "浏览器窗口")
                                            + "：" + countNote
                                            + (browserWindowMinimized ? "（最小化窗口）" : "（普通窗口）"));
                                    }

                                    // Keep this browser window only when it is a
                                    // real browsing window: it either yielded at
                                    // least one page, or it exposed browser tabs
                                    // (even if a URL could not be read this time).
                                    // A window with neither is an Edge internal /
                                    // auxiliary window and is left out so it does
                                    // not become a "还原页面" ghost entry.
                                    //
                                    // 用户 2026-10-15 反馈：最小化的窗口必须被记录下
                                    // 来。一个读取失败但仍是最小化的真实浏览窗口，也要
                                    // 保存窗口本身（哪怕暂时读不到网址），这样它在方案
                                    // 里不会凭空消失；读取时不要还原窗口。
                                    bool keepBrowserWindow =
                                        pages.Count > 0 || knownUiTabCount > 0 || expectedTabCount > 0;
                                    if (!keepBrowserWindow && restoredForPageRead && !onlyBlankTabs)
                                    {
                                        // 还原过就说明这是一个真实的浏览器窗口：内部
                                        // 辅助窗口不会被还原读取。只有空白新标签页的
                                        // 窗口除外（否则会留下没有页面的幽灵窗口）。
                                        keepBrowserWindow = true;
                                    }
                                    // 2026-10-31：把整窗标签指纹一起存下来。
                                    // 跳转时用“网址 + 标签标题 + 序号”做唯一匹配，
                                    // 不再只看窗口标题（标签名字可能就是网址）。
                                    List<BrowserTabFingerprint> tabFingerprints =
                                        BuildBrowserTabFingerprints(pages);
                                    browserWindowItem.BrowserTabs = tabFingerprints;
                                    browserWindowItem.BrowserWindowTitleSnapshot = browserWindow.WindowTitle;
                                    browserWindowItem.BrowserBindingKind = BrowserPageBindingKind.Captured;

                                    if (keepBrowserWindow)
                                    {
                                        result.Add(browserWindowItem);
                                    }
                                    else if (browserWindowMinimized && !onlyBlankTabs)
                                    {
                                        browserCaptureIncomplete = true;
                                        // 连标签都没暴露时不知道缺几个，按“至少 1 个”提示，
                                        // 不能写死成窗口的真实页数。
                                        totals.UnreadPages += 1;
                                        totals.UnreadMinimizedWindows++;
                                        totals.UnreadWindows.Add(new BrowserUnreadWindowInfo
                                        {
                                            DisplayName = browserWindowItem.DisplayName,
                                            UnreadPageCount = 1,
                                            Minimized = true
                                        });
                                        unreadSummaries.Add(
                                            (browserWindowItem.DisplayName ?? "浏览器窗口")
                                            + "：这个窗口是最小化的，读到 0 个标签");
                                    }

                                    for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
                                    {
                                        BrowserPageCapture page = pages[pageIndex];
                                        if (page == null || string.IsNullOrWhiteSpace(page.Url))
                                        {
                                            continue;
                                        }

                                        string pageTitle = page.Title;
                                        result.Add(new SessionItem
                                        {
                                            Kind = SessionItemKind.WebPage,
                                            DisplayName = string.IsNullOrWhiteSpace(pageTitle)
                                                ? (string.IsNullOrWhiteSpace(browserWindow.WindowTitle) ? page.Url : browserWindow.WindowTitle)
                                                : pageTitle,
                                            WindowTitle = string.IsNullOrWhiteSpace(pageTitle) ? browserWindow.WindowTitle : pageTitle,
                                            Url = page.Url,
                                            TargetPath = page.Url,
                                            ExecutablePath = executablePath,
                                            BrowserExecutablePath = executablePath,
                                            BrowserName = browserName,
                                            BrowserGroupKey = browserGroupKey,
                                            ParentId = browserWindowItem.Id,
                                            CapturedProcessId = browserWindow.ProcessId,
                                            StartTimeUtc = startTimeUtc,
                                            BrowserWindowIndex = browserWindowIndex,
                                            BrowserTabIndex = page.TabIndex < 0 ? pageIndex : page.TabIndex,
                                            IsActiveBrowserTab = page.IsActive,
                                            BrowserTabIdentityCaptured = string.Equals(
                                                    page.CaptureSource,
                                                    "BrowserAddressBar+TabAutomation",
                                                    StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(
                                                    page.CaptureSource,
                                                    "BrowserKeyboard",
                                                    StringComparison.OrdinalIgnoreCase),
                                            BrowserTabs = CloneBrowserTabFingerprints(tabFingerprints),
                                            BrowserWindowTitleSnapshot = browserWindow.WindowTitle,
                                            BrowserBindingKind = BrowserPageBindingKind.Captured,
                                            // The first saved page is also the
                                            // browser-window launcher during
                                            // restore. Attach this window's
                                            // proportional placement to it.
                                            WindowPlacement = pageIndex == 0
                                                ? CaptureWindowPlacement(browserWindow.WindowHandle)
                                                : null,
                                            CaptureSource = page.CaptureSource
                                        });
                                    }
                                }
                                finally
                                {
                                    // 读完后立刻把这个窗口最小化回去，还原它原来
                                    // 的状态。只有我们主动还原过的窗口才会走这里，
                                    // 用户本来没最小化的窗口不会被碰。
                                    if (restoredForPageRead)
                                    {
                                        ReMinimizeBrowserWindowAfterPageRead(browserWindow.WindowHandle);
                                    }
                                }
                            }
                            nextBrowserWindowIndexByGroup[browserGroupKey] = nextBrowserWindowIndex;
                        }
        }

        private static List<BrowserTabFingerprint> BuildBrowserTabFingerprints(
            List<BrowserPageCapture> pages)
        {
            List<BrowserTabFingerprint> fingerprints = new List<BrowserTabFingerprint>();
            if (pages == null)
            {
                return fingerprints;
            }
            for (int index = 0; index < pages.Count; index++)
            {
                BrowserPageCapture page = pages[index];
                if (page == null
                    || (string.IsNullOrWhiteSpace(page.Title) && string.IsNullOrWhiteSpace(page.Url)))
                {
                    continue;
                }
                fingerprints.Add(new BrowserTabFingerprint
                {
                    Index = page.TabIndex < 0 ? index : page.TabIndex,
                    Title = page.Title,
                    Url = page.Url,
                    IsActive = page.IsActive
                });
            }
            fingerprints.Sort(delegate(BrowserTabFingerprint left, BrowserTabFingerprint right)
            {
                return left.Index.CompareTo(right.Index);
            });
            return fingerprints;
        }

        private static List<BrowserTabFingerprint> CloneBrowserTabFingerprints(
            List<BrowserTabFingerprint> source)
        {
            List<BrowserTabFingerprint> copy = new List<BrowserTabFingerprint>();
            if (source == null)
            {
                return copy;
            }
            for (int index = 0; index < source.Count; index++)
            {
                if (source[index] != null)
                {
                    copy.Add(source[index].Clone());
                }
            }
            return copy;
        }
    }
}
