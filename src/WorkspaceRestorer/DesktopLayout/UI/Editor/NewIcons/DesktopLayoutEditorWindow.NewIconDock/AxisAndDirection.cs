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
        private void UpdateNewIconAxisSides(
            double centerX, double centerY, double axisX, double axisY)
        {
            if (!newIconAxisSidesInitialized)
            {
                newIconAxisLeftOfCenter = centerX <= axisX;
                // 屏幕坐标 Y 向下：中心 Y 比轴线小，就是笛卡尔坐标的 +y（上方）。
                newIconAxisAboveCenter = centerY <= axisY;
                newIconAxisSidesInitialized = true;
                return;
            }
            // 轴两侧各 24 DIP 回滞：在带内保持原侧，跨过去才换。
            const double AxisHysteresis = 24.0;
            if (newIconAxisLeftOfCenter)
            {
                if (centerX > axisX + AxisHysteresis)
                {
                    newIconAxisLeftOfCenter = false;
                }
            }
            else if (centerX < axisX - AxisHysteresis)
            {
                newIconAxisLeftOfCenter = true;
            }
            if (newIconAxisAboveCenter)
            {
                if (centerY > axisY + AxisHysteresis)
                {
                    newIconAxisAboveCenter = false;
                }
            }
            else if (centerY < axisY - AxisHysteresis)
            {
                newIconAxisAboveCenter = true;
            }
        }

        private NewIconDockDirection ResolveNewIconDockDirection()
        {
            return ResolveNewIconDockDirection(
                GetNewIconTagWidth(),
                GetNewIconTagHeight());
        }

        /// <summary>
        /// 拖动期间用缓存好的标签宽高解析方向，避免每 16ms 调 GetNewIconTagWidth/Height
        /// 里的 Measure。其余边界/十字轴规则与无参版本完全一致。
        /// </summary>
        private NewIconDockDirection ResolveNewIconDockDirection(
            double tagWidth,
            double tagHeight)
        {
            if (pageOverlay == null || pageOverlay.ActualWidth <= 1.0
                || pageOverlay.ActualHeight <= 1.0)
            {
                activeNewIconBoundary = NewIconBoundary.None;
                return newIconDockDirection;
            }
            double pageWidth = pageOverlay.ActualWidth;
            double pageHeight = pageOverlay.ActualHeight;
            double axisX;
            double axisY;
            bool haveAxis;
            if (newIconTagDragFastActive && newIconTagDragAxisValid)
            {
                // 拖动开始缓存一次；4K/150% 下不再每帧做 PointToScreen/PointFromScreen。
                axisX = newIconTagDragAxisX;
                axisY = newIconTagDragAxisY;
                haveAxis = true;
            }
            else
            {
                haveAxis = TryGetDesktopPreviewCenterInPage(out axisX, out axisY);
            }
            if (!haveAxis)
            {
                // 窗口还没排版或坐标变换失败时不要猜方向，保持当前方向，
                // 避免启动/最小化时先闪到错误的一侧。
                activeNewIconBoundary = NewIconBoundary.None;
                return newIconDockDirection;
            }
            tagWidth = Math.Max(1.0, tagWidth);
            tagHeight = Math.Max(1.0, tagHeight);
            double centerX = newIconTagX + tagWidth / 2.0;
            double centerY = newIconTagY + tagHeight / 2.0;
            if (!IsFinite(centerX) || !IsFinite(centerY))
            {
                activeNewIconBoundary = NewIconBoundary.None;
                return newIconDockDirection;
            }
            UpdateNewIconAxisSides(centerX, centerY, axisX, axisY);

            const double Edge = 12.0;
            // 已经贴住某条边时，往内退 16px 才认为离开；避免鼠标轻微抖动在
            // “碰到/没碰到”之间反复横跳，造成边界线闪、方向闪。
            const double ReleaseEdge = Edge + 16.0;
            bool nearTop = newIconTagY <= (activeNewIconBoundary == NewIconBoundary.Top
                ? ReleaseEdge : Edge);
            bool nearBottom = newIconTagY + tagHeight >= pageHeight
                - (activeNewIconBoundary == NewIconBoundary.Bottom ? ReleaseEdge : Edge);
            bool nearLeft = newIconTagX <= (activeNewIconBoundary == NewIconBoundary.Left
                ? ReleaseEdge : Edge);
            bool nearRight = newIconTagX + tagWidth >= pageWidth
                - (activeNewIconBoundary == NewIconBoundary.Right ? ReleaseEdge : Edge);
            // 边界只决定横/竖，具体朝哪边由隐藏十字轴对应的轴决定。
            // 角上仍沿用上/下优先；12/28 的边界回滞会防住逐帧抖动。
            NewIconBoundary hit = NewIconBoundary.None;
            if (nearTop)
            {
                hit = NewIconBoundary.Top;
            }
            else if (nearBottom)
            {
                hit = NewIconBoundary.Bottom;
            }
            else if (nearLeft)
            {
                hit = NewIconBoundary.Left;
            }
            else if (nearRight)
            {
                hit = NewIconBoundary.Right;
            }
            if (hit != NewIconBoundary.None)
            {
                activeNewIconBoundary = hit;
                if (hit == NewIconBoundary.Top || hit == NewIconBoundary.Bottom)
                {
                    return newIconAxisLeftOfCenter
                        ? NewIconDockDirection.Right
                        : NewIconDockDirection.Left;
                }
                return newIconAxisAboveCenter
                    ? NewIconDockDirection.Down
                    : NewIconDockDirection.Up;
            }
            activeNewIconBoundary = NewIconBoundary.None;
            // 没碰边界时保持上一次的横/竖模式，只让轴继续决定左右或上下。
            if (newIconDockDirection == NewIconDockDirection.Left
                || newIconDockDirection == NewIconDockDirection.Right)
            {
                return newIconAxisLeftOfCenter
                    ? NewIconDockDirection.Right
                    : NewIconDockDirection.Left;
            }
            return newIconAxisAboveCenter
                ? NewIconDockDirection.Down
                : NewIconDockDirection.Up;
        }

        private string NewIconDirectionName()
        {
            switch (newIconDockDirection)
            {
                case NewIconDockDirection.Left:
                    return AppText.T(DesktopLayoutTextKeys.NewIconDirectionLeft);
                case NewIconDockDirection.Down:
                    return AppText.T(DesktopLayoutTextKeys.NewIconDirectionDown);
                case NewIconDockDirection.Up:
                    return AppText.T(DesktopLayoutTextKeys.NewIconDirectionUp);
                default:
                    return AppText.T(DesktopLayoutTextKeys.NewIconDirectionRight);
            }
        }

        private void UpdateNewIconChevronGlyph()
        {
            if (newIconChevron != null)
            {
                newIconChevron.Text = NewIconChevronGlyph();
            }
        }

        private string NewIconChevronGlyph()
        {
            bool expanded = newIconDockContent != null
                && newIconDockContent.Visibility == Visibility.Visible;
            switch (newIconDockDirection)
            {
                case NewIconDockDirection.Left:
                    // 收起时箭头朝右、对着“新图标”三个字；展开时朝左。
                    return expanded ? "\uE76B" : "\uE76C";
                case NewIconDockDirection.Down:
                    return expanded ? "\uE70D" : "\uE70E";
                case NewIconDockDirection.Up:
                    return expanded ? "\uE70E" : "\uE70D";
                default:
                    return expanded ? "\uE76C" : "\uE76B";
            }
        }

    }
}
