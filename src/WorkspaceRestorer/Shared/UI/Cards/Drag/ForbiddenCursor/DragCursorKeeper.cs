using System;
using System.Windows.Input;
using System.Windows.Threading;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共】拖动期间持续持有鼠标光标。
    ///
    /// 为什么需要它：光标只在“指针移动那一帧”设一次的话，指针一停、或 WPF 在指针离开
    /// 被捕获元素的子树后，会把 `Mouse.OverrideCursor` 刷回箭头；用户看到的就是
    /// “禁止符号时有时无”。这里用 80ms 定时器反复设，拖动结束立刻停。
    ///
    /// 「启动与恢复」和公共 `DragMoveSession` 原来各有一份一模一样的定时器，现在共用这一份。
    /// </summary>
    internal sealed class DragCursorKeeper : IDisposable
    {
        private const int IntervalMilliseconds = 80;

        private readonly Func<bool> isDragging;
        private readonly Func<bool> isForbidden;

        private DispatcherTimer timer;

        internal DragCursorKeeper(Func<bool> isDragging, Func<bool> isForbidden)
        {
            if (isDragging == null)
            {
                throw new ArgumentNullException("isDragging");
            }
            if (isForbidden == null)
            {
                throw new ArgumentNullException("isForbidden");
            }
            this.isDragging = isDragging;
            this.isForbidden = isForbidden;
        }

        internal void Start()
        {
            if (timer == null)
            {
                timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(IntervalMilliseconds)
                };
                timer.Tick += OnTick;
            }
            if (!timer.IsEnabled)
            {
                timer.Start();
            }
        }

        internal void Stop()
        {
            if (timer != null && timer.IsEnabled)
            {
                timer.Stop();
            }
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!isDragging())
            {
                Stop();
                return;
            }
            try
            {
                Mouse.OverrideCursor = isForbidden() ? Cursors.No : Cursors.SizeAll;
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
            if (timer != null)
            {
                timer.Tick -= OnTick;
                timer = null;
            }
        }
    }
}
