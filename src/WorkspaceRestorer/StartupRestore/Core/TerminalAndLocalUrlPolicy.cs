using System;
using System.Collections.Generic;
using System.IO;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 「终端 / 本地网址」的识别与取舍（2026-10-19 用户要求）。
    ///
    /// 用户原话：
    ///   “启动与恢复设置那，增加‘扫描终端与本地网址’，默认不勾选，作用：勾选时，扫描终端和本地网址；
    ///    没勾选时，不扫描终端和本地网址。因为启动与恢复无法恢复扫描后的终端和本地网址，例如 dsh 和 comfyui，
    ///    扫描了终端和本地网址，也没法恢复，这种添加终端作为长启动的操作，还是用在自启动应用内。
    ///    恢复先不动，如果用户勾选，那么就在提示中告知用户哪些无法恢复。”
    ///
    /// 所以这里只做两件事：
    ///   ① 判断“这一项是不是终端窗口 / 本地网址”；
    ///   ② 按设置决定“留着（并给出提示）”还是“从方案里去掉（并给出提示）”。
    /// **不碰恢复逻辑**：勾选后加进去的终端/本地网址，恢复时仍按原样处理（多半开不出来），
    /// 由提示告诉用户这些东西应该去「自启动应用」里添加。
    /// </summary>
    public static class TerminalAndLocalUrlPolicy
    {
        /// <summary>常见终端/控制台宿主进程名（小写、不带 .exe）。</summary>
        private static readonly HashSet<string> TerminalProcessNames = new HashSet<string>(
            new[]
            {
                "windowsterminal", "wt", "conhost", "cmd", "powershell", "pwsh", "mintty",
                "alacritty", "wezterm", "wezterm-gui", "hyper", "tabby", "conemu", "conemu64",
                "fluentterm", "terminus", "cmder", "git-bash", "bash", "wsl", "wslhost", "openconsole"
            },
            StringComparer.OrdinalIgnoreCase);

        /// <summary>这一项是不是终端窗口（按可执行文件名判断；快捷方式按目标路径）。</summary>
        public static bool IsTerminalItem(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.Application)
            {
                return false;
            }
            if (IsTerminalExecutable(item.ExecutablePath) || IsTerminalExecutable(item.TargetPath))
            {
                return true;
            }
            // 有些终端会把窗口挂在一个“宿主”进程上（例如 Windows Terminal 的 OpenConsole），
            // 这里再看一眼捕获来源/显示名，避免漏掉。
            if (!string.IsNullOrWhiteSpace(item.CaptureSource)
                && item.CaptureSource.IndexOf("terminal", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            return false;
        }

        /// <summary>可执行文件（含 .lnk 快捷方式路径里的文件名）看起来是不是终端。</summary>
        public static bool IsTerminalExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            string name = null;
            try
            {
                name = Path.GetFileNameWithoutExtension(path.Trim());
            }
            catch
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(name) && TerminalProcessNames.Contains(name);
        }

        /// <summary>
        /// 是不是“本地网址”：`file:`、`localhost`、`127.x`、`[::1]`、`0.0.0.0`、`*.local`。
        /// 这些页面在别的机器/开机时通常打不开（例如 ComfyUI 的 127.0.0.1:8188）。
        ///
        /// 【2026-10-19 用户报 bug 改的】方案里存的地址经常**不带协议** —— 真实数据里就是
        /// `127.0.0.1:8188`（ComfyUI）和 `127.0.0.1:3080`（dsh）。原来的写法直接
        /// `Uri.TryCreate(text, Absolute)`：
        ///   · `127.0.0.1:8188` → 解析失败（scheme 不能以数字开头）→ 认不出来；
        ///   · `localhost:8188` → 被当成 scheme=`localhost` 的地址，Host 是空的 → 也认不出来。
        /// 于是“扫描终端与本地网址”关掉了照样把 ComfyUI 写进方案。现在缺协议时先补 `http://` 再解析。
        /// </summary>
        public static bool IsLocalUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }
            string text = url.Trim();
            Uri uri;
            bool parsed = Uri.TryCreate(text, UriKind.Absolute, out uri);
            // `C:\...`、`file:C:/...` 第一次就能解析成“本地文件地址”，先认下来，
            // 免得下面补 http:// 时把它们弄坏。
            if (parsed && uri != null && uri.IsFile)
            {
                return true;
            }
            if (!parsed || uri == null || string.IsNullOrEmpty(uri.Host))
            {
                // 没写协议（`127.0.0.1:8188` / `localhost:8188` / `[::1]:8188`）：
                // 补一个 http:// 再当网址解析。
                if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out uri))
                {
                    return false;
                }
            }
            if (uri.IsFile)
            {
                return true;
            }
            string host = uri.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }
            // .NET 会把 IPv6 展开成 `[0000:...:0001]` 这种写法，直接比字符串会漏，
            // 所以本机地址用 IsLoopback 判（localhost / 127.x / ::1 都算）。
            if (uri.IsLoopback
                || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (host.StartsWith("127.", StringComparison.Ordinal))
            {
                return true;
            }
            return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>网页项是不是本地网址（Url 或 TargetPath 任一个是）。</summary>
        public static bool IsLocalUrlItem(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.WebPage)
            {
                return false;
            }
            return IsLocalUrl(item.Url) || IsLocalUrl(item.TargetPath);
        }

        /// <summary>
        /// 用户**亲手挑选**的项（扫描桌面勾进来的、从「自启动应用」整份复制过来的，
        /// 都记成 `CaptureSource = DesktopSelection`）。
        /// 这些不是“扫描出来的”，保存时不该被这个开关悄悄删掉 —— 用户点了名要它。
        /// </summary>
        public static bool IsExplicitlyChosen(SessionItem item)
        {
            return item != null
                && !string.IsNullOrWhiteSpace(item.CaptureSource)
                && string.Equals(item.CaptureSource.Trim(), "DesktopSelection", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>这一项属于“终端 / 本地网址”吗（用户亲手挑选的不算）。</summary>
        public static bool IsExcludedKind(SessionItem item)
        {
            if (IsExplicitlyChosen(item))
            {
                return false;
            }
            return IsTerminalItem(item) || IsLocalUrlItem(item);
        }

        /// <summary>
        /// 按设置处理一份快照后的结果：提示语 + 数量（【2026-10-19】提示要“如实说明情况”，
        /// 所以保存完成的横幅/托盘气泡要能说出“跳过了几项、为什么跳过”，不能只会说“有些网页没读到”）。
        /// </summary>
        public struct PolicyOutcome
        {
            /// <summary>要显示给用户的提示；没事可提示时是 null。</summary>
            public string Hint;

            /// <summary>涉及“终端 / 本地网址”的项数（不论保留还是跳过）。</summary>
            public int AffectedCount;

            /// <summary>真正从快照里去掉的项数（勾选时永远是 0）。</summary>
            public int SkippedCount;

            /// <summary>true＝设置里勾了“扫描终端与本地网址”（这些项被保留）；false＝被跳过。</summary>
            public bool Included;
        }

        /// <summary>
        /// 按设置处理一份快照：
        ///   · include == true  → 保留这些项，返回一句“哪些无法恢复”的提示；
        ///   · include == false → 从快照里去掉这些项（浏览器父项若因此没有网页了也一起去掉），返回“已跳过”的提示。
        /// 返回值是要显示给用户的提示；没事可提示时返回 null。
        /// </summary>
        public static string Apply(SessionSnapshot snapshot, bool include)
        {
            return ApplyDetailed(snapshot, include).Hint;
        }

        /// <summary>同 <see cref="Apply"/>，但把“涉及几项 / 跳过几项”也带出来（横幅与托盘气泡要用）。</summary>
        public static PolicyOutcome ApplyDetailed(SessionSnapshot snapshot, bool include)
        {
            PolicyOutcome outcome = new PolicyOutcome();
            outcome.Included = include;
            if (snapshot == null || snapshot.Items == null || snapshot.Items.Count == 0)
            {
                return outcome;
            }
            List<SessionItem> excluded = new List<SessionItem>();
            for (int index = 0; index < snapshot.Items.Count; index++)
            {
                SessionItem item = snapshot.Items[index];
                if (IsExcludedKind(item))
                {
                    excluded.Add(item);
                }
            }
            if (excluded.Count == 0)
            {
                return outcome;
            }
            outcome.AffectedCount = excluded.Count;
            if (include)
            {
                outcome.Hint = BuildIncludeHint(excluded);
                return outcome;
            }
            outcome.SkippedCount = excluded.Count;
            // 不勾选：去掉这些项；顺带清掉“因此变成空壳”的浏览器父项。
            HashSet<string> excludedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < excluded.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(excluded[index].Id))
                {
                    excludedIds.Add(excluded[index].Id);
                }
            }
            HashSet<string> emptiedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < excluded.Count; index++)
            {
                SessionItem item = excluded[index];
                if (item.Kind == SessionItemKind.WebPage && !string.IsNullOrWhiteSpace(item.ParentId))
                {
                    emptiedParents.Add(item.ParentId);
                }
            }
            snapshot.Items.RemoveAll(delegate(SessionItem item)
            {
                if (item == null)
                {
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(item.Id) && excludedIds.Contains(item.Id))
                {
                    return true;
                }
                return false;
            });
            // 浏览器父项自己不是终端，但如果它的网页全被去掉了，也没有保存的价值。
            for (int index = snapshot.Items.Count - 1; index >= 0; index--)
            {
                SessionItem parent = snapshot.Items[index];
                if (parent == null || parent.Kind != SessionItemKind.Application || string.IsNullOrWhiteSpace(parent.Id))
                {
                    continue;
                }
                if (!emptiedParents.Contains(parent.Id))
                {
                    continue;
                }
                bool stillHasChild = false;
                for (int child = 0; child < snapshot.Items.Count; child++)
                {
                    SessionItem candidate = snapshot.Items[child];
                    if (candidate != null
                        && string.Equals(candidate.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        stillHasChild = true;
                        break;
                    }
                }
                if (!stillHasChild)
                {
                    snapshot.Items.RemoveAt(index);
                }
            }
            for (int index = 0; index < snapshot.Items.Count; index++)
            {
                snapshot.Items[index].Sequence = index + 1;
            }
            outcome.Hint = AppText.F(StartupRestoreTextKeys.TerminalSkippedFormat, excluded.Count);
            return outcome;
        }

        private static string BuildIncludeHint(List<SessionItem> excluded)
        {
            const int maxNames = 5;
            List<string> names = new List<string>();
            for (int index = 0; index < excluded.Count && names.Count < maxNames; index++)
            {
                SessionItem item = excluded[index];
                string name = item.DisplayName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = !string.IsNullOrWhiteSpace(item.Url) ? item.Url : item.TargetPath;
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
            string list = string.Empty;
            if (names.Count > 0)
            {
                list = AppText.F(
                    StartupRestoreTextKeys.TerminalListFormat,
                    string.Join(AppText.T(StartupRestoreTextKeys.FallbackNameSeparator), names.ToArray()),
                    excluded.Count > maxNames ? AppText.T(StartupRestoreTextKeys.TerminalMoreSuffix) : string.Empty);
            }
            return AppText.F(StartupRestoreTextKeys.TerminalIncludedFormat, excluded.Count, list);
        }
    }
}
