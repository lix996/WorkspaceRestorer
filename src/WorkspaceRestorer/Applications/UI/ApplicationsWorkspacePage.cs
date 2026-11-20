using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.UI.Cards.Drag;
namespace WorkspaceRestorer.UI
{
    // 工作区：自启动应用
    // （从 MainWindow.cs 拆分出来，便于各自维护；同为 partial class MainWindow。）
    public sealed partial class MainWindow
    {
        private Border BuildApplicationsWorkspace()
        {
            Grid content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 标题
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 工具栏（扫描/立即恢复/浏览/搜索）
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 搜索方案/追加当前时间
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 方案卡片 + 扫描结果
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 向下滚动区

            StackPanel heading = new StackPanel();
            // 【2026-11-15 用户要求】标题下一句话 + 问号，完整旧说明收进问号，工具栏紧跟着上移。
            heading.Children.Add(UiFactory.HeadingWithHintKey(
                ApplicationsTextKeys.PageTitle,
                ApplicationsTextKeys.PageShort,
                ApplicationsTextKeys.PageSubtitle,
                18));
            // 用户要求（2026-10-09 18:36）：自启动应用也要有「启动与恢复」那套右键菜单
            // （新建空白方案 ＋ 往当前方案粘贴路径/网址新增…），所以空白处挂同一份菜单。
            // 【2026-10-17 第 50 批修的真 bug】原来这里复用的是「启动与恢复」那份 `CreateBlankContextMenu()`：
            // 它的“新建空白方案”调的是 `CreateBlankStartupProfile()`（建的是**另一个工作区的工作方案**），
            // “粘贴路径/网址新增…”调的是 `AddItemByClipboard(controller.ActiveProfile)`（同样写进另一个工作区）。
            // 在自启动应用页右键，改的却是启动与恢复的数据 —— 与重建第 67 段那两处串区 bug 同一类。
            // 现在换成这页自己的菜单，动作全落在自启动方案上。
            content.ContextMenu = CreateApplicationsContextMenu();

            // 用 WrapPanel 而不是 Grid：按钮多了以后（扫描/加入/浏览/立即恢复/搜索…）
            // Grid 会横着挤出去，右边几个按钮被窗口直接裁掉（截图里抓到过“搜索方案不见了”）。
            // 「启动与恢复」那页也是 WrapPanel，按钮多了会自动换行。
            WrapPanel primaryToolbar = UiFactory.PrimaryActionRow();
            WrapPanel secondaryToolbar = UiFactory.Toolbar();
            WrapPanel planCopyToolbar = UiFactory.Toolbar();
            Button scanButton = UiFactory.CommandButtonKey("\uE721", ApplicationsTextKeys.ScanDesktop, true);
            // 用户要求（2026-10-09 18:36）：“扫描桌面”时要让用户**一个个添加**，
            // 不要一股脑把桌面内容全加进去 —— 所以扫描只读列表，全部默认**不勾选**，
            // 由用户逐条勾选后点“加入当前方案”。（原来的 ScanDesktop() 会把桌面项直接写进方案。）
            scanButton.Click += delegate { ScanDesktopForApplications(); };
            AccessibleToolTip.Attach(scanButton, ApplicationsToolTips.ScanDesktop);
            UiFactory.AddPrimaryAction(primaryToolbar, scanButton);
            Button addSelectedButton = UiFactory.CommandButtonKey("\uE710", ApplicationsTextKeys.AddSelectedToStartup, false);
            addSelectedButton.Margin = new Thickness(0, 0, 8, 8);
            addSelectedButton.VerticalAlignment = VerticalAlignment.Center;
            // 【2026-10-19 用户要求】作用改成：把这份自启动方案**整份（连同名字）**复制成
            // 「启动与恢复」里的一份**新工作方案**，不再并进“当前选中的方案”。
            // （以前 2026-10-18 那版是“只把勾选的项目并进当前方案”，已按新要求替换。）
            addSelectedButton.Click += delegate { CopyApplicationPlanToStartupWorkspace(); };
            AccessibleToolTip.Attach(addSelectedButton, ApplicationsToolTips.AddSelectedToStartup);
            // 这一项放在次要行（浏览/挑文件夹之后），主按钮只留“扫描桌面/立即恢复”。
            // 用户要求（2026-10-09 19:04）：要能跳到系统那种文件选择界面，可以自己跑到 C 盘 / D 盘挑。
            // 【2026-10-17 第 53 批·用户明确要求回退】“浏览还是回退吧，回退成之前的两个（一个是可以多选，
            // 另一个是文件夹）”：第 51 批曾把两个按钮合成自绘窗口（ApplicationItemPickerWindow，
            // 因为它能文件+文件夹混选），用户试过之后不要这个改法，回到“系统打开对话框挑文件（可多选）”
            // ＋“系统选文件夹对话框挑文件夹”两个入口；那个自绘窗口已删除（不留僵尸代码）。
            Button browseButton = UiFactory.CommandButtonKey("\uE8E5", ApplicationsTextKeys.BrowseFiles, false);
            browseButton.Margin = new Thickness(0, 0, 8, 8);
            browseButton.VerticalAlignment = VerticalAlignment.Center;
            browseButton.Click += delegate { BrowseForApplicationItems(); };
            // 提示必须 ≤ 40 字（用户第 56/57 条，自检 Round26 会检查）。
            AccessibleToolTip.Attach(browseButton, ApplicationsToolTips.BrowseFiles);
            secondaryToolbar.Children.Add(browseButton);
            // 系统的“打开”对话框**选不了文件夹**（点文件夹只会进去），所以文件夹单独一个入口。
            Button browseFolderButton = UiFactory.CommandButtonKey("\uE8B7", ApplicationsTextKeys.BrowseFolder, false);
            browseFolderButton.Margin = new Thickness(0, 0, 8, 8);
            browseFolderButton.VerticalAlignment = VerticalAlignment.Center;
            browseFolderButton.Click += delegate { BrowseForApplicationFolder(); };
            AccessibleToolTip.Attach(browseFolderButton, ApplicationsToolTips.BrowseFolder);
            secondaryToolbar.Children.Add(browseFolderButton);
            // 用户要求（2026-10-09 22:29）：“要增加立即恢复的按钮，像启动与恢复一样。”
            Button restoreNow = UiFactory.CommandButtonKey("\uE777", ApplicationsTextKeys.RestoreNow, false);
            restoreNow.Margin = new Thickness(0, 0, 8, 8);
            restoreNow.VerticalAlignment = VerticalAlignment.Center;
            restoreNow.Click += delegate { RestoreApplicationPlanNow(); };
            AccessibleToolTip.Attach(restoreNow, ApplicationsToolTips.RestoreNow);
            UiFactory.AddPrimaryAction(primaryToolbar, restoreNow);
            planCopyToolbar.Children.Add(addSelectedButton);
            // 【2026-10-19 用户要求】“立即恢复”旁边原来的「取消恢复」按钮删掉了：
            // “将启动与恢复，自启动应用内的按钮「取消恢复」删了，相关的按钮代码也去掉，但功能保存，
            //  因为去掉恢复的按钮根本用不上，都是点击恢复后，在提示窗口都有一个取消恢复，
            //  导致按钮的功能重复了且无用。”
            // 取消功能没动：点「立即恢复」后恢复卡片上自带「取消恢复」（调 CancelApplicationsStart）。
            // 用户要求（2026-10-09 22:29）：“搜索的，要和启动与恢复的一致，包括图标样式。”
            Button searchPlan = UiFactory.CommandButtonKey("\uE721", ApplicationsTextKeys.SearchPlan, false);
            searchPlan.Margin = new Thickness(0, 0, 4, 8);
            searchPlan.VerticalAlignment = VerticalAlignment.Center;
            searchPlan.Click += delegate { SearchApplicationPlans(); };
            AccessibleToolTip.Attach(searchPlan, ApplicationsToolTips.SearchPlan);

            // 用户第 50 条：“首先是‘追加当前时间’不要离‘搜索方案’太远。”
            // 用户要求（2026-11-18）：命名框搬进设置后，这两个按钮留在页面，挂到次要工具栏。
            Button appendTime = UiFactory.CommandButtonKey("\uE823", ApplicationsTextKeys.AppendTime, false);
            appendTime.Margin = new Thickness(0, 0, 4, 8);
            appendTime.VerticalAlignment = VerticalAlignment.Center;
            appendTime.Click += delegate { AppendCurrentTimeToApplicationPlanName(); };
            AccessibleToolTip.Attach(appendTime, ApplicationsToolTips.AppendTime);

            // 用户第 85 条：“自启动应用，在扫描桌面这里，扫描后增加一个搜索，否则桌面的东西太多，很难找到。”
            // → 搜索框不再放在工具栏（太不显眼），挪到**扫描结果面板**里、紧挨着那排操作按钮（见下面的 scanActions）。
            // 标题/说明在左，主按钮（扫描桌面、立即恢复）靠右；窄窗口自动落到标题下面。
            // 【2026-10-19 用户要求】主按钮（扫描桌面 / 立即恢复）固定在“标题 + 小字说明”下面，
            // 而且大小、间距和「启动与恢复」的保存/恢复完全一致。
            StackPanel headerBlock = new StackPanel();
            headerBlock.Children.Add(heading);
            headerBlock.Children.Add(primaryToolbar);
            Grid.SetRow(headerBlock, 0);
            content.Children.Add(headerBlock);
            // 次要入口（浏览/挑文件夹/加入启动与恢复）单独一行、放在一起。
            // 两个组各自是 WrapPanel：放得下就紧挨着排一行，放不下就整组换行。
            WrapPanel toolsRow = new WrapPanel();
            toolsRow.Children.Add(secondaryToolbar);
            toolsRow.Children.Add(planCopyToolbar);
            Grid.SetRow(toolsRow, 1);
            content.Children.Add(toolsRow);

            // 用户要求（2026-11-18）：命名框搬进设置 → 自启动应用；
            // 搜索方案/追加当前时间单独留在第三行，不要并进“浏览/挑文件夹”那一行。
            WrapPanel namingActionsRow = UiFactory.Toolbar();
            namingActionsRow.Margin = new Thickness(0);
            searchPlan.Margin = new Thickness(0, 0, 4, 8);
            appendTime.Margin = new Thickness(0, 0, 4, 8);
            searchPlan.Padding = new Thickness(12, 8, 12, 8);
            appendTime.Padding = new Thickness(12, 8, 12, 8);
            namingActionsRow.Children.Add(searchPlan);
            namingActionsRow.Children.Add(appendTime);
            Grid.SetRow(namingActionsRow, 2);
            content.Children.Add(namingActionsRow);

            // 方案卡片区（用户要求：自启动应用也要像「启动与恢复」那样有方案卡片，
            // 卡片上显示项目数和时间，右侧有移动/添加/删除按钮）。
            StackPanel planScrollContent = new StackPanel();
            applicationPlanHost = planScrollContent;
            ScrollViewer planScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = planScrollContent,
                Margin = new Thickness(0, 12, 0, 0)
            };
            desktopPlanScroll = planScroll;
            RefreshApplicationPlanList();
            // 注意：这里**不要**把它加进 content——它在下面会被装进“下半区”那个 Grid 里，
            // 加两次会抛“指定的元素已经是另一个元素的逻辑子元素”（程序直接起不来）。

