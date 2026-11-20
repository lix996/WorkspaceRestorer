using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.StartupRestore.Core
{
    /// <summary>
    /// 「启动项整组搬运」的**唯一一份规则**：把一个启动项（连同它的全部子项）从一份方案
    /// 搬到另一份方案，父项和子项成块、顺序保持。
    ///
    /// 用户 2026-11-01 的要求：
    ///   · “拖浏览器窗口/文件夹父行到别的方案 → 整组一起走（如果里面有子方案，也要一起拖走，
    ///      像拖文件夹一样，千万不要频繁读写磁盘）。”
    ///   · “网页/文件夹的子项：只允许整组一起走。”
    ///
    /// 分类：这是小项规则，按 2026-11-01 的约定放在「启动与恢复」的 Core 里；
    /// 「自启动应用」不复制规则，只放一个适配层
    /// `Applications/Core/ApplicationPlanItemMoveAdapter.cs`（与
    /// `Applications/Deletion/ApplicationPlanItemDeletionAdapter.cs` 同一种做法）。
    ///
    /// 只做**纯计算**：返回新的来源/目标列表，由调用方（ApplicationController）一次落盘。
    /// 这样整组搬移只写一次磁盘，不会“搬一个子项写一次”。
    /// </summary>
    internal static class PlanItemGroupMove
    {
        /// <summary>规则只认这两个字段，两种模型各自翻译过来。</summary>
        internal sealed class NodeInfo
        {
            public string Id;
            public string ParentId;
        }

        /// <summary>
        /// 计算“把 <paramref name="moving"/> 整组搬到 <paramref name="destination"/>”的结果。
        ///
        /// 返回 false 时 <paramref name="reason"/>：
        ///   · <c>invalid</c>  参数不全；
        ///   · <c>child</c>    拖的是子项（用户要求：子项只能整组走）；
        ///   · <c>duplicate</c> 目标方案里已有同身份的项（整组拒绝，绝不重复放）。
        /// </summary>
        internal static bool TryBuild<T>(
            IList<T> source,
            IList<T> destination,
            T moving,
            T anchor,
            bool insertAfter,
            Func<T, NodeInfo> describe,
            Func<IList<T>, T, bool> alreadyExists,
            out List<T> newSource,
            out List<T> newDestination,
            out string reason)
        {
            newSource = null;
            newDestination = null;
            reason = null;
            if (source == null || destination == null || moving == null || describe == null)
            {
                reason = "invalid";
                return false;
            }
            NodeInfo movingNode = describe(moving);
            if (movingNode == null)
            {
                reason = "invalid";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(movingNode.ParentId))
            {
                // 用户 2026-11-01：子项不单独跨方案，只能整组走。
                reason = "child";
                return false;
            }

            // ① 收集整组：父项 + 全部后代。按来源列表顺序添加，支持多层（子项还有子项）。
            List<T> group = new List<T>();
            group.Add(moving);
            HashSet<string> groupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(movingNode.Id))
            {
                groupIds.Add(movingNode.Id);
            }
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int index = 0; index < source.Count; index++)
                {
                    T candidate = source[index];
                    if (candidate == null || object.ReferenceEquals(candidate, moving))
                    {
                        continue;
                    }
                    if (ContainsReference(group, candidate))
                    {
                        continue;
                    }
                    NodeInfo node = describe(candidate);
                    if (node == null || string.IsNullOrWhiteSpace(node.ParentId))
                    {
                        continue;
                    }
                    if (!groupIds.Contains(node.ParentId))
                    {
                        continue;
                    }
                    group.Add(candidate);
                    if (!string.IsNullOrWhiteSpace(node.Id))
                    {
                        groupIds.Add(node.Id);
                    }
                    grew = true;
                }
            }

            // ② 目标方案里已有同身份项 → 整组拒绝（与原来的“不重复放”一致）。
            if (alreadyExists != null)
            {
                for (int index = 0; index < group.Count; index++)
                {
                    if (alreadyExists(destination, group[index]))
                    {
                        reason = "duplicate";
                        return false;
                    }
                }
            }

            // ③ 从来源整组拿走（一次遍历；后面的落盘也只写这一次）。
            newSource = new List<T>(source.Count);
            for (int index = 0; index < source.Count; index++)
            {
                T candidate = source[index];
                if (candidate != null && ContainsReference(group, candidate))
                {
                    continue;
                }
                newSource.Add(candidate);
            }

            // ④ 插到目标：有定位项就按“它之前/之后”，没有就追加到末尾。
            newDestination = new List<T>(destination);
            int insertAt = newDestination.Count;
            if (anchor != null)
            {
                int anchorIndex = newDestination.IndexOf(anchor);
                if (anchorIndex >= 0)
                {
                    insertAt = insertAfter ? anchorIndex + 1 : anchorIndex;
                }
            }
            insertAt = Math.Max(0, Math.Min(insertAt, newDestination.Count));
            newDestination.InsertRange(insertAt, group);
            return true;
        }

        private static bool ContainsReference<T>(IList<T> items, T candidate)
        {
            for (int index = 0; index < items.Count; index++)
            {
                if (object.ReferenceEquals(items[index], candidate))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
