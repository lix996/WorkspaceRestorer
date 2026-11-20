using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Models
{
    [DataContract]
    public sealed class BrowserUnreadWindowInfo
    {
        [DataMember(Order = 1, EmitDefaultValue = false)]
        public string DisplayName { get; set; }

        [DataMember(Order = 2)]
        public int UnreadPageCount { get; set; }

        [DataMember(Order = 3)]
        public bool Minimized { get; set; }
    }

    [DataContract]
    public sealed class SessionSnapshot
    {
        // Version 4 records whether an explicit browser scan was incomplete.
        // The optional fields deserialize to their safe defaults for older
        // snapshots, so existing saved workspaces remain valid.
        public const int CurrentSchemaVersion = 4;

        public SessionSnapshot()
        {
            SchemaVersion = CurrentSchemaVersion;
            CapturedAtUtc = DateTime.UtcNow;
            Items = new List<SessionItem>();
        }

        [DataMember(Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Order = 2)]
        public DateTime CapturedAtUtc { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string ComputerName { get; set; }

        [DataMember(Order = 4)]
        public List<SessionItem> Items { get; set; }

        /// <summary>
        /// True when the application/folder baseline was captured but the
        /// isolated browser worker did not return every expected tab.  The
        /// controller uses this marker to retain previously saved pages.
        /// </summary>
        [DataMember(Order = 5, EmitDefaultValue = false)]
        public bool BrowserCaptureIncomplete { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public string CaptureWarning { get; set; }

        /// <summary>
        /// Per-browser-window read summary when an explicit capture could not
        /// read every tab, for example "Microsoft Edge (1)：读到 12/15 个标签".
        /// The UI shows these to tell the user which window lost pages.
        /// </summary>
        [DataMember(Order = 7, EmitDefaultValue = false)]
        public List<string> UnreadBrowserSummaries { get; set; }

        /// <summary>
        /// 结构化未读窗口清单（窗口名、缺几页、是否最小化）。
        /// 完成横幅要显示“Microsoft Edge (1) 还有 N 个标签页没读到”，不能只有计数。
        /// 旧快照没有这个字段，读出来是 null，文案会退回通用句。
        /// </summary>
        [DataMember(Order = 8, EmitDefaultValue = false)]
        public List<BrowserUnreadWindowInfo> UnreadBrowserWindows { get; set; }

        /// <summary>这次保存忽略了多少个空白新标签页（新建标签页/edge://newtab）。</summary>
        [DataMember(Order = 9, EmitDefaultValue = false)]
        public int IgnoredBlankTabCount { get; set; }

        /// <summary>网页读取整体超时、退回“应用+文件夹”基线时为 true。</summary>
        [DataMember(Order = 10, EmitDefaultValue = false)]
        public bool BrowserCaptureTimedOut { get; set; }

        // ---------------------------------------------------------------
        // 【2026-10-19 用户要求：提示要如实说明情况】下面是“保存时按设置怎么处理终端/本地网址”的结果，
        // **只在内存里传**（不加 DataMember，不落盘）：保存完成的横幅、托盘气泡、状态栏都读它，
        // 这样“跳过了几项、为什么跳过”能说清楚，不会再被当成“有些网页没读到 → 把浏览器窗口显示出来”。
        // ---------------------------------------------------------------

        /// <summary>这次保存涉及“终端 / 本地网址”的项数（保留或跳过都算）。</summary>
        public int PolicyAffectedCount { get; set; }

        /// <summary>这次保存真正被“扫描终端与本地网址”设置跳过（从方案里去掉）的项数。</summary>
        public int PolicySkippedCount { get; set; }

        /// <summary>true＝设置里勾了“扫描终端与本地网址”，这些项被保留（恢复不了，只做记录）。</summary>
        public bool PolicyIncluded { get; set; }

        /// <summary>给用户看的取舍说明（例如“已跳过 5 项终端/本地网址…”）。</summary>
        public string PolicyHint { get; set; }
    }

    /// <summary>
    /// A named startup workspace.  A profile owns a copy of the ordered
    /// session items so several working modes can coexist independently.
    /// </summary>
    [DataContract]
    public sealed class StartupProfile : IPlanFolderNode
    {
        public StartupProfile()
        {
            Id = Guid.NewGuid().ToString("N");
            Name = "工作方案";
            CreatedAtUtc = DateTime.UtcNow;
            UpdatedAtUtc = CreatedAtUtc;
            Items = new List<SessionItem>();
            ExcludedItemKeys = new List<string>();
        }

        [DataMember(Order = 1)]
        public string Id { get; set; }

        [DataMember(Order = 2)]
        public string Name { get; set; }

        [DataMember(Order = 3)]
        public DateTime CreatedAtUtc { get; set; }

        [DataMember(Order = 4)]
        public DateTime UpdatedAtUtc { get; set; }

        [DataMember(Order = 5)]
        public List<SessionItem> Items { get; set; }

        /// <summary>
        /// Stable identities of captured entries that the user explicitly
        /// removed.  Periodic capture creates new SessionItem ids, so these
        /// persisted keys prevent an open window or page from immediately
        /// appearing again.  Older profile files omit this optional member
        /// and are normalized to an empty list when loaded.
        /// </summary>
        [DataMember(Order = 6, EmitDefaultValue = false)]
        public List<string> ExcludedItemKeys { get; set; }

        /// <summary>
        /// 父方案 Id；空 = 根目录。工作方案可以无限层嵌套，和文件夹一样。
        /// 旧方案文件没有这个成员，读出来就是根级。
        /// </summary>
        [DataMember(Order = 7, EmitDefaultValue = false)]
        public string ParentId { get; set; }

        /// <summary>
        /// true = 纯文件夹：只装子方案，没有自己的启动项。
        /// 普通方案也可以有子方案（就是“大方案”），所以这个标记只用来区分“纯文件夹”。
        /// </summary>
        [DataMember(Order = 8, EmitDefaultValue = false)]
        public bool IsFolder { get; set; }

        /// <summary>
        /// Builds the session shape consumed by the existing restore service.
        /// Items are intentionally shared; the profile itself remains the
        /// persisted owner and deletion methods resequence the list.
        /// </summary>
        public SessionSnapshot ToSnapshot()
        {
            SessionSnapshot snapshot = new SessionSnapshot();
            snapshot.CapturedAtUtc = UpdatedAtUtc == default(DateTime) ? DateTime.UtcNow : UpdatedAtUtc;
            snapshot.Items = Items ?? new List<SessionItem>();
            return snapshot;
        }

        public void ReplaceItems(IEnumerable<SessionItem> items)
        {
            Items = items == null ? new List<SessionItem>() : new List<SessionItem>(items);
            Resequence();
            UpdatedAtUtc = DateTime.UtcNow;
        }

        public void Resequence()
        {
            if (Items == null)
            {
                Items = new List<SessionItem>();
                return;
            }

            for (int index = Items.Count - 1; index >= 0; index--)
            {
                if (Items[index] == null)
                {
                    Items.RemoveAt(index);
                }
            }
            for (int index = 0; index < Items.Count; index++)
            {
                Items[index].Sequence = index + 1;
                if (Items[index].StartTimeUtc == default(DateTime))
                {
                    Items[index].StartTimeUtc = UpdatedAtUtc == default(DateTime)
                        ? DateTime.UtcNow
                        : UpdatedAtUtc;
                }
            }
        }
    }

    /// <summary>
    /// Persisted catalog of named startup workspaces.  ActiveProfileId is
    /// deliberately separate from the profile records so deleting/renaming a
    /// profile cannot accidentally alter another profile's contents.
    /// </summary>
    [DataContract]
    public sealed class StartupProfileCatalog
    {
        public const int CurrentSchemaVersion = 2;

        public StartupProfileCatalog()
        {
            SchemaVersion = CurrentSchemaVersion;
            Profiles = new List<StartupProfile>();
        }

        [DataMember(Order = 1)]
        public int SchemaVersion { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public string ActiveProfileId { get; set; }

        [DataMember(Order = 3)]
        public List<StartupProfile> Profiles { get; set; }
    }

    [DataContract]
    public sealed class SessionItem
    {
        public SessionItem()
        {
            Id = Guid.NewGuid().ToString("N");
            IsEnabled = true;
            StartTimeUtc = DateTime.UtcNow;
            BrowserWindowIndex = -1;
            BrowserTabIndex = -1;
            ApplicationWindowIndex = -1;
        }

        [DataMember(Order = 1)]
        public string Id { get; set; }

        [DataMember(Order = 2)]
        public SessionItemKind Kind { get; set; }

        [DataMember(Order = 3)]
        public int Sequence { get; set; }

        [DataMember(Order = 4)]
        public bool IsEnabled { get; set; }

        [DataMember(Order = 5)]
        public DateTime StartTimeUtc { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public string DisplayName { get; set; }

        [DataMember(Order = 7, EmitDefaultValue = false)]
        public string WindowTitle { get; set; }

        [DataMember(Order = 8, EmitDefaultValue = false)]
        public string ExecutablePath { get; set; }

        [DataMember(Order = 9, EmitDefaultValue = false)]
        public string Arguments { get; set; }

        [DataMember(Order = 10, EmitDefaultValue = false)]
        public string WorkingDirectory { get; set; }

        [DataMember(Order = 11, EmitDefaultValue = false)]
        public string TargetPath { get; set; }

        [DataMember(Order = 12)]
        public int CapturedProcessId { get; set; }

        [DataMember(Order = 13, EmitDefaultValue = false)]
        public string CaptureSource { get; set; }

        /// <summary>
        /// URL captured from a browser address bar.  It is populated only for
        /// <see cref="SessionItemKind.WebPage"/> entries.
        /// </summary>
        [DataMember(Order = 14, EmitDefaultValue = false)]
        public string Url { get; set; }

        /// <summary>
        /// Friendly browser process name (for example, Microsoft Edge).
        /// </summary>
        [DataMember(Order = 15, EmitDefaultValue = false)]
        public string BrowserName { get; set; }

        /// <summary>
        /// Browser executable used when restoring a captured page.  Keeping
        /// this separate from ExecutablePath lets UI code treat a page as a
        /// child of its browser application without losing restart data.
        /// </summary>
        [DataMember(Order = 16, EmitDefaultValue = false)]
        public string BrowserExecutablePath { get; set; }

        /// <summary>
        /// Id of the browser application item that owns this page.  It is a
        /// presentation/grouping hint only; restore ordering still follows
        /// Sequence and old snapshots without it continue to work.
        /// </summary>
        [DataMember(Order = 17, EmitDefaultValue = false)]
        public string ParentId { get; set; }

        /// <summary>
        /// Stable grouping key for browser entries.  The executable path is
        /// used by the capture service so pages from the same browser can be
        /// displayed together even when their process id changes.
        /// </summary>
        [DataMember(Order = 18, EmitDefaultValue = false)]
        public string BrowserGroupKey { get; set; }

        /// <summary>
        /// Zero-based visible browser-window index within a browser process.
        /// Older snapshots leave this at -1 and continue to restore normally.
        ///
        /// 【2026-10-15 用户反馈“两个窗口恢复成一个”的真正根因】
        /// 这里原来是 EmitDefaultValue = false。int 的默认值是 0，于是
        /// **第一个窗口的 BrowserWindowIndex = 0 会被静默丢掉**，
        /// 读回来变成默认值 0 …… 单看值好像没变，但“没写进文件”这件事
        /// 在恢复侧是靠 < 0 判断“这个字段缺失”的（见 SessionCaptureService /
        /// SessionRestoreService / ProcessLauncher 里一堆 item.BrowserWindowIndex >= 0），
        /// 于是第一个窗口的标签被拆成两组、恢复成多个窗口。
        /// 现在**永远写出真实值**，不再省略。
        /// </summary>
        [DataMember(Order = 19, EmitDefaultValue = true)]
        public int BrowserWindowIndex { get; set; }

        /// <summary>
        /// Zero-based tab index reported by the browser UI Automation tree.
        /// This keeps multi-tab capture order independent of localized titles.
        /// 同上：0 也是有效值（第一个标签），不能因为“等于默认值”就省略掉。
        /// </summary>
        [DataMember(Order = 20, EmitDefaultValue = true)]
        public int BrowserTabIndex { get; set; }

        /// <summary>
        /// Whether this page was the selected tab at capture time.  It is a
        /// hint for future restore strategies; current restore still opens
        /// pages in their recorded order.
        /// </summary>
        [DataMember(Order = 21, EmitDefaultValue = false)]
        public bool IsActiveBrowserTab { get; set; }

        /// <summary>
        /// Explicitly marks that BrowserWindowIndex/BrowserTabIndex were
        /// captured from a selectable browser tab.  This distinguishes a real
        /// zero-based tab identity from legacy records where missing integer
        /// members deserialize as zero.
        /// </summary>
        [DataMember(Order = 24, EmitDefaultValue = false)]
        public bool BrowserTabIdentityCaptured { get; set; }

        /// <summary>
        /// 保存时这个浏览器窗口里**所有能读到的标签**（序号 + 标题 + 网址 + 是否选中）。
        /// 跳转时先用它和当前标签栏做指纹比对，不再只靠“窗口标题像不像”。
        /// 旧文件没有这个字段时为 null，按原来的标题/序号逻辑兜底。
        /// </summary>
        [DataMember(Order = 25, EmitDefaultValue = false)]
        public List<BrowserTabFingerprint> BrowserTabs { get; set; }

        /// <summary>
        /// 这条网页归属的窗口标题快照（浏览器父窗口标题）。
        /// 和 BrowserTabs 一起用于“同名窗口”消歧。
        /// </summary>
        [DataMember(Order = 26, EmitDefaultValue = false)]
        public string BrowserWindowTitleSnapshot { get; set; }

        /// <summary>
        /// 归属来源：自动捕获 / 用户手动绑定 / 旧数据未知。
        /// </summary>
        [DataMember(Order = 27, EmitDefaultValue = false)]
        public BrowserPageBindingKind BrowserBindingKind { get; set; }

        /// <summary>用户最后一次手动改归属的时间（UTC）；未改过为 null。</summary>
        [DataMember(Order = 28, EmitDefaultValue = false)]
        public DateTime? BrowserBindingUpdatedAtUtc { get; set; }

        /// <summary>
        /// Zero-based top-level window index for applications that own more
        /// than one visible window, currently Visual Studio Code.  It keeps
        /// duplicate windows distinct when they share an executable path.
        /// 同理：0 是有效值，不能省略。
        /// </summary>
        [DataMember(Order = 22, EmitDefaultValue = true)]
        public int ApplicationWindowIndex { get; set; }

        /// <summary>
        /// Optional placement captured for a top-level application window.
        /// Visual Studio Code uses this to restore separate windows and their
        /// positions without serializing editor contents.
        /// </summary>
        [DataMember(Order = 23, EmitDefaultValue = false)]
        public SavedWindowPlacement WindowPlacement { get; set; }

        /// <summary>
        /// Runtime-only restore hint assigned to the first enabled page in a
        /// saved browser window. It is intentionally not serialized.
        /// </summary>
        public bool StartsBrowserWindow { get; set; }

        /// <summary>
        /// Runtime-only jump hint (2026-10-31). 自启动应用的 `.lnk` / `.bat`
        /// 条目需要「按目标 exe 找已打开窗口」，但启动时仍要用原来的
        /// `TargetPath`（快捷方式/脚本）——所以跳转匹配单独走这个字段，
        /// 不改 `ExecutablePath`，也不落盘。没有 DataMember，旧文件不受影响。
        /// </summary>
        public string JumpExecutablePath { get; set; }
    }

    /// <summary>浏览器窗口里一个标签的保存时指纹。</summary>
    [DataContract]
    public sealed class BrowserTabFingerprint
    {
        [DataMember(Order = 1)]
        public int Index { get; set; }

        [DataMember(Order = 2, EmitDefaultValue = false)]
        public string Title { get; set; }

        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string Url { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public bool IsActive { get; set; }

        public BrowserTabFingerprint Clone()
        {
            return new BrowserTabFingerprint
            {
                Index = Index,
                Title = Title,
                Url = Url,
                IsActive = IsActive
            };
        }

        public static List<BrowserTabFingerprint> CloneList(List<BrowserTabFingerprint> source)
        {
            List<BrowserTabFingerprint> copy = new List<BrowserTabFingerprint>();
            if (source == null)
            {
                return copy;
            }
            for (int index = 0; index < source.Count; index++)
            {
                if (source[index] != null)
                {
                    copy.Add(source[index].Clone());
                }
            }
            return copy;
        }
    }

    [DataContract]
    public enum BrowserPageBindingKind
    {
        [EnumMember]
        Unknown = 0,

        [EnumMember]
        Captured = 1,

        [EnumMember]
        UserBound = 2
    }

    [DataContract]
    public enum SessionItemKind
    {
        [EnumMember]
        Application = 0,

        [EnumMember]
        ExplorerFolder = 1,

        [EnumMember]
        Document = 2,

        /// <summary>
        /// A URL read from a browser's address bar.  The corresponding
        /// browser executable is stored in BrowserExecutablePath.
        /// </summary>
        [EnumMember]
        WebPage = 3
    }

    public sealed class SessionCaptureOptions
    {
        public SessionCaptureOptions()
        {
            IncludeExplorerFolders = true;
            IncludeBrowserPages = true;
            // A plain capture must never change a user's visible browser
            // page.  The controller opts in only for an explicit save action
            // that the user initiated.
            AllowBrowserTabActivation = false;
            ExcludedExecutablePaths = new List<string>();
            ExcludedProcessNames = new List<string>();
        }

        public bool IncludeExplorerFolders { get; set; }

        /// <summary>
        /// Enables best-effort capture of the active URL from supported
        /// browsers.  Set false when a caller wants applications only.
        /// </summary>
        public bool IncludeBrowserPages { get; set; }

        /// <summary>
        /// Latest moment (UTC) at which the explicit interactive pass may
        /// still start reading a browser window's pages.  The isolated worker
        /// uses this to finish and minimize any restored window well before
        /// the parent's hard timeout can kill it.  Null disables the bound.
        /// </summary>
        public DateTime? PageReadDeadlineUtc { get; set; }

        /// <summary>
        /// Allows an explicit user-initiated capture to select browser tabs in
        /// order to read their exact address-bar URLs. Background and shutdown
        /// captures set this to false so an idle checkpoint never changes any
        /// application window or visible page.
        /// </summary>
        public bool AllowBrowserTabActivation { get; set; }

        public IList<string> ExcludedExecutablePaths { get; private set; }

        public IList<string> ExcludedProcessNames { get; private set; }
    }
}
