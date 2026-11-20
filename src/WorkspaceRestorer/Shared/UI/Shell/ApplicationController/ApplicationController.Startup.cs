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
        /// 【2026-10-18】给页面用的状态写入口：`StatusText` 的 set 是私有的，页面在“数据改完、界面刷完”
        /// 之后想自己补一句结论时走这里，免得那句话被控制器自己的默认文案覆盖。
        /// </summary>
        public void ReportStatus(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                StatusText = message;
            }
        }

        public int CountdownSeconds
        {
            get { return countdownSeconds; }
            private set
            {
                countdownSeconds = Math.Max(0, value);
                OnPropertyChanged("CountdownSeconds");
            }
        }

        /// <summary>是否正在显示“CPU/内存超限，是否继续恢复”浮窗。</summary>
        public bool LoadPromptVisible
        {
            get { return loadPromptVisible; }
            private set
            {
                if (loadPromptVisible == value)
                {
                    return;
                }
                loadPromptVisible = value;
                OnPropertyChanged("LoadPromptVisible");
            }
        }

        /// <summary>浮窗文字（当前值/上限/已等待/剩余秒数，全部实时更新）。</summary>
        public string LoadPromptText
        {
            get { return loadPromptText ?? string.Empty; }
            private set
            {
                string next = value ?? string.Empty;
                if (string.Equals(loadPromptText, next, StringComparison.Ordinal))
                {
                    return;
                }
                loadPromptText = next;
                OnPropertyChanged("LoadPromptText");
            }
        }

        public int ProgressValue
        {
            get { return progressValue; }
            private set
            {
                progressValue = Math.Max(0, value);
                OnPropertyChanged("ProgressValue");
            }
        }

        public int ProgressMaximum
        {
            get { return progressMaximum; }
            private set
            {
                progressMaximum = Math.Max(0, value);
                OnPropertyChanged("ProgressMaximum");
            }
        }

        public bool HasSnapshot
        {
            get { return LastSnapshot != null && LastSnapshot.Items != null && LastSnapshot.Items.Count > 0; }
        }
        /// <summary>
        /// 程序启动后的收尾：清过期回收站、清掉不属于本次开机的“已恢复”标记，
        /// 然后按设置决定要不要自动恢复工作方案 / 自动启动自启动方案。
        ///
        /// 【2026-10-19 用户报 bug 改的】原来带一个参数：登录自启（--startup）传 false 就整段跳过，
        /// 结果是“勾了「启动程序后自动恢复工作方案」和「启动程序后自动启动自启动方案」，开机后都不执行”。
        /// 现在没有这个参数：**手动打开和登录自启完全一样**，只由那两个开关决定做不做。
        /// </summary>
        public void BeginStartup()
        {
            // 先把 workflows 里“程序还不认识”的文件夹处理掉（能读的收编、读不出的搬到“无法识别”），
            // 再开始恢复；这样刚复制进来的方案文件夹这次启动就能看到，不会被当成垃圾。
            RunMirrorIntake();
            // 回收站里过了保留天数的方案在启动时彻底删掉，不占空间。
            PurgeExpiredRecycleBinEntries();
            // 标记按“本次开机/登录会话”判断：跨天不重置，只有关机/重启后旧标记才作废。
            // 清理时保留同一次开机的旧日期标记，删掉重启前的旧标记。
            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.CleanupStale();
            // 用户要求（2026-10-10 03:12）：两个分区都有自己的“打开程序后自动…”开关，
            // 同时打开时会抢顺序，所以「总设置 → 优先启动项」决定先做哪一个。
            // 【2026-10-19】自启动应用加了等待/间隔后要异步跑，否则开机那 10 秒界面是冻住的；
            // 单元测试没有 WPF Application，仍走同步版（否则测试还没断言，工作已经在后台跑完了）。
            if (System.Windows.Application.Current == null)
            {
                BeginStartupSynchronously();
            }
            else
            {
                BeginStartupAsynchronously();
            }
        }

        private void BeginStartupSynchronously()
        {
            // 图标位置保存有自己的开关/等待；从启动开始独立计时，
            // 不再等「工作方案 / 自启动应用」整段恢复结束后才启动。
            ScheduleStartupDesktopRestore();
            if (Settings.ApplicationsFirst)
            {
                StartApplicationsIfEnabled();
                RestoreStartupOnLaunch();
            }
            else
            {
                RestoreStartupOnLaunch();
                StartApplicationsIfEnabled();
            }
        }

        private async void BeginStartupAsynchronously()
        {
            // 【2026-10-20 用户报“两个恢复弹窗打架/没有读秒”】严格串行：
            //   优先「自启动应用」：先等自启动方案恢复完（含启动前等待的读秒），再开始工作方案；
            //   优先「启动与恢复」：反过来。
            // 每一段之间让 UI 把“完成/开始”事件跑完，避免两张恢复卡片同时出现。
            AppLog.Write("[自动恢复] 序列开始：优先=" + (Settings.ApplicationsFirst ? "自启动应用" : "启动与恢复"));
            AutoStartupChaining = Settings.AutoRestore && Settings.ApplicationsAutoRestore;
            // 图标位置保存的开关/等待独立：从启动这一拍开始计时，
            // 不等下面两段工作/应用恢复跑完。以前放在 finally 里会让布局被串行恢复拖住。
            ScheduleStartupDesktopRestore();
            try
            {
                if (Settings.ApplicationsFirst)
                {
                    AppLog.Write("[自动恢复] 第一段：自启动应用");
                    if (!await StartApplicationsIfEnabledAsync())
                    {
                        return;
                    }
                    AppLog.Write("[自动恢复] 第一段结束，准备切工作方案");
                    await SettleUiAsync();
                    AutoStartupChaining = false;
                    AppLog.Write("[自动恢复] 第二段：工作方案");
                    await RestoreStartupOnLaunchAsync();
                }
                else
                {
                    AppLog.Write("[自动恢复] 第一段：工作方案");
                    if (!await RestoreStartupOnLaunchAsync())
                    {
                        return;
                    }
                    AppLog.Write("[自动恢复] 第一段结束，准备切自启动应用");
                    await SettleUiAsync();
                    AutoStartupChaining = false;
                    AppLog.Write("[自动恢复] 第二段：自启动应用");
                    await StartApplicationsIfEnabledAsync();
                }
            }
            finally
            {
                AutoStartupChaining = false;
                AppLog.Write("[自动恢复] 序列结束");
            }
        }

        /// <summary>
        /// 让界面把已经排队的“恢复开始/完成”事件处理完，再进入下一段，
        /// 避免自启动应用的完成横幅和工作方案的恢复卡片同时出现。
        /// </summary>
        private static async Task SettleUiAsync()
        {
            System.Windows.Application app = System.Windows.Application.Current;
            if (app == null)
            {
                await Task.Yield();
                return;
            }
            await app.Dispatcher.InvokeAsync(
                new Action(delegate { }),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>测试/无界面环境用的同步版（和加等待之前一样，不等待）。</summary>
        private void StartApplicationsIfEnabled()
        {
            if (!Settings.ApplicationsAutoRestore)
            {
                return;
            }
            if (WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature))
            {
                StatusText = AppText.T(ShellTextKeys.StatusApplicationsAlreadyStartedToday);
                return;
            }
            if (CountEnabledApplicationItems(ActiveApplicationPlan) <= 0)
            {
                return;
            }
            StartApplicationsOnLaunch(true, true);
            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.MarkDoneToday(
                WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature);
        }

        /// <summary>自启动方案里“勾选了、而且有路径”的项数。</summary>
        private static int CountEnabledApplicationItems(ApplicationPlan plan)
        {
            if (plan == null || plan.Items == null)
            {
                return 0;
            }
            int count = 0;
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem item = plan.Items[index];
                if (item != null && item.IsEnabled && !string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>「启动与恢复」那一半：打开程序时是否自动恢复工作方案（含图标布局的补做）。</summary>
        private void RestoreStartupOnLaunch()
        {
            // 测试/无界面环境用的同步入口（和原来一样，等待恢复完成）。
            RestoreStartupOnLaunchAsync().GetAwaiter().GetResult();
        }

        /// <summary>「启动与恢复」那一半：打开程序时是否自动恢复工作方案（含图标布局的补做）。</summary>
        private async Task<bool> RestoreStartupOnLaunchAsync()
        {
            if (!Settings.AutoRestore)
            {
                return true;
            }
            // 用户 2026-10-22 最新要求：同一次开机里关掉程序再打开不要重复恢复；
            // 跨天但不关机也不能重置；只有电脑关机/重启后旧标记才作废。
            // 判断依据是标记里的“本次开机/登录会话”，不是日期；恢复完成后记上一笔。
            // （这个标记也管「自启动应用」那一路，两件事各记一笔，互不遮挡。）
            if (WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                WorkspaceRestorer.Infrastructure.AutoRestoreMarker.WorkProfileFeature))
            {
                StatusText = AppText.T(ShellTextKeys.StatusAlreadyRestoredToday);
                return true;
            }
            bool cancelled = false;
            bool attempted = false;
            StartupProfile activeProfile = ActiveProfile;
            if (activeProfile != null)
            {
                if (activeProfile.Items != null && activeProfile.Items.Count > 0)
                {
                    // 【2026-11-17 用户要求】标记由共用恢复入口在“恢复倒计时卡片出现”时写，
                    // 不再等恢复结束；这里只把“这次属于工作方案”告诉它。
                    RestoreResult result = await RestoreProfileAsync(
                        activeProfile, false,
                        WorkspaceRestorer.Infrastructure.AutoRestoreMarker.WorkProfileFeature);
                    cancelled = result != null && result.IsCancelled;
                    attempted = true;
                }
                else
                {
                    StatusText = AppText.T(ShellTextKeys.StatusCurrentProfileEmpty);
                }
            }
            else if (HasSnapshot)
            {
                RestoreResult result = await RestoreAsync(
                    false, WorkspaceRestorer.Infrastructure.AutoRestoreMarker.WorkProfileFeature);
                cancelled = result != null && result.IsCancelled;
                attempted = true;
            }
            else
            {
                StatusText = AppText.T(ShellTextKeys.StatusNoRestorableSession);
            }

            // Explorer may still be rebuilding its desktop view during logon.
            // 图标布局由 BeginStartup 一开始就独立调度（它有自己的开关/等待，不能被工作方案开关挡住）。
            // 【2026-11-17 用户要求】标记已经在恢复卡片出现时写过了，这里不再重复写。
            return !cancelled || !attempted;
        }

        /// <summary>
        /// 【兼容/测试路径】旧的逐项 LaunchPath 启动。
        ///
        /// 2026-10-30 起，真实 WPF 界面（手动「立即恢复」和自动启动异步那一路）走
        /// `Applications/Restore/ApplicationPlanRestoreBridge` → 共用恢复内核。
        /// 这个方法只保留给无 WPF 的同步单元测试/兼容调用；不要再在界面里新增调用。
        /// </summary>
        public int StartApplicationsOnLaunch()
        {
            // 手动点「立即恢复」和“打开程序时自动启动”都走这里；默认带进度卡片。
            // 手动入口也明确要求读“启动前等待”，不再用默认值隐式跳过。
            return StartApplicationsOnLaunch(true, false);
        }

        /// <summary>
        /// 【2026-10-19 用户要求】“自启动应用的立即恢复的代码继续抄「启动与恢复」的交互，
        /// 例如正在做的显示在前面，并且有取消恢复。”
        /// → 这个方法和「启动与恢复」共用同一套界面事件：
        ///   开始：`RestoreStarted`（主窗口弹出“正在启动…”卡片并置顶）；
        ///   过程：`StatusText` 写“正在启动：X”、`ProgressValue/ProgressMaximum` 推进度条（卡片上显示“第 x / N 项”）；
        ///   结束：`RestoreCompleted`（关卡片 + 完成横幅，取消时写“已取消恢复”）。
        /// `reportProgress=false` 只给“不想弹卡片”的内部调用用。
        ///
        /// 注意：`Settings.ApplicationsAutoRestore` 只决定“打开程序时要不要自动启动”，
        /// **不能**挡手动点「立即恢复」（真正 bug 见下面注释）。
        /// </summary>
        public int StartApplicationsOnLaunch(bool reportProgress, bool skipInitialDelay = false)
        {
            // `Settings.ApplicationsAutoRestore` 只决定“打开程序时要不要自动启动”，
            // **不能**挡手动点“立即恢复”：那个开关管的是自动那一路。
            // （真 bug：原来这里把开关也当成了手动入口的闸门，于是没开自动启动时，
            //   点“立即恢复”什么也不做，界面还显示“跳过未勾选 N 项”，看着像“不会跳过”。）
            if (applicationsStarting)
            {
                return 0;
            }
            ApplicationPlan plan = ActiveApplicationPlan;
            if (plan == null || plan.Items == null || plan.Items.Count == 0)
            {
                return 0;
            }
            int planned = CountEnabledApplicationItems(plan);
            // 进入新一段恢复：先清掉上一段可能残留的倒计时，避免新标题配旧秒数。
            CountdownSeconds = 0;
            applicationsCountdownSkipped = false;
            applicationsStarting = true;
            applicationsRestoreRunning = reportProgress;
            if (reportProgress)
            {
                ProgressMaximum = Math.Max(1, planned);
                ProgressValue = 0;
                StatusText = AppText.F(
                    ShellTextKeys.StatusApplicationsRestoringFormat,
                    plan.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan));
                RaiseRestoreStarted();
            }
            int started = 0;
            int failed = 0;
            int skipped = 0;
            int done = 0;
            bool cancelled = false;
            // 用户 2026-10-15 的要求原本只落在「启动与恢复」上：完成横幅要说清“到底是哪些没恢复、为什么”。
            // 【2026-10-19 补齐】自启动方案这一边也照做，不再只给一个“失败 N 项”的数字。
            List<RestoreFailure> failures = new List<RestoreFailure>();
            try
            {
                // 2026-10-19：自启动应用也有“启动前等待”。
                // 2026-10-20：等待期间像「启动与恢复」一样逐秒显示倒计时，恢复卡片上能看到还剩几秒。
                // 2026-10-21：手动点「立即恢复」也不再跳过；只有测试/无界面同步路径显式传 true。
                if (!skipInitialDelay && Settings.ApplicationsInitialDelaySeconds > 0)
                {
                    int waitSeconds = Clamp(Settings.ApplicationsInitialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
                    for (int remaining = waitSeconds; remaining > 0
                        && !applicationsStartCancelled
                        && !applicationsCountdownSkipped; remaining--)
                    {
                        CountdownSeconds = remaining;
                        if (reportProgress)
                        {
                            StatusText = AppText.F(ShellTextKeys.StatusApplicationsCountdownFormat, remaining);
                        }
                        SleepApplicationsDelay(1.0);
                    }
                    CountdownSeconds = 0;
                }
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    if (applicationsStartCancelled)
                    {
                        cancelled = true;
                        break;
                    }
                    ApplicationPlanItem item = plan.Items[index];
                    if (item == null || !item.IsEnabled || string.IsNullOrWhiteSpace(item.TargetPath))
                    {
                        continue;
                    }
                    if (reportProgress)
                    {
                        StatusText = AppText.F(
                            ShellTextKeys.StatusApplicationsStartingItemFormat,
                            string.IsNullOrWhiteSpace(item.DisplayName)
                                ? item.TargetPath
                                : item.DisplayName);
                    }
                    // 用户 2026-10-19：高负载时先等 CPU 降下来（和启动与恢复同一套 CPU 上限/等待上限）。
                    if (Settings.ApplicationsWaitForLoad && !applicationsStartCancelled)
                    {
                        WaitForApplicationsLoad(reportProgress);
                    }
                    if (applicationsStartCancelled)
                    {
                        cancelled = true;
                        break;
                    }
                    try
                    {
                        LaunchResult result = LaunchApplicationPlanItem(item);
                        if (result != null && result.WasAlreadyOpen)
                        {
                            // 用户第 62、63 条：“发现已经恢复的要跳过，而不是重新打开。”
                            skipped++;
                        }
                        else if (result != null && result.Success)
                        {
                            started++;
                        }
                        else
                        {
                            failed++;
                            failures.Add(new RestoreFailure
                            {
                                Name = DescribeApplicationItemName(item),
                                KindText = AppText.T(ShellTextKeys.RestoreKindApplication),
                                // 【2026-10-19】和「启动与恢复」用同一套中文翻译，不再把启动器的英文原样贴给用户。
                                Reason = result != null && !string.IsNullOrWhiteSpace(result.Error)
                                    ? RestoreFailureText.DescribeError(result.Error)
                                    : AppText.T(ShellTextKeys.FallbackStartFailureNoReason)
                            });
                        }
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        failures.Add(new RestoreFailure
                        {
                            Name = DescribeApplicationItemName(item),
                            KindText = AppText.T(ShellTextKeys.RestoreKindApplication),
                            Reason = AppText.F(
                                ShellTextKeys.ErrorApplicationStartFailedFormat,
                                exception.Message)
                        });
                        AppLog.Write("启动自启动方案里的项目失败：" + (item.DisplayName ?? item.TargetPath), exception);
                    }
                    done++;
                    if (reportProgress)
                    {
                        ProgressValue = done;
                    }
                    // 项目间隔 / 网页间隔：最后一项之后不再等（避免恢复尾巴拖长）。
                    if (index < plan.Items.Count - 1 && !applicationsStartCancelled)
                    {
                        double interval = IsWebTargetPath(item.TargetPath)
                            ? Settings.ApplicationsWebPageDelaySeconds
                            : Settings.ApplicationsItemDelaySeconds;
                        if (interval > 0)
                        {
                            SleepApplicationsDelay(interval);
                        }
                    }
                }
            }
            finally
            {
                LastApplicationsStartCancelled = cancelled;
                CountdownSeconds = 0;
                applicationsStarting = false;
                applicationsStartCancelled = false;
                applicationsRestoreRunning = false;
            }
            if (started > 0 || failed > 0 || skipped > 0)
            {
                LastApplicationsStartSkipped = skipped;
                StatusText = AppText.F(
                    ShellTextKeys.StatusApplicationsStartedFormat,
                    plan.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan),
                    started)
                    + (skipped > 0
                        ? AppText.F(ShellTextKeys.StatusApplicationsStartedSkippedFormat, skipped)
                        : string.Empty)
                    + (failed > 0
                        ? AppText.F(ShellTextKeys.StatusApplicationsStartedFailedFormat, failed)
                        : string.Empty)
                    + AppText.T(ShellTextKeys.StatusApplicationsStartedEnd);
            }
            if (reportProgress)
            {
                RaiseApplicationsRestoreCompleted(planned, started, failed, skipped, cancelled, failures);
            }
            return started;
        }

        /// <summary>完成横幅里显示的名字：优先用项目名，没有名字就用路径。</summary>
        private static string DescribeApplicationItemName(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return AppText.T(ShellTextKeys.FallbackUnknownItem);
            }
            if (!string.IsNullOrWhiteSpace(item.DisplayName))
            {
                return item.DisplayName.Trim();
            }
            return string.IsNullOrWhiteSpace(item.TargetPath)
                ? AppText.T(ShellTextKeys.FallbackUnknownItem)
                : item.TargetPath.Trim();
        }

        /// <summary>“自启动方案正在逐项启动”的标记：主窗口据此把恢复卡片上的字换成“正在启动…”。</summary>
        public bool ApplicationsRestoreInProgress
        {
            get { return applicationsRestoreRunning; }
        }

        /// <summary>
        /// 两个自动恢复正在**串行**执行：第一个完成时先不弹完成横幅，
        /// 免得横幅和第二个恢复卡片同时出现（用户报的“两个恢复窗口打架”）。
        /// 第二段开始前会置回 false，最后一段的完成横幅正常显示。
        /// </summary>
        public bool AutoStartupChaining { get; private set; }

        private bool applicationsRestoreRunning;

        /// <summary>「取消恢复」对自启动方案那一半也生效（正在逐项启动时立标记）。</summary>
        public void CancelApplicationsStart()
        {
            applicationsStartCancelled = true;
        }

        private bool applicationsStarting;
        private volatile bool applicationsStartCancelled;

        /// <summary>上一次自动/手动启动自启动方案时，因为“已经打开”而跳过的项数（界面提示用）。</summary>
        public int LastApplicationsStartSkipped { get; private set; }

        /// <summary>上一次自启动方案启动是否被「取消恢复」中止（串行恢复用）。</summary>
        public bool LastApplicationsStartCancelled { get; private set; }

        /// <summary>
        /// 上一次保存时，“扫描终端与本地网址”这个开关给出的提示（例如“已跳过 2 项终端/本地网址…”）。
        /// 提交口 `CommitCapturedSnapshot` 会写它；保存流程在覆盖状态栏文字时要把它带上，
        /// 否则用户看不到“为什么 ComfyUI 那一条不见了”。
        /// </summary>
        public string LastCapturePolicyHint { get; private set; }

    }
}
