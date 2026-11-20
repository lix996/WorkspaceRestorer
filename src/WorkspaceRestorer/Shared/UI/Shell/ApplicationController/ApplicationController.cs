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
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.UI.Cards.Drag;

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
        private readonly ISessionCaptureService captureService;
        private readonly ISessionRestoreService restoreService;
        private readonly IDesktopApplicationService desktopApplicationService;
        private readonly object startupRegistrationService;
        /// <summary>进程启动时 settings.json 是否已经存在（测试模式据此判断“探针有没有自己配设置”）。</summary>
        private readonly bool settingsFileExistedAtStartup;
        private readonly object layoutCoordinator;
        /// <summary>
        /// 布局目录维护（移动日志恢复、孤儿回收、Sync）只在本进程第一次读取布局时做一次。
        /// 以前每次 GetDesktopLayouts/ActiveDesktopLayoutId 都做，拖动时每移动一下都在重写
        /// 每份 布局.xml，界面就会明显卡顿。
        /// </summary>
        private bool layoutStorageMaintained;
        private readonly AtomicJsonStore<SessionSnapshot> sessionStore;
        private readonly AtomicJsonStore<StartupProfileCatalog> startupProfilesStore;
        private readonly RecycleBinFolderStore recycleBinStore;
        /// <summary>「自启动应用」的方案清单（与「启动与恢复」的方案分开存）。</summary>
        private readonly AtomicJsonStore<ApplicationPlanCatalog> applicationPlansStore;
        private readonly ObservableCollection<ApplicationPlan> applicationPlans = new ObservableCollection<ApplicationPlan>();
        private ApplicationPlanCatalog applicationPlanCatalog;
        private readonly AtomicJsonStore<ControllerSettings> settingsStore;
        private readonly ObservableCollection<DesktopEntry> desktopEntries;
        private readonly ObservableCollection<StartupProfile> startupProfiles;
        private CancellationTokenSource restoreCancellation;
        private volatile bool restoreCountdownSkipped;
        private volatile bool applicationsCountdownSkipped;
        private RestoreLoadGate restoreLoadGate;
        private bool loadPromptVisible;
        private string loadPromptText;
        private bool loadPromptAllowed;
        private bool disposed;
        private bool restoring;
        private string statusText;
        private int countdownSeconds;
        private int progressValue;
        private int progressMaximum;
        private SessionSnapshot lastSnapshot;
        private StartupProfileCatalog profileCatalog;
        private ControllerSettings settings;
        private StartupRegistrationInfo startupStatus;
        private CancellationTokenSource startupLayoutCancellation;
        private bool restoringDesktopLayout;
        // Browser URL capture can cross process boundaries and may block in
        // a UI Automation provider.  Keep at most one capture operation in
        // flight so a timed-out worker cannot compete with a later save for
        // browser focus.
        private int sessionCaptureInProgress;
        private int sessionSaveCancelRequested;
        private int layoutCaptureInProgress;
        // A browser window with many tabs is read by walking its tabs one by one
        // (there is no debug port on a browser the user started themselves), which
        // needs several seconds per window. The old 30 s cap cut the save short
        // and dropped the remaining browser windows entirely.
        private const int InteractiveCaptureTimeoutMilliseconds = 180000;
        private const int PassiveCaptureTimeoutMilliseconds = 7000;
        private const int ShutdownCaptureTimeoutMilliseconds = 7000;
        private const int DesktopLayoutCaptureTimeoutMilliseconds = 5000;

        /// <summary>方案移动日志：目录已移动、总 JSON 还没写完时的恢复依据。</summary>
        private string ProfileMoveJournalPath
        {
            get { return PlanFolderMoveJournal.PathFor(AppPaths.StartupRestoreDirectory, "方案移动日志.json"); }
        }

        private string ApplicationMoveJournalPath
        {
            get { return PlanFolderMoveJournal.PathFor(AppPaths.ApplicationsDirectory, "方案移动日志.json"); }
        }

        private string LayoutMoveJournalPath
        {
            get { return PlanFolderMoveJournal.PathFor(AppPaths.DesktopLayoutDirectory, "布局移动日志.json"); }
        }

        public ApplicationController()
            : this(
                new SessionCaptureService(),
                CreateSessionRestoreService(),
                CreateDesktopApplicationService(),
                CreateService("WorkspaceRestorer.Services.StartupRegistrationService"),
                CreateLayoutCoordinator())
        {
        }

        public ApplicationController(
            ISessionCaptureService captureService,
            ISessionRestoreService restoreService,
            IDesktopApplicationService desktopApplicationService,
            object startupRegistrationService,
            object layoutCoordinator)
        {
            if (captureService == null)
            {
                throw new ArgumentNullException("captureService");
            }
            if (restoreService == null)
            {
                throw new ArgumentNullException("restoreService");
            }
            if (desktopApplicationService == null)
            {
                throw new ArgumentNullException("desktopApplicationService");
            }

            this.captureService = captureService;
            this.restoreService = restoreService;
            this.desktopApplicationService = desktopApplicationService;
            this.startupRegistrationService = startupRegistrationService;
            this.layoutCoordinator = layoutCoordinator;
            // 先把旧版目录结构搬到新的 workflows/回收站 结构（只搬一次，搬前留备份）。
            DataLayoutMigrator.Migrate();
            sessionStore = new AtomicJsonStore<SessionSnapshot>(AppPaths.SessionFile, delegate { return new SessionSnapshot(); });
            startupProfilesStore = new AtomicJsonStore<StartupProfileCatalog>(AppPaths.StartupProfilesFile, delegate { return new StartupProfileCatalog(); });
            recycleBinStore = new RecycleBinFolderStore();
            applicationPlansStore = new AtomicJsonStore<ApplicationPlanCatalog>(
                AppPaths.ApplicationPlansFile,
                delegate { return new ApplicationPlanCatalog(); });
            settingsFileExistedAtStartup = File.Exists(AppPaths.SettingsFile);
            settingsStore = new AtomicJsonStore<ControllerSettings>(AppPaths.SettingsFile, delegate { return new ControllerSettings(); });
            desktopEntries = new ObservableCollection<DesktopEntry>();
            startupProfiles = new ObservableCollection<StartupProfile>();
            settings = LoadSettings();
            // 在任何窗口、托盘或静态提示文案加载之前确定界面语言。
            string languageTag = LanguageCatalog.IsSupportedReady(settings.Language)
                ? settings.Language
                : WorkspaceRestorer.Core.Localization.LanguageTags.Default;
            AppText.Initialize(languageTag);
            // settings.json 里的自定义快捷键必须在任何窗口/路由使用之前装进目录；
            // null 表示从未改过，目录自己使用默认绑定（Ctrl+C / Ctrl+V / Ctrl+Z / Ctrl+Shift+Z）。
            WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(settings.ShortcutBindings);
            // 拖动前缀同样要在任何窗口/拖动入口创建之前装好；空值自动回退 Shift + 左键。
            DragGestureSettings.ConfigureFromSettings(
                settings.DragGestureKey,
                settings.DragGestureModifiers,
                settings.DragGestureMouseButton);
            lastSnapshot = LoadSnapshot();
            // 【2026-10-20 用户报】把 workflows 整个删掉后，程序又“恢复出”一个工作方案。
            // 根因：工作方案.json 不在时，旧逻辑会拿 设置\last-session.json 造一份“默认工作方案”。
            // 「自启动应用」删掉方案文件后就是空清单；这里也照做：**清单文件不存在时按空清单启动**，
            // 不再从 last-session 造方案，也不再拿它当“没有方案时的恢复兜底”。
            bool profilesFileExistedAtStartup = File.Exists(AppPaths.StartupProfilesFile);
            profileCatalog = LoadProfileCatalog(lastSnapshot);
            if (!profilesFileExistedAtStartup && profileCatalog.Profiles.Count == 0)
            {
                lastSnapshot = null;
                AppLog.Write("[数据] 工作方案清单不存在，按空清单启动（不再从 last-session 恢复默认方案）。");
            }
            LoadApplicationPlans();
            recycleBinCatalog = LoadRecycleBinCatalog();
            if (profileCatalog.Profiles != null)
            {
                // 用户手动删掉了某份方案的文件夹 → 这份方案也就不再存在（“直接删文件清数据”）。
                // 有安全网：只有父文件夹里还剩至少一个方案文件夹时才会这么判断。
                //
                // 【2026-10-16 加固】这天发现 4 份方案在没有任何日志的情况下从清单里消失
                // （只有回收站里有记录，说明走的是删除接口而不是这条文件夹规则）。
                // 不管是谁触发的，这种“方案凭空少了”都不该发生得无声无息，所以：
                //  · 这条规则清掉的每一份方案**先放进回收站**（以前是直接丢掉，救不回来）；
                //  · 日志写清“因为文件夹不见了”，并带上文件夹名，便于事后追。
                List<StartupProfile> removedByFolderProfiles =
                    StartupProfileFolderMirror.TakeProfilesWhoseFolderWasDeleted(profileCatalog.Profiles);
                if (removedByFolderProfiles.Count > 0)
                {
                    List<string> removedByFolder = new List<string>();
                    for (int index = 0; index < removedByFolderProfiles.Count; index++)
                    {
                        StartupProfile gone = removedByFolderProfiles[index];
                        if (gone == null)
                        {
                            continue;
                        }
                        removedByFolder.Add(gone.Name ?? "工作方案");
                        try
                        {
                            AddToRecycleBin(gone);
                        }
                        catch (Exception exception)
                        {
                            AppLog.Write("把“文件夹已被删除”的方案放进回收站失败：" + (gone.Name ?? "?"), exception);
                        }
                    }
                    AppLog.Write("检测到方案文件夹已被手动删除，已从清单中去掉（并已放进回收站，可恢复）："
                        + string.Join("、", removedByFolder.ToArray()));
                    try
                    {
                        startupProfilesStore.Save(profileCatalog);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("按文件夹清理后保存方案清单失败。", exception);
                    }
                }
                for (int index = 0; index < profileCatalog.Profiles.Count; index++)
                {
                    StartupProfile profile = profileCatalog.Profiles[index];
                    if (profile != null)
                    {
                        profile.Resequence();
                        startupProfiles.Add(profile);
                    }
                }
                // 启动时就把“每份方案一个文件夹”的影子目录补齐：用户删掉了也会重新生成，
                // 这样 workflows\启动与恢复\工作方案 里永远能直接看到有哪些方案。
                StartupProfileFolderMirror.Sync(profileCatalog.Profiles);
            }
            StartupProfile activeStartupProfile = ActiveProfile;
            if (activeStartupProfile != null)
            {
                lastSnapshot = activeStartupProfile.ToSnapshot();
            }
            startupStatus = new StartupRegistrationInfo();
            statusText = lastSnapshot == null || lastSnapshot.Items == null || lastSnapshot.Items.Count == 0
                ? AppText.T(ShellTextKeys.StatusNeverSaved)
                : AppText.F(ShellTextKeys.StatusSavedLastSessionFormat, lastSnapshot.Items.Count);
            progressMaximum = lastSnapshot == null || lastSnapshot.Items == null ? 0 : lastSnapshot.Items.Count;
            RefreshStartupStatus();
            if (!Settings.AutoRestore
                && (Settings.AutoStart || (StartupStatus != null && StartupStatus.IsEnabled)))
            {
                SetAutoStart(false);
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Raised on the dispatcher thread after a session restore finishes
        /// (automatic, manual, or tray initiated).  The UI uses the payload to
        /// show a completion banner or tray notification.
        /// </summary>
        public event Action<RestoreCompletion> RestoreCompleted;

        /// <summary>
        /// Raised on the dispatcher thread after an explicit user save
        /// finishes (main-window save, profile save, or tray save).  The UI
        /// uses the payload to confirm the save and warn about unread pages.
        /// </summary>
        public event Action<SaveCompletion> SaveCompleted;

        /// <summary>
        /// Raised on the dispatcher thread when an explicit save starts.
        /// The UI shows the "正在保存当前工作" card and brings the window to
        /// the front so the user can cancel the scan if needed.
        /// </summary>
        public event Action SaveStarted;

        /// <summary>
        /// Raised on the dispatcher thread when a restore starts (manual,
        /// automatic, or tray initiated).  The UI shows the "正在恢复…" card
        /// and keeps the window on top until it finishes.
        /// </summary>
        public event Action RestoreStarted;

        public ISessionCaptureService CaptureService { get { return captureService; } }

        public ISessionRestoreService RestoreService { get { return restoreService; } }

        public ObservableCollection<DesktopEntry> DesktopEntries { get { return desktopEntries; } }

        /// <summary>
        /// Named startup workspaces shown by the left-side startup panel.
        /// </summary>
        public ObservableCollection<StartupProfile> Profiles { get { return startupProfiles; } }

        /// <summary>
        /// Compatibility alias for callers that prefer the explicit name.
        /// </summary>
        public ObservableCollection<StartupProfile> StartupProfiles { get { return startupProfiles; } }

        public StartupProfile ActiveProfile
        {
            get
            {
                if (profileCatalog == null || profileCatalog.Profiles == null)
                {
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(profileCatalog.ActiveProfileId))
                {
                    StartupProfile selected = profileCatalog.Profiles.FirstOrDefault(delegate(StartupProfile item)
                    {
                        return item != null && string.Equals(item.Id, profileCatalog.ActiveProfileId, StringComparison.OrdinalIgnoreCase);
                    });
                    if (selected != null)
                    {
                        return selected;
                    }
                }

                return profileCatalog.Profiles.FirstOrDefault(delegate(StartupProfile item)
                {
                    return item != null;
                });
            }
        }

        public SessionSnapshot LastSnapshot
        {
            get { return lastSnapshot; }
            private set
            {
                lastSnapshot = value;
                OnPropertyChanged("LastSnapshot");
            }
        }

        public ControllerSettings Settings
        {
            get { return settings; }
            private set
            {
                settings = value ?? new ControllerSettings();
                OnPropertyChanged("Settings");
            }
        }

        public StartupRegistrationInfo StartupStatus
        {
            get { return startupStatus; }
            private set
            {
                startupStatus = value ?? new StartupRegistrationInfo();
                OnPropertyChanged("StartupStatus");
            }
        }

        public bool IsRestoring
        {
            get { return restoring; }
            private set
            {
                if (restoring == value)
                {
                    return;
                }
                restoring = value;
                OnPropertyChanged("IsRestoring");
            }
        }

        /// <summary>图标位置正在恢复。保存工作会话/后台快照要据此拒绝，避免抓到恢复了一半的桌面。</summary>
        public bool IsRestoringDesktopLayout
        {
            get { return restoringDesktopLayout; }
        }

        /// <summary>
        /// 【2026-10-19】这一次恢复的方案里有多少项是“终端 / 本地网址”（这类东西「启动与恢复」恢复不了）。
        /// 恢复卡片会用这一句如实说明，免得用户以为“少的那几项是丢了”。
        /// 自启动方案恢复不走这条（那边终端/本地网址本来就该能启动）。
        /// </summary>
        public int RestoreNonRestorableCount { get; private set; }

        public string StatusText
        {
            get { return statusText; }
            private set
            {
                statusText = value ?? string.Empty;
                OnPropertyChanged("StatusText");
            }
        }

        public void CaptureBeforeShutdown()
        {
            // Keep the historical void entry point for tray/host callers,
            // but never perform a blocking capture on their thread.
            ObserveTask(
                CaptureBeforeShutdownAsync(),
                "Unable to finish the shutdown session checkpoint.");
        }

        /// <summary>
        /// Starts a bounded passive checkpoint for logout/exit.  It never
        /// activates browser tabs and does not block the WPF dispatcher.  A
        /// caller that wants to delay process termination may await the
        /// returned task; fire-and-forget callers still get best-effort
        /// persistence while their UI remains responsive.
        /// </summary>
        public async Task CaptureBeforeShutdownAsync()
        {
            if (disposed)
            {
                return;
            }
            if (IsRestoring)
            {
                CancelRestore();
            }
            if (Interlocked.CompareExchange(ref sessionCaptureInProgress, 1, 0) != 0)
            {
                return;
            }

            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(ShutdownCaptureTimeoutMilliseconds);
            try
            {
                // Bind the whole checkpoint to the profile that was active at
                // the start.  A late profile switch must never redirect a
                // shutdown snapshot into a different work folder.
                StartupProfile captureProfile = ActiveProfile;
                Task<SessionSnapshot> worker = StartSessionCaptureWorker(false);
                Task completed = await Task.WhenAny(
                    worker,
                    Task.Delay(RemainingMilliseconds(deadlineUtc))).ConfigureAwait(true);
                if (completed != worker)
                {
                    Interlocked.CompareExchange(ref sessionCaptureInProgress, 0, 1);
                    AppLog.Write("Passive shutdown session capture exceeded the timeout and was ignored.");
                    return;
                }

                SessionSnapshot snapshot = await worker.ConfigureAwait(true);
                if (snapshot == null || disposed)
                {
                    return;
                }

                StartupProfile storedCaptureProfile = captureProfile == null
                    ? null
                    : FindProfile(captureProfile);
                if (captureProfile != null && storedCaptureProfile == null)
                {
                    return;
                }

                CommitCapturedSnapshot(
                    snapshot,
                    false,
                    false,
                    storedCaptureProfile);
                // 2026-10-23：关机/退出时的影子保存也只保存工作方案。
                // 桌面图标布局必须由用户点「保存图标布局」显式保存，
                // 不能因为退出程序就自动多出一个布局文件夹。
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to capture the session before shutdown.", exception);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            // An interactive capture may have spawned a helper process that
            // is still inside browser UI Automation.  Stop helpers owned by
            // this application before the controller is released so an
            // abandoned scan cannot continue selecting browser tabs after
            // exit.  The process boundary is also what makes this safe: only
            // helpers tracked by SessionCaptureProcess are terminated.
            SessionCaptureProcess.CancelActiveCaptures();
            CancelRestore();
            CancellationTokenSource layoutCancellation = startupLayoutCancellation;
            startupLayoutCancellation = null;
            if (layoutCancellation != null)
            {
                layoutCancellation.Cancel();
            }
            SaveSettings();
        }

    }
}
