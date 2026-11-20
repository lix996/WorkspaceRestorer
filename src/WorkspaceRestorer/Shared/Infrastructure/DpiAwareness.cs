using System;
using System.Runtime.InteropServices;

namespace WorkspaceRestorer.Infrastructure
{
    internal static class DpiAwareness
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDpiAwareness value);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDPIAware();

        public static void EnablePerMonitorV2()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(PerMonitorAwareV2))
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                if (SetProcessDpiAwareness(ProcessDpiAwareness.PerMonitorAware) >= 0)
                {
                    return;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        private enum ProcessDpiAwareness
        {
            Unaware = 0,
            SystemAware = 1,
            PerMonitorAware = 2
        }
    }
}
