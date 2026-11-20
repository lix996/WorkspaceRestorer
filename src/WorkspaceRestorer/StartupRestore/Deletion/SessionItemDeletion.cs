using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.StartupRestore.Deletion
{
    /// <summary>
    /// 「启动与恢复」的小项删除适配器：把 SessionItem 的浏览器/文件夹身份
    /// 交给 StartupRestore/Deletion/PlanItemDeletion 的公共规则。
    /// </summary>
    internal static class SessionItemDeletion
    {
        internal static List<SessionItem> Collect(
            IList<SessionItem> items,
            SessionItem selected)
        {
            return PlanItemDeletion.Collect(items, selected, Describe);
        }

        internal static int RemoveEmptyGroupParents(IList<SessionItem> items)
        {
            return PlanItemDeletion.RemoveEmptyGroupParents(items, Describe);
        }

        internal static bool HasGroupChildren(IList<SessionItem> items, SessionItem parent)
        {
            return PlanItemDeletion.HasGroupChildren(items, parent, Describe);
        }

        private static PlanItemDeletion.NodeInfo Describe(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }
            bool browserPage = item.Kind == SessionItemKind.WebPage;
            bool browserWindow = !browserPage
                && item.Kind == SessionItemKind.Application
                && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
            bool explorerWindow = !browserPage
                && item.Kind == SessionItemKind.Application
                && string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase);
            return new PlanItemDeletion.NodeInfo
            {
                Id = item.Id,
                ParentId = item.ParentId,
                BrowserGroupKey = item.BrowserGroupKey,
                BrowserWindowIndex = item.BrowserWindowIndex,
                IsBrowserPage = browserPage,
                IsBrowserWindow = browserWindow,
                IsGroupParent = browserWindow || explorerWindow
            };
        }
    }
}
