using System;
using System.Collections.Generic;
using System.IO;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Applications.Restore
{
    /// <summary>
    /// 自启动方案 → 恢复内核认识的会话快照。
    ///
    /// 这个适配器只做“翻译”，不创建工作方案、不落盘、不切换当前方案。
    /// 它复用了 ApplicationBrowserPlanAdapter 的浏览器父子规则，并在最后把
    /// 列表顺序归一成 BrowserTabIndex，保证界面顺序就是恢复顺序。
    /// </summary>
    public static class ApplicationPlanRestoreAdapter
    {
        public static SessionSnapshot BuildSnapshot(ApplicationPlan plan)
        {
            return BuildSnapshot(plan, null);
        }

        /// <summary>
        /// 把一棵自启动方案树拍平：当前方案自己的项目 → 子方案从上到下 →
        /// 每个子方案先自己的项目，再递归。allPlans 为空时保持原来的单方案行为。
        /// </summary>
        public static SessionSnapshot BuildSnapshot(ApplicationPlan plan, IList<ApplicationPlan> allPlans)
        {
            if (plan != null && allPlans != null)
            {
                plan = BuildCombinedPlan(plan, allPlans);
            }
            SessionSnapshot snapshot = new SessionSnapshot();
            snapshot.CapturedAtUtc = DateTime.UtcNow;
            snapshot.Items = new List<SessionItem>();
            if (plan == null || plan.Items == null || plan.Items.Count == 0)
            {
                return snapshot;
            }

            // 空浏览器/文件夹父项不再进入恢复清单：它没有子项可恢复，继续当普通
            // Application 传下去会被启动成一个空白窗口。
            List<ApplicationPlanItem> source = new List<ApplicationPlanItem>();
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem candidate = plan.Items[index];
                if (candidate != null
                    && (candidate.IsGroupParent || candidate.IsBrowserWindow || candidate.IsFolderGroup)
                    && !ApplicationPlanItemDeletionAdapter.HasGroupChildren(plan.Items, candidate))
                {
                    continue;
                }
                source.Add(candidate);
            }
            Dictionary<string, SessionItem> explicitParents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SessionItem> browserParents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SavedWindowPlacement> groupPlacements =
                new Dictionary<string, SavedWindowPlacement>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, bool> addedParents =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            // 先建立父项。父项只作为恢复时的“窗口归属”，
            // SessionRestoreService 会过滤掉有子项的父行，不会另外启动空窗口。
            for (int index = 0; index < source.Count; index++)
            {
                ApplicationPlanItem item = source[index];
                if (item == null)
                {
                    continue;
                }
                ApplicationBrowserPlanAdapter.CollectGroupPlacement(item, groupPlacements);
                if (!item.IsGroupParent)
                {
                    continue;
                }
                string parentId = string.IsNullOrWhiteSpace(item.Id)
                    ? Guid.NewGuid().ToString("N")
                    : item.Id;
                SessionItem parent = new SessionItem
                {
                    Id = "app-parent-" + parentId,
                    Kind = item.IsBrowserWindow ? SessionItemKind.Application : item.Kind,
                    IsEnabled = item.IsEnabled,
                    DisplayName = item.DisplayName,
                    TargetPath = item.TargetPath,
                    ExecutablePath = item.IsBrowserWindow ? item.TargetPath : null,
                    WindowTitle = item.WindowTitle,
                    StartTimeUtc = item.StartTimeUtc == default(DateTime) ? DateTime.UtcNow : item.StartTimeUtc,
                    BrowserExecutablePath = item.BrowserExecutablePath,
                    BrowserName = item.BrowserName,
                    BrowserGroupKey = item.BrowserGroupKey,
                    BrowserWindowIndex = item.BrowserWindowIndex,
                    BrowserTabs = BrowserTabFingerprint.CloneList(item.BrowserTabs),
                    BrowserWindowTitleSnapshot = item.BrowserWindowTitleSnapshot,
                    BrowserBindingKind = item.BrowserBindingKind,
                    StartsBrowserWindow = item.IsBrowserWindow,
                    WindowPlacement = item.WindowPlacement == null
                        ? null
                        : item.WindowPlacement.Clone(),
                    CaptureSource = "ApplicationPlanGroup"
                };
                if (item.IsBrowserWindow)
                {
                    if (string.IsNullOrWhiteSpace(parent.BrowserExecutablePath))
                    {
                        parent.BrowserExecutablePath = item.TargetPath;
                    }
                    if (string.IsNullOrWhiteSpace(parent.ExecutablePath))
                    {
                        parent.ExecutablePath = parent.BrowserExecutablePath;
                    }
                }
                explicitParents[item.Id] = parent;
            }

            // 再按原列表顺序生成条目：父项、网页、普通应用/文件夹/文件。
            for (int index = 0; index < source.Count; index++)
            {
                ApplicationPlanItem item = source[index];
                if (item == null)
                {
                    continue;
                }

                SessionItem explicitParent;
                if (item.IsGroupParent
                    && explicitParents.TryGetValue(item.Id, out explicitParent))
                {
                    if (!addedParents.ContainsKey(item.Id))
                    {
                        addedParents[item.Id] = true;
                        snapshot.Items.Add(explicitParent);
                    }
                    continue;
                }

                if (ApplicationBrowserPlanAdapter.IsBrowserPage(item))
                {
                    SessionItem parent = null;
                    if (!string.IsNullOrWhiteSpace(item.ParentId))
                    {
                        explicitParents.TryGetValue(item.ParentId, out parent);
                    }
                    if (parent != null)
                    {
                        snapshot.Items.Add(CreateBrowserPage(item, parent, index));
                        continue;
                    }

                    // 旧方案没有显式父项时，仍让共享浏览器适配器按
                    // BrowserGroupKey + BrowserWindowIndex 合成父项。
                    if (ApplicationBrowserPlanAdapter.TryAppendProfileItem(
                        item,
                        snapshot.Items,
                        browserParents,
                        groupPlacements,
                        DateTime.UtcNow.AddTicks(index)))
                    {
                        continue;
                    }
                }

                SessionItem child = CreateSessionItem(item, index);
                if (child == null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(item.ParentId)
                    && explicitParents.TryGetValue(item.ParentId, out explicitParent))
                {
                    child.ParentId = explicitParent.Id;
                }
                snapshot.Items.Add(child);
            }

            ApplyParentPlacements(snapshot.Items);
            BrowserTabOrder.Normalize(snapshot.Items);
            for (int index = 0; index < snapshot.Items.Count; index++)
            {
                SessionItem item = snapshot.Items[index];
                if (item != null)
                {
                    item.Sequence = index + 1;
                }
            }
            return snapshot;
        }

        private static SessionItem CreateBrowserPage(
            ApplicationPlanItem source,
            SessionItem parent,
            int index)
        {
            return new SessionItem
            {
                Kind = SessionItemKind.WebPage,
                IsEnabled = source.IsEnabled,
                DisplayName = source.DisplayName,
                TargetPath = source.TargetPath,
                Url = source.TargetPath,
                WindowTitle = source.WindowTitle,
                StartTimeUtc = source.StartTimeUtc == default(DateTime)
                    ? DateTime.UtcNow.AddTicks(index)
                    : source.StartTimeUtc,
                ParentId = parent.Id,
                BrowserExecutablePath = FirstNonEmpty(parent.BrowserExecutablePath, parent.ExecutablePath, source.BrowserExecutablePath),
                BrowserName = FirstNonEmpty(parent.BrowserName, source.BrowserName),
                BrowserGroupKey = FirstNonEmpty(parent.BrowserGroupKey, source.BrowserGroupKey),
                BrowserWindowIndex = parent.BrowserWindowIndex >= 0
                    ? parent.BrowserWindowIndex
                    : source.BrowserWindowIndex,
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
        }

        private static SessionItem CreateSessionItem(ApplicationPlanItem source, int index)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.TargetPath))
            {
                return null;
            }
            string path = source.TargetPath.Trim();
            string displayName = string.IsNullOrWhiteSpace(source.DisplayName)
                ? path
                : source.DisplayName.Trim();
            DateTime startTimeUtc = source.StartTimeUtc == default(DateTime)
                ? DateTime.UtcNow.AddTicks(index)
                : source.StartTimeUtc;

            if (LooksLikeWebAddress(path))
            {
                return new SessionItem
                {
                    Kind = SessionItemKind.Document,
                    IsEnabled = source.IsEnabled,
                    DisplayName = displayName,
                    TargetPath = path,
                    WindowTitle = source.WindowTitle,
                    StartTimeUtc = startTimeUtc,
                    WindowPlacement = source.WindowPlacement == null
                        ? null
                        : source.WindowPlacement.Clone(),
                    CaptureSource = "ApplicationPlan"
                };
            }

            string expanded = path;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(path);
            }
            catch
            {
            }
            bool directory = false;
            try
            {
                directory = Directory.Exists(expanded);
            }
            catch
            {
            }
            if (directory)
            {
                return new SessionItem
                {
                    Kind = SessionItemKind.ExplorerFolder,
                    IsEnabled = source.IsEnabled,
                    DisplayName = displayName,
                    TargetPath = path,
                    ExecutablePath = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe"),
                    WindowTitle = source.WindowTitle,
                    StartTimeUtc = startTimeUtc,
                    WindowPlacement = source.WindowPlacement == null
                        ? null
                        : source.WindowPlacement.Clone(),
                    CaptureSource = "ApplicationPlan"
                };
            }

            string extension = string.Empty;
            try
            {
                extension = Path.GetExtension(path) ?? string.Empty;
            }
            catch
            {
            }
            bool executable = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
            return new SessionItem
            {
                Kind = executable ? SessionItemKind.Application : SessionItemKind.Document,
                IsEnabled = source.IsEnabled,
                DisplayName = displayName,
                ExecutablePath = executable ? path : null,
                TargetPath = executable ? null : path,
                WindowTitle = source.WindowTitle,
                WorkingDirectory = GetParentDirectory(path),
                StartTimeUtc = startTimeUtc,
                WindowPlacement = source.WindowPlacement == null
                    ? null
                    : source.WindowPlacement.Clone(),
                CaptureSource = "ApplicationPlan"
            };
        }

        /// <summary>
        /// “最小化”属于窗口，不属于页面内容：父行有状态时，子项一律跟父行走。
        /// 这与启动与恢复的 ApplyParentWindowPlacements 是同一条规则。
        /// </summary>
        private static void ApplyParentPlacements(IList<SessionItem> items)
        {
            if (items == null)
            {
                return;
            }
            Dictionary<string, SessionItem> parents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate != null
                    && candidate.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(candidate.Id))
                {
                    parents[candidate.Id] = candidate;
                }
            }
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item == null
                    || (item.Kind != SessionItemKind.WebPage
                        && item.Kind != SessionItemKind.ExplorerFolder)
                    || string.IsNullOrWhiteSpace(item.ParentId))
                {
                    continue;
                }
                SessionItem parent;
                if (parents.TryGetValue(item.ParentId, out parent)
                    && parent != null
                    && parent.WindowPlacement != null)
                {
                    item.WindowPlacement = parent.WindowPlacement;
                }
            }
        }

        private static ApplicationPlan BuildCombinedPlan(ApplicationPlan root, IList<ApplicationPlan> allPlans)
        {
            ApplicationPlan combined = new ApplicationPlan();
            combined.Id = root.Id;
            combined.Name = root.Name;
            combined.Items = new List<ApplicationPlanItem>();
            AppendPlanItems(root, allPlans, combined.Items, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return combined;
        }

        private static void AppendPlanItems(
            ApplicationPlan plan,
            IList<ApplicationPlan> allPlans,
            List<ApplicationPlanItem> destination,
            HashSet<string> visited)
        {
            if (plan == null || destination == null || string.IsNullOrWhiteSpace(plan.Id) || !visited.Add(plan.Id))
            {
                return;
            }
            if (!plan.IsFolder && plan.Items != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    if (plan.Items[index] != null)
                    {
                        destination.Add(plan.Items[index]);
                    }
                }
            }
            List<ApplicationPlan> children = PlanFolderTree.ChildrenOf(allPlans, plan.Id);
            for (int index = 0; index < children.Count; index++)
            {
                AppendPlanItems(children[index], allPlans, destination, visited);
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }
            for (int index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrWhiteSpace(values[index]))
                {
                    return values[index];
                }
            }
            return null;
        }

        private static string GetParentDirectory(string path)
        {
            try
            {
                return Path.GetDirectoryName(path);
            }
            catch
            {
                return null;
            }
        }

        private static bool LooksLikeWebAddress(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            string trimmed = path.Trim();
            return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase);
        }
    }
}
