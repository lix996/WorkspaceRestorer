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

        private void StartIconLoading()
        {
            List<DesktopIconPlacement> items = new List<DesktopIconPlacement>();
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                if (snapshot.Icons[index] != null)
                {
                    items.Add(snapshot.Icons[index]);
                }
            }
            StartIconLoadingForItems(items, true);
        }

        private void StartIconLoadingForItems(List<DesktopIconPlacement> items, bool updateStatus)
        {
            List<DesktopIconPlacement> pending = new List<DesktopIconPlacement>();
            for (int index = 0; items != null && index < items.Count; index++)
            {
                DesktopIconPlacement item = items[index];
                if (item != null && imageCache.BeginLoading(item))
                {
                    pending.Add(item);
                }
            }
            if (pending.Count == 0)
            {
                if (updateStatus && snapshot.Icons.Count == 0)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorEmptyIcons);
                }
                return;
            }
            items = pending;
            int itemCount = items.Count;
            Thread thread = new Thread(delegate()
            {
                // 每 8 个图标分批回 UI 线程：以前 68 个图标各发一次 Dispatcher，
                // 每次 Set Source 都会重画 4K 画布，打开编辑页时会明显卡顿。
                List<DesktopIconPlacement> batchItems = new List<DesktopIconPlacement>();
                List<BitmapSource> batchIcons = new List<BitmapSource>();
                for (int index = 0; index < items.Count; index++)
                {
                    if (state.Closed)
                    {
                        return;
                    }
                    DesktopIconPlacement item = items[index];
                    BitmapSource icon = DesktopIconImageLoader.Load(item);
                    if (state.Closed)
                    {
                        return;
                    }
                    batchItems.Add(item);
                    batchIcons.Add(icon);
                    if (batchItems.Count >= 8)
                    {
                        DispatchIconBatch(batchItems.ToArray(), batchIcons.ToArray());
                        batchItems = new List<DesktopIconPlacement>();
                        batchIcons = new List<BitmapSource>();
                    }
                }
                if (batchItems.Count > 0)
                {
                    DispatchIconBatch(batchItems.ToArray(), batchIcons.ToArray());
                }
                if (updateStatus)
                {
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (state.Closed || state.UserMoved || newIconDragging)
                        {
                            return;
                        }
                        statusText.Text = AppText.F(
                            DesktopLayoutTextKeys.EditorIconsLoadedFormat,
                            itemCount);
                    }));
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void DispatchIconBatch(DesktopIconPlacement[] batchItems, BitmapSource[] batchIcons)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (state.Closed)
                {
                    return;
                }
                for (int index = 0; index < batchItems.Length; index++)
                {
                    ApplyIcon(batchItems[index], batchIcons[index]);
                }
            }));
        }

        private void ApplyIcon(DesktopIconPlacement item, BitmapSource icon)
        {
            if (state.Closed)
            {
                return;
            }
            if (icon != null)
            {
                imageCache.SetNewIconSource(item, icon);
            }
            Image image;
            bool hasTile = imageCache.TryGetCanvasImage(item, out image);
            if (hasTile && icon != null)
            {
                image.Source = icon;
            }
            Border skeleton;
            if (hasTile && iconSkeletons.TryGetValue(item, out skeleton))
            {
                skeleton.Visibility = Visibility.Collapsed;
            }
            // 新图标一开始没有画布 tile，但标签卡片仍然要更新成真实图标。
            Image trayImage;
            if (icon != null && imageCache.TryGetTrayImage(item, out trayImage))
            {
                trayImage.Source = icon;
            }
        }

        private static BitmapImage LoadScreenshot(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception exception)
            {
                AppLog.Write("[布局编辑] 读取桌面截图失败", exception);
                return null;
            }
        }

    }
}
