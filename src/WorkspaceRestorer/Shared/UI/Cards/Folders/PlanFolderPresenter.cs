using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.UI.Cards.Folders
{
    /// <summary>
    /// 【公共区域 · 方案文件夹】卡片展示的公共小件。
    ///
    /// 只提供“缩进宿主、统计文字、文件夹图标”这类与业务无关的零件；
    /// 三个工作区各自的卡片内容、按钮和拖动仍然在自己的 UI 文件里，
    /// 不把整套卡片硬塞进公共区。
    /// </summary>
    public static class PlanFolderPresenter
    {
        /// <summary>
        /// 子卡片宿主：卡片本身和父级启动项同宽，层级只靠调用方的 42 像素左留白表达。
        ///
        /// 2026-11-01 用户确认去掉左侧灰色竖线和小折角：它们比卡片边框还显眼，
        /// 而且折角只画在整组顶部一次，看起来像“一根杆子挑一下”，反而干扰卡片内容。
        /// 删除后宿主结构不变，卡片位置、宽度、点击和拖动都不受影响。
        /// </summary>
        public static Grid CreateChildHost(out StackPanel childHost)
        {
            Grid host = new Grid();
            childHost = new StackPanel();
            host.Children.Add(childHost);
            return host;
        }

        public static string BuildCountText(int childCount, int itemCount, bool isFolder)
        {
            if (isFolder)
            {
                return childCount <= 0 ? "空文件夹" : (childCount + " 个子方案");
            }
            if (childCount <= 0)
            {
                return itemCount + " 个启动项目";
            }
            return childCount + " 个子方案 · " + itemCount + " 个启动项目";
        }

        public static string FolderGlyph(bool isFolder)
        {
            // Segoe MDL2 Assets：文件夹 / 文档。和两个工作区现有卡片图标同一套字体。
            return isFolder ? "\uE8B7" : "\uE8A5";
        }

        public static TextBlock CreateFolderBadge()
        {
            return new TextBlock
            {
                Text = "文件夹",
                FontSize = 10,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
        }

        /// <summary>
        /// 从鼠标点到的控件往上找“最近一张方案卡片”的 Tag。
        /// 子卡片点自己时不能冒泡选中父卡片，所以调用方拿它和当前卡片比较。
        /// </summary>
        public static object FindOwningCardTag(DependencyObject source)
        {
            DependencyObject cursor = source;
            while (cursor != null)
            {
                FrameworkElement element = cursor as FrameworkElement;
                if (element != null && element.Tag != null)
                {
                    return element.Tag;
                }
                try
                {
                    cursor = VisualTreeHelper.GetParent(cursor);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>把子树拍平成恢复顺序：当前方案自己的项由调用方加，这里只负责子方案顺序。</summary>
        public static List<T> PreOrderChildren<T>(IList<T> nodes, T root) where T : class, IPlanFolderNode
        {
            List<T> result = new List<T>();
            if (nodes == null || root == null)
            {
                return result;
            }
            List<T> children = PlanFolderTree.ChildrenOf(nodes, root.Id);
            for (int index = 0; index < children.Count; index++)
            {
                T child = children[index];
                result.Add(child);
                List<T> descendants = PlanFolderTree.Subtree(nodes, child);
                for (int descendantIndex = 0; descendantIndex < descendants.Count; descendantIndex++)
                {
                    T descendant = descendants[descendantIndex];
                    if (!object.ReferenceEquals(descendant, child))
                    {
                        result.Add(descendant);
                    }
                }
            }
            return result;
        }
    }
}
