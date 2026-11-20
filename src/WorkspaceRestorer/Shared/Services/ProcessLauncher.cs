using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// Starts a captured session item through the Windows shell.  All external
    /// effects are checked at this boundary so callers cannot accidentally
    /// start a process during an isolated test run.
    /// </summary>
    public sealed partial class ProcessLauncher : IProcessLauncher, IBrowserWindowBatchLauncher, IWindowPlacementProcessLauncher
    {
        /// <summary>
        /// 本次进程里“由我们启动过的文件”（小写全路径）。
        /// 用来区分“早就开着的文件”和“恢复过程中刚被我们打开的”：
        /// 前者要跳过并把窗口调到前面，后者不能跳过（否则一份方案里的两个文件只开一个）。
        /// </summary>
        private readonly HashSet<string> documentsLaunchedByUs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool IsDocumentOpenedBeforeThisRestore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }
            try
            {
                return !documentsLaunchedByUs.Contains(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(filePath.Trim())));
            }
            catch
            {
                return true;
            }
        }

        private void RememberDocumentLaunchedByUs(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }
            try
            {
                documentsLaunchedByUs.Add(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(filePath.Trim())));
            }
            catch
            {
            }
        }

        public LaunchResult Launch(SessionItem item)
        {
            if (item == null)
            {
                return Failure("Session item is required.");
            }

            if (OsMutationGuard.IsDisabled || ExternalEffectsDisabled())
            {
                return Suppressed();
            }

            try
            {
                // B2：恢复开始前这个非浏览器窗口本来是开着的，轮到它之前
                // 被用户手动关闭 → 跳过。浏览器网页/浏览器父行永远不会进
                // 这张表，浏览器的恢复逻辑保持原样。
                if (WasRestoreStartWindowClosedByUser(item))
                {
                    return UserClosed();
                }

                // If the item's window (application, workspace or folder) is
                // already open, do not launch another instance of it.
                if (ShouldSkipAlreadyOpen(item))
                {
                    return AlreadyOpen();
                }

                // Browser parents are restored through their saved web pages
                // (each saved window is opened once with all its pages).  A
                // browser parent that reached this point has no saved pages,
                // so launching it would only open an empty browser window.
                if (item.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                {
                    return AlreadyOpen();
                }

                if (item.Kind == SessionItemKind.ExplorerFolder)
                {
                    string folder = item.TargetPath;
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        return Failure("The Explorer folder path is empty.");
                    }

                    if (!Directory.Exists(folder))
                    {
                        return Failure("The Explorer folder does not exist: " + folder);
                    }

                    return Start(
                        Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe"),
                        QuoteArgument(folder),
                        null,
                        item.WindowPlacement,
                        item.WindowTitle,
                        folder);
                }

                if (item.Kind == SessionItemKind.Document)
                {
                    string documentPath = string.IsNullOrWhiteSpace(item.TargetPath)
                        ? item.ExecutablePath
                        : item.TargetPath;
                    LaunchResult documentResult = LaunchPath(documentPath);
                    if (documentResult != null && documentResult.Success && !documentResult.WasAlreadyOpen)
                    {
                        RememberDocumentLaunchedByUs(documentPath);
                    }
                    return documentResult;
                }

                if (item.Kind == SessionItemKind.WebPage)
                {
                    return LaunchWebPage(item);
                }

                if (string.IsNullOrWhiteSpace(item.ExecutablePath))
                {
                    return Failure("The application executable path is empty.");
                }

                string executablePath = Environment.ExpandEnvironmentVariables(item.ExecutablePath.Trim());
                if (!File.Exists(executablePath))
                {
                    return Failure("The application executable does not exist: " + executablePath);
                }

                return Start(
                    executablePath,
                    item.Arguments,
                    item.WorkingDirectory,
                    item.WindowPlacement,
                    item.WindowTitle,
                    item.TargetPath);
            }
            catch (Exception exception)
            {
                return Failure(GetErrorMessage(exception));
            }
        }

        public LaunchResult LaunchPath(string path)
        {
            return LaunchPath(path, null, null);
        }

        public LaunchResult LaunchPath(
            string path,
            SavedWindowPlacement placement,
            string windowTitle)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Failure("A path is required.");
            }

            path = Environment.ExpandEnvironmentVariables(path.Trim());

            // 用户要求（2026-10-10 17:21 / 17:45）：“现在自启动应用并不会跳过，要抄启动与恢复的代码，
            // 发现已经恢复的要跳过，而不是重新打开。”——「启动与恢复」是靠 Launch(SessionItem) 里的
            // ShouldSkipAlreadyOpen 实现的；自启动应用走的是 LaunchPath，原来没有这一步，
            // 所以点“恢复”/开机自动启动时会把已经开着的程序又开一遍（用户举例：图狗启动器）。
            //
            // 顺序很重要：**先判断“是不是已经开着”，再看测试模式的护栏**。
            // 反过来的话，测试模式下护栏会先返回“已抑制”，于是永远统计不到“跳过”，
            // 自检里就看不出跳过逻辑到底有没有生效（这里踩过一次）。
            if (IsTargetAlreadyRunning(path))
            {
                // 用户第 64 条：“点击跳转的时候，如果已经有了，就跳转到有的上去，而不是重新打开，
                // 可以参考启动与恢复的代码。”→ 不只是“不重开”，还要把已经开着的窗口**调出来**。
                if (WindowMinimizeState.IsMinimized(placement))
                {
                    MinimizeExistingTarget(path, windowTitle);
                }
                else
                {
                    ActivateExistingTarget(path);
                }
                return AlreadyOpen();
            }

            if (OsMutationGuard.IsDisabled || ExternalEffectsDisabled())
            {
                return Suppressed();
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return Failure("The target does not exist: " + path);
            }

            try
            {
                // 文件夹要通过 explorer.exe 打开，不能把文件夹本身当可执行文件：
                // 否则窗口身份匹配会把“当前进程路径”和文件夹路径比较，找不到刚打开的
                // 资源管理器窗口，自启动应用的“启动时最小化”就会落空。
                if (Directory.Exists(path))
                {
                    return Start(
                        Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe"),
                        QuoteArgument(path),
                        null,
                        placement,
                        windowTitle,
                        path);
                }

                // ShellExecute lets Windows choose the registered application for
                // documents, shortcuts, URLs, and folders.  The placement is only
                // supplied when the self-start item asked for a window state.
                return Start(path, null, Path.GetDirectoryName(path), placement,
                    windowTitle, path);
            }
            catch (Exception exception)
            {
                return Failure(GetErrorMessage(exception));
            }
        }

        private static LaunchResult Start(string fileName, string arguments, string workingDirectory)
        {
            return Start(fileName, arguments, workingDirectory, null, null);
        }

        private static LaunchResult Start(
            string fileName,
            string arguments,
            string workingDirectory,
            SavedWindowPlacement placement,
            string windowTitle)
        {
            return Start(fileName, arguments, workingDirectory, placement, windowTitle, null);
        }

        private static LaunchResult Start(
            string fileName,
            string arguments,
            string workingDirectory,
            SavedWindowPlacement placement,
            string windowTitle,
            string windowIdentityPath)
        {
            return Start(
                fileName,
                arguments,
                workingDirectory,
                placement,
                windowTitle,
                windowIdentityPath,
                false,
                null);
        }

        private static LaunchResult Start(
            string fileName,
            string arguments,
            string workingDirectory,
            SavedWindowPlacement placement,
            string windowTitle,
            string windowIdentityPath,
            bool browserWindow,
            IList<string> browserWindowTitles)
        {
            return Start(
                fileName,
                arguments,
                workingDirectory,
                placement,
                windowTitle,
                windowIdentityPath,
                browserWindow,
                browserWindowTitles,
                null,
                null);
        }

        private static LaunchResult Start(
            string fileName,
            string arguments,
            string workingDirectory,
            SavedWindowPlacement placement,
            string windowTitle,
            string windowIdentityPath,
            bool browserWindow,
            IList<string> browserWindowTitles,
            string restoreKey,
            ISet<IntPtr> existingWindows,
            string taskbarAppId = null)
        {
            string restoreLabel = !string.IsNullOrWhiteSpace(windowIdentityPath)
                ? windowIdentityPath
                : (string.IsNullOrWhiteSpace(windowTitle) ? fileName : windowTitle);
            SavedWindowPlacement originalPlacement = placement;
            WindowPlacementPolicyDecision sanitized = WindowPlacementPolicy.Current.Inspect(placement);
            if (sanitized.Action == WindowPlacementPolicyAction.Skip)
            {
                AppLog.Write("[窗口恢复] 恢复前体检：跳过隐藏到屏幕外的窗口："
                    + restoreLabel + "；" + sanitized.Reason);
                return Suppressed();
            }
            if (sanitized.Action == WindowPlacementPolicyAction.Clamp)
            {
                placement = sanitized.Placement;
                AppLog.Write("[窗口恢复] 恢复前体检：" + sanitized.Reason + "；" + restoreLabel
                    + " 保存=(" + originalPlacement.Left + "," + originalPlacement.Top + " "
                    + originalPlacement.Width + "x" + originalPlacement.Height + ")"
                    + " → (" + placement.Left + "," + placement.Top + " "
                    + placement.Width + "x" + placement.Height + ")");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.UseShellExecute = true;
            startInfo.WindowStyle = ProcessWindowStyle.Normal;
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                startInfo.Arguments = arguments;
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            {
                startInfo.WorkingDirectory = workingDirectory;
            }

            System.Collections.Generic.ISet<IntPtr> excludedWindows = existingWindows;
            if (excludedWindows == null && placement != null && placement.HasBounds)
            {
                excludedWindows = WindowPlacementService.CaptureExistingWindows(fileName);
            }
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                bool schedulePlacement = placement != null
                    && (placement.HasBounds
                        || string.Equals(placement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase));
                if (schedulePlacement)
                {
                    WindowPlacementService.Schedule(
                        process.Id,
                        fileName,
                        windowTitle,
                        windowIdentityPath,
                        placement,
                        excludedWindows,
                        browserWindow,
                        browserWindowTitles,
                        restoreKey,
                        taskbarAppId);
                }
                process.Dispose();
            }

            return new LaunchResult { Success = true };
        }

        private static LaunchResult Suppressed()
        {
            return new LaunchResult
            {
                Success = true,
                WasSuppressed = true,
                Error = null
            };
        }

        private static LaunchResult AlreadyOpen()
        {
            return new LaunchResult
            {
                Success = true,
                WasSuppressed = false,
                WasAlreadyOpen = true,
                Error = null
            };
        }

        /// <summary>
        /// B2 的用户关闭跳过：不是失败，也不重新打开。
        /// 与“本来就开着”分开计数，完成提示才能说清是哪一种跳过。
        /// </summary>
        private static LaunchResult UserClosed()
        {
            return new LaunchResult
            {
                Success = true,
                WasSuppressed = false,
                WasAlreadyOpen = false,
                WasUserClosed = true,
                Error = null
            };
        }

        private bool ShouldSkipAlreadyOpen(SessionItem item)
        {
            if (item == null)
            {
                return false;
            }

            if (item.Kind == SessionItemKind.ExplorerFolder)
            {
                return SkipAlreadyOpenExplorerFolder(item);
            }

            // 用户 2026-10-15：“不单单是网址，后续像图2这种 txt 文件，里面多个打开也是一样。”
            // 文件类条目原来必开新窗口：它没有 ExecutablePath，恢复时不判断“是不是已经开着”。
            // 现在按文件名找到已经打开的窗口，就算是“已经在运行”，恢复会跳过并把它调到前面。
            //
            // 但要注意：恢复过程中“刚刚由我们打开”的文件，它的窗口标题也马上就会带上文件名，
            // 如果不排除掉，同一份方案里保存的两个文件只会打开第一个。
            // 所以只认“本次恢复开始之前就已经开着的”那些。
            if (item.Kind == SessionItemKind.Document)
            {
                if (IsDocumentOpenedBeforeThisRestore(item.TargetPath)
                    && DocumentWindowFinder.FindWindowShowing(item.TargetPath) != IntPtr.Zero)
                {
                    return true;
                }
                return false;
            }

            if (item.Kind == SessionItemKind.Application
                && string.IsNullOrWhiteSpace(item.BrowserGroupKey))
            {
                return IsLaunchableApplicationAlreadyOpen(item);
            }

            return false;
        }

        private static bool SkipAlreadyOpenExplorerFolder(SessionItem item)
        {
            bool isOpen;
            FindOpenExplorerFolderWindow(item, out isOpen);
            if (!isOpen)
            {
                return false;
            }

            // 【2026-10-25 用户要求】用户已经打开的文件夹一律跳过：
            // 不再按方案里的最小化状态去缩它——不管用户把它缩着还是开着，
            // 恢复都只跳过，不碰现有窗口。
            AppLog.Write("[窗口恢复] 文件夹已经打开，按用户要求直接跳过、不缩："
                + (item == null ? string.Empty : item.TargetPath));
            return true;
        }

        private static IntPtr FindOpenExplorerFolderWindow(SessionItem item, out bool isOpen)
        {
            isOpen = false;
            if (item == null || string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return IntPtr.Zero;
            }

            IntPtr byPath = FindExplorerFolderWindowByPath(item.TargetPath, out isOpen);
            if (byPath != IntPtr.Zero)
            {
                return byPath;
            }

            string executable = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe");
            if (!string.IsNullOrWhiteSpace(item.WindowTitle))
            {
                IntPtr byTitle = FindWindowByExecutableAndTitle(executable, item.WindowTitle);
                if (byTitle != IntPtr.Zero)
                {
                    isOpen = true;
                    return byTitle;
                }
            }

            string folderName = null;
            try
            {
                folderName = Path.GetFileName(item.TargetPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
            }
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                IntPtr byName = FindWindowByExecutableAndTitle(executable, folderName);
                if (byName != IntPtr.Zero)
                {
                    isOpen = true;
                    return byName;
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 按文件夹路径找到已经打开的 Explorer 窗口句柄。
        /// 同时返回 isOpen：即使拿不到 HWND，只要 Shell 明确报告这个路径
        /// 已打开，也不能再开第二个窗口。
        /// </summary>
        private static IntPtr FindExplorerFolderWindowByPath(string folderPath, out bool isOpen)
        {
            isOpen = false;
            string target;
            try
            {
                target = Path.GetFullPath(folderPath).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return IntPtr.Zero;
            }

            object shell = null;
            object windows = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return IntPtr.Zero;
                }
                shell = Activator.CreateInstance(shellType);
                windows = InvokeComMember(shell, "Windows", BindingFlags.InvokeMethod, null);
                int count = Convert.ToInt32(InvokeComMember(windows, "Count", BindingFlags.GetProperty, null));
                for (int index = 0; index < count; index++)
                {
                    object window = null;
                    object document = null;
                    object folder = null;
                    object self = null;
                    bool matched = false;
                    IntPtr handle = IntPtr.Zero;
                    try
                    {
                        window = InvokeComMember(windows, "Item", BindingFlags.InvokeMethod, new object[] { index });
                        document = InvokeComMember(window, "Document", BindingFlags.GetProperty, null);
                        folder = InvokeComMember(document, "Folder", BindingFlags.GetProperty, null);
                        self = InvokeComMember(folder, "Self", BindingFlags.GetProperty, null);
                        string path = Convert.ToString(InvokeComMember(self, "Path", BindingFlags.GetProperty, null));
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            string normalized;
                            try
                            {
                                normalized = Path.GetFullPath(path).TrimEnd(
                                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            }
                            catch
                            {
                                normalized = path.TrimEnd(
                                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            }
                            matched = string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase);
                        }
                        if (matched)
                        {
                            try
                            {
                                long value = Convert.ToInt64(
                                    InvokeComMember(window, "HWND", BindingFlags.GetProperty, null),
                                    System.Globalization.CultureInfo.InvariantCulture);
                                if (value != 0)
                                {
                                    handle = new IntPtr(value);
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        ReleaseComMember(self);
                        ReleaseComMember(folder);
                        ReleaseComMember(document);
                        ReleaseComMember(window);
                    }
                    if (matched)
                    {
                        isOpen = true;
                        return handle;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                ReleaseComMember(windows);
                ReleaseComMember(shell);
            }
            return IntPtr.Zero;
        }

        private static object InvokeComMember(object target, string name, BindingFlags flags, object[] arguments)
        {
            return target.GetType().InvokeMember(name, flags, null, target, arguments, null, null, null);
        }

        private static void ReleaseComMember(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); }
                catch { }
            }
        }

        private static bool IsLaunchableApplicationAlreadyOpen(SessionItem item)
        {
            string executable = item == null ? null : item.ExecutablePath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }
            try
            {
                executable = Environment.ExpandEnvironmentVariables(executable.Trim());
            }
            catch
            {
            }

            bool visualStudioCode = string.Equals(
                item.CaptureSource,
                "VisualStudioCodeWindow",
                StringComparison.OrdinalIgnoreCase);
            if (visualStudioCode)
            {
                string workspace = item.TargetPath;
                string workspaceName = null;
                if (!string.IsNullOrWhiteSpace(workspace))
                {
                    try
                    {
                        workspaceName = Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    }
                    catch
                    {
                    }
                }
                if (FindWindowByExecutableAndTitle(executable, item.WindowTitle) != IntPtr.Zero)
                {
                    return true;
                }
                return !string.IsNullOrWhiteSpace(workspaceName)
                    && FindWindowByExecutableAndTitle(executable, workspaceName) != IntPtr.Zero;
            }

            return FindAnyVisibleWindowByExecutable(executable) != IntPtr.Zero
                || IsProcessRunningByPath(executable);
        }

        /// <summary>
        /// 这个路径代表的东西是不是**已经在运行/已经打开**了
        /// （自启动应用用；逻辑与「启动与恢复」同一套判断，只是入口不同）。
        ///  · 文件夹 → 资源管理器里已经打开了这个文件夹；
        ///  · .exe / .lnk / .bat 等 → 按可执行文件名找同名进程；
        ///  · 网址 → 交给浏览器判断（这里不动，浏览器自己会复用标签）。
        /// </summary>
        private static bool IsTargetAlreadyRunning(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    return IsExplorerFolderOpenByPath(path);
                }
                string extension = Path.GetExtension(path);
                if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                {
                    string target = ResolveShortcutTarget(path);
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        if (Directory.Exists(target))
                        {
                            return IsExplorerFolderOpenByPath(target);
                        }
                        return IsProcessRunningByPath(target);
                    }
                    // 解析不出来时退一步：用快捷方式自己的文件名当进程名试试。
                    return IsProcessRunningByPath(path);
                }
                if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    return IsProcessRunningByPath(path);
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 把“已经在运行/已经打开”的目标窗口调到最前（用户第 64 条）。
        ///  · .exe / .bat / .cmd → 找到同名进程的主窗口，ShowWindow(SW_RESTORE) + SetForegroundWindow；
        ///  · .lnk/.url/文件夹 → 先解析出真实目标，再按上面处理；文件夹还会尝试按标题找资源管理器窗口。
        /// 任何失败都忽略：这一步只是“顺手把窗口调出来”，不该影响“已经打开就不重开”这个结论。
        /// </summary>
        private static void ActivateExistingTarget(string path)
        {
            try
            {
                string target = path;
                if (File.Exists(path))
                {
                    string extension = Path.GetExtension(path);
                    if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                    {
                        string resolved = ResolveShortcutTarget(path);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            target = resolved;
                        }
                    }
                    else if (!string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        // 普通文件（txt/doc/xlsx…）：按文件名找已经打开它的那个窗口，
                        // 直接调到前面，而不是去找“记事本的进程”。
                        IntPtr documentWindow = DocumentWindowFinder.FindWindowShowing(target);
                        if (documentWindow != IntPtr.Zero)
                        {
                            ActivateWindow(documentWindow);
                            return;
                        }
                    }
                }
                if (Directory.Exists(target))
                {
                    ActivateExplorerFolderWindow(target);
                    return;
                }
                ActivateProcessWindows(target);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 自启动项目保存为“恢复时最小化”、但目标已经开着时：
        /// 不再把它还原到前台，而是把现有窗口最小化。
        /// </summary>
        private static void MinimizeExistingTarget(string path, string windowTitle)
        {
            try
            {
                string target = path;
                if (File.Exists(path))
                {
                    string extension = Path.GetExtension(path);
                    if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                    {
                        string resolved = ResolveShortcutTarget(path);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            target = resolved;
                        }
                    }
                }

                if (Directory.Exists(target))
                {
                    bool isOpen;
                    IntPtr folderWindow = FindExplorerFolderWindowByPath(target, out isOpen);
                    if (folderWindow != IntPtr.Zero)
                    {
                        WindowPlacementService.MinimizeExistingWindow(folderWindow);
                    }
                    return;
                }

                if (File.Exists(target))
                {
                    string extension = Path.GetExtension(target);
                    bool executable = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
                    if (!executable)
                    {
                        IntPtr documentWindow = DocumentWindowFinder.FindWindowShowing(target);
                        if (documentWindow != IntPtr.Zero)
                        {
                            WindowPlacementService.MinimizeExistingWindow(documentWindow);
                            return;
                        }
                    }
                }

                IntPtr handle = FindWindowByExecutableAndTitle(target, windowTitle);
                if (handle == IntPtr.Zero)
                {
                    handle = FindAnyVisibleWindowByExecutable(target);
                }
                if (handle != IntPtr.Zero)
                {
                    WindowPlacementService.MinimizeExistingWindow(handle);
                }
            }
            catch
            {
            }
        }

        private static void ActivateProcessWindows(string executablePath)
        {
            // 优先用“按窗口枚举 + 可执行文件路径比对”的办法：它不依赖 Process.MainWindowHandle
            // （打包应用如新版记事本，主进程的 MainWindowHandle 往往是 0，真正有窗口的是子进程）。
            IntPtr handle = FindAnyVisibleWindowByExecutable(executablePath);
            if (handle != IntPtr.Zero)
            {
                ActivateWindow(handle);
                return;
            }

            string normalized = NormalizeLaunchPath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }
            string processName;
            try
            {
                processName = Path.GetFileNameWithoutExtension(normalized);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return;
            }
            for (int index = 0; index < processes.Length; index++)
            {
                try
                {
                    IntPtr processHandle = processes[index].MainWindowHandle;
                    if (processHandle != IntPtr.Zero)
                    {
                        ActivateWindow(processHandle);
                        return;   // 激活一个就够了
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>把窗口还原并调到最前（最小化的先 SW_RESTORE，再置顶、再抢前台）。</summary>
        private static void ActivateWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }
            try
            {
                NativeMethods.ShowWindow(handle, NativeMethods.ShowWindowRestore);
                NativeMethods.BringWindowToTop(handle);
                NativeMethods.SetForegroundWindow(handle);
            }
            catch
            {
            }
        }

        private static void ActivateExplorerFolderWindow(string folder)
        {
            string name;
            try
            {
                name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            Process[] explorers;
            try
            {
                explorers = Process.GetProcessesByName("explorer");
            }
            catch
            {
                return;
            }
            for (int index = 0; index < explorers.Length; index++)
            {
                try
                {
                    IntPtr handle = explorers[index].MainWindowHandle;
                    if (handle == IntPtr.Zero)
                    {
                        continue;
                    }
                    string title = explorers[index].MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title)
                        && title.IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        ActivateWindow(handle);
                        return;
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>资源管理器里是不是已经打开了这个文件夹（按窗口标题的文件夹名判断）。</summary>
        private static bool IsExplorerFolderOpenByPath(string folder)
        {
            string name;
            try
            {
                name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            try
            {
                Process[] explorers = Process.GetProcessesByName("explorer");
                for (int index = 0; index < explorers.Length; index++)
                {
                    string title = explorers[index].MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(title)
                        && title.IndexOf(name, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>把 .lnk 解析成它指向的目标路径（解析不了就返回空）。</summary>
        private static string ResolveShortcutTarget(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return null;
                }
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object link = shellType.InvokeMember("CreateShortcut",
                        System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                    if (link == null)
                    {
                        return null;
                    }
                    object target = link.GetType().InvokeMember("TargetPath",
                        System.Reflection.BindingFlags.GetProperty, null, link, null);
                    return target as string;
                }
                finally
                {
                    try
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsProcessRunningByPath(string executablePath)
        {
            string normalized = NormalizeLaunchPath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }
            string processName;
            try
            {
                processName = Path.GetFileNameWithoutExtension(normalized);
            }
            catch
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            // 兜底（用户第 62～64 条）：有些程序“要启动的路径”和“真正在跑的进程路径”不是同一个文件 ——
            // 例如新版记事本：入口在 System32\notepad.exe，实际窗口属于 WindowsApps 下的打包进程。
            // 只比对完整路径会漏判，于是又会“重新打开”。所以只要**有同名进程且有可见窗口**，就算它已经开着。
            try
            {
                Process[] byName = Process.GetProcessesByName(processName);
                for (int index = 0; index < byName.Length; index++)
                {
                    using (Process candidate = byName[index])
                    {
                        try
                        {
                            if (candidate.MainWindowHandle != IntPtr.Zero)
                            {
                                return true;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                for (int index = 0; index < processes.Length; index++)
                {
                    using (Process process = processes[index])
                    {
                        try
                        {
                            if (process.MainModule != null
                                && !string.IsNullOrWhiteSpace(process.MainModule.FileName)
                                && string.Equals(
                                    NormalizeLaunchPath(process.MainModule.FileName),
                                    normalized,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            // Elevated processes may deny MainModule; the
                            // ProcessName already matched, so treat it as
                            // running rather than starting a duplicate.
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static IntPtr FindAnyVisibleWindowByExecutable(string executablePath)
        {
            string normalized = NormalizeLaunchPath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return IntPtr.Zero;
            }

            IntPtr found = IntPtr.Zero;
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
                    found = windowHandle;
                    return false;
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        private static IntPtr FindWindowByExecutableAndTitle(string executablePath, string title)
        {
            IntPtr first = FindAnyVisibleWindowByExecutable(executablePath);
            if (first == IntPtr.Zero || string.IsNullOrWhiteSpace(title))
            {
                return IntPtr.Zero;
            }

            string normalized = NormalizeLaunchPath(executablePath);
            string fragment = title.Trim();
            IntPtr matched = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }
                if (!ProcessExecutableMatches(windowHandle, normalized))
                {
                    return true;
                }
                string current = ReadNativeTitle(windowHandle);
                if (!string.IsNullOrWhiteSpace(current)
                    && current.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matched = windowHandle;
                    return false;
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return matched;
        }

        private static bool ProcessExecutableMatches(IntPtr windowHandle, string expectedExecutable)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return false;
            }
            string actual = GetProcessExecutablePath((int)processId);
            return !string.IsNullOrWhiteSpace(actual)
                && string.Equals(
                    NormalizeLaunchPath(actual),
                    expectedExecutable,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string GetProcessExecutablePath(int processId)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                false,
                (uint)processId);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    System.Text.StringBuilder path = new System.Text.StringBuilder(32768);
                    int pathLength = path.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref pathLength))
                    {
                        return path.ToString();
                    }
                }
                catch
                {
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.MainModule != null)
                    {
                        return process.MainModule.FileName;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ReadNativeTitle(IntPtr windowHandle)
        {
            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return null;
                }
                System.Text.StringBuilder value = new System.Text.StringBuilder(length + 1);
                NativeMethods.GetWindowText(windowHandle, value, value.Capacity);
                string text = value.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeLaunchPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static LaunchResult Failure(string error)
        {
            return new LaunchResult
            {
                Success = false,
                WasSuppressed = false,
                Error = error
            };
        }

        private static string GetErrorMessage(Exception exception)
        {
            Win32Exception win32 = exception as Win32Exception;
            if (win32 != null && !string.IsNullOrWhiteSpace(win32.Message))
            {
                return win32.Message;
            }

            return exception == null ? "Unable to start the target." : exception.Message;
        }

        private static bool ExternalEffectsDisabled()
        {
            return IsTruthy("WORKSPACE_RESTORER_TEST_MODE")
                || IsTruthy("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS");
        }

        private static bool IsTruthy(string variableName)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
