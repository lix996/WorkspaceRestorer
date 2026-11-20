namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 一条问号说明的语言无关定义：只存 key，不存中文/英文。
    /// 真正文案由 TooltipResolver 在显示时从 AppText 解析。
    /// </summary>
    internal sealed class HelpDefinition
    {
        public string TitleKey;
        public string ShortKey;
        public string DetailKey;

        public static HelpDefinition Create(string titleKey, string shortKey, string detailKey)
        {
            HelpDefinition definition = new HelpDefinition();
            definition.TitleKey = titleKey;
            definition.ShortKey = shortKey;
            definition.DetailKey = detailKey;
            return definition;
        }
    }
}
