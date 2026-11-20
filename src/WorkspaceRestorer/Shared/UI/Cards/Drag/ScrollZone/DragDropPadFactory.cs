using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共件】列表上/下拖动滚动框的外观与初始布局。
    ///
    /// 三个工作区共用同一尺寸/同一外观：高 64、白底圆角 8、提示 11px、平时 Opacity 0。
    /// `CreateTop` 直接带好“透明浮层 + 负上边距 + 不参与命中”，页面只负责放进 Grid；
    /// 不再让每个页面各抄 8–10 行 setup（以前漏过透明底，顶部白板会盖住工具栏）。
    /// </summary>
    internal static class DragDropPadFactory
    {
        internal const double Height = DragScrollZone.ZoneHeight;

        /// <summary>通用创建（底部那条用；需要顶部浮层请用 <see cref="CreateTop"/>）。</summary>
        internal static Border Create(string hintText)
        {
            Border pad = new Border
            {
                Height = Height,
                Background = AppTheme.Surface,
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(6, 4, 6, 0),
                Child = BuildHint(hintText)
            };
            return pad;
        }

        /// <summary>列表上方那条：负上边距浮在列表上沿、平时完全透明、不参与鼠标命中。</summary>
        internal static Border CreateTop(string hintText)
        {
            Border pad = Create(hintText);
            pad.Background = Brushes.Transparent;
            pad.Margin = new Thickness(6, -Height, 6, 0);
            pad.VerticalAlignment = VerticalAlignment.Top;
            pad.IsHitTestVisible = false;
            return pad;
        }

        /// <summary>列表下方那条：真实占一行的白底放置区。</summary>
        internal static Border CreateBottom(string hintText)
        {
            Border pad = Create(hintText);
            pad.VerticalAlignment = VerticalAlignment.Stretch;
            pad.Background = AppTheme.Surface;
            return pad;
        }

        private static TextBlock BuildHint(string hintText)
        {
            return new TextBlock
            {
                Text = hintText ?? string.Empty,
                FontFamily = AppTheme.UiFont,
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.0
            };
        }
    }
}
