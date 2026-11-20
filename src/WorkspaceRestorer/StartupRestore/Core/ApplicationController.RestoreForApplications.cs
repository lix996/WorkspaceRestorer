using System.Threading.Tasks;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「自启动应用 → 启动与恢复」的恢复转接。
    ///
    /// 物理文件在 StartupRestore/Core，代码归启动与恢复所有；Applications 只
    /// 构造一份 SessionSnapshot 和 RestoreOptions 调用这里，不复制浏览器批次、
    /// 最后一页缩窗、并发/取消/进度卡片等任何恢复逻辑。
    ///
    /// 这是 ApplicationController 的 partial 分部：能复用控制器已有的私有恢复
    /// 编排，但不会把自启动业务写进 Shared，也不让 Applications 碰私有字段。
    /// </summary>
    public sealed partial class ApplicationController
    {
        internal async Task<RestoreResult> RestoreExternalSnapshotAsync(
            SessionSnapshot snapshot,
            RestoreOptions options,
            bool applicationsSource,
            bool skipInitialDelay,
            string autoStartupFeature = null)
        {
            if (applicationsSource)
            {
                // 恢复卡片按这个标记显示“正在启动自启动方案…”，
                // 其余进度/取消/完成横幅仍然走同一条 RestoreSnapshotAsync。
                applicationsRestoreRunning = true;
            }
            try
            {
                RestoreResult result = await RestoreSnapshotAsync(
                    snapshot, skipInitialDelay, options, autoStartupFeature).ConfigureAwait(true);
                if (applicationsSource)
                {
                    RememberApplicationRestoreResult(result);
                }
                return result;
            }
            finally
            {
                if (applicationsSource)
                {
                    applicationsRestoreRunning = false;
                }
            }
        }

        private void RememberApplicationRestoreResult(RestoreResult result)
        {
            int skipped = 0;
            if (result != null && result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    RestoreItemResult item = result.Items[index];
                    if (item != null
                        && (item.WasAlreadyOpen
                            || item.WasUserClosed
                            || item.WasBrowserWindowClosed
                            || item.WasBrowserWindowUserMinimized))
                    {
                        skipped++;
                    }
                }
            }
            LastApplicationsStartSkipped = skipped;
            LastApplicationsStartCancelled = result != null && result.IsCancelled;
        }
    }
}
