using WorkspaceRestorer.Desktop;

namespace WorkspaceRestorer.UI
{
    /// <summary>「图标位置保存」恢复布局的完成文案。</summary>
    internal static class DesktopLayoutRestoreText
    {
        internal static string ForStatus(DesktopRestoreResult result)
        {
            if (result == null)
            {
                return AppText.T(DesktopLayoutTextKeys.RestoreNoMatch);
            }
            if (result.Applied)
            {
                return AppText.F(
                    DesktopLayoutTextKeys.RestoreAppliedFormat,
                    result.AppliedCount);
            }
            return AppText.T(DesktopLayoutTextKeys.RestoreNotApplied);
        }

        internal static string ForNoExecution()
        {
            return AppText.T(DesktopLayoutTextKeys.RestoreNotExecuted);
        }

        internal static string ForFailure(string message)
        {
            return AppText.F(
                DesktopLayoutTextKeys.RestoreFailedMessageFormat,
                message ?? string.Empty);
        }
    }
}
