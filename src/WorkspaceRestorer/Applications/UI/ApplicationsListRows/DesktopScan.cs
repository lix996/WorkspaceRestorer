using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        /// <summary>按当前搜索词刷新候选列表（换数据才重建格子，只换搜索词则只改可见性）。</summary>
        private void RefreshDesktopList()
        {
            if (desktopRowHost == null)
            {
                return;
            }
            List<DesktopEntry> entries = new List<DesktopEntry>();
            foreach (DesktopEntry candidate in controller.DesktopEntries)
            {
                if (candidate != null && !string.IsNullOrEmpty(DesktopKey(candidate)))
                {
                    entries.Add(candidate);
                }
            }
            string signature = BuildDesktopSignature(entries);
            if (!string.Equals(signature, desktopTilesSignature, StringComparison.Ordinal))
            {
                RebuildDesktopTiles(entries);
                desktopTilesSignature = signature;
            }
            ApplyDesktopFilter();
        }

        /// <summary>候选清单的指纹：条数 + 每条路径（顺序也算），变了才值得重建格子。</summary>
        private string BuildDesktopSignature(IList<DesktopEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return "0";
            }
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append(entries.Count);
            for (int index = 0; index < entries.Count; index++)
            {
                builder.Append('|').Append(DesktopKey(entries[index]));
            }
            return builder.ToString();
        }

        /// <summary>整批重建格子（只在候选真的换了的时候调用）。</summary>
        private void RebuildDesktopTiles(IList<DesktopEntry> entries)
        {
            Stopwatch rebuildWatch = LogSearchTiming ? Stopwatch.StartNew() : null;
            int iconVersion = ++desktopIconLoadVersion;
            desktopRowHost.Children.Clear();
            desktopTiles.Clear();
            desktopIconHosts.Clear();
            desktopTileChecks.Clear();
            if (desktopListHeader != null)
            {
                desktopListHeader.Text = AppText.F(
                    ApplicationsTextKeys.DesktopHeaderFormat,
                    entries == null ? 0 : entries.Count);
            }
            if (desktopNoMatchHint != null)
            {
                desktopNoMatchHint.Visibility = Visibility.Collapsed;
                desktopRowHost.Children.Add(desktopNoMatchHint);
            }
            if (entries == null || entries.Count == 0)
            {
                // 一条候选都没有：整块面板收起来（用户第 47 条说过空面板显得脏）。
                if (desktopScanCard != null)
                {
                    desktopScanCard.Visibility = Visibility.Collapsed;
                }
                SelectableText empty = UiFactory.SupportingTextKey(ApplicationsTextKeys.ScanNoResults);
                desktopRowHost.Children.Add(empty);
                desktopVisibleCount = 0;
                UpdateDesktopScanSummary();
                return;
            }
            if (desktopScanCard != null)
            {
                desktopScanCard.Visibility = Visibility.Visible;
            }
            for (int index = 0; index < entries.Count; index++)
            {
                DesktopEntry entry = entries[index];
                Border tile = BuildDesktopTile(entry);
                string key = DesktopKey(entry);
                if (!string.IsNullOrEmpty(key))
                {
                    desktopTiles[key] = tile;
                }
                desktopRowHost.Children.Add(tile);
            }
            if (rebuildWatch != null)
            {
                rebuildWatch.Stop();
                WorkspaceRestorer.Infrastructure.AppLog.Write(
                    "[候选] 重建 " + (entries == null ? 0 : entries.Count) + " 个格子 耗时=" + rebuildWatch.ElapsedMilliseconds + "ms");
            }
            StartDesktopIconLoad(iconVersion, entries);
        }

        /// <summary>
        /// 格子建好后，图标交给后台线程一个个取、一个个回填。
        /// 用户 2026-11-04：“能不能先显示图标？让用户选择？因为用户找桌面的东西，很大概率是通过图标来找。”
        /// 所以这里不阻塞 UI：75 个格子先出现，图标按列表顺序陆续补上，随时可以点选。
        /// </summary>
        private void StartDesktopIconLoad(int version, IList<DesktopEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }
            List<DesktopEntry> pending = new List<DesktopEntry>();
            for (int index = 0; index < entries.Count; index++)
            {
                DesktopEntry entry = entries[index];
                if (entry != null && !string.IsNullOrEmpty(DesktopKey(entry)))
                {
                    pending.Add(entry);
                }
            }
            if (pending.Count == 0)
            {
                return;
            }
            WorkspaceRestorer.Services.DesktopIconLoader.Load(
                pending,
                DesktopTileIconPixels,
                delegate(DesktopEntry entry, ImageSource image)
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (version != desktopIconLoadVersion)
                        {
                            return;
                        }
                        ApplyLoadedDesktopIcon(DesktopKey(entry), image, entry == null ? null : entry.DisplayName);
                    }), DispatcherPriority.Background);
                },
                delegate { return version != desktopIconLoadVersion; });
        }

        /// <summary>按搜索词只改可见性：不造控件、不动滚动位置。</summary>
        private void ApplyDesktopFilter()
        {
            Stopwatch filterWatch = LogSearchTiming ? Stopwatch.StartNew() : null;
            string query = desktopSearch == null ? string.Empty : desktopSearch.Text.Trim();
            int visible = 0;
            int checkedCount = 0;
            foreach (DesktopEntry candidate in controller.DesktopEntries)
            {
                if (candidate == null)
                {
                    continue;
                }
                bool matches = MatchesDesktopQuery(candidate, query);
                if (matches)
                {
                    visible++;
                }
                if (candidate.IsSelected)
                {
                    checkedCount++;
                }
                string key = DesktopKey(candidate);
                Border tile;
                if (desktopTiles.TryGetValue(key, out tile) && tile != null)
                {
                    tile.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                }
                // 【2026-10-17 第 51 批】把“数据里的勾选”同步到界面：
                // 「全选 / 取消全选」和“加进方案后自动取消勾选”都是直接改数据再刷新的，
                // 而格子现在是复用的（不再重建），不同步的话会出现“数据勾上了、界面没勾”的假象
                // （Round88 就是在这里抓到的：「全选」之后勾上的有 0 条）。
                CheckBox pick;
                if (desktopTileChecks.TryGetValue(key, out pick) && pick != null
                    && (pick.IsChecked == true) != candidate.IsSelected)
                {
                    pick.IsChecked = candidate.IsSelected;
                }
            }
            desktopVisibleCount = visible;
            bool noMatch = visible == 0 && (controller.DesktopEntries == null ? 0 : controller.DesktopEntries.Count) > 0;
            if (desktopNoMatchHint != null)
            {
                desktopNoMatchHint.Visibility = noMatch ? Visibility.Visible : Visibility.Collapsed;
            }
            if (desktopListHeader != null)
            {
                int total = controller.DesktopEntries == null ? 0 : controller.DesktopEntries.Count;
                desktopListHeader.Text = noMatch
                    ? AppText.F(ApplicationsTextKeys.ScanHeaderNoMatchFormat, total)
                    : AppText.F(ApplicationsTextKeys.DesktopHeaderFormat, visible);
            }
            if (desktopScanSummary != null)
            {
                desktopScanSummary.Text = AppText.F(
                    ApplicationsTextKeys.ScanSummaryFormat, visible, checkedCount);
            }
            if (filterWatch != null)
            {
                filterWatch.Stop();
                WorkspaceRestorer.Infrastructure.AppLog.Write(
                    "[搜索] 词=\"" + query + "\" 可见=" + visible + " 耗时=" + filterWatch.ElapsedMilliseconds + "ms");
            }
        }

        /// <summary>候选是否命中搜索词（名字或路径里含这段文字；空搜索词一律命中）。</summary>
        internal static bool MatchesDesktopQuery(DesktopEntry candidate, string query)
        {
            if (candidate == null)
            {
                return false;
            }
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }
            if ((candidate.DisplayName ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
            {
                return true;
            }
            return (candidate.Path ?? string.Empty).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>
        /// 刷新“扫描结果共 N 项，已勾选 M 项…”那句汇总（勾选是就地改的，不重建列表）。
        /// </summary>
        private void UpdateDesktopScanSummary()
        {
            if (desktopScanSummary == null)
            {
                return;
            }
            int checkedCount = 0;
            foreach (DesktopEntry candidate in controller.DesktopEntries)
            {
                if (candidate != null && candidate.IsSelected)
                {
                    checkedCount++;
                }
            }
            desktopScanSummary.Text = AppText.F(
                ApplicationsTextKeys.ScanSummaryFormat, desktopVisibleCount, checkedCount);
        }

        /// <summary>勾选/取消勾选扫描结果里的全部项目。</summary>
        private void SetAllDesktopSelections(bool selected)
        {
            for (int index = 0; index < controller.DesktopEntries.Count; index++)
            {
                DesktopEntry entry = controller.DesktopEntries[index];
                if (entry != null)
                {
                    entry.IsSelected = selected;
                }
            }
            RefreshDesktopList();
        }

        /// <summary>把勾选的桌面项目加进**当前自启动方案**。</summary>
        private void AddCheckedDesktopEntriesToActiveProfile()
        {
            List<DesktopEntry> selected = new List<DesktopEntry>();
            for (int index = 0; index < controller.DesktopEntries.Count; index++)
            {
                DesktopEntry entry = controller.DesktopEntries[index];
                if (entry != null && entry.IsSelected)
                {
                    selected.Add(entry);
                }
            }
            if (selected.Count == 0)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoChecked);
                return;
            }
            AddDesktopEntriesToCurrentApplicationPlan(selected);
        }

        /// <summary>
        /// 「加入启动与恢复」（工具栏按钮 / 卡片右侧 ＋ / 卡片右键菜单）：
        /// **把这份自启动方案整份（连同名字、连同每项的勾选状态）复制成
        /// 「启动与恢复」里的一份新工作方案**，不再往“当前选中的方案”里追加项目。
        ///
        /// 用户 2026-10-19 原话：“「加入启动与恢复」（包括方案自带的侧边按钮），效果改成，
        /// 将该方案（包括名字）加入到启动与恢复内（相当于新建方案的方式，要添加方案，
        /// 用户手动移动合并就可以了），不再加入到选定的方案内了（相当于复制粘贴……）。”
        ///
        /// 名字沿用自启动方案的名字；重名按 Windows 新建文件夹的方式顺延成“名字（2）”。
        /// 真正“怎么转”在控制器里（`CreateProfileFromApplicationPlan`），这里只管界面、提示与撤销。
        /// </summary>
        private void CopyApplicationPlanToStartupWorkspace()
        {
            CopyApplicationPlanToStartupWorkspace(controller.ActiveApplicationPlan);
        }

        /// <summary>同上，但明确指定“复制哪一份”（卡片 ＋ / 卡片菜单用这个）。</summary>
        private void CopyApplicationPlanToStartupWorkspace(ApplicationPlan plan)
        {
            if (plan == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoActivePlanShort);
                return;
            }
            if (plan.Items == null || plan.Items.Count == 0)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusPlanNoItemsShort);
                return;
            }
            string sourceName = string.IsNullOrWhiteSpace(plan.Name)
                ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                : plan.Name.Trim();
            StartupProfile created = controller.CreateProfileFromApplicationPlan(
                plan, MakeUniqueProfileName(sourceName));
            if (created == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusCopyFailed);
                return;
            }
            RefreshProfileList();
            // 新方案已经是「启动与恢复」的当前方案（和手动新建一致），这里把结论写到状态栏。
            int count = created.Items == null ? 0 : created.Items.Count;
            int uncheckedCount = 0;
            for (int index = 0; created.Items != null && index < created.Items.Count; index++)
            {
                if (created.Items[index] != null && !created.Items[index].IsEnabled)
                {
                    uncheckedCount++;
                }
            }
            string newPlanName = created.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
            controller.ReportStatus(uncheckedCount > 0
                ? AppText.F(
                    ApplicationsTextKeys.StatusCopySuccessUncheckedFormat,
                    sourceName, newPlanName, count, uncheckedCount)
                : AppText.F(
                    ApplicationsTextKeys.StatusCopySuccessFormat,
                    sourceName, newPlanName, count));
            StartupProfile undoCreated = created;
            PushUndo(
                AppText.F(ApplicationsTextKeys.UndoCopyFormat, newPlanName),
                delegate
                {
                    controller.DeleteProfile(undoCreated);
                    RefreshProfileList();
                },
                delegate
                {
                    StartupProfile again = controller.CreateProfileFromApplicationPlan(
                        plan, MakeUniqueProfileName(sourceName));
                    if (again != null)
                    {
                        RefreshProfileList();
                    }
                });
        }

        /// <summary>只添加一条（行尾的 ＋）。</summary>
        private void AddSingleDesktopEntryToActiveProfile(DesktopEntry entry)
        {
            if (entry == null)
            {
                return;
            }
            List<DesktopEntry> single = new List<DesktopEntry>();
            single.Add(entry);
            AddDesktopEntriesToCurrentApplicationPlan(single);
        }


        /// <summary>把扫描结果面板收起来（用户要求：“关掉扫描结果”）。</summary>
        private void HideDesktopScanPanel()
        {
            controller.ReplaceDesktopEntriesForSelection(new List<DesktopEntry>());
            RefreshDesktopList();
            statusText.Text = AppText.T(ApplicationsTextKeys.StatusScanClosed);
        }

    }
}
