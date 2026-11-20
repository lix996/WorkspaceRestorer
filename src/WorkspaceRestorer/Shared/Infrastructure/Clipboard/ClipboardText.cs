using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 【公共区域】把文本写进 Windows 剪贴板。
    ///
    /// 为什么不用 <c>System.Windows.Clipboard.SetText</c>：
    /// 实测这台机器上 WPF 的 SetText 每次要 **1001–1011ms**（OLE 剪贴板同步等待），
    /// 它是在 UI 线程上调用的，按一次 Ctrl+C 界面就冻结约 1 秒；
    /// Win32 的 SetClipboardData 只要几毫秒。
    ///
    /// 三个工作区的复制（工作方案 / 自启动方案 / 图标布局）现在统一走这里，
    /// 不要再各自写 `Clipboard.SetText`。
    /// </summary>
    internal static class ClipboardText
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const int OpenAttempts = 6;
        private const int OpenRetryDelayMilliseconds = 10;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        /// <summary>
        /// 尝试写入 Unicode 文本。成功返回 true；失败返回 false 并给出可直接显示的原因。
        /// 只在剪贴板被别的程序短暂占用时重试约 50ms，不会像 WPF 那样卡 1 秒。
        /// </summary>
        public static bool TrySetText(string text, out string error)
        {
            error = null;
            if (text == null)
            {
                error = "没有可复制的内容";
                return false;
            }

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr memory = IntPtr.Zero;
            bool opened = false;
            try
            {
                for (int attempt = 0; attempt < OpenAttempts; attempt++)
                {
                    if (OpenClipboard(IntPtr.Zero))
                    {
                        opened = true;
                        break;
                    }
                    if (attempt < OpenAttempts - 1)
                    {
                        Thread.Sleep(OpenRetryDelayMilliseconds);
                    }
                }
                if (!opened)
                {
                    error = "剪贴板正被其他程序占用，请稍后重试";
                    return false;
                }

                if (!EmptyClipboard())
                {
                    error = "无法清空系统剪贴板";
                    return false;
                }

                memory = GlobalAlloc(GMEM_MOVEABLE, new UIntPtr((uint)bytes.Length));
                if (memory == IntPtr.Zero)
                {
                    error = "无法为剪贴板分配内存";
                    return false;
                }

                IntPtr target = GlobalLock(memory);
                if (target == IntPtr.Zero)
                {
                    error = "无法锁定剪贴板内存";
                    return false;
                }
                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(CF_UNICODETEXT, memory) == IntPtr.Zero)
                {
                    error = "系统拒绝了剪贴板写入";
                    return false;
                }

                // 成功以后内存归 Windows 所有，不能再 GlobalFree。
                memory = IntPtr.Zero;
                return true;
            }
            catch (Exception exception)
            {
                error = "写剪贴板出错：" + exception.Message;
                return false;
            }
            finally
            {
                if (memory != IntPtr.Zero)
                {
                    GlobalFree(memory);
                }
                if (opened)
                {
                    CloseClipboard();
                }
            }
        }
    }
}
