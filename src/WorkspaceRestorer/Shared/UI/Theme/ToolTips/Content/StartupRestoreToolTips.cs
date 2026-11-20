namespace WorkspaceRestorer.UI
{
    /// <summary>「启动与恢复」的静态提示文案目录；动态的名字/数量用方法。</summary>
    internal static class StartupRestoreToolTips
    {
        private static TooltipDefinition Tip(string nameKey, string descriptionKey, string suggestionKey)
        {
            return TooltipDefinition.Named(nameKey, nameKey, descriptionKey, suggestionKey);
        }

        public static TooltipDefinition CaptureCurrent
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipCaptureCurrentName,
                    StartupRestoreTextKeys.TipCaptureCurrentDescription,
                    StartupRestoreTextKeys.TipCaptureCurrentSuggestion);
            }
        }

        public static TooltipDefinition RestoreNow
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipRestoreNowName,
                    StartupRestoreTextKeys.TipRestoreNowDescription,
                    StartupRestoreTextKeys.TipRestoreNowSuggestion);
            }
        }

        public static TooltipDefinition Search
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipSearchName,
                    StartupRestoreTextKeys.TipSearchDescription,
                    StartupRestoreTextKeys.TipSearchSuggestion);
            }
        }

        public static TooltipDefinition AppendTime
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipAppendTimeName,
                    StartupRestoreTextKeys.TipAppendTimeDescription,
                    StartupRestoreTextKeys.TipAppendTimeSuggestion);
            }
        }

        public static TooltipDefinition DropPadBottom
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipDropPadBottomName,
                    StartupRestoreTextKeys.TipDropPadBottomDescription,
                    StartupRestoreTextKeys.TipDropPadBottomSuggestion);
            }
        }

        public static TooltipDefinition DropPadTop
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipDropPadTopName,
                    StartupRestoreTextKeys.TipDropPadTopDescription,
                    StartupRestoreTextKeys.TipDropPadTopSuggestion);
            }
        }

        public static TooltipDefinition ProfileExpand
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipProfileExpandName,
                    StartupRestoreTextKeys.TipProfileExpandDescription,
                    null);
            }
        }

        public static TooltipDefinition ProfileSave
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipProfileSaveName,
                    StartupRestoreTextKeys.TipProfileSaveDescription,
                    null);
            }
        }

        public static TooltipDefinition ProfileRestore
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipProfileRestoreName,
                    StartupRestoreTextKeys.TipProfileRestoreDescription,
                    null);
            }
        }

        public static TooltipDefinition ProfileDelete
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipProfileDeleteName,
                    StartupRestoreTextKeys.TipProfileDeleteDescription,
                    null);
            }
        }

        public static TooltipDefinition ProfileMove
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipProfileMoveName,
                    StartupRestoreTextKeys.TipProfileMoveDescription,
                    null);
            }
        }

        public static TooltipDefinition FolderCannotSave
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipFolderCannotSaveName,
                    StartupRestoreTextKeys.TipFolderCannotSaveDescription,
                    null);
            }
        }

        public static TooltipDefinition ProfileName
        {
            get
            {
                return TooltipDefinition.Plain(
                    StartupRestoreTextKeys.TipProfileNamePlainDescription, null);
            }
        }

        public static TooltipDefinition ItemEnable
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipItemEnableName,
                    StartupRestoreTextKeys.TipItemEnableDescription,
                    null);
            }
        }

        public static TooltipDefinition ItemGrip
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipItemGripName,
                    StartupRestoreTextKeys.TipItemGripDescription,
                    StartupRestoreTextKeys.TipItemGripSuggestion);
            }
        }

        public static TooltipDefinition ItemDelete
        {
            get
            {
                return Tip(
                    StartupRestoreTextKeys.TipItemDeleteName,
                    StartupRestoreTextKeys.TipItemDeleteDescription,
                    null);
            }
        }

        /// <summary>卡片第二行的动态提示（数量与保存时间由调用方算好）。</summary>
        public static TooltipDefinition ProfileCount(string tooltip)
        {
            TooltipDefinition definition = TooltipDefinition.Literal(null, null, tooltip, null);
            definition.SetAccessibleName = false;
            return definition;
        }

        /// <summary>拖动落点动态提示：名字/标题固定为“落点提示”，正文是当前落点。</summary>
        public static TooltipDefinition DragHint(string text)
        {
            string name = AppText.T(StartupRestoreTextKeys.TipDragHintName);
            TooltipDefinition definition = TooltipDefinition.NamedLiteral(
                name, name, null, null);
            definition.DescriptionText = text;
            return definition;
        }
    }
}
