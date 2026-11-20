namespace WorkspaceRestorer.UI
{
    /// <summary>跨模块复用的少量文案 key。值在 Resources/{语言}/Common/Common.json。</summary>
    internal static class CommonTextKeys
    {
        public const string WorkProfileFallback = "common.workProfileFallback";
        public const string StartupPlanFallback = "common.startupPlanFallback";
        public const string LayoutFallback = "common.layoutFallback";
        public const string SuggestionFormat = "common.tooltip.suggestionFormat";
        public const string HelpAccessibleNameFormat = "common.help.accessibleNameFormat";
        public const string PositionFront = "common.position.front";
        public const string PositionBack = "common.position.back";
        public const string DataCannotWriteFormat = "common.data.cannotWriteFormat";
        public const string DataCannotWriteTitle = "common.data.cannotWriteTitle";

        // ---- 公共卡片/拖动引擎（三个工作区共用同一套手感）的落点提示 ----
        public const string ItemFallback = "common.itemFallback";
        public const string ThisItemFallback = "common.thisItemFallback";
        public const string PlanFallback = "common.planFallback";

        public const string DragHintMoveToFront = "common.drag.hint.moveToFront";
        public const string DragHintMoveToBack = "common.drag.hint.moveToBack";
        public const string DragHintMoveItemToFrontFormat = "common.drag.hint.moveItemToFrontFormat";
        public const string DragHintMoveItemToBackFormat = "common.drag.hint.moveItemToBackFormat";
        public const string DragHintInsertBeforeFormat = "common.drag.hint.insertBeforeFormat";
        public const string DragHintInsertAfterFormat = "common.drag.hint.insertAfterFormat";
        public const string DragHintInsertBetweenFormat = "common.drag.hint.insertBetweenFormat";
        public const string DragHintCancelMoveFormat = "common.drag.hint.cancelMoveFormat";
        public const string DragHintCancelDrop = "common.drag.hint.cancelDrop";
        public const string DragHintForbiddenDescendant = "common.drag.hint.forbiddenDescendant";
        public const string DragHintMoveToRootEndFormat = "common.drag.hint.moveToRootEndFormat";
        public const string DragHintDropIntoTargetFormat = "common.drag.hint.dropIntoTargetFormat";
        public const string DragHintDropIntoPlanFormat = "common.drag.hint.dropIntoPlanFormat";
        public const string DragHintMergeIntoFormat = "common.drag.hint.mergeIntoFormat";
        public const string DragHintMergeIntoOnlyFormat = "common.drag.hint.mergeIntoOnlyFormat";
        public const string DragHintCancelledInterrupted = "common.drag.hint.cancelledInterrupted";
        public const string DragHintNoDrop = "common.drag.hint.noDrop";

        // ---- 公共“点击整行跳转”的悬停提示 ----
        public const string JumpHint = "common.jump.hint";
        public const string JumpDragHintSuffix = "common.jump.dragHintSuffix";
        public const string JumpTestModeSkippedFormat = "common.jump.testModeSkippedFormat";
        public const string JumpFailedFormat = "common.jump.failedFormat";

        // ---- 空白处右键菜单 / 粘贴路径或网址新增（启动与恢复与自启动应用共用） ----
        public const string BlankProfileCreatedFormat = "common.status.blankProfileCreatedFormat";
        public const string MenuNewBlankProfile = "common.menu.newBlankProfile";
        public const string MenuPasteAddToCurrentProfile = "common.menu.pasteAddToCurrentProfile";

        public const string PasteTitle = "common.paste.title";
        public const string PastePrompt = "common.paste.prompt";
        public const string PasteMinimized = "common.paste.minimized";
        public const string PasteNormal = "common.paste.normal";
        public const string PasteAdd = "common.paste.add";
        public const string PasteErrorProfileClipboard = "common.paste.errorProfileClipboard";
        public const string PasteErrorInvalidTarget = "common.paste.errorInvalidTarget";
    }
}
