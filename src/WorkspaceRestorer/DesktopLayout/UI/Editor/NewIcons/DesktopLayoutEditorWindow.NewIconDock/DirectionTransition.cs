using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using Microsoft.Win32;

namespace WorkspaceRestorer.UI
{
    internal sealed partial class DesktopLayoutEditorWindow
    {
        private static void GetNewIconDirectionTowardTagShift(
            NewIconDockDirection direction, out double x, out double y)
        {
            const double Shift = 8.0;
            x = 0.0;
            y = 0.0;
            switch (direction)
            {
                case NewIconDockDirection.Left:
                    x = Shift;
                    break;
                case NewIconDockDirection.Right:
                    x = -Shift;
                    break;
                case NewIconDockDirection.Up:
                    y = Shift;
                    break;
                case NewIconDockDirection.Down:
                    y = -Shift;
                    break;
            }
        }

        private bool TryBeginNewIconDirectionTransition(NewIconDockDirection previousDirection)
        {
            if (newIconDock == null || newIconDockContent == null)
            {
                return false;
            }
            if (!SystemParameters.ClientAreaAnimation)
            {
                return false;
            }
            // 如果上一段过场还在播，不能直接 Cancel：那会把活面板瞬间恢复，
            // 等于把半截画面硬切回完整面板。先把“当前实际看到的旧帧+新帧”
            // 合成为一张接续帧，再从这张接续帧继续往标签收。
            bool canRetarget = newIconDirectionTransitionActive
                && !newIconDirectionTransitionPending
                && (newIconDirectionGhost != null || newIconDirectionNewGhost != null);
            if (canRetarget)
            {
                if (!TryCaptureNewIconDirectionCompositeFrame())
                {
                    CancelNewIconDirectionTransition();
                    return false;
                }
            }
            else
            {
                // pending 表示上一段还没有真正开始，活面板仍是完整可见的；
                // 这时取消不会产生半截画面，可以安全地按活面板重新定格。
                if (newIconDirectionTransitionActive
                    || newIconDirectionTransitionPending
                    || newIconDirectionGhost != null
                    || newIconDirectionNewGhost != null)
                {
                    CancelNewIconDirectionTransition();
                }
                if (!TryCaptureNewIconContentAsOldFrame())
                {
                    return false;
                }
            }

            newIconDirectionTransitionFrom = previousDirection;
            newIconDirectionTransitionVersion++;
            newIconDirectionTransitionActive = true;
            newIconDirectionTransitionPending = true;
            newIconDirectionTransitionRetarget = canRetarget;
            // 快速移动仍然播 B，只把收回/展开压短，让它能跟上标签。
            newIconDirectionTransitionCompact = newIconDragging
                && newIconDragSpeed > FastNewIconDragSpeed;
            if (newIconDirectionTransitionTimer != null)
            {
                newIconDirectionTransitionTimer.Stop();
            }
            return true;
        }

        private bool TryCaptureNewIconContentAsOldFrame()
        {
            if (newIconDockContent.Visibility != Visibility.Visible
                || newIconDockContent.ActualWidth <= 1.0
                || newIconDockContent.ActualHeight <= 1.0
                || newIconDockContent.Opacity < 0.99)
            {
                return false;
            }
            double left = Canvas.GetLeft(newIconDockContent);
            double top = Canvas.GetTop(newIconDockContent);
            if (!IsFinite(left))
            {
                left = 0.0;
            }
            if (!IsFinite(top))
            {
                top = 0.0;
            }
            double width = newIconDockContent.ActualWidth;
            double height = newIconDockContent.ActualHeight;
            try
            {
                RenderTargetBitmap bitmap = RenderNewIconPanelBitmap(width, height);

                Image ghost = new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Opacity = 1.0
                };
                RenderOptions.SetBitmapScalingMode(ghost, BitmapScalingMode.HighQuality);
                newIconDirectionGhostShift = new TranslateTransform(0.0, 0.0);
                ghost.RenderTransform = newIconDirectionGhostShift;
                newIconDirectionGhostIsComposite = false;

                // 快照放在新面板上面、标签下面；它只负责淡出，不参与命中测试。
                Panel.SetZIndex(ghost, 5);
                newIconDirectionGhost = ghost;
                newIconDock.Children.Add(ghost);
                Canvas.SetLeft(ghost, left);
                Canvas.SetTop(ghost, top);
                // 相对逻辑标签坐标保存偏移。不能用 newIconTagHost 的 Canvas 位置：
                // 拖动时标签坐标已经更新，tagHost 可能还没排到新位置；用旧位置算偏移，
                // 快照就会在拖动/接续时漂到标签下方，看起来像面板消失或只剩一条。
                newIconDirectionGhostOffsetX = left - newIconTagX;
                newIconDirectionGhostOffsetY = top - newIconTagY;
                if (!IsFinite(newIconDirectionGhostOffsetX))
                {
                    newIconDirectionGhostOffsetX = 0.0;
                }
                if (!IsFinite(newIconDirectionGhostOffsetY))
                {
                    newIconDirectionGhostOffsetY = 0.0;
                }
                return true;
            }
            catch
            {
                CancelNewIconDirectionTransition();
                return false;
            }
        }

