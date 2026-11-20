using System;

namespace WorkspaceRestorer.Core.Localization
{
    /// <summary>
    /// 一种界面语言的元数据。语言由资源目录和 _language.json 描述，
    /// 页面和设置页不写死“中文/英文”，以后新增语言只增加资源目录。
    /// </summary>
    internal sealed class LanguageDefinition
    {
        public LanguageDefinition(
            string tag,
            string nativeName,
            string englishName,
            string fallbackTag,
            bool uiReady,
            int order,
            string pluralRule)
        {
            Tag = string.IsNullOrWhiteSpace(tag) ? LanguageTags.Default : tag.Trim();
            NativeName = string.IsNullOrWhiteSpace(nativeName) ? Tag : nativeName.Trim();
            EnglishName = string.IsNullOrWhiteSpace(englishName) ? NativeName : englishName.Trim();
            FallbackTag = string.IsNullOrWhiteSpace(fallbackTag) ? LanguageTags.Default : fallbackTag.Trim();
            UiReady = uiReady;
            Order = order;
            PluralRule = string.IsNullOrWhiteSpace(pluralRule) ? "none" : pluralRule.Trim();
        }

        /// <summary>BCP 47 语言代码，同时也是资源目录名，例如 zh-CN / en-US / ja-JP。</summary>
        public string Tag { get; private set; }

        /// <summary>设置页下拉里显示的母语名称，例如“中文（简体）”“English”。</summary>
        public string NativeName { get; private set; }

        /// <summary>英文名称，供日志/诊断和未翻译界面使用。</summary>
        public string EnglishName { get; private set; }

        /// <summary>缺少 key 时回退的语言代码。</summary>
        public string FallbackTag { get; private set; }

        /// <summary>只有翻译完整、经过验收的语言才出现在设置里。</summary>
        public bool UiReady { get; private set; }

        /// <summary>下拉排序；数字小的在前。</summary>
        public int Order { get; private set; }

        /// <summary>单复数规则：none / oneOther。以后新增复杂语种可在解析器里扩展。</summary>
        public string PluralRule { get; private set; }

        public bool UsesSingularKey
        {
            get { return string.Equals(PluralRule, "oneOther", StringComparison.OrdinalIgnoreCase); }
        }

        public override string ToString()
        {
            return Tag + " (" + NativeName + ")";
        }
    }
}
