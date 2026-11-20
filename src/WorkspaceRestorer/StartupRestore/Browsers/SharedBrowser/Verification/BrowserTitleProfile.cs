using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器窗口标题的纯文本解析，不属于窗口摆位。
    /// 包含：活动标签标题、页数标记、空白窗口判断、标题像不像同一页。
    /// 这里不碰 HWND，不切换标签；UIA 数 TabItem 仍由调用方负责。
    /// </summary>
    internal static class BrowserTitleProfile
    {
        internal static bool TryReadPageCount(string title, out int pageCount)
        {
            pageCount = 0;
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            int marker = title.LastIndexOf(" 和另外 ", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                int index = marker + " 和另外 ".Length;
                while (index < title.Length && title[index] == ' ')
                {
                    index++;
                }
                int start = index;
                while (index < title.Length && char.IsDigit(title[index]))
                {
                    index++;
                }
                if (index > start)
                {
                    int otherPages;
                    if (int.TryParse(
                        title.Substring(start, index - start),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out otherPages)
                        && otherPages >= 0
                        && otherPages < 10000)
                    {
                        pageCount = otherPages + 1;
                        return true;
                    }
                }
                return false;
            }

            int englishMarker;
            if (TryFindEnglishMorePagesMarker(title, out englishMarker))
            {
                int index = englishMarker + " and ".Length;
                while (index < title.Length && title[index] == ' ')
                {
                    index++;
                }
                int start = index;
                while (index < title.Length && char.IsDigit(title[index]))
                {
                    index++;
                }
                int otherPages;
                if (index > start
                    && int.TryParse(
                        title.Substring(start, index - start),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out otherPages)
                    && otherPages >= 0
                    && otherPages < 10000)
                {
                    pageCount = otherPages + 1;
                    return true;
                }
                return false;
            }

            if (BrowserCatalog.LooksLikeBrowserWindowTitle(title)
                && BrowserCatalog.IsChromiumBrowserTitle(title))
            {
                pageCount = 1;
                return true;
            }
            return false;
        }

        /// <summary>找英文 “and N more pages/tabs” 的最后一次完整出现。</summary>
        private static bool TryFindEnglishMorePagesMarker(string title, out int markerStart)
        {
            markerStart = -1;
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }
            int search = title.Length;
            while (search > 0)
            {
                int marker = title.LastIndexOf(
                    " and ",
                    search - 1,
                    StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                {
                    return false;
                }
                int index = marker + " and ".Length;
                while (index < title.Length && title[index] == ' ')
                {
                    index++;
                }
                int digitStart = index;
                while (index < title.Length && char.IsDigit(title[index]))
                {
                    index++;
                }
                if (index > digitStart)
                {
                    int after = index;
                    while (after < title.Length && title[after] == ' ')
                    {
                        after++;
                    }
                    if (StartsWithAt(title, after, "more pages")
                        || StartsWithAt(title, after, "more tabs"))
                    {
                        markerStart = marker;
                        return true;
                    }
                }
                search = marker;
            }
            return false;
        }

        private static bool StartsWithAt(string value, int index, string expected)
        {
            return value != null
                && expected != null
                && index >= 0
                && index + expected.Length <= value.Length
                && string.Compare(
                    value,
                    index,
                    expected,
                    0,
                    expected.Length,
                    StringComparison.OrdinalIgnoreCase) == 0;
        }

        internal static string GetActiveTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }
            string text = Normalize(title);
            text = CutAt(text, " - Microsoft");
            text = CutAt(text, " - Google Chrome");
            text = CutAt(text, " - Mozilla Firefox");
            text = CutAt(text, " - Brave");
            text = CutAt(text, " - Opera");
            text = CutAt(text, " - Vivaldi");
            text = CutAt(text, " - Chromium");
            return text.Trim();
        }

        /// <summary>
        /// “窗口当前活动页”和“保存页”是不是同一页。两边都先取活动标签标题
        /// （去掉“和另外 N 个页面 / 用户配置 / 内存使用率 / 浏览器后缀”）再比，
        /// 允许一边比另一边多一个尾巴（例如 Edge 附加的配置名）。
        /// 只用于“两个窗口都显示同一页”时判断标题是不是真的撞了，不参与打分。
        /// </summary>
        internal static bool SameActivePageTitle(string windowTitle, string savedTitle)
        {
            string left = GetActiveTitle(windowTitle);
            string right = GetActiveTitle(savedTitle);
            if (left.Length < 2 || right.Length < 2)
            {
                return false;
            }
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || (left.Length > right.Length
                    && left.StartsWith(right, StringComparison.OrdinalIgnoreCase))
                || (right.Length > left.Length
                    && right.StartsWith(left, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsBlankWindowTitle(string title)
        {
            int pageCount;
            if (TryReadPageCount(title, out pageCount) && pageCount > 1)
            {
                return false;
            }
            string active = GetActiveTitle(title);
            if (string.IsNullOrWhiteSpace(active))
            {
                return true;
            }
            return string.Equals(active, "新建标签页", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "新标签页", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "New Tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "about:blank", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "Microsoft Edge", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "Google Chrome", StringComparison.OrdinalIgnoreCase)
                || string.Equals(active, "无标题", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 标签名是否是空白新标签页（“新建标签页 / 新标签页 / New Tab / about:blank”）。
        /// 只用于“没读到网址”的标签分类；不把“无标题”算空白，避免误伤真实网页。
        /// </summary>
        internal static bool IsBlankBrowserTabName(string value)
        {
            string text = GetActiveTitle(Normalize(value));
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return string.Equals(text, "新建标签页", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "新标签页", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "New Tab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "about:blank", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>空白新标签页的网址（Edge/Chrome 的新标签页、about:blank）。</summary>
        internal static bool IsBlankNewTabUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string text = value.Trim();
            return string.Equals(text, "about:blank", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "edge://newtab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "edge://newtab/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "edge://new-tab-page", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "edge://new-tab-page/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "chrome://newtab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(text, "chrome://newtab/", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool BrowserTitleFragmentsMatch(string actual, string expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }
            string left = Normalize(actual);
            string right = Normalize(expected);
            if (left.Length < 2 || right.Length < 2)
            {
                return false;
            }
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
                || (left.Length > right.Length
                    && left.StartsWith(right, StringComparison.OrdinalIgnoreCase))
                || (right.Length > left.Length
                    && left.Length >= 3
                    && right.StartsWith(left, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool BrowserTitleMatchesAny(string actual, IList<string> expectedTitles)
        {
            if (string.IsNullOrWhiteSpace(actual) || expectedTitles == null)
            {
                return false;
            }
            for (int index = 0; index < expectedTitles.Count; index++)
            {
                if (BrowserTitleFragmentsMatch(actual, expectedTitles[index]))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool BrowserWindowTitleMatches(string actualTitle, IList<string> expectedTitles)
        {
            if (string.IsNullOrWhiteSpace(actualTitle)
                || expectedTitles == null
                || expectedTitles.Count == 0)
            {
                return false;
            }
            if (!BrowserCatalog.LooksLikeBrowserWindowTitle(actualTitle))
            {
                return false;
            }

            string actual = Normalize(actualTitle);
            if (actual.Length < 2)
            {
                return false;
            }

            for (int index = 0; index < expectedTitles.Count; index++)
            {
                string expected = Normalize(expectedTitles[index]);
                if (expected.Length < 2)
                {
                    continue;
                }
                if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                    || (actual.Length > expected.Length
                        && actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
                    || (expected.Length > actual.Length
                        && actual.Length >= 3
                        && expected.StartsWith(actual, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            return false;
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            text = CutAt(text, " - 内存使用率");
            text = CutAt(text, " 和另外 ");
            int englishMarker;
            if (TryFindEnglishMorePagesMarker(text, out englishMarker) && englishMarker > 0)
            {
                text = text.Substring(0, englishMarker).Trim();
            }
            text = CutAt(text, " - 用户配置 ");
            text = CutAt(text, " - Profile ");
            text = RemoveInvisibleTitleCharacters(text);
            return text.Trim();
        }

        private static string CutAt(string value, string marker)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(marker))
            {
                return value;
            }
            int index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index > 0 ? value.Substring(0, index) : value;
        }

        private static string RemoveInvisibleTitleCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }
            char[] buffer = new char[value.Length];
            int written = 0;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '\u200B'
                    || character == '\u200E'
                    || character == '\u200F'
                    || character == '\uFEFF')
                {
                    continue;
                }
                buffer[written++] = character == '\u00A0' ? ' ' : character;
            }
            return new string(buffer, 0, written);
        }
    }
}
