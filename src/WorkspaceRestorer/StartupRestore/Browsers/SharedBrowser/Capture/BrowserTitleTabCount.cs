using System;
using System.Text.RegularExpressions;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 从浏览器窗口标题猜标签数，只在 UIA 完全读不到 TabItem 时使用。
    /// 不同浏览器的本地化标题都可能有自己的页数格式，所以这里用模式匹配，
    /// 不绑定 Edge/Chrome 的固定后缀。
    /// </summary>
    internal static class BrowserTitleTabCount
    {
        internal static int Parse(string windowTitle)
        {
            if (string.IsNullOrWhiteSpace(windowTitle))
            {
                return 0;
            }

            string[] patterns = new[]
            {
                "(?:和|还有|及)\\s*(?:另外|其他|其余)?\\s*(?<count>\\d+)\\s*(?:个页面|个标签页|个标签)",
                "(?:and|plus)\\s*(?<count>\\d+)\\s*(?:more\\s*)?(?:pages?|tabs?)",
                "(?<count>\\d+)\\s*(?:more\\s*)?(?:pages?|tabs?)"
            };
            for (int index = 0; index < patterns.Length; index++)
            {
                try
                {
                    Match match = Regex.Match(
                        windowTitle,
                        patterns[index],
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!match.Success)
                    {
                        continue;
                    }

                    int additional;
                    if (int.TryParse(match.Groups["count"].Value, out additional)
                        && additional >= 0
                        && additional < 1024)
                    {
                        return additional + 1;
                    }
                }
                catch
                {
                }
            }

            return 0;
        }
    }
}