            // 列表区：照启动与恢复那套外观（卡片 + 行：序号 / 勾选 / 名称 / 副标题 / 右侧操作），
            // 但完全在本文件内实现，不引用也不修改启动与恢复的任何代码。
            Border card = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0)          // 顶边对齐格子：上方放置区才能贴住列表可见顶边
            };
            StackPanel cardInner = new StackPanel();
            // 用户要求（2026-10-09 18:59，第27条“不要这样子（图1），而是显示图2这种，有图标，供用户选择”）：
            // 扫描结果要像图 1 那样带一排操作按钮（添加选中的项目 / 全选 / 取消全选 / 关掉扫描结果），
            // 而且面板要能整块收起来——扫描结果不像“方案”那样常驻。
            WrapPanel scanActions = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
            desktopScanSummary = UiFactory.SupportingText(string.Empty);
            desktopScanSummary.VerticalAlignment = VerticalAlignment.Center;
            desktopScanSummary.Margin = new Thickness(0, 0, 10, 6);
            scanActions.Children.Add(desktopScanSummary);
            // 扫描结果一多（桌面 60 多项）就得靠这个搜；放在这一排里最顺手。
            SelectableText scanSearchHint = UiFactory.SupportingTextKey(ApplicationsTextKeys.SearchScanHint);
            scanSearchHint.VerticalAlignment = VerticalAlignment.Center;
            scanSearchHint.Margin = new Thickness(6, 0, 4, 6);
            scanActions.Children.Add(scanSearchHint);
            desktopSearch.Width = 170;
            desktopSearch.VerticalAlignment = VerticalAlignment.Center;
            desktopSearch.Margin = new Thickness(0, 0, 6, 6);
            AccessibleToolTip.Attach(desktopSearch, ApplicationsToolTips.SearchDesktopResults);
            scanActions.Children.Add(desktopSearch);
            Button addChecked = UiFactory.CommandButtonKey("\uE710", ApplicationsTextKeys.AddChecked, true);
            addChecked.Margin = new Thickness(0, 0, 6, 6);
            addChecked.Click += delegate { AddCheckedDesktopEntriesToActiveProfile(); };
            AccessibleToolTip.Attach(addChecked, ApplicationsToolTips.AddChecked);
            scanActions.Children.Add(addChecked);
            Button checkAll = UiFactory.CommandButtonKey("\uE73A", ApplicationsTextKeys.SelectAll, false);
            checkAll.Margin = new Thickness(0, 0, 6, 6);
            checkAll.Click += delegate { SetAllDesktopSelections(true); };
            AccessibleToolTip.Attach(checkAll, ApplicationsToolTips.CheckAll);
            scanActions.Children.Add(checkAll);
            Button uncheckAll = UiFactory.CommandButtonKey("\uE711", ApplicationsTextKeys.UnselectAll, false);
            uncheckAll.Margin = new Thickness(0, 0, 6, 6);
            uncheckAll.Click += delegate { SetAllDesktopSelections(false); };
            AccessibleToolTip.Attach(uncheckAll, ApplicationsToolTips.UncheckAll);
            scanActions.Children.Add(uncheckAll);
            Button closeScan = UiFactory.CommandButtonKey("\uE711", ApplicationsTextKeys.CloseScan, false);
            closeScan.Margin = new Thickness(0, 0, 6, 6);
            closeScan.Click += delegate { HideDesktopScanPanel(); };
            AccessibleToolTip.Attach(closeScan, ApplicationsToolTips.CloseScan);
            scanActions.Children.Add(closeScan);
            cardInner.Children.Add(scanActions);

            desktopListHeader = new TextBlock
            {
                Text = AppText.F(ApplicationsTextKeys.DesktopHeaderFormat, controller.DesktopEntries.Count),
                FontFamily = AppTheme.UiFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                Margin = new Thickness(2, 0, 0, 6)
            };
            cardInner.Children.Add(desktopListHeader);
            // 【2026-10-17 第 51 批】搜索谁都不匹配时显示这一行（面板**不要**收起来，
            // 收起来的话用户会以为“搜索把扫描结果清空了”）。
            desktopNoMatchHint = new TextBlock
            {
                Text = AppText.T(ApplicationsTextKeys.DesktopNoMatch),
                FontFamily = AppTheme.UiFont,
                FontSize = 12,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(2, 4, 0, 8),
                Visibility = Visibility.Collapsed
            };
            // 【2026-10-17 第 50 批】改成大图标格子：候选不再是一行行小字列表，而是会自己换行的格子墙
            desktopRowHost = new WrapPanel();
            // 【2026-10-18 用户要求】扫描结果里的**操作条（搜索 / 添加选中的项目 / 全选 / 取消全选 /
            // 关掉扫描结果）和小标题要钉在上面不动**，只有格子墙自己滚：
            // “在用户滚轮向下选择的时候，要让那些按钮一直在原位，这样好操控，
            //  否则滚轮划到最下面选择时，还要滚轮到最上面点击添加等的按钮”。
            // 以前整个面板（操作条＋标题＋格子）都装在一个 ScrollViewer 里，往下翻按钮就跟着跑了。
            ScrollViewer tileScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                // 面板整体上限仍是 360（第 50 批定的）：
                // 上面固定那两行（操作条 ≈ 46 + 小标题 ≈ 30，含边距 ≈ 92）扣掉，剩下的给格子墙，
                // 一屏照样能看到两排多一点大图标，也不会把下面的方案列表挤没。
                MaxHeight = 268,
                Content = desktopRowHost
            };
            cardInner.Children.Add(tileScroll);
            card.Child = cardInner;
            card.MaxHeight = 360;
            desktopScanCard = card;
            desktopScroll = tileScroll;
            // 一开始就没有扫描结果时，整块面板先收起来（免得占着空地方）。
