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
    /// 网页归属：右键“设置网页归属”、窗口/标签选择与写回（从 BrowserPageJumpWorkspace.cs 搬迁）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 右键“设置网页归属…”：列出所有已打开浏览器窗口/标签，让用户明确选一次。
        /// 两个工作区共用；返回 null 表示用户取消。
        /// </summary>
        internal BrowserPageBindingOption ChooseBrowserPageBinding(SessionItem page)
        {
            if (page == null)
            {
                return null;
            }
            string executable = ResolveJumpExecutable(page);
            string normalized = NormalizeExecutablePath(executable);
            AppLog.Write("[网页归属] 打开选择框：executable=" + (executable ?? "null"));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show(
                    this,
                    AppText.T(StartupRestoreTextKeys.BrowserBindingNoExecutable),
                    AppText.T(StartupRestoreTextKeys.BrowserBindingTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return null;
            }

            IBrowserPageNavigator navigator = BrowserPageNavigatorFactory.Create(normalized);
            List<IntPtr> windows = navigator.EnumerateWindows(normalized);
            List<BrowserPageBindingOption> options = CollectBrowserPageBindingOptions(windows, page);
            AppLog.Write("[网页归属] 窗口=" + windows.Count + "，可选标签=" + options.Count);
            if (options.Count == 0)
            {
                MessageBox.Show(
                    this,
                    AppText.T(StartupRestoreTextKeys.BrowserBindingNoWindows),
                    AppText.T(StartupRestoreTextKeys.BrowserBindingTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return null;
            }

            BrowserPageBindingDialog dialog = new BrowserPageBindingDialog(options);
            dialog.Owner = this;
            return dialog.ShowDialog() == true ? dialog.SelectedOption : null;
        }

        private List<BrowserPageBindingOption> CollectBrowserPageBindingOptions(
            List<IntPtr> windows,
            SessionItem page)
        {
            List<BrowserPageBindingOption> options = new List<BrowserPageBindingOption>();
            if (windows == null || windows.Count == 0)
            {
                return options;
            }
            string savedUrl = page == null ? null : (page.Url ?? page.TargetPath);
            string savedTitle = page == null ? null : page.WindowTitle;
            if (string.IsNullOrWhiteSpace(savedTitle) && page != null)
            {
                savedTitle = page.DisplayName;
            }

            for (int windowIndex = 0; windowIndex < windows.Count; windowIndex++)
            {
                IntPtr handle = windows[windowIndex];
                bool wasMinimized = NativeMethods.IsIconic(handle);
                bool restoredForRead = false;
                try
                {
                    if (wasMinimized)
                    {
                        if (!TryShowBrowserWindowForRead(handle))
                        {
                            continue;
                        }
                        restoredForRead = true;
                    }

                    AutomationElement root = AutomationElement.FromHandle(handle);
                    if (root == null)
                    {
                        continue;
                    }
                    string activeUrl = TryReadActiveTabUrl(root);
                    List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                    if (tabs == null || tabs.Count == 0)
                    {
                        continue;
                    }

                    List<BrowserTabFingerprint> fingerprints = new List<BrowserTabFingerprint>();
                    List<string> names = new List<string>();
                    List<bool> selectedStates = new List<bool>();
                    for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
                    {
                        string name = SessionCaptureService.TryReadAutomationProperty(
                            tabs[tabIndex], AutomationElement.NameProperty);
                        bool selected = IsAutomationTabSelected(tabs[tabIndex]);
                        string tabUrl = selected
                            ? activeUrl
                            : (!string.IsNullOrWhiteSpace(WebAddressTools.CanonicalizeForMatch(name))
                                ? name
                                : null);
                        names.Add(name);
                        selectedStates.Add(selected);
                        fingerprints.Add(new BrowserTabFingerprint
                        {
                            Index = tabIndex,
                            Title = name,
                            Url = tabUrl,
                            IsActive = selected
                        });
                    }

                    string windowTitle = ReadNativeWindowTitle(handle);
                    List<BrowserPageBindingOption> windowOptions = new List<BrowserPageBindingOption>();
                    for (int tabIndex = 0; tabIndex < names.Count; tabIndex++)
                    {
                        string tabName = names[tabIndex];
                        string tabUrl = fingerprints[tabIndex].Url;
                        bool urlMatch = !string.IsNullOrWhiteSpace(savedUrl)
                            && !string.IsNullOrWhiteSpace(tabUrl)
                            && WebAddressTools.TabNameMatchesUrl(tabUrl, savedUrl);
                        bool nameUrlMatch = !string.IsNullOrWhiteSpace(savedUrl)
                            && WebAddressTools.TabNameMatchesUrl(tabName, savedUrl);
                        bool titleMatch = BrowserTabNameLooksLike(tabName, savedTitle);
                        windowOptions.Add(new BrowserPageBindingOption
                        {
                            WindowHandle = handle,
                            WindowTitle = windowTitle,
                            WindowOrdinal = windowIndex,
                            WindowMinimized = wasMinimized,
                            TabIndex = tabIndex,
                            TabName = tabName,
                            TabUrl = tabUrl,
                            IsActive = selectedStates[tabIndex],
                            Tabs = BrowserTabFingerprint.CloneList(fingerprints),
                            IsRecommended = urlMatch || nameUrlMatch
                                || (titleMatch && selectedStates[tabIndex])
                        });
                    }

                    bool windowRecommended = windowOptions.Exists(
                        delegate(BrowserPageBindingOption candidate)
                        {
                            return candidate != null && candidate.IsRecommended;
                        });
                    for (int index = 0; index < windowOptions.Count; index++)
                    {
                        windowOptions[index].WindowRecommended = windowRecommended;
                        options.Add(windowOptions[index]);
                    }
                }
                catch
                {
                }
                finally
                {
                    if (restoredForRead)
                    {
                        ReMinimizeBrowserWindowAfterJumpProbe(handle);
                    }
                }
            }

            // 推荐窗口整组排最前，避免用户在一堆无关 Edge 窗口里翻。
            options.Sort(delegate(BrowserPageBindingOption left, BrowserPageBindingOption right)
            {
                if (left.WindowRecommended != right.WindowRecommended)
                {
                    return left.WindowRecommended ? -1 : 1;
                }
                if (left.IsRecommended != right.IsRecommended)
                {
                    return left.IsRecommended ? -1 : 1;
                }
                if (left.IsActive != right.IsActive)
                {
                    return left.IsActive ? -1 : 1;
                }
                int byWindow = left.WindowOrdinal.CompareTo(right.WindowOrdinal);
                if (byWindow != 0)
                {
                    return byWindow;
                }
                return left.TabIndex.CompareTo(right.TabIndex);
            });
            return options;
        }

        private static bool IsAutomationTabSelected(AutomationElement tab)
        {
            if (tab == null)
            {
                return false;
            }
            try
            {
                object pattern;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern))
                {
                    SelectionItemPattern selection = pattern as SelectionItemPattern;
                    if (selection != null)
                    {
                        return selection.Current.IsSelected;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 手动绑定到后台标签时，UIA 标签名往往只是页面标题，拿不到网址。
        /// 这里在**用户明确选择的窗口**里短暂选中目标标签、读一次地址栏，再把原来的
        /// 标签选回去，最后恢复最小化。只用于“设置网页归属”这种显式操作。
        /// </summary>
        internal string ResolveBrowserTabUrlForBinding(BrowserPageBindingOption option)
        {
            if (option == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(option.TabUrl))
            {
                return option.TabUrl;
            }
            if (option.WindowHandle == IntPtr.Zero)
            {
                return null;
            }

            bool wasMinimized = NativeMethods.IsIconic(option.WindowHandle);
            bool restoredForRead = false;
            try
            {
                if (wasMinimized)
                {
                    if (!TryShowBrowserWindowForRead(option.WindowHandle))
                    {
                        return null;
                    }
                    restoredForRead = true;
                }
                AutomationElement root = AutomationElement.FromHandle(option.WindowHandle);
                if (root == null)
                {
                    return null;
                }
                List<AutomationElement> tabs = SessionCaptureService.FindBrowserTabs(root);
                if (tabs == null || option.TabIndex < 0 || option.TabIndex >= tabs.Count)
                {
                    return null;
                }

                int originalIndex = -1;
                for (int index = 0; index < tabs.Count; index++)
                {
                    if (IsAutomationTabSelected(tabs[index]))
                    {
                        originalIndex = index;
                        break;
                    }
                }

                if (originalIndex != option.TabIndex
                    && !SessionCaptureService.TrySelectBrowserTab(tabs[option.TabIndex]))
                {
                    return null;
                }
                System.Threading.Thread.Sleep(220);
                root = AutomationElement.FromHandle(option.WindowHandle);
                string url = root == null ? null : TryReadActiveTabUrl(root);

                if (originalIndex >= 0 && originalIndex != option.TabIndex)
                {
                    try
                    {
                        List<AutomationElement> currentTabs =
                            SessionCaptureService.FindBrowserTabs(root);
                        if (currentTabs != null && originalIndex < currentTabs.Count)
                        {
                            SessionCaptureService.TrySelectBrowserTab(currentTabs[originalIndex]);
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                    catch
                    {
                    }
                }
                return url;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (restoredForRead)
                {
                    ReMinimizeBrowserWindowAfterJumpProbe(option.WindowHandle);
                }
            }
        }

        /// <summary>
        /// 把用户选中的“窗口 + 标签”写回工作方案这条网页。
        /// 父浏览器窗口不存在时会新建一个，保证网页仍然有归属，不变成孤立项。
        /// </summary>
        internal bool ApplyBrowserPageBinding(
            StartupProfile profile,
            SessionItem page,
            BrowserPageBindingOption option)
        {
            if (profile == null || page == null || option == null
                || profile.Items == null || !profile.Items.Contains(page))
            {
                return false;
            }

            string executable = ResolveJumpExecutable(page);
            string groupKey = page.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                groupKey = NormalizeExecutablePath(executable);
            }
            SessionItem parent = FindBrowserParentForBinding(profile.Items, page, groupKey, option.WindowTitle);
            if (parent == null)
            {
                parent = CreateBrowserParentForBinding(profile, page, groupKey, option);
            }

            string resolvedUrl = ResolveBrowserTabUrlForBinding(option);
            List<BrowserTabFingerprint> tabs = BrowserTabFingerprint.CloneList(option.Tabs);
            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                option.TabUrl = resolvedUrl;
                for (int index = 0; index < tabs.Count; index++)
                {
                    if (tabs[index] != null && tabs[index].Index == option.TabIndex)
                    {
                        tabs[index].Url = resolvedUrl;
                        break;
                    }
                }
            }

            page.ParentId = parent.Id;
            page.BrowserWindowIndex = parent.BrowserWindowIndex;
            page.BrowserTabIndex = option.TabIndex;
            page.WindowTitle = option.TabName;
            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                page.Url = resolvedUrl;
                page.TargetPath = resolvedUrl;
            }
            page.BrowserExecutablePath = FirstNonEmptyValue(
                page.BrowserExecutablePath, parent.BrowserExecutablePath, executable);
            page.BrowserGroupKey = groupKey;
            page.BrowserTabs = tabs;
            page.BrowserWindowTitleSnapshot = option.WindowTitle;
            page.BrowserBindingKind = BrowserPageBindingKind.UserBound;
            page.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
            page.BrowserTabIdentityCaptured = true;
            page.IsActiveBrowserTab = option.IsActive;

            parent.BrowserTabs = BrowserTabFingerprint.CloneList(tabs);
            parent.BrowserWindowTitleSnapshot = option.WindowTitle;
            parent.BrowserBindingKind = BrowserPageBindingKind.UserBound;
            parent.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(parent.WindowTitle))
            {
                parent.WindowTitle = option.WindowTitle;
            }

            controller.SetProfileItemsOrder(profile, profile.Items);
            RefreshProfileList();
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.BrowserBindingStatusFormat,
                page.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback),
                parent.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackBrowserWindowFallback),
                option.TabIndex + 1);
            return true;
        }

        private static SessionItem FindBrowserParentForBinding(
            IList<SessionItem> items,
            SessionItem page,
            string groupKey,
            string windowTitle)
        {
            if (items == null || page == null)
            {
                return null;
            }
            SessionItem byTitle = null;
            int titleHits = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate == null
                    || candidate.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                    || !string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (BrowserTabNameLooksLike(candidate.WindowTitle, windowTitle))
                {
                    titleHits++;
                    byTitle = candidate;
                }
            }
            if (titleHits == 1)
            {
                return byTitle;
            }

            if (!string.IsNullOrWhiteSpace(page.ParentId))
            {
                for (int index = 0; index < items.Count; index++)
                {
                    SessionItem candidate = items[index];
                    if (candidate != null
                        && candidate.Kind == SessionItemKind.Application
                        && string.Equals(candidate.Id, page.ParentId, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrWhiteSpace(candidate.WindowTitle)
                            || BrowserTabNameLooksLike(candidate.WindowTitle, windowTitle)))
                    {
                        return candidate;
                    }
                }
            }

            SessionItem only = null;
            int parents = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate != null
                    && candidate.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                    && string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                {
                    parents++;
                    only = candidate;
                }
            }
            if (parents == 1
                && (string.IsNullOrWhiteSpace(only.WindowTitle)
                    || BrowserTabNameLooksLike(only.WindowTitle, windowTitle)))
            {
                return only;
            }
            return null;
        }

        private SessionItem CreateBrowserParentForBinding(
            StartupProfile profile,
            SessionItem page,
            string groupKey,
            BrowserPageBindingOption option)
        {
            int nextIndex = 0;
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem candidate = profile.Items[index];
                if (candidate != null
                    && candidate.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                    && string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase)
                    && candidate.BrowserWindowIndex >= nextIndex)
                {
                    nextIndex = candidate.BrowserWindowIndex + 1;
                }
            }

            string browserName = FirstNonEmptyValue(
                page.BrowserName,
                AppText.T(StartupRestoreTextKeys.FallbackBrowserFallback));
            SessionItem parent = new SessionItem
            {
                Kind = SessionItemKind.Application,
                DisplayName = browserName + " (" + (nextIndex + 1) + ")",
                ExecutablePath = page.BrowserExecutablePath,
                BrowserExecutablePath = page.BrowserExecutablePath,
                BrowserName = browserName,
                BrowserGroupKey = groupKey,
                BrowserWindowIndex = nextIndex,
                WindowTitle = option.WindowTitle,
                BrowserTabs = BrowserTabFingerprint.CloneList(option.Tabs),
                BrowserWindowTitleSnapshot = option.WindowTitle,
                BrowserBindingKind = BrowserPageBindingKind.UserBound,
                BrowserBindingUpdatedAtUtc = DateTime.UtcNow,
                IsEnabled = true,
                CaptureSource = "UserBoundBrowserWindow"
            };
            int pageIndex = profile.Items.IndexOf(page);
            if (pageIndex < 0)
            {
                pageIndex = profile.Items.Count;
            }
            profile.Items.Insert(pageIndex, parent);
            return parent;
        }

    }
}
