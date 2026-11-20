using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共界面】拖动时跟随鼠标的浮动方框 —— 全程序唯一一份创建与显示逻辑。
    ///
    /// 外观与用户 2026-10-16 定稿的「启动与恢复」完全一致：
    /// 15 号字、Surface 底、主题色 1 像素边、圆角 6、内边距 20×14、阴影 14/2/0.22；
    /// X 固定在来源控件左边缘、只有 Y 跟手（`DragGhostPositioner`）；宽度按 `DragGhostMetrics`。
    ///
    /// 「启动与恢复」「自启动应用」「图标位置保存」都拿 `MainWindow` 里这同一个实例，
    /// 不再各自建 Border/TextBlock 或复制样式。
    /// </summary>
    internal sealed class DragGhostVisual
    {
        private readonly Border border;
        private readonly TextBlock text;
        private double anchorX = double.NaN;
        private double width = DragGhostMetrics.MinWidth;

        private DragGhostVisual(Border border, TextBlock text)
        {
            this.border = border;
            this.text = text;
        }

        /// <summary>按统一样式创建方框（调用方负责加进浮层 Canvas，并定 ZIndex=50）。</summary>
        internal static DragGhostVisual Create()
        {
            TextBlock label = new TextBlock
            {
                FontSize = 15,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = DragGhostMetrics.MaxTextWidth
            };
            Border box = new Border
            {
                Child = label,
                Visibility = Visibility.Collapsed,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Accent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(20, 14, 20, 14),
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.22,
                    Color = Colors.Black
                }
            };
            Panel.SetZIndex(box, 50);
            return new DragGhostVisual(box, label);
        }

        /// <summary>浮层里的那个 Border（宿主需要把它加进 Canvas）。</summary>
        internal Border Element
        {
            get { return border; }
        }

        internal TextBlock TextElement
        {
            get { return text; }
        }

        internal bool IsVisible
        {
            get { return border.Visibility == Visibility.Visible; }
        }

        internal double CurrentWidth
        {
            get { return width; }
        }

        /// <summary>从某个来源控件（行/卡片标题）起拖：取它的实际宽与左边缘。</summary>
        internal void ShowFrom(FrameworkElement source, string label, Canvas layer)
        {
            double sourceWidth = source != null && source.ActualWidth > 0.0 ? source.ActualWidth : 0.0;
            double left = double.NaN;
            if (source != null && layer != null)
            {
                try
                {
                    left = source.TranslatePoint(new Point(0.0, 0.0), layer).X;
                }
                catch
                {
                    left = double.NaN;
                }
            }
            Show(label, sourceWidth, left);
        }

        /// <summary>按已算好的来源宽/左边缘显示（来源控件拿不到时用）。</summary>
        internal void Show(string label, double sourceWidth, double anchorXInLayer)
        {
            text.Text = string.IsNullOrEmpty(label) ? string.Empty : label;
            width = DragGhostMetrics.WidthFor(sourceWidth);
            anchorX = anchorXInLayer;
            border.MaxWidth = double.PositiveInfinity;
            border.Width = width;
            border.Visibility = Visibility.Visible;
        }

        /// <summary>每帧摆位置：X 固定、Y 跟手。</summary>
        internal void Move(Window window, Canvas layer, Point pointerInWindow)
        {
            DragGhostPositioner.Position(window, layer, border, anchorX, width, pointerInWindow);
        }

        internal void Hide()
        {
            border.Visibility = Visibility.Collapsed;
            text.Text = string.Empty;
        }
    }
}
