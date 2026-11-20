namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共组件】问号说明的一整条内容：标题 + 一句话小字 + 可复制的详细说明。
    ///
    /// 和 ToolTipContent 分开，是因为问号链路有两段文字：
    ///   · Short 显示在设置行里，紧跟问号；
    ///   · Detail 只在点击问号后的 InfoDialog 里显示，可按住左键选中复制。
    /// </summary>
    internal sealed class HelpContent
    {
        public string Title;

        public string Short;

        public string Detail;

        public static HelpContent Create(string title, string shortText, string detail)
        {
            HelpContent content = new HelpContent();
            content.Title = title;
            content.Short = shortText;
            content.Detail = detail;
            return content;
        }
    }
}
