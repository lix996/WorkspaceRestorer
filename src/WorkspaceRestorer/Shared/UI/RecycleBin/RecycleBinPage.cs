using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.RecycleBin.Core;

// 这个文件是 MainWindow 的**分部类**（partial）的一部分：它要用 MainWindow 里的
// controller / statusText 以及各页面共用的构造助手（WorkspaceBorder、BuildSettingsRow…），
// 所以命名空间必须与 MainWindow 相同（WorkspaceRestorer.UI）。
// 用户第 71 条要求把回收站代码搬进公共区并按英文命名，这里的做法是：
// **物理位置搬进 RecycleBin 文件夹**（Core 放模型与存取、UI 放页面），
// 命名空间保持不动，避免动到启动与恢复那一侧的任何东西。
namespace WorkspaceRestorer.UI
{
    // 全局「回收站」页面。
    //
    // 布局与「设置」页**同构**：左边是分区导航，右边是当前分区的成行内容
    // （左标题+说明、右控件、发丝线分隔），全部复用设置页那套
    // BuildSettingsRow / BuildSettingsSectionHeader / BuildRecycleBinNavButton 用的导航磁贴。
    // 导航**一开始就把三个工作区都列出来**（不是有内容才出现），各自为空时显示一句占位说明。
    //
    // 【2026-10-19】原来导航里还有一个「回收站保留天数」设置分区（点进去只有一句“去上面那条改”），
    // 用户要求“去掉小侧边栏的多余的「回收站保留天数」，并删除相关代码，只保留最顶上的”，
    // 所以现在导航只有三个工作区；保留天数只在最上面那条常驻栏里改，改完立刻按新天数清一次，
    // 之后每 5 分钟自动清一次（到点就删，不必等重开程序）。
    public sealed partial class MainWindow
    {
        private TextBox recycleBinRetentionBox;
        private WrapPanel recycleBinNavHost;
        private StackPanel recycleBinContentHost;
        private AdaptiveTwoPane recycleBinPane;
        private readonly List<CheckBox> recycleBinCheckBoxes = new List<CheckBox>();
        /// <summary>
        /// 回收站里**跨分区**的勾选状态（2026-10-19 用户要求）：
        /// “回收站按钮，全选是小侧边栏的全选（包括启动与恢复，图标位置保存，自启动应用内的所有），
        ///  不是当前小侧边栏页面的全选”。
        /// 只画当前分区时，别的分区的行并不在界面上，所以不能靠“勾选框集合”记状态；
        /// 这里按条目对象记，切分区、刷新后都还在，恢复/彻底删除也照这份算。
        /// </summary>
        private readonly HashSet<RecycleBinEntry> recycleBinSelection = new HashSet<RecycleBinEntry>();
        private readonly Dictionary<string, StackPanel> recycleBinKindPages =
            new Dictionary<string, StackPanel>(StringComparer.Ordinal);
        private string activeRecycleBinSection = RecycleBinKind.StartupProfile;

        /// <summary>回收站“到期自动删除”的定时器（用户 2026-10-19：“到了时间就自动删除”）。</summary>
        private DispatcherTimer recycleBinPurgeTimer;

        /// <summary>常驻在回收站上方的那一条（保留天数 + 全选/恢复/彻底删除）。</summary>
        private Border topBarHost;

        private Border BuildRecycleBinWorkspace()
        {
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            // 【2026-11-15 用户要求】标题下一句话 + 问号，完整旧说明收进问号，顶部操作栏跟着上移。
            heading.Children.Add(UiFactory.HeadingWithHintKey(
                RecycleBinTextKeys.PageTitle,
                RecycleBinTextKeys.PageShort,
                RecycleBinTextKeys.PageSubtitle,
                18));
            Grid.SetRow(heading, 0);
            content.Children.Add(heading);

            // 用户要求（2026-10-10 18:32）：“把这些按钮常驻在回收站上方，这样回收站设置就不需要有了，
            // 而且也能更快的操作。”→ 保留天数 + 全选 / 恢复 / 彻底删除 三个按钮固定在这一行，切分区也不动。
            topBarHost = BuildRecycleBinTopBar();
            Grid.SetRow(topBarHost, 1);
            content.Children.Add(topBarHost);

            // 【2026-10-19 用户要求】窗口缩小时不要把小侧边栏和内容挤在一起：
            // 宽＝左导航 + 右内容；窄＝导航换到顶部一行，内容占满整行（见 AdaptiveTwoPane）。
            recycleBinNavHost = new WrapPanel { Orientation = Orientation.Vertical };
            recycleBinContentHost = new StackPanel();
            ScrollViewer host = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = recycleBinContentHost
            };
            recycleBinPane = new AdaptiveTwoPane(recycleBinNavHost, host, 540)
            {
                Margin = new Thickness(0, 16, 0, 0)
            };
            Grid.SetRow(recycleBinPane, 2);
            content.Children.Add(recycleBinPane);
            // 用户 2026-10-19：“这个天数一定能生效，并到达时间后自动删除。”
            // → 回收站页面一建好就开始“到点自动清”的定时器（见 EnsureRecycleBinPurgeTimer）。
            EnsureRecycleBinPurgeTimer();
            return WorkspaceBorder(content);
        }


