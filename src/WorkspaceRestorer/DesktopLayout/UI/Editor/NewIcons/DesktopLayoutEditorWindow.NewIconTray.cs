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
        private void CreateNewIconCard(DesktopIconPlacement item)
        {
            if (item == null || newIconCardsPanel == null)
            {
                return;
            }
            bool vertical = verticalNewIconContent;
            Border card = new Border
            {
                // 撤销/重做会重建卡片；这里按当前方向直接给对尺寸，
                // 不必每次拖动标签都遍历所有卡片重设。
                Width = vertical ? double.NaN : 120,
                Height = 42,
                HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
                Margin = vertical ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 6, 0),
                Padding = new Thickness(4),
                Background = EditorPalette.TrayBackground,
                BorderBrush = AppTheme.Positive,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Cursor = Cursors.SizeAll
            };
            AutomationProperties.SetName(
                card,
                AppText.F(
                    DesktopLayoutTextKeys.NewIconCardAutomationFormat,
                    item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback)));

            Grid cardGrid = new Grid();
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Image trayImage = new Image
            {
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(trayImage, BitmapScalingMode.Linear);
            BitmapSource cachedSource;
            if (imageCache.TryGetNewIconSource(item, out cachedSource))
            {
                trayImage.Source = cachedSource;
            }
            Image loadedImage;
            if (trayImage.Source == null && imageCache.TryGetCanvasImage(item, out loadedImage) && loadedImage != null)
            {
                trayImage.Source = loadedImage.Source;
            }
            imageCache.SetTrayImage(item, trayImage);
            cardGrid.Children.Add(trayImage);

            TextBlock name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? AppText.T(DesktopLayoutTextKeys.IconFallback)
                    : item.DisplayName,
                FontSize = 10,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 30,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            cardGrid.Children.Add(name);
            card.Child = cardGrid;
            AttachTrayDrag(card, item);
            newIconCardsPanel.Children.Add(card);
            trayEntries.Add(new TrayEntry(item, card));
        }

        private void SyncNewIconBelt()
        {
            if (newIconBelt == null || newIconSlider == null)
            {
                return;
            }
            syncingBelt = true;
            double maximum = Math.Max(0.0, newIconBelt.ScrollableWidth);
            newIconSlider.Maximum = Math.Max(1.0, maximum);
            newIconSlider.Value = Math.Min(newIconBelt.HorizontalOffset, maximum);
            newIconSlider.IsEnabled = maximum > 0.5;
            if (newIconLeftButton != null)
            {
                newIconLeftButton.IsEnabled = newIconBelt.HorizontalOffset > 0.5;
            }
            if (newIconRightButton != null)
            {
                newIconRightButton.IsEnabled = newIconBelt.HorizontalOffset < maximum - 0.5;
            }
            syncingBelt = false;
        }

        private void UpdateNewIconCount(bool animate)
        {
            int count = unplacedNewItems.Count;
            if (newIconCountText != null)
            {
                newIconCountText.Text = count.ToString();
            }
            if (newIconCountBadge != null)
            {
                if (count == 0)
                {
                    if (animate)
                    {
                        DoubleAnimation fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(160)));
                        fade.Completed += delegate
                        {
                            if (unplacedNewItems.Count == 0)
                            {
                                newIconCountBadge.Visibility = Visibility.Collapsed;
                            }
                        };
                        newIconCountBadge.BeginAnimation(UIElement.OpacityProperty, fade);
                    }
                    else
                    {
                        newIconCountBadge.Visibility = Visibility.Collapsed;
                        newIconCountBadge.Opacity = 1.0;
                    }
                }
                else
                {
                    newIconCountBadge.BeginAnimation(UIElement.OpacityProperty, null);
                    newIconCountBadge.Visibility = Visibility.Visible;
                    newIconCountBadge.Opacity = 1.0;
                }
            }
            if (layoutSummaryText != null && !string.IsNullOrEmpty(layoutSummaryBase))
            {
                string summary = layoutSummaryBase;
                if (count > 0)
                {
                    summary += AppText.F(
                        DesktopLayoutTextKeys.EditorSummaryNewFormat, count);
                }
                layoutSummaryText.Text = summary;
            }
            if (newIconDock != null)
            {
                // 全部拖完也只隐藏数字角标，不隐藏标签。
                newIconDock.Visibility = Visibility.Visible;
                newIconDock.Opacity = 1.0;
            }
            UpdateVerticalNewIconHeight();
            if (verticalNewIconContent && newIconDockContent != null
                && newIconDockContent.Visibility == Visibility.Visible)
            {
                ApplyNewIconDockLayout(false);
            }
        }

        private void MarkNewIconPlaced(DesktopIconPlacement item, bool animate)
        {
            if (item == null || !unplacedNewItems.Remove(item))
            {
                return;
            }
            UpdateNewIconCount(true);
            TrayEntry entry = null;
            for (int index = 0; index < trayEntries.Count; index++)
            {
                if (object.ReferenceEquals(trayEntries[index].Item, item))
                {
                    entry = trayEntries[index];
                    break;
                }
            }
            if (entry == null)
            {
                return;
            }
            Border card = entry.Card;
            Action finish = delegate
            {
                if (newIconCardsPanel != null && newIconCardsPanel.Children.Contains(card))
                {
                    newIconCardsPanel.Children.Remove(card);
                }
                if (trayEntries.Contains(entry))
                {
                    trayEntries.Remove(entry);
                    imageCache.RemoveTrayImage(item);
                }
                SyncNewIconBelt();
            };
            if (!animate)
            {
                finish();
                return;
            }
            // 竖排卡片的 Width 是 NaN（Stretch），直接动画到 0 会先跳成 0；
            // 先用当前实际宽度当起点，卡片才能连续收窄。
            if (double.IsNaN(card.Width))
            {
                card.Width = Math.Max(0.0, card.ActualWidth);
            }
            DoubleAnimation width = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(180)));
            width.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
            width.Completed += delegate { finish(); };
            card.BeginAnimation(FrameworkElement.WidthProperty, width);
            ThicknessAnimation margin = new ThicknessAnimation(
                new Thickness(0), new Duration(TimeSpan.FromMilliseconds(180)));
            margin.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
            card.BeginAnimation(FrameworkElement.MarginProperty, margin);
            DoubleAnimation opacity = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(180)));
            opacity.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
            card.BeginAnimation(UIElement.OpacityProperty, opacity);
        }

        private void RebuildNewIconCards()
        {
            if (newIconCardsPanel == null)
            {
                return;
            }
            newIconCardsPanel.Children.Clear();
            trayEntries.Clear();
            imageCache.ClearTrayImages();
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                if (item != null && unplacedNewItems.Contains(item))
                {
                    CreateNewIconCard(item);
                }
            }
            UpdateNewIconCount(false);
            if (newIconDock != null)
            {
                newIconDock.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDock.Opacity = 1.0;
                // 全部拖完也保留标签，只隐藏数字角标。
                newIconDock.Visibility = Visibility.Visible;
            }
            ApplyNewIconFilter();
            Dispatcher.BeginInvoke(new Action(SyncNewIconBelt), DispatcherPriority.Background);
        }
        // ===== 2026-10-25：新图标标签“轻量跟手”拖动 =====
        // 拖动时只移动标签和展开面板，不再每帧 ApplyNewIconDockLayout：
        // 后者会 Measure、重排展开面板、更新边界、方向一变还会截整块面板图，
        // 展开时 UI 线程被占住，标签就追不上鼠标。
        private bool newIconTagDragFastActive;
        private double newIconTagDragTagWidth;
        private double newIconTagDragTagHeight;
        private double newIconTagDragPanelWidth;
        private double newIconTagDragPanelHeight;
        private bool newIconPanelSnapshotWarmed;

        private void BeginNewIconTagFastDrag()
        {
            if (newIconTagDragFastActive)
            {
                return;
            }
            newIconTagDragFastActive = true;
            newIconTagDragTagWidth = GetNewIconTagWidth();
            newIconTagDragTagHeight = GetNewIconTagHeight();
            newIconTagDragPanelWidth = GetNewIconPanelWidthForDrag();
            newIconTagDragPanelHeight = GetNewIconPanelHeightForDrag();
            // 标签固定 108×54：尺寸只写一次，拖动期间不再触发 Measure/Arrange。
            if (newIconTagHost != null)
            {
                newIconTagHost.Width = newIconTagDragTagWidth;
                newIconTagHost.Height = newIconTagDragTagHeight;
            }
            // 页面矩形/页面原点/轴心只算一次；拖动中不再 TransformToAncestor。
            CacheNewIconTagDragFrame();
            // 标签按整个编辑页夹取；锚点从按下点开始算，第一次 MoveTo 不会跳。
            newIconTagDragBounds = new EditorDragBounds(
                newIconTagDragTagWidth, newIconTagDragTagHeight);
            newIconTagDragBounds.Begin(
                newIconPressPoint.X, newIconPressPoint.Y,
                newIconTagX, newIconTagY,
                newIconTagDragPageBounds);
            dragPointerPhysicalFallback = false;
            newIconTagLeftUpTicks = 0;
            CancelNewIconDirectionTransition();
            EnsureNewIconPanelSettled();
            if (newIconDockContent != null)
            {
                // 如果用户点开面板后马上拖标签，120ms 淡入可能还没结束；
                // 直接结束它，避免拖动跟手和透明度动画抢同一帧。
                newIconDockContent.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDockContent.Opacity = 1.0;
                newIconDockContent.IsHitTestVisible = true;
            }
        }

        private double GetNewIconPanelWidthForDrag()
        {
            if (newIconDockContent == null)
            {
                return 0.0;
            }
            if (!double.IsNaN(newIconDockContent.Width) && newIconDockContent.Width > 1.0)
            {
                return newIconDockContent.Width;
            }
            return Math.Max(1.0, newIconDockContent.ActualWidth);
        }

        private double GetNewIconPanelHeightForDrag()
        {
            if (newIconDockContent == null)
            {
                return 0.0;
            }
            if (!double.IsNaN(newIconDockContent.Height) && newIconDockContent.Height > 1.0)
            {
                return newIconDockContent.Height;
            }
            return Math.Max(1.0, newIconDockContent.ActualHeight);
        }

        /// <summary>
        /// 拖动期间的轻量布局：标签/面板每帧直接跟鼠标；方向仍按隐藏十字轴边移边切，
        /// 但只在方向真正变化时走一次 2026-10-23 的 B 方案过场（旧面板收回、新面板展开），
        /// 不再每帧 ApplyNewIconDockLayout + Measure/重排，所以不会把跟手拖慢。
        /// 过场 ghost 每帧按标签逻辑坐标重新定位，标签不会脱离鼠标。
        /// </summary>
        private void ApplyNewIconTagDragFast()
        {
            if (!newIconTagDragFastActive || newIconTag == null || newIconTagHost == null)
            {
                return;
            }

            NewIconDockDirection previousDirection = newIconDockDirection;
            NewIconDockDirection resolvedDirection = ResolveNewIconDockDirection(
                newIconTagDragTagWidth,
                newIconTagDragTagHeight);
            if (resolvedDirection != previousDirection)
            {
                newIconDockDirection = resolvedDirection;
                RefreshNewIconTagVisual();
                // 先定格旧面板，再改横/竖尺寸；Start 会把新面板也定格成快照，
                // 过场只动快照，活面板在结束后才恢复（B 方案）。
                TryBeginNewIconDirectionTransition(previousDirection);
                ApplyNewIconContentLayout();
                newIconTagDragPanelWidth = GetNewIconPanelWidthForDrag();
                newIconTagDragPanelHeight = GetNewIconPanelHeightForDrag();
            }

            Canvas.SetLeft(newIconTagHost, newIconTagX);
            Canvas.SetTop(newIconTagHost, newIconTagY);

            if (newIconDockContent != null
                && newIconDockContent.Visibility == Visibility.Visible
                && newIconTagDragPanelWidth > 1.0
                && newIconTagDragPanelHeight > 1.0)
            {
                bool verticalContent = newIconDockDirection == NewIconDockDirection.Up
                    || newIconDockDirection == NewIconDockDirection.Down;
                double gap = verticalContent ? 0.0 : 8.0;
                double contentLeft = newIconTagX;
                double contentTop = newIconTagY;
                switch (newIconDockDirection)
                {
                    case NewIconDockDirection.Left:
                        contentLeft = newIconTagX - newIconTagDragPanelWidth - gap;
                        break;
                    case NewIconDockDirection.Down:
                        contentTop = newIconTagY + newIconTagDragTagHeight + gap;
                        break;
                    case NewIconDockDirection.Up:
                        contentTop = newIconTagY - newIconTagDragPanelHeight - gap;
                        break;
                    default:
                        contentLeft = newIconTagX + newIconTagDragTagWidth + gap;
                        break;
                }
                // 拖动期间面板也跟到页面外，和标签保持一体；松手 ApplyNewIconDockLayout
                // 再按页面夹回。否则标签拖到边缘外、面板停在页面边，看起来像“收回/断掉”。
                Canvas.SetLeft(newIconDockContent, contentLeft);
                Canvas.SetTop(newIconDockContent, contentTop);
            }

            // 过场中的旧/新快照按标签当前逻辑坐标跟随；这一步必须每帧做，
            // 否则动画期间标签走了、快照留在原地，就是“面板被收回/断掉”的 bug。
            UpdateNewIconDirectionGhostPosition();
            if (newIconDirectionTransitionPending)
            {
                StartNewIconDirectionTransition();
            }
        }

        private void EndNewIconTagFastDrag()
        {
            if (!newIconTagDragFastActive)
            {
                return;
            }
            newIconTagDragFastActive = false;
        }

        private void UpdateNewIconTagDragPosition(Point point)
        {
            if (!newIconPointerDown)
            {
                return;
            }
            double deltaX = point.X - newIconPressPoint.X;
            double deltaY = point.Y - newIconPressPoint.Y;
            if (!newIconDragging)
            {
                if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
                {
                    return;
                }
                newIconDragging = true;
                newIconLastDragX = newIconTagX;
                newIconLastDragY = newIconTagY;
                newIconLastDragTimeUtc = DateTime.UtcNow.AddMilliseconds(-16);
                newIconDragSpeed = 0.0;
                newIconHoldTimer.Stop();
                newIconTag.Cursor = Cursors.SizeAll;
                BeginNewIconTagFastDrag();
                if (newIconDragFollowTimer != null)
                {
                    newIconDragFollowTimer.Start();
                }
            }
            // 标签按编辑页夹取：页内 1:1，页外钉边但拖动不断；
            // 鼠标回到页内当帧接住，不再做 48 DIP/帧的追赶滑行。
            newIconTagDragBounds.MoveTo(point.X, point.Y, newIconTagDragPageBounds);
            newIconTagX = newIconTagDragBounds.X;
            newIconTagY = newIconTagDragBounds.Y;
            DateTime now = DateTime.UtcNow;
            double elapsedSeconds = (now - newIconLastDragTimeUtc).TotalSeconds;
            if (elapsedSeconds >= 0.006)
            {
                double movedX = newIconTagX - newIconLastDragX;
                double movedY = newIconTagY - newIconLastDragY;
                newIconDragSpeed = Math.Sqrt(
                    movedX * movedX + movedY * movedY) / elapsedSeconds;
                newIconLastDragX = newIconTagX;
                newIconLastDragY = newIconTagY;
                newIconLastDragTimeUtc = now;
            }
            ApplyNewIconTagDragFast();
        }

        /// <summary>
        /// MouseMove 和 16ms 定时器都只“请求一次更新”，由 Dispatcher.Render
        /// 合并成每帧最多执行一次；高回报率鼠标不会把 UI 线程刷爆。
        /// </summary>
        private void RequestNewIconTagDragUpdate()
        {
            if (!newIconPointerDown || newIconTagDragUpdateQueued)
            {
                return;
            }
            newIconTagDragUpdateQueued = true;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                newIconTagDragUpdateQueued = false;
                if (!newIconPointerDown)
                {
                    return;
                }
                UpdateNewIconTagDragPosition(GetMousePositionInPage());
            }), DispatcherPriority.Render);
        }

        private void FinishNewIconTagDragFromMouseState()
        {
            // 16ms 跟手定时器可能比 MouseUp 事件先看到“物理左键已松开”。
            // 如果这时还没进入拖动、也没到长按，仍然是单击：允许它展开/收起；
            // 否则 End(false) 会把这次单击吃掉（Round163 T3a 就是这样红的）。
            bool allowClick = !newIconDragging && !newIconLongPress;
            EndNewIconTagDrag(allowClick);
        }

        private void EndNewIconTagDrag(bool allowClick)
        {
            if (!newIconPointerDown && !newIconDragging)
            {
                return;
            }
            bool suppressClick = newIconLongPress || newIconDragging;
            bool moved = newIconDragging;
            newIconPointerDown = false;
            newIconLongPress = false;
            newIconDragging = false;
            newIconDragSpeed = 0.0;
            newIconTagDragUpdateQueued = false;
            newIconTagCaptureRestoreQueued = false;
            newIconTagLeftUpTicks = 0;
            dragPointerPhysicalFallback = false;
            if (newIconHoldTimer != null)
            {
                newIconHoldTimer.Stop();
            }
            if (newIconDragFollowTimer != null)
            {
                newIconDragFollowTimer.Stop();
            }
            HideNewIconBoundaryGuides();
            if (newIconTag.IsMouseCaptured)
            {
                newIconTag.ReleaseMouseCapture();
            }
            newIconTag.Cursor = Cursors.Hand;
            EndNewIconTagFastDrag();
            if (moved)
            {
                // 松手：先把标签夹回页面，再做一次完整布局（方向/面板一次到位），
                // 最后保存归一化位置。拖动期间没有每帧做这些，所以不会拖慢跟手。
                ClampNewIconTagToPage();
                // 松手夹回页面时仍按最终位置解析方向；如果方向又变，也走同一条 B 过场。
                ApplyNewIconDockLayout();
                HideNewIconBoundaryGuides();
                // 标签拖动也是用户操作；不能再让后台图标加载把状态栏里的
                // “碰到左/右边界…”提示覆盖掉。
                state.UserMoved = true;
                RememberNewIconDockPosition();
            }
            if (allowClick && !suppressClick)
            {
                ToggleNewIconDock();
            }
            newIconTagDragBounds = null;
        }

        private void ToggleNewIconDock()
        {
            if (newIconDockContent == null)
            {
                return;
            }
            // 收起时如果正好在换向过场里，先收掉旧快照/动画，避免残留。
            CancelNewIconDirectionTransition();
            bool expand = newIconDockContent.Visibility != Visibility.Visible;
            if (expand)
            {
                newIconDockContent.Visibility = Visibility.Visible;
                // 第一次展开时先不可见地把面板排好、拍一张快照丢掉：
                // 这样第一次拖标签触发 B 时，RenderTargetBitmap/面板排版/卡片图
                // 都已经预热过，不会把一次性的冷启动成本压在第一次拖动上。
                newIconDockContent.Opacity = 1.0;
                UpdateNewIconChevronGlyph();
                ApplyNewIconDockLayout(false);
                WarmupNewIconPanelSnapshot();
                WarmupNewIconDirectionTransition();
                // 预热在同一帧内完成，用户看不到；随后再走正常的淡入。
                newIconDockContent.Opacity = 0.0;
                newIconDockContent.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(120))));
            }
            else
            {
                newIconDockContent.BeginAnimation(UIElement.OpacityProperty, null);
                newIconDockContent.Opacity = 1.0;
                newIconDockContent.Visibility = Visibility.Collapsed;
                // 只改箭头字形，不重建整个标签，也不在“第一次展开”时重算方向。
                // 第一次展开会让框的测量结果从不完整变完整，若这时重算方向/重建标签，
                // 用户看到的就是标签闪一下。
                UpdateNewIconChevronGlyph();
                ApplyNewIconDockLayout(false);
            }
            statusText.Text = expand
                ? AppText.T(DesktopLayoutTextKeys.NewIconExpanded)
                : AppText.T(DesktopLayoutTextKeys.NewIconCollapsed);
        }

        private void WarmupNewIconPanelSnapshot()
        {
            if (newIconPanelSnapshotWarmed || newIconDockContent == null)
            {
                return;
            }
            newIconPanelSnapshotWarmed = true;
            try
            {
                double width = GetNewIconPanelWidthForDrag();
                double height = GetNewIconPanelHeightForDrag();
                if (width > 1.0 && height > 1.0)
                {
                    RenderTargetBitmap warmup = RenderNewIconPanelBitmap(width, height);
                    // 只是为了触发排版/光栅化/卡片图；不保留。
                    GC.KeepAlive(warmup);
                }
            }
            catch
            {
                // 预热失败不影响正式交互。
            }
        }

        /// <summary>
        /// 第一次展开时在同一个 Dispatcher 操作里跑一遍隐藏的 B 过场：
        /// 旧/新快照、ghost、裁切、RectAnimation、DoubleAnimation 都是一次性 JIT/首次渲染，
        /// 先把它们走一遍再 Cancel，正式第一次拖标签时就不会把这段冷启动压在跟手上。
        /// 面板此时 Opacity=1 但还没到渲染帧，走完立刻 Cancel 并恢复原方向，用户看不到。
        /// </summary>
        private void WarmupNewIconDirectionTransition()
        {
            if (newIconDockContent == null
                || newIconDockContent.Visibility != Visibility.Visible
                || newIconDockContent.ActualWidth <= 1.0
                || newIconDockContent.ActualHeight <= 1.0)
            {
                return;
            }
            NewIconDockDirection originalDirection = newIconDockDirection;
            NewIconDockDirection otherDirection =
                (originalDirection == NewIconDockDirection.Up
                    || originalDirection == NewIconDockDirection.Down)
                ? NewIconDockDirection.Right
                : NewIconDockDirection.Down;
            try
            {
                if (TryBeginNewIconDirectionTransition(originalDirection))
                {
                    newIconDockDirection = otherDirection;
                    ApplyNewIconDockLayout(false);
                    CancelNewIconDirectionTransition();
                }
            }
            catch
            {
                CancelNewIconDirectionTransition();
            }
            finally
            {
                newIconDockDirection = originalDirection;
                ApplyNewIconContentLayout();
                ApplyNewIconDockLayout(false);
                HideNewIconBoundaryGuides();
            }
        }

        private void AttachTrayDrag(Border card, DesktopIconPlacement item)
        {
            DispatcherTimer followTimer = null;
            Action<Point> updateDrag = null;
            Action<bool> finishDrag = null;
            Action requestTrayUpdate = null;
            bool trayOverNewIcon = false;
            Rect trayPageBounds = Rect.Empty;
            bool trayUpdateQueued = false;
            bool captureRestoreQueued = false;
            int leftUpTicks = 0;

            updateDrag = delegate(Point windowPoint)
            {
                if (!trayDragging)
                {
                    return;
                }
                if (dragGhost == null
                    && (Math.Abs(windowPoint.X - trayDragStart.X) > 4
                        || Math.Abs(windowPoint.Y - trayDragStart.Y) > 4))
                {
                    // 新图标卡片从标签面板里拖出来，起点在画布外，所以按整页夹取；
                    // 松手后仍由 PlaceNewIcon 放到预桌面网格。
                    ShowTrayDragGhost(item, windowPoint, trayPageBounds);
                }
                if (dragGhost != null)
                {
                    PositionTrayDragGhost(windowPoint, trayPageBounds);
                    // 拖动过程中就告诉用户“现在松手会进新图标”，
                    // 不能只在松手时判断；边缘发光由 NewIconDropGlow 统一处理。
                    bool over = IsNewIconDropPoint(windowPoint);
                    if (over != trayOverNewIcon)
                    {
                        trayOverNewIcon = over;
                        SetNewIconDropHighlight(over);
                        statusText.Text = over
                            ? AppText.T(DesktopLayoutTextKeys.NewIconReleaseKeep)
                            : AppText.T(DesktopLayoutTextKeys.NewIconDragToCanvas);
                    }
                }
            };

            requestTrayUpdate = delegate
            {
                if (!trayDragging || trayUpdateQueued)
                {
                    return;
                }
                trayUpdateQueued = true;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    trayUpdateQueued = false;
                    if (trayDragging)
                    {
                        updateDrag(GetMousePositionInWindow());
                    }
                }), DispatcherPriority.Render);
            };

            finishDrag = delegate(bool allowDrop)
            {
                if (!trayDragging)
                {
                    return;
                }
                trayDragging = false;
                trayUpdateQueued = false;
                captureRestoreQueued = false;
                leftUpTicks = 0;
                if (followTimer != null)
                {
                    followTimer.Stop();
                }
                Point windowPoint = GetMousePositionInWindow();
                Point canvasPoint = GetMousePositionInDesktopCanvas();
                dragPointerPhysicalFallback = false;
                bool overNewIcon = IsNewIconDropPoint(windowPoint);
                if (card.IsMouseCaptured)
                {
                    card.ReleaseMouseCapture();
                }
                card.Cursor = Cursors.Hand;
                bool hadGhost = dragGhost != null;
                HideTrayDragGhost();
                SetNewIconDropHighlight(false);
                ClearNewIconDropTargets();
                trayOverNewIcon = false;
                if (hadGhost && allowDrop)
                {
                    if (overNewIcon)
                    {
                        statusText.Text = AppText.T(
                            DesktopLayoutTextKeys.NewIconStayed);
                    }
                    else
                    {
                        // 【2026-10-25 用户要求】不再要求必须落在预桌面矩形内：
                        // 画布外松手也放到预桌面内靠鼠标最近的边缘（PlaceNewIcon 会夹网格）。
                        PlaceNewIcon(item, canvasPoint);
                    }
                }
            };

            card.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (state.Saving)
                {
                    return;
                }
                trayDragging = true;
                trayOverNewIcon = false;
                trayDragItem = item;
                trayDragStart = args.GetPosition(this);
                // 页面矩形/原点只在按下时取一次，拖动中不每帧 PointFromScreen。
                trayPageBounds = GetPageOverlayBounds();
                trayUpdateQueued = false;
                captureRestoreQueued = false;
                leftUpTicks = 0;
                dragPointerPhysicalFallback = false;
                CaptureNewIconDropTargets();
                SetNewIconDropHighlight(false);
                statusText.Text = AppText.T(DesktopLayoutTextKeys.NewIconDragStart);
                desktopCanvas.Focus();
                card.Cursor = Cursors.SizeAll;
                card.CaptureMouse();
                if (followTimer == null)
                {
                    followTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(16)
                    };
                    followTimer.Tick += delegate
                    {
                        if (!trayDragging)
                        {
                            followTimer.Stop();
                            return;
                        }
                        if (!NativeMethods.IsLeftMouseButtonDown())
                        {
                            // 连续两个 tick 确认，避免瞬时误判把拖动打断。
                            leftUpTicks++;
                            if (leftUpTicks >= 2)
                            {
                                finishDrag(true);
                            }
                            return;
                        }
                        leftUpTicks = 0;
                        requestTrayUpdate();
                    };
                }
                followTimer.Start();
                args.Handled = true;
            };
            card.MouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!trayDragging)
                {
                    return;
                }
                if (!NativeMethods.IsLeftMouseButtonDown())
                {
                    // 交给 16ms 看门狗连续确认；MouseMove 单次不再结束拖动。
                    return;
                }
                requestTrayUpdate();
            };
            card.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                // 捕获短暂丢失时 WPF 可能补一个假 Up；物理左键还按着就忽略。
                if (NativeMethods.IsLeftMouseButtonDown())
                {
                    if (!card.IsMouseCaptured)
                    {
                        card.CaptureMouse();
                    }
                    args.Handled = true;
                    return;
                }
                finishDrag(true);
                args.Handled = true;
            };
            card.LostMouseCapture += delegate
            {
                if (!trayDragging)
                {
                    return;
                }
                // 拖动生命周期不依赖捕获；先切物理光标，只重捕获一次。
                dragPointerPhysicalFallback = true;
                if (NativeMethods.IsLeftMouseButtonDown() && !state.Saving && !state.Closed)
                {
                    if (captureRestoreQueued)
                    {
                        return;
                    }
                    captureRestoreQueued = true;
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        captureRestoreQueued = false;
                        if (trayDragging && !card.IsMouseCaptured
                            && NativeMethods.IsLeftMouseButtonDown())
                        {
                            card.CaptureMouse();
                        }
                    }), DispatcherPriority.Input);
                    return;
                }
                finishDrag(true);
            };
        }

        private void ShowTrayDragGhost(DesktopIconPlacement item, Point windowPoint, Rect bounds)
        {
            // 【2026-10-24 修“第一次拖新图标没有图标”】
            // 图片缓存由 DesktopLayoutIconImageCache 统一收口：缓存没有就同步补一次，
            // 不会出现“第一次只有文件名、第二次才有图标”。
            BitmapSource source = imageCache.EnsureSource(item);
            // 拖拽中显示“正常桌面图标”，不是白底文件名卡片。
            // 尺寸跟随当前画布预览缩放；坐标只影响视觉，落点仍用保存布局网格另算。
            double scale = canvasScale == null ? 1.0 : canvasScale.ScaleX;
            if (double.IsNaN(scale) || scale <= 0.05)
            {
                scale = 1.0;
            }
            double ghostWidth = Math.Max(20.0, gridWidth * scale);
            double ghostHeight = Math.Max(20.0, gridHeight * scale);
            double iconPixels = Math.Max(8.0, iconSize * scale);
            Canvas content = new Canvas
            {
                Width = ghostWidth,
                Height = ghostHeight,
                IsHitTestVisible = false,
                ClipToBounds = false
            };
            // 自检/读屏用：浮卡本身的边界。Border 没有自动化 peer，
            // 用透明 TextBlock 暴露范围；不参与绘制和命中测试。
            TextBlock ghostBoundsProbe = new TextBlock
            {
                Width = ghostWidth,
                Height = ghostHeight,
                Opacity = 0.0,
                IsHitTestVisible = false,
                Focusable = false
            };
            AutomationProperties.SetName(ghostBoundsProbe,
                AppText.F(
                    DesktopLayoutTextKeys.NewIconDragBoundsAutomationFormat,
                    item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback)));
            Canvas.SetLeft(ghostBoundsProbe, 0.0);
            Canvas.SetTop(ghostBoundsProbe, 0.0);
            content.Children.Add(ghostBoundsProbe);
            if (source != null)
            {
                Image icon = new Image
                {
                    Source = source,
                    Width = iconPixels,
                    Height = iconPixels,
                    Stretch = Stretch.Uniform,
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.Linear);
                Canvas.SetLeft(icon, (ghostWidth - iconPixels) / 2.0);
                Canvas.SetTop(icon, iconTop * scale);
                content.Children.Add(icon);
            }
            TextBlock name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? AppText.T(DesktopLayoutTextKeys.IconFallback)
                    : item.DisplayName,
                FontSize = Math.Max(6.0, labelFontSize * scale),
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = ghostWidth,
                MaxHeight = Math.Max(12.0, ghostHeight - (iconTop + iconSize) * scale),
                IsHitTestVisible = false
            };
            name.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = Math.Max(2.0, 4.0 * scale),
                ShadowDepth = Math.Max(1.0, 1.0 * scale),
                Opacity = 0.85
            };
            Canvas.SetLeft(name, 0.0);
            Canvas.SetTop(name, (iconTop + iconSize + 1.0) * scale);
            content.Children.Add(name);
            dragGhost = new Border
            {
                Width = ghostWidth,
                Height = ghostHeight,
                Background = null,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Child = content,
                IsHitTestVisible = false,
                Opacity = 0.94,
                Tag = iconTop * scale + iconPixels / 2.0
            };
            // 真实鼠标探针靠这个名字量浮卡矩形；不参与命中测试。
            AutomationProperties.SetName(dragGhost,
                AppText.F(
                    DesktopLayoutTextKeys.NewIconDragAutomationFormat,
                    item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback)));
            dragGhostBounds = null;
            dragOverlay.Children.Add(dragGhost);
            dragOverlay.Visibility = Visibility.Visible;
            PositionTrayDragGhost(windowPoint, bounds);
        }

        private void PositionTrayDragGhost(Point windowPoint, Rect bounds)
        {
            if (dragGhost == null)
            {
                return;
            }
            double iconCenterOffset = dragGhost.Tag is double
                ? (double)dragGhost.Tag
                : dragGhost.Height / 2.0;
            double desiredLeft = windowPoint.X - dragGhost.Width / 2.0;
            double desiredTop = windowPoint.Y - iconCenterOffset;
            if (dragGhostBounds == null)
            {
                dragGhostBounds = new EditorDragBounds(dragGhost.Width, dragGhost.Height);
                dragGhostBounds.Begin(
                    windowPoint.X, windowPoint.Y,
                    desiredLeft, desiredTop,
                    bounds);
            }
            else
            {
                dragGhostBounds.MoveTo(windowPoint.X, windowPoint.Y, bounds);
            }
            Canvas.SetLeft(dragGhost, dragGhostBounds.X);
            Canvas.SetTop(dragGhost, dragGhostBounds.Y);
        }

        private void HideTrayDragGhost()
        {
            if (dragGhost != null)
            {
                dragOverlay.Children.Remove(dragGhost);
                dragGhost = null;
            }
            dragGhostBounds = null;
            if (dragOverlay != null)
            {
                dragOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void PlaceNewIcon(DesktopIconPlacement item, Point canvasPoint)
        {
            Border tile;
            bool hadTile = tileByItem.TryGetValue(item, out tile);
            if (!hadTile)
            {
                // 新图标一开始不在画布上，只有从标签拖进来时才创建它的 tile。
                AddIconTile(item);
                Image trayIcon;
                if (imageCache.TryGetTrayImage(item, out trayIcon) && trayIcon.Source != null)
                {
                    Image tileIcon;
                    if (imageCache.TryGetCanvasImage(item, out tileIcon))
                    {
                        tileIcon.Source = trayIcon.Source;
                    }
                    Border tileSkeleton;
                    if (iconSkeletons.TryGetValue(item, out tileSkeleton))
                    {
                        tileSkeleton.Visibility = Visibility.Collapsed;
                    }
                }
                if (!tileByItem.TryGetValue(item, out tile))
                {
                    return;
                }
            }
            // 撤销“拖入新图标”时 tile 只是被隐藏；再次拖入要重新显示，
            // 不能再 AddIconTile（否则同一图标会有两个 tile）。
            tile.Visibility = Visibility.Visible;
            // 不论 tile 是刚建的还是撤销后重新显示的，都从统一图片缓存补一次真实图标。
            ApplyCachedNewIconToTile(item);
            // 释放瞬间只按当前画布网格算最近格子，绝不沿用拖入前的 X/Y 或子格偏移。
            // 关键：Windows 网格原点是 (20,2) 这类偏移，不是 (0,0)；
            // 必须用从当前图标统计出的 gridOriginX/Y，否则会整体错 20/2 像素。
            int maxCol = Math.Max(0, (int)Math.Floor(
                Math.Max(0.0, canvasWidth - gridOriginX - gridWidth) / gridWidth));
            int maxRow = Math.Max(0, (int)Math.Floor(
                Math.Max(0.0, canvasHeight - gridOriginY - gridHeight) / gridHeight));
            int column = (int)Math.Round(
                (canvasPoint.X - gridOriginX - gridWidth / 2.0) / gridWidth,
                MidpointRounding.AwayFromZero);
            int row = (int)Math.Round(
                (canvasPoint.Y - gridOriginY - gridHeight / 2.0) / gridHeight,
                MidpointRounding.AwayFromZero);
            column = Math.Max(0, Math.Min(maxCol, column));
            row = Math.Max(0, Math.Min(maxRow, row));
            double left = gridOriginX + column * gridWidth;
            double top = gridOriginY + row * gridHeight;
            left = Clamp(left, 0, Math.Max(0, canvasWidth - gridWidth));
            top = Clamp(top, 0, Math.Max(0, canvasHeight - gridHeight));

            DesktopIconPositionsState before = CapturePositions();
            bool wasUnplaced = unplacedNewItems.Contains(item);
            double freeLeft;
            double freeTop;
            // 目标格被占用时，从最近的格子向外找空位；不再保留旧 tile 的偏移。
            if (TryFindFreeCell(left, top, item, false, left, top, out freeLeft, out freeTop))
            {
                left = freeLeft;
                top = freeTop;
            }
            Canvas.SetLeft(tile, left);
            Canvas.SetTop(tile, top);
            item.X = (int)Math.Round(left) + virtualBounds.Left;
            item.Y = (int)Math.Round(top) + virtualBounds.Top;
            if (wasUnplaced)
            {
                PushUndoState(before);
            }
            UpdatePlacementOffsets(item);
            state.MarkLayoutChanged();
            if (wasUnplaced)
            {
                MarkNewIconPlaced(item, true);
            }
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.NewIconPlacedFormat,
                item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback));
        }

        private bool MoveTileToNewIconDock(DesktopIconPlacement item, Border tile)
        {
            if (item == null || tile == null || state.Saving || IsMissingIcon(item))
            {
                return false;
            }
            if (!snapshot.Icons.Contains(item))
            {
                snapshot.Icons.Add(item);
            }
            if (!unplacedNewItems.Contains(item))
            {
                unplacedNewItems.Add(item);
            }
            Image image;
            if (imageCache.TryGetCanvasImage(item, out image) && image != null)
            {
                BitmapSource bitmap = image.Source as BitmapSource;
                if (bitmap != null)
                {
                    imageCache.SetNewIconSource(item, bitmap);
                }
            }
            // 【2026-10-25 修“图标被吞、Ctrl+Z 回不来”】
            // 这里**只隐藏** tile，不把它从 tileByItem / canvasImages / skeleton /
            // desktopCanvas 里删掉。RestorePositions 只会更新现存的 tile；删掉以后
            // 即使有撤销记录也没有控件可以重新显示，看起来就是图标被吞掉。
            // 隐藏的 tile 还能被 PlaceNewIcon 复用（拖回画布时不会重复建）。
            tile.Visibility = Visibility.Collapsed;
            tile.Opacity = 1.0;
            tile.Background = Brushes.Transparent;
            tile.BorderBrush = GetIconTileBorder(item);
            Panel.SetZIndex(tile, 10);
            // 没有在画布上处理位置：保存时从默认空位排序开始找。
            item.X = -1000000;
            item.Y = -1000000;
            RebuildNewIconCards();
            UpdateNewIconCount(false);
            if (newIconDock != null)
            {
                newIconDock.Visibility = Visibility.Visible;
                newIconDock.Opacity = 1.0;
            }
            if (!imageCache.HasNewIconSource(item))
            {
                List<DesktopIconPlacement> one = new List<DesktopIconPlacement>();
                one.Add(item);
                StartIconLoadingForItems(one, false);
            }
            state.MarkLayoutChanged();
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.NewIconMovedInFormat,
                item.DisplayName ?? AppText.T(DesktopLayoutTextKeys.IconFallback));
            return true;
        }
    }
}
