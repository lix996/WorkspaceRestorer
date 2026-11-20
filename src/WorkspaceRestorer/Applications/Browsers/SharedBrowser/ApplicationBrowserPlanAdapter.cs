using System;
using System.Collections.Generic;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Applications.Browsers
{
    /// <summary>
    /// 自启动方案里的网页与「启动与恢复」SessionItem 之间的转换。
    /// 只处理浏览器网页的窗口/标签身份，不复制捕获、恢复、缩窗逻辑。
    /// </summary>
    internal static class ApplicationBrowserPlanAdapter
    {
        internal static bool IsBrowserPage(ApplicationPlanItem item)
        {
            return item != null && item.IsBrowserPage;
        }

        internal static void CopyBrowserFields(ApplicationPlanItem target, SessionItem source)
        {
            if (target == null || source == null)
            {
                return;
            }
            target.BrowserGroupKey = source.BrowserGroupKey;
            target.BrowserExecutablePath = source.BrowserExecutablePath;
            target.BrowserName = source.BrowserName;
            target.BrowserWindowIndex = source.BrowserWindowIndex;
            target.BrowserTabIndex = source.BrowserTabIndex;
            target.BrowserTabIdentityCaptured = source.BrowserTabIdentityCaptured;
            target.IsActiveBrowserTab = source.IsActiveBrowserTab;
            target.BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs);
            target.BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot;
            target.BrowserBindingKind = source.BrowserBindingKind;
            target.BrowserBindingUpdatedAtUtc = source.BrowserBindingUpdatedAtUtc;
            target.IsBrowserPage = true;
            target.WindowTitle = source.WindowTitle;
            target.StartTimeUtc = source.StartTimeUtc;
        }

        internal static string GroupKey(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }
            string group = item.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(group))
            {
                group = item.BrowserExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(group))
            {
                group = "browser";
            }
            return group + "|" + item.BrowserWindowIndex;
        }

        internal static void CollectGroupPlacement(
            ApplicationPlanItem item,
            IDictionary<string, SavedWindowPlacement> placements)
        {
            if (item == null || placements == null || item.WindowPlacement == null)
            {
                return;
            }
            string key = GroupKey(item);
            if (!placements.ContainsKey(key))
            {
                placements[key] = item.WindowPlacement.Clone();
            }
        }

        /// <summary>
        /// 把一条自启动项目追加成工作方案条目。返回 true 表示是浏览器网页，
        /// 调用方不要再按普通 Document 再处理一次。
        /// </summary>
        internal static bool TryAppendProfileItem(
            ApplicationPlanItem source,
            List<SessionItem> target,
            IDictionary<string, SessionItem> parents,
            IDictionary<string, SavedWindowPlacement> placements,
            DateTime startTimeUtc)
        {
            if (!IsBrowserPage(source) || target == null)
            {
                return false;
            }
            string key = GroupKey(source);
            SessionItem parent;
            if (parents == null || !parents.TryGetValue(key, out parent))
            {
                string executable = string.IsNullOrWhiteSpace(source.BrowserExecutablePath)
                    ? source.BrowserGroupKey
                    : source.BrowserExecutablePath;
                string browserName = source.BrowserName;
                if (string.IsNullOrWhiteSpace(browserName) && !string.IsNullOrWhiteSpace(executable))
                {
                    browserName = BrowserCatalog.GetDisplayName(executable, null);
                }
                if (string.IsNullOrWhiteSpace(browserName))
                {
                    browserName = "浏览器";
                }
                int windowIndex = source.BrowserWindowIndex;
                parent = new SessionItem
                {
                    Id = "app-browser-" + Guid.NewGuid().ToString("N"),
                    Kind = SessionItemKind.Application,
                    DisplayName = browserName + (windowIndex >= 0 ? " (" + (windowIndex + 1) + ")" : string.Empty),
                    ExecutablePath = executable,
                    BrowserExecutablePath = executable,
                    BrowserName = browserName,
                    BrowserGroupKey = source.BrowserGroupKey,
                    BrowserWindowIndex = windowIndex,
                    WindowTitle = source.BrowserWindowTitleSnapshot,
                    BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs),
                    BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot,
                    BrowserBindingKind = source.BrowserBindingKind,
                    WindowPlacement = source.WindowPlacement == null
                        ? null
                        : source.WindowPlacement.Clone(),
                    StartsBrowserWindow = true,
                    IsEnabled = source.IsEnabled,
                    CaptureSource = "ApplicationPlanBrowser"
                };
                SavedWindowPlacement groupPlacement;
                if (placements != null && placements.TryGetValue(key, out groupPlacement)
                    && groupPlacement != null)
                {
                    parent.WindowPlacement = groupPlacement.Clone();
                }
                target.Add(parent);
                if (parents != null)
                {
                    parents[key] = parent;
                }
            }

            SessionItem page = new SessionItem
            {
                Id = "app-page-" + Guid.NewGuid().ToString("N"),
                Kind = SessionItemKind.WebPage,
                IsEnabled = source.IsEnabled,
                DisplayName = source.DisplayName,
                TargetPath = source.TargetPath,
                Url = source.TargetPath,
                StartTimeUtc = startTimeUtc,
                ParentId = parent.Id,
                BrowserExecutablePath = parent.BrowserExecutablePath,
                BrowserName = parent.BrowserName,
                BrowserGroupKey = parent.BrowserGroupKey,
                BrowserWindowIndex = parent.BrowserWindowIndex,
                BrowserTabIndex = source.BrowserTabIndex,
                BrowserTabIdentityCaptured = source.BrowserTabIdentityCaptured,
                IsActiveBrowserTab = source.IsActiveBrowserTab,
                BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs),
                BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot,
                BrowserBindingKind = source.BrowserBindingKind,
                BrowserBindingUpdatedAtUtc = source.BrowserBindingUpdatedAtUtc,
                WindowPlacement = source.WindowPlacement == null
                    ? null
                    : source.WindowPlacement.Clone(),
                CaptureSource = "ApplicationPlanBrowser"
            };
            target.Add(page);
            return true;
        }
    }
}