        /// <summary>
        /// 回收站的操作按钮条。用户要求（2026-10-09 15:21）：这些操作要放在**三个部分**里，
        /// 不然在分区里想全选、想恢复都不行。所以每个分区页顶部都会放一条（各自独立的控件，
        /// 但接到同一套处理方法上），操作的是"当前分区里勾选的内容"。
        /// </summary>
        /// <summary>
        /// 回收站的操作条：标题 + 一句说明 + 按钮排在**同一行的左边**。
        /// 不用设置行（BuildSettingsRow）来装它：设置行是"左说明 / 右控件"两列，
        /// 说明文字会被挤成一条很窄的竖条（回收站内容区本来就窄），实测会压到左边导航上。
        /// </summary>
        /// <summary>
        /// 常驻在回收站页面**上方**的一条（用户第 67 条要求）：
        /// 左边是“回收站保留天数”，右边是「全选 / 恢复选中 / 彻底删除选中」三个按钮。
        /// 它不随左下角的分区切换而变化，所以“回收站设置”那一节就不必再放操作按钮了。
        /// </summary>
        private Border BuildRecycleBinTopBar()
        {
            Border bar = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 12, 0, 0)
            };
            // 【2026-10-19 用户反馈】“有些按钮没有对齐，距离也像是乱摆放，你自己要注意按钮的排序位置。”
            // 原来用 WrapPanel 从左往右顺排：左边的保留天数/保存/搜索和右边的操作按钮挤在一起，
            // 间距各不相同（还给 WrapPanel 设了 Grid.Column，其实不生效），看着就是乱。
            // 现在改成两列 Grid：**左边**只放设置（保留天数 + 保存），**右边**靠右对齐放
            // 搜索与操作（搜索方案 / 全选 / 彻底删除选中 / 恢复选中），按钮间距统一 8，
            // 全部垂直居中；窗口变窄时右边这一组会自动换行，不会被挤没。
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel retention = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0)
            };
            TextBlock retentionLabel = new TextBlock
            {
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            LocalizedText.Text(retentionLabel, RecycleBinTextKeys.RetentionLabel);
            retention.Children.Add(retentionLabel);
            // 【2026-10-19 用户要求】回收站的说明也按“小字 + 问号”的规矩来：
            // 标签旁边一个问号，悬停看详细、点击弹可复制的说明框。
            Button retentionHelp = HelpHint.CreateHelpButton(RecycleBinToolTips.RetentionHelp);
            retentionHelp.Margin = new Thickness(0, 0, 6, 0);
            retention.Children.Add(retentionHelp);
            if (recycleBinRetentionBox == null)
            {
                recycleBinRetentionBox = new TextBox
                {
                    Width = 54,
                    TextAlignment = TextAlignment.Center,
                    Text = controller.RecycleBinRetentionDays.ToString(CultureInfo.InvariantCulture),
                    MaxLength = 4,
                    VerticalAlignment = VerticalAlignment.Center
                };
                AccessibleToolTip.Attach(recycleBinRetentionBox, RecycleBinToolTips.RetentionBox);
            }
            retention.Children.Add(recycleBinRetentionBox);
            TextBlock retentionUnit = new TextBlock
            {
                FontSize = 13,
                Foreground = AppTheme.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            LocalizedText.Text(retentionUnit, RecycleBinTextKeys.RetentionUnit);
            retention.Children.Add(retentionUnit);
            Button saveRetention = CreateRecycleBinBarButtonKey(RecycleBinTextKeys.RetentionSave, null);
            saveRetention.Margin = new Thickness(8, 0, 0, 0);
            AccessibleToolTip.Attach(saveRetention, RecycleBinToolTips.SaveRetention);
            saveRetention.Click += delegate { SaveRecycleBinRetentionFromBox(); };
            retention.Children.Add(saveRetention);

            // 用户要求（2026-10-10 18:48）：“回收站上面也加一个搜索，功能是相同的，但是就是搜索回收站内的内容。”
            // 【2026-10-19 改】用户：“回收站的搜索使用启动与恢复的「搜索方案」对应的代码（图标也一样，
            // ui 界面也一样，不要和其他搜索与众不同）。”
            // → 和其它工作区**一模一样**的「搜索方案」按钮：同一个公共搜索弹窗
            //   （`Shared/UI/Controls/SearchDialog.cs`），弹窗里按名字/日期找回收站里的内容，
            //   点一条就跳到它所在的分区。
            // 位置：跟“保留天数 + 保存”放在左边这一组（上面那条常驻栏的右半边留给三个操作按钮，
            // 全都放右边会挤成两行、看着更乱）。
            Button searchRecycleBin = CreateRecycleBinBarButtonKey(RecycleBinTextKeys.SearchButton, null);
            searchRecycleBin.Margin = new Thickness(8, 0, 0, 0);
            AccessibleToolTip.Attach(searchRecycleBin, RecycleBinToolTips.Search);
            searchRecycleBin.Click += delegate { SearchRecycleBin(); };
            retention.Children.Add(searchRecycleBin);
            Grid.SetColumn(retention, 0);
            row.Children.Add(retention);

            WrapPanel commands = BuildRecycleBinCommandToolbar(true, true);
            commands.HorizontalAlignment = HorizontalAlignment.Right;
            commands.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(commands, 1);
            row.Children.Add(commands);

            // 【2026-10-19 用户要求】窗口一窄，左边保留天数 + 右边三个操作按钮就挤成好几行、
            // 文字被压成竖排（截图里就是这样）。这里按这一条的**实际宽度**切成两种排法：
            //   · 宽（>= 520 DIP）：保留天数在左、操作按钮靠右，和原来一样一行；
            //   · 窄：保留天数一行，操作按钮换到第二行、从左边开始排（顺序仍是 全选→彻底删除→恢复）。
            // 用 ActualWidth 判断，拖动窗口时会实时切换，不需要重建页面。
            bar.SizeChanged += delegate
            {
                bool stacked = bar.ActualWidth > 0 && bar.ActualWidth < 520;
                if (stacked)
                {
                    Grid.SetRow(retention, 0);
                    Grid.SetColumn(retention, 0);
                    Grid.SetColumnSpan(retention, 2);
                    Grid.SetRow(commands, 1);
                    Grid.SetColumn(commands, 0);
                    Grid.SetColumnSpan(commands, 2);
                    commands.HorizontalAlignment = HorizontalAlignment.Left;
                    commands.Margin = new Thickness(0, 6, 0, 0);
                }
                else
                {
                    Grid.SetRow(retention, 0);
                    Grid.SetColumn(retention, 0);
                    Grid.SetColumnSpan(retention, 1);
                    Grid.SetRow(commands, 0);
                    Grid.SetColumn(commands, 1);
                    Grid.SetColumnSpan(commands, 1);
                    commands.HorizontalAlignment = HorizontalAlignment.Right;
                    commands.Margin = new Thickness(0);
                }
            };

            bar.Child = row;
            return bar;
        }

        /// <summary>
        /// 常驻条上的按钮：`icon` 传 null 就是纯文字按钮（这条栏窄，带图标会挤成两行），
        /// 传图标就按全程序统一的“图标 + 文字”样式做。
        /// </summary>
        private static Button CreateRecycleBinBarButtonKey(string labelKey, string icon)
        {
            if (string.IsNullOrEmpty(icon))
            {
                Button plain = new Button
                {
                    // 比正文按钮窄一点：这一条上要排“保留天数 + 保存 + 搜索 + 三个操作”，
                    // 用户窗口一般 933 DIP 宽（内容区约 630 DIP），间距放宽就会挤成两行。
                    Padding = new Thickness(8, 6, 8, 6),
                    VerticalAlignment = VerticalAlignment.Center
                };
                LocalizedText.Content(plain, labelKey);
                return plain;
            }
            Button button = UiFactory.CommandButtonKey(icon, labelKey, false);
            button.VerticalAlignment = VerticalAlignment.Center;
            return button;
        }

        /// <summary>保存“回收站保留天数”输入框里的值（顶部常驻条用）。</summary>
        private void SaveRecycleBinRetentionFromBox()
        {
            if (recycleBinRetentionBox == null)
            {
                return;
            }
            int days = controller.RecycleBinRetentionDays;
            int parsed;
            if (int.TryParse(recycleBinRetentionBox.Text == null ? string.Empty : recycleBinRetentionBox.Text.Trim(), out parsed)
                && parsed >= 0)
            {
                days = parsed;
            }
            controller.UpdateRecycleBinRetention(days);
            // 用户要求（2026-10-19）：“这个天数一定能生效，并到达时间后自动删除。”
            // → 改完天数立刻按新天数清一次，不用等下一次刷新或重开程序。
            // 0 表示永久保留，Purge 会直接返回 0。
            int purgedNow = controller.PurgeExpiredRecycleBinEntries();
            int effectiveDays = controller.RecycleBinRetentionDays;
            SetRecycleBinStatus(purgedNow > 0
                ? AppText.F(RecycleBinTextKeys.StatusRetentionSavedWithPurgeFormat, effectiveDays, purgedNow)
                : (effectiveDays <= 0
                    ? AppText.T(RecycleBinTextKeys.StatusRetentionSavedPermanent)
                    : AppText.F(RecycleBinTextKeys.StatusRetentionSavedFormat, effectiveDays)));
            RefreshRecycleBinPage();
        }

        private StackPanel BuildRecycleBinCommandBar()
        {
            StackPanel bar = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            // 【2026-11-15 用户要求】分区标题下不再显示“操作”二字：
            // 这三个按钮本身就是操作，标题既占位置又不好看。保留一句话提示 + 问号。
            // 小字和问号的交互走公共区 `Shared/UI/Theme/ToolTips/HelpHint.cs`，和设置页同一份代码。
            bar.Children.Add(HelpHint.BuildHintLine(RecycleBinToolTips.OperationsHelp));
            WrapPanel commands = BuildRecycleBinCommandToolbar(false, false);
            commands.HorizontalAlignment = HorizontalAlignment.Left;
            commands.Margin = new Thickness(0, 8, 0, 0);
            bar.Children.Add(commands);
            return bar;
        }

        /// <summary>
        /// 操作按钮组：全选 / 彻底删除选中 / 恢复选中。
        ///
        /// 【2026-10-19 用户要求】回收站上有两份「全选」，含义不同：
        ///  · 顶部常驻条（`selectAllSections = true`）的全选 = 勾上**三个分区**的全部内容；
        ///  · 分区页里（`false`）的全选 = 只勾当前分区。
        /// `textOnly = true` 给顶部常驻条用：那条栏窄，带图标会挤成两行；分区页里还是图标 + 文字。
        /// </summary>
        private WrapPanel BuildRecycleBinCommandToolbar(bool selectAllSections, bool textOnly)
        {
            WrapPanel commands = new WrapPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            // 顶部条要求 5 个按钮在同一水平线：这里只留右边距；换行时的上下间距由 WrapPanel 的 Margin 负责。
            Thickness gap = new Thickness(0, 0, 8, 0);
            Button selectAll = CreateRecycleBinBarButtonKey(RecycleBinTextKeys.SelectAll, textOnly ? null : "\uE73A");
            Button restoreSelected = CreateRecycleBinBarButtonKey(RecycleBinTextKeys.RestoreSelected, textOnly ? null : "\uE777");
            Button deleteSelected = CreateRecycleBinBarButtonKey(RecycleBinTextKeys.DeleteSelected, textOnly ? null : "\uE74D");
            AccessibleToolTip.Attach(selectAll, RecycleBinToolTips.SelectAll(selectAllSections));
            AccessibleToolTip.Attach(restoreSelected, RecycleBinToolTips.RestoreSelected);
            AccessibleToolTip.Attach(deleteSelected, RecycleBinToolTips.DeleteSelected);
            selectAll.Margin = gap;
            deleteSelected.Margin = gap;
            restoreSelected.Margin = new Thickness(0, 0, 8, 0);
            commands.Children.Add(selectAll);
            // 用户要求（2026-10-10 18:44）：“把所有的全选和恢复选中换一下位置，全选旁边就是删除，
            // 会更方便一些，也符合现在的操作习惯。”→ 顺序：全选 → 彻底删除选中 → 恢复选中。
            commands.Children.Add(deleteSelected);
            commands.Children.Add(restoreSelected);
            selectAll.Click += delegate { ToggleAllRecycleBinChecks(selectAllSections); };
            restoreSelected.Click += delegate { RestoreSelectedRecycleBinEntries(); };
            deleteSelected.Click += delegate { DeleteSelectedRecycleBinEntriesForever(); };
            return commands;
        }

        /// <summary>
        /// 全选 / 取消全选。
        /// `allSections = true`（顶部常驻条）＝ 勾上回收站里三个分区的全部内容；
        /// `false`（分区页里）＝ 只勾当前分区。
        /// 勾选状态记在 `recycleBinSelection` 里，所以切分区、刷新都不会丢；
        /// 切回别的分区时，属于那一页的勾选框会按它恢复勾选状态。
        /// </summary>
        private void ToggleAllRecycleBinChecks(bool allSections)
        {
            IList<RecycleBinEntry> all = controller.RecycleBinEntries;
            List<RecycleBinEntry> targets = new List<RecycleBinEntry>();
            for (int index = 0; all != null && index < all.Count; index++)
            {
                RecycleBinEntry entry = all[index];
                if (entry == null)
                {
                    continue;
                }
                if (!allSections && !string.Equals(KindOf(entry), activeRecycleBinSection, StringComparison.Ordinal))
                {
                    continue;
                }
                targets.Add(entry);
            }
            bool anyUnchecked = false;
            for (int index = 0; index < targets.Count; index++)
            {
                if (!recycleBinSelection.Contains(targets[index]))
                {
                    anyUnchecked = true;
                    break;
                }
            }
            for (int index = 0; index < targets.Count; index++)
            {
                if (anyUnchecked)
                {
                    recycleBinSelection.Add(targets[index]);
                }
                else
                {
                    recycleBinSelection.Remove(targets[index]);
                }
            }
            // 同步当前页面上看得见的勾选框（别的分区的行没画出来，靠 recycleBinSelection 记着）。
            for (int index = 0; index < recycleBinCheckBoxes.Count; index++)
            {
                CheckBox box = recycleBinCheckBoxes[index];
                RecycleBinEntry entry = box == null ? null : box.Tag as RecycleBinEntry;
                if (entry != null)
                {
                    box.IsChecked = recycleBinSelection.Contains(entry);
                }
            }
            SetRecycleBinStatus(targets.Count == 0
                ? AppText.T(RecycleBinTextKeys.StatusSelectEmpty)
                : (anyUnchecked
                    ? (allSections
                        ? AppText.F(RecycleBinTextKeys.StatusSelectAllFormat, targets.Count)
                        : AppText.F(
                            RecycleBinTextKeys.StatusSelectSectionFormat,
                            KindDisplay(activeRecycleBinSection),
                            targets.Count))
                    : AppText.T(RecycleBinTextKeys.StatusDeselectAll)));
        }

        private void RestoreSelectedRecycleBinEntries()
        {
            List<RecycleBinEntry> selected = SelectedRecycleBinEntries();
            if (selected.Count == 0)
            {
                SetRecycleBinStatus(AppText.T(RecycleBinTextKeys.StatusSelectRestoreFirst));
                return;
            }
            int restored = 0;
            int unsupported = 0;
            for (int index = 0; index < selected.Count; index++)
            {
                RecycleBinEntry entry = selected[index];
                if (entry.Profile == null)
                {
                    bool supported = entry.IsPlanTree
                        || string.Equals(entry.Kind, RecycleBinKind.DesktopLayout, StringComparison.Ordinal)
                        || string.Equals(entry.Kind, RecycleBinKind.Application, StringComparison.Ordinal);
                    if (supported && controller.RestoreRecycleBinEntry(entry))
                    {
                        restored++;
                    }
                    else
                    {
                        // 其它工作区的内容由那个类别自己的代码负责还原（回收站不写死）。
                        unsupported++;
                    }
                    continue;
                }
                if (controller.RestoreRecycleBinEntry(entry))
                {
                    restored++;
                }
            }
            RefreshProfileList();
            RefreshApplicationPlanList();
            RefreshDesktopLayoutCards();
            RefreshRecycleBinPage();
            SetRecycleBinStatus(unsupported == 0
                ? AppText.F(RecycleBinTextKeys.StatusRestoredFormat, restored)
                : AppText.F(RecycleBinTextKeys.StatusRestoredPartialFormat, restored, unsupported));
        }

        private void DeleteSelectedRecycleBinEntriesForever()
        {
            List<RecycleBinEntry> selected = SelectedRecycleBinEntries();
            if (selected.Count == 0)
            {
                SetRecycleBinStatus(AppText.T(RecycleBinTextKeys.StatusSelectDeleteFirst));
                return;
            }
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(RecycleBinTextKeys.ConfirmDeleteTitle),
                AppText.F(RecycleBinTextKeys.ConfirmDeleteSelectedFormat, selected.Count))) { return; }
            // 【2026-10-17】彻底删除也要能 Ctrl+Z：条目对象里还留着原始数据，放回去就是恢复。
            List<RecycleBinEntry> deleted = new List<RecycleBinEntry>(selected);
            controller.DeleteRecycleBinEntriesForever(selected);
            RefreshRecycleBinPage();
            SetRecycleBinStatus(AppText.F(RecycleBinTextKeys.StatusDeletedSelectedFormat, selected.Count));
            PushRecycleBinDeleteUndo(
                AppText.F(RecycleBinTextKeys.UndoDeleteSelectedFormat, deleted.Count),
                deleted);
        }

        /// <summary>右键单个条目：恢复 / 彻底删除（用户要求 2026-10-09 15:51）。</summary>
        private void RestoreSingleRecycleBinEntry(RecycleBinEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            if (entry.Profile == null)
            {
                bool supported = entry.IsPlanTree
                    || string.Equals(entry.Kind, RecycleBinKind.DesktopLayout, StringComparison.Ordinal)
                    || string.Equals(entry.Kind, RecycleBinKind.Application, StringComparison.Ordinal);
                if (supported && controller.RestoreRecycleBinEntry(entry))
                {
                    RefreshProfileList();
                    RefreshApplicationPlanList();
                    RefreshDesktopLayoutCards();
                    RefreshRecycleBinPage();
                    SetRecycleBinStatus(AppText.F(
                        RecycleBinTextKeys.StatusRestoredOneFormat,
                        entry.Title ?? AppText.T(RecycleBinTextKeys.RowDeletedContent)));
                }
                else
                {
                    SetRecycleBinStatus(AppText.T(RecycleBinTextKeys.StatusRestoreUnsupported));
                }
                return;
            }
            if (controller.RestoreRecycleBinEntry(entry))
            {
                RefreshProfileList();
                RefreshRecycleBinPage();
                SetRecycleBinStatus(AppText.F(
                    RecycleBinTextKeys.StatusRestoredOneFormat,
                    entry.Title ?? AppText.T(RecycleBinTextKeys.RowDeletedContent)));
            }
            else
            {
                SetRecycleBinStatus(AppText.T(RecycleBinTextKeys.StatusRestoreFailed));
            }
        }

        private void DeleteSingleRecycleBinEntryForever(RecycleBinEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            string name = string.IsNullOrWhiteSpace(entry.Title)
                ? AppText.T(RecycleBinTextKeys.RowUntitled)
                : ("“" + entry.Title + "”");
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(RecycleBinTextKeys.ConfirmDeleteTitle),
                AppText.F(RecycleBinTextKeys.ConfirmDeleteOneFormat, name))) { return; }
            List<RecycleBinEntry> single = new List<RecycleBinEntry>();
            single.Add(entry);
            // 【2026-10-17】单条彻底删除也要能 Ctrl+Z。
            List<RecycleBinEntry> deletedSingle = new List<RecycleBinEntry>();
            deletedSingle.Add(entry);
            controller.DeleteRecycleBinEntriesForever(single);
            RefreshRecycleBinPage();
            SetRecycleBinStatus(AppText.T(RecycleBinTextKeys.StatusDeletedOneFormat));
            PushRecycleBinDeleteUndo(
                AppText.F(
                    RecycleBinTextKeys.UndoDeleteOneFormat,
                    entry.Title ?? AppText.T(RecycleBinTextKeys.RowDeletedContent)),
                deletedSingle);
        }

        /// <summary>
        /// 回收站自己的提示（勾选 / 恢复 / 彻底删除 / 保留天数）。
        /// 【2026-10-19】原来它写进「设置」分区页里的那个 `recycleBinStatusText`，
        /// 那个分区已按用户要求删掉，所以改成写主窗口底部那条公共状态栏
        /// —— 和其它工作区走同一个地方，弹窗关掉以后也看得见。
        /// </summary>
        private void SetRecycleBinStatus(string text)
        {
            if (statusText != null)
            {
                statusText.Text = text == null ? string.Empty : text;
            }
        }

        /// <summary>
        /// 现在勾选的全部条目（可能是跨分区的：顶部「全选」会一次勾上三个分区）。
        /// 顺序按回收站列表本身的顺序，和界面上从上到下一致。
        /// </summary>
        private List<RecycleBinEntry> SelectedRecycleBinEntries()
        {
            List<RecycleBinEntry> selected = new List<RecycleBinEntry>();
            IList<RecycleBinEntry> entries = controller.RecycleBinEntries;
            for (int index = 0; entries != null && index < entries.Count; index++)
            {
                RecycleBinEntry entry = entries[index];
                if (entry != null && recycleBinSelection.Contains(entry))
                {
                    selected.Add(entry);
                }
            }
            return selected;
        }

        /// <summary>把已经从回收站里消失的条目从勾选集合里去掉（彻底删除 / 恢复 / 过期清理之后）。</summary>
        private void PruneRecycleBinSelection()
        {
            if (recycleBinSelection.Count == 0)
            {
                return;
            }
            HashSet<RecycleBinEntry> alive = new HashSet<RecycleBinEntry>();
            IList<RecycleBinEntry> entries = controller.RecycleBinEntries;
            for (int index = 0; entries != null && index < entries.Count; index++)
            {
                if (entries[index] != null)
                {
                    alive.Add(entries[index]);
                }
            }
            recycleBinSelection.RemoveWhere(delegate(RecycleBinEntry candidate)
            {
                return candidate == null || !alive.Contains(candidate);
            });
        }

        /// <summary>
        /// 重画导航与内容。导航**只有三个工作区分区**（`RecycleBinKind.DisplayOrder`，一开始就都在）；
        /// 某个分区还没有内容时显示一句占位说明。
        ///
        /// 【2026-10-19 用户要求】“回收站，去掉小侧边栏的多余的「回收站保留天数」，并删除相关代码，
        /// 只保留最顶上的会生成保留天数（这个天数一定能生效，并到达时间后自动删除）。”
        /// → 这里原来还有一个“设置”分区（点进去只有一句“去最上面那条里改”），
        /// 连同 `RecycleBinSettingsSection` / `BuildRecycleBinSettingsPage` / 那个分区页一起删掉了；
        /// 保留天数只在最上面那条常驻里改（`BuildRecycleBinTopBar` + `SaveRecycleBinRetentionFromBox`）。
        /// </summary>
        private void RefreshRecycleBinPage()
        {
            if (recycleBinNavHost == null || recycleBinContentHost == null)
            {
                return;
            }
            controller.PurgeExpiredRecycleBinEntries();
            PruneRecycleBinSelection();
            if (recycleBinRetentionBox != null)
            {
                recycleBinRetentionBox.Text = controller.RecycleBinRetentionDays.ToString(CultureInfo.InvariantCulture);
            }

            // 按类别归拢（组内按删除时间倒序）
            IList<RecycleBinEntry> entries = controller.RecycleBinEntries;
            Dictionary<string, List<RecycleBinEntry>> byKind = new Dictionary<string, List<RecycleBinEntry>>(StringComparer.Ordinal);
            for (int index = 0; index < entries.Count; index++)
            {
                RecycleBinEntry entry = entries[index];
                if (entry == null)
                {
                    continue;
                }
                string kind = KindOf(entry);
                if (!byKind.ContainsKey(kind))
                {
                    byKind[kind] = new List<RecycleBinEntry>();
                }
                byKind[kind].Add(entry);
            }
            foreach (KeyValuePair<string, List<RecycleBinEntry>> pair in byKind)
            {
                pair.Value.Sort(delegate(RecycleBinEntry left, RecycleBinEntry right)
                {
                    return right.DeletedAtUtc.CompareTo(left.DeletedAtUtc);
                });
            }

            // 导航分区：三个工作区（固定顺序），再加未知类别
            List<string> sections = new List<string>();
            for (int index = 0; index < RecycleBinKind.DisplayOrder.Length; index++)
            {
                sections.Add(RecycleBinKind.DisplayOrder[index]);
            }
            foreach (KeyValuePair<string, List<RecycleBinEntry>> pair in byKind)
            {
                if (!sections.Contains(pair.Key))
                {
                    sections.Add(pair.Key);
                }
            }
            if (!sections.Contains(activeRecycleBinSection))
            {
                activeRecycleBinSection = RecycleBinKind.StartupProfile;
            }

            recycleBinNavHost.Children.Clear();
            for (int index = 0; index < sections.Count; index++)
            {
                string section = sections[index];
                recycleBinNavHost.Children.Add(BuildRecycleBinNavButton(section, "\uE8B7", KindDisplay(section)));
            }
            if (recycleBinPane != null)
            {
                recycleBinPane.RefreshLayout();
            }

            // 内容区
            recycleBinContentHost.Children.Clear();
            recycleBinCheckBoxes.Clear();

            StackPanel page;
            if (!recycleBinKindPages.TryGetValue(activeRecycleBinSection, out page))
            {
                page = new StackPanel();
                recycleBinKindPages[activeRecycleBinSection] = page;
            }
            page.Children.Clear();
            string activeKindKey = RecycleBinTextKeys.KindKey(activeRecycleBinSection);
            page.Children.Add(activeKindKey == null
                ? BuildSettingsSectionHeader(activeRecycleBinSection)
                : BuildSettingsSectionHeaderKey(activeKindKey));
            // 用户要求（2026-10-09 15:21）：三个工作区分区里也要能全选 / 恢复 / 彻底删除，
            // 所以每个分区页顶部都放一条操作按钮（操作的是本分区里勾选的内容）。
            page.Children.Add(BuildRecycleBinCommandBar());
            List<RecycleBinEntry> items;
            if (!byKind.TryGetValue(activeRecycleBinSection, out items) || items.Count == 0)
            {
                page.Children.Add(BuildSettingsRow(
                    HelpDefinition.Create(
                        RecycleBinTextKeys.EmptyTitle,
                        RecycleBinTextKeys.EmptyDetail,
                        RecycleBinTextKeys.EmptyDetail),
                    null));
            }
            else
            {
                // 【2026-10-19】列表只负责按类别显示**全部**内容；搜索改成和「启动与恢复」同一个弹窗
                // （见下面的 `SearchRecycleBin`），不再有“按关键字筛空”的中间状态。
                for (int index = 0; index < items.Count; index++)
                {
                    page.Children.Add(BuildRecycleBinEntryRow(items[index]));
                }
            }
            recycleBinContentHost.Children.Add(page);
        }

        /// <summary>
        /// 回收站的「搜索方案」：与「启动与恢复」「自启动应用」**同一个公共弹窗**
        /// （`Shared/UI/Controls/SearchDialog.cs`），只是数据源换成回收站里的内容，
        /// 点一条之后跳到它所在的分区。
        ///
        /// 用户 2026-10-19：“回收站的搜索使用启动与恢复的「搜索方案」对应的代码
        /// （图标也一样，ui 界面也一样，不要和其他搜索与众不同）。”
        /// 匹配规则也在公共区（`Shared/UI/Controls/SearchMatch.cs`）：名字 / 副标题 / 所属工作区 / 删除日期。
        /// </summary>
        private void SearchRecycleBin()
        {
            ShowSearchDialog(
                AppText.T(RecycleBinTextKeys.SearchTitle),
                AppText.T(RecycleBinTextKeys.SearchDescription),
                // 【2026-10-19 用户要求】三个工作区的搜索完全一致：勾选框、多词 AND、时间词、高亮
                // 都在公共件里；这里只提供“这一条叫什么、什么时候删除、副标题/分区也算文字”。
                delegate(string keyword, bool includeTime)
                {
                    List<SearchHit> hits = new List<SearchHit>();
                    IList<RecycleBinEntry> entries = controller.RecycleBinEntries;
                    for (int index = 0; entries != null && index < entries.Count; index++)
                    {
                        RecycleBinEntry entry = entries[index];
                        if (entry == null)
                        {
                            continue;
                        }
                        string deleted = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                            entry.DeletedAtUtc, AppText.CurrentLanguageTag);
                        Shared.SearchTarget target = new Shared.SearchTarget
                        {
                            Name = entry.Title,
                            // 副标题（清单几项）与所属分区也参与文字搜索，但不高亮。
                            ExtraText = new[] { entry.Subtitle, KindOf(entry) },
                            TimeText = deleted,
                            SavedUtc = entry.DeletedAtUtc,
                            UpdatedUtc = entry.DeletedAtUtc
                        };
                        if (!Shared.SearchMatcher.Matches(target, keyword, includeTime))
                        {
                            continue;
                        }
                        List<SearchTextRange> nameRanges = new List<SearchTextRange>();
                        List<SearchTextRange> timeRanges = new List<SearchTextRange>();
                        Shared.SearchMatcher.Highlight(target, keyword, includeTime, nameRanges, timeRanges);
                        SearchHit hit = new SearchHit();
                        hit.Title = string.IsNullOrWhiteSpace(entry.Title)
                            ? AppText.T(RecycleBinTextKeys.RowDeletedContent)
                            : entry.Title;
                        string prefix = AppText.F(
                            RecycleBinTextKeys.RowSourceFormat,
                            KindDisplay(KindOf(entry)));
                        string timeLabel = " · " + AppText.F(
                            RecycleBinTextKeys.RowDeleteAtFormat, string.Empty).Trim();
                        hit.Detail = deleted.Length == 0 ? prefix : prefix + timeLabel + deleted;
                        hit.TitleSegments = BuildHighlightSegments(hit.Title, nameRanges, 0);
                        hit.DetailSegments = deleted.Length == 0
                            ? BuildHighlightSegments(hit.Detail, null, 0)
                            : BuildHighlightSegments(hit.Detail, timeRanges, -(prefix.Length + timeLabel.Length));
                        hit.Tag = entry;
                        hits.Add(hit);
                    }
                    return hits;
                },
                delegate(SearchHit hit)
                {
                    RecycleBinEntry captured = hit == null ? null : hit.Tag as RecycleBinEntry;
                    if (captured == null)
                    {
                        return;
                    }
                    // 点一条＝切到它所在的分区（和「启动与恢复」点了就切到那个方案是同一个手感）。
                    activeRecycleBinSection = KindOf(captured);
                    RefreshRecycleBinPage();
                    SetRecycleBinStatus(AppText.F(
                        RecycleBinTextKeys.StatusLocatedFormat,
                        captured.Title ?? AppText.T(RecycleBinTextKeys.RowDeletedContent),
                        KindDisplay(KindOf(captured))));
                },
                AppText.T(RecycleBinTextKeys.SearchEmpty));
        }

        /// <summary>
        /// 回收站“到点自动删除”的定时器：用户 2026-10-19 要求
        /// “这个天数一定能生效，并到达时间后自动删除”。
        /// 程序一直开着时，过期条目也要在几分钟内自己消失，不能只在下次启动时才清。
        /// 只在第一次画回收站页面时创建一次；Tick 里清完若有变化就重画列表。
        /// </summary>
        private void EnsureRecycleBinPurgeTimer()
        {
            if (recycleBinPurgeTimer != null)
            {
                return;
            }
            recycleBinPurgeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(5)
            };
            recycleBinPurgeTimer.Tick += delegate
            {
                if (controller == null)
                {
                    return;
                }
                int removed = controller.PurgeExpiredRecycleBinEntries();
                if (removed > 0)
                {
                    RefreshRecycleBinPage();
                    SetRecycleBinStatus(AppText.F(RecycleBinTextKeys.StatusAutoPurgedFormat, removed));
                }
            };
            recycleBinPurgeTimer.Start();
        }

        private Button BuildRecycleBinNavButton(string sectionId, string icon, string label)
        {
            string captured = sectionId;
            // 选中的分区要**一直亮着**（用户 2026-10-19：“小侧边栏要像旁边那样子亮起……常驻亮起”）：
            // 把“是不是当前分区”作为回调交给导航按钮，鼠标移开时它会恢复选中色，而不是擦成透明。
            Button button = CreateSettingsNavButton(
                icon,
                label,
                label,
                delegate { return string.Equals(captured, activeRecycleBinSection, StringComparison.Ordinal); });
            button.Click += delegate
            {
                activeRecycleBinSection = captured;
                RefreshRecycleBinPage();
            };
            return button;
        }

        private Border BuildRecycleBinEntryRow(RecycleBinEntry entry)
        {
            CheckBox box = new CheckBox { Tag = entry, VerticalAlignment = VerticalAlignment.Center };
            box.MinWidth = 0;
            box.Padding = new Thickness(0);
            box.Content = null;
            // 勾选状态存在 recycleBinSelection 里（跨分区都能记住），这里按它回填；
            // 勾/取消时同步回集合，顶部「全选」和「彻底删除选中/恢复选中」都认这一份。
            box.IsChecked = recycleBinSelection.Contains(entry);
            box.Checked += delegate { recycleBinSelection.Add(entry); };
            box.Unchecked += delegate { recycleBinSelection.Remove(entry); };
            AccessibleToolTip.Attach(box, RecycleBinToolTips.RowCheckBox(entry.Title));
            recycleBinCheckBoxes.Add(box);

            string title = string.IsNullOrWhiteSpace(entry.Title)
                ? (entry.Profile == null
                    ? AppText.T(RecycleBinTextKeys.RowDeletedContent)
                    : (entry.Profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback)))
                : entry.Title;
            string deletedAt = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                entry.DeletedAtUtc, AppText.CurrentLanguageTag);
            string subtitle = string.IsNullOrWhiteSpace(entry.Subtitle)
                ? AppText.F(RecycleBinTextKeys.RowDeleteAtFormat, deletedAt)
                : AppText.F(RecycleBinTextKeys.RowSubtitleFormat, entry.Subtitle, deletedAt);

            StackPanel control = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            // 用户要求（2026-10-09 17:47）：这行提示太长、界面太乱 → 改成悬停提示。
            // 行里只留一句最短的结论，细节放到 ToolTip 里。
            string remainingText = entry.RemainingDays < 0
                ? AppText.T(RecycleBinTextKeys.RowPermanent)
                : AppText.F(RecycleBinTextKeys.RowRemainingFormat, entry.RemainingDays);
            TextBlock remaining = new TextBlock
            {
                Text = remainingText,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = entry.RemainingDays < 0 ? StatusTokens.PositiveStrong : StatusTokens.DangerStrong,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            AccessibleToolTip.Attach(remaining, RecycleBinToolTips.Remaining(entry.RemainingDays));
            control.Children.Add(remaining);
            control.Children.Add(box);
            // flat = true：内层不带背景/圆角，让外层 hoverHost 的圆角露出来（悬停亮起就是圆角的）。
            Border innerRow = BuildSettingsRow(title, subtitle, control, true, false);
            // 用户第 78、79 条：“回收站内的还是直角”“我说的是回收站待删除的方案，鼠标悬停时，
            // 颜色还是直角”——**要圆角的是悬停时那块背景色**。
            // 真因：上一轮我把悬停色加在了内层（`BuildSettingsRow` 返回的那一层，直角、
            // 而且它自己还有一层同色背景把外层的圆角挡掉），所以看上去仍是直角。
            // 现在：悬停色只加在**外层这个带圆角（8）的外框**上，
            // 内层不再自带背景色（见 BuildSettingsRow 的 `flat` 参数），圆角就露出来了。
            Border hoverHost = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6, 4, 6, 4),
                Child = innerRow
            };
            // 用户第 80 条：“左边可以了，右边还是直角。”
            // 圆角框只把自己的**背景**画成圆角，里面的内容（那一行）会一直铺到矩形边界，
            // 于是右边看着仍是直角。这里给外框加**尺寸裁剪**，让内容也跟着圆角走
            // （和「自启动应用」方案卡片同一招）。
            hoverHost.ClipToBounds = true;
            hoverHost.SizeChanged += delegate
            {
                hoverHost.Clip = new System.Windows.Media.RectangleGeometry(
                    new Rect(0.0, 0.0, hoverHost.ActualWidth, hoverHost.ActualHeight));
            };
            // 标题在 **innerRow** 里（不是外框），注意别在外框上找（踩过：改为外框后取不到标题）。
            TextBlock titleBlock = null;
            Grid rowGrid = innerRow.Child as Grid;
            StackPanel textPanel = rowGrid == null || rowGrid.Children.Count == 0
                ? null
                : rowGrid.Children[0] as StackPanel;
            if (textPanel != null && textPanel.Children.Count > 0)
            {
                titleBlock = textPanel.Children[0] as TextBlock;
            }
            // 用户要求（2026-10-09 15:51）：回收站里右键某一条，可以单独彻底删除或恢复。
            // 【2026-10-19】菜单装配走公共件；动作仍是本页自己的方法。
            RecycleBinEntry captured = entry;
            ContextMenu menu = Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(
                    AppText.T(RecycleBinTextKeys.MenuRestoreOne),
                    delegate { RestoreSingleRecycleBinEntry(captured); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(RecycleBinTextKeys.MenuDeleteOne),
                    delegate { DeleteSingleRecycleBinEntryForever(captured); }));
            Shared.ContextMenus.Attach(hoverHost, menu, control);
            // 悬停时改的是外框 hoverHost 的背景（带圆角 8 的那一层，这样亮起来的块是圆角的）。
            System.Windows.Media.Brush normalRowBackground = hoverHost.Background;
            System.Windows.Media.Brush normalTitle = titleBlock == null ? null : titleBlock.Foreground;
            // 用户要求（2026-10-10 19:45）：“鼠标放到对应的方案要有微微亮起，参考启动与恢复的代码的交互部分，
            // 这样搜索的时候，点击跳转要微微亮起，鼠标放过去也会亮起，要有提示。”
            // → 与「启动与恢复」的清单行同一套交互：悬停整行浅浅亮起 + 标题变色加下划线，并给出提示。
            hoverHost.MouseEnter += delegate
            {
                hoverHost.Background = AppTheme.RowHoverSurface;   // 带圆角的那一层
                if (titleBlock != null)
                {
                    titleBlock.Foreground = AppTheme.Accent;
                    titleBlock.TextDecorations = TextDecorations.Underline;
                }
            };
            hoverHost.MouseLeave += delegate
            {
                hoverHost.Background = normalRowBackground;
                if (titleBlock != null)
                {
                    titleBlock.Foreground = normalTitle;
                    titleBlock.TextDecorations = null;
                }
            };
            // 行提示写着“左键点这一行＝勾选”，这里把行为补上：
            // 点标题/空白处也能勾选；点在勾选框本身时交给勾选框自己处理，避免一次点击切两下。
            hoverHost.Cursor = Cursors.Hand;
            hoverHost.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (box.IsMouseOver)
                {
                    return;
                }
                box.IsChecked = !(box.IsChecked == true);
                args.Handled = true;
            };
            AccessibleToolTip.Attach(
                hoverHost,
                RecycleBinToolTips.RowHover(entry.Title, KindOf(entry), entry.RemainingDays));
            return hoverHost;
        }

        /// <summary>内部 Kind 值（中文数据）的当前语言显示名。</summary>
        private static string KindDisplay(string kind)
        {
            string key = RecycleBinTextKeys.KindKey(kind);
            return key == null ? (kind ?? string.Empty) : AppText.T(key);
        }

        /// <summary>旧条目没有 Kind 字段，按"启动与恢复"处理。</summary>
        private static string KindOf(RecycleBinEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Kind))
            {
                return RecycleBinKind.StartupProfile;
            }
            return entry.Kind;
        }
    }
}
