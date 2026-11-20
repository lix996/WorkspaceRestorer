using System;
using System.Globalization;
using System.Text.RegularExpressions;
using WorkspaceRestorer.Core.Localization;

namespace WorkspaceRestorer.Core
{
    /// <summary>
    /// 【公共组件】全程序唯一的时间文本来源。
    ///
    /// 三个原则：
    ///   · 用户看得见的时间只有一套写法：`时区名 yyyy/M/d日 H:mm分`
    ///     （中国是“北京时间 2026/11/4日 15:03分”，其它地区用本机时区名）；
    ///   · 机器用的时间仍保持 ISO：日志、标记文件、日期文件名、内部键；
    ///   · 页面不许再自己写 `ToString("yyyy...")`，以后换格式只改这里。
    ///
    /// 放在 Shared/Core（纯 .NET，不依赖 WPF），因为界面、文件夹镜像、
    /// 回收站说明文件和日志都要用同一份规则。
    /// </summary>
    internal static class TimeText
    {
        public const string SortableMinuteFormat = "yyyy-MM-dd HH:mm";

        public const string SortableSecondFormat = "yyyy-MM-dd HH:mm:ss";

        public const string DateStampFormat = "yyyy-MM-dd";

        public const string ClockSecondFormat = "HH:mm:ss";

        /// <summary>末尾的时间戳后缀（历史上出现过三种写法，都要能认出来并剥掉）。</summary>
        private static readonly Regex AppendedStampSuffix =
            new Regex(
                @"_(?:[^\d]*\d{4}/\d{1,2}/\d{1,2}日\s+\d{1,2}:\d{2}分"
                + @"|[^\d_]*\d{4}/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}"
                + @"|\d{4}/\d{1,2}/\d{1,2}\s+\d{1,2}:\d{2}"
                + @"|[^\d_]*\d{4}\.\d{2}\.\d{2}日，\d{2}:\d{2}分)\s*$",
                RegexOptions.Compiled);

        /// <summary>“北京时间 2026/10/10日 3:12分”。默认中文，数据/日志调用不受界面语言影响。</summary>
        public static string FormatLocalStamp(DateTime localTime, TimeZoneInfo zone)
        {
            return FormatLocalStamp(localTime, zone, LanguageTags.ZhCn);
        }

        /// <summary>
        /// 按界面语言格式化时间戳。
        /// 中文：“北京时间 2026/10/10日 3:12分”
        /// 英文：“China Standard Time 2026/10/10 3:12”
        /// </summary>
        public static string FormatLocalStamp(DateTime localTime, TimeZoneInfo zone, string language)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            if (!LanguageTags.IsChinese(language))
            {
                return FormatZoneLabel(zone, language)
                    + " " + localTime.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                    + " " + localTime.ToString("H:mm", CultureInfo.InvariantCulture);
            }
            return FormatZoneLabel(zone, language)
                + " " + localTime.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + "日 "
                + localTime.ToString("H:mm", CultureInfo.InvariantCulture) + "分";
        }

        /// <summary>
        /// 用**这台电脑自己的时区**格式化（用户明确要求：中国就是北京时间，
        /// 美国就是美国那边的时区名，代码里不许写死“北京时间”）。
        /// </summary>
        public static string FormatComputerLocalStamp(DateTime localTime)
        {
            return FormatComputerLocalStamp(localTime, LanguageTags.ZhCn);
        }

