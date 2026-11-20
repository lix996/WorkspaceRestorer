using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed class DesktopApplicationService : IDesktopApplicationService
    {
        private readonly IProcessLauncher processLauncher;

        public DesktopApplicationService()
            : this(new ProcessLauncher())
        {
        }

        public DesktopApplicationService(IProcessLauncher processLauncher)
        {
            if (processLauncher == null)
            {
                throw new ArgumentNullException("processLauncher");
            }

            this.processLauncher = processLauncher;
        }

        public IList<DesktopEntry> Scan()
        {
            List<DesktopEntry> entries = new List<DesktopEntry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddDesktopEntries(entries, seen, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DesktopEntrySource.CurrentUser);
            AddDesktopEntries(entries, seen, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), DesktopEntrySource.Public);

            return entries
                .OrderBy(delegate(DesktopEntry entry) { return entry.DisplayName ?? string.Empty; }, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(delegate(DesktopEntry entry) { return entry.Path ?? string.Empty; }, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public LaunchResult Launch(DesktopEntry entry)
        {
            if (entry == null)
            {
                return new LaunchResult
                {
                    Success = false,
                    Error = "Desktop entry is required."
                };
            }

            // 自启动应用的启动项可以带“恢复时最小化/最大化”的窗口状态。
            // 真实启动器实现可选扩展；测试替身仍走原来的单参数入口。
            IWindowPlacementProcessLauncher placedLauncher =
                processLauncher as IWindowPlacementProcessLauncher;
            if (placedLauncher != null && entry.WindowPlacement != null)
            {
                return placedLauncher.LaunchPath(
                    entry.Path, entry.WindowPlacement, entry.WindowTitle);
            }
            return processLauncher.LaunchPath(entry.Path);
        }

        private static void AddDesktopEntries(
            IList<DesktopEntry> destination,
            ISet<string> seen,
            string desktopPath,
            DesktopEntrySource source)
        {
            if (string.IsNullOrWhiteSpace(desktopPath) || !Directory.Exists(desktopPath))
            {
                return;
            }

            try
            {
                foreach (string path in Directory.GetFileSystemEntries(desktopPath))
                {
                    try
                    {
                        string normalized = PathText.Normalize(path);
                        if (!seen.Add(normalized))
                        {
                            continue;
                        }

                        FileAttributes attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.Hidden) != 0
                            || (attributes & FileAttributes.System) != 0)
                        {
                            continue;
                        }

                        bool directory = (attributes & FileAttributes.Directory) != 0;
                        string extension = directory ? string.Empty : Path.GetExtension(path);
                        string displayName = Path.GetFileName(path);
                        bool shortcut = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);
                        if (shortcut)
                        {
                            displayName = Path.GetFileNameWithoutExtension(path);
                        }

                        destination.Add(new DesktopEntry
                        {
                            DisplayName = string.IsNullOrWhiteSpace(displayName) ? path : displayName,
                            Path = path,
                            Extension = extension ?? string.Empty,
                            Source = source,
                            IsShortcut = shortcut
                        });
                    }
                    catch
                    {
                        // A desktop item can disappear or deny metadata access while scanning.
                    }
                }
            }
            catch
            {
                // A missing or unavailable desktop should not prevent the public
                // desktop (or the other source) from being scanned.
            }
        }
    }
}
