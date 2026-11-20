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
        private void RefreshNewIconTagVisual()
        {
            if (newIconTag == null)
            {
                return;
            }
            TextBlock icon = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = AppTheme.IconFont,
                FontSize = 15,
                Foreground = AppTheme.Positive,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock title = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            };
            LocalizedText.Text(title, DesktopLayoutTextKeys.NewIconTagTitle);
            // 可见文字不变；只把当前展开方向写进可访问名字，读屏和自检都能确认
            // “现在到底是向上/下/左/右”，不必靠截图猜标签形状。
            AutomationProperties.SetName(
                title,
                AppText.F(
                    DesktopLayoutTextKeys.NewIconTagAutomationFormat,
                    NewIconDirectionName()));
            newIconChevron = new TextBlock
            {
                Text = NewIconChevronGlyph(),
                FontFamily = AppTheme.IconFont,
                FontSize = 9,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool horizontal = newIconDockDirection == NewIconDockDirection.Left
                || newIconDockDirection == NewIconDockDirection.Right;
            if (horizontal)
            {
                Grid row = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel titlePart = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                icon.Margin = new Thickness(0, 0, 5, 0);
                titlePart.Children.Add(icon);
                titlePart.Children.Add(title);
                if (newIconDockDirection == NewIconDockDirection.Left)
                {
                    newIconChevron.Margin = new Thickness(0, 0, 6, 0);
                    row.Children.Add(newIconChevron);
                    Grid.SetColumn(titlePart, 1);
                    row.Children.Add(titlePart);
                }
                else
                {
                    row.Children.Add(titlePart);
                    newIconChevron.Margin = new Thickness(7, 0, 0, 0);
                    Grid.SetColumn(newIconChevron, 2);
                    row.Children.Add(newIconChevron);
                }
                newIconTag.Child = row;
            }
            else
            {
                StackPanel column = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                StackPanel titlePart = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                icon.Margin = new Thickness(0, 0, 5, 0);
                titlePart.Children.Add(icon);
                titlePart.Children.Add(title);
                newIconChevron.Margin = new Thickness(0, 2, 0, 0);
                newIconChevron.HorizontalAlignment = HorizontalAlignment.Center;
                if (newIconDockDirection == NewIconDockDirection.Up)
                {
                    newIconChevron.Margin = new Thickness(0, 0, 0, 2);
                    column.Children.Add(newIconChevron);
                    column.Children.Add(titlePart);
                }
                else
                {
                    column.Children.Add(titlePart);
                    column.Children.Add(newIconChevron);
                }
                newIconTag.Child = column;
            }
        }

        /// <summary>
        /// 上/下展开时，整个面板改成竖向列表：检索在上面，卡片从上到下一张张排；
        /// 左/右展开仍保留原来的横向传送带。
        /// </summary>
        private void ApplyNewIconContentLayout()
        {
            if (newIconDockContent == null || newIconCardsPanel == null
                || newIconBelt == null || newIconSlider == null || newIconFilter == null)
            {
                return;
            }
            bool vertical = newIconDockDirection == NewIconDockDirection.Up
                || newIconDockDirection == NewIconDockDirection.Down;
            if (vertical != verticalNewIconContent)
            {
                verticalNewIconContent = vertical;
                if (vertical)
                {
                    // 用户 2026-10-21 截图明确：向下/向上展开不是 320 宽的短卡，
                    // 而是和标签同宽的窄高竖栏，检索在顶，下面留出可滚动的卡片区。
                    double panelWidth = Math.Max(108.0, GetNewIconTagWidth());
                    newIconDockContent.Width = panelWidth;
                    newIconDockContent.Height = 280;
                    newIconCardsPanel.Orientation = Orientation.Vertical;
                    newIconBelt.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    newIconBelt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    newIconBelt.Height = 214;
                    newIconBelt.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    newIconSlider.Visibility = Visibility.Collapsed;
                    // 检索标签约 30、左右内边距 16；输入框吃掉剩余宽度。
                    newIconFilter.Width = Math.Max(56.0, panelWidth - 46.0);
                    if (newIconLeftButton != null) { newIconLeftButton.Visibility = Visibility.Collapsed; }
                    if (newIconRightButton != null) { newIconRightButton.Visibility = Visibility.Collapsed; }
                    if (newIconBeltHint != null) { newIconBeltHint.Visibility = Visibility.Collapsed; }
                }
                else
                {
                    newIconDockContent.Width = 430;
                    newIconDockContent.Height = 140;
                    newIconCardsPanel.Orientation = Orientation.Horizontal;
                    newIconBelt.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
                    newIconBelt.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    newIconBelt.Height = 58;
                    newIconBelt.HorizontalContentAlignment = HorizontalAlignment.Left;
                    newIconFilter.Width = 150;
                    newIconSlider.Visibility = Visibility.Visible;
                    if (newIconLeftButton != null) { newIconLeftButton.Visibility = Visibility.Visible; }
                    if (newIconRightButton != null) { newIconRightButton.Visibility = Visibility.Visible; }
                    if (newIconBeltHint != null) { newIconBeltHint.Visibility = Visibility.Visible; }
                }
                for (int index = 0; index < trayEntries.Count; index++)
                {
                    Border card = trayEntries[index].Card;
                    if (card == null)
                    {
                        continue;
                    }
                    if (vertical)
                    {
                        card.Width = double.NaN;
                        card.HorizontalAlignment = HorizontalAlignment.Stretch;
                        card.Margin = new Thickness(0, 0, 0, 6);
                    }
                    else
                    {
                        card.Width = 120;
                        card.HorizontalAlignment = HorizontalAlignment.Left;
                        card.Margin = new Thickness(0, 0, 6, 0);
                    }
                }
            }
            UpdateVerticalNewIconHeight();
        }

        private void UpdateVerticalNewIconHeight()
        {
            if (!verticalNewIconContent || newIconDockContent == null || newIconBelt == null)
            {
                return;
            }
            int count = unplacedNewItems == null ? 0 : unplacedNewItems.Count;
            // 用户截图：0 个新图标时也要是一条向下的窄高竖栏，不能缩成 120 高的横卡。
            // 1~4 张都保持 280；再多按每张 48 往下长，最高 420。
            double height = 280.0 + Math.Max(0, count - 4) * 48.0;
            if (height > 420.0)
            {
                height = 420.0;
            }
            newIconDockContent.Height = height;
            newIconBelt.Height = Math.Max(54.0, height - 66.0);
        }

        private static LinearGradientBrush CreateNewIconBoundaryBrush()
        {
            // 四个方向共用同一套浅冷灰渐变；只改 Start/End，贴边处稍深、向内淡出。
            // 不用深灰蓝，避免叠在蓝色壁纸上发脏；这里是高亮度的低饱和蓝灰。
            LinearGradientBrush brush = new LinearGradientBrush
            {
                StartPoint = new Point(0.0, 0.5),
                EndPoint = new Point(1.0, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(70, 174, 188, 203), 0.0));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(44, 174, 188, 203), 0.20));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(16, 196, 207, 219), 0.60));
            brush.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, 232, 238, 244), 1.0));
            return brush;
        }

        private void HideNewIconBoundaryGuides()
        {
            if (newIconBoundaryGuides != null)
            {
                newIconBoundaryGuides.BeginAnimation(UIElement.OpacityProperty, null);
                newIconBoundaryGuides.Visibility = Visibility.Collapsed;
            }
            if (newIconBoundaryPill != null)
            {
                newIconBoundaryPill.BeginAnimation(UIElement.OpacityProperty, null);
                newIconBoundaryPill.Visibility = Visibility.Collapsed;
            }
            activeNewIconBoundary = NewIconBoundary.None;
            lastNewIconBoundaryStatus = null;
        }

        private void UpdateNewIconBoundaryGuides()
        {
            if (newIconBoundaryGuides == null)
            {
                return;
            }
            if (!newIconDragging)
            {
                newIconBoundaryGuides.Visibility = Visibility.Collapsed;
                if (newIconBoundaryPill != null)
                {
                    newIconBoundaryPill.Visibility = Visibility.Collapsed;
                }
                return;
            }
            double pageWidth = GetPageOverlayWidth();
            double pageHeight = GetPageOverlayHeight();
            if (pageWidth <= 1.0 || pageHeight <= 1.0)
            {
                return;
            }
            newIconBoundaryGuides.Width = pageWidth;
            newIconBoundaryGuides.Height = pageHeight;
            // 只有真正贴到某条边时才显示那一条；不再整圈常亮。
            if (activeNewIconBoundary == NewIconBoundary.None)
            {
                newIconBoundaryGuides.BeginAnimation(UIElement.OpacityProperty, null);
                newIconBoundaryGuides.Visibility = Visibility.Collapsed;
                if (newIconBoundaryPill != null)
                {
                    newIconBoundaryPill.BeginAnimation(UIElement.OpacityProperty, null);
                    newIconBoundaryPill.Visibility = Visibility.Collapsed;
                }
                UpdateNewIconBoundaryStatus();
                return;
            }
            if (newIconBoundaryGuides.Visibility != Visibility.Visible)
            {
                newIconBoundaryGuides.Visibility = Visibility.Visible;
                newIconBoundaryGuides.Opacity = 0.0;
                newIconBoundaryGuides.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(120))));
            }
            if (newIconBoundaryBar == null || newIconBoundaryBrush == null)
            {
                return;
            }
            // 一整条长圆角矩形覆盖当前边界：贴边一侧稍深，向页面内侧渐变淡出。
            // 四个方向共用同一个 Rectangle 和同一套渐变；只改位置/宽高/渐变方向。
            const double EdgeInset = 8.0;
            const double BarThickness = 64.0;
            bool verticalEdge = activeNewIconBoundary == NewIconBoundary.Left
                || activeNewIconBoundary == NewIconBoundary.Right;
            double barWidth;
            double barHeight;
            double barLeft;
            double barTop;
            if (verticalEdge)
            {
                barWidth = Math.Min(BarThickness, pageWidth);
                barHeight = Math.Max(1.0, pageHeight - EdgeInset * 2.0);
                barLeft = activeNewIconBoundary == NewIconBoundary.Left
                    ? 0.0 : pageWidth - barWidth;
                barTop = EdgeInset;
                newIconBoundaryBar.RadiusX = 20.0;
                newIconBoundaryBar.RadiusY = 28.0;
                if (activeNewIconBoundary == NewIconBoundary.Left)
                {
                    newIconBoundaryBrush.StartPoint = new Point(0.0, 0.5);
                    newIconBoundaryBrush.EndPoint = new Point(1.0, 0.5);
                }
                else
                {
                    newIconBoundaryBrush.StartPoint = new Point(1.0, 0.5);
                    newIconBoundaryBrush.EndPoint = new Point(0.0, 0.5);
                }
            }
            else
            {
                barWidth = Math.Max(1.0, pageWidth - EdgeInset * 2.0);
                barHeight = Math.Min(BarThickness, pageHeight);
                barLeft = EdgeInset;
                barTop = activeNewIconBoundary == NewIconBoundary.Top
                    ? 0.0 : pageHeight - barHeight;
                newIconBoundaryBar.RadiusX = 28.0;
                newIconBoundaryBar.RadiusY = 20.0;
                if (activeNewIconBoundary == NewIconBoundary.Top)
                {
                    newIconBoundaryBrush.StartPoint = new Point(0.5, 0.0);
                    newIconBoundaryBrush.EndPoint = new Point(0.5, 1.0);
                }
                else
                {
                    newIconBoundaryBrush.StartPoint = new Point(0.5, 1.0);
                    newIconBoundaryBrush.EndPoint = new Point(0.5, 0.0);
                }
            }
            newIconBoundaryBar.Width = barWidth;
            newIconBoundaryBar.Height = barHeight;
            Canvas.SetLeft(newIconBoundaryBar, barLeft);
            Canvas.SetTop(newIconBoundaryBar, barTop);
            newIconBoundaryBar.Visibility = Visibility.Visible;
            UpdateNewIconBoundaryPill(pageWidth, pageHeight);
            UpdateNewIconBoundaryStatus();
        }

        private void UpdateNewIconBoundaryStatus()
        {
            if (!newIconDragging || statusText == null)
            {
                return;
            }
            string text;
            if (activeNewIconBoundary != NewIconBoundary.None)
            {
                text = AppText.F(
                    DesktopLayoutTextKeys.NewIconBoundaryHitFormat,
                    NewIconBoundaryName(activeNewIconBoundary),
                    NewIconDirectionName());
            }
            else
            {
                text = AppText.T(DesktopLayoutTextKeys.NewIconBoundaryNoneStatus);
            }
            if (!string.Equals(text, lastNewIconBoundaryStatus, StringComparison.Ordinal))
            {
                lastNewIconBoundaryStatus = text;
                statusText.Text = text;
            }
        }

        private void UpdateNewIconBoundaryPill(double pageWidth, double pageHeight)
        {
            if (newIconBoundaryPill == null || newIconBoundaryPillText == null)
            {
                return;
            }
            if (activeNewIconBoundary == NewIconBoundary.None)
            {
                newIconBoundaryPill.BeginAnimation(UIElement.OpacityProperty, null);
                newIconBoundaryPill.Visibility = Visibility.Collapsed;
                return;
            }
            bool newlyVisible = newIconBoundaryPill.Visibility != Visibility.Visible;
            newIconBoundaryPillIcon.Text = NewIconDirectionArrow();
            newIconBoundaryPillText.Text = AppText.F(
                DesktopLayoutTextKeys.NewIconBoundaryPillFormat,
                NewIconBoundaryName(activeNewIconBoundary),
                NewIconDirectionName());
            newIconBoundaryPill.Visibility = Visibility.Visible;
            if (newlyVisible)
            {
                newIconBoundaryPill.Opacity = 0.0;
                newIconBoundaryPill.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(140))));
            }
            newIconBoundaryPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double pillWidth = Math.Max(1.0, newIconBoundaryPill.DesiredSize.Width);
            double pillHeight = Math.Max(1.0, newIconBoundaryPill.DesiredSize.Height);
            double tagWidth = GetNewIconTagWidth();
            double tagHeight = GetNewIconTagHeight();
            double left = newIconTagX + tagWidth / 2.0 - pillWidth / 2.0;
            double top = newIconTagY - pillHeight - 10.0;
            if (top < 8.0)
            {
                top = newIconTagY + tagHeight + 10.0;
                if (activeNewIconBoundary == NewIconBoundary.Top)
                {
                    // 贴顶边时不要把提示压到标题/说明文字上，放到画布上沿更干净。
                    top = Math.Max(top, 110.0);
                }
            }
            left = Clamp(left, 8.0, Math.Max(8.0, pageWidth - pillWidth - 8.0));
            top = Clamp(top, 8.0, Math.Max(8.0, pageHeight - pillHeight - 8.0));
            Canvas.SetLeft(newIconBoundaryPill, left);
            Canvas.SetTop(newIconBoundaryPill, top);
        }

        private static string NewIconBoundaryName(NewIconBoundary boundary)
        {
            switch (boundary)
            {
                case NewIconBoundary.Top:
                    return AppText.T(DesktopLayoutTextKeys.NewIconBoundaryTop);
                case NewIconBoundary.Bottom:
                    return AppText.T(DesktopLayoutTextKeys.NewIconBoundaryBottom);
                case NewIconBoundary.Left:
                    return AppText.T(DesktopLayoutTextKeys.NewIconBoundaryLeft);
                case NewIconBoundary.Right:
                    return AppText.T(DesktopLayoutTextKeys.NewIconBoundaryRight);
                default:
                    return AppText.T(DesktopLayoutTextKeys.NewIconBoundaryNone);
            }
        }

        private string NewIconDirectionArrow()
        {
            switch (newIconDockDirection)
            {
                case NewIconDockDirection.Left:
                    return "←";
                case NewIconDockDirection.Down:
                    return "↓";
                case NewIconDockDirection.Up:
                    return "↑";
                default:
                    return "→";
            }
        }

    }
}
