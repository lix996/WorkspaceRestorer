using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed partial class SessionCaptureService
    {
        private static bool CdpListMatchesWindow(List<BrowserDebugProtocol.BrowserTabEntry> cdpTabs, string windowTitle)
        {
            if (cdpTabs == null
                || cdpTabs.Count == 0
                || string.IsNullOrWhiteSpace(windowTitle))
            {
                return false;
            }

            // The window title is the active tab's title plus the browser name,
            // so accepting the CDP list when any page target title is a
            // substring of the window title proves this window is in the list.
            for (int index = 0; index < cdpTabs.Count; index++)
            {
                BrowserDebugProtocol.BrowserTabEntry entry = cdpTabs[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Title))
                {
                    continue;
                }
                if (windowTitle.IndexOf(entry.Title.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsPageUrl(List<BrowserPageCapture> pages, string url)
        {
            if (pages == null || string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            for (int index = 0; index < pages.Count; index++)
            {
                BrowserPageCapture page = pages[index];
                if (page == null)
                {
                    continue;
                }
                string existing = string.IsNullOrWhiteSpace(page.Url) ? null : page.Url.Trim();
                if (!string.IsNullOrWhiteSpace(existing)
                    && string.Equals(existing, url.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryCaptureBrowserPages(
            IntPtr windowHandle,
            string fallbackTitle,
            out List<BrowserPageCapture> pages,
            out int expectedTabCount,
            bool allowKeyboardFallback,
            out int knownUiTabCount,
            out int ignoredBlankTabCount,
            out int unreadTabCount,
            out bool onlyBlankTabs)
        {
            pages = new List<BrowserPageCapture>();
            expectedTabCount = 0;
            knownUiTabCount = 0;
            ignoredBlankTabCount = 0;
            unreadTabCount = 0;
            onlyBlankTabs = false;
            // Test and explicit external-effect guard modes must remain
            // observational.  UI Automation Selection/Invoke can change the
            // user's visible browser tab even without sending keystrokes, so
            // skip the multi-tab activation pass entirely in those modes.
            if (windowHandle == IntPtr.Zero || ExternalEffectsDisabled())
            {
                return false;
            }

            AutomationElement root = null;
            int selectedIndex = -1;
            bool tabActivationAvailable = false;
            int tabCount = 0;
            try
            {
                root = AutomationElement.FromHandle(windowHandle);
                if (root != null)
                {
                    // Restrict the collection to the browser tab strip. A
                    // full-tree TabItem query also sees ARIA tabs inside the
                    // current webpage, which corrupts both the count and the
                    // saved left-to-right order.
                    List<AutomationElement> browserTabs = FindBrowserTabs(root);
                    tabCount = browserTabs == null ? 0 : Math.Min(browserTabs.Count, 1024);

                    for (int index = 0; index < tabCount; index++)
                    {
                        AutomationElement tab = FindBrowserTabAt(
                            AutomationElement.FromHandle(windowHandle),
                            index);
                        bool isSelected;
                        bool supported = TryReadBrowserTabSelection(tab, out isSelected);
                        if (supported)
                        {
                            tabActivationAvailable = true;
                            if (isSelected)
                            {
                                selectedIndex = index;
                                break;
                            }
                            // Some Edge providers expose SelectionItemPattern
                            // but fail to report IsSelected on the first
                            // automation snapshot. Use the window title as a
                            // conservative fallback so the capture can still
                            // restore the original tab after reading the other
                            // tabs.
                            if (selectedIndex < 0)
                            {
                                string tabName = TryReadAutomationProperty(
                                    tab,
                                    AutomationElement.NameProperty);
                                if (IsProbableActiveBrowserTab(tabName, fallbackTitle))
                                {
                                    selectedIndex = index;
                                }
                            }
                        }
                        else if (CanActivateBrowserTab(tab))
                        {
                            // Current Edge builds may expose InvokePattern but
                            // no SelectionItemPattern. Match the active tab
                            // title to the top-level window title so it can be
                            // restored.
                            tabActivationAvailable = true;
                            string tabName = TryReadAutomationProperty(
                                tab,
                                AutomationElement.NameProperty);
                            if (selectedIndex < 0
                                && IsProbableActiveBrowserTab(tabName, fallbackTitle))
                            {
                                selectedIndex = index;
                            }
                        }
                    }
                }

                // Never activate tabs through UI Automation unless the
                // provider offers a supported pattern and the current tab can
                // be identified; otherwise it would be impossible to restore
                // visible state. The keyboard fallback below starts at the
                // browser's current tab and has its own restoration guard.
                if (tabActivationAvailable && selectedIndex >= 0)
                {
                    for (int index = 0; index < tabCount; index++)
                    {
                        try
                        {
                            // Chromium can invalidate the original collection
                            // when a tab is selected. Re-query both the root
                            // and the tab before every operation so one stale
                            // provider element cannot abort the rest.
                            AutomationElement currentRoot = AutomationElement.FromHandle(windowHandle);
                            AutomationElement tab = FindBrowserTabAt(currentRoot, index);
                            if (tab == null)
                            {
                                continue;
                            }

                            bool isCurrentlySelected;
                            bool selectionKnown = TryReadBrowserTabSelection(tab, out isCurrentlySelected);
                            if (!selectionKnown || !isCurrentlySelected)
                            {
                                string expectedTabTitle = TryReadAutomationProperty(
                                    tab,
                                    AutomationElement.NameProperty);
                                if (!TrySelectBrowserTab(tab))
                                {
                                    continue;
                                }

                                // Edge updates the address bar asynchronously.
                                // A successful Select()/Invoke() only means the
                                // request was accepted; wait until the fresh
                                // accessibility element reports the selection
                                // when that pattern is available before reading
                                // its URL.
                                if (!WaitForBrowserTabReady(
                                    windowHandle,
                                    index,
                                    expectedTabTitle,
                                    900))
                                {
                                    // Do not read the address bar while it may
                                    // still contain the previous tab's URL.
                                    // The keyboard fallback below can retry the
                                    // tab from a clean active-browser state.
                                    continue;
                                }
                            }

                            string pageUrl;
                            string pageTitle;
                            if (!TryCaptureBrowserPageWithRetry(
                                windowHandle,
                                fallbackTitle,
                                out pageUrl,
                                out pageTitle))
                            {
                                continue;
                            }

                            AutomationElement titleRoot = AutomationElement.FromHandle(windowHandle);
                            AutomationElement titleTab = FindBrowserTabAt(titleRoot, index);
                            string tabTitle = TryReadAutomationProperty(
                                titleTab ?? tab,
                                AutomationElement.NameProperty);
                            if (!string.IsNullOrWhiteSpace(tabTitle))
                            {
                                pageTitle = tabTitle.Trim();
                            }

                            AddOrReplaceBrowserPage(pages, new BrowserPageCapture
                            {
                                Url = pageUrl,
                                Title = string.IsNullOrWhiteSpace(pageTitle) ? fallbackTitle : pageTitle,
                                TabIndex = index,
                                IsActive = index == selectedIndex,
                                CaptureSource = "BrowserAddressBar+TabAutomation"
                            });
                        }
                        catch
                        {
                            // A tab may close or navigate while it is
                            // inspected; keep the pages already captured and
                            // continue.
                        }
                    }
                }
            }
            catch
            {
                // UI Automation providers can invalidate elements while a
                // tab navigates.  Preserve pages already captured this pass.
            }
            finally
            {
                if (tabActivationAvailable && selectedIndex >= 0)
                {
                    try
                    {
                        AutomationElement restoreRoot = AutomationElement.FromHandle(windowHandle);
                        AutomationElement restoreTab = FindBrowserTabAt(restoreRoot, selectedIndex);
                        if (restoreTab == null && root != null)
                        {
                            restoreTab = FindBrowserTabAt(root, selectedIndex);
                        }
                        TrySelectBrowserTab(restoreTab);
                    }
                    catch
                    {
                    }
                }
            }

            // 空白新标签页（新建标签页 / edge://newtab）不算“没读到的网页”：
            // 先把空白网址从 pages 里去掉；没读到网址、但标签名是空白新标签页的
            // 序号记为“忽略”。真实网页（包括标题是“无标题”的）仍按未读处理，
            // 不能为了消警告把真页面藏掉。
            RemoveBlankNewTabPages(pages);

            List<string> tabNames = new List<string>();
            try
            {
                for (int index = 0; index < tabCount; index++)
                {
                    AutomationElement nameTab = FindBrowserTabAt(
                        AutomationElement.FromHandle(windowHandle),
                        index);
                    tabNames.Add(TryReadAutomationProperty(nameTab, AutomationElement.NameProperty));
                }
            }
            catch
            {
                // 读标签名失败时按“都不是空白”处理：宁可保留未读提示，
                // 也不能把真实网页误当成空白新标签页忽略。
                tabNames.Clear();
            }
            List<int> readIndexes = new List<int>();
            for (int index = 0; index < pages.Count; index++)
            {
                if (pages[index] != null && pages[index].TabIndex >= 0)
                {
                    readIndexes.Add(pages[index].TabIndex);
                }
            }
            ignoredBlankTabCount = BrowserCaptureCounts.CountIgnoredBlankTabs(
                tabNames,
                readIndexes);

            int titleTabCount = ParseBrowserTabCountFromTitle(fallbackTitle);
            // Trust the live UI Automation tab list whenever it reports at
            // least one tab: the localized window-title phrase (for example
            // "和另外 2 个页面") can be stale or refer to the other window and
            // previously made a one-tab window look like three.  Only fall
            // back to the title when Edge exposes no tabs at all.
            int meaningfulTabCount = tabCount > 0
                ? Math.Max(0, tabCount - ignoredBlankTabCount)
                : Math.Max(0, titleTabCount);
            expectedTabCount = meaningfulTabCount;
            knownUiTabCount = tabCount > 0 ? meaningfulTabCount : 0;
            onlyBlankTabs = tabCount > 0
                && ignoredBlankTabCount >= tabCount
                && pages.Count == 0;
            unreadTabCount = Math.Max(0, expectedTabCount - pages.Count);
            if (expectedTabCount > pages.Count)
            {
                // A number of Edge releases expose tab names but reject both
                // SelectionItemPattern.Select and InvokePattern. Only the
                // explicit save path reaches this method, so use a bounded
                // keyboard pass as a compatibility fallback. It copies the
                // address bar value for each tab, then cycles back to the tab
                // that was active when capture began.  A minimized window that
                // was restored for this read never takes the keyboard route:
                // sending keystrokes would require stealing the user's
                // foreground window.
                if (allowKeyboardFallback)
                {
                    TryCaptureBrowserPagesWithKeyboard(
                        windowHandle,
                        fallbackTitle,
                        expectedTabCount,
                        selectedIndex,
                        pages);
                }
            }

            return pages.Count > 0;
        }

        /// <summary>
        /// 把空白新标签页 URL（about:blank / edge://newtab / chrome://newtab）
        /// 从采集结果里清掉。返回清掉的条数，只用于诊断。
        /// 同一个类里 BrowserBranch 的地址栏兜底之后也要再调一次，否则
        /// about:blank 会被兜底重新加回来，只有空白标签的窗口就清不掉了。
        /// </summary>
        private static int RemoveBlankNewTabPages(IList<BrowserPageCapture> pages)
        {
            if (pages == null)
            {
                return 0;
            }
            int removed = 0;
            for (int index = pages.Count - 1; index >= 0; index--)
            {
                BrowserPageCapture page = pages[index];
                if (page != null && BrowserTitleProfile.IsBlankNewTabUrl(page.Url))
                {
                    pages.RemoveAt(index);
                    removed++;
                }
            }
            return removed;
        }

        private static void AddOrReplaceBrowserPage(
            IList<BrowserPageCapture> pages,
            BrowserPageCapture candidate)
        {
            if (pages == null || candidate == null || string.IsNullOrWhiteSpace(candidate.Url))
            {
                return;
            }

            for (int index = 0; index < pages.Count; index++)
            {
                BrowserPageCapture existing = pages[index];
                if (existing != null && existing.TabIndex == candidate.TabIndex)
                {
                    // A keyboard result is used only when the direct pass
                    // produced fewer tabs than expected. It is then the more
                    // reliable value for every index because a failed UIA
                    // selection can otherwise leave the previous tab's URL
                    // looking valid.
                    if (string.IsNullOrWhiteSpace(existing.Url)
                        || string.Equals(candidate.CaptureSource, "BrowserKeyboard", StringComparison.OrdinalIgnoreCase))
                    {
                        pages[index] = candidate;
                    }
                    return;
                }
            }

            pages.Add(candidate);
        }

        // 保留这个命名入口，测试和旧调用方仍可反射到它；
        // 真正实现已归到 Browsers/Capture/BrowserTitleTabCount。
        private static int ParseBrowserTabCountFromTitle(string windowTitle)
        {
            return BrowserTitleTabCount.Parse(windowTitle);
        }

        private static bool TryCaptureBrowserPagesWithKeyboard(
            IntPtr windowHandle,
            string fallbackTitle,
            int expectedTabCount,
            int initialTabIndex,
            IList<BrowserPageCapture> pages)
        {
            if (windowHandle == IntPtr.Zero
                || pages == null
                || expectedTabCount <= 1
                || ExternalEffectsDisabled())
            {
                return false;
            }

            // Do not send keystrokes unless this process actually owns the
            // foreground browser window. SetForegroundWindow can be denied by
            // Windows' foreground lock; in that case abort rather than risk
            // sending Ctrl+L/Ctrl+Tab to the keeper's own UI.
            IntPtr originalForeground = NativeMethods.GetForegroundWindow();
            if (!FocusBrowserWindow(windowHandle))
            {
                return false;
            }

            bool capturedAny = false;
            try
            {
                Thread.Sleep(120);
                int firstTabIndex = initialTabIndex >= 0 && initialTabIndex < expectedTabCount
                    ? initialTabIndex
                    : 0;
                for (int offset = 0; offset < expectedTabCount; offset++)
                {
                    int tabIndex = (firstTabIndex + offset) % expectedTabCount;
                    string pageUrl;
                    if (TryReadActiveBrowserUrlWithKeyboard(
                        windowHandle,
                        fallbackTitle,
                        out pageUrl))
                    {
                        AddOrReplaceBrowserPage(pages, new BrowserPageCapture
                        {
                            Url = pageUrl,
                            Title = ReadActiveBrowserTabTitle(windowHandle, fallbackTitle),
                            TabIndex = tabIndex,
                            IsActive = offset == 0,
                            CaptureSource = "BrowserKeyboard"
                        });
                        capturedAny = true;
                    }

                    // Advance after every item, including the final one.  N
                    // advances across N tabs returns Edge to the tab that was
                    // active when this pass started.
                    if (!SendBrowserKeys("^{TAB}"))
                    {
                        break;
                    }
                    Thread.Sleep(offset + 1 < expectedTabCount ? 150 : 80);
                }
            }
            catch
            {
            }
            finally
            {
                if (originalForeground != IntPtr.Zero
                    && NativeMethods.IsWindow(originalForeground)
                    && originalForeground != windowHandle)
                {
                    FocusBrowserWindow(originalForeground);
                }
            }

            return capturedAny;
        }

        private static bool FocusBrowserWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }

            uint targetThreadId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out targetThreadId);
            uint currentThreadId = NativeMethods.GetCurrentThreadId();
            bool attached = targetThreadId != 0
                && currentThreadId != 0
                && targetThreadId != currentThreadId
                && NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
            try
            {
                NativeMethods.SetForegroundWindow(windowHandle);
                Thread.Sleep(80);
                return NativeMethods.GetForegroundWindow() == windowHandle;
            }
            finally
            {
                if (attached)
                {
                    try
                    {
                        NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool SendBrowserKeys(string keys)
        {
            if (string.IsNullOrWhiteSpace(keys))
            {
                return false;
            }

            try
            {
                ushort virtualKey;
                bool withControl;
                if (string.Equals(keys, "^{TAB}", StringComparison.Ordinal))
                {
                    virtualKey = NativeMethods.VirtualKeyTab;
                    withControl = true;
                }
                else if (string.Equals(keys, "^l", StringComparison.Ordinal))
                {
                    virtualKey = NativeMethods.VirtualKeyL;
                    withControl = true;
                }
                else if (string.Equals(keys, "{ESC}", StringComparison.Ordinal))
                {
                    virtualKey = NativeMethods.VirtualKeyEscape;
                    withControl = false;
                }
                else
                {
                    return false;
                }

                List<NativeMethods.NativeInput> inputs = new List<NativeMethods.NativeInput>();
                if (withControl)
                {
                    inputs.Add(NativeMethods.NativeInput.Keyboard(NativeMethods.VirtualKeyControl, false));
                }
                inputs.Add(NativeMethods.NativeInput.Keyboard(virtualKey, false));
                inputs.Add(NativeMethods.NativeInput.Keyboard(virtualKey, true));
                if (withControl)
                {
                    inputs.Add(NativeMethods.NativeInput.Keyboard(NativeMethods.VirtualKeyControl, true));
                }

                NativeMethods.NativeInput[] inputArray = inputs.ToArray();
                uint sent = NativeMethods.SendInput(
                    (uint)inputArray.Length,
                    inputArray,
                    Marshal.SizeOf(typeof(NativeMethods.NativeInput)));
                return sent == (uint)inputArray.Length;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadActiveBrowserUrlWithKeyboard(
            IntPtr windowHandle,
            string fallbackTitle,
            out string pageUrl)
        {
            pageUrl = null;
            if (!SendBrowserKeys("^l"))
            {
                return false;
            }

            try
            {
                Thread.Sleep(70);
                string pageTitle;
                return TryCaptureBrowserPageWithRetry(
                    windowHandle,
                    fallbackTitle,
                    out pageUrl,
                    out pageTitle);
            }
            finally
            {
                // Escape leaves the page untouched and returns keyboard focus
                // to the browser content before Ctrl+Tab advances the next tab.
                SendBrowserKeys("{ESC}");
            }
        }

        private static string ReadActiveBrowserTabTitle(IntPtr windowHandle, string fallbackTitle)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                List<AutomationElement> tabs = root == null ? null : FindBrowserTabs(root);
                if (tabs != null)
                {
                    for (int index = 0; index < Math.Min(tabs.Count, 1024); index++)
                    {
                        bool selected;
                        if (TryReadBrowserTabSelection(tabs[index], out selected) && selected)
                        {
                            string name = TryReadAutomationProperty(tabs[index], AutomationElement.NameProperty);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                return name.Trim();
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            string title = ReadNativeWindowTitle(windowHandle);
            return string.IsNullOrWhiteSpace(title) ? fallbackTitle : title;
        }

        private static string ReadNativeWindowTitle(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return null;
                }
                StringBuilder text = new StringBuilder(length + 1);
                return NativeMethods.GetWindowText(windowHandle, text, text.Capacity) > 0
                    ? text.ToString().Trim()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        internal static string TryReadAutomationProperty(AutomationElement element, AutomationProperty property)
        {
            return BrowserTabFinder.TryReadAutomationProperty(element, property);
        }

        internal static bool CanActivateBrowserTab(AutomationElement tab)
        {
            return BrowserTabFinder.CanActivateBrowserTab(tab);
        }

        internal static bool IsProbableActiveBrowserTab(string tabName, string windowTitle)
        {
            return BrowserTabFinder.IsProbableActiveBrowserTab(tabName, windowTitle);
        }

        internal static bool TryReadBrowserTabSelection(AutomationElement tab, out bool isSelected)
        {
            return BrowserTabFinder.TryReadBrowserTabSelection(tab, out isSelected);
        }

        internal static bool TrySelectBrowserTab(AutomationElement tab)
        {
            return BrowserTabFinder.TrySelectBrowserTab(tab);
        }

        internal static List<AutomationElement> FindBrowserTabs(AutomationElement root)
        {
            return BrowserTabFinder.FindBrowserTabs(root);
        }

        internal static AutomationElement FindBrowserTabAt(AutomationElement root, int index)
        {
            return BrowserTabFinder.FindBrowserTabAt(root, index);
        }

        /// <summary>
        /// Reads the active browser address bar through UI Automation.  This
        /// is retained as the compatibility path when a browser does not
        /// expose selectable TabItem controls.
        /// </summary>
        private static bool TryCaptureBrowserPage(
            IntPtr windowHandle,
            string fallbackTitle,
            out string pageUrl,
            out string pageTitle)
        {
            pageUrl = null;
            pageTitle = fallbackTitle;
            if (windowHandle == IntPtr.Zero)
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

                try
                {
                    string rootName = root.Current.Name;
                    if (!string.IsNullOrWhiteSpace(rootName))
                    {
                        pageTitle = rootName.Trim();
                    }
                }
                catch
                {
                }

                Condition addressCandidates = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                AutomationElementCollection descendants = root.FindAll(TreeScope.Descendants, addressCandidates);
                string fallbackUrl = null;
                int inspected = 0;
                for (int index = 0; index < descendants.Count && inspected < 512; index++, inspected++)
                {
                    AutomationElement element = descendants[index];
                    if (element == null)
                    {
                        continue;
                    }

                    ControlType controlType;
                    try
                    {
                        controlType = element.Current.ControlType;
                    }
                    catch
                    {
                        continue;
                    }
                    if (controlType != ControlType.Edit
                        && controlType != ControlType.ComboBox
                        && controlType != ControlType.Document)
                    {
                        continue;
                    }

                    string name = TryReadAutomationProperty(element, AutomationElement.NameProperty);
                    string automationId = TryReadAutomationProperty(element, AutomationElement.AutomationIdProperty);
                    string className = TryReadAutomationProperty(element, AutomationElement.ClassNameProperty);
                    string value = TryReadAutomationValue(element);
                    if (!IsBrowserUrl(value))
                    {
                        continue;
                    }

                    string normalized = NormalizeBrowserUrl(value);
                    if (LooksLikeAddressBar(name, automationId, className))
                    {
                        pageUrl = normalized;
                        return true;
                    }

                    // Only Edit/ComboBox controls are safe fallbacks.  A
                    // browser Document element can contain arbitrary page
                    // text and must never be mistaken for the address bar.
                    if (fallbackUrl == null
                        && (controlType == ControlType.Edit || controlType == ControlType.ComboBox))
                    {
                        fallbackUrl = normalized;
                    }
                }

                if (fallbackUrl != null)
                {
                    pageUrl = fallbackUrl;
                    return true;
                }
            }
            catch
            {
                // UI Automation can fail for elevated windows, sign-out,
                // browser restarts, or providers that do not implement the
                // address bar pattern.  Browser capture is best effort.
            }

            return false;
        }

        /// <summary>
        /// Returns the real browser tab strip in left-to-right order.
        /// Chromium exposes page-level ARIA tab controls in the same UIA tree;
        /// a plain Descendants/TabItem query therefore cannot be used for
        /// capture.  Prefer direct TabItem children of a ControlType.Tab
        /// container. If a provider omits the container, only TabItem children
        /// directly under the browser window are considered; page descendants
        /// are intentionally ignored.
        /// </summary>
        private static bool WaitForBrowserTabReady(
            IntPtr windowHandle,
            int tabIndex,
            string expectedTabTitle,
            int timeoutMilliseconds)
        {
            int elapsed = 0;
            while (elapsed < timeoutMilliseconds)
            {
                try
                {
                    AutomationElement root = AutomationElement.FromHandle(windowHandle);
                    AutomationElement tab = FindBrowserTabAt(root, tabIndex);
                    bool selected;
                    if (TryReadBrowserTabSelection(tab, out selected) && selected)
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedTabTitle))
                    {
                        string currentTitle = ReadNativeWindowTitle(windowHandle);
                        if (IsProbableActiveBrowserTab(expectedTabTitle, currentTitle))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(50);
                elapsed += 50;
            }

            // Providers that expose neither a reliable selected state nor a
            // native title update are still given a short settling interval,
            // but the caller will not trust the address bar indefinitely.
            Thread.Sleep(100);
            return false;
        }

        private static bool TryCaptureBrowserPageWithRetry(
            IntPtr windowHandle,
            string fallbackTitle,
            out string pageUrl,
            out string pageTitle)
        {
            pageUrl = null;
            pageTitle = fallbackTitle;
            // Address-bar providers update asynchronously after a tab switch;
            // allow the first accessibility refresh to settle, then retry
            // briefly instead of recording the URL from the prior tab.
            Thread.Sleep(120);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (TryCaptureBrowserPage(windowHandle, fallbackTitle, out pageUrl, out pageTitle))
                {
                    return true;
                }

                if (attempt < 3)
                {
                    Thread.Sleep(90);
                }
            }

            return false;
        }

        private static string TryReadAutomationValue(AutomationElement element)
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

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    TextPattern textPattern = pattern as TextPattern;
                    if (textPattern != null)
                    {
                        return textPattern.DocumentRange.GetText(4096);
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool LooksLikeAddressBar(string name, string automationId, string className)
        {
            string text = ((name ?? string.Empty) + " " + (automationId ?? string.Empty) + " " + (className ?? string.Empty)).ToLowerInvariant();
            return text.Contains("address")
                || text.Contains("omnibox")
                || text.Contains("location")
                || text.Contains("url")
                || text.Contains("addresseditbox")
                || text.Contains("地址")
                || text.Contains("搜索栏")
                || text.Contains("搜索框");
        }

        private static bool IsBrowserUrl(string value)
        {
            // 规则的唯一实现放在 WebAddressTools（公共区）：
            // 保存侧和恢复侧必须用同一份，否则会出现“能存下来却恢复不了”。
            return WebAddressTools.IsWebAddress(value);
        }

        private static string NormalizeBrowserUrl(string value)
        {
            // ValuePattern returns the address-bar text exactly as displayed.
            // Validate with a trimmed copy, but persist the original string so
            // query ordering, casing, fragments and encoded characters are not
            // rewritten during capture.
            return value;
        }

        private static bool PrepareMinimizedBrowserWindowForPageRead(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero
                || !NativeMethods.IsWindow(windowHandle)
                || !NativeMethods.IsIconic(windowHandle))
            {
                return false;
            }

            try
            {
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                NativeMethods.ShowWindow(windowHandle, NativeMethods.SwShowNoActivate);
                Thread.Sleep(250);
                if (NativeMethods.IsIconic(windowHandle))
                {
                    // 没能还原（例如被系统策略挡住），不要再动它。
                    return false;
                }

                // 压到最底层：位置和大小都不动，只改 Z 序、不激活、不改变显示状态。
                // 这样即使用户正在看的窗口没有最大化，也不会被这个还原出来的窗口
                // 盖住；用户根本看不到它出现。
                try
                {
                    NativeMethods.SetWindowPos(
                        windowHandle,
                        NativeMethods.HWND_BOTTOM,
                        0,
                        0,
                        0,
                        0,
                        NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
                    GC.KeepAlive(foreground);
                }
                catch
                {
                }

                lock (RestoredMinimizedWindowsLock)
                {
                    if (!RestoredMinimizedWindows.Contains(windowHandle))
                    {
                        RestoredMinimizedWindows.Add(windowHandle);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ReMinimizeBrowserWindowAfterPageRead(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return;
            }

            lock (RestoredMinimizedWindowsLock)
            {
                RestoredMinimizedWindows.Remove(windowHandle);
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
        /// Safety net that minimizes every browser window this capture pass
        /// restored for page reading.  It is invoked when the isolated worker
        /// finishes (including on exceptions), so a timed-out or failed read
        /// can never leave a restored window sitting on the user's desktop.
        /// </summary>
        internal static void ReMinimizeRestoredBrowserWindows()
        {
            List<IntPtr> windows;
            lock (RestoredMinimizedWindowsLock)
            {
                windows = new List<IntPtr>(RestoredMinimizedWindows);
                RestoredMinimizedWindows.Clear();
            }
            for (int index = 0; index < windows.Count; index++)
            {
                ReMinimizeBrowserWindowAfterPageRead(windows[index]);
            }
        }

    }
}
