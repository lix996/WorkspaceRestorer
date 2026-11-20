using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Core.Cards.PlanFolders
{
    /// <summary>
    /// 移动前的位置：只记结构，不复制启动项。
    /// 撤销“并入/调顺序”时用它把目录和父子关系一起放回原处。
    /// </summary>
    public sealed class PlanFolderMoveSnapshot
    {
        public string PlanId { get; set; }

        public string ParentId { get; set; }

        public int SiblingIndex { get; set; }

        public string DirectoryPath { get; set; }
    }

    /// <summary>
    /// 删除/复制一棵方案子树时的公共快照。
    ///
    /// 工作区把 <see cref="Nodes"/> 序列化成自己的 JSON 放进回收站；
    /// 这个类型本身只描述“根是谁、原来在哪、树里有哪些节点”，不绑定具体工作区。
    /// </summary>
    public sealed class PlanFolderSnapshot<T> where T : class, IPlanFolderNode
    {
        public PlanFolderSnapshot()
        {
            Nodes = new List<T>();
        }

        public string RootId { get; set; }

        public string OriginalParentId { get; set; }

        public int OriginalSiblingIndex { get; set; }

        /// <summary>给人看的原路径，例如“大方案 A / 小方案 A1”。</summary>
        public string OriginalPath { get; set; }

        public List<T> Nodes { get; set; }
    }

    /// <summary>
    /// 从回收站恢复时怎么找“原来的位置”。
    ///
    /// 关键规则（用户 2026-11-01 确认）：
    ///  · 原父方案还在 → 回到原父方案下面，同级位置尽量保持；
    ///  · 原父方案不在了（删除/永久删除/还在回收站）→ 放根目录末尾；
    ///  · 绝不为了恢复子方案而自动重建大方案。
    /// </summary>
    public static class PlanFolderRestorePlacement
    {
        public static int ClampSiblingIndex(int originalIndex, int siblingCount)
        {
            if (siblingCount <= 0)
            {
                return 0;
            }
            if (originalIndex < 0)
            {
                return siblingCount;
            }
            return Math.Max(0, Math.Min(originalIndex, siblingCount));
        }

        public static string DescribeOriginalPath(string originalPath, string fallbackName)
        {
            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                return originalPath.Trim();
            }
            return string.IsNullOrWhiteSpace(fallbackName) ? "未命名方案" : fallbackName.Trim();
        }
    }
}
