using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「自启动应用」逐项启动的完成文案。
    ///
    /// 第一轮保持与「启动与恢复」相同的用户可见句子，只把入口独立出来：
    /// 以后要改成“自启动应用已启动 X / N 项”，只动这个文件。
    /// </summary>
    internal static class ApplicationsRestoreText
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
