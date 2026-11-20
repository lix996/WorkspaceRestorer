using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>成功、警告、危险三种状态的前景色。</summary>
    internal static class StatusTokens
    {
        internal static SolidColorBrush Positive = AppColors.ThemeBrush("#198754");
        internal static SolidColorBrush Warning = AppColors.ThemeBrush("#B45309");
        internal static SolidColorBrush Danger = AppColors.ThemeBrush("#B42318");
        internal static SolidColorBrush PositiveStrong = AppColors.ThemeBrush("#2E7D32");
        internal static SolidColorBrush DangerStrong = AppColors.ThemeBrush("#C62828");
    }
}
