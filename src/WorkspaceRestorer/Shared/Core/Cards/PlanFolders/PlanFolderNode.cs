using System;
using System.Collections.Generic;
using System.Text;

namespace WorkspaceRestorer.Core.Cards.PlanFolders
{
    /// <summary>
    /// 方案文件夹节点约定：工作方案、自启动方案、图标布局都实现这四个成员。
    /// 只描述“树结构”，不包含各自的启动项，也不负责落盘。
    /// </summary>
    public interface IPlanFolderNode
    {
        string Id { get; set; }

        string Name { get; set; }

        /// <summary>父方案 Id；空字符串/null 表示根级。</summary>
        string ParentId { get; set; }

        /// <summary>true = 纯文件夹（只装子方案，没有自己的启动项）。</summary>
        bool IsFolder { get; set; }
    }

    /// <summary>
    /// 【公共区域 · 方案文件夹】无限层方案嵌套的纯规则。
    ///
    /// 数据结构是“扁平清单 + ParentId”，不是每个方案里再放 List&lt;方案&gt;：
    ///  · 旧 JSON 升级简单：没有 ParentId 就是根级；
    ///  · 搜索、撤销、回收站、复制粘贴不用递归序列化；
    ///  · 父子顺序统一由清单顺序表达（保存时整理成前序：父方案在前，整棵子树紧跟）。
    ///
    /// 这里只做计算，不落盘、不弹框、不碰界面。工作区各自的按钮和业务仍在各自文件夹。
    /// </summary>
    public static class PlanFolderTree
    {
        /// <summary>
        /// 修复旧数据并整理成前序：
        ///  · 空 Id / 重复 Id 重新生成；
        ///  · 父项不存在、指向自己、A→B→A 循环，一律退回根级；
        ///  · 根方案和同级子方案保持原清单顺序；
        ///  · 输出顺序 = 根1、根1的全部后代、根2……
        /// 返回被修复的节点数（删除 null 不计）。
        /// </summary>
        public static int Normalize<T>(IList<T> nodes) where T : class, IPlanFolderNode
        {
            if (nodes == null)
            {
                return 0;
            }

            int repaired = 0;
            for (int index = nodes.Count - 1; index >= 0; index--)
            {
                if (nodes[index] == null)
                {
                    nodes.RemoveAt(index);
                }
            }
            if (nodes.Count == 0)
            {
                return 0;
            }

            Dictionary<string, T> byId = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (string.IsNullOrWhiteSpace(node.Id) || byId.ContainsKey(node.Id))
                {
                    node.Id = Guid.NewGuid().ToString("N");
                    repaired++;
                }
                byId[node.Id] = node;
            }

            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (string.IsNullOrWhiteSpace(node.ParentId))
                {
                    node.ParentId = null;
                    continue;
                }

                T parent;
                if (!byId.TryGetValue(node.ParentId, out parent)
                    || parent == null
                    || string.Equals(parent.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                {
                    node.ParentId = null;
                    repaired++;
                    continue;
                }

                // 沿父链往上走，走回自己就是循环；循环节点退回根级，不让整棵树卡死。
                HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                visited.Add(node.Id);
                T cursor = parent;
                bool cycle = false;
                while (cursor != null)
                {
                    if (string.IsNullOrWhiteSpace(cursor.Id) || !visited.Add(cursor.Id))
                    {
                        cycle = true;
                        break;
                    }
                    if (string.IsNullOrWhiteSpace(cursor.ParentId))
                    {
                        break;
                    }
                    T next;
                    if (!byId.TryGetValue(cursor.ParentId, out next))
                    {
                        break;
                    }
                    cursor = next;
                }
                if (cycle)
                {
                    node.ParentId = null;
                    repaired++;
                }
            }

            // 以当前清单顺序收集每个父项的直接子方案。
            Dictionary<string, List<T>> children = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
            List<T> roots = new List<T>();
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (string.IsNullOrWhiteSpace(node.ParentId))
                {
                    roots.Add(node);
                    continue;
                }
                List<T> list;
                if (!children.TryGetValue(node.ParentId, out list))
                {
                    list = new List<T>();
                    children[node.ParentId] = list;
                }
                list.Add(node);
            }

