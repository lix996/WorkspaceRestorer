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

        private static void ResolveGridOrigin(
            DesktopShellSnapshot snapshot,
            DesktopRect bounds,
            double cellWidth,
            double cellHeight,
            out double originX,
            out double originY)
        {
            originX = 0.0;
            originY = 0.0;
            if (snapshot == null || snapshot.Icons == null
                || snapshot.Icons.Count == 0 || bounds == null
                || cellWidth < 1.0 || cellHeight < 1.0)
            {
                return;
            }
            Dictionary<int, int> xCounts = new Dictionary<int, int>();
            Dictionary<int, int> yCounts = new Dictionary<int, int>();
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item == null || item.X < -999000 || item.Y < -999000)
                {
                    continue;
                }
                int remainderX = (int)Math.Round(
                    PositiveModulo(item.X - bounds.Left, cellWidth));
                int remainderY = (int)Math.Round(
                    PositiveModulo(item.Y - bounds.Top, cellHeight));
                if (remainderX >= cellWidth)
                {
                    remainderX = 0;
                }
                if (remainderY >= cellHeight)
                {
                    remainderY = 0;
                }
                int count;
                xCounts.TryGetValue(remainderX, out count);
                xCounts[remainderX] = count + 1;
                yCounts.TryGetValue(remainderY, out count);
                yCounts[remainderY] = count + 1;
            }
            originX = DominantRemainder(xCounts, cellWidth);
            originY = DominantRemainder(yCounts, cellHeight);
        }

        private static double DominantRemainder(
            Dictionary<int, int> counts,
            double cellSize)
        {
            int bestRemainder = 0;
            int bestCount = -1;
            foreach (KeyValuePair<int, int> pair in counts)
            {
                if (pair.Value > bestCount
                    || (pair.Value == bestCount && pair.Key < bestRemainder))
                {
                    bestRemainder = pair.Key;
                    bestCount = pair.Value;
                }
            }
            if (bestRemainder < 0 || bestRemainder >= cellSize)
            {
                bestRemainder = 0;
            }
            return bestRemainder;
        }

        private static DesktopRect ResolveVirtualBounds(DisplayTopology topology)
        {
            DesktopRect bounds = topology.VirtualBounds;
            if (bounds != null && bounds.Width > 0 && bounds.Height > 0)
            {
                return bounds;
            }
            if (topology.Monitors != null && topology.Monitors.Count > 0)
            {
                int left = int.MaxValue;
                int top = int.MaxValue;
                int right = int.MinValue;
                int bottom = int.MinValue;
                for (int index = 0; index < topology.Monitors.Count; index++)
                {
                    DisplayMonitor monitor = topology.Monitors[index];
                    if (monitor == null || monitor.Bounds == null || monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0)
                    {
                        continue;
                    }
                    left = Math.Min(left, monitor.Bounds.Left);
                    top = Math.Min(top, monitor.Bounds.Top);
                    right = Math.Max(right, monitor.Bounds.Right);
                    bottom = Math.Max(bottom, monitor.Bounds.Bottom);
                }
                if (right > left && bottom > top)
                {
                    return new DesktopRect(left, top, right - left, bottom - top);
                }
            }
            return new DesktopRect(0, 0, 1920, 1080);
        }

        private void AddBackground(DisplayTopology topology)
        {
            int wallpaperStyle;
            BitmapImage wallpaper = LoadWallpaper(out wallpaperStyle);
            bool hasWallpaper = false;
            if (wallpaper != null)
            {
                if (wallpaperStyle == 22 || topology.Monitors == null || topology.Monitors.Count == 0)
                {
                    AddWallpaperRect(wallpaper, wallpaperStyle, 0, 0, canvasWidth, canvasHeight);
                }
                else
                {
                    for (int index = 0; index < topology.Monitors.Count; index++)
                    {
                        DisplayMonitor monitor = topology.Monitors[index];
                        if (monitor == null || monitor.Bounds == null
                            || monitor.Bounds.Width <= 0 || monitor.Bounds.Height <= 0)
                        {
                            continue;
                        }
                        AddWallpaperRect(
                            wallpaper,
                            wallpaperStyle,
                            monitor.Bounds.Left - virtualBounds.Left,
                            monitor.Bounds.Top - virtualBounds.Top,
                            monitor.Bounds.Width,
                            monitor.Bounds.Height);
                    }
                }
                hasWallpaper = true;
            }

            if (!hasWallpaper)
            {
                BitmapImage fallback = LoadScreenshot(screenshotPath);
                BitmapSource blurred = fallback == null ? null : CreateBlurredScreenshot(fallback);
                if (blurred != null)
                {
                    Image background = new Image
                    {
                        Source = blurred,
                        Width = canvasWidth,
                        Height = canvasHeight,
                        Stretch = Stretch.Fill,
                        IsHitTestVisible = false
                    };
                    RenderOptions.SetBitmapScalingMode(background, BitmapScalingMode.Linear);
                    Canvas.SetLeft(background, 0);
                    Canvas.SetTop(background, 0);
                    desktopCanvas.Children.Add(background);
                }
                else
                {
                    Rectangle gradient = new Rectangle
                    {
                        Width = canvasWidth,
                        Height = canvasHeight,
                        IsHitTestVisible = false,
                        Fill = new LinearGradientBrush(
                            Color.FromRgb(30, 58, 95),
                            Color.FromRgb(15, 23, 42),
                            90.0)
                    };
                    Canvas.SetLeft(gradient, 0);
                    Canvas.SetTop(gradient, 0);
                    desktopCanvas.Children.Add(gradient);
                }
            }

            Rectangle shade = new Rectangle
            {
                Width = canvasWidth,
                Height = canvasHeight,
                IsHitTestVisible = false,
                Fill = hasWallpaper ? EditorPalette.WallpaperMaskLight : EditorPalette.WallpaperMaskDark
            };
            Canvas.SetLeft(shade, 0);
            Canvas.SetTop(shade, 0);
            desktopCanvas.Children.Add(shade);
        }

        private void AddWallpaperRect(BitmapImage wallpaper, int style, double left, double top, double width, double height)
        {
            if (wallpaper == null || width <= 0 || height <= 0)
            {
                return;
            }
            Border clip = new Border
            {
                Width = width,
                Height = height,
                Background = EditorPalette.CanvasBackground,
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            Image image = new Image
            {
                Source = wallpaper,
                IsHitTestVisible = false
            };
            if (style == 6)
            {
                image.Stretch = Stretch.Uniform;
                image.Width = width;
                image.Height = height;
                image.HorizontalAlignment = HorizontalAlignment.Center;
                image.VerticalAlignment = VerticalAlignment.Center;
            }
            else if (style == 2)
            {
                image.Stretch = Stretch.Fill;
                image.Width = width;
                image.Height = height;
            }
            else if (style == 0)
            {
                image.Stretch = Stretch.None;
                image.HorizontalAlignment = HorizontalAlignment.Center;
                image.VerticalAlignment = VerticalAlignment.Center;
            }
            else
            {
                image.Stretch = Stretch.UniformToFill;
                image.Width = width;
                image.Height = height;
            }
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
            clip.Child = image;
            Canvas.SetLeft(clip, left);
            Canvas.SetTop(clip, top);
            desktopCanvas.Children.Add(clip);
        }

        private static BitmapImage LoadWallpaper(out int style)
        {
            style = 10;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Control Panel\\Desktop"))
                {
                    if (key == null)
                    {
                        return null;
                    }
                    object styleValue = key.GetValue("WallpaperStyle");
                    int parsedStyle;
                    if (styleValue != null && int.TryParse(Convert.ToString(styleValue), out parsedStyle))
                    {
                        style = parsedStyle;
                    }
                    string path = key.GetValue("WallPaper") as string;
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
            }
            catch (Exception exception)
            {
                AppLog.Write("[布局编辑] 读取壁纸失败", exception);
                return null;
            }
        }

        private static BitmapSource CreateBlurredScreenshot(BitmapSource source)
        {
            try
            {
                if (source == null || source.PixelWidth < 32 || source.PixelHeight < 32)
                {
                    return null;
                }
                int targetWidth = Math.Max(32, source.PixelWidth / 24);
                double scale = targetWidth / (double)source.PixelWidth;
                TransformedBitmap small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                small.Freeze();
                return small;
            }
            catch
            {
                return null;
            }
        }

    }
}
