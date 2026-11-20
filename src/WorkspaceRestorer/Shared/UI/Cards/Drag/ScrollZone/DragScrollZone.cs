using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共界面】列表上/下拖动滚动框 —— 三个工作区唯一一份。
    ///
    /// 它同时负责三件事，保证“看到的就是能用的”：
    ///  · 上/下放置条亮起（active `#EAF3F6`；顶部 idle 透明、底部 idle 白底）；
    ///  · 指针进入条带就按总设置的速度自动滚动（33ms、整条同速、夹在 0..ScrollableHeight）；
    ///  · 对外给出列表/上条/下条矩形，以及“指针在不在可放区”（6 像素边线容差）。
    ///
    /// 用户 2026-11-01 拍板：取消原来的 8 像素内缩，**亮起＝能放＝能滚**。
    /// 旧的 `DragScrollController` / `DragDropZoneBinder` 没有任何调用，已删除。
    /// </summary>
    internal sealed class DragScrollZone : IDisposable
    {
        /// <summary>上/下放置条的高度（显示与生效共用这一个值）。</summary>
        internal const double ZoneHeight = 64.0;

        private const int ScrollIntervalMilliseconds = 33;
        private const double ExitSlack = 8.0;

        private readonly Window window;
        private readonly ScrollViewer list;
        private readonly Border topPad;
        private readonly Border bottomPad;
        private readonly Func<double> speedFactor;
        private DispatcherTimer timer;
        private int direction;
        private int padState = -2;
        private Rect listRect;
        private Rect topZoneRect;
        private Rect bottomZoneRect;
        private bool ready;

        internal DragScrollZone(
            Window window,
            ScrollViewer list,
            Border topPad,
            Border bottomPad,
            Func<double> speedFactor)
        {
            this.window = window;
            this.list = list;
            this.topPad = topPad;
            this.bottomPad = bottomPad;
            this.speedFactor = speedFactor;
        }

        internal int Direction
        {
            get { return direction; }
        }

        internal bool IsReady
        {
            get { return ready; }
        }

        internal Rect ListRect
        {
            get { return listRect; }
        }

        internal Rect TopZoneRect
        {
            get { return topZoneRect; }
        }

        internal Rect BottomZoneRect
        {
            get { return bottomZoneRect; }
        }

        /// <summary>重新量列表与两块放置条的矩形（起拖时、列表滚动/窗口尺寸变化后）。</summary>
        internal void Refresh()
        {
            ready = false;
            if (window == null || list == null)
            {
                return;
            }
            Point origin;
            try
            {
                origin = list.TranslatePoint(new Point(0.0, 0.0), window);
            }
            catch
            {
                return;
            }
            double width = list.ActualWidth;
            double height = list.ActualHeight;
            listRect = new Rect(origin.X, origin.Y, width, height);
            topZoneRect = ZoneRect(topPad, listRect.Left, Math.Max(0.0, listRect.Top - ZoneHeight),
                listRect.Width, ZoneHeight);
            bottomZoneRect = ZoneRect(bottomPad, listRect.Left, listRect.Bottom,
                listRect.Width, ZoneHeight);
            // 放置条和列表之间可能隔着一个列表上/下边距（「启动与恢复」列表有 12 DIP 上边距）。
            // 那个缝在视觉上不算带子，但指针从带子滑进列表时不应该突然变成“禁止”；
            // 把生效区补到列表边缘，做到“亮起＝能放＝能滚”的连续判定。
            if (topZoneRect.Bottom <= listRect.Top + 1.0)
            {
                topZoneRect = new Rect(topZoneRect.X, topZoneRect.Y, topZoneRect.Width,
                    Math.Max(ZoneHeight, listRect.Top - topZoneRect.Y));
            }
            if (bottomZoneRect.Top >= listRect.Bottom - 1.0)
            {
                bottomZoneRect = new Rect(bottomZoneRect.X, listRect.Bottom, bottomZoneRect.Width,
                    Math.Max(ZoneHeight, bottomZoneRect.Bottom - listRect.Bottom));
            }
            ready = listRect.Width > 1.0 && listRect.Height > 1.0;
        }

        /// <summary>拖动中每帧调一次：按指针位置决定方向、滚动、点亮对应放置条。</summary>
        internal void Update(Point pointerInWindow)
        {
            if (!ready)
            {
                Refresh();
                if (!ready)
                {
                    return;
                }
            }
            else if (!listRect.Contains(pointerInWindow)
                && !topZoneRect.Contains(pointerInWindow)
                && !bottomZoneRect.Contains(pointerInWindow))
            {
                // 指针不在列表/条带附近时才重算矩形，避免停在边界上时来回抖。
                RefreshIfMoved();
            }

            int next = 0;
            // 只认上/下放置条自己的矩形，不再用“列表顶边 ± 64”猜。
            // 旧写法会在工具栏、列表外空白、甚至放置条上方 64 像素处就开始亮/滚，
            // 但真正的落点判定又只认带子，于是出现“亮着却不能放”。
            bool inList = listRect.Contains(pointerInWindow);
            if (!inList)
            {
                Rect topHit = topZoneRect;
                Rect bottomHit = bottomZoneRect;
                if (direction == -1)
                {
                    topHit = new Rect(topHit.X, topHit.Y - ExitSlack,
                        topHit.Width, topHit.Height + ExitSlack);
                }
                else if (direction == 1)
                {
                    bottomHit = new Rect(bottomHit.X, bottomHit.Y,
                        bottomHit.Width, bottomHit.Height + ExitSlack);
                }
                if (topHit.Contains(pointerInWindow))
                {
                    next = -1;
                }
                else if (bottomHit.Contains(pointerInWindow))
                {
                    next = 1;
                }
            }

            direction = next;
            SetPadState(next);
            if (next == 0)
            {
                StopTimer();
            }
            else
            {
                StartTimer();
            }
        }

        /// <summary>指针是否在“列表 + 上下放置条”的容差范围内。</summary>
        internal bool Contains(Point pointerInWindow)
        {
            if (!ready)
            {
                return false;
            }
            return DragDropArea.Contains(listRect, topZoneRect, bottomZoneRect, pointerInWindow);
        }

        /// <summary>指针是否在上下放置条的容差范围内（不含列表本身）。</summary>
        internal bool IsInZone(Point pointerInWindow)
        {
            if (!ready)
            {
                return false;
            }
            return DragDropArea.IsInZone(topZoneRect, bottomZoneRect, pointerInWindow);
        }

        /// <summary>指针是否在列表本身（含 6 像素边线容差）。</summary>
        internal bool IsInList(Point pointerInWindow)
        {
            if (!ready)
            {
                return false;
            }
            return DragDropArea.Inflate(listRect, DragDropArea.Tolerance).Contains(pointerInWindow);
        }

        /// <summary>拖动结束：停止滚动、熄灭两块放置条、清空方向。</summary>
        internal void Reset()
        {
            StopTimer();
            direction = 0;
            SetPadState(0);
        }

        public void Dispose()
        {
            Reset();
            if (timer != null)
            {
                timer.Tick -= OnTick;
                timer = null;
            }
        }

        private void RefreshIfMoved()
        {
            if (window == null || list == null)
            {
                return;
            }
            Point origin;
            try
            {
                origin = list.TranslatePoint(new Point(0.0, 0.0), window);
            }
            catch
            {
                return;
            }
            if (Math.Abs(origin.X - listRect.Left) > 0.5
                || Math.Abs(origin.Y - listRect.Top) > 0.5
                || Math.Abs(list.ActualWidth - listRect.Width) > 0.5
                || Math.Abs(list.ActualHeight - listRect.Height) > 0.5)
            {
                Refresh();
            }
        }

        private static Rect ZoneRect(Border pad, double fallbackLeft, double fallbackTop,
            double fallbackWidth, double fallbackHeight)
        {
            if (pad != null && pad.ActualWidth > 1.0 && pad.ActualHeight > 1.0)
            {
                try
                {
                    Point origin = pad.TranslatePoint(new Point(0.0, 0.0), Window.GetWindow(pad));
                    return new Rect(origin.X, origin.Y, pad.ActualWidth, pad.ActualHeight);
                }
                catch
                {
                }
            }
            return new Rect(fallbackLeft, fallbackTop, fallbackWidth, fallbackHeight);
        }

        private void StartTimer()
        {
            if (timer == null)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ScrollIntervalMilliseconds) };
                timer.Tick += OnTick;
            }
            if (!timer.IsEnabled)
            {
                timer.Start();
            }
        }

        private void StopTimer()
        {
            if (timer != null && timer.IsEnabled)
            {
                timer.Stop();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (window == null || list == null || direction == 0)
            {
                StopTimer();
                return;
            }
            // 每一下都重新读指针并重算：指针离开条带/窗口时立刻停下，不会“鼠标走了还在滚”。
            Update(Mouse.GetPosition(window));
            if (direction == 0)
            {
                StopTimer();
                return;
            }
            double speed = 1.0;
            try
            {
                if (speedFactor != null)
                {
                    speed = Math.Max(0.5, Math.Min(3.0, speedFactor()));
                }
            }
            catch
            {
            }
            double step = DragScrollSpeed.PixelsPerSecond(speed) * (ScrollIntervalMilliseconds / 1000.0);
            double next = list.VerticalOffset + direction * step;
            next = Math.Max(0.0, Math.Min(list.ScrollableHeight, next));
            list.ScrollToVerticalOffset(next);
        }

        private void SetPadState(int activeDirection)
        {
            if (padState == activeDirection)
            {
                return;
            }
            padState = activeDirection;
            SetPadActive(bottomPad, activeDirection == 1, false);
            SetPadActive(topPad, activeDirection == -1, true);
        }

        private static void SetPadActive(Border pad, bool active, bool transparentWhenIdle)
        {
            if (pad == null)
            {
                return;
            }
            pad.Background = active
                ? AppTheme.SelectionSurface
                : (transparentWhenIdle ? Brushes.Transparent : AppTheme.Surface);
            TextBlock hint = pad.Child as TextBlock;
            if (hint != null)
            {
                hint.Opacity = active ? 1.0 : 0.0;
            }
        }
    }
}
