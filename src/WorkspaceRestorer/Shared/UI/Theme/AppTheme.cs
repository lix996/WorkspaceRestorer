using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    internal static class AppTheme
    {
        // 颜色属性返回 ThemePalette 的共享令牌 Brush：切主题时原地改 Color，
        // 所有已经拿到该对象的控件都会更新；不要把令牌直接放进资源或样式。
        // 字体只在 AppFonts.cs 里定义；这两个字段保留给现有调用点，避免大范围改 using。
        public static readonly FontFamily UiFont = AppFonts.Ui;
        public static readonly FontFamily IconFont = AppFonts.Icon;
        public static SolidColorBrush WindowBackground
        {
            get { return SurfaceTokens.Window; }
        }
        public static SolidColorBrush Surface
        {
            get { return SurfaceTokens.Surface; }
        }
        public static SolidColorBrush TextPrimary
        {
            get { return TextTokens.Primary; }
        }
        public static SolidColorBrush TextSecondary
        {
            get { return TextTokens.Secondary; }
        }
        // Hairline borders only; the old #D8DEE4 read as heavy boxes.
        public static SolidColorBrush Border
        {
            get { return BorderTokens.Default; }
        }
        public static SolidColorBrush HoverSurface
        {
            get { return SurfaceTokens.Hover; }
        }
        public static SolidColorBrush HoverBorder
        {
            get { return BorderTokens.Hover; }
        }
        public static SolidColorBrush PressedSurface
        {
            get { return SurfaceTokens.Pressed; }
        }
        public static SolidColorBrush ActiveSurface
        {
            get { return SurfaceTokens.Active; }
        }
        public static SolidColorBrush Accent
        {
            get { return AccentTokens.Base; }
        }
        public static SolidColorBrush AccentHover
        {
            get { return AccentTokens.Hover; }
        }
        public static SolidColorBrush Positive
        {
            get { return StatusTokens.Positive; }
        }
        public static SolidColorBrush Warning
        {
            get { return StatusTokens.Warning; }
        }
        public static SolidColorBrush Danger
        {
            get { return StatusTokens.Danger; }
        }

        // 卡片/行/提示条的语义表面；调用方不要再写 AppTheme.Brush("#...")。
        public static SolidColorBrush RowSurface
        {
            get { return SurfaceTokens.Row; }
        }
        public static SolidColorBrush RowHoverSurface
        {
            get { return SurfaceTokens.RowHover; }
        }
        public static SolidColorBrush SelectionSurface
        {
            get { return SurfaceTokens.Selection; }
        }
        public static SolidColorBrush NavigationActiveSurface
        {
            get { return SurfaceTokens.ActiveStrong; }
        }
        public static SolidColorBrush SuccessSurface
        {
            get { return SurfaceTokens.Success; }
        }
        public static SolidColorBrush DangerSurface
        {
            get { return SurfaceTokens.Danger; }
        }
        public static SolidColorBrush WarningSurface
        {
            get { return SurfaceTokens.Warning; }
        }
        public static SolidColorBrush NeutralNoticeSurface
        {
            get { return SurfaceTokens.NeutralNotice; }
        }
        public static SolidColorBrush PanelSurface
        {
            get { return SurfaceTokens.Panel; }
        }
        public static SolidColorBrush PreviewSurface
        {
            get { return SurfaceTokens.Preview; }
        }
        public static SolidColorBrush RailSurface
        {
            get { return SurfaceTokens.Rail; }
        }
        public static SolidColorBrush PopupBorder
        {
            get { return BorderTokens.Popup; }
        }
        public static SolidColorBrush SoftBorder
        {
            get { return BorderTokens.Soft; }
        }

        /// <summary>Corner radius used by every control, card and popup.</summary>
        public const double Radius = 8;

        public static void Apply(Application application)
        {
            // 安装主题服务：跟随系统主题、深色标题栏和窗口加载时补色。
            ThemeService.Install();
            // 【2026-10-19】可选中复制的只读文字：点空白＝取消选中（全程序一次，主窗口/弹窗都覆盖）。
            SelectableText.InstallSelectionCancelHandler();

            ResourceDictionary resources = application.Resources;
            // 先注册稳定的 DynamicResource key；每次主题切换都会重新注册克隆。
            ThemeResources.Register(resources);

            Style textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, AppFonts.Ui));
            textStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, AppFonts.Body));
            textStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            textStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            resources.Add(typeof(TextBlock), textStyle);

            Style controlStyle = new Style(typeof(Control));
            controlStyle.Setters.Add(new Setter(Control.FontFamilyProperty, AppFonts.Ui));
            controlStyle.Setters.Add(new Setter(Control.FontSizeProperty, AppFonts.Body));
            controlStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            resources.Add(typeof(Control), controlStyle);

            // 右键菜单：默认的 WPF 菜单是老式的方角灰底，用户要求照着 Win11 的样子做
            // （2026-10-09 17:47）。这里统一一套圆角、留白、悬停淡色的外观，
            // 全程序所有 ContextMenu 都会跟着变。
            Style menuItemStyle = new Style(typeof(MenuItem));
            menuItemStyle.Setters.Add(new Setter(MenuItem.FontFamilyProperty, AppFonts.Ui));
            menuItemStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, AppFonts.Body));
            menuItemStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            menuItemStyle.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(12, 7, 12, 7)));
            menuItemStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, RoundedMenuItemTemplate()));
            resources.Add(typeof(MenuItem), menuItemStyle);

            Style contextMenuStyle = new Style(typeof(ContextMenu));
            contextMenuStyle.Setters.Add(new Setter(ContextMenu.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            contextMenuStyle.Setters.Add(new Setter(ContextMenu.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Border)));
            contextMenuStyle.Setters.Add(new Setter(ContextMenu.BorderThicknessProperty, new Thickness(1)));
            contextMenuStyle.Setters.Add(new Setter(ContextMenu.PaddingProperty, new Thickness(6)));
            contextMenuStyle.Setters.Add(new Setter(ContextMenu.TemplateProperty, RoundedContextMenuTemplate()));
            resources.Add(typeof(ContextMenu), contextMenuStyle);

            Style buttonStyle = new Style(typeof(Button));
            buttonStyle.BasedOn = controlStyle;
            buttonStyle.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            buttonStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Border)));
            buttonStyle.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(1)));
            buttonStyle.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(14, 8, 14, 8)));
            buttonStyle.Setters.Add(new Setter(Button.MinHeightProperty, 36.0));
            buttonStyle.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
            buttonStyle.Setters.Add(new Setter(Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            buttonStyle.Setters.Add(new Setter(Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            buttonStyle.Setters.Add(new Setter(Button.TemplateProperty, RoundedButtonTemplate()));
            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverSurface)));
            hover.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverBorder)));
            buttonStyle.Triggers.Add(hover);
            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.PressedSurface)));
            pressed.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverBorder)));
            buttonStyle.Triggers.Add(pressed);
            Trigger disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Button.OpacityProperty, 0.55));
            buttonStyle.Triggers.Add(disabled);
            resources.Add(typeof(Button), buttonStyle);

            Style accentButton = new Style(typeof(Button), buttonStyle);
            accentButton.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            accentButton.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            accentButton.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            Trigger accentHover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            accentHover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.AccentHover)));
            accentButton.Triggers.Add(accentHover);
            Trigger accentPressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            accentPressed.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.AccentPressed)));
            accentButton.Triggers.Add(accentPressed);
            resources.Add("AccentButton", accentButton);

            // 【2026-10-19 用户要求】“重置所有设置……按钮是绿色的，显得重要。”
            // 绿色按钮做成公共命名样式（GreenButton）：重要的确认类操作用它，别处也能复用。
            Style greenButton = new Style(typeof(Button), buttonStyle);
            greenButton.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButton)));
            greenButton.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButton)));
            greenButton.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            Trigger greenHover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            greenHover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButtonHover)));
            greenHover.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButtonHover)));
            greenButton.Triggers.Add(greenHover);
            Trigger greenPressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            greenPressed.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButtonPressed)));
            greenPressed.Setters.Add(new Setter(Button.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.PositiveButtonPressed)));
            greenButton.Triggers.Add(greenPressed);
            resources.Add("GreenButton", greenButton);

            Style iconButton = new Style(typeof(Button), buttonStyle);
            iconButton.Setters.Add(new Setter(Button.WidthProperty, 38.0));
            iconButton.Setters.Add(new Setter(Button.HeightProperty, 38.0));
            iconButton.Setters.Add(new Setter(Button.MinHeightProperty, 38.0));
            iconButton.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(0)));
            iconButton.Setters.Add(new Setter(Button.FontFamilyProperty, IconFont));
            iconButton.Setters.Add(new Setter(Button.FontSizeProperty, 16.0));
            iconButton.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            iconButton.Setters.Add(new Setter(Button.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            iconButton.Setters.Add(new Setter(Button.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextSecondary)));
            // Icon actions are the most used controls, so they light up with an
            // accent tint and the glyph itself turns accent on hover.  The tint is
            // deeper than ActiveSurface so it also shows on a selected card.
            Trigger iconHover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            iconHover.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.IconHover)));
            iconHover.Setters.Add(new Setter(Button.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            iconButton.Triggers.Add(iconHover);
            Trigger iconPressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            iconPressed.Setters.Add(new Setter(Button.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.IconPressed)));
            iconPressed.Setters.Add(new Setter(Button.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.AccentHover)));
            iconButton.Triggers.Add(iconPressed);
            resources.Add("IconButton", iconButton);

            Style textBoxStyle = new Style(typeof(TextBox), controlStyle);
            textBoxStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            textBoxStyle.Setters.Add(new Setter(TextBox.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Border)));
            textBoxStyle.Setters.Add(new Setter(TextBox.BorderThicknessProperty, new Thickness(1)));
            textBoxStyle.Setters.Add(new Setter(TextBox.PaddingProperty, new Thickness(10, 6, 10, 6)));
            textBoxStyle.Setters.Add(new Setter(TextBox.MinHeightProperty, 36.0));
            textBoxStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            textBoxStyle.Setters.Add(new Setter(TextBox.TemplateProperty, RoundedTextBoxTemplate()));
            // 所有文本框统一获得成熟文本框的行为：
            //  · 按住左键拖到左右边缘时内容自动横向滚动（WPF 默认不做）
            //  · 右键弹出标准文本菜单（剪切/复制/粘贴/全选）——自定义模板覆盖了默认样式，
            //    不去补的话右键是没有文本菜单的
            textBoxStyle.Setters.Add(new EventSetter(
                UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(OnAnyTextBoxPreviewMouseMove)));
            textBoxStyle.Setters.Add(new EventSetter(
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyTextBoxLoaded)));
            resources.Add(typeof(TextBox), textBoxStyle);

            // 【2026-11-15 P1】可选中复制的只读文字已改成 TextBlock 实现：
            // 不再有 TextBox 的文本编辑层，语言切换/换文字只走普通文本布局。
            Style selectableTextStyle = new Style(typeof(SelectableText));
            selectableTextStyle.Setters.Add(new Setter(TextBlock.FontFamilyProperty, AppFonts.Ui));
            selectableTextStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, AppFonts.Body));
            selectableTextStyle.Setters.Add(new Setter(
                TextBlock.ForegroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            selectableTextStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            selectableTextStyle.Setters.Add(new Setter(
                TextBlock.BackgroundProperty,
                System.Windows.Media.Brushes.Transparent));
            selectableTextStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(0)));
            selectableTextStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.IBeam));
            resources.Add(typeof(SelectableText), selectableTextStyle);

            Style listViewStyle = new Style(typeof(ListView), controlStyle);
            listViewStyle.Setters.Add(new Setter(ListView.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            listViewStyle.Setters.Add(new Setter(ListView.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Border)));
            listViewStyle.Setters.Add(new Setter(ListView.BorderThicknessProperty, new Thickness(1)));
            listViewStyle.Setters.Add(new Setter(ListView.PaddingProperty, new Thickness(0)));
            resources.Add(typeof(ListView), listViewStyle);

            Style checkBoxStyle = new Style(typeof(CheckBox), controlStyle);
            checkBoxStyle.Setters.Add(new Setter(CheckBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.CursorProperty, Cursors.Hand));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.CheckBoxBorder)));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.BorderThicknessProperty, new Thickness(1)));
            checkBoxStyle.Setters.Add(new Setter(CheckBox.TemplateProperty, RoundedCheckBoxTemplate()));
            resources.Add(typeof(CheckBox), checkBoxStyle);

            Style sliderStyle = new Style(typeof(Slider), controlStyle);
            sliderStyle.Setters.Add(new Setter(Slider.VerticalAlignmentProperty, VerticalAlignment.Center));
            resources.Add(typeof(Slider), sliderStyle);

            // 【2026-10-19 用户要求】“将优化启动项的下拉框修改成现代的，看起来高级的，
            //  而且把这种高级的下拉框写入公共代码，后续下拉框的设计就用现代高级的这种。”
            // 默认 Aero2 的 ComboBox 是 3D 斜边、老气。这里换成公共模板：
            // 白底细边框圆角、右侧小箭头、悬停/展开变主题色；下拉面板是白色圆角卡片 + 投影，
            // 选项悬停浅灰、选中浅青 + 主题色加粗。以后所有 ComboBox 自动用这一套。
            Style comboBoxStyle = new Style(typeof(ComboBox), controlStyle);
            comboBoxStyle.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Surface)));
            comboBoxStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Border)));
            comboBoxStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            comboBoxStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 0, 34, 0)));
            comboBoxStyle.Setters.Add(new Setter(Control.MinHeightProperty, 36.0));
            comboBoxStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            comboBoxStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            comboBoxStyle.Setters.Add(new Setter(Control.TemplateProperty, ModernComboBoxTemplate()));
            resources.Add(typeof(ComboBox), comboBoxStyle);

            // ComboBox 的选项（公共样式）：圆角、悬停浅灰、选中浅青 + 主题色。
            Style comboBoxItemStyle = new Style(typeof(ComboBoxItem));
            comboBoxItemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 7, 10, 7)));
            comboBoxItemStyle.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 1, 0, 1)));
            comboBoxItemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.TextPrimary)));
            comboBoxItemStyle.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            comboBoxItemStyle.Setters.Add(new Setter(Control.TemplateProperty, ModernComboBoxItemTemplate()));
            Trigger comboItemHover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
            comboItemHover.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverSurface)));
            comboBoxItemStyle.Triggers.Add(comboItemHover);
            Trigger comboItemSelected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
            comboItemSelected.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.ActiveSurface)));
            comboItemSelected.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            comboItemSelected.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            comboBoxItemStyle.Triggers.Add(comboItemSelected);
            resources.Add(typeof(ComboBoxItem), comboBoxItemStyle);

            // 悬浮提示与问号说明的外观、延迟和 ToolTipService 参数统一在
            // `Shared/UI/Theme/ToolTips/ToolTipTheme.cs`；这里只负责安装一次。
            ToolTipTheme.Install(resources);
            // 默认 ScrollBar 模板读系统色，WPF 资源覆盖不到；统一换成主题模板。
            ScrollBarTheme.Install(resources);
        }

        /// <summary>
        /// Flat, rounded button chrome. Colours keep coming from the button's own
        /// Background/BorderBrush so the existing styles and triggers still work.
        /// </summary>
        private static ControlTemplate RoundedButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            frame.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(Radius));
            frame.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            frame.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            frame.AppendChild(presenter);
            template.VisualTree = frame;
            return template;
        }

        /// <summary>
        /// Rounded text box chrome.  The content host keeps the built-in editing
        /// behaviour (caret, selection, scrolling); only the frame is replaced, so
        /// text can never be clipped by the template.
        /// </summary>
        /// <summary>
        /// 所有文本框共用：按住左键拖到编辑框左右边缘时，内容自动横向滚动，
        /// 这样被挡住的部分也能一路选中（WPF 的 TextBox 默认不做这件事）。
        /// </summary>
        private static void OnAnyTextBoxPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs args)
        {
            TextBox box = sender as TextBox;
            if (box == null || args.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                return;
            }
            ScrollViewer host = FindContentHost(box);
            if (host == null || host.ScrollableWidth <= 0.0)
            {
                return;
            }
            Point point = args.GetPosition(box);
            const double edge = 16.0;
            const double step = 20.0;
            if (point.X > box.ActualWidth - edge)
            {
                host.ScrollToHorizontalOffset(Math.Min(host.ScrollableWidth, host.HorizontalOffset + step));
            }
            else if (point.X < edge)
            {
                host.ScrollToHorizontalOffset(Math.Max(0.0, host.HorizontalOffset - step));
            }
        }

        /// <summary>所有文本框共用：加载时补上标准文本右键菜单（如果还没有）。</summary>
        private static void OnAnyTextBoxLoaded(object sender, RoutedEventArgs args)
        {
            TextBox box = sender as TextBox;
            if (box == null || box.ContextMenu != null)
            {
                return;
            }
            // 【2026-10-19】文本框的标准右键菜单（剪切/复制/粘贴/全选）统一在公共件
            // `Shared/UI/Controls/ContextMenus.cs` 里造，这里只负责“还没有就挂上”。
            Shared.ContextMenus.EnsureTextMenu(box);
        }

        /// <summary>取文本框模板里的标准滚动宿主。</summary>
        internal static ScrollViewer FindContentHost(TextBox box)
        {
            if (box == null || box.Template == null)
            {
                return null;
            }
            return box.Template.FindName("PART_ContentHost", box) as ScrollViewer;
        }

        /// <summary>把文本框视图拉回开头（进入编辑时用，配合全选）。</summary>
        internal static void ScrollToStart(TextBox box)
        {
            if (box == null)
            {
                return;
            }
            Action scroll = delegate
            {
                ScrollViewer host = FindContentHost(box);
                if (host != null)
                {
                    host.ScrollToHorizontalOffset(0.0);
                }
            };
            scroll();
            box.Dispatcher.BeginInvoke(scroll, System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 右键菜单的外框：圆角 + 细边框 + 一点留白（照着 Win11 的样子）。
        /// 用 Border 包一个 StackPanel 装菜单项，StackPanel 必须是 IsItemsHost。
        /// </summary>
        private static ControlTemplate RoundedContextMenuTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(ContextMenu));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            frame.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(Radius));
            frame.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(ContextMenu.BackgroundProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(ContextMenu.BorderBrushProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(ContextMenu.BorderThicknessProperty));
            frame.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(ContextMenu.PaddingProperty));
            frame.SetValue(FrameworkElement.SnapsToDevicePixelsProperty, true);
            FrameworkElementFactory items = new FrameworkElementFactory(typeof(StackPanel));
            // IsItemsHost 在 FrameworkElementFactory 上要通过 SetValue 设（它没有 CLR 属性）。
            items.SetValue(Panel.IsItemsHostProperty, true);
            frame.AppendChild(items);
            template.VisualTree = frame;
            return template;
        }

        /// <summary>单个菜单项：圆角悬停底色，去掉 WPF 默认那条左边距和灰底。</summary>
        private static ControlTemplate RoundedMenuItemTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(MenuItem));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            frame.Name = "menuItemFrame";
            frame.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6.0));
            frame.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            frame.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(MenuItem.PaddingProperty));
            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);
            template.VisualTree = frame;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverSurface), "menuItemFrame"));
            template.Triggers.Add(hover);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabled);
            return template;
        }

        private static ControlTemplate RoundedTextBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(TextBox));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            frame.Name = "textFrame";
            frame.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(Radius));
            frame.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(TextBox.BackgroundProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(TextBox.BorderBrushProperty));
            frame.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(TextBox.BorderThicknessProperty));
            FrameworkElementFactory host = new FrameworkElementFactory(typeof(ScrollViewer));
            host.Name = "PART_ContentHost";
            host.SetValue(Control.PaddingProperty, new TemplateBindingExtension(TextBox.PaddingProperty));
            // ScrollViewer 默认是 Disabled（完全不可滚），必须显式设成 Hidden：
            // 这样内容可以滚动但看不到滚动条——WPF 的默认 TextBox 模板就是这么做的。
            // 少了这两行，长文本既不会随光标滚动，拖到边缘也不会跟着滚。
            host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
            host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            host.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            frame.AppendChild(host);
            template.VisualTree = frame;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.HoverBorder), "textFrame"));
            template.Triggers.Add(hover);
            Trigger focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focused.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "textFrame"));
            template.Triggers.Add(focused);
            return template;
        }

        /// <summary>
        /// Rounded check box: a soft square that fills with the accent colour and
        /// shows a tick, instead of the square, dated default chrome.
        /// </summary>
        private static ControlTemplate RoundedCheckBoxTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(CheckBox));
            FrameworkElementFactory panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            FrameworkElementFactory box = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            box.Name = "box";
            box.SetValue(FrameworkElement.WidthProperty, 17.0);
            box.SetValue(FrameworkElement.HeightProperty, 17.0);
            box.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(5));
            box.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(CheckBox.BackgroundProperty));
            box.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(CheckBox.BorderBrushProperty));
            box.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new TemplateBindingExtension(CheckBox.BorderThicknessProperty));
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            FrameworkElementFactory tick = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            tick.Name = "tick";
            tick.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 0,4.5 L 3.6,8 L 10,1"));
            tick.SetValue(System.Windows.Shapes.Path.StrokeProperty, System.Windows.Media.Brushes.White);
            tick.SetValue(System.Windows.Shapes.Path.StrokeThicknessProperty, 2.0);
            tick.SetValue(System.Windows.Shapes.Path.StrokeStartLineCapProperty, PenLineCap.Round);
            tick.SetValue(System.Windows.Shapes.Path.StrokeEndLineCapProperty, PenLineCap.Round);
            tick.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            tick.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            tick.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(tick);
            panel.AppendChild(box);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            panel.AppendChild(presenter);
            template.VisualTree = panel;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "box"));
            template.Triggers.Add(hover);
            Trigger checkedState = new Trigger { Property = CheckBox.IsCheckedProperty, Value = true };
            checkedState.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "box"));
            checkedState.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "box"));
            checkedState.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "tick"));
            template.Triggers.Add(checkedState);
            return template;
        }

        /// <summary>
        /// 现代下拉框模板（公共代码，用户 2026-10-19 要求“后续下拉框的设计就用现代高级的这种”）：
        /// 白底细边框圆角 8 + 右侧小箭头；悬停/展开边框变主题色；
        /// 展开面板是白色圆角卡片 + 柔和投影；选项样式见 <see cref="ModernComboBoxItemTemplate"/>。
        /// 只支持**非可编辑**的 ComboBox（当前界面只用这种；以后要做可输入的下拉，另写模板）。
        /// </summary>
        private static ControlTemplate ModernComboBoxTemplate()
        {
            // 外层：边框（鼠标悬停/展开/焦点时改变边框色）
            FrameworkElementFactory chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            chrome.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            chrome.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            chrome.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(12, 0, 34, 0));
            chrome.SetValue(FrameworkElement.MinHeightProperty, 36.0);

            FrameworkElementFactory inner = new FrameworkElementFactory(typeof(Grid));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            presenter.SetValue(UIElement.IsHitTestVisibleProperty, false);
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemProperty));
            presenter.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
            presenter.SetValue(ContentPresenter.ContentStringFormatProperty, new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
            inner.AppendChild(presenter);

            FrameworkElementFactory arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.Name = "Arrow";
            arrow.SetValue(TextBlock.TextProperty, "\uE70D");
            arrow.SetValue(TextBlock.FontFamilyProperty, IconFont);
            arrow.SetValue(TextBlock.FontSizeProperty, 9.0);
            // 不能用 new SolidColorBrush(TextSecondary.Color)：切主题后它不会更新。
            // 绑定到 ComboBox.Foreground（由样式用 DynamicResource 提供），再用透明度弱化。
            arrow.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
            arrow.SetValue(UIElement.OpacityProperty, 0.7);
            arrow.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            inner.AppendChild(arrow);
            chrome.AppendChild(inner);

            // 透明按钮盖在整块上：点哪里都能展开/收起（不改变外观）。
            FrameworkElementFactory toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "DropDownToggle";
            toggle.SetValue(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            toggle.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            toggle.SetValue(Control.FocusableProperty, false);
            toggle.SetValue(Control.CursorProperty, Cursors.Hand);
            toggle.SetValue(ToggleButton.ClickModeProperty, ClickMode.Press);
            toggle.SetValue(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay
            });
            FrameworkElementFactory toggleChrome = new FrameworkElementFactory(typeof(Border));
            toggleChrome.SetValue(System.Windows.Controls.Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            ControlTemplate toggleTemplate = new ControlTemplate(typeof(ToggleButton));
            toggleTemplate.VisualTree = toggleChrome;
            toggle.SetValue(Control.TemplateProperty, toggleTemplate);

            // 展开面板
            FrameworkElementFactory popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Slide);
            popup.SetValue(Popup.StaysOpenProperty, false);
            popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(ComboBox.IsDropDownOpenProperty));

            FrameworkElementFactory drop = new FrameworkElementFactory(typeof(Border));
            drop.Name = "DropDownBorder";
            drop.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(ComboBox.BackgroundProperty));
            drop.SetValue(System.Windows.Controls.Border.BorderBrushProperty, new TemplateBindingExtension(ComboBox.BorderBrushProperty));
            drop.SetValue(System.Windows.Controls.Border.BorderThicknessProperty, new Thickness(1));
            drop.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(8));
            drop.SetValue(System.Windows.Controls.Border.PaddingProperty, new Thickness(4));
            drop.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
            drop.SetValue(FrameworkElement.MinWidthProperty, new Binding("ActualWidth")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
            });
            drop.SetValue(FrameworkElement.MaxHeightProperty, new TemplateBindingExtension(ComboBox.MaxDropDownHeightProperty));
            System.Windows.Media.Effects.DropShadowEffect dropShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.18,
                Color = Color.FromRgb(8, 32, 40)
            };
            dropShadow.Freeze();
            drop.SetValue(UIElement.EffectProperty, dropShadow);

            FrameworkElementFactory scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
            scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            FrameworkElementFactory itemsHost = new FrameworkElementFactory(typeof(StackPanel));
            itemsHost.SetValue(StackPanel.IsItemsHostProperty, true);
            itemsHost.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Contained);
            scroll.AppendChild(itemsHost);
            drop.AppendChild(scroll);
            popup.AppendChild(drop);

            ControlTemplate template = new ControlTemplate(typeof(ComboBox));
            template.VisualTree = new FrameworkElementFactory(typeof(Grid));
            template.VisualTree.AppendChild(chrome);
            template.VisualTree.AppendChild(toggle);
            template.VisualTree.AppendChild(popup);

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.ComboHoverBorder), "Chrome"));
            hover.Setters.Add(new Setter(System.Windows.Controls.Border.BackgroundProperty, new DynamicResourceExtension(ThemeResourceKeys.ComboHoverSurface), "Chrome"));
            hover.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "Arrow"));
            template.Triggers.Add(hover);
            Trigger focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "Chrome"));
            template.Triggers.Add(focus);
            Trigger open = new Trigger { Property = ComboBox.IsDropDownOpenProperty, Value = true };
            open.Setters.Add(new Setter(System.Windows.Controls.Border.BorderBrushProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "Chrome"));
            open.Setters.Add(new Setter(TextBlock.TextProperty, "\uE70E", "Arrow"));
            open.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(ThemeResourceKeys.Accent), "Arrow"));
            template.Triggers.Add(open);
            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
            template.Triggers.Add(disabled);
            return template;
        }

        /// <summary>下拉框选项：圆角、内外边距统一；悬停/选中的颜色由 <see cref="ComboBoxItem"/> 样式负责。</summary>
        private static ControlTemplate ModernComboBoxItemTemplate()
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            ControlTemplate template = new ControlTemplate(typeof(ComboBoxItem));
            template.VisualTree = border;
            return template;
        }

        /// <summary>
        /// 兼容旧调用点：新代码不要再用裸色号，改用上面的语义令牌。
        /// </summary>
        public static SolidColorBrush Brush(string color)
        {
            return AppColors.FromHex(color);
        }
    }
}
