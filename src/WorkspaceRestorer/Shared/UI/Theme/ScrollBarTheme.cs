using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 全程序滚动条外观。WPF 默认 ScrollBar 模板读的是系统色，不会被主题资源影响，
    /// 所以这里用一份公共模板：轨道/滑块/箭头都走 ThemeResourceKeys，切主题时自动更新。
    /// Track 的 Thumb/RepeatButton 是 CLR 属性，FrameworkElementFactory 不能 AppendChild，
    /// 因此用 ThemedTrack 在构造时装好零件；不要在页面里单独设置滚动条颜色。
    /// </summary>
    internal static class ScrollBarTheme
    {
        internal static void Install(ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }
            Style style = new Style(typeof(ScrollBar));
            style.Setters.Add(new Setter(
                Control.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.ScrollBarTrack)));
            style.Setters.Add(new Setter(
                Control.BorderBrushProperty,
                new DynamicResourceExtension(ThemeResourceKeys.Border)));
            style.Setters.Add(new Setter(Control.TemplateProperty, CreateTemplate(false)));

            // 上下/左右箭头固定在两端，不再交给 Track 排版；
            // 横向滚动条换一套左右箭头的模板。
            Trigger horizontal = new Trigger
            {
                Property = ScrollBar.OrientationProperty,
                Value = Orientation.Horizontal
            };
            horizontal.Setters.Add(new Setter(
                Control.TemplateProperty,
                CreateTemplate(true)));
            style.Triggers.Add(horizontal);
            resources.Add(typeof(ScrollBar), style);
        }

        private static ControlTemplate CreateTemplate(bool horizontal)
        {
            ControlTemplate template = new ControlTemplate(typeof(ScrollBar));
            FrameworkElementFactory root = new FrameworkElementFactory(typeof(Grid));
            root.SetValue(Panel.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            FrameworkElementFactory track = new FrameworkElementFactory(typeof(ThemedTrack));
            track.Name = "PART_Track";
            track.SetValue(Track.OrientationProperty, new TemplateBindingExtension(ScrollBar.OrientationProperty));
            track.SetValue(Track.MinimumProperty, new TemplateBindingExtension(ScrollBar.MinimumProperty));
            track.SetValue(Track.MaximumProperty, new TemplateBindingExtension(ScrollBar.MaximumProperty));
            track.SetValue(Track.ValueProperty, new TemplateBindingExtension(ScrollBar.ValueProperty));
            track.SetValue(Track.ViewportSizeProperty, new TemplateBindingExtension(ScrollBar.ViewportSizeProperty));
            root.AppendChild(track);

            if (horizontal)
            {
                root.AppendChild(CreateArrowOverlay(false, false));
                root.AppendChild(CreateArrowOverlay(false, true));
            }
            else
            {
                root.AppendChild(CreateArrowOverlay(true, false));
                root.AppendChild(CreateArrowOverlay(true, true));
            }

            template.VisualTree = root;
            return template;
        }

        /// <summary>
        /// 固定在滚动条两端的箭头。Track 自己的 RepeatButton 只保留点击区域，
        /// 外观和位置由这里这两个独立按钮负责，所以滚动/拖动时箭头不会跟着 Track 重排。
        /// </summary>
        private static FrameworkElementFactory CreateArrowOverlay(bool vertical, bool increase)
        {
            string glyph = vertical
                ? (increase ? "\uE70D" : "\uE70E")
                : (increase ? "\uE76C" : "\uE76B");

            FrameworkElementFactory button = new FrameworkElementFactory(typeof(RepeatButton));
            button.Name = vertical
                ? (increase ? "PART_ArrowDown" : "PART_ArrowUp")
                : (increase ? "PART_ArrowRight" : "PART_ArrowLeft");
            button.SetValue(Control.FocusableProperty, false);
            button.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            button.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            button.SetValue(Control.TemplateProperty, CreateFixedArrowTemplate(glyph));
            button.SetValue(
                ButtonBase.CommandProperty,
                vertical
                    ? (increase ? ScrollBar.LineDownCommand : ScrollBar.LineUpCommand)
                    : (increase ? ScrollBar.LineRightCommand : ScrollBar.LineLeftCommand));
            button.SetValue(
                ButtonBase.CommandTargetProperty,
                new Binding
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
                });

            if (vertical)
            {
                button.SetValue(FrameworkElement.HeightProperty, 16.0);
                button.SetValue(
                    FrameworkElement.VerticalAlignmentProperty,
                    increase ? VerticalAlignment.Bottom : VerticalAlignment.Top);
                button.SetValue(
                    FrameworkElement.HorizontalAlignmentProperty,
                    HorizontalAlignment.Stretch);
            }
            else
            {
                button.SetValue(FrameworkElement.WidthProperty, 16.0);
                button.SetValue(
                    FrameworkElement.HorizontalAlignmentProperty,
                    increase ? HorizontalAlignment.Right : HorizontalAlignment.Left);
                button.SetValue(
                    FrameworkElement.VerticalAlignmentProperty,
                    VerticalAlignment.Stretch);
            }
            return button;
        }

        private static ControlTemplate CreateFixedArrowTemplate(string glyph)
        {
            ControlTemplate template = new ControlTemplate(typeof(RepeatButton));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            frame.SetValue(Border.BorderThicknessProperty, new Thickness(0));

            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.SetValue(TextBlock.TextProperty, glyph);
            arrow.SetValue(TextBlock.FontFamilyProperty, AppFonts.Icon);
            arrow.SetValue(TextBlock.FontSizeProperty, 8.0);
            arrow.SetValue(
                TextBlock.ForegroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.TextSecondary));
            arrow.SetValue(
                FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            arrow.SetValue(
                FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            frame.AppendChild(arrow);

            template.VisualTree = frame;
            return template;
        }
    }

    /// <summary>
    /// Track 的 Thumb/DecreaseRepeatButton/IncreaseRepeatButton 是 CLR 属性，
    /// 只能在构造时装零件；本类只被 ScrollBarTheme 使用。
    /// </summary>
    internal sealed class ThemedTrack : Track
    {
        public ThemedTrack()
        {
            Thumb = CreateThumb();
            DecreaseRepeatButton = CreateRepeatButton();
            IncreaseRepeatButton = CreateRepeatButton();
            ApplyOrientation(Orientation.Vertical);
            Loaded += delegate { UpdateCommandTargets(); };
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == OrientationProperty)
            {
                ApplyOrientation((Orientation)e.NewValue);
            }
        }

        private static Thumb CreateThumb()
        {
            Thumb thumb = new Thumb
            {
                Focusable = false,
                Template = CreateThumbTemplate()
            };
            thumb.SetResourceReference(Control.BackgroundProperty, ThemeResourceKeys.ScrollBarThumb);
            return thumb;
        }

        private static ControlTemplate CreateThumbTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Thumb));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "thumbFrame";
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4.0));
            frame.SetValue(Border.MarginProperty, new Thickness(2.0));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            template.VisualTree = frame;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.ScrollBarThumbHover),
                "thumbFrame"));
            template.Triggers.Add(hover);

            Trigger dragging = new Trigger { Property = Thumb.IsDraggingProperty, Value = true };
            dragging.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.ScrollBarThumbPressed),
                "thumbFrame"));
            template.Triggers.Add(dragging);
            return template;
        }

        private static RepeatButton CreateRepeatButton()
        {
            TextBlock arrow = new TextBlock
            {
                FontFamily = AppFonts.Icon,
                FontSize = 8.0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            arrow.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextSecondary);
            RepeatButton button = new RepeatButton
            {
                Focusable = false,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                // 箭头外观由 ScrollBarTheme 的固定浮层负责；Track 自带的按钮只保留
                // 点击区域和 Track 布局，避免滚动时箭头跟着 Track 重排。
                Opacity = 0.0,
                Content = arrow,
                Template = CreateArrowTemplate()
            };
            return button;
        }

        internal static ControlTemplate CreateArrowTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(RepeatButton));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(presenter);
            template.VisualTree = frame;
            return template;
        }

        private void UpdateCommandTargets()
        {
            // RepeatButton 不是 FrameworkElementFactory 子项，TemplatedParent 不会自动成为其命令目标；
            // 等 Loaded 时 ThemedTrack 已经挂在 ScrollBar 模板里，再把目标指向 ScrollBar。
            IInputElement target = TemplatedParent as IInputElement;
            if (DecreaseRepeatButton != null)
            {
                DecreaseRepeatButton.CommandTarget = target;
            }
            if (IncreaseRepeatButton != null)
            {
                IncreaseRepeatButton.CommandTarget = target;
            }
        }

        private void ApplyOrientation(Orientation orientation)
        {
            bool horizontal = orientation == Orientation.Horizontal;
            IsDirectionReversed = !horizontal;
            UpdateCommandTargets();
            if (Thumb != null)
            {
                Thumb.MinWidth = horizontal ? 24.0 : 8.0;
                Thumb.MinHeight = horizontal ? 8.0 : 24.0;
            }
            RepeatButton decrease = DecreaseRepeatButton;
            RepeatButton increase = IncreaseRepeatButton;
            if (decrease != null)
            {
                decrease.Command = horizontal ? ScrollBar.LineLeftCommand : ScrollBar.LineUpCommand;
                TextBlock arrow = decrease.Content as TextBlock;
                if (arrow != null)
                {
                    arrow.Text = horizontal ? "\uE76B" : "\uE70E";
                }
                decrease.Width = horizontal ? 16.0 : double.NaN;
                decrease.Height = horizontal ? double.NaN : 16.0;
            }
            if (increase != null)
            {
                increase.Command = horizontal ? ScrollBar.LineRightCommand : ScrollBar.LineDownCommand;
                TextBlock arrow = increase.Content as TextBlock;
                if (arrow != null)
                {
                    arrow.Text = horizontal ? "\uE76C" : "\uE70D";
                }
                increase.Width = horizontal ? 16.0 : double.NaN;
                increase.Height = horizontal ? double.NaN : 16.0;
            }
        }
    }
}
