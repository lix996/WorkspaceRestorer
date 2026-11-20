using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 浏览器窗口/标签定位、URL/标题匹配、临时还原与选择（从 BrowserPageJumpWorkspace.cs 搬迁）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 只对“最小化候选窗口”做临时安静还原：SW_SHOWNOACTIVATE（不抢焦点）
        /// + 压到 Z 序最底层（不盖住用户正在看的窗口）。
        /// 返回 true 表示窗口已经可读；调用方必须负责在不是目标时最小化回去。
        /// </summary>
        private static bool TryShowBrowserWindowForRead(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            if (!NativeMethods.IsIconic(windowHandle))
            {
                return true;
            }

            try
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowNoActivate);
                // 和保存侧读取最小化浏览器一样：还原后 Chromium 要过一小会儿才发布标签栏。
                System.Threading.Thread.Sleep(250);
                if (NativeMethods.IsIconic(windowHandle))
                {
                    return false;
                }

                NativeMethods.SetWindowPos(
                    windowHandle,
                    NativeMethods.HWND_BOTTOM,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// A 的“真正不弹窗”版本：先记下窗口原来的 WINDOWPLACEMENT，再
        /// SW_SHOWNOACTIVATE 并立刻移到屏幕外，读标签时用户看不到窗口。
        /// 读完后必须用 <see cref="RestoreBrowserWindowAfterSilentRead"/> 还原
        /// （连同原来的最小化状态和普通位置）。
        /// </summary>
        private static bool TryCaptureBrowserWindowPlacement(
            IntPtr windowHandle,
            out NativeMethods.WindowPlacement placement)
        {
            placement = NativeMethods.WindowPlacement.Create();
            return windowHandle != IntPtr.Zero
                && NativeMethods.IsWindow(windowHandle)
                && NativeMethods.GetWindowPlacement(windowHandle, ref placement);
        }

        private static bool TryPrepareBrowserWindowForSilentRead(
            IntPtr windowHandle,
            ref NativeMethods.WindowPlacement savedPlacement,
            out bool restoreNeeded)
        {
            restoreNeeded = false;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            if (!NativeMethods.IsIconic(windowHandle))
            {
                // 用户在这几毫秒里自己还原了：不要再摆它，直接只读。
                return false;
            }

            restoreNeeded = true;
            try
            {
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowNoActivate);
                // 同一轮消息处理里马上移出屏幕，避免在正常位置画出一帧。
                NativeMethods.SetWindowPos(
                    windowHandle,
                    NativeMethods.HWND_BOTTOM,
                    -32000,
                    -32000,
                    0,
                    0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                System.Threading.Thread.Sleep(200);
                if (NativeMethods.IsIconic(windowHandle))
                {
                    return false;
                }
                // 有些 Chromium 版本在 SW_SHOWNOACTIVATE 后会先回到原位置，
                // 再补一次，确保读取期间确实在屏幕外。
                NativeMethods.SetWindowPos(
                    windowHandle,
                    NativeMethods.HWND_BOTTOM,
                    -32000,
                    -32000,
                    0,
                    0,
                    NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RestoreBrowserWindowAfterSilentRead(
            IntPtr windowHandle,
            ref NativeMethods.WindowPlacement savedPlacement)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return;
            }
            try
            {
                if (NativeMethods.SetWindowPlacement(windowHandle, ref savedPlacement))
                {
                    return;
                }
            }
            catch
            {
            }
            try
            {
                if (!NativeMethods.IsIconic(windowHandle))
                {
                    // SetWindowPlacement 失败时至少把普通位置放回去，再最小化，
                    // 不能把用户窗口留在屏幕外。
                    int width = savedPlacement.NormalPosition.Right - savedPlacement.NormalPosition.Left;
                    int height = savedPlacement.NormalPosition.Bottom - savedPlacement.NormalPosition.Top;
                    if (width > 0 && height > 0)
                    {
                        NativeMethods.SetWindowPos(
                            windowHandle,
                            NativeMethods.HWND_BOTTOM,
                            savedPlacement.NormalPosition.Left,
                            savedPlacement.NormalPosition.Top,
                            width,
                            height,
                            NativeMethods.SwpNoActivate);
                    }
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                }
            }
            catch
            {
            }
        }

        private static void ReMinimizeBrowserWindowAfterJumpProbe(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return;
            }
            try
            {
                if (!NativeMethods.IsIconic(windowHandle))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowMinimized);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 扫描专用：最小化窗口安静还原 -> 读标签 -> **无论找没找到都立刻最小化回去**。
        /// 只有最后选中的赢家才会被真正激活，扫描阶段不改变用户的窗口状态。
        /// </summary>
        private static bool TryLocateSavedTabForScan(
            IntPtr windowHandle,
            SessionItem page,
            out bool activeUrlMatched,
            out int targetTabIndex,
            out string targetTabName,
            out bool targetFromPosition,
            out int matchScore)
        {
            activeUrlMatched = false;
            targetTabIndex = -1;
            targetTabName = null;
            targetFromPosition = false;
            matchScore = 0;
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            if (!NativeMethods.IsWindow(windowHandle) || !NativeMethods.IsIconic(windowHandle))
            {
                return TryLocateSavedTab(
                    windowHandle, page,
                    out activeUrlMatched, out targetTabIndex, out targetTabName,
                    out targetFromPosition, out matchScore);
            }

            if (!TryShowBrowserWindowForRead(windowHandle))
            {
                ReMinimizeBrowserWindowAfterJumpProbe(windowHandle);
                return false;
            }

            try
            {
                return TryLocateSavedTab(
                    windowHandle, page,
                    out activeUrlMatched, out targetTabIndex, out targetTabName,
                    out targetFromPosition, out matchScore);
            }
            finally
            {
                ReMinimizeBrowserWindowAfterJumpProbe(windowHandle);
            }
        }

        /// <summary>
        /// 在**一个**浏览器窗口里定位保存的那一页。只读，不切换、不还原、不激活。
        ///
        /// 匹配顺序（稳定版）：
        ///   1. 当前活动标签网址 == 保存网址；
        ///   2. 标签名字本身就是保存网址（KeyCrop 这类 UIA 名字是 URL 的页面）；
        ///   3. 保存时整窗标签指纹（序号 + 标题 + 网址）；
        ///   4. 保存时的标签序号 + 标题/网址；
        ///   5. 窗口里唯一一个标题像保存标题的标签。
        /// 找不到唯一匹配就返回 false，绝不“猜一个”。
        /// </summary>
        /// <summary>
        /// 只读定位 + 有界重试。A 用它给 Chromium 发布标签栏留时间，
        /// 重试期间不激活窗口、不切换标签。
        /// </summary>
        private static bool TryLocateSavedTabWithRetry(
            IntPtr windowHandle,
            SessionItem page,
            int retryCount,
            out bool activeUrlMatched,
            out int targetTabIndex,
            out string targetTabName,
            out bool targetFromPosition,
            out int matchScore)
        {
            bool located = TryLocateSavedTab(
                windowHandle, page,
                out activeUrlMatched, out targetTabIndex, out targetTabName,
                out targetFromPosition, out matchScore);
            for (int attempt = 0; !located && attempt < retryCount; attempt++)
            {
                System.Threading.Thread.Sleep(300);
                if (!NativeMethods.IsWindow(windowHandle))
                {
                    break;
                }
                located = TryLocateSavedTab(
                    windowHandle, page,
                    out activeUrlMatched, out targetTabIndex, out targetTabName,
                    out targetFromPosition, out matchScore);
            }
            return located;
        }

        private static bool TryLocateSavedTab(
            IntPtr windowHandle,
            SessionItem page,
            out bool activeUrlMatched,
            out int targetTabIndex,
            out string targetTabName,
            out bool targetFromPosition,
            out int matchScore)
        {
            activeUrlMatched = false;
            targetTabIndex = -1;
            targetTabName = null;
            targetFromPosition = false;
            matchScore = 0;
            if (windowHandle == IntPtr.Zero || page == null)
            {
                return false;
            }

            string savedUrl = page.Url ?? page.TargetPath;
            string savedTitle = page.WindowTitle;
            if (string.IsNullOrWhiteSpace(savedTitle))
            {
                savedTitle = page.DisplayName;
            }
            int savedTabIndex = page.BrowserTabIndex;
            IList<BrowserTabFingerprint> savedTabs = page.BrowserTabs;

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }

                // 1. 当前显示的就是保存页：最可靠，不用切标签。
                //    但如果用户手动绑定的是“后台标签”，地址栏显示的是别的标签，
                //    不能因为旧网址还没更新就误判成目标；要求当前选中序号也吻合。
                string activeUrl = TryReadActiveTabUrl(root);
                bool activeUrlValueMatches = !string.IsNullOrWhiteSpace(activeUrl)
                    && !string.IsNullOrWhiteSpace(savedUrl)
                    && WebAddressTools.TabNameMatchesUrl(activeUrl, savedUrl);

                List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                if (tabs == null || tabs.Count == 0)
                {
                    if (activeUrlValueMatches
                        && page.BrowserBindingKind != BrowserPageBindingKind.UserBound)
                    {
                        activeUrlMatched = true;
                        matchScore = 1000;
                        return true;
                    }
                    return false;
                }

                List<string> names = new List<string>(tabs.Count);
                int selectedTabIndex = -1;
                for (int index = 0; index < tabs.Count; index++)
                {
                    names.Add(SessionCaptureService.TryReadAutomationProperty(
                        tabs[index], AutomationElement.NameProperty));
                    if (IsAutomationTabSelected(tabs[index]))
                    {
                        selectedTabIndex = index;
                    }
                }
                if (activeUrlValueMatches
                    && (page.BrowserBindingKind != BrowserPageBindingKind.UserBound
                        || savedTabIndex < 0
                        || selectedTabIndex == savedTabIndex))
                {
                    activeUrlMatched = true;
                    // 2026-11-06：把当前选中的序号一起带出去。两个窗口显示同一页时，
                    // 保存的 BrowserTabIndex 才能参与破平，选择框也显示正确的标签。
                    if (selectedTabIndex >= 0 && selectedTabIndex < names.Count)
                    {
                        targetTabIndex = selectedTabIndex;
                        targetTabName = names[selectedTabIndex];
                    }
                    matchScore = 1000;
                    return true;
                }

                // 2. 标签名字就是网址（UIA 对有些页面只给 keycrop.net/login?...）。
                int urlHit = -1;
                int urlHits = 0;
                for (int index = 0; index < names.Count; index++)
                {
                    if (!string.IsNullOrWhiteSpace(savedUrl)
                        && WebAddressTools.TabNameMatchesUrl(names[index], savedUrl))
                    {
                        urlHits++;
                        urlHit = index;
                    }
                }
                if (urlHits == 1)
                {
                    targetTabIndex = urlHit;
                    targetTabName = names[urlHit];
                    matchScore = 900;
                    return true;
                }
                if (urlHits > 1)
                {
                    // 同一窗口里真的有两个相同网址：保存的序号能区分就用它。
                    if (savedTabIndex >= 0
                        && savedTabIndex < names.Count
                        && WebAddressTools.TabNameMatchesUrl(names[savedTabIndex], savedUrl))
                    {
                        targetTabIndex = savedTabIndex;
                        targetTabName = names[savedTabIndex];
                        targetFromPosition = true;
                        matchScore = 880;
                        return true;
                    }
                    return false;
                }

                // 3. 保存时的整窗标签指纹：序号位同时核对 URL/标题。
                if (savedTabs != null && savedTabs.Count > 0)
                {
                    int fingerprintHit = -1;
                    int fingerprintHits = 0;
                    for (int index = 0; index < savedTabs.Count; index++)
                    {
                        BrowserTabFingerprint fingerprint = savedTabs[index];
                        if (fingerprint == null
                            || fingerprint.Index < 0
                            || fingerprint.Index >= names.Count)
                        {
                            continue;
                        }
                        // 整窗指纹里有很多标签；只有“这条指纹本身像目标页”
                        // 才能拿来确认，不能拿窗口里任意一个标签的指纹当目标。
                        bool fingerprintIsTarget =
                            (!string.IsNullOrWhiteSpace(savedUrl)
                                && !string.IsNullOrWhiteSpace(fingerprint.Url)
                                && WebAddressTools.TabNameMatchesUrl(fingerprint.Url, savedUrl))
                            || (!string.IsNullOrWhiteSpace(savedTitle)
                                && !string.IsNullOrWhiteSpace(fingerprint.Title)
                                && BrowserTabNameLooksLike(fingerprint.Title, savedTitle));
                        if (!fingerprintIsTarget)
                        {
                            continue;
                        }
                        string name = names[fingerprint.Index];
                        bool matches = (!string.IsNullOrWhiteSpace(fingerprint.Url)
                                && WebAddressTools.TabNameMatchesUrl(name, fingerprint.Url))
                            || (!string.IsNullOrWhiteSpace(fingerprint.Title)
                                && BrowserTabNameLooksLike(name, fingerprint.Title));
                        if (matches)
                        {
                            fingerprintHits++;
                            fingerprintHit = fingerprint.Index;
                        }
                    }
                    if (fingerprintHits == 1)
                    {
                        targetTabIndex = fingerprintHit;
                        targetTabName = names[fingerprintHit];
                        matchScore = 820;
                        return true;
                    }
                }

                // 4. 保存时的标签序号：只有标题/网址/指纹至少一项对得上才算数。
                if (savedTabIndex >= 0 && savedTabIndex < names.Count)
                {
                    string positionName = names[savedTabIndex];
                    bool positionUrl = !string.IsNullOrWhiteSpace(savedUrl)
                        && WebAddressTools.TabNameMatchesUrl(positionName, savedUrl);
                    bool positionFingerprint = SavedFingerprintMatches(
                        savedTabs, savedTabIndex, positionName, savedUrl, savedTitle);
                    bool positionTitle = BrowserTabNameLooksLike(positionName, savedTitle);
                    if (positionUrl)
                    {
                        targetTabIndex = savedTabIndex;
                        targetTabName = positionName;
                        matchScore = 860;
                        return true;
                    }
                    if (positionFingerprint)
                    {
                        targetTabIndex = savedTabIndex;
                        targetTabName = positionName;
                        targetFromPosition = true;
                        matchScore = 780;
                        return true;
                    }
                    if (positionTitle)
                    {
                        targetTabIndex = savedTabIndex;
                        targetTabName = positionName;
                        targetFromPosition = true;
                        matchScore = 650;
                        return true;
                    }
                }

                // 5. 唯一一个标题像保存标题的标签。
                int nameHit = -1;
                int nameHits = 0;
                for (int index = 0; index < names.Count; index++)
                {
                    if (BrowserTabNameLooksLike(names[index], savedTitle))
                    {
                        nameHits++;
                        nameHit = index;
                    }
                }
                if (nameHits == 1)
                {
                    targetTabIndex = nameHit;
                    targetTabName = names[nameHit];
                    matchScore = 560;
                    return true;
                }

                // 名字完全对不上、序号位也没有指纹/标题/网址支持：不要硬跳。
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static bool SavedFingerprintMatches(
            IList<BrowserTabFingerprint> fingerprints,
            int index,
            string currentName,
            string savedUrl,
            string savedTitle)
        {
            if (fingerprints == null || string.IsNullOrWhiteSpace(currentName))
            {
                return false;
            }
            for (int cursor = 0; cursor < fingerprints.Count; cursor++)
            {
                BrowserTabFingerprint fingerprint = fingerprints[cursor];
                if (fingerprint == null || fingerprint.Index != index)
                {
                    continue;
                }
                bool fingerprintIsTarget =
                    (!string.IsNullOrWhiteSpace(savedUrl)
                        && !string.IsNullOrWhiteSpace(fingerprint.Url)
                        && WebAddressTools.TabNameMatchesUrl(fingerprint.Url, savedUrl))
                    || (!string.IsNullOrWhiteSpace(savedTitle)
                        && !string.IsNullOrWhiteSpace(fingerprint.Title)
                        && BrowserTabNameLooksLike(fingerprint.Title, savedTitle));
                if (!fingerprintIsTarget)
                {
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(fingerprint.Url)
                    && WebAddressTools.TabNameMatchesUrl(currentName, fingerprint.Url))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(fingerprint.Title)
                    && BrowserTabNameLooksLike(currentName, fingerprint.Title))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 切换标签后核对一次地址栏。**先立刻读一次**（浏览器通常马上就把地址栏更新了），
        /// 读到目标网址就返回；只有在还没更新时才短等重试。
        /// 这样常见情况几乎没有额外等待，最坏情况也封顶在约 400 毫秒。
        /// </summary>
        private static bool VerifyActiveTabUrl(IntPtr windowHandle, string savedUrl, out bool matched)
        {
            matched = false;
            if (string.IsNullOrWhiteSpace(savedUrl))
            {
                return false;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                string current = TryReadActiveTabUrl(root);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    matched = WebAddressTools.TabNameMatchesUrl(current, savedUrl);
                    if (matched)
                    {
                        return true;
                    }
                }
                if (attempt < 4)
                {
                    System.Threading.Thread.Sleep(80);
                }
            }
            return true;
        }

        /// <summary>
        /// 读“当前选中标签”的网址（就是窗口地址栏里的值）。
        /// 只读，不会切换标签，所以可以放心在打分时调用。
        /// </summary>
        private static string TryReadActiveTabUrl(AutomationElement root)
        {
            if (root == null)
            {
                return null;
            }
            try
            {
                AutomationElementCollection edits = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                if (edits == null)
                {
                    return null;
                }
                for (int index = 0; index < Math.Min(edits.Count, 32); index++)
                {
                    AutomationElement edit = edits[index];
                    if (edit == null)
                    {
                        continue;
                    }
                    string name = SessionCaptureService.TryReadAutomationProperty(
                        edit,
                        AutomationElement.NameProperty);
                    string className = SessionCaptureService.TryReadAutomationProperty(
                        edit,
                        AutomationElement.ClassNameProperty);
                    if (!LooksLikeBrowserAddressBar(name, className))
                    {
                        continue;
                    }
                    string value = TryReadAutomationValueOf(edit);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// 判断一个 Edit 控件是不是浏览器地址栏。
        /// 实测本机 Edge：Name='地址和搜索栏'、ClassName='OmniboxViewViews'。
        /// 这里**只用地址栏专属的特征**（不像保存侧那样把“搜索框”也算进来），
        /// 免得把网页里的搜索框当成地址栏。
        /// </summary>
        private static bool LooksLikeBrowserAddressBar(string name, string className)
        {
            string text = ((name ?? string.Empty) + " " + (className ?? string.Empty)).ToLowerInvariant();
            return text.Contains("omnibox")
                || text.Contains("addresseditbox")
                || text.Contains("address")
                || text.Contains("地址")
                || text.Contains("address和搜索栏");
        }

        private static string TryReadAutomationValueOf(AutomationElement element)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    ValuePattern valuePattern = pattern as ValuePattern;
                    if (valuePattern != null)
                    {
                        return valuePattern.Current.Value;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>
        /// 数一数有几个标签的名字对得上保存的标题（用来判断“是不是有好几个同名标签，
        /// 所以没法按标题确定是哪一页”）。
        /// </summary>
        private static void CountTabsMatchingName(
            List<IntPtr> windows,
            string savedTitle,
            out int tabCount,
            out int windowCount)
        {
            tabCount = 0;
            windowCount = 0;
            if (windows == null || string.IsNullOrWhiteSpace(savedTitle))
            {
                return;
            }

            for (int index = 0; index < windows.Count; index++)
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(windows[index]);
                    if (root == null)
                    {
                        continue;
                    }
                    List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                    if (tabs == null)
                    {
                        continue;
                    }
                    int hitsInWindow = 0;
                    for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
                    {
                        string name = SessionCaptureService.TryReadAutomationProperty(
                            tabs[tabIndex],
                            AutomationElement.NameProperty);
                        if (BrowserTabNameLooksLike(name, savedTitle))
                        {
                            hitsInWindow++;
                        }
                    }
                    if (hitsInWindow > 0)
                    {
                        tabCount += hitsInWindow;
                        windowCount++;
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 按保存下来的标签序号选中标签，但**先确认这个标签现在确实是那一页**。
        /// 校验用“保存下来的标题”或“保存下来的网址”去比对该标签当前的名字。
        /// 对不上就返回 false，让调用方换下一个窗口试——
        /// 否则会在多个浏览器窗口之间“跳到了对的序号、错的内容”。
        /// </summary>
        private static bool TrySelectBrowserTabAt(IntPtr windowHandle, int tabIndex, string expectation)
        {
            if (windowHandle == IntPtr.Zero || tabIndex < 0)
            {
                return false;
            }
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                if (tabs == null || tabIndex >= tabs.Count)
                {
                    return false;
                }

                AutomationElement tab = tabs[tabIndex];
                if (tab == null)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(expectation))
                {
                    string name = SessionCaptureService.TryReadAutomationProperty(
                        tab,
                        AutomationElement.NameProperty);
                    if (!BrowserTabNameLooksLike(name, expectation))
                    {
                        return false;
                    }
                }

                return SessionCaptureService.TrySelectBrowserTab(tab);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断“标签当前的名字”和“保存时记下的标题或网址”是不是同一页。
        /// 允许浏览器附加后缀（实测 Edge 会加“ - 内存使用率 - 400 MB”），
        /// 所以用双向包含 + 去噪比对，而不是相等。
        /// </summary>
        private static bool BrowserTabNameLooksLike(string tabName, string expectation)
        {
            if (string.IsNullOrWhiteSpace(tabName) || string.IsNullOrWhiteSpace(expectation))
            {
                return false;
            }

            string name = tabName.Trim();
            string wanted = expectation.Trim();
            if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0
                || wanted.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string nameCore = StripBrowserTabTitleNoise(name);
            string wantedCore = StripBrowserTabTitleNoise(wanted);
            return !string.IsNullOrWhiteSpace(nameCore)
                && !string.IsNullOrWhiteSpace(wantedCore)
                && (string.Equals(nameCore, wantedCore, StringComparison.OrdinalIgnoreCase)
                    || nameCore.IndexOf(wantedCore, StringComparison.OrdinalIgnoreCase) >= 0
                    || wantedCore.IndexOf(nameCore, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool TrySelectBrowserTabByTitle(IntPtr windowHandle, string title)
        {
            if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
            {
                return false;
            }
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                // 必须用保存侧那一份“只认真标签栏”的实现：
                // 直接查 Descendants+TabItem 会把网页里的 ARIA 标签也算进来，
                // 按名字就可能命中假标签（用户 2026-10-15 反馈的跳转不准）。
                List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                if (tabs == null || tabs.Count == 0)
                {
                    return false;
                }

                string wanted = title.Trim();
                string wantedCore = StripBrowserTabTitleNoise(wanted);
                AutomationElement nameHit = null;
                AutomationElement coreHit = null;
                for (int index = 0; index < tabs.Count; index++)
                {
                    AutomationElement tab = tabs[index];
                    if (tab == null)
                    {
                        continue;
                    }
                    string name = SessionCaptureService.TryReadAutomationProperty(
                        tab,
                        AutomationElement.NameProperty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    // 保存下来的名字可能带浏览器附加的后缀（实测本机 Edge 会带
                    // “- 内存使用率 - 400 MB”），所以先宽松匹配、再退化到去噪比对。
                    if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        nameHit = tab;
                        break;
                    }
                    if (nameHit == null)
                    {
                        string nameCore = StripBrowserTabTitleNoise(name.Trim());
                        if ((!string.IsNullOrWhiteSpace(wantedCore) && !string.IsNullOrWhiteSpace(nameCore)
                                && string.Equals(nameCore, wantedCore, StringComparison.OrdinalIgnoreCase))
                            || (!string.IsNullOrWhiteSpace(wantedCore)
                                && name.IndexOf(wantedCore, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            if (coreHit == null)
                            {
                                coreHit = tab;
                            }
                        }
                    }
                }

                AutomationElement target = nameHit ?? coreHit;
                return target != null && SessionCaptureService.TrySelectBrowserTab(target);
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 去掉浏览器给标签名附加的尾巴，例如本机 Edge 的“ - 内存使用率 - 400 MB”。
        /// 只用于**比较**，不改动保存下来的原文。
        /// </summary>
        private static string StripBrowserTabTitleNoise(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string result = value.Trim();
            // 反复剥，直到没有再匹配的尾巴（有的窗口会连着挂两段）。
            for (int guard = 0; guard < 4; guard++)
            {
                int cut = -1;
                string[] markers = new[]
                {
                    " - 内存使用率", " - 記憶體使用量", " - Memory usage", " - メモリ使用率"
                };
                for (int index = 0; index < markers.Length; index++)
                {
                    int at = result.IndexOf(markers[index], StringComparison.OrdinalIgnoreCase);
                    if (at >= 0 && (cut < 0 || at < cut))
                    {
                        cut = at;
                    }
                }
                if (cut < 0)
                {
                    break;
                }
                result = result.Substring(0, cut).Trim();
            }
            return result;
        }

        private static bool TrySelectBrowserTabByUrl(IntPtr windowHandle, string url)
        {
            if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            try
            {
                // The debug interface lists every open tab's URL. Find the index
                // of the tab whose URL matches the saved one, then select the
                // matching tab in the accessibility tab strip (best-effort: the
                // two lists are usually in the same left-to-right order).
                List<BrowserDebugProtocol.BrowserTabEntry> tabs =
                    BrowserDebugProtocol.TryReadPageTabs(BrowserDebugProtocol.DefaultDebugPort);
                if (tabs == null)
                {
                    return false;
                }
                int matchIndex = -1;
                for (int index = 0; index < tabs.Count; index++)
                {
                    BrowserDebugProtocol.BrowserTabEntry tab = tabs[index];
                    if (tab == null || string.IsNullOrWhiteSpace(tab.Url))
                    {
                        continue;
                    }
                    if (string.Equals(tab.Url.Trim(), url.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        matchIndex = index;
                        break;
                    }
                }
                if (matchIndex < 0)
                {
                    return false;
                }

                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                // 同上：只认真标签栏，别把网页里的 ARIA 标签按序号算进去。
                List<AutomationElement> tabs2 = SessionCaptureService.FindBrowserTabs(root);
                if (tabs2 == null || matchIndex >= tabs2.Count)
                {
                    return false;
                }
                return SessionCaptureService.TrySelectBrowserTab(tabs2[matchIndex]);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySelectAutomationTab(AutomationElement tab)
        {
            try
            {
                object pattern;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    SelectionItemPattern selection = pattern as SelectionItemPattern;
                    if (selection != null)
                    {
                        try { selection.Select(); return true; }
                        catch { }
                    }
                }
                if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    InvokePattern invoke = pattern as InvokePattern;
                    if (invoke != null)
                    {
                        try { invoke.Invoke(); return true; }
                        catch { }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

    }
}
