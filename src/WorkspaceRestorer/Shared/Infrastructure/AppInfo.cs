using System;
using System.Reflection;

[assembly: AssemblyTitle("Workspace Restorer")]
[assembly: AssemblyProduct("Workspace Restorer")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 产品元数据的唯一来源：版本、作者、邮箱、GitHub 地址。
    /// 只放稳定数据，不放界面文案；界面名称由 AppText 提供。
    /// </summary>
    public static class AppInfo
    {
        public const string Version = "1.0.0";

        /// <summary>作者署名先留空；关于页显示“未配置”。</summary>
        public const string AuthorName = "";

        /// <summary>作者邮箱先留空；写邮件按钮会置灰。</summary>
        public const string AuthorEmail = "";

        /// <summary>GitHub 仓库先留空；检查更新和 GitHub 按钮会置灰。</summary>
        public const string GitHubRepositoryUrl = "";

        public static bool IsGitHubConfigured
        {
            get { return !string.IsNullOrWhiteSpace(GitHubRepositoryUrl); }
        }

        public static bool IsEmailConfigured
        {
            get { return !string.IsNullOrWhiteSpace(AuthorEmail); }
        }

        public static bool IsAuthorConfigured
        {
            get { return !string.IsNullOrWhiteSpace(AuthorName); }
        }

        public static string GitHubReleasesUrl
        {
            get
            {
                if (!IsGitHubConfigured)
                {
                    return string.Empty;
                }
                return GitHubRepositoryUrl.TrimEnd('/') + "/releases";
            }
        }

        public static string DisplayVersion
        {
            get { return "v" + Version; }
        }
    }
}
