using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkspaceRestorer.Services
{
    internal static class NativeMethods
    {
        internal const uint ProcessQueryLimitedInformation = 0x1000;
        internal const int DwmwaCloaked = 14;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpShowWindow = 0x0040;
        internal const int SwShowNormal = 1;
        internal const int SwShowMinimized = 2;
        internal const int SwShowMaximized = 3;
        internal const int SwShowNoActivate = 4;
        internal const int SwRestore = 9;
        internal const uint InputKeyboard = 1;
        internal const uint KeyEventKeyUp = 0x0002;
        internal const ushort VirtualKeyControl = 0x11;
        internal const ushort VirtualKeyTab = 0x09;
        internal const ushort VirtualKeyEscape = 0x1B;
        internal const ushort VirtualKeyL = 0x4C;

        internal delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr parameter);

        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        /// <summary>
        /// 把窗口压到 Z 序最底层。读取最小化浏览器标签时要先把窗口显示出来，
        /// 压到底层才能保证它不会盖住用户正在看的窗口。
        /// </summary>
        internal static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowTextLength(IntPtr windowHandle);

        /// <summary>
        /// Current cursor position in physical screen pixels.  During an OLE
        /// drag-and-drop loop both WPF's mouse position and the point carried by
        /// DragOver can be stale, so this is the only dependable pointer source.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr windowHandle);

        /// <summary>
        /// 显示/还原窗口（用户第 64 条：已经有的话就“跳转到有的上去”，而不是重新打开）。
        /// 单用 SetForegroundWindow 有时会被 Windows 的前台锁拒绝，
        /// 先 ShowWindow(SW_RESTORE) 把最小化的窗口还原出来，成功率更高。
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr windowHandle, int command);

        /// <summary>SW_RESTORE：还原被最小化的窗口并激活它。</summary>
        internal const int ShowWindowRestore = 9;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(
            uint attachingThreadId,
            uint attachedThreadId,
            bool attach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            [In] NativeInput[] inputs,
            int inputSize);

        /// <summary>
        /// 虚拟键码：鼠标左键。GetAsyncKeyState 对鼠标键用的是 VK_LBUTTON / VK_RBUTTON 这一套，
        /// 不是 VK_* 键盘码。
        /// </summary>
        internal const int VirtualKeyLeftButton = 0x01;

        /// <summary>鼠标右键（拖动可以配置成右键起拖，看门狗用）。</summary>
        internal const int VirtualKeyRightButton = 0x02;

        /// <summary>
        /// 物理按键当前是否按下（异步键状态，**与窗口焦点、鼠标捕获都无关**）。
        ///
        /// 为什么需要它：拖动（“移动中”那套状态）原本只靠 WPF 的 MouseLeftButtonUp 收尾。
        /// 用户一按截屏工具的快捷键（Snipaste / QQ 截图这类），全屏浮层窗口拿走了鼠标捕获，
        /// 松手事件就发给那个浮层了 —— 我们的窗口收不到 MouseUp，于是一直停在“移动中”：
        /// 浮动方框/插入线不消失、光标一直是“移动”形状、上下自动滚动还在滚（看起来就是卡顿）。
        /// WPF 的 `Mouse.LeftButton` 在窗口失活后也不可靠（它跟着最后处理的消息走），
        /// 所以这里用系统的异步键状态当“物理按键还在不在”的唯一依据。
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        /// <summary>左键物理上还按着吗（供拖动看门狗用，见 NativeMethods.VirtualKeyLeftButton）。</summary>
        internal static bool IsLeftMouseButtonDown()
        {
            try
            {
                return (GetAsyncKeyState(VirtualKeyLeftButton) & 0x8000) != 0;
            }
            catch
            {
                // 取不到时“当作还按着”，宁可让正常松手去收尾，也不要误取消一次真拖动。
                return true;
            }
        }

        /// <summary>右键物理上还按着吗（拖动设置选“右键拖动”时，看门狗改看右键）。</summary>
        internal static bool IsRightMouseButtonDown()
        {
            try
            {
                return (GetAsyncKeyState(VirtualKeyRightButton) & 0x8000) != 0;
            }
            catch
            {
                return true;
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowPlacement(
            IntPtr windowHandle,
            ref WindowPlacement placement);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPlacement(
            IntPtr windowHandle,
            ref WindowPlacement placement);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            out int value,
            int valueSize);

        // ==== 文件关联查询（用来找“这个 txt 现在是被哪个程序打开的”）====
        // AssocQueryString 比直接读注册表可靠：用户改过默认程序也能拿到正确的。
        internal const int AssocfNone = 0;
        internal const int AssocstrExecutable = 2;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int AssocQueryString(
            int flags,
            int str,
            string association,
            string extra,
            StringBuilder output,
            ref int outputSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            IntPtr processHandle,
            int flags,
            StringBuilder executableName,
            ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetApplicationRestartSettings(
            IntPtr processHandle,
            StringBuilder commandLine,
            ref uint size,
            out uint flags);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(
            out NativeFileTime idleTime,
            out NativeFileTime kernelTime,
            out NativeFileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeFileTime
        {
            internal uint LowDateTime;
            internal uint HighDateTime;

            internal ulong ToUInt64()
            {
                return ((ulong)HighDateTime << 32) | LowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MemoryStatusEx
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;

            internal static MemoryStatusEx Create()
            {
                MemoryStatusEx value = new MemoryStatusEx();
                value.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                return value;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeInput
        {
            internal uint Type;
            internal NativeInputUnion Data;

            internal static NativeInput Keyboard(ushort virtualKey, bool keyUp)
            {
                NativeInput value = new NativeInput();
                value.Type = InputKeyboard;
                value.Data.KeyboardInput = new NativeKeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0
                };
                return value;
            }
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct NativeInputUnion
        {
            [FieldOffset(0)]
            internal NativeKeyboardInput KeyboardInput;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeKeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        /// <summary>
        /// Native WINDOWPLACEMENT.  rcNormalPosition remains the user's
        /// normal bounds even while a window is minimized or maximized.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowPlacement
        {
            internal int Length;
            internal int Flags;
            internal int ShowCommand;
            internal NativePoint MinPosition;
            internal NativePoint MaxPosition;
            internal NativeRect NormalPosition;

            internal static WindowPlacement Create()
            {
                WindowPlacement value = new WindowPlacement();
                value.Length = Marshal.SizeOf(typeof(WindowPlacement));
                return value;
            }
        }

        internal static readonly IntPtr MonitorDefaultToNearest = new IntPtr(2);

        /// <summary>`GetMonitorInfo` 的 dwFlags：这块是主显示器。</summary>
        internal const uint MonitorInfoPrimary = 0x00000001;

        internal delegate bool EnumMonitorsProc(IntPtr monitor, IntPtr hdc, ref NativeRect rectangle, IntPtr data);

        /// <summary>枚举当前所有显示器（用户 2026-10-18：要按“现在几块屏、拓扑变没变”来放窗口）。</summary>
        [DllImport("user32.dll")]
        internal static extern bool EnumDisplayMonitors(
            IntPtr deviceContext, IntPtr clipRectangle, EnumMonitorsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MonitorInfo
        {
            internal uint cbSize;
            internal NativeRect rcMonitor;
            internal NativeRect rcWork;
            internal uint dwFlags;

            internal static MonitorInfo Create()
            {
                MonitorInfo value = new MonitorInfo();
                value.cbSize = (uint)Marshal.SizeOf(typeof(MonitorInfo));
                return value;
            }
        }

    }
}
