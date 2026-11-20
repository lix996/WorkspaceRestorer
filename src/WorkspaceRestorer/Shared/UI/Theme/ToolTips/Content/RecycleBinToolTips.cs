namespace WorkspaceRestorer.UI
{
    /// <summary>回收站页面的提示与问号说明。</summary>
    internal static class RecycleBinToolTips
    {
        private static TooltipDefinition Tip(
            string nameKey,
            string titleKey,
            string descriptionKey,
            string suggestionKey)
        {
            return TooltipDefinition.Named(nameKey, titleKey, descriptionKey, suggestionKey);
        }

        public static HelpDefinition RetentionHelp
        {
            get
            {
                return HelpDefinition.Create(
                    RecycleBinTextKeys.HelpRetentionTitle,
                    RecycleBinTextKeys.HelpRetentionShort,
                    RecycleBinTextKeys.HelpRetentionDetail);
            }
        }

        public static HelpDefinition OperationsHelp
        {
            get
            {
                return HelpDefinition.Create(
                    RecycleBinTextKeys.HelpOperationsTitle,
                    RecycleBinTextKeys.HelpOperationsShort,
                    RecycleBinTextKeys.HelpOperationsDetail);
            }
        }

        public static TooltipDefinition RetentionBox
        {
            get
            {
                return Tip(
                    RecycleBinTextKeys.TipRetentionBoxName,
                    RecycleBinTextKeys.TipRetentionBoxTitle,
                    RecycleBinTextKeys.TipRetentionBoxDescription,
                    RecycleBinTextKeys.TipRetentionBoxSuggestion);
            }
        }

        public static TooltipDefinition SaveRetention
        {
            get
            {
                return Tip(
                    RecycleBinTextKeys.TipSaveRetentionName,
                    RecycleBinTextKeys.TipSaveRetentionTitle,
                    RecycleBinTextKeys.TipSaveRetentionDescription,
                    RecycleBinTextKeys.TipSaveRetentionSuggestion);
            }
        }

        public static TooltipDefinition Search
        {
            get
            {
                return Tip(
                    RecycleBinTextKeys.TipSearchName,
                    RecycleBinTextKeys.TipSearchTitle,
                    RecycleBinTextKeys.TipSearchDescription,
                    RecycleBinTextKeys.TipSearchSuggestion);
            }
        }

        public static TooltipDefinition RestoreSelected
        {
            get
            {
                return Tip(
                    RecycleBinTextKeys.TipRestoreSelectedName,
                    RecycleBinTextKeys.TipRestoreSelectedTitle,
                    RecycleBinTextKeys.TipRestoreSelectedDescription,
                    RecycleBinTextKeys.TipRestoreSelectedSuggestion);
            }
        }

        public static TooltipDefinition DeleteSelected
        {
            get
            {
                return Tip(
                    RecycleBinTextKeys.TipDeleteSelectedName,
                    RecycleBinTextKeys.TipDeleteSelectedTitle,
                    RecycleBinTextKeys.TipDeleteSelectedDescription,
                    RecycleBinTextKeys.TipDeleteSelectedSuggestion);
            }
        }

        public static TooltipDefinition SelectAll(bool selectAllSections)
        {
            return Tip(
                RecycleBinTextKeys.TipSelectAllName,
                RecycleBinTextKeys.TipSelectAllTitle,
                selectAllSections
                    ? RecycleBinTextKeys.TipSelectAllDescriptionAll
                    : RecycleBinTextKeys.TipSelectAllDescriptionSection,
                selectAllSections
                    ? RecycleBinTextKeys.TipSelectAllSuggestionAll
                    : RecycleBinTextKeys.TipSelectAllSuggestionSection);
        }

        public static TooltipDefinition RowCheckBox(string title)
        {
            string name = string.IsNullOrWhiteSpace(title)
                ? AppText.T(RecycleBinTextKeys.RowUntitled)
                : title;
            return TooltipDefinition.NamedLiteral(
                name,
                name,
                RecycleBinTextKeys.TipRowCheckBoxDescription,
                null);
        }

        public static TooltipDefinition Remaining(int remainingDays)
        {
            return TooltipDefinition.Named(
                RecycleBinTextKeys.TipRemainingName,
                RecycleBinTextKeys.TipRemainingTitle,
                remainingDays < 0
                    ? RecycleBinTextKeys.TipRemainingPermanentDescription
                    : RecycleBinTextKeys.TipRemainingDescriptionFormat,
                RecycleBinTextKeys.TipRemainingSuggestion)
                .WithArguments(remainingDays);
        }

        public static TooltipDefinition RowHover(string title, string kind, int remainingDays)
        {
            string name = string.IsNullOrWhiteSpace(title)
                ? AppText.T(RecycleBinTextKeys.RowUntitled)
                : title;
            string remainingText = remainingDays < 0
                ? AppText.T(RecycleBinTextKeys.TipRowHoverPermanentText)
                : AppText.F(RecycleBinTextKeys.TipRowHoverRemainingFormat, remainingDays);
            string kindKey = RecycleBinTextKeys.KindKey(kind);
            string kindName = kindKey == null ? (kind ?? string.Empty) : AppText.T(kindKey);
            return TooltipDefinition.NamedLiteral(
                name,
                name,
                RecycleBinTextKeys.TipRowHoverDescriptionFormat,
                null)
                .WithArguments(kindName, remainingText);
        }
    }
}
