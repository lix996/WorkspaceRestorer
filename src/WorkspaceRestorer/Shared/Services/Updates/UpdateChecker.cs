using System;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Services.Updates
{
    /// <summary>
    /// 检查更新：只比较版本和返回下载页，不自动下载、不自动安装。
    /// 网络取数在 GitHubReleaseSource；本类只做配置判断、版本比较和结果归类。
    /// </summary>
    internal static class UpdateChecker
    {
        internal static bool IsConfigured
        {
            get { return AppInfo.IsGitHubConfigured; }
        }

        internal static UpdateResult Check(string currentVersion)
        {
            string version = string.IsNullOrWhiteSpace(currentVersion)
                ? AppInfo.Version
                : currentVersion.Trim();
            if (!IsConfigured)
            {
                return UpdateResult.NotConfigured(version);
            }
            if (IsTestMode())
            {
                return UpdateResult.Suppressed(version);
            }
            try
            {
                UpdateRelease release = GitHubReleaseSource.FetchLatest(AppInfo.GitHubRepositoryUrl);
                string latest = NormalizeVersion(release.TagName);
                Version current;
                Version latestVersion;
                if (!TryParseVersion(version, out current)
                    || !TryParseVersion(latest, out latestVersion))
                {
                    return UpdateResult.Failed(version, "无法解析版本号：" + release.TagName);
                }
                if (latestVersion.CompareTo(current) > 0)
                {
                    return UpdateResult.UpdateAvailable(
                        version, latest, release.HtmlUrl, release.ReleaseNotes);
                }
                return UpdateResult.UpToDate(version, latest);
            }
            catch (Exception exception)
            {
                return UpdateResult.Failed(version, exception.Message);
            }
        }

        /// <summary>去掉 v 前缀和预发布/构建元数据，只保留数字版本。</summary>
        internal static string NormalizeVersion(string tag)
        {
            string text = (tag ?? string.Empty).Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(1).Trim();
            }
            int cut = text.IndexOfAny(new[] { '-', '+' });
            if (cut >= 0)
            {
                text = text.Substring(0, cut).Trim();
            }
            return text;
        }

        internal static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            string normalized = NormalizeVersion(text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }
            return Version.TryParse(normalized, out version);
        }

        private static bool IsTestMode()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"),
                "1",
                StringComparison.Ordinal);
        }
    }
}
