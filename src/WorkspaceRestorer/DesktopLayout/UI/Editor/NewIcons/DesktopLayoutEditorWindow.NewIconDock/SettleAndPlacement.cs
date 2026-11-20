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
        private void UpdateNewIconDirectionGhostPosition()
        {
            if (newIconDirectionGhost != null)
            {
                Canvas.SetLeft(newIconDirectionGhost,
                    newIconTagX + newIconDirectionGhostOffsetX);
                Canvas.SetTop(newIconDirectionGhost,
                    newIconTagY + newIconDirectionGhostOffsetY);
            }
            if (newIconDirectionNewGhost != null)
            {
                Canvas.SetLeft(newIconDirectionNewGhost,
                    newIconTagX + newIconDirectionNewGhostOffsetX);
                Canvas.SetTop(newIconDirectionNewGhost,
                    newIconTagY + newIconDirectionNewGhostOffsetY);
            }
        }

        private void EnsureNewIconPanelSettled()
        {
            if (newIconDock == null || newIconDockContent == null
                || newIconDirectionTransitionActive
                || newIconDirectionTransitionPending)
            {
                return;
            }
            bool removedGhost = false;
            for (int index = newIconDock.Children.Count - 1; index >= 0; index--)
            {
                Image image = newIconDock.Children[index] as Image;
                if (image == null)
                {
                    continue;
                }
                image.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDock.Children.RemoveAt(index);
                image.Source = null;
                removedGhost = true;
            }
            if (removedGhost)
            {
                newIconDirectionGhost = null;
                newIconDirectionNewGhost = null;
                newIconDirectionGhostShift = null;
                newIconDirectionGhostClip = null;
                newIconDirectionContentShift = null;
                newIconDirectionContentClip = null;
                newIconDirectionGhostIsComposite = false;
                newIconDirectionTransitionCompact = false;
                newIconDirectionTransitionRetarget = false;
            }

            bool stale = newIconDockContent.Clip != null
                || newIconDockContent.RenderTransform != null
                || newIconDockContent.CacheMode != null;
            if (removedGhost || stale)
            {
                newIconDockContent.Clip = null;
                newIconDockContent.RenderTransform = null;
                newIconDockContent.CacheMode = null;
                newIconDockContent.IsHitTestVisible = true;
                newIconDockContent.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDockContent.Opacity = 1.0;
            }
            if (newIconDockContent.Visibility != Visibility.Visible)
            {
                return;
            }

            bool vertical = newIconDockDirection == NewIconDockDirection.Up
                || newIconDockDirection == NewIconDockDirection.Down;
            double expectedWidth = vertical
                ? Math.Max(108.0, GetNewIconTagWidth()) : 430.0;
            if (!IsFinite(newIconDockContent.Width)
                || Math.Abs(newIconDockContent.Width - expectedWidth) > 0.5)
            {
                // Width 被过场中间状态污染时，强制重跑横/竖内容布局。
                verticalNewIconContent = !vertical;
                ApplyNewIconContentLayout();
            }
        }

        private void ApplyNewIconDockLayout(bool resolveDirection = true,
            bool allowDirectionTransition = true)
        {
            if (newIconDock == null || newIconTag == null || newIconTagHost == null
                || newIconDockContent == null)
            {
                return;
            }
            EnsureNewIconPanelSettled();
            ClampNewIconTagToPage();
            if (resolveDirection)
            {
                NewIconDockDirection previousDirection = newIconDockDirection;
                NewIconDockDirection resolvedDirection = ResolveNewIconDockDirection();
                newIconDockDirection = resolvedDirection;
                bool directionChanged = previousDirection != resolvedDirection;
                // 拖动时大部分 MouseMove 方向没变；每帧重建 tag 的 StackPanel/TextBlock
                // 会造成明显卡顿。只有方向真的变了（或还没有 tag 内容）才重建。
                if (newIconTag.Child == null || directionChanged)
                {
                    RefreshNewIconTagVisual();
                }
                if (directionChanged && allowDirectionTransition)
                {
                    // 必须在 ApplyNewIconContentLayout 改尺寸之前定格旧面板。
                    TryBeginNewIconDirectionTransition(previousDirection);
                }
            }
            ApplyNewIconContentLayout();
            UpdateNewIconBoundaryGuides();
            newIconTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tagWidth = Math.Max(1.0, newIconTag.DesiredSize.Width);
            double tagHeight = Math.Max(1.0, newIconTag.DesiredSize.Height);
            newIconTagHost.Width = tagWidth;
            newIconTagHost.Height = tagHeight;
            Canvas.SetLeft(newIconTagHost, newIconTagX);
            Canvas.SetTop(newIconTagHost, newIconTagY);
            // 上下展开时面板贴住标签正下方/正上方，左边缘和标签对齐；
            // 左右展开仍留 8px，避免和标签圆角粘在一起。
            bool verticalContent = newIconDockDirection == NewIconDockDirection.Up
                || newIconDockDirection == NewIconDockDirection.Down;
            double gap = verticalContent ? 0.0 : 8.0;
            double contentLeft = newIconTagX;
            double contentTop = newIconTagY;
            switch (newIconDockDirection)
            {
                case NewIconDockDirection.Left:
                    contentLeft = newIconTagX - newIconDockContent.Width - gap;
                    break;
                case NewIconDockDirection.Down:
                    contentLeft = newIconTagX;
                    contentTop = newIconTagY + tagHeight + gap;
                    break;
                case NewIconDockDirection.Up:
                    contentLeft = newIconTagX;
                    contentTop = newIconTagY - newIconDockContent.Height - gap;
                    break;
                default:
                    contentLeft = newIconTagX + tagWidth + gap;
                    break;
            }
            // 上一版行为：面板按编辑页范围夹取，允许贴到编辑页边缘。
            double minimumLeft = verticalContent ? 0.0 : 6.0;
            double maximumLeft = Math.Max(minimumLeft,
                GetPageOverlayWidth() - newIconDockContent.Width - 6.0);
            double maximumTop = Math.Max(0.0,
                GetPageOverlayHeight() - newIconDockContent.Height);
            contentLeft = Clamp(contentLeft, minimumLeft, maximumLeft);
            contentTop = Clamp(contentTop, 0.0, maximumTop);
            Canvas.SetLeft(newIconDockContent, contentLeft);
            Canvas.SetTop(newIconDockContent, contentTop);
            UpdateNewIconDirectionGhostPosition();
            if (newIconDirectionTransitionPending)
            {
                StartNewIconDirectionTransition();
            }
        }

        private void RememberNewIconDockPosition()
        {
            double width = GetPageOverlayWidth();
            double height = GetPageOverlayHeight();
            if (width <= 1.0 || height <= 1.0)
            {
                return;
            }
            double normalizedX = Clamp(newIconTagX / width, 0.0, 1.0);
            double normalizedY = Clamp(newIconTagY / height, 0.0, 1.0);
            try
            {
                controller.SaveDesktopLayoutEditorDockPosition(layout, normalizedX, normalizedY);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to remember the new-icon dock position.", exception);
            }
        }

        private void ClampNewIconTagToPage()
        {
            // 与拖动共用同一套边界计算；拖动中已经夹住，这里只是松手/窗口变化时的防御。
            double tagWidth = GetNewIconTagWidth();
            double tagHeight = GetNewIconTagHeight();
            EditorDragBounds.ClampTo(
                GetPageOverlayBounds(),
                tagWidth, tagHeight,
                ref newIconTagX, ref newIconTagY);
        }

        private void PlaceNewIconDockBesideTitle()
        {
            if (newIconTag == null || pageOverlay == null || layoutTitleLine == null
                || newIconDockPositionInitialized)
            {
                return;
            }
            try
            {
                double pageWidth = GetPageOverlayWidth();
                double pageHeight = GetPageOverlayHeight();
                double savedX;
                double savedY;
                if (pageWidth > 1.0 && pageHeight > 1.0
                    && controller.TryGetDesktopLayoutEditorDockPosition(layout, out savedX, out savedY))
                {
                    newIconTagX = Clamp(savedX * pageWidth, 0.0,
                        Math.Max(0.0, pageWidth - GetNewIconTagWidth()));
                    newIconTagY = Clamp(savedY * pageHeight, 0.0,
                        Math.Max(0.0, pageHeight - GetNewIconTagHeight()));
                    newIconDockPositionInitialized = true;
                    ApplyNewIconDockLayout();
                    return;
                }
                // 标题和浮层是 root 下的兄弟节点，不能直接 TransformToAncestor(pageOverlay)；
                // 先换到窗口坐标，再减掉浮层自身的窗口原点。
                Point point = ToPageOverlay(
                    layoutTitleLine.TransformToAncestor(this).Transform(new Point(0.0, 0.0)));
                // 水平 StackPanel 自己会被拉满整行，ActualWidth 很大；只能累加子元素宽度，
                // 否则“新图标”会被推到窗口最右边。
                double titleGroupWidth = 0.0;
                for (int index = 0; index < layoutTitleLine.Children.Count; index++)
                {
                    FrameworkElement child = layoutTitleLine.Children[index] as FrameworkElement;
                    if (child != null)
                    {
                        titleGroupWidth += child.ActualWidth
                            + child.Margin.Left + child.Margin.Right;
                    }
                }
                newIconTagX = point.X + titleGroupWidth + 10.0;
                newIconTagY = Math.Max(12.0, point.Y - 2.0);
                newIconDockPositionInitialized = true;
                ApplyNewIconDockLayout();
            }
            catch
            {
            }
        }

        private Button CreateBeltArrow(string glyph, string nameKey, double direction)
        {
            Button button = new Button
            {
                Style = (Style)Application.Current.Resources["IconButton"],
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                FontSize = 12,
                Padding = new Thickness(0)
            };
            button.Content = new TextBlock
            {
                Text = glyph,
                FontFamily = AppTheme.IconFont,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, AppText.T(nameKey));
            AccessibleToolTip.Attach(button, DesktopLayoutToolTips.NewIconScroll(nameKey));
            button.Click += delegate
            {
                if (newIconBelt != null)
                {
                    newIconBelt.ScrollToHorizontalOffset(
                        Math.Max(0.0, newIconBelt.HorizontalOffset + direction * 140.0));
                }
            };
            return button;
        }
    }
}
