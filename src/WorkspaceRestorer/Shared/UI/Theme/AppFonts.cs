using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共组件】全程序唯一的字体来源。
    ///
    /// 字族只在这里定义；页面、公共控件和悬浮提示都从这里取，不再自己
    /// `new FontFamily(...)`。`AppTheme.UiFont` / `IconFont` 保留为兼容入口，
    /// 现有调用点不用改。
    ///
    /// 字号只收“公共控件确实共用”的几档；卡片/清单里的特殊字号仍留在页面，
    /// 避免为了形式统一把现有布局改走样。新增代码优先用这里的角色值。
    /// </summary>
    internal static class AppFonts
    {
        /// <summary>正文与标题字体。</summary>
        public static readonly FontFamily Ui =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI Variable Text, Segoe UI");

        /// <summary>图标字体（按钮、展开箭头、卡片图标）。</summary>
        public static readonly FontFamily Icon =
            new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>桌面图标标签字体：先 Segoe UI，中文回退到微软雅黑。</summary>
        public static readonly FontFamily DesktopLabel =
            new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

        /// <summary>普通符号（例如快捷方式箭头）字体。</summary>
        public static readonly FontFamily Symbol =
            new FontFamily("Segoe UI Symbol, Segoe UI");

        /// <summary>卡片第二行、状态栏小字。</summary>
        public const double Caption = 11.0;

        /// <summary>说明文字、提示正文。</summary>
        public const double Small = 12.0;

        /// <summary>默认正文。</summary>
        public const double Body = 13.0;

        /// <summary>卡片标题、输入框标题。</summary>
        public const double Subtitle = 14.0;

        /// <summary>页面标题。</summary>
        public const double Heading = 18.0;

        /// <summary>悬浮提示标题。</summary>
        public const double TooltipTitle = 12.5;

        /// <summary>悬浮提示正文。</summary>
        public const double TooltipBody = 12.0;

        /// <summary>悬浮提示里的“建议”行。</summary>
        public const double TooltipHint = 11.5;

        /// <summary>问号说明弹框的标题。</summary>
        public const double DialogTitle = 15.0;

        /// <summary>问号说明弹框的正文。</summary>
        public const double DialogBody = 12.5;

        /// <summary>问号按钮里的“?”。</summary>
        public const double HelpGlyph = 10.5;
    }
}
