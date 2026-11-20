using System;
using System.Collections.Generic;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Infrastructure.PlanMirrors;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【启动时的文件夹收编】把 `workflows\...\` 里“程序还不认识”的文件夹处理掉：
    ///
    ///  · 读得出 方案.json、Id 又不在清单里 → 问一次要不要导入（选「否」就记下来，下次内容变了再问）；
    ///  · 读不出来（没有 方案.json / JSON 坏了 / 没有 Id）→ 搬到 `备份\无法识别\&lt;工作区&gt;\`，
    ///    **只移动，永不删除**，用户随时能去翻。
    ///
    /// 用户要的用法（2026-11-20）：把一整个方案文件夹从别处复制进来，重启程序就能看到。
    /// 以前这里是把清单外的文件夹直接删掉，复制进来的方案会被当成垃圾清掉，所以改成现在这样。
    ///
    /// 只新增、不改已有方案；导入后仍然走原来的“保存清单 + 重建镜像”那条路。
    /// </summary>
    public sealed partial class ApplicationController
    {
        /// <summary>测试/探针用：yes = 直接导入，no = 直接当用户选了「否」，不弹框。</summary>
        private const string IntakeModeEnvironmentVariable = "WORKSPACE_RESTORER_INTAKE";

        private bool mirrorIntakeCompleted;

        /// <summary>启动时收编一次；重复调用不再重复扫。</summary>
        public void RunMirrorIntake()
        {
            if (mirrorIntakeCompleted)
            {
                return;
            }
            mirrorIntakeCompleted = true;
            try
            {
                RunMirrorIntakeCore();
            }
            catch (Exception exception)
            {
                AppLog.Write("收编未登记的方案文件夹失败（不影响程序使用）。", exception);
            }
        }

        private void RunMirrorIntakeCore()
        {
            List<MirrorIntakeFolder<StartupProfile>> profileCandidates =
                new List<MirrorIntakeFolder<StartupProfile>>();
            List<string> profileUnrecognized = new List<string>();
            StartupProfileFolderMirror.ScanUnadopted(
                startupProfiles,
                profileCandidates,
                profileUnrecognized);

            List<MirrorIntakeFolder<ApplicationPlan>> planCandidates =
                new List<MirrorIntakeFolder<ApplicationPlan>>();
            List<string> planUnrecognized = new List<string>();
            ApplicationPlanFolderMirror.ScanUnadopted(
                applicationPlans,
                planCandidates,
                planUnrecognized);

            // 认不出来的先搬走（不删除、不询问）：留着只会让人以为程序坏了。
            MoveUnrecognized(profileUnrecognized, AppPaths.StartupRestoreKind);
            MoveUnrecognized(planUnrecognized, AppPaths.ApplicationsKind);

            HashSet<string> ignored = MirrorIntake.LoadIgnored();
            List<string> ignoredProfiles;
            List<string> ignoredPlans;
            List<MirrorIntakeFolder<StartupProfile>> profilePending = MirrorIntake.ExcludeIgnored(
                profileCandidates,
                AppPaths.StartupRestoreKind,
                ignored,
                out ignoredProfiles);
            List<MirrorIntakeFolder<ApplicationPlan>> planPending = MirrorIntake.ExcludeIgnored(
                planCandidates,
                AppPaths.ApplicationsKind,
                ignored,
                out ignoredPlans);

            int total = profilePending.Count + planPending.Count;
            if (total == 0)
            {
                return;
            }

            if (!AskIntake(total))
            {
                // 用户说「否」：记下这份文件夹现在的样子，下次内容没变就不再打扰。
                for (int index = 0; index < profilePending.Count; index++)
                {
                    ignored.Add(MirrorIntake.Signature(AppPaths.StartupRestoreKind, profilePending[index].Directory));
                }
                for (int index = 0; index < planPending.Count; index++)
                {
                    ignored.Add(MirrorIntake.Signature(AppPaths.ApplicationsKind, planPending[index].Directory));
                }
                MirrorIntake.SaveIgnored(ignored);
                AppLog.Write("发现 " + total + " 个未登记的方案文件夹，本次选择不导入（保持原样）。");
                return;
            }

            if (profilePending.Count > 0)
            {
                MirrorIntake.Adopt(profilePending, startupProfiles);
                PersistProfiles();
                OnPropertyChanged("Profiles");
                OnPropertyChanged("StartupProfiles");
            }
            if (planPending.Count > 0)
            {
                MirrorIntake.Adopt(planPending, applicationPlans);
                PersistApplicationPlans();
                OnPropertyChanged("ApplicationPlans");
            }

            for (int index = 0; index < profilePending.Count; index++)
            {
                ignored.Remove(MirrorIntake.Signature(AppPaths.StartupRestoreKind, profilePending[index].Directory));
            }
            for (int index = 0; index < planPending.Count; index++)
            {
                ignored.Remove(MirrorIntake.Signature(AppPaths.ApplicationsKind, planPending[index].Directory));
            }
            MirrorIntake.SaveIgnored(ignored);
            AppLog.Write("已收编 " + total + " 个未登记的方案文件夹（工作方案 " + profilePending.Count
                + " 份、自启动方案 " + planPending.Count + " 份）。");
        }

        private static void MoveUnrecognized(List<string> directories, string kind)
        {
            if (directories == null || directories.Count == 0)
            {
                return;
            }
            for (int index = 0; index < directories.Count; index++)
            {
                string destination;
                if (MirrorIntake.MoveToUnrecognized(directories[index], kind, out destination))
                {
                    AppLog.Write("认不出来的文件夹已搬到“无法识别”：" + directories[index]
                        + " -> " + destination);
                }
            }
        }

        /// <summary>
        /// 问一次要不要导入。测试模式（或没有界面）默认**不导入、不弹框**：
        ///   WORKSPACE_RESTORER_INTAKE=yes  → 直接当用户选了「是」
        ///   WORKSPACE_RESTORER_INTAKE=no   → 直接当用户选了「否」
        ///   WORKSPACE_RESTORER_INTAKE=ask  → 测试模式下也弹真框（真机探针点按钮用）
        /// 不设变量时：真实运行会弹框；测试模式不弹、按「否」处理。
        /// </summary>
        private static bool AskIntake(int count)
        {
            string forced = Environment.GetEnvironmentVariable(IntakeModeEnvironmentVariable);
            if (string.Equals(forced, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(forced, "no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool askForReal = string.Equals(forced, "ask", StringComparison.OrdinalIgnoreCase);
            bool testMode = string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"),
                "1",
                StringComparison.Ordinal);
            if ((testMode && !askForReal) || System.Windows.Application.Current == null)
            {
                return false;
            }

            return WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                null,
                AppText.T(ShellTextKeys.DialogIntakeTitle),
                AppText.F(ShellTextKeys.DialogIntakeMessageFormat, count),
                AppText.T(ShellTextKeys.DialogIntakeDetail));
        }
    }
}
