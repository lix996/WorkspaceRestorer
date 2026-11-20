namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 最后一页之后的纯缩窗决策。
    /// 流程代码只负责收集事实，这里回答“现在能不能缩”。
    ///
    /// 关键边界：没有精确 HWND、用户已接管、没有标签/标题证据时，
    /// 一律不缩；绝不按标题另找一个窗口来缩。
    /// </summary>
    internal enum BrowserMinimizeAction
    {
        Skip = 0,
        Minimize = 1
    }

    internal static class BrowserMinimizeDecision
    {
        internal static BrowserMinimizeAction Decide(
            bool hasExactWindow,
            bool userClosed,
            bool userMinimized,
            bool tabsReady,
            bool titleOrPageMatches,
            bool exactTabCount)
        {
            if (!hasExactWindow || userClosed || userMinimized)
            {
                return BrowserMinimizeAction.Skip;
            }
            if (tabsReady)
            {
                return BrowserMinimizeAction.Minimize;
            }
            if (titleOrPageMatches || exactTabCount)
            {
                return BrowserMinimizeAction.Minimize;
            }

            // 单页空白标题的“标签已出现”已经由 tabsReady 分支覆盖；
            // 多页没有标签/标题/精确页数证据时不猜。
            return BrowserMinimizeAction.Skip;
        }

        /// <summary>
        /// 纯测试/日志用：没有标签证据时，只有标题/页数或精确 UIA 标签数
        /// 才能作为兜底依据。
        /// </summary>
        internal static bool CanFallbackToTitle(
            bool titleOrPageMatches,
            bool exactTabCount)
        {
            return titleOrPageMatches || exactTabCount;
        }
    }
}
