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
        public SessionSnapshot CaptureSnapshot()
        {
            ThrowIfDisposed();
            return CaptureSnapshotInternal(true, true);
        }

        /// <summary>
        /// Captures the current session without running browser UI
        /// Automation on the WPF dispatcher.  The service is executed on a
        /// dedicated STA worker because UI Automation is apartment-sensitive.
        /// A hung provider is abandoned after the bounded timeout; its
        /// background thread cannot keep the application alive or freeze the
        /// window.
        /// </summary>
        public Task<SessionSnapshot> CaptureSnapshotAsync()
        {
            ThrowIfDisposed();
            return CaptureSnapshotAsyncCore(ActiveProfile, true);
        }

        /// <summary>
        /// Captures data for a new named profile without replacing the
        /// currently selected profile while the scan is running.
        /// </summary>
        public Task<SessionSnapshot> CaptureSnapshotForNewProfileAsync()
        {
            ThrowIfDisposed();
            // A new profile is named only after the scan completes.  Keep the
            // raw result out of LastSnapshot so the currently selected profile
            // cannot be changed while the user is still choosing its name.
            return CaptureSnapshotAsyncCore(null, false);
        }

        private async Task<SessionSnapshot> CaptureSnapshotAsyncCore(
            StartupProfile captureProfile,
            bool commitToCurrentState)
        {
            if (Interlocked.CompareExchange(ref sessionCaptureInProgress, 1, 0) != 0)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCaptureBusy);
                return null;
            }

            sessionSaveCancelRequested = 0;
            RaiseSaveStarted();
            Task<SessionSnapshot> worker = null;
            try
            {
                StatusText = AppText.T(ShellTextKeys.StatusCaptureReading);
                worker = StartSessionCaptureWorker(true);
                Task completed = await Task.WhenAny(
                    worker,
                    Task.Delay(InteractiveCaptureTimeoutMilliseconds)).ConfigureAwait(true);
                if (completed != worker)
                {
                    // The worker is background-only and may be inside a
                    // provider that cannot be interrupted.  Release the
                    // controller gate on timeout so one abandoned provider
                    // cannot permanently disable every later save.
                    Interlocked.CompareExchange(ref sessionCaptureInProgress, 0, 1);
                    if (WasSessionSaveCancelled())
                    {
                        StatusText = AppText.T(ShellTextKeys.StatusCaptureCancelled);
                        RaiseSaveCompleted(null, false, true);
                        return null;
                    }
                    if (!disposed)
                    {
                        StatusText = AppText.T(ShellTextKeys.StatusCaptureTimeout);
                    }
                    AppLog.Write("Interactive session capture exceeded the timeout and was ignored.");
                    RaiseSaveCompleted(null, true, false);
                    return null;
                }

                SessionSnapshot snapshot = await worker.ConfigureAwait(true);
                if (snapshot == null)
                {
                    if (WasSessionSaveCancelled())
                    {
                        StatusText = AppText.T(ShellTextKeys.StatusCaptureCancelled);
                        RaiseSaveCompleted(null, false, true);
                        return null;
                    }
                    if (!disposed)
                    {
                        StatusText = AppText.T(ShellTextKeys.StatusCaptureFailedNoWindow);
                    }
                    RaiseSaveCompleted(null, true, false);
                    return null;
                }

                if (disposed)
                {
                    return null;
                }

                // 【2026-10-19】终端窗口 / 本地网址按设置取舍的事，现在统一放到公共提交口
                // `CommitCapturedSnapshot`（以前只加在“保存当前工作”这一条路上，别的保存路径照旧会写进去）。
                if (WasSessionSaveCancelled())
                {
                    StatusText = AppText.T(ShellTextKeys.StatusCaptureCancelled);
                    RaiseSaveCompleted(null, false, true);
                    return null;
                }

                if (!commitToCurrentState)
                {
                    // 「保存为新方案」还没写进任何方案，这里先把终端/本地网址按设置取舍，再交给“起名字”的界面。
                    // 【2026-10-19】把“涉及几项 / 跳过几项”记在快照上，保存完成的横幅与托盘气泡据此如实说明；
                    // 不再把它塞进 CaptureWarning（那会让横幅误以为“网页没读到 → 去显示最小化窗口”）。
                    TerminalAndLocalUrlPolicy.PolicyOutcome newProfileOutcome =
                        TerminalAndLocalUrlPolicy.ApplyDetailed(snapshot, Settings.ScanTerminalsAndLocalUrls);
                    ApplyPolicyOutcome(snapshot, newProfileOutcome);
                    LastCapturePolicyHint = string.IsNullOrWhiteSpace(newProfileOutcome.Hint)
                        ? string.Empty
                        : newProfileOutcome.Hint.Trim();
                    return snapshot;
                }

                StartupProfile storedCaptureProfile = captureProfile == null
                    ? null
                    : FindProfile(captureProfile);
                if (captureProfile != null && storedCaptureProfile == null)
                {
                    StatusText = AppText.T(ShellTextKeys.StatusCaptureTargetGone);
                    return null;
                }

                SessionSnapshot committed = CommitCapturedSnapshot(snapshot, true, true, storedCaptureProfile);
                if (committed != null)
                {
                    RaiseSaveCompleted(committed, false, false);
                }
                return committed;
            }
            catch (Exception exception)
            {
                if (WasSessionSaveCancelled())
                {
                    StatusText = AppText.T(ShellTextKeys.StatusCaptureCancelled);
                    RaiseSaveCompleted(null, false, true);
                    return null;
                }
                if (!disposed)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusCaptureFailedFormat, exception.Message);
                }
                AppLog.Write("Unable to capture the current session asynchronously.", exception);
                RaiseSaveCompleted(null, true, false);
                return null;
            }
        }

        /// <summary>
        /// Requests cancellation of the explicit session save that is running.
        /// The capture worker helpers are terminated at their process boundary;
        /// the controller reports the cancel through <see cref="SaveCompleted"/>
        /// and never commits a partial snapshot.
        /// </summary>
        public bool CancelSessionSave()
        {
            if (sessionCaptureInProgress == 0)
            {
                return false;
            }
            sessionSaveCancelRequested = 1;
            SessionCaptureProcess.CancelActiveCaptures();
            if (!disposed)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCaptureCancelling);
            }
            return true;
        }

        private bool WasSessionSaveCancelled()
        {
            return sessionSaveCancelRequested != 0;
        }

        private void RaiseSaveStarted()
        {
            Action handler = SaveStarted;
            if (handler != null)
            {
                handler();
            }
        }

        private void RaiseRestoreStarted()
        {
            Action handler = RestoreStarted;
            if (handler != null)
            {
                handler();
            }
        }

        private Task<SessionSnapshot> StartSessionCaptureWorker(bool allowInteractiveBrowserCapture)
        {
            TaskCompletionSource<SessionSnapshot> completion = new TaskCompletionSource<SessionSnapshot>();
            Thread worker = new Thread(delegate()
            {
                SessionSnapshot result = null;
                try
                {
                    SessionCaptureOptions options = new SessionCaptureOptions
                    {
                        IncludeExplorerFolders = true,
                        IncludeBrowserPages = allowInteractiveBrowserCapture,
                        AllowBrowserTabActivation = allowInteractiveBrowserCapture
                    };
                    result = captureService.Capture(options);
                }
                catch (Exception exception)
                {
                    AppLog.Write("Unable to capture the current session on the worker thread.", exception);
                }
                finally
                {
                    Interlocked.Exchange(ref sessionCaptureInProgress, 0);
                }
                completion.TrySetResult(result);
            });
            worker.IsBackground = true;
            worker.Name = "WorkspaceRestorer.SessionCapture";
            worker.SetApartmentState(ApartmentState.STA);
            try
            {
                worker.Start();
            }
            catch
            {
                Interlocked.Exchange(ref sessionCaptureInProgress, 0);
                throw;
            }
            return completion.Task;
        }

        public SessionSnapshot SaveCurrentWorkToProfile(StartupProfile profile)
        {
            ThrowIfDisposed();
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                StatusText = AppText.T(ShellTextKeys.StatusProfileMissing);
                return null;
            }
            if (stored.IsFolder)
            {
                StatusText = AppText.T(ShellTextKeys.StatusProfileFolderCannotSave);
                return null;
            }

            // CaptureSnapshotInternal mirrors captures into the active profile.
            // Select this profile first so saving a non-active folder cannot
            // accidentally overwrite whichever profile was previously active.
            SetActiveProfile(stored);
            SessionSnapshot snapshot = CaptureSnapshotInternal(true, true);
            if (snapshot == null)
            {
                return null;
            }

            snapshot.Items = KeepSavedPages(stored, snapshot.Items);
            stored.ReplaceItems(snapshot.Items);
            profileCatalog.ActiveProfileId = stored.Id;
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            OnPropertyChanged("ActiveProfile");
            StatusText = AppText.F(
                ShellTextKeys.StatusProfileSavedFormat,
                stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                stored.Items.Count);
            return snapshot;
        }

        /// <summary>
        /// Asynchronous counterpart used by the WPF profile buttons.  The
        /// selected profile is established before capture so the completed
        /// snapshot is committed to that folder by the common commit path.
        /// </summary>
        public async Task<SessionSnapshot> SaveCurrentWorkToProfileAsync(StartupProfile profile)
        {
            ThrowIfDisposed();
            // 【2026-10-24】恢复期间不能保存：否则会把“恢复到一半”的窗口状态写进方案。
            // 页面上的遮罩不挡后面的按钮，所以互斥必须放在 controller 层，不能只靠 UI。
            if (IsRestoring || restoringDesktopLayout)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCaptureWhileRestoring);
                return null;
            }
            StartupProfile stored = FindProfile(profile);
            if (stored == null)
            {
                StatusText = AppText.T(ShellTextKeys.StatusProfileMissing);
                return null;
            }
            if (stored.IsFolder)
            {
                StatusText = AppText.T(ShellTextKeys.StatusProfileFolderCannotSave);
                return null;
            }

            SetActiveProfile(stored);
            SessionSnapshot snapshot = await CaptureSnapshotAsyncCore(stored, true).ConfigureAwait(true);
            if (snapshot == null)
            {
                return null;
            }

            stored = FindProfile(stored);
            if (stored == null)
            {
                return null;
            }
            string savedMessage = AppText.F(
                ShellTextKeys.StatusProfileSavedFormat,
                stored.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                stored.Items.Count);
            if (!string.IsNullOrWhiteSpace(LastCapturePolicyHint))
            {
                savedMessage += AppText.T(ShellTextKeys.StatusDetailSeparator)
                    + LastCapturePolicyHint.Trim();
            }
            StatusText = BuildCaptureStatus(savedMessage, snapshot);
            return snapshot;
        }

        /// <summary>
        /// Captures a periodic checkpoint without replacing the current status
        /// message.  This runs on the WPF dispatcher so Shell automation stays
        /// on an initialized STA thread.
        /// </summary>
        public void CaptureBackgroundSnapshot()
        {
            ObserveTask(
                CaptureBackgroundSnapshotAsync(),
                "Unable to finish the periodic background snapshot.");
        }

        public async Task CaptureBackgroundSnapshotAsync()
        {
            if (disposed
                || IsRestoring
                || restoringDesktopLayout
                || Interlocked.CompareExchange(ref sessionCaptureInProgress, 1, 0) != 0)
            {
                return;
            }

            try
            {
                StartupProfile captureProfile = ActiveProfile;
                if (captureProfile != null && captureProfile.IsFolder)
                {
                    // 纯文件夹只装子方案，后台定时保存不能把启动项塞进它。
                    // 这里已经拿过 sessionCaptureInProgress，提前返回必须放锁，
                    // 否则之后所有保存/恢复都会被“正在保存”永久挡住。
                    Interlocked.Exchange(ref sessionCaptureInProgress, 0);
                    return;
                }
                Task<SessionSnapshot> worker = StartSessionCaptureWorker(false);
                Task completed = await Task.WhenAny(
                    worker,
                    Task.Delay(PassiveCaptureTimeoutMilliseconds)).ConfigureAwait(true);
                if (completed != worker)
                {
                    Interlocked.CompareExchange(ref sessionCaptureInProgress, 0, 1);
                    AppLog.Write("The periodic background snapshot exceeded its time limit.");
                    return;
                }

                SessionSnapshot snapshot = await worker.ConfigureAwait(true);
                if (snapshot != null && !disposed)
                {
                    StartupProfile storedCaptureProfile = captureProfile == null
                        ? null
                        : FindProfile(captureProfile);
                    if (captureProfile == null || storedCaptureProfile != null)
                    {
                        CommitCapturedSnapshot(
                            snapshot,
                            false,
                            false,
                            storedCaptureProfile);
                        // 2026-10-23：后台定时保存只更新工作方案快照。
                        // 不要再调 CaptureDesktopLayoutAsync —— 它会把当前桌面
                        // 写进「图标位置保存」的自动布局，造成两个功能串联。
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to capture the periodic background snapshot.", exception);
            }
        }

        private SessionSnapshot CaptureSnapshotInternal(bool announce, bool allowInteractiveBrowserCapture)
        {
            try
            {
                StartupProfile captureProfile = ActiveProfile;
                if (captureProfile != null && captureProfile.IsFolder)
                {
                    StatusText = AppText.T(ShellTextKeys.StatusProfileFolderCannotSave);
                    return null;
                }
                SessionCaptureOptions options = new SessionCaptureOptions
                {
                    IncludeExplorerFolders = true,
                    IncludeBrowserPages = allowInteractiveBrowserCapture,
                    AllowBrowserTabActivation = allowInteractiveBrowserCapture
                };
                SessionSnapshot snapshot = captureService.Capture(options);
                return CommitCapturedSnapshot(
                    snapshot,
                    announce,
                    allowInteractiveBrowserCapture,
                    captureProfile);
            }
            catch (Exception exception)
            {
                if (announce)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusCaptureFailedFormat, exception.Message);
                }
                AppLog.Write("Unable to capture the current session.", exception);
                return null;
            }
        }

        private SessionSnapshot CommitCapturedSnapshot(
            SessionSnapshot snapshot,
            bool announce,
            bool allowInteractiveBrowserCapture,
            StartupProfile captureProfile)
        {
            try
            {
                if (snapshot == null)
                {
                    return null;
                }
                StartupProfile mergeProfile = captureProfile;
                if (mergeProfile == null
                    && !allowInteractiveBrowserCapture
                    && LastSnapshot != null
                    && LastSnapshot.Items != null)
                {
                    mergeProfile = new StartupProfile { Items = LastSnapshot.Items };
                }
                if (mergeProfile != null)
                {
                    // Keep entries that were explicitly selected from the
                    // desktop list.  A periodic capture observes the current
                    // windows and must not silently erase those choices.
                    snapshot.Items = MergeCapturedItems(
                        snapshot.Items,
                        mergeProfile,
                        !allowInteractiveBrowserCapture || snapshot.BrowserCaptureIncomplete);
                }
                // 【2026-10-19 用户报 bug 改的】“扫描终端与本地网址”必须在**每一条保存路径**上都生效：
                // 以前只加在“保存当前工作”那一条上，别的路径（后台捕获、右键保存、托盘保存……）
                // 照样把 ComfyUI 的 127.0.0.1:8188 写进方案里，用户明明关掉了开关还是能看见它。
                // 现在统一放在这个公共提交口，连“合并进来的旧条目”也一起按设置处理。
                //   · 没勾选（默认）→ 从方案里去掉，并提示“已跳过几项”；
                //   · 勾选 → 保留，并提示“这些无法恢复、想常驻请去自启动应用”。
                // 恢复逻辑本身一行没动；用户亲手挑选/复制进来的项（DesktopSelection）也不算“扫描到的”。
                LastCapturePolicyHint = string.Empty;
                TerminalAndLocalUrlPolicy.PolicyOutcome terminalPolicy =
                    TerminalAndLocalUrlPolicy.ApplyDetailed(snapshot, Settings.ScanTerminalsAndLocalUrls);
                string terminalPolicyHint = terminalPolicy.Hint;
                ApplyPolicyOutcome(snapshot, terminalPolicy);
                if (!string.IsNullOrWhiteSpace(terminalPolicyHint))
                {
                    LastCapturePolicyHint = terminalPolicyHint.Trim();
                }
                bool captureProfileIsActive = captureProfile != null && IsActiveProfile(captureProfile);
                if (captureProfile == null || captureProfileIsActive)
                {
                    SaveLastSnapshot(snapshot);
                    LastSnapshot = snapshot;
                }
                if (captureProfile != null)
                {
                    snapshot.Items = KeepSavedPages(captureProfile, snapshot.Items);
                    // KeepSavedPages 可能把方案里原有的网页再补回来，补回来后再按设置过一遍（不再重复提示）。
                    TerminalAndLocalUrlPolicy.PolicyOutcome afterMerge =
                        TerminalAndLocalUrlPolicy.ApplyDetailed(snapshot, Settings.ScanTerminalsAndLocalUrls);
                    ApplyPolicyOutcome(snapshot, afterMerge);
                    captureProfile.ReplaceItems(snapshot.Items);
                    PersistProfiles();
                    OnPropertyChanged("Profiles");
                    OnPropertyChanged("StartupProfiles");
                    if (captureProfileIsActive)
                    {
                        OnPropertyChanged("ActiveProfile");
                    }
                }
                ProgressMaximum = snapshot.Items == null ? 0 : snapshot.Items.Count;
                ProgressValue = 0;
                if (announce)
                {
                    string message = AppText.F(ShellTextKeys.StatusSessionSavedFormat, ProgressMaximum);
                    if (!string.IsNullOrWhiteSpace(terminalPolicyHint))
                    {
                        message += AppText.T(ShellTextKeys.StatusDetailSeparator)
                            + terminalPolicyHint.Trim();
                    }
                    StatusText = BuildCaptureStatus(message, snapshot);
                }
                OnPropertyChanged("HasSnapshot");
                return snapshot;
            }
            catch (Exception exception)
            {
                if (announce)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusCaptureFailedFormat, exception.Message);
                }
                AppLog.Write("Unable to capture the current session.", exception);
                return null;
            }
        }

        private void SaveLastSnapshot(SessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            try
            {
                sessionStore.Save(snapshot);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save the last session snapshot.", exception);
            }
        }

        private SessionSnapshot LoadSnapshot()
        {
            try
            {
                SessionSnapshot value = sessionStore.Load();
                return value == null || value.Items == null || value.Items.Count == 0 ? null : value;
            }
            catch
            {
                return null;
            }
        }

    }
}
