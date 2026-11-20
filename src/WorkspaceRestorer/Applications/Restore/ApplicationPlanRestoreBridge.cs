using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Applications.Restore
{
    /// <summary>
    /// 「自启动应用 → 启动与恢复」的转接文件。
    ///
    /// 只做三件事：
    ///   1. 把自启动方案翻译成 SessionSnapshot；
    ///   2. 按自启动应用自己的设置生成 RestoreOptions；
    ///   3. 调用 StartupRestore/Core 里的共用恢复入口。
    ///
    /// 不创建工作方案、不落盘、不切换当前方案，也不复制浏览器批次/缩窗逻辑。
    /// 窗口防跑出屏幕的策略由 `SessionRestoreService` 构造时注册到
    /// `WindowPlacementPolicy`；本桥接只负责转接，不复制 `StartupRestore/Windows/` 的规则。
    /// </summary>
    public static class ApplicationPlanRestoreBridge
    {
        public static async Task<RestoreResult> RestoreAsync(
            ApplicationController controller,
            ApplicationPlan plan,
            bool skipInitialDelay,
            string autoStartupFeature = null)
        {
            if (controller == null || plan == null)
            {
                return null;
            }

            SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(plan, controller.ApplicationPlans);
            RestoreOptions options = BuildOptions(controller.Settings, skipInitialDelay);
            RestoreResult result = await controller.RestoreExternalSnapshotAsync(
                snapshot, options, true, skipInitialDelay, autoStartupFeature).ConfigureAwait(true);

            if (result != null && !result.IsCancelled)
            {
                WriteCompletionStatus(controller, plan, result);
            }
            return result;
        }

        private static RestoreOptions BuildOptions(
            ApplicationController.ControllerSettings settings,
            bool skipInitialDelay)
        {
            RestoreOptions options = new RestoreOptions();
            if (settings == null)
            {
                return options;
            }
            options.InitialDelay = skipInitialDelay
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Clamp(
                    settings.ApplicationsInitialDelaySeconds,
                    0,
                    ApplicationController.ControllerSettings.InitialDelayMaximumSeconds));
            options.ItemDelay = TimeSpan.FromSeconds(Clamp(
                settings.ApplicationsItemDelaySeconds, 0, 3600));
            options.WebPageDelay = TimeSpan.FromSeconds(ClampDouble(
                settings.ApplicationsWebPageDelaySeconds, 0, 3600));
            options.WaitForAcceptableLoad = settings.ApplicationsWaitForLoad;
            options.MaximumCpuPercent = ClampDouble(settings.MaximumCpuPercent, 5, 100);
            options.LoadWaitTimeout = TimeSpan.FromSeconds(Clamp(
                settings.LoadWaitTimeoutSeconds, 0, 3600));
            options.MaximumMemoryPercent = ClampDouble(settings.MaximumMemoryPercent, 5, 100);
            options.MemoryWaitTimeout = TimeSpan.FromSeconds(Clamp(
                settings.MemoryWaitTimeoutSeconds, 0, 3600));
            return options;
        }

        private static void WriteCompletionStatus(
            ApplicationController controller,
            ApplicationPlan plan,
            RestoreResult result)
        {
            int started = 0;
            int skipped = 0;
            int userClosed = 0;
            int browserWindowClosed = 0;
            int browserWindowUserMinimized = 0;
            int failed = 0;
            if (result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    RestoreItemResult item = result.Items[index];
                    if (item == null)
                    {
                        continue;
                    }
                    if (item.WasAlreadyOpen)
                    {
                        skipped++;
                    }
                    else if (item.WasUserClosed)
                    {
                        userClosed++;
                    }
                    else if (item.WasBrowserWindowClosed)
                    {
                        browserWindowClosed++;
                    }
                    else if (item.WasBrowserWindowUserMinimized)
                    {
                        browserWindowUserMinimized++;
                    }
                    else if (item.Success)
                    {
                        started++;
                    }
                    else
                    {
                        failed++;
                    }
                }
            }
            string name = string.IsNullOrWhiteSpace(plan.Name)
                ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                : plan.Name;
            // 保存为最小化、但期限内没读到标题的浏览器窗口：窗户被有意留在普通状态。
            // 这是窗口级结果（网页本身启动成功），自启动应用的状态栏也要照「启动与恢复」说清楚。
            int browserWindowNotReady = result == null ? 0 : result.BrowserWindowNotReadyCount;
            string message = AppText.F(
                    ApplicationsTextKeys.RestoreStartedFormat, name, started)
                + (skipped > 0
                    ? AppText.F(ApplicationsTextKeys.RestoreSkippedFormat, skipped)
                    : string.Empty)
                + (userClosed > 0
                    ? AppText.F(ApplicationsTextKeys.RestoreUserClosedFormat, userClosed)
                    : string.Empty)
                + (browserWindowClosed > 0
                    ? AppText.F(ApplicationsTextKeys.RestoreBrowserClosedFormat, browserWindowClosed)
                    : string.Empty)
                + (browserWindowUserMinimized > 0
                    ? AppText.F(
                        ApplicationsTextKeys.RestoreBrowserMinimizedFormat,
                        browserWindowUserMinimized)
                    : string.Empty)
                + (browserWindowNotReady > 0
                    ? "，" + BrowserWindowNotReadyNotice.Summary(browserWindowNotReady)
                    : string.Empty)
                + (failed > 0
                    ? AppText.F(ApplicationsTextKeys.RestoreFailedFormat, failed)
                    : string.Empty)
                + AppText.T(ApplicationsTextKeys.RestoreEndPeriod);
            // Progress<T> 的 ItemCompleted 回调是投递到 UI 队列的，可能在
            // await 返回之后才执行，把中文结论盖成英文 “Started …”。
            // 用 Background 优先级把最终结论排在这些进度回调之后。
            Application app = Application.Current;
            if (app != null && app.Dispatcher != null)
            {
                app.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(delegate { controller.ReportStatus(message); }));
            }
            else
            {
                controller.ReportStatus(message);
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            if (value > maximum)
            {
                return maximum;
            }
            return value;
        }

        private static double ClampDouble(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            if (value > maximum)
            {
                return maximum;
            }
            return value;
        }
    }
}
