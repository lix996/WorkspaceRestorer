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
        /// 只扫描桌面、**不修改任何方案**（「自启动应用」的“扫描桌面”用这个）。
        /// 用户要求（2026-10-09 18:36）：扫描后要让用户一个个添加，不要一股脑全加，
        /// 所以扫描与“加进方案”必须分开。
        /// </summary>
        public IList<DesktopEntry> ScanDesktopEntriesOnly()
        {
            ThrowIfDisposed();
            if (desktopApplicationService == null)
            {
                return new List<DesktopEntry>();
            }
            IList<DesktopEntry> scanned = desktopApplicationService.Scan();
            return scanned == null ? new List<DesktopEntry>() : scanned;
        }

        /// <summary>
        /// 用“只读扫描”的结果替换桌面列表（供用户逐个勾选）；全部默认未勾选。
        /// 不写盘、不改方案。
        /// </summary>
        public void ReplaceDesktopEntriesForSelection(IList<DesktopEntry> entries)
        {
            ThrowIfDisposed();
            desktopEntries.Clear();
            if (entries != null)
            {
                for (int index = 0; index < entries.Count; index++)
                {
                    DesktopEntry entry = entries[index];
                    if (entry == null)
                    {
                        continue;
                    }
                    entry.IsSelected = false;
                    desktopEntries.Add(entry);
                }
            }
            OnPropertyChanged("DesktopEntries");
        }

        // ───────────────────────── 「自启动应用」的方案 ─────────────────────────
        // 用户要求（2026-10-09 22:36）：“自启动应用的启动方案的小类，要参考启动与恢复的小类代码，
        // 移动，是否展开等都要有，启动与恢复有的，自启动应用也要有。”
        // 数据与「启动与恢复」分开存（workflows\自启动应用\自启动方案.json），互不影响。

        public ObservableCollection<ApplicationPlan> ApplicationPlans { get { return applicationPlans; } }

        public ApplicationPlan ActiveApplicationPlan
        {
            get
            {
                if (applicationPlanCatalog == null || string.IsNullOrWhiteSpace(applicationPlanCatalog.ActivePlanId))
                {
                    return applicationPlans.Count > 0 ? applicationPlans[0] : null;
                }
                for (int index = 0; index < applicationPlans.Count; index++)
                {
                    ApplicationPlan plan = applicationPlans[index];
                    if (plan != null && string.Equals(plan.Id, applicationPlanCatalog.ActivePlanId, StringComparison.OrdinalIgnoreCase))
                    {
                        return plan;
                    }
                }
                return applicationPlans.Count > 0 ? applicationPlans[0] : null;
            }
        }

        public IList<DesktopEntry> ScanDesktop()
        {
            ThrowIfDisposed();
            try
            {
                IList<DesktopEntry> scanned = desktopApplicationService.Scan();
                desktopEntries.Clear();
                if (scanned != null)
                {
                    for (int index = 0; index < scanned.Count; index++)
                    {
                        if (scanned[index] != null)
                        {
                            desktopEntries.Add(scanned[index]);
                        }
                    }
                }
                StatusText = AppText.F(ShellTextKeys.StatusDesktopScannedFormat, desktopEntries.Count);
                OnPropertyChanged("DesktopEntries");
                return desktopEntries;
            }
            catch (Exception exception)
            {
                desktopEntries.Clear();
                StatusText = AppText.F(ShellTextKeys.StatusDesktopScanFailedFormat, exception.Message);
                AppLog.Write("Unable to scan desktop entries.", exception);
                return desktopEntries;
            }
        }

        public LaunchResult LaunchDesktopEntry(DesktopEntry entry)
        {
            if (entry == null)
            {
                return new LaunchResult { Success = false, Error = AppText.T(ShellTextKeys.ErrorDesktopEntryEmpty) };
            }
            try
            {
                LaunchResult result = desktopApplicationService.Launch(entry);
                StatusText = result != null && result.Success
                    ? AppText.F(ShellTextKeys.StatusDesktopLaunchedFormat, entry.DisplayName)
                    : AppText.F(
                        ShellTextKeys.StatusLaunchFailedFormat,
                        result == null ? AppText.T(ShellTextKeys.FallbackUnknownError) : result.Error);
                return result;
            }
            catch (Exception exception)
            {
                StatusText = AppText.F(ShellTextKeys.StatusLaunchFailedFormat, exception.Message);
                return new LaunchResult { Success = false, Error = exception.Message };
            }
        }

        /// <summary>
        /// Adds desktop entries selected by the user to a named startup
        /// profile.  This is deliberately separate from ScanDesktop so a
        /// refresh or first load can never mutate a saved profile.
        /// </summary>
        public int AddDesktopEntriesToProfile(StartupProfile profile, IEnumerable<DesktopEntry> entries, string successMessageFormat = null)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null || entries == null)
            {
                return 0;
            }

            if (stored.Items == null)
            {
                stored.Items = new List<SessionItem>();
            }

            int added = 0;
            DateTime addedAt = DateTime.UtcNow;
            foreach (DesktopEntry entry in entries)
            {
                if (entry == null || !entry.IsSelected || string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                SessionItem existing = FindDesktopEntryItem(stored.Items, entry);
                if (existing != null)
                {
                    // A running executable may already be present in the
                    // captured session.  Mark that item as an explicit
                    // desktop selection so it survives after the process is
                    // closed and remains part of the next restore.
                    if (!string.Equals(existing.CaptureSource, "DesktopSelection", StringComparison.OrdinalIgnoreCase))
                    {
                        existing.CaptureSource = "DesktopSelection";
                        added++;
                    }
                    continue;
                }

                stored.Items.Add(CreateDesktopSessionItem(entry, addedAt.AddTicks(added)));
                added++;
            }

            if (added == 0)
            {
                StatusText = AppText.T(ShellTextKeys.StatusDesktopAlreadyInProfile);
                return 0;
            }

            stored.Resequence();
            stored.UpdatedAtUtc = DateTime.UtcNow;
            PersistProfiles();
            if (IsActiveProfile(stored))
            {
                SessionSnapshot snapshot = stored.ToSnapshot();
                LastSnapshot = snapshot;
                SaveLastSnapshot(snapshot);
                ProgressMaximum = snapshot.Items == null ? 0 : snapshot.Items.Count;
                ProgressValue = 0;
                OnPropertyChanged("HasSnapshot");
            }
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            // 【2026-10-18】允许调用方给一句自己的话（{0} ＝ 加了几项）：
            // 「加入启动与恢复」按钮要把口径写成“加入了启动与恢复的方案”，而不是“当前方案”。
            StatusText = string.IsNullOrWhiteSpace(successMessageFormat)
                ? AppText.F(ShellTextKeys.StatusDesktopAddedFormat, added)
                : string.Format(successMessageFormat, added);
            return added;
        }

        /// <summary>
        /// Convenience overload for the active profile.  It does not create
        /// a profile implicitly; callers can create one as an explicit user
        /// action when no profile exists yet.
        /// </summary>
        public int AddDesktopEntriesToActiveProfile(IEnumerable<DesktopEntry> entries)
        {
            return AddDesktopEntriesToProfile(ActiveProfile, entries);
        }

    }
}
