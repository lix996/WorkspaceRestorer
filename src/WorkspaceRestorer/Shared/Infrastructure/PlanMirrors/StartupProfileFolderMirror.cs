using System;
using System.Collections.Generic;
using System.Text;
using WorkspaceRestorer.Core.Cards.PlanFolders.Mirror;
using WorkspaceRestorer.Infrastructure.PlanMirrors;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 把工作方案镜像成“一份方案一个文件夹”，放在
    /// `workflows\启动与恢复\工作方案\&lt;方案名&gt;\` 下面（用户要求 2026-10-08：
    /// 数据放进 workflows 并按工作区分段，方便直接看出有哪些方案、也方便手动清理）。
    ///
    /// 每个方案文件夹里有：
    ///  · 启动清单.txt —— 人可读的清单（序号、是否启用、名称、路径/网址），
    ///    用户不用打开 json 就能看清这份方案里有什么；
    ///  · 方案.json —— 这份方案的原始数据（备份/迁移用）。
    ///
    /// 文件夹只是快照，不是权威数据**：除了下面这条明确的规则：
    /// 用户手动删掉了某份方案的文件夹，下次启动时那份方案就会从清单里消失
    /// （见 <see cref="RemoveProfilesWhoseFolderWasDeleted"/>，带安全网）。
    /// 其余情况程序的清单仍以同目录下的 `工作方案.json` 为准，
    /// 这样即使用户手动动过文件夹，也不会把方案弄坏。
    /// （公开类型：测试需要直接验证文件夹命名与“删文件夹＝清方案”的规则。）
    ///
    /// 本类只保留“工作方案”自己的中文清单文字和公开 API；
    /// 建目录、写入、删除、嵌套遍历在 PlanFolderMirrorEngine，纯命名/路径在 Core。
    /// </summary>
    public static class StartupProfileFolderMirror
    {
        private const string ListFileName = "启动清单.txt";
        public const string DataFileName = "方案.json";

        private static readonly PlanFolderMirrorEngine<StartupProfile> Engine =
            new PlanFolderMirrorEngine<StartupProfile>(
                "工作方案",
                "工作方案",
                ListFileName,
                DataFileName,
                NameOf,
                BuildListText);

        public static void Sync(IList<StartupProfile> profiles)
        {
            Engine.Sync(profiles, AppPaths.StartupProfilesFolderRoot);
        }

        /// <summary>
        /// 一份方案在镜像树里的完整目录（按它当前的 ParentId 和同级顺序算）。
        /// 移动目录前用它拿旧路径；数据改完后用它拿新路径。
        /// </summary>
        public static string FolderPathFor(StartupProfile profile, IList<StartupProfile> allProfiles)
        {
            return Engine.FolderPathFor(profile, allProfiles, AppPaths.StartupProfilesFolderRoot);
        }

        /// <summary>读镜像目录里的 方案.json，返回 Id；读不出来返回空字符串。</summary>
        public static string ReadProfileIdAt(string dataPath)
        {
            return PlanMirrorFile.ReadIdAt<StartupProfile>(dataPath);
        }

        /// <summary>读一个目录里的 方案.json（收编用）；读不出来返回 null。</summary>
        internal static StartupProfile ReadProfileAtDirectory(string directory)
        {
            return Engine.ReadNodeAt(directory);
        }

        /// <summary>
        /// 扫 `工作方案\` 里还没登记进来的方案文件夹：
        /// 读得出来、Id 不在清单里的进 <paramref name="adoptable"/>，读不出来的进 <paramref name="unrecognized"/>。
        /// </summary>
        internal static void ScanUnadopted(
            IList<StartupProfile> profiles,
            List<MirrorIntakeFolder<StartupProfile>> adoptable,
            List<string> unrecognized)
        {
            MirrorIntake.Scan(
                profiles,
                AppPaths.StartupProfilesFolderRoot,
                ReadProfileAtDirectory,
                adoptable,
                unrecognized);
        }

        /// <summary>只重写一份方案的镜像目录（移动后调用，不重写整棵树）。</summary>
        public static void SyncMovedProfile(StartupProfile profile, IList<StartupProfile> allProfiles)
        {
            Engine.SyncMoved(profile, allProfiles, AppPaths.StartupProfilesFolderRoot);
        }

        /// <summary>写到指定目录（目录移动时可能为了避开同名旧目录而用了“名字（2）”）。</summary>
        public static void WriteProfileAtDirectory(StartupProfile profile, string directory)
        {
            Engine.WriteAtDirectory(profile, directory);
        }

        /// <summary>
        /// 按方案列表算出每一份方案对应的文件夹名（与 Sync 用的规则完全一致）。
        /// 重名时按 Windows 新建文件夹的做法顺延成“名字（2）”。
        /// </summary>
        public static List<string> BuildFolderNames(IList<StartupProfile> profiles)
        {
            return PlanFolderName.BuildFolderNames(profiles, NameOf, "工作方案");
        }

        /// <summary>
        /// 用户手动删掉了某份方案的文件夹时，把它从方案清单里去掉（用户要求：
        /// “能够直接删除对应的文件，达到能够手动清除数据方法”）。
        ///
        /// **安全网**：只有当“工作方案”这个父文件夹存在、而且里面**至少还有一个**方案文件夹时
        /// 才会按文件夹删方案。这样刚装好程序（一个文件夹都没有）、或者整个文件夹被清空时，
        /// 程序不会把清单里的方案一口气全删掉。
        /// 返回被去掉的方案名（没有变化就返回空列表）。
        /// </summary>
        public static List<string> RemoveProfilesWhoseFolderWasDeleted(IList<StartupProfile> profiles)
        {
            List<StartupProfile> taken = TakeProfilesWhoseFolderWasDeleted(profiles);
            List<string> removed = new List<string>();
            for (int index = 0; index < taken.Count; index++)
            {
                removed.Add(taken[index] == null ? "工作方案" : (taken[index].Name ?? "工作方案"));
            }
            return removed;
        }

        /// <summary>
        /// 与 <see cref="RemoveProfilesWhoseFolderWasDeleted"/> 同一条规则，但返回**被去掉的方案本体**
        /// （不是名字），让调用方可以先把它们放进回收站——这样“文件夹被删”也不会真的丢数据。
        /// 2026-10-16 加固：以前这条规则是直接丢掉，用户没法找回。
        /// </summary>
        public static List<StartupProfile> TakeProfilesWhoseFolderWasDeleted(IList<StartupProfile> profiles)
        {
            return Engine.TakeMissingNodes(profiles, AppPaths.StartupProfilesFolderRoot);
        }

        private static string NameOf(StartupProfile profile)
        {
            return profile == null ? null : profile.Name;
        }

        private static string BuildListText(StartupProfile profile)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("工作方案：" + (profile.Name ?? "工作方案"));
            string savedAt = StartupProfileCardText.FormatSavedAt(profile);
            builder.AppendLine("最后保存：" + (savedAt.Length > 0 ? savedAt : "（没有记录）"));
            builder.AppendLine("启动项目：" + (profile.Items == null ? 0 : profile.Items.Count) + " 个");
            builder.AppendLine("────────────────────────────────────────");
            if (profile.Items == null || profile.Items.Count == 0)
            {
                builder.AppendLine("（这份方案里还没有启动项目）");
            }
            else
            {
                for (int index = 0; index < profile.Items.Count; index++)
                {
                    SessionItem item = profile.Items[index];
                    if (item == null)
                    {
                        continue;
                    }
                    builder.AppendLine((index + 1) + ". " + (item.IsEnabled ? "[√] " : "[  ] ") + Describe(item));
                    string detail = DetailOf(item);
                    if (detail.Length > 0)
                    {
                        builder.AppendLine("      " + detail);
                    }
                }
            }
            builder.AppendLine();
            builder.AppendLine("说明：本文件只是方便查看的快照；程序实际使用同目录下的“工作方案.json”。");
            builder.AppendLine("要删掉这份方案：删掉本文件夹，并在“工作方案.json”里删掉对应的一项。");
            return builder.ToString();
        }

        private static string Describe(SessionItem item)
        {
            string name = item.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = item.WindowTitle;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = item.Url;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = item.TargetPath;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "未命名项目";
            }
            return name;
        }

        private static string DetailOf(SessionItem item)
        {
            if (item.Kind == SessionItemKind.WebPage && !string.IsNullOrWhiteSpace(item.Url))
            {
                return item.Url;
            }
            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return item.TargetPath;
            }
            return item.ExecutablePath ?? string.Empty;
        }
    }
}