        private sealed class NewIconTransitionFrame
        {
            public Image Image;
            public BitmapSource Source;
            public double Left;
            public double Top;
            public double Width;
            public double Height;
            public Rect ClipRect;
            public double Opacity;
        }

        private static bool TryReadNewIconTransitionFrame(
            Image image, out NewIconTransitionFrame frame)
        {
            frame = null;
            if (image == null)
            {
                return false;
            }
            BitmapSource source = image.Source as BitmapSource;
            if (source == null)
            {
                return false;
            }
            double width = image.Width;
            double height = image.Height;
            if (!IsFinite(width) || width <= 0.5)
            {
                width = image.ActualWidth;
            }
            if (!IsFinite(height) || height <= 0.5)
            {
                height = image.ActualHeight;
            }
            if (!IsFinite(width) || !IsFinite(height)
                || width <= 0.5 || height <= 0.5)
            {
                return false;
            }
            double left = Canvas.GetLeft(image);
            double top = Canvas.GetTop(image);
            if (!IsFinite(left))
            {
                left = 0.0;
            }
            if (!IsFinite(top))
            {
                top = 0.0;
            }
            Rect clip = new Rect(0.0, 0.0, width, height);
            RectangleGeometry clipGeometry = image.Clip as RectangleGeometry;
            if (clipGeometry != null)
            {
                clip.Intersect(clipGeometry.Rect);
            }
            if (clip.IsEmpty || clip.Width <= 0.01 || clip.Height <= 0.01)
            {
                return false;
            }
            double shiftX = 0.0;
            double shiftY = 0.0;
            TranslateTransform shift = image.RenderTransform as TranslateTransform;
            if (shift != null)
            {
                shiftX = shift.X;
                shiftY = shift.Y;
            }
            double opacity = image.Opacity;
            if (!IsFinite(opacity) || opacity <= 0.001)
            {
                return false;
            }
            frame = new NewIconTransitionFrame
            {
                Image = image,
                Source = source,
                Left = left + shiftX,
                Top = top + shiftY,
                Width = width,
                Height = height,
                ClipRect = clip,
                Opacity = opacity
            };
            return true;
        }

