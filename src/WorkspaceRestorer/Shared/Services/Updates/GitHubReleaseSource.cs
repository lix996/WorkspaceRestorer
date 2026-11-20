using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Services.Updates
{
    /// <summary>
    /// 从 GitHub 公开仓库读取 latest Release。
    /// 只接受 https://github.com/{owner}/{repo} 形式的仓库地址；私有仓库不放 token。
    /// </summary>
    internal static class GitHubReleaseSource
    {
        [DataContract]
        private sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")]
            public string TagName { get; set; }

            [DataMember(Name = "html_url")]
            public string HtmlUrl { get; set; }

            [DataMember(Name = "body")]
            public string Body { get; set; }
        }

        internal static UpdateRelease FetchLatest(string repositoryUrl)
        {
            string apiUrl;
            string error;
            if (!TryBuildApiUrl(repositoryUrl, out apiUrl, out error))
            {
                throw new InvalidOperationException(error);
            }

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
            request.Method = "GET";
            request.UserAgent = "WorkspaceRestorer/" + AppInfo.Version;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;

            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException("GitHub 返回了空响应。");
                    }
                    DataContractJsonSerializer serializer =
                        new DataContractJsonSerializer(typeof(ReleaseDto));
                    ReleaseDto dto = serializer.ReadObject(stream) as ReleaseDto;
                    if (dto == null || string.IsNullOrWhiteSpace(dto.TagName))
                    {
                        throw new InvalidOperationException("GitHub 返回的数据没有版本号。");
                    }
                    string url = dto.HtmlUrl ?? string.Empty;
                    if (!url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
                    {
                        // 不打开非 GitHub 地址，避免把用户带到未知站点。
                        url = string.Empty;
                    }
                    return new UpdateRelease(dto.TagName.Trim(), url, dto.Body ?? string.Empty);
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Close();
                    if (status == 404)
                    {
                        throw new InvalidOperationException("GitHub 仓库或 Release 不存在。");
                    }
                    if (status == 403)
                    {
                        throw new InvalidOperationException("GitHub 请求被限流，请稍后再试。");
                    }
                    throw new InvalidOperationException("GitHub 返回 HTTP " + status + "。");
                }
                throw new InvalidOperationException("无法连接 GitHub：" + exception.Message);
            }
        }

        private static bool TryBuildApiUrl(string repositoryUrl, out string apiUrl, out string error)
        {
            apiUrl = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(repositoryUrl))
            {
                error = "GitHub 仓库未配置。";
                return false;
            }
            string trimmed = repositoryUrl.Trim().TrimEnd('/');
            if (!trimmed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                error = "GitHub 仓库地址必须是 https://github.com/{owner}/{repo}。";
                return false;
            }
            string path = trimmed.Substring("https://github.com/".Length).Trim('/');
            string[] parts = path.Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = "GitHub 仓库地址必须包含 owner 和 repo。";
                return false;
            }
            apiUrl = "https://api.github.com/repos/" + parts[0] + "/" + parts[1] + "/releases/latest";
            return true;
        }
    }

    internal sealed class UpdateRelease
    {
        public string TagName { get; private set; }
        public string HtmlUrl { get; private set; }
        public string ReleaseNotes { get; private set; }

        internal UpdateRelease(string tagName, string htmlUrl, string releaseNotes)
        {
            TagName = tagName ?? string.Empty;
            HtmlUrl = htmlUrl ?? string.Empty;
            ReleaseNotes = releaseNotes ?? string.Empty;
        }
    }
}