        public static string FormatComputerLocalStamp(DateTime localTime, string language)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            try
            {
                return FormatLocalStamp(localTime, TimeZoneInfo.Local, language);
            }
            catch (Exception)
            {
                // 极端情况下拿不到时区信息，也不要让界面崩：退回不带时区名的写法。
                try
                {
                    if (!LanguageTags.IsChinese(language))
                    {
                        return localTime.ToString("yyyy/M/d", CultureInfo.InvariantCulture)
                            + " " + localTime.ToString("H:mm", CultureInfo.InvariantCulture);
                    }
                    return localTime.ToString("yyyy/M/d", CultureInfo.InvariantCulture) + "日 "
                        + localTime.ToString("H:mm", CultureInfo.InvariantCulture) + "分";
                }
                catch (ArgumentOutOfRangeException)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>把 UTC 时间按这台电脑的时区显示成“时区名 + 日期时间”。</summary>
        public static string FormatUtcAsComputerLocalStamp(DateTime utcTime)
        {
            return FormatUtcAsComputerLocalStamp(utcTime, LanguageTags.ZhCn);
        }

        public static string FormatUtcAsComputerLocalStamp(DateTime utcTime, string language)
        {
            if (utcTime == default(DateTime))
            {
                return string.Empty;
            }
            try
            {
                return FormatComputerLocalStamp(utcTime.ToLocalTime(), language);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>时区显示名：去掉“(UTC+08:00) ”前缀和“(美国和加拿大)”这类后缀。</summary>
        public static string FormatZoneLabel(TimeZoneInfo zone)
        {
            return FormatZoneLabel(zone, LanguageTags.ZhCn);
        }

        public static string FormatZoneLabel(TimeZoneInfo zone, string language)
        {
            bool english = !LanguageTags.IsChinese(language);
            if (zone == null)
            {
                return english ? "Local Time" : "本地时间";
            }
            if (string.Equals(zone.Id, "China Standard Time", StringComparison.OrdinalIgnoreCase))
            {
                return english ? "China Standard Time" : "北京时间";
            }
            // 英文界面优先用系统标准名，避免拿到中文的 DisplayName。
            string name = english ? zone.StandardName : zone.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = zone.StandardName;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                return english ? "Local Time" : "本地时间";
            }
            string text = name.Trim();
            int close = text.IndexOf(')');
            if (text.StartsWith("(", StringComparison.Ordinal) && close > 0)
            {
                text = text.Substring(close + 1).Trim();
            }
            int open = text.IndexOf('(');
            if (open > 0)
            {
                text = text.Substring(0, open).Trim();
            }
            // 有些时区的显示名是一串城市（“Osaka, Sapporo, Tokyo”），
            // 当标签太啰嗦，改用系统标准名（“Japan Standard Time”）。
            if (text.IndexOf(',') >= 0)
            {
                string standard = (zone.StandardName ?? string.Empty).Trim();
                if (standard.Length > 0)
                {
                    text = standard;
                }
            }
            if (text.Length == 0)
            {
                if (string.IsNullOrWhiteSpace(zone.Id))
                {
                    return english ? "Local Time" : "本地时间";
                }
                return zone.Id;
            }
            return text;
        }

        /// <summary>给一个基名接上**本机时区**的当前时间（调用方不用自己取时区）。</summary>
        public static string AppendStampNow(string name, string fallbackName, DateTime now)
        {
            return AppendStampNow(name, fallbackName, now, LanguageTags.ZhCn);
        }

        public static string AppendStampNow(
            string name, string fallbackName, DateTime now, string language)
        {
            TimeZoneInfo zone;
            try
            {
                zone = TimeZoneInfo.Local;
            }
            catch (Exception)
            {
                zone = null;
            }
            return AppendStamp(name, fallbackName, now, zone, language);
        }

        /// <summary>把名字末尾所有旧时间戳剥干净，返回“基名”。</summary>
        public static string StripStamp(string name)
        {
            string text = name == null ? string.Empty : name.Trim();
            while (AppendedStampSuffix.IsMatch(text))
            {
                text = AppendedStampSuffix.Replace(text, string.Empty).Trim();
            }
            return text;
        }

        /// <summary>给一个基名接上当前时间（反复调用只更新时间，不叠加）。默认中文。</summary>
        public static string AppendStamp(string name, string fallbackName, DateTime now, TimeZoneInfo zone)
        {
            return AppendStamp(name, fallbackName, now, zone, LanguageTags.ZhCn);
        }

        /// <summary>按界面语言给名字追加时间戳；旧中文/新英文格式都会被 StripStamp 识别。</summary>
        public static string AppendStamp(
            string name, string fallbackName, DateTime now, TimeZoneInfo zone, string language)
        {
            string baseName = StripStamp(name);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = string.IsNullOrWhiteSpace(fallbackName)
                    ? (!LanguageTags.IsChinese(language) ? "Profile" : "方案")
                    : fallbackName;
            }
            return baseName + "_" + FormatLocalStamp(now, zone, language);
        }

        /// <summary>机器可读的分钟级时间（文件夹镜像、旧搜索文本等）。</summary>
        public static string FormatSortableMinute(DateTime localTime)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            return localTime.ToString(SortableMinuteFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>机器可读的秒级时间（日志、标记文件）。</summary>
        public static string FormatSortableSecond(DateTime localTime)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            return localTime.ToString(SortableSecondFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>日期文件名/日期行（日志文件名、自动恢复标记）。</summary>
        public static string FormatDateStamp(DateTime localTime)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            return localTime.ToString(DateStampFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>只显示时分秒（自动恢复标记的“时间”行）。</summary>
        public static string FormatClockSecond(DateTime localTime)
        {
            if (localTime == default(DateTime))
            {
                return string.Empty;
            }
            return localTime.ToString(ClockSecondFormat, CultureInfo.InvariantCulture);
        }
    }
}
