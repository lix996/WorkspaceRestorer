using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 一份「自启动应用」方案（用户 2026-10-09 起要求：自启动应用也要像「启动与恢复」那样
    /// 有方案卡片、能新建/展开/改名/删除）。
    ///
    /// 它与「启动与恢复」的方案是**两套数据**：启动与恢复是“扫描当前会话后一键恢复”，
    /// 自启动应用是“用户自己挑常驻要启动的 bat / 快捷方式”。用户明确说“不要把两个大类过度组合”。
    /// </summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class ApplicationPlan : IPlanFolderNode
    {
        public ApplicationPlan()
        {
            Id = System.Guid.NewGuid().ToString("N");
            Name = "自启动方案";
            CreatedAtUtc = System.DateTime.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
            Items = new System.Collections.Generic.List<ApplicationPlanItem>();
        }

        [System.Runtime.Serialization.DataMember(Order = 1)]
        public string Id { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 2)]
        public string Name { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 3)]
        public System.DateTime CreatedAtUtc { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 4)]
        public System.DateTime UpdatedAtUtc { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 5)]
        public System.Collections.Generic.List<ApplicationPlanItem> Items { get; set; }

        /// <summary>父方案 Id；空 = 根目录。自启动方案也可以无限层嵌套。</summary>
        [System.Runtime.Serialization.DataMember(Order = 6, EmitDefaultValue = false)]
        public string ParentId { get; set; }

        /// <summary>true = 纯文件夹：只装子方案，没有自己的启动项。</summary>
        [System.Runtime.Serialization.DataMember(Order = 7, EmitDefaultValue = false)]
        public bool IsFolder { get; set; }
    }

    /// <summary>自启动方案里的一个启动项（应用、文件、文件夹或网页）。</summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class ApplicationPlanItem
    {
        public ApplicationPlanItem()
        {
            Id = System.Guid.NewGuid().ToString("N");
            IsEnabled = true;
            StartTimeUtc = System.DateTime.UtcNow;
            BrowserWindowIndex = -1;
            BrowserTabIndex = -1;
        }

        [System.Runtime.Serialization.DataMember(Order = 1)]
        public string Id { get; set; }

        /// <summary>显示名（默认取文件名，去扩展名）。</summary>
        [System.Runtime.Serialization.DataMember(Order = 2)]
        public string DisplayName { get; set; }

        /// <summary>要启动的路径或网址。</summary>
        [System.Runtime.Serialization.DataMember(Order = 3)]
        public string TargetPath { get; set; }

        /// <summary>是否随方案启动。</summary>
        [System.Runtime.Serialization.DataMember(Order = 4)]
        public bool IsEnabled { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 5, EmitDefaultValue = false)]
        public string Note { get; set; }

        /// <summary>
        /// 自启动应用恢复时使用的窗口状态（主要用到 WindowState/Minimized）。
        /// 和「启动与恢复」共用同一个模型，旧的自启动方案没有这个字段时按正常启动。
        /// </summary>
        [System.Runtime.Serialization.DataMember(Order = 6, EmitDefaultValue = false)]
        public SavedWindowPlacement WindowPlacement { get; set; }

        /// <summary>
        /// 网页所属的浏览器窗口身份。只有从「启动与恢复」复制过来的网页才有；
        /// 扫描桌面单独添加的网址保持 -1，按普通网址打开。
        /// </summary>
        [System.Runtime.Serialization.DataMember(Order = 7, EmitDefaultValue = false)]
        public string BrowserGroupKey { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 8, EmitDefaultValue = false)]
        public string BrowserExecutablePath { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 9, EmitDefaultValue = false)]
        public string BrowserName { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 10, EmitDefaultValue = true)]
        public int BrowserWindowIndex { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 11, EmitDefaultValue = true)]
        public int BrowserTabIndex { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 12, EmitDefaultValue = false)]
        public bool BrowserTabIdentityCaptured { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 13, EmitDefaultValue = false)]
        public bool IsActiveBrowserTab { get; set; }

        /// <summary>
        /// 旧自启动方案没有浏览器字段；DataContract 反序列化不会调用构造函数，
        /// 不能只靠 BrowserWindowIndex 默认值判断，必须有显式标记。
        /// </summary>
        [System.Runtime.Serialization.DataMember(Order = 14, EmitDefaultValue = false)]
        public bool IsBrowserPage { get; set; }

        /// <summary>和 SessionItemKind 对齐，用来区分程序、文件夹、网页、文档。</summary>
        [System.Runtime.Serialization.DataMember(Order = 15, EmitDefaultValue = true)]
        public SessionItemKind Kind { get; set; }

        /// <summary>父项 Id：浏览器网页挂在浏览器窗口下，文件夹子项挂在文件夹下。</summary>
        [System.Runtime.Serialization.DataMember(Order = 16, EmitDefaultValue = false)]
        public string ParentId { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 17, EmitDefaultValue = false)]
        public bool IsGroupParent { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 18, EmitDefaultValue = false)]
        public bool IsBrowserWindow { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 19, EmitDefaultValue = false)]
        public bool IsFolderGroup { get; set; }

        /// <summary>
        /// 浏览器父行/文件夹父行的窗口标题。启动与恢复的父行副标题显示的是这个，
        /// 不是“N 个网页”；复制方案时从 SessionItem.WindowTitle 带过来。
        /// </summary>
        [System.Runtime.Serialization.DataMember(Order = 20, EmitDefaultValue = false)]
        public string WindowTitle { get; set; }

        /// <summary>父行/项目的记录时间，用于和启动与恢复一样显示本地时间戳。</summary>
        [System.Runtime.Serialization.DataMember(Order = 21, EmitDefaultValue = true)]
        public System.DateTime StartTimeUtc { get; set; }

        /// <summary>
        /// 保存时这个浏览器窗口里的全部标签指纹。旧方案没有时为 null，
        /// 跳转仍按原来的标题/序号兜底。
        /// </summary>
        [System.Runtime.Serialization.DataMember(Order = 22, EmitDefaultValue = false)]
        public System.Collections.Generic.List<BrowserTabFingerprint> BrowserTabs { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 23, EmitDefaultValue = false)]
        public string BrowserWindowTitleSnapshot { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 24, EmitDefaultValue = false)]
        public BrowserPageBindingKind BrowserBindingKind { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 25, EmitDefaultValue = false)]
        public System.DateTime? BrowserBindingUpdatedAtUtc { get; set; }
    }

    /// <summary>自启动方案的清单（与「启动与恢复」的方案文件分开存）。</summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class ApplicationPlanCatalog
    {
        public const int CurrentSchemaVersion = 2;

        public ApplicationPlanCatalog()
        {
            SchemaVersion = CurrentSchemaVersion;
            Plans = new System.Collections.Generic.List<ApplicationPlan>();
        }

        [System.Runtime.Serialization.DataMember(Order = 1)]
        public int SchemaVersion { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 2, EmitDefaultValue = false)]
        public string ActivePlanId { get; set; }

        [System.Runtime.Serialization.DataMember(Order = 3)]
        public System.Collections.Generic.List<ApplicationPlan> Plans { get; set; }
    }

    public enum DesktopEntrySource
    {
        CurrentUser,
        Public
    }

    public sealed class DesktopEntry
    {
        /// <summary>
        /// Transient UI selection.  Scanning never treats an entry as a
        /// startup item; it is added to a profile only after the user clicks
        /// the explicit add command.
        /// </summary>
        public bool IsSelected { get; set; }

        public string DisplayName { get; set; }

        public string Path { get; set; }

        public string Extension { get; set; }

        public DesktopEntrySource Source { get; set; }

        public bool IsShortcut { get; set; }

        /// <summary>自启动应用恢复时使用的窗口状态；扫描/新增时通常为空。</summary>
        public SavedWindowPlacement WindowPlacement { get; set; }

        /// <summary>用于找窗口的标题提示（自启动应用方案项的名字）。</summary>
        public string WindowTitle { get; set; }
    }

    public sealed class StartupRegistrationInfo
    {
        public bool IsEnabled { get; set; }

        public bool WasSuppressed { get; set; }

        public string Command { get; set; }

        public string Error { get; set; }
    }
}
