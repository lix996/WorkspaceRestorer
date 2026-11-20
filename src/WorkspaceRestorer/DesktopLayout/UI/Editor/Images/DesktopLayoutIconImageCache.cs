using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【编辑页图标图片缓存】画布 tile、新图标卡片、拖动浮卡共用的唯一图片来源。
    ///
    /// 以前 `iconImages` / `newIconSources` / `trayImages` 三份缓存由不同路径各自读写：
    /// 新图标卡片先建出来、图片后台还没有加载完时，第一次拖新图标就会拿到空 Source，
    /// 浮卡只剩文件名；等后台加载完成后第二次拖就正常。这个类把三条路径合流：
    ///   · 任何路径需要图片，都走 <see cref="EnsureSource"/>；
    ///   · 缓存里没有就同步补加载一次，并同时回填画布/卡片；
    ///   · loading 集合也集中管理，避免重复加载或漏加载。
    /// </summary>
    internal sealed class DesktopLayoutIconImageCache
    {
        private readonly Dictionary<DesktopIconPlacement, Image> canvasImages =
            new Dictionary<DesktopIconPlacement, Image>();
        private readonly Dictionary<DesktopIconPlacement, Image> trayImages =
            new Dictionary<DesktopIconPlacement, Image>();
        private readonly Dictionary<DesktopIconPlacement, BitmapSource> newIconSources =
            new Dictionary<DesktopIconPlacement, BitmapSource>();
        private readonly HashSet<DesktopIconPlacement> loading =
            new HashSet<DesktopIconPlacement>();

        public bool TryGetCanvasImage(DesktopIconPlacement item, out Image image)
        {
            return canvasImages.TryGetValue(item, out image);
        }

        public void SetCanvasImage(DesktopIconPlacement item, Image image)
        {
            if (item != null && image != null)
            {
                canvasImages[item] = image;
            }
        }

        public void RemoveCanvasImage(DesktopIconPlacement item)
        {
            if (item != null)
            {
                canvasImages.Remove(item);
            }
        }

        public bool TryGetTrayImage(DesktopIconPlacement item, out Image image)
        {
            return trayImages.TryGetValue(item, out image);
        }

        public void SetTrayImage(DesktopIconPlacement item, Image image)
        {
            if (item != null && image != null)
            {
                trayImages[item] = image;
            }
        }

        public void RemoveTrayImage(DesktopIconPlacement item)
        {
            if (item != null)
            {
                trayImages.Remove(item);
            }
        }

        public void ClearTrayImages()
        {
            trayImages.Clear();
        }

        public bool TryGetNewIconSource(DesktopIconPlacement item, out BitmapSource source)
        {
            if (item != null && newIconSources.TryGetValue(item, out source) && source != null)
            {
                return true;
            }
            source = null;
            return false;
        }

        public bool HasNewIconSource(DesktopIconPlacement item)
        {
            return item != null && newIconSources.ContainsKey(item);
        }

        public void SetNewIconSource(DesktopIconPlacement item, BitmapSource source)
        {
            if (item == null || source == null)
            {
                return;
            }
            newIconSources[item] = source;
            Image tray;
            if (trayImages.TryGetValue(item, out tray) && tray != null && tray.Source == null)
            {
                tray.Source = source;
            }
            Image canvas;
            if (canvasImages.TryGetValue(item, out canvas) && canvas != null && canvas.Source == null)
            {
                canvas.Source = source;
            }
        }

        public void RemoveNewIconSource(DesktopIconPlacement item)
        {
            if (item != null)
            {
                newIconSources.Remove(item);
            }
        }

        public bool BeginLoading(DesktopIconPlacement item)
        {
            return item != null && loading.Add(item);
        }

        public void EndLoading(DesktopIconPlacement item)
        {
            if (item != null)
            {
                loading.Remove(item);
            }
        }

        public void RemoveItem(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return;
            }
            canvasImages.Remove(item);
            trayImages.Remove(item);
            newIconSources.Remove(item);
            loading.Remove(item);
        }

        /// <summary>
        /// 取新图标图片：先查缓存，缓存没有就同步补一次。
        /// 这是“第一次拖新图标没有图标”的统一收口。
        /// </summary>
        public BitmapSource EnsureSource(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return null;
            }
            BitmapSource source;
            if (TryGetNewIconSource(item, out source))
            {
                // 缓存里已有图片，也要回填当前这一批控件：
                // 撤销/过滤/重建卡片后，旧的 newIconSources 还在，但 trayImages 可能是新建的空 Image。
                SetNewIconSource(item, source);
                return source;
            }
            Image image;
            if (canvasImages.TryGetValue(item, out image) && image != null)
            {
                source = image.Source as BitmapSource;
                if (source != null)
                {
                    SetNewIconSource(item, source);
                    return source;
                }
            }
            if (trayImages.TryGetValue(item, out image) && image != null)
            {
                source = image.Source as BitmapSource;
                if (source != null)
                {
                    SetNewIconSource(item, source);
                    return source;
                }
            }
            try
            {
                source = DesktopIconImageLoader.Load(item);
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 起拖时补读新图标图片失败：" + (item.DisplayName ?? "图标"), exception);
                source = null;
            }
            if (source != null)
            {
                SetNewIconSource(item, source);
            }
            return source;
        }
    }
}
