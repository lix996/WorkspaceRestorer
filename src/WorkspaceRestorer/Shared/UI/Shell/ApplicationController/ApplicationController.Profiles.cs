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
        public StartupProfile CreateProfile(string name)
        {
            return CreateProfile(name, null, false);
        }

        /// <summary>
        /// 新建一份工作方案或纯文件夹。parent 为空 = 根级；parent 不为空 = 放进它里面。
        /// 纯文件夹没有自己的启动项，只用来装子方案。
        /// </summary>
        public StartupProfile CreateProfile(string name, StartupProfile parent, bool isFolder)
        {
            ThrowIfDisposed();
            StartupProfile parentStored = parent == null ? null : FindProfile(parent);
            StartupProfile profile = new StartupProfile();
            profile.Name = NormalizeProfileName(name);
            profile.ParentId = parentStored == null ? null : parentStored.Id;
            profile.IsFolder = isFolder;
            startupProfiles.Add(profile);
            PlanFolderTree.Normalize(startupProfiles);
            profileCatalog.ActiveProfileId = profile.Id;
            RememberActiveProfileId();
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            OnPropertyChanged("ActiveProfile");
            StatusText = isFolder
                ? AppText.F(ShellTextKeys.StatusProfileFolderCreatedFormat, profile.Name)
                : AppText.F(ShellTextKeys.StatusProfileCreatedFormat, profile.Name);
            return profile;
        }

        public StartupProfile CreateProfileFolder(string name, StartupProfile parent)
        {
            return CreateProfile(name, parent, true);
        }

        public StartupProfile CreateChildProfile(string name, StartupProfile parent)
        {
            return CreateProfile(name, parent, false);
        }

        public StartupProfile CreateStartupProfile(string name)
        {
            return CreateProfile(name);
        }

        public IList<StartupProfile> RootProfiles
        {
            get { return PlanFolderTree.Roots(startupProfiles); }
        }

        public IList<StartupProfile> ChildProfiles(StartupProfile parent)
        {
            return parent == null ? new List<StartupProfile>() : PlanFolderTree.ChildrenOf(startupProfiles, parent.Id);
        }

        /// <summary>把一个方案移进另一个方案（target 为空 = 移到根级）。</summary>
        public bool MoveProfileInto(StartupProfile source, StartupProfile target)
        {
            ThrowIfDisposed();
            StartupProfile storedSource = FindProfile(source);
            StartupProfile storedTarget = target == null ? null : FindProfile(target);
            if (storedSource == null || (target != null && storedTarget == null))
            {
                return false;
            }
            if (!PlanFolderTree.CanMove(startupProfiles, storedSource, storedTarget))
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotMoveIntoItself);
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureProfileMove(storedSource);
            if (!PlanFolderTree.MoveInto(startupProfiles, storedSource, storedTarget))
            {
                return false;
            }
            bool saved = CommitProfileMove(before, storedSource);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = storedTarget == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedToRootFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile))
                : AppText.F(
                    ShellTextKeys.StatusMovedIntoFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                    storedTarget.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
            return saved;
        }

        /// <summary>把方案移到另一个方案前面/后面，成为它的同级。</summary>
        public bool MoveProfileRelative(StartupProfile source, StartupProfile target, bool after)
        {
            ThrowIfDisposed();
            StartupProfile storedSource = FindProfile(source);
            StartupProfile storedTarget = FindProfile(target);
            if (storedSource == null || storedTarget == null)
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureProfileMove(storedSource);
            if (!PlanFolderTree.MoveRelative(startupProfiles, storedSource, storedTarget, after))
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotMoveUnderChild);
                return false;
            }
            bool saved = CommitProfileMove(before, storedSource);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = AppText.T(ShellTextKeys.StatusProfileOrderAdjusted);
            return saved;
        }

        public bool MoveProfileToRoot(StartupProfile source)
        {
            return MoveProfileInto(source, null);
        }

        /// <summary>
        /// 把方案移到根级的**最前**（direction &lt; 0）或**最后**（direction &gt; 0）。
        ///
        /// 【为什么不能用 MoveProfileToRoot】它走的是 `MoveProfileInto(source, null)`；
        /// `PlanFolderTree.MoveInto` 在“源方案已经是根级”时会直接返回 true、**不改变同级顺序**。
        /// 于是根级方案拖到顶部/底部/列表下方空白时会“假成功”（界面提示已移动，JSON 没变）。
        /// 这里以第一个/最后一个根方案为锚点调用 `MoveRelative`，根级和子级方案都能真正换位。
        /// 返回 false = 本来就在那一端（或没有可移动的位置），调用方不要写撤销记录。
        /// </summary>
        public bool MoveProfileToRootEdge(StartupProfile source, int direction)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(source);
            if (stored == null || direction == 0)
            {
                return false;
            }

            IList<StartupProfile> roots = PlanFolderTree.Roots(startupProfiles);
            StartupProfile anchor = null;
            if (direction < 0)
            {
                for (int index = 0; index < roots.Count; index++)
                {
                    if (!object.ReferenceEquals(roots[index], stored))
                    {
                        anchor = roots[index];
                        break;
                    }
                }
            }
            else
            {
                for (int index = roots.Count - 1; index >= 0; index--)
                {
                    if (!object.ReferenceEquals(roots[index], stored))
                    {
                        anchor = roots[index];
                        break;
                    }
                }
            }

            bool alreadyRoot = string.IsNullOrWhiteSpace(stored.ParentId);
            if (anchor == null)
            {
                if (alreadyRoot)
                {
                    // 根级只有一个方案，或它本来就是唯一根方案：没有“更前/更后”。
                    return false;
                }
                PlanFolderMoveSnapshot beforeOnly = CaptureProfileMove(stored);
                if (!PlanFolderTree.MoveInto(startupProfiles, stored, null))
                {
                    return false;
                }
                bool savedOnly = CommitProfileMove(beforeOnly, stored);
                OnPropertyChanged("Profiles");
                OnPropertyChanged("StartupProfiles");
                StatusText = direction < 0
                    ? AppText.F(
                        ShellTextKeys.StatusMovedToFrontFormat,
                        stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile))
                    : AppText.F(
                        ShellTextKeys.StatusMovedToBackFormat,
                        stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
                return savedOnly;
            }

            if (alreadyRoot)
            {
                int oldIndex = PlanFolderTree.SiblingIndex(startupProfiles, stored);
                if (direction < 0 && oldIndex == 0)
                {
                    return false;
                }
                if (direction > 0 && oldIndex == roots.Count - 1)
                {
                    return false;
                }
            }

            PlanFolderMoveSnapshot before = CaptureProfileMove(stored);
            if (!PlanFolderTree.MoveRelative(startupProfiles, stored, anchor, direction > 0))
            {
                return false;
            }
            bool saved = CommitProfileMove(before, stored);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = direction < 0
                ? AppText.F(
                    ShellTextKeys.StatusMovedToFrontFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile))
                : AppText.F(
                    ShellTextKeys.StatusMovedToBackFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
            return saved;
        }

        /// <summary>
        /// 按“父方案 Id + 同级位置”放回原处；撤销/重做和回收站恢复共用。
        /// 原父方案已经不存在时退回根级，绝不自动重建父方案。
        /// </summary>
        public bool MoveProfileToPlacement(StartupProfile profile, string parentId, int siblingIndex)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                return false;
            }
            StartupProfile parent = string.IsNullOrWhiteSpace(parentId)
                ? null
                : PlanFolderTree.FindById(startupProfiles, parentId);
            if (!PlanFolderTree.CanMove(startupProfiles, stored, parent))
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureProfileMove(stored);
            if (!PlanFolderTree.MoveInto(startupProfiles, stored, parent))
            {
                return false;
            }
            if (siblingIndex >= 0)
            {
                PlanFolderTree.PlaceAtSiblingIndex(startupProfiles, stored, siblingIndex);
            }
            bool saved = CommitProfileMove(before, stored);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = parent == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedBackToRootFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile))
                : AppText.F(
                    ShellTextKeys.StatusMovedBackIntoFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                    parent.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
            return saved;
        }

        private PlanFolderMoveSnapshot CaptureProfileMove(StartupProfile profile)
        {
            PlanFolderMoveSnapshot snapshot = new PlanFolderMoveSnapshot();
            if (profile == null)
            {
                return snapshot;
            }
            snapshot.PlanId = profile.Id;
            snapshot.ParentId = profile.ParentId;
            snapshot.SiblingIndex = PlanFolderTree.SiblingIndex(startupProfiles, profile);
            snapshot.DirectoryPath = StartupProfileFolderMirror.FolderPathFor(profile, startupProfiles);
            return snapshot;
        }

        /// <summary>
        /// 方案已经改好 ParentId/顺序之后调用：移动目录、写总 JSON、只更新被移动方案自己的文件夹。
        /// 不再为了移动一份方案而整棵树 SyncNested。
        /// </summary>
        private bool CommitProfileMove(PlanFolderMoveSnapshot before, StartupProfile profile)
        {
            if (before == null || profile == null)
            {
                return false;
            }
            string parentDirectory;
            StartupProfile parent = string.IsNullOrWhiteSpace(profile.ParentId)
                ? null
                : PlanFolderTree.FindById(startupProfiles, profile.ParentId);
            parentDirectory = parent == null
                ? AppPaths.StartupProfilesFolderRoot
                : StartupProfileFolderMirror.FolderPathFor(parent, startupProfiles);
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
                entry.PlanId = profile.Id;
                entry.OldParentId = before.ParentId;
                entry.NewParentId = profile.ParentId;
                entry.OldDirectory = before.DirectoryPath;
                entry.NewDirectory = destination;
                entry.OldSiblingIndex = before.SiblingIndex;
                entry.NewSiblingIndex = PlanFolderTree.SiblingIndex(startupProfiles, profile);
                entry.RecordedUtc = DateTime.UtcNow;
                try
                {
                    PlanFolderMoveJournal.Append(ProfileMoveJournalPath, entry);
                    journalWritten = true;
                }
                catch (Exception exception)
                {
                    // 日志写不进去就不搬目录：宁可只改数据，也不能让磁盘和清单各说各话。
                    AppLog.Write("[方案移动] 移动日志写入失败，本次只更新数据。", exception);
                }
                if (journalWritten)
                {
                    moveResult = PlanFolderDirectoryMover.Move(
                        before.DirectoryPath,
                        destination,
                        profile.Id,
                        StartupProfileFolderMirror.DataFileName,
                        StartupProfileFolderMirror.ReadProfileIdAt);
                }
            }
            bool saved = PersistProfiles(false);
            string folderToWrite = destination;
            if (string.IsNullOrWhiteSpace(folderToWrite))
            {
                folderToWrite = StartupProfileFolderMirror.FolderPathFor(profile, startupProfiles);
            }
            if (!string.IsNullOrWhiteSpace(folderToWrite)
                && (moveResult == null || moveResult.Moved || moveResult.AlreadyInPlace || !moveResult.Moved))
            {
                StartupProfileFolderMirror.WriteProfileAtDirectory(profile, folderToWrite);
            }
            if (saved && journalWritten)
            {
                PlanFolderMoveJournal.Clear(ProfileMoveJournalPath);
            }
            return saved;
        }

        /// <summary>
        /// 按 id 找父方案；找不到返回 null（回收站恢复时父方案已经不存在）。
        /// </summary>
        public StartupProfile FindProfileParentOrNull(string parentId)
        {
            return string.IsNullOrWhiteSpace(parentId) ? null : PlanFolderTree.FindById(startupProfiles, parentId);
        }

        /// <summary>
        /// Creates a named profile from an already captured snapshot.  This
        /// alias keeps the persistence operation convenient for alternate UI
        /// flows that capture first and name the folder second.
        /// </summary>
        public StartupProfile SaveStartupProfile(string name, SessionSnapshot sourceSnapshot)
        {
            StartupProfile profile = CreateProfile(name);
            if (sourceSnapshot != null)
            {
                profile.ReplaceItems(sourceSnapshot.Items);
                profileCatalog.ActiveProfileId = profile.Id;
                RememberActiveProfileId();
                PersistProfiles();
                LastSnapshot = profile.ToSnapshot();
                SaveLastSnapshot(LastSnapshot);
                ProgressMaximum = LastSnapshot.Items == null ? 0 : LastSnapshot.Items.Count;
                OnPropertyChanged("LastSnapshot");
                OnPropertyChanged("HasSnapshot");
                // The first-save flow (no profile existed yet) commits through
                // this method, so its completion must notify the UI too.
                RaiseSaveCompleted(sourceSnapshot, false, false);
                // 状态栏要带上“扫描终端与本地网址”的提示，不然用户不知道为什么少了几项。
                string savedMessage = AppText.F(
                    ShellTextKeys.StatusProfileCreatedWithCountFormat,
                    profile.Name,
                    ProgressMaximum);
                if (!string.IsNullOrWhiteSpace(LastCapturePolicyHint))
                {
                    savedMessage += AppText.T(ShellTextKeys.StatusDetailSeparator)
                        + LastCapturePolicyHint.Trim();
                }
                StatusText = BuildCaptureStatus(savedMessage, sourceSnapshot);
            }
            return profile;
        }

        public StartupProfile RenameProfile(StartupProfile profile, string name)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                return null;
            }

            stored.Name = NormalizeProfileName(name);
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = AppText.F(ShellTextKeys.StatusProfileRenamedFormat, stored.Name);
            return stored;
        }

        public bool DeleteProfile(StartupProfile profile)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                return false;
            }

            List<StartupProfile> subtree = PlanFolderTree.Subtree(startupProfiles, stored);
            if (subtree.Count == 0)
            {
                return false;
            }
            string originalParentId = stored.ParentId;
            int originalSiblingIndex = PlanFolderTree.SiblingIndex(startupProfiles, stored);
            string originalPath = PlanFolderTree.PathOf(startupProfiles, stored, " / ");
            bool activeRemoved = false;
            for (int index = 0; index < subtree.Count; index++)
            {
                if (IsActiveProfile(subtree[index]))
                {
                    activeRemoved = true;
                    break;
                }
            }

            // 先放进回收站，超过保留天数才真正删除。回收站写盘失败时保留原方案，
            // 避免清单和回收站两边都没有。
            if (!AddProfileTreeToRecycleBin(stored, subtree, originalParentId, originalSiblingIndex, originalPath))
            {
                StatusText = AppText.F(
                    ShellTextKeys.StatusProfileDeleteRecycleFailedFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
                return false;
            }
            PlanFolderTree.RemoveSubtree(startupProfiles, stored);
            if (activeRemoved)
            {
                StartupProfile replacement = string.IsNullOrWhiteSpace(originalParentId)
                    ? null
                    : PlanFolderTree.FindById(startupProfiles, originalParentId);
                if (replacement == null)
                {
                    IList<StartupProfile> roots = PlanFolderTree.Roots(startupProfiles);
                    replacement = roots.Count > 0 ? roots[0] : null;
                }
                profileCatalog.ActiveProfileId = replacement == null ? null : replacement.Id;
                RememberActiveProfileId();
                if (replacement != null)
                {
                    LastSnapshot = replacement.ToSnapshot();
                    SaveLastSnapshot(replacement.ToSnapshot());
                    ProgressMaximum = LastSnapshot.Items == null ? 0 : LastSnapshot.Items.Count;
                }
                else
                {
                    LastSnapshot = null;
                    SaveLastSnapshot(new SessionSnapshot());
                    ProgressMaximum = 0;
                    ProgressValue = 0;
                }
            }
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            OnPropertyChanged("ActiveProfile");
            OnPropertyChanged("HasSnapshot");
            string deletedSourceName = stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile);
            StatusText = subtree.Count > 1
                ? AppText.F(
                    ShellTextKeys.StatusProfileDeletedWithChildrenFormat,
                    deletedSourceName,
                    subtree.Count - 1,
                    RecycleBinRetentionDescription)
                : AppText.F(
                    ShellTextKeys.StatusProfileDeletedFormat,
                    deletedSourceName,
                    RecycleBinRetentionDescription);
            return true;
        }

        public bool DeleteStartupProfile(StartupProfile profile)
        {
            return DeleteProfile(profile);
        }
        // ------------------------------------------------------------------
        // 回收站：删除的工作方案先放这里，超过保留天数自动彻底删除。
        // ------------------------------------------------------------------

        private RecycleBinCatalog recycleBinCatalog;

        public int RecycleBinRetentionDays
        {
            get { return recycleBinCatalog == null ? 15 : recycleBinCatalog.RetentionDays; }
        }

        /// <summary>给确认框/状态栏用的保留说明：0 天时说“永久保留”，不再显示“保留 0 天”。</summary>
        public string RecycleBinRetentionDescription
        {
            get
            {
                int days = RecycleBinRetentionDays;
                return days <= 0
                    ? AppText.T(ShellTextKeys.StatusRetentionPermanent)
                    : AppText.F(ShellTextKeys.StatusRetentionDaysFormat, days);
            }
        }

        public IList<RecycleBinEntry> RecycleBinEntries
        {
            get
            {
                return recycleBinCatalog == null || recycleBinCatalog.Entries == null
                    ? new List<RecycleBinEntry>()
                    : (IList<RecycleBinEntry>)recycleBinCatalog.Entries;
            }
        }

        /// <summary>
        /// 启动时补做“目录已经移动、总 JSON 还没写完”的那一步。
        /// 只按日志修正父子关系，不做整棵树重写；日志在总 JSON 写成功后删除。
        /// </summary>
        private void RecoverProfileMoveJournal(StartupProfileCatalog catalog)
        {
            if (catalog == null || catalog.Profiles == null)
            {
                return;
            }
            string path = ProfileMoveJournalPath;
            PlanFolderMoveJournalData journal = PlanFolderMoveJournal.Load(path);
            if (journal.Entries == null || journal.Entries.Count == 0)
            {
                return;
            }
            int applied = PlanFolderMoveJournal.Apply(
                catalog.Profiles,
                journal.Entries,
                delegate(StartupProfile profile) { return profile == null ? null : profile.Id; });
            if (applied > 0)
            {
                try
                {
                    startupProfilesStore.Save(catalog);
                }
                catch (Exception exception)
                {
                    AppLog.Write("[方案移动] 启动修复时保存工作方案总清单失败，保留移动日志。", exception);
                    return;
                }
            }
            PlanFolderMoveJournal.Clear(path);
            AppLog.Write("[方案移动] 启动修复工作方案移动日志 " + applied + " 条。");
        }

        private StartupProfileCatalog LoadProfileCatalog(SessionSnapshot fallbackSnapshot)
        {
            StartupProfileCatalog catalog;
            try
            {
                catalog = startupProfilesStore.Load();
            }
            catch
            {
                catalog = new StartupProfileCatalog();
            }

            if (catalog == null)
            {
                catalog = new StartupProfileCatalog();
            }
            if (catalog.Profiles == null)
            {
                catalog.Profiles = new List<StartupProfile>();
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = catalog.Profiles.Count - 1; index >= 0; index--)
            {
                StartupProfile profile = catalog.Profiles[index];
                if (profile == null)
                {
                    catalog.Profiles.RemoveAt(index);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
                {
                    profile.Id = Guid.NewGuid().ToString("N");
                    ids.Add(profile.Id);
                }
                profile.Name = NormalizeProfileName(profile.Name);
                if (profile.CreatedAtUtc == default(DateTime))
                {
                    profile.CreatedAtUtc = DateTime.UtcNow;
                }
                if (profile.UpdatedAtUtc == default(DateTime))
                {
                    profile.UpdatedAtUtc = profile.CreatedAtUtc;
                }
                if (profile.Items == null)
                {
                    profile.Items = new List<SessionItem>();
                }
                EnsureExcludedItemKeys(profile);
                profile.Resequence();
                // 用户 2026-10-15：“恢复后两个浏览器窗口变成一个”。
                // 旧版本把 BrowserWindowIndex = 0（第一个窗口）静默丢掉了，
                // 已经存坏的方案要靠这里按 ParentId 修回来。详见
                // Shared\Core\BrowserPlan\BrowserPageGrouping.cs 的注释。
                BrowserPageGrouping.Repair(profile.Items);
                SessionCaptureService.NormalizeBrowserWindowDisplayNames(profile.Items);
                int prunedEmptyGroupParents = SessionItemDeletion.RemoveEmptyGroupParents(profile.Items);
                if (prunedEmptyGroupParents > 0)
                {
                    profile.Resequence();
                    AppLog.Write("[工作方案] 清理空父项：" + prunedEmptyGroupParents
                        + " 个（方案：" + (profile.Name ?? "工作方案") + "）", null);
                }
            }

            int repairedFolderTree = PlanFolderTree.Normalize(catalog.Profiles);
            if (repairedFolderTree > 0)
            {
                AppLog.Write("[工作方案] 方案文件夹树修复节点：" + repairedFolderTree + " 个", null);
            }
            RecoverProfileMoveJournal(catalog);
            catalog.SchemaVersion = PlanFolderMigration.CurrentSchemaVersion;

            if (catalog.Profiles.Count == 0 && fallbackSnapshot != null && fallbackSnapshot.Items != null && fallbackSnapshot.Items.Count > 0)
            {
                // 【2026-10-20 用户要求】删掉 workflows 后不要“恢复出”一个默认工作方案。
                // 「自启动应用」那边删掉方案文件就是空清单，这里保持一致：
                // 不再从 last-session.json 造“默认工作方案”。
                AppLog.Write("[数据] 工作方案清单为空；不再从 last-session 自动造“默认工作方案”（用户删除 workflows 的情况）。");
            }

            if (catalog.Profiles.Count > 0)
            {
                // 用户要求（2026-10-04 / 2026-10-20）：用户选过的方案优先；没有选过就用**最新**的方案。
                // 2026-10-20：除了工作方案.json 的 ActiveProfileId，settings.json 里还留了一份
                // LastActiveProfileId 备份。两者不一致时以“用户最后一次手动选择”为准，
                // 这样文件被整理/迁移/异常导致 ActiveProfileId 丢了，也不会退回最新方案。
                bool changed = false;
                string remembered = Settings == null ? null : Settings.LastActiveProfileId;
                bool rememberedFound = false;
                if (!string.IsNullOrWhiteSpace(remembered))
                {
                    for (int index = 0; index < catalog.Profiles.Count; index++)
                    {
                        StartupProfile candidate = catalog.Profiles[index];
                        if (candidate != null
                            && string.Equals(candidate.Id, remembered, StringComparison.OrdinalIgnoreCase))
                        {
                            rememberedFound = true;
                            if (!string.Equals(catalog.ActiveProfileId, remembered, StringComparison.OrdinalIgnoreCase))
                            {
                                catalog.ActiveProfileId = candidate.Id;
                                changed = true;
                            }
                            break;
                        }
                    }
                }
                if (!rememberedFound)
                {
                    changed = StartupProfileDefaults.ApplyActiveProfileId(catalog) || changed;
                }
                if (changed)
                {
                    try
                    {
                        // 立刻落盘：下次启动就等于“用户上次用的方案”，不会再飘。
                        startupProfilesStore.Save(catalog);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("Unable to persist the default active startup profile.", exception);
                    }
                }
                RememberActiveProfileId(catalog);
            }
            else
            {
                catalog.ActiveProfileId = null;
                RememberActiveProfileId(catalog);
            }

            return catalog;
        }

        /// <summary>
        /// 本次捕获一个网页都没有、而方案里存着网页时，保留原有网页（含它们所属的
        /// 父行），避免"关掉浏览器再保存"把已保存的网站悄悄清空。想真正删除网页，
        /// 请在清单里手动删除。
        /// </summary>
        private List<SessionItem> KeepSavedPages(StartupProfile profile, IList<SessionItem> incoming)
        {
            List<SessionItem> result = incoming == null
                ? new List<SessionItem>()
                : new List<SessionItem>(incoming);
            if (profile == null || profile.Items == null)
            {
                return result;
            }
            int incomingPages = 0;
            for (int index = 0; index < result.Count; index++)
            {
                if (result[index] != null && result[index].Kind == SessionItemKind.WebPage)
                {
                    incomingPages++;
                }
            }
            if (incomingPages > 0)
            {
                return result;
            }
            List<SessionItem> savedPages = new List<SessionItem>();
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem item = profile.Items[index];
                if (item != null && item.Kind == SessionItemKind.WebPage)
                {
                    savedPages.Add(item);
                }
            }
            if (savedPages.Count == 0)
            {
                return result;
            }
            for (int index = 0; index < savedPages.Count; index++)
            {
                SessionItem page = savedPages[index];
                SessionItem parent = null;
                if (!string.IsNullOrWhiteSpace(page.ParentId))
                {
                    for (int scan = 0; scan < profile.Items.Count; scan++)
                    {
                        SessionItem candidate = profile.Items[scan];
                        if (candidate != null
                            && string.Equals(candidate.Id, page.ParentId, StringComparison.OrdinalIgnoreCase))
                        {
                            parent = candidate;
                            break;
                        }
                    }
                }
                if (parent != null)
                {
                    bool parentPresent = false;
                    for (int scan = 0; scan < result.Count; scan++)
                    {
                        if (result[scan] != null
                            && string.Equals(result[scan].Id, parent.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            parentPresent = true;
                            break;
                        }
                    }
                    if (!parentPresent)
                    {
                        result.Add(parent);
                    }
                }
                bool pagePresent = false;
                for (int scan = 0; scan < result.Count; scan++)
                {
                    if (result[scan] != null
                        && string.Equals(result[scan].Id, page.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        pagePresent = true;
                        break;
                    }
                }
                if (!pagePresent)
                {
                    result.Add(page);
                }
            }
            StatusText = AppText.F(ShellTextKeys.StatusKeptSavedPagesFormat, savedPages.Count);
            return result;
        }

        private void PersistProfiles()
        {
            PersistProfiles(true);
        }

        /// <summary>
        /// 保存工作方案清单。<paramref name="syncFolders"/> 为 false 时只写总 JSON，
        /// 由调用方对被移动的那一份方案做增量目录更新，避免每次移动都整棵树重写。
        /// 返回总 JSON 是否写成功；失败时调用方保留移动日志，下次启动再补。
        /// </summary>
        private bool PersistProfiles(bool syncFolders)
        {
            if (profileCatalog == null)
            {
                profileCatalog = new StartupProfileCatalog();
            }
            PlanFolderTree.Normalize(startupProfiles);
            profileCatalog.Profiles = startupProfiles.ToList();
            PlanFolderTree.Normalize(profileCatalog.Profiles);
            profileCatalog.SchemaVersion = PlanFolderMigration.CurrentSchemaVersion;
            // 落盘前再对齐一次网页的窗口/标签身份，保证新写的文件一定是对的
            // （内存里的条目可能是旧文件读进来的，或从别处拷贝过来的）。
            for (int index = 0; index < profileCatalog.Profiles.Count; index++)
            {
                StartupProfile profile = profileCatalog.Profiles[index];
                if (profile != null && profile.Items != null)
                {
                    BrowserPageGrouping.Repair(profile.Items);
                }
            }
            // 2026-10-23 用户要求：不再往“备份\工作方案”复制定期 .json 快照。
            bool saved = true;
            try
            {
                startupProfilesStore.Save(profileCatalog);
            }
            catch (Exception exception)
            {
                saved = false;
                AppLog.Write("Unable to save startup profiles.", exception);
            }
            // 每份方案再写一份“可读文件夹”（用户要求：数据放在 workflows\启动与恢复 里，
            // 能直接看到、也能手动删）。文件夹只是快照，程序的清单仍以工作方案.json 为准。
            if (syncFolders)
            {
                StartupProfileFolderMirror.Sync(profileCatalog.Profiles);
            }
            return saved;
        }

    }
}
