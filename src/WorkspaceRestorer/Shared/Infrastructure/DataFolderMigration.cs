using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 固定数据目录名的迁移。要处理两种情况，规则一样：
    ///  · 旧版中文名 → 新版英文名（2026-11 做过一次）；
    ///  · 英文产物 ↔ 中文产物（两套名字互换，随时可能来回切）。
    ///
    /// 规则：
    ///  · 只改程序固定目录；用户自己起的方案名/布局名不动；
    ///  · 按“层级”改：先父目录、再子目录，改完父目录后子目录用新名字继续找；
    ///  · 新旧目录同时存在时合并，新目录优先，旧文件不覆盖（冲突文件进 备份\Legacy\Conflicts）；
    ///  · 说明.txt 只是普通文件，跟着目录走，不删、不改内容；
    ///  · 迁移没完成就抛异常，绝不能带着空的新目录继续启动。
    /// </summary>
    internal static class DataFolderMigration
    {
        private const string MarkerFileName = ".data-layout-v2.json";
        private const string MigrationLogFileName = "data-migration.log";

        /// <summary>某一层里“哪些固定目录可能换名字”。Parent 相对数据根。</summary>
        private sealed class Level
        {
            public string Parent;
            public Func<DataLayout, string>[] Selectors;
        }

        private static class Names
        {
            public static readonly Func<DataLayout, string> Workflows = delegate(DataLayout layout) { return layout.Workflows; };
            public static readonly Func<DataLayout, string> StartupRestore = delegate(DataLayout layout) { return layout.StartupRestore; };
            public static readonly Func<DataLayout, string> DesktopLayout = delegate(DataLayout layout) { return layout.DesktopLayout; };
            public static readonly Func<DataLayout, string> Applications = delegate(DataLayout layout) { return layout.Applications; };
            public static readonly Func<DataLayout, string> StartupProfiles = delegate(DataLayout layout) { return layout.StartupProfiles; };
            public static readonly Func<DataLayout, string> Layouts = delegate(DataLayout layout) { return layout.Layouts; };
            public static readonly Func<DataLayout, string> ApplicationPlans = delegate(DataLayout layout) { return layout.ApplicationPlans; };
            public static readonly Func<DataLayout, string> RecycleBin = delegate(DataLayout layout) { return layout.RecycleBin; };
            public static readonly Func<DataLayout, string> Settings = delegate(DataLayout layout) { return layout.Settings; };
            public static readonly Func<DataLayout, string> Logs = delegate(DataLayout layout) { return layout.Logs; };
            public static readonly Func<DataLayout, string> Backup = delegate(DataLayout layout) { return layout.Backup; };
            public static readonly Func<DataLayout, string> Temp = delegate(DataLayout layout) { return layout.Temp; };
            public static readonly Func<DataLayout, string> AutoRestore = delegate(DataLayout layout) { return layout.AutoRestore; };
            public static readonly Func<DataLayout, string> StartupProfileBackups = delegate(DataLayout layout) { return layout.StartupProfileBackups; };
            public static readonly Func<DataLayout, string> Legacy = delegate(DataLayout layout) { return layout.Legacy; };
            public static readonly Func<DataLayout, string> Unrecognized = delegate(DataLayout layout) { return layout.Unrecognized; };
        }

        public static void EnsureMigrated()
        {
            string dataRoot = AppPaths.DataRoot;
            Directory.CreateDirectory(dataRoot);

            DataLayout active = DataLayouts.Active;
            if (!ConversionNeeded(dataRoot, active))
            {
                return;
            }

            try
            {
                bool moved = Convert(dataRoot, active);
                WriteMarker(Path.Combine(dataRoot, MarkerFileName), active, moved);
                if (moved)
                {
                    WriteBootstrapLog("固定数据目录名已切换到 " + active.Variant + "。");
                }
            }
            catch (Exception exception)
            {
                WriteBootstrapLog("固定数据目录名迁移失败，程序不会继续启动。", exception);
                throw new InvalidOperationException(
                    "数据目录改名失败。原数据没有被删除；请关闭可能占用 data 目录的程序/资源管理器后重试。"
                    + " 详情见 data\\default\\data-migration.log。",
                    exception);
            }
        }

        /// <summary>
        /// 把数据根里的固定目录名统一成 <paramref name="target"/> 那一套，返回是否真的动过文件。
        /// 测试里可以直接指定目标方案（英文产物里也能验证中文目录名的迁移）。
        /// </summary>
        internal static bool Convert(string dataRoot, DataLayout target)
        {
            if (string.IsNullOrWhiteSpace(dataRoot) || target == null)
            {
                return false;
            }

            bool moved = false;
            List<Level> levels = BuildLevels(target);
            for (int index = 0; index < levels.Count; index++)
            {
                Level level = levels[index];
                string parent = level.Parent.Length == 0
                    ? dataRoot
                    : Path.Combine(dataRoot, level.Parent);
                if (level.Parent.Length > 0 && !Directory.Exists(parent))
                {
                    continue;
                }
                MigrateLevel(parent, target, level.Selectors, ref moved);
            }

            AssertNoNonEmptyForeignDirectories(dataRoot, target, levels);
            return moved;
        }

        private static bool ConversionNeeded(string dataRoot, DataLayout active)
        {
            string marker = Path.Combine(dataRoot, MarkerFileName);
            if (!File.Exists(marker))
            {
                return true;
            }

            string recorded = ReadMarkerVariant(marker);
            if (!string.Equals(recorded, active.Variant, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 标记对得上，但目录里还留着另一套名字（比如把另一套的数据整个贴进来）：
            // 这不算“已经迁好”，再按合并规则走一遍，避免出现两套固定目录并存。
            List<Level> levels = BuildLevels(active);
            for (int index = 0; index < levels.Count; index++)
            {
                Level level = levels[index];
                string parent = level.Parent.Length == 0
                    ? dataRoot
                    : Path.Combine(dataRoot, level.Parent);
                if (!Directory.Exists(parent))
                {
                    continue;
                }
                if (FindForeignDirectories(parent, active, level.Selectors).Count > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<Level> BuildLevels(DataLayout active)
        {
            List<Level> levels = new List<Level>();
            levels.Add(new Level
            {
                Parent = string.Empty,
                Selectors = new Func<DataLayout, string>[]
                {
                    Names.Workflows, Names.RecycleBin, Names.Settings,
                    Names.Logs, Names.Backup, Names.Temp
                }
            });
            levels.Add(new Level
            {
                Parent = active.Workflows,
                Selectors = new Func<DataLayout, string>[]
                {
                    Names.StartupRestore, Names.DesktopLayout, Names.Applications
                }
            });
            levels.Add(new Level
            {
                Parent = Path.Combine(active.Workflows, active.StartupRestore),
                Selectors = new Func<DataLayout, string>[] { Names.StartupProfiles }
            });
            levels.Add(new Level
            {
                Parent = Path.Combine(active.Workflows, active.DesktopLayout),
                Selectors = new Func<DataLayout, string>[] { Names.Layouts }
            });
            levels.Add(new Level
            {
                Parent = Path.Combine(active.Workflows, active.Applications),
                Selectors = new Func<DataLayout, string>[] { Names.ApplicationPlans }
            });
            levels.Add(new Level
            {
                Parent = active.RecycleBin,
                Selectors = new Func<DataLayout, string>[]
                {
                    Names.StartupRestore, Names.DesktopLayout, Names.Applications
                }
            });
            levels.Add(new Level
            {
                Parent = active.Backup,
                Selectors = new Func<DataLayout, string>[]
                {
                    Names.StartupProfileBackups, Names.Legacy
                }
            });
            levels.Add(new Level
            {
                Parent = Path.Combine(active.Backup, active.Legacy),
                Selectors = new Func<DataLayout, string>[] { Names.Unrecognized }
            });
            levels.Add(new Level
            {
                Parent = active.Temp,
                Selectors = new Func<DataLayout, string>[] { Names.AutoRestore }
            });
            return levels;
        }

        private static void MigrateLevel(
            string parent,
            DataLayout target,
            Func<DataLayout, string>[] selectors,
            ref bool moved)
        {
            if (!Directory.Exists(parent))
            {
                return;
            }

            for (int index = 0; index < selectors.Length; index++)
            {
                Func<DataLayout, string> selector = selectors[index];
                string destinationName = selector(target);
                if (string.IsNullOrWhiteSpace(destinationName))
                {
                    continue;
                }

                List<string> allNames = DataLayouts.NamesAt(selector, true);
                for (int foreign = 0; foreign < allNames.Count; foreign++)
                {
                    // 目标名字本身当然不搬；其余任何一套方案里的名字都要搬过来。
                    if (string.Equals(allNames[foreign], destinationName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string source = Path.Combine(parent, allNames[foreign]);
                    if (!Directory.Exists(source))
                    {
                        continue;
                    }
                    string destination = Path.Combine(parent, destinationName);
                    if (IsSamePath(source, destination))
                    {
                        continue;
                    }
                    MoveOrMergeDirectory(source, destination);
                    moved = true;
                }
            }
        }

        private static List<string> FindForeignDirectories(
            string parent,
            DataLayout active,
            Func<DataLayout, string>[] selectors)
        {
            List<string> found = new List<string>();
            if (!Directory.Exists(parent))
            {
                return found;
            }

            for (int index = 0; index < selectors.Length; index++)
            {
                Func<DataLayout, string> selector = selectors[index];
                string activeName = selector(active);
                List<string> names = DataLayouts.NamesAt(selector, true);
                for (int nameIndex = 0; nameIndex < names.Count; nameIndex++)
                {
                    if (string.Equals(names[nameIndex], activeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    string candidate = Path.Combine(parent, names[nameIndex]);
                    if (Directory.Exists(candidate) && !IsSamePath(candidate, Path.Combine(parent, activeName)))
                    {
                        found.Add(candidate);
                    }
                }
            }
            return found;
        }

        private static void AssertNoNonEmptyForeignDirectories(
            string dataRoot,
            DataLayout active,
            List<Level> levels)
        {
            for (int index = 0; index < levels.Count; index++)
            {
                Level level = levels[index];
                string parent = level.Parent.Length == 0
                    ? dataRoot
                    : Path.Combine(dataRoot, level.Parent);
                List<string> leftovers = FindForeignDirectories(parent, active, level.Selectors);
                for (int leftover = 0; leftover < leftovers.Count; leftover++)
                {
                    string path = leftovers[leftover];
                    DeleteIfEmpty(path);
                    if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length > 0)
                    {
                        throw new IOException("迁移后旧目录仍有内容：" + path);
                    }
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, false);
                    }
                }
            }
        }

        private static bool IsSamePath(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadMarkerVariant(string markerPath)
        {
            try
            {
                string text = File.ReadAllText(markerPath, Encoding.UTF8);
                Match match = Regex.Match(text, "\"Variant\"\\s*:\\s*\"([^\"]*)\"");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteMarker(string markerPath, DataLayout active, bool moved)
        {
            try
            {
                string text = "{\"Version\":3,\"Variant\":\"" + active.Variant + "\",\"CompletedAt\":\""
                    + DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK") + "\",\"Moved\":"
                    + (moved ? "true" : "false") + "}";
                File.WriteAllText(markerPath, text, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                WriteBootstrapLog("完成标记写入失败（不影响数据）。", exception);
            }
        }

        /// <summary>迁移日志直接写文件，不能走 AppLog；AppLog 会再次调用 EnsureDataDirectories。</summary>
        internal static void WriteBootstrapLog(string message)
        {
            WriteBootstrapLog(message, null);
        }

        internal static void WriteBootstrapLog(string message, Exception exception)
        {
            try
            {
                string directory = AppPaths.DataRoot;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, MigrationLogFileName);
                StringBuilder builder = new StringBuilder();
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                builder.Append("  ");
                builder.Append(message ?? string.Empty);
                if (exception != null)
                {
                    builder.AppendLine();
                    builder.Append(exception);
                }
                builder.AppendLine();
                File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static void MoveOrMergeDirectory(string source, string destination)
        {
            if (!Directory.Exists(source))
            {
                return;
            }
            if (IsReparsePoint(source))
            {
                throw new IOException("迁移遇到目录链接，拒绝跟随：" + source);
            }

            if (!Directory.Exists(destination))
            {
                try
                {
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                    Directory.Move(source, destination);
                    return;
                }
                catch (Exception exception)
                {
                    WriteBootstrapLog("目录移动失败，改用复制合并：" + source + " -> " + destination, exception);
                }
            }

            Directory.CreateDirectory(destination);
            string[] directories = Directory.GetDirectories(source);
            for (int index = 0; index < directories.Length; index++)
            {
                string childSource = directories[index];
                string childDestination = Path.Combine(destination, Path.GetFileName(childSource));
                MoveOrMergeDirectory(childSource, childDestination);
            }

            string[] files = Directory.GetFiles(source);
            for (int index = 0; index < files.Length; index++)
            {
                MoveFileOrArchive(files[index], Path.Combine(destination, Path.GetFileName(files[index])));
            }

            DeleteIfEmpty(source);
        }

        private static void MoveFileOrArchive(string source, string destination)
        {
            if (!File.Exists(source))
            {
                return;
            }
            try
            {
                if (!File.Exists(destination))
                {
                    try
                    {
                        File.Move(source, destination);
                        return;
                    }
                    catch (Exception exception)
                    {
                        WriteBootstrapLog("文件移动失败，改用复制：" + source + " -> " + destination, exception);
                    }

                    File.Copy(source, destination, false);
                    if (new FileInfo(source).Length != new FileInfo(destination).Length)
                    {
                        throw new IOException("复制后文件长度不一致：" + destination);
                    }
                    File.Delete(source);
                    return;
                }

                if (FilesAreEqual(source, destination))
                {
                    File.Delete(source);
                    return;
                }

                ArchiveConflict(source);
            }
            catch (Exception exception)
            {
                throw new IOException("迁移文件失败：" + source + " -> " + destination, exception);
            }
        }

        private static bool FilesAreEqual(string left, string right)
        {
            try
            {
                FileInfo leftInfo = new FileInfo(left);
                FileInfo rightInfo = new FileInfo(right);
                if (leftInfo.Length != rightInfo.Length)
                {
                    return false;
                }

                using (FileStream leftStream = File.OpenRead(left))
                using (FileStream rightStream = File.OpenRead(right))
                {
                    byte[] leftBuffer = new byte[8192];
                    byte[] rightBuffer = new byte[8192];
                    while (true)
                    {
                        int leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
                        int rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
                        if (leftRead != rightRead)
                        {
                            return false;
                        }
                        if (leftRead <= 0)
                        {
                            return true;
                        }
                        for (int index = 0; index < leftRead; index++)
                        {
                            if (leftBuffer[index] != rightBuffer[index])
                            {
                                return false;
                            }
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static void ArchiveConflict(string source)
        {
            string conflictRoot = Path.Combine(
                AppPaths.LegacyBackupDirectory,
                "Conflicts",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(conflictRoot);
            string candidate = Path.Combine(conflictRoot, Path.GetFileName(source));
            int suffix = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(
                    conflictRoot,
                    Path.GetFileNameWithoutExtension(source) + "-" + suffix + Path.GetExtension(source));
                suffix++;
            }
            File.Move(source, candidate);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteIfEmpty(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }
                string[] entries = Directory.GetFileSystemEntries(directory);
                if (entries.Length == 0)
                {
                    Directory.Delete(directory, false);
                }
            }
            catch
            {
            }
        }
    }
}
