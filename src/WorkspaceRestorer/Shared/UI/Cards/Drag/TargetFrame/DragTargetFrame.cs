using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共界面】拖动时的“目标卡片着色框 + 气泡” —— 全程序唯一一份。
    ///
    /// 2026-11-01 用户要求：拖动并入到某张方案卡片时，对应卡片要有颜色。
    ///  · 框：2 像素主题色、圆角 10、**半透明主题色底**（只画在浮层上，不覆盖文字）；
    ///  · 气泡：主题色圆角 6、并入图标 `\uE8B7` + 白字，贴在浮动方框右下 6 像素。
    ///
    /// 只画在浮层上，**不改目标卡片自己的 Background/BorderBrush/BorderThickness**，
    /// 所以不会和 hover/选中态打架。方案“并入/移入”、项目行拖到方案卡片都用它。
    /// </summary>
    internal sealed class DragTargetFrame
    {
        /// <summary>目标卡片着色：主题青的 20% 透明底，卡片文字仍然清晰。</summary>
        private static readonly Brush TargetFill = SurfaceTokens.SearchHitBorder;

        private readonly Canvas layer;
        private readonly DragGhostVisual ghost;
        private Border frame;
        private Border bubble;
        private TextBlock bubbleText;

        internal DragTargetFrame(Canvas layer, DragGhostVisual ghost)
        {
            this.layer = layer;
            this.ghost = ghost;
        }

        internal bool IsVisible
        {
            get { return frame != null && frame.Visibility == Visibility.Visible; }
        }

        /// <summary>画框 + 气泡（气泡文字为空时只画框）。</summary>
        internal void Show(FrameworkElement target, string hint)
        {
            if (!ShowFrame(target))
            {
                return;
            }
            if (string.IsNullOrEmpty(hint))
            {
                HideBubble();
                return;
            }
            EnsureBubble();
            if (bubble == null)
            {
                return;
            }
            if (bubbleText != null && !string.Equals(bubbleText.Text, hint, StringComparison.Ordinal))
            {
                bubbleText.Text = hint;
            }
            if (bubbleText != null)
            {
                AccessibleToolTip.AttachDescription(bubbleText, hint, null);
            }
            bubble.Visibility = Visibility.Visible;
            // 先量一次：第一帧 ActualWidth 还是 0，直接定位会把气泡放到浮动方框右边缘，
            // 下一帧再跳回来。Measure 之后 DesiredSize 已经可用。
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            PositionBubble();
        }

        /// <summary>只画框，不显示气泡（项目行拖到方案卡片时提示在状态栏）。</summary>
        internal bool ShowFrame(FrameworkElement target)
        {
            if (target == null || layer == null)
            {
                Hide();
                return false;
            }
            EnsureFrame();
            if (frame == null)
            {
                return false;
            }
            Point origin;
            try
            {
                origin = target.TranslatePoint(new Point(0.0, 0.0), layer);
            }
            catch
            {
                Hide();
                return false;
            }
            Rect rect = DragTargetGeometry.FrameRect(origin, target.ActualWidth, target.ActualHeight);
            Canvas.SetLeft(frame, rect.X);
            Canvas.SetTop(frame, rect.Y);
            frame.Width = rect.Width;
            frame.Height = rect.Height;
            frame.Visibility = Visibility.Visible;
            return true;
        }

        internal void Hide()
        {
            if (frame != null)
            {
                frame.Visibility = Visibility.Collapsed;
            }
            HideBubble();
        }

        /// <summary>浮动方框每帧移动后，气泡跟着重摆（只改 Canvas 坐标，不参与布局）。</summary>
        internal void RepositionBubble()
        {
            if (bubble != null && bubble.Visibility == Visibility.Visible)
            {
                PositionBubble();
            }
        }

        /// <summary>切工作区/窗口关闭时彻底移除浮层元素。</summary>
        internal void Dispose()
        {
            Hide();
            if (frame != null && layer != null)
            {
                try { layer.Children.Remove(frame); } catch { }
            }
            if (bubble != null && layer != null)
            {
                try { layer.Children.Remove(bubble); } catch { }
            }
            frame = null;
            bubble = null;
            bubbleText = null;
        }

        private void EnsureFrame()
        {
            if (frame != null || layer == null)
            {
                return;
            }
            frame = new Border
            {
                BorderBrush = AppTheme.Accent,
                BorderThickness = new Thickness(2.0),
                CornerRadius = new CornerRadius(10.0),
                Background = TargetFill,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            // 层级：插入线/高亮框 10，浮动方框 50，气泡 60。
            // 高亮框必须在浮动方框**下面**：否则框的左右竖边会从浮动方框上穿过去，
            // 看起来像“两张卡片被一个大框套在一起”，用户 2026-11-01 报的“卡片交错、
            // 没有上下层关系”就是这种画法。框被方框挡住中间，只露四周，才是“目标卡片在下面”。
            Panel.SetZIndex(frame, 10);
            layer.Children.Add(frame);
        }

        private void EnsureBubble()
        {
            if (bubble != null || layer == null)
            {
                return;
            }
            StackPanel inner = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock icon = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = AppTheme.IconFont,
                FontSize = 13.0,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 6.0, 0.0)
            };
            bubbleText = new TextBlock
            {
                FontSize = 12.0,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 480.0
            };
            inner.Children.Add(icon);
            inner.Children.Add(bubbleText);
            bubble = new Border
            {
                Background = AppTheme.Accent,
                CornerRadius = new CornerRadius(6.0),
                Padding = new Thickness(9.0, 4.0, 9.0, 4.0),
                IsHitTestVisible = false,
                Child = inner,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(bubble, 60);
            layer.Children.Add(bubble);
        }

        private void HideBubble()
        {
            if (bubble != null)
            {
                bubble.Visibility = Visibility.Collapsed;
            }
        }

        private void PositionBubble()
        {
            if (bubble == null || ghost == null || !ghost.IsVisible)
            {
                if (bubble != null)
                {
                    bubble.Visibility = Visibility.Collapsed;
                }
                return;
            }
            Border ghostElement = ghost.Element;
            double ghostLeft = Canvas.GetLeft(ghostElement);
            double ghostTop = Canvas.GetTop(ghostElement);
            if (double.IsNaN(ghostLeft)) { ghostLeft = 0.0; }
            if (double.IsNaN(ghostTop)) { ghostTop = 0.0; }
            double width = ghostElement.ActualWidth > 1.0 ? ghostElement.ActualWidth : ghostElement.Width;
            double bubbleWidth = bubble.ActualWidth > 1.0
                ? bubble.ActualWidth
                : bubble.DesiredSize.Width;
            Point point = DragTargetGeometry.BubblePosition(
                ghostLeft, ghostTop, width, ghostElement.ActualHeight, bubbleWidth);
            Canvas.SetLeft(bubble, point.X);
            Canvas.SetTop(bubble, point.Y);
        }
    }
}
