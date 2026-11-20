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
        public bool RemoveProfileItem(StartupProfile profile, SessionItem item)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || item == null || stored.Items == null)
            {
                return false;
            }

            SessionItem existing = stored.Items.FirstOrDefault(delegate(SessionItem candidate)
            {
                return candidate != null &&
                    ((!string.IsNullOrWhiteSpace(item.Id) && string.Equals(candidate.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                     || object.ReferenceEquals(candidate, item));
            });
            if (existing == null)
            {
                return false;
            }

            List<SessionItem> removedItems = SessionItemDeletion.Collect(stored.Items, existing);
            EnsureExcludedItemKeys(stored);
            for (int index = 0; index < removedItems.Count; index++)
            {
                SessionItem removed = removedItems[index];
                AddExcludedItemKey(stored, GetItemExclusionKey(removed));

                // 父项被连带删掉时才写窗口/分组排除键；只删网页、父项还在时
                // 不能写分组键，否则同一窗口剩下的网页会被一起排除掉。
                if (IsBrowserWindowItem(removed) || IsExplorerWindowItem(removed))
                {
                    AddExcludedItemKey(stored, GetParentExclusionKey(removed));
                }
                if (IsExplorerWindowItem(removed))
                {
                    AddExcludedItemKey(stored, GetExplorerWindowTitleExclusionKey(removed));
                }
                stored.Items.Remove(removed);
            }
            stored.Resequence();
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot activeSnapshot = stored.ToSnapshot();
                LastSnapshot = activeSnapshot;
                SaveLastSnapshot(activeSnapshot);
                ProgressMaximum = activeSnapshot.Items == null ? 0 : activeSnapshot.Items.Count;
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = AppText.F(
                ShellTextKeys.StatusProfileItemRemovedFormat,
                item.DisplayName ?? AppText.T(ShellTextKeys.FallbackItem));
            return true;
        }

        public bool RemoveStartupProfileItem(StartupProfile profile, SessionItem item)
        {
            return RemoveProfileItem(profile, item);
        }

        /// <summary>
        /// Replaces a profile's item order (drag-and-drop reorder) and
        /// persists it.
        /// </summary>
        public bool SetProfileItemsOrder(StartupProfile profile, IList<SessionItem> ordered)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || ordered == null)
            {
                return false;
            }
            stored.ReplaceItems(ordered);
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot snapshot = stored.ToSnapshot();
                LastSnapshot = snapshot;
                SaveLastSnapshot(snapshot);
                ProgressMaximum = snapshot.Items == null ? 0 : snapshot.Items.Count;
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            return true;
        }

        /// <summary>
        /// Reorders the work folders (profiles) to match the supplied list.
        /// Unknown entries are ignored; any folder missing from the list keeps
        /// its relative place at the end so a drag can never lose a profile.
        /// </summary>
        public bool SetProfilesOrder(IList<StartupProfile> ordered)
        {
            ThrowIfDisposed();
            if (ordered == null)
            {
                return false;
            }
            List<StartupProfile> current = startupProfiles.ToList();
            if (current.Count <= 1)
            {
                return false;
            }

            List<StartupProfile> result = new List<StartupProfile>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < ordered.Count; index++)
            {
                StartupProfile stored = FindProfile(ordered[index]);
                if (stored != null && !string.IsNullOrWhiteSpace(stored.Id) && seen.Add(stored.Id))
                {
                    result.Add(stored);
                }
            }
            for (int index = 0; index < current.Count; index++)
            {
                StartupProfile existing = current[index];
                if (existing != null && !string.IsNullOrWhiteSpace(existing.Id) && seen.Add(existing.Id))
                {
                    result.Add(existing);
                }
            }
            if (result.Count != current.Count)
            {
                return false;
            }

            bool changed = false;
            for (int index = 0; index < result.Count; index++)
            {
                if (!object.ReferenceEquals(result[index], current[index]))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
            {
                return false;
            }

            for (int index = 0; index < result.Count; index++)
            {
                startupProfiles[index] = result[index];
            }
            PersistProfiles(false);
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = AppText.T(ShellTextKeys.StatusProfileOrderAdjusted);
            return true;
        }

        /// <summary>
        /// Appends a user-supplied item (folder path, file/executable or web
        /// URL) to the given profile without capturing windows.
        /// </summary>
        public SessionItem AddSessionItemToProfile(StartupProfile profile, SessionItem item)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || item == null)
            {
                return null;
            }

            if (stored.Items == null)
            {
                stored.Items = new List<SessionItem>();
            }
            if (item.StartTimeUtc == default(DateTime))
            {
                item.StartTimeUtc = DateTime.UtcNow;
            }
            stored.Items.Add(item);
            stored.Resequence();
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot snapshot = stored.ToSnapshot();
                LastSnapshot = snapshot;
                SaveLastSnapshot(snapshot);
                ProgressMaximum = snapshot.Items == null ? 0 : snapshot.Items.Count;
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            return item;
        }

        public void SetActiveProfile(StartupProfile profile)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                return;
            }

            profileCatalog.ActiveProfileId = stored.Id;
            RememberActiveProfileId();
            SessionSnapshot snapshot = stored.ToSnapshot();
            LastSnapshot = snapshot;
            SaveLastSnapshot(snapshot);
            PersistProfiles();
            ProgressMaximum = snapshot.Items == null ? 0 : snapshot.Items.Count;
            OnPropertyChanged("ActiveProfile");
            OnPropertyChanged("HasSnapshot");
            StatusText = AppText.F(
                ShellTextKeys.StatusActiveProfileFormat,
                stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
        }

        /// <summary>
        /// 把“用户手动选中的方案”再记一份到 settings.json。
        /// 工作方案.json 的 ActiveProfileId 仍是权威值；这一份是防丢的备份：
        /// 万一清单被整理/迁移/异常导致 ActiveProfileId 丢失，下次启动仍能回到用户选的那份。
        /// </summary>
        private void RememberActiveProfileId(StartupProfileCatalog catalog = null)
        {
            if (Settings == null)
            {
                return;
            }
            StartupProfileCatalog source = catalog ?? profileCatalog;
            string id = source == null ? null : source.ActiveProfileId;
            if (string.Equals(Settings.LastActiveProfileId, id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Settings.LastActiveProfileId = id;
            SaveSettings();
            AppLog.Write("[方案选择] 记住当前工作方案：" + (string.IsNullOrWhiteSpace(id) ? "（无）" : id));
        }

    }
}
