using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Core;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.StartupRestore.Core;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// Owns the application lifecycle.  Window code only observes this object
    /// and invokes commands, which keeps long-running restore work off the UI
    /// thread and makes the core easy to exercise without creating a window.
    /// </summary>
    // partial：StartupRestore/Core 里有一个薄薄的“自启动应用恢复转接”分部。
    // 代码仍归启动与恢复文件夹，Applications 只调用公开入口。
    public sealed partial class ApplicationController : INotifyPropertyChanged, IDisposable
    {
        /// <summary>
        /// 【2026-10-19 用户要求】把一份「自启动方案」**整份**（连同名字、连同每项的勾选状态）
        /// 复制成「启动与恢复」里的一份**新工作方案**。
        ///
        /// 用户原话：“自启动应用，「加入启动与恢复」（包括方案自带的侧边按钮），效果改成，将该方案（包括名字）
        /// 加入到启动与恢复内（相当于新建方案的方式，要添加方案，用户手动移动合并就可以了），
        /// 不再加入到选定的方案内了（相当于复制粘贴……）。”
        ///
        /// 与 `AddDesktopEntriesToProfile` 的区别：那个是**往已有方案里追加**（还会按路径去重），
        /// 这个是**新建一份**，所以调用方要先把名字顺延好（`MakeUniqueProfileName`）。
        /// 转换规则见 `CreateSessionItemFromApplicationItem`。
        /// </summary>
        /// <summary>
        /// 把一棵自启动方案树里的项目按“自己的项 → 子方案从上到下递归”收集起来。
        /// 自启动方案复制回「启动与恢复」时用；不会把纯文件夹自己当项目。
        /// </summary>
        private List<ApplicationPlanItem> CollectApplicationPlanTreeItems(ApplicationPlan root)
        {
            List<ApplicationPlanItem> result = new List<ApplicationPlanItem>();
            CollectApplicationPlanTreeItems(
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                result);
            return result;
        }

        private void CollectApplicationPlanTreeItems(
            ApplicationPlan plan,
            HashSet<string> visited,
            List<ApplicationPlanItem> result)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.Id) || !visited.Add(plan.Id))
            {
                return;
            }
            if (!plan.IsFolder && plan.Items != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem candidate = plan.Items[index];
                    if (candidate != null
                        && (candidate.IsGroupParent || candidate.IsBrowserWindow || candidate.IsFolderGroup)
                        && !ApplicationPlanItemDeletionAdapter.HasGroupChildren(plan.Items, candidate))
                    {
                        continue;
                    }
                    if (candidate != null)
                    {
                        result.Add(candidate);
                    }
                }
            }
            List<ApplicationPlan> children = PlanFolderTree.ChildrenOf(applicationPlans, plan.Id);
            for (int index = 0; index < children.Count; index++)
            {
                CollectApplicationPlanTreeItems(children[index], visited, result);
            }
        }

        public StartupProfile CreateProfileFromApplicationPlan(ApplicationPlan plan, string name)
        {
            ThrowIfDisposed();
            if (plan == null)
            {
                return null;
            }
            string finalName = string.IsNullOrWhiteSpace(name)
                ? (string.IsNullOrWhiteSpace(plan.Name)
                    ? AppText.T(ShellTextKeys.FallbackStartupPlan)
                    : plan.Name.Trim())
                : name.Trim();
            StartupProfile created = CreateProfile(finalName);
            if (created == null)
            {
                return null;
            }
            List<ApplicationPlanItem> source = CollectApplicationPlanTreeItems(plan);
            List<SessionItem> items = new List<SessionItem>();
            Dictionary<string, SessionItem> browserParents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SavedWindowPlacement> browserPlacements =
                new Dictionary<string, SavedWindowPlacement>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SessionItem> explicitParents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < source.Count; index++)
            {
                ApplicationBrowserPlanAdapter.CollectGroupPlacement(source[index], browserPlacements);
                ApplicationPlanItem item = source[index];
                if (item == null || !item.IsGroupParent)
                {
                    continue;
                }
                SessionItem parent = new SessionItem
                {
                    Id = "app-parent-" + (string.IsNullOrWhiteSpace(item.Id)
                        ? Guid.NewGuid().ToString("N")
                        : item.Id),
                    Kind = item.IsBrowserWindow
                        ? SessionItemKind.Application
                        : item.Kind,
                    DisplayName = item.DisplayName,
                    TargetPath = item.TargetPath,
                    ExecutablePath = item.IsBrowserWindow ? item.TargetPath : null,
                    IsEnabled = item.IsEnabled,
                    WindowTitle = item.WindowTitle,
                    StartTimeUtc = item.StartTimeUtc == default(DateTime)
                        ? DateTime.UtcNow
                        : item.StartTimeUtc,
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
                explicitParents[item.Id] = parent;
            }
            HashSet<string> addedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime now = DateTime.UtcNow;
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
                    if (addedParents.Add(item.Id))
                    {
                        items.Add(explicitParent);
                    }
                    continue;
                }
                if (ApplicationBrowserPlanAdapter.IsBrowserPage(item))
                {
                    if (!string.IsNullOrWhiteSpace(item.ParentId)
                        && explicitParents.TryGetValue(item.ParentId, out explicitParent))
                    {
                        SessionItem page = new SessionItem
                        {
                            Id = "app-page-" + Guid.NewGuid().ToString("N"),
                            Kind = SessionItemKind.WebPage,
                            IsEnabled = item.IsEnabled,
                            DisplayName = item.DisplayName,
                            TargetPath = item.TargetPath,
                            Url = item.TargetPath,
                            StartTimeUtc = now.AddTicks(index),
                            ParentId = explicitParent.Id,
                            BrowserExecutablePath = explicitParent.BrowserExecutablePath,
                            BrowserName = explicitParent.BrowserName,
                            BrowserGroupKey = explicitParent.BrowserGroupKey,
                            BrowserWindowIndex = explicitParent.BrowserWindowIndex,
                            BrowserTabIndex = item.BrowserTabIndex,
                            BrowserTabIdentityCaptured = item.BrowserTabIdentityCaptured,
                            IsActiveBrowserTab = item.IsActiveBrowserTab,
                            BrowserTabs = BrowserTabFingerprint.CloneList(item.BrowserTabs),
                            BrowserWindowTitleSnapshot = item.BrowserWindowTitleSnapshot,
                            BrowserBindingKind = item.BrowserBindingKind,
                            BrowserBindingUpdatedAtUtc = item.BrowserBindingUpdatedAtUtc,
                            WindowPlacement = item.WindowPlacement == null
                                ? null
                                : item.WindowPlacement.Clone(),
                            CaptureSource = "ApplicationPlanBrowser"
                        };
                        items.Add(page);
                        continue;
                    }
                    if (ApplicationBrowserPlanAdapter.TryAppendProfileItem(
                        item,
                        items,
                        browserParents,
                        browserPlacements,
                        now.AddTicks(index)))
                    {
                        continue;
                    }
                }
                SessionItem child = CreateSessionItemFromApplicationItem(item, now.AddTicks(index));
                if (child != null)
                {
                    if (!string.IsNullOrWhiteSpace(item.ParentId)
                        && explicitParents.TryGetValue(item.ParentId, out explicitParent))
                    {
                        child.ParentId = explicitParent.Id;
                    }
                    items.Add(child);
                }
            }
            // 界面列表顺序就是标签顺序：不要信任自启动方案里可能过期的
            // BrowserTabIndex，统一按当前列表重新编号后再写进工作方案。
            BrowserTabOrder.Normalize(items);
            // 网页条目可能因为没路径被跳过；父项若因此变成空壳，不能带进工作方案。
            SessionItemDeletion.RemoveEmptyGroupParents(items);
            created.ReplaceItems(items);
            // 与“新建方案”一致：新方案成为当前方案，last-session 跟着换（不换的话界面上还会显示旧方案）。
            SetActiveProfile(created);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            return created;
        }

        /// <summary>
        /// 把一条自启动项目转成「启动与恢复」的会话条目：新 Id、勾选状态原样带过去。
        ///  · 普通文件夹/程序/文件仍按原来的 DesktopEntry 判定；
        ///  · 带浏览器身份的网页由 ApplicationBrowserPlanAdapter 还原成浏览器父项 + 网页子项，
        ///    这样复制回「启动与恢复」时仍是同一个浏览器窗口里的多个标签；
        ///  · 单独扫描桌面加入的网址仍是 Document（按普通网址打开）。
        /// </summary>
        private static SessionItem CreateSessionItemFromApplicationItem(ApplicationPlanItem source, DateTime startTimeUtc)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.TargetPath))
            {
                return null;
            }
            string path = source.TargetPath.Trim();
            string displayName = string.IsNullOrWhiteSpace(source.DisplayName) ? path : source.DisplayName.Trim();
            if (LooksLikeWebAddress(path))
            {
                return new SessionItem
                {
                    Kind = SessionItemKind.Document,
                    IsEnabled = source.IsEnabled,
                    DisplayName = displayName,
                    TargetPath = path,
                    StartTimeUtc = source.StartTimeUtc == default(DateTime) ? startTimeUtc : source.StartTimeUtc,
                    WindowTitle = source.WindowTitle,
                    CaptureSource = "DesktopSelection"
                };
            }
            DesktopEntry entry = new DesktopEntry();
            entry.Path = path;
            entry.DisplayName = displayName;
            try
            {
                entry.Extension = System.IO.Path.GetExtension(path) ?? string.Empty;
            }
            catch
            {
                entry.Extension = string.Empty;
            }
            SessionItem item = CreateDesktopSessionItem(entry, startTimeUtc);
            item.IsEnabled = source.IsEnabled;
            item.WindowTitle = source.WindowTitle;
            if (source.StartTimeUtc != default(DateTime))
            {
                item.StartTimeUtc = source.StartTimeUtc;
            }
            return item;
        }

        /// <summary>「自启动应用」里允许把网址当启动项（`TargetPath` 存网址）；这里只认这三种写法。</summary>
        internal static bool LooksLikeWebAddress(string path)
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

        /// <summary>
        /// 【2026-10-19 用户要求】反过来：把一份「工作方案」**整份**（连同名字）复制成
        /// 「自启动应用」里的一份**新自启动方案**（“反之如此”：启动与恢复 Ctrl+C → 自启动应用 Ctrl+V）。
        ///
        /// 只搬“能启动的东西”：程序取 `ExecutablePath`，文件夹/文档取 `TargetPath`，网页取 `Url`；
        /// 没有路径的条目（例如没抓到地址的窗口）跳过，数量记在 `LastPlanCopySkippedCount` 里，
        /// 供调用方在状态栏说清楚“跳过了几项”。普通路径重复的只留一条；
        /// 浏览器网页按“窗口 + 标签位置 + URL”保留重复地址，不能把同一窗口里的重复标签吃掉。
        /// </summary>
        public ApplicationPlan CreateApplicationPlanFromProfile(StartupProfile profile, string name)
        {
            ThrowIfDisposed();
            LastPlanCopySkippedCount = 0;
            if (profile == null)
            {
                return null;
            }
            string finalName = string.IsNullOrWhiteSpace(name)
                ? (string.IsNullOrWhiteSpace(profile.Name)
                    ? AppText.T(ShellTextKeys.FallbackWorkProfile)
                    : profile.Name.Trim())
                : name.Trim();
            ApplicationPlan created = CreateApplicationPlan(finalName);
            if (created == null)
            {
                return null;
            }
            List<SessionItem> source = profile.Items ?? new List<SessionItem>();
            List<ApplicationPlanItem> items = new List<ApplicationPlanItem>();
            Dictionary<string, SavedWindowPlacement> browserPlacements =
                new Dictionary<string, SavedWindowPlacement>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < source.Count; index++)
            {
                SessionItem item = source[index];
                if (item == null || item.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                {
                    continue;
                }
                ApplicationPlanItem placement = new ApplicationPlanItem
                {
                    BrowserGroupKey = item.BrowserGroupKey,
                    BrowserWindowIndex = item.BrowserWindowIndex,
                    WindowPlacement = item.WindowPlacement
                };
                ApplicationBrowserPlanAdapter.CollectGroupPlacement(placement, browserPlacements);
            }
            Dictionary<string, string> sourceToAppId =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, ApplicationPlanItem> appById =
                new Dictionary<string, ApplicationPlanItem>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            for (int index = 0; index < source.Count; index++)
            {
                SessionItem sourceItem = source[index];
                ApplicationPlanItem item = CreateApplicationItemFromSessionItem(sourceItem);
                if (item == null)
                {
                    skipped++;
                    continue;
                }
                items.Add(item);
                appById[item.Id] = item;
                if (sourceItem != null && !string.IsNullOrWhiteSpace(sourceItem.Id))
                {
                    sourceToAppId[sourceItem.Id] = item.Id;
                }
            }

            // ParentId 从 SessionItem 的 Id 映射到新自启动项目的 Id。
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem item = items[index];
                if (!string.IsNullOrWhiteSpace(item.ParentId))
                {
                    string mapped;
                    item.ParentId = sourceToAppId.TryGetValue(item.ParentId, out mapped)
                        ? mapped
                        : null;
                }
            }

            // 文件夹父项：有子项挂在自己下面的 ExplorerFolder 才算组父项。
            // 浏览器父项在 CreateApplicationItemFromSessionItem 里已经标记。
            Dictionary<string, ApplicationPlanItem> browserParentByGroup =
                new Dictionary<string, ApplicationPlanItem>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem item = items[index];
                if (item.IsBrowserWindow)
                {
                    browserParentByGroup[ApplicationBrowserPlanAdapter.GroupKey(item)] = item;
                }
            }
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem item = items[index];
                bool hasChildren = items.Exists(delegate(ApplicationPlanItem candidate)
                {
                    return candidate != null
                        && string.Equals(candidate.ParentId, item.Id, StringComparison.OrdinalIgnoreCase);
                });
                if (!item.IsBrowserWindow && hasChildren)
                {
                    item.IsGroupParent = true;
                    bool folderChild = items.Exists(delegate(ApplicationPlanItem candidate)
                    {
                        return candidate != null
                            && string.Equals(candidate.ParentId, item.Id, StringComparison.OrdinalIgnoreCase)
                            && candidate.Kind == SessionItemKind.ExplorerFolder;
                    });
                    item.IsFolderGroup = folderChild
                        || item.Kind == SessionItemKind.ExplorerFolder
                        || (!string.IsNullOrWhiteSpace(item.DisplayName)
                            && item.DisplayName.IndexOf("文件资源管理器", StringComparison.Ordinal) >= 0);
                }
                if (item.IsBrowserPage)
                {
                    ApplicationPlanItem browserParent;
                    if (string.IsNullOrWhiteSpace(item.ParentId)
                        && browserParentByGroup.TryGetValue(
                            ApplicationBrowserPlanAdapter.GroupKey(item),
                            out browserParent))
                    {
                        item.ParentId = browserParent.Id;
                    }
                }
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ApplicationPlanItem> deduped = new List<ApplicationPlanItem>();
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem item = items[index];
                if (item.IsGroupParent || !string.IsNullOrWhiteSpace(item.ParentId))
                {
                    deduped.Add(item);
                    continue;
                }
                string dedupeKey;
                if (ApplicationBrowserPlanAdapter.IsBrowserPage(item))
                {
                    string groupKey = ApplicationBrowserPlanAdapter.GroupKey(item);
                    SavedWindowPlacement groupPlacement;
                    if (item.WindowPlacement == null
                        && browserPlacements.TryGetValue(groupKey, out groupPlacement))
                    {
                        item.WindowPlacement = groupPlacement.Clone();
                    }
                    dedupeKey = "B|" + groupKey + "|" + item.BrowserTabIndex + "|" + item.TargetPath;
                }
                else
                {
                    dedupeKey = "P|" + item.TargetPath;
                }
                if (!seen.Add(dedupeKey))
                {
                    skipped++;
                    continue;
                }
                deduped.Add(item);
            }

            // 来源方案里可能已经留着“0 个子项”的空浏览器/文件夹父项；复制时不要再把它
            // 带进自启动方案，否则恢复时会变成一个空白窗口。
            int prunedEmptyGroupParents = ApplicationPlanItemDeletionAdapter.RemoveEmptyGroupParents(deduped);
            if (prunedEmptyGroupParents > 0)
            {
                AppLog.Write("[自启动应用] 复制工作方案时跳过空父项："
                    + prunedEmptyGroupParents + " 个", null);
            }
            created.Items = deduped;
            created.UpdatedAtUtc = DateTime.UtcNow;
            SetActiveApplicationPlan(created);   // 落盘 + 同步方案镜像文件夹 + 界面刷新
            OnPropertyChanged("ApplicationPlans");
            LastPlanCopySkippedCount = skipped;
            return created;
        }

        /// <summary>上一次“工作方案复制成自启动方案”时被跳过的条目数（没有路径 / 重复路径）。</summary>
        public int LastPlanCopySkippedCount { get; private set; }

        private static ApplicationPlanItem CreateApplicationItemFromSessionItem(SessionItem source)
        {
            if (source == null)
            {
                return null;
            }
            string path = null;
            if (source.Kind == SessionItemKind.Application)
            {
                path = string.IsNullOrWhiteSpace(source.ExecutablePath) ? source.TargetPath : source.ExecutablePath;
            }
            else if (source.Kind == SessionItemKind.ExplorerFolder || source.Kind == SessionItemKind.Document)
            {
                path = string.IsNullOrWhiteSpace(source.TargetPath) ? source.ExecutablePath : source.TargetPath;
            }
            else if (source.Kind == SessionItemKind.WebPage)
            {
                path = string.IsNullOrWhiteSpace(source.Url) ? source.TargetPath : source.Url;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            path = path.Trim();
            string displayName = string.IsNullOrWhiteSpace(source.DisplayName) ? path : source.DisplayName.Trim();
            ApplicationPlanItem created = new ApplicationPlanItem
            {
                DisplayName = displayName,
                TargetPath = path,
                IsEnabled = source.IsEnabled,
                Kind = source.Kind,
                ParentId = source.ParentId,
                WindowTitle = source.WindowTitle,
                StartTimeUtc = source.StartTimeUtc,
                WindowPlacement = source.WindowPlacement == null
                    ? null
                    : source.WindowPlacement.Clone()
            };
            if (source.Kind == SessionItemKind.Application
                && !string.IsNullOrWhiteSpace(source.BrowserGroupKey))
            {
                created.IsGroupParent = true;
                created.IsBrowserWindow = true;
                created.BrowserGroupKey = source.BrowserGroupKey;
                created.BrowserExecutablePath = source.BrowserExecutablePath;
                created.BrowserName = source.BrowserName;
                created.BrowserWindowIndex = source.BrowserWindowIndex;
                created.BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs);
                created.BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot;
                created.BrowserBindingKind = source.BrowserBindingKind;
            }
            if (source.Kind == SessionItemKind.WebPage)
            {
                ApplicationBrowserPlanAdapter.CopyBrowserFields(created, source);
            }
            return created;
        }
        public bool SetProfileItemEnabled(StartupProfile profile, SessionItem item, bool enabled)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || item == null || stored.Items == null)
            {
                return false;
            }

            SessionItem existing = stored.Items.FirstOrDefault(delegate(SessionItem candidate)
            {
                return candidate != null
                    && ((!string.IsNullOrWhiteSpace(item.Id)
                         && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                        || object.ReferenceEquals(candidate, item));
            });
            if (existing == null)
            {
                return false;
            }

            if (existing.IsEnabled == enabled)
            {
                return true;
            }

            existing.IsEnabled = enabled;
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot snapshot = stored.ToSnapshot();
                LastSnapshot = snapshot;
                SaveLastSnapshot(snapshot);
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = enabled
                ? AppText.T(ShellTextKeys.StatusProfileItemEnabled)
                : AppText.T(ShellTextKeys.StatusProfileItemDisabled);
            return true;
        }

        /// <summary>
        /// 启动与恢复行上的“是否最小化”开关。只改保存状态，不操作当前已经打开的窗口：
        ///   · 勾选：记住当前 Normal/Maximized，改成 Minimized；
        ///   · 取消：回到最小化前的状态（旧方案没有记录时回 Normal）。
        /// </summary>
        public bool SetProfileItemWindowMinimized(
            StartupProfile profile,
            SessionItem item,
            bool minimized)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || item == null || stored.Items == null)
            {
                return false;
            }

            SessionItem existing = stored.Items.FirstOrDefault(delegate(SessionItem candidate)
            {
                return candidate != null
                    && ((!string.IsNullOrWhiteSpace(item.Id)
                         && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                        || object.ReferenceEquals(candidate, item));
            });
            if (existing == null)
            {
                return false;
            }

            SavedWindowPlacement placement = existing.WindowPlacement;
            if (placement == null)
            {
                if (!minimized)
                {
                    return true;
                }
                placement = new SavedWindowPlacement { HasBounds = false };
                existing.WindowPlacement = placement;
            }

            if (minimized)
            {
                WindowMinimizeState.SetMinimized(placement, true);
            }
            else
            {
                WindowMinimizeState.SetMinimized(placement, false);
                if (!placement.HasBounds)
                {
                    // 由“最小化”临时创建的无坐标记录，取消后没有可恢复的位置，
                    // 直接移除，保持和用户没设置过时一致。
                    existing.WindowPlacement = null;
                }
            }

            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot snapshot = stored.ToSnapshot();
                LastSnapshot = snapshot;
                SaveLastSnapshot(snapshot);
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = minimized
                ? AppText.T(ShellTextKeys.StatusMinimizedOnRestore)
                : AppText.T(ShellTextKeys.StatusMinimizedOff);
            return true;
        }

    }
}
