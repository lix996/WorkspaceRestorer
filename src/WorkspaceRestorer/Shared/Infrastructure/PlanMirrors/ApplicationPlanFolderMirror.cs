using System;
using System.Collections.Generic;
using System.Text;
using WorkspaceRestorer.Core.Cards.PlanFolders.Mirror;
using WorkspaceRestorer.Infrastructure.PlanMirrors;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 把「自启动应用」的方案镜像成“一份方案一个文件夹”，放在
    /// `workflows\自启动应用\自启动方案\<方案名>\` 下面。
    ///
    /// 用户要求（2026-10-10 02:41）：
    /// “启动方案是可以创建文件夹的，数据要放到
    ///   C:\...\zuizhongchanwu\workflows\自启动应用 内的。”
    ///
    /// 与「启动与恢复」的 <see cref="StartupProfileFolderMirror"/> 共用磁盘引擎，但仍是两套数据：
    ///  · 每个方案文件夹里有 `启动清单.txt`（人可读）与 `方案.json`（原始数据）；
    ///  · 文件夹只是快照，权威数据是同目录的 `自启动方案.json`；
    ///  · 例外：用户手动删掉某份方案的文件夹时，下次启动那份方案就从清单里消失
    ///    （带安全网：父文件夹里至少还剩一个方案文件夹才按文件夹删方案）。
    ///
    /// 本类只保留“自启动方案”自己的中文清单文字和公开 API；
    /// 建目录、写入、删除、嵌套遍历在 PlanFolderMirrorEngine，纯命名/路径在 Core。
    /// </summary>
    public static class ApplicationPlanFolderMirror
    {
        private const string ListFileName = "启动清单.txt";
        public const string DataFileName = "方案.json";

        private static readonly PlanFolderMirrorEngine<ApplicationPlan> Engine =
            new PlanFolderMirrorEngine<ApplicationPlan>(
                "自启动方案",
                "自启动方案",
                ListFileName,
                DataFileName,
                NameOf,
                BuildListText);

        public static void Sync(IList<ApplicationPlan> plans)
        {
            Engine.Sync(plans, AppPaths.ApplicationPlansFolderRoot);
        }

        /// <summary>一份自启动方案在镜像树里的完整目录。</summary>
        public static string FolderPathFor(ApplicationPlan plan, IList<ApplicationPlan> allPlans)
        {
            return Engine.FolderPathFor(plan, allPlans, AppPaths.ApplicationPlansFolderRoot);
        }

        /// <summary>找这份自启动方案实际所在的镜像文件夹（删除方案前调用）。</summary>
        public static string FindFolderFor(ApplicationPlan plan, IList<ApplicationPlan> allPlans)
        {
            return Engine.FindFolderFor(plan, allPlans, AppPaths.ApplicationPlansFolderRoot);
        }

        /// <summary>删除一份自启动方案自己的镜像文件夹；调用前必须已经把数据写进回收站。</summary>
        public static bool DeleteFolderAt(string directory)
        {
            return Engine.DeleteFolderAt(directory, AppPaths.ApplicationPlansFolderRoot);
        }

        public static string ReadPlanIdAt(string dataPath)
        {
            return PlanMirrorFile.ReadIdAt<ApplicationPlan>(dataPath);
        }

        /// <summary>读一个目录里的 方案.json（收编用）；读不出来返回 null。</summary>
        internal static ApplicationPlan ReadPlanAtDirectory(string directory)
        {
            return Engine.ReadNodeAt(directory);
        }

        /// <summary>
        /// 扫 `自启动方案\` 里还没登记进来的方案文件夹：
        /// 读得出来、Id 不在清单里的进 <paramref name="adoptable"/>，读不出来的进 <paramref name="unrecognized"/>。
        /// </summary>
        internal static void ScanUnadopted(
            IList<ApplicationPlan> plans,
            List<MirrorIntakeFolder<ApplicationPlan>> adoptable,
            List<string> unrecognized)
        {
            MirrorIntake.Scan(
                plans,
                AppPaths.ApplicationPlansFolderRoot,
                ReadPlanAtDirectory,
                adoptable,
                unrecognized);
        }

        public static void SyncMovedPlan(ApplicationPlan plan, IList<ApplicationPlan> allPlans)
        {
            Engine.SyncMoved(plan, allPlans, AppPaths.ApplicationPlansFolderRoot);
        }

        public static void WritePlanAtDirectory(ApplicationPlan plan, string directory)
        {
            Engine.WriteAtDirectory(plan, directory);
        }

        /// <summary>算每份方案对应的文件夹名（重名按 Windows 新建文件夹的做法顺延成“名字（2）”）。</summary>
        public static List<string> BuildFolderNames(IList<ApplicationPlan> plans)
        {
            return PlanFolderName.BuildFolderNames(plans, NameOf, "自启动方案");
        }

        /// <summary>
        /// 用户手动删掉了某份方案的文件夹 → 把它从清单里去掉。
        /// 安全网：只有父文件夹里**至少还有一个**方案文件夹时才按文件夹删方案。
        /// </summary>
        public static List<string> RemovePlansWhoseFolderWasDeleted(IList<ApplicationPlan> plans)
        {
            List<ApplicationPlan> taken = Engine.TakeMissingNodes(plans, AppPaths.ApplicationPlansFolderRoot);
            List<string> removed = new List<string>();
            for (int index = 0; index < taken.Count; index++)
            {
                removed.Add(taken[index] == null ? "自启动方案" : (taken[index].Name ?? "自启动方案"));
            }
            return removed;
        }

        private static string NameOf(ApplicationPlan plan)
        {
            return plan == null ? null : plan.Name;
        }

        private static string BuildListText(ApplicationPlan plan)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("自启动方案：" + (plan.Name ?? "自启动方案"));
            string savedAt = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(plan.UpdatedAtUtc);
            builder.AppendLine("最后保存：" + (savedAt.Length > 0 ? savedAt : "（没有记录）"));
            builder.AppendLine("启动项目：" + (plan.Items == null ? 0 : plan.Items.Count) + " 个");
            builder.AppendLine("────────────────────────────────────────");
            if (plan.Items == null || plan.Items.Count == 0)
            {
                builder.AppendLine("（这份方案里还没有启动项目）");
            }
            else
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem item = plan.Items[index];
                    if (item == null)
                    {
                        continue;
                    }
                    string name = string.IsNullOrWhiteSpace(item.DisplayName)
                        ? (item.TargetPath ?? "未命名项目")
                        : item.DisplayName;
                    builder.AppendLine((index + 1) + ". " + (item.IsEnabled ? "[√] " : "[  ] ") + name);
                    if (!string.IsNullOrWhiteSpace(item.TargetPath))
                    {
                        builder.AppendLine("      " + item.TargetPath);
                    }
                }
            }
            builder.AppendLine();
            builder.AppendLine("说明：本文件只是方便查看的快照；程序实际使用上一层的“自启动方案.json”。");
            builder.AppendLine("要删掉这份方案：删掉本文件夹（程序下次启动会把它从清单里去掉）。");
            return builder.ToString();
        }
    }
}
