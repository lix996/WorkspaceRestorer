using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WorkspaceRestorer.Models;

// 本文件是 MainWindow 的**分部类**：搜索弹窗要用主窗口当 Owner，
// 所以命名空间与 MainWindow 相同（WorkspaceRestorer.UI），物理位置放在公共区的 Controls 里。
namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共区 · 2026-10-19】全程序通用的“搜索”弹窗外壳。
    ///
    /// 用户原话：“回收站的搜索使用启动与恢复的「搜索方案」对应的代码（图标也一样，ui 界面也一样，
    /// 不要和其他搜索与众不同），并将该代码移植到公共文件夹内，自启动应用也享用移植后的公共代码
    /// （自启动应用的搜索代码就删了，不要留下僵尸代码），后续的图标位置保存也能用同一种代码，
    /// 这样后续只用修改公共代码，搜索的方式就能够作用到程序的所有搜索上。”
    ///
    /// 所以这里**只有外壳**：标题、说明、搜索框、结果列表、回车/Esc、高亮样式。
    /// “搜什么数据”和“点一条之后做什么”一律由调用方给（`search` / `pick` 两个委托），
    /// 各工作区不要再各写一套自己的搜索窗口：
    ///  · 「启动与恢复」：`ShowProfileSearchDialog()`（工作方案，显示“同时按时间搜索”勾选框）；
    ///  · 「自启动应用」：`SearchApplicationPlans()`；
    ///  · 「回收站」：`SearchRecycleBin()`（回收站里的内容，点一条跳到它所在的分区）；
    ///  · 以后「图标位置保存」要加搜索，也调这里。
    ///
    /// 匹配规则本身也不在这里，而在公共的 <see cref="Shared.SearchMatch"/>（文字包含 + 数字比较日期）。
    /// 也就是说：**改搜索框样式只用改这个文件；改匹配规则只用改 SearchMatch.cs**，所有工作区一起生效。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>搜索框：占满整行、最多 50 个字（**不要**再用设置页那个 `CreateNumberBox()`，
        /// 它宽 54、MaxLength=4，只够敲一个数字）。</summary>
        private static TextBox CreateSearchTextBox()
        {
            return new TextBox
            {
                Height = 34,
                MaxLength = 50,
                TextAlignment = TextAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        /// <summary>
        /// 通用的搜索弹窗：**数据源与“点一下做什么”由调用方给**。
        ///
        /// 2026-10-17 修的真 bug（历史记录，搬进公共区时原样保留）：这个弹窗原来写死搜
        /// `controller.StartupProfiles`，而「自启动应用」的「搜索方案」也调用它 —— 于是在自启动应用页
        /// 搜出来的是另一个工作区的方案，点结果还会把那边切成当前方案。
        ///
        /// 【2026-10-19 用户要求】三个工作区的搜索必须**一模一样**：
        /// “自启动应用…为什么没有搜索内没有对应的「同时按时间搜索」，后续搜索内添加新的功能时，
        ///  例如其他的搜索方法，都要同步到其余两个上的。”
        /// → 所以这个弹窗的界面**不再有开关**：勾选框、「搜索方案」按钮、结果两行+高亮、
        ///   多个词空格分隔、回车/Esc，全程序只有这一份；匹配与高亮规则在 `SearchMatcher.cs`。
        ///   调用方只提供“这一条叫什么、什么时候保存/删除”和“点一条做什么”。
        ///
        /// `emptyText`：搜不到时那句话。传 null 用默认的“方案”说法；回收站/其它工作区传自己的说法
        /// （自己那份里也要提醒“勾上「同时按时间搜索」”）。
        /// </summary>
        private void ShowSearchDialog(
            string title,
            string description,
            Func<string, bool, List<SearchHit>> search,
            Action<SearchHit> pick,
            string emptyText = null,
            bool showTimeSearch = true,
            string searchButtonKey = null)
        {
            Window dialog = new Window
            {
                Title = title,
                Width = 640,
                Height = 480,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = AppTheme.Surface,
                FontFamily = AppTheme.UiFont
            };

            Grid layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            heading.Children.Add(UiFactory.Heading(title, 16));
            heading.Children.Add(UiFactory.SupportingText(description));
            Grid.SetRow(heading, 0);
            layout.Children.Add(heading);

            // 搜索框原来用的是 `CreateNumberBox()`（那是设置页的数字框：宽 54、**MaxLength = 4**！）
            // —— 于是用户只能敲进 4 个字符，多词搜索根本没法用。现在是真正的搜索框。
            Grid searchRow = new Grid { Margin = new Thickness(0, 12, 0, 6) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox queryBox = CreateSearchTextBox();
            queryBox.Text = string.Empty;
            // 悬浮说明要讲清“多个词空格分隔”，这是用户特意要求强调的一点。
            AccessibleToolTip.Attach(queryBox, ShellToolTips.SearchQuery);
            Grid.SetColumn(queryBox, 0);
            searchRow.Children.Add(queryBox);
            // 搜索按钮文字默认仍是“搜索方案”；快捷键等没有“方案”的调用方可以传自己的 key。
            string buttonKey = string.IsNullOrWhiteSpace(searchButtonKey)
                ? ShellTextKeys.SearchDialogSearch
                : searchButtonKey;
            Button searchButton = UiFactory.CommandButtonKey(
                "\uE721", buttonKey, true);
            searchButton.Margin = new Thickness(8, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(searchButtonKey))
            {
                AccessibleToolTip.Attach(searchButton, ShellToolTips.SearchDialogSearch);
            }
            else
            {
                // 自定义文字时只挂通用说明；按钮内容由图标+文字面板组成，UIA 推不出名字，这里补上。
                AccessibleToolTip.AttachDescription(searchButton, ShellToolTips.SearchDialogSearch);
                AutomationProperties.SetName(searchButton, AppText.T(buttonKey));
            }
            Grid.SetColumn(searchButton, 1);
            searchRow.Children.Add(searchButton);
            Grid.SetRow(searchRow, 1);
            layout.Children.Add(searchRow);

            // 勾选框：默认不勾（用户明确要求）。勾上才把时间一起搜。
            // 2026-11-16：快捷键没有保存/删除时间，调用方传 showTimeSearch=false 时整行不出现；
            // 其它三个工作区不传，仍然保留原来的勾选框。
            CheckBox timeBox = null;
            if (showTimeSearch)
            {
                timeBox = new CheckBox
                {
                    IsChecked = false,
                    Margin = new Thickness(0, 0, 0, 8),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Foreground = AppTheme.TextPrimary
                };
                LocalizedText.Content(timeBox, ShellTextKeys.SearchTimeLabel);
                // 勾选框的内容就是它的含义，用 AttachDescription 只挂说明、不覆盖无障碍名字。
                AccessibleToolTip.AttachDescription(timeBox, ShellToolTips.SearchTimeBox);
                Grid.SetRow(timeBox, 2);
                layout.Children.Add(timeBox);
            }

            StackPanel results = new StackPanel();
            ScrollViewer host = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = results
            };
            Grid.SetRow(host, 3);
            layout.Children.Add(host);

            Action runSearch = null;
            runSearch = delegate
            {
                results.Children.Clear();
                string keyword = queryBox.Text == null ? string.Empty : queryBox.Text.Trim();
                bool includeTime = timeBox != null && timeBox.IsChecked == true;
                List<SearchHit> hits = search == null ? null : search(keyword, includeTime);
                if (hits == null || hits.Count == 0)
                {
                    string message = emptyText;
                    if (string.IsNullOrEmpty(message))
                    {
                        message = includeTime
                            ? AppText.T(ShellTextKeys.SearchEmptyWithTime)
                            : AppText.T(ShellTextKeys.SearchEmptyNoTime)
                              + (showTimeSearch ? AppText.T(ShellTextKeys.SearchEmptyTimeHint) : string.Empty);
                    }
                    else if (!includeTime && showTimeSearch)
                    {
                        // 调用方给的说法（自启动应用/回收站）也统一补一句“还能按时间搜”，
                        // 免得只有「启动与恢复」的用户知道有这个勾选框。
                        message += AppText.T(ShellTextKeys.SearchEmptyTimeHint);
                    }
                    results.Children.Add(new TextBlock
                    {
                        Text = message,
                        Foreground = AppTheme.TextSecondary,
                        Margin = new Thickness(0, 8, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                    return;
                }
                for (int index = 0; index < hits.Count; index++)
                {
                    SearchHit hit = hits[index];
                    Button open = UiFactory.CommandButton(
                        "\uE8A5",
                        hit.Title ?? AppText.T(ShellTextKeys.SearchResultFallback),
                        false);
                    open.HorizontalAlignment = HorizontalAlignment.Stretch;
                    open.HorizontalContentAlignment = HorizontalAlignment.Left;
                    open.Margin = new Thickness(0, 0, 0, 4);
                    // 【2026-10-17 第 45 批】结果行改成两行：第一行名字，第二行“清单 N 项 · 保存时间”，
                    // 命中的字（名字或日期）加亮 —— 用户要求“结果后面要显示日期”“要有高亮部分”。
                    // 调用方给了分段才走两行；没给（自启动应用、回收站）就是单行纯文本。
                    if (hit.TitleSegments != null || hit.DetailSegments != null)
                    {
                        StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
                        content.Children.Add(new TextBlock
                        {
                            Text = "\uE8A5",
                            FontFamily = AppTheme.IconFont,
                            FontSize = 15,
                            Margin = new Thickness(0, 0, 8, 0),
                            Foreground = AppTheme.TextPrimary,
                            VerticalAlignment = VerticalAlignment.Top
                        });
                        StackPanel lines = new StackPanel();
                        lines.Children.Add(BuildSearchText(hit.TitleSegments, hit.Title, 13.5, AppTheme.TextPrimary));
                        if (hit.DetailSegments != null && hit.DetailSegments.Count > 0)
                        {
                            FrameworkElement detailLine = BuildSearchText(hit.DetailSegments, hit.Detail, 11.5, AppTheme.TextSecondary);
                            detailLine.Margin = new Thickness(0, 2, 0, 0);
                            lines.Children.Add(detailLine);
                        }
                        content.Children.Add(lines);
                        open.Content = content;
                    }
                    // 结果按钮的内容是“图标 + 文字”的面板：补上无障碍名字，
                    // 读屏软件与自检才能按名字找到它（自检踩过：按钮没有名字，只找到里面的文字）。
                    AccessibleToolTip.Attach(open, ShellToolTips.SearchResult(hit.Title, hit.Detail));
                    SearchHit capturedHit = hit;
                    open.Click += delegate
                    {
                        if (pick != null)
                        {
                            pick(capturedHit);
                        }
                        dialog.Close();
                    };
                    results.Children.Add(open);
                }
            };

            searchButton.Click += delegate { runSearch(); };
            queryBox.KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    runSearch();
                    args.Handled = true;
                }
            };
            if (timeBox != null)
            {
                // 勾/去勾立刻重搜，不用再点一次「搜索方案」。
                timeBox.Checked += delegate { runSearch(); };
                timeBox.Unchecked += delegate { runSearch(); };
            }
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    dialog.Close();
                }
            };

            runSearch();
            dialog.Content = layout;
            dialog.ShowDialog();
        }

        /// <summary>
        /// 把“原文 + 命中区间”切成若干段，命中段标成高亮。
        /// `offset`：区间是相对哪一段文字算的（例如区间是相对“时间串”，而原文是“清单 7 项 · 时间串”，
        /// 这里传时间串在原文里的起点）。
        ///
        /// 【2026-10-19】从「启动与恢复」的 `ProfileTools` 搬进公共区：三个工作区的搜索结果行
        /// 现在都用同一套“两行 + 高亮”。
        /// </summary>
        private static List<SearchSegment> BuildHighlightSegments(string text, IList<SearchTextRange> ranges, int offset)
        {
            List<SearchSegment> segments = new List<SearchSegment>();
            if (string.IsNullOrEmpty(text))
            {
                return segments;
            }
            List<SearchTextRange> usable = new List<SearchTextRange>();
            if (ranges != null)
            {
                for (int index = 0; index < ranges.Count; index++)
                {
                    SearchTextRange range = ranges[index];
                    int start = range.Start - offset;
                    if (start < 0 || range.Length <= 0 || start >= text.Length)
                    {
                        continue;
                    }
                    int length = Math.Min(range.Length, text.Length - start);
                    usable.Add(new SearchTextRange(start, length));
                }
            }
            usable.Sort(delegate(SearchTextRange left, SearchTextRange right) { return left.Start.CompareTo(right.Start); });
            int cursor = 0;
            for (int index = 0; index < usable.Count; index++)
            {
                SearchTextRange range = usable[index];
                if (range.Start < cursor)
                {
                    continue;   // 叠在一起的区间只认前一段，避免拼出重复文字
                }
                if (range.Start > cursor)
                {
                    segments.Add(new SearchSegment { Text = text.Substring(cursor, range.Start - cursor), Highlight = false });
                }
                segments.Add(new SearchSegment { Text = text.Substring(range.Start, range.Length), Highlight = true });
                cursor = range.Start + range.Length;
            }
            if (cursor < text.Length)
            {
                segments.Add(new SearchSegment { Text = text.Substring(cursor), Highlight = false });
            }
            return segments;
        }

        /// <summary>搜索结果里的一条：显示名 + 悬浮说明 + 调用方自己的数据。</summary>
        /// <remarks>
        /// `TitleSegments` / `DetailSegments`：用户要求“搜索要有高亮部分，勾了日期的话日期也要亮起”，
        /// 所以结果行可以按区间拼 `Run`。谁填谁走两行，不填就是单行纯文本。
        /// </remarks>
        private sealed class SearchHit
        {
            public string Title;
            public string Detail;
            public object Tag;
            public List<SearchSegment> TitleSegments;
            public List<SearchSegment> DetailSegments;
        }

        /// <summary>搜索结果里的一小段文字（高亮＝这次搜索命中的部分）。</summary>
        private sealed class SearchSegment
        {
            public string Text;
            public bool Highlight;
        }

        /// <summary>
        /// 把一段带高亮的文字做成 TextBlock。
        ///
        /// 【2026-10-17 第 46 批】高亮从“主题青加粗 + 20% 底色的硬边方块”换成**圆角药丸**：
        /// 淡青底（`#1F0E7490`）+ 深青字（`#0B5566`）+ 圆角 4 + 左右各 3 像素内缩。
        /// 用户嫌之前那版“看起来不是很高级”，方块贴着字边最容易显廉价，圆角带留白才像设计过的标签。
        /// 用 `InlineUIContainer` 装一个小 `Border` 实现；外面套一层 `ClipToBounds` 的 Border，
        /// 万一是超长名字，溢出的部分会被裁掉而不是糊到行框上。
        /// </summary>
        private static FrameworkElement BuildSearchText(List<SearchSegment> segments, string plainText, double fontSize, Brush foreground)
        {
            TextBlock block = new TextBlock
            {
                FontSize = fontSize,
                Foreground = foreground,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (segments == null || segments.Count == 0)
            {
                block.Text = plainText ?? string.Empty;
            }
            else
            {
                for (int index = 0; index < segments.Count; index++)
                {
                    SearchSegment segment = segments[index];
                    if (segment.Text == null || segment.Text.Length == 0)
                    {
                        continue;
                    }
                    if (!segment.Highlight)
                    {
                        block.Inlines.Add(new Run(segment.Text));
                        continue;
                    }
                    Border pill = new Border
                    {
                        Background = SurfaceTokens.SearchHitBackground,
                        BorderBrush = SurfaceTokens.SearchHitBorder,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Padding = new Thickness(3, 0, 3, 0),
                        Margin = new Thickness(1, 0, 1, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock
                        {
                            Text = segment.Text,
                            FontSize = fontSize,
                            FontWeight = FontWeights.SemiBold,
                            Foreground = SurfaceTokens.SearchHitText,
                            TextWrapping = TextWrapping.NoWrap
                        }
                    };
                    block.Inlines.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.Center });
                }
            }
            // 【无障碍】高亮那一段现在装在 `InlineUIContainer` 里，UIA 拼名字时**不会**把药丸里的字算进来
            // （实测读出来是“清单 1 项 · 北京时间 ”，日期没了）。所以整行显式给一个完整名字，
            // 读屏软件和自检拿到的仍是整句话，药丸只是视觉装饰。
            AutomationProperties.SetName(block, plainText ?? string.Empty);
            // 裁掉溢出：`InlineUIContainer` 不参与 TextBlock 的省略号裁剪，超长名字得自己兜住。
            return new Border
            {
                Child = block,
                ClipToBounds = true,
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }
    }
}
