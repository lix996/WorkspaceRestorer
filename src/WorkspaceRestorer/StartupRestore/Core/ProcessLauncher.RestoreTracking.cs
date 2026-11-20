using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// B2：登记“恢复开始前用户已经打开的非浏览器窗口”。
    ///
    /// 用户要求：
    ///   · 恢复过程中，前面已经启动完的项目不再回头恢复（主循环本来就不会回头）；
    ///   · 轮到某个项目之前，用户把它手动关了 → 这一项直接跳过，不再重新打开；
    ///   · 浏览器例外：浏览器窗口关闭不代表整组网页不要，浏览器的恢复逻辑
    ///     保持原样，不能因为关了一个窗口就跳过其它网页。
    ///
    /// 判定只认“恢复开始时记下的精确 HWND + PID：
    ///   · 窗口还在且 PID 一致 = 还开着；
    ///   · 窗口销毁，或 HWND 被别的 PID 复用 = 用户已关闭；
    ///   · 最小化、隐藏到托盘但 HWND 仍存在 = 不当作关闭。
    /// 找不到唯一窗口时宁可不登记，也不拿同 exe 的其它窗口瞎猜。
    /// </summary>
    public sealed partial class ProcessLauncher : IRestoreItemTrackingLauncher
    {
        private sealed class RestoreStartWindow
        {
            internal IntPtr Handle;
            internal int ProcessId;
        }

        private readonly Dictionary<SessionItem, List<RestoreStartWindow>> restoreStartWindows =
            new Dictionary<SessionItem, List<RestoreStartWindow>>();

        private volatile bool restoreItemTrackingActive;

        void IRestoreItemTrackingLauncher.BeginRestoreItemTracking(IList<SessionItem> items)
        {
            restoreStartWindows.Clear();
            restoreItemTrackingActive = false;
            if (items == null || items.Count == 0)
            {
                return;
            }

            int trackedItems = 0;
            int trackedWindows = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (!ShouldTrackRestoreStartItem(item))
                {
                    continue;
                }

                try
                {
                    IntPtr handle = FindRestoreStartWindow(item);
                    if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle))
                    {
                        continue;
                    }

                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(handle, out processId);
                    if (processId == 0 || processId > int.MaxValue)
                    {
                        continue;
                    }

                    List<RestoreStartWindow> windows;
                    if (!restoreStartWindows.TryGetValue(item, out windows))
                    {
                        windows = new List<RestoreStartWindow>();
                        restoreStartWindows[item] = windows;
                    }
                    windows.Add(new RestoreStartWindow
                    {
                        Handle = handle,
                        ProcessId = (int)processId
                    });
                    trackedItems++;
                    trackedWindows++;
                }
                catch (Exception exception)
                {
                    // B2 是“尽量按用户意图跳过”的增强，登记失败不能挡住整次恢复。
                    AppLog.Write("[恢复] 登记已打开窗口失败，该项不启用提前关闭跳过："
                        + (item.DisplayName ?? item.TargetPath ?? "(未命名)"), exception);
                }
            }

            restoreItemTrackingActive = true;
            AppLog.Write("[恢复] 已登记恢复前用户打开的非浏览器窗口："
                + trackedItems + " 项 / " + trackedWindows + " 个窗口；"
                + "轮到该项前被用户关闭的将跳过，浏览器不参与此规则。");
        }

        void IRestoreItemTrackingLauncher.EndRestoreItemTracking()
        {
            restoreItemTrackingActive = false;
            restoreStartWindows.Clear();
        }

        /// <summary>
        /// 只登记非浏览器项。浏览器窗口/网页故意排除，保持现有恢复逻辑。
        /// </summary>
        internal static bool ShouldTrackRestoreStartItem(SessionItem item)
        {
            if (item == null)
            {
                return false;
            }
            if (item.Kind == SessionItemKind.WebPage)
            {
                return false;
            }
            if (TerminalAndLocalUrlPolicy.IsExcludedKind(item))
            {
                return false;
            }
            if (item.Kind == SessionItemKind.Application)
            {
                // 浏览器父行只负责网页归属，不是用户要启动的独立窗口。
                if (!string.IsNullOrWhiteSpace(item.BrowserGroupKey)
                    || string.Equals(item.CaptureSource, "BrowserWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                // 没有文件夹子项时资源管理器父行会被过滤掉；这里再防一次。
                if (string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return item.Kind == SessionItemKind.Application
                || item.Kind == SessionItemKind.ExplorerFolder
                || item.Kind == SessionItemKind.Document;
        }

        /// <summary>
        /// 恢复开始时找这个条目对应的那一个窗口。
        /// 找不到唯一窗口就返回 0：宁可不启用 B2，也不误判成“用户关了”。
        /// </summary>
        private static IntPtr FindRestoreStartWindow(SessionItem item)
        {
            if (item == null)
            {
                return IntPtr.Zero;
            }

            if (item.Kind == SessionItemKind.Document)
            {
                string document = string.IsNullOrWhiteSpace(item.TargetPath)
                    ? item.ExecutablePath
                    : item.TargetPath;
                return DocumentWindowFinder.FindWindowShowing(document);
            }

            if (item.Kind == SessionItemKind.ExplorerFolder)
            {
                bool isOpen;
                return FindOpenExplorerFolderWindow(item, out isOpen);
            }

            if (item.Kind != SessionItemKind.Application)
            {
                return IntPtr.Zero;
            }

            string executable = ResolveRestoreTrackingExecutable(item);
            if (string.IsNullOrWhiteSpace(executable))
            {
                return IntPtr.Zero;
            }

            List<IntPtr> windows = FindVisibleWindowsByExecutable(executable);
            if (windows.Count == 0)
            {
                return IntPtr.Zero;
            }
            if (windows.Count == 1)
            {
                return windows[0];
            }

            // 同一 exe 多个窗口：只有保存了窗口序号或标题时才挑，
            // 否则返回 0（不能把别的窗口当成这一项）。
            List<IntPtr> titled = new List<IntPtr>();
            if (!string.IsNullOrWhiteSpace(item.WindowTitle))
            {
                for (int index = 0; index < windows.Count; index++)
                {
                    string title = ReadNativeTitle(windows[index]);
                    if (!string.IsNullOrWhiteSpace(title)
                        && title.IndexOf(item.WindowTitle.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        titled.Add(windows[index]);
                    }
                }
            }

            int windowIndex = item.ApplicationWindowIndex;
            if (windowIndex >= 0 && windowIndex < windows.Count)
            {
                // 标题也吻合时优先用序号；序号本身已经足以区分同一 exe 的窗口。
                return windows[windowIndex];
            }
            if (titled.Count == 1)
            {
                return titled[0];
            }
            if (titled.Count > 1 && windowIndex >= 0 && windowIndex < titled.Count)
            {
                return titled[windowIndex];
            }
            return IntPtr.Zero;
        }

        private static string ResolveRestoreTrackingExecutable(SessionItem item)
        {
            string path = item == null ? null : item.ExecutablePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = item == null ? null : item.TargetPath;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            path = Environment.ExpandEnvironmentVariables(path.Trim());

            string extension;
            try
            {
                extension = Path.GetExtension(path);
            }
            catch
            {
                extension = null;
            }
            if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
            {
                string target = ResolveShortcutTarget(path);
                if (!string.IsNullOrWhiteSpace(target)
                    && (File.Exists(target) || Directory.Exists(target)))
                {
                    if (Directory.Exists(target))
                    {
                        return "%WINDIR%\\explorer.exe";
                    }
                    return target;
                }
            }
            return path;
        }

        /// <summary>
        /// 枚举这个可执行文件当前的可见顶层窗口（保持 EnumWindows 的 Z 序）。
        /// 恢复前登记和“找任意窗口”分开，避免把多个窗口压成一个。
        /// </summary>
        private static List<IntPtr> FindVisibleWindowsByExecutable(string executablePath)
        {
            List<IntPtr> windows = new List<IntPtr>();
            string normalized = NormalizeLaunchPath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return windows;
            }

            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }
                if (ProcessExecutableMatches(windowHandle, normalized))
                {
                    windows.Add(windowHandle);
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (windows.Count > 0)
            {
                return windows;
            }

            // 打包应用（新版记事本等）和部分提权程序拿不到真实 exe 路径：
            // 按进程名再找一遍。这和 ShouldSkipAlreadyOpen 里的兜底是同一思路，
            // 只用来登记“恢复前已经开着”的窗口，不会启动/关闭任何东西。
            string expectedName;
            try
            {
                expectedName = Path.GetFileNameWithoutExtension(normalized);
            }
            catch
            {
                expectedName = null;
            }
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                return windows;
            }
            NativeMethods.EnumWindowsProc byName = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }
                if (ProcessNameMatches(windowHandle, expectedName))
                {
                    windows.Add(windowHandle);
                }
                return true;
            };
            NativeMethods.EnumWindows(byName, IntPtr.Zero);
            GC.KeepAlive(byName);
            return windows;
        }

        private static bool ProcessNameMatches(IntPtr windowHandle, string expectedProcessName)
        {
            if (string.IsNullOrWhiteSpace(expectedProcessName))
            {
                return false;
            }
            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(
                        process.ProcessName,
                        expectedProcessName,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 恢复开始时登记的那个精确窗口是否已经被用户关闭。
        /// 最小化/隐藏但 HWND 还在 → 不算关闭；HWND 被别的 PID 复用 → 算关闭。
        /// </summary>
        private bool WasRestoreStartWindowClosedByUser(SessionItem item)
        {
            if (!restoreItemTrackingActive || item == null)
            {
                return false;
            }

            List<RestoreStartWindow> windows;
            if (!restoreStartWindows.TryGetValue(item, out windows)
                || windows == null
                || windows.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < windows.Count; index++)
            {
                RestoreStartWindow window = windows[index];
                if (!NativeMethods.IsWindow(window.Handle))
                {
                    continue;
                }
                uint processId;
                NativeMethods.GetWindowThreadProcessId(window.Handle, out processId);
                if ((int)processId == window.ProcessId)
                {
                    return false;
                }
            }

            AppLog.Write("[恢复] 用户在轮到此项前已关闭，跳过、不重新打开："
                + (item.DisplayName ?? item.TargetPath ?? item.ExecutablePath ?? "(未命名)"));
            return true;
        }
    }
}
