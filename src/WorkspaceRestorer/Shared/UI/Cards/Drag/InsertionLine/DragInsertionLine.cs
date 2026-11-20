using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共界面】拖动时的分割辅助线 —— 只画在浮层 Canvas 上，**绝不插进布局**。
    ///
    /// 为什么不再往 StackPanel 里插真元素：真元素会让下面的行整体上下移动约 2 像素；
    /// 指针压在行缝附近时，“插线 → 行移动 → 指针不在行上 → 判不合法 → 拔线 → 行移回”
    /// 会形成逐帧反馈（2026-10-31 在「启动与恢复」修过，现在三个工作区统一）。
    ///
    /// 行线 2px、卡片缝线 3px；缝上/下两张卡片“腾开”5px（140ms），
    /// `RenderTransform` 不参与布局，所以不会反过来改变落点。
    /// </summary>
    internal sealed class DragInsertionLine
    {
        private const double RoomShift = 5.0;
        private const double RoomMilliseconds = 140.0;

        private readonly Canvas layer;
        private Border line;
        private readonly Dictionary<FrameworkElement, TranslateTransform> roomShifts =
            new Dictionary<FrameworkElement, TranslateTransform>();
        private readonly Dictionary<FrameworkElement, Transform> roomOriginalTransforms =
            new Dictionary<FrameworkElement, Transform>();
        private FrameworkElement roomUpper;
        private FrameworkElement roomLower;

        internal DragInsertionLine(Canvas layer)
        {
            this.layer = layer;
        }

        internal bool IsVisible
        {
            get { return line != null && line.Visibility == Visibility.Visible; }
        }

        /// <summary>行线：锚点元素的左边、宽＝锚点宽，画在它的上沿/下沿。</summary>
        internal bool ShowRow(FrameworkElement anchor, bool insertAfter, Rect clip)
        {
            if (anchor == null || layer == null)
            {
                Hide();
                return false;
            }
            Point origin;
            try
            {
                origin = anchor.TranslatePoint(new Point(0.0, 0.0), layer);
            }
            catch
            {
                Hide();
                return false;
            }
            double width = anchor.ActualWidth;
            if (width <= 1.0)
            {
                Hide();
                return false;
            }
            Rect rect = DragInsertionGeometry.RowLine(
                new Rect(origin.X, origin.Y, width, anchor.ActualHeight), insertAfter);
            if (!DragInsertionGeometry.Clip(ref rect, clip))
            {
                Hide();
                return false;
            }
            return Show(rect, DragInsertionGeometry.RowLineHeight, 1.0);
        }

        /// <summary>卡片缝线：以 <paramref name="seamY"/> 为中心，横向按列表可视区裁剪。</summary>
        internal bool ShowSeam(double left, double seamY, double width, Rect clip)
        {
            if (layer == null || width <= 1.0)
            {
                Hide();
                return false;
            }
            Rect rect = new Rect(left, seamY - (DragInsertionGeometry.CardLineHeight / 2.0),
                width, DragInsertionGeometry.CardLineHeight);
            if (!DragInsertionGeometry.Clip(ref rect, clip))
            {
                Hide();
                return false;
            }
            return Show(rect, DragInsertionGeometry.CardLineHeight, 1.5);
        }

        /// <summary>让缝上/下两张卡片各让开 5 像素（只在换缝时重启动画）。</summary>
        internal void SetRoom(FrameworkElement upper, FrameworkElement lower)
        {
            if (object.ReferenceEquals(upper, roomUpper) && object.ReferenceEquals(lower, roomLower))
            {
                return;
            }
            ClearRoom();
            roomUpper = upper;
            roomLower = lower;
            Shift(upper, -RoomShift);
            Shift(lower, RoomShift);
        }

        /// <summary>把卡片恢复原位（松手、取消、切工作区都要调）。</summary>
        internal void ClearRoom()
        {
            roomUpper = null;
            roomLower = null;
            if (roomShifts.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<FrameworkElement, TranslateTransform> pair in roomShifts)
            {
                try
                {
                    pair.Value.BeginAnimation(TranslateTransform.YProperty, null);
                    pair.Value.Y = 0.0;
                    Transform original;
                    if (roomOriginalTransforms.TryGetValue(pair.Key, out original))
                    {
                        if (object.ReferenceEquals(pair.Key.RenderTransform, pair.Value))
                        {
                            pair.Key.RenderTransform = original;
                        }
                    }
                }
                catch
                {
                }
            }
            roomShifts.Clear();
            roomOriginalTransforms.Clear();
        }

        /// <summary>收起线并清掉腾位（拖动收尾统一走这里）。</summary>
        internal void Hide()
        {
            ClearRoom();
            if (line != null && layer != null)
            {
                try
                {
                    layer.Children.Remove(line);
                }
                catch
                {
                }
            }
            line = null;
        }

        private bool Show(Rect rect, double height, double radius)
        {
            if (line == null)
            {
                line = new Border
                {
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(line, 10);
                layer.Children.Add(line);
            }
            line.Height = height;
            line.CornerRadius = new CornerRadius(radius);
            line.Background = AppTheme.Accent;
            line.Width = rect.Width;
            line.Visibility = Visibility.Visible;
            Canvas.SetLeft(line, rect.X);
            Canvas.SetTop(line, rect.Y);
            return true;
        }

        private void Shift(FrameworkElement card, double shift)
        {
            if (card == null)
            {
                return;
            }
            TranslateTransform transform;
            if (!roomShifts.TryGetValue(card, out transform))
            {
                transform = new TranslateTransform(0.0, 0.0);
                roomShifts[card] = transform;
                roomOriginalTransforms[card] = card.RenderTransform;
                card.RenderTransform = transform;
            }
            DoubleAnimation animation = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(RoomMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.YProperty, animation);
        }
    }
}
