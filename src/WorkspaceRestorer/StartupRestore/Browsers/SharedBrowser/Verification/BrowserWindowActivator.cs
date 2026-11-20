using System;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    internal static class BrowserWindowActivator
    {
        internal static bool TryActivate(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }
            try
            {
                if (NativeMethods.IsIconic(windowHandle))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.ShowWindowRestore);
                }
                if (NativeMethods.GetForegroundWindow() == windowHandle)
                {
                    return true;
                }
                bool set = false;
                try
                {
                    set = NativeMethods.SetForegroundWindow(windowHandle);
                }
                catch
                {
                }
                if (set || NativeMethods.GetForegroundWindow() == windowHandle)
                {
                    return true;
                }

                uint foregroundProcessId;
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                uint foregroundThread = NativeMethods.GetWindowThreadProcessId(
                    foreground,
                    out foregroundProcessId);
                uint currentThread = NativeMethods.GetCurrentThreadId();
                bool attached = false;
                try
                {
                    if (foregroundThread != 0 && foregroundThread != currentThread)
                    {
                        attached = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
                    }
                    set = NativeMethods.SetForegroundWindow(windowHandle);
                }
                catch
                {
                }
                finally
                {
                    if (attached)
                    {
                        try
                        {
                            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
                        }
                        catch
                        {
                        }
                    }
                }
                return set || NativeMethods.GetForegroundWindow() == windowHandle;
            }
            catch
            {
                return false;
            }
        }
    }
}
