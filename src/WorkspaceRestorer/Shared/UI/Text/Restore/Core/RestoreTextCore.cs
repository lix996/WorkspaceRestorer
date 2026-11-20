using System.Text;

using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 恢复完成文案的公共机制：只拼“成功 X / N、跳过几项、失败几项”这些计数，
    /// 不写任何模块专有句子。模块句子在 StartupRestore/Applications/DesktopLayout
    /// 三个文件夹里。
    /// </summary>
    internal static class RestoreTextCore
    {
        internal static RestoreNoticeText BannerNotice(RestoreCompletion completion)
        {
            if (completion == null)
            {
                return new RestoreNoticeText
                {
                    Summary = string.Empty,
                    Detail = string.Empty,
                    Kind = RestoreNoticeKind.Neutral
                };
            }

            if (completion.IsCancelled)
            {
                if (completion.IsAutoStopped && !string.IsNullOrWhiteSpace(completion.CancelReason))
                {
                    return new RestoreNoticeText
                    {
                        Summary = completion.CancelReason,
                        Detail = completion.CancelReason,
                        Persistent = true,
                        Kind = RestoreNoticeKind.Error
                    };
                }

                string cancelled = AppText.F(
                    RestoreTextKeys.CancelledFormat,
                    completion.SuccessCount,
                    completion.TotalCount);
                return new RestoreNoticeText
                {
                    Summary = cancelled,
                    Detail = cancelled,
                    Kind = RestoreNoticeKind.Neutral
                };
            }

            if (completion.FailedCount > 0)
            {
                StringBuilder summary = new StringBuilder();
                summary.Append(AppText.F(
                    RestoreTextKeys.FailedSummaryFormat,
                    completion.SuccessCount,
                    completion.TotalCount,
                    completion.FailedCount,
                    SummarizeFailures(completion, 3)));
                if (completion.BrowserWindowClosedCount > 0)
                {
                    summary.Append(AppText.F(
                        RestoreTextKeys.ExtraBrowserClosedFormat,
                        completion.BrowserWindowClosedCount));
                }
                if (completion.BrowserWindowUserMinimizedCount > 0)
                {
                    summary.Append(AppText.F(
                        RestoreTextKeys.ExtraBrowserMinimizedFormat,
                        completion.BrowserWindowUserMinimizedCount));
                }
                AppendBrowserWindowNotReady(
                    summary,
                    completion,
                    AppText.T(RestoreTextKeys.ExtraBrowserSeparator));
                return new RestoreNoticeText
                {
                    Summary = summary.ToString(),
                    // 失败清单后面补一句“页面未就绪”的完整说明：横幅只放摘要，
                    // 具体哪一项为什么没恢复在状态栏/悬浮提示里。
                    Detail = BuildFailureReport(completion)
                        + BuildAdviceLine(completion),
                    Kind = RestoreNoticeKind.Error
                };
            }

            string success = BuildSuccessSummary(completion, true);
            if (completion.BrowserWindowNotReadyCount > 0)
            {
                // 页面未就绪不是失败：网页已经打开了，只是没敢替用户缩下去。
                // 用警告色 + 第二行说清“为什么没缩、之后怎么办”，
                // 「启动与恢复」和「自启动应用」共用这一份文案。
                string advice = BrowserWindowNotReadyNotice.Advice(
                    completion.BrowserWindowNotReadyCount);
                return new RestoreNoticeText
                {
                    Summary = success + "\n" + advice,
                    Detail = success + "\n" + advice,
                    Kind = RestoreNoticeKind.Warning
                };
            }
            return new RestoreNoticeText
            {
                Summary = success,
                Detail = success,
                Kind = RestoreNoticeKind.Success
            };
        }

        internal static string TrayText(RestoreCompletion completion)
        {
            if (completion == null)
            {
                return string.Empty;
            }
            if (completion.IsCancelled)
            {
                if (completion.IsAutoStopped && !string.IsNullOrWhiteSpace(completion.CancelReason))
                {
                    return completion.CancelReason;
                }
                return AppText.F(
                    RestoreTextKeys.CancelledFormat,
                    completion.SuccessCount,
                    completion.TotalCount);
            }

            StringBuilder text = new StringBuilder();
            if (completion.FailedCount > 0)
            {
                text.Append(AppText.F(
                    RestoreTextKeys.TrayFailedFormat,
                    completion.SuccessCount,
                    completion.TotalCount,
                    completion.FailedCount));
                AppendUserClosed(text, completion, "；");
                AppendBrowserWindowClosed(text, completion, "；");
                AppendBrowserWindowUserMinimized(text, completion, "；");
                AppendBrowserWindowNotReady(text, completion, "；");
                return text.ToString();
            }

            text.Append(AppText.F(
                RestoreTextKeys.TraySuccessFormat,
                completion.SuccessCount,
                completion.TotalCount));
            if (completion.BrowserWindowNotReadyCount <= 0)
            {
                // 有“页面未就绪”时不能再写“全部恢复完成”，否则后半句自相矛盾。
                text.Append(AppText.T(RestoreTextKeys.AllRestoredSuffix));
            }
            AppendUserClosed(text, completion, "；");
            AppendBrowserWindowClosed(text, completion, "；");
            AppendBrowserWindowUserMinimized(text, completion, "；");
            AppendBrowserWindowNotReady(text, completion, "；");
            return text.ToString();
        }

        internal static string StatusText(RestoreCompletion completion)
        {
            if (completion == null)
            {
                return string.Empty;
            }
            if (completion.IsCancelled)
            {
                if (completion.IsAutoStopped && !string.IsNullOrWhiteSpace(completion.CancelReason))
                {
                    return completion.CancelReason;
                }
                return AppText.T(RestoreTextKeys.StatusCancelled);
            }

            StringBuilder text = new StringBuilder();
            text.Append(AppText.F(
                RestoreTextKeys.StatusBaseFormat,
                completion.SuccessCount,
                completion.TotalCount));
            if (completion.FailedCount > 0)
            {
                text.Append(AppText.F(
                    RestoreTextKeys.StatusFailedSuffix,
                    completion.FailedCount));
            }
            AppendBrowserWindowClosed(text, completion, "，");
            AppendBrowserWindowUserMinimized(text, completion, "，");
            AppendBrowserWindowNotReady(text, completion, "，");
            return text.ToString();
        }

        internal static string BuildSuccessSummary(RestoreCompletion completion, bool includeAlreadyOpen)
        {
            StringBuilder text = new StringBuilder();
            text.Append(AppText.F(
                RestoreTextKeys.StatusBaseFormat,
                completion.SuccessCount,
                completion.TotalCount));
            if (includeAlreadyOpen && completion.AlreadyOpenCount > 0)
            {
                text.Append(AppText.F(
                    RestoreTextKeys.AlreadyOpenSuffix,
                    completion.AlreadyOpenCount));
            }
            AppendUserClosed(text, completion, "，");
            AppendBrowserWindowClosed(text, completion, "，");
            AppendBrowserWindowUserMinimized(text, completion, "，");
            AppendBrowserWindowNotReady(text, completion, "，");
            return text.ToString();
        }

        internal static string SummarizeFailures(RestoreCompletion completion, int maximumNames)
        {
            if (completion == null || completion.Failures == null || completion.Failures.Count == 0)
            {
                return AppText.T(RestoreTextKeys.SummaryNoDetail);
            }

            StringBuilder builder = new StringBuilder();
            int shown = 0;
            for (int index = 0; index < completion.Failures.Count && shown < maximumNames; index++)
            {
                RestoreFailure failure = completion.Failures[index];
                if (failure == null)
                {
                    continue;
                }
                if (shown > 0)
                {
                    builder.Append(AppText.T(RestoreTextKeys.FailureSeparator));
                }
                if (!string.IsNullOrWhiteSpace(failure.Reason))
                {
                    builder.Append(AppText.F(
                        RestoreTextKeys.FailureNameWithReasonFormat,
                        failure.Name,
                        failure.Reason));
                }
                else
                {
                    builder.Append(failure.Name);
                }
                shown++;
            }
            if (completion.Failures.Count > shown)
            {
                builder.Append(AppText.F(
                    RestoreTextKeys.FailuresMoreFormat,
                    completion.Failures.Count));
            }
            return builder.ToString();
        }

        internal static string BuildFailureReport(RestoreCompletion completion)
        {
            if (completion == null || completion.Failures == null || completion.Failures.Count == 0)
            {
                return AppText.T(RestoreTextKeys.FailureNone);
            }

            StringBuilder builder = new StringBuilder();
            builder.Append(AppText.F(
                RestoreTextKeys.FailureReportHeaderFormat,
                completion.Failures.Count));
            for (int index = 0; index < completion.Failures.Count; index++)
            {
                RestoreFailure failure = completion.Failures[index];
                if (failure == null)
                {
                    continue;
                }
                builder.Append(AppText.F(RestoreTextKeys.FailureItemFormat, index + 1));
                if (!string.IsNullOrWhiteSpace(failure.KindText))
                {
                    builder.Append(AppText.F(
                        RestoreTextKeys.FailureKindFormat,
                        failure.KindText));
                }
                builder.Append(failure.Name);
                if (!string.IsNullOrWhiteSpace(failure.Reason))
                {
                    builder.Append(AppText.F(
                        RestoreTextKeys.FailureReasonFormat,
                        failure.Reason));
                }
                if (index + 1 < completion.Failures.Count)
                {
                    builder.Append(AppText.T(RestoreTextKeys.FailureDetailSeparator));
                }
            }
            return builder.ToString();
        }

        private static void AppendUserClosed(
            StringBuilder text,
            RestoreCompletion completion,
            string separator)
        {
            if (completion.UserClosedCount > 0)
            {
                text.Append(separator)
                    .Append(AppText.F(
                        RestoreTextKeys.UserClosedFormat,
                        completion.UserClosedCount));
            }
        }

        private static void AppendBrowserWindowClosed(
            StringBuilder text,
            RestoreCompletion completion,
            string separator)
        {
            if (completion.BrowserWindowClosedCount > 0)
            {
                text.Append(separator)
                    .Append(AppText.F(
                        RestoreTextKeys.BrowserClosedFormat,
                        completion.BrowserWindowClosedCount));
            }
        }

        private static void AppendBrowserWindowUserMinimized(
            StringBuilder text,
            RestoreCompletion completion,
            string separator)
        {
            if (completion.BrowserWindowUserMinimizedCount > 0)
            {
                text.Append(separator)
                    .Append(AppText.F(
                        RestoreTextKeys.BrowserMinimizedFormat,
                        completion.BrowserWindowUserMinimizedCount));
            }
        }

        /// <summary>
        /// 保存为最小化、但期限内没读到真实标题的单页窗口：窗口被有意留在普通状态。
        /// 句子放在 Models/BrowserWindowNotReadyNotice，浏览器实现和提示共用一份。
        /// </summary>
        private static void AppendBrowserWindowNotReady(
            StringBuilder text,
            RestoreCompletion completion,
            string separatorPrefix)
        {
            if (completion.BrowserWindowNotReadyCount > 0)
            {
                text.Append(separatorPrefix)
                    .Append(BrowserWindowNotReadyNotice.Summary(
                        completion.BrowserWindowNotReadyCount));
            }
        }

        /// <summary>“为什么没缩、之后怎么办”的完整说明；没有未就绪窗口时为空。</summary>
        private static string BuildAdviceLine(RestoreCompletion completion)
        {
            if (completion.BrowserWindowNotReadyCount <= 0)
            {
                return string.Empty;
            }
            return "\n" + BrowserWindowNotReadyNotice.Advice(
                completion.BrowserWindowNotReadyCount);
        }
    }
}
