using System;
using System.Windows.Input;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共界面】拖动期间的“禁止 / 可放”光标。
    ///
    /// 内部用 `DragCursorKeeper` 每 80ms 重设 `Mouse.OverrideCursor`：
    /// WPF 在指针离开被捕获元素的子树后会刷回箭头，只在移动那一帧设一次会出现
    /// “禁止符号时有时无”。「启动与恢复」「自启动应用」「图标位置保存」现在共用这一份。
    ///
    /// 注意：这一项**不画图标**，就是系统 `Cursors.No`（用户 2026-11-01 拍板保持系统光标）。
    /// “这一帧能不能放”仍由各工作区状态机决定，本类只负责状态与光标。
    /// </summary>
    internal sealed class DragForbiddenCursor : IDisposable
    {
        private readonly DragCursorKeeper keeper;
        private bool forbidden;

        internal DragForbiddenCursor(Func<bool> isDragging)
        {
            keeper = new DragCursorKeeper(
                isDragging,
                delegate { return forbidden; });
        }

        internal bool IsForbidden
        {
            get { return forbidden; }
        }

        /// <summary>起拖时开始保持光标。</summary>
        internal void Start()
        {
            keeper.Start();
        }

        /// <summary>设置这一帧是否禁止；状态没变也会重设一次，避免被系统刷回箭头。</summary>
        internal void Set(bool value)
        {
            forbidden = value;
            try
            {
                Mouse.OverrideCursor = value ? Cursors.No : Cursors.SizeAll;
            }
            catch
            {
            }
        }

        /// <summary>收尾：停保持器、清掉覆盖光标。</summary>
        internal void Stop()
        {
            keeper.Stop();
            forbidden = false;
            try
            {
                Mouse.OverrideCursor = null;
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
            keeper.Dispose();
        }
    }
}
