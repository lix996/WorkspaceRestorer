using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    internal static class UiFactory
    {

        /// <summary>
        /// 标题文字。【2026-10-19】改成 <see cref="SelectableText"/>：用户要求“现有的字体都可以按住左键
        /// 选取复制”，标题/说明这类**不可交互**的文字全部可选中复制（外观不变：透明、无边框）。
        /// </summary>
        public static SelectableText Heading(string text, double size)
        {
            return new SelectableText
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary
            };
        }

        /// <summary>标题文字，文案 key 由 LocalizedText 跟随语言即时更新。</summary>
        public static SelectableText HeadingKey(string key, double size)
        {
            SelectableText block = Heading(string.Empty, size);
            LocalizedText.Text(block, key);
            return block;
        }

        /// <summary>
        /// 【2026-11-15】“大标题 + 一句话 + 问号”的公共标题行。
        ///
        /// 页面上原来那行两三个折行的长说明不再占位置：只留一句短话，
        /// 完整的旧说明放进问号（悬停看详情、点击弹可复制说明框）。
        /// 交互仍然是全程序唯一那份 <see cref="HelpHint.BuildHintLine(HelpDefinition)"/>，
        /// 和设置行的小字 + 问号完全一致；这里只负责把标题和小字排在一起。
        /// </summary>
        public static StackPanel HeadingWithHintKey(
            string titleKey,
            string shortKey,
            string detailKey,
            double size)
        {
            StackPanel header = new StackPanel();
            header.Children.Add(HeadingKey(titleKey, size));
            Grid hint = HelpHint.BuildHintLine(HelpDefinition.Create(titleKey, shortKey, detailKey));
            if (hint != null)
            {
                header.Children.Add(hint);
            }
            return header;
        }

        /// <summary>说明文字（可选中复制；字体比标题小一号、次要色）。</summary>
        public static SelectableText SupportingText(string text)
        {
            return new SelectableText
            {
                Text = text,
                FontSize = AppFonts.Small,
                Foreground = AppTheme.TextSecondary
            };
        }

        /// <summary>说明文字，文案 key 由 LocalizedText 跟随语言即时更新。</summary>
        public static SelectableText SupportingTextKey(string key)
        {
            SelectableText block = SupportingText(string.Empty);
            LocalizedText.Text(block, key);
            return block;
        }

        public static Button CommandButton(string icon, string text, bool accent)
        {
            TextBlock label;
            Button button = BuildCommandButton(icon, accent, out label);
            label.Text = text;
            return button;
        }

        /// <summary>命令按钮，文字用 key 登记，语言切换后只更新文字不换按钮。</summary>
        public static Button CommandButtonKey(string icon, string key, bool accent)
        {
            TextBlock label;
            Button button = BuildCommandButton(icon, accent, out label);
            LocalizedText.Text(label, key);
            return button;
        }

        private static Button BuildCommandButton(string icon, bool accent, out TextBlock label)
        {
            Button button = new Button();
            if (accent)
            {
                button.Style = (Style)Application.Current.Resources["AccentButton"];
            }

            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = IconGlyph(icon),
                // 图标必须用图标字体：以前这里错用了文本字体（微软雅黑），
                // 于是普通命令按钮的图标全都缺字形、显示成空框。
                FontFamily = AppFonts.Icon,
                FontSize = 15,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = accent ? Brushes.White : AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            label = new TextBlock
            {
                Foreground = accent ? Brushes.White : AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(label);
            button.Content = content;
            return button;
        }

        /// <summary>
        /// 行首“序号 + 展开箭头”按钮的尺寸。启动与恢复、自启动应用共用这一份，
        /// 不要在调用方再写 54/24 之类的数字；列宽 = 按钮宽 + 右侧留白。
        /// </summary>
        internal const double RowExpandButtonWidth = 54.0;

        internal const double RowExpandButtonHeight = 24.0;

        internal const double RowExpandColumnWidth = 56.0;

        /// <summary>
        /// 【两页共用】“序号 + 展开箭头”按钮。
        ///
        /// 外观、悬停、按下全部由 <see cref="IconButton"/> 的 IconButton 样式提供：
        /// 悬停时按钮整块变成浅蓝，按下再深一档。调用方**不要**再设置 Background / Foreground /
        /// BorderThickness，否则本地值会盖掉样式触发器，鼠标放上去就没有任何反应。
        /// 序号保持次要色（和启动与恢复一样），箭头用同一套图标字体和行高。
        /// </summary>
        public static Button OrdinalExpandButton(int ordinal, bool expanded, out TextBlock arrowGlyph)
        {
            Button button = IconButton(
                expanded ? "\uE70D" : "\uE76C",
                "展开或折叠项目列表",
                "显示或隐藏此项目中已保存的网页或文件夹。");
            button.Width = RowExpandButtonWidth;
            button.Height = RowExpandButtonHeight;
            button.MinHeight = RowExpandButtonHeight;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(0, 0, RowExpandColumnWidth - RowExpandButtonWidth, 0);

            TextBlock ordinalText = new TextBlock
            {
                Text = ordinal > 0 ? ordinal + "." : string.Empty,
                FontFamily = AppFonts.Ui,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                MinWidth = 24,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 4, 0)
            };
            arrowGlyph = new TextBlock
            {
                Text = IconGlyph(expanded ? "\uE70D" : "\uE76C"),
                FontFamily = AppFonts.Icon,
                FontSize = 15,
                // 固定行高：图标字体的默认行距会把字形挤出按钮可视区，箭头下半截会被裁。
                LineHeight = 20,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(2, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel toggleContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            toggleContent.Children.Add(ordinalText);
            toggleContent.Children.Add(arrowGlyph);
            button.Content = toggleContent;
            return button;
        }

        /// <summary>展开状态变化时只换字形；字体、尺寸和悬停背景仍由按钮样式统一管理。</summary>
        public static void SetExpandGlyph(TextBlock arrowGlyph, bool expanded)
        {
            if (arrowGlyph == null)
            {
                return;
            }
            arrowGlyph.Text = IconGlyph(expanded ? "\uE70D" : "\uE76C");
        }

        public static Button IconButton(string icon, string accessibleName, string description)
        {
            return IconButton(icon, ToolTipContent.Named(accessibleName, description));
        }

        /// <summary>
        /// 图标按钮的公共内容版本：文字从 Theme/ToolTips/Content 的模块目录取。
        /// </summary>
        public static Button IconButton(string icon, TooltipDefinition definition)
        {
            Button button = IconButton(icon, ToolTipContent.Plain(string.Empty));
            AccessibleToolTip.Attach(button, definition);
            return button;
        }

        public static Button IconButton(string icon, ToolTipContent content)
        {
            Button button = new Button
            {
                // 图标放进自己的 TextBlock：这是工具栏按钮已验证可行的写法。
                // 直接给按钮 Content 赋字符串时，按钮模板渲染内容可能不用我们设的字体，
                // 结果卡片上的展开箭头、图标按钮都是空框；TextBlock 上的本地字体最稳。
                Content = new TextBlock
                {
                    Text = IconGlyph(icon),
                    FontFamily = AppFonts.Icon,
                    FontSize = 16,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                FontFamily = AppFonts.Icon,
                Style = (Style)Application.Current.Resources["IconButton"]
            };
            // 明确指定图标字体（本地值优先于样式，任何样式都覆盖不掉）。
            // 之前这里被改成文本字体，导致卡片上的图标按钮、展开箭头全显示为空框。
            button.FontFamily = AppFonts.Icon;
            AccessibleToolTip.Attach(button, content);
            return button;
        }

        /// <summary>
        /// 图标内容：必须放进一个带图标字体的 TextBlock。直接给 Content 赋字符串时，
        /// 按钮模板渲染内容不保证使用我们设的字体，会显示成空框（箭头被"吞掉"就是这个原因）。
        /// </summary>
        public static TextBlock IconContent(string icon)
        {
            return new TextBlock
            {
                Text = IconGlyph(icon),
                FontFamily = AppFonts.Icon,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }
        /// <summary>
        /// 图标字形直接用传入的 Segoe Fluent Icons / MDL2 码位。
        /// 以前这里有一张"翻译表"，把 MDL2 字形换成 \u25A3 之类的普通几何符号——
        /// 那是早期用文本字体时的权宜之计，结果按钮上显示的都是方块。现在按钮统一
        /// 使用 AppFonts.Icon（已逐一渲染确认字形齐全），翻译表必须去掉。
        /// </summary>
        public static string IconGlyph(string icon)
        {
            return string.IsNullOrEmpty(icon) ? string.Empty : icon;
        }

        /// <summary>
        /// 【2026-10-19 用户要求】页面工具条统一成一种“能换行、间距一致”的排法。
        /// 原来每个页面各写各的 WrapPanel、按钮各自带 Margin（有的 8、有的 0、有的带 bottom 4），
        /// 窗口一窄换行后就高低不齐、间距像乱摆。新代码请一律：
        /// <code>
        /// WrapPanel bar = UiFactory.Toolbar();
        /// UiFactory.AddToolbarItem(bar, button);
        /// </code>
        /// 宽的时候是一行；窄的时候整体换行，每个按钮右边/下边都是 8，看着是整齐的格子。
        /// </summary>
        public static WrapPanel Toolbar()
        {
            return new WrapPanel
            {
                Margin = new Thickness(0, 8, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>把控件放进工具条，并统一“右 8 / 下 8”的间距（见 <see cref="Toolbar"/>）。</summary>
        public static void AddToolbarItem(WrapPanel toolbar, UIElement item)
        {
            if (toolbar == null || item == null)
            {
                return;
            }
            item.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 8));
            toolbar.Children.Add(item);
        }

        /// <summary>
        /// 【2026-10-19 用户要求】三个工作区的**主操作按钮**（保存/恢复、扫描/恢复）必须固定在
        /// “标题 + 小字说明”下面同一位置，大小和间距统一：
        /// 左边主按钮（accent）+ 右边次按钮，间距 8；所有按钮 MinWidth 140，看起来一样大。
        /// </summary>
        public static WrapPanel PrimaryActionRow()
        {
            return new WrapPanel
            {
                Margin = new Thickness(0, 10, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>把主/次操作按钮放进 <see cref="PrimaryActionRow"/>，统一间距与最小宽度。</summary>
        public static void AddPrimaryAction(WrapPanel row, Button button)
        {
            if (row == null || button == null)
            {
                return;
            }
            button.Margin = new Thickness(0, 0, 8, 0);
            button.MinWidth = 140;
            row.Children.Add(button);
        }

        /// <summary>
        /// 页面顶部的小字说明：最多显示两行（窗口变窄时允许被裁掉一部分），
        /// 完整的文字放进悬浮提示里 —— 这样下面的主按钮位置不会被文字越撑越靠下。
        /// </summary>
        public static SelectableText ClampedSupportingText(string text, double maxHeight = 38)
        {
            SelectableText block = SupportingText(text);
            block.TextWrapping = TextWrapping.Wrap;
            block.MaxHeight = maxHeight;
            AccessibleToolTip.AttachDescription(block, text, null);
            return block;
        }

        public static SelectableText ClampedSupportingTextKey(string key, double maxHeight = 38)
        {
            SelectableText block = SupportingTextKey(key);
            block.TextWrapping = TextWrapping.Wrap;
            block.MaxHeight = maxHeight;
            AccessibleToolTip.AttachDescription(block, AppText.T(key), null);
            return block;
        }

        public static Border Separator()
        {
            return new Border { Height = 1, Background = AppTheme.Border };
        }
    }
}