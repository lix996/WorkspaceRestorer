using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Core;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.StartupRestore.Core;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// Owns the application lifecycle.  Window code only observes this object
    /// and invokes commands, which keeps long-running restore work off the UI
    /// thread and makes the core easy to exercise without creating a window.
    /// </summary>
    // partial：StartupRestore/Core 里有一个薄薄的“自启动应用恢复转接”分部。
    // 代码仍归启动与恢复文件夹，Applications 只调用公开入口。
    public sealed partial class ApplicationController : INotifyPropertyChanged, IDisposable
    {
        private static int CountSuccessfulItems(RestoreResult result)
        {
            int count = 0;
            if (result != null && result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    if (result.Items[index] != null && result.Items[index].Success)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// B3：用户关闭浏览器窗口后跳过的网页数。完成状态栏和横幅共用这个计数，
        /// 避免状态栏只写“成功 N/N”，让用户以为所有网页都真的打开了。
        /// </summary>
        private static int CountBrowserWindowClosedItems(RestoreResult result)
        {
            int count = 0;
            if (result != null && result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    if (result.Items[index] != null
                        && result.Items[index].WasBrowserWindowClosed)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// 用户手动最小化浏览器窗口后跳过的网页数。
        /// 与“关窗跳过”分开统计，状态栏和横幅才能写清是缩小而不是关闭。
        /// </summary>
        private static int CountBrowserWindowUserMinimizedItems(RestoreResult result)
        {
            int count = 0;
            if (result != null && result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    if (result.Items[index] != null
                        && result.Items[index].WasBrowserWindowUserMinimized)
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// 与工作会话的 `RestoreCompletion` 用同一个模型，所以卡片、横幅、取消提示的文案都不用另写一套。
        /// </summary>
        private void RaiseApplicationsRestoreCompleted(
            int total,
            int started,
            int failed,
            int skipped,
            bool cancelled,
            IList<RestoreFailure> failures)
        {
            Action<RestoreCompletion> handler = RestoreCompleted;
            if (handler == null)
            {
                return;
            }
            InvokeRestoreCompletedHandler(new RestoreCompletion
            {
                Source = RestoreSource.Applications,
                IsCancelled = cancelled,
                TotalCount = total,
                SuccessCount = started,
                FailedCount = failed,
                AlreadyOpenCount = skipped,
                Failures = failures ?? new List<RestoreFailure>()
            });
        }

        /// <summary>
        /// 完成事件可能从后台线程抛（自启动应用的等待/间隔在 Task.Run 里跑），
        /// 而订阅方（App.OnRestoreCompleted）会读窗口状态，必须在界面线程触发。
        /// </summary>
        private void InvokeRestoreCompletedHandler(RestoreCompletion completion)
        {
            Action raise = delegate
            {
                Action<RestoreCompletion> current = RestoreCompleted;
                if (current != null)
                {
                    current(completion);
                }
            };
            System.Windows.Application app = System.Windows.Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(raise);
            }
            else
            {
                raise();
            }
        }

        private void RaiseRestoreCompleted(RestoreResult result, bool isCancelled)
        {
            Action<RestoreCompletion> handler = RestoreCompleted;
            if (handler == null)
            {
                return;
            }

            int total = result == null || result.Items == null ? 0 : result.Items.Count;
            int alreadyOpen = 0;
            int userClosed = 0;
            int browserWindowClosed = 0;
            int browserWindowUserMinimized = 0;
            if (result != null && result.Items != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    RestoreItemResult item = result.Items[index];
                    if (item != null && item.WasAlreadyOpen)
                    {
                        alreadyOpen++;
                    }
                    if (item != null && item.WasUserClosed)
                    {
                        userClosed++;
                    }
                    if (item != null && item.WasBrowserWindowClosed)
                    {
                        browserWindowClosed++;
                    }
                    if (item != null && item.WasBrowserWindowUserMinimized)
                    {
                        browserWindowUserMinimized++;
                    }
                }
            }
            // 用户 2026-10-15：要说清楚“到底是哪些没恢复、为什么”。
            List<RestoreFailure> failures = DescribeRestoreFailures(result == null ? null : result.Items);
            InvokeRestoreCompletedHandler(new RestoreCompletion
            {
                // 同一条恢复内核也会被「自启动应用 → 立即恢复 / 开机自动启动」调用：
                // 那时按自启动应用那一路取文案。现在两份文案一样，
                // 但路由必须对，否则以后只改一边就会串到另一边。
                Source = applicationsRestoreRunning
                    ? RestoreSource.Applications
                    : RestoreSource.StartupRestore,
                IsCancelled = isCancelled,
                IsAutoStopped = result != null && result.IsAutoStopped,
                CancelReason = result == null ? null : result.CancelReason,
                TotalCount = total,
                SuccessCount = CountSuccessfulItems(result),
                FailedCount = failures.Count,
                AlreadyOpenCount = alreadyOpen,
                UserClosedCount = userClosed,
                BrowserWindowClosedCount = browserWindowClosed,
                BrowserWindowUserMinimizedCount = browserWindowUserMinimized,
                // 窗口级结果：网页本身启动成功了，只是保存的最小化没敢替用户做。
                BrowserWindowNotReadyCount = result == null ? 0 : result.BrowserWindowNotReadyCount,
                Failures = failures
            });
        }

        /// <summary>
        /// 从恢复结果里挑出“没成功、也不是被抑制”的条目，逐条给出名字和中文原因。
        /// 用户 2026-10-15 要求：“要在提示上说清楚到底是哪些没有恢复（以及为什么无法恢复）”。
        /// 真正的实现在公共模型 RestoreFailureText 里（那边 public，测试可以直接调）。
        /// </summary>
        internal static List<RestoreFailure> DescribeRestoreFailures(IList<RestoreItemResult> results)
        {
            return RestoreFailureText.Describe(results);
        }

        private void RaiseSaveCompleted(SessionSnapshot snapshot, bool failed, bool cancelled)
        {
            Action<SaveCompletion> handler = SaveCompleted;
            if (handler == null)
            {
                return;
            }

            handler(new SaveCompletion
            {
                ItemCount = snapshot == null || snapshot.Items == null ? 0 : snapshot.Items.Count,
                Incomplete = snapshot != null
                    && (snapshot.BrowserCaptureIncomplete
                        || !string.IsNullOrWhiteSpace(snapshot.CaptureWarning)),
                // 【2026-10-19】“有些网页没读到”只由**真的没读到**决定；终端/本地网址是按设置跳过的，
                // 单独记下来给横幅用，两者不能混在一起（否则会误导用户去显示最小化窗口）。
                BrowserIncomplete = snapshot != null && snapshot.BrowserCaptureIncomplete,
                UnreadBrowserWindows = snapshot == null ? null : snapshot.UnreadBrowserWindows,
                BrowserCaptureTimedOut = snapshot != null && snapshot.BrowserCaptureTimedOut,
                PolicyAffectedCount = snapshot == null ? 0 : snapshot.PolicyAffectedCount,
                PolicySkippedCount = snapshot == null ? 0 : snapshot.PolicySkippedCount,
                PolicyIncluded = snapshot != null && snapshot.PolicyIncluded,
                PolicyHint = snapshot == null ? null : snapshot.PolicyHint,
                Failed = failed,
                Cancelled = cancelled,
                WarningText = snapshot == null ? null : snapshot.CaptureWarning
            });
        }

        private static SessionItem CreateDesktopSessionItem(DesktopEntry entry, DateTime startTimeUtc)
        {
            string path = entry.Path == null ? string.Empty : entry.Path.Trim();
            bool isDirectory = false;
            try
            {
                isDirectory = Directory.Exists(Environment.ExpandEnvironmentVariables(path));
            }
            catch
            {
            }

            if (isDirectory)
            {
                return new SessionItem
                {
                    Kind = SessionItemKind.ExplorerFolder,
                    IsEnabled = true,
                    DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? path : entry.DisplayName,
                    TargetPath = path,
                    ExecutablePath = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe"),
                    StartTimeUtc = startTimeUtc,
                    CaptureSource = "DesktopSelection"
                };
            }

            string extension = entry.Extension ?? string.Empty;
            bool executable = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
            return new SessionItem
            {
                Kind = executable ? SessionItemKind.Application : SessionItemKind.Document,
                IsEnabled = true,
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? path : entry.DisplayName,
                ExecutablePath = executable ? path : null,
                TargetPath = executable ? null : path,
                WorkingDirectory = GetParentDirectory(path),
                StartTimeUtc = startTimeUtc,
                CaptureSource = "DesktopSelection"
            };
        }

        private static List<SessionItem> MergeCapturedItems(
            IList<SessionItem> capturedItems,
            StartupProfile profile,
            bool preserveUnobservedNestedItems)
        {
            List<SessionItem> result = capturedItems == null
                ? new List<SessionItem>()
                : new List<SessionItem>(capturedItems.Where(delegate(SessionItem item) { return item != null; }));
            if (profile == null)
            {
                return result;
            }

            // Browser window ordinals are assigned from the current visible
            // order. Stabilize them before exclusions and state matching so a
            // foreground-window change cannot move a deleted/disabled page to
            // another browser window.
            StabilizeBrowserWindowIdentities(result, profile.Items);

            EnsureExcludedItemKeys(profile);
            if (profile.ExcludedItemKeys.Count > 0)
            {
                HashSet<string> excludedKeys = new HashSet<string>(profile.ExcludedItemKeys, StringComparer.OrdinalIgnoreCase);
                result = result.Where(delegate(SessionItem item)
                {
                    return !IsExcludedCapturedItem(item, excludedKeys);
                }).ToList();
            }
            if (profile.Items == null)
            {
                return result;
            }

            // Carry per-item enable choices across periodic captures.  The
            // capture service creates fresh SessionItem instances each time,
            // so matching by path is required instead of matching by id.
            for (int index = 0; index < result.Count; index++)
            {
                SessionItem capturedItem = result[index];
                SessionItem previous = FindEquivalentSessionItem(profile.Items, capturedItem);
                if (previous != null)
                {
                    capturedItem.IsEnabled = previous.IsEnabled;
                    PreserveCapturedWindowPlacement(capturedItem, previous);
                    if (IsVisualStudioCodeItem(capturedItem) && IsVisualStudioCodeItem(previous))
                    {
                        // Window titles can temporarily lose the workspace
                        // name while Code is loading. Keep the last known path
                        // and launch arguments instead of downgrading the next
                        // restore to an empty Code window.
                        if (string.IsNullOrWhiteSpace(capturedItem.TargetPath)
                            && !string.IsNullOrWhiteSpace(previous.TargetPath))
                        {
                            capturedItem.TargetPath = previous.TargetPath;
                            capturedItem.Arguments = previous.Arguments;
                        }
                    }
                    if (string.Equals(previous.CaptureSource, "DesktopSelection", StringComparison.OrdinalIgnoreCase))
                    {
                        capturedItem.CaptureSource = "DesktopSelection";
                    }
                }
            }

            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem selected = profile.Items[index];
                if (selected == null)
                {
                    continue;
                }

                bool keepExplicitDesktopSelection = string.Equals(
                    selected.CaptureSource,
                    "DesktopSelection",
                    StringComparison.OrdinalIgnoreCase);
                bool keepPassiveNestedItem = preserveUnobservedNestedItems
                    && IsNestedSessionItem(selected)
                    && HasCapturedNestedParent(result, selected);
                if (!keepExplicitDesktopSelection && !keepPassiveNestedItem)
                {
                    continue;
                }

                if (!ContainsEquivalentSessionItem(result, selected))
                {
                    SessionItem parent = keepPassiveNestedItem
                        ? FindCapturedNestedParent(result, selected)
                        : null;
                    if (parent != null)
                    {
                        selected.ParentId = parent.Id;
                    }
                    result.Add(selected);
                }
            }

            // Window identity stabilization can copy an older parent label
            // (for example the legacy "Microsoft Edge (1)" text). Recompute
            // browser labels after merging so a single visible window is
            // displayed without a misleading ordinal while multiple windows
            // keep their numbered names.
            SessionCaptureService.NormalizeBrowserWindowDisplayNames(result);
            return result;
        }

        /// <summary>
        /// “保存当前方案”重新捕获窗口时，处理上一版方案里的窗口状态：
        ///   · 用户手动点过最小化/取消最小化 → 保留用户选择，只更新位置/大小；
        ///   · 没手动点过 → 沿用旧逻辑：新捕获没有有效坐标时保留上一份有效位置。
        /// </summary>
        private static void PreserveCapturedWindowPlacement(
            SessionItem capturedItem,
            SessionItem previous)
        {
            if (capturedItem == null || previous == null)
            {
                return;
            }

            SavedWindowPlacement previousPlacement = previous.WindowPlacement;
            SavedWindowPlacement capturedPlacement = capturedItem.WindowPlacement;
            if (previousPlacement != null && previousPlacement.MinimizeChoiceExplicit)
            {
                if (capturedPlacement == null || !capturedPlacement.HasBounds)
                {
                    // 窗口重建时暂时没有句柄/坐标：整份沿用上一版，包括用户选择。
                    capturedItem.WindowPlacement = previousPlacement;
                    return;
                }
                // 新捕获有真实位置：位置/大小用新的，最小化选择用用户手动设置的。
                capturedPlacement.WindowState = previousPlacement.WindowState;
                capturedPlacement.WindowStateBeforeMinimize = previousPlacement.WindowStateBeforeMinimize;
                capturedPlacement.MinimizeChoiceExplicit = true;
                return;
            }

            if ((capturedPlacement == null || !capturedPlacement.HasBounds)
                && previousPlacement != null
                && previousPlacement.HasBounds)
            {
                // A window can briefly lose its native handle while an
                // application or Explorer is rebuilding. Keep the last valid
                // placement instead of replacing it with an empty value during
                // a periodic capture.
                capturedItem.WindowPlacement = previousPlacement;
            }
        }

        private static void StabilizeBrowserWindowIdentities(
            IList<SessionItem> capturedItems,
            IList<SessionItem> previousItems)
        {
            if (capturedItems == null || previousItems == null)
            {
                return;
            }

            HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> stableIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> stableParentIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < capturedItems.Count; index++)
            {
                SessionItem captured = capturedItems[index];
                if (captured == null
                    || captured.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(captured.BrowserGroupKey)
                    || captured.BrowserWindowIndex < 0)
                {
                    continue;
                }

                SessionItem previous = FindMatchingBrowserWindow(previousItems, captured, claimed);
                if (previous == null)
                {
                    continue;
                }

                int originalIndex = captured.BrowserWindowIndex;
                string browserIdentity = BuildBrowserWindowIdentity(
                    GetBrowserGroupIdentity(captured),
                    originalIndex);
                stableIndexes[browserIdentity] = previous.BrowserWindowIndex;
                if (!string.IsNullOrWhiteSpace(previous.Id))
                {
                    stableParentIds[browserIdentity] = previous.Id;
                }

                captured.BrowserWindowIndex = previous.BrowserWindowIndex;
                captured.DisplayName = previous.DisplayName;
                captured.IsEnabled = previous.IsEnabled;
                if (!string.IsNullOrWhiteSpace(previous.Id))
                {
                    claimed.Add(previous.Id);
                }
            }

            for (int index = 0; index < capturedItems.Count; index++)
            {
                SessionItem captured = capturedItems[index];
                if (captured == null || captured.Kind != SessionItemKind.WebPage || captured.BrowserWindowIndex < 0)
                {
                    continue;
                }

                string browserIdentity = BuildBrowserWindowIdentity(
                    GetBrowserGroupIdentity(captured),
                    captured.BrowserWindowIndex);
                int stableIndex;
                if (stableIndexes.TryGetValue(browserIdentity, out stableIndex))
                {
                    captured.BrowserWindowIndex = stableIndex;
                    string stableParentId;
                    if (stableParentIds.TryGetValue(browserIdentity, out stableParentId))
                    {
                        captured.ParentId = stableParentId;
                    }
                }
            }
        }

        private static string BuildBrowserWindowIdentity(string browserGroup, int windowIndex)
        {
            return (browserGroup ?? string.Empty).Trim().ToLowerInvariant() + "|window|" + windowIndex;
        }

        private static SessionItem FindMatchingBrowserWindow(
            IList<SessionItem> previousItems,
            SessionItem captured,
            ISet<string> claimed)
        {
            SessionItem ordinalFallback = null;
            for (int index = 0; index < previousItems.Count; index++)
            {
                SessionItem previous = previousItems[index];
                if (previous == null
                    || previous.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(previous.BrowserGroupKey)
                    || (!string.IsNullOrWhiteSpace(previous.Id) && claimed.Contains(previous.Id))
                    || !string.Equals(
                        GetBrowserGroupIdentity(previous),
                        GetBrowserGroupIdentity(captured),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(captured.WindowTitle)
                    && string.Equals(
                        captured.WindowTitle.Trim(),
                        (previous.WindowTitle ?? string.Empty).Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return previous;
                }

                if (previous.BrowserWindowIndex == captured.BrowserWindowIndex)
                {
                    ordinalFallback = previous;
                }
            }

            return ordinalFallback;
        }

        private static bool HasCapturedBrowserWindow(IList<SessionItem> items, SessionItem page)
        {
            return FindCapturedBrowserWindow(items, page) != null;
        }

        private static bool HasCapturedNestedParent(IList<SessionItem> items, SessionItem nested)
        {
            return FindCapturedNestedParent(items, nested) != null;
        }

        private static SessionItem FindCapturedNestedParent(IList<SessionItem> items, SessionItem nested)
        {
            if (nested == null)
            {
                return null;
            }
            if (nested.Kind == SessionItemKind.WebPage)
            {
                return FindCapturedBrowserWindow(items, nested);
            }
            if (nested.Kind != SessionItemKind.ExplorerFolder || items == null)
            {
                return null;
            }

            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (!IsExplorerWindowItem(candidate))
                {
                    continue;
                }
                if (nested.ApplicationWindowIndex >= 0
                    && candidate.ApplicationWindowIndex >= 0
                    && nested.ApplicationWindowIndex == candidate.ApplicationWindowIndex)
                {
                    return candidate;
                }
                if (!string.IsNullOrWhiteSpace(nested.ParentId)
                    && !string.IsNullOrWhiteSpace(candidate.Id)
                    && string.Equals(nested.ParentId, candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static SessionItem FindCapturedBrowserWindow(IList<SessionItem> items, SessionItem page)
        {
            if (items == null || page == null || page.Kind != SessionItemKind.WebPage)
            {
                return null;
            }

            string pageGroup = GetBrowserGroupIdentity(page);
            SessionItem unknownOrdinalFallback = null;
            int unknownOrdinalCount = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate == null
                    || candidate.Kind != SessionItemKind.Application
                    || string.IsNullOrWhiteSpace(candidate.BrowserGroupKey))
                {
                    continue;
                }

                if (candidate.BrowserWindowIndex == page.BrowserWindowIndex
                    && string.Equals(
                        GetBrowserGroupIdentity(candidate),
                        pageGroup,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }

                // A passive/baseline browser scan can identify the browser
                // process but fail to assign a visible-window ordinal.  When
                // the group has exactly one such unknown parent, it is safe
                // to use it for retaining previously captured pages.  Do
                // not guess in an ambiguous multi-window group.
                if (page.BrowserWindowIndex >= 0 && candidate.BrowserWindowIndex < 0)
                {
                    unknownOrdinalFallback = candidate;
                    unknownOrdinalCount++;
                }
            }

            if (page.BrowserWindowIndex < 0)
            {
                // Legacy page records did not carry a window ordinal.  Keep
                // the historical same-group fallback for those records.
                for (int index = 0; index < items.Count; index++)
                {
                    SessionItem candidate = items[index];
                    if (candidate != null
                        && candidate.Kind == SessionItemKind.Application
                        && string.Equals(
                            GetBrowserGroupIdentity(candidate),
                            pageGroup,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            return unknownOrdinalCount == 1 ? unknownOrdinalFallback : null;
        }

        private static void EnsureExcludedItemKeys(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            if (profile.ExcludedItemKeys == null)
            {
                profile.ExcludedItemKeys = new List<string>();
                return;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = profile.ExcludedItemKeys.Count - 1; index >= 0; index--)
            {
                string key = profile.ExcludedItemKeys[index];
                if (string.IsNullOrWhiteSpace(key) || !seen.Add(key.Trim()))
                {
                    profile.ExcludedItemKeys.RemoveAt(index);
                }
                else if (!string.Equals(key, key.Trim(), StringComparison.Ordinal))
                {
                    profile.ExcludedItemKeys[index] = key.Trim();
                }
            }
        }

        private static void AddExcludedItemKey(StartupProfile profile, string key)
        {
            if (profile == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            EnsureExcludedItemKeys(profile);
            if (!profile.ExcludedItemKeys.Any(delegate(string existing)
            {
                return string.Equals(existing, key, StringComparison.OrdinalIgnoreCase);
            }))
            {
                profile.ExcludedItemKeys.Add(key);
            }
        }

        private static bool IsExcludedCapturedItem(SessionItem item, ISet<string> excludedKeys)
        {
            if (item == null || excludedKeys == null || excludedKeys.Count == 0)
            {
                return false;
            }

            string itemKey = GetItemExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(itemKey) && excludedKeys.Contains(itemKey))
            {
                return true;
            }

            string browserGroupKey = GetBrowserGroupExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(browserGroupKey) && excludedKeys.Contains(browserGroupKey))
            {
                return true;
            }

            string parentKey = GetParentExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(parentKey) && excludedKeys.Contains(parentKey))
            {
                return true;
            }

            string explorerTitleKey = GetExplorerWindowTitleExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(explorerTitleKey) && excludedKeys.Contains(explorerTitleKey))
            {
                return true;
            }

            // Profiles created before Explorer parents were introduced used
            // the generic path key for folder entries.  Continue honoring it
            // so an old deletion does not reappear after upgrading.
            string legacyFolderKey = GetLegacyExplorerFolderExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(legacyFolderKey)
                && !string.Equals(legacyFolderKey, itemKey, StringComparison.OrdinalIgnoreCase)
                && excludedKeys.Contains(legacyFolderKey))
            {
                return true;
            }

            // A legacy profile may have deleted a page before tab identities
            // were available.  Continue honoring that broader key even when
            // a later capture includes window/tab metadata.
            string legacyPageKey = GetLegacyWebPageExclusionKey(item);
            if (!string.IsNullOrWhiteSpace(legacyPageKey)
                && !string.Equals(legacyPageKey, itemKey, StringComparison.OrdinalIgnoreCase)
                && excludedKeys.Contains(legacyPageKey))
            {
                return true;
            }

            // Old or imported page records may not know their browser path.
            // A URL-only key remains a supported compatibility fallback.
            string urlOnlyKey = GetUrlOnlyExclusionKey(item);
            return !string.IsNullOrWhiteSpace(urlOnlyKey) && excludedKeys.Contains(urlOnlyKey);
        }

        private static string GetItemExclusionKey(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (IsExplorerWindowItem(item))
            {
                return GetExplorerWindowExclusionKey(item);
            }

            if (item.Kind == SessionItemKind.ExplorerFolder)
            {
                string folder = NormalizeComparablePath(item.TargetPath);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return null;
                }
                // The COM window ordinal can change when Explorer windows
                // close or reopen.  The folder path is the stable identity
                // needed to keep an explicitly deleted folder suppressed.
                return "explorer-folder|" + folder;
            }

            if (IsVisualStudioCodeItem(item))
            {
                string executable = NormalizeComparablePath(item.ExecutablePath);
                string target = NormalizeComparablePath(item.TargetPath);
                string index = item.ApplicationWindowIndex < 0
                    ? "unknown"
                    : item.ApplicationWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    // Workspace path remains the stable identity when a user
                    // drags windows around or Windows enumerates them in a
                    // different order after login. The window index is used
                    // only by the in-memory matcher to separate two windows
                    // that intentionally open the same folder; persisting the
                    // ordinal would make a deleted item reappear when another
                    // Code window is opened before it.
                    return string.IsNullOrWhiteSpace(executable)
                        ? "code-window|path|" + target
                        : "code-window|" + executable + "|path|" + target;
                }
                return string.IsNullOrWhiteSpace(executable)
                    ? "code-window|index|" + index
                    : "code-window|" + executable + "|index|" + index;
            }

            if (item.Kind == SessionItemKind.WebPage)
            {
                string url = GetWebPageUrl(item);
                if (string.IsNullOrWhiteSpace(url))
                {
                    return null;
                }
                string browserGroup = GetBrowserGroupIdentity(item);
                if (string.IsNullOrWhiteSpace(browserGroup))
                {
                    return GetUrlOnlyExclusionKey(item);
                }
                if (HasBrowserTabIdentity(item))
                {
                    string window = item.BrowserWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string tab = item.BrowserTabIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return "web-page-tab|" + browserGroup + "|" + window + "|" + tab + "|" + url;
                }
                return "web-page|" + browserGroup + "|" + url;
            }

            if (IsBrowserWindowItem(item) && item.BrowserWindowIndex >= 0)
            {
                return "browser-window|" + GetBrowserGroupIdentity(item) + "|"
                    + item.BrowserWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            string path = !string.IsNullOrWhiteSpace(item.TargetPath)
                ? item.TargetPath
                : item.ExecutablePath;
            string normalizedPath = NormalizeComparablePath(path);
            return string.IsNullOrWhiteSpace(normalizedPath) ? null : "item-path|" + normalizedPath;
        }

        private static bool IsVisualStudioCodeItem(SessionItem item)
        {
            return item != null
                && string.Equals(item.CaptureSource, "VisualStudioCodeWindow", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBrowserWindowItem(SessionItem item)
        {
            return item != null
                && item.Kind == SessionItemKind.Application
                && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
        }

        private static bool IsExplorerWindowItem(SessionItem item)
        {
            return item != null
                && item.Kind == SessionItemKind.Application
                && string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetExplorerWindowExclusionKey(SessionItem item)
        {
            if (!IsExplorerWindowItem(item))
            {
                return null;
            }
            string executable = NormalizeComparablePath(item.ExecutablePath);
            return "explorer-window|" + executable + "|window|"
                + item.ApplicationWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string GetExplorerWindowTitleExclusionKey(SessionItem item)
        {
            if (!IsExplorerWindowItem(item) || string.IsNullOrWhiteSpace(item.WindowTitle))
            {
                return null;
            }
            string executable = NormalizeComparablePath(item.ExecutablePath);
            return "explorer-window|" + executable + "|title|" + item.WindowTitle.Trim();
        }

        private static string GetParentExclusionKey(SessionItem item)
        {
            if (IsExplorerWindowItem(item))
            {
                return GetExplorerWindowExclusionKey(item);
            }
            return GetBrowserGroupExclusionKey(item);
        }

        private static string GetLegacyExplorerFolderExclusionKey(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.ExplorerFolder)
            {
                return null;
            }
            string folder = NormalizeComparablePath(item.TargetPath);
            return string.IsNullOrWhiteSpace(folder) ? null : "item-path|" + folder;
        }

        private static bool IsNestedSessionItem(SessionItem item)
        {
            return item != null
                && (item.Kind == SessionItemKind.WebPage
                    || item.Kind == SessionItemKind.ExplorerFolder);
        }

        private static string GetUrlOnlyExclusionKey(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.WebPage)
            {
                return null;
            }
            string url = GetWebPageUrl(item);
            return string.IsNullOrWhiteSpace(url) ? null : "web-page-url|" + url;
        }

        private static string GetLegacyWebPageExclusionKey(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.WebPage)
            {
                return null;
            }

            string url = GetWebPageUrl(item);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string browserGroup = GetBrowserGroupIdentity(item);
            return string.IsNullOrWhiteSpace(browserGroup)
                ? null
                : "web-page|" + browserGroup + "|" + url;
        }

        private static string GetBrowserGroupExclusionKey(SessionItem item)
        {
            if (item == null
                || (item.Kind != SessionItemKind.WebPage && string.IsNullOrWhiteSpace(item.BrowserGroupKey)))
            {
                return null;
            }
            string browserGroup = GetBrowserGroupIdentity(item);
            if (string.IsNullOrWhiteSpace(browserGroup))
            {
                return null;
            }
            if (item.BrowserWindowIndex >= 0)
            {
                return "browser-window|" + browserGroup + "|"
                    + item.BrowserWindowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return "browser-group|" + browserGroup;
        }

        private static string GetBrowserGroupIdentity(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }

            string value = item.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = item.BrowserExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(value)
                && (item.Kind == SessionItemKind.WebPage || !string.IsNullOrWhiteSpace(item.BrowserGroupKey)))
            {
                value = item.ExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            value = value.Trim();
            bool looksLikePath = Path.IsPathRooted(value)
                || value.IndexOf(Path.DirectorySeparatorChar) >= 0
                || value.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
            return looksLikePath ? NormalizeComparablePath(value) : value;
        }

        private static string GetWebPageUrl(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }
            string value = !string.IsNullOrWhiteSpace(item.Url) ? item.Url : item.TargetPath;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

    }
}
