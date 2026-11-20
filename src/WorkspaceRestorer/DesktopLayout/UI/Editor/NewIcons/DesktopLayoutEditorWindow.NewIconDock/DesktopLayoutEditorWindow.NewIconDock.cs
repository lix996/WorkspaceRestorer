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
        private Canvas BuildNewIconDock()
        {
            List<DesktopIconPlacement> newItems = new List<DesktopIconPlacement>();
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item != null && unplacedNewItems.Contains(item))
                {
                    newItems.Add(item);
                }
            }
            // 即使当前没有新图标也创建标签：全部拖进预桌面后只隐藏红色数字，
            // 标签本身保留，用户的位置也还在（用户 2026-10-21 要求）。
            newIconDock = new Canvas { ClipToBounds = false };
            // 拖动“新图标”标签时，只在碰到的那条边显示一整条长圆角渐变：
            // 整条边都被覆盖，贴边处稍深、向内淡出；四个角用 Rectangle 的椭圆圆角。
            // 四个方向共用同一个控件、同一套渐变，只换位置和长短边。
            newIconBoundaryGuides = new Canvas
            {
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            newIconBoundaryBrush = CreateNewIconBoundaryBrush();
            newIconBoundaryBar = new Rectangle
            {
                Fill = newIconBoundaryBrush,
                RadiusX = 20.0,
                RadiusY = 28.0,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(newIconBoundaryBar, 0);
            Canvas.SetTop(newIconBoundaryBar, 0);
            Panel.SetZIndex(newIconBoundaryBar, 0);
            newIconBoundaryGuides.Children.Add(newIconBoundaryBar);
            Panel.SetZIndex(newIconBoundaryGuides, 40);
            pageOverlay.Children.Add(newIconBoundaryGuides);

            newIconBoundaryPillIcon = new TextBlock
            {
                FontFamily = AppFonts.Symbol,
                FontSize = 13,
                Foreground = EditorPalette.DockText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            newIconBoundaryPillText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = EditorPalette.DockTextStrong,
                VerticalAlignment = VerticalAlignment.Center
            };
            StackPanel pillRow = new StackPanel { Orientation = Orientation.Horizontal };
            pillRow.Children.Add(newIconBoundaryPillIcon);
            pillRow.Children.Add(newIconBoundaryPillText);
            newIconBoundaryPill = new Border
            {
                Background = EditorPalette.DockBackground,
                BorderBrush = EditorPalette.DockBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 5, 11, 5),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Child = pillRow
            };
            try
            {
                newIconBoundaryPill.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Opacity = 0.16,
                    Color = Colors.Black
                };
            }
            catch
            {
            }
            Panel.SetZIndex(newIconBoundaryPill, 10);
            newIconBoundaryGuides.Children.Add(newIconBoundaryPill);

            newIconTagHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            newIconTag = new Border
            {
                // 固定 108×54：方向在左/右/上/下之间切换时标签不再突然变宽变窄，
                // 只让内容和箭头平滑换向，视觉上稳很多。
                Width = 108,
                Height = 54,
                Padding = new Thickness(8),
                Background = new LinearGradientBrush(
                    Color.FromRgb(248, 255, 251),
                    Color.FromRgb(232, 250, 241),
                    90.0),
                BorderBrush = EditorPalette.NewIconBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = new ScaleTransform(1.0, 1.0),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            try
            {
                newIconTag.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 2,
                    Opacity = 0.14,
                    Color = Colors.Black
                };
            }
            catch
            {
            }
            LocalizedText.AccessibleName(
                newIconTag, DesktopLayoutTextKeys.NewIconTagPanelAutomation);
            AccessibleToolTip.Attach(newIconTag, DesktopLayoutToolTips.NewIconTag);
            newIconTagHost.Children.Add(newIconTag);
            // 自检/读屏用：标签本身的 108×54 边界。Border 没有自动化 peer，
            // 用一个透明的 TextBlock 暴露范围；不参与绘制和命中测试。
            TextBlock tagBoundsProbe = new TextBlock
            {
                Width = 108,
                Height = 54,
                Opacity = 0.0,
                IsHitTestVisible = false,
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            LocalizedText.AccessibleName(
                tagBoundsProbe, DesktopLayoutTextKeys.NewIconTagBoundsAutomation);
            newIconTagHost.Children.Insert(0, tagBoundsProbe);

            // 红色数量角标是 overlay：不参与 tag 的布局，4 位以上数字可以溢出标签，
            // 但 IsHitTestVisible=false，不影响点击展开/长按拖动。
            newIconCountBadge = new Border
            {
                MinWidth = 18,
                Height = 18,
                Padding = new Thickness(4, 0, 4, 0),
                Background = EditorPalette.DangerBadge,
                CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0),
                IsHitTestVisible = false
            };
            newIconCountText = new TextBlock
            {
                Text = newItems.Count.ToString(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 10
            };
            newIconCountBadge.Child = newIconCountText;
            Panel.SetZIndex(newIconCountBadge, 20);
            newIconTagHost.Children.Add(newIconCountBadge);
            Panel.SetZIndex(newIconTagHost, 30);

            newIconHoldTimer = new DispatcherTimer();
            newIconHoldTimer.Interval = TimeSpan.FromMilliseconds(350);
            newIconHoldTimer.Tick += delegate
            {
                if (!newIconPointerDown)
                {
                    newIconHoldTimer.Stop();
                    return;
                }
                // 只用于区分“按住不放”与单击：按住超过阈值后松手不再展开。
                // 拖动从鼠标真正移动就开始，不再要求先等 350ms（用户实测“按着移不动”）。
                newIconLongPress = true;
                newIconHoldTimer.Stop();
            };
            // 只靠 MouseMove 时，鼠标甩得太快会丢事件，标签就停在半路。
            // 拖动期间用 16ms 定时器直接读 Mouse.GetPosition，保证一直跟手。
            newIconDragFollowTimer = new DispatcherTimer();
            newIconDragFollowTimer.Interval = TimeSpan.FromMilliseconds(16);
            newIconDragFollowTimer.Tick += delegate
            {
                if (!newIconPointerDown)
                {
                    newIconDragFollowTimer.Stop();
                    return;
                }
                // 物理左键状态比 WPF 的 Mouse.LeftButton 可靠：鼠标划出窗口/
                // 丢失捕获时，WPF 可能误报已松开，标签就会在用户还按着的时候停下。
                if (!NativeMethods.IsLeftMouseButtonDown())
                {
                    // 连续两个 tick 都确认松开才结束，避免瞬时误判把拖动打断。
                    newIconTagLeftUpTicks++;
                    if (newIconTagLeftUpTicks >= 2)
                    {
                        FinishNewIconTagDragFromMouseState();
                    }
                    return;
                }
                newIconTagLeftUpTicks = 0;
                // 鼠标可能一按下就甩出窗口，MouseMove 没机会先把 dragging 置 true；
                // 这里只请求一次合并更新，由 Update... 自己判断是否过拖动阈值。
                RequestNewIconTagDragUpdate();
            };
            newIconTag.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (state.Saving)
                {
                    return;
                }
                newIconPointerDown = true;
                newIconLongPress = false;
                newIconDragging = false;
                newIconTagDragUpdateQueued = false;
                newIconTagCaptureRestoreQueued = false;
                newIconTagLeftUpTicks = 0;
                dragPointerPhysicalFallback = false;
                if (newIconDragFollowTimer != null)
                {
                    newIconDragFollowTimer.Stop();
                }
                // 先缓存页面原点/轴心，再取按下点；页内按下不读物理光标。
                CacheNewIconTagDragFrame();
                newIconPressPoint = GetMousePositionInPage();
                // 单击只负责展开/收起，不重算方向；等真正超过拖动阈值后，
                // 再由 ApplyNewIconDockLayout 按当前位置和隐藏十字轴判断。
                HideNewIconBoundaryGuides();
                newIconTag.CaptureMouse();
                newIconHoldTimer.Stop();
                newIconHoldTimer.Start();
                newIconDragFollowTimer.Start();
                args.Handled = true;
            };
            newIconTag.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!newIconPointerDown)
                {
                    return;
                }
                if (!NativeMethods.IsLeftMouseButtonDown())
                {
                    // 交给 16ms 看门狗连续确认，MouseMove 不再单次误判就结束。
                    return;
                }
                RequestNewIconTagDragUpdate();
                args.Handled = true;
            };
            newIconTag.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                // 捕获短暂丢失时 WPF 可能补一个假 Up；物理左键还按着就忽略。
                if (NativeMethods.IsLeftMouseButtonDown())
                {
                    if (!newIconTag.IsMouseCaptured)
                    {
                        newIconTag.CaptureMouse();
                    }
                    args.Handled = true;
                    return;
                }
                EndNewIconTagDrag(true);
                args.Handled = true;
            };
            newIconTag.LostMouseCapture += delegate
            {
                if (!newIconPointerDown)
                {
                    return;
                }
                // 快速甩动/划出窗口时捕获可能短暂丢失；拖动生命周期不依赖捕获。
                // 标记为物理光标模式，并只安排一次重新捕获，绝不在这里结束拖动。
                dragPointerPhysicalFallback = true;
                if (NativeMethods.IsLeftMouseButtonDown() && !state.Saving && !state.Closed)
                {
                    if (newIconTagCaptureRestoreQueued)
                    {
                        return;
                    }
                    newIconTagCaptureRestoreQueued = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        newIconTagCaptureRestoreQueued = false;
                        if (newIconPointerDown && !newIconTag.IsMouseCaptured
                            && NativeMethods.IsLeftMouseButtonDown())
                        {
                            newIconTag.CaptureMouse();
                        }
                    }), DispatcherPriority.Input);
                    return;
                }
                FinishNewIconTagDragFromMouseState();
            };

            newIconDockContent = new NewIconDockPanel
            {
                Width = 430,
                Height = 140,
                Padding = new Thickness(8),
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            // 自检/读屏用的稳定名字：换向时能直接拿到整块面板的屏幕矩形，
            // 不必靠截图像素猜宽度。
            LocalizedText.AccessibleName(
                newIconDockContent, DesktopLayoutTextKeys.NewIconPanelAutomation);
            Grid contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid searchRow = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel searchPanel = new StackPanel { Orientation = Orientation.Horizontal };
            TextBlock searchLabel = new TextBlock
            {
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            LocalizedText.Text(
                searchLabel, DesktopLayoutTextKeys.NewIconFilterLabel);
            searchPanel.Children.Add(searchLabel);
            newIconFilter = new TextBox
            {
                Width = 150,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(8, 2, 8, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AccessibleToolTip.Attach(newIconFilter, DesktopLayoutToolTips.NewIconFilter);
            newIconFilter.TextChanged += delegate { ApplyNewIconFilter(); };
            newIconFilter.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    args.Handled = true;
                }
            };
            searchPanel.Children.Add(newIconFilter);
            searchRow.Children.Add(searchPanel);
            newIconBeltHint = new TextBlock
            {
                FontSize = 10,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            LocalizedText.Text(
                newIconBeltHint, DesktopLayoutTextKeys.NewIconBeltHint);
            Grid.SetColumn(newIconBeltHint, 1);
            searchRow.Children.Add(newIconBeltHint);
            contentGrid.Children.Add(searchRow);

            Grid beltRow = new Grid();
            beltRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            beltRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            beltRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border beltFrame = new Border
            {
                Background = EditorPalette.DockSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(3)
            };
            newIconBelt = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Height = 58
            };
            newIconCardsPanel = new StackPanel { Orientation = Orientation.Horizontal };
            LocalizedText.AccessibleName(
                newIconBelt, DesktopLayoutTextKeys.NewIconBeltAutomation);
            newIconBelt.Content = newIconCardsPanel;
            newIconBelt.ScrollChanged += delegate { SyncNewIconBelt(); };
            beltFrame.Child = newIconBelt;
            Grid.SetColumn(beltFrame, 1);
            beltRow.Children.Add(beltFrame);

            newIconLeftButton = CreateBeltArrow(
                "\uE76B", DesktopLayoutTextKeys.NewIconScrollLeft, -1);
            newIconLeftButton.Margin = new Thickness(0, 0, 3, 0);
            newIconLeftButton.VerticalAlignment = VerticalAlignment.Center;
            beltRow.Children.Add(newIconLeftButton);
            newIconRightButton = CreateBeltArrow(
                "\uE76C", DesktopLayoutTextKeys.NewIconScrollRight, 1);
            newIconRightButton.Margin = new Thickness(3, 0, 0, 0);
            newIconRightButton.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(newIconRightButton, 2);
            beltRow.Children.Add(newIconRightButton);
            contentGrid.Children.Add(beltRow);
            Grid.SetRow(beltRow, 1);

            newIconSlider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = 0,
                Height = 18,
                Margin = new Thickness(0, 4, 0, 0),
                IsMoveToPointEnabled = true,
                SmallChange = 40,
                LargeChange = 200
            };
            LocalizedText.AccessibleName(
                newIconSlider, DesktopLayoutTextKeys.NewIconSliderAutomation);
            newIconSlider.ValueChanged += delegate
            {
                if (!syncingBelt && newIconBelt != null)
                {
                    newIconBelt.ScrollToHorizontalOffset(newIconSlider.Value);
                }
            };
            contentGrid.Children.Add(newIconSlider);
            Grid.SetRow(newIconSlider, 2);

            for (int index = 0; index < newItems.Count; index++)
            {
                CreateNewIconCard(newItems[index]);
            }

            newIconBelt.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                {
                    return;
                }
                beltDragging = true;
                beltDragStart = args.GetPosition(newIconBelt);
                beltStartOffset = newIconBelt.HorizontalOffset;
                newIconBelt.CaptureMouse();
                args.Handled = true;
            };
            newIconBelt.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!beltDragging || !newIconBelt.IsMouseCaptured || args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }
                double delta = args.GetPosition(newIconBelt).X - beltDragStart.X;
                newIconBelt.ScrollToHorizontalOffset(Math.Max(0.0, beltStartOffset - delta));
            };
            newIconBelt.MouseLeftButtonUp += delegate
            {
                if (beltDragging)
                {
                    beltDragging = false;
                    if (newIconBelt.IsMouseCaptured)
                    {
                        newIconBelt.ReleaseMouseCapture();
                    }
                }
            };
            newIconBelt.LostMouseCapture += delegate { beltDragging = false; };

            newIconDockContent.Child = contentGrid;
            newIconDock.Children.Add(newIconTagHost);
            newIconDock.Children.Add(newIconDockContent);
            newIconTagX = 12.0;
            newIconTagY = 84.0;
            RefreshNewIconTagVisual();
            ApplyNewIconDockLayout();
            UpdateNewIconCount(false);
            Dispatcher.BeginInvoke(new Action(SyncNewIconBelt), DispatcherPriority.Background);

            return newIconDock;
        }

    }
}
