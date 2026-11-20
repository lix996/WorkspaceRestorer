using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【2026-10-19 用户要求】设置行“?”的详情弹框。
    ///
    /// 用户原话：
    ///   “为了让用户能够复制设置内的悬浮提示的字……小字旁边要有一个问号，和 comfyui 不同的是，
    ///    点击后跳出弹框，点击弹框外或右上角的打叉按钮就可以关闭，在弹框内，可以手动复制，
    ///    用鼠标按住左键就可以选取文字，不要有复制按钮。”
    ///
    /// 【2026-10-20 改成 Popup】说明框原来是独立 Window：关闭时会先切前台再抢回来，闪一下。
    /// Popup 不是激活窗口，点外面只关浮层，不会切前台。
    ///
    /// 【2026-10-20 再修“第一次用闪/卡”】用户反馈第一次点问号闪、拖选卡、点空白取消也卡，
    /// 点几次后又正常 —— 典型的一次性成本（JIT、文字排版、投影、Popup HWND、选中视觉）。
    /// 现在：
    ///  · 说明框的 Popup / 卡片 / 文字控件**只创建一次并复用**，不再每次点击重建；
    ///  · 主窗口空闲时在屏幕外把它真正渲染一次，并预热“选中文字”这条路径；
    ///  · 打开不用淡入动画；点外面/×/Esc 关闭逻辑不变，正文可拖选 + Ctrl+C。
    /// </summary>
    internal static class InfoDialog
    {
        private const double DialogWidth = 470;

        private sealed class DialogParts
        {
            public Border Card;
            public SelectableText Title;
            public SelectableText Body;
            public Button Close;
        }

        private static Popup cachedPopup;
        private static DialogParts cachedParts;
        private static bool prewarmed;

        private static Popup current;
        private static Window currentOwner;
        private static KeyEventHandler ownerKeyDownHandler;
        private static EventHandler ownerDeactivatedHandler;
        private static Stopwatch currentShowWatch;

        public static void Show(Window owner, UIElement anchor, string title, string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
            {
                return;
            }
            CloseCurrent();
            EnsureBuilt();

            cachedParts.Title.Text = title ?? string.Empty;
            cachedParts.Body.Text = detail;
            cachedParts.Body.Select(0, 0);

            Popup popup = cachedPopup;
            popup.PlacementTarget = anchor;
            popup.Placement = PlacementMode.Bottom;
            popup.StaysOpen = false;
            popup.HorizontalOffset = -(DialogWidth - (anchor is FrameworkElement ? ((FrameworkElement)anchor).ActualWidth : 0.0));
            popup.VerticalOffset = 8.0;

            current = popup;
            currentOwner = owner;
            currentShowWatch = Stopwatch.StartNew();
            AttachOwnerHandlers(owner);
            popup.IsOpen = true;
        }

        /// <summary>
        /// 空闲预热：同一个 Popup/卡片只创建一次，屏幕外真正渲染 + 选中一次文字，
        /// 把第一次点击才付的 JIT / 排版 / 投影 / 选中视觉成本提前付掉。
        /// </summary>
        public static void Prewarm(Window owner)
        {
            if (prewarmed || owner == null)
            {
                return;
            }
            prewarmed = true;
            try
            {
                EnsureBuilt();
                Stopwatch watch = Stopwatch.StartNew();
                cachedParts.Title.Text = AppText.T(ShellTextKeys.DialogInfoTitle);
                cachedParts.Body.Text = AppText.T(ShellTextKeys.DialogInfoUsage);
                cachedParts.Card.Opacity = 0.01;
                cachedParts.Card.IsHitTestVisible = false;

                Popup popup = cachedPopup;
                popup.IsOpen = false;
                popup.PlacementTarget = null;
                popup.Placement = PlacementMode.Absolute;
                popup.StaysOpen = true;
                popup.HorizontalOffset = -5000;
                popup.VerticalOffset = -5000;

                IInputElement previousFocus = Keyboard.FocusedElement;
                bool finished = false;
                Action finish = delegate
                {
                    if (finished)
                    {
                        return;
                    }
                    finished = true;
                    // 预热还没收尾，用户已经点了问号、真正打开了说明框：
                    // 这时绝不能把弹框关掉，否则就会“刚打开又闪一下没了”。
                    bool realPopupOpened = current != null;
                    if (!realPopupOpened)
                    {
                        try
                        {
                            cachedParts.Body.Focus();
                            cachedParts.Body.SelectFirstLine();
                            cachedParts.Body.Select(0, 0);
                        }
                        catch (Exception) { }
                        try { popup.IsOpen = false; } catch (Exception) { }
                    }
                    cachedParts.Card.Opacity = 1.0;
                    cachedParts.Card.IsHitTestVisible = true;
                    if (!realPopupOpened)
                    {
                        try
                        {
                            // 把预热时临时拿走的键盘焦点还回去。
                            if (previousFocus is UIElement)
                            {
                                ((UIElement)previousFocus).Focus();
                            }
                            else
                            {
                                owner.Focus();
                            }
                        }
                        catch (Exception) { }
                    }
                    AppLog.Write("[说明框] 预热完成 " + watch.ElapsedMilliseconds + " ms" + (realPopupOpened ? "（用户已打开，保留弹框）" : string.Empty));
                };
                popup.IsOpen = true;
                // 不依赖 Popup.Opened（离屏绝对定位时它可能不触发）；直接排队做预热收尾，
                // 再用定时器兜底，保证不会留一个看不见的 Popup 在屏幕外。
                popup.Dispatcher.BeginInvoke(finish, DispatcherPriority.Loaded);
                DispatcherTimer fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                fallback.Tick += delegate { fallback.Stop(); finish(); };
                fallback.Start();
            }
            catch (Exception exception)
            {
                AppLog.Write("说明框预热失败（不影响使用）。", exception);
            }
        }

        private static void EnsureBuilt()
        {
            if (cachedPopup != null && cachedParts != null)
            {
                return;
            }
            cachedParts = BuildCard(delegate { CloseCurrent(); });
            Popup popup = new Popup
            {
                AllowsTransparency = true,
                // 不用淡入：第一次打开时动画/首帧合成最容易看起来“闪一下”。
                PopupAnimation = PopupAnimation.None,
                StaysOpen = false,
                Child = cachedParts.Card
            };
            popup.Opened += delegate
            {
                if (object.ReferenceEquals(current, popup) && currentShowWatch != null)
                {
                    long openedMs = currentShowWatch.ElapsedMilliseconds;
                    popup.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        AppLog.Write("[说明框] 打开耗时 " + openedMs + " ms（渲染完成 " + currentShowWatch.ElapsedMilliseconds + " ms）");
                    }), DispatcherPriority.Render);
                }
            };
            popup.Closed += delegate
            {
                if (object.ReferenceEquals(current, popup))
                {
                    current = null;
                    currentShowWatch = null;
                }
                DetachOwnerHandlers();
            };
            cachedPopup = popup;
        }

        /// <summary>构建说明卡片（内容与原 Window 版一致）。</summary>
        private static DialogParts BuildCard(Action close)
        {
            DialogParts parts = new DialogParts();

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SelectableText heading = new SelectableText
            {
                FontSize = AppFonts.DialogTitle,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(heading, 0);
            header.Children.Add(heading);

            Button closeButton = BuildCloseButton();
            closeButton.Click += delegate { if (close != null) { close(); } };
            Grid.SetColumn(closeButton, 1);
            header.Children.Add(closeButton);
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            SelectableText body = new SelectableText
            {
                FontSize = AppFonts.DialogBody,
                Foreground = AppTheme.TextPrimary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0)
            };
            ScrollViewer scroller = new ScrollViewer
            {
                Content = body,
                MaxHeight = 430,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0),
                Focusable = false
            };
            Grid.SetRow(scroller, 1);
            layout.Children.Add(scroller);

            TextBlock usage = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 14, 0, 0)
            };
            LocalizedText.Text(usage, ShellTextKeys.DialogInfoUsage);
            Grid.SetRow(usage, 2);
            layout.Children.Add(usage);

            parts.Title = heading;
            parts.Body = body;
            parts.Close = closeButton;
            parts.Card = new Border
            {
                Width = DialogWidth,
                MaxHeight = 620,
                Background = AppTheme.Surface,
                BorderBrush = AppTheme.PopupBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(20, 16, 20, 16),
                Child = layout,
                Effect = BuildShadow()
            };
            return parts;
        }

        private static void AttachOwnerHandlers(Window owner)
        {
            if (owner == null)
            {
                return;
            }
            ownerKeyDownHandler = delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape && current != null)
                {
                    args.Handled = true;
                    CloseCurrent();
                }
            };
            ownerDeactivatedHandler = delegate { CloseCurrent(); };
            owner.PreviewKeyDown += ownerKeyDownHandler;
            owner.Deactivated += ownerDeactivatedHandler;
        }

        private static void DetachOwnerHandlers()
        {
            Window owner = currentOwner;
            currentOwner = null;
            if (owner == null)
            {
                return;
            }
            if (ownerKeyDownHandler != null)
            {
                owner.PreviewKeyDown -= ownerKeyDownHandler;
            }
            if (ownerDeactivatedHandler != null)
            {
                owner.Deactivated -= ownerDeactivatedHandler;
            }
            ownerKeyDownHandler = null;
            ownerDeactivatedHandler = null;
        }

        private static void CloseCurrent()
        {
            Popup previous = current;
            if (previous == null)
            {
                return;
            }
            current = null;
            currentShowWatch = null;
            DetachOwnerHandlers();
            try { previous.IsOpen = false; } catch (Exception) { }
        }

        private static DropShadowEffect BuildShadow()
        {
            DropShadowEffect shadow = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Direction = 270,
                Opacity = 0.22,
                Color = Color.FromRgb(8, 32, 40)
            };
            shadow.Freeze();
            return shadow;
        }

        private static Button BuildCloseButton()
        {
            Button button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Content = new TextBlock
                {
                    Text = "\uE711",
                    FontFamily = AppFonts.Icon,
                    FontSize = AppFonts.TooltipBody,
                    Foreground = AppTheme.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AccessibleToolTip.Attach(button, ShellToolTips.CloseInfo);
            return button;
        }
    }
}
