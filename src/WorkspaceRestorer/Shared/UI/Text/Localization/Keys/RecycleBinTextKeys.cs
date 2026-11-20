using WorkspaceRestorer.Models;
using WorkspaceRestorer.RecycleBin.Core;

namespace WorkspaceRestorer.UI
{
    /// <summary>回收站页面、操作、状态和提示的文案 key。正文在 RecycleBin/RecycleBin.json。</summary>
    internal static class RecycleBinTextKeys
    {
        public const string PageTitle = "recycle.page.title";
        public const string PageSubtitle = "recycle.page.subtitle";
        /// <summary>标题下只显示一句话，完整说明在问号里（见 PageSubtitle）。</summary>
        public const string PageShort = "recycle.page.short";

        public const string RetentionLabel = "recycle.retention.label";
        public const string RetentionUnit = "recycle.retention.unit";
        public const string RetentionSave = "recycle.retention.save";
        public const string SearchButton = "recycle.search.button";

        public const string SelectAll = "recycle.actions.selectAll";
        public const string RestoreSelected = "recycle.actions.restoreSelected";
        public const string DeleteSelected = "recycle.actions.deleteSelected";

        public const string EmptyTitle = "recycle.empty.title";
        public const string EmptyDetail = "recycle.empty.detail";

        public const string SearchTitle = "recycle.search.title";
        public const string SearchDescription = "recycle.search.description";
        public const string SearchEmpty = "recycle.search.empty";

        public const string RowDeletedContent = "recycle.row.deletedContent";
        public const string RowUntitled = "recycle.row.untitled";
        public const string RowDeleteAtFormat = "recycle.row.deleteAtFormat";
        public const string RowSubtitleFormat = "recycle.row.subtitleFormat";
        public const string RowPermanent = "recycle.row.permanent";
        public const string RowRemainingFormat = "recycle.row.remainingFormat";
        public const string RowSourceFormat = "recycle.row.sourceFormat";

        public const string MenuRestoreOne = "recycle.menu.restoreOne";
        public const string MenuDeleteOne = "recycle.menu.deleteOne";

        public const string ConfirmDeleteTitle = "recycle.confirm.deleteTitle";
        public const string ConfirmDeleteSelectedFormat = "recycle.confirm.deleteSelectedFormat";
        public const string ConfirmDeleteOneFormat = "recycle.confirm.deleteOneFormat";

        public const string StatusRetentionSavedFormat = "recycle.status.retentionSavedFormat";
        public const string StatusRetentionSavedPermanent = "recycle.status.retentionSavedPermanent";
        public const string StatusRetentionSavedWithPurgeFormat = "recycle.status.retentionSavedWithPurgeFormat";
        public const string StatusSelectEmpty = "recycle.status.selectEmpty";
        public const string StatusSelectAllFormat = "recycle.status.selectAllFormat";
        public const string StatusSelectSectionFormat = "recycle.status.selectSectionFormat";
        public const string StatusDeselectAll = "recycle.status.deselectAll";
        public const string StatusSelectRestoreFirst = "recycle.status.selectRestoreFirst";
        public const string StatusRestoredFormat = "recycle.status.restoredFormat";
        public const string StatusRestoredPartialFormat = "recycle.status.restoredPartialFormat";
        public const string StatusSelectDeleteFirst = "recycle.status.selectDeleteFirst";
        public const string StatusDeletedSelectedFormat = "recycle.status.deletedSelectedFormat";
        public const string StatusRestoredOneFormat = "recycle.status.restoredOneFormat";
        public const string StatusRestoreUnsupported = "recycle.status.restoreUnsupported";
        public const string StatusRestoreFailed = "recycle.status.restoreFailed";
        public const string StatusDeletedOneFormat = "recycle.status.deletedOneFormat";
        public const string StatusLocatedFormat = "recycle.status.locatedFormat";
        public const string StatusAutoPurgedFormat = "recycle.status.autoPurgedFormat";

        public const string UndoDeleteSelectedFormat = "recycle.undo.deleteSelectedFormat";
        public const string UndoDeleteOneFormat = "recycle.undo.deleteOneFormat";