            List<T> ordered = new List<T>(nodes.Count);
            Stack<T> stack = new Stack<T>();
            for (int index = roots.Count - 1; index >= 0; index--)
            {
                stack.Push(roots[index]);
            }
            HashSet<string> emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                T node = stack.Pop();
                if (node == null || (!string.IsNullOrWhiteSpace(node.Id) && !emitted.Add(node.Id)))
                {
                    continue;
                }
                ordered.Add(node);
                List<T> childList;
                if (!string.IsNullOrWhiteSpace(node.Id) && children.TryGetValue(node.Id, out childList))
                {
                    for (int index = childList.Count - 1; index >= 0; index--)
                    {
                        stack.Push(childList[index]);
                    }
                }
            }

            // 理论上不会漏；真漏了也补到末尾，绝不因为一次整理丢方案。
            if (ordered.Count != nodes.Count)
            {
                for (int index = 0; index < nodes.Count; index++)
                {
                    if (nodes[index] != null && !ordered.Contains(nodes[index]))
                    {
                        ordered.Add(nodes[index]);
                    }
                }
            }

            nodes.Clear();
            for (int index = 0; index < ordered.Count; index++)
            {
                nodes.Add(ordered[index]);
            }
            return repaired;
        }

        public static T FindById<T>(IList<T> nodes, string id) where T : class, IPlanFolderNode
        {
            if (nodes == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (node != null && string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return node;
                }
            }
            return null;
        }

        public static List<T> Roots<T>(IList<T> nodes) where T : class, IPlanFolderNode
        {
            List<T> result = new List<T>();
            if (nodes == null)
            {
                return result;
            }
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (node != null && string.IsNullOrWhiteSpace(node.ParentId))
                {
                    result.Add(node);
                }
            }
            return result;
        }

        public static List<T> ChildrenOf<T>(IList<T> nodes, string parentId) where T : class, IPlanFolderNode
        {
            List<T> result = new List<T>();
            if (nodes == null || string.IsNullOrWhiteSpace(parentId))
            {
                return result;
            }
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (node != null && string.Equals(node.ParentId, parentId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(node);
                }
            }
            return result;
        }

        public static int ChildCount<T>(IList<T> nodes, string parentId) where T : class, IPlanFolderNode
        {
            return ChildrenOf(nodes, parentId).Count;
        }

        /// <summary>整棵子树（前序，包含 root 自己）。</summary>
        public static List<T> Subtree<T>(IList<T> nodes, T root) where T : class, IPlanFolderNode
        {
            List<T> result = new List<T>();
            if (nodes == null || root == null)
            {
                return result;
            }
            List<T> ordered = new List<T>(nodes);
            Normalize(ordered);
            bool found = false;
            for (int index = 0; index < ordered.Count; index++)
            {
                T node = ordered[index];
                if (object.ReferenceEquals(node, root)
                    || (!string.IsNullOrWhiteSpace(root.Id)
                        && node != null
                        && string.Equals(node.Id, root.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    found = true;
                }
                if (found && (object.ReferenceEquals(node, root)
                    || IsDescendantId(ordered, node.Id, root.Id)))
                {
                    result.Add(node);
                }
            }
            return result;
        }

        public static bool IsDescendantId<T>(IList<T> nodes, string candidateId, string ancestorId) where T : class, IPlanFolderNode
        {
            if (nodes == null
                || string.IsNullOrWhiteSpace(candidateId)
                || string.IsNullOrWhiteSpace(ancestorId)
                || string.Equals(candidateId, ancestorId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            T cursor = FindById(nodes, candidateId);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor != null && !string.IsNullOrWhiteSpace(cursor.ParentId))
            {
                if (!visited.Add(cursor.Id))
                {
                    return false;
                }
                if (string.Equals(cursor.ParentId, ancestorId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                cursor = FindById(nodes, cursor.ParentId);
            }
            return false;
        }

        public static bool CanMove<T>(IList<T> nodes, T source, T targetParent) where T : class, IPlanFolderNode
        {
            if (nodes == null || source == null)
            {
                return false;
            }
            if (targetParent == null)
            {
                return true;
            }
            if (object.ReferenceEquals(source, targetParent)
                || string.Equals(source.Id, targetParent.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return !IsDescendantId(nodes, targetParent.Id, source.Id);
        }

        /// <summary>
        /// 移入某个方案下面（targetParent = null 表示移到根级末尾）。
        /// 来源方案和它的整棵子树原样保留，不复制、不删除、不摊平。
        /// </summary>
        public static bool MoveInto<T>(IList<T> nodes, T source, T targetParent) where T : class, IPlanFolderNode
        {
            if (!CanMove(nodes, source, targetParent))
            {
                return false;
            }
            T storedSource = FindById(nodes, source.Id);
            T storedTarget = targetParent == null ? null : FindById(nodes, targetParent.Id);
            if (storedSource == null || (targetParent != null && storedTarget == null))
            {
                return false;
            }
            if (string.Equals(storedSource.ParentId, storedTarget == null ? null : storedTarget.Id, StringComparison.OrdinalIgnoreCase))
            {
                // 已经是这个父项：中心落点只表示“移进去”，不改变它原来的同级顺序。
                Normalize(nodes);
                return true;
            }

            string newParentId = storedTarget == null ? null : storedTarget.Id;
            List<T> subtree = Subtree(nodes, storedSource);
            for (int index = 0; index < subtree.Count; index++)
            {
                nodes.Remove(subtree[index]);
            }
            storedSource.ParentId = newParentId;
            int insertAt = nodes.Count;
            if (storedTarget != null)
            {
                int targetIndex = nodes.IndexOf(storedTarget);
                insertAt = targetIndex < 0 ? nodes.Count : targetIndex + 1;
            }
            nodes.Insert(Math.Max(0, Math.Min(insertAt, nodes.Count)), storedSource);
            Normalize(nodes);
            return true;
        }

        /// <summary>
        /// 移到 target 前面/后面，成为它的同级方案（target 的父级就是新父级）。
        /// </summary>
        public static bool MoveRelative<T>(IList<T> nodes, T source, T target, bool after) where T : class, IPlanFolderNode
        {
            if (nodes == null || source == null || target == null)
            {
                return false;
            }
            if (object.ReferenceEquals(source, target)
                || string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (IsDescendantId(nodes, target.Id, source.Id))
            {
                return false;
            }
            T storedSource = FindById(nodes, source.Id);
            T storedTarget = FindById(nodes, target.Id);
            if (storedSource == null || storedTarget == null)
            {
                return false;
            }

            List<T> sourceSubtree = Subtree(nodes, storedSource);
            List<T> targetSubtree = Subtree(nodes, storedTarget);
            for (int index = 0; index < sourceSubtree.Count; index++)
            {
                nodes.Remove(sourceSubtree[index]);
            }
            storedSource.ParentId = storedTarget.ParentId;
            int targetIndex = nodes.IndexOf(storedTarget);
            if (targetIndex < 0)
            {
                nodes.Add(storedSource);
            }
            else
            {
                int insertAt = after ? targetIndex + 1 : targetIndex;
                nodes.Insert(Math.Max(0, Math.Min(insertAt, nodes.Count)), storedSource);
            }
            Normalize(nodes);
            return true;
        }

        /// <summary>
        /// 把已经挂好 ParentId 的节点放到同级指定位置（0 开始）；超出范围就放末尾。
        /// 回收站恢复“原来的同级位置”用。
        /// </summary>
        public static bool PlaceAtSiblingIndex<T>(IList<T> nodes, T node, int siblingIndex) where T : class, IPlanFolderNode
        {
            if (nodes == null || node == null)
            {
                return false;
            }
            T stored = FindById(nodes, node.Id);
            if (stored == null)
            {
                return false;
            }
            List<T> siblings = string.IsNullOrWhiteSpace(stored.ParentId)
                ? Roots(nodes)
                : ChildrenOf(nodes, stored.ParentId);
            siblings.RemoveAll(delegate(T candidate)
            {
                return candidate == null
                    || object.ReferenceEquals(candidate, stored)
                    || string.Equals(candidate.Id, stored.Id, StringComparison.OrdinalIgnoreCase);
            });
            int insertAt = Math.Max(0, Math.Min(siblingIndex, siblings.Count));
            if (insertAt < siblings.Count)
            {
                // 放到“原来这个位置”的兄弟前面；Normalize 会按 sibling 列表顺序重排。
                return MoveRelative(nodes, stored, siblings[insertAt], false);
            }
            if (siblings.Count > 0)
            {
                return MoveRelative(nodes, stored, siblings[siblings.Count - 1], true);
            }
            return true;
        }

        /// <summary>删除整棵子树；返回被移除的节点（前序），供回收站/撤销使用。</summary>
        public static List<T> RemoveSubtree<T>(IList<T> nodes, T root) where T : class, IPlanFolderNode
        {
            List<T> removed = Subtree(nodes, root);
            if (removed.Count == 0)
            {
                return removed;
            }
            for (int index = 0; index < removed.Count; index++)
            {
                nodes.Remove(removed[index]);
            }
            Normalize(nodes);
            return removed;
        }

        public static int Depth<T>(IList<T> nodes, T node) where T : class, IPlanFolderNode
        {
            if (node == null)
            {
                return 0;
            }
            int depth = 0;
            T cursor = node;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor != null && !string.IsNullOrWhiteSpace(cursor.ParentId))
            {
                if (!visited.Add(cursor.Id))
                {
                    break;
                }
                cursor = FindById(nodes, cursor.ParentId);
                if (cursor != null)
                {
                    depth++;
                }
            }
            return depth;
        }

        public static List<T> Ancestors<T>(IList<T> nodes, T node) where T : class, IPlanFolderNode
        {
            List<T> result = new List<T>();
            if (node == null)
            {
                return result;
            }
            T cursor = node;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor != null && !string.IsNullOrWhiteSpace(cursor.ParentId))
            {
                if (!visited.Add(cursor.Id))
                {
                    break;
                }
                cursor = FindById(nodes, cursor.ParentId);
                if (cursor == null)
                {
                    break;
                }
                result.Insert(0, cursor);
            }
            return result;
        }

        public static string PathOf<T>(IList<T> nodes, T node, string separator) where T : class, IPlanFolderNode
        {
            List<T> ancestors = Ancestors(nodes, node);
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < ancestors.Count; index++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(separator);
                }
                builder.Append(string.IsNullOrWhiteSpace(ancestors[index].Name) ? "未命名" : ancestors[index].Name);
            }
            if (node != null)
            {
                if (builder.Length > 0)
                {
                    builder.Append(separator);
                }
                builder.Append(string.IsNullOrWhiteSpace(node.Name) ? "未命名" : node.Name);
            }
            return builder.ToString();
        }

        /// <summary>按当前清单顺序算某个方案在它同级中的位置（0 开始）；找不到返回 -1。</summary>
        public static int SiblingIndex<T>(IList<T> nodes, T node) where T : class, IPlanFolderNode
        {
            if (nodes == null || node == null)
            {
                return -1;
            }
            List<T> siblings = string.IsNullOrWhiteSpace(node.ParentId)
                ? Roots(nodes)
                : ChildrenOf(nodes, node.ParentId);
            for (int index = 0; index < siblings.Count; index++)
            {
                if (object.ReferenceEquals(siblings[index], node)
                    || string.Equals(siblings[index].Id, node.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return -1;
        }
    }
}
