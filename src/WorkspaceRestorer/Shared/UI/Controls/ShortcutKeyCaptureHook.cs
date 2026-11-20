using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【快捷键捕获专用】只在“请按新快捷键”期间安装的底层键盘钩子。
    ///
    /// 为什么不能只靠 WPF 的 PreviewKeyDown：
    ///   2026-11-16 实测，部分机器上 Alt+字母会被系统菜单整段吃掉，WPF 只收到单独的
    ///   LeftAlt，后面的字母连 PreProcessInput 都收不到。用户要求键盘前缀可以选 Alt，
    ///   所以捕获期间临时用 WH_KEYBOARD_LL 补收；普通输入、非捕获状态完全不挂钩子。
    ///
    /// 边界：
    ///   · 只在录键弹窗是前台窗口时接管；
    ///   · Enter / Esc / Tab / 纯修饰键一律放行，仍由 WPF 处理确认、取消和焦点；
    ///   · 捕获结束（确认、取消、关窗）立即卸载；
    ///   · 不记录鼠标，不改变任何拖动/单击/双击行为。
    /// </summary>
    internal sealed class ShortcutKeyCaptureHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int WmKeyDown = 0x0100;
        private const int WmSysKeyDown = 0x0104;

        private readonly Window owner;
        private readonly Action<Key, ModifierKeys> captured;
        private readonly bool allowModifierOnly;
        private LowLevelKeyboardProc callback;
        private IntPtr hook;
        private IntPtr ownerHandle;
        private bool disposed;

        internal ShortcutKeyCaptureHook(Window owner, Action<Key, ModifierKeys> captured)
            : this(owner, captured, false)
        {
        }

        /// <param name="allowModifierOnly">
        /// true = 拖动前缀那种“可以只有修饰键”的捕获：按下 Ctrl/Alt/Shift 也会回调
        /// <see cref="Key.None"/> + 当前修饰键，但不吞掉这次按键（WPF 仍要更新修饰键状态）。
        /// </param>
        internal ShortcutKeyCaptureHook(
            Window owner,
            Action<Key, ModifierKeys> captured,
            bool allowModifierOnly)
        {
            if (owner == null)
            {
                throw new ArgumentNullException("owner");
            }
            this.owner = owner;
            this.captured = captured;
            this.allowModifierOnly = allowModifierOnly;
        }

        internal bool IsActive
        {
            get { return hook != IntPtr.Zero; }
        }

        internal bool Start()
        {
            if (disposed || hook != IntPtr.Zero)
            {
                return hook != IntPtr.Zero;
            }
            try
            {
                ownerHandle = new WindowInteropHelper(owner).Handle;
                if (ownerHandle == IntPtr.Zero)
                {
                    return false;
                }
                callback = HookCallback;
                hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero)
                {
                    AppLog.Write("[快捷键捕获] 键盘钩子安装失败，错误码=" + Marshal.GetLastWin32Error());
                    callback = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[快捷键捕获] 键盘钩子安装异常", exception);
                callback = null;
                hook = IntPtr.Zero;
                return false;
            }
        }

        internal void Stop()
        {
            if (hook != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(hook);
                }
                catch (Exception exception)
                {
                    AppLog.Write("[快捷键捕获] 卸载键盘钩子失败", exception);
                }
                hook = IntPtr.Zero;
            }
            callback = null;
            ownerHandle = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0
                    && hook != IntPtr.Zero
                    && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown)
                    && GetForegroundWindow() == ownerHandle)
                {
                    KbdLlHookStruct data = (KbdLlHookStruct)Marshal.PtrToStructure(
                        lParam, typeof(KbdLlHookStruct));
                    Key key = KeyInterop.KeyFromVirtualKey(data.vkCode);
                    if (key != Key.None
                        && !ShortcutKeyText.IsModifierKey(key)
                        && !ShortcutKeyText.IsReservedKey(key))
                    {
                        ModifierKeys modifiers = ReadModifiers();
                        // 先吞掉这次按键，避免它同时触发菜单/按钮/全局快捷键；
                        // 再回到 Dispatcher 队列里更新界面，避免在底层钩子里直接改 WPF 控件。
                        Dispatch(key, modifiers);
                        return (IntPtr)1;
                    }
                    if (allowModifierOnly && key != Key.None && ShortcutKeyText.IsModifierKey(key))
                    {
                        // 拖动前缀允许“只按 Shift”；不吞掉修饰键，让 WPF 照常维护按键状态。
                        Dispatch(Key.None, ReadModifiers());
                    }
                }
            }
            catch (Exception exception)
            {
                // 钩子回调绝不能把异常抛回系统；写日志后继续放行。
                AppLog.Write("[快捷键捕获] 键盘钩子回调异常", exception);
            }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        /// <summary>回到 Dispatcher 队列里更新界面，避免在底层钩子里直接改 WPF 控件。</summary>
        private void Dispatch(Key key, ModifierKeys modifiers)
        {
            Action<Key, ModifierKeys> handler = captured;
            if (handler == null)
            {
                return;
            }
            owner.Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                new Action(delegate
                {
                    if (hook != IntPtr.Zero)
                    {
                        handler(key, modifiers);
                    }
                }));
        }

        /// <summary>从真实键状态读修饰键；不依赖 WPF 的 Keyboard.Modifiers（Alt 组合时可能还没更新）。</summary>
        private static ModifierKeys ReadModifiers()
        {
            ModifierKeys modifiers = ModifierKeys.None;
            if ((GetAsyncKeyState(0x11) & 0x8000) != 0) { modifiers |= ModifierKeys.Control; } // VK_CONTROL
            if ((GetAsyncKeyState(0x12) & 0x8000) != 0) { modifiers |= ModifierKeys.Alt; }     // VK_MENU
            if ((GetAsyncKeyState(0x10) & 0x8000) != 0) { modifiers |= ModifierKeys.Shift; }   // VK_SHIFT
            return modifiers;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(
            int idHook,
            LowLevelKeyboardProc lpfn,
            IntPtr hMod,
            uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(
            IntPtr hhk,
            int nCode,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
