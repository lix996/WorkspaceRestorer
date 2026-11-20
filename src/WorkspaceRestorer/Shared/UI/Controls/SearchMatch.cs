using System;
using System.Text;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】搜索关键字匹配 —— 三个工作区**共用同一套规则**。
    ///
    /// 用户要求（2026-10-10 19:32）：
    /// “回收站的搜索方案，操作要和启动与恢复的一致，自启动应用的搜索方案也要和启动与恢复的一致。”
    ///
    /// 规则（与「启动与恢复」原来的实现完全一致，只是搬出来共用）：
    ///  · 纯文字：在“名字 / 路径 / 说明”里做不区分大小写的包含匹配；
    ///  · 带日期：把输入和目标里的**数字**都抽出来比较，
    ///    于是 `2026-10-08`、`2026/10/8`、`2026年10月8日`、`20261008`、`10-08` 都能命中同一天。
    /// </summary>
    internal static class SearchMatch
    {
        /// <summary>输入是不是“看起来在找日期”（至少 4 位数字，例如 10-08、20261008）。</summary>
        public static bool LooksLikeDate(string keyword)
        {
            return DigitsOnly(keyword).Length >= 4;
        }

        /// <summary>只保留数字（`2026-10-08 15:16` → `202610081516`）。</summary>
        public static string DigitsOnly(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                if (char.IsDigit(text[index]))
                {
                    builder.Append(text[index]);
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// 关键字是否命中这若干段文本（任一命中即可）。
        /// 文字命中用包含匹配；日期命中用“抽取数字后包含”.
        /// </summary>
        public static bool MatchesAny(string keyword, params string[] fields)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return true;
            }
            string needle = keyword.Trim();
            string needleDigits = DigitsOnly(needle);
            bool byDate = needleDigits.Length >= 4;
            if (fields != null)
            {
                for (int index = 0; index < fields.Length; index++)
                {
                    string field = fields[index];
                    if (string.IsNullOrEmpty(field))
                    {
                        continue;
                    }
                    if (field.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    {
                        return true;
                    }
                    if (byDate && DigitsOnly(field).IndexOf(needleDigits, StringComparison.Ordinal) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
