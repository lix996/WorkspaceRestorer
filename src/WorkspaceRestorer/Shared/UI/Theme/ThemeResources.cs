using System.Windows;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 把语义 Brush 注册成稳定的 DynamicResource key。
    /// 注册时使用冻结克隆，令牌 Brush 本身保持未冻结，切主题时原地改色；
    /// 引用同一令牌的控件和 DynamicResource 样式都会一起更新。
    /// </summary>
    internal static class ThemeResources
    {
        internal static void Register(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }
            Add(resources, ThemeResourceKeys.WindowBackground, AppTheme.WindowBackground);
            Add(resources, ThemeResourceKeys.Surface, AppTheme.Surface);
            Add(resources, ThemeResourceKeys.TextPrimary, AppTheme.TextPrimary);
            Add(resources, ThemeResourceKeys.TextSecondary, AppTheme.TextSecondary);
            Add(resources, ThemeResourceKeys.Border, AppTheme.Border);
            Add(resources, ThemeResourceKeys.HoverSurface, AppTheme.HoverSurface);
            Add(resources, ThemeResourceKeys.HoverBorder, AppTheme.HoverBorder);
            Add(resources, ThemeResourceKeys.PressedSurface, AppTheme.PressedSurface);
            Add(resources, ThemeResourceKeys.ActiveSurface, AppTheme.ActiveSurface);
            Add(resources, ThemeResourceKeys.Accent, AppTheme.Accent);
            Add(resources, ThemeResourceKeys.AccentHover, AppTheme.AccentHover);
            Add(resources, ThemeResourceKeys.AccentPressed, AccentTokens.Pressed);
            Add(resources, ThemeResourceKeys.Positive, AppTheme.Positive);
            Add(resources, ThemeResourceKeys.Warning, AppTheme.Warning);
            Add(resources, ThemeResourceKeys.Danger, AppTheme.Danger);

            Add(resources, ThemeResourceKeys.RowSurface, AppTheme.RowSurface);
            Add(resources, ThemeResourceKeys.RowHoverSurface, AppTheme.RowHoverSurface);
            Add(resources, ThemeResourceKeys.SelectionSurface, AppTheme.SelectionSurface);
            Add(resources, ThemeResourceKeys.NavigationActiveSurface, AppTheme.NavigationActiveSurface);
            Add(resources, ThemeResourceKeys.SuccessSurface, AppTheme.SuccessSurface);
            Add(resources, ThemeResourceKeys.DangerSurface, AppTheme.DangerSurface);
            Add(resources, ThemeResourceKeys.WarningSurface, AppTheme.WarningSurface);
            Add(resources, ThemeResourceKeys.NeutralNoticeSurface, AppTheme.NeutralNoticeSurface);
            Add(resources, ThemeResourceKeys.PanelSurface, AppTheme.PanelSurface);
            Add(resources, ThemeResourceKeys.PreviewSurface, AppTheme.PreviewSurface);
            Add(resources, ThemeResourceKeys.RailSurface, AppTheme.RailSurface);
            Add(resources, ThemeResourceKeys.PopupBorder, AppTheme.PopupBorder);
            Add(resources, ThemeResourceKeys.SoftBorder, AppTheme.SoftBorder);

            Add(resources, ThemeResourceKeys.PositiveButton, ControlTokens.PositiveButton);
            Add(resources, ThemeResourceKeys.PositiveButtonHover, ControlTokens.PositiveButtonHover);
            Add(resources, ThemeResourceKeys.PositiveButtonPressed, ControlTokens.PositiveButtonPressed);
            Add(resources, ThemeResourceKeys.IconHover, ControlTokens.IconHover);
            Add(resources, ThemeResourceKeys.IconPressed, ControlTokens.IconPressed);
            Add(resources, ThemeResourceKeys.CheckBoxBorder, ControlTokens.CheckBoxBorder);
            Add(resources, ThemeResourceKeys.ComboHoverBorder, ControlTokens.ComboHoverBorder);
            Add(resources, ThemeResourceKeys.ComboHoverSurface, ControlTokens.ComboHoverSurface);
            Add(resources, ThemeResourceKeys.HelpBorder, ControlTokens.HelpBorder);
            Add(resources, ThemeResourceKeys.HelpPressed, ControlTokens.HelpPressed);
            Add(resources, ThemeResourceKeys.HelpHover, ControlTokens.HelpHover);
            Add(resources, ThemeResourceKeys.ScrollBarTrack, ControlTokens.ScrollBarTrack);
            Add(resources, ThemeResourceKeys.ScrollBarThumb, ControlTokens.ScrollBarThumb);
            Add(resources, ThemeResourceKeys.ScrollBarThumbHover, ControlTokens.ScrollBarThumbHover);
            Add(resources, ThemeResourceKeys.ScrollBarThumbPressed, ControlTokens.ScrollBarThumbPressed);

            // 默认 WPF 控件（文本框等）读 SystemColors；每次换主题也换成当前克隆。
            resources[SystemColors.ControlTextBrushKey] = CopyBrush(AppTheme.TextPrimary);
            resources[SystemColors.WindowBrushKey] = CopyBrush(AppTheme.Surface);
        }

        /// <summary>
        /// 给控件属性挂 DynamicResource。页面不想写 key 时用这个入口。
        /// </summary>
        internal static void Bind(DependencyObject target, DependencyProperty property, string key)
        {
            if (target == null || property == null || string.IsNullOrEmpty(key))
            {
                return;
            }
            FrameworkElement element = target as FrameworkElement;
            if (element != null)
            {
                element.SetResourceReference(property, key);
                return;
            }
            FrameworkContentElement content = target as FrameworkContentElement;
            if (content != null)
            {
                content.SetResourceReference(property, key);
            }
        }

        private static void Add(ResourceDictionary resources, string key, Brush brush)
        {
            if (brush != null)
            {
                resources[key] = CopyBrush(brush);
            }
        }

        /// <summary>
        /// 令牌 Brush 必须保持未冻结，切主题时才能原地改 Color。
        /// 资源字典只放冻结克隆，避免 WPF 反向冻结令牌本身。
        /// </summary>
        private static Brush CopyBrush(Brush brush)
        {
            if (brush == null)
            {
                return null;
            }
            Brush copy = brush.Clone();
            if (copy.CanFreeze)
            {
                copy.Freeze();
            }
            return copy;
        }
    }
}
