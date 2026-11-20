namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 把语言无关的 TooltipDefinition / HelpDefinition 解析成当前语言的显示内容。
    /// 所有提示文案只在这里查 AppText，页面和 Content 不拼用户可见句子。
    /// </summary>
    internal static class TooltipResolver
    {
        internal static ToolTipContent Resolve(TooltipDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }
            ToolTipContent content = new ToolTipContent();
            content.AccessibleName = string.IsNullOrEmpty(definition.AccessibleNameText)
                ? Format(definition.AccessibleNameKey, definition.Arguments)
                : definition.AccessibleNameText;
            content.Title = string.IsNullOrEmpty(definition.TitleText)
                ? Format(definition.TitleKey, definition.Arguments)
                : definition.TitleText;
            content.Description = string.IsNullOrEmpty(definition.DescriptionText)
                ? Format(definition.DescriptionKey, definition.Arguments)
                : definition.DescriptionText;
            content.Suggestion = string.IsNullOrEmpty(definition.SuggestionText)
                ? Format(definition.SuggestionKey, definition.Arguments)
                : definition.SuggestionText;
            content.HelpText = Format(definition.HelpTextKey, definition.Arguments);
            content.SetAccessibleName = definition.SetAccessibleName;
            return content;
        }

        internal static HelpContent Resolve(HelpDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }
            return HelpContent.Create(
                AppText.T(definition.TitleKey),
                AppText.T(definition.ShortKey),
                AppText.T(definition.DetailKey));
        }

        private static string Format(string key, object[] args)
        {
            return string.IsNullOrEmpty(key)
                ? null
                : (args == null || args.Length == 0 ? AppText.T(key) : AppText.F(key, args));
        }
    }
}
