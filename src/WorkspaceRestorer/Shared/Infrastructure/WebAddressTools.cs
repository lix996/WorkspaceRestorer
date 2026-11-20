using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 浏览器地址栏文本的判定规则（保存网页时判断“这是不是一个网址”，
    /// 恢复网页时判断“这个网址能不能安全地交给浏览器”）。
    ///
    /// 2026-10-15 用户反馈“恢复的时候有问题”时发现：
    /// 保存侧（SessionCaptureService）和恢复侧（ProcessLauncher）**各自抄了一份**
    /// 同样的协议白名单，我修好保存侧之后，恢复侧那份没改，于是
    /// `127.0.0.1:3080` 能存下来、却**恢复不了**（报“网址为空或不支持”）。
    /// 所以这里的规矩是：**这份规则只能有一处**，两边都调这里。
    ///
    /// 【2026-10-18 用户反馈】“复制的网址带有双引号都无法检测到，并添加到方案内”。
    /// 真因：各处入口直接拿原始文本去 `Uri.TryCreate`，而 `"https://x"`、`“https://x”`、
    /// `<https://x>`、`[标题](https://x)` 这些形态都建不出 Uri，于是被判成“不是网址”。
    /// 现在统一先过 <see cref="Normalize"/> 把外层引号/书名号/Markdown 链接剥掉，再做判定；
    /// 各输入入口（粘贴新增、新增网页、从剪贴板导入）都用 <see cref="TryPrepareWebAddress"/>。
    /// </summary>
    internal static class WebAddressTools
    {
        /// <summary>
        /// 剪贴板里是不是「启动与恢复」按 Ctrl+C 复制工作方案时写进去的那段文本？
        ///
        /// 【2026-10-18 用户截图里的真实现场】他在「启动与恢复」复制过一份工作方案（Ctrl+C），
        /// 剪贴板里就留着 `CSK1|启动与恢复{"Id":"p1",...}`；随后到「自启动应用」右键粘贴网址，
        /// 程序把这整段 JSON 当“路径/网址”去判，于是状态栏甩出一整段 JSON，看着像“又失败了”。
        /// 这里只认标记前缀（与 `StartupProfileClipboard.ClipboardHeaderPrefix` 保持同一个值），
        /// 让调用方回一句人话：“这是复制的工作方案，不是网址”。
        /// </summary>
        internal static bool LooksLikeCopiedProfile(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            // 与 Models\StartupProfileClipboard.ClipboardHeaderPrefix 一致（那边是 "CSK1|"）。
            return value.TrimStart().StartsWith("CSK1|", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把“人复制过来的网址文本”整理成干净的网址：
        /// 去零宽字符、去首尾空白、剥掉成对或单边的引号（" ' “ ” ‘ ’ 「 」 《 》 &lt;&gt; ），
        /// 以及把 Markdown 链接 `[标题](地址)` 取出地址。
        /// 整理不了（空）时返回空串，**不抛异常**。
        /// </summary>
        internal static string Normalize(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                // 零宽空格/BOM/方向标记：复制网页上的一段链接时经常混进来。
                if (character == '\u200B' || character == '\u200C' || character == '\u200D'
                    || character == '\uFEFF' || character == '\u2060')
                {
                    continue;
                }
                if (character == '\r' || character == '\n')
                {
                    continue;
                }
                builder.Append(character);
            }
            string text = builder.ToString().Trim();

            // Markdown 链接：[标题](地址) / [标题](<地址>)
            if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith(")", StringComparison.Ordinal))
            {
                Match match = Regex.Match(text, @"^\[[^\]]*\]\(\s*<?([^)\s>]+)>?\s*\)$");
                if (match.Success && match.Groups[1].Value.Length > 0)
                {
                    text = match.Groups[1].Value.Trim();
                }
            }

            // 成对/单边的包裹符号：反复剥，处理 `"'https://x'"` 这种多层。
            for (int guard = 0; guard < 6 && text.Length > 0; guard++)
            {
                bool stripped = false;
                if (text.Length >= 2)
                {
                    char first = text[0];
                    char last = text[text.Length - 1];
                    if (IsMatchingWrapper(first, last))
                    {
                        text = text.Substring(1, text.Length - 2).Trim();
                        stripped = true;
                    }
                }
                if (!stripped && text.Length >= 1)
                {
                    char first = text[0];
                    if (IsOpeningWrapper(first))
                    {
                        text = text.Substring(1).Trim();
                        stripped = true;
                    }
                }
                if (!stripped && text.Length >= 1)
                {
                    char last = text[text.Length - 1];
                    if (IsClosingWrapper(last) || last == '>' || last == '》')
                    {
                        text = text.Substring(0, text.Length - 1).Trim();
                        stripped = true;
                    }
                }
                if (!stripped)
                {
                    break;
                }
            }
            return text.Trim();
        }

        /// <summary>
        /// 把“人复制过来的网址文本”整理成**能直接存、能直接给浏览器**的地址：
        /// 先 <see cref="Normalize"/>，再判定；没写协议的（example.com、127.0.0.1:3080）
        /// 补上 `http://`（以前存原样，交给浏览器也能开，但把它补全可避免“只当搜索词”的歧义）。
        /// 不是网址时返回 false，prepared 为空。
        /// </summary>
        internal static bool TryPrepareWebAddress(string value, out string prepared)
        {
            prepared = Normalize(value);
            if (string.IsNullOrEmpty(prepared) || !IsWebAddress(prepared))
            {
                prepared = string.Empty;
                return false;
            }
            if (LooksLikeSchemeLessAddress(prepared))
            {
                prepared = "http://" + prepared;
            }
            return true;
        }

        /// <summary>
        /// 判断一个地址栏字符串是不是浏览器能打开的网址。
        /// 先认“没有写协议”的写法，再退回标准协议判断——顺序很关键，见下。
        /// </summary>
        internal static bool IsWebAddress(string value)
        {
            string text = Normalize(value);
            if (string.IsNullOrEmpty(text) || text.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            {
                return false;
            }

            // 本机开发的页面在地址栏里就是没有协议的写法（实测 [127.0.0.1:3080]）。
            // 这种写法必须放在 Uri.TryCreate 之前判断，原因（本机 .NET Framework 4.8
            // 实测，用项目同一个 csc.exe 编的小程序跑出来的）：
            //   127.0.0.1:3080        -> TryCreate=False（连 Uri 都建不出来，
            //                            旧代码在这里就直接 return false）
            //   localhost:3000        -> TryCreate=True  但 Scheme="localhost"
            //   example.com/x         -> TryCreate=False
            //   www.example.com/a     -> TryCreate=False
            // 而浏览器打开这些地址本来就等同于 http://。
            if (LooksLikeSchemeLessAddress(text))
            {
                return true;
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                return false;
            }

            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                case "file":
                case "ftp":
                case "about":
                case "edge":
                case "chrome":
                case "brave":
                case "opera":
                case "vivaldi":
                case "moz-extension":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 识别没有写协议的网址，例如 127.0.0.1:3080、localhost:3000、
        /// 192.168.1.10、example.com/index.html。
        /// 要求必须有主机名特征（本机名 / 含点的域名 / 带端口的主机），
        /// 否则网页里任意一段文字都会被误认成网址。
        /// </summary>
        internal static bool LooksLikeSchemeLessAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string candidate = Normalize(value);
            if (candidate.Length == 0)
            {
                return false;
            }
            if (candidate.IndexOfAny(new[] { '\r', '\n', ' ' }) >= 0)
            {
                return false;
            }

            // 先剥掉路径、查询串和锚点，只留下 "主机[:端口]" 这一段。
            string host = candidate;
            int slash = host.IndexOf('/');
            if (slash >= 0)
            {
                host = host.Substring(0, slash);
            }
            int query = host.IndexOfAny(new[] { '?', '#' });
            if (query >= 0)
            {
                host = host.Substring(0, query);
            }

            string hostName = host;
            int colon = host.LastIndexOf(':');
            if (colon >= 0)
            {
                string portText = host.Substring(colon + 1);
                int port;
                if (!int.TryParse(portText, out port) || port <= 0 || port > 65535)
                {
                    return false;
                }
                hostName = host.Substring(0, colon);
            }

            if (string.IsNullOrWhiteSpace(hostName))
            {
                return false;
            }
            if (string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // IPv4 或带点的域名。必须含点，否则普通单词会被误判成网址。
            if (hostName.IndexOf('.') < 0)
            {
                return false;
            }
            for (int index = 0; index < hostName.Length; index++)
            {
                char character = hostName[index];
                bool acceptable = (character >= 'a' && character <= 'z')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character == '.'
                    || character == '-'
                    || character == '_';
                if (!acceptable)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 把网址整理成“只用于比较”的规范形式：去掉协议、默认端口、末尾斜杠，
        /// 解掉 %2F 这类转义，主机名转小写。**不用于保存/打开**，只用于跳转时
        /// 比较“保存的 URL”和“UIA 标签名字里显示的 URL”。
        ///
        /// 例：
        ///   https://keycrop.net/login?redirect=%2Faccount
        ///   keycrop.net/login?redirect=%2Faccount
        /// 两者都得到 keycrop.net/login?redirect=/account。
        /// </summary>
        internal static string CanonicalizeForMatch(string value)
        {
            string text = Normalize(value);
            if (text.Length == 0)
            {
                return string.Empty;
            }

            // 标签名可能带浏览器附加的内存尾巴；先只切已知尾巴，不要动正常网址里的空格。
            int noise = text.IndexOf(" - 内存使用率", StringComparison.OrdinalIgnoreCase);
            if (noise <= 0)
            {
                noise = text.IndexOf(" - 記憶體使用量", StringComparison.OrdinalIgnoreCase);
            }
            if (noise > 0)
            {
                text = text.Substring(0, noise).Trim();
            }

            if (!text.Contains("://") && LooksLikeSchemeLessAddress(text))
            {
                text = "https://" + text;
            }

            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                return string.Empty;
            }

            string scheme = (uri.Scheme ?? string.Empty).ToLowerInvariant();
            if (scheme != "http" && scheme != "https" && scheme != "file"
                && scheme != "ftp" && scheme != "about" && scheme != "edge"
                && scheme != "chrome" && scheme != "brave" && scheme != "opera"
                && scheme != "vivaldi" && scheme != "moz-extension")
            {
                return string.Empty;
            }

            string host = (uri.Host ?? string.Empty).ToLowerInvariant();
            string path = uri.AbsolutePath ?? string.Empty;
            string query = uri.Query ?? string.Empty;
            string fragment = uri.Fragment ?? string.Empty;
            try
            {
                path = Uri.UnescapeDataString(path);
                query = Uri.UnescapeDataString(query);
                fragment = Uri.UnescapeDataString(fragment);
            }
            catch
            {
            }

            // 统一成 host + path + query + fragment；默认端口不要。
            if (scheme != "file"
                && !uri.IsDefaultPort
                && uri.Port > 0
                && !host.Contains(":"))
            {
                host += ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            {
                path = path.TrimEnd('/');
            }
            if (path.Length == 0)
            {
                path = "/";
            }
            return host + path + query + fragment;
        }

        /// <summary>
        /// 标签名字是不是保存网址的同一个页面。标签名字可能是完整 URL、
        /// 省略协议的 URL，或者带内存尾巴；标题不是 URL 时返回 false。
        /// </summary>
        internal static bool TabNameMatchesUrl(string tabName, string savedUrl)
        {
            string wanted = CanonicalizeForMatch(savedUrl);
            string actual = CanonicalizeForMatch(tabName);
            if (string.IsNullOrWhiteSpace(wanted) || string.IsNullOrWhiteSpace(actual))
            {
                return false;
            }
            if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 只把 www. 当作可选前缀再比一次；其他差异不猜。
            string wantedNoWww = RemoveWww(wanted);
            string actualNoWww = RemoveWww(actual);
            return string.Equals(actualNoWww, wantedNoWww, StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveWww(string canonical)
        {
            if (canonical == null)
            {
                return string.Empty;
            }
            return canonical.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? canonical.Substring(4)
                : canonical;
        }

        /// <summary>首尾是不是一对包裹符号（引号/书名号/尖括号/方括号）。</summary>
        private static bool IsMatchingWrapper(char first, char last)
        {
            return (first == '"' && last == '"')
                || (first == '\'' && last == '\'')
                || (first == '“' && last == '”')
                || (first == '‘' && last == '’')
                || (first == '「' && last == '」')
                || (first == '『' && last == '』')
                || (first == '《' && last == '》')
                || (first == '〈' && last == '〉')
                || (first == '<' && last == '>')
                || (first == '[' && last == ']')
                || (first == '【' && last == '】')
                || (first == '（' && last == '）')
                || (first == '(' && last == ')');
        }

        private static bool IsOpeningWrapper(char character)
        {
            return character == '"' || character == '\'' || character == '“' || character == '‘'
                || character == '「' || character == '『' || character == '《' || character == '〈'
                || character == '<' || character == '[' || character == '【'
                || character == '（' || character == '(';
        }

        private static bool IsClosingWrapper(char character)
        {
            // 【注意】**不要**在这里放 ')' ']' '）'：Wikipedia 这类网址本身就带右括号
            //（https://en.wikipedia.org/wiki/Foo_(bar)），当成包裹符号剥掉会把网址弄坏。
            // 只有“首尾成对”的情况（IsMatchingWrapper）才允许剥括号。
            return character == '"' || character == '\'' || character == '”' || character == '’'
                || character == '」' || character == '』' || character == '》' || character == '〉';
        }
    }
}
