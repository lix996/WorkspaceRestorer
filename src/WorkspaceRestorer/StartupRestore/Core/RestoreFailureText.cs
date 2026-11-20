using System;
using System.Collections.Generic;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 把“恢复失败的条目”翻译成用户看得懂的中文。
    ///
    /// 用户 2026-10-15 要求：
    /// “显示某些无法恢复，要在提示上说清楚到底是哪些没有恢复（以及为什么无法恢复，你要检查）”。
    ///
    /// 放在公共模型里而不是控制器私有方法里，是因为它是纯格式化逻辑、
    /// 需要被单元测试直接覆盖（恢复一次真机代价太大，没法每次都跑）。
    /// </summary>
    public static class RestoreFailureText
    {
        /// <summary>
        /// 从恢复结果里挑出“没成功、也不是被抑制”的条目，逐条给出名字、类型和原因。
        /// </summary>
        public static List<RestoreFailure> Describe(IList<RestoreItemResult> results)
        {
            List<RestoreFailure> failures = new List<RestoreFailure>();
            if (results == null)
            {
                return failures;
            }

            for (int index = 0; index < results.Count; index++)
            {
                RestoreItemResult item = results[index];
                if (item == null || item.Success || item.WasSuppressed)
                {
                    continue;
                }
                failures.Add(new RestoreFailure
                {
                    Name = DescribeItem(item.Item),
                    KindText = DescribeKind(item.Item),
                    Reason = DescribeReason(item)
                });
            }
            return failures;
        }

        /// <summary>给“没能恢复”的条目取一个用户认得出的名字。</summary>
        public static string DescribeItem(SessionItem item)
        {
            if (item == null)
            {
                return AppText.T(StartupRestoreTextKeys.FallbackUnknownItem);
            }
            if (!string.IsNullOrWhiteSpace(item.DisplayName))
            {
                return item.DisplayName.Trim();
            }
            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return item.TargetPath.Trim();
            }
            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                return item.Url.Trim();
            }
            if (!string.IsNullOrWhiteSpace(item.ExecutablePath))
            {
                return item.ExecutablePath.Trim();
            }
            return AppText.T(StartupRestoreTextKeys.FallbackUnnamedItem);
        }

        public static string DescribeKind(SessionItem item)
        {
            if (item == null)
            {
                return AppText.T(StartupRestoreTextKeys.FallbackProjectFallback);
            }
            switch (item.Kind)
            {
                case SessionItemKind.Application:
                    return string.IsNullOrWhiteSpace(item.BrowserGroupKey)
                        ? AppText.T(StartupRestoreTextKeys.FallbackApplicationKind)
                        : AppText.T(StartupRestoreTextKeys.FallbackBrowserWindowFallback);
                case SessionItemKind.WebPage:
                    return AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback);
                case SessionItemKind.ExplorerFolder:
                    return AppText.T(StartupRestoreTextKeys.FallbackFolderKind);
                case SessionItemKind.Document:
                    return AppText.T(StartupRestoreTextKeys.FallbackFileKind);
                default:
                    return AppText.T(StartupRestoreTextKeys.FallbackProjectFallback);
            }
        }

        /// <summary>
        /// 把启动器给的英文原因翻译成中文，并说清怎么办。
        /// 【2026-10-19】拆出 <see cref="DescribeError"/>：自启动应用的失败原因也要用同一套中文说法
        /// （用户要求“自启动应用也要同步更新此类问题”，那边原来直接把英文错误贴到横幅上）。
        /// </summary>
        public static string DescribeReason(RestoreItemResult item)
        {
            // 【2026-10-19】终端窗口 / 本地网址：这类东西「启动与恢复」本来就恢复不了。
            // 勾了「扫描终端与本地网址」时它们会被保存进方案，恢复时必须如实说清原因，
            // 而不是丢一句启动器的英文错误。
            if (item != null && TerminalAndLocalUrlPolicy.IsExcludedKind(item.Item))
            {
                return AppText.T(StartupRestoreTextKeys.FailureTerminalReason);
            }
            string error = item == null || string.IsNullOrWhiteSpace(item.Error)
                ? null
                : item.Error.Trim();
            return DescribeError(error);
        }

        /// <summary>把启动器给的英文错误翻成中文（没有错误信息时给一句中性的“原因不明”）。</summary>
        public static string DescribeError(string rawError)
        {
            string error = string.IsNullOrWhiteSpace(rawError) ? null : rawError.Trim();
            if (string.IsNullOrWhiteSpace(error))
            {
                return AppText.T(StartupRestoreTextKeys.FailureNoReason);
            }

            // 程序 / 文件夹 / 文件 已经不在原来的位置
            if (error.StartsWith("The application executable does not exist:", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.F(StartupRestoreTextKeys.FailureAppMissingFormat, After(error, "does not exist:"));
            }
            if (error.StartsWith("The Explorer folder does not exist:", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.F(StartupRestoreTextKeys.FailureFolderMissingFormat, After(error, "does not exist:"));
            }
            if (error.StartsWith("The target does not exist:", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.F(StartupRestoreTextKeys.FailureTargetMissingFormat, After(error, "does not exist:"));
            }

            // 旧方案留下的空条目
            if (error.StartsWith("The Explorer folder path is empty.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureEmptyFolderRecord);
            }
            if (error.StartsWith("The application executable path is empty.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureEmptyAppRecord);
            }
            if (error.StartsWith("The saved web page URL is empty or unsupported.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureEmptyWebRecord);
            }
            if (error.StartsWith("Session item is required.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureEmptyRecord);
            }
            if (error.StartsWith("A path is required.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureEmptyPathRecord);
            }
            if (error.StartsWith("The launcher returned no result.", StringComparison.OrdinalIgnoreCase))
            {
                return AppText.T(StartupRestoreTextKeys.FailureNoResult);
            }

            // 权限类
            if (error.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("(5)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return AppText.T(StartupRestoreTextKeys.FailureAccessDenied);
            }

            // 没认出来的：原样给出去，总比“原因不明”有用。
            return error;
        }

        private static string After(string text, string marker)
        {
            int at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return text;
            }
            return text.Substring(at + marker.Length).Trim();
        }
    }
}
