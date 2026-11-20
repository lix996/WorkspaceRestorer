using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 颜色令牌的底层工厂：只接受 #RRGGBB / #AARRGGBB。
    /// FromHex 返回冻结 Brush，用于局部固定色和资源克隆；
    /// ThemeBrush 返回未冻结的共享令牌，只供 ThemePalette/令牌文件使用。
    ///
    /// 页面和控件模板不再直接调用它；它们只认 SurfaceTokens / ControlTokens /
    /// TextTokens / BorderTokens / AccentTokens / StatusTokens 里的用途名。
    /// </summary>
    internal static class AppColors
    {
        internal static SolidColorBrush FromHex(string color)
        {
            SolidColorBrush brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
            return brush;
        }

        /// <summary>
        /// 主题语义色专用：不 Freeze，主题切换时直接改 Brush.Color，
        /// 所有引用同一个 Brush 的控件会立即重绘。
        /// 不能用于跨线程访问的 Brush；当前主题令牌只在 UI 线程使用。
        /// </summary>
        internal static SolidColorBrush ThemeBrush(string color)
        {
            SolidColorBrush brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(color));
            return brush;
        }
    }
}
