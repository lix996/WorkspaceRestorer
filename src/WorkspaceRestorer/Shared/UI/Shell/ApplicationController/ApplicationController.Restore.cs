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
        public Task<RestoreResult> RestoreAsync(bool skipInitialDelay, string autoStartupFeature = null)
        {
            ThrowIfDisposed();
            StartupProfile active = ActiveProfile;
            if (active != null)
            {
                return RestoreSnapshotAsync(
                    BuildProfileRestoreSnapshot(active), skipInitialDelay, null, autoStartupFeature);
            }
            return RestoreSnapshotAsync(LastSnapshot, skipInitialDelay, null, autoStartupFeature);
        }

        public Task<RestoreResult> RestoreProfileAsync(StartupProfile profile)
        {
            return RestoreProfileAsync(profile, false);
        }

        public Task<RestoreResult> RestoreProfileAsync(
            StartupProfile profile, bool skipInitialDelay, string autoStartupFeature = null)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                StatusText = AppText.T(ShellTextKeys.StatusProfileMissing);
                return Task.FromResult<RestoreResult>(null);
            }
            if (stored.IsFolder && PlanFolderTree.ChildCount(startupProfiles, stored.Id) == 0)
            {
                StatusText = AppText.T(ShellTextKeys.StatusFolderEmptyNoRestore);
                return Task.FromResult<RestoreResult>(null);
            }
            // 【2026-10-24】先确认这次恢复真的能开始，再切当前方案：
            // 以前先 SetActiveProfile，恢复被并发操作拒绝时，当前方案已经变了。
            if (IsRestoring || sessionCaptureInProgress != 0 || restoringDesktopLayout)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotSwitchProfileBusy);
                return Task.FromResult<RestoreResult>(null);
            }

            SetActiveProfile(stored);
            return RestoreSnapshotAsync(
                BuildProfileRestoreSnapshot(stored), skipInitialDelay, null, autoStartupFeature);
        }

        /// <summary>
        /// 把一棵方案树拍平成现有恢复服务认识的清单：
        /// 当前方案自己的启动项 → 子方案从上到下 → 每个子方案先自己的项，再递归。
        /// 纯文件夹没有自己的项，直接进入子方案。
        /// </summary>
        public SessionSnapshot BuildProfileRestoreSnapshot(StartupProfile root)
        {
            SessionSnapshot snapshot = new SessionSnapshot();
            snapshot.CapturedAtUtc = DateTime.UtcNow;
            snapshot.Items = new List<SessionItem>();
            AppendProfileRestoreItems(
                root,
                snapshot.Items,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            for (int index = 0; index < snapshot.Items.Count; index++)
            {
                snapshot.Items[index].Sequence = index + 1;
            }
            return snapshot;
        }

        private void AppendProfileRestoreItems(
            StartupProfile profile,
            IList<SessionItem> destination,
            HashSet<string> visited)
        {
            if (profile == null || destination == null)
            {
                return;
            }
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(stored.Id) && !visited.Add(stored.Id))
            {
                return;
            }
            if (!stored.IsFolder && stored.Items != null)
            {
                for (int index = 0; index < stored.Items.Count; index++)
                {
                    if (stored.Items[index] != null)
                    {
                        destination.Add(stored.Items[index]);
                    }
                }
            }
            List<StartupProfile> children = PlanFolderTree.ChildrenOf(startupProfiles, stored.Id);
            for (int index = 0; index < children.Count; index++)
            {
                AppendProfileRestoreItems(children[index], destination, visited);
            }
        }

        public Task<RestoreResult> RestoreStartupProfile(StartupProfile profile, bool skipInitialDelay)
        {
            return RestoreProfileAsync(profile, skipInitialDelay);
        }

        public Task<RestoreResult> RestoreStartupProfile(StartupProfile profile)
        {
            return RestoreProfileAsync(profile, false);
        }

        private Task<RestoreResult> RestoreSnapshotAsync(SessionSnapshot snapshot, bool skipInitialDelay)
        {
            return RestoreSnapshotAsync(snapshot, skipInitialDelay, null, null);
        }

        /// <summary>
        /// 共用恢复编排。optionsOverride 为 null 时仍按「启动与恢复」的设置；
        /// 自启动应用转接会传入自己的 RestoreOptions，保持它原有等待/间隔设置。
        /// </summary>
        private async Task<RestoreResult> RestoreSnapshotAsync(
            SessionSnapshot snapshot,
            bool skipInitialDelay,
            RestoreOptions optionsOverride,
            string autoStartupFeature = null)
        {
            if (IsRestoring)
            {
                return null;
            }
            // 【2026-10-24】保存/后台快照进行中也不能开始恢复：两边互斥，否则会抓到半截状态。
            if (sessionCaptureInProgress != 0 || restoringDesktopLayout)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotRestoreWhileBusy);
                return null;
            }

            if (snapshot == null || snapshot.Items == null || snapshot.Items.Count == 0)
            {
                StatusText = AppText.T(ShellTextKeys.StatusNoRestorableSession);
                return null;
            }

            IsRestoring = true;
            restoreCancellation = new CancellationTokenSource();
            CancellationToken token = restoreCancellation.Token;
            restoreLoadGate = new RestoreLoadGate();
            loadPromptAllowed = true;
            HideLoadPrompt();
            ProgressMaximum = snapshot.Items.Count;
            ProgressValue = 0;
            CountdownSeconds = 0;
            // 【2026-10-19】先数一遍“这个方案里有多少项是终端/本地网址”：恢复卡片会如实说明
            // “这类东西「启动与恢复」恢复不了”，不要等它们失败了才靠一句英文错误去猜。
            RestoreNonRestorableCount = 0;
            for (int index = 0; index < snapshot.Items.Count; index++)
            {
                if (TerminalAndLocalUrlPolicy.IsExcludedKind(snapshot.Items[index]))
                {
                    RestoreNonRestorableCount++;
                }
            }
            RestoreOptions options = optionsOverride ?? BuildRestoreOptions(skipInitialDelay);
            options.LoadGate = restoreLoadGate;
            RestoreResult result = null;
            bool completionRaised = false;

            try
            {
                // Mirror the explicit save flow: raise the start event so the
                // window comes to the front and shows the "正在恢复…" card.
                restoreCountdownSkipped = false;
                RaiseRestoreStarted();
                // 【2026-11-17 用户要求】恢复倒计时卡片一出现就写“本次开机已做过”标记：
                // 不等恢复完成，也不管成功、失败、取消；只要卡片出现过，这次开机就不再重复自动恢复。
                // 只有打开程序后的自动恢复会传 autoStartupFeature；手动「立即恢复」传 null，不写。
                if (!string.IsNullOrWhiteSpace(autoStartupFeature))
                {
                    AutoRestoreMarker.MarkDoneToday(autoStartupFeature);
                    AppLog.Write("[自动恢复] 已写本次开机标记：" + autoStartupFeature);
                }

                if (!skipInitialDelay)
                {
                    int seconds = options.InitialDelay <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(options.InitialDelay.TotalSeconds);
                    for (int remaining = seconds; remaining > 0 && !restoreCountdownSkipped; remaining--)
                    {
                        token.ThrowIfCancellationRequested();
                        CountdownSeconds = remaining;
                        StatusText = AppText.F(ShellTextKeys.StatusRestoreCountdownFormat, remaining);
                        // 100ms 粒度，点“立即恢复”最多等一小段就进入恢复，
                        // 不再把当前这一整秒等完。
                        for (int tick = 0; tick < 10 && !restoreCountdownSkipped; tick++)
                        {
                            await Task.Delay(100, token).ConfigureAwait(true);
                        }
                    }
                    CountdownSeconds = 0;
                }

                // The controller renders the initial countdown so the service
                // receives zero here and never waits twice.
                options.InitialDelay = TimeSpan.Zero;
                IProgress<RestoreProgress> progress = new Progress<RestoreProgress>(UpdateRestoreProgress);
                result = await restoreService.RestoreAsync(snapshot, options, progress, token).ConfigureAwait(true);
                if (result == null)
                {
                    StatusText = AppText.T(ShellTextKeys.StatusRestoreNoResult);
                    result = new RestoreResult
                    {
                        StartedAtUtc = DateTime.UtcNow,
                        FinishedAtUtc = DateTime.UtcNow
                    };
                    RaiseRestoreCompleted(result, false);
                    completionRaised = true;
                }
                else if (result.IsCancelled)
                {
                    StatusText = string.IsNullOrWhiteSpace(result.CancelReason)
                        ? AppText.T(ShellTextKeys.StatusRestoreCancelled)
                        : result.CancelReason;
                }
                else
                {
                    int success = CountSuccessfulItems(result);
                    int browserWindowClosed = CountBrowserWindowClosedItems(result);
                    int browserWindowUserMinimized = CountBrowserWindowUserMinimizedItems(result);
                    StatusText = RestoreText.ForStatus(new RestoreCompletion
                    {
                        Source = applicationsRestoreRunning
                            ? RestoreSource.Applications
                            : RestoreSource.StartupRestore,
                        TotalCount = result.Items.Count,
                        SuccessCount = success,
                        BrowserWindowClosedCount = browserWindowClosed,
                        BrowserWindowUserMinimizedCount = browserWindowUserMinimized,
                        // 保存为最小化的单页窗口没读到标题时，状态栏也要如实说明
                        // “页面未就绪、窗口保持原样”，不能只写“成功 N / N 项”。
                        BrowserWindowNotReadyCount = result.BrowserWindowNotReadyCount
                    });
                }
                if (!completionRaised && result != null)
                {
                    RaiseRestoreCompleted(result, result.IsCancelled);
                    completionRaised = true;
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                StatusText = AppText.T(ShellTextKeys.StatusRestoreCancelled);
                result = new RestoreResult
                {
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow,
                    IsCancelled = true
                };
                RaiseRestoreCompleted(result, true);
                completionRaised = true;
                return result;
            }
            catch (Exception exception)
            {
                StatusText = AppText.F(ShellTextKeys.StatusRestoreFailedFormat, exception.Message);
                AppLog.Write("Unable to restore the saved session.", exception);
                // Always close the progress card even when a provider or
                // launcher throws before the restore service can return a
                // result.  The partial result keeps any items already
                // attempted visible to the completion summary.
                if (result == null)
                {
                    result = new RestoreResult
                    {
                        StartedAtUtc = DateTime.UtcNow,
                        FinishedAtUtc = DateTime.UtcNow
                    };
                }
                RaiseRestoreCompleted(result, false);
                completionRaised = true;
                return result;
            }
            finally
            {
                if (!completionRaised && result != null)
                {
                    RaiseRestoreCompleted(result, result.IsCancelled);
                }
                CountdownSeconds = 0;
                loadPromptAllowed = false;
                HideLoadPrompt();
                restoreLoadGate = null;
                IsRestoring = false;
                if (restoreCancellation != null)
                {
                    restoreCancellation.Dispose();
                    restoreCancellation = null;
                }
            }
        }

        public void CancelRestore()
        {
            loadPromptAllowed = false;
            HideLoadPrompt();
            CancellationTokenSource source = restoreCancellation;
            if (source != null)
            {
                source.Cancel();
            }
        }

        /// <summary>
        /// 负载浮窗上的“立即恢复”：跳过本次恢复后续的 CPU/内存等待。
        /// 只影响本次恢复，不取消已经启动的项目，也不改设置。
        /// </summary>
        public void ContinueRestoreDespiteLoad()
        {
            RestoreLoadGate gate = restoreLoadGate;
            if (gate != null)
            {
                gate.ContinueForRestore();
            }
            loadPromptAllowed = false;
            HideLoadPrompt();
            StatusText = AppText.T(ShellTextKeys.StatusRestoreContinueChosen);
        }

        private void HideLoadPrompt()
        {
            LoadPromptVisible = false;
            LoadPromptText = string.Empty;
        }

        /// <summary>
        /// 恢复卡片上的“立即恢复”：只跳过启动前读秒，不取消恢复。
        /// 两个倒计时流程共用这张卡片，所以两边都置位。
        /// </summary>
        public void SkipRestoreCountdown()
        {
            restoreCountdownSkipped = true;
            applicationsCountdownSkipped = true;
        }

        private void ScheduleStartupDesktopRestore()
        {
            if (!Settings.LayoutAutoRestore)
            {
                return;
            }
            CancellationTokenSource previous = startupLayoutCancellation;
            if (previous != null)
            {
                previous.Cancel();
            }

            CancellationTokenSource current = new CancellationTokenSource();
            startupLayoutCancellation = current;
            RestoreDesktopLayoutAfterLoginAsync(current);
        }

        private async void RestoreDesktopLayoutAfterLoginAsync(CancellationTokenSource source)
        {
            try
            {
                int seconds = Clamp(Settings.LayoutInitialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
                if (seconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds), source.Token).ConfigureAwait(true);
                }
                source.Token.ThrowIfCancellationRequested();
                if (!disposed && Settings.LayoutAutoRestore)
                {
                    await RestoreDesktopLayoutAsync().ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to restore the desktop layout after logon.", exception);
            }
            finally
            {
                if (object.ReferenceEquals(startupLayoutCancellation, source))
                {
                    startupLayoutCancellation = null;
                }
                source.Dispose();
            }
        }

        private RestoreOptions BuildRestoreOptions(bool skipInitialDelay)
        {
            RestoreOptions options = new RestoreOptions();
            options.InitialDelay = skipInitialDelay ? TimeSpan.Zero : TimeSpan.FromSeconds(Clamp(Settings.InitialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds));
            options.ItemDelay = TimeSpan.FromSeconds(Clamp(Settings.ItemDelaySeconds, 0, 3600));
            options.WebPageDelay = TimeSpan.FromSeconds(ClampDouble(Settings.WebPageDelaySeconds, 0, 3600));
            options.WaitForAcceptableLoad = Settings.WaitForLoad;
            options.MaximumCpuPercent = ClampDouble(Settings.MaximumCpuPercent, 5, 100);
            options.LoadWaitTimeout = TimeSpan.FromSeconds(Clamp(Settings.LoadWaitTimeoutSeconds, 0, 3600));
            options.MaximumMemoryPercent = ClampDouble(Settings.MaximumMemoryPercent, 5, 100);
            options.MemoryWaitTimeout = TimeSpan.FromSeconds(Clamp(Settings.MemoryWaitTimeoutSeconds, 0, 3600));
            return options;
        }

        private void UpdateRestoreProgress(RestoreProgress progress)
        {
            if (progress == null)
            {
                return;
            }
            // Progress<T> 的回调是投递到 UI 队列的，可能在恢复结束之后才到。
            // 如果已经不在恢复中，不能再让旧的“负载等待”进度把浮窗重新弹出来。
            if (!IsRestoring)
            {
                HideLoadPrompt();
                return;
            }
            if (progress.TotalItems > 0)
            {
                ProgressMaximum = progress.TotalItems;
            }
            if (progress.ItemNumber > 0)
            {
                ProgressValue = Math.Min(progress.ItemNumber, ProgressMaximum);
            }
            // Completed / Cancelled 的英文消息由服务层发出；最终中文结论
            // 由 RestoreSnapshotAsync 或自启动应用转接在 await 之后写。
            // 这里若再写一次英文，会把中文结论盖掉（Round166 抓到的
            // “Session restore completed.” 就是这个原因）。
            if (progress.State == RestoreProgressState.Completed
                || progress.State == RestoreProgressState.Cancelled)
            {
                loadPromptAllowed = false;
                HideLoadPrompt();
                return;
            }
            if (progress.LoadPauseActive && loadPromptAllowed)
            {
                LoadPromptText = BuildLoadPromptText(progress);
                LoadPromptVisible = true;
                StatusText = AppText.T(ShellTextKeys.StatusLoadPaused);
            }
            else
            {
                HideLoadPrompt();
                if (progress.SystemLoad != null && progress.State == RestoreProgressState.WaitingForSystemLoad)
                {
                    StatusText = AppText.F(
                        ShellTextKeys.StatusLoadWaitingFormat,
                        progress.SystemLoad.CpuPercent, progress.SystemLoad.MemoryPercent);
                }
                else if (!string.IsNullOrWhiteSpace(progress.Message))
                {
                    StatusText = progress.Message;
                }
                else if (progress.Item != null)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusRestoringItemFormat, progress.Item.DisplayName);
                }
            }
        }

        /// <summary>
        /// 浮窗文字：始终保留 CPU、内存两行，避免一行消失/出现导致浮窗高度和位置跳动。
        /// 当前值实时显示，上限/等待秒数来自用户设置；等待上限在进入暂停时锁定。
        /// </summary>
        private static string BuildLoadPromptText(RestoreProgress progress)
        {
            SystemLoadSnapshot load = progress.SystemLoad;
            double cpu = load == null ? 0 : load.CpuPercent;
            double memory = load == null ? 0 : load.MemoryPercent;
            string cpuState = progress.CpuOverLimit
                ? AppText.F(ShellTextKeys.StatusOverLimitFormat, progress.CpuOverSeconds)
                : AppText.T(ShellTextKeys.StatusUnderLimit);
            string memoryState = progress.MemoryOverLimit
                ? AppText.F(ShellTextKeys.StatusOverLimitFormat, progress.MemoryOverSeconds)
                : AppText.T(ShellTextKeys.StatusUnderLimit);

            List<string> lines = new List<string>();
            lines.Add(AppText.F(ShellTextKeys.LoadPromptCpuFormat,
                cpu, progress.CpuLimitPercent, cpuState));
            lines.Add(AppText.F(ShellTextKeys.LoadPromptMemoryFormat,
                memory, progress.MemoryLimitPercent, memoryState));
            lines.Add(string.Empty);
            lines.Add(AppText.F(ShellTextKeys.LoadPromptWaitLimitFormat, progress.EffectiveWaitLimitSeconds));
            lines.Add(AppText.F(
                ShellTextKeys.LoadPromptElapsedFormat,
                progress.EffectiveElapsedSeconds,
                progress.EffectiveRemainingSeconds));
            lines.Add(AppText.T(ShellTextKeys.LoadPromptAutoContinueNote));
            return string.Join(Environment.NewLine, lines.ToArray());
        }

    }
}
