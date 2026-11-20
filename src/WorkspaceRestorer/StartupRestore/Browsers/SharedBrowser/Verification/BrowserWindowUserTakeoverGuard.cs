using System;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// “用户在恢复中手动最小化浏览器窗口”的唯一判定器。
    ///
    /// 与关窗守卫同级，但语义不同：
    ///   · 窗口还在，只是被用户缩了；
    ///   · 这是用户接管，该窗口组剩余网页不再启动；
    ///   · 不还原、不激活、最后也不缩窗。
    ///
    /// 只认恢复时认领到的精确 HWND + PID；程序自己调用的最小化用
    /// MarkAppMinimizeIssued 排除，避免把自己的动作误判成用户接管。
    /// </summary>
    internal sealed class BrowserWindowUserTakeoverGuard
    {
        private readonly object sync = new object();
        private readonly string restoreKey;
        private readonly BrowserWindowCloseGuard closeGuard;
        private bool appMinimizeIssued;
        private bool userTookOver;

        internal BrowserWindowUserTakeoverGuard(
            string restoreKey,
            BrowserWindowCloseGuard closeGuard)
        {
            this.restoreKey = restoreKey;
            this.closeGuard = closeGuard;
        }

        internal bool HasUserTookOver
        {
            get
            {
                lock (sync)
                {
                    return userTookOver;
                }
            }
        }

        /// <summary>
        /// 程序即将调用 ApplyMinimizedToBrowserWindow 之前置位。
        /// 之后窗口变成 IsIconic 不能再算“用户手动最小化”。
        /// </summary>
        internal void MarkAppMinimizeIssued()
        {
            lock (sync)
            {
                appMinimizeIssued = true;
            }
        }

        /// <summary>
        /// 只读检查一次。返回 true 后本组永久按“用户已接管”处理；
        /// 用户之后自己还原窗口，也不会自动恢复控制。
        /// </summary>
        internal bool ConfirmUserMinimized()
        {
            bool changed = false;
            lock (sync)
            {
                if (userTookOver)
                {
                    return true;
                }
                if (appMinimizeIssued || closeGuard == null)
                {
                    return false;
                }
                if (!closeGuard.HasTrackedWindow || closeGuard.IsClosedByUser)
                {
                    return false;
                }

                IntPtr window = closeGuard.WindowHandle;
                if (window == IntPtr.Zero
                    || !NativeMethods.IsWindow(window)
                    || !NativeMethods.IsIconic(window))
                {
                    return false;
                }

                userTookOver = true;
                changed = true;
            }

            if (changed)
            {
                AppLog.Write("[窗口恢复] 用户已最小化浏览器窗口，"
                    + "停止该组剩余网页并取消最终缩窗："
                    + (restoreKey ?? "(未知窗口)")
                    + " HWND=" + closeGuard.WindowHandle);
            }
            return true;
        }
    }
}
