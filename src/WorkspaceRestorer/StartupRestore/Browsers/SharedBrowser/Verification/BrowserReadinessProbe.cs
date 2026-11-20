using System;
using System.Collections.Generic;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    internal enum BrowserReadinessState
    {
        /// <summary>窗口标题或任一真实标签标题能对上保存页：算“加载好了”。</summary>
        TitleReady = 0,

        /// <summary>还没读到真实标题：页面没加载完，调用方保持原样并提示。</summary>
        NotReady = 1,

        /// <summary>检查时窗口已经不在。</summary>
        WindowLost = 2
    }

    internal sealed class BrowserReadinessResult
    {
        internal BrowserReadinessState State { get; set; }
        internal int ElapsedMilliseconds { get; set; }
        internal string Detail { get; set; }

        internal bool IsReady
        {
            get { return State == BrowserReadinessState.TitleReady; }
        }
    }

    /// <summary>
    /// 缩窗前的唯一一次检查（方案 R+，2026-11-05 用户确认）。
    ///
    /// 最后一页之后先固定 settle（1.5 秒），然后**只读一次**：
    ///   · 窗口标题或任一真实标签标题能对上保存页 → TitleReady，调用方缩一次；
    ///   · 读不到 → NotReady，调用方保持原样，并在完成提示里说明“页面未就绪”。
    ///
    /// 这里刻意不做循环、不 Sleep、不等 CDP：以前那套“等 CDP /json + 等真实标题
    /// （多页 4+12 秒、单页 18 秒）”在热 Edge 上没有调试端口时会白等十几秒，
    /// 正是用户 2026-11-05 报“怎么还是等那么久”的原因。
    ///
    /// 地址栏只用来把 NotReady 的原因写清楚（“已导航但标题没出来”/“连地址栏都没到”），
    /// 不参与缩窗判断：地址栏在空白引导窗口出现约 110ms 时就已经是目标网址，
    /// 过早缩会被 Edge 加载完再弹回来。
    /// </summary>
    internal static class BrowserReadinessProbe
    {
        internal static BrowserReadinessResult CheckOnce(
            IntPtr windowHandle,
            IList<string> expectedUrls,
            IList<string> expectedTitles)
        {
            DateTime startedUtc = DateTime.UtcNow;
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                return BuildResult(
                    BrowserReadinessState.WindowLost,
                    startedUtc,
                    "检查时浏览器窗口已消失");
            }
            if (HasUiaTitleEvidence(windowHandle, expectedTitles))
            {
                return BuildResult(
                    BrowserReadinessState.TitleReady,
                    startedUtc,
                    "已读到真实标签标题");
            }

            // 地址栏到了不代表页面加载完；只用于解释“为什么没缩”。
            bool addressPresent = HasAddressBarEvidence(windowHandle, expectedUrls);
            return BuildResult(
                BrowserReadinessState.NotReady,
                startedUtc,
                addressPresent
                    ? "地址栏已到目标，但真实标题仍未出现"
                    : "没有读到地址栏或真实标题");
        }

        /// <summary>
        /// 地址栏是否已经到保存网址（只比 host + path，忽略 query/fragment）。
        /// 无协议写法（chatgpt.com）先由 WebAddressTools 补成 http:// 再比较。
        /// </summary>
        internal static bool HasAddressBarEvidence(
            IntPtr windowHandle,
            IList<string> expectedUrls)
        {
            if (expectedUrls == null || expectedUrls.Count == 0)
            {
                return false;
            }
            string address;
            if (!BrowserAddressBarReader.TryReadUrl(windowHandle, out address)
                || string.IsNullOrWhiteSpace(address))
            {
                return false;
            }
            for (int index = 0; index < expectedUrls.Count; index++)
            {
                if (UrlMatches(address, expectedUrls[index]))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool UrlMatches(string actual, string expected)
        {
            string left = NormalizeUrl(actual);
            string right = NormalizeUrl(expected);
            if (left.Length == 0 || right.Length == 0)
            {
                return false;
            }
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Uri leftUri;
            Uri rightUri;
            if (Uri.TryCreate(left, UriKind.Absolute, out leftUri)
                && Uri.TryCreate(right, UriKind.Absolute, out rightUri)
                && string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                string leftPath = (leftUri.AbsolutePath ?? string.Empty).TrimEnd('/');
                string rightPath = (rightUri.AbsolutePath ?? string.Empty).TrimEnd('/');
                // 地址栏常只显示站点根（chatgpt.com）；根路径对上 host 就算导航已发生。
                if (leftPath.Length == 0 || rightPath.Length == 0)
                {
                    return true;
                }
                return leftPath.StartsWith(rightPath, StringComparison.OrdinalIgnoreCase)
                    || rightPath.StartsWith(leftPath, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// 纯 URL 多重集合匹配，测试直接调用这一层，不需要构造 CDP 内部类型。
        /// </summary>
        internal static int CountMatchingUrls(
            IList<string> expectedUrls,
            IList<string> actualUrls)
        {
            if (expectedUrls == null || expectedUrls.Count == 0 || actualUrls == null)
            {
                return 0;
            }

            bool[] used = new bool[actualUrls.Count];
            int matched = 0;
            for (int expectedIndex = 0; expectedIndex < expectedUrls.Count; expectedIndex++)
            {
                string expected = NormalizeUrl(expectedUrls[expectedIndex]);
                if (expected.Length == 0)
                {
                    continue;
                }
                for (int actualIndex = 0; actualIndex < actualUrls.Count; actualIndex++)
                {
                    if (used[actualIndex])
                    {
                        continue;
                    }
                    if (string.Equals(
                        expected,
                        NormalizeUrl(actualUrls[actualIndex]),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        used[actualIndex] = true;
                        matched++;
                        break;
                    }
                }
            }
            return matched;
        }

        internal static string NormalizeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            string text = value.Trim();
            string prepared;
            if (WebAddressTools.TryPrepareWebAddress(text, out prepared)
                && !string.IsNullOrWhiteSpace(prepared))
            {
                text = prepared;
            }
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri))
            {
                return text.TrimEnd('/').ToLowerInvariant();
            }
            string path = (uri.AbsolutePath ?? string.Empty).TrimEnd('/');
            return (uri.Scheme + "://" + uri.Host + path).ToLowerInvariant();
        }

        /// <summary>
        /// 缩前最后一道证据：窗口标题或任一真实标签标题能对上保存页。
        /// 空白引导窗口可能已经“有 2 个标签”，但标题都还是“无标题”；
        /// 这时不能靠“UIA 数到 N 个标签”直接缩，否则 Edge 加载完仍可能把它弹回来。
        /// </summary>
        internal static bool HasUiaTitleEvidence(
            IntPtr windowHandle,
            IList<string> expectedTitles)
        {
            if (windowHandle == IntPtr.Zero
                || expectedTitles == null
                || expectedTitles.Count == 0)
            {
                return false;
            }

            string windowTitle = BrowserWindowLocator.ReadTitle(windowHandle);
            if (BrowserTitleProfile.BrowserWindowTitleMatches(windowTitle, expectedTitles))
            {
                return true;
            }

            List<string> tabTitles;
            int tabCount;
            if (!BrowserWindowVerifier.TryReadTabTitles(
                    windowHandle,
                    out tabTitles,
                    out tabCount))
            {
                return false;
            }
            for (int tabIndex = 0; tabIndex < tabTitles.Count; tabIndex++)
            {
                string tabTitle = tabTitles[tabIndex];
                if (string.IsNullOrWhiteSpace(tabTitle)
                    || BrowserTitleProfile.IsBlankWindowTitle(tabTitle))
                {
                    continue;
                }
                for (int titleIndex = 0; titleIndex < expectedTitles.Count; titleIndex++)
                {
                    if (BrowserTitleProfile.BrowserTitleFragmentsMatch(
                        tabTitle,
                        expectedTitles[titleIndex]))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static BrowserReadinessResult BuildResult(
            BrowserReadinessState state,
            DateTime startedUtc,
            string detail)
        {
            return new BrowserReadinessResult
            {
                State = state,
                ElapsedMilliseconds = (int)Math.Max(
                    0,
                    (DateTime.UtcNow - startedUtc).TotalMilliseconds),
                Detail = detail
            };
        }
    }
}
