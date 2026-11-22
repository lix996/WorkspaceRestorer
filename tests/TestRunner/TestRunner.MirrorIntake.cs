using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Infrastructure.PlanMirrors;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Tests
{
    /// <summary>
    /// 【镜像收编】用户把一整个方案文件夹复制进 workflows 之后：
    ///  · 复制进来的方案能被识别、导入清单（不用重启第二次）；
    ///  · 程序看不懂的文件夹只搬到 `备份\无法识别\&lt;工作区&gt;\`，绝不删除；
    ///  · 同步不再把“清单外的文件夹”当垃圾删掉，只删“已登记方案的旧文件夹”。
    /// </summary>
    internal static partial class TestRunner
    {
        private static void RunMirrorIntakeTests()
        {
            Run("镜像收编：复制进来的方案文件夹能被识别并追加进清单", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    StartupProfile copied = new StartupProfile();
                    copied.Id = "copied-" + Guid.NewGuid().ToString("N");
                    copied.Name = "复制来的方案";
                    WriteProfileSnapshot(
                        Path.Combine(AppPaths.StartupProfilesFolderRoot, "复制来的方案"),
                        copied);

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);

                    AssertEqual(1, adoptable.Count);
                    AssertEqual(0, unrecognized.Count);
                    AssertEqual("复制来的方案", adoptable[0].Node.Name);

                    MirrorIntake.Adopt(adoptable, known);
                    AssertEqual(1, known.Count);
                    AssertEqual(copied.Id, known[0].Id);
                    AssertEqual("复制来的方案", known[0].Name);
                });
            });

            Run("镜像收编：嵌套文件夹按父文件夹还原父子关系", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    StartupProfile parent = new StartupProfile();
                    parent.Id = "parent-" + Guid.NewGuid().ToString("N");
                    parent.Name = "大方案";
                    WriteProfileSnapshot(
                        Path.Combine(AppPaths.StartupProfilesFolderRoot, "大方案"),
                        parent);

                    StartupProfile child = new StartupProfile();
                    child.Id = "child-" + Guid.NewGuid().ToString("N");
                    child.Name = "小方案";
                    WriteProfileSnapshot(
                        Path.Combine(AppPaths.StartupProfilesFolderRoot, "大方案", "小方案"),
                        child);

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);

                    AssertEqual(2, adoptable.Count);
                    MirrorIntake.Adopt(adoptable, known);

                    StartupProfile storedChild = PlanFolderTreeForTest(known, child.Id);
                    Assert(storedChild != null, "子方案应已导入。");
                    AssertEqual(parent.Id, storedChild.ParentId);
                });
            });

            Run("镜像收编：认不出来的文件夹搬到“无法识别”，只移不删", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    string bad = Path.Combine(AppPaths.StartupProfilesFolderRoot, "看不懂的文件夹");
                    Directory.CreateDirectory(bad);
                    File.WriteAllText(Path.Combine(bad, "随便.txt"), "keep me", new UTF8Encoding(false));

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);

                    AssertEqual(0, adoptable.Count);
                    AssertEqual(1, unrecognized.Count);
                    AssertEqual(bad, unrecognized[0]);

                    string destination;
                    Assert(MirrorIntake.MoveToUnrecognized(bad, AppPaths.StartupRestoreKind, out destination),
                        "应能把认不出来的文件夹搬到“无法识别”。");
                    Assert(!Directory.Exists(bad), "搬走后原位置不应还在。");
                    Assert(Directory.Exists(destination), "目标文件夹应存在：" + destination);
                    AssertEqual(
                        Path.GetFullPath(Path.Combine(AppPaths.UnrecognizedKindDirectory(AppPaths.StartupRestoreKind), "看不懂的文件夹")),
                        Path.GetFullPath(destination));
                    AssertEqual("keep me", File.ReadAllText(Path.Combine(destination, "随便.txt")));
                });
            });

            Run("镜像同步：清单外的文件夹不被删掉，只删已登记方案的旧文件夹", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    StartupProfile knownProfile = new StartupProfile();
                    knownProfile.Id = "known-" + Guid.NewGuid().ToString("N");
                    knownProfile.Name = "甲方案";
                    knownProfile.UpdatedAtUtc = new DateTime(2026, 11, 20, 1, 0, 0, DateTimeKind.Utc);

                    string root = AppPaths.StartupProfilesFolderRoot;
                    WriteProfileSnapshot(Path.Combine(root, "甲方案"), knownProfile);
                    // 改名前的旧文件夹：里面是同一份方案（Id 已登记），应被清掉。
                    WriteProfileSnapshot(Path.Combine(root, "旧名字"), knownProfile);

                    // 清单外、读得出来的文件夹：复制进来的新方案，绝不能被删。
                    StartupProfile copied = new StartupProfile();
                    copied.Id = "copied-" + Guid.NewGuid().ToString("N");
                    copied.Name = "复制来的方案";
                    WriteProfileSnapshot(Path.Combine(root, "复制来的方案"), copied);

                    // 清单外、读不出来的文件夹：也不能删。
                    string broken = Path.Combine(root, "坏文件夹");
                    Directory.CreateDirectory(broken);
                    File.WriteAllText(Path.Combine(broken, "方案.json"), "{ 这不是 JSON", new UTF8Encoding(false));

                    List<StartupProfile> profiles = new List<StartupProfile>();
                    profiles.Add(knownProfile);
                    StartupProfileFolderMirror.Sync(profiles);

                    Assert(Directory.Exists(Path.Combine(root, "甲方案")), "在清单里的方案文件夹应保留。");
                    Assert(!Directory.Exists(Path.Combine(root, "旧名字")), "已登记方案的旧文件夹应被清掉。");
                    Assert(Directory.Exists(Path.Combine(root, "复制来的方案")),
                        "清单外、读得出来的文件夹不能被同步删掉。");
                    Assert(Directory.Exists(broken), "读不出来的文件夹不能被同步删掉。");
                });
            });

            Run("镜像收编：选过「否」的文件夹内容没变就不再打扰", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    string folder = Path.Combine(AppPaths.StartupProfilesFolderRoot, "不要的方案");
                    StartupProfile ignored = new StartupProfile();
                    ignored.Id = "ignored-" + Guid.NewGuid().ToString("N");
                    ignored.Name = "不要的方案";
                    WriteProfileSnapshot(folder, ignored);

                    HashSet<string> signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    MirrorIntake.Defer(AppPaths.StartupRestoreKind, folder, signatures);

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);
                    AssertEqual(1, adoptable.Count);

                    List<string> skipped;
                    List<MirrorIntakeFolder<StartupProfile>> pending = MirrorIntake.ExcludeIgnored(
                        adoptable,
                        AppPaths.StartupRestoreKind,
                        signatures,
                        out skipped);
                    AssertEqual(0, pending.Count);
                    AssertEqual(1, skipped.Count);
                });
            });

            Run("镜像收编：暂不导入只压本次登录会话，重启后再问", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    string folder = Path.Combine(AppPaths.StartupProfilesFolderRoot, "暂不导入的方案");
                    StartupProfile deferred = new StartupProfile();
                    deferred.Id = "defer-" + Guid.NewGuid().ToString("N");
                    deferred.Name = "暂不导入的方案";
                    WriteProfileSnapshot(folder, deferred);

                    HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    MirrorIntake.Defer(AppPaths.StartupRestoreKind, folder, ignored);
                    MirrorIntake.SaveIgnored(ignored);

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);
                    AssertEqual(1, adoptable.Count);

                    List<string> skipped;
                    List<MirrorIntakeFolder<StartupProfile>> sameSession = MirrorIntake.ExcludeIgnored(
                        adoptable,
                        AppPaths.StartupRestoreKind,
                        ignored,
                        out skipped);
                    AssertEqual(0, sameSession.Count);
                    AssertEqual(1, skipped.Count);

                    string previousSession = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_LOGON_SESSION");
                    try
                    {
                        Environment.SetEnvironmentVariable("WORKSPACE_RESTORER_LOGON_SESSION", "simulated-next-logon");
                        List<MirrorIntakeFolder<StartupProfile>> nextSession = MirrorIntake.ExcludeIgnored(
                            adoptable,
                            AppPaths.StartupRestoreKind,
                            ignored,
                            out skipped);
                        AssertEqual(1, nextSession.Count);
                        AssertEqual(0, skipped.Count);
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("WORKSPACE_RESTORER_LOGON_SESSION", previousSession);
                    }

                    MirrorIntake.Forget(AppPaths.StartupRestoreKind, folder, ignored);
                    MirrorIntake.SaveIgnored(ignored);
                    List<MirrorIntakeFolder<StartupProfile>> afterForget = MirrorIntake.ExcludeIgnored(
                        adoptable,
                        AppPaths.StartupRestoreKind,
                        ignored,
                        out skipped);
                    AssertEqual(1, afterForget.Count);
                });
            });

            Run("镜像收编：旧版没有前缀的忽略记录升级后作废一次", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    string folder = Path.Combine(AppPaths.StartupProfilesFolderRoot, "旧忽略方案");
                    StartupProfile legacy = new StartupProfile();
                    legacy.Id = "legacy-ignore-" + Guid.NewGuid().ToString("N");
                    legacy.Name = "旧忽略方案";
                    WriteProfileSnapshot(folder, legacy);

                    HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ignored.Add(MirrorIntake.Signature(AppPaths.StartupRestoreKind, folder));

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);
                    List<string> skipped;
                    List<MirrorIntakeFolder<StartupProfile>> pending = MirrorIntake.ExcludeIgnored(
                        adoptable,
                        AppPaths.StartupRestoreKind,
                        ignored,
                        out skipped);
                    AssertEqual(1, pending.Count);
                    AssertEqual(0, skipped.Count);
                });
            });

            Run("镜像收编：父子同时候选时只移动最外层父文件夹", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    AppPaths.EnsureDataDirectories();

                    StartupProfile parent = new StartupProfile();
                    parent.Id = "top-parent-" + Guid.NewGuid().ToString("N");
                    parent.Name = "父方案";
                    WriteProfileSnapshot(
                        Path.Combine(AppPaths.StartupProfilesFolderRoot, "父方案"),
                        parent);

                    StartupProfile child = new StartupProfile();
                    child.Id = "top-child-" + Guid.NewGuid().ToString("N");
                    child.Name = "子方案";
                    WriteProfileSnapshot(
                        Path.Combine(AppPaths.StartupProfilesFolderRoot, "父方案", "子方案"),
                        child);

                    List<StartupProfile> known = new List<StartupProfile>();
                    List<MirrorIntakeFolder<StartupProfile>> adoptable =
                        new List<MirrorIntakeFolder<StartupProfile>>();
                    List<string> unrecognized = new List<string>();
                    StartupProfileFolderMirror.ScanUnadopted(known, adoptable, unrecognized);
                    AssertEqual(2, adoptable.Count);

                    List<MirrorIntakeFolder<StartupProfile>> top =
                        MirrorIntake.SelectTopMostFolders(adoptable);
                    AssertEqual(1, top.Count);
                    AssertEqual("父方案", top[0].Name);

                    string destination;
                    Assert(MirrorIntake.MoveToUnrecognized(
                        top[0].Directory,
                        AppPaths.StartupRestoreKind,
                        out destination), "父文件夹应能移到无法识别。");
                    Assert(File.Exists(Path.Combine(destination, "子方案", "方案.json")),
                        "移动父文件夹时子方案目录应一起带走。");
                });
            });

            Run("目录名方案：英文数据目录能整体切成中文再切回来，文件不丢", delegate
            {
                WithTemporaryDataRoot(delegate(string dataRoot)
                {
                    WriteTestFile(dataRoot, @"workflows\StartupRestore\StartupProfiles\方案甲\方案.json", "profile-a");
                    WriteTestFile(dataRoot, @"workflows\DesktopLayout\Layouts\布局甲\布局.xml", "layout-a");
                    WriteTestFile(dataRoot, @"workflows\Applications\ApplicationPlans\方案乙\方案.json", "app-a");
                    WriteTestFile(dataRoot, RelativeToDataRoot(AppPaths.SettingsFile), "settings");
                    WriteTestFile(dataRoot, @"Backup\Legacy\old.json", "legacy-a");

                    AppPaths.EnsureDataDirectories();

                    DataLayout chinese = null;
                    for (int index = 0; index < DataLayouts.All.Length; index++)
                    {
                        if (string.Equals(DataLayouts.All[index].Variant, "zh-CN", StringComparison.OrdinalIgnoreCase))
                        {
                            chinese = DataLayouts.All[index];
                        }
                    }
                    Assert(chinese != null, "构建时应把中文目录名方案也生成进来。");

                    DataFolderMigration.Convert(dataRoot, chinese);
                    AssertFile(dataRoot, @"workflows\启动与恢复\工作方案\方案甲\方案.json", "profile-a");
                    AssertFile(dataRoot, @"workflows\图标位置保存\布局\布局甲\布局.xml", "layout-a");
                    AssertFile(dataRoot, @"workflows\自启动应用\自启动方案\方案乙\方案.json", "app-a");
                    AssertFile(dataRoot, @"设置\settings.json", "settings");
                    AssertFile(dataRoot, @"备份\搬家前的旧文件\old.json", "legacy-a");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, "Settings")), "切到中文后旧的英文目录不应残留。");

                    DataFolderMigration.Convert(dataRoot, DataLayouts.All[0]);
                    AssertFile(dataRoot, @"workflows\StartupRestore\StartupProfiles\方案甲\方案.json", "profile-a");
                    AssertFile(dataRoot, @"Settings\settings.json", "settings");
                    Assert(!Directory.Exists(Path.Combine(dataRoot, "设置")), "切回英文后中文目录不应残留。");
                });
            });
        }

        private static StartupProfile PlanFolderTreeForTest(IList<StartupProfile> profiles, string id)
        {
            if (profiles == null)
            {
                return null;
            }
            for (int index = 0; index < profiles.Count; index++)
            {
                if (profiles[index] != null && string.Equals(profiles[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return profiles[index];
                }
            }
            return null;
        }

        /// <summary>按程序真实的写法把一个方案写成 方案.json（带 BOM）。</summary>
        private static void WriteProfileSnapshot(string directory, StartupProfile profile)
        {
            Directory.CreateDirectory(directory);
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(StartupProfile));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, profile);
                File.WriteAllText(
                    Path.Combine(directory, StartupProfileFolderMirror.DataFileName),
                    Encoding.UTF8.GetString(stream.ToArray()),
                    new UTF8Encoding(true));
            }
        }
    }
}
