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
using WorkspaceRestorer.Infrastructure.PlanMirrors;
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
        /// <summary>读自启动方案清单（构造时调用一次）。</summary>
        private void LoadApplicationPlans()
        {
            ApplicationPlanCatalog catalog = null;
            try
            {
                catalog = applicationPlansStore.Load();
            }
            catch (Exception exception)
            {
                AppLog.Write("自启动方案读取失败，将从空清单开始。", exception);
            }
            if (catalog == null)
            {
                catalog = new ApplicationPlanCatalog();
            }
            if (catalog.Plans == null)
            {
                catalog.Plans = new List<ApplicationPlan>();
            }
            for (int index = catalog.Plans.Count - 1; index >= 0; index--)
            {
                ApplicationPlan plan = catalog.Plans[index];
                if (plan == null)
                {
                    catalog.Plans.RemoveAt(index);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(plan.Id))
                {
                    plan.Id = Guid.NewGuid().ToString("N");
                }
                if (plan.Items == null)
                {
                    plan.Items = new List<ApplicationPlanItem>();
                }
                if (plan.CreatedAtUtc == default(DateTime))
                {
                    plan.CreatedAtUtc = DateTime.UtcNow;
                }
                if (plan.UpdatedAtUtc == default(DateTime))
                {
                    plan.UpdatedAtUtc = plan.CreatedAtUtc;
                }
                // 【2026-10-31 验证拖动时发现】旧版/自检数据里的项目可能没有 StartTimeUtc。
                // DataContractJsonSerializer 在保存时会把 MinValue 转 UTC，直接抛
                // “DateTime 值无法序列化” → 整份自启动方案写不进去（拖动后顺序只停在内存里）。
                // 读入时补成方案创建时间，既保住旧数据，也避免下一次保存失败。
                for (int itemIndex = 0; itemIndex < plan.Items.Count; itemIndex++)
                {
                    ApplicationPlanItem item = plan.Items[itemIndex];
                    if (item != null && item.StartTimeUtc == default(DateTime))
                    {
                        item.StartTimeUtc = plan.CreatedAtUtc;
                    }
                }
                // 旧数据里可能留下“0 个子项”的浏览器/文件夹父项（以前删子项时父项没跟着走）。
                // 空壳没有可恢复内容，继续留着只会在恢复时开一个空白窗口；读入时清掉。
                int prunedEmptyGroupParents = ApplicationPlanItemDeletionAdapter.RemoveEmptyGroupParents(plan.Items);
                if (prunedEmptyGroupParents > 0)
                {
                    AppLog.Write("[自启动应用] 清理空父项：" + prunedEmptyGroupParents
                        + " 个（方案：" + (plan.Name ?? "自启动方案") + "）", null);
                }
            }
            int repairedFolderTree = PlanFolderTree.Normalize(catalog.Plans);
            if (repairedFolderTree > 0)
            {
                AppLog.Write("[自启动应用] 方案文件夹树修复节点：" + repairedFolderTree + " 个", null);
            }
            RecoverApplicationPlanMoveJournal(catalog);
            catalog.SchemaVersion = PlanFolderMigration.CurrentSchemaVersion;
            applicationPlanCatalog = catalog;
            // 用户手动删掉了某份方案的文件夹 → 下次启动把它从清单里去掉（带安全网）。
            IList<string> removedByFolder = ApplicationPlanFolderMirror.RemovePlansWhoseFolderWasDeleted(catalog.Plans);
            for (int removedIndex = 0; removedIndex < removedByFolder.Count; removedIndex++)
            {
                AppLog.Write("按文件夹移除自启动方案：" + removedByFolder[removedIndex], null);
            }
            applicationPlans.Clear();
            for (int index = 0; index < catalog.Plans.Count; index++)
            {
                applicationPlans.Add(catalog.Plans[index]);
            }
        }

        private void PersistApplicationPlans()
        {
            PersistApplicationPlans(true);
        }

        private bool PersistApplicationPlans(bool syncFolders)
        {
            if (applicationPlanCatalog == null)
            {
                applicationPlanCatalog = new ApplicationPlanCatalog();
            }
            PlanFolderTree.Normalize(applicationPlans);
            applicationPlanCatalog.Plans = new List<ApplicationPlan>();
            for (int index = 0; index < applicationPlans.Count; index++)
            {
                if (applicationPlans[index] != null)
                {
                    applicationPlanCatalog.Plans.Add(applicationPlans[index]);
                }
            }
            PlanFolderTree.Normalize(applicationPlanCatalog.Plans);
            applicationPlanCatalog.SchemaVersion = PlanFolderMigration.CurrentSchemaVersion;
            bool saved = true;
            try
            {
                applicationPlansStore.Save(applicationPlanCatalog);
            }
            catch (Exception exception)
            {
                saved = false;
                AppLog.Write("自启动方案保存失败。", exception);
            }
            // 用户要求（2026-10-10 02:41）：“启动方案是可以创建文件夹的，数据要放到
            // …\workflows\自启动应用 内的。” → 每份方案镜像成一个文件夹。
            if (syncFolders)
            {
                ApplicationPlanFolderMirror.Sync(applicationPlans);
            }
            return saved;
        }

        /// <summary>新建一份自启动方案（返回新建的那一份）。</summary>
        public ApplicationPlan CreateApplicationPlan(string name)
        {
            return CreateApplicationPlan(name, null, false);
        }

        public ApplicationPlan CreateApplicationPlan(string name, ApplicationPlan parent, bool isFolder)
        {
            ThrowIfDisposed();
            ApplicationPlan parentStored = parent == null ? null : FindApplicationPlan(parent);
            ApplicationPlan plan = new ApplicationPlan();
            plan.Name = NormalizeApplicationPlanName(name);
            plan.ParentId = parentStored == null ? null : parentStored.Id;
            plan.IsFolder = isFolder;
            applicationPlans.Add(plan);
            PlanFolderTree.Normalize(applicationPlans);
            if (applicationPlanCatalog != null)
            {
                applicationPlanCatalog.ActivePlanId = plan.Id;
            }
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            StatusText = isFolder
                ? AppText.F(ShellTextKeys.StatusPlanFolderCreatedFormat, plan.Name)
                : AppText.F(ShellTextKeys.StatusPlanCreatedFormat, plan.Name);
            return plan;
        }

        public ApplicationPlan CreateApplicationPlanFolder(string name, ApplicationPlan parent)
        {
            return CreateApplicationPlan(name, parent, true);
        }

        public IList<ApplicationPlan> RootApplicationPlans
        {
            get { return PlanFolderTree.Roots(applicationPlans); }
        }

        public IList<ApplicationPlan> ChildApplicationPlans(ApplicationPlan parent)
        {
            return parent == null ? new List<ApplicationPlan>() : PlanFolderTree.ChildrenOf(applicationPlans, parent.Id);
        }

        public bool MoveApplicationPlanInto(ApplicationPlan source, ApplicationPlan target)
        {
            ThrowIfDisposed();
            ApplicationPlan storedSource = FindApplicationPlan(source);
            ApplicationPlan storedTarget = target == null ? null : FindApplicationPlan(target);
            if (storedSource == null || (target != null && storedTarget == null))
            {
                return false;
            }
            if (!PlanFolderTree.CanMove(applicationPlans, storedSource, storedTarget))
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotMoveIntoItself);
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureApplicationPlanMove(storedSource);
            if (!PlanFolderTree.MoveInto(applicationPlans, storedSource, storedTarget))
            {
                return false;
            }
            bool saved = CommitApplicationPlanMove(before, storedSource);
            OnPropertyChanged("ApplicationPlans");
            StatusText = storedTarget == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedToRootFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan))
                : AppText.F(
                    ShellTextKeys.StatusMovedIntoFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan),
                    storedTarget.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan));
            return saved;
        }

        public bool MoveApplicationPlanRelative(ApplicationPlan source, ApplicationPlan target, bool after)
        {
            ThrowIfDisposed();
            ApplicationPlan storedSource = FindApplicationPlan(source);
            ApplicationPlan storedTarget = FindApplicationPlan(target);
            if (storedSource == null || storedTarget == null)
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureApplicationPlanMove(storedSource);
            if (!PlanFolderTree.MoveRelative(applicationPlans, storedSource, storedTarget, after))
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotMoveUnderChild);
                return false;
            }
            bool saved = CommitApplicationPlanMove(before, storedSource);
            OnPropertyChanged("ApplicationPlans");
            StatusText = AppText.T(ShellTextKeys.StatusPlanOrderAdjusted);
            return saved;
        }

        public bool MoveApplicationPlanToRoot(ApplicationPlan source)
        {
            return MoveApplicationPlanInto(source, null);
        }

        public bool MoveApplicationPlanToPlacement(ApplicationPlan plan, string parentId, int siblingIndex)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null)
            {
                return false;
            }
            ApplicationPlan parent = string.IsNullOrWhiteSpace(parentId)
                ? null
                : PlanFolderTree.FindById(applicationPlans, parentId);
            if (!PlanFolderTree.CanMove(applicationPlans, stored, parent))
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureApplicationPlanMove(stored);
            if (!PlanFolderTree.MoveInto(applicationPlans, stored, parent))
            {
                return false;
            }
            if (siblingIndex >= 0)
            {
                PlanFolderTree.PlaceAtSiblingIndex(applicationPlans, stored, siblingIndex);
            }
            bool saved = CommitApplicationPlanMove(before, stored);
            OnPropertyChanged("ApplicationPlans");
            StatusText = parent == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedBackToRootFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan))
                : AppText.F(
                    ShellTextKeys.StatusMovedBackIntoFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan),
                    parent.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan));
            return saved;
        }

        private PlanFolderMoveSnapshot CaptureApplicationPlanMove(ApplicationPlan plan)
        {
            PlanFolderMoveSnapshot snapshot = new PlanFolderMoveSnapshot();
            if (plan == null)
            {
                return snapshot;
            }
            snapshot.PlanId = plan.Id;
            snapshot.ParentId = plan.ParentId;
            snapshot.SiblingIndex = PlanFolderTree.SiblingIndex(applicationPlans, plan);
            snapshot.DirectoryPath = ApplicationPlanFolderMirror.FolderPathFor(plan, applicationPlans);
            return snapshot;
        }

        private bool CommitApplicationPlanMove(PlanFolderMoveSnapshot before, ApplicationPlan plan)
        {
            if (before == null || plan == null)
            {
                return false;
            }
            ApplicationPlan parent = string.IsNullOrWhiteSpace(plan.ParentId)
                ? null
                : PlanFolderTree.FindById(applicationPlans, plan.ParentId);
            string parentDirectory = parent == null
                ? AppPaths.ApplicationPlansFolderRoot
                : ApplicationPlanFolderMirror.FolderPathFor(parent, applicationPlans);
            string destination = string.IsNullOrWhiteSpace(before.DirectoryPath)
                || string.IsNullOrWhiteSpace(parentDirectory)
                ? null
                : PlanFolderDirectoryMover.BuildDestinationPath(parentDirectory, before.DirectoryPath);
            bool sameDirectory = !string.IsNullOrWhiteSpace(destination)
                && string.Equals(
                    Path.GetFullPath(before.DirectoryPath),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase);
            bool journalWritten = false;
            PlanFolderDirectoryMoveResult moveResult = null;
            if (!sameDirectory && !string.IsNullOrWhiteSpace(destination))
            {
                PlanFolderMoveJournalEntry entry = new PlanFolderMoveJournalEntry();
                entry.PlanId = plan.Id;
                entry.OldParentId = before.ParentId;
                entry.NewParentId = plan.ParentId;
                entry.OldDirectory = before.DirectoryPath;
                entry.NewDirectory = destination;
                entry.OldSiblingIndex = before.SiblingIndex;
                entry.NewSiblingIndex = PlanFolderTree.SiblingIndex(applicationPlans, plan);
                entry.RecordedUtc = DateTime.UtcNow;
                try
                {
                    PlanFolderMoveJournal.Append(ApplicationMoveJournalPath, entry);
                    journalWritten = true;
                }
                catch (Exception exception)
                {
                    AppLog.Write("[自启动应用] 移动日志写入失败，本次只更新数据。", exception);
                }
                if (journalWritten)
                {
                    moveResult = PlanFolderDirectoryMover.Move(
                        before.DirectoryPath,
                        destination,
                        plan.Id,
                        ApplicationPlanFolderMirror.DataFileName,
                        ApplicationPlanFolderMirror.ReadPlanIdAt);
                }
            }
            bool saved = PersistApplicationPlans(false);
            string folderToWrite = string.IsNullOrWhiteSpace(destination)
                ? ApplicationPlanFolderMirror.FolderPathFor(plan, applicationPlans)
                : destination;
            if (!string.IsNullOrWhiteSpace(folderToWrite))
            {
                ApplicationPlanFolderMirror.WritePlanAtDirectory(plan, folderToWrite);
            }
            if (saved && journalWritten)
            {
                PlanFolderMoveJournal.Clear(ApplicationMoveJournalPath);
            }
            return saved;
        }

        /// <summary>整棵自启动方案树里可启动（勾选 + 有路径）的项目数。</summary>
        public int CountEnabledApplicationPlanTreeItems(ApplicationPlan root)
        {
            if (root == null)
            {
                return 0;
            }
            int count = 0;
            CountEnabledApplicationPlanTreeItems(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref count);
            return count;
        }

        private void CountEnabledApplicationPlanTreeItems(
            ApplicationPlan plan,
            HashSet<string> visited,
            ref int count)
        {
            if (plan == null || string.IsNullOrWhiteSpace(plan.Id) || !visited.Add(plan.Id))
            {
                return;
            }
            if (!plan.IsFolder && plan.Items != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item != null && item.IsEnabled && !string.IsNullOrWhiteSpace(item.TargetPath))
                    {
                        count++;
                    }
                }
            }
            List<ApplicationPlan> children = PlanFolderTree.ChildrenOf(applicationPlans, plan.Id);
            for (int index = 0; index < children.Count; index++)
            {
                CountEnabledApplicationPlanTreeItems(children[index], visited, ref count);
            }
        }

        /// <summary>这个自启动方案是不是被用户展开过（没记过＝收起）。</summary>
        public bool IsApplicationPlanExpanded(string planId)
        {
            if (string.IsNullOrWhiteSpace(planId) || Settings.ExpandedApplicationPlanIds == null)
            {
                return false;
            }
            for (int index = 0; index < Settings.ExpandedApplicationPlanIds.Count; index++)
            {
                if (string.Equals(Settings.ExpandedApplicationPlanIds[index], planId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>记住/忘掉某个自启动方案的展开状态（落盘，重启后仍然按用户的来）。</summary>
        public void SetApplicationPlanExpanded(string planId, bool expanded)
        {
            if (string.IsNullOrWhiteSpace(planId))
            {
                return;
            }
            if (Settings.ExpandedApplicationPlanIds == null)
            {
                Settings.ExpandedApplicationPlanIds = new List<string>();
            }
            List<string> ids = Settings.ExpandedApplicationPlanIds;
            for (int index = ids.Count - 1; index >= 0; index--)
            {
                if (string.Equals(ids[index], planId, StringComparison.OrdinalIgnoreCase))
                {
                    ids.RemoveAt(index);
                }
            }
            if (expanded)
            {
                ids.Add(planId);
            }
            SaveSettings();
        }

        /// <summary>
        /// 按给定顺序重排自启动方案（卡片拖动调顺序用，2026-10-17）。
        /// 只改清单顺序（`自启动方案.json` 里的 Plans 数组顺序），不动任何方案的内容。
        /// </summary>
        public void SetApplicationPlanOrder(IList<ApplicationPlan> ordered)
        {
            ThrowIfDisposed();
            if (ordered == null || ordered.Count == 0)
            {
                return;
            }
            List<ApplicationPlan> next = new List<ApplicationPlan>();
            for (int index = 0; index < ordered.Count; index++)
            {
                ApplicationPlan plan = FindApplicationPlan(ordered[index]);
                if (plan != null && !next.Contains(plan))
                {
                    next.Add(plan);
                }
            }
            // 漏掉的（理论上不会有）按原顺序补到末尾，绝不因为一次拖动丢方案。
            for (int index = 0; index < applicationPlans.Count; index++)
            {
                if (!next.Contains(applicationPlans[index]))
                {
                    next.Add(applicationPlans[index]);
                }
            }
            if (next.Count != applicationPlans.Count)
            {
                return;
            }
            bool changed = false;
            for (int index = 0; index < next.Count; index++)
            {
                if (!object.ReferenceEquals(applicationPlans[index], next[index]))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
            {
                return;
            }
            applicationPlans.Clear();
            for (int copy = 0; copy < next.Count; copy++)
            {
                applicationPlans.Add(next[copy]);
            }
            PersistApplicationPlans(false);
            OnPropertyChanged("ApplicationPlans");
            System.Text.StringBuilder names = new System.Text.StringBuilder();
            for (int index = 0; index < next.Count; index++)
            {
                if (index > 0)
                {
                    names.Append(" / ");
                }
                names.Append(next[index] == null ? "?" : (next[index].Name ?? "自启动方案"));
            }
            AppLog.Write("[自启动应用] 按拖动结果重排方案顺序：" + names.ToString());
        }

        public void SetActiveApplicationPlan(ApplicationPlan plan)
        {
            ThrowIfDisposed();
            if (plan == null || applicationPlanCatalog == null)
            {
                return;
            }
            applicationPlanCatalog.ActivePlanId = plan.Id;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
        }

        public bool RenameApplicationPlan(ApplicationPlan plan, string name)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null)
            {
                return false;
            }
            stored.Name = NormalizeApplicationPlanName(name);
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            return true;
        }

        public bool DeleteApplicationPlan(ApplicationPlan plan)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null)
            {
                return false;
            }
            List<ApplicationPlan> subtree = PlanFolderTree.Subtree(applicationPlans, stored);
            if (subtree.Count == 0)
            {
                return false;
            }
            string originalParentId = stored.ParentId;
            int originalSiblingIndex = PlanFolderTree.SiblingIndex(applicationPlans, stored);
            string originalPath = PlanFolderTree.PathOf(applicationPlans, stored, " / ");
            // 同「启动与恢复」：移除前先记住镜像文件夹，移除后它会被当成“外部复制进来”的文件夹保留。
            string mirrorFolder = ApplicationPlanFolderMirror.FindFolderFor(stored, applicationPlans);
            bool activeRemoved = false;
            for (int index = 0; index < subtree.Count; index++)
            {
                if (applicationPlanCatalog != null
                    && subtree[index] != null
                    && string.Equals(applicationPlanCatalog.ActivePlanId, subtree[index].Id, StringComparison.OrdinalIgnoreCase))
                {
                    activeRemoved = true;
                    break;
                }
            }

            // 先写回收站，再从列表里移除（顺序反了就找不回来了）。
            if (!AddApplicationPlanTreeToRecycleBin(stored, subtree, originalParentId, originalSiblingIndex, originalPath))
            {
                StatusText = AppText.F(
                    ShellTextKeys.StatusPlanDeleteRecycleFailedFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan));
                return false;
            }
            PlanFolderTree.RemoveSubtree(applicationPlans, stored);
            if (activeRemoved && applicationPlanCatalog != null)
            {
                ApplicationPlan replacement = string.IsNullOrWhiteSpace(originalParentId)
                    ? null
                    : PlanFolderTree.FindById(applicationPlans, originalParentId);
                if (replacement == null)
                {
                    IList<ApplicationPlan> roots = PlanFolderTree.Roots(applicationPlans);
                    replacement = roots.Count > 0 ? roots[0] : null;
                }
                applicationPlanCatalog.ActivePlanId = replacement == null ? null : replacement.Id;
            }
            bool catalogSaved = PersistApplicationPlans(true);
            // 清单先落盘，再删镜像文件夹；删除失败就记下签名，避免下次启动重复询问。
            if (catalogSaved
                && !string.IsNullOrWhiteSpace(mirrorFolder)
                && !ApplicationPlanFolderMirror.DeleteFolderAt(mirrorFolder))
            {
                MirrorIntake.RememberIgnored(AppPaths.ApplicationsKind, mirrorFolder);
            }
            OnPropertyChanged("ApplicationPlans");
            StatusText = AppText.F(
                ShellTextKeys.StatusPlanDeletedFormat,
                stored.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan))
                + (subtree.Count > 1
                    ? AppText.F(ShellTextKeys.StatusPlanDeletedChildrenFormat, subtree.Count - 1)
                    : string.Empty)
                + AppText.F(
                    ShellTextKeys.StatusPlanDeletedRecycleSuffixFormat,
                    RecycleBinRetentionDescription);
            return true;
        }

        public int AddDesktopEntriesToApplicationPlan(ApplicationPlan plan, IList<DesktopEntry> entries)
        {
            return AddDesktopEntriesToApplicationPlan(plan, entries, null, null);
        }

        /// <summary>
        /// 把一批桌面项加进指定的自启动方案，并用调用方给的话做状态栏结论；返回实际新增数量。
        ///
        /// 【2026-10-17 第 50 批】为什么要这个重载：状态栏文字是**控制器统一写**的
        /// （界面上的 statusText 跟着 `StatusText` 走），调用方在返回后自己再写一句会被覆盖掉——
        /// 「浏览…」原来的“已把浏览选中的 N 个项目加入自启动方案…”就是这么被
        /// “已把 N 项加入自启动方案：名字”盖掉的；“重复浏览”那句同样被“这些项目已经在自启动方案里了”盖掉。
        /// 所以两种结论都交给控制器一起写。
        /// </summary>
        public int AddDesktopEntriesToApplicationPlan(
            ApplicationPlan plan,
            IList<DesktopEntry> entries,
            string successStatus,
            string duplicateStatus)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null || entries == null || entries.Count == 0)
            {
                return 0;
            }
            if (stored.IsFolder)
            {
                StatusText = AppText.T(ShellTextKeys.StatusPlanFolderCannotHoldItems);
                return 0;
            }
            int added = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                DesktopEntry entry = entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }
                bool exists = false;
                for (int itemIndex = 0; itemIndex < stored.Items.Count; itemIndex++)
                {
                    ApplicationPlanItem existing = stored.Items[itemIndex];
                    if (existing != null && string.Equals(existing.TargetPath, entry.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                {
                    continue;
                }
                ApplicationPlanItem item = new ApplicationPlanItem();
                item.DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Path : entry.DisplayName;
                item.TargetPath = entry.Path;
                item.IsEnabled = true;
                stored.Items.Add(item);
                added++;
            }
            if (added > 0)
            {
                stored.UpdatedAtUtc = DateTime.UtcNow;
                PersistApplicationPlans();
                OnPropertyChanged("ApplicationPlans");
                StatusText = string.IsNullOrEmpty(successStatus)
                    ? AppText.F(
                        ShellTextKeys.StatusPlanItemsAddedFormat,
                        added,
                        stored.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan))
                    : successStatus;
            }
            else
            {
                StatusText = string.IsNullOrEmpty(duplicateStatus)
                    ? AppText.T(ShellTextKeys.StatusPlanItemsDuplicate)
                    : duplicateStatus;
            }
            return added;
        }

        /// <summary>
        /// 从自启动方案里删除一个小项。
        ///
        /// 删除规则与「启动与恢复」共用 <see cref="PlanItemDeletion"/>：
        ///   · 父项的最后一个子项（网页、文件夹、普通文件都算）→ 空父项一起删，继续向上；
        ///   · 浏览器父项 → 该窗口全部网页一起删；
        ///   · 文件夹父项 → 全部后代一起删。
        /// </summary>
        public bool RemoveApplicationPlanItem(ApplicationPlan plan, ApplicationPlanItem item)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null || item == null || stored.Items == null)
            {
                return false;
            }
            ApplicationPlanItem existing = null;
            for (int index = 0; index < stored.Items.Count; index++)
            {
                ApplicationPlanItem candidate = stored.Items[index];
                if (candidate != null
                    && ((!string.IsNullOrWhiteSpace(item.Id)
                         && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                        || object.ReferenceEquals(candidate, item)))
                {
                    existing = candidate;
                    break;
                }
            }
            if (existing == null)
            {
                return false;
            }

            List<ApplicationPlanItem> removed = ApplicationPlanItemDeletionAdapter.Collect(stored.Items, existing);
            if (removed.Count == 0)
            {
                return false;
            }
            for (int index = stored.Items.Count - 1; index >= 0; index--)
            {
                ApplicationPlanItem candidate = stored.Items[index];
                if (candidate != null && removed.Contains(candidate))
                {
                    stored.Items.RemoveAt(index);
                }
            }
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            return true;
        }

        public void SetApplicationPlanItemsOrder(ApplicationPlan plan, IList<ApplicationPlanItem> ordered)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null || ordered == null)
            {
                return;
            }
            stored.Items = new List<ApplicationPlanItem>(ordered);
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
        }

        /// <summary>
        /// 自启动应用行里的“恢复时最小化”开关。
        /// 与启动与恢复共用 WindowMinimizeState：开启记住原状态，取消恢复原状态。
        /// </summary>
        public bool SetApplicationPlanItemWindowMinimized(
            ApplicationPlan plan,
            ApplicationPlanItem item,
            bool minimized)
        {
            ThrowIfDisposed();
            ApplicationPlan stored = FindApplicationPlan(plan);
            if (stored == null || item == null || stored.Items == null)
            {
                return false;
            }
            ApplicationPlanItem existing = null;
            for (int index = 0; index < stored.Items.Count; index++)
            {
                ApplicationPlanItem candidate = stored.Items[index];
                if (candidate != null
                    && ((!string.IsNullOrWhiteSpace(item.Id)
                         && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                        || object.ReferenceEquals(candidate, item)))
                {
                    existing = candidate;
                    break;
                }
            }
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
            WindowMinimizeState.SetMinimized(placement, minimized);
            if (!minimized && !placement.HasBounds)
            {
                existing.WindowPlacement = null;
            }
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            StatusText = minimized
                ? AppText.T(ShellTextKeys.StatusMinimizedOnStartup)
                : AppText.T(ShellTextKeys.StatusMinimizedOnStartupOff);
            return true;
        }

        /// <summary>
        /// 用一份快照**整体替换**自启动方案（公共撤销栈用：撤销/重做「自启动应用」里的一步，
        /// 2026-10-17 用户要求“快捷键作用于全局，自启动应用也要能撤销”）。
        ///
        /// 为什么用“整体快照”而不是每个操作各写一个反动作：自启动应用会改数据的操作很多
        /// （增删项目、拖动排序、跨方案搬移、启用停用、改名、新建、删除、并入），
        /// 每个都手写反动作极易漏、且漏了用户看到的就是“Ctrl+Z 没反应”。
        /// 快照式只有一条路径，永远不会漏。
        ///
        /// 调用方必须传**副本**（撤销栈里存的快照不能被后续编辑改到），这里会直接接管这些对象。
        /// </summary>
        public void ApplyApplicationPlansSnapshot(IList<ApplicationPlan> plans, string activePlanId)
        {
            ThrowIfDisposed();
            if (applicationPlanCatalog == null)
            {
                applicationPlanCatalog = new ApplicationPlanCatalog();
            }
            applicationPlans.Clear();
            if (plans != null)
            {
                for (int index = 0; index < plans.Count; index++)
                {
                    ApplicationPlan plan = plans[index];
                    if (plan == null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(plan.Id))
                    {
                        plan.Id = Guid.NewGuid().ToString("N");
                    }
                    if (plan.Items == null)
                    {
                        plan.Items = new List<ApplicationPlanItem>();
                    }
                    for (int itemIndex = 0; itemIndex < plan.Items.Count; itemIndex++)
                    {
                        if (plan.Items[itemIndex] == null)
                        {
                            plan.Items.RemoveAt(itemIndex);
                            itemIndex--;
                        }
                    }
                    applicationPlans.Add(plan);
                }
            }
            PlanFolderTree.Normalize(applicationPlans);
            applicationPlanCatalog.ActivePlanId = activePlanId;
            if (string.IsNullOrWhiteSpace(applicationPlanCatalog.ActivePlanId) && applicationPlans.Count > 0)
            {
                applicationPlanCatalog.ActivePlanId = applicationPlans[0].Id;
            }
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            AppLog.Write("[自启动应用] 按快照还原：方案 " + applicationPlans.Count + " 份，当前方案 Id="
                + (applicationPlanCatalog.ActivePlanId ?? "(无)"));
        }

        /// <summary>
        /// 用户第 89 条：「自启动应用」也要能像「启动与恢复」那样，
        /// 把 a 方案的某一个启动项拖到 b 方案里（a 里就没有了）。
        /// `insertAfter` 决定插在目标项之后还是之前（target 为 null＝追加到末尾）。
        /// </summary>
        public bool MoveApplicationItemBetweenPlans(ApplicationPlan from, ApplicationPlan to, ApplicationPlanItem item, ApplicationPlanItem target, bool insertAfter)
        {
            ThrowIfDisposed();
            ApplicationPlan source = FindApplicationPlan(from);
            ApplicationPlan destination = FindApplicationPlan(to);
            if (source == null || destination == null || item == null || object.ReferenceEquals(source, destination))
            {
                return false;
            }
            List<ApplicationPlanItem> sourceItems = new List<ApplicationPlanItem>(source.Items);
            int index = sourceItems.IndexOf(item);
            if (index < 0)
            {
                return false;
            }
            List<ApplicationPlanItem> destinationItems = new List<ApplicationPlanItem>(destination.Items);
            // 【2026-11-01】整组搬运规则只有一份（StartupRestore/Core/PlanItemGroupMove）；
            // 这里只做 ApplicationPlanItem 的翻译。
            //   · 拖浏览器窗口/文件夹父行＝父项 + 全部子项成块搬过去，顺序保持；
            //   · 拖子项＝拒绝（用户要求：子项只能整组走）；
            //   · 目标里已有同路径项＝整组拒绝，不重复放。
            List<ApplicationPlanItem> newSource;
            List<ApplicationPlanItem> newDestination;
            if (!ApplicationPlanItemMoveAdapter.TryMove(
                sourceItems,
                destinationItems,
                item,
                target,
                insertAfter,
                out newSource,
                out newDestination))
            {
                return false;
            }
            destination.Items = newDestination;
            destination.UpdatedAtUtc = DateTime.UtcNow;
            source.Items = newSource;
            source.UpdatedAtUtc = DateTime.UtcNow;
            // 整组一次落盘：不在子项循环里逐项写。
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            return true;
        }

        /// <summary>把一份自启动方案里的全部项目并进另一份（拖动方案卡片到另一张卡片上）。返回并入的项数。</summary>
        public int MergeApplicationPlanInto(ApplicationPlan source, ApplicationPlan target)
        {
            ThrowIfDisposed();
            ApplicationPlan from = FindApplicationPlan(source);
            ApplicationPlan into = FindApplicationPlan(target);
            if (from == null || into == null || object.ReferenceEquals(from, into))
            {
                return 0;
            }
            List<ApplicationPlanItem> destinationItems = new List<ApplicationPlanItem>(into.Items);
            List<ApplicationPlanItem> sourceItems = new List<ApplicationPlanItem>(from.Items);
            int added = 0;
            for (int index = 0; index < sourceItems.Count; index++)
            {
                ApplicationPlanItem item = sourceItems[index];
                if (item == null)
                {
                    continue;
                }
                string candidatePath = string.IsNullOrWhiteSpace(item.TargetPath) ? string.Empty : item.TargetPath.Trim();
                bool duplicate = false;
                for (int scan = 0; scan < destinationItems.Count; scan++)
                {
                    ApplicationPlanItem existing = destinationItems[scan];
                    if (existing == null)
                    {
                        continue;
                    }
                    string path = string.IsNullOrWhiteSpace(existing.TargetPath) ? string.Empty : existing.TargetPath.Trim();
                    if (candidatePath.Length > 0 && string.Equals(path, candidatePath, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    continue;
                }
                destinationItems.Add(item);
                added++;
            }
            into.Items = destinationItems;
            into.UpdatedAtUtc = DateTime.UtcNow;
            PersistApplicationPlans();
            OnPropertyChanged("ApplicationPlans");
            return added;
        }

        public ApplicationPlan FindApplicationPlan(ApplicationPlan plan)
        {
            if (plan == null)
            {
                return null;
            }
            for (int index = 0; index < applicationPlans.Count; index++)
            {
                ApplicationPlan candidate = applicationPlans[index];
                if (candidate == null)
                {
                    continue;
                }
                if (object.ReferenceEquals(candidate, plan)
                    || (!string.IsNullOrWhiteSpace(plan.Id)
                        && string.Equals(candidate.Id, plan.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string NormalizeApplicationPlanName(string name)
        {
            string text = name == null ? string.Empty : name.Trim();
            return text.Length == 0 ? AppText.T(ShellTextKeys.FallbackStartupPlan) : text;
        }

        /// <summary>
        /// 立刻启动一个自启动方案里的项目（供「自启动应用 → 立即恢复」用）。
        /// 复用「自启动应用」自己的服务（`DesktopApplicationService.Launch`），
        /// 它内部走统一的进程启动器，因此同样受测试模式的“禁止外部副作用”保护。
        /// </summary>
        public LaunchResult LaunchApplicationPlanItem(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = AppText.T(ShellTextKeys.ErrorPathEmpty)
                };
            }
            return LaunchApplicationPlanItemCore(targetPath, null, null);
        }

        /// <summary>
        /// 启动自启动方案里的一个项目，并把该项保存的窗口状态一起交给启动器。
        /// 这样“恢复时最小化”不只对启动与恢复生效。
        /// </summary>
        public LaunchResult LaunchApplicationPlanItem(ApplicationPlanItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = AppText.T(ShellTextKeys.ErrorPathEmpty)
                };
            }
            return LaunchApplicationPlanItemCore(
                item.TargetPath, item.WindowPlacement, item.DisplayName);
        }

        private LaunchResult LaunchApplicationPlanItemCore(
            string targetPath,
            SavedWindowPlacement placement,
            string windowTitle)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = AppText.T(ShellTextKeys.ErrorPathEmpty)
                };
            }
            if (desktopApplicationService == null)
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = AppText.T(ShellTextKeys.ErrorLaunchServiceUnavailable)
                };
            }
            DesktopEntry entry = new DesktopEntry();
            entry.Path = targetPath;
            entry.DisplayName = targetPath;
            entry.WindowTitle = string.IsNullOrWhiteSpace(windowTitle) ? targetPath : windowTitle;
            entry.WindowPlacement = placement;
            return desktopApplicationService.Launch(entry);
        }

        private void RecoverApplicationPlanMoveJournal(ApplicationPlanCatalog catalog)
        {
            if (catalog == null || catalog.Plans == null)
            {
                return;
            }
            string path = ApplicationMoveJournalPath;
            PlanFolderMoveJournalData journal = PlanFolderMoveJournal.Load(path);
            if (journal.Entries == null || journal.Entries.Count == 0)
            {
                return;
            }
            int applied = PlanFolderMoveJournal.Apply(
                catalog.Plans,
                journal.Entries,
                delegate(ApplicationPlan plan) { return plan == null ? null : plan.Id; });
            if (applied > 0)
            {
                try
                {
                    applicationPlansStore.Save(catalog);
                }
                catch (Exception exception)
                {
                    AppLog.Write("[方案移动] 启动修复时保存自启动方案总清单失败，保留移动日志。", exception);
                    return;
                }
            }
            PlanFolderMoveJournal.Clear(path);
            AppLog.Write("[方案移动] 启动修复自启动方案移动日志 " + applied + " 条。");
        }

    }
}
