using System;
using System.Collections.Generic;
using System.IO;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private const string DataDirectoryVariable = "WORKSPACE_RESTORER_DATA_DIR";

        /// <summary>
        /// 固定数据目录名的迁移：另一套名字（英文产物是中文名，中文产物是英文名）搬成当前这套，
        /// 用户目录和文件内容不能丢。这里用独立的临时数据根，跑完恢复原来的 WORKSPACE_RESTORER_DATA_DIR。
        /// </summary>
        private static void RunDataFolderMigrationTests()
        {
            Run("数据目录名：另一套固定目录迁移到当前方案，文件内容保留", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    DataLayout other = OtherDataLayout();
                    string otherStartup = Path.Combine(other.Workflows, other.StartupRestore);
                    string otherDesktop = Path.Combine(other.Workflows, other.DesktopLayout);
                    string otherApps = Path.Combine(other.Workflows, other.Applications);

                    WriteTestFile(dataRoot, Path.Combine(otherStartup, "工作方案.json"), "profiles");
                    WriteTestFile(dataRoot, Path.Combine(otherStartup, other.StartupProfiles, "方案甲", "方案.json"), "profile-a");
                    WriteTestFile(dataRoot, Path.Combine(otherStartup, "说明.txt"), "local-note");
                    WriteTestFile(dataRoot, Path.Combine(otherDesktop, "布局档案.xml"), "layouts");
                    WriteTestFile(dataRoot, Path.Combine(otherDesktop, other.Layouts, "布局甲", "布局.xml"), "layout-a");
                    WriteTestFile(dataRoot, Path.Combine(otherApps, "自启动方案.json"), "applications");
                    WriteTestFile(dataRoot, Path.Combine(otherApps, other.ApplicationPlans, "方案乙", "方案.json"), "app-a");
                    WriteTestFile(dataRoot, Path.Combine(other.RecycleBin, other.StartupRestore, "条目甲", "条目.json"), "recycle-a");
                    WriteTestFile(dataRoot, Path.Combine(other.RecycleBin, other.DesktopLayout, "条目乙", "条目.json"), "recycle-b");
                    WriteTestFile(dataRoot, Path.Combine(other.Settings, "settings.json"), "settings");
                    WriteTestFile(dataRoot, Path.Combine(other.Logs, "2026-11-19.log"), "log");
                    WriteTestFile(dataRoot, Path.Combine(other.Backup, other.StartupProfileBackups, "old.json"), "backup-a");
                    WriteTestFile(dataRoot, Path.Combine(other.Backup, other.Legacy, "old.json"), "legacy-a");
                    WriteTestFile(dataRoot, Path.Combine(other.Temp, other.AutoRestore, "2026-11-19.标记"), "marker");

                    AppPaths.EnsureDataDirectories();

                    AssertFile(dataRoot, RelativeToDataRoot(AppPaths.StartupProfilesFile), "profiles");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.StartupProfilesFolderRoot, "方案甲", "方案.json")), "profile-a");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.StartupRestoreDirectory, "说明.txt")), "local-note");
                    AssertFile(dataRoot, RelativeToDataRoot(AppPaths.DesktopLayoutFile), "layouts");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.DesktopLayoutsDirectory, "布局甲", "布局.xml")), "layout-a");
                    AssertFile(dataRoot, RelativeToDataRoot(AppPaths.ApplicationPlansFile), "applications");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.ApplicationPlansFolderRoot, "方案乙", "方案.json")), "app-a");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(
                        AppPaths.RecycleBinKindDirectory(AppPaths.StartupRestoreKind), "条目甲", "条目.json")), "recycle-a");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(
                        AppPaths.RecycleBinKindDirectory(AppPaths.DesktopLayoutKind), "条目乙", "条目.json")), "recycle-b");
                    AssertFile(dataRoot, RelativeToDataRoot(AppPaths.SettingsFile), "settings");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.LogDirectory, "2026-11-19.log")), "log");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.StartupProfileBackupsDirectory, "old.json")), "backup-a");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.LegacyBackupDirectory, "old.json")), "legacy-a");
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.AutoRestoreMarkerDirectory, "2026-11-19.标记")), "marker");

                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.RecycleBin)), "旧回收站目录不应残留。");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.Settings)), "旧设置目录不应残留。");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.Logs)), "旧日志目录不应残留。");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.Workflows, other.StartupRestore)), "旧工作区目录不应残留。");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.Workflows, other.DesktopLayout)), "旧工作区目录不应残留。");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, other.Workflows, other.Applications)), "旧工作区目录不应残留。");

                    string markerPath = Path.Combine(dataRoot, ".data-layout-v2.json");
                    Assert(File.Exists(markerPath), "迁移完成后应写完成标记。");

                    // 再跑一次不能重复搬、不能把旧数据弄丢。
                    AppPaths.EnsureDataDirectories();
                    AssertFile(dataRoot, RelativeToDataRoot(Path.Combine(AppPaths.StartupProfilesFolderRoot, "方案甲", "方案.json")), "profile-a");
                    Assert(File.Exists(markerPath), "完成标记应保留。");
                });
            });

            Run("数据目录名：全新目录只建当前方案的固定目录", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    DataLayout other = OtherDataLayout();
                    AssertEqual(DataLayouts.Active.StartupRestore, AppPaths.StartupRestoreDirectoryName);
                    AssertEqual(DataLayouts.Active.DesktopLayout, AppPaths.DesktopLayoutDirectoryName);
                    AssertEqual(DataLayouts.Active.Applications, AppPaths.ApplicationsDirectoryName);
                    AppPaths.EnsureDataDirectories();

                    Assert(!Directory.Exists(Path.Combine(dataRoot, AppPaths.StartupRestoreDirectoryName)),
                        "工作区应位于 workflows 下，不应直接放在数据根。");
                    Assert(Directory.Exists(AppPaths.StartupRestoreDirectory), "缺少启动与恢复目录。");
                    Assert(Directory.Exists(AppPaths.DesktopLayoutDirectory), "缺少图标位置保存目录。");
                    Assert(Directory.Exists(AppPaths.ApplicationsDirectory), "缺少自启动应用目录。");
                    Assert(Directory.Exists(AppPaths.StartupProfilesFolderRoot), "缺少方案文件夹目录。");
                    Assert(Directory.Exists(AppPaths.DesktopLayoutsDirectory), "缺少布局文件夹目录。");
                    Assert(Directory.Exists(AppPaths.ApplicationPlansFolderRoot), "缺少自启动方案文件夹目录。");
                    Assert(Directory.Exists(AppPaths.RecycleBinKindDirectory(AppPaths.StartupRestoreKind)), "缺少回收站分段。");
                    Assert(Directory.Exists(AppPaths.RecycleBinKindDirectory(AppPaths.DesktopLayoutKind)), "缺少回收站分段。");
                    Assert(Directory.Exists(AppPaths.RecycleBinKindDirectory(AppPaths.ApplicationsKind)), "缺少回收站分段。");
                    Assert(Directory.Exists(Path.Combine(dataRoot, AppPaths.SettingsDirectoryName)), "缺少设置目录。");
                    Assert(Directory.Exists(AppPaths.LogDirectory), "缺少日志目录。");
                    Assert(Directory.Exists(AppPaths.BackupDirectory), "缺少备份目录。");
                    Assert(Directory.Exists(AppPaths.StartupProfileBackupsDirectory), "缺少备份子目录。");
                    Assert(Directory.Exists(AppPaths.LegacyBackupDirectory), "缺少旧文件备份目录。");
                    Assert(Directory.Exists(AppPaths.TempDirectory), "缺少临时目录。");
                    Assert(Directory.Exists(AppPaths.AutoRestoreMarkerDirectory), "缺少自动恢复标记目录。");

                    string[] otherNames = new string[]
                    {
                        other.RecycleBin, other.Settings, other.Logs, other.Backup, other.Temp,
                        Path.Combine(other.Workflows, other.StartupRestore),
                        Path.Combine(other.Workflows, other.DesktopLayout),
                        Path.Combine(other.Workflows, other.Applications)
                    };
                    for (int index = 0; index < otherNames.Length; index++)
                    {
                        Assert(!Directory.Exists(Path.Combine(dataRoot, otherNames[index])),
                            "不应创建另一套方案的目录：" + otherNames[index]);
                    }
                });
            });

            Run("回收站旧单文件：自动拆成当前回收站分段并留档", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    string json = "{ \"RetentionDays\": 15, \"Entries\": [ { "
                        + "\"DeletedAtUtc\": \"/Date(1791338400000)/\", "
                        + "\"Kind\": \"启动与恢复\", "
                        + "\"Title\": \"被删的方案\", "
                        + "\"Profile\": { \"Id\": \"d1\", \"Name\": \"被删的方案\", "
                        + "\"CreatedAtUtc\": \"/Date(1791338400000)/\", "
                        + "\"UpdatedAtUtc\": \"/Date(1791338400000)/\", \"Items\": [] } } ] }";
                    WriteTestFile(dataRoot, "recycle-bin.json", json);

                    WorkspaceRestorer.RecycleBin.Core.RecycleBinFolderStore store =
                        new WorkspaceRestorer.RecycleBin.Core.RecycleBinFolderStore();
                    WorkspaceRestorer.RecycleBin.Core.RecycleBinCatalog catalog = store.Load();
                    AssertEqual(1, catalog.Entries.Count);

                    string kindRoot = AppPaths.RecycleBinKindDirectory(AppPaths.StartupRestoreKind);
                    Assert(Directory.Exists(kindRoot), "旧条目应拆到回收站的启动与恢复分段。");
                    AssertEqual(1, Directory.GetDirectories(kindRoot).Length);
                    Assert(File.Exists(Path.Combine(AppPaths.LegacyBackupDirectory, "recycle-bin.json")),
                        "旧 recycle-bin.json 应搬到旧文件备份目录。");
                });
            });

            Run("数据目录名：删掉设置目录只重建目录，不生成说明.txt", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();
                    string settings = Path.Combine(dataRoot, AppPaths.SettingsDirectoryName);
                    Directory.Delete(settings, true);
                    AppPaths.EnsureDataDirectories();

                    Assert(Directory.Exists(settings), "设置目录应被重建。");
                    Assert(!File.Exists(Path.Combine(settings, "说明.txt")),
                        "程序不应生成说明.txt。");
                    string[] notes = Directory.GetFiles(dataRoot, "说明.txt", SearchOption.AllDirectories);
                    AssertEqual(0, notes.Length);
                });
            });
        }

        /// <summary>另一套目录名方案（英文产物里是中文那套，中文产物里是英文那套）。</summary>
        private static DataLayout OtherDataLayout()
        {
            DataLayout[] all = DataLayouts.All;
            for (int index = 0; index < all.Length; index++)
            {
                if (all[index] != null
                    && !string.Equals(all[index].Variant, DataLayouts.ActiveVariant, StringComparison.OrdinalIgnoreCase))
                {
                    return all[index];
                }
            }
            return DataLayouts.Active;
        }

        /// <summary>把 AppPaths 的绝对路径变成相对数据根的一段（AssertFile/WriteTestFile 用）。</summary>
        private static string RelativeToDataRoot(string fullPath)
        {
            string root = AppPaths.DataRoot;
            if (!string.IsNullOrEmpty(root)
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
            }
            return fullPath;
        }

        private static void WithTemporaryDataRoot(Action<string> action)
        {
            string previous = Environment.GetEnvironmentVariable(DataDirectoryVariable);
            string root = Path.Combine(
                Path.GetTempPath(),
                "workspace-restorer-data-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Environment.SetEnvironmentVariable(DataDirectoryVariable, root);
                action(root);
            }
            finally
            {
                Environment.SetEnvironmentVariable(DataDirectoryVariable, previous);
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void WriteTestFile(string root, string relativePath, string content)
        {
            string path = Path.Combine(root, relativePath);
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(path, content);
        }

        private static void AssertFile(string root, string relativePath, string expectedContent)
        {
            string path = Path.Combine(root, relativePath);
            Assert(File.Exists(path), "缺少文件：" + relativePath);
            AssertEqual(expectedContent, File.ReadAllText(path));
        }
    }
}
