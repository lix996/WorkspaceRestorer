using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 修复“网页属于哪个浏览器窗口 / 是第几个标签”这两个身份字段。
    ///
    /// 背景（用户 2026-10-15 反馈“恢复后两个窗口变成一个”）：
    /// <c>SessionItem.BrowserWindowIndex</c> 原来是
    /// <c>[DataMember(EmitDefaultValue = false)]</c>，而 int 的默认值是 0，
    /// 于是**第一个浏览器窗口的 0 会被静默丢掉**。恢复侧到处用
    /// <c>BrowserWindowIndex &gt;= 0</c> 判断“这个字段有没有”，缺了就按
    /// “没有窗口身份”处理，同一个窗口的标签被拆成两组，恢复成好几个窗口。
    ///
    /// `EmitDefaultValue = true` 只能保证**以后**写对；
    /// **已经存坏的方案文件**靠这里的修复在加载时补回来。
    ///
    /// 修复依据：<c>ParentId</c> 指向所属的浏览器窗口条目，这个字段一直是正常写出的，
    /// 所以它是可靠的“分组依据”。
    /// </summary>
    internal static class BrowserPageGrouping
    {
        /// <summary>就地修好一份方案里的网页分组。返回被改动的条目数（便于记录/测试）。</summary>
        internal static int Repair(IList<SessionItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return 0;
            }

            int changed = 0;

            // 1) 先给每个“浏览器窗口条目”定一个稳定的窗口序号。
            //    已经有合法序号（>= 0）的沿用，缺的按出现顺序补一个没被占用的。
            Dictionary<string, int> windowIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            HashSet<int> usedIndexes = new HashSet<int>();
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (!IsBrowserWindowItem(item))
                {
                    continue;
                }

                if (item.BrowserWindowIndex >= 0)
                {
                    usedIndexes.Add(item.BrowserWindowIndex);
                }
            }

            int nextFreeIndex = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (!IsBrowserWindowItem(item) || string.IsNullOrWhiteSpace(item.Id))
                {
                    continue;
                }

                int windowIndex = item.BrowserWindowIndex;
                if (windowIndex < 0)
                {
                    while (usedIndexes.Contains(nextFreeIndex))
                    {
                        nextFreeIndex++;
                    }
                    windowIndex = nextFreeIndex;
                    usedIndexes.Add(windowIndex);
                    item.BrowserWindowIndex = windowIndex;
                    changed++;
                }
                windowIndexById[item.Id.Trim()] = windowIndex;
            }

            // 2) 每个网页：按 ParentId 把窗口序号对齐到父条目。
            //    父条目找不到时，保留原有合法序号（可能来自更旧的快照）。
            Dictionary<string, SessionItem> byId = new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item != null && !string.IsNullOrWhiteSpace(item.Id) && !byId.ContainsKey(item.Id.Trim()))
                {
                    byId[item.Id.Trim()] = item;
                }
            }

            for (int index = 0; index < items.Count; index++)
            {
                SessionItem page = items[index];
                if (page == null || page.Kind != SessionItemKind.WebPage)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(page.ParentId))
                {
                    continue;
                }

                int parentWindowIndex;
                if (!windowIndexById.TryGetValue(page.ParentId.Trim(), out parentWindowIndex))
                {
                    continue;
                }
                if (page.BrowserWindowIndex != parentWindowIndex)
                {
                    page.BrowserWindowIndex = parentWindowIndex;
                    changed++;
                }
            }

            // 3) 每个窗口内的标签序号：缺失（< 0）或重复的，按当前顺序重排。
            //    已经有唯一合法序号的标签不动。
            Dictionary<string, HashSet<int>> usedTabsByWindow = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem page = items[index];
                if (page == null || page.Kind != SessionItemKind.WebPage || page.BrowserWindowIndex < 0)
                {
                    continue;
                }

                string windowKey = WindowKey(page);
                HashSet<int> used;
                if (!usedTabsByWindow.TryGetValue(windowKey, out used))
                {
                    used = new HashSet<int>();
                    usedTabsByWindow[windowKey] = used;
                }
                if (page.BrowserTabIndex >= 0 && used.Add(page.BrowserTabIndex))
                {
                    continue;
                }

                // 序号缺失或和同窗口里别的标签撞车：找这个窗口里还没用过的第一个空位。
                int candidate = 0;
                while (used.Contains(candidate))
                {
                    candidate++;
                }
                used.Add(candidate);
                if (page.BrowserTabIndex != candidate)
                {
                    page.BrowserTabIndex = candidate;
                    changed++;
                }
            }

            return changed;
        }

        private static string WindowKey(SessionItem page)
        {
            string group = page.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(group))
            {
                group = page.BrowserExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(group))
            {
                group = page.ExecutablePath;
            }
            return (group ?? string.Empty).Trim().ToLowerInvariant()
                + "|" + page.BrowserWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>浏览器窗口条目：应用类、且带浏览器分组键。</summary>
        private static bool IsBrowserWindowItem(SessionItem item)
        {
            return item != null
                && item.Kind == SessionItemKind.Application
                && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
        }
    }
}
