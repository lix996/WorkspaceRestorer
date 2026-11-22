using System;
using System.Reflection;

[assembly: AssemblyTitle("Workspace Restorer")]
[assembly: AssemblyProduct("Workspace Restorer")]
[assembly: AssemblyCompany("lix996")]
[assembly: AssemblyCopyright("Copyright (c) 2026 lix996")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 产品元数据的唯一来源：版本、作者、邮箱、GitHub 地址。
    /// 只放稳定数据，不放界面文案；界面名称由 AppText 提供。
    /// </summary>
    public static class AppInfo
    {
        public const string Version = "1.0.1";

        /// <summary>作者署名和公开邮箱；关于页会显示并启用“写邮件”。</summary>
        public const string AuthorName = "lix996";
        public const string AuthorEmail = "chenmi9906@gmail.com";

        /// <summary>GitHub 仓库；项目主页、检查更新和 Releases 入口共用这个值。</summary>
        public const string GitHubRepositoryUrl = "https://github.com/lix996/WorkspaceRestorer";

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
