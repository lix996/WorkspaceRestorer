namespace WorkspaceRestorer.Browsers
{
    /// <summary>Microsoft Edge 的浏览器定义数据；公共捕获/恢复代码只通过 BrowserCatalog 使用。</summary>
    internal static class MicrosoftEdgeBrowserDefinition
    {
        internal static BrowserCatalog.Definition Create()
        {
            return new BrowserCatalog.Definition
            {
                Id = "edge",
                DisplayName = "Microsoft Edge",
                Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "msedge" },
                TitleSuffixes = new[] { " - Microsoft" },
                ChromiumTitle = true
            };
        }
    }
}
