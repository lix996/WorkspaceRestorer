namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 一条悬浮提示的语言无关定义：只存 key、动态参数和无障碍策略。
    /// 页面只引用定义，不再写中文/英文；语言资源在 TooltipResolver 里解析。
    /// </summary>
    internal sealed class TooltipDefinition
    {
        public string AccessibleNameKey;
        public string TitleKey;
        public string DescriptionKey;
        public string SuggestionKey;
        public string HelpTextKey;
        public bool SetAccessibleName;
        public object[] Arguments;

        /// <summary>动态名字（例如用户起的方案名）；非空时优先于 AccessibleNameKey。</summary>
        public string AccessibleNameText;

        /// <summary>动态标题；非空时优先于 TitleKey。</summary>
        public string TitleText;

        /// <summary>动态正文；非空时优先于 DescriptionKey。</summary>
        public string DescriptionText;

        /// <summary>动态建议；非空时优先于 SuggestionKey。</summary>
        public string SuggestionText;

        public static TooltipDefinition Named(
            string accessibleNameKey,
            string titleKey,
            string descriptionKey,
            string suggestionKey)
        {
            TooltipDefinition definition = new TooltipDefinition();
            definition.AccessibleNameKey = accessibleNameKey;
            definition.TitleKey = titleKey;
            definition.DescriptionKey = descriptionKey;
            definition.SuggestionKey = suggestionKey;
            definition.SetAccessibleName = true;
            return definition;
        }

        /// <summary>名字/标题直接给动态文字（不翻译）；正文仍用 key。</summary>
        public static TooltipDefinition NamedLiteral(
            string accessibleName,
            string title,
            string descriptionKey,
            string suggestionKey)
        {
            TooltipDefinition definition = Named(
                null, null, descriptionKey, suggestionKey);
            definition.AccessibleNameText = accessibleName;
            definition.TitleText = title;
            definition.SetAccessibleName = true;
            return definition;
        }

        /// <summary>名字、标题、正文全部是动态文字；用于落点提示/同步状态。</summary>
        public static TooltipDefinition Literal(
            string accessibleName,
            string title,
            string description,
            string suggestion)
        {
            TooltipDefinition definition = new TooltipDefinition();
            definition.AccessibleNameText = accessibleName;
            definition.TitleText = title;
            definition.DescriptionText = description;
            definition.SuggestionText = suggestion;
            definition.SetAccessibleName = !string.IsNullOrEmpty(accessibleName);
            return definition;
        }

        public static TooltipDefinition Plain(string descriptionKey, string suggestionKey)
        {
            TooltipDefinition definition = new TooltipDefinition();
            definition.DescriptionKey = descriptionKey;
            definition.SuggestionKey = suggestionKey;
            definition.SetAccessibleName = false;
            return definition;
        }

        public TooltipDefinition WithArguments(params object[] args)
        {
            Arguments = args;
            return this;
        }

        public TooltipDefinition WithHelpText(string helpTextKey)
        {
            HelpTextKey = helpTextKey;
            return this;
        }
    }
}
