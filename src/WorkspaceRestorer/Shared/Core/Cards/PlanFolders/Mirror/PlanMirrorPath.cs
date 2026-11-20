using System;
using System.Collections.Generic;
using System.IO;

namespace WorkspaceRestorer.Core.Cards.PlanFolders.Mirror
{
    /// <summary>
    /// 【公共区域 · 方案镜像】只算相对目录段和字符串路径，不读磁盘、不建目录。
    ///
    /// 镜像目录按“父方案 / 子方案 / 孙方案”分层，同一层的名字由
    /// <see cref="PlanFolderName"/> 按当前同级顺序算。这里只做树查询和 Path.Combine，
    /// 真正的落盘在 Shared\Infrastructure\PlanMirrors。
    /// </summary>
    public static class PlanMirrorPath
    {
        /// <summary>
        /// 从根到这份方案的相对目录段；节点或清单为空返回 null。
        /// 节点 Id 为空时返回空列表（沿用旧行为：调用方会得到镜像根目录）。
        /// </summary>
        public static List<string> BuildRelativeSegments<T>(
            T node,
            IList<T> allNodes,
            Func<T, string> nameOf,
            string fallbackName) where T : class, IPlanFolderNode
        {
            if (node == null || allNodes == null)
            {
                return null;
            }

            List<string> segments = new List<string>();
            T cursor = node;
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (cursor != null
                && !string.IsNullOrWhiteSpace(cursor.Id)
                && visited.Add(cursor.Id))
            {
                IList<T> siblings = string.IsNullOrWhiteSpace(cursor.ParentId)
                    ? PlanFolderTree.Roots(allNodes)
                    : PlanFolderTree.ChildrenOf(allNodes, cursor.ParentId);
                List<string> names = PlanFolderName.BuildFolderNames(siblings, nameOf, fallbackName);
                int index = -1;
                for (int siblingIndex = 0; siblingIndex < siblings.Count; siblingIndex++)
                {
                    T sibling = siblings[siblingIndex];
                    if (sibling != null
                        && string.Equals(sibling.Id, cursor.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        index = siblingIndex;
                        break;
                    }
                }
                if (index < 0 || index >= names.Count)
                {
                    return null;
                }

                segments.Insert(0, names[index]);
                if (string.IsNullOrWhiteSpace(cursor.ParentId))
                {
                    break;
                }
                cursor = PlanFolderTree.FindById(allNodes, cursor.ParentId);
            }
            return segments;
        }

        /// <summary>把相对目录段拼到镜像根目录下面；段列表为 null 返回 null。</summary>
        public static string BuildPath(string root, IList<string> segments)
        {
            if (string.IsNullOrWhiteSpace(root) || segments == null)
            {
                return null;
            }

            string path = root;
            for (int index = 0; index < segments.Count; index++)
            {
                path = Path.Combine(path, segments[index]);
            }
            return path;
        }
    }
}
