using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using Microsoft.Win32;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 图标布局编辑器（弹出页）。
    ///
    /// 【2026-10-20 用户要求】“编辑后，布局要和截图的小一致，图标也是（就是等比例缩放）”。
    /// 所以这里不再把截图按格子裁成一块块壁纸方块，而是：
    ///   · 画布 = 桌面的真实像素坐标（virtualBounds），桌面上每个格子的位置就是截图里的位置；
    ///   · 背景 = 当前壁纸（没有壁纸时用截图模糊版），不放带图标的截图，避免和编辑后的图标重影；
    ///   · 图标 = 从 Windows 取透明背景的大图标，按网格宽度等比缩放，文字也按同一比例缩放；
    ///   · 默认“适应窗口”，一打开就能看到整张桌面，和卡片截图是同一个比例。
    /// </summary>
    internal sealed partial class DesktopLayoutEditorWindow : Window
    {

        /// <summary>
        /// 编辑页先按“已保存布局”打开，当前桌面扫描完成后在这里补齐：
        /// 新增图标进标签，已删除图标标红，并更新摘要。加载遮罩会挡住这期间的误操作。
        /// </summary>
        internal void ApplyCurrentDesktopSnapshot(DesktopShellSnapshot current)
        {
            if (state.Closed || state.DesktopLoadCompleted)
            {
                return;
            }
            if (current == null || current.Icons == null)
            {
                CompleteDesktopLoad(
                    AppText.T(DesktopLayoutTextKeys.EditorCannotReadIcons));
                return;
            }
            HashSet<string> autoMergedIdentities = ReadAutoMergedIdentities(layout);
            HashSet<string> savedIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (layout != null && layout.Icons != null)
            {
                for (int index = 0; index < layout.Icons.Count; index++)
                {
                    DesktopIconPlacement saved = layout.Icons[index];
                    if (saved != null && !string.IsNullOrEmpty(saved.Identity))
                    {
                        string identity = DesktopIdentity.Normalize(saved.Identity);
                        // 自动并集的图标虽然存在 layout.Icons 里，但它不是这份布局
                        // 明确保存的基准；下面仍要按“新图标”处理。
                        if (!autoMergedIdentities.Contains(identity))
                        {
                            savedIdentities.Add(identity);
                        }
                    }
                }
            }
            HashSet<string> liveIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < current.Icons.Count; index++)
            {
                DesktopIconPlacement live = current.Icons[index];
                if (live != null && !string.IsNullOrEmpty(live.Identity))
                {
                    liveIdentities.Add(DesktopIdentity.Normalize(live.Identity));
                }
            }
            scannedMissingIdentities.Clear();
            int missingCount = 0;
            if (layout != null && layout.Icons != null)
            {
                for (int index = 0; index < layout.Icons.Count; index++)
                {
                    DesktopIconPlacement saved = layout.Icons[index];
                    if (saved == null || string.IsNullOrEmpty(saved.Identity))
                    {
                        continue;
                    }
                    string identity = DesktopIdentity.Normalize(saved.Identity);
                    // 自动并集图标本来就不算这份布局的基准，不能因为桌面上
                    // 暂时找不到就提示“已删除/找不到”。
                    if (autoMergedIdentities.Contains(identity))
                    {
                        continue;
                    }
                    if (!liveIdentities.Contains(identity) && scannedMissingIdentities.Add(identity))
                    {
                        missingCount++;
                    }
                }
            }

            HashSet<string> snapshotIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item != null && !string.IsNullOrEmpty(item.Identity))
                {
                    snapshotIdentities.Add(DesktopIdentity.Normalize(item.Identity));
                }
            }
            List<DesktopIconPlacement> newItems = new List<DesktopIconPlacement>();
            for (int index = 0; index < current.Icons.Count; index++)
            {
                DesktopIconPlacement live = current.Icons[index];
                if (live == null || string.IsNullOrEmpty(live.Identity))
                {
                    continue;
                }
                string identity = DesktopIdentity.Normalize(live.Identity);
                DesktopIconPlacement savedItem = FindLayoutItem(layout, identity);
                bool recreated = savedItem != null && IsRecreatedAfterLayout(live, savedItem);
                // 只要当前桌面上还有这个图标，就把最新创建时间带回编辑页快照。
                // 保存时它会写进 sidecar，下次编辑才能正确识别“同名文件又重建”。
                DesktopIconPlacement item = FindSnapshotItem(snapshot, identity);
                if (item != null)
                {
                    item.CreatedUtc = live.CreatedUtc;
                }
                if (savedIdentities.Contains(identity) && !recreated)
                {
                    continue;
                }
                bool autoMerged = autoMergedIdentities.Contains(identity);
                bool treatAsNew = autoMerged || recreated;
                if (item == null)
                {
                    if (!treatAsNew && !snapshotIdentities.Add(identity))
                    {
                        continue;
                    }
                    item = ClonePlacementForEditor(live);
                    // 当前桌面可能来自另一台/另一分辨率的显示器；新图标不能在
                    // 保存布局里沿用当前屏幕的绝对坐标，否则会落到保存画布之外。
                    // 置为哨兵，保存时按保存布局的网格原点/空位重新排。
                    item.X = -1000000;
                    item.Y = -1000000;
                    snapshot.Icons.Add(item);
                    snapshotIdentities.Add(identity);
                }
                else if (!treatAsNew)
                {
                    // 已在编辑页快照里、又不是自动并集/重建的图标：要么是明确保存的
                    // （前面已跳过），要么已经在“新图标”里，不能重复加一份。
                    continue;
                }
                else
                {
                    // 这个图标在旧布局里有同名记录，但当前是自动并集或删除后重建的；
                    // 不能沿用旧位置，也不能继续在预桌面上显示旧 tile。
                    RemoveIconTileForNewIcon(item);
                    item.X = -1000000;
                    item.Y = -1000000;
                    item.CreatedUtc = live.CreatedUtc;
                    item.DisplayName = live.DisplayName;
                    if (recreated)
                    {
                        // 同名重建的文件图标可能也换了；丢掉旧缓存，重新读一次。
                        imageCache.RemoveNewIconSource(item);
                        imageCache.EndLoading(item);
                    }
                }
                if (newIdentities.Add(item.Identity ?? string.Empty))
                {
                    unplacedNewItems.Add(item);
                }
                newItems.Add(item);
            }
            // 自动并集图标如果在当前桌面上已经找不到，就不该继续占着“新图标”；
            // 从编辑页快照里拿掉，保存时会自然从这份布局移除。
            List<DesktopIconPlacement> staleAutoMerged = new List<DesktopIconPlacement>();
            List<DesktopIconPlacement> unplacedSnapshot =
                new List<DesktopIconPlacement>(unplacedNewItems);
            for (int index = 0; index < unplacedSnapshot.Count; index++)
            {
                DesktopIconPlacement item = unplacedSnapshot[index];
                if (item == null || string.IsNullOrEmpty(item.Identity))
                {
                    continue;
                }
                string identity = DesktopIdentity.Normalize(item.Identity);
                if (autoMergedIdentities.Contains(identity) && !liveIdentities.Contains(identity))
                {
                    staleAutoMerged.Add(item);
                }
            }
            for (int index = 0; index < staleAutoMerged.Count; index++)
            {
                DesktopIconPlacement item = staleAutoMerged[index];
                unplacedNewItems.Remove(item);
                newIdentities.Remove(item.Identity ?? string.Empty);
                snapshot.Icons.Remove(item);
                snapshotIdentities.Remove(DesktopIdentity.Normalize(item.Identity));
            }

            layoutSummaryBase = DesktopLayoutFolderStore.DescribeDisplay(layout)
                + AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryIconsFormat,
                    snapshot.Icons.Count);
            if (missingCount > 0)
            {
                layoutSummaryBase += AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryMissingFormat,
                    missingCount);
            }
            RebuildNewIconCards();
            RefreshMissingIconBorders();
            UpdateNewIconCount(false);
            if (newItems.Count > 0)
            {
                StartIconLoadingForItems(newItems, false);
            }
            CompleteDesktopLoad(
                AppText.T(DesktopLayoutTextKeys.EditorDesktopLoaded));
        }

        internal void ShowSavedCanvasNotice(string notice)
        {
            if (statusText != null && !string.IsNullOrWhiteSpace(notice))
            {
                statusText.Text = notice;
            }
        }

        /// <summary>
        /// 同一个布局的编辑页已经打开、但被最小化时，从任务栏/主页面再次点
        /// “编辑图标布局”应该把原窗口还原并带到前面，绝不能关掉重开：
        /// 重开不仅会丢掉未保存的拖动，还会让用户以为布局被重置。
        /// </summary>
        internal void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            if (!IsVisible)
            {
                Show();
            }
            Activate();
            if (desktopCanvas != null && desktopCanvas.IsVisible)
            {
                desktopCanvas.Focus();
            }
        }

        private void CompleteDesktopLoad(string message)
        {
            state.DesktopLoadCompleted = true;
            if (statusText != null && !string.IsNullOrEmpty(message))
            {
                statusText.Text = message;
            }
            if (desktopLoadingCover != null)
            {
                desktopLoadingCover.IsHitTestVisible = false;
                DoubleAnimation fade = new DoubleAnimation(0.0,
                    new Duration(TimeSpan.FromMilliseconds(160)));
                fade.Completed += delegate
                {
                    if (desktopLoadingCover != null)
                    {
                        desktopLoadingCover.Visibility = Visibility.Collapsed;
                    }
                };
                desktopLoadingCover.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            if (desktopCanvas != null)
            {
                desktopCanvas.Focus();
            }
        }

        private static DesktopIconPlacement ClonePlacementForEditor(DesktopIconPlacement source)
        {
            return new DesktopIconPlacement
            {
                Identity = source.Identity,
                DisplayName = source.DisplayName,
                CreatedUtc = source.CreatedUtc,
                MonitorKey = source.MonitorKey,
                X = source.X,
                Y = source.Y,
                MonitorOffsetX = source.MonitorOffsetX,
                MonitorOffsetY = source.MonitorOffsetY,
                NormalizedX = source.NormalizedX,
                NormalizedY = source.NormalizedY
            };
        }

        private static HashSet<string> ReadAutoMergedIdentities(DesktopTopologyLayout layout)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (layout == null || layout.AutoMergedIdentities == null)
            {
                return result;
            }
            for (int index = 0; index < layout.AutoMergedIdentities.Count; index++)
            {
                string identity = DesktopIdentity.Normalize(layout.AutoMergedIdentities[index]);
                if (!string.IsNullOrEmpty(identity))
                {
                    result.Add(identity);
                }
            }
            return result;
        }

        private static DesktopIconPlacement FindSnapshotItem(
            DesktopShellSnapshot snapshot,
            string identity)
        {
            if (snapshot == null || snapshot.Icons == null || string.IsNullOrEmpty(identity))
            {
                return null;
            }
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item != null && DesktopIdentity.Equals(item.Identity, identity))
                {
                    return item;
                }
            }
            return null;
        }

        private static DesktopIconPlacement FindLayoutItem(
            DesktopTopologyLayout layout,
            string identity)
        {
            if (layout == null || layout.Icons == null || string.IsNullOrEmpty(identity))
            {
                return null;
            }
            for (int index = 0; index < layout.Icons.Count; index++)
            {
                DesktopIconPlacement item = layout.Icons[index];
                if (item != null && DesktopIdentity.Equals(item.Identity, identity))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// Windows 的图标身份只看路径/名称：把同名文件删掉再新建，身份不变。
        /// 但用户看到的是一个新文件，必须重新归入“新图标”，不能继续占旧位置。
        /// 用“当前文件创建时间晚于这份布局的保存时间/上次记录的创建时间”判断；
        /// 旧档案没有 CreatedUtc 时退回布局的 UpdatedUtc，兼容本次已出现的数据。
        /// </summary>
        private bool IsRecreatedAfterLayout(
            DesktopIconPlacement live,
            DesktopIconPlacement saved)
        {
            return DesktopIconFreshness.RecreatedAfterLayout(
                live, saved,
                layout == null ? DateTime.MinValue : layout.CreatedUtc,
                layout == null ? DateTime.MinValue : layout.UpdatedUtc);
        }

        private void RemoveIconTileForNewIcon(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return;
            }
            Border tile;
            if (tileByItem.TryGetValue(item, out tile))
            {
                desktopCanvas.Children.Remove(tile);
                tileByItem.Remove(item);
            }
            imageCache.RemoveCanvasImage(item);
            iconSkeletons.Remove(item);
        }

        /// <summary>
        /// 每帧只建 6 个图标 tile。68 个图标的标签、快捷方式角标、阴影文字
        /// 全部同步建出来会把首帧卡住；窗口先显示，再分批补。
        /// </summary>
        private void BeginIconTileBuild()
        {
            if (state.IconTileBuildStarted)
            {
                return;
            }
            state.IconTileBuildStarted = true;
            List<DesktopIconPlacement> items = new List<DesktopIconPlacement>();
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item == null)
                {
                    continue;
                }
                // 新图标先只在“新图标”标签里，不直接铺到预桌面。
                if (newIdentities.Contains(item.Identity ?? string.Empty))
                {
                    continue;
                }
                items.Add(item);
            }
            if (items.Count == 0)
            {
                StartIconLoading();
                return;
            }
            int itemIndex = 0;
            Action buildBatch = null;
            buildBatch = delegate
            {
                if (state.Closed)
                {
                    return;
                }
                int end = Math.Min(itemIndex + 6, items.Count);
                for (; itemIndex < end; itemIndex++)
                {
                    AddIconTile(items[itemIndex]);
                }
                if (itemIndex < items.Count)
                {
                    Dispatcher.BeginInvoke(buildBatch, DispatcherPriority.Background);
                }
                else
                {
                    StartIconLoading();
                }
            };
            Dispatcher.BeginInvoke(buildBatch, DispatcherPriority.Background);
        }

        private bool IsMissingIcon(DesktopIconPlacement item)
        {
            return item != null && !string.IsNullOrEmpty(item.Identity)
                && scannedMissingIdentities.Contains(DesktopIdentity.Normalize(item.Identity));
        }

        /// <summary>
        /// 图标的语义描边：已删除优先红框，其次新图标绿框，普通图标透明。
        /// 悬停、拖动结束、扫描刷新都走这里，避免各存一份状态后颜色互相覆盖。
        /// </summary>
        private Brush GetIconTileBorder(DesktopIconPlacement item)
        {
            if (IsMissingIcon(item))
            {
                return missingScanHighlightActive
                    ? EditorPalette.DangerOutline
                    : AppTheme.Danger;
            }
            return item != null && newIdentities.Contains(item.Identity ?? string.Empty)
                ? AppTheme.Positive
                : Brushes.Transparent;
        }

    }
}
