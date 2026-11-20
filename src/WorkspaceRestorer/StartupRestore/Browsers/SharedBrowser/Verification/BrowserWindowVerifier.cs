using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Automation;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器最终窗口的纯校验：标题、页数、UIA 真实标签数、缩后存活。
    /// 不再依赖 WindowPlacementService 的私有实现。
    /// </summary>
    internal static class BrowserWindowVerifier
    {
        internal static bool StillPresentAfterMinimize(
            IntPtr windowHandle,
            out string title,
            out int pageCount)
        {
            title = null;
            pageCount = 0;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            title = ReadTitle(windowHandle);
            BrowserTitleProfile.TryReadPageCount(title, out pageCount);
            // 缩后只确认“窗口还在 + 仍最小化”，不能因为标题为空/还没发布
            // 就把已经缩下去的单页窗口判成失败。
            return true;
        }

        internal static bool MatchesExpectation(
            IntPtr windowHandle,
            IList<string> expectedTitles,
            string lastPageTitle,
            int expectedPageCount,
            out string title,
            out int pageCount)
        {
            title = null;
            pageCount = 0;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            title = ReadTitle(windowHandle);
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            bool hasCount = BrowserTitleProfile.TryReadPageCount(title, out pageCount);
            if (expectedPageCount > 0
                && (!hasCount || pageCount != expectedPageCount)
                && !NativeMethods.IsIconic(windowHandle))
            {
                int uiTabCount;
                if (TryReadTabCount(windowHandle, out uiTabCount))
                {
                    pageCount = uiTabCount;
                    hasCount = true;
                }
            }
            bool exactKnownCount = expectedPageCount > 1
                && hasCount
                && pageCount == expectedPageCount;
            if (BrowserTitleProfile.IsBlankWindowTitle(title) && !exactKnownCount)
            {
                return false;
            }

            bool hasTitles = expectedTitles != null && expectedTitles.Count > 0;
            if (hasTitles)
            {
                if (!BrowserTitleProfile.BrowserWindowTitleMatches(title, expectedTitles)
                    && !exactKnownCount)
                {
                    return false;
                }
            }
            else
            {
                string active = BrowserTitleProfile.GetActiveTitle(title);
                if ((string.IsNullOrWhiteSpace(lastPageTitle)
                        || !BrowserTitleProfile.BrowserTitleFragmentsMatch(active, lastPageTitle))
                    && !exactKnownCount)
                {
                    return false;
                }
            }

            if (expectedPageCount > 0
                && hasCount
                && pageCount != expectedPageCount)
            {
                return false;
            }
            return true;
        }

        internal static bool TryReadTabCount(IntPtr windowHandle, out int tabCount)
        {
            List<string> titles;
            return TryReadTabTitles(windowHandle, out titles, out tabCount);
        }

        /// <summary>
        /// 读浏览器标签栏里每个标签的标题。缩前就绪判断用它确认
        /// “标签不只是建出来了，至少已经有一个真实标题”，避免把
        /// “无标题 + 另外 1 个页面”这种还在加载的窗口当成已就绪。
        /// </summary>
        internal static bool TryReadTabTitles(
            IntPtr windowHandle,
            out List<string> titles,
            out int tabCount)
        {
            titles = new List<string>();
            tabCount = 0;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindowVisible(windowHandle))
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
                List<AutomationElement> tabs = BrowserTabFinder.FindBrowserTabs(root);
                if (tabs == null || tabs.Count == 0)
                {
                    return false;
                }
                tabCount = tabs.Count;
                for (int index = 0; index < tabs.Count; index++)
                {
                    string title = null;
                    try
                    {
                        title = tabs[index] == null ? null : tabs[index].Current.Name;
                    }
                    catch
                    {
                    }
                    titles.Add(title ?? string.Empty);
                }
                return true;
            }
            catch
            {
                return false;
            }
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