// 一开始没有扫描结果时整块面板收起（只留一行提示），扫描出东西后再展开。
            card.Visibility = controller.DesktopEntries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            // 上/下两块拖动放置区：外观与“透明浮层/白底占一行”的规则都由公共工厂负责。
            desktopTopPad = DragDropPadFactory.CreateTop(AppText.T(ApplicationsTextKeys.DragPadTop));
            desktopBottomPad = DragDropPadFactory.CreateBottom(AppText.T(ApplicationsTextKeys.DragPadBottom));

            // ================== 【2026-11-18：命名行搬进设置后，扫描结果紧贴工具栏】 ==================
            // 扫描结果面板现在紧跟工具栏那一行（原来紧跟“新工作命名”行）；
            // 方案卡片列表吃掉剩下的高度。
            // 两块拖动带（上/下）跟着方案列表走（与「启动与恢复」同一套位置：上面那条是浮层、下面那条是独立一行），
            // 这样空闲时不会白占一条高度，拖动时照样能亮、能自动滚。
            Grid lower = new Grid();
            lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 扫描结果面板（紧贴工具栏）
            lower.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 190.0 });   // 方案卡片列表
            lower.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 列表下方的向下滚动带
            planScroll.Visibility = Visibility.Visible;
            // 【2026-10-18】扫描结果面板现在整块就是 `card`（操作条固定、格子墙自己滚），
            // 不再套一层外层 ScrollViewer —— 否则滚轮往下翻时按钮会跟着跑掉。
            Grid.SetRow(card, 0);
            lower.Children.Add(card);
            Grid.SetRow(planScroll, 1);
            lower.Children.Add(planScroll);
            // 上面那条带是浮层（负上边距向上延伸、不占布局、不可命中），叠在方案列表上沿；
            // 下面那条是方案列表下方独立的一行。
            Grid.SetRow(desktopTopPad, 1);
            lower.Children.Add(desktopTopPad);
            Grid.SetRow(desktopBottomPad, 2);
            lower.Children.Add(desktopBottomPad);
            Grid.SetRow(lower, 3);
            content.Children.Add(lower);
            return WorkspaceBorder(content);
        }

        /// <summary>
        /// 扫描桌面：**只把桌面项读出来列在下面的列表里**，全部默认不勾选，
        /// 由用户一个一个挑（用户要求 2026-10-09 18:36：“扫描桌面”时要让用户一个个添加，
        /// 不要一股脑全部添加桌面的内容）。
        /// </summary>
        private void ScanDesktopForApplications()
        {
            try
            {
                // 注意：这里刻意不调用 controller.ScanDesktop()——那个方法会把桌面项
                // 直接写进当前方案，属于“一股脑全加”，正是用户不要的行为。
                IList<DesktopEntry> entries = controller.ScanDesktopEntriesOnly();
                controller.ReplaceDesktopEntriesForSelection(entries);
                RefreshDesktopList();
                int count = controller.DesktopEntries == null ? 0 : controller.DesktopEntries.Count;
                statusText.Text = count == 0
                    ? AppText.T(ApplicationsTextKeys.StatusScanEmpty)
                    : AppText.F(ApplicationsTextKeys.StatusScanFoundFormat, count);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusScanFailedFormat, exception.Message);
            }
        }

        /// <summary>
        /// 「浏览…」：打开系统的文件选择窗口（可多选），选完**直接加进当前自启动方案**。
        ///
        /// 用户 2026-10-09 19:04：要能跳到系统那种界面，可以去 C 盘 / D 盘挑；20:57 补充“第一次点要默认桌面”。
        /// 用户 2026-10-17 第 50 批：“浏览没法添加” → 选完直接进方案（`AddBrowsedTargetsToCurrentApplicationPlan`）。
        /// 用户 2026-10-17 第 53 批：**明确要求回退成两个按钮**（系统打开对话框挑文件 / 系统选文件夹对话框挑文件夹），
        /// 第 51 批那个能“文件+文件夹混选”的自绘窗口因此删掉了。
        /// </summary>
        private void BrowseForApplicationItems()
        {
            if (browseFileDialog == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusBrowseUnavailable);
                return;
            }
            try
            {
                // 多选：让用户可以一次挑好几个文件。
                browseFileDialog.Multiselect = true;
                bool? result = browseFileDialog.ShowDialog(this);
                if (result != true)
                {
                    return;
                }
                AddBrowsedTargetsToCurrentApplicationPlan(browseFileDialog.FileNames);
                // 选完把下次的起始目录记在用户最后去的地方，方便连续挑（第一次仍是桌面）。
                string folder = System.IO.Path.GetDirectoryName(browseFileDialog.FileName);
                if (!string.IsNullOrWhiteSpace(folder) && System.IO.Directory.Exists(folder))
                {
                    browseFileDialog.InitialDirectory = folder;
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusBrowseFailedFormat, exception.Message);
            }
        }

        /// <summary>
        /// 「挑文件夹…」：系统“打开”对话框选不了文件夹（点进去只会进入），而“开机自动打开某个文件夹”是常见需求，
        /// 所以单独给一个入口；选完与“浏览…”同一条路加进方案。
        /// </summary>
        private void BrowseForApplicationFolder()
        {
            try
            {
                using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
                {
                    dialog.Description = AppText.T(ApplicationsTextKeys.FolderDialogDescription);
                    dialog.ShowNewFolderButton = false;
                    string lastFolder = browseFileDialog == null ? null : browseFileDialog.InitialDirectory;
                    if (!string.IsNullOrWhiteSpace(lastFolder) && System.IO.Directory.Exists(lastFolder))
                    {
                        dialog.SelectedPath = lastFolder;
                    }
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    {
                        return;
                    }
                    string chosen = dialog.SelectedPath;
                    if (string.IsNullOrWhiteSpace(chosen))
                    {
                        return;
                    }
                    AddBrowsedTargetsToCurrentApplicationPlan(new string[] { chosen });
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusFolderFailedFormat, exception.Message);
            }
        }

        /// <summary>
        /// 「自启动应用」空白处的右键菜单（用户 2026-10-09 18:36：要和「启动与恢复」一样有这几项）。
        ///
        /// 【2026-10-17 第 50 批】这一份是**自启动应用自己的**：所有动作都落在自启动方案上。
        /// 之前它复用的是「启动与恢复」的菜单，于是“新建空白方案”建的是一份工作方案、
        /// “粘贴路径/网址新增…”写进的也是工作方案 —— 在自启动应用页右键却改了另一个工作区。
        /// </summary>
        private ContextMenu CreateApplicationsContextMenu()
        {
            // 【2026-10-18 用户要求】右键菜单与「启动与恢复」对齐：新建空白方案 ＋ 粘贴路径/网址新增（**弹对话框**）。
            // 「挑文件夹…」「扫描桌面…」从菜单里删掉 —— 工具栏上已经有同名按钮，菜单里重复且用不上。
            // 具体实现放在本工作区的 `ApplicationsMenus.cs`（不进公共区）。
            return CreateApplicationsBlankContextMenu();
        }

        /// <summary>
        /// 把剪贴板里的路径/网址加进当前自启动方案（右键菜单“往当前方案粘贴路径/网址新增…”）。
        /// 与「浏览…」走同一条落地方：直接进方案，不是先进候选。
        ///
        /// 【2026-10-18 用户第二次反馈“右键后还是没法添加网址”】截图里剪贴板其实是
        /// 「启动与恢复」按 Ctrl+C 复制工作方案时写进去的文本（`CSK1|启动与恢复{...}`），
        /// 它当然不是网址；旧实现只回一句“这不是能用的路径或网址：” ＋ 一整段 JSON，看着像又失败了。
        /// 现在四种情况分别处理：
        ///   ① 剪贴板是复制的工作方案 → 把里面**能用的项目**取出来加进方案；一个都没有才提示；
        ///   ② 多行文本 → 逐行找第一个能用的网址/存在的路径；
        ///   ③ 单行 → 整理后判定；
        ///   ④ 实在不行 → 只说前 80 个字，不甩 JSON。
        /// </summary>
        private void AddPastedTargetText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusClipboardEmpty);
                return;
            }
            if (WebAddressTools.LooksLikeCopiedProfile(text))
            {
                List<string> extracted = ExtractTargetsFromCopiedProfile(text);
                if (extracted.Count > 0)
                {
                    AddBrowsedTargetsToCurrentApplicationPlan(extracted.ToArray());
                    return;
                }
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusClipboardProfileNoTargets);
                return;
            }

            string target = null;
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            for (int index = 0; index < lines.Length && string.IsNullOrEmpty(target); index++)
            {
                string candidate = WebAddressTools.Normalize(lines[index]);
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }
                string preparedUrl;
                if (WebAddressTools.TryPrepareWebAddress(candidate, out preparedUrl))
                {
                    target = preparedUrl;
                    break;
                }
                bool candidateExists = false;
                try
                {
                    candidateExists = System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate);
                }
                catch
                {
                    candidateExists = false;
                }
                if (candidateExists)
                {
                    target = candidate;
                }
            }
            if (string.IsNullOrEmpty(target))
            {
                string preview = WebAddressTools.Normalize(text);
                if (preview.Length > 80)
                {
                    preview = preview.Substring(0, 80) + "…";
                }
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusInvalidTargetFormat, preview);
                return;
            }
            AddBrowsedTargetsToCurrentApplicationPlan(new string[] { target });
        }

        /// <summary>
        /// 从「启动与恢复」复制的工作方案文本里取出**能用的目标**（网址或磁盘上存在的路径）。
        /// 用户 2026-10-18 的现场：剪贴板里是 `CSK1|启动与恢复{...}`，右键粘贴新增时报“不是路径/网址”；
        /// 既然他手里是一份方案，就把方案里能用的项目取出来加进自启动方案，比报错有用。
        /// </summary>
        private static List<string> ExtractTargetsFromCopiedProfile(string clipboardText)
        {
            List<string> targets = new List<string>();
            StartupProfile profile = StartupProfileClipboard.DeserializeAsCopy(clipboardText, "粘贴导入");
            if (profile == null || profile.Items == null)
            {
                return targets;
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem item = profile.Items[index];
                if (item == null)
                {
                    continue;
                }
                string candidate = !string.IsNullOrWhiteSpace(item.Url) ? item.Url : item.TargetPath;
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                candidate = WebAddressTools.Normalize(candidate);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }
                string preparedUrl;
                if (WebAddressTools.TryPrepareWebAddress(candidate, out preparedUrl))
                {
                    if (seen.Add(preparedUrl))
                    {
                        targets.Add(preparedUrl);
                    }
                    continue;
                }
                bool exists = false;
                try
                {
                    exists = System.IO.File.Exists(candidate) || System.IO.Directory.Exists(candidate);
                }
                catch
                {
                    exists = false;
                }
                if (exists && seen.Add(candidate))
                {
                    targets.Add(candidate);
                }
            }
            return targets;
        }

        /// <summary>
        /// 新建一份空白的自启动方案（重名按 Windows 方式顺延成“名字（2）”）。
        /// 数据存在 `workflows\自启动应用\自启动方案.json`，与「启动与恢复」的方案完全分开。
        /// </summary>
        private void CreateBlankApplicationPlan()
        {
            // 名字来自 设置 → 自启动应用 → 新自启动方案名（没自定义时用默认名）。
            string name = GetNewApplicationPlanName();
            name = MakeUniqueApplicationPlanName(name);
            // 【2026-10-17】新建方案也要能 Ctrl+Z（否则建完发现名字不对，只能再手动删）。
            ApplicationPlansSnapshot beforeCreate = CaptureApplicationPlansSnapshot();
            ApplicationPlan created = controller.CreateApplicationPlan(name);
            if (created == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusCreateFailed);
                return;
            }
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(ApplicationsTextKeys.StatusCreatedFormat, created.Name);
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoCreateFormat, created.Name),
                beforeCreate);
        }

        /// <summary>末尾的“（N）”序号；连续新建时从 N+1 接着顺延，避免“（2）（2）”。</summary>
        private static readonly System.Text.RegularExpressions.Regex ApplicationPlanNumberedSuffix =
            new System.Text.RegularExpressions.Regex(
                @"^(.*?)（(\d+)）$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>自启动方案重名时按 Windows 新建文件夹的方式顺延。</summary>
        private string MakeUniqueApplicationPlanName(string name)
        {
            string baseName = string.IsNullOrWhiteSpace(name)
                ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                : name.Trim();
            if (!ApplicationPlanNameExists(baseName))
            {
                return baseName;
            }
            int next = 2;
            System.Text.RegularExpressions.Match numbered = ApplicationPlanNumberedSuffix.Match(baseName);
            if (numbered.Success)
            {
                int parsed;
                if (int.TryParse(numbered.Groups[2].Value, out parsed) && parsed >= 1)
                {
                    baseName = numbered.Groups[1].Value.Trim();
                    next = parsed + 1;
                }
            }
            for (int suffix = next; suffix < next + 1000; suffix++)
            {
                string candidate = baseName + "（" + suffix + "）";
                if (!ApplicationPlanNameExists(candidate))
                {
                    return candidate;
                }
            }
            return baseName + "（" + Guid.NewGuid().ToString("N").Substring(0, 4) + "）";
        }

        private bool ApplicationPlanNameExists(string name)
        {
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (plans == null)
            {
                return false;
            }
            for (int index = 0; index < plans.Count; index++)
            {
                ApplicationPlan plan = plans[index];
                if (plan != null && string.Equals(plan.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// “追加当前时间”：把“本机时区名 + 当前日期时间”接到**当前自启动方案**的名字后面。
        /// 用户第 50 条要求它紧挨着“搜索方案”，并且两个大类都要有同一个功能；
        /// 规则放在公共的 <see cref="WorkspaceRestorer.Core.TimeText"/> 里（反复点只更新时间，不叠加），
        /// 名字改写走 `controller.RenameApplicationPlan`，文件夹镜像会跟着改名。
        /// </summary>
        private void AppendCurrentTimeToApplicationPlanName()
        {
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            if (plan == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoActivePlan);
                return;
            }
            string oldName = plan.Name;
            string newName = WorkspaceRestorer.Core.TimeText.AppendStamp(
                oldName,
                AppText.T(CommonTextKeys.StartupPlanFallback),
                DateTime.Now,
                TimeZoneInfo.Local,
                AppText.CurrentLanguage.Tag);
            if (!controller.RenameApplicationPlan(plan, newName))
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusAppendFailed);
                return;
            }
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(ApplicationsTextKeys.StatusAppendDoneFormat, newName);
            PushUndo(
                AppText.T(ApplicationsTextKeys.UndoAppend),
                delegate
                {
                    controller.RenameApplicationPlan(plan, oldName);
                    RefreshApplicationPlanList();
                },
                delegate
                {
                    controller.RenameApplicationPlan(plan, newName);
                    RefreshApplicationPlanList();
                });
        }

        /// <summary>
        /// 搜索自启动方案。
        /// 【2026-10-17 修的真 bug】这里原来调用的是 `ShowProfileSearchDialog(...)`，
        /// 而那个弹窗写死搜 `controller.StartupProfiles`（**「启动与恢复」的工作方案**），
        /// 点结果还会把那边切成当前方案 —— 在自启动应用页搜出来的、点开的都是另一个工作区的东西。
        /// 现在用同一个弹窗外壳，数据源与“点一下做什么”由本工作区自己给。
        /// 【2026-10-19】弹窗外壳与匹配规则都搬进公共区了：外壳 `Shared/UI/Controls/SearchDialog.cs`、
        /// 匹配 `Shared/UI/Controls/SearchMatch.cs`。本工作区原来那份 `ApplicationPlanMatchesQuery`
        /// **已删除**（用户要求“自启动应用的搜索代码就删了，不要留下僵尸代码”）——
        /// 以后改搜索方式只改公共件，这里不用动。
        /// </summary>
        private void SearchApplicationPlans()
        {
            ShowSearchDialog(
                AppText.T(ApplicationsTextKeys.SearchPlan),
                AppText.T(ApplicationsTextKeys.SearchDescription),
                // 【2026-10-19 用户要求】“自启动应用…为什么没有搜索内没有对应的「同时按时间搜索」，
                // 后续搜索内添加新的功能时…都要同步到其余两个上的。”
                // → 勾选框、多词 AND、时间词、高亮全部走公共件；这里只提供“这一条叫什么、什么时候保存”。
                delegate(string keyword, bool includeTime)
                {
                    List<SearchHit> hits = new List<SearchHit>();
                    IList<ApplicationPlan> plans = controller.ApplicationPlans;
                    for (int index = 0; plans != null && index < plans.Count; index++)
                    {
                        ApplicationPlan plan = plans[index];
                        if (plan == null)
                        {
                            continue;
                        }
                        string saved = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                            plan.UpdatedAtUtc, AppText.CurrentLanguageTag);
                        Shared.SearchTarget target = new Shared.SearchTarget
                        {
                            Name = plan.Name,
                            TimeText = saved,
                            SavedUtc = plan.UpdatedAtUtc,
                            UpdatedUtc = plan.UpdatedAtUtc,
                            CreatedUtc = plan.CreatedAtUtc
                        };
                        if (!Shared.SearchMatcher.Matches(target, keyword, includeTime))
                        {
                            continue;
                        }
                        List<SearchTextRange> nameRanges = new List<SearchTextRange>();
                        List<SearchTextRange> timeRanges = new List<SearchTextRange>();
                        Shared.SearchMatcher.Highlight(target, keyword, includeTime, nameRanges, timeRanges);
                        SearchHit hit = new SearchHit();
                        hit.Title = string.IsNullOrWhiteSpace(plan.Name)
                            ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                            : plan.Name;
                        string prefix = AppText.F(
                            ApplicationsTextKeys.SearchDetailFormat,
                            plan.Items == null ? 0 : plan.Items.Count);
                        string timeLabel = AppText.F(
                            ApplicationsTextKeys.SearchLastSavedFormat, string.Empty);
                        hit.Detail = saved.Length == 0 ? prefix : prefix + timeLabel + saved;
                        hit.TitleSegments = BuildHighlightSegments(hit.Title, nameRanges, 0);
                        hit.DetailSegments = saved.Length == 0
                            ? BuildHighlightSegments(hit.Detail, null, 0)
                            : BuildHighlightSegments(hit.Detail, timeRanges, -(prefix.Length + timeLabel.Length));
                        hit.Tag = plan;
                        hits.Add(hit);
                    }
                    return hits;
                },
                delegate(SearchHit hit)
                {
                    ApplicationPlan captured = hit == null ? null : hit.Tag as ApplicationPlan;
                    if (captured == null)
                    {
                        return;
                    }
                    controller.SetActiveApplicationPlan(captured);
                    RefreshApplicationPlanList();
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusSwitchedFormat,
                        captured.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                },
                AppText.T(ApplicationsTextKeys.SearchEmpty));
        }

        /// <summary>
        /// “立即恢复”：按当前自启动方案的顺序，把里面**勾选**的项目启动一遍。
        ///
        /// 现在不再自己逐项 Process.Start，而是把方案翻译成 SessionSnapshot，
        /// 交给 StartupRestore/Core 的共用恢复入口：
        ///   · 浏览器批次、最后一页缩窗、最终窗口校验；
        ///   · 已打开项跳过、父行过滤、取消/进度/完成横幅；
        /// 全部与「启动与恢复」同一份代码。
        /// </summary>
        private async void RestoreApplicationPlanNow()
        {
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            if (plan == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoPlan);
                return;
            }
            if (controller.CountEnabledApplicationPlanTreeItems(plan) == 0)
            {
                statusText.Text = plan.IsFolder
                    ? AppText.T(ApplicationsTextKeys.StatusFolderNoItems)
                    : AppText.T(ApplicationsTextKeys.StatusPlanNoItems);
                return;
            }
            if (controller.IsRestoring)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusRestoring);
                return;
            }

            string progressText = AppText.F(
                ApplicationsTextKeys.StatusRestoringFormat,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
            statusText.Text = progressText;
            try
            {
                RestoreResult result = await WorkspaceRestorer.Applications.Restore.ApplicationPlanRestoreBridge
                    .RestoreAsync(controller, plan, false);
                if (result == null && string.Equals(statusText.Text, progressText, StringComparison.Ordinal))
                {
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusNoRestorableFormat,
                        plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("按自启动方案恢复失败。", exception);
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusRestoreFailedFormat, exception.Message);
            }
        }

        /// <summary>方案里勾选且填了路径、能启动的项目个数。</summary>
        private static int CountEnabledApplicationPlanItems(ApplicationPlan plan)
        {
            if (plan == null || plan.Items == null)
            {
                return 0;
            }
            int count = 0;
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem item = plan.Items[index];
                if (item != null && item.IsEnabled && !string.IsNullOrWhiteSpace(item.TargetPath))
                {
                    count++;
                }
            }
            return count;
        }
        /// <summary>
        /// Left-hand "设置" workspace: documents the keyboard shortcuts the
        /// window currently supports.
        /// </summary>
        /// <summary>
        /// The 设置 workspace.  It is divided into sections exactly like the left
        /// rail is divided into workspaces: 总设置 holds the options that apply
        /// everywhere, and each workspace gets its own section.  Sections without
        /// options yet are kept as empty shells on purpose, so new settings have
        /// an obvious home and nothing has to be reshuffled later.
        /// </summary>
        // ------------------------------------------------------------------
        // 设置面板（照 DSH 设置弹层的逻辑）
        //
        // 结构：标题 + 关闭按钮 / 左侧分区列表 / 右侧当前分区内容 / 左下角"重置"。
        // 分区是**注册式**的：CreateSettingsSections() 里按顺序列出来，
        // 每个分区自带 id、标题、图标和内容构建方法；以后加设置只需往这个列表里
        // 加一条，菜单、切换、重置入口都会自动跟着走，不必改结构代码。
        // ------------------------------------------------------------------


        private static TextBlock CreateLocalizedHeader(string key)
        {
            TextBlock header = new TextBlock();
            LocalizedText.Text(header, key);
            return header;
        }

        private static GridView CreateDesktopView()
        {
            GridView view = new GridView();
            view.Columns.Add(new GridViewColumn
            {
                Header = CreateLocalizedHeader(ApplicationsTextKeys.ColumnSelect),
                Width = 56,
                CellTemplate = CreateDesktopSelectionTemplate()
            });
            view.Columns.Add(new GridViewColumn { Header = CreateLocalizedHeader(ApplicationsTextKeys.ColumnDesktopItem), Width = 170, DisplayMemberBinding = new Binding("DisplayName") });
            view.Columns.Add(new GridViewColumn { Header = CreateLocalizedHeader(ApplicationsTextKeys.ColumnSource), Width = 80, DisplayMemberBinding = new Binding("Source") });
            view.Columns.Add(new GridViewColumn { Header = CreateLocalizedHeader(ApplicationsTextKeys.ColumnPath), Width = 260, DisplayMemberBinding = new Binding("Path") });
            view.Columns.Add(new GridViewColumn
            {
                Header = CreateLocalizedHeader(ApplicationsTextKeys.ColumnActions),
                Width = 54,
                CellTemplate = CreateDesktopOpenTemplate()
            });
            return view;
        }

        private static DataTemplate CreateDesktopSelectionTemplate()
        {
            DataTemplate template = new DataTemplate();
            FrameworkElementFactory checkBox = new FrameworkElementFactory(typeof(CheckBox));
            checkBox.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            AccessibleToolTip.AttachFactory(checkBox, ApplicationsToolTips.DesktopSelectionFactory);
            checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsSelected")
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            template.VisualTree = checkBox;
            return template;
        }

        private static DataTemplate CreateDesktopOpenTemplate()
        {
            DataTemplate template = new DataTemplate();
            FrameworkElementFactory openButton = new FrameworkElementFactory(typeof(Button));
            openButton.SetValue(Button.WidthProperty, 32.0);
            openButton.SetValue(Button.HeightProperty, 32.0);
            openButton.SetValue(Button.PaddingProperty, new Thickness(0));
            openButton.SetValue(Button.FontFamilyProperty, AppTheme.UiFont);
            openButton.SetValue(Button.FontSizeProperty, 16.0);
            openButton.SetValue(Button.ContentProperty, "\u2197");
            openButton.SetValue(Button.TagProperty, "open");
            AccessibleToolTip.AttachFactory(openButton, ApplicationsToolTips.DesktopOpenFactory);
            openButton.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            openButton.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = openButton;
            return template;
        }


    }
}
