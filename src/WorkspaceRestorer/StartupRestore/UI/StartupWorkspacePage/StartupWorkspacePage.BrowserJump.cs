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
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 网页跳转：目标窗口优先、候选匹配、唯一选择（从 BrowserPageJumpWorkspace.cs 搬迁）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private void JumpToSavedBrowserTab(IList<SessionItem> sourceItems, SessionItem page)
        {
            if (page == null)
            {
                return;
            }

            string title = page.WindowTitle;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = page.DisplayName;
            }
            string savedUrl = page.Url ?? page.TargetPath;
            string executable = ResolveJumpExecutable(page);
            string normalized = NormalizeExecutablePath(executable);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                OpenSavedItem(page);
                return;
            }

            IBrowserPageNavigator navigator = BrowserPageNavigatorFactory.Create(normalized);
            List<IntPtr> windows = navigator.EnumerateWindows(normalized);
            if (windows.Count == 0)
            {
                OpenSavedItem(page);
                return;
            }

            // 2026-10-31 稳定版（目标优先，不再全窗口扫描）：
            //   1. 先用保存的归属/窗口标题/整窗指纹找**唯一目标窗口**；
            //   2. 目标可见 → 零窗口操作；目标最小化 → 只还原目标一次；
            //   3. 目标不唯一时只扫**可见窗口**（零还原）；
            //   4. 仍没有才尝试**一个**最可能的最小化窗口；
            //   5. 绝不为了“收集所有候选”遍历所有最小化窗口。
            SessionItem browserWindow = FindBrowserWindowParent(sourceItems, page);
            List<BrowserJumpWindow> candidates =
                BuildBrowserJumpWindows(windows, page, browserWindow, title, sourceItems);

            // A0. 两个以上窗口都可能装着这页：先按“保存父窗口标题 + 整窗标签数”认窗口；
            //     认不出来就让用户选，不再靠标题分数静默挑一个。
            if (TryResolveDuplicateBrowserWindows(candidates, page, browserWindow))
            {
                return;
            }

            // A. 保存的归属已经足够确定：只碰目标窗口。
            if (TryJumpStrongTarget(candidates, page))
            {
                return;
            }

            // B. 可见窗口只读扫描；不还原任何最小化窗口。
            List<BrowserTabMatch> visibleMatches = CollectVisibleBrowserMatches(candidates, page);
            if (visibleMatches.Count > 0)
            {
                BrowserTabMatch visibleWinner = SelectBrowserTabMatch(page, browserWindow, visibleMatches);
                if (visibleWinner == null)
                {
                    visibleWinner = ShowBrowserTabMatchChooser(visibleMatches, page);
                }
                if (visibleWinner != null && ActivateBrowserTabMatch(visibleWinner, page))
                {
                    return;
                }
                if (visibleWinner == null)
                {
                    statusText.Text = AppText.T(StartupRestoreTextKeys.BrowserJumpMultipleSameTitle);
                    return;
                }
            }

            // C. 仍没有：只尝试一个最可能的最小化窗口，绝不遍历所有最小化窗口。
            BrowserJumpWindow minimizedCandidate = FindBestMinimizedCandidate(candidates);
            if (minimizedCandidate != null)
            {
                BrowserTabMatch minimizedMatch;
                if (TryLocateCandidateForJump(minimizedCandidate, page, out minimizedMatch))
                {
                    if (ActivateBrowserTabMatch(minimizedMatch, page))
                    {
                        return;
                    }
                    ReMinimizeBrowserWindowAfterJumpProbe(minimizedCandidate.Handle);
                }
            }

            // D. 旧调试端口兜底：只对本来就可见的窗口尝试，不还原最小化窗口。
            if (!string.IsNullOrWhiteSpace(savedUrl))
            {
                for (int index = 0; index < candidates.Count; index++)
                {
                    if (candidates[index].Minimized)
                    {
                        continue;
                    }
                    if (TrySelectBrowserTabByUrl(candidates[index].Handle, savedUrl.Trim()))
                    {
                        ActivateBrowserTabWindow(candidates[index].Handle);
                        statusText.Text = AppText.F(
                            StartupRestoreTextKeys.BrowserJumpedByUrlFormat,
                            page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback));
                        return;
                    }
                }
            }

            // 没找到就保持原状，绝不把“顺便扫过”的最小化窗口留在桌面上。
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.BrowserJumpNotFoundFormat,
                page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback));
        }

        /// <summary>
        /// 保存的归属/快照/整窗指纹只指向一个目标窗口时，只检查这个窗口。
        /// 目标可见时零窗口操作；目标最小化时才安静还原一次。
        /// </summary>
        private bool TryJumpStrongTarget(
            List<BrowserJumpWindow> candidates,
            SessionItem page)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }
            BrowserJumpWindow top = candidates[0];
            bool strong = candidates.Count == 1
                || (top.Score >= 80
                    && candidates[1].Score <= top.Score - 80);
            if (!strong)
            {
                return false;
            }

            BrowserTabMatch match;
            if (!TryLocateCandidateForJump(top, page, out match))
            {
                return false;
            }
            if (ActivateBrowserTabMatch(match, page))
            {
                return true;
            }
            if (top.Minimized)
            {
                ReMinimizeBrowserWindowAfterJumpProbe(top.Handle);
            }
            return false;
        }

        /// <summary>
        /// 多个窗口都可能装着同一条网页时的身份判定（2026-11-06）。
        ///
        /// 场景：Edge(1) 和 Edge(2) 都显示同一个网址，用户点 Edge(2) 里的那条。
        /// 标题和网址只能证明“这页在这个窗口里”，没法证明用户点的是哪个窗口：
        ///   · 保存父窗口标题现在只在一个窗口上出现 → 就是它；
        ///   · 否则当前标签数和保存时唯一吻合（允许差 1）→ 就是它；
        ///   · 都不行 → 只读扫描可见窗口，让用户选一次。
        /// 返回 true 表示已经处理（跳了或在状态栏说明），调用方不要再走后面的兜底；
        /// 返回 false 表示只有一个候选窗口，保持原来的单窗口行为。
        /// </summary>
        private bool TryResolveDuplicateBrowserWindows(
            List<BrowserJumpWindow> candidates,
            SessionItem page,
            SessionItem browserWindow)
        {
            if (candidates == null || candidates.Count < 2)
            {
                return false;
            }

            IList<BrowserTabFingerprint> savedTabs = page == null ? null : page.BrowserTabs;
            if ((savedTabs == null || savedTabs.Count == 0)
                && browserWindow != null
                && browserWindow.BrowserTabs != null
                && browserWindow.BrowserTabs.Count > 0)
            {
                savedTabs = browserWindow.BrowserTabs;
            }

            List<BrowserJumpWindowFacts> facts =
                new List<BrowserJumpWindowFacts>(candidates.Count);
            for (int index = 0; index < candidates.Count; index++)
            {
                BrowserJumpWindow candidate = candidates[index];
                facts.Add(new BrowserJumpWindowFacts
                {
                    ContainsSavedPage = candidate.ActivePageMatches
                        || candidate.SavedTabContainsTitle,
                    ParentTitleMatches = candidate.ParentTitleMatches,
                    PageCountKnown = candidate.PageCountKnown,
                    PageCount = candidate.PageCount
                });
            }

            int chosenIndex;
            BrowserJumpWindowChoice choice = BrowserJumpWindowIdentity.Decide(
                facts,
                savedTabs == null ? -1 : savedTabs.Count,
                out chosenIndex);
            if (choice == BrowserJumpWindowChoice.None)
            {
                return false;
            }

            if (choice != BrowserJumpWindowChoice.Ambiguous)
            {
                BrowserTabMatch match;
                if (TryLocateCandidateForJump(candidates[chosenIndex], page, out match)
                    && ActivateBrowserTabMatch(match, page))
                {
                    return true;
                }
                // 定位/激活失败：不换一个窗口硬跳，退到“让用户选”。
            }

            List<BrowserTabMatch> visibleMatches =
                CollectVisibleBrowserMatches(candidates, page);
            if (visibleMatches.Count > 0)
            {
                BrowserTabMatch picked = ShowBrowserTabMatchChooser(visibleMatches, page);
                if (picked != null && ActivateBrowserTabMatch(picked, page))
                {
                    return true;
                }
                statusText.Text = AppText.T(StartupRestoreTextKeys.BrowserJumpCancelledMultiple);
                return true;
            }

            statusText.Text = AppText.T(StartupRestoreTextKeys.BrowserJumpMultipleWindows);
            return true;
        }

        /// <summary>只读扫描本来就没最小化的窗口；不还原任何窗口。</summary>
        private static List<BrowserTabMatch> CollectVisibleBrowserMatches(
            List<BrowserJumpWindow> candidates,
            SessionItem page)
        {
            List<BrowserTabMatch> matches = new List<BrowserTabMatch>();
            if (candidates == null)
            {
                return matches;
            }
            for (int index = 0; index < candidates.Count; index++)
            {
                BrowserJumpWindow candidate = candidates[index];
                if (candidate.Minimized)
                {
                    continue;
                }
                BrowserTabMatch match = TryLocateCandidateReadOnly(candidate, page);
                if (match != null)
                {
                    matches.Add(match);
                }
            }
            return matches;
        }

        private static BrowserJumpWindow FindBestMinimizedCandidate(
            List<BrowserJumpWindow> candidates)
        {
            if (candidates == null)
            {
                return null;
            }
            for (int index = 0; index < candidates.Count; index++)
            {
                if (candidates[index].Minimized && candidates[index].Score > 0)
                {
                    return candidates[index];
                }
            }
            return null;
        }

        /// <summary>
        /// A：先安静读，确认目标标签确实存在后才激活窗口。
        ///
        /// 最小化候选：
        ///   1. 先尝试不还原直接读（UIA 偶尔已发布标签）；
        ///   2. 需要还原时用 SW_SHOWNOACTIVATE，并立刻移到屏幕外读；
        ///      绝不先 SetForegroundWindow/BringWindowToTop；
        ///   3. 最多等约 1 秒让 Chromium 发布标签栏；
        ///   4. 读前保存 WINDOWPLACEMENT，读后连同最小化状态和普通位置一起还原；
        ///   5. 找到目标才返回，随后由 ActivateBrowserTabMatch 激活并切标签；
        ///      没找到保持最小化，不再把窗口弹到用户面前。
        /// </summary>
        private bool TryLocateCandidateForJump(
            BrowserJumpWindow candidate,
            SessionItem page,
            out BrowserTabMatch match)
        {
            match = null;
            if (candidate == null)
            {
                return false;
            }

            bool restoredForTarget = false;
            bool silentReadPrepared = false;
            bool silentRestoreNeeded = false;
            NativeMethods.WindowPlacement savedPlacement = NativeMethods.WindowPlacement.Create();
            bool located = false;
            bool activeUrlMatched;
            int targetTabIndex;
            string targetTabName;
            bool targetFromPosition;
            int matchScore;
            try
            {
                if (candidate.Minimized)
                {
                    // 不还原先试一次：如果标签已经发布，就完全不动窗口。
                    located = TryLocateSavedTabWithRetry(
                        candidate.Handle, page, 0,
                        out activeUrlMatched,
                        out targetTabIndex,
                        out targetTabName,
                        out targetFromPosition,
                        out matchScore);
                    if (!located
                        && TryCaptureBrowserWindowPlacement(candidate.Handle, out savedPlacement))
                    {
                        if (TryPrepareBrowserWindowForSilentRead(
                            candidate.Handle,
                            ref savedPlacement,
                            out silentRestoreNeeded))
                        {
                            silentReadPrepared = silentRestoreNeeded;
                        }
                        else if (silentRestoreNeeded)
                        {
                            // 已经动过窗口但没准备好：不要继续读，finally 还原。
                            silentReadPrepared = true;
                            return false;
                        }
                        // silentRestoreNeeded=false 表示用户刚好自己还原了：
                        // 只需只读，不要抢窗口。
                    }
                    else if (!located)
                    {
                        // 拿不到原 placement 时退回旧的“安静显示 + 底层”，
                        // 至少不抢前台；finally 仍会缩回去。
                        restoredForTarget = true;
                        if (!TryShowBrowserWindowForRead(candidate.Handle))
                        {
                            return false;
                        }
                    }

                    if (!located)
                    {
                        // 屏幕外显示后 Chromium 需要一点时间发布整条标签栏。
                        located = TryLocateSavedTabWithRetry(
                            candidate.Handle, page, 3,
                            out activeUrlMatched,
                            out targetTabIndex,
                            out targetTabName,
                            out targetFromPosition,
                            out matchScore);
                    }
                }
                else
                {
                    located = TryLocateSavedTabWithRetry(
                        candidate.Handle, page, 1,
                        out activeUrlMatched,
                        out targetTabIndex,
                        out targetTabName,
                        out targetFromPosition,
                        out matchScore);
                }

                if (!located)
                {
                    return false;
                }
                match = CreateBrowserTabMatch(
                    candidate, activeUrlMatched, targetTabIndex, targetTabName,
                    targetFromPosition, matchScore);
                return true;
            }
            finally
            {
                if (silentReadPrepared)
                {
                    RestoreBrowserWindowAfterSilentRead(candidate.Handle, ref savedPlacement);
                }
                if (!located && restoredForTarget)
                {
                    ReMinimizeBrowserWindowAfterJumpProbe(candidate.Handle);
                }
            }
        }

        private static BrowserTabMatch TryLocateCandidateReadOnly(
            BrowserJumpWindow candidate,
            SessionItem page)
        {
            if (candidate == null)
            {
                return null;
            }
            bool activeUrlMatched;
            int targetTabIndex;
            string targetTabName;
            bool targetFromPosition;
            int matchScore;
            bool located = TryLocateSavedTab(
                candidate.Handle, page,
                out activeUrlMatched,
                out targetTabIndex,
                out targetTabName,
                out targetFromPosition,
                out matchScore);
            if (!located)
            {
                return null;
            }
            return CreateBrowserTabMatch(
                candidate, activeUrlMatched, targetTabIndex, targetTabName,
                targetFromPosition, matchScore);
        }

        private static BrowserTabMatch CreateBrowserTabMatch(
            BrowserJumpWindow candidate,
            bool activeUrlMatched,
            int targetTabIndex,
            string targetTabName,
            bool targetFromPosition,
            int matchScore)
        {
            return new BrowserTabMatch
            {
                Handle = candidate.Handle,
                WindowTitle = ReadNativeWindowTitle(candidate.Handle),
                Minimized = candidate.Minimized,
                TabIndex = targetTabIndex,
                TabName = targetTabName,
                ActiveUrlMatched = activeUrlMatched,
                TargetFromPosition = targetFromPosition,
                MatchScore = matchScore,
                WindowScore = candidate.Score,
                WindowPageCount = candidate.PageCount,
                TotalScore = 0
            };
        }

        /// <summary>
        /// 多个候选时选唯一赢家。分数接近就返回 null，交给用户选，
        /// 不允许再出现“同名窗口里随便跳一个”的行为。
        /// </summary>
        private static BrowserTabMatch SelectBrowserTabMatch(
            SessionItem page,
            SessionItem browserWindow,
            List<BrowserTabMatch> matches)
        {
            if (matches == null || matches.Count == 0)
            {
                return null;
            }
            if (matches.Count == 1)
            {
                return matches[0];
            }

            for (int index = 0; index < matches.Count; index++)
            {
                BrowserTabMatch match = matches[index];
                int score = match.MatchScore + match.WindowScore;
                if (page != null)
                {
                    if (!string.IsNullOrWhiteSpace(page.BrowserWindowTitleSnapshot)
                        && BrowserTabNameLooksLike(match.WindowTitle, page.BrowserWindowTitleSnapshot))
                    {
                        score += 240;
                    }
                    if (browserWindow != null
                        && BrowserTabNameLooksLike(match.WindowTitle, browserWindow.WindowTitle))
                    {
                        score += 120;
                    }
                    if (page.BrowserTabIndex >= 0 && page.BrowserTabIndex == match.TabIndex)
                    {
                        score += 60;
                    }
                    if (page.BrowserBindingKind == BrowserPageBindingKind.UserBound)
                    {
                        score += 40;
                    }
                }
                match.TotalScore = score;
            }

            matches.Sort(delegate(BrowserTabMatch left, BrowserTabMatch right)
            {
                return right.TotalScore.CompareTo(left.TotalScore);
            });
            if (matches[0].TotalScore - matches[1].TotalScore >= 40)
            {
                return matches[0];
            }
            return null;
        }

        /// <summary>
        /// 多个同名页面/多个窗口都命中时，让用户选一次。绝不静默跳到第一个。
        /// </summary>
        private BrowserTabMatch ShowBrowserTabMatchChooser(
            List<BrowserTabMatch> matches,
            SessionItem page)
        {
            if (matches == null || matches.Count == 0)
            {
                return null;
            }

            Window dialog = new Window
            {
                Title = AppText.T(StartupRestoreTextKeys.BrowserJumpChooserTitle),
                Owner = this,
                Width = 920,
                Height = 560,
                MinWidth = 720,
                MinHeight = 420,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false
            };
            Grid root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            TextBlock hint = new TextBlock
            {
                Text = AppText.T(StartupRestoreTextKeys.BrowserJumpChooserHint),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = AppTheme.TextSecondary
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            ListBox list = new ListBox
            {
                FontSize = 14,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 12)
            };
            int savedPageCount = page != null && page.BrowserTabs != null
                ? page.BrowserTabs.Count
                : -1;
            for (int index = 0; index < matches.Count; index++)
            {
                BrowserTabMatch match = matches[index];
                string windowTitle = match.WindowTitle
                    ?? AppText.T(StartupRestoreTextKeys.FallbackBrowserWindowFallback);
                string tabName = match.TabName
                    ?? AppText.T(StartupRestoreTextKeys.FallbackTabTitleUnknown);
                string kind = match.ActiveUrlMatched
                    ? AppText.T(StartupRestoreTextKeys.BrowserJumpMatchByUrl)
                    : (match.TargetFromPosition
                        ? AppText.T(StartupRestoreTextKeys.BrowserJumpMatchByPosition)
                        : AppText.T(StartupRestoreTextKeys.BrowserJumpMatchByTitle));
                string countNote = match.WindowPageCount > 0
                    ? (savedPageCount > 0
                        ? AppText.F(
                            StartupRestoreTextKeys.BrowserJumpCountNoteFormat,
                            match.WindowPageCount,
                            savedPageCount)
                        : AppText.F(
                            StartupRestoreTextKeys.BrowserJumpCountNoteUnsavedFormat,
                            match.WindowPageCount))
                    : string.Empty;
                string minimized = match.Minimized
                    ? AppText.T(StartupRestoreTextKeys.BrowserBindingMinimizedSuffix)
                    : string.Empty;
                string label = AppText.F(
                    StartupRestoreTextKeys.BrowserJumpOptionFormat,
                    windowTitle,
                    countNote,
                    minimized,
                    match.TabIndex + 1,
                    tabName,
                    kind);
                ListBoxItem item = new ListBoxItem
                {
                    Content = label,
                    Tag = match,
                    Padding = new Thickness(10, 8, 10, 8)
                };
                list.Items.Add(item);
            }
            list.SelectedIndex = 0;
            Grid.SetRow(list, 1);
            root.Children.Add(list);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Button ok = new Button
            {
                Content = AppText.T(StartupRestoreTextKeys.BrowserJumpChoose),
                Width = 150,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            Button cancel = new Button
            {
                Content = AppText.T(StartupRestoreTextKeys.BrowserJumpCancel),
                Width = 90,
                Height = 34,
                IsCancel = true
            };
            BrowserTabMatch selected = null;
            ok.Click += delegate
            {
                ListBoxItem item = list.SelectedItem as ListBoxItem;
                selected = item == null ? null : item.Tag as BrowserTabMatch;
                if (selected != null)
                {
                    dialog.DialogResult = true;
                }
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            dialog.Content = root;
            bool? result = dialog.ShowDialog();
            return result == true ? selected : null;
        }

        private static bool ActivateBrowserTabWindow(IntPtr windowHandle)
        {
            return BrowserPageNavigatorFactory.Create(null).Activate(windowHandle);
        }

        private bool ActivateBrowserTabMatch(BrowserTabMatch match, SessionItem page)
        {
            if (match == null || !ActivateBrowserTabWindow(match.Handle))
            {
                return false;
            }

            // 激活后重新读一次，避免扫描阶段拿到的标签元素在窗口状态变化后失效。
            bool activeUrlMatched;
            int targetTabIndex;
            string targetTabName;
            bool targetFromPosition;
            int matchScore;
            bool located = TryLocateSavedTab(
                match.Handle, page,
                out activeUrlMatched, out targetTabIndex, out targetTabName,
                out targetFromPosition, out matchScore);
            if (!located)
            {
                activeUrlMatched = match.ActiveUrlMatched;
                targetTabIndex = match.TabIndex;
                targetTabName = match.TabName;
                targetFromPosition = match.TargetFromPosition;
            }
            return TryActivateLocatedBrowserTab(
                match.Handle, page, activeUrlMatched,
                targetTabIndex, targetTabName, targetFromPosition);
        }

        /// <summary>
        /// 把一个已经定位到的标签真正切过去 / 调到前面，并写状态栏。
        /// 返回 false 表示“这个候选定位到了却选不中”。
        /// </summary>
        private bool TryActivateLocatedBrowserTab(
            IntPtr windowHandle,
            SessionItem page,
            bool activeUrlMatched,
            int targetTabIndex,
            string targetTabName,
            bool targetFromPosition)
        {
            string savedUrl = page.Url ?? page.TargetPath;

            // 目标就是当前显示的这一页：不用切，直接把它调到前面。
            if (activeUrlMatched)
            {
                ActivateBrowserTabWindow(windowHandle);
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.BrowserJumpedFormat,
                    page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback));
                return true;
            }

            if (targetTabIndex < 0)
            {
                return false;
            }

            // 必须先把目标窗口调到前台，再选标签。Edge 在非活动窗口上
            // SelectionItemPattern.Select() 会返回成功但标签不切换（Round168 实测）。
            ActivateBrowserTabWindow(windowHandle);
            if (TrySelectBrowserTabAtIndex(windowHandle, page, targetTabIndex, targetTabName))
            {
                bool verified;
                if (VerifyActiveTabUrl(windowHandle, savedUrl, out verified) && verified)
                {
                    statusText.Text = AppText.F(
                    StartupRestoreTextKeys.BrowserJumpedFormat,
                    page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback));
                }
                else if (targetFromPosition)
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.BrowserJumpedPositionFormat,
                        page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback),
                        page.BrowserTabIndex + 1);
                }
                else
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.BrowserJumpedTitleFormat,
                        page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback));
                }
                return true;
            }

            return false;
        }

        private static bool TrySelectBrowserTabAtIndex(
            IntPtr windowHandle,
            SessionItem page,
            int preferredIndex,
            string preferredName)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                if (tabs == null || tabs.Count == 0)
                {
                    return false;
                }

                int index = preferredIndex;
                if (index < 0 || index >= tabs.Count
                    || !CurrentTabMatchesPage(tabs[index], page, preferredName))
                {
                    index = FindCurrentTabIndexForPage(tabs, page);
                }
                if (index < 0 || index >= tabs.Count)
                {
                    return false;
                }
                return SessionCaptureService.TrySelectBrowserTab(tabs[index]);
            }
            catch
            {
                return false;
            }
        }

        private static bool CurrentTabMatchesPage(
            AutomationElement tab,
            SessionItem page,
            string preferredName)
        {
            if (tab == null || page == null)
            {
                return false;
            }
            string name = SessionCaptureService.TryReadAutomationProperty(
                tab, AutomationElement.NameProperty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(preferredName)
                && BrowserTabNameLooksLike(name, preferredName))
            {
                return true;
            }
            string savedUrl = page.Url ?? page.TargetPath;
            if (!string.IsNullOrWhiteSpace(savedUrl)
                && WebAddressTools.TabNameMatchesUrl(name, savedUrl))
            {
                return true;
            }
            return BrowserTabNameLooksLike(name, page.WindowTitle);
        }

        private static int FindCurrentTabIndexForPage(
            List<AutomationElement> tabs,
            SessionItem page)
        {
            if (tabs == null || page == null)
            {
                return -1;
            }
            string savedUrl = page.Url ?? page.TargetPath;
            int urlHit = -1;
            int urlHits = 0;
            int titleHit = -1;
            int titleHits = 0;
            for (int index = 0; index < tabs.Count; index++)
            {
                string name = SessionCaptureService.TryReadAutomationProperty(
                    tabs[index], AutomationElement.NameProperty);
                if (!string.IsNullOrWhiteSpace(savedUrl)
                    && WebAddressTools.TabNameMatchesUrl(name, savedUrl))
                {
                    urlHits++;
                    urlHit = index;
                }
                if (BrowserTabNameLooksLike(name, page.WindowTitle))
                {
                    titleHits++;
                    titleHit = index;
                }
            }
            if (urlHits == 1)
            {
                return urlHit;
            }
            if (urlHits == 0 && titleHits == 1)
            {
                return titleHit;
            }
            return -1;
        }

        /// <summary>
        /// 候选窗口。只用于跳转排序，不参与保存数据。
        /// </summary>
        private sealed class BrowserJumpWindow
        {
            public IntPtr Handle;
            public bool Minimized;
            public int Score;
            public int EnumerationOrder;

            // 2026-11-06 同名网页消歧用：标题/指纹的身份事实和整窗标签数。
            public bool ActivePageMatches;
            public bool SavedTabContainsTitle;
            public bool ParentTitleMatches;
            public bool PageCountKnown;
            public int PageCount;
        }

        /// <summary>一个窗口里定位到的候选标签。先收集、后选赢家，避免“第一个命中就跳”。</summary>
        private sealed class BrowserTabMatch
        {
            public IntPtr Handle;
            public string WindowTitle;
            public bool Minimized;
            public int TabIndex;
            public string TabName;
            public bool ActiveUrlMatched;
            public bool TargetFromPosition;
            public int MatchScore;
            public int WindowScore;
            public int WindowPageCount;
            public int TotalScore;
        }

        private static SessionItem FindBrowserWindowParent(IList<SessionItem> items, SessionItem page)
        {
            if (items == null || page == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(page.ParentId))
            {
                for (int index = 0; index < items.Count; index++)
                {
                    SessionItem candidate = items[index];
                    if (candidate != null
                        && candidate.Kind != SessionItemKind.WebPage
                        && string.Equals(candidate.Id, page.ParentId, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(page.BrowserGroupKey))
            {
                return null;
            }

            SessionItem fallback = null;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate == null
                    || candidate.Kind == SessionItemKind.WebPage
                    || string.IsNullOrWhiteSpace(candidate.WindowTitle)
                    || !string.Equals(candidate.BrowserGroupKey, page.BrowserGroupKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (page.BrowserWindowIndex >= 0
                    && candidate.BrowserWindowIndex >= 0
                    && candidate.BrowserWindowIndex != page.BrowserWindowIndex)
                {
                    continue;
                }
                if (fallback != null)
                {
                    // 同组同序号有多个父项时无法判断，宁可不给提示。
                    return null;
                }
                fallback = candidate;
            }
            return fallback;
        }

        /// <summary>
        /// 用“窗口标题 / 保存页标题 / 父窗口保存标题 / BrowserWindowIndex”做只读打分。
        /// 窗口标题就是当前活动标签的标题，所以它只能证明“保存的那一页现在是活动标签”
        /// 这一种情况；其余情况仍由后面的标签栏定位兜底。
        /// </summary>
        private static List<BrowserJumpWindow> BuildBrowserJumpWindows(
            List<IntPtr> windows,
            SessionItem page,
            SessionItem browserWindow,
            string savedTitle,
            IList<SessionItem> sourceItems)
        {
            List<BrowserJumpWindow> result = new List<BrowserJumpWindow>();
            if (windows == null)
            {
                return result;
            }

            string groupKey = page == null ? null : page.BrowserGroupKey;
            int savedWindowIndex = page == null ? -1 : page.BrowserWindowIndex;
            for (int index = 0; index < windows.Count; index++)
            {
                IntPtr handle = windows[index];
                string windowTitle = ReadNativeWindowTitle(handle);
                bool titleMatchesSaved = BrowserTabNameLooksLike(windowTitle, savedTitle);
                bool activePageMatches = BrowserTitleProfile.SameActivePageTitle(
                    windowTitle, savedTitle);
                bool parentTitleMatches = browserWindow != null
                    && BrowserTabNameLooksLike(windowTitle, browserWindow.WindowTitle);
                bool savedTabContainsTitle = page != null
                    && page.BrowserTabs != null
                    && SavedTabTitleMatchesWindow(windowTitle, page.BrowserTabs);
                int pageCount = -1;
                bool pageCountKnown = BrowserTitleProfile.TryReadPageCount(
                    windowTitle, out pageCount);
                if (!pageCountKnown)
                {
                    pageCount = -1;
                }
                int score = 0;

                if (titleMatchesSaved)
                {
                    score += page != null && page.IsActiveBrowserTab ? 140 : 100;
                }
                if (parentTitleMatches)
                {
                    score += 80;
                }
                if (page != null
                    && !string.IsNullOrWhiteSpace(page.BrowserWindowTitleSnapshot)
                    && BrowserTabNameLooksLike(windowTitle, page.BrowserWindowTitleSnapshot))
                {
                    score += 160;
                }
                if (page != null && page.BrowserBindingKind == BrowserPageBindingKind.UserBound)
                {
                    score += 30;
                }
                if (savedTabContainsTitle)
                {
                    // 保存的整窗指纹里任意一个标签标题等于当前窗口标题，
                    // 说明目标网页很可能就在这个窗口（无需先还原窗口）。
                    score += 90;
                }

                int mappedIndex = FindSavedBrowserWindowIndexForTitle(
                    sourceItems, windowTitle, groupKey);
                if (mappedIndex >= 0
                    && savedWindowIndex >= 0
                    && mappedIndex == savedWindowIndex)
                {
                    score += 60;
                }

                result.Add(new BrowserJumpWindow
                {
                    Handle = handle,
                    Minimized = NativeMethods.IsIconic(handle),
                    Score = score,
                    EnumerationOrder = index,
                    ActivePageMatches = activePageMatches,
                    SavedTabContainsTitle = savedTabContainsTitle,
                    ParentTitleMatches = parentTitleMatches,
                    PageCountKnown = pageCountKnown,
                    PageCount = pageCount
                });
            }

            result.Sort(delegate(BrowserJumpWindow left, BrowserJumpWindow right)
            {
                int byScore = right.Score.CompareTo(left.Score);
                if (byScore != 0)
                {
                    return byScore;
                }
                if (left.Minimized != right.Minimized)
                {
                    return left.Minimized ? 1 : -1;
                }
                return left.EnumerationOrder.CompareTo(right.EnumerationOrder);
            });
            return result;
        }

        private static bool SavedTabTitleMatchesWindow(
            string windowTitle,
            IList<BrowserTabFingerprint> tabs)
        {
            if (string.IsNullOrWhiteSpace(windowTitle) || tabs == null)
            {
                return false;
            }
            for (int index = 0; index < tabs.Count; index++)
            {
                BrowserTabFingerprint tab = tabs[index];
                if (tab != null
                    && !string.IsNullOrWhiteSpace(tab.Title)
                    && BrowserTabNameLooksLike(windowTitle, tab.Title))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 用保存下来的“浏览器父窗口标题”反查这个实际窗口当初的 BrowserWindowIndex。
        /// 标题对不上（例如保存后用户切了标签）时返回 -1，不要猜。
        /// </summary>
        private static int FindSavedBrowserWindowIndexForTitle(
            IList<SessionItem> items,
            string actualTitle,
            string groupKey)
        {
            if (items == null
                || string.IsNullOrWhiteSpace(actualTitle)
                || string.IsNullOrWhiteSpace(groupKey))
            {
                return -1;
            }

            int match = -1;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate == null
                    || candidate.Kind == SessionItemKind.WebPage
                    || !string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(candidate.WindowTitle)
                    || !BrowserTabNameLooksLike(actualTitle, candidate.WindowTitle))
                {
                    continue;
                }
                if (match >= 0)
                {
                    // 多个父窗口标题都像：无法唯一映射，放弃这个提示。
                    return -1;
                }
                match = candidate.BrowserWindowIndex;
            }
            return match;
        }

        private static bool TryReadActiveTabUrlExact(IntPtr windowHandle, string savedUrl)
        {
            if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(savedUrl))
            {
                return false;
            }
            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }
                string current = TryReadActiveTabUrl(root);
                return !string.IsNullOrWhiteSpace(current)
                    && WebAddressTools.TabNameMatchesUrl(current, savedUrl);
            }
            catch
            {
                return false;
            }
        }

    }
}
