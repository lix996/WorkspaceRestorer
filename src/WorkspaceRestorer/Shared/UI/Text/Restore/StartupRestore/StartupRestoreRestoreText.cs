using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>「启动与恢复」整份工作方案的恢复完成文案。</summary>
    internal static class StartupRestoreRestoreText
    {
        internal static RestoreNoticeText ForBanner(RestoreCompletion completion)
        {
            return RestoreTextCore.BannerNotice(completion);
        }

        internal static string ForTray(RestoreCompletion completion)
        {
            return RestoreTextCore.TrayText(completion);
        }

        internal static string ForStatus(RestoreCompletion completion)
        {
            return RestoreTextCore.StatusText(completion);
        }
    }
}
