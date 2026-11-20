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
using WorkspaceRestorer.UI.Cards.Drag;

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

        private void AddIconTile(DesktopIconPlacement item)
        {
            bool isNew = newIdentities.Contains(item.Identity ?? string.Empty);
            bool isMissing = IsMissingIcon(item);
            double tileWidth = gridWidth;
            double tileHeight = gridHeight;

            Border tile = new Border
            {
                Width = tileWidth,
                Height = tileHeight,
                Background = Brushes.Transparent,
                // 已删除/找不到的图标红框；新图标绿框。
                BorderBrush = GetIconTileBorder(item),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = false,
                SnapsToDevicePixels = true,
                Cursor = Cursors.SizeAll
            };
            AutomationProperties.SetName(
                tile,
                AppText.F(
                    DesktopLayoutTextKeys.EditorIconAutomationFormat,
                    item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback)));
            if (isMissing)
            {
                // 用 AttachDescription 而不是 Attach：保留 UIA 名字“图标：xxx”，
                // 只额外补悬浮说明，读屏仍能听到具体图标名。
                AccessibleToolTip.AttachDescription(tile, DesktopLayoutToolTips.MissingIcon);
                EnsureMissingIconContextMenu(tile, item);
            }

            StackPanel content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid iconHost = new Grid
            {
                Width = iconSize,
                Height = iconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, iconTop, 0, 0)
            };
            Border skeleton = new Border
            {
                Background = EditorPalette.TileFloating,
                BorderBrush = EditorPalette.TileFloatingBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Math.Max(3.0, iconSize * 0.16))
            };
            Image iconImage = new Image
            {
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.Linear);
            iconHost.Children.Add(skeleton);
            iconHost.Children.Add(iconImage);
            if (DesktopIconImageLoader.IsShortcut(item))
            {
                // 快捷方式左下角的白色小箭头。Windows 桌面截图里有，编辑器里也补上，
                // 不然同一份布局看起来会少一层信息。
                Border shortcutBadge = new Border
                {
                    Width = iconSize * 0.26,
                    Height = iconSize * 0.26,
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(iconSize * 0.02, 0, 0, iconSize * 0.02),
                    Child = new TextBlock
                    {
                        Text = "\u2197",
                        FontFamily = AppFonts.Symbol,
                        FontSize = iconSize * 0.17,
                        Foreground = EditorPalette.TileLink,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                iconHost.Children.Add(shortcutBadge);
            }
            content.Children.Add(iconHost);

            string rawLabel = string.IsNullOrWhiteSpace(item.DisplayName)
                ? AppText.T(DesktopLayoutTextKeys.IconFallback)
                : item.DisplayName;
            double labelWidth = Math.Max(24.0, tileWidth);
            string labelText = FormatDesktopLabel(rawLabel, labelWidth, labelFontSize);
            bool preformatted = labelText.IndexOf('\n') >= 0;
            // 以前每个标签一个 DropShadowEffect：68 个像素着色器 + 4K 画布缩放，
            // 拖动时 UI 线程直接卡死。现在用“深色文字偏移 1px + 白色文字”两层文字，
            // 效果和桌面标签的阴影接近，但只是普通文字绘制，没有 Effect 的离屏渲染。
            TextBlock labelShadow = CreateDesktopLabel(
                labelText, labelWidth, preformatted, EditorPalette.TileLabelShadow);
            labelShadow.RenderTransform = new TranslateTransform(1.2, 1.2);
            TextBlock label = CreateDesktopLabel(labelText, labelWidth, preformatted, Brushes.White);
            Grid labelLayer = new Grid
            {
                Width = labelWidth,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            labelLayer.Children.Add(labelShadow);
            labelLayer.Children.Add(label);
            content.Children.Add(labelLayer);

            Grid tileContent = new Grid();
            tileContent.Children.Add(content);
            if (isNew)
            {
                Border badge = new Border
                {
                    Background = AppTheme.Positive,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 0, 4, 1),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 2, 0),
                    Child = new TextBlock
                    {
                        Text = AppText.T(DesktopLayoutTextKeys.EditorNewBadge),
                        FontSize = Math.Max(8.0, labelFontSize * 0.55),
                        Foreground = Brushes.White
                    }
                };
                tileContent.Children.Add(badge);
            }
            tile.Child = tileContent;
            Canvas.SetLeft(tile, item.X - virtualBounds.Left);
            Canvas.SetTop(tile, item.Y - virtualBounds.Top);
            Panel.SetZIndex(tile, 10);

            Brush normalBackground = Brushes.Transparent;
            bool dragging = false;
            bool overNewIconDock = false;
            DispatcherTimer dragWatchdog = null;
            Action finishDrag = null;
            DesktopIconPositionsState dragBeforeState = null;
            Point dragStartWindowPoint = new Point(0.0, 0.0);
            bool iconDragGhostVisible = false;
            double dragStartLeft = 0;
            double dragStartTop = 0;
            Rect canvasDragBounds = Rect.Empty;
            bool dragUpdateQueued = false;
            bool captureRestoreQueued = false;
            int leftUpTicks = 0;
            Action updateDragPosition = null;
            Action requestDragUpdate = null;

            finishDrag = delegate
            {
                if (!dragging)
                {
                    return;
                }
                dragging = false;
                dragUpdateQueued = false;
                captureRestoreQueued = false;
                leftUpTicks = 0;
                if (dragWatchdog != null)
                {
                    dragWatchdog.Stop();
                }
                if (tile.IsMouseCaptured)
                {
                    tile.ReleaseMouseCapture();
                }
                Point releasePoint = GetMousePositionInWindow();
                bool dropToNewIcons = IsNewIconDropPoint(releasePoint);
                bool reallyMoved = iconDragGhostVisible;
                EndIconDragGhost();
                SetNewIconDropHighlight(false);
                ClearNewIconDropTargets();
                overNewIconDock = false;
                tile.Opacity = 1.0;
                tile.Visibility = Visibility.Visible;
                Panel.SetZIndex(tile, 10);
                dragPointerPhysicalFallback = false;
                if (reallyMoved && dropToNewIcons
                    && MoveTileToNewIconDock(item, tile))
                {
                    // 【2026-10-25 修“图标被吞、Ctrl+Z 回不来”】
                    // 以前这里直接 dragBeforeState = null，没有撤销记录；
                    // MoveTileToNewIconDock 又会把 tile 整个删掉，撤销也没有控件可显示。
                    PushUndoState(dragBeforeState);
                    dragBeforeState = null;
                    iconDragGhostVisible = false;
                    tile.Background = normalBackground;
                    tile.BorderBrush = GetIconTileBorder(item);
                    return;
                }
                if (reallyMoved)
                {
                    // 松手不在预桌面内时，图标中心夹到最近边缘，再交给 SnapTile 吸附网格。
                    Point canvasPoint = GetMousePositionInDesktopCanvas();
                    Point center = ClampIconCenterToCanvas(canvasPoint, tileWidth, tileHeight);
                    Canvas.SetLeft(tile, center.X - tileWidth / 2.0);
                    Canvas.SetTop(tile, center.Y - tileHeight / 2.0);
                    PushUndoState(dragBeforeState);
                    SnapTile(tile, item, dragStartLeft, dragStartTop);
                    statusText.Text = AppText.F(
                        DesktopLayoutTextKeys.EditorMovedIconFormat,
                        item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback));
                    state.MarkLayoutChanged();
                    if (unplacedNewItems.Contains(item))
                    {
                        MarkNewIconPlaced(item, true);
                    }
                }
                else
                {
                    Canvas.SetLeft(tile, dragStartLeft);
                    Canvas.SetTop(tile, dragStartTop);
                }
                dragBeforeState = null;
                iconDragGhostVisible = false;
                tile.Background = normalBackground;
                tile.BorderBrush = GetIconTileBorder(item);
            };

            updateDragPosition = delegate
            {
                if (!dragging)
                {
                    return;
                }
                // 【2026-10-25 用户要求】拖动图标不再只限制在预桌面里：
                // 真实 tile 留在原地、只把不透明度降为 0，用全窗口浮卡跟鼠标，
                // 这样整页（包括画布外的新图标标签/展开面板）都能看到图标在鼠标下面。
                Point windowPoint = GetMousePositionInWindow();
                if (!iconDragGhostVisible
                    && (Math.Abs(windowPoint.X - dragStartWindowPoint.X) > 3.0
                        || Math.Abs(windowPoint.Y - dragStartWindowPoint.Y) > 3.0))
                {
                    iconDragGhostVisible = true;
                    // 只在浮卡出现时算一次可见预桌面；拖动中不再每帧
                    // TransformToAncestor（4K/150% 下这是明显卡顿源）。
                    canvasDragBounds = GetVisibleDesktopCanvasBounds();
                    BeginIconDragGhost(item, windowPoint, canvasDragBounds);
                    tile.Opacity = 0.0;
                }
                bool over = IsNewIconDropPoint(windowPoint);
                if (over != overNewIconDock)
                {
                    overNewIconDock = over;
                    SetNewIconDropHighlight(over);
                    statusText.Text = over
                        ? AppText.T(DesktopLayoutTextKeys.EditorDragOverNewIcons)
                        : AppText.T(DesktopLayoutTextKeys.EditorDragHint);
                }
                if (iconDragGhostVisible)
                {
                    MoveIconDragGhost(windowPoint, canvasDragBounds);
                }
            };

            requestDragUpdate = delegate
            {
                if (!dragging || dragUpdateQueued)
                {
                    return;
                }
                dragUpdateQueued = true;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    dragUpdateQueued = false;
                    if (dragging)
                    {
                        updateDragPosition();
                    }
                }), DispatcherPriority.Render);
            };

            tile.MouseEnter += delegate
            {
                if (!dragging && !state.Saving)
                {
                    tile.Background = EditorPalette.TileHover;
                    // 语义描边（新图标绿、已删除红）在悬停时也要保留，
                    // 不能鼠标一进来就变成普通强调色。
                    tile.BorderBrush = GetIconTileBorder(item);
                }
            };
            tile.MouseLeave += delegate
            {
                if (!dragging)
                {
                    tile.Background = normalBackground;
                    tile.BorderBrush = GetIconTileBorder(item);
                }
            };
            tile.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (state.Saving)
                {
                    return;
                }
                dragging = true;
                overNewIconDock = false;
                iconDragGhostVisible = false;
                dragUpdateQueued = false;
                captureRestoreQueued = false;
                leftUpTicks = 0;
                dragPointerPhysicalFallback = false;
                SetNewIconDropHighlight(false);
                CaptureNewIconDropTargets();
                dragBeforeState = CapturePositions();
                dragStartWindowPoint = args.GetPosition(this);
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorDragHint);
                // 鼠标可能被拖出窗口/画布；定时看左键是否真的松了。
                // 仍按着左键时不结束拖动，松手（哪怕在窗口外）才结束。
                if (dragWatchdog == null)
                {
                    dragWatchdog = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(16)
                    };
                    dragWatchdog.Tick += delegate
                    {
                        if (!dragging)
                        {
                            dragWatchdog.Stop();
                            return;
                        }
                        if (!NativeMethods.IsLeftMouseButtonDown())
                        {
                            // 连续两个 tick 确认，避免瞬时误判把拖动打断。
                            leftUpTicks++;
                            if (leftUpTicks >= 2)
                            {
                                finishDrag();
                            }
                            return;
                        }
                        leftUpTicks = 0;
                        // 鼠标甩得快时 MouseMove 可能丢事件；每 16ms
                        // 只请求一次合并更新，由 Render 每帧最多执行一次。
                        requestDragUpdate();
                    };
                }
                dragWatchdog.Start();
                desktopCanvas.Focus();
                double left = Canvas.GetLeft(tile);
                double top = Canvas.GetTop(tile);
                if (double.IsNaN(left))
                {
                    left = 0;
                }
                if (double.IsNaN(top))
                {
                    top = 0;
                }
                dragStartLeft = left;
                dragStartTop = top;
                tile.CaptureMouse();
                tile.Opacity = 1.0;
                Panel.SetZIndex(tile, 1000);
                tile.Background = EditorPalette.TileSelected;
                tile.BorderBrush = AppTheme.Accent;
                args.Handled = true;
            };
            tile.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!dragging)
                {
                    return;
                }
                if (!NativeMethods.IsLeftMouseButtonDown())
                {
                    // 交给 16ms 看门狗连续确认；MouseMove 单次不再结束拖动。
                    return;
                }
                if (!tile.IsMouseCaptured)
                {
                    tile.CaptureMouse();
                }
                requestDragUpdate();
                // 拖动过程中只移动画面上的 tile，不改快照坐标。
                // 松手后由 SnapTile 一次性写回；否则拖到半格又弹回原位时，
                // 画面看的是旧格、保存写的却是半格坐标。
            };
            tile.MouseLeftButtonUp += delegate
            {
                // 捕获短暂丢失时 WPF 可能补一个假 Up；物理左键还按着就忽略。
                if (NativeMethods.IsLeftMouseButtonDown())
                {
                    if (!tile.IsMouseCaptured)
                    {
                        tile.CaptureMouse();
                    }
                    return;
                }
                finishDrag();
            };
            tile.LostMouseCapture += delegate
            {
                if (!dragging)
                {
                    return;
                }
                // 拖动生命周期不依赖鼠标捕获；先切物理光标，只重捕获一次。
                dragPointerPhysicalFallback = true;
                if (NativeMethods.IsLeftMouseButtonDown() && !state.Saving && !state.Closed)
                {
                    if (captureRestoreQueued)
                    {
                        return;
                    }
                    captureRestoreQueued = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        captureRestoreQueued = false;
                        if (dragging && !tile.IsMouseCaptured
                            && NativeMethods.IsLeftMouseButtonDown())
                        {
                            tile.CaptureMouse();
                        }
                    }), DispatcherPriority.Input);
                    return;
                }
                finishDrag();
            };

            imageCache.SetCanvasImage(item, iconImage);
            iconSkeletons[item] = skeleton;
            tileByItem[item] = tile;
            desktopCanvas.Children.Add(tile);
            // 新图标第一次拖进画布时，图片可能只在“新图标卡片”缓存里；
            // 建好 tile 后立刻把真实图片补上，避免只剩灰色 skeleton。
            if (isNew)
            {
                ApplyCachedNewIconToTile(item);
            }
        }

        /// <summary>
        /// 把图片缓存里的新图标图片同步写到画布 tile；没有缓存时 EnsureSource 会补加载。
        /// 放置新图标后调用，保证不会出现“拖动浮卡有图标、放下后 tile 空白”。
        /// </summary>
        private void ApplyCachedNewIconToTile(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return;
            }
            BitmapSource source = imageCache.EnsureSource(item);
            if (source == null)
            {
                return;
            }
            Image tileImage;
            if (imageCache.TryGetCanvasImage(item, out tileImage) && tileImage != null)
            {
                tileImage.Source = source;
            }
            Border skeleton;
            if (iconSkeletons.TryGetValue(item, out skeleton) && skeleton != null)
            {
                skeleton.Visibility = Visibility.Collapsed;
            }
        }



        private void EnsureMissingIconContextMenu(Border tile, DesktopIconPlacement item)
        {
            if (tile == null || item == null || tile.ContextMenu != null)
            {
                return;
            }
            ContextMenu missingMenu = new ContextMenu();
            MenuItem removeMissing = new MenuItem
            {
                Header = AppText.T(DesktopLayoutTextKeys.EditorRemoveMissingMenu)
            };
            removeMissing.Click += delegate
            {
                RemoveMissingIconFromPreview(item, tile);
            };
            missingMenu.Items.Add(removeMissing);
            tile.ContextMenu = missingMenu;
        }

        private void RemoveMissingIconFromPreview(DesktopIconPlacement item, Border tile)
        {
            if (item == null || tile == null || state.Saving || !IsMissingIcon(item))
            {
                return;
            }
            snapshot.Icons.Remove(item);
            string identity = DesktopIdentity.Normalize(item.Identity);
            if (!string.IsNullOrEmpty(identity))
            {
                scannedMissingIdentities.Remove(identity);
            }
            tileByItem.Remove(item);
            imageCache.RemoveCanvasImage(item);
            iconSkeletons.Remove(item);
            imageCache.RemoveNewIconSource(item);
            desktopCanvas.Children.Remove(tile);
            RebuildLayoutSummaryFromSnapshot();
            state.MarkLayoutChanged();
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.EditorRemovedMissingFormat,
                item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback));
        }

        private void RebuildLayoutSummaryFromSnapshot()
        {
            int missing = 0;
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                if (IsMissingIcon(snapshot.Icons[index]))
                {
                    missing++;
                }
            }
            layoutSummaryBase = DesktopLayoutFolderStore.DescribeDisplay(layout)
                + AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryIconsFormat,
                    snapshot.Icons.Count);
            if (missing > 0)
            {
                layoutSummaryBase += AppText.F(
                    DesktopLayoutTextKeys.EditorSummaryMissingFormat,
                    missing);
            }
            UpdateNewIconCount(false);
        }

        private void ApplyMissingIconHighlight(bool active)
        {
            missingScanHighlightActive = active;
            if (missingScanHighlightTimer == null)
            {
                missingScanHighlightTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5.0)
                };
                missingScanHighlightTimer.Tick += delegate
                {
                    HideMissingIconHighlight();
                };
            }
            missingScanHighlightTimer.Stop();
            foreach (KeyValuePair<DesktopIconPlacement, Border> pair in tileByItem)
            {
                Border tile = pair.Value;
                DesktopIconPlacement item = pair.Key;
                if (tile == null || item == null || !IsMissingIcon(item))
                {
                    continue;
                }
                tile.BorderBrush = active ? EditorPalette.DangerOutline : AppTheme.Danger;
                tile.BorderThickness = new Thickness(active ? 4 : 2);
                tile.Effect = active
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = Color.FromRgb(255, 59, 48),
                        BlurRadius = 14,
                        ShadowDepth = 0,
                        Opacity = 0.7
                    }
                    : null;
            }
            if (active)
            {
                // 比扫描浮层多显示 1 秒（浮层 4 秒，高亮 5 秒）；
                // 点空白处会立即收起。
                missingScanHighlightTimer.Start();
            }
        }

        private void HideMissingIconHighlight()
        {
            if (missingScanHighlightTimer != null)
            {
                missingScanHighlightTimer.Stop();
            }
            if (missingScanHighlightActive)
            {
                ApplyMissingIconHighlight(false);
            }
        }

        private void RefreshMissingIconBorders()
        {
            foreach (KeyValuePair<DesktopIconPlacement, Border> pair in tileByItem)
            {
                Border tile = pair.Value;
                DesktopIconPlacement item = pair.Key;
                if (tile == null || item == null)
                {
                    continue;
                }
                bool isMissing = IsMissingIcon(item);
                tile.BorderBrush = GetIconTileBorder(item);
                tile.BorderThickness = new Thickness(2);
                if (isMissing)
                {
                    AccessibleToolTip.AttachDescription(tile, DesktopLayoutToolTips.MissingIcon);
                    EnsureMissingIconContextMenu(tile, item);
                }
                else
                {
                    // 文件重新出现/重新扫描后要真的恢复普通状态，不能只把红框擦掉，
                    // 鼠标提示里还留着“已被删除”。
                    tile.ContextMenu = null;
                    tile.ToolTip = null;
                    // SetHelpText(element, null) 会抛 ArgumentException（“不是属性 HelpText 的有效值”），
                    // 扫描时整批刷新就会失败。清附加属性要用 ClearValue。
                    tile.ClearValue(AutomationProperties.HelpTextProperty);
                }
            }
        }

        private void ApplyNewIconFilter()
        {
            if (newIconFilter == null)
            {
                return;
            }
            string filter = newIconFilter.Text.Trim();
            for (int index = 0; index < trayEntries.Count; index++)
            {
                TrayEntry entry = trayEntries[index];
                bool visible = filter.Length == 0
                    || (entry.Item.DisplayName ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                entry.Card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

    }
}
