using System;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// B3：恢复过程中“用户关闭浏览器窗口”的唯一判定器。
    ///
    /// 只认恢复时真正认领到的**精确 HWND + PID**：
    ///   · 窗口还在且 PID 一致 = 还开着（最小化/隐藏不影响）；
    ///   · 窗口销毁，或 HWND 被别的 PID 复用 = 已关闭；
    ///   · 连续两次检查都失效才下结论，避免浏览器重建窗口的瞬间误判；
    ///   · 一旦判定关闭就永久生效，绝不换一个新窗口继续恢复/缩窗。
    ///
    /// 这里不主动关窗、不缩窗、不激活窗口，只读状态。
    /// </summary>
    internal sealed class BrowserWindowCloseGuard
    {
        private readonly object sync = new object();
        private readonly string restoreKey;
        private IntPtr windowHandle;
        private int processId;
        private int consecutiveMissingChecks;
        private bool closedByUser;

        internal BrowserWindowCloseGuard()
            : this(null)
        {
        }

        internal BrowserWindowCloseGuard(string restoreKey)
        {
            this.restoreKey = restoreKey;
        }

        internal IntPtr WindowHandle
        {
            get
            {
                lock (sync)
                {
                    return windowHandle;
                }
            }
        }

        internal int ProcessId
        {
            get
            {
                lock (sync)
                {
                    return processId;
                }
            }
        }

        /// <summary>
        /// 是否已经认领过窗口。注意：认领后窗口被关闭时这里仍为 true，
        /// 调用方应看 <see cref="IsClosedByUser"/>。
        /// </summary>
        internal bool HasTrackedWindow
        {
            get
            {
                lock (sync)
                {
                    return windowHandle != IntPtr.Zero;
                }
            }
        }

        internal bool IsClosedByUser
        {
            get
            {
                lock (sync)
                {
                    return closedByUser;
                }
            }
        }

        /// <summary>
        /// 认领恢复窗口。只允许认领一次；之后即使出现另一个窗口也不替换，
        /// 防止用户关窗后浏览器新建窗口被误当成原窗口继续恢复。
        /// </summary>
        internal bool Attach(IntPtr handle)
        {
            if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
            {
                return false;
            }

            uint ownerProcessId;
            NativeMethods.GetWindowThreadProcessId(handle, out ownerProcessId);
            if (ownerProcessId == 0 || ownerProcessId > int.MaxValue)
            {
                return false;
            }

            lock (sync)
            {
                if (closedByUser)
                {
                    return false;
                }
                if (windowHandle != IntPtr.Zero)
                {
                    return windowHandle == handle
                        && processId == (int)ownerProcessId;
                }
                windowHandle = handle;
                processId = (int)ownerProcessId;
                consecutiveMissingChecks = 0;
                return true;
            }
        }

        /// <summary>
        /// Edge/Chromium 有时会用新 HWND 替换引导窗口。只有旧句柄已经失效、
        /// 新句柄仍属于同一个浏览器进程时才允许接替，避免把用户关窗后
        /// 自己新开的窗口误当成原窗口继续恢复。
        /// </summary>
        internal bool TryAdoptReplacement(IntPtr replacement)
        {
            if (replacement == IntPtr.Zero || !NativeMethods.IsWindow(replacement))
            {
                return false;
            }

            uint ownerProcessId;
            NativeMethods.GetWindowThreadProcessId(replacement, out ownerProcessId);
            if (ownerProcessId == 0 || ownerProcessId > int.MaxValue)
            {
                return false;
            }

            lock (sync)
            {
                if (closedByUser || windowHandle == IntPtr.Zero)
                {
                    return false;
                }
                if (windowHandle == replacement)
                {
                    // 同一个 HWND 也可能已经被别的 PID 复用，必须继续核对 PID。
                    return IsAliveLocked();
                }
                if ((int)ownerProcessId != processId
                    || NativeMethods.IsWindow(windowHandle))
                {
                    return false;
                }
                windowHandle = replacement;
                consecutiveMissingChecks = 0;
                return true;
            }
        }

        /// <summary>
        /// 窗口跟踪器里曾经记过这个窗口，但句柄现在已经失效。说明恢复窗口
        /// 确实存在过并已消失；按用户关窗处理，避免下一张网页再开一个新窗口。
        /// 只在该守卫还没认领过其它窗口时生效。
        /// </summary>
        internal void MarkTrackedWindowLost(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            bool changed = false;
            lock (sync)
            {
                if (closedByUser)
                {
                    return;
                }
                if (windowHandle != IntPtr.Zero && windowHandle != handle)
                {
                    return;
                }
                windowHandle = handle;
                processId = 0;
                consecutiveMissingChecks = 2;
                closedByUser = true;
                changed = true;
            }

            if (changed)
            {
                AppLog.Write("[窗口恢复] 浏览器窗口已消失（跟踪句柄失效），"
                    + "停止该窗口剩余网页并取消缩窗："
                    + (restoreKey ?? "(未知窗口)") + " HWND=" + handle);
            }
        }

        /// <summary>
        /// 只检查一次。连续两次失效才把窗口标记为“用户已关闭”。
        /// 返回 true 表示已经确认关闭。
        /// </summary>
        internal bool ObserveOnce()
        {
            lock (sync)
            {
                if (closedByUser)
                {
                    return true;
                }
                if (windowHandle == IntPtr.Zero)
                {
                    return false;
                }
                if (IsAliveLocked())
                {
                    consecutiveMissingChecks = 0;
                    return false;
                }
                consecutiveMissingChecks++;
                if (consecutiveMissingChecks < 2)
                {
                    return false;
                }
                closedByUser = true;
            }

            AppLog.Write("[窗口恢复] 用户已关闭浏览器窗口，"
                + "停止该窗口剩余网页并取消缩窗："
                + (restoreKey ?? "(未知窗口)") + " HWND=" + WindowHandle);
            return true;
        }

        /// <summary>
        /// 连续检查两次（中间等一小会儿）后确认窗口是否已被用户关闭。
        /// 窗口还开着时几乎是零开销；只有疑似消失时才等待确认。
        /// </summary>
        internal bool ConfirmClosedByUser(int delayMilliseconds)
        {
            if (IsClosedByUser)
            {
                return true;
            }
            if (!HasTrackedWindow)
            {
                return false;
            }
            if (IsAliveNow())
            {
                lock (sync)
                {
                    consecutiveMissingChecks = 0;
                }
                return false;
            }

            if (delayMilliseconds > 0)
            {
                Thread.Sleep(delayMilliseconds);
            }
            if (IsAliveNow())
            {
                lock (sync)
                {
                    consecutiveMissingChecks = 0;
                }
                return false;
            }

            bool changed = false;
            lock (sync)
            {
                if (!closedByUser)
                {
                    consecutiveMissingChecks = 2;
                    closedByUser = true;
                    changed = true;
                }
            }
            if (changed)
            {
                AppLog.Write("[窗口恢复] 用户已关闭浏览器窗口，"
                    + "停止该窗口剩余网页并取消缩窗："
                    + (restoreKey ?? "(未知窗口)") + " HWND=" + WindowHandle);
            }
            return true;
        }

        /// <summary>只读判断，不累加“连续失效”计数。</summary>
        internal bool IsAliveNow()
        {
            lock (sync)
            {
                return IsAliveLocked();
            }
        }

        private bool IsAliveLocked()
        {
            if (closedByUser || windowHandle == IntPtr.Zero)
            {
                return false;
            }
            if (!NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            uint ownerProcessId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out ownerProcessId);
            return ownerProcessId != 0
                && ownerProcessId <= int.MaxValue
                && (int)ownerProcessId == processId;
        }
    }
}
