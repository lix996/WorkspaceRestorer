using System;
using System.Collections.Generic;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// Coordinates topology-specific snapshots.  Positions are never copied
    /// between topologies; only the identity set is unified.  This is what
    /// keeps a laptop-only layout authoritative after an external display is
    /// unplugged.
    /// </summary>
    public sealed class DesktopLayoutCoordinator
    {
        private readonly DesktopIconService iconService;
        private readonly DesktopLayoutRepository repository;
        private readonly DisplayTopologyService topologyService;

        public DesktopLayoutCoordinator()
            : this(new DesktopIconService(), new DesktopLayoutRepository(), new DisplayTopologyService())
        {
        }

        public DesktopLayoutCoordinator(DesktopIconService iconService, DesktopLayoutRepository repository)
            : this(iconService, repository, new DisplayTopologyService())
        {
        }

        public DesktopLayoutCoordinator(DesktopIconService iconService, DesktopLayoutRepository repository, DisplayTopologyService topologyService)
        {
            this.iconService = iconService ?? new DesktopIconService();
            if (repository == null)
            {
                throw new ArgumentNullException("repository");
            }
            this.repository = repository;
            this.topologyService = topologyService ?? new DisplayTopologyService();
        }

        public DesktopShellSnapshot CaptureCurrent()
        {
            DesktopShellSnapshot snapshot = iconService.Capture();
            SaveSnapshot(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Captures the current desktop without changing the persisted
        /// catalog.  Callers that need a bounded/background capture can save
        /// the returned snapshot only after their timeout and validity checks.
        /// </summary>
        public DesktopShellSnapshot CaptureOnly()
        {
            return iconService.Capture();
        }

        /// <summary>读取布局清单（界面列“文件夹”用）；不触发任何捕获/保存。</summary>
        public DesktopLayoutCatalog LoadCatalog()
        {
            return repository.Load();
        }

        // Compatibility names used by the UI controller.  Keeping these as
        // thin aliases makes the coordinator usable from reflection-based
        // service composition as well as from strongly typed callers.
        public DesktopShellSnapshot CaptureAndMerge()
        {
            return CaptureCurrent();
        }

        public DesktopShellSnapshot CaptureAndSave(DisplayTopology topology)
        {
            DesktopShellSnapshot snapshot = iconService.Capture(topology);
            SaveSnapshot(snapshot);
            return snapshot;
        }

        public void SaveSnapshot(DesktopShellSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            DisplayTopology topology = snapshot.Topology ?? new DisplayTopology();
            string signature = topology.Signature ?? string.Empty;
            DesktopTopologyLayout layout = new DesktopTopologyLayout();
            // 自动捕获只更新“这一套显示器组合的自动布局”，不能按拓扑签名
            // 把用户命名的多份布局顶掉（用户 2026-10-21 的布局消失就是这个问题）。
            layout.Id = string.IsNullOrWhiteSpace(signature)
                ? "layout-auto-" + Guid.NewGuid().ToString("N")
                : "layout-" + signature;
            layout.Name = DesktopLayoutFolderStore.Describe(new DesktopTopologyLayout
            {
                Topology = topology
            });
            layout.TopologySignature = signature;
            layout.Topology = topology;
            layout.Icons = CloneIcons(snapshot.Icons);
            layout.GridWidth = snapshot.GridWidth <= 0 ? 75 : snapshot.GridWidth;
            layout.GridHeight = snapshot.GridHeight <= 0 ? 75 : snapshot.GridHeight;
            layout.UpdatedUtc = DateTime.UtcNow;

            DesktopLayoutCatalog catalog = repository.Load();
            DesktopLayoutRepository.Normalize(catalog);
            MergeIdentitySet(catalog, layout, topology);
            UpsertLayout(catalog, layout);

            // A genuine one-monitor capture is always the portable source of
            // truth.  Never overwrite it from a multi-monitor capture.
            if (topology.IsSingleDisplay)
            {
                catalog.PortableSingleDisplayLayout = CloneLayout(layout);
            }
            else
            {
                // Keep icons first seen while docked in the laptop-only union.
                // Their position is synthesized on the laptop primary monitor;
                // positions from the external monitor are never copied across.
                MergeIntoPortableLayout(catalog, layout);
                // PortableSingleDisplayLayout historically lived outside the
                // topology list.  Keep the corresponding single-monitor
                // entry in Layouts in lockstep so exact-signature restores do
                // not observe a stale icon set after a docked capture.
                SyncPortableLayoutEntry(catalog);
            }
            // 用户 2026-10-20：在小屏上新加图标，切回大屏也要有。
            // 把这次捕获里新增的身份并进**所有**布局（放在各自主屏的空位），
            // 切换拓扑时大屏布局也认得这个新图标。
            MergeIdentitiesIntoAllLayouts(catalog, layout);
            repository.Save(catalog);
        }

        /// <summary>保存/覆盖一份**命名布局**（用户 2026-10-20 要求和图2工作方案的卡片操作一致）。</summary>
        public DesktopTopologyLayout SaveSnapshotAs(DesktopShellSnapshot snapshot, string layoutId, string name)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            DisplayTopology topology = snapshot.Topology ?? new DisplayTopology();
            string signature = topology.Signature ?? string.Empty;

            DesktopLayoutCatalog catalog = repository.Load();
            DesktopLayoutRepository.Normalize(catalog);
            DesktopTopologyLayout layout = DesktopLayoutRepository.FindById(catalog, layoutId);
            bool isNew = layout == null;
            if (isNew)
            {
                layout = new DesktopTopologyLayout();
                layout.Id = string.IsNullOrWhiteSpace(layoutId) ? Guid.NewGuid().ToString("N") : layoutId;
                layout.CreatedUtc = DateTime.UtcNow;
                int maxOrder = 0;
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    if (catalog.Layouts[index].SortOrder > maxOrder)
                    {
                        maxOrder = catalog.Layouts[index].SortOrder;
                    }
                }
                layout.SortOrder = maxOrder + 1;
                catalog.Layouts.Add(layout);
            }
            layout.TopologySignature = signature;
            layout.Topology = topology;
            layout.Icons = CloneIcons(snapshot.Icons);
            // 用户明确保存这一份布局：所有图标（包括编辑页里刚摆好的新图标）
            // 现在都是“这份布局自己的”，不再有自动并集标记。
            layout.AutoMergedIdentities = new List<string>();
            layout.GridWidth = snapshot.GridWidth <= 0 ? 75 : snapshot.GridWidth;
            layout.GridHeight = snapshot.GridHeight <= 0 ? 75 : snapshot.GridHeight;
            layout.UpdatedUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(name))
            {
                layout.Name = name.Trim();
            }
            if (string.IsNullOrWhiteSpace(layout.Name))
            {
                layout.Name = DesktopLayoutFolderStore.Describe(layout);
            }
            catalog.ActiveLayoutId = layout.Id;
            DesktopLayoutRepository.SetChoice(catalog, layout.TopologySignature, layout.Id);
            if (topology.IsSingleDisplay && IsAutomaticLayoutId(layout.Id))
            {
                // 只有自动布局才同步到 PortableSingleDisplayLayout。
                // 用户命名的多份布局不能轮流改写这个“自动回退”槽位。
                catalog.PortableSingleDisplayLayout = CloneLayout(layout);
            }
            // 新图标并进所有布局：小屏新增的图标，切回大屏也要在。
            MergeIdentitiesIntoAllLayouts(catalog, layout);
            repository.Save(catalog);
            return layout;
        }

        public void SaveCatalog(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }
            repository.Save(catalog);
        }

        /// <summary>
        /// 只保存卡片的展示顺序/父文件夹（sidecar）；
        /// 拖动换顺序不再重写整份布局档案和每份布局 XML。
        /// </summary>
        public void SaveDisplayOrder(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }
            repository.SaveDisplayOrder(catalog);
        }

        public DesktopRestorePlan BuildRestorePlan()
        {
            DisplayTopology current = topologyService.Capture();
            return BuildRestorePlan(current);
        }

        public DesktopRestorePlan BuildRestorePlan(DisplayTopology currentTopology)
        {
            if (currentTopology == null)
            {
                currentTopology = new DisplayTopology();
            }
            DesktopLayoutCatalog catalog = repository.Load();
            DesktopLayoutRepository.Normalize(catalog);

            DesktopTopologyLayout source = null;
            bool usedPortable = false;
            // 【2026-10-20】先看“这个显示器组合上次选中的是哪份命名布局”；
            // 没有记录再按精确签名找，最后才用 portable 兜底。
            string chosenId = DesktopLayoutRepository.FindChoice(catalog, currentTopology.Signature);
            if (string.IsNullOrWhiteSpace(chosenId))
            {
                chosenId = catalog.ActiveLayoutId;
            }
            DesktopTopologyLayout chosen = DesktopLayoutRepository.FindById(catalog, chosenId);
            if (chosen != null
                && string.Equals(chosen.TopologySignature ?? string.Empty, currentTopology.Signature ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                source = chosen;
            }
            if (source == null)
            {
                source = FindNewestLayout(catalog, currentTopology.Signature);
            }
            if (source == null && currentTopology.IsSingleDisplay && catalog.PortableSingleDisplayLayout != null)
            {
                source = catalog.PortableSingleDisplayLayout;
                usedPortable = true;
            }
            if (source == null && !currentTopology.IsSingleDisplay)
            {
                // A monitor can keep the same physical identity while its
                // resolution or scaling changes. Reuse that topology's
                // positions and let normalized offsets adapt to the new size.
                source = FindCompatibleLayout(catalog, currentTopology);
            }
            if (source == null)
            {
                return new DesktopRestorePlan
                {
                    SourceTopologySignature = string.Empty,
                    CurrentTopologySignature = currentTopology.Signature ?? string.Empty,
                    UsedPortableFallback = false,
                    Items = new List<DesktopRestoreItem>()
                };
            }

            DesktopRestorePlan plan = new DesktopRestorePlan();
            plan.SourceTopologySignature = source.TopologySignature ?? string.Empty;
            plan.CurrentTopologySignature = currentTopology.Signature ?? string.Empty;
            plan.UsedPortableFallback = usedPortable;
            plan.Items = new List<DesktopRestoreItem>();

            DisplayMonitor currentPrimary = currentTopology.GetPrimaryMonitor();
            for (int index = 0; index < source.Icons.Count; index++)
            {
                DesktopIconPlacement placement = source.Icons[index];
                if (placement == null || string.IsNullOrEmpty(placement.Identity))
                {
                    continue;
                }
                // 哨兵 = 用户把它留在“新图标”里，不是桌面坐标。
                // 恢复布局时保持它现在的位置，不把 -1000000 铺到屏幕左上角。
                if (placement.X < -999000 || placement.Y < -999000)
                {
                    continue;
                }

                DisplayMonitor targetMonitor = currentTopology.FindMonitor(placement.MonitorKey);
                if (targetMonitor == null)
                {
                    targetMonitor = currentPrimary;
                }
                if (targetMonitor == null)
                {
                    continue;
                }

                int x = targetMonitor.Bounds.Left + ScaleOffset(placement.MonitorOffsetX, placement.NormalizedX, targetMonitor.Bounds.Width);
                int y = targetMonitor.Bounds.Top + ScaleOffset(placement.MonitorOffsetY, placement.NormalizedY, targetMonitor.Bounds.Height);

                DesktopRestoreItem item = new DesktopRestoreItem();
                item.Identity = placement.Identity;
                item.DisplayName = placement.DisplayName;
                // Clamp in physical screen coordinates, then convert back to
                // the ListView's virtual-desktop client coordinates expected by
                // LVM_SETITEMPOSITION32.
                x = ClampCoordinate(x, targetMonitor.WorkingArea, true);
                y = ClampCoordinate(y, targetMonitor.WorkingArea, false);
                item.X = x - (currentTopology.VirtualBounds == null ? 0 : currentTopology.VirtualBounds.Left);
                item.Y = y - (currentTopology.VirtualBounds == null ? 0 : currentTopology.VirtualBounds.Top);
                item.TargetMonitorKey = targetMonitor.StableKey ?? string.Empty;
                plan.Items.Add(item);
            }
            return plan;
        }

        public DesktopRestoreResult RestoreCurrent()
        {
            DesktopRestorePlan plan = BuildRestorePlan();
            return iconService.Restore(plan);
        }

        public DesktopRestoreResult RestoreBestMatch()
        {
            return RestoreCurrent();
        }

        public DesktopRestoreResult Restore(DesktopRestorePlan plan)
        {
            return iconService.Restore(plan);
        }

        private static int ScaleOffset(int exactOffset, double normalized, int dimension)
        {
            if (dimension <= 0)
            {
                return Math.Max(0, exactOffset);
            }
            if (normalized > 0.000001 || normalized < -0.000001)
            {
                double value = normalized * dimension;
                if (value > int.MaxValue)
                {
                    return int.MaxValue;
                }
                if (value < int.MinValue)
                {
                    return int.MinValue;
                }
                return (int)Math.Round(value);
            }
            return exactOffset;
        }

        private static int ClampCoordinate(int value, DesktopRect area, bool horizontal)
        {
            if (area == null)
            {
                return value;
            }
            int minimum = horizontal ? area.Left : area.Top;
            int maximum = horizontal ? area.Right - 1 : area.Bottom - 1;
            if (maximum < minimum)
            {
                return minimum;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static List<DesktopIconPlacement> CloneIcons(IList<DesktopIconPlacement> source)
        {
            List<DesktopIconPlacement> result = new List<DesktopIconPlacement>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                DesktopIconPlacement item = source[index];
                if (item == null)
                {
                    continue;
                }
                result.Add(CloneIcon(item));
            }
            return result;
        }

        private static DesktopIconPlacement CloneIcon(DesktopIconPlacement source)
        {
            DesktopIconPlacement copy = new DesktopIconPlacement();
            copy.Identity = source.Identity;
            copy.DisplayName = source.DisplayName;
            copy.CreatedUtc = source.CreatedUtc;
            copy.MonitorKey = source.MonitorKey;
            copy.X = source.X;
            copy.Y = source.Y;
            copy.MonitorOffsetX = source.MonitorOffsetX;
            copy.MonitorOffsetY = source.MonitorOffsetY;
            copy.NormalizedX = source.NormalizedX;
            copy.NormalizedY = source.NormalizedY;
            return copy;
        }

        private static DesktopTopologyLayout CloneLayout(DesktopTopologyLayout source)
        {
            DesktopTopologyLayout copy = new DesktopTopologyLayout();
            copy.TopologySignature = source.TopologySignature;
            copy.Topology = source.Topology;
            copy.Icons = CloneIcons(source.Icons);
            copy.AutoMergedIdentities = source.AutoMergedIdentities == null
                ? new List<string>()
                : new List<string>(source.AutoMergedIdentities);
            copy.GridWidth = source.GridWidth;
            copy.GridHeight = source.GridHeight;
            copy.UpdatedUtc = source.UpdatedUtc;
            return copy;
        }

        /// <summary>
        /// 把本次捕获里新增的图标身份并进目录里的**每一份**布局（含 portable）。
        /// 这样在小屏上新加的图标，切回大屏后大屏布局也包含它，不会“回到大屏就没了”。
        /// 位置用各布局自己的主屏空位，不复制跨屏坐标。
        /// </summary>
        private static void MergeIdentitiesIntoAllLayouts(DesktopLayoutCatalog catalog, DesktopTopologyLayout source)
        {
            if (catalog == null || source == null || catalog.Layouts == null)
            {
                return;
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout target = catalog.Layouts[index];
                if (target == null || target.IsFolder || object.ReferenceEquals(target, source))
                {
                    // 布局文件夹只是分组，不能把新图标并进它的 Icons（否则会变成“有图标又不能恢复”的怪数据）。
                    continue;
                }
                if (target.Icons == null)
                {
                    target.Icons = new List<DesktopIconPlacement>();
                }
                HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int iconIndex = 0; iconIndex < target.Icons.Count; iconIndex++)
                {
                    DesktopIconPlacement item = target.Icons[iconIndex];
                    if (item != null && !string.IsNullOrEmpty(item.Identity))
                    {
                        identities.Add(item.Identity);
                    }
                }
                MergeIdentityFromLayout(source, target, target.Topology, identities, true);
            }
            if (catalog.PortableSingleDisplayLayout != null && !object.ReferenceEquals(catalog.PortableSingleDisplayLayout, source))
            {
                DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
                if (portable.Icons == null)
                {
                    portable.Icons = new List<DesktopIconPlacement>();
                }
                HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int iconIndex = 0; iconIndex < portable.Icons.Count; iconIndex++)
                {
                    DesktopIconPlacement item = portable.Icons[iconIndex];
                    if (item != null && !string.IsNullOrEmpty(item.Identity))
                    {
                        identities.Add(item.Identity);
                    }
                }
                MergeIdentityFromLayout(source, portable, portable.Topology, identities, true);
            }
        }

        private static void MergeIdentitySet(DesktopLayoutCatalog catalog, DesktopTopologyLayout current, DisplayTopology topology)
        {            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < current.Icons.Count; index++)
            {
                DesktopIconPlacement item = current.Icons[index];
                if (item != null && !string.IsNullOrEmpty(item.Identity))
                {
                    identities.Add(item.Identity);
                }
            }

            if (catalog.Layouts != null)
            {
                for (int layoutIndex = 0; layoutIndex < catalog.Layouts.Count; layoutIndex++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[layoutIndex];
                    MergeIdentityFromLayout(layout, current, topology, identities, false);
                }
            }
            // Older catalogs may keep the portable layout only in the
            // dedicated property.  It still participates in the global set.
            if (catalog.PortableSingleDisplayLayout != null)
            {
                MergeIdentityFromLayout(catalog.PortableSingleDisplayLayout, current, topology, identities, false);
            }
        }

        private static void MergeIntoPortableLayout(DesktopLayoutCatalog catalog, DesktopTopologyLayout source)
        {
            if (catalog == null || source == null || source.Icons == null)
            {
                return;
            }

            DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
            if (portable == null)
            {
                portable = new DesktopTopologyLayout();
                portable.Topology = BuildPortableTopology(source.Topology);
                portable.TopologySignature = portable.Topology.Signature;
                portable.GridWidth = source.GridWidth;
                portable.GridHeight = source.GridHeight;
                catalog.PortableSingleDisplayLayout = portable;
            }
            if (portable.Icons == null)
            {
                portable.Icons = new List<DesktopIconPlacement>();
            }
            if (portable.Topology == null || portable.Topology.GetPrimaryMonitor() == null)
            {
                portable.Topology = BuildPortableTopology(source.Topology);
            }

            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < portable.Icons.Count; index++)
            {
                DesktopIconPlacement item = portable.Icons[index];
                if (item != null && !string.IsNullOrEmpty(item.Identity))
                {
                    identities.Add(item.Identity);
                }
            }

            DisplayTopology portableTopology = portable.Topology;
            for (int index = 0; index < source.Icons.Count; index++)
            {
                DesktopIconPlacement sourceIcon = source.Icons[index];
                if (sourceIcon == null || string.IsNullOrEmpty(sourceIcon.Identity) || identities.Contains(sourceIcon.Identity))
                {
                    continue;
                }

                Point spare = FindSparePoint(portable.Icons, portableTopology, portable.GridWidth, portable.GridHeight);
                DesktopIconPlacement addition = CloneIcon(sourceIcon);
                addition.X = spare.X;
                addition.Y = spare.Y;
                DisplayMonitor monitor = portableTopology == null ? null : portableTopology.GetPrimaryMonitor();
                if (monitor != null)
                {
                    int virtualLeft = portableTopology.VirtualBounds == null ? 0 : portableTopology.VirtualBounds.Left;
                    int virtualTop = portableTopology.VirtualBounds == null ? 0 : portableTopology.VirtualBounds.Top;
                    int screenX = spare.X + virtualLeft;
                    int screenY = spare.Y + virtualTop;
                    addition.MonitorKey = monitor.StableKey;
                    addition.MonitorOffsetX = screenX - monitor.Bounds.Left;
                    addition.MonitorOffsetY = screenY - monitor.Bounds.Top;
                    addition.NormalizedX = monitor.Bounds.Width <= 0 ? 0.0 : (double)addition.MonitorOffsetX / monitor.Bounds.Width;
                    addition.NormalizedY = monitor.Bounds.Height <= 0 ? 0.0 : (double)addition.MonitorOffsetY / monitor.Bounds.Height;
                }
                portable.Icons.Add(addition);
                identities.Add(addition.Identity);
                // 这也是自动并进 portable 的图标，不是用户明确摆在这份布局里的；
                // 编辑这份布局时要继续出现在“新图标”里。
                RememberAutoMerged(portable, addition.Identity);
            }
            portable.UpdatedUtc = DateTime.UtcNow;
        }

        private static bool IsAutomaticLayoutId(string id)
        {
            return !string.IsNullOrWhiteSpace(id)
                && id.StartsWith("layout-", StringComparison.OrdinalIgnoreCase);
        }

        private static void SyncPortableLayoutEntry(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || catalog.PortableSingleDisplayLayout == null)
            {
                return;
            }
            if (catalog.Layouts == null)
            {
                catalog.Layouts = new List<DesktopTopologyLayout>();
            }

            DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
            if (string.IsNullOrWhiteSpace(portable.Id))
            {
                portable.Id = string.IsNullOrWhiteSpace(portable.TopologySignature)
                    ? "layout-auto-" + Guid.NewGuid().ToString("N")
                    : "layout-" + portable.TopologySignature;
            }
            int matchingIndex = -1;
            // 同一显示器组合可以有多份命名布局，这里只能更新“自动”那份，
            // 不能像以前那样按签名抓第一份，否则多屏捕获会把用户的命名布局顶掉。
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout candidate = catalog.Layouts[index];
                if (candidate != null
                    && string.Equals(candidate.Id, portable.Id, StringComparison.OrdinalIgnoreCase))
                {
                    matchingIndex = index;
                    break;
                }
            }

            DesktopTopologyLayout replacement = CloneLayout(portable);
            if (matchingIndex >= 0)
            {
                // Preserve a real topology signature when replacing a
                // resolution-specific entry; the portable property remains
                // the fallback source while exact-signature lookup keeps
                // working for that previously captured topology.
                string existingSignature = catalog.Layouts[matchingIndex].TopologySignature;
                if (!string.IsNullOrEmpty(existingSignature)
                    && !string.Equals(existingSignature, replacement.TopologySignature, StringComparison.OrdinalIgnoreCase))
                {
                    replacement.TopologySignature = existingSignature;
                }
                catalog.Layouts[matchingIndex] = replacement;
            }
            else
            {
                catalog.Layouts.Add(replacement);
            }
        }

        private static DisplayTopology BuildPortableTopology(DisplayTopology source)
        {
            DisplayTopology result = new DisplayTopology();
            if (source == null || source.Monitors == null || source.Monitors.Count == 0)
            {
                result.Signature = "PORTABLE";
                result.Monitors.Add(new DisplayMonitor
                {
                    StableKey = "PORTABLE",
                    IsPrimary = true,
                    Bounds = new DesktopRect(0, 0, 1920, 1080),
                    WorkingArea = new DesktopRect(0, 0, 1920, 1040)
                });
                result.VirtualBounds = new DesktopRect(0, 0, 1920, 1080);
                return result;
            }

            DisplayMonitor primary = source.GetPrimaryMonitor();
            DisplayMonitor copy = new DisplayMonitor
            {
                StableKey = primary.StableKey,
                DeviceName = primary.DeviceName,
                DeviceId = primary.DeviceId,
                FriendlyName = primary.FriendlyName,
                IsPrimary = true,
                DpiX = primary.DpiX,
                DpiY = primary.DpiY,
                Bounds = new DesktopRect(primary.Bounds.Left, primary.Bounds.Top, primary.Bounds.Width, primary.Bounds.Height),
                WorkingArea = new DesktopRect(primary.WorkingArea.Left, primary.WorkingArea.Top, primary.WorkingArea.Width, primary.WorkingArea.Height)
            };
            result.Monitors.Add(copy);
            result.VirtualBounds = new DesktopRect(copy.Bounds.Left, copy.Bounds.Top, copy.Bounds.Width, copy.Bounds.Height);
            result.Signature = "PORTABLE-" + (copy.StableKey ?? string.Empty);
            return result;
        }

        private static void MergeIdentityFromLayout(
            DesktopTopologyLayout source,
            DesktopTopologyLayout current,
            DisplayTopology topology,
            HashSet<string> identities,
            bool markAutoMerged)
        {
            if (source == null || source.Icons == null)
            {
                return;
            }
            for (int iconIndex = 0; iconIndex < source.Icons.Count; iconIndex++)
            {
                DesktopIconPlacement item = source.Icons[iconIndex];
                if (item == null || string.IsNullOrEmpty(item.Identity) || identities.Contains(item.Identity))
                {
                    continue;
                }
                Point spare = FindSparePoint(current.Icons, topology, current.GridWidth, current.GridHeight);
                DesktopIconPlacement addition = CloneIcon(item);
                addition.X = spare.X;
                addition.Y = spare.Y;
                DisplayMonitor monitor = topology == null ? null : topology.GetPrimaryMonitor();
                if (monitor != null)
                {
                    int virtualLeft = topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Left;
                    int virtualTop = topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Top;
                    int screenX = spare.X + virtualLeft;
                    int screenY = spare.Y + virtualTop;
                    addition.MonitorKey = monitor.StableKey;
                    addition.MonitorOffsetX = screenX - monitor.Bounds.Left;
                    addition.MonitorOffsetY = screenY - monitor.Bounds.Top;
                    addition.NormalizedX = monitor.Bounds.Width <= 0 ? 0.0 : (double)addition.MonitorOffsetX / monitor.Bounds.Width;
                    addition.NormalizedY = monitor.Bounds.Height <= 0 ? 0.0 : (double)addition.MonitorOffsetY / monitor.Bounds.Height;
                }
                current.Icons.Add(addition);
                identities.Add(addition.Identity);
                if (markAutoMerged)
                {
                    RememberAutoMerged(current, addition.Identity);
                }
            }
        }

        /// <summary>
        /// 记下“自动并集”身份。清单只用于编辑页判断新图标，
        /// 不去重 Icons（同一个身份仍只能有一个图标）。
        /// </summary>
        private static void RememberAutoMerged(DesktopTopologyLayout layout, string identity)
        {
            if (layout == null || string.IsNullOrEmpty(identity))
            {
                return;
            }
            if (layout.AutoMergedIdentities == null)
            {
                layout.AutoMergedIdentities = new List<string>();
            }
            for (int index = 0; index < layout.AutoMergedIdentities.Count; index++)
            {
                if (string.Equals(layout.AutoMergedIdentities[index], identity, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            layout.AutoMergedIdentities.Add(identity);
        }

        private static Point FindSparePoint(IList<DesktopIconPlacement> placements, DisplayTopology topology, int gridWidth, int gridHeight)
        {
            DisplayMonitor monitor = topology == null ? null : topology.GetPrimaryMonitor();
            DesktopRect area = monitor == null ? new DesktopRect(0, 0, 1920, 1080) : monitor.WorkingArea;
            if (area == null || area.Width <= 0 || area.Height <= 0)
            {
                area = new DesktopRect(0, 0, 1920, 1080);
            }
            gridWidth = gridWidth <= 0 ? 75 : gridWidth;
            gridHeight = gridHeight <= 0 ? 75 : gridHeight;
            int columns = Math.Max(1, area.Width / gridWidth);
            int rows = Math.Max(1, area.Height / gridHeight);
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int screenX = area.Left + column * gridWidth;
                    int screenY = area.Top + row * gridHeight;
                    int x = screenX - (topology == null || topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Left);
                    int y = screenY - (topology == null || topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Top);
                    bool occupied = false;
                    if (placements != null)
                    {
                        for (int index = 0; index < placements.Count; index++)
                        {
                            DesktopIconPlacement item = placements[index];
                            if (item != null && Math.Abs(item.X - x) < Math.Max(1, gridWidth / 2)
                                && Math.Abs(item.Y - y) < Math.Max(1, gridHeight / 2))
                            {
                                occupied = true;
                                break;
                            }
                        }
                    }
                    if (!occupied)
                    {
                        return new Point(x, y);
                    }
                }
            }
            return new Point(
                area.Left - (topology == null || topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Left),
                area.Top - (topology == null || topology.VirtualBounds == null ? 0 : topology.VirtualBounds.Top));
        }

        private static DesktopTopologyLayout FindNewestLayout(DesktopLayoutCatalog catalog, string signature)
        {
            if (catalog == null || catalog.Layouts == null || string.IsNullOrEmpty(signature))
            {
                return null;
            }
            DesktopTopologyLayout best = null;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null || layout.IsFolder
                    || !string.Equals(layout.TopologySignature, signature, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (best == null || layout.UpdatedUtc > best.UpdatedUtc)
                {
                    best = layout;
                }
            }
            return best;
        }

        private static DesktopTopologyLayout FindLayout(DesktopLayoutCatalog catalog, string signature)
        {
            if (catalog == null || catalog.Layouts == null || string.IsNullOrEmpty(signature))
            {
                return null;
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout != null && !layout.IsFolder
                    && string.Equals(layout.TopologySignature, signature, StringComparison.OrdinalIgnoreCase))
                {
                    return layout;
                }
            }
            return null;
        }

        private static DesktopTopologyLayout FindCompatibleLayout(DesktopLayoutCatalog catalog, DisplayTopology current)
        {
            if (catalog == null || catalog.Layouts == null || current == null || current.Monitors == null)
            {
                return null;
            }

            HashSet<string> currentKeys = MonitorKeys(current);
            DesktopTopologyLayout best = null;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout candidate = catalog.Layouts[index];
                if (candidate == null || candidate.IsFolder
                    || candidate.Topology == null || candidate.Topology.Monitors == null)
                {
                    continue;
                }

                HashSet<string> candidateKeys = MonitorKeys(candidate.Topology);
                if (candidateKeys.Count != currentKeys.Count || !SetsEqual(candidateKeys, currentKeys))
                {
                    continue;
                }

                if (best == null || candidate.UpdatedUtc > best.UpdatedUtc)
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static HashSet<string> MonitorKeys(DisplayTopology topology)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (topology != null && topology.Monitors != null)
            {
                for (int index = 0; index < topology.Monitors.Count; index++)
                {
                    DisplayMonitor monitor = topology.Monitors[index];
                    if (monitor != null && !string.IsNullOrEmpty(monitor.StableKey))
                    {
                        result.Add(monitor.StableKey);
                    }
                }
            }
            return result;
        }

        private static bool SetsEqual(HashSet<string> left, HashSet<string> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }
            foreach (string value in left)
            {
                if (!right.Contains(value))
                {
                    return false;
                }
            }
            return true;
        }

        private static void UpsertLayout(DesktopLayoutCatalog catalog, DesktopTopologyLayout layout)
        {
            if (catalog == null || layout == null)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(layout.Id))
            {
                layout.Id = string.IsNullOrWhiteSpace(layout.TopologySignature)
                    ? Guid.NewGuid().ToString("N")
                    : "layout-" + layout.TopologySignature;
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout existing = catalog.Layouts[index];
                if (existing == null || !string.Equals(existing.Id, layout.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // 自动保存保留原来的创建时间/排序/用户改过的自动布局名字。
                layout.CreatedUtc = existing.CreatedUtc == default(DateTime) ? DateTime.UtcNow : existing.CreatedUtc;
                layout.SortOrder = existing.SortOrder;
                if (!string.IsNullOrWhiteSpace(existing.Name))
                {
                    layout.Name = existing.Name;
                }
                catalog.Layouts[index] = layout;
                return;
            }
            catalog.Layouts.Add(layout);
        }

        private struct Point
        {
            internal int X;
            internal int Y;

            internal Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }
    }
}
