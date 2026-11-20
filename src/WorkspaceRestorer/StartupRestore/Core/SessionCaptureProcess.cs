using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// Runs cross-process UI Automation in a disposable copy of this EXE.
    /// A UI Automation provider can block inside unmanaged code indefinitely;
    /// a process boundary is the only reliable hard timeout on .NET Framework.
    /// </summary>
    internal static class SessionCaptureProcess
    {
        internal const string WorkerSwitch = "--session-capture-worker";
        internal const int PassiveTimeoutMilliseconds = 5000;
        // Walking a browser window's tabs takes roughly a second per tab, so a
        // session with a couple of 20-tab windows needs far more than the old
        // 20 s. Killing the worker mid-read threw the whole page capture away and
        // saved only the applications and folders.
        internal const int InteractiveTimeoutMilliseconds = 180000;
        private const string TemporaryFilePrefix = ProductIdentity.SessionCaptureTempPrefix;
        private static readonly object ActiveProcessesLock = new object();
        private static readonly List<Process> ActiveProcesses = new List<Process>();
        private static long cancellationVersion;

        /// <summary>
        /// Terminates only helper processes created by this class.  UI
        /// Automation providers can remain inside unmanaged code after the
        /// controller is closing; leaving such a helper alive could let an
        /// explicit browser scan continue selecting tabs after the keeper has
        /// exited.  The version increment also closes the race where the
        /// second (interactive) phase is about to start while cancellation is
        /// requested.
        /// </summary>
        internal static void CancelActiveCaptures()
        {
            List<Process> processes;
            lock (ActiveProcessesLock)
            {
                cancellationVersion++;
                processes = new List<Process>(ActiveProcesses);
            }

            for (int index = 0; index < processes.Count; index++)
            {
                TryTerminate(processes[index]);
            }
        }

        internal static int ActiveCaptureCount
        {
            get
            {
                lock (ActiveProcessesLock)
                {
                    RemoveInactiveProcessesLocked();
                    return ActiveProcesses.Count;
                }
            }
        }

        internal static SessionSnapshot Capture(SessionCaptureOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            long operationVersion = GetCancellationVersion();

            if (!options.AllowBrowserTabActivation)
            {
                return CaptureOnce(
                    options.IncludeExplorerFolders,
                    options.IncludeBrowserPages,
                    false,
                    PassiveTimeoutMilliseconds,
                    operationVersion);
            }

            // Capture a non-interactive baseline first.  If Edge's provider
            // later hangs, the user still gets applications, browser parents,
            // and Explorer folders instead of losing the whole save.
            SessionSnapshot baseline = CaptureOnce(
                options.IncludeExplorerFolders,
                false,
                false,
                PassiveTimeoutMilliseconds,
                operationVersion);

            if (!IsCancellationVersionCurrent(operationVersion))
            {
                return baseline;
            }

            SessionSnapshot interactive = CaptureOnce(
                options.IncludeExplorerFolders,
                options.IncludeBrowserPages,
                true,
                InteractiveTimeoutMilliseconds,
                operationVersion);
            if (interactive != null)
            {
                if (interactive.BrowserCaptureIncomplete
                    && string.IsNullOrWhiteSpace(interactive.CaptureWarning))
                {
                    interactive.CaptureWarning = BrowserCaptureWarningText.Build(interactive);
                }
                return interactive;
            }

            if (baseline != null)
            {
                baseline.BrowserCaptureIncomplete = true;
                baseline.BrowserCaptureTimedOut = true;
                baseline.CaptureWarning = BrowserCaptureWarningText.BuildTimeout();
            }
            return baseline;
        }

        /// <summary>
        /// Handles the private worker command before WPF and the single-
        /// instance guard are initialized.  Returns false for normal launches.
        /// </summary>
        internal static bool TryRunWorker(string[] args)
        {
            if (args == null
                || args.Length == 0
                || !string.Equals(args[0], WorkerSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Environment.ExitCode = RunWorker(args);
            return true;
        }

        private static int RunWorker(string[] args)
        {
            if (args.Length != 5)
            {
                return 2;
            }

            string outputPath;
            if (!TryValidateWorkerOutputPath(args[1], out outputPath))
            {
                return 3;
            }

            bool includeExplorerFolders;
            bool includeBrowserPages;
            bool allowBrowserTabActivation;
            if (!TryParseFlag(args[2], out includeExplorerFolders)
                || !TryParseFlag(args[3], out includeBrowserPages)
                || !TryParseFlag(args[4], out allowBrowserTabActivation))
            {
                return 4;
            }

            try
            {
                SessionCaptureOptions options = new SessionCaptureOptions
                {
                    IncludeExplorerFolders = includeExplorerFolders,
                    IncludeBrowserPages = includeBrowserPages,
                    AllowBrowserTabActivation = allowBrowserTabActivation
                };
                if (allowBrowserTabActivation)
                {
                    // Reading a browser window means walking its tabs, so a window
                    // with ~20 tabs needs well over the old 15 s. Keep the budget
                    // inside the parent's interactive timeout, which was raised to
                    // match, so a big session is captured completely instead of
                    // silently dropping the remaining browser windows.
                    options.PageReadDeadlineUtc = DateTime.UtcNow.AddSeconds(150);
                }
                SessionSnapshot snapshot = new SessionCaptureService().CaptureInCurrentProcess(options);
                if (snapshot == null)
                {
                    return 5;
                }

                AtomicJsonStore<SessionSnapshot> store = new AtomicJsonStore<SessionSnapshot>(
                    outputPath,
                    delegate { return new SessionSnapshot(); });
                store.Save(snapshot);
                return 0;
            }
            catch (Exception exception)
            {
                AppLog.Write("The isolated session capture worker failed.", exception);
                return 6;
            }
            finally
            {
                // Even a failed capture must minimize every browser window
                // that was restored in order to read its pages.
                SessionCaptureService.ReMinimizeRestoredBrowserWindows();
            }
        }

        private static SessionSnapshot CaptureOnce(
            bool includeExplorerFolders,
            bool includeBrowserPages,
            bool allowBrowserTabActivation,
            int timeoutMilliseconds,
            long operationVersion)
        {
            if (!IsCancellationVersionCurrent(operationVersion))
            {
                return null;
            }

            string executablePath = TryGetCurrentExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                AppLog.Write("Unable to start isolated capture because the current executable path is unavailable.");
                return null;
            }

            string outputPath = Path.Combine(
                Path.GetTempPath(),
                TemporaryFilePrefix + Guid.NewGuid().ToString("N") + ".json");
            Process process = null;
            int workerProcessId = 0;
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = QuoteArgument(WorkerSwitch)
                        + " " + QuoteArgument(outputPath)
                        + " " + (includeExplorerFolders ? "1" : "0")
                        + " " + (includeBrowserPages ? "1" : "0")
                        + " " + (allowBrowserTabActivation ? "1" : "0"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
                };
                process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }
                workerProcessId = process.Id;

                if (!TrackProcess(process, operationVersion))
                {
                    // Cancellation won the start/registration race.  The
                    // process is known to have been created by this method, so
                    // terminating it is safe and cannot affect user programs.
                    TryTerminate(process);
                    return null;
                }

                if (!process.WaitForExit(Math.Max(1000, timeoutMilliseconds)))
                {
                    AppLog.Write("The isolated session capture worker exceeded its time limit.");
                    TryTerminate(process);
                    return null;
                }
                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    AppLog.Write("The isolated session capture worker returned exit code " + process.ExitCode + ".");
                    return null;
                }

                AtomicJsonStore<SessionSnapshot> store = new AtomicJsonStore<SessionSnapshot>(
                    outputPath,
                    delegate { return new SessionSnapshot(); });
                return store.Load();
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to run the isolated session capture worker.", exception);
                TryTerminate(process);
                return null;
            }
            finally
            {
                UntrackProcess(process);
                if (process != null)
                {
                    process.Dispose();
                }
                TryDelete(outputPath);
                TryDelete(outputPath + ".bak");
                if (workerProcessId > 0)
                {
                    TryDelete(outputPath + ".tmp." + workerProcessId);
                }
            }
        }

        private static long GetCancellationVersion()
        {
            lock (ActiveProcessesLock)
            {
                return cancellationVersion;
            }
        }

        private static bool IsCancellationVersionCurrent(long version)
        {
            lock (ActiveProcessesLock)
            {
                return cancellationVersion == version;
            }
        }

        private static bool TrackProcess(Process process, long version)
        {
            if (process == null)
            {
                return false;
            }

            lock (ActiveProcessesLock)
            {
                RemoveInactiveProcessesLocked();
                if (cancellationVersion != version)
                {
                    return false;
                }
                ActiveProcesses.Add(process);
                return true;
            }
        }

        private static void UntrackProcess(Process process)
        {
            if (process == null)
            {
                return;
            }

            lock (ActiveProcessesLock)
            {
                for (int index = ActiveProcesses.Count - 1; index >= 0; index--)
                {
                    if (object.ReferenceEquals(ActiveProcesses[index], process))
                    {
                        ActiveProcesses.RemoveAt(index);
                    }
                }
            }
        }

        private static void RemoveInactiveProcessesLocked()
        {
            for (int index = ActiveProcesses.Count - 1; index >= 0; index--)
            {
                Process process = ActiveProcesses[index];
                bool remove = process == null;
                if (!remove)
                {
                    try
                    {
                        remove = process.HasExited;
                    }
                    catch
                    {
                        // A disposed/invalid process object is no longer a
                        // live helper and must not remain in the registry.
                        remove = true;
                    }
                }
                if (remove)
                {
                    ActiveProcesses.RemoveAt(index);
                }
            }
        }

        private static bool TryValidateWorkerOutputPath(string candidate, out string outputPath)
        {
            outputPath = null;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(candidate);
                string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fileName = Path.GetFileName(fullPath);
                if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(fileName)
                    || !fileName.StartsWith(TemporaryFilePrefix, StringComparison.OrdinalIgnoreCase)
                    || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                outputPath = fullPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseFlag(string value, out bool result)
        {
            result = false;
            if (string.Equals(value, "1", StringComparison.Ordinal))
            {
                result = true;
                return true;
            }
            return string.Equals(value, "0", StringComparison.Ordinal);
        }

        private static string TryGetCurrentExecutablePath()
        {
            try
            {
                using (Process current = Process.GetCurrentProcess())
                {
                    if (current.MainModule != null)
                    {
                        return current.MainModule.FileName;
                    }
                }
            }
            catch
            {
            }

            try
            {
                return Assembly.GetExecutingAssembly().Location;
            }
            catch
            {
                return null;
            }
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static void TryTerminate(Process process)
        {
            if (process == null)
            {
                return;
            }
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(1000);
                }
            }
            catch
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
