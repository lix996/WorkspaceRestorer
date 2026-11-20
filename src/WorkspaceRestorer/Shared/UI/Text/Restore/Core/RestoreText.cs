using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 恢复完成文案的路由：横幅、托盘、状态栏只调这里，不再自己判断
    /// “这是启动与恢复还是自启动应用”。
    /// </summary>
    internal static class RestoreText
    {
        internal static RestoreNoticeText ForBanner(RestoreCompletion completion)
        {
            if (completion != null && completion.Source == RestoreSource.Applications)
            {
                return ApplicationsRestoreText.ForBanner(completion);
            }
            return StartupRestoreRestoreText.ForBanner(completion);
        }

        internal static string ForTray(RestoreCompletion completion)
        {
            if (completion != null && completion.Source == RestoreSource.Applications)
            {
                return ApplicationsRestoreText.ForTray(completion);
            }
            return StartupRestoreRestoreText.ForTray(completion);
        }

        internal static string ForStatus(RestoreCompletion completion)
        {
            if (completion != null && completion.Source == RestoreSource.Applications)
            {
                return ApplicationsRestoreText.ForStatus(completion);
            }
            return StartupRestoreRestoreText.ForStatus(completion);
        }
    }
}
