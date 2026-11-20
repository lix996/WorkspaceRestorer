using System;
using System.IO;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 把旧版目录结构一次性搬到新结构（workflows\三个工作区 + 回收站\三段 + 设置\）。
    ///
    /// 用户要求（2026-10-08）“保存工作的方案在合适的文件夹处新建一个 workflows 文件夹，
    /// 里面保存对应工作方案的数据位置（分三段），回收站的数据也在里面，也要分成三段”，
    /// 这样可以直接删掉对应文件夹来手动清数据。
    ///
    /// 只在“新位置还没有文件、旧位置有文件”时搬一次，并在搬之前把旧文件复制到
    /// `备份\搬家前的旧文件\`，万一搬坏了还能拿回来。整个过程不抛异常。
    /// </summary>
    internal static class DataLayoutMigrator
    {
        public static void Migrate()
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                MoveFileIfNeeded(AppPaths.LegacyStartupProfilesFile, AppPaths.StartupProfilesFile);
                MoveFileIfNeeded(AppPaths.LegacyDesktopLayoutFile, AppPaths.DesktopLayoutFile);
                MoveFileIfNeeded(AppPaths.LegacySettingsFile, AppPaths.SettingsFile);
                MoveFileIfNeeded(AppPaths.LegacySessionFile, AppPaths.SessionFile);
                MoveFileIfNeeded(AppPaths.LegacyWindowStateFile, AppPaths.WindowStateFile);
                // 回收站原来是一个 json，现在改成“每段一个文件夹 + 每条一个文件夹”，
                // 由 RecycleBinFolderStore 负责拆分，这里只负责把旧文件留到数据根下不动。
            }
            catch (Exception exception)
            {
                AppLog.Write("数据目录搬家失败（程序会继续用新结构运行）。", exception);
            }
        }

        private static void MoveFileIfNeeded(string source, string target)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                {
                    return;
                }
                if (!File.Exists(source) || File.Exists(target))
                {
                    return;
                }
                string targetDirectory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }
                // 先备份再搬：用户的数据只有一份，宁可多留一个副本。
                try
                {
                    string backupDirectory = AppPaths.LegacyBackupDirectory;
                    Directory.CreateDirectory(backupDirectory);
                    string backupPath = Path.Combine(backupDirectory, Path.GetFileName(source));
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(source, backupPath, false);
                    }
                }
                catch
                {
                }
                File.Move(source, target);
                AppLog.Write("已把 " + Path.GetFileName(source) + " 搬到 " + target);
            }
            catch (Exception exception)
            {
                AppLog.Write("搬迁 " + source + " 失败（不影响程序继续运行）。", exception);
            }
        }
    }
}
