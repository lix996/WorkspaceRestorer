namespace WorkspaceRestorer.UI
{
    /// <summary>设置页、导航和通用页的文案 key。值在 Resources/{语言}/Settings/Settings.json。</summary>
    internal static class SettingsTextKeys
    {
        public const string PageTitle = "settings.page.title";
        public const string PageSubtitle = "settings.page.subtitle";

        public const string NavGeneral = "settings.nav.general";
        public const string NavGeneralDescription = "settings.nav.general.description";
        public const string NavStartup = "settings.nav.startup";
        public const string NavStartupDescription = "settings.nav.startup.description";
        public const string NavApplications = "settings.nav.applications";
        public const string NavApplicationsDescription = "settings.nav.applications.description";
        public const string NavLayout = "settings.nav.layout";
        public const string NavLayoutDescription = "settings.nav.layout.description";
        public const string NavShortcuts = "settings.nav.shortcuts";
        public const string NavShortcutsDescription = "settings.nav.shortcuts.description";
        public const string NavPreferences = "settings.nav.preferences";
        public const string NavPreferencesDescription = "settings.nav.preferences.description";

        public const string GeneralTitle = "settings.general.title";
        public const string GeneralSubtitle = "settings.general.subtitle";
        public const string LanguageLabel = "settings.language.label";
        public const string LanguageHint = "settings.language.hint";
        public const string LanguageDetail = "settings.language.detail";
        public const string LanguageAppliedHint = "settings.language.appliedHint";
        public const string AppearanceResetHint = "settings.appearance.resetHint";

        public const string ThemeLabel = "settings.theme.label";
        public const string ThemeHint = "settings.theme.hint";
        public const string ThemeDetail = "settings.theme.detail";
        public const string ThemeLight = "settings.theme.light";
        public const string ThemeDark = "settings.theme.dark";
        public const string ThemeSystem = "settings.theme.system";
        public const string ThemeRestartHint = "settings.theme.restartHint";
        public const string ThemeAppliedHint = "settings.theme.appliedHint";

        public const string PriorityStartupFirst = "settings.priority.startupFirst";
        public const string PriorityApplicationsFirst = "settings.priority.applicationsFirst";
        public const string ResetDragSpeedTipName = "settings.tip.resetDragSpeed.name";
        public const string ResetDragSpeedTipTitle = "settings.tip.resetDragSpeed.title";
        public const string ResetDragSpeedTipDescription = "settings.tip.resetDragSpeed.description";

        public const string ResetSectionLabel = "settings.resetSection.label";
        public const string ResetSectionTitle = "settings.resetSection.title";
        public const string ResetSectionShort = "settings.resetSection.short";
        public const string ResetSectionDetail = "settings.resetSection.detail";
        public const string ResetSectionNoneShort = "settings.resetSection.noneShort";
        public const string ResetSectionNoneDetail = "settings.resetSection.noneDetail";
        public const string ResetSectionConfirmTitleFormat = "settings.resetSection.confirmTitleFormat";
        public const string ResetSectionConfirmMessageFormat = "settings.resetSection.confirmMessageFormat";
        public const string ResetAllLabel = "settings.resetAll.label";
        public const string ResetAllConfirmTitle = "settings.resetAll.confirmTitle";
        public const string ResetAllConfirmBody = "settings.resetAll.confirmBody";
        public const string ResetAllDone = "settings.resetAll.done";

        public const string ShortcutsTitle = "settings.shortcuts.title";
        public const string ShortcutsSubtitle = "settings.shortcuts.subtitle";
        /// <summary>快捷键标题下只显示一句话，完整说明在问号里（见 ShortcutsSubtitle）。</summary>
        public const string ShortcutsShort = "settings.shortcuts.short";
        public const string ShortcutsCountFormat = "settings.shortcuts.countFormat";
        public const string ShortcutsScopeLabel = "settings.shortcuts.scope";

        // 【2026-11-16】快捷键编辑：右侧按钮、按键捕获提示、错误与空状态。
        public const string ShortcutUnset = "settings.shortcuts.unset";
        public const string ShortcutEmptyHint = "settings.shortcuts.emptyHint";
        public const string ShortcutPressHint = "settings.shortcuts.pressHint";
        public const string ShortcutCapAccessibleFormat = "settings.shortcuts.capAccessibleFormat";
        public const string ShortcutSaved = "settings.shortcuts.saved";
        public const string ShortcutDeleted = "settings.shortcuts.deleted";
        public const string ShortcutRestored = "settings.shortcuts.restored";
        public const string ShortcutTableBinding = "settings.shortcuts.table.binding";
        public const string ShortcutTableCommand = "settings.shortcuts.table.command";
        public const string ShortcutTableActions = "settings.shortcuts.table.actions";

        // 【2026-11-19】表格底部分页条：照 ComfyUI KeybindingPanel 的页码 + 每页条数。
        public const string ShortcutsPagerFirstName = "settings.shortcuts.pager.first.name";
        public const string ShortcutsPagerFirstTitle = "settings.shortcuts.pager.first.title";
        public const string ShortcutsPagerFirstDescription = "settings.shortcuts.pager.first.description";
        public const string ShortcutsPagerPreviousName = "settings.shortcuts.pager.previous.name";
        public const string ShortcutsPagerPreviousTitle = "settings.shortcuts.pager.previous.title";
        public const string ShortcutsPagerPreviousDescription = "settings.shortcuts.pager.previous.description";
        public const string ShortcutsPagerNextName = "settings.shortcuts.pager.next.name";
        public const string ShortcutsPagerNextTitle = "settings.shortcuts.pager.next.title";
        public const string ShortcutsPagerNextDescription = "settings.shortcuts.pager.next.description";
        public const string ShortcutsPagerLastName = "settings.shortcuts.pager.last.name";
        public const string ShortcutsPagerLastTitle = "settings.shortcuts.pager.last.title";
        public const string ShortcutsPagerLastDescription = "settings.shortcuts.pager.last.description";
        public const string ShortcutsPagerPageName = "settings.shortcuts.pager.page.name";
        public const string ShortcutsPagerPageTitle = "settings.shortcuts.pager.page.title";
        public const string ShortcutsPagerPageDescription = "settings.shortcuts.pager.page.description";
        public const string ShortcutsPagerRowsName = "settings.shortcuts.pager.rows.name";
        public const string ShortcutsPagerRowsTitle = "settings.shortcuts.pager.rows.title";
        public const string ShortcutsPagerRowsDescription = "settings.shortcuts.pager.rows.description";
        public const string ShortcutsPagerRowsLabel = "settings.shortcuts.pager.rowsLabel";

        public const string ShortcutSearchButton = "settings.shortcuts.search.button";
        public const string ShortcutSearchAction = "settings.shortcuts.search.action";
        public const string ShortcutSearchTitle = "settings.shortcuts.search.title";
        public const string ShortcutSearchDescription = "settings.shortcuts.search.description";
        public const string ShortcutSearchEmpty = "settings.shortcuts.search.empty";
        public const string ShortcutEditTitle = "settings.shortcuts.dialog.editTitle";
        public const string ShortcutAddTitle = "settings.shortcuts.dialog.addTitle";
        public const string ShortcutEditBody = "settings.shortcuts.dialog.editBody";
        public const string ShortcutAddBody = "settings.shortcuts.dialog.addBody";
        public const string ShortcutCancel = "settings.shortcuts.dialog.cancel";
        public const string ShortcutSave = "settings.shortcuts.dialog.save";
        // 【2026-11-17】录键弹窗左下角的撤销 / 重做（只作用于弹窗里的录键历史）。
        public const string ShortcutUndo = "settings.shortcuts.dialog.undo";
        public const string ShortcutRedo = "settings.shortcuts.dialog.redo";
        public const string ShortcutEditTipName = "settings.shortcuts.edit.tipName";
        public const string ShortcutEditTipTitle = "settings.shortcuts.edit.tipTitle";
        public const string ShortcutEditTipDescription = "settings.shortcuts.edit.tipDescription";
        public const string ShortcutDeleteTipName = "settings.shortcuts.delete.tipName";
        public const string ShortcutDeleteTipTitle = "settings.shortcuts.delete.tipTitle";
        public const string ShortcutDeleteTipDescription = "settings.shortcuts.delete.tipDescription";
        public const string ShortcutAddTipName = "settings.shortcuts.add.tipName";
        public const string ShortcutAddTipTitle = "settings.shortcuts.add.tipTitle";
        public const string ShortcutAddTipDescription = "settings.shortcuts.add.tipDescription";
        public const string ShortcutRestoreTipName = "settings.shortcuts.restore.tipName";
        public const string ShortcutRestoreTipTitle = "settings.shortcuts.restore.tipTitle";
        public const string ShortcutRestoreTipDescription = "settings.shortcuts.restore.tipDescription";
        public const string ShortcutResetAllLabel = "settings.shortcuts.resetAll.label";
        public const string ShortcutResetAllShort = "settings.shortcuts.resetAll.short";
        public const string ShortcutResetAllDetail = "settings.shortcuts.resetAll.detail";
        public const string ShortcutResetAllConfirmTitle = "settings.shortcuts.resetAll.confirmTitle";
        public const string ShortcutResetAllConfirmMessage = "settings.shortcuts.resetAll.confirmMessage";
        public const string ShortcutResetAllDone = "settings.shortcuts.resetAll.done";
        public const string ShortcutErrorNoKey = "settings.shortcuts.error.noKey";
        public const string ShortcutErrorOnlyModifier = "settings.shortcuts.error.onlyModifier";
        public const string ShortcutErrorReservedFormat = "settings.shortcuts.error.reservedFormat";
        public const string ShortcutErrorUnknownCommand = "settings.shortcuts.error.unknownCommand";
        public const string ShortcutErrorDuplicateFormat = "settings.shortcuts.error.duplicateFormat";
        public const string ShortcutErrorConflictFormat = "settings.shortcuts.error.conflictFormat";
        public const string ShortcutSaveFailed = "settings.shortcuts.error.saveFailed";
        public const string ShortcutPageBuildFailed = "settings.shortcuts.error.pageBuildFailed";

        // 【2026-11-16】双击修改、右键复制、单键警告。
        public const string ShortcutDoubleClickHint = "settings.shortcuts.doubleClickHint";
        public const string ShortcutCopyMenu = "settings.shortcuts.copyMenu";
        public const string ShortcutCopiedFormat = "settings.shortcuts.copiedFormat";
        public const string ShortcutCopyFailed = "settings.shortcuts.copyFailed";
        // 【2026-11-17】绑定列右侧向右箭头：+N 徽标 + “还有 +N 个快捷键”，点击弹浮窗。
        public const string ShortcutExpandMoreFormat = "settings.shortcuts.expandMoreFormat";
        public const string ShortcutOtherBindings = "settings.shortcuts.otherBindings";
        public const string ShortcutSingleKeyWarning = "settings.shortcuts.warning.singleKey";

        // 【2026-11-16】拖动行：前缀 + 左键/右键拖动。
        public const string DragGestureWord = "settings.shortcuts.drag.word";
        public const string DragGestureEditTitle = "settings.shortcuts.drag.editTitle";
        public const string DragGestureBody = "settings.shortcuts.drag.body";
        public const string DragGestureLeftButton = "settings.shortcuts.drag.leftButton";
        public const string DragGestureRightButton = "settings.shortcuts.drag.rightButton";
        public const string DragGestureMouseLeftName = "settings.shortcuts.drag.mouseLeft";
        public const string DragGestureMouseRightName = "settings.shortcuts.drag.mouseRight";
        public const string DragGestureSaved = "settings.shortcuts.drag.saved";
        public const string DragGestureRestored = "settings.shortcuts.drag.restored";
        public const string DragGestureErrorNoPrefix = "settings.shortcuts.drag.error.noPrefix";
        public const string DragGestureErrorSave = "settings.shortcuts.drag.error.save";
        public const string DragGestureWarningTyping = "settings.shortcuts.drag.warning.typing";
        public const string DragGestureAccessibleFormat = "settings.shortcuts.drag.capAccessibleFormat";
        public const string DragGestureEditTipName = "settings.shortcuts.drag.edit.tipName";
        public const string DragGestureEditTipTitle = "settings.shortcuts.drag.edit.tipTitle";
        public const string DragGestureEditTipDescription = "settings.shortcuts.drag.edit.tipDescription";
        public const string DragGestureRestoreTipName = "settings.shortcuts.drag.restore.tipName";
        public const string DragGestureRestoreTipTitle = "settings.shortcuts.drag.restore.tipTitle";
        public const string DragGestureRestoreTipDescription = "settings.shortcuts.drag.restore.tipDescription";
    }
}