        public const string HelpRetentionTitle = "recycle.help.retention.title";
        public const string HelpRetentionShort = "recycle.help.retention.short";
        public const string HelpRetentionDetail = "recycle.help.retention.detail";
        public const string HelpOperationsTitle = "recycle.help.operations.title";
        public const string HelpOperationsShort = "recycle.help.operations.short";
        public const string HelpOperationsDetail = "recycle.help.operations.detail";

        public const string TipRetentionBoxName = "recycle.tip.retentionBox.name";
        public const string TipRetentionBoxTitle = "recycle.tip.retentionBox.title";
        public const string TipRetentionBoxDescription = "recycle.tip.retentionBox.description";
        public const string TipRetentionBoxSuggestion = "recycle.tip.retentionBox.suggestion";
        public const string TipSaveRetentionName = "recycle.tip.saveRetention.name";
        public const string TipSaveRetentionTitle = "recycle.tip.saveRetention.title";
        public const string TipSaveRetentionDescription = "recycle.tip.saveRetention.description";
        public const string TipSaveRetentionSuggestion = "recycle.tip.saveRetention.suggestion";
        public const string TipSearchName = "recycle.tip.search.name";
        public const string TipSearchTitle = "recycle.tip.search.title";
        public const string TipSearchDescription = "recycle.tip.search.description";
        public const string TipSearchSuggestion = "recycle.tip.search.suggestion";
        public const string TipRestoreSelectedName = "recycle.tip.restoreSelected.name";
        public const string TipRestoreSelectedTitle = "recycle.tip.restoreSelected.title";
        public const string TipRestoreSelectedDescription = "recycle.tip.restoreSelected.description";
        public const string TipRestoreSelectedSuggestion = "recycle.tip.restoreSelected.suggestion";
        public const string TipDeleteSelectedName = "recycle.tip.deleteSelected.name";
        public const string TipDeleteSelectedTitle = "recycle.tip.deleteSelected.title";
        public const string TipDeleteSelectedDescription = "recycle.tip.deleteSelected.description";
        public const string TipDeleteSelectedSuggestion = "recycle.tip.deleteSelected.suggestion";
        public const string TipSelectAllName = "recycle.tip.selectAll.name";
        public const string TipSelectAllTitle = "recycle.tip.selectAll.title";
        public const string TipSelectAllDescriptionAll = "recycle.tip.selectAll.descriptionAll";
        public const string TipSelectAllDescriptionSection = "recycle.tip.selectAll.descriptionSection";
        public const string TipSelectAllSuggestionAll = "recycle.tip.selectAll.suggestionAll";
        public const string TipSelectAllSuggestionSection = "recycle.tip.selectAll.suggestionSection";
        public const string TipRowCheckBoxDescription = "recycle.tip.rowCheckBox.description";
        public const string TipRemainingName = "recycle.tip.remaining.name";
        public const string TipRemainingTitle = "recycle.tip.remaining.title";
        public const string TipRemainingPermanentDescription = "recycle.tip.remaining.permanentDescription";
        public const string TipRemainingDescriptionFormat = "recycle.tip.remaining.descriptionFormat";
        public const string TipRemainingSuggestion = "recycle.tip.remaining.suggestion";
        public const string TipRowHoverDescriptionFormat = "recycle.tip.rowHover.descriptionFormat";
        public const string TipRowHoverPermanentText = "recycle.tip.rowHover.permanentText";
        public const string TipRowHoverRemainingFormat = "recycle.tip.rowHover.remainingFormat";

        /// <summary>内部 Kind 值（中文数据）到显示 key 的映射；未知类别返回原值。</summary>
        internal static string KindKey(string kind)
        {
            if (string.Equals(kind, RecycleBinKind.StartupProfile, System.StringComparison.Ordinal))
            {
                return SettingsTextKeys.NavStartup;
            }
            if (string.Equals(kind, RecycleBinKind.DesktopLayout, System.StringComparison.Ordinal))
            {
                return SettingsTextKeys.NavLayout;
            }
            if (string.Equals(kind, RecycleBinKind.Application, System.StringComparison.Ordinal))
            {
                return SettingsTextKeys.NavApplications;
            }
            return null;
        }
    }
}
