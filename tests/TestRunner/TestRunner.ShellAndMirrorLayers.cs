using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WorkspaceRestorer.Core.Cards.PlanFolders.Mirror;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunShellAndMirrorLayerTests()
        {
            Run("ShellOpener：测试模式只返回抑制，不真的打开", delegate
            {
                string file = Path.Combine(
                    Path.GetTempPath(),
                    "WorkspaceRestorer-ShellOpener-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(file, "shell opener probe", new UTF8Encoding(true));
                try
                {
                    ShellOpenResult openedFile = ShellOpener.OpenFile(file);
                    Assert(openedFile.Success, "测试模式下已有文件应算已处理。" );
                    Assert(openedFile.WasSuppressed, "测试模式不能让 ShellOpener 真的打开文件。" );
                    Assert(openedFile.Status == ShellOpenStatus.Suppressed, "状态应为 Suppressed。" );

                    ShellOpenResult openedFolder = ShellOpener.OpenFolder(Path.GetDirectoryName(file));
                    Assert(openedFolder.WasSuppressed, "测试模式不能真的打开文件夹。" );

                    ShellOpenResult openedTarget = ShellOpener.Open("notepad.exe", null, null);
                    Assert(openedTarget.WasSuppressed, "测试模式不能真的启动 Process.Start。" );

                    ShellOpenResult missing = ShellOpener.OpenFile(file + ".missing");
                    Assert(!missing.Success, "不存在的文件应返回失败。" );
                    Assert(missing.Status == ShellOpenStatus.Failed, "不存在的文件状态应为 Failed。" );
                    Assert(missing.Error.Contains("文件不存在"), "失败原因应说明文件不存在。" );

                    ShellOpenResult empty = ShellOpener.Open("  ", null, null);
                    Assert(!empty.Success, "空路径应返回失败。" );
                }
                finally
                {
                    try { File.Delete(file); } catch { }
                }
            });

            Run("镜像路径：纯规则算命名和相对目录，不依赖磁盘", delegate
            {
                List<StartupProfile> profiles = new List<StartupProfile>
                {
                    new StartupProfile { Id = "a", Name = "甲" },
                    new StartupProfile { Id = "b", Name = "乙", ParentId = "a" },
                    new StartupProfile { Id = "c", Name = "甲" },
                    new StartupProfile { Id = "d", Name = "非法:名字/带*号" },
                    new StartupProfile { Id = "e", Name = "   " },
                    null
                };

                List<string> names = PlanFolderName.BuildFolderNames(
                    profiles,
                    delegate(StartupProfile profile) { return profile == null ? null : profile.Name; },
                    "工作方案");
                AssertEqual(6, names.Count);
                AssertEqual("甲", names[0]);
                AssertEqual("乙", names[1]);
                AssertEqual("甲（2）", names[2]);
                Assert(names[3].IndexOf(':') < 0 && names[3].IndexOf('/') < 0 && names[3].IndexOf('*') < 0,
                    "非法字符要替换掉。" );
                AssertEqual("工作方案", names[4]);
                AssertEqual(string.Empty, names[5]);

                StartupProfile child = profiles[1];
                List<string> segments = PlanMirrorPath.BuildRelativeSegments(
                    child,
                    profiles,
                    delegate(StartupProfile profile) { return profile == null ? null : profile.Name; },
                    "工作方案");
                AssertEqual(2, segments.Count);
                AssertEqual("甲", segments[0]);
                AssertEqual("乙", segments[1]);
                AssertEqual(
                    Path.Combine("C:\\root", "甲", "乙"),
                    PlanMirrorPath.BuildPath("C:\\root", segments));

                List<string> missingParent = PlanMirrorPath.BuildRelativeSegments(
                    new StartupProfile { Id = "x", Name = "孤儿", ParentId = "not-found" },
                    profiles,
                    delegate(StartupProfile profile) { return profile == null ? null : profile.Name; },
                    "工作方案");
                Assert(missingParent == null, "父项找不到时应返回 null，而不是写到错误目录。" );
            });

            Run("镜像公开壳：启动清单文字和 UTF-8 BOM 保持原样", delegate
            {
                string directory = Path.Combine(
                    Path.GetTempPath(),
                    "WorkspaceRestorer-Mirror-" + Guid.NewGuid().ToString("N"));
                StartupProfile profile = new StartupProfile
                {
                    Id = "mirror-" + Guid.NewGuid().ToString("N"),
                    Name = "镜像测试",
                    UpdatedAtUtc = new DateTime(2026, 11, 5, 6, 0, 0, DateTimeKind.Utc)
                };
                profile.Items.Add(new SessionItem
                {
                    DisplayName = "记事本",
                    IsEnabled = true,
                    TargetPath = @"C:\Windows\System32\notepad.exe"
                });

                try
                {
                    StartupProfileFolderMirror.WriteProfileAtDirectory(profile, directory);
                    string listPath = Path.Combine(directory, "启动清单.txt");
                    string dataPath = Path.Combine(directory, "方案.json");
                    Assert(File.Exists(listPath), "镜像应写 启动清单.txt。" );
                    Assert(File.Exists(dataPath), "镜像应写 方案.json。" );

                    byte[] listBytes = File.ReadAllBytes(listPath);
                    Assert(listBytes.Length >= 3
                        && listBytes[0] == 0xEF
                        && listBytes[1] == 0xBB
                        && listBytes[2] == 0xBF,
                        "启动清单.txt 应保持 UTF-8 BOM。" );
                    byte[] dataBytes = File.ReadAllBytes(dataPath);
                    Assert(dataBytes.Length >= 3
                        && dataBytes[0] == 0xEF
                        && dataBytes[1] == 0xBB
                        && dataBytes[2] == 0xBF,
                        "方案.json 应保持 UTF-8 BOM。" );

                    string savedAt = StartupProfileCardText.FormatSavedAt(profile);
                    string expected = string.Join(Environment.NewLine, new string[]
                    {
                        "工作方案：" + profile.Name,
                        "最后保存：" + savedAt,
                        "启动项目：1 个",
                        "────────────────────────────────────────",
                        "1. [√] 记事本",
                        "      C:\\Windows\\System32\\notepad.exe",
                        string.Empty,
                        "说明：本文件只是方便查看的快照；程序实际使用同目录下的“工作方案.json”。",
                        "要删掉这份方案：删掉本文件夹，并在“工作方案.json”里删掉对应的一项。"
                    }) + Environment.NewLine;
                    AssertEqual(expected, File.ReadAllText(listPath, Encoding.UTF8));
                    AssertEqual(profile.Id, StartupProfileFolderMirror.ReadProfileIdAt(dataPath));
                }
                finally
                {
                    try { Directory.Delete(directory, true); } catch { }
                }
            });
        }
    }
}
