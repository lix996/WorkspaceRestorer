using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using WorkspaceRestorer.Core;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域 · 2026-10-19】搜索弹窗要搜的“一条东西”：名字 + 时间 + 时间戳。
    ///
    /// 用户原话：
    ///   “自启动应用，里面的搜索代码不是从「启动与恢复」的代码中搬到公共区域一起使用了吗，
    ///    为什么没有搜索内没有对应的「同时按时间搜索」，后续搜索内添加新的功能时，
    ///    例如其他的搜索方法，都要同步到其余两个上的。”
    ///
    /// 所以三个工作区的搜索都只描述“这一条叫什么、什么时候保存/删除”，
    /// 匹配规则、勾选时间的语义、高亮区间**全部**由 <see cref="SearchMatcher"/> 统一决定。
    /// 以后加新的搜索方法只改公共件，三个工作区一起生效，不会再出现“只有某一处有”的情况。
    /// </summary>
    internal sealed class SearchTarget
    {
        /// <summary>名字（参与文字搜索，也参与高亮）。</summary>
        public string Name;

        /// <summary>也参与文字搜索、但不高亮的附注（例如回收站的副标题与所属分区）。</summary>
        public string[] ExtraText;

        /// <summary>界面上显示的时间串（例如“北京时间 2026/10/16日 15:32分”或“2026-10-19 13:51”）。</summary>
        public string TimeText;

        /// <summary>“保存/删除时间”（按时间词搜索时用它，例如「15分」只比这个）。</summary>
        public DateTime SavedUtc;

        /// <summary>最后一次修改时间（纯数字短词也会比它，和历史行为一致）。</summary>
        public DateTime UpdatedUtc;

        /// <summary>创建时间（可空；有的话完整日期写法也能命中）。</summary>
        public DateTime CreatedUtc;
    }

    /// <summary>
    /// 三个工作区**共用同一套**的搜索匹配规则：
    ///  · 空格/逗号分隔的多个词是**并且**关系（每个词都要命中）；
    ///  · 文字：名字（及附注）里不区分大小写的包含匹配，带日期写法时按数字归一化比较；
    ///  · 时间：只有勾上「同时按时间搜索」才比 —— 支持「15分/15时/15日/15月/2026年」这种带单位写法、
    ///    纯数字短词（15＝15 日/月/时/分，26＝2026 年）以及 2026-10-08 / 20261008 / 10-08 这类日期写法；
    ///  · 高亮：给出“名字里哪几段命中、时间串里哪几段命中”，界面按区间加亮。
    ///
    /// 规则原来只写在「启动与恢复」的 `StartupProfileSearch` 里；2026-10-19 用户要求
    /// “后续搜索内添加新的功能时…都要同步到其余两个上”，所以整段搬到公共区，
    /// 三个工作区（工作方案 / 自启动方案 / 回收站）都走这里，`StartupProfileSearch` 只剩一层薄包装。
    /// </summary>
    internal static class SearchMatcher
    {
        /// <summary>“15分 / 15时 / 15点 / 15日 / 15号 / 15月 / 2026年”这种带单位的时间词。</summary>
        private static readonly Regex TimeUnitTerm = new Regex(
            @"^(\d{1,4})\s*(分钟|分|时|点|日|号|月|年)$",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// 把搜索框里的一串字拆成若干“词”：空格（半角/全角）、逗号（半角/中文）都算分隔符。
        /// 多个词之间是**并且**关系：每个词都要命中，这一条才会出现在结果里。
        /// </summary>
        public static IList<string> SplitTerms(string keyword)
        {
            List<string> terms = new List<string>();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return terms;
            }
            string[] parts = keyword.Split(
                new[] { ' ', '\t', '\r', '\n', '　', ',', '，' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index] == null ? string.Empty : parts[index].Trim();
                if (part.Length > 0)
                {
                    terms.Add(part);
                }
            }
            return terms;
        }

        /// <summary>这一条是否匹配关键字；关键字为空（或只有空白）时视为全部匹配。</summary>
        public static bool Matches(SearchTarget target, string keyword, bool includeTime)
        {
            IList<string> terms = SplitTerms(keyword);
            if (terms.Count == 0)
            {
                return true;
            }
            if (target == null)
            {
                return false;
            }
            // 【整串先试一次，再按词“并且”】用户可能把界面上那一整串时间直接粘进来搜（带空格），
            // 如果只按词拆，就会变成多个词、每个都要命中，反而搜不到。
            if (terms.Count > 1 && MatchesTerm(target, keyword == null ? string.Empty : keyword.Trim(), includeTime))
            {
                return true;
            }
            for (int index = 0; index < terms.Count; index++)
            {
                if (!MatchesTerm(target, terms[index], includeTime))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>单个词是否命中：先看名字（含附注），勾了时间再看时间。</summary>
        public static bool MatchesTerm(SearchTarget target, string term, bool includeTime)
        {
            if (target == null)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(term))
            {
                return true;
            }
            string needle = term.Trim();
            if (MatchesName(target, needle))
            {
                return true;
            }
            if (!includeTime)
            {
                return false;
            }
            return MatchesTimeTerm(target, needle);
        }

        /// <summary>一个词是否命中“时间”：带单位的按单位比，纯数字按任一分量比，其余走日期写法。</summary>
        public static bool MatchesTimeTerm(SearchTarget target, string term)
        {
            if (target == null || string.IsNullOrWhiteSpace(term))
            {
                return false;
            }
            string needle = term.Trim();
            Match unit = TimeUnitTerm.Match(needle);
            if (unit.Success)
            {
                int wanted;
                if (!int.TryParse(unit.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out wanted))
                {
                    return false;
                }
                return MatchesTimeUnit(target, unit.Groups[2].Value, wanted);
            }
            if (MatchesTimeComponent(target, needle))
            {
                return true;
            }
            string stamp = target.TimeText ?? string.Empty;
            if (stamp.Length == 0)
            {
                return false;
            }
            return SearchMatch.LooksLikeDate(needle) && MatchesStamp(target, stamp, needle);
        }

        /// <summary>
        /// 算这一条在本次搜索里“哪里命中了”：
        ///  · 名字里命中的词 → 高亮名字里的那几段（附注命中不高亮）；
        ///  · 只靠时间命中的词 → 高亮时间串里的数字（找不到连续数字就把整段日期高亮）。
        /// </summary>
        public static void Highlight(
            SearchTarget target,
            string keyword,
            bool includeTime,
            List<SearchTextRange> nameRanges,
            List<SearchTextRange> timeRanges)
        {
            if (nameRanges == null || timeRanges == null || target == null)
            {
                return;
            }
            IList<string> terms = SplitTerms(keyword);
            string name = target.Name ?? string.Empty;
            string stamp = target.TimeText ?? string.Empty;
            for (int index = 0; index < terms.Count; index++)
            {
                string term = terms[index];
                bool nameHit = name.Length > 0 && SearchMatch.MatchesAny(term, name);
                if (nameHit)
                {
                    AddOccurrences(name, term, nameRanges);
                    continue;
                }
                if (!includeTime || stamp.Length == 0)
                {
                    continue;
                }
                if (!MatchesTimeTerm(target, term))
                {
                    continue;
                }
                string digits = DigitsOnly(term);
                int before = timeRanges.Count;
                if (digits.Length > 0 && AddOccurrences(stamp, digits, timeRanges) > 0)
                {
                    continue;
                }
                if (timeRanges.Count == before)
                {
                    // 数字对不上（例如写成 2026-10-08 这种分段写法）→ 把整段日期时间高亮，
                    // 明确告诉用户“这一条是按时间命中的”。
                    int start = FirstDigitIndex(stamp);
                    if (start >= 0)
                    {
                        timeRanges.Add(new SearchTextRange(start, stamp.Length - start));
                    }
                }
            }
        }

        /// <summary>名字 + 附注里是否出现了这个词。</summary>
        private static bool MatchesName(SearchTarget target, string needle)
        {
            if (target.Name != null && SearchMatch.MatchesAny(needle, target.Name))
            {
                return true;
            }
            if (target.ExtraText != null)
            {
                for (int index = 0; index < target.ExtraText.Length; index++)
                {
                    string extra = target.ExtraText[index];
                    if (!string.IsNullOrEmpty(extra) && SearchMatch.MatchesAny(needle, extra))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>带单位的时间词：`15分` 只比“分”，`15日` 只比“日”……（只比保存/删除时间）。</summary>
        private static bool MatchesTimeUnit(SearchTarget target, string unit, int wanted)
        {
            if (string.Equals(unit, "分", StringComparison.Ordinal)
                || string.Equals(unit, "分钟", StringComparison.Ordinal))
            {
                return LocalTimeSatisfies(target.SavedUtc, delegate(DateTime local) { return local.Minute == wanted; });
            }
            if (string.Equals(unit, "时", StringComparison.Ordinal)
                || string.Equals(unit, "点", StringComparison.Ordinal))
            {
                return LocalTimeSatisfies(target.SavedUtc, delegate(DateTime local) { return local.Hour == wanted; });
            }
            if (string.Equals(unit, "日", StringComparison.Ordinal)
                || string.Equals(unit, "号", StringComparison.Ordinal))
            {
                return LocalTimeSatisfies(target.SavedUtc, delegate(DateTime local) { return local.Day == wanted; });
            }
            if (string.Equals(unit, "月", StringComparison.Ordinal))
            {
                return LocalTimeSatisfies(target.SavedUtc, delegate(DateTime local) { return local.Month == wanted; });
            }
            if (string.Equals(unit, "年", StringComparison.Ordinal))
            {
                return LocalTimeSatisfies(target.SavedUtc, delegate(DateTime local)
                {
                    return local.Year == wanted || local.Year % 100 == wanted;
                });
            }
            return false;
        }

        private static bool LocalTimeSatisfies(DateTime utc, Func<DateTime, bool> predicate)
        {
            if (utc == default(DateTime) || predicate == null)
            {
                return false;
            }
            DateTime local;
            try
            {
                local = utc.ToLocalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            return predicate(local);
        }

        /// <summary>
        /// “短短几个数字”直接命中时间分量：`15` 命中 15 日 / 15 月 / 15 时 / 15 分，
        /// `26` 命中 2026 年（后两位），`2026` 命中整年。
        ///
        /// 【2026-10-19】只比“保存/删除时间”（`SavedUtc`），不再比创建时间和最后修改时间：
        ///   · 用户说的“搜索时间”就是保存时间；
        ///   · 新建的东西 `CreatedAtUtc` 就是“现在”，于是当此刻正好是 15 分/15 时/15 日时，
        ///     搜任何短数字都会把**所有东西**当成命中（单元测试在 15:xx 跑就会假红，那天就踩到了）。
        /// 完整日期写法（2026-10-08 这类）仍然允许命中创建时间，见 <see cref="MatchesStamp"/>。
        /// </summary>
        private static bool MatchesTimeComponent(SearchTarget target, string needle)
        {
            string digits = DigitsOnly(needle);
            // 只处理纯数字（中间带 - / 空格 / 年月日 的交给下面那套日期写法）。
            if (digits.Length == 0 || digits.Length > 4 || digits.Length != needle.Length)
            {
                return false;
            }
            int wanted;
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out wanted))
            {
                return false;
            }
            return LocalTimeMatchesPart(target.SavedUtc, wanted);
        }

        private static bool LocalTimeMatchesPart(DateTime utc, int wanted)
        {
            return LocalTimeSatisfies(utc, delegate(DateTime local)
            {
                return local.Year == wanted
                    || local.Year % 100 == wanted
                    || local.Month == wanted
                    || local.Day == wanted
                    || local.Hour == wanted
                    || local.Minute == wanted;
            });
        }

        /// <summary>
        /// 把用户输入的各种日期写法都折算成“年月日时分”再比。
        /// 界面上时间的写法各工作区不同（“北京时间 2026/10/16日 15:32分” / “2026-10-19 13:51”），
        /// 用户却习惯敲 2026-10-08 / 20261008 / 10-08，所以统一抽数字来比。
        /// </summary>
        private static bool MatchesStamp(SearchTarget target, string stamp, string needle)
        {
            if (stamp.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0)
            {
                return true;
            }

            string stampDigits = DigitsOnly(stamp);
            string needleDigits = DigitsOnly(needle);
            if (needleDigits.Length == 0)
            {
                return false;
            }
            if (stampDigits.IndexOf(needleDigits, StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            // 只写了日期的一部分（例如 2026-10-08 而卡片上是 2026/10/8）：
            // 把两边的“年/月/日”分段拿出来比，月、日允许没有前导零。
            if (MatchesDateParts(stamp, needle))
            {
                return true;
            }

            // 只写了“月-日”（例如 10-08）：去掉前导零再比一次，能对上 10/8。
            string stampLoose = stamp.Replace(" 0", " ").Replace("-0", "-").Replace("/0", "/");
            string needleLoose = needle.Replace(" 0", " ").Replace("-0", "-").Replace("/0", "/");
            if (needleLoose.Length > 0
                && stampLoose.IndexOf(needleLoose, StringComparison.CurrentCultureIgnoreCase) >= 0)
            {
                return true;
            }

            // 还支持用创建时间搜（有些东西改了名，用户记得是哪天建的）。
            string created = TimeText.FormatUtcAsComputerLocalStamp(
                target.CreatedUtc, AppText.CurrentLanguageTag);
            if (created.Length > 0)
            {
                if (created.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0)
                {
                    return true;
                }
                if (needleDigits.Length > 0
                    && DigitsOnly(created).IndexOf(needleDigits, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static int AddOccurrences(string text, string needle, List<SearchTextRange> ranges)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle))
            {
                return 0;
            }
            int added = 0;
            int from = 0;
            while (from <= text.Length - needle.Length)
            {
                int at = text.IndexOf(needle, from, StringComparison.CurrentCultureIgnoreCase);
                if (at < 0)
                {
                    break;
                }
                if (!AlreadyCovered(ranges, at, needle.Length))
                {
                    ranges.Add(new SearchTextRange(at, needle.Length));
                    added++;
                }
                from = at + 1;
            }
            return added;
        }

        private static bool AlreadyCovered(List<SearchTextRange> ranges, int start, int length)
        {
            for (int index = 0; index < ranges.Count; index++)
            {
                SearchTextRange range = ranges[index];
                if (range.Start <= start && range.Start + range.Length >= start + length)
                {
                    return true;
                }
            }
            return false;
        }

        private static int FirstDigitIndex(string text)
        {
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] >= '0' && text[index] <= '9')
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>
        /// 把“年/月/日”当成三个数字来比，忽略分隔符和前导零。
        /// 例：界面上的 “2026/10/8” 能命中用户输入的 “2026-10-08”“2026年10月8日”“20261008”。
        /// </summary>
        private static bool MatchesDateParts(string stamp, string needle)
        {
            int[] needleParts = ParseDateParts(needle);
            if (needleParts == null)
            {
                return false;
            }
            int[] stampParts = ParseDateParts(stamp);
            if (stampParts == null)
            {
                return false;
            }
            if (needleParts[0] != 0 && needleParts[0] != stampParts[0])
            {
                return false;
            }
            return needleParts[1] == stampParts[1] && needleParts[2] == stampParts[2];
        }

        /// <summary>从一段文本里取出“年、月、日”三个数字；月或日缺失时返回 null。</summary>
        private static int[] ParseDateParts(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            Match match = Regex.Match(
                text,
                @"(?:(\d{4})\s*[-/.年]\s*)?(\d{1,2})\s*[-/.月]\s*(\d{1,2})",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                // 纯数字写法（20261008、1008）：前 4 位当年、接着两位当月、再两位当日。
                Match digits = Regex.Match(
                    text,
                    @"(?:(\d{4})?)(\d{2})(\d{2})(?!\d)",
                    RegexOptions.CultureInvariant);
                if (!digits.Success)
                {
                    return null;
                }
                int digitYear = 0;
                if (digits.Groups[1].Success)
                {
                    int.TryParse(digits.Groups[1].Value, out digitYear);
                }
                int digitMonth;
                int digitDay;
                if (!int.TryParse(digits.Groups[2].Value, out digitMonth)
                    || !int.TryParse(digits.Groups[3].Value, out digitDay)
                    || digitMonth < 1 || digitMonth > 12 || digitDay < 1 || digitDay > 31)
                {
                    return null;
                }
                if (digitYear != 0 && (digitYear < 1900 || digitYear > 2999))
                {
                    digitYear = 0;
                }
                return new[] { digitYear, digitMonth, digitDay };
            }

            int year = 0;
            if (match.Groups[1].Success)
            {
                int.TryParse(match.Groups[1].Value, out year);
            }
            int month;
            int day;
            if (!int.TryParse(match.Groups[2].Value, out month)
                || !int.TryParse(match.Groups[3].Value, out day))
            {
                return null;
            }
            if (year != 0 && (year < 1900 || year > 2999))
            {
                year = 0;
            }
            return new[] { year, month, day };
        }

        private static string DigitsOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            System.Text.StringBuilder builder = new System.Text.StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (c >= '0' && c <= '9')
                {
                    builder.Append(c);
                }
            }
            return builder.ToString();
        }
    }
}
