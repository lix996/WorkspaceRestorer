using System;
using WorkspaceRestorer.Core;
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 工作方案卡片第二行的文字（用户 2026-10-04 要求在这里加上日期）。
    ///
    /// 形如：“21 个启动项目 · 北京时间 2026/10/16日 15:30分”，
    /// 时间是这份方案的**最后保存时间**（StartupProfile.UpdatedAtUtc）。
    ///
    /// 用户 2026-10-16 要求统一成「追加当前时间」那套格式，并且
    /// **“一定是电脑上当前时间的格式（如果是中国就是北京时间，如果是美国就是美国那边的时间，
    /// 千万不要固定写死成北京时间）”** → 时区一律取 <see cref="TimeZoneInfo.Local"/>，
    /// 具体见 TimeText.FormatComputerLocalStamp。
    ///
    /// 抽成纯函数是为了让测试能直接验证格式，界面只负责把字符串塞进 TextBlock。
    /// </summary>
    public static class StartupProfileCardText
    {
        /// <summary>卡片第二行：“N 个启动项目 · 本机时区名 yyyy/M/d日 H:mm分”。</summary>
        public static string BuildCountText(StartupProfile profile)
        {
            return BuildCountText(profile, 0);
        }

        public static string BuildCountText(StartupProfile profile, int childCount)
        {
            return BuildCountText(profile, childCount, LanguageTags.ZhCn);
        }

        public static string BuildCountText(StartupProfile profile, int childCount, string language)
        {
            int count = profile == null || profile.Items == null ? 0 : profile.Items.Count;
            bool folder = profile != null && profile.IsFolder;
            string text;
            if (folder)
            {
                text = childCount <= 0
                    ? AppText.T(StartupRestoreTextKeys.CardEmptyFolder)
                    : AppText.F(StartupRestoreTextKeys.CardChildCountFormat, childCount);
            }
            else if (childCount > 0)
            {
                text = AppText.F(StartupRestoreTextKeys.CardChildAndItemFormat, childCount, count);
            }
            else
            {
                text = AppText.F(StartupRestoreTextKeys.CardItemCountFormat, count);
            }
            string saved = FormatSavedAt(profile, language);
            return string.IsNullOrEmpty(saved) ? text : text + " · " + saved;
        }

        /// <summary>最后保存时间，按这台电脑的时区显示成“时区名 + 日期时间”；没有记录时返回空串。</summary>
        public static string FormatSavedAt(StartupProfile profile)
        {
            return FormatSavedAt(profile, LanguageTags.ZhCn);
        }

        public static string FormatSavedAt(StartupProfile profile, string language)
        {
            DateTime savedAtUtc = StartupProfileDefaults.LastSavedAtUtc(profile);
            if (savedAtUtc == default(DateTime))
            {
                return string.Empty;
            }
            return TimeText.FormatUtcAsComputerLocalStamp(savedAtUtc, language);
        }

        /// <summary>卡片第二行的悬浮提示。</summary>
        public static string BuildCountToolTip(StartupProfile profile)
        {
            return BuildCountToolTip(profile, LanguageTags.ZhCn);
        }

        public static string BuildCountToolTip(StartupProfile profile, string language)
        {
            string saved = FormatSavedAt(profile, language);
            if (string.IsNullOrEmpty(saved))
            {
                return AppText.T(StartupRestoreTextKeys.CardSavedAtEmpty);
            }
            return AppText.F(StartupRestoreTextKeys.CardSavedAtFormat, saved);
        }
    }
}
