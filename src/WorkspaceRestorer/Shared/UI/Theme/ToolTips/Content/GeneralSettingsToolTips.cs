namespace WorkspaceRestorer.UI
{
    /// <summary>“通用”页语言/主题两行的问号定义。机制在 ToolTips，文案在语言资源。</summary>
    internal static class GeneralSettingsToolTips
    {
        public static HelpDefinition LanguageRow
        {
            get
            {
                return HelpDefinition.Create(
                    SettingsTextKeys.LanguageLabel,
                    SettingsTextKeys.LanguageHint,
                    SettingsTextKeys.LanguageDetail);
            }
        }

        public static HelpDefinition ThemeRow
        {
            get
            {
                return HelpDefinition.Create(
                    SettingsTextKeys.ThemeLabel,
                    SettingsTextKeys.ThemeHint,
                    SettingsTextKeys.ThemeDetail);
            }
        }
    }
}
