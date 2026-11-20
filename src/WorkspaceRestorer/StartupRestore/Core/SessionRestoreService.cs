using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Services
{
    public sealed class SessionRestoreService : ISessionRestoreService
    {
        private readonly IProcessLauncher processLauncher;
        private readonly ISystemLoadMonitor systemLoadMonitor;

        public SessionRestoreService()
            : this(new ProcessLauncher(), new SystemLoadMonitor())
        {
        }

        public SessionRestoreService(
            IProcessLauncher processLauncher,
            ISystemLoadMonitor systemLoadMonitor)
        {
            if (processLauncher == null)
            {
                throw new ArgumentNullException("processLauncher");
            }

            if (systemLoadMonitor == null)
            {
                throw new ArgumentNullException("systemLoadMonitor");
            }

            this.processLauncher = processLauncher;
            this.systemLoadMonitor = systemLoadMonitor;

            // 窗口恢复策略属于「启动与恢复」；自启动应用通过本服务转接，自动获得同一套
            // 隐藏窗口过滤/恢复前体检/恢复后巡检，不需要在 Applications 里再复制窗口代码。
            WindowPlacementPolicy.Current = new StartupRestoreWindowPlacementPolicy();
        }

        public async Task<RestoreResult> RestoreAsync(
            SessionSnapshot snapshot,
            RestoreOptions options,
            IProgress<RestoreProgress> progress,
            CancellationToken cancellationToken)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            RestoreResult result = new RestoreResult();
            result.StartedAtUtc = DateTime.UtcNow;
            List<SessionItem> items = GetEnabledItems(snapshot);
            bool spreadWebPages = options.WebPageDelay > TimeSpan.Zero;
            // 每个浏览器窗口还有几页没启动：最后一页启动完才允许缩最小化窗口。
            Dictionary<string, int> remainingBrowserPages =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SessionItem> browserFirstPages =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SessionItem> browserLastPages =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            // 【2026-10-18】一次恢复开始：清掉“这块屏已经放了几个窗口”的计数，
            // 拓扑变少屏时窗口才能从主屏左上角开始阶梯错开（不会每次恢复都越错越远）。
            WindowPlacementService.ResetRestoreBatch();
            IBrowserWindowBatchLauncher batchLauncher = processLauncher as IBrowserWindowBatchLauncher;
            IRestoreItemTrackingLauncher itemTracker = processLauncher as IRestoreItemTrackingLauncher;
            // 只有正常走完全部流程才让“3 秒后台补缩”继续；取消/失败要它立刻作废。
            bool restoreCompleted = false;
            if (batchLauncher != null)
            {
                batchLauncher.BeginBrowserRestore();
            }
            if (itemTracker != null)
            {
                itemTracker.BeginRestoreItemTracking(items);
            }

            try
            {
                Report(progress, new RestoreProgress
                {
                    State = RestoreProgressState.WaitingInitialDelay,
                    ItemNumber = 0,
                    TotalItems = items.Count,
                    Message = AppText.T(StartupRestoreTextKeys.RestoreProgressWaitingInitialDelay)
                });
                await DelayAsync(options.InitialDelay, cancellationToken).ConfigureAwait(false);

                for (int index = 0; index < items.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SessionItem item = items[index];

                    SystemLoadSnapshot load = null;
                    if (options.WaitForAcceptableLoad)
                    {
                        load = await WaitForAcceptableLoadAsync(
                            options,
                            item,
                            index + 1,
                            items.Count,
                            progress,
                            cancellationToken).ConfigureAwait(false);
                    }

                    Report(progress, new RestoreProgress
                    {
                        State = RestoreProgressState.StartingItem,
                        ItemNumber = index + 1,
                        TotalItems = items.Count,
                        Item = item,
                        SystemLoad = load,
                        Message = AppText.F(
                            StartupRestoreTextKeys.RestoreProgressStartingFormat, GetItemName(item))
                    });

                    // B3：轮到这一页时，如果用户已经把这个网页所在的浏览器
                    // 窗口打叉关闭，就跳过该窗口组剩余网页。只影响这一组，
                    // 其它浏览器窗口继续按原顺序恢复。
                    bool browserWindowClosed = false;
                    bool browserWindowUserMinimized = false;
                    if (batchLauncher != null && item.Kind == SessionItemKind.WebPage)
                    {
                        try
                        {
                            browserWindowClosed =
                                batchLauncher.IsBrowserWindowClosedByUser(item);
                            if (!browserWindowClosed)
                            {
                                browserWindowUserMinimized =
                                    batchLauncher.IsBrowserWindowUserMinimized(item);
                            }
                        }
                        catch (Exception exception)
                        {
                            // 关窗/接管判定是增强，读窗口失败不能反过来停掉整次恢复。
                            AppLog.Write("[窗口恢复] 检查浏览器窗口状态时失败，继续恢复："
                                + GetItemName(item), exception);
                        }
                    }

                    LaunchResult launch;
                    if (browserWindowClosed)
                    {
                        // 只记结果，不再调用启动器；这个窗口组后面的网页
                        // 也会在各自轮到时走同一分支。
                        launch = new LaunchResult
                        {
                            Success = true,
                            WasBrowserWindowClosed = true
                        };
                    }
                    else if (browserWindowUserMinimized)
                    {
                        // 用户手动最小化 = 用户接管，不再还原窗口去开剩余网页。
                        launch = new LaunchResult
                        {
                            Success = true,
                            WasBrowserWindowUserMinimized = true
                        };
                    }
                    else
                    {
                    try
                    {
                        // When a web-page gap is configured, every saved page is
                        // launched on its own so the restored window fills up one
                        // tab at a time instead of asking the browser to fetch
                        // every URL in the same instant (which fails when the
                        // machine has just booted and the network is still
                        // settling).
                        if (batchLauncher != null && item.StartsBrowserWindow)
                        {
                            List<SessionItem> browserPages = GetBrowserWindowPages(items, item);
                            // B3：浏览器窗口始终逐页启动，不再把整窗 URL 一次性
                            // 交给浏览器。只有逐页启动，用户中途关窗时才能真的
                            // 不再打开该组剩余网页；批量启动的 URL 无法撤回。
                            // 网页间隔仍按用户设置控制，间隔为 0 时连续逐页启动。
                            batchLauncher.PrepareBrowserWindowTitles(item, browserPages);

                            string browserKey = GetBrowserWindowKey(item);
                            if (!string.IsNullOrWhiteSpace(browserKey) && browserPages.Count > 0)
                            {
                                remainingBrowserPages[browserKey] = browserPages.Count;
                                browserFirstPages[browserKey] = item;
                                browserLastPages[browserKey] = browserPages[browserPages.Count - 1];
                            }
                        }
                        launch = processLauncher.Launch(item);
                    }
                    catch (Exception exception)
                    {
                        launch = new LaunchResult
                        {
                            Success = false,
                            Error = exception.Message
                        };
                    }
                    }

                    RestoreItemResult itemResult = new RestoreItemResult();
                    itemResult.Item = item;
                    itemResult.AttemptedAtUtc = DateTime.UtcNow;
                    itemResult.Success = launch != null && launch.Success;
                    itemResult.WasSuppressed = launch != null && launch.WasSuppressed;
                    itemResult.WasAlreadyOpen = launch != null && launch.WasAlreadyOpen;
                    itemResult.WasUserClosed = launch != null && launch.WasUserClosed;
                    itemResult.WasBrowserWindowClosed =
                        launch != null && launch.WasBrowserWindowClosed;
                    itemResult.WasBrowserWindowUserMinimized =
                        launch != null && launch.WasBrowserWindowUserMinimized;
                    itemResult.Error = launch == null ? "The launcher returned no result." : launch.Error;
                    result.Items.Add(itemResult);

                    Report(progress, new RestoreProgress
                    {
                        State = RestoreProgressState.ItemCompleted,
                        ItemNumber = index + 1,
                        TotalItems = items.Count,
                        Item = item,
                        SystemLoad = load,
                        Message = itemResult.WasBrowserWindowClosed
                            ? AppText.F(StartupRestoreTextKeys.RestoreProgressBrowserClosedFormat, GetItemName(item))
                            : (itemResult.WasBrowserWindowUserMinimized
                                ? AppText.F(StartupRestoreTextKeys.RestoreProgressBrowserMinimizedFormat, GetItemName(item))
                                : (itemResult.WasUserClosed
                                    ? AppText.F(StartupRestoreTextKeys.RestoreProgressUserClosedFormat, GetItemName(item))
                                    : (itemResult.Success
                                        ? AppText.F(StartupRestoreTextKeys.RestoreProgressStartedFormat, GetItemName(item))
                                        : AppText.F(StartupRestoreTextKeys.RestoreProgressFailedFormat, GetItemName(item)))))
                    });

                    // 这个浏览器窗口的最后一页已经启动完：只有到这一步，
                    // 保存为最小化的窗口才缩一次。
                    if (item != null && item.Kind == SessionItemKind.WebPage)
                    {
                        string browserKey = GetBrowserWindowKey(item);
                        int remaining;
                        if (!string.IsNullOrWhiteSpace(browserKey)
                            && remainingBrowserPages.TryGetValue(browserKey, out remaining))
                        {
                            remaining--;
                            if (remaining <= 0)
                            {
                                remainingBrowserPages.Remove(browserKey);
                                SessionItem firstPage;
                                SessionItem lastPage;
                                browserFirstPages.TryGetValue(browserKey, out firstPage);
                                browserLastPages.TryGetValue(browserKey, out lastPage);
                                browserFirstPages.Remove(browserKey);
                                browserLastPages.Remove(browserKey);
                                if (batchLauncher != null && firstPage != null)
                                {
                                    batchLauncher.CompleteBrowserWindowRestore(firstPage, lastPage);
                                }
                            }
                            else
                            {
                                remainingBrowserPages[browserKey] = remaining;
                            }
                        }
                    }

                    if (index + 1 < items.Count)
                    {
                        // Items that were already open (or suppressed) did not
                        // start anything, so skip the between-item delay and
                        // move to the next one quickly.
                        bool fastSkip = itemResult.WasAlreadyOpen
                            || itemResult.WasSuppressed
                            || itemResult.WasUserClosed
                            || itemResult.WasBrowserWindowClosed
                            || itemResult.WasBrowserWindowUserMinimized;
                        if (!fastSkip)
                        {
                            // Web pages use their own gap so the restore can
                            // hand them to the browser one at a time; every
                            // other transition keeps the general item delay.
                            bool nextIsWebPage = index + 1 < items.Count
                                && items[index + 1] != null
                                && items[index + 1].Kind == SessionItemKind.WebPage;
                            bool currentIsWebPage = item != null && item.Kind == SessionItemKind.WebPage;
                            TimeSpan gap = spreadWebPages && currentIsWebPage && nextIsWebPage
                                ? options.WebPageDelay
                                : options.ItemDelay;
                            await DelayAsync(gap, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                // B1：所有项目都启动完之后，先等浏览器缩窗 worker 收尾，
                // 再宣布“恢复完成”。现在 worker 最多 settle 3 秒；3 秒没读到标题
                // 就立刻返回（补缩交给后台线程），所以完成提示不会被拖到 6 秒。
                // 取消时不等待：catch 里直接结束，finally 会让 worker 过期。
                if (batchLauncher != null)
                {
                    Report(progress, new RestoreProgress
                    {
                        State = RestoreProgressState.Finalizing,
                        ItemNumber = items.Count,
                        TotalItems = items.Count,
                        Message = AppText.T(StartupRestoreTextKeys.RestoreProgressFinalizing)
                    });
                    bool settled = await batchLauncher
                        .WaitForBrowserRestoreSettledAsync(cancellationToken)
                        .ConfigureAwait(false);
                    // 收尾 worker 结束后才能读：单页窗口“3 秒内没读到标题、所以先保持原样”
                    // 是最后一页之后才知道的，收尾前读会漏数。
                    result.BrowserWindowNotReadyCount = batchLauncher.BrowserWindowNotReadyCount;
                    if (!settled)
                    {
                        AppLog.Write("[窗口恢复] 浏览器缩窗收尾到达硬上限，仍有 worker 未结束；"
                            + "恢复完成后不再允许晚到的窗口控制。");
                    }
                }

                restoreCompleted = true;
                Report(progress, new RestoreProgress
                {
                    State = RestoreProgressState.Completed,
                    ItemNumber = items.Count,
                    TotalItems = items.Count,
                    Message = AppText.T(StartupRestoreTextKeys.RestoreProgressCompleted)
                });
            }
            catch (RestoreLoadLimitException limit)
            {
                result.IsCancelled = true;
                result.IsAutoStopped = true;
                result.CancelReason = limit.Reason;
                Report(progress, new RestoreProgress
                {
                    State = RestoreProgressState.Cancelled,
                    ItemNumber = result.Items.Count,
                    TotalItems = items.Count,
                    Message = limit.Reason
                });
            }
            catch (OperationCanceledException)
            {
                result.IsCancelled = true;
                Report(progress, new RestoreProgress
                {
                    State = RestoreProgressState.Cancelled,
                    ItemNumber = result.Items.Count,
                    TotalItems = items.Count,
                    Message = AppText.T(StartupRestoreTextKeys.RestoreProgressCancelled)
                });
            }
            finally
            {
                if (batchLauncher != null)
                {
                    if (!restoreCompleted)
                    {
                        // 取消 / 失败：让已排队的“3 秒后台补缩”作废，
                        // 恢复中止后绝不能再有窗口被缩。
                        batchLauncher.AbortBrowserLateMinimize();
                    }
                    batchLauncher.EndBrowserRestore();
                }
                if (itemTracker != null)
                {
                    itemTracker.EndRestoreItemTracking();
                }
                result.FinishedAtUtc = DateTime.UtcNow;
            }

            return result;
        }

        private async Task<SystemLoadSnapshot> WaitForAcceptableLoadAsync(
            RestoreOptions options,
            SessionItem item,
            int itemNumber,
            int totalItems,
            IProgress<RestoreProgress> progress,
            CancellationToken cancellationToken)
        {
            DateTime? cpuOverSince = null;
            DateTime? memoryOverSince = null;
            DateTime pauseStartedUtc = DateTime.MinValue;
            TimeSpan? lockedWait = null;
            int cpuBelowStreak = 0;
            int memoryBelowStreak = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SystemLoadSnapshot load;
                try
                {
                    load = await systemLoadMonitor.ReadAsync(
                        NormalizeInterval(options.CpuSampleInterval),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // A load probe is advisory.  If Windows temporarily refuses
                    // the counters, continue rather than blocking restoration.
                    load = new SystemLoadSnapshot
                    {
                        IsAvailable = false,
                        Error = exception.Message
                    };
                }

                if (load == null || !load.IsAvailable)
                {
                    Report(progress, new RestoreProgress
                    {
                        State = RestoreProgressState.WaitingForSystemLoad,
                        ItemNumber = itemNumber,
                        TotalItems = totalItems,
                        Item = item,
                        SystemLoad = load,
                        Message = AppText.T(StartupRestoreTextKeys.RestoreProgressLoadUnavailable)
                    });
                    return load;
                }

                // 用户点过“立即恢复”：本次恢复后续不再被 CPU/内存挡住。
                if (options.LoadGate != null && options.LoadGate.SkipLoadGate)
                {
                    return load;
                }

                DateTime now = DateTime.UtcNow;
                bool cpuOver = load.CpuPercent > options.MaximumCpuPercent;
                bool memoryOver = load.MemoryPercent > options.MaximumMemoryPercent;

                if (!lockedWait.HasValue)
                {
                    if (!cpuOver && !memoryOver)
                    {
                        return load;
                    }

                    // 进入本次暂停：把等待上限锁定在这里。
                    // 两项同时超限取较长值；只有一项就取它自己的。
                    pauseStartedUtc = now;
                    lockedWait = cpuOver && memoryOver
                        ? Max(options.LoadWaitTimeout, options.MemoryWaitTimeout)
                        : (cpuOver ? options.LoadWaitTimeout : options.MemoryWaitTimeout);
                    cpuBelowStreak = cpuOver ? 0 : 1;
                    memoryBelowStreak = memoryOver ? 0 : 1;
                }
                else
                {
                    // 稳定判定：连续 2 次采样都低于自己的上限，才算这一项恢复。
                    // 这样 CPU 9%/11% 在 10% 上限附近抖动时，不会一次低采样就让浮窗状态乱跳。
                    if (cpuOver)
                    {
                        cpuBelowStreak = 0;
                    }
                    else
                    {
                        cpuBelowStreak++;
                    }
                    if (memoryOver)
                    {
                        memoryBelowStreak = 0;
                    }
                    else
                    {
                        memoryBelowStreak++;
                    }
                }

                if (cpuOver)
                {
                    if (!cpuOverSince.HasValue)
                    {
                        cpuOverSince = now;
                    }
                }
                else
                {
                    cpuOverSince = null;
                }
                if (memoryOver)
                {
                    if (!memoryOverSince.HasValue)
                    {
                        memoryOverSince = now;
                    }
                }
                else
                {
                    memoryOverSince = null;
                }

                if (!cpuOver && !memoryOver
                    && cpuBelowStreak >= 2
                    && memoryBelowStreak >= 2)
                {
                    return load;
                }

                double elapsedSeconds = Math.Max(0.0, (now - pauseStartedUtc).TotalSeconds);
                double waitSeconds = Math.Max(0.0, lockedWait.Value.TotalSeconds);
                double remainingSeconds = Math.Max(0.0, waitSeconds - elapsedSeconds);
                int effectiveElapsed = (int)Math.Floor(elapsedSeconds);
                int effectiveRemaining = (int)Math.Ceiling(remainingSeconds);

                Report(progress, new RestoreProgress
                {
                    State = RestoreProgressState.WaitingForSystemLoad,
                    ItemNumber = itemNumber,
                    TotalItems = totalItems,
                    Item = item,
                    SystemLoad = load,
                    LoadPauseActive = true,
                    CpuOverLimit = cpuOver,
                    MemoryOverLimit = memoryOver,
                    CpuLimitPercent = (int)Math.Round(options.MaximumCpuPercent),
                    MemoryLimitPercent = (int)Math.Round(options.MaximumMemoryPercent),
                    CpuWaitLimitSeconds = (int)Math.Round(options.LoadWaitTimeout.TotalSeconds),
                    MemoryWaitLimitSeconds = (int)Math.Round(options.MemoryWaitTimeout.TotalSeconds),
                    CpuOverSeconds = cpuOver ? (int)Math.Floor((now - cpuOverSince.Value).TotalSeconds) : 0,
                    MemoryOverSeconds = memoryOver ? (int)Math.Floor((now - memoryOverSince.Value).TotalSeconds) : 0,
                    EffectiveWaitLimitSeconds = (int)Math.Round(waitSeconds),
                    EffectiveElapsedSeconds = effectiveElapsed,
                    EffectiveRemainingSeconds = effectiveRemaining,
                    Message = AppText.T(StartupRestoreTextKeys.RestoreProgressLoadPaused)
                });

                if (remainingSeconds <= 0.0)
                {
                    if (cpuOver || memoryOver)
                    {
                        throw new RestoreLoadLimitException(
                            BuildLoadLimitReason(cpuOver, memoryOver, options));
                    }
                    return load;
                }

                await DelayAsync(options.LoadPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 自动停止的完整中文句子。只写用户设置的上限，不写当前值/等待秒数；
        /// 用户要求提示里不能出现写死的百分比。
        /// </summary>
        private static string BuildLoadLimitReason(
            bool cpuOver,
            bool memoryOver,
            RestoreOptions options)
        {
            int cpuLimit = (int)Math.Round(options.MaximumCpuPercent);
            int memoryLimit = (int)Math.Round(options.MaximumMemoryPercent);
            if (cpuOver && memoryOver)
            {
                return AppText.F(
                    StartupRestoreTextKeys.RestoreProgressLoadLimitBothFormat, cpuLimit, memoryLimit);
            }
            if (cpuOver)
            {
                return AppText.F(
                    StartupRestoreTextKeys.RestoreProgressLoadLimitCpuFormat, cpuLimit);
            }
            return AppText.F(
                StartupRestoreTextKeys.RestoreProgressLoadLimitMemoryFormat, memoryLimit);
        }

        private static TimeSpan Max(TimeSpan left, TimeSpan right)
        {
            return left >= right ? left : right;
        }

        private static List<SessionItem> GetEnabledItems(SessionSnapshot snapshot)
        {
            IEnumerable<SessionItem> source = snapshot.Items ?? new List<SessionItem>();
            HashSet<string> disabledParentIds = new HashSet<string>(
                source
                    .Where(delegate(SessionItem item)
                    {
                        return item != null
                            && item.Kind == SessionItemKind.Application
                            && !item.IsEnabled
                            && !string.IsNullOrWhiteSpace(item.Id);
                    })
                    .Select(delegate(SessionItem item) { return item.Id.Trim(); }),
                StringComparer.OrdinalIgnoreCase);
            List<SessionItem> enabled = source
                .Where(delegate(SessionItem item)
                {
                    return item != null
                        && item.IsEnabled
                        && (string.IsNullOrWhiteSpace(item.ParentId)
                            || !disabledParentIds.Contains(item.ParentId.Trim()));
                })
                .ToList();

            // Browser and Explorer application rows are presentation parents
            // when they own nested entries.  Restore only the user-editable
            // child entries so an empty browser/Explorer window is never
            // started in addition to the explicit page/folder list.
            // Build these sets from all saved children, including disabled
            // ones.  If a user disables every child, the parent must still be
            // suppressed rather than opening a blank window.
            HashSet<string> nestedParentIds = new HashSet<string>(
                source
                    .Where(delegate(SessionItem item)
                    {
                        return IsNestedSessionItem(item)
                            && !string.IsNullOrWhiteSpace(item.ParentId);
                    })
                    .Select(delegate(SessionItem item) { return item.ParentId.Trim(); }),
                StringComparer.OrdinalIgnoreCase);
            List<SessionItem> ordered = enabled
                .Where(delegate(SessionItem item)
                {
                    if (item.Kind != SessionItemKind.Application)
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(item.Id)
                        && nestedParentIds.Contains(item.Id.Trim()))
                    {
                        return false;
                    }

                    // 资源管理器窗口父项也只负责“文件夹归属哪个窗口”。有文件夹时
                    // 上面已由 nestedParentIds 过滤；没有文件夹时不能当普通 explorer.exe
                    // 启动，否则会出现一个空白资源管理器窗口。
                    if (string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    // 浏览器父项只负责“网页归属哪个窗口”，本身不是用户要启动的应用。
                    // 有网页时上面已由 nestedParentIds 过滤；没有网页时也不能落到这里
                    // 被当成普通 msedge.exe 启动，否则会出现一个空白 Edge 窗口。
                    if (!string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                    {
                        return false;
                    }
                    return true;
                })
                .OrderBy(delegate(SessionItem item) { return item.Sequence <= 0 ? int.MaxValue : item.Sequence; })
                .ThenBy(delegate(SessionItem item) { return item.StartTimeUtc; })
                .ThenBy(delegate(SessionItem item) { return item.DisplayName ?? string.Empty; }, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // Preserve the saved global sequence exactly. Each browser window is
            // launched one page at a time; the first page establishes/claims the
            // exact window, so interleaved applications do not change the
            // recorded order and a user-closed window can stop only its own
            // remaining pages.
            // Explorer folders / browser pages inherit the window-level
            // minimize choice from their parent row before the browser starter
            // checks whether the first page carries a placement.
            ApplyParentWindowPlacements(ordered, snapshot.Items);
            PrepareBrowserWindowStarters(ordered, enabled);
            return ordered;
        }

        /// <summary>
        /// “最小化”属于窗口，不属于页面内容：
        /// 文件资源管理器父行/浏览器父行上的开关是唯一来源。恢复时把父窗口的
        /// WindowPlacement 交给子文件夹/网页，避免用户移动或合并子项后，
        /// 子项还带着旧窗口的最小化状态（或父行开关看起来无效）。
        /// 独立文件夹（没有父窗口）继续用自己的 placement。
        /// </summary>
        private static void ApplyParentWindowPlacements(
            IList<SessionItem> ordered,
            IList<SessionItem> sourceItems)
        {
            if (ordered == null || sourceItems == null)
            {
                return;
            }
            Dictionary<string, SessionItem> parents =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sourceItems.Count; index++)
            {
                SessionItem candidate = sourceItems[index];
                if (candidate != null
                    && candidate.Kind == SessionItemKind.Application
                    && !string.IsNullOrWhiteSpace(candidate.Id))
                {
                    parents[candidate.Id.Trim()] = candidate;
                }
            }

            for (int index = 0; index < ordered.Count; index++)
            {
                SessionItem item = ordered[index];
                if (item == null
                    || (item.Kind != SessionItemKind.WebPage
                        && item.Kind != SessionItemKind.ExplorerFolder))
                {
                    continue;
                }

                SessionItem parent = null;
                if (!string.IsNullOrWhiteSpace(item.ParentId))
                {
                    parents.TryGetValue(item.ParentId.Trim(), out parent);
                }
                if (parent == null && item.Kind == SessionItemKind.WebPage)
                {
                    parent = FindBrowserWindowParent(sourceItems, item);
                }
                if (parent != null && parent.WindowPlacement != null)
                {
                    item.WindowPlacement = parent.WindowPlacement;
                }
            }
        }

        private static List<SessionItem> GetBrowserWindowPages(
            IList<SessionItem> items,
            SessionItem firstPage)
        {
            string key = GetBrowserWindowKey(firstPage);
            if (string.IsNullOrWhiteSpace(key) || items == null)
            {
                return new List<SessionItem> { firstPage };
            }
            return items
                .Where(delegate(SessionItem item)
                {
                    return item != null
                        && item.Kind == SessionItemKind.WebPage
                        && string.Equals(GetBrowserWindowKey(item), key, StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(delegate(SessionItem item)
                {
                    return item.BrowserTabIndex < 0 ? int.MaxValue : item.BrowserTabIndex;
                })
                .ThenBy(delegate(SessionItem item)
                {
                    return item.Sequence <= 0 ? int.MaxValue : item.Sequence;
                })
                .ToList();
        }

        private static bool IsNestedSessionItem(SessionItem item)
        {
            return item != null
                && (item.Kind == SessionItemKind.WebPage
                    || item.Kind == SessionItemKind.ExplorerFolder);
        }

        private static void PrepareBrowserWindowStarters(
            IList<SessionItem> ordered,
            IList<SessionItem> sourceItems)
        {
            HashSet<string> startedWindows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < ordered.Count; index++)
            {
                SessionItem item = ordered[index];
                if (item == null)
                {
                    continue;
                }
                item.StartsBrowserWindow = false;
                if (item.Kind != SessionItemKind.WebPage)
                {
                    continue;
                }

                string group = GetBrowserGroup(item);
                string key = GetBrowserWindowKey(item);
                if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                if (!startedWindows.Add(key))
                {
                    continue;
                }

                item.StartsBrowserWindow = true;
                if (item.WindowPlacement == null || !item.WindowPlacement.HasBounds)
                {
                    SessionItem parent = FindBrowserWindowParent(sourceItems, item);
                    if (parent != null)
                    {
                        item.WindowPlacement = parent.WindowPlacement;
                    }
                }
            }
        }

        private static SessionItem FindBrowserWindowParent(IList<SessionItem> items, SessionItem page)
        {
            if (items == null || page == null)
            {
                return null;
            }
            string group = GetBrowserGroup(page);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate == null || candidate.Kind != SessionItemKind.Application)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(page.ParentId)
                    && !string.IsNullOrWhiteSpace(candidate.Id)
                    && string.Equals(page.ParentId, candidate.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
                if (page.BrowserWindowIndex >= 0
                    && candidate.BrowserWindowIndex == page.BrowserWindowIndex
                    && string.Equals(group, GetBrowserGroup(candidate), StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string GetBrowserWindowKey(string group, int windowIndex)
        {
            return group.Trim() + "|window|" + windowIndex;
        }

        private static string GetBrowserWindowKey(SessionItem item)
        {
            if (item == null || item.Kind != SessionItemKind.WebPage)
            {
                return string.Empty;
            }
            string group = GetBrowserGroup(item);
            if (string.IsNullOrWhiteSpace(group))
            {
                return string.Empty;
            }
            if (item.BrowserWindowIndex >= 0)
            {
                return GetBrowserWindowKey(group, item.BrowserWindowIndex);
            }
            if (!string.IsNullOrWhiteSpace(item.ParentId))
            {
                return group.Trim() + "|parent|" + item.ParentId.Trim();
            }
            return string.Empty;
        }

        private static string GetBrowserGroup(SessionItem item)
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
            if (string.IsNullOrWhiteSpace(value))
            {
                value = item.ExecutablePath;
            }
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            return Task.Delay(delay, cancellationToken);
        }

        private static TimeSpan NormalizeInterval(TimeSpan value)
        {
            return value < TimeSpan.Zero ? TimeSpan.Zero : value;
        }

        private static void Report(IProgress<RestoreProgress> progress, RestoreProgress value)
        {
            if (progress != null)
            {
                progress.Report(value);
            }
        }

        private static string GetItemName(SessionItem item)
        {
            string fallback = AppText.T(StartupRestoreTextKeys.FallbackThisItem);
            if (item == null)
            {
                return fallback;
            }

            if (!string.IsNullOrWhiteSpace(item.DisplayName))
            {
                return item.DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(item.Url))
            {
                return item.Url;
            }

            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return item.TargetPath;
            }

            return string.IsNullOrWhiteSpace(item.ExecutablePath) ? fallback : item.ExecutablePath;
        }
    }
}
