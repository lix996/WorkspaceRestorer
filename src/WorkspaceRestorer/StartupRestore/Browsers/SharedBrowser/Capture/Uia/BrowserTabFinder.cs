using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器真正的标签栏 UIA 操作：只认 ControlType.Tab 下的 TabItem，
    /// 排除网页自己的 ARIA 标签；保存、跳转、最终校验共用这一份实现。
    /// </summary>
    internal static class BrowserTabFinder
    {
        internal static bool CanActivateBrowserTab(AutomationElement tab)
        {
            if (tab == null)
            {
                return false;
            }

            try
            {
                object pattern;
                return tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)
                    || tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsProbableActiveBrowserTab(string tabName, string windowTitle)
        {
            if (string.IsNullOrWhiteSpace(tabName) || string.IsNullOrWhiteSpace(windowTitle))
            {
                return false;
            }

            string tab = tabName.Trim();
            string title = windowTitle.Trim();
            return string.Equals(tab, title, StringComparison.CurrentCultureIgnoreCase)
                || title.StartsWith(tab + " ", StringComparison.CurrentCultureIgnoreCase)
                || title.StartsWith(tab + " -", StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>读一个标签的选中状态。</summary>
        internal static bool TryReadBrowserTabSelection(AutomationElement tab, out bool isSelected)
        {
            isSelected = false;
            if (tab == null)
            {
                return false;
            }

            try
            {
                object pattern;
                if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    return false;
                }

                SelectionItemPattern selection = pattern as SelectionItemPattern;
                if (selection == null)
                {
                    return false;
                }

                isSelected = selection.Current.IsSelected;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>选中一个浏览器标签（保存时切标签读网址、跳转时也用它）。</summary>
        internal static bool TrySelectBrowserTab(AutomationElement tab)
        {
            if (tab == null)
            {
                return false;
            }

            try
            {
                object pattern;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    SelectionItemPattern selection = pattern as SelectionItemPattern;
                    if (selection != null)
                    {
                        try
                        {
                            selection.Select();
                            return true;
                        }
                        catch
                        {
                            // Some Chromium providers expose the pattern but
                            // reject Select; continue with their Invoke path.
                        }
                    }
                }

                // Edge versions that do not expose SelectionItemPattern may
                // still expose InvokePattern on each tab. Invoking the tab is
                // the supported activation path for those providers.
                if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                {
                    InvokePattern invoke = pattern as InvokePattern;
                    if (invoke != null)
                    {
                        try
                        {
                            invoke.Invoke();
                            return true;
                        }
                        catch
                        {
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 返回真正的浏览器标签栏（从左到右）。
        ///
        /// 用户 2026-10-15 反馈“点击跳转地址跳不到对应网址”时统一到这一处：
        /// 「启动与恢复」里点条目跳转原来自己写了一份
        /// `Descendants + TabItem` 的查询，那会**把网页里的 ARIA 标签也算进来**
        /// （实测同一个 Edge 窗口：真标签 4 个、查询结果 6 个），
        /// 于是按名字命中的可能是网页里的假标签，跳转就落错地方。
        /// 现在保存和跳转**共用这一个方法**，不再各写一份。
        /// </summary>
        internal static List<AutomationElement> FindBrowserTabs(AutomationElement root)
        {
            List<AutomationElement> best = null;
            int bestScore = int.MinValue;
            if (root == null)
            {
                return best;
            }

            try
            {
                AutomationElementCollection strips = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
                if (strips != null)
                {
                    for (int stripIndex = 0; stripIndex < strips.Count; stripIndex++)
                    {
                        AutomationElement strip = strips[stripIndex];
                        AutomationElementCollection children = strip.FindAll(
                            TreeScope.Children,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                        if (children == null || children.Count == 0)
                        {
                            continue;
                        }

                        List<AutomationElement> candidate = new List<AutomationElement>();
                        for (int childIndex = 0; childIndex < Math.Min(children.Count, 1024); childIndex++)
                        {
                            AutomationElement tab = children[childIndex];
                            if (tab == null)
                            {
                                continue;
                            }

                            // 只认像浏览器标签的项。Chromium 的浏览器标签类名带产品
                            // 名（EdgeTab / ChromeTab…），网页 ARIA 标签的类名是打包
                            // 器生成的哈希名（实测 wSkVaW_tab），一个都不含。
                            string tabClassName = TryReadAutomationProperty(
                                tab,
                                AutomationElement.ClassNameProperty);
                            if (IsPageAriaTab(tabClassName))
                            {
                                continue;
                            }

                            candidate.Add(tab);
                        }

                        int score = candidate.Count;
                        string descriptor = ((TryReadAutomationProperty(strip, AutomationElement.NameProperty) ?? string.Empty)
                            + " " + (TryReadAutomationProperty(strip, AutomationElement.AutomationIdProperty) ?? string.Empty)
                            + " " + (TryReadAutomationProperty(strip, AutomationElement.ClassNameProperty) ?? string.Empty))
                            .ToLowerInvariant();
                        if (descriptor.Contains("tabstrip") || descriptor.Contains("tabbar")
                            || descriptor.Contains("tabcontrol") || descriptor.Contains("tabview")
                            || descriptor.Contains("browser"))
                        {
                            score += 1000;
                        }
                        if (descriptor.Contains("tablist"))
                        {
                            score -= 500;
                        }

                        // 用户 2026-10-15 反馈“网页没有全部保存”。根因就在这里：
                        // 网页自己的 ARIA 标签也是 ControlType.Tab + TabItem，
                        // 于是“子项最多的容器胜出”这条规则会选中网页那一组，
                        // 真正的浏览器标签栏被丢掉。
                        //
                        // 实测（本机 Edge）：
                        //   浏览器标签栏 class='EdgeTabContainerImpl'，标签 class='EdgeTab'
                        //   网页 ARIA 标签   class='wSkVaW_tabs'，标签 class='wSkVaW_tab'
                        // 浏览器标签容器的类名里带 edge/chrome/firefox/brave 等产品名，
                        // 网页的类名是打包器生成的哈希名，永远不含这些词。
                        if (LooksLikeBrowserTabStripContainer(descriptor))
                        {
                            score += 4000;
                        }
                        if (LooksLikePageAriaTabStrip(descriptor))
                        {
                            score -= 4000;
                        }

                        if (best == null || score > bestScore)
                        {
                            best = candidate;
                            bestScore = score;
                        }
                    }
                }

                if (best != null && best.Count > 0)
                {
                    return OrderBrowserTabs(best);
                }

                // A few Chromium accessibility providers expose the tab items
                // directly under the window without a Tab container. Prefer
                // this shallow query before considering the whole tree.
                AutomationElementCollection direct = root.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                if (direct != null && direct.Count > 0)
                {
                    List<AutomationElement> directTabs = new List<AutomationElement>();
                    for (int index = 0; index < Math.Min(direct.Count, 1024); index++)
                    {
                        if (direct[index] != null)
                        {
                            directTabs.Add(direct[index]);
                        }
                    }
                    return OrderBrowserTabs(directTabs);
                }

                // Some Edge/Chromium builds publish the tab items without a
                // ControlType.Tab container and deeper than the window root
                // (for example under a custom tab-strip host).  If nothing was
                // found above, fall back to a full-tree TabItem scan so pages
                // are still captured instead of silently saving zero tabs.
                AutomationElementCollection anyTabs = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
                if (anyTabs != null && anyTabs.Count > 0)
                {
                    List<AutomationElement> fallback = new List<AutomationElement>();
                    for (int index = 0; index < Math.Min(anyTabs.Count, 1024); index++)
                    {
                        if (anyTabs[index] != null)
                        {
                            fallback.Add(anyTabs[index]);
                        }
                    }
                    if (fallback.Count > 0)
                    {
                        return OrderBrowserTabs(fallback);
                    }
                }
            }
            catch
            {
            }

            return best;
        }

        /// <summary>
        /// 判断一个 ControlType.Tab 容器的类名/名字是否就是浏览器的标签栏。
        /// 浏览器（Chromium 系、Firefox）的标签栏容器类名里带自己的产品名。
        /// </summary>
        private static bool LooksLikeBrowserTabStripContainer(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
            {
                return false;
            }

            string[] markers = new[]
            {
                "edgetab", "chrometab", "firefoxtab", "bralvetab", "operatab",
                "vivalditab", "tabstrip", "tabbar", "tabcontainer", "tabview"
            };
            for (int index = 0; index < markers.Length; index++)
            {
                if (descriptor.IndexOf(markers[index], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 判断一个 ControlType.Tab 容器是否是网页内容自己的 ARIA 标签组。
        /// 网页（React/Vue 打包产物）的类名是构建时生成的短哈希名，例如
        /// "wSkVaW_tabs"；它们永远不含浏览器产品名，所以用“不含产品名”来反选。
        /// </summary>
        private static bool LooksLikePageAriaTabStrip(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor))
            {
                return false;
            }
            return !LooksLikeBrowserTabStripContainer(descriptor);
        }

        /// <summary>
        /// 判断一个 TabItem 是否是网页里的 ARIA 标签。浏览器标签的类名带产品名
        /// （EdgeTab、ChromeTab…）；网页标签的类名是打包器生成的哈希名。
        /// 拿不到类名时返回 false，宁可保留也不误删真实浏览器标签。
        /// </summary>
        private static bool IsPageAriaTab(string tabClassName)
        {
            if (string.IsNullOrWhiteSpace(tabClassName))
            {
                return false;
            }

            string className = tabClassName.Trim();
            if (className.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // 完全不像标签控件的类名；交给上层容器评分去判断。
                return false;
            }

            string[] markers = new[]
            {
                "edgetab", "chrometab", "firefoxtab", "bralvetab", "operatab",
                "vivalditab", "browsertab", "tabitem"
            };
            for (int index = 0; index < markers.Length; index++)
            {
                if (className.IndexOf(markers[index], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static List<AutomationElement> OrderBrowserTabs(IList<AutomationElement> tabs)
        {
            if (tabs == null)
            {
                return null;
            }

            // UIA generally exposes children in visual order, but sorting by
            // the tab bounds makes that contract explicit and fixes providers
            // that return the selected tab first after an activation.
            List<BrowserTabPosition> positioned = new List<BrowserTabPosition>();
            for (int index = 0; index < tabs.Count; index++)
            {
                AutomationElement tab = tabs[index];
                double left = double.MaxValue;
                double top = double.MaxValue;
                try
                {
                    System.Windows.Rect bounds = tab.Current.BoundingRectangle;
                    if (!double.IsNaN(bounds.Left) && !double.IsInfinity(bounds.Left))
                    {
                        left = bounds.Left;
                    }
                    if (!double.IsNaN(bounds.Top) && !double.IsInfinity(bounds.Top))
                    {
                        top = bounds.Top;
                    }
                }
                catch
                {
                }
                positioned.Add(new BrowserTabPosition
                {
                    Tab = tab,
                    Left = left,
                    Top = top,
                    Index = index
                });
            }

            return positioned
                .OrderBy(delegate(BrowserTabPosition item) { return item.Top; })
                .ThenBy(delegate(BrowserTabPosition item) { return item.Left; })
                .ThenBy(delegate(BrowserTabPosition item) { return item.Index; })
                .Select(delegate(BrowserTabPosition item) { return item.Tab; })
                .ToList();
        }

        internal static AutomationElement FindBrowserTabAt(AutomationElement root, int index)
        {
            if (root == null || index < 0)
            {
                return null;
            }

            try
            {
                List<AutomationElement> tabs = FindBrowserTabs(root);
                return tabs != null && index < tabs.Count ? tabs[index] : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读一个无障碍元素的属性（拿不到时返回 null，不抛异常）。</summary>
        internal static string TryReadAutomationProperty(AutomationElement element, AutomationProperty property)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(property, true);
                return value == null ? null : Convert.ToString(value);
            }
            catch
            {
                return null;
            }
        }

        private sealed class BrowserTabPosition
        {
            internal AutomationElement Tab { get; set; }

            internal double Left { get; set; }

            internal double Top { get; set; }

            internal int Index { get; set; }
        }
    }
}
