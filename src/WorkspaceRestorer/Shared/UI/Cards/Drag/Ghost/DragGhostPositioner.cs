using System;
using System.Windows;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共】拖动浮动方框的定位（纯几何，不含任何业务状态）。
    ///
    /// 口径是用户 2026-10-16 定稿的：**X 固定在来源行左边缘，只有 Y 跟手**，
    /// 宽度保持行宽；指针靠近窗口底边时方框往上夹住，不跑出窗口。
    /// 「启动与恢复」的 `ItemDragDrop.PositionDragGhost` 与公共引擎的
    /// `DragMoveSession.PositionGhost` 原来是两份同样的算术，现在都调用这里。
    /// </summary>
    internal static class DragGhostPositioner
    {
        internal static void Position(
            Window window,
            Canvas layer,
            Border ghost,
            double anchorX,
            double ghostWidth,
            Point pointerInWindow)
        {
            if (window == null || layer == null || ghost == null)
            {
                return;
            }

            Point point;
            Point origin;
            Point corner;
            try
            {
                point = window.TranslatePoint(pointerInWindow, layer);
                origin = window.TranslatePoint(new Point(0.0, 0.0), layer);
                corner = window.TranslatePoint(
                    new Point(window.ActualWidth, window.ActualHeight), layer);
            }
            catch
            {
                return;
            }

            double width = ghostWidth > 0.0 ? ghostWidth : 320.0;
            double ghostHeight = ghost.ActualHeight > 0.0 ? ghost.ActualHeight : 40.0;

            // 方框 X = 来源行左边缘（anchorX）；没有锚点时才退回“指针右侧 16 像素”。
            double maxLeft = corner.X - width - 4.0;
            if (maxLeft < origin.X + 4.0)
            {
                maxLeft = origin.X + 4.0;
            }
            double left = double.IsNaN(anchorX) ? point.X + 16.0 : anchorX;
            if (left > maxLeft)
            {
                left = maxLeft;
            }
            if (left < origin.X + 4.0)
            {
                left = origin.X + 4.0;
            }
            double top = Math.Min(
                Math.Max(origin.Y + 8.0, point.Y + 18.0),
                Math.Max(origin.Y + 8.0, corner.Y - ghostHeight - 8.0));

            Canvas.SetLeft(ghost, left);
            Canvas.SetTop(ghost, top);
        }
    }
}
