namespace WorkspaceRestorer.UI
{
    /// <summary>「图标位置保存」页面、卡片和编辑器的静态提示文案；正文在 DesktopLayout.json。</summary>
    internal static class DesktopLayoutToolTips
    {
        private static TooltipDefinition Named(
            string nameKey,
            string descriptionKey,
            string suggestionKey)
        {
            return TooltipDefinition.Named(nameKey, nameKey, descriptionKey, suggestionKey);
        }

        public static TooltipDefinition SaveLayout
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.SaveLayout,
                    DesktopLayoutTextKeys.TipSaveLayoutDescription,
                    DesktopLayoutTextKeys.TipSaveLayoutSuggestion);
            }
        }

        public static TooltipDefinition RestoreNow
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.RestoreNow,
                    DesktopLayoutTextKeys.TipRestoreNowDescription,
                    DesktopLayoutTextKeys.TipRestoreNowSuggestion);
            }
        }

        public static TooltipDefinition SearchLayouts
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.SearchLayouts,
                    DesktopLayoutTextKeys.TipSearchDescription,
                    DesktopLayoutTextKeys.TipSearchSuggestion);
            }
        }

        public static TooltipDefinition AppendTime
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.AppendTime,
                    DesktopLayoutTextKeys.TipAppendTimeDescription,
                    DesktopLayoutTextKeys.TipAppendTimeSuggestion);
            }
        }

        public static TooltipDefinition NewFolder
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.NewFolderAutomation,
                    DesktopLayoutTextKeys.TipNewFolderDescription,
                    DesktopLayoutTextKeys.TipNewFolderSuggestion);
            }
        }

        public static TooltipDefinition ScrollTop
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipScrollAreaName,
                    DesktopLayoutTextKeys.TipScrollAreaDescription,
                    DesktopLayoutTextKeys.TipScrollTopSuggestion);
            }
        }

        public static TooltipDefinition ScrollBottom
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipScrollAreaName,
                    DesktopLayoutTextKeys.TipScrollAreaDescription,
                    DesktopLayoutTextKeys.TipScrollBottomSuggestion);
            }
        }

        public static TooltipDefinition CardExpand
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.ExpandCollapseAutomation,
                    DesktopLayoutTextKeys.TipCardExpandDescription,
                    null);
            }
        }

        public static TooltipDefinition DesktopScreenshot
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipDesktopScreenshotName,
                    DesktopLayoutTextKeys.TipDesktopScreenshotDescription,
                    DesktopLayoutTextKeys.TipDesktopScreenshotSuggestion);
            }
        }

        public static TooltipDefinition CardMove
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipCardMoveName,
                    DesktopLayoutTextKeys.TipCardMoveDescription,
                    null);
            }
        }

        public static TooltipDefinition AddChild
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipAddChildName,
                    DesktopLayoutTextKeys.TipAddChildDescription,
                    null);
            }
        }

        public static TooltipDefinition AddChildFolder
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipAddChildFolderName,
                    DesktopLayoutTextKeys.TipAddChildFolderDescription,
                    null);
            }
        }

        public static TooltipDefinition Edit
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipEditName,
                    DesktopLayoutTextKeys.TipEditDescription,
                    null);
            }
        }

        public static TooltipDefinition SaveHere
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipSaveHereName,
                    DesktopLayoutTextKeys.TipSaveHereDescription,
                    null);
            }
        }

        public static TooltipDefinition RestoreHere
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipRestoreHereName,
                    DesktopLayoutTextKeys.TipRestoreHereDescription,
                    null);
            }
        }

        public static TooltipDefinition DeleteFolder
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.DeleteFolderTitle,
                    DesktopLayoutTextKeys.TipDeleteFolderDescription,
                    null);
            }
        }

        public static TooltipDefinition DeleteHere
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipDeleteHereName,
                    DesktopLayoutTextKeys.TipDeleteHereDescription,
                    null);
            }
        }

        public static HelpDefinition EditorHelp
        {
            get
            {
                return HelpDefinition.Create(
                    DesktopLayoutTextKeys.TipEditName,
                    DesktopLayoutTextKeys.TipEditorHelpShort,
                    DesktopLayoutTextKeys.TipEditorHelpDetail);
            }
        }

        public static TooltipDefinition ScanDeleted
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.EditorScanDeleted,
                    DesktopLayoutTextKeys.TipScanDeletedDescription,
                    DesktopLayoutTextKeys.TipScanDeletedSuggestion);
            }
        }

        public static TooltipDefinition ZoomOut
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipZoomOutName,
                    DesktopLayoutTextKeys.TipZoomOutDescription,
                    null);
            }
        }

        public static TooltipDefinition ZoomIn
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipZoomInName,
                    DesktopLayoutTextKeys.TipZoomInDescription,
                    null);
            }
        }

        public static TooltipDefinition FitWindow
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.EditorFitWindow,
                    DesktopLayoutTextKeys.TipFitWindowDescription,
                    null);
            }
        }

        public static TooltipDefinition CancelEdit
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipCancelEditName,
                    DesktopLayoutTextKeys.TipCancelEditDescription,
                    null);
            }
        }

        public static TooltipDefinition FinishSave
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.EditorFinish,
                    DesktopLayoutTextKeys.TipFinishSaveDescription,
                    DesktopLayoutTextKeys.TipFinishSaveSuggestion);
            }
        }

        public static TooltipDefinition NewIconTag
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.NewIconTagTitle,
                    DesktopLayoutTextKeys.TipNewIconTagDescription,
                    DesktopLayoutTextKeys.TipNewIconTagSuggestion);
            }
        }

        public static TooltipDefinition NewIconFilter
        {
            get
            {
                return Named(
                    DesktopLayoutTextKeys.TipNewIconFilterName,
                    DesktopLayoutTextKeys.TipNewIconFilterDescription,
                    null);
            }
        }

        public static TooltipDefinition MissingIcon
        {
            get
            {
                return TooltipDefinition.Plain(
                    DesktopLayoutTextKeys.TipMissingIconDescription,
                    DesktopLayoutTextKeys.TipMissingIconSuggestion);
            }
        }

        public static TooltipDefinition NewIconScroll(string nameKey)
        {
            return Named(
                nameKey,
                DesktopLayoutTextKeys.TipNewIconScrollDescription,
                null);
        }
    }
}
