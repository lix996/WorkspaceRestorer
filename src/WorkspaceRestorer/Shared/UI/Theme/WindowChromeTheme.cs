using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 深色窗口标题栏。只改 DWM 的沉浸式深色属性，不接管窗口边框。
    /// Windows 10 1809+ 用属性 20，旧版本退回 19；不支持时安静失败。
    /// </summary>
    internal static class WindowChromeTheme
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private static bool installed;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int attribute,
            ref int value,
            int valueSize);

        internal static void Install()
        {
            if (installed)
            {
                return;
            }
            installed = true;
            try
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnWindowLoaded));
            }
            catch (Exception)
            {
                // 主题失败不能阻止窗口打开。
            }
        }

        internal static void ApplyToAllWindows(bool dark)
        {
            Application application = Application.Current;
            if (application == null)
            {
                return;
            }
            foreach (Window window in application.Windows)
            {
                Apply(window, dark);
            }
        }

        internal static void Apply(Window window, bool dark)
        {
            if (window == null)
            {
                return;
            }
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                {
                    return;
                }
                int value = dark ? 1 : 0;
                int result = DwmSetWindowAttribute(
                    handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
                if (result != 0)
                {
                    DwmSetWindowAttribute(
                        handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, sizeof(int));
                }
            }
            catch (Exception)
            {
                // 系统不支持深色标题栏时保持系统默认。
            }
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            Window window = sender as Window;
            if (window == null)
            {
                return;
            }
            Apply(window, ThemeService.EffectiveDark);
            // 有些 Windows 11 版本在 Loaded 时还没准备好 DWM 标题栏状态，
            // 内容渲染完成后再补一次；失败仍由 Apply 内部吞掉。
            window.ContentRendered += delegate
            {
                Apply(window, ThemeService.EffectiveDark);
            };
            window.Activated += delegate
            {
                // 激活时系统可能重新处理非客户区；激活后再补一次最稳。
                Apply(window, ThemeService.EffectiveDark);
            };
        }
    }
}
