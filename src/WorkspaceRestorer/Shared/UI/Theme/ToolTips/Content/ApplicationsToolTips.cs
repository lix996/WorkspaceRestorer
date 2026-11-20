namespace WorkspaceRestorer.UI
{
    /// <summary>「自启动应用」的静态提示文案目录；动态的数量/时间用方法。</summary>
    internal static class ApplicationsToolTips
    {
        private static TooltipDefinition Tip(string nameKey, string descriptionKey, string suggestionKey)
        {
            return TooltipDefinition.Named(nameKey, nameKey, descriptionKey, suggestionKey);
        }

        public static TooltipDefinition ScanDesktop
        {
            get { return Tip(ApplicationsTextKeys.ScanDesktop, ApplicationsTextKeys.TipScanDesktopDescription, null); }
        }

        public static TooltipDefinition AddSelectedToStartup
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.AddSelectedToStartup,
                    ApplicationsTextKeys.TipAddSelectedDescription,
                    ApplicationsTextKeys.TipAddSelectedSuggestion);
            }
        }

        public static TooltipDefinition BrowseFiles
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.BrowseFiles,
                    ApplicationsTextKeys.TipBrowseFilesDescription,
                    ApplicationsTextKeys.TipBrowseFilesSuggestion);
            }
        }

        public static TooltipDefinition BrowseFolder
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.BrowseFolder,
                    ApplicationsTextKeys.TipBrowseFolderDescription,
                    ApplicationsTextKeys.TipBrowseFolderSuggestion);
            }
        }

        public static TooltipDefinition RestoreNow
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.RestoreNow,
                    ApplicationsTextKeys.TipRestoreNowDescription,
                    ApplicationsTextKeys.TipRestoreNowSuggestion);
            }
        }

        public static TooltipDefinition SearchPlan
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.SearchPlan,
                    ApplicationsTextKeys.TipSearchPlanDescription,
                    ApplicationsTextKeys.TipSearchPlanSuggestion);
            }
        }

        public static TooltipDefinition AppendTime
        {
            get { return Tip(ApplicationsTextKeys.AppendTime, ApplicationsTextKeys.TipAppendTimeDescription, null); }
        }

        public static TooltipDefinition SearchDesktopResults
        {
            get
            {
                return Tip(
                    ApplicationsTextKeys.TipSearchResultsName,
                    ApplicationsTextKeys.TipSearchResultsDescription,
                    ApplicationsTextKeys.TipSearchResultsSuggestion);
            }
        }

        public static TooltipDefinition AddChecked
        {
            get { return Tip(ApplicationsTextKeys.AddChecked, ApplicationsTextKeys.TipAddCheckedDescription, null); }
        }

        public static TooltipDefinition CheckAll
        {
            get { return Tip(ApplicationsTextKeys.SelectAll, ApplicationsTextKeys.TipCheckAllDescription, null); }
        }

        public static TooltipDefinition UncheckAll
        {
            get { return Tip(ApplicationsTextKeys.UnselectAll, ApplicationsTextKeys.TipUncheckAllDescription, null); }
        }

        public static TooltipDefinition CloseScan
        {
            get { return Tip(ApplicationsTextKeys.CloseScan, ApplicationsTextKeys.TipCloseScanDescription, null); }
        }

        private static TooltipDefinition Named(string nameKey, string descriptionKey, string suggestionKey)
        {
            return TooltipDefinition.Named(nameKey, nameKey, descriptionKey, suggestionKey);
        }

        public static TooltipDefinition DesktopSelectionFactory
        {
            get
            {
                return TooltipDefinition.Named(
                    ApplicationsTextKeys.TipDesktopSelectionName,
                    ApplicationsTextKeys.TipDesktopSelectionName,
                    ApplicationsTextKeys.TipDesktopSelectionDescription,
                    null)
                    .WithHelpText(ApplicationsTextKeys.TipDesktopSelectionHelp);
            }
        }

        public static TooltipDefinition DesktopOpenFactory
        {
            get
            {
                return TooltipDefinition.Named(
                    ApplicationsTextKeys.TipDesktopOpenName,
                    ApplicationsTextKeys.TipDesktopOpenName,
                    ApplicationsTextKeys.TipDesktopOpenDescription,
                    null)
                    .WithHelpText(ApplicationsTextKeys.TipDesktopOpenHelp);
            }
        }

        public static TooltipDefinition DesktopPick
        {
            get { return Named(ApplicationsTextKeys.TipDesktopSelectionName, ApplicationsTextKeys.TipDesktopPickDescription, null); }
        }

        public static TooltipDefinition DesktopAddOne
        {
            get { return Named(ApplicationsTextKeys.TipDesktopAddOneName, ApplicationsTextKeys.TipDesktopAddOneDescription, null); }
        }

        public static TooltipDefinition DesktopOpenTile
        {
            get { return Named(ApplicationsTextKeys.TipDesktopOpenTileName, ApplicationsTextKeys.TipDesktopOpenTileDescription, null); }
        }

        public static TooltipDefinition DesktopRemove
        {
            get { return Named(ApplicationsTextKeys.TipDesktopRemoveName, ApplicationsTextKeys.TipDesktopRemoveDescription, null); }
        }

        public static TooltipDefinition PlanExpand
        {
            get { return Named(ApplicationsTextKeys.TipPlanExpandName, ApplicationsTextKeys.TipPlanExpandDescription, null); }
        }

        public static TooltipDefinition ItemDelete
        {
            get { return Named(ApplicationsTextKeys.TipItemDeleteName, ApplicationsTextKeys.TipItemDeleteDescription, null); }
        }

        public static TooltipDefinition PlanName
        {
            get { return TooltipDefinition.Plain(ApplicationsTextKeys.TipPlanNamePlainDescription, null); }
        }

        public static TooltipDefinition PlanMove
        {
            get { return Named(ApplicationsTextKeys.TipPlanMoveName, ApplicationsTextKeys.TipPlanMoveDescription, null); }
        }

        public static TooltipDefinition PlanCopyToStartup
        {
            get { return Named(ApplicationsTextKeys.TipPlanCopyName, ApplicationsTextKeys.TipPlanCopyDescription, null); }
        }

        public static TooltipDefinition PlanRestoreThis
        {
            get { return Named(ApplicationsTextKeys.TipPlanRestoreName, ApplicationsTextKeys.TipPlanRestoreDescription, null); }
        }

        public static TooltipDefinition PlanDelete
        {
            get { return Named(ApplicationsTextKeys.TipPlanDeleteName, ApplicationsTextKeys.TipPlanDeleteDescription, null); }
        }

        public static TooltipDefinition GroupEnable
        {
            get { return Named(ApplicationsTextKeys.TipGroupEnableName, ApplicationsTextKeys.TipGroupEnableDescription, null); }
        }

        public static TooltipDefinition GroupGrip
        {
            get { return Named(ApplicationsTextKeys.TipGroupGripName, ApplicationsTextKeys.TipGroupGripDescription, ApplicationsTextKeys.TipGroupGripSuggestion); }
        }

        public static TooltipDefinition ItemEnable
        {
            get { return Named(ApplicationsTextKeys.TipItemEnableName, ApplicationsTextKeys.TipItemEnableDescription, null); }
        }

        public static TooltipDefinition ItemGrip
        {
            get { return Named(ApplicationsTextKeys.TipGroupGripName, ApplicationsTextKeys.TipItemGripDescription, null); }
        }
        public static TooltipDefinition PlanSavedAt(string savedAt)
        {
            if (string.IsNullOrWhiteSpace(savedAt))
            {
                return TooltipDefinition.Plain(ApplicationsTextKeys.TipPlanSavedAtEmpty, null);
            }
            return TooltipDefinition.Plain(ApplicationsTextKeys.TipPlanSavedAtFormat, null)
                .WithArguments(savedAt);
        }

        public static TooltipDefinition DragHint(string text)
        {
            string name = AppText.T(ApplicationsTextKeys.TipDragHintName);
            return TooltipDefinition.Literal(name, name, text, null);
        }

        public static TooltipDefinition SyncStatus(string text)
        {
            return TooltipDefinition.Literal(text, text, text, null);
        }
    }
}
