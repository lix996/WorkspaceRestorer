namespace WorkspaceRestorer.UI
{
    /// <summary>外壳、导航、通用对话框的语言无关提示定义。正文在 Shell/Shell.json。</summary>
    internal static class ShellToolTips
    {
        private static TooltipDefinition Named(string prefix)
        {
            return TooltipDefinition.Named(
                prefix + ".name",
                prefix + ".title",
                prefix + ".description",
                null);
        }

        private static TooltipDefinition NamedWithSuggestion(string prefix)
        {
            return TooltipDefinition.Named(
                prefix + ".name",
                prefix + ".title",
                prefix + ".description",
                prefix + ".suggestion");
        }

        private static TooltipDefinition Plain(string prefix)
        {
            return TooltipDefinition.Plain(prefix + ".description", null);
        }

        public static TooltipDefinition SettingsNavigation
        {
            get { return Named("shell.tip.settingsNavigation"); }
        }

        public static TooltipDefinition SettingsNavigationFull
        {
            get { return Named("shell.tip.settingsNavigationFull"); }
        }

        public static TooltipDefinition StartupNavigation
        {
            get { return Named("shell.tip.startupNavigation"); }
        }

        public static TooltipDefinition LayoutNavigation
        {
            get { return Named("shell.tip.layoutNavigation"); }
        }

        public static TooltipDefinition ApplicationsNavigation
        {
            get { return Named("shell.tip.applicationsNavigation"); }
        }

        public static TooltipDefinition RecycleBinNavigation
        {
            get { return Named("shell.tip.recycleBinNavigation"); }
        }

        public static TooltipDefinition ImmediateRestore
        {
            get { return Named("shell.tip.immediateRestore"); }
        }

        public static TooltipDefinition SearchQuery
        {
            get { return NamedWithSuggestion("shell.tip.searchQuery"); }
        }

        public static TooltipDefinition SearchTimeBox
        {
            get { return Plain("shell.tip.searchTimeBox"); }
        }

        public static TooltipDefinition SavingOverlay
        {
            get { return Plain("shell.tip.savingOverlay"); }
        }

        public static TooltipDefinition RestoreOverlay
        {
            get { return Plain("shell.tip.restoreOverlay"); }
        }

        public static TooltipDefinition SearchDialogSearch
        {
            get { return NamedWithSuggestion("shell.tip.searchDialogSearch"); }
        }

        public static TooltipDefinition ConfirmYes
        {
            get { return Named("shell.tip.confirmYes"); }
        }

        public static TooltipDefinition ConfirmNo
        {
            get { return Named("shell.tip.confirmNo"); }
        }

        public static TooltipDefinition NameInput
        {
            get { return Named("shell.tip.nameInput"); }
        }

        public static TooltipDefinition NameOk
        {
            get { return Named("shell.tip.nameOk"); }
        }

        public static TooltipDefinition NameCancel
        {
            get { return Named("shell.tip.nameCancel"); }
        }

        public static TooltipDefinition CloseInfo
        {
            get { return Named("shell.tip.closeInfo"); }
        }

        public static ToolTipContent SearchResult(string title, string detail)
        {
            return ToolTipContent.Named(
                string.IsNullOrWhiteSpace(title)
                    ? AppText.T(ShellTextKeys.SearchResultFallback)
                    : title,
                string.IsNullOrWhiteSpace(detail)
                    ? AppText.T(ShellTextKeys.SearchResultDetailFallback)
                    : detail);
        }
    }
}
