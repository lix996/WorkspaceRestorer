using System;
using System.Collections.Generic;
using System.IO;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Infrastructure.PlanMirrors;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【启动时的文件夹收编】把 `workflows\...\` 里“程序还不认识”的文件夹处理掉：
    ///
    ///  · 读得出 方案.json、Id 又不在清单里 → 三选一：导入 / 暂不导入 / 移到“无法识别”；
    ///  · 读不出来（没有 方案.json / JSON 坏了 / 没有 Id）→ 搬到 `备份\无法识别\&lt;工作区&gt;\`，
    ///    **只移动，永不删除**，用户随时能去翻。
    ///
    /// 用户要的用法（2026-11-20）：把一整个方案文件夹从别处复制进来，重启程序就能看到。
    /// 以前这里是把清单外的文件夹直接删掉，复制进来的方案会被当成垃圾清掉，所以改成现在这样。
    ///
    /// 只新增、不改已有方案；导入后仍然走原来的“保存清单 + 重建镜像”那条路。
    /// 暂不导入只在本次登录会话有效；移到“无法识别”只移动、不删除，也不进程序回收站。
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

            MirrorIntakeChoice choice = AskIntake(total);
            if (choice == MirrorIntakeChoice.Defer)
            {
                DeferUnregistered(profilePending, planPending, ignored, total);
                return;
            }
            if (choice == MirrorIntakeChoice.Quarantine)
            {
                QuarantineUnregistered(profilePending, planPending, ignored);
                return;
            }
            ImportUnregistered(profilePending, planPending, ignored, total);
        }

        /// <summary>用户选「导入」：先落盘，成功后再清掉忽略记录。</summary>
        private void ImportUnregistered(
            List<MirrorIntakeFolder<StartupProfile>> profilePending,
            List<MirrorIntakeFolder<ApplicationPlan>> planPending,
            HashSet<string> ignored,
            int total)
        {
            bool savedAll = true;
            if (profilePending.Count > 0)
            {
                MirrorIntake.Adopt(profilePending, startupProfiles);
                bool saved = PersistProfiles(true);
                OnPropertyChanged("Profiles");
                OnPropertyChanged("StartupProfiles");
                if (saved)
                {
                    for (int index = 0; index < profilePending.Count; index++)
                    {
                        MirrorIntake.Forget(
                            AppPaths.StartupRestoreKind,
                            profilePending[index].Directory,
                            ignored);
                    }
                }
                else
                {
                    savedAll = false;
                }
            }
            if (planPending.Count > 0)
            {
                MirrorIntake.Adopt(planPending, applicationPlans);
                bool saved = PersistApplicationPlans(true);
                OnPropertyChanged("ApplicationPlans");
                if (saved)
                {
                    for (int index = 0; index < planPending.Count; index++)
                    {
                        MirrorIntake.Forget(
                            AppPaths.ApplicationsKind,
                            planPending[index].Directory,
                            ignored);
                    }
                }
                else
                {
                    savedAll = false;
                }
            }

            MirrorIntake.SaveIgnored(ignored);
            if (!savedAll)
            {
                StatusText = AppText.T(ShellTextKeys.StatusIntakeImportFailed);
                AppLog.Write("收编未登记方案时清单写盘失败，本次导入未完成：" + total + " 个候选。");
                return;
            }
            AppLog.Write("已收编 " + total + " 个未登记的方案文件夹（工作方案 " + profilePending.Count
                + " 份、自启动方案 " + planPending.Count + " 份）。");
        }

        /// <summary>用户选「暂不导入」：只记到本次登录会话，不移动、不删除。</summary>
        private void DeferUnregistered(
            List<MirrorIntakeFolder<StartupProfile>> profilePending,
            List<MirrorIntakeFolder<ApplicationPlan>> planPending,
            HashSet<string> ignored,
            int total)
        {
            for (int index = 0; index < profilePending.Count; index++)
            {
                MirrorIntake.Defer(
                    AppPaths.StartupRestoreKind,
                    profilePending[index].Directory,
                    ignored);
            }
            for (int index = 0; index < planPending.Count; index++)
            {
                MirrorIntake.Defer(
                    AppPaths.ApplicationsKind,
                    planPending[index].Directory,
                    ignored);
            }
            MirrorIntake.SaveIgnored(ignored);
            StatusText = AppText.F(ShellTextKeys.StatusIntakeDeferredFormat, total);
            AppLog.Write("发现 " + total + " 个未登记的方案文件夹，本次选择暂不导入（保持原样）。");
        }

        /// <summary>用户选「移到无法识别」：只移动，不删除，也不写程序回收站。</summary>
        private void QuarantineUnregistered(
            List<MirrorIntakeFolder<StartupProfile>> profilePending,
            List<MirrorIntakeFolder<ApplicationPlan>> planPending,
            HashSet<string> ignored)
        {
            List<string> profilePaths = new List<string>();
            List<MirrorIntakeFolder<StartupProfile>> topProfiles =
                MirrorIntake.SelectTopMostFolders(profilePending);
            for (int index = 0; index < topProfiles.Count; index++)
            {
                profilePaths.Add(topProfiles[index].Directory);
            }
            List<string> planPaths = new List<string>();
            List<MirrorIntakeFolder<ApplicationPlan>> topPlans =
                MirrorIntake.SelectTopMostFolders(planPending);
            for (int index = 0; index < topPlans.Count; index++)
            {
                planPaths.Add(topPlans[index].Directory);
            }

            List<string> movedRoots = new List<string>();
            int moved = MoveReadableFolders(profilePaths, AppPaths.StartupRestoreKind, movedRoots);
            moved += MoveReadableFolders(planPaths, AppPaths.ApplicationsKind, movedRoots);

            for (int index = 0; index < profilePending.Count; index++)
            {
                if (IsUnderAny(profilePending[index].Directory, movedRoots))
                {
                    MirrorIntake.Forget(
                        AppPaths.StartupRestoreKind,
                        profilePending[index].Directory,
                        ignored);
                }
            }
            for (int index = 0; index < planPending.Count; index++)
            {
                if (IsUnderAny(planPending[index].Directory, movedRoots))
                {
                    MirrorIntake.Forget(
                        AppPaths.ApplicationsKind,
                        planPending[index].Directory,
                        ignored);
                }
            }

            MirrorIntake.SaveIgnored(ignored);
            StatusText = AppText.F(ShellTextKeys.StatusIntakeMovedFormat, moved);
            AppLog.Write("已把 " + moved + " 个未登记方案文件夹移到“无法识别”（只移动，不删除）。");
        }

        private static int MoveReadableFolders(
            List<string> directories,
            string kind,
            List<string> movedRoots)
        {
            int moved = 0;
            for (int index = 0; index < directories.Count; index++)
            {
                string destination;
                if (MirrorIntake.MoveToUnrecognized(directories[index], kind, out destination))
                {
                    moved++;
                    if (movedRoots != null)
                    {
                        movedRoots.Add(directories[index]);
                    }
                    AppLog.Write("未登记方案已移到“无法识别”：" + directories[index]
                        + " -> " + destination);
                }
            }
            return moved;
        }

        private static bool IsUnderAny(string directory, List<string> roots)
        {
            if (string.IsNullOrWhiteSpace(directory) || roots == null)
            {
                return false;
            }
            string path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            for (int index = 0; index < roots.Count; index++)
            {
                string root = Path.GetFullPath(roots[index]).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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
        /// 问一次怎么处理。测试模式（或没有界面）默认**暂不导入、不弹框**：
        ///   WORKSPACE_RESTORER_INTAKE=yes  → 直接当用户选了「导入」
        ///   WORKSPACE_RESTORER_INTAKE=no   → 直接当用户选了「暂不导入」
        ///   WORKSPACE_RESTORER_INTAKE=move → 直接当用户选了「移到无法识别」
        ///   WORKSPACE_RESTORER_INTAKE=ask  → 测试模式下也弹真框（真机探针点按钮用）
        /// 不设变量时：真实运行会弹框；测试模式不弹、按「暂不导入」处理。
        /// </summary>
        private static MirrorIntakeChoice AskIntake(int count)
        {
            string forced = Environment.GetEnvironmentVariable(IntakeModeEnvironmentVariable);
            if (string.Equals(forced, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return MirrorIntakeChoice.Import;
            }
            if (string.Equals(forced, "no", StringComparison.OrdinalIgnoreCase))
            {
                return MirrorIntakeChoice.Defer;
            }
            if (string.Equals(forced, "move", StringComparison.OrdinalIgnoreCase)
                || string.Equals(forced, "quarantine", StringComparison.OrdinalIgnoreCase))
            {
                return MirrorIntakeChoice.Quarantine;
            }

            bool askForReal = string.Equals(forced, "ask", StringComparison.OrdinalIgnoreCase);
            bool testMode = string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"),
                "1",
                StringComparison.Ordinal);
            if ((testMode && !askForReal) || System.Windows.Application.Current == null)
            {
                return MirrorIntakeChoice.Defer;
            }

            return MirrorIntakeDialog.Ask(null, count);
        }
    }
}
