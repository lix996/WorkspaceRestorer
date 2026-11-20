using System;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 浅色/深色两套语义色。
    ///
    /// 主题令牌是**同一个未冻结 Brush 对象**：切主题时只改它的 Color，
    /// 所有引用它的控件（包括隐藏页、延迟创建的内容和悬停缓存）一起重绘。
    /// 因此这里绝不能把令牌 Brush 直接放进资源字典或样式；ThemeResources
    /// 注册资源时使用克隆并冻结克隆，令牌本身保持可写。
    /// </summary>
    internal static class ThemePalette
    {
        internal static void Apply(bool dark)
        {
            ApplySurface(dark);
            ApplyText(dark);
            ApplyBorder(dark);
            ApplyAccent(dark);
            ApplyStatus(dark);
            ApplyControl(dark);
        }

        private static void ApplySurface(bool dark)
        {
            Replace(ref SurfaceTokens.Window, dark ? "#0B1220" : "#F7F8FA");
            Replace(ref SurfaceTokens.Surface, dark ? "#111827" : "#FFFFFF");
            Replace(ref SurfaceTokens.Row, dark ? "#172033" : "#FAFBFC");
            Replace(ref SurfaceTokens.RowHover, dark ? "#1F3442" : "#E7F1F8");
            Replace(ref SurfaceTokens.Selection, dark ? "#15333D" : "#EAF3F6");
            Replace(ref SurfaceTokens.Active, dark ? "#17313A" : "#E8F1F4");
            Replace(ref SurfaceTokens.ActiveStrong, dark ? "#1E3E49" : "#DCEAEF");
            Replace(ref SurfaceTokens.Hover, dark ? "#1E293B" : "#E9EFF4");
            Replace(ref SurfaceTokens.Pressed, dark ? "#263449" : "#DCE5EB");
            Replace(ref SurfaceTokens.Success, dark ? "#123A2A" : "#E7F4EC");
            Replace(ref SurfaceTokens.Danger, dark ? "#3A1D1D" : "#FDECEA");
            Replace(ref SurfaceTokens.Warning, dark ? "#3A2A12" : "#FDF3E3");
            Replace(ref SurfaceTokens.NeutralNotice, dark ? "#1F2937" : "#EEF2F5");
            Replace(ref SurfaceTokens.Panel, dark ? "#16202E" : "#F3F7F8");
            Replace(ref SurfaceTokens.Preview, dark ? "#0F172A" : "#F7FAFB");
            Replace(ref SurfaceTokens.Rail, dark ? "#0F1A26" : "#F1F5F7");
            Replace(ref SurfaceTokens.SearchHitBackground, dark ? "#3322B8CF" : "#1F0E7490");
            Replace(ref SurfaceTokens.SearchHitBorder, dark ? "#5522B8CF" : "#330E7490");
            Replace(ref SurfaceTokens.SearchHitText, dark ? "#67E8F9" : "#0B5566");
            Replace(ref SurfaceTokens.DragTargetFill, dark ? "#3322B8CF" : "#330E7490");
        }

        private static void ApplyText(bool dark)
        {
            Replace(ref TextTokens.Primary, dark ? "#E5EDF5" : "#18212A");
            Replace(ref TextTokens.Secondary, dark ? "#9FB0C0" : "#5B6875");
        }

        private static void ApplyBorder(bool dark)
        {
            Replace(ref BorderTokens.Default, dark ? "#2B3A4A" : "#E4E8EC");
            Replace(ref BorderTokens.Hover, dark ? "#3D5164" : "#C3D3DD");
            Replace(ref BorderTokens.Popup, dark ? "#31465A" : "#DCE5E9");
            Replace(ref BorderTokens.Soft, dark ? "#223142" : "#EDF2F4");
        }

        private static void ApplyAccent(bool dark)
        {
            Replace(ref AccentTokens.Base, dark ? "#22B8CF" : "#0E7490");
            Replace(ref AccentTokens.Hover, dark ? "#38D0E6" : "#155E75");
            Replace(ref AccentTokens.Pressed, dark ? "#0E7490" : "#164E63");
        }

        private static void ApplyStatus(bool dark)
        {
            Replace(ref StatusTokens.Positive, dark ? "#34D399" : "#198754");
            Replace(ref StatusTokens.Warning, dark ? "#FBBF24" : "#B45309");
            Replace(ref StatusTokens.Danger, dark ? "#F87171" : "#B42318");
            Replace(ref StatusTokens.PositiveStrong, dark ? "#4ADE80" : "#2E7D32");
            Replace(ref StatusTokens.DangerStrong, dark ? "#FCA5A5" : "#C62828");
        }

        private static void ApplyControl(bool dark)
        {
            Replace(ref ControlTokens.PositiveButton, dark ? "#15803D" : "#1B8A5A");
            Replace(ref ControlTokens.PositiveButtonHover, dark ? "#16A34A" : "#16744C");
            Replace(ref ControlTokens.PositiveButtonPressed, dark ? "#166534" : "#115E3D");
            Replace(ref ControlTokens.IconHover, dark ? "#1E3A4A" : "#D8E9EF");
            Replace(ref ControlTokens.IconPressed, dark ? "#24485A" : "#C4DFE7");
            Replace(ref ControlTokens.CheckBoxBorder, dark ? "#4B5D6E" : "#C3CDD6");
            Replace(ref ControlTokens.ComboHoverBorder, dark ? "#4B5D6E" : "#B9CBD2");
            Replace(ref ControlTokens.ComboHoverSurface, dark ? "#16202E" : "#FCFDFE");
            Replace(ref ControlTokens.HelpBorder, dark ? "#3B4E5E" : "#C6D3D9");
            Replace(ref ControlTokens.HelpPressed, dark ? "#24485A" : "#D3E7EC");
            Replace(ref ControlTokens.HelpHover, dark ? "#1E3A4A" : "#E4F0F3");
            Replace(ref ControlTokens.ScrollBarTrack, dark ? "#0B1220" : "#F7F8FA");
            Replace(ref ControlTokens.ScrollBarThumb, dark ? "#3D5164" : "#C3CDD6");
            Replace(ref ControlTokens.ScrollBarThumbHover, dark ? "#4B5D6E" : "#A8B3C4");
            Replace(ref ControlTokens.ScrollBarThumbPressed, dark ? "#5B7085" : "#8A97A6");
        }

        private static void Replace(ref SolidColorBrush target, string hex)
        {
            Color color = (Color)ColorConverter.ConvertFromString(hex);
            if (target == null)
            {
                target = AppColors.ThemeBrush(hex);
                return;
            }
            if (target.IsFrozen)
            {
                // 令牌 Brush 一旦被放进资源/样式就会被 WPF 冻结；这时不能再原地改色，
                // 也不能换成新对象（已有控件仍指向旧对象）。这是编程错误，直接暴露。
                throw new InvalidOperationException("Theme token brush is frozen: " + hex);
            }
            target.Color = color;
        }
    }
}
