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

            // 系统代理可能被代理服务商限流，或返回不属于 GitHub 的 403。
            // 如果代理失败，再走一次直连；两条路都失败才把两边的原因一起报出来。
            string proxyError = null;
            IWebProxy systemProxy = GetSystemProxy(apiUrl);
            if (systemProxy != null)
            {
                try
                {
                    return Fetch(apiUrl, systemProxy);
                }
                catch (InvalidOperationException exception)
                {
                    proxyError = exception.Message;
                }
            }

            try
            {
                return Fetch(apiUrl, null);
            }
            catch (InvalidOperationException exception)
            {
                if (string.IsNullOrWhiteSpace(proxyError))
                {
                    throw;
                }
                throw new InvalidOperationException(
                    "系统代理：" + proxyError + "；直连：" + exception.Message);
            }
        }

        private static UpdateRelease Fetch(string apiUrl, IWebProxy proxy)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
            request.Method = "GET";
            request.UserAgent = "WorkspaceRestorer/" + AppInfo.Version;
            request.Accept = "application/vnd.github+json";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            request.Proxy = proxy;

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
                throw DescribeRequestFailure(exception);
            }
        }

        private static InvalidOperationException DescribeRequestFailure(WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            if (response == null)
            {
                return new InvalidOperationException("无法连接 GitHub：" + exception.Message);
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                string body = ReadResponseBody(response);
                if (status == 404)
                {
                    return new InvalidOperationException("GitHub 仓库或 Release 不存在。");
                }
                if (status == 403)
                {
                    if (IsRateLimited(
                        response.Headers["X-RateLimit-Remaining"],
                        body))
                    {
                        return new InvalidOperationException("GitHub 请求被限流，请稍后再试。");
                    }
                    return new InvalidOperationException(
                        "GitHub 返回 HTTP 403（可能是网络代理拦截），请检查网络或代理。");
                }
                return new InvalidOperationException("GitHub 返回 HTTP " + status + "。");
            }
        }

        private static IWebProxy GetSystemProxy(string apiUrl)
        {
            try
            {
                IWebProxy proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null || proxy.IsBypassed(new Uri(apiUrl)))
                {
                    return null;
                }
                WebProxy webProxy = proxy as WebProxy;
                if (webProxy != null && webProxy.Address == null)
                {
                    return null;
                }
                return proxy;
            }
            catch
            {
                // 读代理设置失败时不能挡住直连。
                return null;
            }
        }

        /// <summary>GitHub 限流会带 X-RateLimit-Remaining: 0；其他 403 更可能是代理拦截。</summary>
        internal static bool IsRateLimited(string remaining, string responseBody)
        {
            if (string.Equals(
                (remaining ?? string.Empty).Trim(),
                "0",
                StringComparison.Ordinal))
            {
                return true;
            }
            return !string.IsNullOrEmpty(responseBody)
                && responseBody.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadResponseBody(HttpWebResponse response)
        {
            try
            {
                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return string.Empty;
                    }
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        char[] buffer = new char[2048];
                        int count = reader.Read(buffer, 0, buffer.Length);
                        return count > 0 ? new string(buffer, 0, count) : string.Empty;
                    }
                }
            }
            catch
            {
                return string.Empty;
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
