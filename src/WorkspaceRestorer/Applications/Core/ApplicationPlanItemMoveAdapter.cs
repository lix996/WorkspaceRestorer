using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.StartupRestore.Core;

namespace WorkspaceRestorer.Applications.Core
{
    /// <summary>
    /// 「自启动应用」的启动项整组搬运适配层。
    ///
    /// 规则只有一份，在 `StartupRestore/Core/PlanItemGroupMove`；这里只把
    /// `ApplicationPlanItem` 的 Id/ParentId 翻译过去，并把“同一路径视为同一项”的
    /// 去重口径交给规则。**不要在这里再写一套父项+子项的搬移逻辑。**
    ///
    /// 与 `Applications/Deletion/ApplicationPlanItemDeletionAdapter.cs` 同一种做法：
    /// 规则住「启动与恢复」，自启动应用只做适配。
    /// </summary>
    internal static class ApplicationPlanItemMoveAdapter
    {
        internal static bool TryMove(
            IList<ApplicationPlanItem> source,
            IList<ApplicationPlanItem> destination,
            ApplicationPlanItem moving,
            ApplicationPlanItem anchor,
            bool insertAfter,
            out List<ApplicationPlanItem> newSource,
            out List<ApplicationPlanItem> newDestination)
        {
            string reason;
            return PlanItemGroupMove.TryBuild(
                source,
                destination,
                moving,
                anchor,
                insertAfter,
                Describe,
                HasSameTarget,
                out newSource,
                out newDestination,
                out reason);
        }

        /// <summary>被规则判定为“不能搬”的原因（child / duplicate / invalid）。</summary>
        internal static bool TryMove(
            IList<ApplicationPlanItem> source,
            IList<ApplicationPlanItem> destination,
            ApplicationPlanItem moving,
            ApplicationPlanItem anchor,
            bool insertAfter,
            out List<ApplicationPlanItem> newSource,
            out List<ApplicationPlanItem> newDestination,
            out string reason)
        {
            return PlanItemGroupMove.TryBuild(
                source,
                destination,
                moving,
                anchor,
                insertAfter,
                Describe,
                HasSameTarget,
                out newSource,
                out newDestination,
                out reason);
        }

        private static PlanItemGroupMove.NodeInfo Describe(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return null;
            }
            return new PlanItemGroupMove.NodeInfo
            {
                Id = item.Id,
                ParentId = item.ParentId
            };
        }

        /// <summary>
        /// 目标方案里是否已有同一个项目：按路径比较（与控制器原来的口径一致）。
        /// 空路径不参与比较，避免把一批“没有路径”的项误判成重复。
        /// </summary>
        private static bool HasSameTarget(IList<ApplicationPlanItem> items, ApplicationPlanItem candidate)
        {
            if (items == null || candidate == null)
            {
                return false;
            }
            string candidatePath = string.IsNullOrWhiteSpace(candidate.TargetPath)
                ? string.Empty
                : candidate.TargetPath.Trim();
            if (candidatePath.Length == 0)
            {
                return false;
            }
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem existing = items[index];
                if (existing == null)
                {
                    continue;
                }
                string path = string.IsNullOrWhiteSpace(existing.TargetPath)
                    ? string.Empty
                    : existing.TargetPath.Trim();
                if (path.Length > 0 && string.Equals(path, candidatePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
