using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed partial class SessionCaptureService : ISessionCaptureService
    {
        private static readonly object RestoredMinimizedWindowsLock = new object();
        private static readonly List<IntPtr> RestoredMinimizedWindows = new List<IntPtr>();

        private static readonly HashSet<string> SystemProcessNames = new HashSet<string>(
            new[]
            {
                "applicationframehost",
                "ctfmon",
                "dwm",
                "explorer",
                "lockapp",
                "searchapp",
                "searchhost",
                "securityhealthsystray",
                "shellexperiencehost",
                "sihost",
                "startmenuexperiencehost",
                "textinputhost"
            },
            StringComparer.OrdinalIgnoreCase);

        public SessionSnapshot Capture()
        {
            return Capture(new SessionCaptureOptions());
        }

        public SessionSnapshot Capture(SessionCaptureOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            // Tests and explicit side-effect guards stay in-process.  Normal
            // application captures run in a helper copy of this EXE so a hung
            // browser or Explorer automation provider can be terminated.
            if (!ExternalEffectsDisabled())
            {
                return SessionCaptureProcess.Capture(options);
            }

            return CaptureInCurrentProcess(options);
        }

        internal SessionSnapshot CaptureInCurrentProcess(SessionCaptureOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            SessionSnapshot snapshot = new SessionSnapshot();
            snapshot.ComputerName = Environment.MachineName;

            bool browserCaptureIncomplete;
            List<string> unreadSummaries;
            BrowserCaptureTotals browserTotals;
            List<SessionItem> items = CaptureApplications(
                options,
                out browserCaptureIncomplete,
                out unreadSummaries,
                out browserTotals);
            snapshot.BrowserCaptureIncomplete = browserCaptureIncomplete;
            snapshot.UnreadBrowserSummaries = unreadSummaries == null || unreadSummaries.Count == 0
                ? null
                : unreadSummaries;
            snapshot.UnreadBrowserWindows = browserTotals.UnreadWindows.Count == 0
                ? null
                : browserTotals.UnreadWindows;
            snapshot.IgnoredBlankTabCount = browserTotals.IgnoredBlankTabs;
            snapshot.BrowserCaptureTimedOut = false;
            if (browserCaptureIncomplete)
            {
                // 文案统一在这里生成：横幅、托盘、状态栏共用一份，
                // 不再写“浏览器窗口最小化时只能读到当前那一页”这种一刀切的话。
                snapshot.CaptureWarning = BrowserCaptureWarningText.Build(snapshot);
            }
            if (options.IncludeExplorerFolders)
            {
                items.AddRange(CaptureExplorerFolders());
            }

            // The first pass cannot know how many windows of one browser are
            // present until every process has been inspected.  Normalize the
            // parent labels after capture so a single Edge window is shown as
            // "Microsoft Edge", while multiple windows remain distinguishable
            // as "Microsoft Edge (1)", "Microsoft Edge (2)", and so on.
            NormalizeBrowserWindowDisplayNames(items);

            List<SessionItem> ordered = items
                .OrderBy(item => item.StartTimeUtc)
                .ThenBy(item => item.Kind)
                .ThenBy(item => item.ApplicationWindowIndex >= 0
                    ? item.ApplicationWindowIndex
                    : int.MaxValue)
                .ThenBy(item => item.Kind == SessionItemKind.WebPage
                    ? (item.BrowserGroupKey ?? string.Empty)
                    : string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.BrowserWindowIndex >= 0
                    ? item.BrowserWindowIndex
                    : int.MaxValue)
                .ThenBy(item => item.Kind == SessionItemKind.WebPage && item.BrowserTabIndex >= 0
                    ? item.BrowserTabIndex
                    : int.MaxValue)
                .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            for (int index = 0; index < ordered.Count; index++)
            {
                ordered[index].Sequence = index + 1;
            }

            snapshot.Items = ordered;
            return snapshot;
        }

        private static List<SessionItem> CaptureApplications(
            SessionCaptureOptions options,
            out bool browserCaptureIncomplete,
            out List<string> unreadSummaries,
            out BrowserCaptureTotals browserTotals)
        {
            browserCaptureIncomplete = false;
            unreadSummaries = new List<string>();
            browserTotals = new BrowserCaptureTotals();
            List<WindowCandidate> candidates = EnumerateVisibleWindows();
            List<SessionItem> result = new List<SessionItem>();
            int currentProcessId = Process.GetCurrentProcess().Id;
            int currentSessionId = Process.GetCurrentProcess().SessionId;
            string currentExecutablePath = TryGetCurrentExecutablePath();
            HashSet<string> excludedNames = BuildExcludedProcessNames(options);
            HashSet<string> excludedPaths = BuildExcludedPaths(options);
            // Electron applications can expose one top-level window per
            // process (or several windows from one process). Keep workspace
            // assignment state outside the process loop so a second Code
            // process cannot be given the same storage path and later be
            // merged away by the controller. Cache separately per executable
            // so Code and Code Insiders workspaces are never mixed.
            Dictionary<string, List<string>> knownCodePathsByExecutable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> assignedCodePathsByExecutable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> nextBrowserWindowIndexByGroup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int visualStudioCodeWindowSequence = 0;

            foreach (IGrouping<int, WindowCandidate> processWindows in candidates.GroupBy(delegate(WindowCandidate item) { return item.ProcessId; }))
            {
                WindowCandidate candidate = processWindows
                    .OrderByDescending(delegate(WindowCandidate item) { return item.IsForeground; })
                    .First();
                if (candidate.ProcessId == currentProcessId)
                {
                    continue;
                }

                try
                {
                    using (Process process = Process.GetProcessById(candidate.ProcessId))
                    {
                        if (process.SessionId != currentSessionId)
                        {
                            continue;
                        }

                        string processName = process.ProcessName;
                        if (excludedNames.Contains(processName) || SystemProcessNames.Contains(processName))
                        {
                            continue;
                        }

                        string executablePath;
                        string restartArguments;
                        if (!TryReadLaunchInformation(process, out executablePath, out restartArguments))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(executablePath)
                            || !File.Exists(executablePath)
                            || excludedPaths.Contains(PathText.Normalize(executablePath))
                            || PathsEqual(executablePath, currentExecutablePath))
                        {
                            continue;
                        }

                        DateTime startTimeUtc;
                        try
                        {
                            startTimeUtc = process.StartTime.ToUniversalTime();
                        }
                        catch
                        {
                            startTimeUtc = DateTime.UtcNow;
                        }

                        // VS Code is an Electron multi-window application. A
                        // single process can own several visible top-level
                        // windows, so grouping only by process would silently
                        // drop all but the first workspace. Capture each
                        // visible window separately and keep only a probable
                        // workspace path plus its native window placement.
                        if (IsVisualStudioCode(executablePath, processName))
                        {
                            string codeExecutableKey = PathText.Normalize(executablePath);
                            List<string> knownCodePaths;
                            if (!knownCodePathsByExecutable.TryGetValue(codeExecutableKey, out knownCodePaths))
                            {
                                knownCodePaths = ReadVisualStudioCodeWorkspacePaths(executablePath);
                                knownCodePathsByExecutable[codeExecutableKey] = knownCodePaths;
                            }
                            HashSet<string> assignedCodePaths;
                            if (!assignedCodePathsByExecutable.TryGetValue(codeExecutableKey, out assignedCodePaths))
                            {
                                assignedCodePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                assignedCodePathsByExecutable[codeExecutableKey] = assignedCodePaths;
                            }
                            List<WindowCandidate> codeWindows = processWindows
                                .OrderByDescending(delegate(WindowCandidate item) { return item.IsForeground; })
                                .ThenBy(delegate(WindowCandidate item) { return item.WindowTitle ?? string.Empty; }, StringComparer.CurrentCultureIgnoreCase)
                                .ToList();
                            for (int windowIndex = 0; windowIndex < codeWindows.Count; windowIndex++)
                            {
                                WindowCandidate codeWindow = codeWindows[windowIndex];
                                int globalWindowIndex = visualStudioCodeWindowSequence++;
                                string workspacePath = TryGetVisualStudioCodePath(
                                    codeWindow.WindowTitle,
                                    knownCodePaths,
                                    assignedCodePaths);
                                if (!string.IsNullOrWhiteSpace(workspacePath))
                                {
                                    assignedCodePaths.Add(workspacePath);
                                }
                                result.Add(new SessionItem
                                {
                                    Kind = SessionItemKind.Application,
                                    DisplayName = "Visual Studio Code",
                                    WindowTitle = codeWindow.WindowTitle,
                                    ExecutablePath = executablePath,
                                    Arguments = BuildVisualStudioCodeArguments(workspacePath),
                                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                                    TargetPath = workspacePath,
                                    CapturedProcessId = codeWindow.ProcessId,
                                    StartTimeUtc = startTimeUtc,
                                    // Use a capture-wide index.  VS Code may
                                    // expose separate visible windows through
                                    // separate Electron processes; a
                                    // per-process index would make every
                                    // process start at zero and the controller
                                    // would merge the windows together.
                                    ApplicationWindowIndex = globalWindowIndex,
                                    WindowPlacement = CaptureWindowPlacement(codeWindow.WindowHandle),
                                    CaptureSource = "VisualStudioCodeWindow"
                                });
                            }
                            continue;
                        }

                        if (BrowserCatalog.IsSupportedBrowser(executablePath, processName))
                        {
                            CaptureBrowserWindowGroup(
                                processWindows,
                                executablePath,
                                processName,
                                nextBrowserWindowIndexByGroup,
                                startTimeUtc,
                                options,
                                result,
                                ref browserCaptureIncomplete,
                                ref unreadSummaries,
                                browserTotals);
                            continue;
                        }

                        result.Add(new SessionItem
                        {
                            Kind = SessionItemKind.Application,
                            DisplayName = GetDisplayName(executablePath, processName),
                            WindowTitle = candidate.WindowTitle,
                            ExecutablePath = executablePath,
                            Arguments = restartArguments,
                            WorkingDirectory = Path.GetDirectoryName(executablePath),
                            CapturedProcessId = candidate.ProcessId,
                            StartTimeUtc = startTimeUtc,
                            WindowPlacement = CaptureWindowPlacement(candidate.WindowHandle),
                            CaptureSource = string.IsNullOrWhiteSpace(restartArguments)
                                ? "VisibleWindow"
                                : "VisibleWindow+ApplicationRestart"
                        });
                    }
                }
                catch
                {
                    // Processes can exit or become inaccessible while the window list is inspected.
                }
            }

            return result;
        }

        private static string BuildBrowserWindowDisplayName(string browserName, int browserWindowIndex)
        {
            string name = StripBrowserWindowOrdinal(
                string.IsNullOrWhiteSpace(browserName) ? "浏览器" : browserName.Trim());
            return name + " (" + (browserWindowIndex + 1) + ")";
        }

        private static string StripBrowserWindowOrdinal(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string result = value.Trim();
            Match match;
            try
            {
                match = Regex.Match(result, "\\s+\\(\\d+\\)$", RegexOptions.CultureInvariant);
            }
            catch
            {
                return result;
            }

            return match.Success
                ? result.Substring(0, match.Index).Trim()
                : result;
        }

        internal static void NormalizeBrowserWindowDisplayNames(IList<SessionItem> items)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }

            Dictionary<string, int> windowCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<int>> reservedDisplayOrdinals = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item == null
                    || item.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                {
                    continue;
                }

                string group = item.BrowserGroupKey.Trim();
                int count;
                if (!windowCounts.TryGetValue(group, out count))
                {
                    count = 0;
                }
                windowCounts[group] = count + 1;
                if (item.BrowserWindowIndex >= 0)
                {
                    HashSet<int> ordinals;
                    if (!reservedDisplayOrdinals.TryGetValue(group, out ordinals))
                    {
                        ordinals = new HashSet<int>();
                        reservedDisplayOrdinals[group] = ordinals;
                    }
                    // Keep the persisted zero-based identity untouched; the
                    // display ordinal is one-based and exists only for the
                    // human-readable parent label.
                    ordinals.Add(item.BrowserWindowIndex == int.MaxValue
                        ? int.MaxValue
                        : item.BrowserWindowIndex + 1);
                }
            }

            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item == null
                    || item.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                {
                    continue;
                }

                int count;
                if (!windowCounts.TryGetValue(item.BrowserGroupKey.Trim(), out count))
                {
                    count = 1;
                }

                string browserName = StripBrowserWindowOrdinal(
                    string.IsNullOrWhiteSpace(item.BrowserName)
                        ? item.DisplayName
                        : item.BrowserName);
                if (count <= 1)
                {
                    item.DisplayName = string.IsNullOrWhiteSpace(browserName)
                        ? "浏览器"
                        : browserName.Trim();
                }
                else
                {
                    int displayOrdinal;
                    if (item.BrowserWindowIndex >= 0)
                    {
                        displayOrdinal = item.BrowserWindowIndex == int.MaxValue
                            ? int.MaxValue
                            : item.BrowserWindowIndex + 1;
                    }
                    else
                    {
                        HashSet<int> ordinals;
                        if (!reservedDisplayOrdinals.TryGetValue(item.BrowserGroupKey.Trim(), out ordinals))
                        {
                            ordinals = new HashSet<int>();
                            reservedDisplayOrdinals[item.BrowserGroupKey.Trim()] = ordinals;
                        }
                        displayOrdinal = 1;
                        while (ordinals.Contains(displayOrdinal) && displayOrdinal < int.MaxValue)
                        {
                            displayOrdinal++;
                        }
                        ordinals.Add(displayOrdinal);
                    }
                    item.DisplayName = StripBrowserWindowOrdinal(
                        string.IsNullOrWhiteSpace(browserName) ? "浏览器" : browserName)
                        + " (" + displayOrdinal + ")";
                }
            }
        }

        private static string GetBrowserGroupKey(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return "browser";
            }

            return PathText.Normalize(executablePath);
        }

        private static bool IsVisualStudioCode(string executablePath, string processName)
        {
            string name = processName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(executablePath);
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            switch (name.Trim().ToLowerInvariant())
            {
                case "code":
                case "code-insiders":
                case "code - insiders":
                case "codium":
                case "vscodium":
                    return true;
                default:
                    return false;
            }
        }

        private static string TryGetVisualStudioCodePath(
            string windowTitle,
            IList<string> knownPaths,
            ISet<string> assignedPaths)
        {
            string value = string.IsNullOrWhiteSpace(windowTitle) ? string.Empty : windowTitle.Trim();
            string[] suffixes = new[]
            {
                " - Visual Studio Code - Insiders",
                " - Visual Studio Code",
                " - Insiders",
                " - VSCodium"
            };
            for (int index = 0; index < suffixes.Length; index++)
            {
                if (value.EndsWith(suffixes[index], StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(0, value.Length - suffixes[index].Length).Trim();
                    break;
                }
            }

            // When VS Code is opened directly on a folder, the title before
            // the product suffix is the full path. Check it before splitting
            // on the conventional " - " title separator because a real path
            // may itself contain that sequence.
            string direct = TrimCodeTitleDecorations(value);
            string directPath = TryConvertFileUriToPath(direct);
            if (LooksLikeExistingPath(directPath))
            {
                return PathText.Normalize(directPath);
            }

            string[] pieces = value.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = pieces.Length - 1; index >= 0; index--)
            {
                string candidate = TrimCodeTitleDecorations(pieces[index]);
                string candidatePath = TryConvertFileUriToPath(candidate);
                if (LooksLikeExistingPath(candidatePath))
                {
                    return PathText.Normalize(candidatePath);
                }
            }

            // VS Code keeps the last set of opened windows in storage.json.
            // The file can be locked while Code is running, so this is a
            // best-effort fallback used only when the accessible title does
            // not carry an absolute path.
            if (knownPaths != null && knownPaths.Count > 0)
            {
                string titleLower = value.ToLowerInvariant();
                for (int index = 0; index < knownPaths.Count; index++)
                {
                    string path = knownPaths[index];
                    if (string.IsNullOrWhiteSpace(path)
                        || (assignedPaths != null && assignedPaths.Contains(path)))
                    {
                        continue;
                    }
                    string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrWhiteSpace(name)
                        && titleLower.IndexOf(name.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }
                }

                // A single stored workspace and a single unassigned window is
                // also safe to associate. When there are several stored
                // paths, do not guess by array order: storage.json can retain
                // recently closed windows and that would save a path that is
                // not actually open in the visible Code window.
                if (knownPaths.Count == 1
                    && (assignedPaths == null || assignedPaths.Count == 0))
                {
                    return knownPaths[0];
                }
            }

            return null;
        }

        private static string TryConvertFileUriToPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            try
            {
                Uri uri;
                if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile)
                {
                    return uri.LocalPath;
                }
            }
            catch
            {
            }
            return value;
        }

        private static List<string> ReadVisualStudioCodeWorkspacePaths(string executablePath)
        {
            List<string> result = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string product = Path.GetFileNameWithoutExtension(executablePath ?? string.Empty);
            string[] productFolders;
            if (string.Equals(product, "code-insiders", StringComparison.OrdinalIgnoreCase)
                || string.Equals(product, "code - insiders", StringComparison.OrdinalIgnoreCase))
            {
                productFolders = new[] { "Code - Insiders" };
            }
            else if (string.Equals(product, "codium", StringComparison.OrdinalIgnoreCase)
                || string.Equals(product, "vscodium", StringComparison.OrdinalIgnoreCase))
            {
                productFolders = new[] { "VSCodium" };
            }
            else
            {
                productFolders = new[] { "Code" };
            }

            // VS Code has used both %APPDATA%\\<product>\\storage.json and
            // %APPDATA%\\<product>\\User\\globalStorage\\storage.json for
            // window state across releases. Read the top-level file first,
            // then the global store as a compatibility fallback. The product
            // matching the executable is preferred so Code and Code Insiders
            // workspaces do not get mixed together.
            List<string> files = new List<string>();
            for (int folderIndex = 0; folderIndex < productFolders.Length; folderIndex++)
            {
                string folder = productFolders[folderIndex];
                files.Add(Path.Combine(roaming, folder, "storage.json"));
                files.Add(Path.Combine(roaming, folder, "User", "globalStorage", "storage.json"));
            }

            for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
            {
                string file = files[fileIndex];
                if (!File.Exists(file))
                {
                    continue;
                }

                string text = TryReadTextFile(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                AddJsonPathValues(result, text, "folderUri");
                AddJsonPathValues(result, text, "workspaceUri");
                AddJsonPathValues(result, text, "configPath");
                AddJsonPathValues(result, text, "folder");
                AddJsonPathValues(result, text, "workspace");
                if (result.Count > 0)
                {
                    break;
                }
            }
            return result;
        }

        private static void AddJsonPathValues(IList<string> destination, string json, string propertyName)
        {
            if (destination == null || string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            string pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\\"(?<value>(?:\\\\.|[^\\\"\\\\])*)\\\"";
            MatchCollection matches;
            try
            {
                matches = Regex.Matches(json, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return;
            }

            for (int index = 0; index < matches.Count; index++)
            {
                string encoded = matches[index].Groups["value"].Value;
                string value = UnescapeJsonString(encoded);
                string path = TryConvertFileUriToPath(value);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                path = PathText.Normalize(path);
                if (!destination.Any(delegate(string existing)
                {
                    return string.Equals(existing, path, StringComparison.OrdinalIgnoreCase);
                }))
                {
                    destination.Add(path);
                }
            }
        }

        private static string TryReadTextFile(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string UnescapeJsonString(string value)
        {
            if (value == null)
            {
                return null;
            }
            return value.Replace("\\/", "/")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        private static string TrimCodeTitleDecorations(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Trim().Trim('[', ']', '*');
        }

        private static bool LooksLikeExistingPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string candidate = Environment.ExpandEnvironmentVariables(value.Trim());
            if (!Path.IsPathRooted(candidate))
            {
                return false;
            }

            try
            {
                return File.Exists(candidate) || Directory.Exists(candidate);
            }
            catch
            {
                return false;
            }
        }

        private static string BuildVisualStudioCodeArguments(string workspacePath)
        {
            return string.IsNullOrWhiteSpace(workspacePath)
                ? "--new-window"
                : "--new-window " + QuoteArgument(workspacePath);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static SavedWindowPlacement CaptureWindowPlacement(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return null;
            }

            NativeMethods.NativeRect rectangle;
            if (!NativeMethods.GetWindowRect(windowHandle, out rectangle))
            {
                return null;
            }

            bool isMaximized = NativeMethods.IsZoomed(windowHandle);
            bool isMinimized = NativeMethods.IsIconic(windowHandle);

            // GetWindowRect reports the minimized icon rectangle for an
            // iconic window.  Use the stored normal rectangle's position and
            // size so a shutdown capture keeps the user's window bounds.
            NativeMethods.WindowPlacement nativePlacement = NativeMethods.WindowPlacement.Create();
            if ((isMinimized || isMaximized)
                && NativeMethods.GetWindowPlacement(windowHandle, ref nativePlacement))
            {
                rectangle.Left = nativePlacement.NormalPosition.Left;
                rectangle.Top = nativePlacement.NormalPosition.Top;
                rectangle.Right = nativePlacement.NormalPosition.Right;
                rectangle.Bottom = nativePlacement.NormalPosition.Bottom;
            }

            SavedWindowPlacement placement = new SavedWindowPlacement
            {
                HasBounds = true,
                Left = rectangle.Left,
                Top = rectangle.Top,
                Width = rectangle.Right - rectangle.Left,
                Height = rectangle.Bottom - rectangle.Top,
                WindowState = isMaximized
                    ? "Maximized"
                    : (isMinimized ? "Minimized" : "Normal")
            };
            // 【2026-10-18 用户要求】记下“这个窗口在第几块屏 + 当时一共几块屏”，
            // 恢复时才能在“三屏变单屏 / 单屏变双屏 / 排列换了”的情况下放对地方。
            WindowPlacementService.FillMonitorInfo(placement);
            return placement;
        }

        /// <summary>
        /// Captures every tab exposed by a visible Chromium/Edge window.  The
        /// Chromium accessibility tree normally exposes tab titles and a
        /// SelectionItem pattern, but it does not expose a URL for inactive
        /// tabs.  Selecting each tab briefly lets the address bar provider
        /// publish the exact URL.  The originally selected tab is restored in
        /// the finally block so an explicit save does not leave the browser on
        /// a different page.  If a provider does not expose selection, the
        /// caller falls back to the single active address-bar capture.
        /// </summary>
        private static List<BrowserPageCapture> TryReadCdpPagesForBrowser(
            string browserGroupKey,
            string windowTitle,
            Dictionary<string, List<BrowserDebugProtocol.BrowserTabEntry>> cacheByGroup,
            HashSet<string> assignedUrls)
        {
            if (string.IsNullOrWhiteSpace(browserGroupKey) || cacheByGroup == null || assignedUrls == null)
            {
                return null;
            }

            List<BrowserDebugProtocol.BrowserTabEntry> tabs;
            if (!cacheByGroup.TryGetValue(browserGroupKey, out tabs))
            {
                tabs = BrowserDebugProtocol.TryReadPageTabs(BrowserDebugProtocol.DefaultDebugPort);
                cacheByGroup[browserGroupKey] = tabs ?? new List<BrowserDebugProtocol.BrowserTabEntry>();
            }
            if (tabs == null || tabs.Count == 0)
            {
                // No debug endpoint reachable for this browser.
                return null;
            }
            if (!CdpListMatchesWindow(tabs, windowTitle))
            {
                // This window is not part of the listed browser; let the UI
                // Automation read handle it.
                return null;
            }

            List<BrowserPageCapture> pages = new List<BrowserPageCapture>();
            for (int index = 0; index < tabs.Count; index++)
            {
                BrowserDebugProtocol.BrowserTabEntry tab = tabs[index];
                if (tab == null || string.IsNullOrWhiteSpace(tab.Url))
                {
                    continue;
                }
                string url = tab.Url.Trim();
                if (assignedUrls.Contains(url))
                {
                    continue;
                }
                assignedUrls.Add(url);
                pages.Add(new BrowserPageCapture
                {
                    Url = url,
                    Title = string.IsNullOrWhiteSpace(tab.Title) ? windowTitle : tab.Title,
                    TabIndex = pages.Count,
                    IsActive = pages.Count == 0,
                    CaptureSource = "BrowserDebugProtocol"
                });
            }
            return pages;
        }

        private static List<WindowCandidate> EnumerateVisibleWindows()
        {
            List<WindowCandidate> result = new List<WindowCandidate>();
            IntPtr shellWindow = NativeMethods.GetShellWindow();
            IntPtr desktopWindow = NativeMethods.GetDesktopWindow();
            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();

            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (windowHandle == shellWindow
                    || windowHandle == desktopWindow
                    || !NativeMethods.IsWindowVisible(windowHandle)
                    || IsCloaked(windowHandle))
                {
                    return true;
                }

                int titleLength = NativeMethods.GetWindowTextLength(windowHandle);
                if (titleLength <= 0)
                {
                    return true;
                }

                StringBuilder title = new StringBuilder(titleLength + 1);
                if (NativeMethods.GetWindowText(windowHandle, title, title.Capacity) <= 0
                    || string.IsNullOrWhiteSpace(title.ToString()))
                {
                    return true;
                }

                // 夸克这类“关闭到托盘”的窗口会把窗口挪到 -32000,-32000，
                // 但 IsIconic=false、IsWindowVisible=true，看起来和普通窗口一样。
                // 如果把它当正常窗口保存，恢复时会被拉到屏幕左缘。
                // 真正最小化的窗口（IsIconic=true）不走这里，仍按 NormalPosition 保存。
                if (WindowVisibility.IsHiddenToTrayWindow(windowHandle))
                {
                    AppLog.Write("[采集] 跳过隐藏到屏幕外的窗口（-32000）："
                        + title.ToString().Trim());
                    return true;
                }

                uint processId;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
                if (processId == 0 || processId > int.MaxValue)
                {
                    return true;
                }

                int id = (int)processId;
                result.Add(new WindowCandidate
                {
                    ProcessId = id,
                    WindowTitle = title.ToString().Trim(),
                    WindowHandle = windowHandle,
                    IsForeground = windowHandle == foregroundWindow
                });

                return true;
            };

            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return result;
        }

        private static bool IsCloaked(IntPtr windowHandle)
        {
            try
            {
                int cloaked;
                int result = NativeMethods.DwmGetWindowAttribute(
                    windowHandle,
                    NativeMethods.DwmwaCloaked,
                    out cloaked,
                    sizeof(int));
                return result == 0 && cloaked != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// 用户 2026-10-15 要求：最小化的浏览器也必须能被读到、并且不能被丢掉。
        ///
        /// Chromium 在窗口最小化时不发布标签栏和地址栏的无障碍树（实测 TabItem 数
        /// 为 0），所以只能把它显示出来再读。这里用 SW_SHOWNOACTIVATE 只显示不激
        /// 活，然后用 SetWindowPos 把它压到最底层，保证它不会盖住用户正在看的窗口、
        /// 也不会抢走焦点。调用方读完必须调用
        /// <see cref="ReMinimizeBrowserWindowAfterPageRead"/> 把它最小化回去。
        /// </summary>
        private static bool TryReadLaunchInformation(
            Process process,
            out string executablePath,
            out string restartArguments)
        {
            executablePath = null;
            restartArguments = null;
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                false,
                (uint)process.Id);

            if (handle != IntPtr.Zero)
            {
                try
                {
                    StringBuilder path = new StringBuilder(32768);
                    int pathLength = path.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref pathLength))
                    {
                        executablePath = path.ToString();
                    }

                    StringBuilder arguments = new StringBuilder(32768);
                    uint argumentLength = (uint)arguments.Capacity;
                    uint flags;
                    if (NativeMethods.GetApplicationRestartSettings(
                        handle,
                        arguments,
                        ref argumentLength,
                        out flags) == 0)
                    {
                        restartArguments = NullIfWhiteSpace(arguments.ToString());
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // GetApplicationRestartSettings is best effort on supported Windows versions.
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    executablePath = process.MainModule.FileName;
                }
                catch
                {
                    return false;
                }
            }

            return !string.IsNullOrWhiteSpace(executablePath);
        }

        private static List<SessionItem> CaptureExplorerFolders()
        {
            List<SessionItem> result = new List<SessionItem>();
            object shell = null;
            object windows = null;
            DateTime explorerStartTime = GetExplorerStartTimeUtc();
            int folderWindowIndex = 0;

            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return result;
                }

                shell = Activator.CreateInstance(shellType);
                windows = InvokeMember(shell, "Windows", BindingFlags.InvokeMethod, null);
                int count = Convert.ToInt32(InvokeMember(windows, "Count", BindingFlags.GetProperty, null));

                for (int index = 0; index < count; index++)
                {
                    object window = null;
                    object document = null;
                    object folder = null;
                    object self = null;

                    try
                    {
                        window = InvokeMember(windows, "Item", BindingFlags.InvokeMethod, new object[] { index });
                        string fullName = Convert.ToString(
                            InvokeMember(window, "FullName", BindingFlags.GetProperty, null));
                        if (!string.Equals(
                            Path.GetFileNameWithoutExtension(fullName),
                            "explorer",
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string folderPath = null;
                        try
                        {
                            document = InvokeMember(window, "Document", BindingFlags.GetProperty, null);
                            folder = InvokeMember(document, "Folder", BindingFlags.GetProperty, null);
                            self = InvokeMember(folder, "Self", BindingFlags.GetProperty, null);
                            folderPath = Convert.ToString(
                                InvokeMember(self, "Path", BindingFlags.GetProperty, null));
                        }
                        catch
                        {
                            folderPath = TryConvertFileUrl(Convert.ToString(
                                InvokeMember(window, "LocationURL", BindingFlags.GetProperty, null)));
                        }

                        if (string.IsNullOrWhiteSpace(folderPath)
                            || !Directory.Exists(folderPath))
                        {
                            continue;
                        }

                        string explorerWindowTitle = null;
                        try
                        {
                            explorerWindowTitle = Convert.ToString(
                                InvokeMember(window, "LocationName", BindingFlags.GetProperty, null));
                        }
                        catch
                        {
                        }

                        int currentFolderWindowIndex = folderWindowIndex++;
                        string explorerExecutable = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe");
                        SavedWindowPlacement explorerPlacement = CaptureExplorerWindowPlacement(window);
                        SessionItem explorerParent = new SessionItem
                        {
                            Kind = SessionItemKind.Application,
                            DisplayName = "文件资源管理器 (" + (currentFolderWindowIndex + 1) + ")",
                            TargetPath = null,
                            ExecutablePath = explorerExecutable,
                            StartTimeUtc = explorerStartTime,
                            WindowTitle = explorerWindowTitle,
                            ApplicationWindowIndex = currentFolderWindowIndex,
                            WindowPlacement = explorerPlacement,
                            CaptureSource = "ExplorerWindow"
                        };
                        result.Add(explorerParent);
                        result.Add(new SessionItem
                        {
                            Kind = SessionItemKind.ExplorerFolder,
                            DisplayName = GetFolderDisplayName(folderPath),
                            TargetPath = folderPath,
                            ExecutablePath = explorerExecutable,
                            StartTimeUtc = explorerStartTime,
                            WindowTitle = explorerWindowTitle,
                            ParentId = explorerParent.Id,
                            ApplicationWindowIndex = currentFolderWindowIndex,
                            WindowPlacement = explorerPlacement,
                            CaptureSource = "ExplorerFolderEntry"
                        });
                    }
                    catch
                    {
                        // A Shell window can disappear while COM is enumerating it.
                    }
                    finally
                    {
                        ReleaseComObject(self);
                        ReleaseComObject(folder);
                        ReleaseComObject(document);
                        ReleaseComObject(window);
                    }
                }
            }
            catch
            {
                // Explorer COM automation may be unavailable during sign-out or shell restart.
            }
            finally
            {
                ReleaseComObject(windows);
                ReleaseComObject(shell);
            }

            return result;
        }

        private static SavedWindowPlacement CaptureExplorerWindowPlacement(object shellWindow)
        {
            if (shellWindow == null)
            {
                return null;
            }

            try
            {
                object value = InvokeMember(shellWindow, "HWND", BindingFlags.GetProperty, null);
                long handleValue = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
                return handleValue == 0
                    ? null
                    : CaptureWindowPlacement(new IntPtr(handleValue));
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeMember(object target, string name, BindingFlags flags, object[] arguments)
        {
            if (target == null)
            {
                throw new InvalidOperationException("COM automation object is unavailable.");
            }

            return target.GetType().InvokeMember(
                name,
                flags,
                null,
                target,
                arguments,
                null,
                null,
                null);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.FinalReleaseComObject(value);
                }
                catch
                {
                }
            }
        }

        private static string TryConvertFileUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.IsFile)
            {
                return uri.LocalPath;
            }

            return null;
        }

        private static DateTime GetExplorerStartTimeUtc()
        {
            DateTime earliest = DateTime.UtcNow;
            try
            {
                int currentSession = Process.GetCurrentProcess().SessionId;
                foreach (Process process in Process.GetProcessesByName("explorer"))
                {
                    using (process)
                    {
                        try
                        {
                            if (process.SessionId == currentSession)
                            {
                                DateTime value = process.StartTime.ToUniversalTime();
                                if (value < earliest)
                                {
                                    earliest = value;
                                }
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

            return earliest;
        }

        private static HashSet<string> BuildExcludedProcessNames(SessionCaptureOptions options)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in options.ExcludedProcessNames)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(Path.GetFileNameWithoutExtension(value.Trim()));
                }
            }

            return result;
        }

        private static HashSet<string> BuildExcludedPaths(SessionCaptureOptions options)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in options.ExcludedExecutablePaths)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(PathText.Normalize(value));
                }
            }

            return result;
        }

        private static string TryGetCurrentExecutablePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }

        private static string GetDisplayName(string executablePath, string fallback)
        {
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(executablePath);
                if (!string.IsNullOrWhiteSpace(version.FileDescription))
                {
                    return version.FileDescription.Trim();
                }

                if (!string.IsNullOrWhiteSpace(version.ProductName))
                {
                    return version.ProductName.Trim();
                }
            }
            catch
            {
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? Path.GetFileNameWithoutExtension(executablePath)
                : fallback;
        }

        private static string GetFolderDisplayName(string path)
        {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(
                PathText.Normalize(left),
                PathText.Normalize(right),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool ExternalEffectsDisabled()
        {
            return IsTruthy("WORKSPACE_RESTORER_TEST_MODE")
                || IsTruthy("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS");
        }

        private static bool IsTruthy(string variableName)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class BrowserPageCapture
        {
            internal string Url { get; set; }

            internal string Title { get; set; }

            internal int TabIndex { get; set; }

            internal bool IsActive { get; set; }

            internal string CaptureSource { get; set; }
        }



        private sealed class WindowCandidate
        {
            internal int ProcessId { get; set; }

            internal string WindowTitle { get; set; }

            internal IntPtr WindowHandle { get; set; }

            internal bool IsForeground { get; set; }
        }
    }
}
