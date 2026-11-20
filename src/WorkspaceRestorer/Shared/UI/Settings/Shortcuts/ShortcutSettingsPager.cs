using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 设置页“快捷键”表格底部的分页条。
    ///
    /// 2026-11-19 用户要求照 ComfyUI 的 KeybindingPanel：
    ///   · 默认每页 10 条，可切 5 / 10 / 25 / 50；
    ///   · 底部一直显示分页条（只有一页也显示）；
    ///   · 第一页 / 上一页 / 页码 1–5 / 下一页 / 最后一页 + “每页”下拉；
    ///   · 整条居中，边界箭头禁用，当前页高亮。
    ///
    /// 页码窗口算法与 PrimeVue Paginator 的 calculatePageLinkBoundaries 一致；
    /// 本类只负责分页条本身，表格数据仍由设置页按页切片后交给它。
    /// </summary>
    internal sealed class ShortcutSettingsPager : StackPanel
    {
        internal const int DefaultPageSize = ShortcutPageSettings.DefaultPageSize;
        internal const int PageLinkSize = 5;

        private const double PagerButtonSize = 32.0;
        // 公共下拉框模板内部有 12/34 的左右留白和 36 的最小高度；
        // 70 宽、32 高会裁掉右侧箭头和底部边框，所以给足 88×38。
        private const double PageSizeBoxWidth = 88.0;

        private readonly StackPanel pageLinksHost;
        private readonly Button firstButton;
        private readonly Button previousButton;
        private readonly Button nextButton;
        private readonly Button lastButton;
        private readonly TextBlock pageSizeLabel;
        private readonly ComboBox pageSizeBox;
        private readonly List<Button> pageLinkButtons = new List<Button>();
        private readonly Style pageButtonStyle;
        private readonly Style currentPageButtonStyle;
        private int[] currentPageLinks = new int[0];
        private bool updatingControls;

        public event EventHandler PageChanged;
        public event EventHandler PageSizeChanged;

        public int TotalCount { get; private set; }
        public int PageSize { get; private set; }
        public int CurrentPage { get; private set; }
        public int PageCount { get; private set; }

        public ShortcutSettingsPager()
        {
            Orientation = Orientation.Horizontal;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(0, 8, 0, 0);

            pageButtonStyle = CreatePageButtonStyle();
            currentPageButtonStyle = (Style)Application.Current.Resources["AccentButton"];

            firstButton = BuildNavigationButton(
                "\uE892", SettingsToolTips.PagerFirst, "ShortcutPagerFirst");
            previousButton = BuildNavigationButton(
                "\uE76B", SettingsToolTips.PagerPrevious, "ShortcutPagerPrev");
            nextButton = BuildNavigationButton(
                "\uE76C", SettingsToolTips.PagerNext, "ShortcutPagerNext");
            lastButton = BuildNavigationButton(
                "\uE893", SettingsToolTips.PagerLast, "ShortcutPagerLast");

            firstButton.Click += delegate { GoToPage(1); };
            previousButton.Click += delegate { GoToPage(CurrentPage - 1); };
            nextButton.Click += delegate { GoToPage(CurrentPage + 1); };
            lastButton.Click += delegate { GoToPage(PageCount); };

            Children.Add(firstButton);
            Children.Add(previousButton);

            pageLinksHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Children.Add(pageLinksHost);

            Children.Add(nextButton);
            Children.Add(lastButton);

            pageSizeLabel = new TextBlock
            {
                FontSize = AppFonts.Caption,
                Foreground = AppTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(12, 0, 6, 0)
            };
            LocalizedText.Text(pageSizeLabel, SettingsTextKeys.ShortcutsPagerRowsLabel);
            Children.Add(pageSizeLabel);

            pageSizeBox = new ComboBox
            {
                Width = PageSizeBoxWidth,
                Height = 38,
                MinHeight = 36,
                VerticalAlignment = VerticalAlignment.Center
            };
            for (int index = 0; index < ShortcutPageSettings.PageSizeOptions.Length; index++)
            {
                pageSizeBox.Items.Add(ShortcutPageSettings.PageSizeOptions[index]);
            }
            AutomationProperties.SetAutomationId(pageSizeBox, "ShortcutPagerRows");
            AccessibleToolTip.Attach(pageSizeBox, SettingsToolTips.PagerRows);
            pageSizeBox.SelectionChanged += OnPageSizeChanged;
            // 底栏贴着窗口底部，公共下拉模板默认向下弹会把 25/50 推出屏幕；
            // 这里只给这个下拉框改成向上弹，公共模板本身不动。
            pageSizeBox.Loaded += delegate { EnsurePageSizeDropDownPlacement(); };
            pageSizeBox.DropDownOpened += delegate { EnsurePageSizeDropDownPlacement(); };
            Children.Add(pageSizeBox);

            SetState(0, DefaultPageSize, 1);
        }

        /// <summary>
        /// 回填总条数、每页条数和当前页。只更新控件，不触发 PageChanged / PageSizeChanged，
        /// 避免“设置页刷新 → 分页条事件 → 设置页刷新”的递归。
        /// </summary>
        public void SetState(int totalCount, int pageSize, int currentPage)
        {
            int safeTotal = Math.Max(0, totalCount);
            int safePageSize = ShortcutPageSettings.NormalizePageSize(pageSize);
            int pageCount = ComputePageCount(safeTotal, safePageSize);
            int safePage = ClampPage(currentPage, pageCount);

            TotalCount = safeTotal;
            PageSize = safePageSize;
            PageCount = pageCount;
            CurrentPage = safePage;

            updatingControls = true;
            try
            {
                pageSizeBox.SelectedIndex = Array.IndexOf(
                    ShortcutPageSettings.PageSizeOptions, safePageSize);
            }
            finally
            {
                updatingControls = false;
            }

            RebuildPageLinksIfNeeded();
            UpdateNavigationState();
        }

        public static int ComputePageCount(int totalCount, int pageSize)
        {
            if (totalCount <= 0 || pageSize <= 0)
            {
                return 0;
            }
            return (totalCount + pageSize - 1) / pageSize;
        }

        public static int ClampPage(int currentPage, int pageCount)
        {
            if (pageCount <= 0)
            {
                return 1;
            }
            if (currentPage < 1)
            {
                return 1;
            }
            if (currentPage > pageCount)
            {
                return pageCount;
            }
            return currentPage;
        }

        /// <summary>
        /// 返回当前页附近最多 linkCount 个页码（1 起）。
        /// 窗口算法照 PrimeVue：先以当前页居中取窗口，靠近末页时再向左补齐。
        /// </summary>
        public static int[] ComputePageLinks(int currentPage, int pageCount, int linkCount)
        {
            if (pageCount <= 0 || linkCount <= 0)
            {
                return new int[0];
            }

            int visible = Math.Min(linkCount, pageCount);
            int pageIndex = ClampPage(currentPage, pageCount) - 1;
            int start = Math.Max(0, (int)Math.Ceiling(pageIndex - visible / 2.0));
            int end = Math.Min(pageCount - 1, start + visible - 1);
            int delta = linkCount - (end - start + 1);
            start = Math.Max(0, start - delta);

            int[] links = new int[end - start + 1];
            for (int index = 0; index < links.Length; index++)
            {
                links[index] = start + index + 1;
            }
            return links;
        }

        public static int ComputePageForIndex(int itemIndex, int pageSize)
        {
            if (itemIndex < 0 || pageSize <= 0)
            {
                return 1;
            }
            return itemIndex / pageSize + 1;
        }

        public static int GetPageStart(int currentPage, int pageSize, int totalCount)
        {
            if (totalCount <= 0 || pageSize <= 0)
            {
                return 0;
            }
            int pageCount = ComputePageCount(totalCount, pageSize);
            int page = ClampPage(currentPage, pageCount);
            return (page - 1) * pageSize;
        }

        public static int GetPageLength(int currentPage, int pageSize, int totalCount)
        {
            if (totalCount <= 0 || pageSize <= 0)
            {
                return 0;
            }
            int start = GetPageStart(currentPage, pageSize, totalCount);
            int remaining = totalCount - start;
            if (remaining <= 0)
            {
                return 0;
            }
            return Math.Min(pageSize, remaining);
        }

        private static Style CreatePageButtonStyle()
        {
            Style baseStyle = (Style)Application.Current.Resources[typeof(Button)];
            Style style = new Style(typeof(Button), baseStyle);
            style.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Button.BorderBrushProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.MinHeightProperty, 0.0));
            style.Setters.Add(new Setter(Button.WidthProperty, PagerButtonSize));
            style.Setters.Add(new Setter(Button.HeightProperty, PagerButtonSize));
            style.Setters.Add(new Setter(Button.FontSizeProperty, AppFonts.Small));

            Trigger hover = new Trigger { Property = Button.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(
                Button.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.HoverSurface)));
            hover.Setters.Add(new Setter(
                Button.ForegroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.Accent)));
            style.Triggers.Add(hover);
            return style;
        }

        private Button BuildNavigationButton(
            string icon,
            TooltipDefinition definition,
            string automationId)
        {
            Button button = UiFactory.IconButton(icon, definition);
            button.Width = PagerButtonSize;
            button.Height = PagerButtonSize;
            button.MinWidth = 0;
            button.MinHeight = 0;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(1, 0, 1, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        private Button BuildPageButton(int page)
        {
            Button button = new Button
            {
                Width = PagerButtonSize,
                Height = PagerButtonSize,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, 2, 0),
                FontSize = AppFonts.Small,
                Style = pageButtonStyle,
                Tag = page,
                Content = new TextBlock
                {
                    Text = page.ToString(CultureInfo.InvariantCulture),
                    FontFamily = AppFonts.Ui,
                    FontSize = AppFonts.Small,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AutomationProperties.SetAutomationId(
                button,
                "ShortcutPagerPage" + page.ToString(CultureInfo.InvariantCulture));
            AccessibleToolTip.Attach(button, SettingsToolTips.PagerPage(page));
            int targetPage = page;
            button.Click += delegate { GoToPage(targetPage); };
            return button;
        }

        private void GoToPage(int page)
        {
            if (PageCount <= 0)
            {
                return;
            }
            int target = ClampPage(page, PageCount);
            if (target == CurrentPage)
            {
                return;
            }

            CurrentPage = target;
            UpdatePageLinkSelection();
            UpdateNavigationState();

            EventHandler handler = PageChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }

            // 调用方通常在 PageChanged 里刷新表格并重新 SetState；
            // 这里再补一次，保证没人订阅时页码窗口也会跟着滑动。
            RebuildPageLinksIfNeeded();
        }

        private void OnPageSizeChanged(object sender, SelectionChangedEventArgs args)
        {
            if (updatingControls)
            {
                return;
            }

            object selected = pageSizeBox.SelectedItem;
            if (selected == null)
            {
                return;
            }

            int size;
            try
            {
                size = Convert.ToInt32(selected, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return;
            }
            catch (InvalidCastException)
            {
                return;
            }

            if (size <= 0 || size == PageSize)
            {
                return;
            }

            PageSize = size;
            PageCount = ComputePageCount(TotalCount, size);
            CurrentPage = ClampPage(CurrentPage, PageCount);
            RebuildPageLinksIfNeeded();
            UpdateNavigationState();

            EventHandler handler = PageSizeChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void RebuildPageLinksIfNeeded()
        {
            int[] links = ComputePageLinks(CurrentPage, PageCount, PageLinkSize);
            if (PageLinksEqual(links, currentPageLinks))
            {
                UpdatePageLinkSelection();
                return;
            }

            currentPageLinks = links;
            pageLinksHost.Children.Clear();
            pageLinkButtons.Clear();
            for (int index = 0; index < links.Length; index++)
            {
                Button button = BuildPageButton(links[index]);
                pageLinkButtons.Add(button);
                pageLinksHost.Children.Add(button);
            }
            UpdatePageLinkSelection();
        }

        private void UpdatePageLinkSelection()
        {
            for (int index = 0; index < pageLinkButtons.Count; index++)
            {
                Button button = pageLinkButtons[index];
                int page = (int)button.Tag;
                bool selected = page == CurrentPage;
                button.Style = selected ? currentPageButtonStyle : pageButtonStyle;
                button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private void UpdateNavigationState()
        {
            bool hasPages = PageCount > 0;
            firstButton.IsEnabled = hasPages && CurrentPage > 1;
            previousButton.IsEnabled = hasPages && CurrentPage > 1;
            nextButton.IsEnabled = hasPages && CurrentPage < PageCount;
            lastButton.IsEnabled = hasPages && CurrentPage < PageCount;
        }

        private void EnsurePageSizeDropDownPlacement()
        {
            pageSizeBox.ApplyTemplate();
            if (pageSizeBox.Template == null)
            {
                return;
            }
            Popup popup = pageSizeBox.Template.FindName("PART_Popup", pageSizeBox) as Popup;
            if (popup != null)
            {
                popup.Placement = PlacementMode.Top;
            }
        }

        private static bool PageLinksEqual(int[] left, int[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
