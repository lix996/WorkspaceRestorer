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

        private double GetDpiScale()
        {
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0.1)
                {
                    return dpi.DpiScaleX;
                }
            }
            catch
            {
            }
            return 1.0;
        }

        private double ComputeFitZoom()
        {
            if (scroll == null)
            {
                return 1.0;
            }
            double viewportWidth = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            double viewportHeight = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
            if (viewportWidth < 60 || viewportHeight < 60)
            {
                return 1.0;
            }
            // 多留 12px 余量：正好等于 48px 时 WPF 的取整会让 ScrollViewer
            // 误以为内容比视口大 1px，于是“适应窗口”也冒出滚动条。
            double availableWidth = Math.Max(80.0, viewportWidth - 60.0);
            double availableHeight = Math.Max(80.0, viewportHeight - 60.0);
            double fitWidth = availableWidth * dpiScale / (canvasWidth + 12.0);
            double fitHeight = availableHeight * dpiScale / (canvasHeight + 12.0);
            return Math.Max(0.05, Math.Min(1.0, Math.Min(fitWidth, fitHeight)));
        }

        private void FitToWindow()
        {
            dpiScale = GetDpiScale();
            fitMode = true;
            SetZoom(ComputeFitZoom(), false);
        }

        private void UpdateZoomHostSize(double scale)
        {
            if (zoomHost == null || desktopFrame == null)
            {
                return;
            }
            // 画面的排版尺寸 = 画布 + 边框/内边距/外边距；缩放只改变 RenderTransform，
            // 但 ScrollViewer 必须知道“缩放后的尺寸”才知道滚动范围。
            double chromeX = desktopFrame.Margin.Left + desktopFrame.Margin.Right
                + desktopFrame.Padding.Left + desktopFrame.Padding.Right
                + desktopFrame.BorderThickness.Left + desktopFrame.BorderThickness.Right;
            double chromeY = desktopFrame.Margin.Top + desktopFrame.Margin.Bottom
                + desktopFrame.Padding.Top + desktopFrame.Padding.Bottom
                + desktopFrame.BorderThickness.Top + desktopFrame.BorderThickness.Bottom;
            zoomHost.Width = Math.Max(1.0, (canvasWidth + chromeX) * scale);
            zoomHost.Height = Math.Max(1.0, (canvasHeight + chromeY) * scale);
        }

        /// <summary>按鼠标位置缩放：滚轮指向哪个图标，放大后它还留在鼠标下面。</summary>
        private void SetZoomAt(double value, Point viewportPoint)
        {
            if (scroll == null)
            {
                return;
            }
            double newZoom = Math.Max(0.05, Math.Min(2.0, value));
            if (Math.Abs(newZoom - zoom) < 0.0001)
            {
                zoomText.Text = ((int)Math.Round(newZoom * 100)) + "%";
                return;
            }
            SettleNewIconDirectionTransitionForViewportChange();
            double oldScale = zoom / dpiScale;
            if (oldScale <= 0)
            {
                return;
            }
            // 内容比视口小时 ScrollViewer 会居中，此时 origin 不是 -Offset，
            // 必须用实际变换后的 origin 才能保证鼠标指向的图标不跳。
            Point oldOrigin = zoomHost.TransformToAncestor(scroll).Transform(new Point(0, 0));
            double canvasX = (viewportPoint.X - oldOrigin.X) / oldScale;
            double canvasY = (viewportPoint.Y - oldOrigin.Y) / oldScale;

            zoom = newZoom;
            double newScale = zoom / dpiScale;
            canvasScale.ScaleX = newScale;
            canvasScale.ScaleY = newScale;
            UpdateZoomHostSize(newScale);
            zoomText.Text = ((int)Math.Round(zoom * 100)) + "%";

            // 同一个输入事件里完成“改尺寸 + 重新布局 + 校正滚动位置”，
            // 中间不再让 Dispatcher 插一帧；否则会先画放大后的旧位置，再跳到新位置。
            scroll.UpdateLayout();
            double viewportWidth = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            double viewportHeight = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
            double targetX = zoomHost.Width > viewportWidth - 0.5
                ? canvasX * newScale - viewportPoint.X
                : 0.0;
            double targetY = zoomHost.Height > viewportHeight - 0.5
                ? canvasY * newScale - viewportPoint.Y
                : 0.0;
            scroll.ScrollToHorizontalOffset(Math.Max(0.0, targetX));
            scroll.ScrollToVerticalOffset(Math.Max(0.0, targetY));
            // ScrollToOffset 本身也可能把 Arrange 推迟到下一帧；再压一次，
            // 保证“尺寸 + 偏移”一起进入本帧，不出现旧位置的一帧。
            scroll.UpdateLayout();
            ReEvaluateNewIconDockAfterViewportChange();
        }

        private void SetZoom(double value, bool preserveAnchor)
        {
            double newZoom = Math.Max(0.05, Math.Min(2.0, value));
            if (Math.Abs(newZoom - zoom) < 0.0001)
            {
                zoomText.Text = ((int)Math.Round(newZoom * 100)) + "%";
                return;
            }
            SettleNewIconDirectionTransitionForViewportChange();
            double oldScale = zoom / dpiScale;
            bool hasAnchor = preserveAnchor && scroll != null && oldScale > 0;
            double anchorCanvasX = 0;
            double anchorCanvasY = 0;
            if (hasAnchor)
            {
                // 锚点是视口左上角，不是视口中心。桌面中心经常是大片壁纸，
                // 从“适应窗口”放大时锚在中心会让用户第一眼看不到任何图标。
                Point oldOrigin = zoomHost.TransformToAncestor(scroll).Transform(new Point(0, 0));
                anchorCanvasX = (0.0 - oldOrigin.X) / oldScale;
                anchorCanvasY = (0.0 - oldOrigin.Y) / oldScale;
            }

            zoom = newZoom;
            double newScale = zoom / dpiScale;
            canvasScale.ScaleX = newScale;
            canvasScale.ScaleY = newScale;
            UpdateZoomHostSize(newScale);
            zoomText.Text = ((int)Math.Round(zoom * 100)) + "%";

            if (hasAnchor)
            {
                scroll.UpdateLayout();
                double viewportWidth = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
                double viewportHeight = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
                double targetX = zoomHost.Width > viewportWidth - 0.5
                    ? anchorCanvasX * newScale
                    : 0.0;
                double targetY = zoomHost.Height > viewportHeight - 0.5
                    ? anchorCanvasY * newScale
                    : 0.0;
                scroll.ScrollToHorizontalOffset(Math.Max(0.0, targetX));
                scroll.ScrollToVerticalOffset(Math.Max(0.0, targetY));
                scroll.UpdateLayout();
            }
            ReEvaluateNewIconDockAfterViewportChange();
        }

        private void ShowScanToast(string message)
        {
            if (scanToast == null || scanToastText == null)
            {
                return;
            }
            scanToastText.Text = message;
            scanToast.Visibility = Visibility.Visible;
            if (scanToastTimer != null)
            {
                scanToastTimer.Stop();
                scanToastTimer.Start();
            }
        }

        private void HideScanToast()
        {
            if (scanToastTimer != null)
            {
                scanToastTimer.Stop();
            }
            if (scanToast != null)
            {
                scanToast.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 和当前桌面重新对比，列出布局里记着但桌面上已经找不到的图标。
        /// 只提示并标记；真正写回布局时在 FinishAsync 里剔除。
        /// </summary>
        private async void ScanDeletedIconsAsync()
        {
            if (scanningDeleted || state.Saving)
            {
                return;
            }
            scanningDeleted = true;
            if (scanDeletedButton != null)
            {
                scanDeletedButton.IsEnabled = false;
            }
            ShowScanToast(AppText.T(DesktopLayoutTextKeys.EditorScanComparing));
            statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorScanning);
            try
            {
                DesktopShellSnapshot current = await controller.CaptureDesktopSnapshotWithoutSavingAsync();
                if (current == null || current.Icons == null)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorScanCannotRead);
                    ShowScanToast(AppText.T(DesktopLayoutTextKeys.EditorScanCannotRead));
                    return;
                }
                HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < current.Icons.Count; index++)
                {
                    DesktopIconPlacement item = current.Icons[index];
                    if (item != null && !string.IsNullOrEmpty(item.Identity))
                    {
                        live.Add(DesktopIdentity.Normalize(item.Identity));
                    }
                }
                scannedMissingIdentities.Clear();
                List<string> missingNames = new List<string>();
                int missing = 0;
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
                        if (live.Contains(identity))
                        {
                            continue;
                        }
                        missing++;
                        scannedMissingIdentities.Add(identity);
                        if (missingNames.Count < 8)
                        {
                            missingNames.Add(string.IsNullOrWhiteSpace(saved.DisplayName)
                                ? AppText.T(DesktopLayoutTextKeys.EditorUnnamedIcon)
                                : saved.DisplayName);
                        }
                    }
                }
                string baseSummary = DesktopLayoutFolderStore.DescribeDisplay(layout)
                    + AppText.F(
                        DesktopLayoutTextKeys.EditorSummaryIconsFormat,
                        snapshot.Icons.Count);
                if (missing > 0)
                {
                    baseSummary += AppText.F(
                        DesktopLayoutTextKeys.EditorSummaryMissingFormat,
                        missing);
                }
                layoutSummaryBase = baseSummary;
                RefreshMissingIconBorders();
                UpdateNewIconCount(false);
                if (missing == 0)
                {
                    HideMissingIconHighlight();
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorScanNoMissing);
                    ShowScanToast(
                        AppText.T(DesktopLayoutTextKeys.EditorScanNoMissingToast));
                }
                else
                {
                    string names = string.Join(
                        AppText.T(DesktopLayoutTextKeys.EditorScanNameSeparator),
                        missingNames.ToArray());
                    if (missing > missingNames.Count)
                    {
                        names += AppText.F(
                            DesktopLayoutTextKeys.EditorScanMoreFormat,
                            missing - missingNames.Count);
                    }
                    string message = AppText.F(
                        DesktopLayoutTextKeys.EditorScanMissingFormat,
                        missing,
                        names);
                    statusText.Text = AppText.T(
                        DesktopLayoutTextKeys.EditorScanCompletePrefix) + message;
                    ShowScanToast(message);
                    ApplyMissingIconHighlight(true);
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    DesktopLayoutTextKeys.EditorScanFailedFormat, exception.Message);
                ShowScanToast(AppText.F(
                    DesktopLayoutTextKeys.EditorScanFailedToastFormat,
                    exception.Message));
            }
            finally
            {
                scanningDeleted = false;
                if (scanDeletedButton != null)
                {
                    scanDeletedButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// 用户没有从“新图标”标签拖进预桌面的图标：保存时按当前桌面网格的空位放，
        /// 不再直接沿用桌面上可能和布局重叠的位置。
        /// </summary>
        /// <summary>
        /// 保存时保留“新图标”的未放置状态：不再把用户拖进新图标的图标自动塞进空格。
        /// X/Y 写哨兵，下次打开编辑页时仍作为新图标卡片出现；恢复布局时跳过这些哨兵，
        /// 不把 -1000000 当成真实桌面坐标。
        /// </summary>
        private void KeepUnplacedNewIconsUnplaced()
        {
            if (unplacedNewItems.Count == 0)
            {
                return;
            }
            foreach (DesktopIconPlacement item in unplacedNewItems)
            {
                if (item == null)
                {
                    continue;
                }
                item.X = -1000000;
                item.Y = -1000000;
            }
        }

        private static bool IsUnplacedPlacement(DesktopIconPlacement item)
        {
            return item != null && (item.X < -999000 || item.Y < -999000);
        }

        private async System.Threading.Tasks.Task FinishAsync()
        {
            if (state.Saving)
            {
                return;
            }
            if (!state.DesktopLoadCompleted)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorStillLoading);
                return;
            }
            state.Saving = true;
            try
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorSavingToLayout);
                // 这里只写布局文件夹，不调用 ApplyDesktopIconSnapshot。
                // 当前可能是笔记本小屏，直接应用会把大屏布局搬错；要应用时由用户在
                // 对应显示器组合下点布局卡片的“立即恢复”。
                await System.Threading.Tasks.Task.Yield();
                if (state.Closed)
                {
                    return;
                }
                snapshot.CapturedUtc = DateTime.UtcNow;
                if (scannedMissingIdentities.Count > 0)
                {
                    snapshot.Icons.RemoveAll(delegate(DesktopIconPlacement item)
                    {
                        return item != null && !string.IsNullOrEmpty(item.Identity)
                            && scannedMissingIdentities.Contains(DesktopIdentity.Normalize(item.Identity));
                    });
                }
                KeepUnplacedNewIconsUnplaced();
                DesktopTopologyLayout saved = await controller.SaveDesktopSnapshotToProfileAsync(
                    snapshot, layout, true, false);
                if (saved == null)
                {
                    string saveFailed = AppText.T(
                        DesktopLayoutTextKeys.EditorSaveFailed);
                    statusText.Text = saveFailed;
                    MessageBox.Show(
                        this,
                        saveFailed,
                        AppText.T(DesktopLayoutTextKeys.EditorDialogTitle),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                state.SavedLayout = true;
                Close();
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    DesktopLayoutTextKeys.EditorSaveFailedDetailFormat,
                    exception.Message);
                MessageBox.Show(
                    this,
                    AppText.F(
                        DesktopLayoutTextKeys.EditorSaveEditedFailedFormat,
                        exception.Message),
                    AppText.T(DesktopLayoutTextKeys.EditorDialogTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                state.Saving = false;
            }
        }
    }
}
