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
using WorkspaceRestorer.Core.Cards.Drag;

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
        [DataContract]
        public sealed class ControllerSettings
        {
            // 【2026-10-20 用户要求】两个“启动前等待”默认 5 秒（原来的 10 秒太慢），
            // 最低 0（立刻开始），最长 60 秒。只限“启动前等待”；项目间隔、网页间隔不受影响。
            public const int InitialDelayDefaultSeconds = 5;
            public const int InitialDelayMaximumSeconds = 60;

            public ControllerSettings()
            {
                AutoStart = false;
                // Do not launch a saved session merely because the keeper was
                // opened.  Users explicitly opt in and the choice is persisted
                // in settings.json for subsequent launches.
                AutoRestore = true;   // 用户 2026-10-19 默认：打开（没有工作方案时自然什么都不恢复）
                WaitForLoad = true;
                InitialDelaySeconds = InitialDelayDefaultSeconds;
                ItemDelaySeconds = 3;
                // Spread saved web pages out so a freshly booted machine does
                // not have every page race the network at the same instant.
                // 0 keeps the old "hand every URL to the browser at once" mode.
                WebPageDelaySeconds = 0.5;
                // 用户 2026-10-19 默认：自启动应用自己的一套等待/限速（代码在 Applications/Settings 与这里）。
                ApplicationsAutoRestore = true;
                ApplicationsWaitForLoad = true;
                ApplicationsInitialDelaySeconds = InitialDelayDefaultSeconds;
                ApplicationsItemDelaySeconds = 3;
                ApplicationsWebPageDelaySeconds = 0.5;
                // 用户 2026-10-23：图标位置保存的自动恢复独立于另外两个，默认关闭；启动前等待默认 5 秒。
                LayoutAutoRestore = false;
                LayoutInitialDelaySeconds = InitialDelayDefaultSeconds;
                // 语言/主题：默认简体中文、浅色；旧 settings.json 没有这两个成员时按默认值。
                Language = "zh-CN";
                Theme = "light";
            }

            [DataMember(Order = 1)]
            public bool AutoStart { get; set; }

            [DataMember(Order = 2)]
            public bool AutoRestore { get; set; }

            [DataMember(Order = 3)]
            public bool WaitForLoad { get; set; }

        /// <summary>启动项目时允许的最大 CPU 占用百分比，超过就暂停恢复。</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)]
        public double? MaximumCpuPercentValue { get; set; }

        [IgnoreDataMember]
        public double MaximumCpuPercent
        {
            get { return MaximumCpuPercentValue.HasValue ? MaximumCpuPercentValue.Value : 75.0; }
            set { MaximumCpuPercentValue = value; }
        }

        /// <summary>
        /// CPU 一直降不下来时最长等待多少秒；到点仍高就暂停恢复。
        /// 0 表示不等待（直接暂停）。
        /// </summary>
        [DataMember(Order = 9, EmitDefaultValue = false)]
        public int? LoadWaitTimeoutSecondsValue { get; set; }

        [IgnoreDataMember]
        public int LoadWaitTimeoutSeconds
        {
            get { return LoadWaitTimeoutSecondsValue.HasValue ? LoadWaitTimeoutSecondsValue.Value : 120; }
            set { LoadWaitTimeoutSecondsValue = value; }
        }

        /// <summary>启动项目时允许的最大内存占用百分比，超过就暂停恢复。默认 75。</summary>
        [DataMember(Order = 19, EmitDefaultValue = false)]
        public double? MaximumMemoryPercentValue { get; set; }

        [IgnoreDataMember]
        public double MaximumMemoryPercent
        {
            get { return MaximumMemoryPercentValue.HasValue ? MaximumMemoryPercentValue.Value : 75.0; }
            set { MaximumMemoryPercentValue = value; }
        }

        /// <summary>内存一直降不下来时最长等待多少秒；到点仍高就暂停恢复。</summary>
        [DataMember(Order = 20, EmitDefaultValue = false)]
        public int? MemoryWaitTimeoutSecondsValue { get; set; }

        [IgnoreDataMember]
        public int MemoryWaitTimeoutSeconds
        {
            get { return MemoryWaitTimeoutSecondsValue.HasValue ? MemoryWaitTimeoutSecondsValue.Value : 120; }
            set { MemoryWaitTimeoutSecondsValue = value; }
        }



            [DataMember(Order = 4)]
            public int InitialDelaySeconds { get; set; }

            [DataMember(Order = 5)]
            public int ItemDelaySeconds { get; set; }

            /// <summary>
            /// Backing value of <see cref="WebPageDelaySeconds"/>.  It is
            /// nullable on purpose: settings.json files written before this
            /// option existed have no member at all, and the JSON serializer
            /// leaves missing members at default(double) instead of running the
            /// constructor. A null therefore means "not configured yet" and the
            /// new default (0.5 s, one page at a time) applies, while an
            /// explicit 0 keeps the old "all pages at once" behaviour.
            /// </summary>
            [DataMember(Order = 6, EmitDefaultValue = false)]
            public double? WebPageDelaySecondsValue { get; set; }

            [IgnoreDataMember]
            public double WebPageDelaySeconds
            {
                get { return WebPageDelaySecondsValue.HasValue ? WebPageDelaySecondsValue.Value : 0.5; }
                set { WebPageDelaySecondsValue = value; }
            }

            /// <summary>
            /// Backing value of <see cref="DragScrollSpeed"/>.  Null (an older
            /// settings.json without the member) means "not configured yet" and
            /// the default speed applies.
            /// </summary>
            [DataMember(Order = 7, EmitDefaultValue = false)]
            public double? DragScrollSpeedValue { get; set; }

            /// <summary>
            /// Multiplier for the auto-scroll speed used while dragging an entry
            /// into the "drag here to scroll" strips.  1.0 is the default; the UI
            /// offers 0.5x - 3x.
            /// </summary>
            [IgnoreDataMember]
            public double DragScrollSpeed
            {
                get { return DragScrollSpeedValue.HasValue ? DragScrollSpeedValue.Value : 1.0; }
                set { DragScrollSpeedValue = value; }
            }

            /// <summary>
            /// 优先启动项（用户要求 2026-10-10 03:12）：
            /// 两个分区都开了“启动程序后自动恢复…”时先做哪一个。
            /// 0 = 先「启动与恢复」的工作方案（默认）；1 = 先「自启动应用」的方案。
            /// 这是全局行为，所以放在总设置里、代码也在公共区域（<see cref="StartupPriority"/>）。
            /// </summary>
            [DataMember(Order = 8, EmitDefaultValue = false)]
            public int? StartupPriorityValue { get; set; }

            /// <summary>
            /// 「自启动应用」分区：打开程序时是否自动启动当前自启动方案（与 AutoRestore 独立）。
            /// </summary>
            [DataMember(Order = 9, EmitDefaultValue = false)]
            public bool ApplicationsAutoRestore { get; set; }

            /// <summary>
            /// 用户手动展开过的自启动方案 Id（用户第 81 条：“如果用户操作了展开……就按照用户的来”）。
            /// 存在设置里，所以关掉程序再打开也记得；没记过的一律**默认收起**。
            /// </summary>
            [DataMember(Order = 10, EmitDefaultValue = false)]
            public List<string> ExpandedApplicationPlanIds { get; set; }

            /// <summary>true = 先启动「自启动应用」的方案；false = 先恢复「启动与恢复」的工作方案。</summary>
            /// <summary>
            /// 「启动与恢复」：保存方案时要不要把**终端窗口**和**本地网址**（localhost/127.0.0.1/file:）也扫进来。
            /// 默认 false。用户 2026-10-19 原话：增加“扫描终端与本地网址”，默认不勾选；勾选时扫描，
            /// 没勾选时不扫描；因为启动与恢复恢复不了它们，勾选后要在提示里告知用户哪些无法恢复。
            /// </summary>
            [DataMember(Order = 11, EmitDefaultValue = false)]
            public bool ScanTerminalsAndLocalUrls { get; set; }

            /// <summary>「自启动应用」：启动每一项前是否等高负载降下来（用户 2026-10-19 要求加上）。</summary>
            [DataMember(Order = 12, EmitDefaultValue = false)]
            public bool ApplicationsWaitForLoad { get; set; }

            /// <summary>「自启动应用」：开始启动前等待多少秒。</summary>
            [DataMember(Order = 13, EmitDefaultValue = false)]
            public int ApplicationsInitialDelaySeconds { get; set; }

            /// <summary>「自启动应用」：每启动一项后间隔多少秒。</summary>
            [DataMember(Order = 14, EmitDefaultValue = false)]
            public int ApplicationsItemDelaySeconds { get; set; }

            /// <summary>「自启动应用」：网页之间的间隔（秒）。</summary>
            [DataMember(Order = 15, EmitDefaultValue = false)]
            public double? ApplicationsWebPageDelaySecondsValue { get; set; }

            /// <summary>
            /// 用户最后一次手动选中的工作方案 Id（2026-10-20）。
            /// 单独再存一份：即使工作方案.json 里的 ActiveProfileId 因为整理文件夹/迁移/异常丢失，
            /// 下次启动也能回到用户选过的那份，而不是退回“最新方案”。
            /// </summary>
            [DataMember(Order = 16, EmitDefaultValue = false)]
            public string LastActiveProfileId { get; set; }

            [IgnoreDataMember]
            public double ApplicationsWebPageDelaySeconds
            {
                get { return ApplicationsWebPageDelaySecondsValue.HasValue ? ApplicationsWebPageDelaySecondsValue.Value : 0.5; }
                set { ApplicationsWebPageDelaySecondsValue = value; }
            }

            [IgnoreDataMember]
            public bool ApplicationsFirst
            {
                // 用户 2026-10-19 默认：先启动「自启动应用」的方案（没配置过也按 1 处理）。
            get { return !StartupPriorityValue.HasValue || StartupPriorityValue.Value == 1; }
                set { StartupPriorityValue = value ? 1 : 0; }
            }

            /// <summary>图标位置保存：打开程序后是否自动恢复桌面图标布局。默认关闭，和另外两个独立。</summary>
            [DataMember(Order = 17, EmitDefaultValue = false)]
            public bool LayoutAutoRestore { get; set; }

            /// <summary>图标位置保存的“启动前等待”，旧设置没有这个成员时按 5 秒。</summary>
            [DataMember(Order = 18, EmitDefaultValue = false)]
            public int? LayoutInitialDelaySecondsValue { get; set; }

            [IgnoreDataMember]
            public int LayoutInitialDelaySeconds
            {
                get { return LayoutInitialDelaySecondsValue.HasValue ? LayoutInitialDelaySecondsValue.Value : InitialDelayDefaultSeconds; }
                set { LayoutInitialDelaySecondsValue = value; }
            }

            /// <summary>界面语言代码：zh-CN / en-US。旧设置没有这个成员时按 zh-CN。</summary>
            [DataMember(Order = 21, EmitDefaultValue = false)]
            public string Language { get; set; }

            /// <summary>主题代码：light / dark / system。旧设置没有这个成员时按 light。</summary>
            [DataMember(Order = 22, EmitDefaultValue = false)]
            public string Theme { get; set; }

            /// <summary>
            /// 用户自定义的全局快捷键（一条命令可以有多条）。
            /// null = 从没改过，使用 ShortcutCatalog 的默认绑定；
            /// 空列表 = 用户确实把快捷键都删了，不能偷偷恢复默认。
            /// </summary>
            [DataMember(Order = 23, EmitDefaultValue = false)]
            public List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> ShortcutBindings { get; set; }

            /// <summary>
            /// 拖动前缀的主键名（2026-11-16）。“None” = 只有修饰键；null = 默认 Shift。
            /// </summary>
            [DataMember(Order = 24, EmitDefaultValue = false)]
            public string DragGestureKey { get; set; }

            /// <summary>拖动前缀的修饰键位组合；null = 默认 Shift。</summary>
            [DataMember(Order = 25, EmitDefaultValue = false)]
            public int? DragGestureModifiers { get; set; }

            /// <summary>整块拖动用哪个鼠标键：“Left” / “Right”；null = 默认左键。</summary>
            [DataMember(Order = 26, EmitDefaultValue = false)]
            public string DragGestureMouseButton { get; set; }

            /// <summary>
            /// 「启动与恢复」新建工作方案时用的名字（设置 → 启动与恢复 → 新工作方案名）。
            /// null/空白 = 没自定义，运行时用默认名「工作方案」。
            /// </summary>
            [DataMember(Order = 27, EmitDefaultValue = false)]
            public string NewStartupProfileName { get; set; }

            /// <summary>「自启动应用」新建自启动方案时用的名字；null = 默认名「自启动方案」。</summary>
            [DataMember(Order = 28, EmitDefaultValue = false)]
            public string NewApplicationPlanName { get; set; }

            /// <summary>「图标位置保存」保存新布局时用的名字；null = 默认名「图标布局」。</summary>
            [DataMember(Order = 29, EmitDefaultValue = false)]
            public string NewLayoutName { get; set; }

            /// <summary>
            /// 设置 → 快捷键 → 分页条“每页”条数。null = 从没改过，按默认 10 条。
            /// 以前只存在分页条自己的字段里，所以每次打开都回到 10；现在跟其它设置一起写 settings.json。
            /// </summary>
            [DataMember(Order = 30, EmitDefaultValue = false)]
            public int? ShortcutPageSizeValue { get; set; }

            [IgnoreDataMember]
            public int ShortcutPageSize
            {
                get
                {
                    return ShortcutPageSizeValue.HasValue
                        ? ShortcutPageSizeValue.Value
                        : ShortcutPageSettings.DefaultPageSize;
                }
                set { ShortcutPageSizeValue = value; }
            }
        }

        /// <summary>自启动应用的等待（可被“取消恢复”打断，按 100ms 检查一次）。</summary>
        private void SleepApplicationsDelay(double seconds)
        {
            int totalMs = (int)Math.Round(Math.Max(0, seconds) * 1000.0);
            int waited = 0;
            while (waited < totalMs
                && !applicationsStartCancelled
                && !applicationsCountdownSkipped)
            {
                int step = Math.Min(100, totalMs - waited);
                Thread.Sleep(step);
                waited += step;
            }
        }

        /// <summary>这一项是不是网页（只有网页才用“网页间隔”）。</summary>
        private static bool IsWebTargetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            return path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 【无 WPF 的同步兼容路径】自启动应用的“高负载时等待”。
        /// WPF 下手动/自动自启动恢复都走 `ApplicationPlanRestoreBridge` → `SessionRestoreService`，
        /// 由共用负载门弹出 CPU/内存浮窗；这里只保留给没有 WPF Application 的同步测试/兼容入口。
        /// </summary>
        private void WaitForApplicationsLoad(bool reportProgress)
        {
            DateTime startedUtc = DateTime.UtcNow;
            int timeoutSeconds = Clamp(Settings.LoadWaitTimeoutSeconds, 0, 3600);
            SystemLoadMonitor monitor = new SystemLoadMonitor();
            while (!applicationsStartCancelled)
            {
                SystemLoadSnapshot load = null;
                try
                {
                    load = monitor.ReadAsync(TimeSpan.FromMilliseconds(400), CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception)
                {
                    return;   // 读不到负载就当作“可以继续”，不挡启动
                }
                if (load == null || !load.IsAvailable || load.CpuPercent <= Settings.MaximumCpuPercent)
                {
                    return;
                }
                if (timeoutSeconds > 0
                    && (DateTime.UtcNow - startedUtc).TotalSeconds >= timeoutSeconds)
                {
                    return;
                }
                if (reportProgress)
                {
                    StatusText = AppText.F(
                        ShellTextKeys.StatusCpuWaitingFormat,
                        (int)load.CpuPercent,
                        (int)Settings.MaximumCpuPercent);
                }
            }
        }

        private sealed class DisabledRestoreService : ISessionRestoreService
        {
            public async Task<RestoreResult> RestoreAsync(SessionSnapshot snapshot, RestoreOptions options, IProgress<RestoreProgress> progress, CancellationToken cancellationToken)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                if (progress != null)
                {
                    progress.Report(new RestoreProgress
                    {
                        State = RestoreProgressState.Completed,
                        ItemNumber = snapshot == null || snapshot.Items == null ? 0 : snapshot.Items.Count,
                        TotalItems = snapshot == null || snapshot.Items == null ? 0 : snapshot.Items.Count,
                        Message = AppText.T(ShellTextKeys.ErrorRestoreServiceUnavailable)
                    });
                }
                return new RestoreResult
                {
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow
                };
            }
        }

        private sealed class DisabledDesktopApplicationService : IDesktopApplicationService
        {
            public IList<DesktopEntry> Scan() { return new List<DesktopEntry>(); }

            public LaunchResult Launch(DesktopEntry entry)
            {
                return new LaunchResult { Success = false, Error = AppText.T(ShellTextKeys.ErrorDesktopLaunchServiceUnavailable) };
            }
        }
    }
}
