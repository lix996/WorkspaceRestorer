namespace WorkspaceRestorer.UI
{
    /// <summary>设置页、公共设置行和主窗口开关的静态提示文案。</summary>
    internal static class SettingsToolTips
    {
        // 设置行的 key 前缀。正文在 Resources/{语言}/Settings/Settings.json，
        // 页面只拿定义，语言切换后 HelpHint 会重新解析，不重建设置页。
        private static HelpDefinition Help(string prefix)
        {
            return HelpDefinition.Create(prefix + ".title", prefix + ".short", prefix + ".detail");
        }

        public static HelpDefinition StartupAutoRestoreRow { get { return Help("settings.help.startupAutoRestore"); } }

        public static HelpDefinition ScanTerminalsRow { get { return Help("settings.help.scanTerminals"); } }

        public static HelpDefinition LoadGateRow { get { return Help("settings.help.loadGate"); } }

        public static HelpDefinition StartupInitialDelayRow { get { return Help("settings.help.startupInitialDelay"); } }

        public static HelpDefinition StartupItemDelayRow { get { return Help("settings.help.startupItemDelay"); } }

        public static HelpDefinition StartupWebDelayRow { get { return Help("settings.help.startupWebDelay"); } }

        public static HelpDefinition ApplicationsAutoRestoreRow { get { return Help("settings.help.applicationsAutoRestore"); } }

        public static HelpDefinition ApplicationsWaitForLoadRow { get { return Help("settings.help.applicationsWaitForLoad"); } }

        public static HelpDefinition ApplicationsInitialDelayRow { get { return Help("settings.help.applicationsInitialDelay"); } }

        public static HelpDefinition ApplicationsItemDelayRow { get { return Help("settings.help.applicationsItemDelay"); } }

        public static HelpDefinition ApplicationsWebDelayRow { get { return Help("settings.help.applicationsWebDelay"); } }

        public static HelpDefinition LayoutAutoRestoreRow { get { return Help("settings.help.layoutAutoRestore"); } }

        public static HelpDefinition LayoutInitialDelayRow { get { return Help("settings.help.layoutInitialDelay"); } }

        // 【2026-11-18】三个工作区的“新命名”从页面搬进设置后，各占自己分区的一行。
        public static HelpDefinition StartupNewNameRow { get { return Help("settings.help.startupNewName"); } }

        public static HelpDefinition ApplicationsNewNameRow { get { return Help("settings.help.applicationsNewName"); } }

        public static HelpDefinition LayoutNewNameRow { get { return Help("settings.help.layoutNewName"); } }

        public static HelpDefinition GeneralScrollSpeedRow { get { return Help("settings.help.generalScrollSpeed"); } }

        public static HelpDefinition AutoStartRow { get { return Help("settings.help.autoStart"); } }

        public static HelpDefinition StartupPriorityRow { get { return Help("settings.help.startupPriority"); } }

        public static HelpDefinition ResetAllRow { get { return Help("settings.help.resetAll"); } }

        public static HelpDefinition CpuLimitRow { get { return Help("settings.help.cpuLimit"); } }

        public static HelpDefinition CpuWaitRow { get { return Help("settings.help.cpuWait"); } }

        public static HelpDefinition MemoryLimitRow { get { return Help("settings.help.memoryLimit"); } }

        public static HelpDefinition MemoryWaitRow { get { return Help("settings.help.memoryWait"); } }

        private static TooltipDefinition Tip(string prefix)
        {
            return TooltipDefinition.Named(
                prefix + ".name",
                prefix + ".title",
                prefix + ".description",
                prefix + ".suggestion");
        }

        private static TooltipDefinition PlainTip(string prefix)
        {
            return TooltipDefinition.Plain(prefix + ".description", null);
        }

        private static TooltipDefinition TipWithoutSuggestion(string prefix)
        {
            return TooltipDefinition.Named(
                prefix + ".name",
                prefix + ".title",
                prefix + ".description",
                null);
        }

        private static TooltipDefinition PagerTip(string nameKey, string titleKey, string descriptionKey)
        {
            return TooltipDefinition.Named(nameKey, titleKey, descriptionKey, null);
        }

        // 【2026-11-19】快捷键表格底部分页条：第一页 / 上一页 / 页码 / 下一页 / 最后一页 / 每页条数。
        public static TooltipDefinition PagerFirst
        {
            get
            {
                return PagerTip(
                    SettingsTextKeys.ShortcutsPagerFirstName,
                    SettingsTextKeys.ShortcutsPagerFirstTitle,
                    SettingsTextKeys.ShortcutsPagerFirstDescription);
            }
        }

        public static TooltipDefinition PagerPrevious
        {
            get
            {
                return PagerTip(
                    SettingsTextKeys.ShortcutsPagerPreviousName,
                    SettingsTextKeys.ShortcutsPagerPreviousTitle,
                    SettingsTextKeys.ShortcutsPagerPreviousDescription);
            }
        }

        public static TooltipDefinition PagerNext
        {
            get
            {
                return PagerTip(
                    SettingsTextKeys.ShortcutsPagerNextName,
                    SettingsTextKeys.ShortcutsPagerNextTitle,
                    SettingsTextKeys.ShortcutsPagerNextDescription);
            }
        }

        public static TooltipDefinition PagerLast
        {
            get
            {
                return PagerTip(
                    SettingsTextKeys.ShortcutsPagerLastName,
                    SettingsTextKeys.ShortcutsPagerLastTitle,
                    SettingsTextKeys.ShortcutsPagerLastDescription);
            }
        }

        public static TooltipDefinition PagerPage(int page)
        {
            return PagerTip(
                SettingsTextKeys.ShortcutsPagerPageName,
                SettingsTextKeys.ShortcutsPagerPageTitle,
                SettingsTextKeys.ShortcutsPagerPageDescription).WithArguments(page);
        }

        public static TooltipDefinition PagerRows
        {
            get
            {
                return PagerTip(
                    SettingsTextKeys.ShortcutsPagerRowsName,
                    SettingsTextKeys.ShortcutsPagerRowsTitle,
                    SettingsTextKeys.ShortcutsPagerRowsDescription);
            }
        }

        public static TooltipDefinition ApplicationsAutoRestore { get { return Tip("settings.tip.applicationsAutoRestore"); } }

        public static TooltipDefinition ApplicationsWaitForLoad { get { return Tip("settings.tip.applicationsWaitForLoad"); } }

        public static TooltipDefinition LayoutAutoRestore { get { return Tip("settings.tip.layoutAutoRestore"); } }

        public static TooltipDefinition LayoutInitialDelay { get { return Tip("settings.tip.layoutInitialDelay"); } }

        public static TooltipDefinition CpuLimit { get { return Tip("settings.tip.cpuLimit"); } }

        public static TooltipDefinition CpuWait { get { return Tip("settings.tip.cpuWait"); } }

        public static TooltipDefinition MemoryLimit { get { return Tip("settings.tip.memoryLimit"); } }

        public static TooltipDefinition MemoryWait { get { return Tip("settings.tip.memoryWait"); } }

        public static TooltipDefinition StartupInitialDelay { get { return Tip("settings.tip.startupInitialDelay"); } }

        public static TooltipDefinition StartupItemDelay { get { return Tip("settings.tip.startupItemDelay"); } }

        public static TooltipDefinition StartupWebDelay { get { return Tip("settings.tip.startupWebDelay"); } }

        public static TooltipDefinition ApplicationsInitialDelay { get { return Tip("settings.tip.applicationsInitialDelay"); } }

        public static TooltipDefinition ApplicationsItemDelay { get { return Tip("settings.tip.applicationsItemDelay"); } }

        public static TooltipDefinition ApplicationsWebDelay { get { return Tip("settings.tip.applicationsWebDelay"); } }

        public static TooltipDefinition ProfileName { get { return Tip("settings.tip.profileName"); } }

        public static TooltipDefinition ApplicationsNewName { get { return Tip("settings.tip.applicationsNewName"); } }

        public static TooltipDefinition LayoutNewName { get { return Tip("settings.tip.layoutNewName"); } }

        // 【2026-11-19】命名框截断时的“完整值”提示：标题/无障碍名/建议沿用该行的设置 tip，
        // 正文填当前值（数据，不翻译）；只有截断时才挂，调用方见 SettingsPage。
        public static TooltipDefinition NamingValue(TooltipDefinition source, string value)
        {
            TooltipDefinition definition = new TooltipDefinition();
            if (source != null)
            {
                definition.AccessibleNameKey = source.AccessibleNameKey;
                definition.TitleKey = source.TitleKey;
                definition.SuggestionKey = source.SuggestionKey;
                definition.AccessibleNameText = source.AccessibleNameText;
                definition.TitleText = source.TitleText;
                definition.SetAccessibleName = true;
            }
            definition.DescriptionText = value ?? string.Empty;
            return definition;
        }

        public static TooltipDefinition AutoStart { get { return Tip("settings.tip.autoStart"); } }

        public static TooltipDefinition StartupAutoRestore { get { return Tip("settings.tip.startupAutoRestore"); } }

        public static TooltipDefinition LoadGate { get { return Tip("settings.tip.loadGate"); } }

        public static TooltipDefinition ScanTerminals { get { return Tip("settings.tip.scanTerminals"); } }

        public static TooltipDefinition ApplicationsAutoStart { get { return Tip("settings.tip.applicationsAutoStart"); } }

        public static TooltipDefinition DragScrollSpeedValue { get { return PlainTip("settings.tip.dragScrollSpeedValue"); } }

        public static TooltipDefinition DragScrollSpeedSlider { get { return Tip("settings.tip.dragScrollSpeedSlider"); } }

        public static TooltipDefinition ResetAll { get { return TipWithoutSuggestion("settings.tip.resetAll"); } }

        public static ToolTipContent Navigation(string label, string description)
        {
            return ToolTipContent.Named(label, description);
        }

        public static TooltipDefinition StartupPriority { get { return Tip("settings.tip.startupPriority"); } }
    }
}
