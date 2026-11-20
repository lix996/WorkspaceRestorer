namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 卡片动作按钮的唯一尺寸来源。
    ///
    /// 以前启动与恢复/自启动应用是 32、图标布局是 30、自启动应用的删除还是
    /// IconButton 默认的 38，父行和子行因此对不齐。以后这些数字只在这里改。
    /// </summary>
    internal static class ActionMetrics
    {
        internal const double ButtonSize = 32.0;

        internal const double GripWidth = 26.0;

        internal const double Gap = 4.0;

        internal static double WidthOf(ActionSlot slot, ActionBarKind kind)
        {
            if (slot == ActionSlot.Move
                && (kind == ActionBarKind.ItemRow || kind == ActionBarKind.GroupRow))
            {
                return GripWidth;
            }
            return ButtonSize;
        }
    }
}
