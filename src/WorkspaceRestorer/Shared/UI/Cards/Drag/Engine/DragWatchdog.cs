using System;
using System.Windows;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共】拖动被打断时自动“取消移动”。
    ///
    /// 这套信号与拍数原来散在「启动与恢复」和公共引擎两处；2026-11-01 起只保留这一份，
    /// 启动与恢复也由 `DragMoveSession` 接入，不再有 `StartupRestore/UI/DragWatchdog.cs`。
    /// 现在只留这一份：
    ///   · 每 100ms 看一次物理鼠标键（左键或右键，由本次拖动开始时决定）；
    ///   · 连续 2 拍读到“已松开” → 松手事件丢了 → 取消；
    ///   · 窗口连续 5 拍不是前台 → 截屏工具等接管了输入 → 取消；
    ///   · 正常松手会先把 `isDragging` 变成 false，下一拍这里自己停，不会抢在 MouseUp 前面。
    ///
    /// 调用方只负责：`Start()`、`Stop()`，以及 `cancel(reason)` 里做自己的收尾。
    /// </summary>
    internal sealed class DragWatchdog : IDisposable
    {
        /// <summary>轮询间隔（毫秒）。</summary>
        private const int IntervalMilliseconds = 100;

        /// <summary>物理左键连续多少拍都读到“已松开”才取消（2 拍 ≈ 200ms）。</summary>
        private const int ReleaseTicks = 2;

        /// <summary>窗口连续多少拍都不是前台才取消（5 拍 ≈ 500ms，留给截屏工具先拍下画面）。</summary>
        private const int InactiveTicks = 5;

        private readonly Window window;
        private readonly Func<bool> isDragging;
        private readonly Func<bool> isMouseButtonDown;
        private readonly Action<string> cancel;

        private DispatcherTimer timer;
        private int releaseCount;
        private int inactiveCount;

        internal DragWatchdog(
            Window window,
            Func<bool> isDragging,
            Func<bool> isMouseButtonDown,
            Action<string> cancel)
        {
            if (isDragging == null)
            {
                throw new ArgumentNullException("isDragging");
            }
            if (isMouseButtonDown == null)
            {
                throw new ArgumentNullException("isMouseButtonDown");
            }
            if (cancel == null)
            {
                throw new ArgumentNullException("cancel");
            }
            this.window = window;
            this.isDragging = isDragging;
            this.isMouseButtonDown = isMouseButtonDown;
            this.cancel = cancel;
        }

        internal void Start()
        {
            if (timer == null)
            {
                // Background：低于输入消息的优先级，保证正常松手先被处理。
                timer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(IntervalMilliseconds)
                };
                timer.Tick += OnTick;
            }
            releaseCount = 0;
            inactiveCount = 0;
            if (!timer.IsEnabled)
            {
                timer.Start();
            }
        }

        internal void Stop()
        {
            releaseCount = 0;
            inactiveCount = 0;
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

            if (!isMouseButtonDown())
            {
                releaseCount++;
                inactiveCount = 0;
                if (releaseCount >= ReleaseTicks)
                {
                    Stop();
                    cancel("物理鼠标键已经松开，但程序一直没收到松手事件（多半被截屏工具或别的程序接走了）");
                }
                return;
            }
            releaseCount = 0;

            if (window != null && !window.IsActive)
            {
                inactiveCount++;
                if (inactiveCount >= InactiveTicks)
                {
                    Stop();
                    cancel("窗口已经不是前台（另一支程序，例如截屏工具的全屏浮层，接管了鼠标）");
                }
                return;
            }
            inactiveCount = 0;
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
