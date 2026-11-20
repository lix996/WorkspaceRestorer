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
    internal sealed partial class DesktopLayoutEditorWindow
    {
        private double GetPageOverlayWidth()
        {
            if (pageOverlay != null && pageOverlay.ActualWidth > 1.0)
            {
                return pageOverlay.ActualWidth;
            }
            return ActualWidth > 1.0 ? ActualWidth : 1200.0;
        }

        private double GetPageOverlayHeight()
        {
            if (pageOverlay != null && pageOverlay.ActualHeight > 1.0)
            {
                return pageOverlay.ActualHeight;
            }
            return ActualHeight > 1.0 ? ActualHeight : 800.0;
        }

        /// <summary>
        /// 编辑页客户区。标签和新图标卡片按这个矩形夹取；
        /// 画布图标浮卡不按整页，按下面的可见预桌面夹取。
        /// </summary>
        private Rect GetPageOverlayBounds()
        {
            return new Rect(0.0, 0.0, GetPageOverlayWidth(), GetPageOverlayHeight());
        }

        /// <summary>
        /// 当前可见的预桌面矩形（窗口坐标）。
        /// 预桌面可能被缩放或滚动，只取画布和 ScrollViewer 视口的交集；
        /// 拿不到有效矩形时退回编辑页，至少不会把图标浮卡放到窗口外。
        /// </summary>
        private Rect GetVisibleDesktopCanvasBounds()
        {
            if (desktopCanvas == null || scroll == null)
            {
                return GetPageOverlayBounds();
            }
            try
            {
                Point topLeft = desktopCanvas.TransformToAncestor(this)
                    .Transform(new Point(0.0, 0.0));
                Point bottomRight = desktopCanvas.TransformToAncestor(this)
                    .Transform(new Point(desktopCanvas.ActualWidth, desktopCanvas.ActualHeight));
                Rect canvasRect = new Rect(topLeft, bottomRight);
                if (canvasRect.Width <= 1.0 || canvasRect.Height <= 1.0)
                {
                    return GetPageOverlayBounds();
                }
                Point scrollTopLeft = scroll.TransformToAncestor(this)
                    .Transform(new Point(0.0, 0.0));
                Rect scrollRect = new Rect(
                    scrollTopLeft,
                    new Size(scroll.ActualWidth, scroll.ActualHeight));
                canvasRect.Intersect(scrollRect);
                if (canvasRect.IsEmpty || canvasRect.Width <= 1.0 || canvasRect.Height <= 1.0)
                {
                    return GetPageOverlayBounds();
                }
                return canvasRect;
            }
            catch
            {
                return GetPageOverlayBounds();
            }
        }

        private double GetNewIconTagWidth()
        {
            if (newIconTag == null)
            {
                return 100.0;
            }
            newIconTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(1.0, newIconTag.DesiredSize.Width);
        }

        private double GetNewIconTagHeight()
        {
            if (newIconTag == null)
            {
                return 44.0;
            }
            newIconTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Max(1.0, newIconTag.DesiredSize.Height);
        }

        private Point ToPageOverlay(Point windowPoint)
        {
            Point overlayOrigin = pageOverlay.TransformToAncestor(this).Transform(new Point(0.0, 0.0));
            return new Point(windowPoint.X - overlayOrigin.X, windowPoint.Y - overlayOrigin.Y);
        }

        private bool IsInsideWindowClient(Point windowPoint)
        {
            return windowPoint.X >= 0.0 && windowPoint.Y >= 0.0
                && windowPoint.X <= ActualWidth && windowPoint.Y <= ActualHeight;
        }

        /// <summary>
        /// 鼠标还在窗口内时，WPF 自己的坐标最轻、也最跟手：
        /// 不 P/Invoke、不做 PointFromScreen。
        /// 只有真的出了窗口，或捕获丢失后 WPF 坐标不可信，才读物理光标。
        /// </summary>
        private bool CanUseWpfMousePosition(Point windowPoint)
        {
            return !dragPointerPhysicalFallback
                && IsMouseOver
                && IsInsideWindowClient(windowPoint);
        }

        /// <summary>
        /// 捕获丢失后先切物理光标；一旦鼠标回到窗口内，就恢复 WPF 坐标，
        /// 否则从窗口外回来后的整段拖动都会继续走 PointFromScreen。
        /// </summary>
        private Point ReadDragWindowPoint()
        {
            Point windowPoint = Mouse.GetPosition(this);
            if (dragPointerPhysicalFallback
                && IsMouseOver
                && IsInsideWindowClient(windowPoint))
            {
                dragPointerPhysicalFallback = false;
            }
            return windowPoint;
        }

        private Point GetPhysicalMousePositionInPage()
        {
            NativeMethods.NativePoint native;
            if (NativeMethods.GetCursorPos(out native))
            {
                try
                {
                    return pageOverlay.PointFromScreen(new Point(native.X, native.Y));
                }
                catch
                {
                }
            }
            return Mouse.GetPosition(pageOverlay);
        }

        /// <summary>
        /// 拖动时读鼠标位置：窗口内走 WPF，窗口外才用 GetCursorPos。
        /// 页面外但窗口内仍用 WPF 坐标，不切物理光标——这是旧版跟手的关键。
        /// </summary>
        private Point GetMousePositionInPage()
        {
            Point windowPoint = ReadDragWindowPoint();
            if (CanUseWpfMousePosition(windowPoint))
            {
                if (newIconTagDragPageOriginValid)
                {
                    return new Point(
                        windowPoint.X - newIconTagDragPageOrigin.X,
                        windowPoint.Y - newIconTagDragPageOrigin.Y);
                }
                return Mouse.GetPosition(pageOverlay);
            }
            return GetPhysicalMousePositionInPage();
        }

        /// <summary>鼠标位置换成窗口内容坐标（整页拖动、浮卡拖动用）。</summary>
        private Point GetMousePositionInWindow()
        {
            Point windowPoint = ReadDragWindowPoint();
            if (CanUseWpfMousePosition(windowPoint))
            {
                return windowPoint;
            }
            NativeMethods.NativePoint native;
            if (NativeMethods.GetCursorPos(out native))
            {
                try
                {
                    return PointFromScreen(new Point(native.X, native.Y));
                }
                catch
                {
                }
            }
            return windowPoint;
        }

        /// <summary>鼠标位置换成预桌面坐标（松手落点、吸附网格用）。</summary>
        private Point GetMousePositionInDesktopCanvas()
        {
            Point windowPoint = ReadDragWindowPoint();
            if (CanUseWpfMousePosition(windowPoint))
            {
                return Mouse.GetPosition(desktopCanvas);
            }
            NativeMethods.NativePoint native;
            if (NativeMethods.GetCursorPos(out native))
            {
                try
                {
                    return desktopCanvas.PointFromScreen(new Point(native.X, native.Y));
                }
                catch
                {
                }
            }
            return Mouse.GetPosition(desktopCanvas);
        }

        /// <summary>
        /// 拖动开始时缓存页面矩形、页面原点、隐藏十字轴心。
        /// 拖动期间不再做 TransformToAncestor；窗口缩放/滚动不属于标签拖动场景。
        /// </summary>
        private void CacheNewIconTagDragFrame()
        {
            newIconTagDragPageBounds = GetPageOverlayBounds();
            newIconTagDragPageOriginValid = false;
            try
            {
                if (pageOverlay != null)
                {
                    newIconTagDragPageOrigin = pageOverlay.TransformToAncestor(this)
                        .Transform(new Point(0.0, 0.0));
                    newIconTagDragPageOriginValid = true;
                }
            }
            catch
            {
                newIconTagDragPageOrigin = new Point(0.0, 0.0);
            }
            newIconTagDragAxisValid = TryGetDesktopPreviewCenterInPage(
                out newIconTagDragAxisX, out newIconTagDragAxisY);
        }

        private bool TryGetDesktopPreviewCenterInPage(out double axisX, out double axisY)
        {
            axisX = 0.0;
            axisY = 0.0;
            if (desktopCanvas == null || pageOverlay == null
                || desktopCanvas.ActualWidth <= 1.0 || desktopCanvas.ActualHeight <= 1.0
                || pageOverlay.ActualWidth <= 1.0 || pageOverlay.ActualHeight <= 1.0)
            {
                return false;
            }
            try
            {
                // 轴放在预桌面画布中心；先换到屏幕，再换回编辑页 DIP。
                // 这样轴、标签中心、边界都在 pageOverlay 的同一套坐标里。
                Point screenPoint = desktopCanvas.PointToScreen(new Point(
                    desktopCanvas.ActualWidth / 2.0,
                    desktopCanvas.ActualHeight / 2.0));
                Point overlayPoint = pageOverlay.PointFromScreen(screenPoint);
                if (!IsFinite(overlayPoint.X) || !IsFinite(overlayPoint.Y))
                {
                    return false;
                }
                axisX = overlayPoint.X;
                axisY = overlayPoint.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private RenderTargetBitmap RenderNewIconPanelBitmap(double width, double height)
        {
            // 直接 Render 一个 Canvas 子元素时，Canvas.Left/Top 的布局偏移
            // 不会自动扣掉，之前会得到整张透明图；改成先把它临时排到原点，
            // 渲染完再恢复。VisualBrush 在横排切竖排的同一帧里会只拍到
            // 左边一条，所以这里不用 VisualBrush。
            double oldLeft = Canvas.GetLeft(newIconDockContent);
            double oldTop = Canvas.GetTop(newIconDockContent);
            if (!IsFinite(oldLeft))
            {
                oldLeft = 0.0;
            }
            if (!IsFinite(oldTop))
            {
                oldTop = 0.0;
            }
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0.1 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0.1 ? dpi.DpiScaleY : 1.0;
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * scaleX)),
                Math.Max(1, (int)Math.Ceiling(height * scaleY)),
                96.0 * scaleX,
                96.0 * scaleY,
                PixelFormats.Pbgra32);
            try
            {
                Canvas.SetLeft(newIconDockContent, 0.0);
                Canvas.SetTop(newIconDockContent, 0.0);
                newIconDockContent.Measure(new Size(width, height));
                newIconDockContent.Arrange(new Rect(0.0, 0.0, width, height));
                newIconDockContent.UpdateLayout();
                bitmap.Render(newIconDockContent);
            }
            finally
            {
                Canvas.SetLeft(newIconDockContent, oldLeft);
                Canvas.SetTop(newIconDockContent, oldTop);
                newIconDockContent.Measure(new Size(width, height));
                newIconDockContent.Arrange(new Rect(oldLeft, oldTop, width, height));
                newIconDockContent.UpdateLayout();
            }
            bitmap.Freeze();
            return bitmap;
        }

    }
}