        private bool TryCaptureNewIconDirectionCompositeFrame()
        {
            if (newIconDock == null)
            {
                return false;
            }
            // 合成顺序必须和屏幕层级一致：新帧 z=4，旧帧 z=5。
            List<NewIconTransitionFrame> frames = new List<NewIconTransitionFrame>();
            NewIconTransitionFrame frame;
            if (TryReadNewIconTransitionFrame(newIconDirectionNewGhost, out frame))
            {
                frames.Add(frame);
            }
            if (TryReadNewIconTransitionFrame(newIconDirectionGhost, out frame))
            {
                frames.Add(frame);
            }
            if (frames.Count == 0)
            {
                return false;
            }
            Rect union = new Rect(
                frames[0].Left + frames[0].ClipRect.X,
                frames[0].Top + frames[0].ClipRect.Y,
                frames[0].ClipRect.Width,
                frames[0].ClipRect.Height);
            for (int index = 0; index < frames.Count; index++)
            {
                NewIconTransitionFrame item = frames[index];
                Rect visible = new Rect(
                    item.Left + item.ClipRect.X,
                    item.Top + item.ClipRect.Y,
                    item.ClipRect.Width,
                    item.ClipRect.Height);
                union.Union(visible);
            }
            if (union.IsEmpty || union.Width <= 0.5 || union.Height <= 0.5)
            {
                return false;
            }
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX > 0.1 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0.1 ? dpi.DpiScaleY : 1.0;
                RenderTargetBitmap bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(union.Width * scaleX)),
                    Math.Max(1, (int)Math.Ceiling(union.Height * scaleY)),
                    96.0 * scaleX,
                    96.0 * scaleY,
                    PixelFormats.Pbgra32);
                DrawingVisual compositeVisual = new DrawingVisual();
                using (DrawingContext drawing = compositeVisual.RenderOpen())
                {
                    for (int index = 0; index < frames.Count; index++)
                    {
                        NewIconTransitionFrame item = frames[index];
                        double offsetX = item.Left - union.Left;
                        double offsetY = item.Top - union.Top;
                        drawing.PushOpacity(item.Opacity);
                        drawing.PushClip(new RectangleGeometry(new Rect(
                            offsetX + item.ClipRect.X,
                            offsetY + item.ClipRect.Y,
                            item.ClipRect.Width,
                            item.ClipRect.Height)));
                        drawing.DrawImage(item.Source,
                            new Rect(offsetX, offsetY, item.Width, item.Height));
                        drawing.Pop();
                        drawing.Pop();
                    }
                }
                bitmap.Render(compositeVisual);
                bitmap.Freeze();

                Image composite = new Image
                {
                    Source = bitmap,
                    Width = union.Width,
                    Height = union.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Opacity = 1.0
                };
                RenderOptions.SetBitmapScalingMode(composite, BitmapScalingMode.HighQuality);

                RemoveNewIconDirectionGhost();
                RemoveNewIconDirectionNewGhost();
                Panel.SetZIndex(composite, 5);
                newIconDirectionGhost = composite;
                newIconDirectionGhostIsComposite = true;
                newIconDirectionGhostShift = null;
                newIconDirectionGhostClip = null;
                Canvas.SetLeft(composite, union.Left);
                Canvas.SetTop(composite, union.Top);
                newIconDirectionGhostOffsetX = union.Left - newIconTagX;
                newIconDirectionGhostOffsetY = union.Top - newIconTagY;
                newIconDock.Children.Add(composite);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Rect GetNewIconDirectionRetractedRect(
            NewIconDockDirection direction, double width, double height)
        {
            // 保留 1 DIP 的薄边，而不是精确 0：WPF 对高度为 0 的 Clip
            // 在部分上下翻转场景里不会继续重绘，整块会一直等到动画结束才出现。
            double thinWidth = Math.Min(1.0, Math.Max(0.0, width));
            double thinHeight = Math.Min(1.0, Math.Max(0.0, height));
            switch (direction)
            {
                case NewIconDockDirection.Left:
                    return new Rect(
                        Math.Max(0.0, width - thinWidth), 0.0, thinWidth, height);
                case NewIconDockDirection.Right:
                    return new Rect(0.0, 0.0, thinWidth, height);
                case NewIconDockDirection.Up:
                    return new Rect(
                        0.0, Math.Max(0.0, height - thinHeight), width, thinHeight);
                default:
                    return new Rect(0.0, 0.0, width, thinHeight);
            }
        }

        private void StartNewIconDirectionTransition()
        {
            if (!newIconDirectionTransitionActive || !newIconDirectionTransitionPending
                || newIconDockContent == null)
            {
                return;
            }
            newIconDirectionTransitionPending = false;
            int version = newIconDirectionTransitionVersion;
            bool compact = newIconDirectionTransitionCompact;
            bool retarget = newIconDirectionTransitionRetarget;
            TimeSpan oldDuration = TimeSpan.FromMilliseconds(compact ? 70 : 110);
            // 接续帧是在旧过场没播完时接上的，旧画面可能已经快收没了；
            // 这时新画面必须立刻展开，不能等 30/90ms，否则会露出一帧空白。
            TimeSpan newBegin = retarget
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(compact ? 30 : 90);
            TimeSpan newDuration = TimeSpan.FromMilliseconds(compact ? 100 : 170);
            double oldX;
            double oldY;
            double newX;
            double newY;
            GetNewIconDirectionTowardTagShift(newIconDirectionTransitionFrom, out oldX, out oldY);
            GetNewIconDirectionTowardTagShift(newIconDockDirection, out newX, out newY);
            UpdateNewIconDirectionGhostPosition();

            // 单块旧面板仍然像抽屉一样往标签里收：裁切边从远离标签的一侧
            // 退到贴标签的一侧，同时轻轻往标签方向移动并淡出。
            // 接续帧可能同时包含左右/上下两块面板，单边裁切会出现硬切口，
            // 所以改成四边一起收向标签点，收到 1 DIP 再淡出。
            if (newIconDirectionGhost != null)
            {
                double ghostWidth = newIconDirectionGhost.Width;
                double ghostHeight = newIconDirectionGhost.Height;
                if (!IsFinite(ghostWidth) || ghostWidth <= 0.0)
                {
                    ghostWidth = newIconDockContent.ActualWidth;
                }
                if (!IsFinite(ghostHeight) || ghostHeight <= 0.0)
                {
                    ghostHeight = newIconDockContent.ActualHeight;
                }
                RectangleGeometry ghostClip = new RectangleGeometry(
                    new Rect(0.0, 0.0, ghostWidth, ghostHeight));
                newIconDirectionGhostClip = ghostClip;
                newIconDirectionGhost.Clip = ghostClip;
                Rect retractedRect;
                if (newIconDirectionGhostIsComposite)
                {
                    // 标签点相对接续帧的本地坐标。接续帧跟着标签走，
                    // 所以这个点在整段动画里保持不变。
                    double ghostLeft = Canvas.GetLeft(newIconDirectionGhost);
                    double ghostTop = Canvas.GetTop(newIconDirectionGhost);
                    if (!IsFinite(ghostLeft))
                    {
                        ghostLeft = newIconTagX + newIconDirectionGhostOffsetX;
                    }
                    if (!IsFinite(ghostTop))
                    {
                        ghostTop = newIconTagY + newIconDirectionGhostOffsetY;
                    }
                    double targetX = Clamp(newIconTagX - ghostLeft, 0.0,
                        Math.Max(0.0, ghostWidth - 1.0));
                    double targetY = Clamp(newIconTagY - ghostTop, 0.0,
                        Math.Max(0.0, ghostHeight - 1.0));
                    retractedRect = new Rect(targetX, targetY, 1.0, 1.0);
                }
                else
                {
                    retractedRect = GetNewIconDirectionRetractedRect(
                        newIconDirectionTransitionFrom, ghostWidth, ghostHeight);
                }
                RectAnimation retract = new RectAnimation(
                    retractedRect, oldDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                retract.Completed += delegate
                {
                    if (version == newIconDirectionTransitionVersion)
                    {
                        RemoveNewIconDirectionGhost();
                    }
                };
                ghostClip.BeginAnimation(RectangleGeometry.RectProperty, retract);
                if (!newIconDirectionGhostIsComposite
                    && newIconDirectionGhostShift != null)
                {
                    newIconDirectionGhostShift.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(oldX, oldDuration)
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                        });
                    newIconDirectionGhostShift.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(oldY, oldDuration)
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                        });
                }
                newIconDirectionGhost.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0.15, oldDuration)
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                    });
            }

            // 新画面也先定格成位图再展开：活面板里的 ScrollViewer 在竖向
            // 裁切/移动时会留下旧位置的内容，所以过场只动快照，结束时再换回活面板。
            double panelWidth = newIconDockContent.Width;
            double panelHeight = newIconDockContent.Height;
            if (!IsFinite(panelWidth) || panelWidth <= 1.0)
            {
                panelWidth = newIconDockContent.ActualWidth;
            }
            if (!IsFinite(panelHeight) || panelHeight <= 1.0)
            {
                panelHeight = newIconDockContent.ActualHeight;
            }
            if (panelWidth <= 1.0 || panelHeight <= 1.0)
            {
                CancelNewIconDirectionTransition();
                return;
            }
            newIconDockContent.Clip = null;
            newIconDockContent.RenderTransform = null;
            newIconDockContent.CacheMode = null;
            newIconDockContent.Opacity = 1.0;
            newIconDockContent.Measure(new Size(panelWidth, panelHeight));
            double panelLeft = Canvas.GetLeft(newIconDockContent);
            double panelTop = Canvas.GetTop(newIconDockContent);
            if (!IsFinite(panelLeft))
            {
                panelLeft = 0.0;
            }
            if (!IsFinite(panelTop))
            {
                panelTop = 0.0;
            }
            newIconDockContent.Arrange(
                new Rect(panelLeft, panelTop, panelWidth, panelHeight));
            newIconDockContent.UpdateLayout();
            newIconDock.UpdateLayout();
            try
            {
                RenderTargetBitmap bitmap = RenderNewIconPanelBitmap(
                    panelWidth, panelHeight);
                Image newGhost = new Image
                {
                    Source = bitmap,
                    Width = panelWidth,
                    Height = panelHeight,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Opacity = 1.0
                };
                RenderOptions.SetBitmapScalingMode(
                    newGhost, BitmapScalingMode.HighQuality);
                newIconDirectionContentShift = new TranslateTransform(newX, newY);
                newGhost.RenderTransform = newIconDirectionContentShift;
                Panel.SetZIndex(newGhost, 4);
                newIconDirectionNewGhost = newGhost;
                newIconDock.Children.Add(newGhost);
                Canvas.SetLeft(newGhost, panelLeft);
                Canvas.SetTop(newGhost, panelTop);
                newIconDirectionNewGhostOffsetX = panelLeft - newIconTagX;
                newIconDirectionNewGhostOffsetY = panelTop - newIconTagY;

                RectangleGeometry contentClip = new RectangleGeometry(
                    GetNewIconDirectionRetractedRect(
                        newIconDockDirection, panelWidth, panelHeight));
                newIconDirectionContentClip = contentClip;
                newGhost.Clip = contentClip;
                newIconDockContent.Opacity = 0.0;
                newIconDockContent.IsHitTestVisible = false;

                RectAnimation open = new RectAnimation(
                    new Rect(0.0, 0.0, panelWidth, panelHeight), newDuration)
                {
                    BeginTime = newBegin,
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                open.Completed += delegate
                {
                    FinishNewIconDirectionTransition(version);
                };
                contentClip.BeginAnimation(RectangleGeometry.RectProperty, open);
                newIconDirectionContentShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0.0, newDuration)
                    {
                        BeginTime = newBegin,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                newIconDirectionContentShift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0.0, newDuration)
                    {
                        BeginTime = newBegin,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
            }
            catch
            {
                CancelNewIconDirectionTransition();
            }
            if (newIconDirectionTransitionActive)
            {
                StartNewIconDirectionTransitionTimer();
            }
        }

        private void StartNewIconDirectionTransitionTimer()
        {
            if (newIconDirectionTransitionTimer == null)
            {
                newIconDirectionTransitionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(340)
                };
                newIconDirectionTransitionTimer.Tick += delegate
                {
                    newIconDirectionTransitionTimer.Stop();
                    if (newIconDirectionTransitionActive)
                    {
                        FinishNewIconDirectionTransition(
                            newIconDirectionTransitionVersion);
                    }
                };
            }
            newIconDirectionTransitionTimer.Stop();
            newIconDirectionTransitionTimer.Start();
        }

        private void ReEvaluateNewIconDockAfterViewportChange()
        {
            if (state.Closed || !newIconDockPositionInitialized
                || newIconTag == null || newIconDockContent == null)
            {
                return;
            }
            // 缩放/滚动会让预桌面中心（隐藏十字轴）换位置；方向必须按新轴重算，
            // 否则会出现“轴已经变了，面板还朝旧方向”，在页边只剩一条。
            ApplyNewIconDockLayout();
        }

        private void SettleNewIconDirectionTransitionForViewportChange()
        {
            // 缩放/适应窗口/窗口尺寸变化时，动画里的快照和裁切可能停在半路。
            // 这里做一次硬恢复：结束过场、清掉所有快照 Image、清裁切/变换，
            // 再强制重排面板，避免留下窄条或半块面板。
            CancelNewIconDirectionTransition();
            if (newIconDock != null)
            {
                for (int index = newIconDock.Children.Count - 1; index >= 0; index--)
                {
                    Image image = newIconDock.Children[index] as Image;
                    if (image == null)
                    {
                        continue;
                    }
                    image.BeginAnimation(UIElement.OpacityProperty, null);
                    newIconDock.Children.RemoveAt(index);
                    image.Source = null;
                }
            }
            newIconDirectionGhost = null;
            newIconDirectionNewGhost = null;
            newIconDirectionGhostShift = null;
            newIconDirectionGhostClip = null;
            newIconDirectionContentShift = null;
            newIconDirectionContentClip = null;
            newIconDirectionGhostIsComposite = false;
            newIconDirectionTransitionCompact = false;
            newIconDirectionTransitionRetarget = false;
            if (newIconDockContent == null
                || newIconDockContent.Visibility != Visibility.Visible)
            {
                return;
            }
            newIconDockContent.Clip = null;
            newIconDockContent.RenderTransform = null;
            newIconDockContent.CacheMode = null;
            newIconDockContent.IsHitTestVisible = true;
            newIconDockContent.BeginAnimation(UIElement.OpacityProperty, null);
            newIconDockContent.Opacity = 1.0;

            // 强制重跑一次横/竖内容布局，保证 Width/Height、卡片方向、
            // 检索框宽度都回到 108/430 的正常值，而不是停留在过场中间。
            bool vertical = newIconDockDirection == NewIconDockDirection.Up
                || newIconDockDirection == NewIconDockDirection.Down;
            verticalNewIconContent = !vertical;
            ApplyNewIconContentLayout();
            ApplyNewIconDockLayout(false);
        }

        private void RemoveNewIconDirectionGhost()
        {
            newIconDirectionGhostIsComposite = false;
            if (newIconDirectionGhost == null)
            {
                newIconDirectionGhostShift = null;
                newIconDirectionGhostClip = null;
                return;
            }
            newIconDirectionGhost.BeginAnimation(UIElement.OpacityProperty, null);
            if (newIconDirectionGhostClip != null)
            {
                newIconDirectionGhostClip.BeginAnimation(
                    RectangleGeometry.RectProperty, null);
            }
            newIconDirectionGhost.Clip = null;
            newIconDirectionGhostClip = null;
            newIconDirectionGhostShift = null;
            if (newIconDock != null)
            {
                newIconDock.Children.Remove(newIconDirectionGhost);
            }
            newIconDirectionGhost.Source = null;
            newIconDirectionGhost = null;
        }

        private void FinishNewIconDirectionTransition(int version)
        {
            if (version != newIconDirectionTransitionVersion)
            {
                return;
            }
            CancelNewIconDirectionTransition();
        }

        private void RemoveNewIconDirectionNewGhost()
        {
            if (newIconDirectionNewGhost == null)
            {
                newIconDirectionContentShift = null;
                newIconDirectionContentClip = null;
                return;
            }
            newIconDirectionNewGhost.BeginAnimation(UIElement.OpacityProperty, null);
            if (newIconDirectionContentClip != null)
            {
                newIconDirectionContentClip.BeginAnimation(
                    RectangleGeometry.RectProperty, null);
            }
            newIconDirectionNewGhost.Clip = null;
            if (newIconDock != null)
            {
                newIconDock.Children.Remove(newIconDirectionNewGhost);
            }
            newIconDirectionNewGhost.Source = null;
            newIconDirectionNewGhost = null;
            newIconDirectionContentShift = null;
            newIconDirectionContentClip = null;
        }

        private void CancelNewIconDirectionTransition()
        {
            if (!newIconDirectionTransitionActive && !newIconDirectionTransitionPending
                && newIconDirectionGhost == null && newIconDirectionNewGhost == null
                && newIconDirectionContentShift == null
                && newIconDirectionContentClip == null
                && !newIconDirectionGhostIsComposite)
            {
                return;
            }
            newIconDirectionTransitionVersion++;
            newIconDirectionTransitionActive = false;
            newIconDirectionTransitionPending = false;
            newIconDirectionTransitionCompact = false;
            newIconDirectionTransitionRetarget = false;
            if (newIconDirectionTransitionTimer != null)
            {
                newIconDirectionTransitionTimer.Stop();
            }
            RemoveNewIconDirectionGhost();
            RemoveNewIconDirectionNewGhost();
            newIconDirectionGhostIsComposite = false;
            if (newIconDockContent != null)
            {
                newIconDockContent.Clip = null;
                newIconDockContent.RenderTransform = null;
                newIconDockContent.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDockContent.CacheMode = null;
                newIconDockContent.IsHitTestVisible = true;
                newIconDockContent.Opacity = 1.0;
            }
        }

    }
}
