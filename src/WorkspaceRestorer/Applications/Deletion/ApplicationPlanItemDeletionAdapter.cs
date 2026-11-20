using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.Applications.Deletion
{
    /// <summary>
    /// 「自启动应用」的小项删除适配器。
    ///
    /// 规则只有一份，在 StartupRestore/Deletion/PlanItemDeletion；这里仅把
    /// ApplicationPlanItem 的浏览器/文件夹身份翻译过去，不复制删除逻辑。
    /// </summary>
    internal static class ApplicationPlanItemDeletionAdapter
    {
        internal static List<ApplicationPlanItem> Collect(
            IList<ApplicationPlanItem> items,
            ApplicationPlanItem selected)
        {
            return PlanItemDeletion.Collect(items, selected, Describe);
        }

        internal static int RemoveEmptyGroupParents(IList<ApplicationPlanItem> items)
        {
            return PlanItemDeletion.RemoveEmptyGroupParents(items, Describe);
        }

        internal static bool HasGroupChildren(
            IList<ApplicationPlanItem> items,
            ApplicationPlanItem parent)
        {
            return PlanItemDeletion.HasGroupChildren(items, parent, Describe);
        }

        private static PlanItemDeletion.NodeInfo Describe(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return null;
            }
            bool browserPage = item.IsBrowserPage || item.Kind == SessionItemKind.WebPage;
            bool browserWindow = item.IsBrowserWindow
                || (!browserPage
                    && item.IsGroupParent
                    && item.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(item.BrowserGroupKey));
            return new PlanItemDeletion.NodeInfo
            {
                Id = item.Id,
                ParentId = item.ParentId,
                BrowserGroupKey = item.BrowserGroupKey,
                BrowserWindowIndex = item.BrowserWindowIndex,
                IsBrowserPage = browserPage,
                IsBrowserWindow = browserWindow,
                IsGroupParent = item.IsGroupParent || item.IsFolderGroup || browserWindow
            };
        }
    }
}
