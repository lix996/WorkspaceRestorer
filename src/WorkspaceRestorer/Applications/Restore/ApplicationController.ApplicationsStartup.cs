using System.Threading.Tasks;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「自启动应用」自动启动那一路的转接分部。
    ///
    /// 物理文件在 Applications/Restore，代码归自启动应用；它只负责：
    ///   · 判断自动启动开关 / 今日标记 / 有没有可启动项；
    ///   · 调 ApplicationPlanRestoreBridge 走共用恢复内核；
    ///   · 记今日标记。
    ///
    /// 真正的恢复编排仍在 StartupRestore/Core，Shared/UI/Shell 不依赖 Applications。
    /// </summary>
    public sealed partial class ApplicationController
    {
        /// <summary>
        /// 自动那一路：只有打开了“启动程序后自动启动自启动方案”才会启动。
        ///
        /// 【2026-10-19 用户报 bug 改的】这个开关以前**不看本次开机标记**，所以同一次开机里关掉程序再打开，
        /// 自启动方案会被再启动一遍（用户原话：“临时里明明有今天的标记文件了……自启动应用居然还能重启。
        /// 这个标记功能，要适用于全部的打开应用的重启功能”）。
        /// 现在和「启动与恢复」用同一个标记文件，但**各记一笔**：两个开关都开时，
        /// 先做的那一个不会把后一个也挡掉。
        /// </summary>
        private async Task<bool> StartApplicationsIfEnabledAsync()
        {
            if (!Settings.ApplicationsAutoRestore)
            {
                return true;
            }
            if (WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature))
            {
                StatusText = AppText.T(ApplicationsTextKeys.StatusAlreadyStartedToday);
                return true;
            }
            if (CountEnabledApplicationItems(ActiveApplicationPlan) <= 0)
            {
                // 没有可启动的项目：不记标记，用户配好方案后下次打开仍然会启动。
                return true;
            }
            // 不再在后台线程里跑旧的逐项 LaunchPath 循环；直接走共用恢复内核。
            // 恢复服务本身是 async，BeginStartup 会 await 它，优先顺序不变。
            // 【2026-11-17 用户要求】标记由共用恢复入口在“恢复倒计时卡片出现”时写，
            // 不再等恢复结束；这里只把“这次属于自启动应用”告诉它。
            ApplicationPlan plan = ActiveApplicationPlan;
            RestoreResult result = await WorkspaceRestorer.Applications.Restore.ApplicationPlanRestoreBridge
                .RestoreAsync(this, plan, false,
                    WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature)
                .ConfigureAwait(true);
            return result == null || !result.IsCancelled;
        }
    }
}
