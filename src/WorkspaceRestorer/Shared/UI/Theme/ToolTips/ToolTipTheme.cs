using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共组件】全程序悬浮提示的唯一外观入口。
    ///
    /// 这里只管“提示长什么样、默认挂多久、问号按钮长什么样”：
    ///   · <see cref="ToolTip"/> 全局样式：白底、圆角 10、细边框、柔和投影；
    ///   · 问号按钮的 HelpButton 样式；
    ///   · 统一的显示位置、延迟、停留时间和“禁用时也能看到提示”；
    ///   · 宽度上限。
    ///
    /// 提示的内容（标题/说明/建议）和 ToolTipService 的挂法在
    /// <see cref="AccessibleToolTip"/> 里；两者同在 Theme/ToolTips 目录，
    /// 以后换外观只动本文件，换内容排版只动 AccessibleToolTip。
    /// </summary>
    internal static class ToolTipTheme
    {
        /// <summary>提示卡片的最大宽度，和原来的全局样式保持一致。</summary>
        internal const double MaximumWidth = 460.0;

        internal const int InitialShowDelayMilliseconds = 400;

        internal const int BetweenShowDelayMilliseconds = 100;

        internal const int ShowDurationMilliseconds = 15000;

        /// <summary>在 AppTheme.Apply 里注册全局提示外观；全程序只调一次。</summary>
        internal static void Install(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            Style toolTipStyle = new Style(typeof(ToolTip));
            toolTipStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            toolTipStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            toolTipStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.PopupBorder)));
            toolTipStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            toolTipStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            toolTipStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, MaximumWidth));
            toolTipStyle.Setters.Add(new Setter(ToolTip.HasDropShadowProperty, false));
            toolTipStyle.Setters.Add(new Setter(Control.TemplateProperty, CreateToolTipTemplate()));
            resources[typeof(ToolTip)] = toolTipStyle;

            Style helpButtonStyle = new Style(typeof(Button));
            helpButtonStyle.Setters.Add(new Setter(Control.WidthProperty, 17.0));
            helpButtonStyle.Setters.Add(new Setter(Control.HeightProperty, 17.0));
            helpButtonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            helpButtonStyle.Setters.Add(new Setter(Control.CursorProperty, System.Windows.Input.Cursors.Hand));
            helpButtonStyle.Setters.Add(new Setter(Control.FocusableProperty, false));
            helpButtonStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextSecondary)));
            helpButtonStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            helpButtonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.HelpBorder)));
            helpButtonStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            helpButtonStyle.Setters.Add(new Setter(Control.TemplateProperty, CreateHelpButtonTemplate()));
            resources["HelpButton"] = helpButtonStyle;
        }

        /// <summary>
        /// 所有提示共用的 ToolTipService 参数。调用方只传内容，不再自己设延迟/停留时间。
        /// </summary>
        internal static void ApplyDefaults(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }
            ToolTipService.SetPlacement(element, PlacementMode.Bottom);
            ToolTipService.SetHorizontalOffset(element, 4);
            ToolTipService.SetInitialShowDelay(element, InitialShowDelayMilliseconds);
            ToolTipService.SetBetweenShowDelay(element, BetweenShowDelayMilliseconds);
            ToolTipService.SetShowDuration(element, ShowDurationMilliseconds);
            ToolTipService.SetShowOnDisabled(element, true);
        }

        /// <summary>悬浮提示卡片：主题表面色、圆角 10、细边框、下方柔和投影。</summary>
        private static ControlTemplate CreateToolTipTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "TipBorder";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 10));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            DropShadowEffect shadow = new DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.16,
                Color = Color.FromRgb(8, 32, 40)
            };
            shadow.Freeze();
            border.SetValue(UIElement.EffectProperty, shadow);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(0));
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(ToolTip));
            template.VisualTree = border;
            return template;
        }

        /// <summary>问号按钮的小圆模板（悬停变主题浅色，按下更深一点）。</summary>
        private static ControlTemplate CreateHelpButtonTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "HelpBorder";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            ControlTemplate template = new ControlTemplate(typeof(Button));
            template.VisualTree = border;
            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.HelpHover), "HelpBorder"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "HelpBorder"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            template.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.HelpPressed), "HelpBorder"));
            template.Triggers.Add(pressed);
            return template;
        }
    }
}
