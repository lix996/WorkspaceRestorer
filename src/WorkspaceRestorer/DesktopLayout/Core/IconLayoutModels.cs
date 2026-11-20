using System;
using System.Collections.Generic;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Desktop
{
    [Serializable]
    public sealed class DesktopRect
    {
        public DesktopRect()
        {
        }

        public DesktopRect(int left, int top, int width, int height)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public int Left { get; set; }

        public int Top { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int Right
        {
            get { return Left + Width; }
        }

        public int Bottom
        {
            get { return Top + Height; }
        }

        public bool Contains(int x, int y)
        {
            return x >= Left && x < Right && y >= Top && y < Bottom;
        }
    }

    [Serializable]
    public sealed class DisplayMonitor
    {
        public DisplayMonitor()
        {
            Bounds = new DesktopRect();
            WorkingArea = new DesktopRect();
            StableKey = string.Empty;
            DeviceName = string.Empty;
            DeviceId = string.Empty;
            FriendlyName = string.Empty;
            DpiX = 96;
            DpiY = 96;
        }

        public string StableKey { get; set; }

        public string DeviceName { get; set; }

        public string DeviceId { get; set; }

        public string FriendlyName { get; set; }

        public bool IsPrimary { get; set; }

        public int DpiX { get; set; }

        public int DpiY { get; set; }

        public DesktopRect Bounds { get; set; }

        public DesktopRect WorkingArea { get; set; }
    }

    [Serializable]
    public sealed class DisplayTopology
    {
        public DisplayTopology()
        {
            Signature = string.Empty;
            Monitors = new List<DisplayMonitor>();
            VirtualBounds = new DesktopRect();
        }

        public string Signature { get; set; }

        public List<DisplayMonitor> Monitors { get; set; }

        public DesktopRect VirtualBounds { get; set; }

        public bool IsSingleDisplay
        {
            get { return Monitors != null && Monitors.Count == 1; }
        }

        public DisplayMonitor GetPrimaryMonitor()
        {
            if (Monitors == null || Monitors.Count == 0)
            {
                return null;
            }

            for (int index = 0; index < Monitors.Count; index++)
            {
                if (Monitors[index].IsPrimary)
                {
                    return Monitors[index];
                }
            }

            return Monitors[0];
        }

        public DisplayMonitor FindMonitor(string stableKey)
        {
            if (Monitors == null || string.IsNullOrEmpty(stableKey))
            {
                return null;
            }

            for (int index = 0; index < Monitors.Count; index++)
            {
                if (string.Equals(Monitors[index].StableKey, stableKey, StringComparison.OrdinalIgnoreCase))
                {
                    return Monitors[index];
                }
            }

            return null;
        }
    }

    [Serializable]
    public sealed class DesktopIconPlacement
    {
        public DesktopIconPlacement()
        {
            Identity = string.Empty;
            DisplayName = string.Empty;
            MonitorKey = string.Empty;
        }

        public string Identity { get; set; }

        public string DisplayName { get; set; }

        /// <summary>
        /// 桌面项目本身的创建时间（UTC）。用于识别“同名文件被删掉后又新建”：
        /// Windows 的 name:/path: 身份会把它当成同一个图标，但用户看到的是新文件，
        /// 编辑布局时必须重新放进“新图标”，不能沿用旧布局里的位置。
        /// 这是运行时信息，不写进布局 XML：布局 XML 的 DataContract 一旦增加成员，
        /// 旧档案就会反序列化失败（仓库里已有明确教训）。只在本次扫描/编辑内使用。
        /// </summary>
        [NonSerialized]
        private DateTime createdUtc;

        public DateTime CreatedUtc
        {
            get { return createdUtc; }
            set { createdUtc = value; }
        }

        public string MonitorKey { get; set; }

        // Coordinates returned by IFolderView, relative to the virtual desktop view.
        public int X { get; set; }

        public int Y { get; set; }

        // Coordinates relative to the owning monitor. They preserve the exact layout
        // while NormalizedX/Y allow restoration after a resolution change.
        public int MonitorOffsetX { get; set; }

        public int MonitorOffsetY { get; set; }

        public double NormalizedX { get; set; }

        public double NormalizedY { get; set; }
    }

    [Serializable]
    public sealed class DesktopShellSnapshot
    {
        public DesktopShellSnapshot()
        {
            Topology = new DisplayTopology();
            Icons = new List<DesktopIconPlacement>();
            GridWidth = 75;
            GridHeight = 75;
            CapturedUtc = DateTime.UtcNow;
        }

        public DisplayTopology Topology { get; set; }

        public List<DesktopIconPlacement> Icons { get; set; }

        public int GridWidth { get; set; }

        public int GridHeight { get; set; }

        public bool AutoArrangeEnabled { get; set; }

        public DateTime CapturedUtc { get; set; }
    }

    [Serializable]
    public sealed class DesktopTopologyLayout : IPlanFolderNode
    {
        public DesktopTopologyLayout()
        {
            TopologySignature = string.Empty;
            Topology = new DisplayTopology();
            Icons = new List<DesktopIconPlacement>();
            AutoMergedIdentities = new List<string>();
            GridWidth = 75;
            GridHeight = 75;
            UpdatedUtc = DateTime.UtcNow;
            CreatedUtc = DateTime.UtcNow;
            Id = string.Empty;
            Name = string.Empty;
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public int SortOrder { get; set; }

        /// <summary>
        /// 父布局 Id；空 = 根级。图标布局只复用方案文件夹的分组，不接小项删除。
        ///
        /// 这里故意用“显式字段 + 属性”而不是自动属性：
        /// 布局档案是旧的 `[Serializable]` XML，DataContractSerializer 读旧文件时
        /// 要求每个字段都在；只有字段能标 `[OptionalField]`，自动属性不能标。
        /// 不这样写，旧 `布局档案.xml` 会因为缺 ParentId/IsFolder 而整份读不出来。
        /// </summary>
        [System.Runtime.Serialization.OptionalField]
        private string parentId;

        /// <summary>true = 纯文件夹，只装子布局，不能直接恢复/保存桌面。</summary>
        [System.Runtime.Serialization.OptionalField]
        private bool isFolder;

        public string ParentId
        {
            get { return parentId; }
            set { parentId = value; }
        }

        public bool IsFolder
        {
            get { return isFolder; }
            set { isFolder = value; }
        }

        public DateTime CreatedUtc { get; set; }

        public string TopologySignature { get; set; }

        public DisplayTopology Topology { get; set; }

        public List<DesktopIconPlacement> Icons { get; set; }

        /// <summary>
        /// 这些图标是“切换显示器时为了不让新图标消失”自动并进本布局的，
        /// 不是用户在这份布局里明确保存过的位置。
        ///
        /// 它们仍留在 Icons 里，恢复时能一起带过去；但编辑页必须把它们当成
        /// “新图标”，否则用户在小屏上新建文件、又在别的布局保存过一次以后，
        /// 再编辑这份布局就会看到它被直接铺在预桌面上（2026-10-23 用户报的严重问题）。
        /// 用户对这份布局点“完成并保存/保存当前桌面到此布局”时会清空这个清单。
        /// </summary>
        [NonSerialized]
        private List<string> autoMergedIdentities;

        public List<string> AutoMergedIdentities
        {
            get { return autoMergedIdentities; }
            set { autoMergedIdentities = value; }
        }

        public int GridWidth { get; set; }

        public int GridHeight { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }

    [Serializable]
    public sealed class DesktopLayoutChoice
    {
        public string TopologySignature { get; set; }

        public string LayoutId { get; set; }
    }

    [Serializable]
    public sealed class DesktopLayoutCatalog
    {
        public DesktopLayoutCatalog()
        {
            FormatVersion = 1;
            Layouts = new List<DesktopTopologyLayout>();
            ActiveLayoutChoices = new List<DesktopLayoutChoice>();
        }

        public int FormatVersion { get; set; }

        public List<DesktopTopologyLayout> Layouts { get; set; }

        /// <summary>当前选中的布局（全局；切换显示器时会按拓扑选择记录里的 id）。</summary>
        public string ActiveLayoutId { get; set; }

        /// <summary>每个显示器拓扑最后选中的布局 id（用户切换回某组合时恢复这一份）。</summary>
        public List<DesktopLayoutChoice> ActiveLayoutChoices { get; set; }

        // This is always a one-monitor layout. It is updated from a real laptop-only
        // capture when one is available, otherwise it is synthesized from the primary.
        public DesktopTopologyLayout PortableSingleDisplayLayout { get; set; }
    }

    public sealed class DesktopRestoreItem
    {
        public DesktopRestoreItem()
        {
            Identity = string.Empty;
            DisplayName = string.Empty;
        }

        public string Identity { get; set; }

        public string DisplayName { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public string TargetMonitorKey { get; set; }
    }

    public sealed class DesktopRestorePlan
    {
        public DesktopRestorePlan()
        {
            SourceTopologySignature = string.Empty;
            CurrentTopologySignature = string.Empty;
            Items = new List<DesktopRestoreItem>();
        }

        public string SourceTopologySignature { get; set; }

        public string CurrentTopologySignature { get; set; }

        public List<DesktopRestoreItem> Items { get; set; }

        public bool UsedPortableFallback { get; set; }
    }

    public sealed class DesktopRestoreResult
    {
        public DesktopRestoreResult()
        {
            MissingIdentities = new List<string>();
            ErrorMessage = string.Empty;
        }

        public bool Applied { get; set; }

        public bool SkippedByPolicy { get; set; }

        public bool AutoArrangeEnabled { get; set; }

        public int RequestedCount { get; set; }

        public int AppliedCount { get; set; }

        public List<string> MissingIdentities { get; set; }

        public string ErrorMessage { get; set; }
    }
}
