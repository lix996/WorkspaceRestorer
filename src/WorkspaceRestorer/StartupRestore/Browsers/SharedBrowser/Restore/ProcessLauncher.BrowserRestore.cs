using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public sealed partial class ProcessLauncher
    {
        private readonly Dictionary<string, List<string>> pendingBrowserBatches =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // 浏览器窗口标题 = “当前标签标题 + 和另外 N 个页面 + 浏览器名”。
        // 保存侧第一页的 WindowTitle 通常只是标签标题（还可能带
        // “ - 内存使用率 - 100 MB”这种采集后缀），不能拿它去和完整窗口标题
        // 做精确比较；否则会退化成“同 exe 任意窗口”，把 Edge 的“还原页面”
        // 辅助窗口当成目标窗口最小化。这里把一批页面的标题都带上，供恢复时
        // 按“任一标签标题”认领正确窗口。
        private readonly Dictionary<string, List<string>> pendingBrowserWindowTitles =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // 这个浏览器窗口保存了几页、最后一页标题是什么。
        // 最后一页之后只对本轮认领到的精确 HWND 做标题/页数校验；
        // 句柄没了就放弃，不按标题找替代窗口。页数在这里顺手记录，
        // 不改 IBrowserWindowBatchLauncher 的接口。
        private readonly Dictionary<string, int> pendingBrowserPageCounts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> pendingBrowserLastPageTitles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 这个浏览器窗口保存了哪些网址。最后一页之后缩窗前用它和 CDP /json
        // 的实时标签列表比对；只用于就绪判断，不用于按网址找窗口。
        private readonly Dictionary<string, List<string>> pendingBrowserUrls =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> startedBrowserBatches =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 一次恢复里每个浏览器 exe 只预热一次：Edge 没有进程时先用
        // --no-startup-window 起后台进程，让它的崩溃会话恢复没有机会抢窗口。
        private readonly HashSet<string> warmedBrowserProcesses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 本次恢复里“保存为最小化的单页窗口，期限内没读到真实标题，所以保持原样”的窗口数。
        // 收尾 worker 在后台累加，恢复主循环等收尾结束后再读走写进 RestoreResult。
        private int browserWindowsNotReadyCount;

        /// <summary>
        /// 保存为“最小化”的浏览器窗口：第一页先按最小化前的样子打开，
        /// 等这个窗口的最后一页启动完，再由 <see cref="CompleteBrowserWindowRestore"/>
        /// 缩一次。绝不能在第一页就缩——否则后面每开一个标签都会把窗口弹出来，
        /// 看起来就是反复闪烁。
        /// </summary>
        private sealed class BrowserWindowRestoreContext
        {
            internal string Key;
            internal string ExecutablePath;
            internal SavedWindowPlacement FinalPlacement;
            internal SavedWindowPlacement DisplayPlacement;
            internal IList<string> Titles;
            internal IList<string> ExpectedUrls;
            internal ISet<IntPtr> ExistingWindows;
            internal int ExpectedPageCount;
            internal string LastPageTitle;
            internal int Generation;
            internal string TaskbarAppId;
            // B1：这个恢复批次开始认领到的精确浏览器窗口句柄。
            // 最后一页之后只允许控制这个句柄；句柄没了就直接放弃，
            // 不再按标题/页数去认一个新窗口（否则用户关掉再重开的窗口会被误缩）。
            internal IntPtr WindowHandle;
            // true 才在最后一页之后最小化；Normal/Maximized 只做逐页目标
            // 跟踪和 AUMID，不缩窗。
            internal bool MinimizeAtEnd;
            // B3：只认这个恢复窗口的精确 HWND + PID。用户关窗后立刻停止
            // 该组剩余网页，并让最后缩窗任务直接放弃。
            internal BrowserWindowCloseGuard CloseGuard;
            // 用户手动最小化 = 用户接管：剩余网页不再启动，最后不缩、不还原。
            internal BrowserWindowUserTakeoverGuard UserTakeoverGuard;
        }

        private readonly Dictionary<string, BrowserWindowRestoreContext> browserWindowRestores =
            new Dictionary<string, BrowserWindowRestoreContext>(StringComparer.OrdinalIgnoreCase);

        // B1：最后一页之后的缩窗 worker 计数。恢复主循环结束后等这些 worker
        // 全部退出，再关闭本轮的窗口控制会话。
        private readonly object browserRestoreWorkerSync = new object();
        private int browserRestoreWorkersActive;
        private TaskCompletionSource<bool> browserRestoreWorkersIdle;
        private bool browserRestoreCompletionWaitStarted;

        private const int BrowserMinimizeVerifyDelayMilliseconds = 700;
        private const int BrowserPageTargetWaitMilliseconds = 5000;
        // B3：第一页启动后先快速认领窗口，后面每一页才有“精确窗口”可检查。
        // 只做 1 秒快速尝试；冷启动没认领到也不阻塞后续页，收尾会再等。
        private const int BrowserInitialTargetWaitMilliseconds = 1000;
        // 最后一页之后，如果第一页/后续页都没认领到窗口，再给一次收尾认领。
        private const int BrowserFinalTargetWaitMilliseconds = 3000;
        // 已认领的句柄失效时，先短暂找一次同组替代窗口：Edge/Chromium 可能
        // 自己用新 HWND 替换引导窗口。找不到才按“用户关窗”处理。
        private const int BrowserReplacementWaitMilliseconds = 800;
        // 疑似窗口消失时，两次检查之间留一点时间，避免把浏览器重建窗口
        // 的瞬间误判成“用户关闭”。
        private const int BrowserCloseConfirmDelayMilliseconds = 200;
        // 方案 A（2026-11-06 用户确认）：每个窗口收尾最多 =
        // settle（BrowserWindowNotReadyNotice.SettleMilliseconds，3 秒）
        // + 一次瞬时检查 + 缩窗复验 0.7 秒（+ Edge 换 HWND 时 1.2 秒接替）。
        // 没读到标题时 worker 立刻退出、补缩交给后台线程，所以这里不用再加大。
        // 这里只是硬上限；超时后本轮不再允许晚到的窗口控制。
        private const int BrowserRestoreSettleTimeoutMilliseconds = 10000;

        /// <summary>
        /// 一次恢复会话期间为 true。恢复时每个浏览器窗口只有第一页带
        /// <see cref="SessionItem.StartsBrowserWindow"/>，后面的页必须作为标签
        /// 开进同一个窗口；不能再因为“页面继承了父窗口位置”就逐页 --new-window。
        /// 手工点击单个网页不受这个开关影响，仍按原来的单页行为打开。
        /// </summary>
        private volatile bool browserRestoreActive;

        /// <summary>
        /// 每开始一次恢复就 +1。最后一页之后的缩窗 worker 会带着自己那一批的
        /// 编号；用户取消后马上又点一次恢复时，上一轮的 worker 发现编号变了
        /// 就直接退出，避免隔了几秒又去缩新一轮的窗口。
        /// </summary>
        private volatile int browserRestoreGeneration;

        /// <summary>
        /// “3 秒后后台补缩”的批次编号：新一次恢复开始、或用户取消/恢复失败时 +1。
        /// 后台补缩线程带着排队时的编号，发现变了就放弃，绝不在新一轮里补缩旧窗口。
        /// 它和 browserRestoreGeneration 不同：正常收尾不动它，否则刚排队的补缩
        /// 会被自己的 EndBrowserRestore 取消掉。
        /// </summary>
        private int browserLateMinimizeEpoch;

        void IBrowserWindowBatchLauncher.BeginBrowserRestore()
        {
            browserRestoreActive = true;
            browserRestoreGeneration++;
            // 新一次恢复：上一轮还在后台等着的补缩必须作废，不能缩到新一轮的窗口。
            Interlocked.Increment(ref browserLateMinimizeEpoch);
            lock (browserRestoreWorkerSync)
            {
                browserRestoreWorkersActive = 0;
                browserRestoreWorkersIdle = new TaskCompletionSource<bool>();
                browserRestoreCompletionWaitStarted = false;
            }
            // 每一轮恢复重新数“页面未就绪”的窗口，不能把上一轮的数字带进这一轮提示。
            Interlocked.Exchange(ref browserWindowsNotReadyCount, 0);
            pendingBrowserBatches.Clear();
            pendingBrowserWindowTitles.Clear();
            pendingBrowserPageCounts.Clear();
            pendingBrowserLastPageTitles.Clear();
            pendingBrowserUrls.Clear();
            startedBrowserBatches.Clear();
            warmedBrowserProcesses.Clear();
            browserWindowRestores.Clear();
            // 新一轮恢复必须从干净的窗口记忆开始：上一轮记住的 HWND
            // 即使还活着，也不能拿来当这一轮的目标。
            WindowPlacementService.ClearBrowserWindowHandles();
            // 【2026-10-24】这里是“一次恢复开始”的唯一入口，文档的“本次恢复中由我们打开”
            // 记录也必须跟着批次清空。否则上一轮由我们打开的文档会一直被当成“已经开着”，
            // 第二轮恢复就会再开一个重复窗口。
            documentsLaunchedByUs.Clear();
        }

        void IBrowserWindowBatchLauncher.PrepareBrowserWindowBatch(
            SessionItem firstPage,
            IList<SessionItem> pages)
        {
            if (firstPage == null || pages == null)
            {
                return;
            }

            string key = GetBrowserWindowKey(firstPage);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            List<string> urls = new List<string>();
            for (int index = 0; index < pages.Count; index++)
            {
                SessionItem page = pages[index];
                string url = page == null ? null : page.Url;
                if (string.IsNullOrWhiteSpace(url) && page != null)
                {
                    url = page.TargetPath;
                }
                if (IsSafeUrl(url))
                {
                    urls.Add(url.Trim());
                }
            }

            if (urls.Count == 0)
            {
                return;
            }

            // A later restore may reuse the same browser/window indexes.  The
            // preparation call is the boundary that invalidates old routing.
            pendingBrowserBatches[key] = urls;
            pendingBrowserWindowTitles[key] = CollectBrowserWindowTitles(pages);
            RememberBrowserWindowPreparation(key, pages);
            startedBrowserBatches.Remove(key);
        }

        void IBrowserWindowBatchLauncher.PrepareBrowserWindowTitles(
            SessionItem firstPage,
            IList<SessionItem> pages)
        {
            if (firstPage == null)
            {
                return;
            }
            string key = GetBrowserWindowKey(firstPage);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            pendingBrowserWindowTitles[key] = CollectBrowserWindowTitles(pages);
            RememberBrowserWindowPreparation(key, pages);
        }

        /// <summary>
        /// 记下这个窗口保存了几页、最后一页是什么。第一页启动时带进
        /// BrowserWindowRestoreContext；最后一页之后靠这两项确认“缩的是不是
        /// 真正装着全部页面的那个窗口”。
        /// </summary>
        private void RememberBrowserWindowPreparation(string key, IList<SessionItem> pages)
        {
            if (string.IsNullOrWhiteSpace(key) || pages == null || pages.Count == 0)
            {
                return;
            }
            pendingBrowserPageCounts[key] = pages.Count;
            pendingBrowserUrls[key] = CollectBrowserWindowUrls(pages);
            string lastTitle = GetBrowserPageTitle(pages[pages.Count - 1]);
            if (!string.IsNullOrWhiteSpace(lastTitle))
            {
                pendingBrowserLastPageTitles[key] = lastTitle;
            }
        }

        private static string GetBrowserPageTitle(SessionItem page)
        {
            if (page == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(page.WindowTitle))
            {
                return page.WindowTitle.Trim();
            }
            if (!string.IsNullOrWhiteSpace(page.DisplayName))
            {
                return page.DisplayName.Trim();
            }
            return null;
        }

        void IBrowserWindowBatchLauncher.CompleteBrowserWindowRestore(
            SessionItem firstPage,
            SessionItem lastPage)
        {
            if (firstPage == null)
            {
                return;
            }
            string key = GetBrowserWindowKey(firstPage);
            BrowserWindowRestoreContext context;
            if (string.IsNullOrWhiteSpace(key)
                || !browserWindowRestores.TryGetValue(key, out context))
            {
                // 普通/最大化窗口不需要“最后再缩一次”。
                return;
            }
            browserWindowRestores.Remove(key);
            if (!context.MinimizeAtEnd)
            {
                // 普通/最大化窗口只做逐页目标跟踪，不缩。
                return;
            }
            if (context.Titles == null || context.Titles.Count == 0)
            {
                List<string> fallbackTitles = new List<string>();
                AddBrowserWindowTitle(fallbackTitles, GetBrowserPageTitle(lastPage));
                if (fallbackTitles.Count > 0)
                {
                    context.Titles = fallbackTitles;
                }
            }
            if (string.IsNullOrWhiteSpace(context.LastPageTitle))
            {
                context.LastPageTitle = GetBrowserPageTitle(lastPage);
            }
            if (OsMutationGuard.IsDisabled || ExternalEffectsDisabled())
            {
                return;
            }

            // 在入队时就把“这个恢复批次认领到的窗口”固定下来。
            // 如果第一页的定位 worker 还没来得及登记，worker 会在 settle 后
            // 再取一次；但只取 BrowserWindowTracker 里属于本轮的句柄，
            // 绝不按标题重新解析一个替代窗口。
            if (context.WindowHandle == IntPtr.Zero)
            {
                IntPtr tracked = BrowserWindowTracker.Get(context.Key);
                if (tracked != IntPtr.Zero)
                {
                    context.WindowHandle = tracked;
                    if (RememberBrowserWindowTarget(context, tracked))
                    {
                        AppLog.Write("[窗口恢复] 最后一页后发现浏览器窗口已关闭，"
                            + "不再排缩窗任务：" + context.Key + " HWND=" + tracked);
                        return;
                    }
                }
            }

            // B3：用户已经关掉这个窗口（或最后一页启动前后刚好关掉），
            // 不再激活、不再缩窗。
            if (ConfirmBrowserWindowClosed(context))
            {
                AppLog.Write("[窗口恢复] 浏览器窗口已由用户关闭，"
                    + "不再排缩窗任务：" + context.Key);
                return;
            }
            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                AppLog.Write("[窗口恢复] 用户已最小化浏览器窗口，"
                    + "不再排缩窗任务：" + context.Key);
                return;
            }

            lock (browserRestoreWorkerSync)
            {
                if (!browserRestoreActive || browserRestoreCompletionWaitStarted)
                {
                    AppLog.Write("[窗口恢复] 恢复收尾已开始，不再接受新的缩窗任务："
                        + context.Key);
                    return;
                }
                browserRestoreWorkersActive++;
            }

            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        CompleteBrowserWindowRestoreWorker(context);
                    }
                    finally
                    {
                        FinishBrowserRestoreWorker(context.Generation);
                    }
                });
            }
            catch
            {
                FinishBrowserRestoreWorker(context.Generation);
                throw;
            }
        }

        private void FinishBrowserRestoreWorker(int generation)
        {
            TaskCompletionSource<bool> idle = null;
            lock (browserRestoreWorkerSync)
            {
                // 收尾超时后马上又点了一次恢复：旧 worker 不能去减新一轮的
                // 计数器，否则新一轮的收尾门禁会提前放行。
                if (generation != browserRestoreGeneration)
                {
                    return;
                }
                if (browserRestoreWorkersActive > 0)
                {
                    browserRestoreWorkersActive--;
                }
                if (browserRestoreWorkersActive == 0
                    && browserRestoreCompletionWaitStarted)
                {
                    idle = browserRestoreWorkersIdle;
                }
            }
            if (idle != null)
            {
                idle.TrySetResult(true);
            }
        }

        /// <summary>
        /// 最后一页启动完并 settle 后，只对**本轮认领到的精确 HWND**
        /// 做有界校验并缩一次。句柄没了就放弃，不按标题找替代窗口。
        /// 整个恢复期间只有这一处会缩最小化浏览器窗口。
        /// </summary>
        private void CompleteBrowserWindowRestoreWorker(
            BrowserWindowRestoreContext context)
        {
            if (context == null || context.FinalPlacement == null)
            {
                return;
            }

            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                AppLog.Write("[窗口恢复] 用户在最后一页后已最小化浏览器窗口，"
                    + "取消缩窗：" + context.Key);
                return;
            }

            if (context.Generation != browserRestoreGeneration)
            {
                AppLog.Write("[窗口恢复] 上一轮浏览器最小化任务已过期，跳过：" + context.Key);
                return;
            }

            // B1：只控制本轮恢复真正认领到的那个浏览器窗口。
            // 如果它已经不在（用户手动关了，或 Edge 自己替换了窗口），
            // 直接放弃，绝不按标题/页数再找一个替代窗口来缩。
            IntPtr window = context.WindowHandle;
            if (window == IntPtr.Zero)
            {
                window = BrowserWindowTracker.Get(context.Key);
                if (window != IntPtr.Zero)
                {
                    if (RememberBrowserWindowTarget(context, window))
                    {
                        AppLog.Write("[窗口恢复] 浏览器窗口已关闭，本次不最小化："
                            + context.Key + " HWND=" + window);
                        return;
                    }
                }
            }

            // 第一页快速认领没赶上（空白标题/冷启动慢）时，收尾再给一次
            // 有界认领；仍然只认同进程、新出现、唯一的浏览器主窗口。
            if (window == IntPtr.Zero)
            {
                IntPtr claimed = BrowserWindowClaimer.TryClaim(
                    context.Key,
                    context.ExecutablePath,
                    context.Titles,
                    context.ExistingWindows,
                    BrowserFinalTargetWaitMilliseconds,
                    delegate
                    {
                        return context.CloseGuard != null
                            && context.CloseGuard.IsClosedByUser;
                    },
                    delegate
                    {
                        return context.UserTakeoverGuard != null
                            && context.UserTakeoverGuard.ConfirmUserMinimized();
                    });
                if (claimed != IntPtr.Zero)
                {
                    if (RememberBrowserWindowTarget(context, claimed))
                    {
                        AppLog.Write("[窗口恢复] 收尾认领时浏览器窗口已关闭，"
                            + "本次不最小化：" + context.Key + " HWND=" + claimed);
                        return;
                    }
                    window = context.WindowHandle;
                }
            }

            if (window == IntPtr.Zero)
            {
                AppLog.Write("[窗口恢复] 本轮没有认领到精确浏览器窗口，本次不最小化："
                    + context.Key);
                return;
            }
            if (!NativeMethods.IsWindow(window))
            {
                AppLog.Write("[窗口恢复] 浏览器窗口已被用户关闭，本次不最小化、不找替代窗口："
                    + context.Key + " HWND=" + window);
                return;
            }

            if (context.Generation != browserRestoreGeneration)
            {
                AppLog.Write("[窗口恢复] 浏览器最小化任务在就绪等待前已过期，跳过：" + context.Key);
                return;
            }
            if (ConfirmBrowserWindowClosed(context))
            {
                AppLog.Write("[窗口恢复] 缩窗前发现浏览器窗口已由用户关闭，"
                    + "取消缩窗：" + context.Key);
                return;
            }

            // 方案 A（2026-11-06 用户确认）：最后一页之后先固定 settle 3 秒，
            // 然后只读一次标题：
            //   · 读到真实标题 → 照旧缩一次；
            //   · 读不到（页面还没加载完）→ 窗口先保持原样，完成提示照常在 3 秒弹出，
            //     同时排一个后台补缩（再等 3 秒读一次，读到才缩，读不到彻底放手）。
            // 单页/多页共用这一套，耗时与站点快慢无关，不会再出现 4/12/18 秒的等待。
            int settleStarted = Environment.TickCount;
            while (Environment.TickCount - settleStarted
                < BrowserWindowNotReadyNotice.SettleMilliseconds)
            {
                if (context.Generation != browserRestoreGeneration
                    || !NativeMethods.IsWindow(window))
                {
                    AppLog.Write("[窗口恢复] 等待窗口稳定时浏览器窗口失效，取消缩窗："
                        + context.Key + " HWND=" + window);
                    return;
                }
                if (ConfirmBrowserWindowClosed(context))
                {
                    AppLog.Write("[窗口恢复] 等待窗口稳定时用户关闭了浏览器窗口，取消缩窗："
                        + context.Key);
                    return;
                }
                if (context.UserTakeoverGuard != null
                    && context.UserTakeoverGuard.ConfirmUserMinimized())
                {
                    AppLog.Write("[窗口恢复] 等待窗口稳定时用户最小化了浏览器窗口，取消缩窗："
                        + context.Key);
                    return;
                }
                Thread.Sleep(100);
            }

            if (context.Generation != browserRestoreGeneration)
            {
                AppLog.Write("[窗口恢复] 浏览器最小化任务在检查前已过期，跳过：" + context.Key);
                return;
            }

            // 只读一次：有真实标题算“加载好了”，没有就按“页面未就绪”处理，绝不再等。
            BrowserReadinessResult check = BrowserReadinessProbe.CheckOnce(
                window,
                context.ExpectedUrls,
                context.Titles);
            string windowTitle = null;
            int windowPageCount = 0;
            bool matches = false;

            if (check.State == BrowserReadinessState.WindowLost)
            {
                AppLog.Write("[窗口恢复] 检查时浏览器窗口已消失，取消缩窗：" + context.Key);
                return;
            }

            if (!check.IsReady)
            {
                // 保存时就是最小化、窗口还在、用户没关没缩：这次“没缩”要计入完成提示，
                // 否则用户会以为保存的最小化状态失效了。保存为普通/最大化的窗口
                // 本来就不缩，不提示；用户关窗/接管走各自的提示。
                bool userMinimizedNow = context.UserTakeoverGuard != null
                    && context.UserTakeoverGuard.ConfirmUserMinimized();
                bool reportNotReady = ShouldReportBrowserWindowNotReady(
                    context.MinimizeAtEnd,
                    NativeMethods.IsWindow(window),
                    NativeMethods.IsIconic(window) || userMinimizedNow,
                    false);
                if (reportNotReady)
                {
                    Interlocked.Increment(ref browserWindowsNotReadyCount);
                }
                AppLog.Write("[窗口恢复] 窗口出现后 "
                    + BrowserWindowNotReadyNotice.SettleSecondsText
                    + " 秒内没有读到真实标题，先保持原样、不缩、不还原、不抢前台："
                    + context.Key
                    + " HWND=" + window
                    + " 期望页数=" + context.ExpectedPageCount
                    + " 检查用时=" + check.ElapsedMilliseconds + "ms"
                    + " 计入页面未就绪=" + reportNotReady
                    + " 保存最小化=" + context.MinimizeAtEnd
                    + " 说明=" + (check.Detail ?? string.Empty));

                // 方案 A（2026-11-06 用户确认）：完成提示不等这里——worker 立刻退出，
                // 横幅仍在 3 秒弹出；补缩交给后台线程再等 3 秒读一次标题。
                if (reportNotReady)
                {
                    ScheduleLateBrowserMinimize(context, window);
                }
                return;
            }

            matches = WindowPlacementService.BrowserWindowMatchesExpectation(
                window,
                context.Titles,
                context.LastPageTitle,
                context.ExpectedPageCount,
                out windowTitle,
                out windowPageCount);
            AppLog.Write("[窗口恢复] 窗口出现后 "
                + BrowserWindowNotReadyNotice.SettleSecondsText
                + " 秒内读到真实标题，继续缩窗："
                + context.Key + " HWND=" + window
                + " 检查用时=" + check.ElapsedMilliseconds + "ms"
                + " 标题=" + (windowTitle ?? string.Empty)
                + " 说明=" + (check.Detail ?? string.Empty));

            if (BrowserMinimizeDecision.Decide(
                    true,
                    false,
                    false,
                    check.IsReady,
                    matches,
                    false) == BrowserMinimizeAction.Skip)
            {
                AppLog.Write("[窗口恢复] 没有足够的标题证据，跳过缩窗："
                    + context.Key + " HWND=" + window
                    + " 标题匹配=" + matches
                    + " 期望页数=" + context.ExpectedPageCount
                    + " 实际页数=" + windowPageCount
                    + " 标题=" + (windowTitle ?? string.Empty));
                return;
            }

            if (!matches)
            {
                AppLog.Write("[窗口恢复] 标题/页数未匹配，但已读到真实标题，"
                    + "按精确句柄继续：" + context.Key + " HWND=" + window
                    + " 期望页数=" + context.ExpectedPageCount
                    + " 实际页数=" + windowPageCount);
            }

            // 精确窗口可能被 Edge 替换过；缩之前再确认任务栏身份，保证
            // 最小化后仍归到用户固定那个 Edge 图标下。
            if (!string.IsNullOrWhiteSpace(context.TaskbarAppId))
            {
                TaskbarIdentityService.ApplyToWindow(window, context.TaskbarAppId);
            }

            if (NativeMethods.IsIconic(window))
            {
                // 能走到这里说明程序还没调用过最小化；这是用户接管，不是“已经缩好”。
                AppLog.Write("[窗口恢复] 缩窗前发现窗口已被用户最小化，"
                    + "不再激活/缩窗：" + context.Key + " HWND=" + window);
                return;
            }

            // B3：激活/缩窗之前再确认一次，用户在这一刻关窗也不能再碰它。
            if (ConfirmBrowserWindowClosed(context))
            {
                AppLog.Write("[窗口恢复] 激活前发现浏览器窗口已由用户关闭，取消缩窗："
                    + context.Key);
                return;
            }
            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                AppLog.Write("[窗口恢复] 激活前发现用户已最小化浏览器窗口，取消缩窗："
                    + context.Key);
                return;
            }

            // 任务栏分组记住的是“最后活动的窗口”。如果目标不是前台，
            // 先把它切到前台再缩；否则用户点任务栏时可能去还原一个已经
            // 消失的临时窗口，表现就是“点了没反应/又弹初始化窗口”。
            if (NativeMethods.GetForegroundWindow() != window)
            {
                bool activated = WindowPlacementService.TryActivateBrowserWindow(window);
                AppLog.Write("[窗口恢复] 最小化前激活最终浏览器窗口："
                    + context.Key + " HWND=" + window + " 成功=" + activated);
                if (activated)
                {
                    Thread.Sleep(100);
                }
            }

            if (context.Generation != browserRestoreGeneration)
            {
                AppLog.Write("[窗口恢复] 浏览器最小化任务在缩窗前已过期，跳过：" + context.Key);
                return;
            }
            if (ConfirmBrowserWindowClosed(context))
            {
                AppLog.Write("[窗口恢复] 缩窗前发现浏览器窗口已由用户关闭，取消缩窗："
                    + context.Key);
                return;
            }
            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                AppLog.Write("[窗口恢复] 缩窗前发现用户已最小化浏览器窗口，取消缩窗："
                    + context.Key);
                return;
            }

            if (context.UserTakeoverGuard != null)
            {
                context.UserTakeoverGuard.MarkAppMinimizeIssued();
            }

            WindowPlacementService.BrowserMinimizeOutcome outcome =
                WindowPlacementService.ApplyMinimizedToBrowserWindow(
                    window,
                    context.FinalPlacement,
                    context.Titles,
                    context.LastPageTitle,
                    context.ExpectedPageCount,
                    delegate
                    {
                        return BrowserWindowControlStillCurrent(context);
                    });

            // C：标题/页数校验失败时，只要这个精确 HWND 仍然有效、仍属于
            // 同一个浏览器进程，并且已经读到真实标题，就按句柄兜底最小化一次。
            // 绝不缩新窗口，也绝不缩没有标题证据的空白初始化窗口。
            if (outcome == WindowPlacementService.BrowserMinimizeOutcome.TitleMismatch
                && context.Generation == browserRestoreGeneration
                && NativeMethods.IsWindow(window)
                && check.IsReady)
            {
                bool forced = WindowPlacementService.TryForceMinimizeBrowserWindow(
                    window,
                    context.ExecutablePath,
                    delegate
                    {
                        return BrowserWindowControlStillCurrent(context);
                    });
                AppLog.Write("[窗口恢复] 最小化校验失败，按精确句柄兜底："
                    + context.Key + " HWND=" + window + " 成功=" + forced);
                if (forced)
                {
                    outcome = WindowPlacementService.BrowserMinimizeOutcome.Applied;
                }
            }

            VerifyBrowserWindowMinimized(
                context,
                window,
                outcome,
                windowTitle,
                windowPageCount);
        }

        /// <summary>
        /// 方案 A（2026-11-06 用户确认）：3 秒内没读到标题就排一个后台补缩。
        /// worker 立刻退出，所以恢复完成提示（横幅）照常在 3 秒弹出，不会被补缩拖住。
        /// 后台线程只认本轮认领到的精确 HWND + PID，并且再等 3 秒读一次标题：
        ///   · 读到 → 静默补缩一次（不提示、不改横幅、不抢前台）；
        ///   · 读不到 → 彻底放手，不再提示、不再重试。
        /// </summary>
        private void ScheduleLateBrowserMinimize(
            BrowserWindowRestoreContext context,
            IntPtr window)
        {
            if (context == null
                || context.FinalPlacement == null
                || window == IntPtr.Zero
                || !NativeMethods.IsWindow(window))
            {
                return;
            }

            bool userClosed = context.CloseGuard != null && context.CloseGuard.IsClosedByUser;
            bool userMinimized = context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.HasUserTookOver;
            if (!ShouldScheduleLateBrowserMinimize(
                    context.MinimizeAtEnd,
                    NativeMethods.IsWindow(window),
                    false,
                    userClosed,
                    userMinimized))
            {
                return;
            }

            int epoch = Volatile.Read(ref browserLateMinimizeEpoch);
            AppLog.Write("[窗口恢复] 排队后台补缩：等 "
                + BrowserWindowNotReadyNotice.LateRetrySecondsText
                + " 秒后再读一次标题，读到才补缩：" + context.Key
                + " HWND=" + window);
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        LateBrowserMinimizeWorker(context, window, epoch);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("[窗口恢复] 后台补缩异常，已放弃："
                            + context.Key + " 原因=" + exception.Message);
                    }
                });
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 后台补缩排队失败，已放弃："
                    + context.Key + " 原因=" + exception.Message);
            }
        }

        private void LateBrowserMinimizeWorker(
            BrowserWindowRestoreContext context,
            IntPtr window,
            int epoch)
        {
            int elapsed = 0;
            while (elapsed < BrowserWindowNotReadyNotice.LateRetryMilliseconds)
            {
                Thread.Sleep(100);
                elapsed += 100;
                if (!LateBrowserMinimizeStillAllowed(context, window, epoch))
                {
                    AppLog.Write("[窗口恢复] 后台补缩：等待期间窗口已关闭、已被用户接管"
                        + "或已开始新一轮恢复，放弃：" + context.Key + " HWND=" + window);
                    return;
                }
            }

            BrowserReadinessResult check = BrowserReadinessProbe.CheckOnce(
                window,
                context.ExpectedUrls,
                context.Titles);
            if (check.State == BrowserReadinessState.WindowLost
                || !LateBrowserMinimizeStillAllowed(context, window, epoch))
            {
                AppLog.Write("[窗口恢复] 后台补缩：窗口已消失或已不可控制，放弃："
                    + context.Key + " HWND=" + window);
                return;
            }
            if (!check.IsReady)
            {
                AppLog.Write("[窗口恢复] 后台补缩：又等 "
                    + BrowserWindowNotReadyNotice.LateRetrySecondsText
                    + " 秒仍未读到真实标题，彻底放手（窗口保持正常，不再提示、不再重试）："
                    + context.Key + " HWND=" + window
                    + " 说明=" + (check.Detail ?? string.Empty));
                return;
            }

            AppLog.Write("[窗口恢复] 后台补缩：读到真实标题，补缩一次（不抢前台）："
                + context.Key + " HWND=" + window
                + " 检查用时=" + check.ElapsedMilliseconds + "ms"
                + " 说明=" + (check.Detail ?? string.Empty));

            // 不激活：3 秒后用户可能已经切到别的窗口，再抢前台会打断他。
            // 任务栏分组在收尾缩窗前已经建立，这里只补“最小化”这一个状态。
            if (!string.IsNullOrWhiteSpace(context.TaskbarAppId))
            {
                TaskbarIdentityService.ApplyToWindow(window, context.TaskbarAppId);
            }
            if (context.UserTakeoverGuard != null)
            {
                // 之后窗口变成 IsIconic 是程序自己缩的，不能再算成用户接管。
                context.UserTakeoverGuard.MarkAppMinimizeIssued();
            }

            WindowPlacementService.BrowserMinimizeOutcome outcome =
                WindowPlacementService.ApplyMinimizedToBrowserWindow(
                    window,
                    context.FinalPlacement,
                    context.Titles,
                    context.LastPageTitle,
                    context.ExpectedPageCount,
                    delegate
                    {
                        return LateBrowserMinimizeStillAllowed(context, window, epoch);
                    });

            if (outcome == WindowPlacementService.BrowserMinimizeOutcome.TitleMismatch
                && check.IsReady)
            {
                bool forced = WindowPlacementService.TryForceMinimizeBrowserWindow(
                    window,
                    context.ExecutablePath,
                    delegate
                    {
                        return LateBrowserMinimizeStillAllowed(context, window, epoch);
                    });
                AppLog.Write("[窗口恢复] 后台补缩校验失败，按精确句柄兜底："
                    + context.Key + " HWND=" + window + " 成功=" + forced);
                if (forced)
                {
                    outcome = WindowPlacementService.BrowserMinimizeOutcome.Applied;
                }
            }

            Thread.Sleep(300);
            bool stillIconic = NativeMethods.IsWindow(window)
                && NativeMethods.IsIconic(window);
            AppLog.Write("[窗口恢复] 后台补缩结束："
                + context.Key + " HWND=" + window
                + " 结果=" + outcome
                + " 仍最小化=" + stillIconic);
        }

        /// <summary>
        /// 后台补缩还允不允许动窗口：批次没变（没开始新一轮恢复）、窗口还是那个
        /// 精确 HWND、用户没关、没手动最小化。任何一条不满足就永久放弃。
        /// </summary>
        private bool LateBrowserMinimizeStillAllowed(
            BrowserWindowRestoreContext context,
            IntPtr window,
            int epoch)
        {
            if (context == null
                || window == IntPtr.Zero
                || epoch != Volatile.Read(ref browserLateMinimizeEpoch))
            {
                return false;
            }
            if (!NativeMethods.IsWindow(window))
            {
                return false;
            }
            if (context.CloseGuard != null && context.CloseGuard.ObserveOnce())
            {
                return false;
            }
            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// 该不该排后台补缩：只有“保存为最小化 + 窗口还在 + 3 秒没读到 + 用户没关没缩”
        /// 四条都成立才排。保存为普通/最大化的窗口本来就不缩；用户关窗/接管走各自提示。
        /// </summary>
        internal static bool ShouldScheduleLateBrowserMinimize(
            bool savedMinimized,
            bool windowAlive,
            bool pageReady,
            bool userClosed,
            bool userMinimized)
        {
            return savedMinimized && windowAlive && !pageReady && !userClosed && !userMinimized;
        }

        /// <summary>
        /// 记下这个恢复窗口的精确 HWND。返回 true 表示确认它已经消失/被用户关闭。
        /// 一旦守卫认领了某个 HWND，后续出现的其它窗口一律不替换。
        /// </summary>
        private static bool RememberBrowserWindowTarget(
            BrowserWindowRestoreContext context,
            IntPtr handle)
        {
            if (context == null || handle == IntPtr.Zero)
            {
                return false;
            }
            if (context.WindowHandle == IntPtr.Zero)
            {
                context.WindowHandle = handle;
            }
            if (context.CloseGuard == null)
            {
                return false;
            }
            if (context.CloseGuard.Attach(handle))
            {
                return false;
            }

            // 跟踪器记过这个窗口，但句柄已经失效：不是“还没创建”，
            // 而是“曾经有、现在没了”。按用户关窗处理，防止下一页再开新窗。
            if (!NativeMethods.IsWindow(handle))
            {
                context.CloseGuard.MarkTrackedWindowLost(handle);
                return true;
            }
            return context.CloseGuard.IsClosedByUser;
        }

        /// <summary>
        /// B3：这个窗口组是否已被用户关闭。只读精确 HWND + PID；
        /// 没有认领到窗口时返回 false（不能把“还没建窗”当成“用户关窗”）。
        /// </summary>
        private static bool ConfirmBrowserWindowClosed(BrowserWindowRestoreContext context)
        {
            if (context == null || context.CloseGuard == null)
            {
                return false;
            }
            if (context.CloseGuard.IsClosedByUser)
            {
                return true;
            }

            // 已认领的窗口突然失效：先给浏览器 800ms 自证“只是换了个 HWND”。
            // 只有同一浏览器进程的新窗口才允许接替；找不到才判用户关窗。
            if (context.CloseGuard.HasTrackedWindow
                && !context.CloseGuard.IsAliveNow())
            {
                IntPtr replacement = WindowPlacementService.WaitForBrowserWindow(
                    context.Key,
                    context.ExecutablePath,
                    context.Titles,
                    context.ExistingWindows,
                    BrowserReplacementWaitMilliseconds);
                if (replacement != IntPtr.Zero
                    && context.CloseGuard.TryAdoptReplacement(replacement))
                {
                    context.WindowHandle = replacement;
                    AppLog.Write("[窗口恢复] 浏览器窗口被浏览器自己替换，"
                        + "继续跟踪新句柄：" + context.Key + " HWND=" + replacement);
                    return false;
                }
            }

            IntPtr tracked = BrowserWindowTracker.Get(context.Key);
            if (tracked != IntPtr.Zero)
            {
                // 位置校正 worker 发现 Edge 用新 HWND 替换了引导窗口时，
                // 只在旧句柄已经失效、同一个浏览器进程的前提下接替。
                if (context.CloseGuard.HasTrackedWindow
                    && tracked != context.CloseGuard.WindowHandle
                    && context.CloseGuard.TryAdoptReplacement(tracked))
                {
                    context.WindowHandle = tracked;
                    AppLog.Write("[窗口恢复] 浏览器窗口被浏览器自己替换，"
                        + "继续跟踪新句柄：" + context.Key + " HWND=" + tracked);
                    return false;
                }
                if (RememberBrowserWindowTarget(context, tracked))
                {
                    return true;
                }
            }
            return context.CloseGuard.ConfirmClosedByUser(
                BrowserCloseConfirmDelayMilliseconds);
        }

        /// <summary>
        /// 缩窗/激活过程中的“仍然可以控制这个窗口”判断。
        /// 每一轮都会检查一次精确窗口；连续两次失效就立即停下。
        /// </summary>
        private bool BrowserWindowControlStillCurrent(
            BrowserWindowRestoreContext context)
        {
            if (context == null || context.Generation != browserRestoreGeneration)
            {
                return false;
            }
            if (context.CloseGuard == null)
            {
                return true;
            }
            if (context.CloseGuard.ObserveOnce())
            {
                return false;
            }
            return !context.CloseGuard.IsClosedByUser;
        }

        /// <summary>
        /// 缩完后的统一复验：只认这个精确 HWND。窗口如果被关闭/替换，
        /// 不写“已最小化”，也不再去控制任何新窗口。
        /// </summary>
        private void VerifyBrowserWindowMinimized(
            BrowserWindowRestoreContext context,
            IntPtr window,
            WindowPlacementService.BrowserMinimizeOutcome outcome,
            string title,
            int pageCount)
        {
            Thread.Sleep(BrowserMinimizeVerifyDelayMilliseconds);
            string currentTitle;
            int currentPageCount;
            bool stillValid = WindowPlacementService.BrowserWindowStillPresentAfterMinimize(
                window,
                out currentTitle,
                out currentPageCount);
            bool stillIconic = stillValid && NativeMethods.IsIconic(window);

            // Edge/Chromium 可能在缩下去之后用新 HWND 接替引导窗口。
            // 只在这 1.2 秒内接替一次；新窗口若仍是最小化就算成功，
            // 若已经可见就如实报失败，不再追着它反复缩。
            if (!stillValid && context.CloseGuard != null)
            {
                IntPtr replacement = WindowPlacementService.WaitForBrowserWindow(
                    context.Key,
                    context.ExecutablePath,
                    context.Titles,
                    context.ExistingWindows,
                    1200);
                if (replacement != IntPtr.Zero
                    && replacement != window
                    && context.CloseGuard.TryAdoptReplacement(replacement))
                {
                    context.WindowHandle = replacement;
                    BrowserWindowTracker.Set(context.Key, replacement);
                    window = replacement;
                    stillValid = WindowPlacementService.BrowserWindowStillPresentAfterMinimize(
                        window,
                        out currentTitle,
                        out currentPageCount);
                    stillIconic = stillValid && NativeMethods.IsIconic(window);
                    AppLog.Write("[窗口恢复] 缩窗后 Edge 替换了 HWND，已接替新句柄："
                        + context.Key + " HWND=" + replacement
                        + " 仍有效=" + stillValid
                        + " 仍最小化=" + stillIconic);
                }
            }

            if (context.Generation != browserRestoreGeneration)
            {
                AppLog.Write("[窗口恢复] 浏览器最小化任务完成后发现已开始新一轮恢复，不再报成功："
                    + context.Key + " HWND=" + window);
                return;
            }
            if (stillValid && stillIconic)
            {
                AppLog.Write("[窗口恢复] 浏览器窗口已最小化（验证通过）："
                    + context.Key + " HWND=" + window
                    + " 页数=" + currentPageCount + " 标题=" + currentTitle);
            }
            else
            {
                AppLog.Write("[窗口恢复] 浏览器窗口最小化后未确认（可能被替换/关闭）："
                    + context.Key + " HWND=" + window
                    + " 结果=" + outcome
                    + " 仍有效=" + stillValid
                    + " 仍最小化=" + stillIconic
                    + " 页数=" + currentPageCount
                    + " 标题=" + (currentTitle ?? string.Empty)
                    + " 校验前标题=" + (title ?? string.Empty)
                    + " 校验前页数=" + pageCount);
            }
        }

        private static List<string> CollectBrowserWindowTitles(IList<SessionItem> pages)
        {
            List<string> titles = new List<string>();
            if (pages == null)
            {
                return titles;
            }
            for (int index = 0; index < pages.Count; index++)
            {
                SessionItem page = pages[index];
                if (page == null)
                {
                    continue;
                }
                AddBrowserWindowTitle(titles, page.WindowTitle);
                AddBrowserWindowTitle(titles, page.DisplayName);
            }
            return titles;
        }

        private static List<string> CollectBrowserWindowUrls(IList<SessionItem> pages)
        {
            List<string> urls = new List<string>();
            if (pages == null)
            {
                return urls;
            }
            for (int index = 0; index < pages.Count; index++)
            {
                SessionItem page = pages[index];
                if (page == null)
                {
                    continue;
                }
                string url = page.Url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    url = page.TargetPath;
                }
                if (IsSafeUrl(url))
                {
                    urls.Add(url.Trim());
                }
            }
            return urls;
        }

        private static void AddBrowserWindowTitle(List<string> titles, string value)
        {
            if (titles == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            string trimmed = value.Trim();
            if (trimmed.Length == 0)
            {
                return;
            }
            for (int index = 0; index < titles.Count; index++)
            {
                if (string.Equals(titles[index], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            titles.Add(trimmed);
        }

        async Task<bool> IBrowserWindowBatchLauncher.WaitForBrowserRestoreSettledAsync(
            CancellationToken cancellationToken)
        {
            Task idleTask;
            lock (browserRestoreWorkerSync)
            {
                browserRestoreCompletionWaitStarted = true;
                if (browserRestoreWorkersActive <= 0)
                {
                    return true;
                }
                if (browserRestoreWorkersIdle == null)
                {
                    return false;
                }
                idleTask = browserRestoreWorkersIdle.Task;
            }

            Task timeoutTask = Task.Delay(
                BrowserRestoreSettleTimeoutMilliseconds,
                cancellationToken);
            Task completed = await Task.WhenAny(idleTask, timeoutTask).ConfigureAwait(false);
            if (object.ReferenceEquals(completed, idleTask))
            {
                return true;
            }

            // 用户取消时把 OperationCanceledException 交给恢复服务，
            // 让它走“已取消”而不是“收尾超时”。
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        /// <summary>
        /// 本次恢复里“保存为最小化的单页窗口，期限内没读到真实标题，所以保持原样”的窗口数。
        /// 收尾 worker 在后台累加，恢复服务等收尾结束后再读。
        /// </summary>
        int IBrowserWindowBatchLauncher.BrowserWindowNotReadyCount
        {
            get { return Volatile.Read(ref browserWindowsNotReadyCount); }
        }

        void IBrowserWindowBatchLauncher.AbortBrowserLateMinimize()
        {
            // 用户取消 / 恢复失败：立刻让已排队的“3 秒后台补缩”作废，
            // 保证恢复中止之后不会再有任何窗口动作。正常完成不要调用。
            Interlocked.Increment(ref browserLateMinimizeEpoch);
            AppLog.Write("[窗口恢复] 取消已排队的后台补缩：本次恢复已中止。");
        }

        void IBrowserWindowBatchLauncher.EndBrowserRestore()
        {
            // 先让本轮的 worker 过期，再清掉批次状态。即使收尾超时，
            // 任何一个还没退出的 worker 在下一次检查 generation 时都会停，
            // 不会在“恢复完成”之后再激活/缩窗。
            browserRestoreActive = false;
            browserRestoreGeneration++;
            lock (browserRestoreWorkerSync)
            {
                browserRestoreCompletionWaitStarted = false;
                browserRestoreWorkersIdle = null;
            }
            WindowPlacementService.ClearBrowserWindowHandles();
            pendingBrowserBatches.Clear();
            pendingBrowserWindowTitles.Clear();
            pendingBrowserPageCounts.Clear();
            pendingBrowserLastPageTitles.Clear();
            pendingBrowserUrls.Clear();
            startedBrowserBatches.Clear();
            browserWindowRestores.Clear();
            // 批次结束就释放文档跟踪：下一次恢复从干净状态重新判断“是不是早就开着”。
            documentsLaunchedByUs.Clear();
        }

        /// <summary>
        /// B3：恢复主循环在启动每一页之前调用。只认该窗口组自己的精确
        /// HWND + PID；关闭后该组剩余网页全部按“用户关闭浏览器窗口”跳过。
        /// </summary>
        bool IBrowserWindowBatchLauncher.IsBrowserWindowClosedByUser(SessionItem page)
        {
            if (!browserRestoreActive
                || page == null
                || page.Kind != SessionItemKind.WebPage)
            {
                return false;
            }

            string key = GetBrowserWindowKey(page);
            BrowserWindowRestoreContext context;
            if (string.IsNullOrWhiteSpace(key)
                || !browserWindowRestores.TryGetValue(key, out context)
                || context == null
                || context.Generation != browserRestoreGeneration)
            {
                return false;
            }

            bool closed = ConfirmBrowserWindowClosed(context);
            if (closed)
            {
                AppLog.Write("[窗口恢复] 浏览器窗口已关闭，跳过该窗口剩余网页："
                    + key + " 当前页=" + (page.DisplayName ?? page.Url ?? "(未命名)"));
            }
            return closed;
        }

        /// <summary>
        /// 用户在恢复中手动最小化了浏览器窗口：同组剩余网页全部跳过。
        /// 不还原、不激活、最后也不缩窗；和关窗一样不是失败。
        /// </summary>
        bool IBrowserWindowBatchLauncher.IsBrowserWindowUserMinimized(SessionItem page)
        {
            if (!browserRestoreActive
                || page == null
                || page.Kind != SessionItemKind.WebPage)
            {
                return false;
            }

            string key = GetBrowserWindowKey(page);
            BrowserWindowRestoreContext context;
            if (string.IsNullOrWhiteSpace(key)
                || !browserWindowRestores.TryGetValue(key, out context)
                || context == null
                || context.Generation != browserRestoreGeneration
                || context.UserTakeoverGuard == null)
            {
                return false;
            }

            bool minimized = context.UserTakeoverGuard.ConfirmUserMinimized();
            if (minimized)
            {
                AppLog.Write("[窗口恢复] 用户已最小化浏览器窗口，跳过该窗口剩余网页："
                    + key + " 当前页=" + (page.DisplayName ?? page.Url ?? "(未命名)"));
            }
            return minimized;
        }

        /// <summary>
        /// 用户关闭浏览器窗口导致的跳过：不是失败，也不重新打开。
        /// 与 B2 的非浏览器“用户关闭”分开计数，完成提示才能说清是网页。
        /// </summary>
        private static LaunchResult BrowserWindowClosed()
        {
            return new LaunchResult
            {
                Success = true,
                WasSuppressed = false,
                WasAlreadyOpen = false,
                WasBrowserWindowClosed = true,
                Error = null
            };
        }

        /// <summary>
        /// 用户手动最小化浏览器窗口 = 用户接管。
        /// 不是失败，不再重开、不再还原、最后也不缩窗。
        /// </summary>
        private static LaunchResult BrowserWindowUserMinimized()
        {
            return new LaunchResult
            {
                Success = true,
                WasSuppressed = false,
                WasAlreadyOpen = false,
                WasBrowserWindowUserMinimized = true,
                Error = null
            };
        }

        private LaunchResult LaunchWebPage(SessionItem item)
        {
            string url = item == null ? null : item.Url;
            if (string.IsNullOrWhiteSpace(url) && item != null)
            {
                // TargetPath is the compatibility field used by older
                // snapshots and by imported records.
                url = item.TargetPath;
            }
            if (!IsSafeUrl(url))
            {
                return Failure("The saved web page URL is empty or unsupported.");
            }

            // Capture keeps the address-bar string verbatim for user editing,
            // but shell/browser launch should not receive incidental leading
            // or trailing whitespace exposed by an accessibility provider.
            string launchUrl = url.Trim();

            // B3：给恢复主循环兜底。即使主循环漏了检查，真正启动下一页之前
            // 也会再确认一次；用户已经关窗就返回“浏览器窗口关闭跳过”，
            // 绝不再把这一页交给浏览器。
            string browserWindowKey = GetBrowserWindowKey(item);
            BrowserWindowRestoreContext existingRestoreContext;
            if (browserRestoreActive
                && !string.IsNullOrWhiteSpace(browserWindowKey)
                && browserWindowRestores.TryGetValue(browserWindowKey, out existingRestoreContext))
            {
                if (ConfirmBrowserWindowClosed(existingRestoreContext))
                {
                    return BrowserWindowClosed();
                }
                if (existingRestoreContext.UserTakeoverGuard != null
                    && existingRestoreContext.UserTakeoverGuard.ConfirmUserMinimized())
                {
                    return BrowserWindowUserMinimized();
                }
            }

            string browserPath = item.BrowserExecutablePath;
            if (string.IsNullOrWhiteSpace(browserPath))
            {
                browserPath = item.ExecutablePath;
            }

            if (!string.IsNullOrWhiteSpace(browserPath))
            {
                browserPath = Environment.ExpandEnvironmentVariables(browserPath.Trim());
            }

            // Prefer the original browser when it is still installed.  The
            // shell fallback preserves restore behavior after a browser has
            // moved or been replaced by another version.
            if (!string.IsNullOrWhiteSpace(browserPath) && File.Exists(browserPath))
            {
                List<string> batchUrls = null;
                bool startsSavedWindow = ShouldStartNewBrowserWindow(item, browserRestoreActive);
                if (item.StartsBrowserWindow
                    && !string.IsNullOrWhiteSpace(browserWindowKey)
                    && pendingBrowserBatches.TryGetValue(browserWindowKey, out batchUrls)
                    && batchUrls != null
                    && batchUrls.Count > 0)
                {
                    // Chromium accepts multiple URL arguments and creates one
                    // tab per URL in the same new window.  This keeps pages
                    // bound to their captured window even when two windows'
                    // pages were interleaved in an older snapshot.
                    string batchedArguments = WithBrowserDebugPort(
                        browserPath,
                        BrowserCatalog.GetNewWindowSwitch(browserPath));
                    for (int index = 0; index < batchUrls.Count; index++)
                    {
                        batchedArguments += " " + QuoteArgument(batchUrls[index]);
                    }
                    pendingBrowserBatches.Remove(browserWindowKey);
                    List<string> browserWindowTitles = null;
                    pendingBrowserWindowTitles.TryGetValue(browserWindowKey, out browserWindowTitles);
                    pendingBrowserWindowTitles.Remove(browserWindowKey);
                    LaunchResult batchResult = LaunchBrowserWindow(
                        browserPath,
                        batchedArguments,
                        item,
                        browserWindowKey,
                        browserWindowTitles);
                    if (batchResult != null && batchResult.Success)
                    {
                        startedBrowserBatches.Add(browserWindowKey);
                    }
                    return batchResult;
                }

                // Pages after the first one were already passed as URL
                // arguments to the first launch.  Returning a successful
                // already-open result preserves progress/result accounting
                // without asking the shell to attach to an arbitrary active
                // browser window.
                if (!item.StartsBrowserWindow
                    && !string.IsNullOrWhiteSpace(browserWindowKey)
                    && startedBrowserBatches.Contains(browserWindowKey))
                {
                    return AlreadyOpen();
                }

                // If the page is already open in a tab, the manual list click
                // handles the jump; during a restore each saved page is opened
                // fresh so the session resumes exactly as it was saved.
                //
                // 逐页恢复时，后面的标签必须开进第一页那个窗口。Edge 已经
                // 运行（尤其还有旧的最小化窗口）时，不带 --new-window 的 URL
                // 会被接到 Edge 的“最后活动窗口”，可能落到旧窗口里；所以
                // 每一页启动前先确认/激活目标窗口。只在恢复上下文里做，
                // 手工单独打开网页的行为不变。
                if (browserRestoreActive
                    && !item.StartsBrowserWindow
                    && !string.IsNullOrWhiteSpace(browserWindowKey))
                {
                    BrowserWindowRestoreContext restoreContext;
                    if (browserWindowRestores.TryGetValue(browserWindowKey, out restoreContext))
                    {
                        bool userMinimized;
                        if (!EnsureBrowserWindowTarget(restoreContext, out userMinimized))
                        {
                            return userMinimized
                                ? BrowserWindowUserMinimized()
                                : BrowserWindowClosed();
                        }
                    }
                }

                string arguments = WithBrowserDebugPort(
                    browserPath,
                    QuoteArgument(launchUrl));
                if (startsSavedWindow)
                {
                    // Compatibility path for records that predate batch
                    // preparation. The first saved tab establishes the
                    // original browser window.
                    arguments = BrowserCatalog.GetNewWindowSwitch(browserPath) + " " + arguments;
                    if (browserRestoreActive)
                    {
                        List<string> browserWindowTitles = null;
                        if (!string.IsNullOrWhiteSpace(browserWindowKey))
                        {
                            pendingBrowserWindowTitles.TryGetValue(browserWindowKey, out browserWindowTitles);
                            pendingBrowserWindowTitles.Remove(browserWindowKey);
                        }
                        LaunchResult firstPageResult = LaunchBrowserWindow(
                            browserPath,
                            arguments,
                            item,
                            browserWindowKey,
                            browserWindowTitles);
                        if (firstPageResult != null && firstPageResult.Success)
                        {
                            RememberInitialBrowserWindowTarget(browserWindowKey);
                        }
                        return firstPageResult;
                    }
                }
                return Start(
                    browserPath,
                    arguments,
                    Path.GetDirectoryName(browserPath),
                    startsSavedWindow ? item.WindowPlacement : null,
                    item.WindowTitle,
                    null);
            }

            return Start(launchUrl, null, null);
        }

        private static string WithBrowserDebugPort(string browserPath, string arguments)
        {
            return BrowserCatalog.IsChromiumPath(browserPath)
                ? BrowserDebugProtocol.WithDebugPort(arguments)
                : arguments;
        }

        /// <summary>
        /// 单页和多页都要在首屏后尝试认领窗口；只有已经认领过才跳过。
        /// 这是 2026-11-05 单页 GPT 组“完全不缩”的直接修复点。
        /// </summary>
        internal static bool ShouldAttemptInitialBrowserWindowClaim(
            int expectedPageCount,
            bool hasTrackedWindow)
        {
            return !hasTrackedWindow;
        }

        /// <summary>
        /// “页面未就绪”要不要进完成提示（方案 R+：单页/多页共用）：
        ///   · 只有保存时就是最小化（MinimizeAtEnd）才提示——普通/最大化窗口本来就不缩；
        ///   · 精确窗口必须还在；
        ///   · 用户没有手动最小化、也没有关窗——那是用户接管，走已有的关窗/接管提示。
        /// 返回 true 表示“窗口保持原样”是预期外结果，完成提示要如实说明，
        /// 不能只报“成功 N / N 项”让用户以为最小化已经做了。
        /// </summary>
        internal static bool ShouldReportBrowserWindowNotReady(
            bool savedMinimized,
            bool windowAlive,
            bool windowMinimized,
            bool userClosed)
        {
            return savedMinimized && windowAlive && !windowMinimized && !userClosed;
        }

        /// <summary>
        /// B3：第一页启动后立刻认领这个窗口的精确 HWND。单页和多页走同一条路；
        /// 这里只做 1 秒快速认领，没认领到也不阻塞后续页，收尾会再等。
        /// </summary>
        private void RememberInitialBrowserWindowTarget(string browserWindowKey)
        {
            if (!browserRestoreActive
                || string.IsNullOrWhiteSpace(browserWindowKey))
            {
                return;
            }

            BrowserWindowRestoreContext context;
            if (!browserWindowRestores.TryGetValue(browserWindowKey, out context)
                || context == null
                || context.CloseGuard == null
                || !ShouldAttemptInitialBrowserWindowClaim(
                    context.ExpectedPageCount,
                    context.CloseGuard.HasTrackedWindow))
            {
                return;
            }

            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                return;
            }

            IntPtr tracked = BrowserWindowTracker.Get(browserWindowKey);
            if (tracked != IntPtr.Zero)
            {
                if (RememberBrowserWindowTarget(context, tracked))
                {
                    AppLog.Write("[窗口恢复] 第一页启动后浏览器窗口已经关闭，"
                        + "该窗口剩余网页将全部跳过：" + browserWindowKey
                        + " HWND=" + tracked);
                }
                if (context.CloseGuard.HasTrackedWindow)
                {
                    return;
                }
            }

            IntPtr target = BrowserWindowClaimer.TryClaim(
                browserWindowKey,
                context.ExecutablePath,
                context.Titles,
                context.ExistingWindows,
                BrowserInitialTargetWaitMilliseconds,
                delegate
                {
                    return context.CloseGuard.IsClosedByUser;
                },
                delegate
                {
                    return context.UserTakeoverGuard != null
                        && context.UserTakeoverGuard.ConfirmUserMinimized();
                });
            if (target == IntPtr.Zero)
            {
                AppLog.Write("[窗口恢复] 第一页启动后暂未认领到浏览器窗口，"
                    + "后面仍会逐页检查：" + browserWindowKey);
                return;
            }
            if (RememberBrowserWindowTarget(context, target))
            {
                AppLog.Write("[窗口恢复] 第一页启动后浏览器窗口已经关闭，"
                    + "该窗口剩余网页将全部跳过：" + browserWindowKey
                    + " HWND=" + target);
                return;
            }
            AppLog.Write("[窗口恢复] 已认领浏览器窗口，后续关窗/最小化将停止该组："
                + browserWindowKey + " HWND=" + target
                + " 保存页数=" + context.ExpectedPageCount);
        }

        /// <summary>
        /// 逐页恢复时，在启动下一页之前确认目标窗口还在，并在必要时把它
        /// 切到前台。这样 Edge 的“在最后活动窗口里开标签”才会落在我们
        /// 第一页创建的那个窗口，而不是恢复前就存在的旧窗口。
        /// </summary>
        private static bool EnsureBrowserWindowTarget(
            BrowserWindowRestoreContext context,
            out bool userMinimized)
        {
            userMinimized = false;
            if (context == null || string.IsNullOrWhiteSpace(context.Key))
            {
                return true;
            }

            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                userMinimized = true;
                return false;
            }

            // B3：上一页启动后用户可能已经把窗口打叉关闭。先查精确句柄，
            // 确认关闭就直接返回 false，让调用方跳过本组剩余网页。
            if (ConfirmBrowserWindowClosed(context))
            {
                AppLog.Write("[窗口恢复] 启动下一页前发现浏览器窗口已关闭，"
                    + "跳过该窗口剩余网页：" + context.Key);
                return false;
            }

            // 已经认领到精确窗口时，只用它，不再让跟踪器把目标换到另一个
            // 同 exe 窗口；只有还没认领到时才等待/定位。
            IntPtr target = context.CloseGuard == null
                ? IntPtr.Zero
                : context.CloseGuard.WindowHandle;
            if (target == IntPtr.Zero
                || (context.CloseGuard != null && !context.CloseGuard.IsAliveNow()))
            {
                target = WindowPlacementService.WaitForBrowserWindow(
                    context.Key,
                    context.ExecutablePath,
                    context.Titles,
                    context.ExistingWindows,
                    BrowserPageTargetWaitMilliseconds);
            }
            if (target == IntPtr.Zero)
            {
                // Wait 期间也可能刚好关窗。这里再看一次；仍无法确认时
                // 只能按原逻辑继续，不能把“还没建窗”当成“用户关窗”。
                if (ConfirmBrowserWindowClosed(context))
                {
                    AppLog.Write("[窗口恢复] 等待目标窗口时发现浏览器窗口已关闭，"
                        + "跳过该窗口剩余网页：" + context.Key);
                    return false;
                }
                if (context.UserTakeoverGuard != null
                    && context.UserTakeoverGuard.ConfirmUserMinimized())
                {
                    userMinimized = true;
                    return false;
                }
                AppLog.Write("[窗口恢复] 逐页恢复时目标浏览器窗口暂未找到："
                    + context.Key + "；下一页仍会启动，最后由最终校验兜底。");
                return true;
            }

            if (RememberBrowserWindowTarget(context, target))
            {
                AppLog.Write("[窗口恢复] 认领到目标浏览器窗口时它已失效，"
                    + "跳过该窗口剩余网页：" + context.Key + " HWND=" + target);
                return false;
            }

            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                userMinimized = true;
                return false;
            }

            // 恢复前没有别的 Edge 窗口时，新窗口本来就是最后活动窗口，
            // 不需要反复抢前台；只有确实存在旧窗口时才做这一步。
            if (context.ExistingWindows == null || context.ExistingWindows.Count == 0)
            {
                return true;
            }
            if (NativeMethods.GetForegroundWindow() == target)
            {
                return true;
            }

            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                userMinimized = true;
                return false;
            }

            bool activated = WindowPlacementService.TryActivateBrowserWindow(target);
            AppLog.Write("[窗口恢复] 逐页启动前确认目标浏览器窗口："
                + context.Key + " HWND=" + target + " 激活=" + activated);
            if (context.UserTakeoverGuard != null
                && context.UserTakeoverGuard.ConfirmUserMinimized())
            {
                userMinimized = true;
                return false;
            }
            return !ConfirmBrowserWindowClosed(context);
        }

        /// <summary>
        /// 浏览器窗口第一页的统一启动入口。
        ///
        /// 【2026-10-25 用户要求】保存为最小化的浏览器窗口：
        ///   · 第一页只按“最小化前状态”打开，不在这里缩；
        ///   · 等这个窗口的最后一页启动完（SessionRestoreService 调 Complete），
        ///     再缩一次。这样既保留逐页间隔，也不会每开一页就缩/弹一次。
        /// 普通/最大化窗口仍然只应用原来的位置和状态。
        /// </summary>
        /// <summary>
        /// Edge/Chrome 没有进程时先用 --no-startup-window 起后台进程。
        /// 如果直接 --new-window 启动，Edge 的崩溃会话恢复（exit_type=Crashed）
        /// 会和方案页面抢同一个窗口，实测会把 8 页窗口换成 xtdowner 11 页，
        /// 随后 App 就可能缩到已经变成别的页面的窗口。先起无窗口后台进程，
        /// 会话恢复没有窗口可抢；随后 --new-window 开出来的就是干净窗口。
        /// 已有浏览器进程时不做：用户自己的会话已经在跑，强起反而多余。
        /// </summary>
        private void EnsureBrowserProcessWarmStarted(string browserPath)
        {
            if (string.IsNullOrWhiteSpace(browserPath)
                || OsMutationGuard.IsDisabled
                || ExternalEffectsDisabled()
                || !BrowserCatalog.IsChromiumPath(browserPath))
            {
                return;
            }
            string key = NormalizeLaunchPath(browserPath);
            if (string.IsNullOrWhiteSpace(key) || warmedBrowserProcesses.Contains(key))
            {
                return;
            }

            // 有可见窗口才是用户自己的会话，不能碰。Edge 的“启动增强”会在
            // 登录后常驻一个没有窗口的 msedge.exe；旧逻辑只要看到进程就跳过
            // 预热，第一次 --new-window 于是直接触发崩溃会话恢复，出现 9 页
            // RunningHub/微信窗口并把 8 页窗口替换掉。必须把“只有后台进程”
            // 当成需要预热的冷启动，而不是当成用户会话。
            if (FindAnyVisibleWindowByExecutable(browserPath) != IntPtr.Zero)
            {
                AppLog.Write("[窗口恢复] 浏览器已有可见窗口，按用户会话处理，不预热：" + browserPath);
                return;
            }
            if (IsProcessRunningByPath(browserPath))
            {
                AppLog.Write("[窗口恢复] 浏览器只有无窗口的后台进程，先结束后台再预热：" + browserPath);
                StopBackgroundBrowserProcesses(browserPath);
                Thread.Sleep(500);
            }

            // 先清掉 exit_type=Crashed，再预热。否则 --no-startup-window 也挡不住
            // 第一条 --new-window 把旧会话拉进来（8 页会被替换成 15 页）。
            BrowserProfileCleaner.TrySuppressCrashRestore(browserPath);

            if (!warmedBrowserProcesses.Add(key))
            {
                return;
            }
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = browserPath;
                startInfo.Arguments = "--no-startup-window";
                startInfo.UseShellExecute = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process process = Process.Start(startInfo);
                if (process != null)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
                AppLog.Write("[窗口恢复] 浏览器进程已用 --no-startup-window 预热，阻止崩溃会话恢复抢窗口："
                    + browserPath);
                // 不用 WaitForInputIdle：后台 Edge 没有普通输入队列时可能长时间
                // 不返回；冷启动只多等一点固定时间。
                Thread.Sleep(3000);
            }
            catch (Exception exception)
            {
                warmedBrowserProcesses.Remove(key);
                AppLog.Write("[窗口恢复] 浏览器进程预热失败，继续按原方式启动", exception);
            }
        }

        private static void StopBackgroundBrowserProcesses(string browserPath)
        {
            string normalized = NormalizeLaunchPath(browserPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }
            string processName;
            try
            {
                processName = Path.GetFileNameWithoutExtension(normalized);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch
            {
                return;
            }
            for (int index = 0; index < processes.Length; index++)
            {
                Process process = processes[index];
                try
                {
                    string actual = GetProcessExecutablePath(process.Id);
                    if (string.IsNullOrWhiteSpace(actual)
                        || !string.Equals(NormalizeLaunchPath(actual), normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    // 前面已经确认没有可见窗口；这里再确认一次，避免误杀
                    // 刚好在检查后弹出的用户窗口。
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        continue;
                    }
                    process.Kill();
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private LaunchResult LaunchBrowserWindow(
            string browserPath,
            string arguments,
            SessionItem item,
            string browserWindowKey,
            IList<string> browserWindowTitles)
        {
            EnsureBrowserProcessWarmStarted(browserPath);
            bool minimized = browserRestoreActive
                && WindowMinimizeState.IsMinimized(item.WindowPlacement);
            SavedWindowPlacement launchPlacement = minimized
                ? CreateDisplayPlacement(item.WindowPlacement)
                : item.WindowPlacement;
            // 恢复前已经有窗口时必须全部记下来，最后复核不能把旧窗口
            // 当成这一轮新建的目标。以前只在有 bounds 时才抓，用户新增的
            // 最小化浏览器（没有坐标）会漏掉。
            ISet<IntPtr> existingWindows = WindowPlacementService.CaptureExistingWindows(browserPath);
            string restoreKey = browserRestoreActive
                && !string.IsNullOrWhiteSpace(browserWindowKey)
                ? browserWindowKey
                : null;
            // 先读用户固定任务栏的 AppID；没有固定项/多个固定项无法判断时为 null，
            // 保持系统默认分组。不要在这台电脑上写死 MSEdge.SB2…。
            string taskbarAppId = TaskbarIdentityService.ResolvePinnedAppId(browserPath);

            int expectedPageCount = 0;
            string lastPageTitle = null;
            List<string> expectedUrls = null;
            if (!string.IsNullOrWhiteSpace(browserWindowKey))
            {
                pendingBrowserPageCounts.TryGetValue(browserWindowKey, out expectedPageCount);
                pendingBrowserLastPageTitles.TryGetValue(browserWindowKey, out lastPageTitle);
                pendingBrowserUrls.TryGetValue(browserWindowKey, out expectedUrls);
            }

            if (restoreKey != null)
            {
                BrowserWindowCloseGuard closeGuard = new BrowserWindowCloseGuard(restoreKey);
                browserWindowRestores[restoreKey] = new BrowserWindowRestoreContext
                {
                    Key = restoreKey,
                    ExecutablePath = browserPath,
                    FinalPlacement = item.WindowPlacement,
                    DisplayPlacement = launchPlacement,
                    Titles = browserWindowTitles,
                    ExpectedUrls = expectedUrls,
                    ExistingWindows = existingWindows,
                    ExpectedPageCount = expectedPageCount,
                    LastPageTitle = lastPageTitle,
                    Generation = browserRestoreGeneration,
                    TaskbarAppId = taskbarAppId,
                    MinimizeAtEnd = minimized,
                    CloseGuard = closeGuard,
                    UserTakeoverGuard = new BrowserWindowUserTakeoverGuard(restoreKey, closeGuard)
                };
                AppLog.Write("[窗口恢复] 浏览器窗口开始逐页恢复："
                    + restoreKey
                    + " 保存页数=" + expectedPageCount
                    + " 恢复前已有窗口=" + (existingWindows == null ? 0 : existingWindows.Count)
                    + " 固定任务栏身份=" + (taskbarAppId ?? "(无)")
                    + " 最后最小化=" + minimized);
            }

            return Start(
                browserPath,
                arguments,
                Path.GetDirectoryName(browserPath),
                launchPlacement,
                item.WindowTitle,
                null,
                true,
                browserWindowTitles,
                restoreKey,
                existingWindows,
                taskbarAppId);
        }

        private static SavedWindowPlacement CreateDisplayPlacement(SavedWindowPlacement placement)
        {
            if (placement == null)
            {
                return null;
            }
            SavedWindowPlacement display = placement.Clone();
            display.WindowState = WindowMinimizeState.Normalize(placement.WindowStateBeforeMinimize);
            return display;
        }

        /// <summary>
        /// 判断一个网页条目应不应该用 <c>--new-window</c> 新建浏览器窗口。
        ///
        /// 【2026-10-25 用户报“两个浏览器恢复成满屏浏览器”的根因】
        /// 恢复前 <c>ApplyParentWindowPlacements</c> 会把父窗口位置复制给每个网页，
        /// 旧逻辑把“有 WindowPlacement”也当成“这是窗口的第一页”。于是同一窗口
        /// 里的 8 个标签每页都拿到 --new-window，两个浏览器恢复成了 14 个窗口。
        /// 恢复期间必须只认 PrepareBrowserWindowStarters 标出的第一页；
        /// 手工单独打开一个网页时仍保留旧的单页判断。
        /// </summary>
        internal static bool ShouldStartNewBrowserWindow(SessionItem item, bool browserRestoreActive)
        {
            if (item == null)
            {
                return false;
            }

            if (item.StartsBrowserWindow)
            {
                return true;
            }

            if (browserRestoreActive)
            {
                // 恢复已由 SessionRestoreService 为每个浏览器窗口选好第一页；
                // 其余页只作为标签打开，绝不能再凭父窗口位置逐页新建窗口。
                return false;
            }

            // 手工点击/单独启动一个网页的兼容路径：原来的行为不变。
            return item.BrowserWindowIndex >= 0
                && ((item.WindowPlacement != null && item.WindowPlacement.HasBounds)
                    || item.BrowserTabIndex == 0);
        }

        private static string GetBrowserWindowKey(SessionItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            string group = item.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(group))
            {
                group = item.BrowserExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(group))
            {
                group = item.ExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(group))
            {
                return string.Empty;
            }
            if (item.BrowserWindowIndex >= 0)
            {
                return group.Trim() + "|window|" + item.BrowserWindowIndex;
            }
            if (!string.IsNullOrWhiteSpace(item.ParentId))
            {
                return group.Trim() + "|parent|" + item.ParentId.Trim();
            }
            return string.Empty;
        }

        private static bool IsSafeUrl(string value)
        {
            // 和保存侧（SessionCaptureService）用同一份规则，唯一实现在
            // Shared\Infrastructure\WebAddressTools.cs。
            // 2026-10-15 用户反馈“恢复的时候有问题”：这里原来自己抄了一份协议白名单，
            // 没有“没有写协议的地址”这条，于是 127.0.0.1:3080 存得下来却恢复不了。
            return WebAddressTools.IsWebAddress(value);
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

    }
}
