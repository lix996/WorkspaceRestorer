using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.StartupRestore.Deletion
{
    /// <summary>
    /// 【启动与恢复 · 删除规则】删除“方案清单里的一个小项”时，哪些项必须跟着一起删。
    ///
    /// 这段代码属于「启动与恢复」；「自启动应用」通过自己的适配器调用，
    /// 不复制一份规则。「图标位置保存」没有这种小项删除，所以不引用。
    ///
    /// 规则（2026-11-01 用户确认，2026-11-01 晚扩展到所有父子项）：
    ///   · 普通程序/文件：只删自己；
    ///   · 还有兄弟项的子项：只删这个子项；
    ///   · 父项的最后一个子项：子项和空父项一起删，并继续向上检查；
    ///     浏览器网页、资源管理器文件夹、普通文件都适用，不再只限浏览器；
    ///   · 浏览器父项：父项 + 该窗口全部网页；
    ///   · 文件夹/其它父项：父项 + 全部后代，递归。
    ///
    /// 两个工作区的完整快照撤销都建立在“删除前的清单”上，所以这里把连带项
    /// 一次算全，撤销才能把父项和网页一起放回来。
    /// </summary>
    internal static class PlanItemDeletion
    {
        /// <summary>
        /// 删除规则只认这几个结构信息；具体模型由各工作区的适配器填。
        /// </summary>
        internal sealed class NodeInfo
        {
            public string Id;
            public string ParentId;
            public string BrowserGroupKey;
            public int BrowserWindowIndex;
            public bool IsBrowserPage;
            public bool IsBrowserWindow;
            public bool IsGroupParent;
        }

        internal static List<T> Collect<T>(
            IList<T> items,
            T selected,
            Func<T, NodeInfo> describe)
        {
            List<T> result = new List<T>();
            if (items == null || selected == null || describe == null)
            {
                return result;
            }
            NodeInfo selectedNode = describe(selected);
            if (selectedNode == null)
            {
                return result;
            }

            result.Add(selected);

            if (selectedNode.IsBrowserWindow)
            {
                // 删整组：同一浏览器窗口的网页全部带上，包括 ParentId 丢失但
                // BrowserGroupKey/窗口号还能对上的旧数据。
                List<T> pages = FindBrowserPages(items, selectedNode, describe);
                for (int index = 0; index < pages.Count; index++)
                {
                    if (!result.Contains(pages[index]))
                    {
                        result.Add(pages[index]);
                    }
                }
            }

            // 关键规则：只要删掉的是父项的最后一个子项，空父项就一起删，并继续向上检查。
            // 浏览器网页、资源管理器里的文件夹、普通文件都适用。还有兄弟项时父项保留。
            AddEmptyAncestors(result, items, selected, describe);

            // 文件夹父项、以及任何 ParentId 挂在已选父项下面的后代，递归带上。
            AddDescendants(result, items, describe);
            return result;
        }

        /// <summary>删掉没有任何子项的浏览器/文件夹父项；返回清掉的个数。</summary>
        internal static int RemoveEmptyGroupParents<T>(
            IList<T> items,
            Func<T, NodeInfo> describe)
        {
            if (items == null || describe == null)
            {
                return 0;
            }
            int removed = 0;
            for (int index = items.Count - 1; index >= 0; index--)
            {
                T item = items[index];
                NodeInfo node = describe(item);
                if (node == null || !node.IsGroupParent)
                {
                    continue;
                }
                if (HasChild(items, node, describe))
                {
                    continue;
                }
                items.RemoveAt(index);
                removed++;
            }
            return removed;
        }

        internal static bool HasGroupChildren<T>(
            IList<T> items,
            T parent,
            Func<T, NodeInfo> describe)
        {
            if (items == null || parent == null || describe == null)
            {
                return false;
            }
            NodeInfo node = describe(parent);
            return node != null && HasChild(items, node, describe);
        }

        private static void AddDescendants<T>(
            List<T> result,
            IList<T> items,
            Func<T, NodeInfo> describe)
        {
            bool added;
            do
            {
                added = false;
                for (int index = 0; index < items.Count; index++)
                {
                    T candidate = items[index];
                    if (candidate == null || result.Contains(candidate))
                    {
                        continue;
                    }
                    NodeInfo child = describe(candidate);
                    if (child == null || string.IsNullOrWhiteSpace(child.ParentId))
                    {
                        continue;
                    }
                    for (int parentIndex = 0; parentIndex < result.Count; parentIndex++)
                    {
                        NodeInfo parent = describe(result[parentIndex]);
                        if (parent != null
                            && !string.IsNullOrWhiteSpace(parent.Id)
                            && string.Equals(parent.Id, child.ParentId, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(candidate);
                            added = true;
                            break;
                        }
                    }
                }
            }
            while (added);
        }

        private static List<T> FindBrowserPages<T>(
            IList<T> items,
            NodeInfo target,
            Func<T, NodeInfo> describe)
        {
            List<T> pages = new List<T>();
            if (items == null || target == null)
            {
                return pages;
            }
            for (int index = 0; index < items.Count; index++)
            {
                T candidate = items[index];
                NodeInfo node = describe(candidate);
                if (node == null || !node.IsBrowserPage)
                {
                    continue;
                }
                if (BelongsToBrowserTarget(node, target))
                {
                    pages.Add(candidate);
                }
            }
            return pages;
        }

        /// <summary>
        /// 从当前项往上找直接父项：优先 ParentId；网页的 ParentId 丢失时按
        /// BrowserGroupKey + 窗口号找浏览器父项。文件夹/文件只认 ParentId。
        /// </summary>
        private static T FindParent<T>(
            IList<T> items,
            NodeInfo child,
            Func<T, NodeInfo> describe)
        {
            if (items == null || child == null)
            {
                return default(T);
            }

            if (!string.IsNullOrWhiteSpace(child.ParentId))
            {
                for (int index = 0; index < items.Count; index++)
                {
                    T candidate = items[index];
                    NodeInfo node = describe(candidate);
                    if (node != null
                        && !node.IsBrowserPage
                        && !string.IsNullOrWhiteSpace(node.Id)
                        && string.Equals(node.Id, child.ParentId, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            if (child.IsBrowserPage)
            {
                // ParentId 丢失的旧网页：按浏览器窗口身份找父项。
                for (int index = 0; index < items.Count; index++)
                {
                    T candidate = items[index];
                    NodeInfo node = describe(candidate);
                    if (node != null
                        && !node.IsBrowserPage
                        && node.IsBrowserWindow
                        && SameBrowserWindow(node, child))
                    {
                        return candidate;
                    }
                }
            }
            return default(T);
        }

        /// <summary>
        /// 如果 child 是父项最后一个子项，就把空父项加进结果，再继续看父项是不是
        /// 更上一层父项的最后一个子项（嵌套文件夹也能一路收干净）。
        /// </summary>
        private static void AddEmptyAncestors<T>(
            List<T> result,
            IList<T> items,
            T selected,
            Func<T, NodeInfo> describe)
        {
            T currentItem = selected;
            NodeInfo current = describe(currentItem);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current != null)
            {
                if (!string.IsNullOrWhiteSpace(current.Id) && !visited.Add(current.Id))
                {
                    break;
                }

                T parent = FindParent(items, current, describe);
                if (parent == null)
                {
                    break;
                }
                NodeInfo parentNode = describe(parent);
                if (parentNode == null
                    || HasOtherChild(items, parentNode, currentItem, describe))
                {
                    break;
                }
                if (!result.Contains(parent))
                {
                    result.Add(parent);
                }
                currentItem = parent;
                current = parentNode;
            }
        }

        /// <summary>父项除 exclude 之外还有没有别的子项。</summary>
        private static bool HasOtherChild<T>(
            IList<T> items,
            NodeInfo parent,
            T exclude,
            Func<T, NodeInfo> describe)
        {
            if (items == null || parent == null)
            {
                return false;
            }
            for (int index = 0; index < items.Count; index++)
            {
                T candidate = items[index];
                if (candidate == null || object.ReferenceEquals(candidate, exclude))
                {
                    continue;
                }
                NodeInfo node = describe(candidate);
                if (node == null)
                {
                    continue;
                }
                if (SameNonEmpty(node.ParentId, parent.Id))
                {
                    return true;
                }
                if (parent.IsBrowserWindow
                    && node.IsBrowserPage
                    && BelongsToBrowserTarget(node, parent))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasChild<T>(
            IList<T> items,
            NodeInfo parent,
            Func<T, NodeInfo> describe)
        {
            if (items == null || parent == null)
            {
                return false;
            }
            for (int index = 0; index < items.Count; index++)
            {
                NodeInfo node = describe(items[index]);
                if (node == null)
                {
                    continue;
                }
                if (SameNonEmpty(node.ParentId, parent.Id))
                {
                    return true;
                }
                if (node.IsBrowserPage && BelongsToBrowserTarget(node, parent))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool BelongsToBrowserTarget(NodeInfo candidate, NodeInfo target)
        {
            if (candidate == null || target == null)
            {
                return false;
            }
            if (target.IsBrowserPage)
            {
                if (SameNonEmpty(candidate.ParentId, target.ParentId))
                {
                    return true;
                }
                return SameBrowserWindow(candidate, target);
            }

            if (SameNonEmpty(candidate.ParentId, target.Id))
            {
                return true;
            }
            return SameBrowserWindow(candidate, target);
        }

        private static bool SameBrowserWindow(NodeInfo left, NodeInfo right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(left.BrowserGroupKey)
                || string.IsNullOrWhiteSpace(right.BrowserGroupKey)
                || !string.Equals(left.BrowserGroupKey, right.BrowserGroupKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return left.BrowserWindowIndex == right.BrowserWindowIndex;
        }

        private static bool SameNonEmpty(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
