using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 图标布局编辑器专用的深色画布、半透明遮罩、新图标提示色。
    ///
    /// 这些不是三个工作区共用的“表面”，放在全局 Theme/Tokens 会污染普通页面；
    /// 但也不能散落在各个编辑器分部文件里，所以集中在这一份。
    /// </summary>
    internal static class EditorPalette
    {
        internal static readonly SolidColorBrush CanvasBackground = AppColors.FromHex("#0B1220");
        internal static readonly SolidColorBrush WallpaperMaskLight = AppColors.FromHex("#1A000000");
        internal static readonly SolidColorBrush WallpaperMaskDark = AppColors.FromHex("#33000000");
        internal static readonly SolidColorBrush Panel = AppColors.FromHex("#E8EDF2");
        internal static readonly SolidColorBrush Sidebar = AppColors.FromHex("#F2F5F9");
        internal static readonly SolidColorBrush SidebarBorder = AppColors.FromHex("#D4DEE8");
        internal static readonly SolidColorBrush OverlayMask = AppColors.FromHex("#B30B1220");
        internal static readonly SolidColorBrush OverlayWindow = AppColors.FromHex("#F20F172A");
        internal static readonly SolidColorBrush OverlayAccentBorder = AppColors.FromHex("#331E88E5");
        internal static readonly SolidColorBrush MutedText = AppColors.FromHex("#A8B3C4");
        internal static readonly SolidColorBrush TileHover = AppColors.FromHex("#22FFFFFF");
        internal static readonly SolidColorBrush TileFloating = AppColors.FromHex("#24FFFFFF");
        internal static readonly SolidColorBrush TileFloatingBorder = AppColors.FromHex("#30FFFFFF");
        internal static readonly SolidColorBrush TileSelected = AppColors.FromHex("#33FFFFFF");
        internal static readonly SolidColorBrush DockBackground = AppColors.FromHex("#F2F8FAFC");
        internal static readonly SolidColorBrush DockBorder = AppColors.FromHex("#4D94A3B8");
        internal static readonly SolidColorBrush NewIconBorder = AppColors.FromHex("#9AD9BE");
        internal static readonly SolidColorBrush DangerBadge = AppColors.FromHex("#D92D20");
        internal static readonly SolidColorBrush DockSurface = AppColors.FromHex("#F8FAFC");
        internal static readonly SolidColorBrush DockText = AppColors.FromHex("#64748B");
        internal static readonly SolidColorBrush DockTextStrong = AppColors.FromHex("#334155");
        internal static readonly SolidColorBrush TileLink = AppColors.FromHex("#1668B3");
        internal static readonly SolidColorBrush TileLabelShadow = AppColors.FromHex("#B0000000");
        internal static readonly SolidColorBrush DangerOutline = AppColors.FromHex("#FF3B30");
        internal static readonly SolidColorBrush TrayBackground = AppColors.FromHex("#F0FDF4");
    }
}
