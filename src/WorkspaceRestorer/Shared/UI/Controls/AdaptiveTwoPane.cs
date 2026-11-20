using System;
using System.Windows;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【2026-10-19 用户要求】“缩小和放大软件程序的窗口大小，会导致某些按钮会重叠……排版也不美观，
    /// 要不要适应屏幕。”
    ///
    /// 设置页 / 回收站原来是“左边固定 190 的导航 + 右边内容”。窗口一窄，内容区就被压成窄条
    /// （小字变竖排、滑块顶到边上、按钮互相挤）。这里做成**自适应两栏**：
    ///   · 宽的时候：左导航 + 右内容（和原来一样）；
    ///   · 窄的时候：导航变成**顶部一行**（WrapPanel 横向，放不下会自动换行），内容占满整行；
    ///   · 导航里的按钮在竖排时统一宽度、横排时按内容宽度，不会一半宽一半窄。
    ///
    /// 【2026-11-15 用户要求】导航宽度不再固定 190：
    ///   按当前语言最长的一个导航按钮量宽度，再在右侧留 12 DIP 呼吸空间；
    ///   最短 128、最长 180 只是护栏，实际宽度由文字决定。
    ///   这样“总设置 / 通用 / 关于”这类短标签右边不会空一长条。
    ///
    /// 用法：`new AdaptiveTwoPane(navWrapPanel, contentElement, 540)`。
    /// 导航内容重建后调一次 `RefreshLayout()`，重新量文字并排版。
    /// </summary>
    internal sealed class AdaptiveTwoPane : Grid
    {
        private const double MinimumNavigationWidth = 128.0;
        private const double MaximumNavigationWidth = 180.0;
        private const double NavigationRightPadding = 12.0;
        private const double SeparatorSideMargin = 8.0;

        private readonly WrapPanel navigation;
        private readonly FrameworkElement content;
        private readonly double breakpoint;
        private bool narrow;
        private bool applyingLayout;

        public AdaptiveTwoPane(WrapPanel navigation, FrameworkElement content, double breakpoint)
        {
            if (navigation == null)
            {
                throw new ArgumentNullException("navigation");
            }
            if (content == null)
            {
                throw new ArgumentNullException("content");
            }

            this.navigation = navigation;
            this.content = content;
            this.breakpoint = breakpoint;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MinimumNavigationWidth) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Children.Add(navigation);
            Children.Add(content);

            narrow = false;
            Apply(false);
            SizeChanged += delegate(object sender, SizeChangedEventArgs args)
            {
                Apply(ActualWidth > 0 && ActualWidth < breakpoint);
            };
            Loaded += delegate { ApplyLayoutOnly(); };
        }

        /// <summary>当前是不是“窄”模式（给自检/调试看，也方便以后加别的响应式样式）。</summary>
        public bool IsNarrow
        {
            get { return narrow; }
        }

        /// <summary>导航内容重建之后调用：按当前语言重新量宽度并排版。</summary>
        public void RefreshLayout()
        {
            ApplyLayoutOnly();
        }

        private void Apply(bool makeNarrow)
        {
            if (narrow == makeNarrow && Children.Count == 2)
            {
                ApplyLayoutOnly();
                return;
            }
            narrow = makeNarrow;
            ApplyLayoutOnly();
        }

        private void ApplyLayoutOnly()
        {
            if (applyingLayout)
            {
                return;
            }
            applyingLayout = true;
            try
            {
                navigation.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
                if (narrow)
                {
                    // 导航在上面、内容在下面，两栏都占满。
                    ColumnDefinitions[0].Width = new GridLength(0);
                    Grid.SetRow(navigation, 0);
                    Grid.SetColumn(navigation, 0);
                    Grid.SetRowSpan(navigation, 1);
                    Grid.SetColumnSpan(navigation, 2);
                    Grid.SetRow(content, 1);
                    Grid.SetColumn(content, 0);
                    Grid.SetRowSpan(content, 1);
                    Grid.SetColumnSpan(content, 2);
                    content.Margin = new Thickness(0, 12, 0, 0);

                    foreach (UIElement child in navigation.Children)
                    {
                        FrameworkElement element = child as FrameworkElement;
                        if (element == null)
                        {
                            continue;
                        }
                        // 横排时按钮按文字宽度；分隔线在顶栏里会变成一条横杠，直接收起。
                        bool isButton = child is Button;
                        element.Width = isButton ? double.NaN : 0.0;
                        element.Visibility = isButton ? Visibility.Visible : Visibility.Collapsed;
                        element.HorizontalAlignment = HorizontalAlignment.Left;
                    }
                }
                else
                {
                    double navigationWidth = CalculateNavigationWidth();
                    ColumnDefinitions[0].Width = new GridLength(navigationWidth);
                    Grid.SetRow(navigation, 0);
                    Grid.SetColumn(navigation, 0);
                    Grid.SetRowSpan(navigation, 2);
                    Grid.SetColumnSpan(navigation, 1);
                    Grid.SetRow(content, 0);
                    Grid.SetColumn(content, 1);
                    Grid.SetRowSpan(content, 2);
                    Grid.SetColumnSpan(content, 1);
                    content.Margin = new Thickness(18, 0, 0, 0);

                    foreach (UIElement child in navigation.Children)
                    {
                        Button button = child as Button;
                        if (button != null)
                        {
                            button.Width = navigationWidth;
                            button.HorizontalAlignment = HorizontalAlignment.Stretch;
                            continue;
                        }
                        FrameworkElement element = child as FrameworkElement;
                        if (element == null)
                        {
                            continue;
                        }
                        // 分隔线跟随导航宽度；左右各留 8，正好铺满这一列。
                        element.Visibility = Visibility.Visible;
                        element.Width = Math.Max(0.0, navigationWidth - SeparatorSideMargin * 2.0);
                        element.HorizontalAlignment = HorizontalAlignment.Center;
                    }
                }
            }
            finally
            {
                applyingLayout = false;
            }
        }

        /// <summary>
        /// 按当前语言量最长的导航按钮：按钮内容（图标 + 文字 + 内边距）的实际宽度，
        /// 再加 12 DIP 右侧呼吸空间，最后夹在 128–180 之间。
        /// 不写死某个宽度，换语言或改标签后调 RefreshLayout() 就会重新算。
        /// </summary>
        private double CalculateNavigationWidth()
        {
            double widest = 0.0;
            foreach (UIElement child in navigation.Children)
            {
                Button button = child as Button;
                if (button == null)
                {
                    continue;
                }
                // 先松开旧宽度，否则量到的还是上一轮的固定值。
                button.Width = double.NaN;
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (!double.IsNaN(button.DesiredSize.Width) && button.DesiredSize.Width > widest)
                {
                    widest = button.DesiredSize.Width;
                }
            }
            if (widest <= 0.0)
            {
                return MinimumNavigationWidth;
            }
            double width = Math.Ceiling(widest + NavigationRightPadding);
            if (width < MinimumNavigationWidth)
            {
                return MinimumNavigationWidth;
            }
            if (width > MaximumNavigationWidth)
            {
                return MaximumNavigationWidth;
            }
            return width;
        }
    }
}
