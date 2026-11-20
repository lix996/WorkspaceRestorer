using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 浏览器标签顺序归一。
    ///
    /// 规则只有一条：**列表顺序是事实源，BrowserTabIndex 只是派生缓存。**
    /// 原来这段算法藏在 UI 拖动代码里，自启动应用的转接清单看不到；
    /// 现在放在这里，启动与恢复、自启动应用转接都调用同一份。
    /// </summary>
    public static class BrowserTabOrder
    {
        /// <summary>按列表顺序重排所有浏览器窗口里的标签。</summary>
        public static void Normalize(IList<SessionItem> items)
        {
            if (items == null)
            {
                return;
            }
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem parent = items[index];
                if (!IsBrowserParent(parent))
                {
                    continue;
                }
                NormalizeWindowTabs(items, parent);
            }
        }

        /// <summary>只重排某一个浏览器窗口里的标签。</summary>
        public static void NormalizeWindowTabs(IList<SessionItem> items, SessionItem window)
        {
            if (items == null || !IsBrowserParent(window))
            {
                return;
            }
            int tabIndex = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem page = items[index];
                if (!IsPageOf(window, page))
                {
                    continue;
                }
                page.ParentId = window.Id;
                page.BrowserExecutablePath = FirstNonEmpty(
                    window.BrowserExecutablePath, window.ExecutablePath, page.BrowserExecutablePath);
                page.BrowserName = FirstNonEmpty(window.BrowserName, page.BrowserName);
                page.BrowserGroupKey = FirstNonEmpty(window.BrowserGroupKey, page.BrowserGroupKey);
                if (window.BrowserWindowIndex >= 0)
                {
                    page.BrowserWindowIndex = window.BrowserWindowIndex;
                }
                page.BrowserTabIndex = tabIndex;
                tabIndex++;
            }
            if (tabIndex > 0)
            {
                window.StartsBrowserWindow = true;
            }
        }

        private static bool IsBrowserParent(SessionItem item)
        {
            return item != null
                && item.Kind == SessionItemKind.Application
                && (!string.IsNullOrWhiteSpace(item.BrowserGroupKey)
                    || !string.IsNullOrWhiteSpace(item.BrowserExecutablePath));
        }

        private static bool IsPageOf(SessionItem window, SessionItem page)
        {
            if (window == null
                || page == null
                || page.Kind != SessionItemKind.WebPage)
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(page.ParentId)
                && !string.Equals(page.ParentId, window.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(page.BrowserGroupKey)
                && !string.IsNullOrWhiteSpace(window.BrowserGroupKey)
                && !string.Equals(page.BrowserGroupKey, window.BrowserGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (page.BrowserWindowIndex >= 0
                && window.BrowserWindowIndex >= 0
                && page.BrowserWindowIndex != window.BrowserWindowIndex)
            {
                return false;
            }
            return true;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }
            for (int index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(values[index]))
                {
                    return values[index];
                }
            }
            return null;
        }
    }
}
