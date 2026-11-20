namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共组件】一条提示的完整内容。
    ///
    /// 页面不再直接拼“名字 + 说明 + 建议”四个字符串，而是从各模块的
    /// `*ToolTips` 目录取一条 ToolTipContent，交给 AccessibleToolTip 渲染。
    ///
    /// 为什么保留 AccessibleName 和 Title 两个字段：
    ///   · 读屏名字（AccessibleName）有时要带上“查看「…」的说明”；
    ///   · 气泡标题（Title）只要“CPU 上限”这类短标题。
    /// 两者可以不同；SetAccessibleName=false 时用于“文字本身就是内容”的控件，
    /// 不覆盖控件自己的无障碍名字。
    /// </summary>
    internal sealed class ToolTipContent
    {
        public string AccessibleName;

        public string Title;

        public string Description;

        public string Suggestion;

        /// <summary>可选：无障碍 HelpText 想写得比提示正文更具体时用它。</summary>
        public string HelpText;

        /// <summary>true = 同时设置无障碍名字；false = 只挂提示和 HelpText。</summary>
        public bool SetAccessibleName;

        public static ToolTipContent Named(string accessibleName, string description)
        {
            // 和旧版 Attach(element, name, description) 一致：无障碍名字同时当气泡标题。
            return Named(accessibleName, accessibleName, description, null);
        }

        public static ToolTipContent Named(string accessibleName, string description, string suggestion)
        {
            return Named(accessibleName, accessibleName, description, suggestion);
        }

        public static ToolTipContent Named(
            string accessibleName,
            string tooltipTitle,
            string description,
            string suggestion)
        {
            ToolTipContent content = new ToolTipContent();
            content.AccessibleName = accessibleName;
            content.Title = tooltipTitle;
            content.Description = description;
            content.Suggestion = suggestion;
            content.SetAccessibleName = true;
            return content;
        }

        public static ToolTipContent Plain(string description)
        {
            return Plain(description, null);
        }

        public static ToolTipContent Plain(string description, string suggestion)
        {
            ToolTipContent content = new ToolTipContent();
            content.Description = description;
            content.Suggestion = suggestion;
            content.SetAccessibleName = false;
            return content;
        }

        /// <summary>提示正文和 HelpText 分开写的条目（DataTemplate 的简单提示常用）。</summary>
        public static ToolTipContent NamedWithHelp(
            string accessibleName,
            string description,
            string helpText)
        {
            ToolTipContent content = Named(accessibleName, description);
            content.HelpText = helpText;
            return content;
        }

        /// <summary>动态文案：数量、路径、当前名字等由调用方传进来。</summary>
        public static ToolTipContent Dynamic(
            string accessibleName,
            string tooltipTitle,
            string description,
            string suggestion)
        {
            return Named(accessibleName, tooltipTitle, description, suggestion);
        }
    }
}
