using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        private void RefreshAll()
        {
            RefreshSessionList();
            RefreshProfileList();
            RefreshDesktopList();
            RefreshProgress();
            RefreshSettings();
            RefreshLayoutSummary();
        }



        private void RefreshLoadPrompt()
        {
            if (loadLimitPrompt == null || controller == null)
            {
                return;
            }
            if (controller.LoadPromptVisible)
            {
                loadLimitPrompt.Show(controller.LoadPromptText);
            }
            else
            {
                loadLimitPrompt.Hide();
            }
        }

        private void RefreshProgress()
        {
            statusText.Text = controller.StatusText;
            countdownText.Text = controller.CountdownSeconds > 0
                ? AppText.F(ShellTextKeys.StatusCountdownFormat, controller.CountdownSeconds)
                : string.Empty;
            if (immediateRestoreButton != null)
            {
                immediateRestoreButton.Visibility = controller.CountdownSeconds > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            progressBar.Maximum = Math.Max(1, controller.ProgressMaximum);
            progressBar.Value = Math.Min(controller.ProgressValue, progressBar.Maximum);
            // 只有正在恢复才显示进度条：它现在是一整行宽的细线，恢复完还留着会像一条多余的分隔线。
            progressBar.Visibility = controller.IsRestoring ? Visibility.Visible : Visibility.Collapsed;
            if (!controller.IsRestoring)
            {
                progressBar.Value = 0;
            }
            UpdateRestoreProgressText();
        }

        private void UpdateRestoreProgressText()
        {
            if (restoreProgressText == null
                || restoreOverlay == null
                || restoreOverlay.Visibility != Visibility.Visible)
            {
                return;
            }
            int total = Math.Max(1, controller.ProgressMaximum);
            int current = Math.Min(Math.Max(0, controller.ProgressValue), total);
            string status = controller.StatusText;
            string line;
            if (total > 1)
            {
                line = AppText.F(ShellTextKeys.RestoreProgressItemsFormat, current, total);
            }
            else
            {
                line = AppText.T(ShellTextKeys.RestoreProgressRestoring);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                line = AppText.F(ShellTextKeys.RestoreProgressLineFormat, line, status);
            }
            restoreProgressText.Text = line;
        }

        private void OnRestoreCompleted(RestoreCompletion completion)
        {
            // 【2026-10-19】自启动方案那一半是在**后台线程**跑完的，事件可能不是界面线程发的：
            // 工作会话那一半原来就带着这个判断（见下面 Dispatcher.HasShutdownStarted），这里补上线程切换。
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { OnRestoreCompleted(completion); }));
                return;
            }
            if (completion == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            HideRestoreOverlay();
            // 串行自动恢复的第一段（例如先自启动应用、再工作方案）：中间不弹完成横幅，
            // 否则横幅和下一段恢复卡片会叠在一起，看起来像两个窗口打架。
            // 但 CPU/内存超时自动暂停必须把原因显示出来，否则用户不知道自动恢复为什么停了。
            if (controller != null && controller.AutoStartupChaining && !completion.IsAutoStopped)
            {
                return;
            }
            // The window must be on screen to show the normal banner; when it is
            // hidden to the tray, the App shows a tray balloon instead.
            // CPU/内存自动暂停例外：即使窗口在托盘/最小化，也把常驻提示留在界面上；
            // 用户重新打开窗口时能立刻看到原因，不会“打开后发现什么都没恢复”。
            bool visible = IsVisible && WindowState != WindowState.Minimized;
            if (IsLoaded && (visible || completion.IsAutoStopped))
            {
                if (visible)
                {
                    BringToFront(this);
                }
                ShowRestoreNotice(completion);
            }
        }

        private void OnRestoreStarted()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(delegate { OnRestoreStarted(); }));
                return;
            }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            // Mirrors the explicit save flow: come to the front and show the
            // centered "正在恢复…" card with its cancel button.
            if (IsLoaded && IsVisible && WindowState != WindowState.Minimized)
            {
                ShowRestoreOverlay();
                BringToFront(this);
            }
        }

        private void OnSaveStarted()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            // An explicit save is starting: come to the front and show the
            // centered "正在保存当前工作…" card with its cancel button.
            if (IsLoaded && IsVisible && WindowState != WindowState.Minimized)
            {
                ShowSavingOverlay();
                BringToFront(this);
            }
        }

        private void UpdateOperationTopmost()
        {
            bool active = IsVisible && WindowState != WindowState.Minimized
                && ((savingOverlay != null && savingOverlay.Visibility == Visibility.Visible)
                    || (restoreOverlay != null && restoreOverlay.Visibility == Visibility.Visible)
                    || (restoreNotice != null && restoreNotice.Visibility == Visibility.Visible));
            if (active && !operationTopmostOwned)
            {
                topmostBeforeOperation = Topmost;
                operationTopmostOwned = true;
                Topmost = true;
            }
            else if (!active && operationTopmostOwned)
            {
                operationTopmostOwned = false;
                Topmost = topmostBeforeOperation;
            }
        }

        private void ShowSavingOverlay()
        {
            if (savingOverlay == null)
            {
                return;
            }
            HideRestoreNotice();
            // While browsers jump to the front during the scan, the saving
            // card must stay visible, so the keeper window is kept topmost for
            // the duration of the save.
            savingOverlay.Visibility = Visibility.Visible;
            UpdateOperationTopmost();
        }

        private void HideSavingOverlay()
        {
            if (savingOverlay != null)
            {
                savingOverlay.Visibility = Visibility.Collapsed;
            }
            UpdateOperationTopmost();
        }

        private void ShowRestoreOverlay()
        {
            if (restoreOverlay == null)
            {
                return;
            }
            HideRestoreNotice();
            // 【2026-10-19】这张卡片两个流程共用：工作会话恢复 / 自启动方案逐项启动。
            // 只换标题和说明，其余（置顶、进度行、取消按钮）完全一样
            // —— 用户要求“自启动应用的立即恢复继续抄「启动与恢复」的交互”。
            bool applications = controller != null && controller.ApplicationsRestoreInProgress;
            if (restoreTitleText != null)
            {
                restoreTitleText.Text = applications
                    ? AppText.T(ShellTextKeys.RestoreTitleApplications)
                    : AppText.T(ShellTextKeys.RestoreTitleProfile);
            }
            if (restoreDetailText != null)
            {
                // 【2026-10-19】文案统一由 `RestoreCardText.Detail` 生成（可以单测）：
                // 「启动与恢复」恢复时，如果这份方案里有终端/本地网址，就如实说明“它们恢复不了”，
                // 别让用户以为少的那几项是丢了；「自启动应用」那边不用这段。
                bool applicationsRestore = applications;
                int nonRestorable = controller == null ? 0 : controller.RestoreNonRestorableCount;
                restoreDetailText.Text = RestoreCardText.Detail(applicationsRestore, nonRestorable);
            }
            // Keep the progress card above newly launched windows with one
            // stable topmost lifetime. Never pulse z-order on each launch.
            restoreOverlay.Visibility = Visibility.Visible;
            UpdateOperationTopmost();
        }
        private void HideRestoreOverlay()
        {
            if (restoreOverlay != null)
            {
                restoreOverlay.Visibility = Visibility.Collapsed;
            }
            if (loadLimitPrompt != null)
            {
                loadLimitPrompt.Hide();
            }
            UpdateOperationTopmost();
        }

        private void OnSaveCompleted(SaveCompletion completion)
        {
            if (completion == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            HideSavingOverlay();
            // After an explicit save the keeper comes to the front so the
            // completion banner is actually visible.  When the window is in
            // the tray, the App notifies through a tray balloon instead.
            if (IsLoaded && IsVisible && WindowState != WindowState.Minimized)
            {
                BringToFront(this);
                ShowSaveNotice(completion);
            }
        }

        private void ShowRestoreNotice(RestoreCompletion completion)
        {
            if (completion == null)
            {
                return;
            }

            // 文案在 Text/Restore/ 的三个模块目录里；这里只按语义挑颜色。
            RestoreNoticeText notice = RestoreText.ForBanner(completion);
            SolidColorBrush background;
            SolidColorBrush foreground;
            switch (notice.Kind)
            {
                case RestoreNoticeKind.Success:
                    background = AppTheme.SuccessSurface;
                    foreground = AppTheme.Positive;
                    break;
                case RestoreNoticeKind.Error:
                    background = AppTheme.DangerSurface;
                    foreground = AppTheme.Danger;
                    break;
                case RestoreNoticeKind.Warning:
                    background = AppTheme.WarningSurface;
                    foreground = AppTheme.Warning;
                    break;
                default:
                    background = AppTheme.NeutralNoticeSurface;
                    foreground = AppTheme.TextPrimary;
                    break;
            }

            // 失败时横幅只放摘要，完整清单写进底部状态栏，鼠标停在横幅上也能看到全文。
            if (notice.Kind == RestoreNoticeKind.Error && !notice.Persistent)
            {
                statusText.Text = notice.Detail;
                if (restoreNotice != null)
                {
                    AccessibleToolTip.AttachDescription(restoreNotice, notice.Detail, null, 60000);
                }
            }

            ShowBanner(notice.Summary, background, foreground, !notice.Persistent);
        }

        private void ShowSaveNotice(SaveCompletion completion)
        {
            if (completion == null)
            {
                return;
            }

            string text;
            SolidColorBrush background;
            SolidColorBrush foreground;
            if (completion.Cancelled)
            {
                text = AppText.T(ShellTextKeys.NoticeSaveCancelled);
                background = AppTheme.NeutralNoticeSurface;
                foreground = AppTheme.TextPrimary;
            }
            else if (completion.Failed)
            {
                text = AppText.T(ShellTextKeys.NoticeSaveFailedDetail);
                background = AppTheme.DangerSurface;
                foreground = AppTheme.Danger;
            }
            else if (completion.Incomplete || completion.PolicyAffectedCount > 0)
            {
                // 用户要求（2026-10-09）：这条提示要短、要能一眼看懂怎么办；
                // 【2026-10-19】文案统一由 `SaveCompletionText` 生成（横幅与托盘气泡共用一份）：
                //   · 真没读到网页 → 才建议“把最小化的浏览器窗口显示出来”；
                //   · 终端/本地网址被设置跳过 → 说清“跳过了几项、为什么”、怎么改设置才保存；
                //   · 勾了设置被保留 → 说清“这些恢复不了，想常驻请加到自启动应用”。
                text = SaveCompletionText.Build(completion, true);
                background = AppTheme.WarningSurface;
                foreground = AppTheme.Warning;
            }
            else
            {
                text = AppText.F(ShellTextKeys.NoticeSavedFormat, completion.ItemCount);
                background = AppTheme.SuccessSurface;
                foreground = AppTheme.Positive;
            }

            ShowBanner(text, background, foreground);
        }

        private void ShowBanner(string text, SolidColorBrush background, SolidColorBrush foreground)
        {
            ShowBanner(text, background, foreground, true);
        }

        private void ShowBanner(string text, SolidColorBrush background, SolidColorBrush foreground, bool autoHide)
        {
            restoreNoticeText.Text = text;
            restoreNoticeText.Foreground = foreground;
            restoreNotice.Background = background;
            restoreNotice.Visibility = Visibility.Visible;
            restoreNoticePersistent = !autoHide;
            UpdateOperationTopmost();
            restoreNoticeTimer.Stop();
            if (autoHide)
            {
                restoreNoticeTimer.Start();
            }
        }

        private void HideRestoreNotice()
        {
            restoreNoticeTimer.Stop();
            restoreNoticePersistent = false;
            if (restoreNotice != null)
            {
                restoreNotice.Visibility = Visibility.Collapsed;
            }
            UpdateOperationTopmost();
        }



























    }
}
