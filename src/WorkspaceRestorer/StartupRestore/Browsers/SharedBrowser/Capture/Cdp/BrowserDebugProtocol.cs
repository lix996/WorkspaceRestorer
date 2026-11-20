using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// Reads every tab of a Chromium browser (Chrome/Edge) over its DevTools
    /// Protocol endpoint. A browser started with --remote-debugging-port=N
    /// exposes http://localhost:N/json which lists ALL open page targets with
    /// their title and URL - including background and inactive tabs - without
    /// switching any tab. This is the reliable way to save a full session.
    /// </summary>
    internal static class BrowserDebugProtocol
    {
        internal const int DefaultDebugPort = 9222;

        /// <summary>
        /// Enumerates the page tabs (title + URL) reported by the browser's
        /// debug endpoint. Returns an empty list when no endpoint is reachable
        /// (browser not started with the debug port) so callers always fall
        /// back to the accessibility-based capture.
        /// </summary>
        // A closed local port answers with a refusal that takes ~2 s on some
        // machines, and a browser the user started themselves has no debug port,
        // so an unbounded request used to stall the whole save. Bound it hard and
        // remember the answer briefly: the capture asks once per browser window.
        private const int DebugReadTimeoutMilliseconds = 800;
        // 恢复就绪门必须看到“现在”的标签列表，不能吃上面那 8 秒缓存。
        // 端口拒绝时也要尽快返回，不能让收尾 worker 卡在旧缓存上。
        private const int DebugFreshReadTimeoutMilliseconds = 500;
        private static readonly TimeSpan RecentLifetime = TimeSpan.FromSeconds(8);
        private static readonly object CacheLock = new object();
        private static List<BrowserTabEntry> recentTabs;
        private static int recentPort;
        private static DateTime recentStampUtc = DateTime.MinValue;

        internal static List<BrowserTabEntry> TryReadPageTabs(int port)
        {
            lock (CacheLock)
            {
                if (recentTabs != null
                    && recentPort == port
                    && (DateTime.UtcNow - recentStampUtc) < RecentLifetime)
                {
                    return new List<BrowserTabEntry>(recentTabs);
                }
            }

            bool reachable;
            List<BrowserTabEntry> fresh = ReadPageTabsCore(
                port,
                DebugReadTimeoutMilliseconds,
                out reachable);
            lock (CacheLock)
            {
                recentTabs = fresh;
                recentPort = port;
                recentStampUtc = DateTime.UtcNow;
            }
            return new List<BrowserTabEntry>(fresh);
        }

        /// <summary>
        /// 恢复就绪门专用：绕过 8 秒缓存，直接读当前标签列表。
        /// reachable=false 表示端口不可达；reachable=true 且列表为空表示
        /// 端口是通的，只是此刻还没有 page 目标。两者不能混为一谈。
        /// </summary>
        internal static List<BrowserTabEntry> TryReadPageTabsFresh(
            int port,
            out bool reachable)
        {
            return ReadPageTabsCore(
                port,
                DebugFreshReadTimeoutMilliseconds,
                out reachable);
        }

        private static List<BrowserTabEntry> ReadPageTabsCore(
            int port,
            int timeoutMilliseconds,
            out bool reachable)
        {
            reachable = false;
            List<BrowserTabEntry> result = new List<BrowserTabEntry>();
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json");
                // Never let a configured system proxy intercept localhost, and
                // never wait on a port that simply is not open.
                request.Proxy = null;
                request.KeepAlive = false;
                request.Timeout = timeoutMilliseconds;
                request.ReadWriteTimeout = timeoutMilliseconds;
                string json;
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }
                reachable = true;
                if (string.IsNullOrWhiteSpace(json))
                {
                    return result;
                }

                List<CdpTarget> targets = DeserializeTargets(json);
                if (targets == null)
                {
                    return result;
                }

                for (int index = 0; index < targets.Count; index++)
                {
                    CdpTarget target = targets[index];
                    if (target == null
                        || !string.Equals(target.Type, "page", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(new BrowserTabEntry
                    {
                        Title = string.IsNullOrWhiteSpace(target.Title) ? string.Empty : target.Title,
                        Url = string.IsNullOrWhiteSpace(target.Url) ? string.Empty : target.Url
                    });
                }
            }
            catch
            {
                // The debug endpoint is not reachable; the legacy capture path
                // remains the source of truth.
                reachable = false;
                result.Clear();
            }
            return result;
        }

        /// <summary>
        /// Appends the debug-port switch to a browser command line if it is not
        /// already present. Safe to call on every browser launch: if another
        /// browser process already owns the port, the switch is simply ignored.
        /// </summary>
        internal static string WithDebugPort(string arguments)
        {
            string flag = "--remote-debugging-port=" + DefaultDebugPort.ToString();
            if (arguments == null)
            {
                return flag;
            }

            string trimmed = arguments.Trim();
            if (trimmed.Length == 0)
            {
                return flag;
            }

            if (trimmed.IndexOf("--remote-debugging-port=", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("--remote-debugging-port ", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return arguments;
            }

            return trimmed + " " + flag;
        }

        private static List<CdpTarget> DeserializeTargets(string json)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(List<CdpTarget>));
            using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                try
                {
                    return serializer.ReadObject(stream) as List<CdpTarget>;
                }
                catch
                {
                    return null;
                }
            }
        }

        [DataContract]
        private sealed class CdpTarget
        {
            [DataMember(Name = "type")]
            public string Type { get; set; }

            [DataMember(Name = "title")]
            public string Title { get; set; }

            [DataMember(Name = "url")]
            public string Url { get; set; }
        }

        internal sealed class BrowserTabEntry
        {
            internal string Title { get; set; }
            internal string Url { get; set; }
        }
    }
}
