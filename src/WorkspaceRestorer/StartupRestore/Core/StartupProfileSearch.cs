using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Models
{
    /// <summary>一条命中区间：起点 + 长度（用于在界面上把那几个字高亮）。</summary>
    public sealed class SearchTextRange
    {
        public SearchTextRange(int start, int length)
        {
            Start = start;
            Length = length;
        }

        public int Start { get; private set; }

        public int Length { get; private set; }
    }

    /// <summary>一条方案在本次搜索里“哪里命中了”：名字里的区间 + 时间串里的区间。</summary>
    public sealed class ProfileSearchHighlights
    {
        public ProfileSearchHighlights()
        {
            NameRanges = new List<SearchTextRange>();
            TimeRanges = new List<SearchTextRange>();
        }

        /// <summary>名字里要加亮的字（相对 `profile.Name`）。</summary>
        public List<SearchTextRange> NameRanges { get; private set; }

        /// <summary>时间串里要加亮的字（相对 `StartupProfileCardText.FormatSavedAt` 那一串）。</summary>
        public List<SearchTextRange> TimeRanges { get; private set; }
    }

    /// <summary>
    /// “搜索工作方案”的入口。**匹配规则本身已经搬到公共区** `UI.Shared.SearchMatcher`
    /// （2026-10-19 用户要求：自启动应用 / 回收站 / 启动与恢复 三处搜索必须完全一致，
    /// 以后加新的搜索方法也只改公共件）。这里只保留“把工作方案翻译成公共的 SearchTarget”这一层，
    /// 以及原来那套公开方法（单元测试与界面都在用）。
    ///
    /// 演变（都记在这里，免得以后改丢）：
    ///  · 2026-10-08：名字搜索和日期搜索合在一起；
    ///  · 2026-10-17 第 44 批：时间搜索改成**勾选**（默认不勾＝只按名字），并支持“15 → 15日/15月/15时/15分”；
    ///  · 2026-10-17 第 45 批（用户要求）：
    ///     ① **空格分隔的多个词要同时命中**（AND）——“方案 15分”＝名字里有“方案”**并且**保存时间是 15 分；
    ///     ② 支持带单位的时间词：`15分`/`15时`/`15点`/`15日`/`15号`/`15月`/`2026年`；
    ///     ③ 给出**命中区间**（名字与时间各一套），界面上要按区间把那几个字加亮。
    ///  · 2026-10-19：规则搬到 `Shared/UI/Controls/SearchMatcher.cs`，三个工作区共用同一套。
    /// </summary>
    public static class StartupProfileSearch
    {
        /// <summary>把搜索框里的一串字拆成若干“词”（空格/逗号分隔，多个词之间是**并且**关系）。</summary>
        public static IList<string> SplitTerms(string keyword)
        {
            return WorkspaceRestorer.UI.Shared.SearchMatcher.SplitTerms(keyword);
        }

        /// <summary>方案是否匹配关键字（默认把时间也算上，等同于勾选「同时按时间搜索」）。</summary>
        public static bool Matches(StartupProfile profile, string keyword)
        {
            return Matches(profile, keyword, true);
        }

        /// <summary>方案是否匹配关键字（`includeTime == false` 时只按名字）。</summary>
        public static bool Matches(StartupProfile profile, string keyword, bool includeTime)
        {
            return WorkspaceRestorer.UI.Shared.SearchMatcher.Matches(ToTarget(profile), keyword, includeTime);
        }

        /// <summary>单个词是否命中：先看名字，勾了时间再看时间。</summary>
        public static bool MatchesTerm(StartupProfile profile, string term, bool includeTime)
        {
            return WorkspaceRestorer.UI.Shared.SearchMatcher.MatchesTerm(ToTarget(profile), term, includeTime);
        }

        /// <summary>一个词是否命中“时间”：带单位的按单位比，纯数字按任一分量比，其余走日期写法。</summary>
        public static bool MatchesTimeTerm(StartupProfile profile, string term)
        {
            return WorkspaceRestorer.UI.Shared.SearchMatcher.MatchesTimeTerm(ToTarget(profile), term);
        }

        /// <summary>算这条方案在本次搜索里“哪里命中了”，界面按这些区间加亮。</summary>
        public static ProfileSearchHighlights Highlight(StartupProfile profile, string keyword, bool includeTime)
        {
            ProfileSearchHighlights result = new ProfileSearchHighlights();
            WorkspaceRestorer.UI.Shared.SearchMatcher.Highlight(
                ToTarget(profile),
                keyword,
                includeTime,
                result.NameRanges,
                result.TimeRanges);
            return result;
        }

        /// <summary>按关键字过滤一份方案列表。</summary>
        public static List<StartupProfile> Filter(IEnumerable<StartupProfile> profiles, string keyword)
        {
            return Filter(profiles, keyword, true);
        }

        /// <summary>按关键字过滤一份方案列表（`includeTime == false` 时只按名字）。</summary>
        public static List<StartupProfile> Filter(IEnumerable<StartupProfile> profiles, string keyword, bool includeTime)
        {
            List<StartupProfile> result = new List<StartupProfile>();
            if (profiles == null)
            {
                return result;
            }
            foreach (StartupProfile profile in profiles)
            {
                if (Matches(profile, keyword, includeTime))
                {
                    result.Add(profile);
                }
            }
            return result;
        }

        /// <summary>把一份工作方案翻译成公共的搜索目标（名字 / 显示用时间 / 三个时间戳）。</summary>
        private static WorkspaceRestorer.UI.Shared.SearchTarget ToTarget(StartupProfile profile)
        {
            WorkspaceRestorer.UI.Shared.SearchTarget target =
                new WorkspaceRestorer.UI.Shared.SearchTarget();
            if (profile == null)
            {
                return target;
            }
            target.Name = profile.Name;
            target.TimeText = StartupProfileCardText.FormatSavedAt(profile);
            // 用户说的“搜索时间”指的是保存时间；没保存过的方案 LastSavedAtUtc 会退回创建时间。
            target.SavedUtc = StartupProfileDefaults.LastSavedAtUtc(profile);
            target.UpdatedUtc = profile.UpdatedAtUtc;
            target.CreatedUtc = profile.CreatedAtUtc;
            return target;
        }
    }
}
