using System;
using System.Globalization;

namespace WorkspaceRestorer.Core.Localization
{
    /// <summary>
    /// 语言代码常量与 BCP 47 解析。这里只认代码，不再用枚举；
    /// 新增语言不需要改这个文件。
    /// </summary>
    internal static class LanguageTags
    {
        internal const string ZhCn = "zh-CN";
        internal const string EnUs = "en-US";
        internal const string Default = ZhCn;

        internal static bool IsChinese(string tag)
        {
            return !string.IsNullOrWhiteSpace(tag)
                && tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Normalize(string tag)
        {
            return string.IsNullOrWhiteSpace(tag) ? Default : tag.Trim();
        }

        internal static bool Same(string left, string right)
        {
            return string.Equals(
                Normalize(left),
                Normalize(right),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static CultureInfo ToCulture(string tag)
        {
            try
            {
                return CultureInfo.GetCultureInfo(Normalize(tag));
            }
            catch (Exception)
            {
                return CultureInfo.InvariantCulture;
            }
        }
    }
}
