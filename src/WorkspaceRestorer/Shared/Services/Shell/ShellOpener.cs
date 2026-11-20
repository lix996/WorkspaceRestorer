using System;
using System.Diagnostics;
using System.IO;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 【公共件】用 Windows Shell 打开一个路径。
    ///
    /// 它既不是“数据和算法”（Core 的职责），也不是 WPF 界面（UI 的职责）：
    /// 这里只做 Process.Start + UseShellExecute 这一个系统边界动作，
    /// 所以放在 Shared\Services\Shell，和 ProcessLauncher 同属“系统边界”这一层。
    ///
    /// 只负责“打开路径”这一类小事：
    ///  · 启动与恢复里“窗口提不到最前时，按原参数再启动一次”；
    ///  · 自启动应用里“打开此桌面项目”；
    ///  · 图标位置保存里“点截图，用默认图片查看器打开”。
    ///
    /// 不接管 ProcessLauncher：恢复项目、浏览器窗口、窗口定位仍走原流程。
    /// 测试/隔离模式下返回 Suppressed，不真的启动任何进程。
    /// </summary>
    public static class ShellOpener
    {
        private const string TestModeVariable = "WORKSPACE_RESTORER_TEST_MODE";
        private const string DisableExternalEffectsVariable = "WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS";

        /// <summary>用系统默认方式打开文件、文件夹、快捷方式或网址。</summary>
        public static ShellOpenResult Open(string target)
        {
            return Open(target, null, null);
        }

        /// <summary>
        /// 用系统默认方式打开目标，可带命令行参数和工作目录。
        /// 不在这里判断目标是否存在：调用方可能传网址、快捷方式或 Shell 命名对象。
        /// </summary>
        public static ShellOpenResult Open(string target, string arguments, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return ShellOpenResult.Failed("目标路径为空。");
            }

            if (ExternalEffectsDisabled())
            {
                return ShellOpenResult.Suppressed();
            }

            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = target.Trim();
                startInfo.UseShellExecute = true;
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    startInfo.Arguments = arguments;
                }
                if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                {
                    startInfo.WorkingDirectory = workingDirectory;
                }
                Process.Start(startInfo);
                return ShellOpenResult.Opened();
            }
            catch (Exception exception)
            {
                return ShellOpenResult.Failed(exception.Message);
            }
        }

        /// <summary>用默认关联程序打开一个文件（例如桌面截图交给默认图片查看器）。</summary>
        public static ShellOpenResult OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ShellOpenResult.Failed("文件路径为空。");
            }
            if (!File.Exists(path))
            {
                return ShellOpenResult.Failed("文件不存在： " + path);
            }
            return Open(path, null, Path.GetDirectoryName(path) ?? string.Empty);
        }

        /// <summary>用资源管理器打开一个文件夹。</summary>
        public static ShellOpenResult OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return ShellOpenResult.Failed("文件夹路径为空。");
            }
            if (!Directory.Exists(path))
            {
                return ShellOpenResult.Failed("文件夹不存在： " + path);
            }
            return Open(path, null, null);
        }

        /// <summary>
        /// 测试或隔离模式：不启动任何外部进程。
        /// 与 ProcessLauncher 使用同一组环境变量，避免出现“有的地方被抑制、有的地方真的打开”。
        /// </summary>
        private static bool ExternalEffectsDisabled()
        {
            if (OsMutationGuard.IsDisabled)
            {
                return true;
            }
            return IsTruthy(TestModeVariable)
                || IsTruthy(DisableExternalEffectsVariable);
        }

        private static bool IsTruthy(string variableName)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
