using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 恢复期间用户选择“立即恢复”后的标记。
    /// 一旦置位，本次恢复后续不再因为 CPU/内存超限暂停；CPU/内存仍然照常读取，
    /// 只是不再挡启动。每次恢复都会新建一个，不能跨恢复复用。
    /// </summary>
    public sealed class RestoreLoadGate
    {
        private int skipLoadGate;

        public bool SkipLoadGate
        {
            get { return Interlocked.CompareExchange(ref skipLoadGate, 0, 0) != 0; }
        }

        public void ContinueForRestore()
        {
            Interlocked.Exchange(ref skipLoadGate, 1);
        }
    }

    public sealed class RestoreOptions
    {
        public RestoreOptions()
        {
            // 2026-10-20 用户要求：启动前等待默认 5 秒（原 10 秒太慢），控制器会按 0–60 秒注入设置值。
            InitialDelay = TimeSpan.FromSeconds(5);
            ItemDelay = TimeSpan.FromSeconds(3);
            // Historical behaviour unless the controller injects the user's
            // "web page gap" setting: zero hands every URL of a browser window
            // to the browser in one launch.
            WebPageDelay = TimeSpan.Zero;
            // 等待时每秒刷新一次“当前 CPU/内存 + 已等待秒数”，不能几秒才跳一下。
            LoadPollInterval = TimeSpan.FromMilliseconds(500);
            CpuSampleInterval = TimeSpan.FromMilliseconds(500);
            MaximumCpuPercent = 75;
            // 0 表示不等待；内存/CPU 超限时直接按停止处理。
            LoadWaitTimeout = TimeSpan.FromSeconds(120);
            MaximumMemoryPercent = 75;
            MemoryWaitTimeout = TimeSpan.FromSeconds(120);
            WaitForAcceptableLoad = true;
            LoadGate = null;
        }

        public TimeSpan InitialDelay { get; set; }

        public TimeSpan ItemDelay { get; set; }

        /// <summary>
        /// Gap between two saved web pages when a browser window's pages are
        /// restored.  Browser windows are always restored one page per launch
        /// (so a closed window can cancel the rest); zero simply means no wait.
        /// </summary>
        public TimeSpan WebPageDelay { get; set; }

        public TimeSpan LoadPollInterval { get; set; }

        public TimeSpan CpuSampleInterval { get; set; }

        public double MaximumCpuPercent { get; set; }

        /// <summary>CPU 一直降不下来时最多等多久；到点仍高就暂停恢复。</summary>
        public TimeSpan LoadWaitTimeout { get; set; }

        public double MaximumMemoryPercent { get; set; }

        /// <summary>内存一直降不下来时最多等多久；到点仍高就暂停恢复。</summary>
        public TimeSpan MemoryWaitTimeout { get; set; }

        public bool WaitForAcceptableLoad { get; set; }

        /// <summary>用户点“立即恢复”后置位；本次恢复后续不再被 CPU/内存挡住。</summary>
        public RestoreLoadGate LoadGate { get; set; }
    }

    public enum RestoreProgressState
    {
        WaitingInitialDelay,
        WaitingForSystemLoad,
        StartingItem,
        ItemCompleted,
        /// <summary>
        /// 所有项目都已启动完，正在等待浏览器最小化收尾。
        /// 这张卡片仍然显示，直到收尾结束或到达硬上限。
        /// </summary>
        Finalizing,
        Completed,
        Cancelled
    }

    public sealed class RestoreProgress
    {
        public RestoreProgressState State { get; set; }

        public int ItemNumber { get; set; }

        public int TotalItems { get; set; }

        public SessionItem Item { get; set; }

        public SystemLoadSnapshot SystemLoad { get; set; }

        public string Message { get; set; }

        // ---- 负载等待（CPU / 内存） ----
        /// <summary>true＝当前处于一次负载暂停中（即使两项刚好都低于上限，也算暂停未结束）。</summary>
        public bool LoadPauseActive { get; set; }

        public bool CpuOverLimit { get; set; }

        public bool MemoryOverLimit { get; set; }

        public int CpuLimitPercent { get; set; }

        public int MemoryLimitPercent { get; set; }

        public int CpuWaitLimitSeconds { get; set; }

        public int MemoryWaitLimitSeconds { get; set; }

        public int CpuOverSeconds { get; set; }

        public int MemoryOverSeconds { get; set; }

        /// <summary>当前合并等待上限：两项都超时取较长值，否则取超限那一项自己的。</summary>
        public int EffectiveWaitLimitSeconds { get; set; }

        public int EffectiveElapsedSeconds { get; set; }

        public int EffectiveRemainingSeconds { get; set; }
    }

    public sealed class SystemLoadSnapshot
    {
        public bool IsAvailable { get; set; }

        public double CpuPercent { get; set; }

        public double MemoryPercent { get; set; }

        public string Error { get; set; }
    }

    public sealed class LaunchResult
    {
        public bool Success { get; set; }

        public bool WasSuppressed { get; set; }

        public bool WasAlreadyOpen { get; set; }

        /// <summary>
        /// 恢复开始前这个项目本来是开着的，轮到它之前被用户手动关闭了。
        /// 这种情况按“用户已关闭，跳过”处理，不重新启动。
        /// 浏览器网页和浏览器窗口永远不走这个标记（浏览器保持现有恢复逻辑）。
        /// </summary>
        public bool WasUserClosed { get; set; }

        /// <summary>
        /// B3：用户把网页所在的整个浏览器窗口打叉关闭了。
        /// 该窗口组剩余网页按“跳过”处理，不再启动，也不在最后缩窗。
        /// 只影响这一个浏览器窗口，其它浏览器窗口继续恢复。
        /// </summary>
        public bool WasBrowserWindowClosed { get; set; }

        /// <summary>
        /// 用户在恢复中手动把浏览器窗口最小化了。
        /// 这是“用户接管”：该窗口组剩余网页不再启动，不还原、不激活、最后也不缩窗。
        /// 与“用户关窗”分开记录，完成提示才能说清是缩小还是关闭。
        /// </summary>
        public bool WasBrowserWindowUserMinimized { get; set; }

        public string Error { get; set; }
    }

    public sealed class RestoreItemResult
    {
        public SessionItem Item { get; set; }

        public DateTime AttemptedAtUtc { get; set; }

        public bool Success { get; set; }

        public bool WasSuppressed { get; set; }

        public bool WasAlreadyOpen { get; set; }

        /// <summary>恢复前开着、轮到此项之前被用户关闭，因此跳过，不是失败。</summary>
        public bool WasUserClosed { get; set; }

        /// <summary>
        /// B3：用户关闭了整个浏览器窗口，这一页属于该窗口组，因此跳过。
        /// 不是失败，也不会重新打开。
        /// </summary>
        public bool WasBrowserWindowClosed { get; set; }

        /// <summary>
        /// 用户在恢复中手动最小化了整个浏览器窗口，这一页属于该窗口组，因此跳过。
        /// 不是失败；不还原、不激活、最后不缩窗。
        /// </summary>
        public bool WasBrowserWindowUserMinimized { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// CPU/内存等待到上限仍然超限时，恢复服务用这个异常结束整次恢复。
    /// `Reason` 是给用户看的完整中文句子（只写用户设置的上限，不写当前值/秒数）。
    /// </summary>
    public sealed class RestoreLoadLimitException : Exception
    {
        public RestoreLoadLimitException(string reason)
            : base(reason)
        {
            Reason = reason ?? string.Empty;
        }

        public string Reason { get; private set; }
    }

    public sealed class RestoreResult
    {
        public RestoreResult()
        {
            Items = new List<RestoreItemResult>();
        }

        public DateTime StartedAtUtc { get; set; }

        public DateTime FinishedAtUtc { get; set; }

        public bool IsCancelled { get; set; }

        /// <summary>true＝因为 CPU/内存等待超时自动停止，不是用户点取消。</summary>
        public bool IsAutoStopped { get; set; }

        /// <summary>自动停止的完整中文原因；手动取消时为 null。</summary>
        public string CancelReason { get; set; }

        /// <summary>
        /// 本次恢复里“保存为最小化的单页浏览器窗口，3 秒内没读到真实标题，
        /// 所以先保持原样、没有缩窗”的窗口数。不是失败：网页已经打开，
        /// 只是没敢替用户缩下去。由浏览器批次启动器在收尾后回报。
        /// 这些窗口随后还会在后台补缩一次（读到标题才缩），但提示按 3 秒那一刻
        /// 的状态统计——补缩成功也不会改提示，这是用户 2026-11-06 定的。
        /// </summary>
        public int BrowserWindowNotReadyCount { get; set; }

        public IList<RestoreItemResult> Items { get; private set; }
    }

    /// <summary>
    /// 这次恢复完成事件来自哪个工作区。文案目录按这个值路由到
    /// Text/Restore/StartupRestore 或 Text/Restore/Applications。
    /// </summary>
    public enum RestoreSource
    {
        StartupRestore = 0,
        Applications = 1
    }

    /// <summary>
    /// Summary of one finished restore operation.  The controller raises this
    /// on the dispatcher so the UI can show whether every enabled item
    /// started, or whether some items failed.
    /// </summary>
    public sealed class RestoreCompletion
    {
        public RestoreCompletion()
        {
            Failures = new List<RestoreFailure>();
            Source = RestoreSource.StartupRestore;
        }

        public RestoreSource Source { get; set; }

        public bool IsCancelled { get; set; }

        /// <summary>true＝CPU/内存超时自动停止；这种提示要一直显示到用户点击空白处。</summary>
        public bool IsAutoStopped { get; set; }

        /// <summary>自动停止的完整中文原因；手动取消时为空。</summary>
        public string CancelReason { get; set; }

        public int TotalCount { get; set; }

        public int SuccessCount { get; set; }

        public int FailedCount { get; set; }

        public int AlreadyOpenCount { get; set; }

        /// <summary>
        /// 恢复开始前开着、轮到此项之前被用户关闭的项数（只统计非浏览器项）。
        /// 这些项按“跳过”处理，不计失败，也不会重新打开。
        /// </summary>
        public int UserClosedCount { get; set; }

        /// <summary>
        /// B3：用户关闭浏览器窗口后，该窗口组被跳过的网页数。
        /// 只统计网页，不和非浏览器的“用户关闭”混在一起。
        /// </summary>
        public int BrowserWindowClosedCount { get; set; }

        /// <summary>
        /// 用户在恢复中手动最小化浏览器窗口后，该窗口组被跳过的网页数。
        /// 与“浏览器窗口关闭”分开统计，提示要写清是“最小化”而不是“关闭”。
        /// </summary>
        public int BrowserWindowUserMinimizedCount { get; set; }

        /// <summary>
        /// 保存为最小化、但 3 秒内没读到真实标题的单页浏览器窗口数。
        /// 这些窗口先留在普通状态（不缩、不还原、不抢前台），后台再补缩一次，
        /// 不是失败；横幅要用警告色说明“页面未就绪 + 窗口保持原样”。
        /// </summary>
        public int BrowserWindowNotReadyCount { get; set; }

        /// <summary>
        /// 用户 2026-10-15 要求：“显示某些无法恢复，要在提示上说清楚到底是哪些没有恢复
        /// （以及为什么无法恢复）”。这里逐条记下没成功的是哪一项、原因是什么。
        /// </summary>
        public IList<RestoreFailure> Failures { get; set; }
    }

    /// <summary>一条“没能恢复”的记录：是哪一项、为什么。</summary>
    public sealed class RestoreFailure
    {
        /// <summary>条目展示名（例如“Microsoft Edge (2)”或某个网页标题）。</summary>
        public string Name { get; set; }

        /// <summary>条目的类型说明（应用 / 网页 / 文件夹 / 文件）。</summary>
        public string KindText { get; set; }

        /// <summary>失败原因，已经翻译成中文、可直接给用户看。</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// Summary of one finished explicit session save.  The controller raises
    /// this on the dispatcher so the UI can confirm the save and warn when
    /// some browser pages could not be read.
    /// </summary>
    public sealed class SaveCompletion
    {
        public int ItemCount { get; set; }

        /// <summary>有内容没保存完整（浏览器网页没读全 / 捕获有警告）。</summary>
        public bool Incomplete { get; set; }

        /// <summary>真的“有些网页没读到”（浏览器窗口最小化时只能读到当前页）。</summary>
        public bool BrowserIncomplete { get; set; }

        /// <summary>结构化未读窗口清单（窗口名、缺几页、是否最小化），横幅要显示窗口名。</summary>
        public List<BrowserUnreadWindowInfo> UnreadBrowserWindows { get; set; }

        /// <summary>网页读取整体超时、退回“应用+文件夹”基线。</summary>
        public bool BrowserCaptureTimedOut { get; set; }

        /// <summary>这次保存涉及“终端 / 本地网址”的项数（保留或跳过都算）。</summary>
        public int PolicyAffectedCount { get; set; }

        /// <summary>按「扫描终端与本地网址」设置真正被跳过的项数（勾选时为 0）。</summary>
        public int PolicySkippedCount { get; set; }

        /// <summary>true＝设置里勾了「扫描终端与本地网址」，这些项被保留（恢复不了，只做记录）。</summary>
        public bool PolicyIncluded { get; set; }

        /// <summary>控制器给的那句取舍说明（“已跳过 5 项终端/本地网址…”）。</summary>
        public string PolicyHint { get; set; }

        public bool Failed { get; set; }

        public bool Cancelled { get; set; }

        /// <summary>
        /// Optional per-window detail of which browser windows lost pages.
        /// </summary>
        public string WarningText { get; set; }
    }

    /// <summary>
    /// 保存时“哪些浏览器窗口还有标签页没读到、为什么、怎么办”的唯一文案。
    /// 横幅、托盘气泡、状态栏共用；不再出现“读不全”，也不再让用户手动显示
    /// 最小化窗口（程序本来就会自动还原读取再缩回）。
    /// </summary>
    public static class BrowserCaptureWarningText
    {
        private static string TimeoutText
        {
            get { return AppText.T(StartupRestoreTextKeys.CaptureTimeout); }
        }

        public static string Build(SessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return string.Empty;
            }
            if (snapshot.BrowserCaptureTimedOut)
            {
                return TimeoutText;
            }
            if (!snapshot.BrowserCaptureIncomplete)
            {
                return string.Empty;
            }
            return BuildAdvice(snapshot.UnreadBrowserWindows, false);
        }

        public static string BuildTimeout()
        {
            return TimeoutText;
        }

        internal static string BuildAdvice(
            IList<BrowserUnreadWindowInfo> windows,
            bool timedOut)
        {
            if (timedOut)
            {
                return TimeoutText;
            }

            string names = FormatWindowNames(windows);
            int pages = CountUnreadPages(windows);
            bool anyMinimized = HasMinimized(windows);
            bool anyNormal = HasNormal(windows);
            if (pages <= 0)
            {
                return AppText.T(StartupRestoreTextKeys.CaptureUnreadGeneric);
            }
            if (anyMinimized && anyNormal)
            {
                return AppText.F(StartupRestoreTextKeys.CaptureUnreadMixedFormat, names, pages);
            }
            if (anyMinimized)
            {
                return AppText.F(StartupRestoreTextKeys.CaptureUnreadMinimizedFormat, names, pages);
            }
            return AppText.F(StartupRestoreTextKeys.CaptureUnreadNormalFormat, names, pages);
        }

        internal static string FormatWindowNames(IList<BrowserUnreadWindowInfo> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return AppText.T(StartupRestoreTextKeys.FallbackBrowserFallback);
            }

            List<string> names = new List<string>();
            for (int index = 0; index < windows.Count && names.Count < 2; index++)
            {
                BrowserUnreadWindowInfo window = windows[index];
                string name = window == null ? null : window.DisplayName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                name = name.Trim();
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
            if (names.Count == 0)
            {
                return AppText.T(StartupRestoreTextKeys.FallbackBrowserFallback);
            }
            string joined = string.Join(
                AppText.T(StartupRestoreTextKeys.FallbackNameSeparator), names.ToArray());
            if (windows.Count <= 2)
            {
                return joined;
            }
            return AppText.F(StartupRestoreTextKeys.CaptureWindowListFormat, joined, windows.Count);
        }

        internal static int CountUnreadPages(IList<BrowserUnreadWindowInfo> windows)
        {
            if (windows == null || windows.Count == 0)
            {
                return 0;
            }
            int total = 0;
            for (int index = 0; index < windows.Count; index++)
            {
                BrowserUnreadWindowInfo window = windows[index];
                if (window != null && window.UnreadPageCount > 0)
                {
                    total += window.UnreadPageCount;
                }
            }
            return total > 0 ? total : windows.Count;
        }

        internal static bool HasMinimized(IList<BrowserUnreadWindowInfo> windows)
        {
            if (windows == null)
            {
                return false;
            }
            for (int index = 0; index < windows.Count; index++)
            {
                if (windows[index] != null && windows[index].Minimized)
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool HasNormal(IList<BrowserUnreadWindowInfo> windows)
        {
            if (windows == null)
            {
                return false;
            }
            for (int index = 0; index < windows.Count; index++)
            {
                if (windows[index] != null && !windows[index].Minimized)
                {
                    return true;
                }
            }
            return false;
        }
    }


    /// <summary>
    /// 【2026-10-19】恢复卡片（“正在恢复工作会话…”那块）的第二段说明。
    ///
    /// 用户原话：“……恢复时的提示也要更新（自启动应用也要同步更新此类问题）。”
    /// 所以：
    ///   · 「启动与恢复」恢复时，如果这份方案里有终端/本地网址（它们本来就恢复不了），
    ///     必须当场说明，而不是等它们失败后只丢一句原因；
    ///   · 「自启动应用」逐项启动不用这段（那边终端/本地网址本来就应该能启动）。
    /// </summary>
    public static class RestoreCardText
    {
        public static string Detail(bool applications, int nonRestorableCount)
        {
            if (applications)
            {
                return AppText.T(StartupRestoreTextKeys.RestoreCardApplications);
            }
            string text = AppText.T(StartupRestoreTextKeys.RestoreCardStartup);
            if (nonRestorableCount > 0)
            {
                text += "\n" + AppText.F(
                    StartupRestoreTextKeys.RestoreCardNonRestorableFormat, nonRestorableCount);
            }
            return text;
        }
    }

    /// <summary>
    /// 【2026-10-19】保存完成时给用户看的那句话 —— 横幅和托盘气泡**共用这一份**，不许各写一套。
    ///
    /// 用户原话：
    ///   “由于我没有在设置那勾选“扫描终端与本地网址”那么这里的提示也要更新，要如实说明情况，
    ///    不要误导用户，恢复时的提示也要更新（自启动应用也要同步更新此类问题）。”
    ///
    /// 所以这里分三件事说清楚，绝不把它们混成“有些网页没读到 → 去显示最小化的浏览器窗口”：
    ///   ① 真的没读到网页（最小化）→ 才给“显示出来再保存一次”的建议；
    ///   ② 终端/本地网址被设置跳过 → 说明“跳过了几项、为什么、想保存要怎么改设置”；
    ///   ③ 勾了设置、这些项被保留 → 说明“它们恢复不了，想开机常驻请加到自启动应用”。
    /// </summary>
    public static class SaveCompletionText
    {
        /// <summary>横幅（两行）与托盘气泡（一行）共用的文案。</summary>
        public static string Build(SaveCompletion completion, bool twoLines)
        {
            if (completion == null)
            {
                return string.Empty;
            }
            string separator = twoLines
                ? "\n"
                : AppText.T(StartupRestoreTextKeys.RestoreSeparatorInline);
            if (completion.Cancelled)
            {
                return AppText.T(StartupRestoreTextKeys.RestoreSaveCancelled);
            }
            if (completion.Failed)
            {
                return AppText.T(StartupRestoreTextKeys.RestoreSaveFailed);
            }

            StringBuilder text = new StringBuilder();
            text.Append(AppText.F(StartupRestoreTextKeys.RestoreSavedFormat, completion.ItemCount));
            string advice = string.Empty;
            if (completion.PolicySkippedCount > 0)
            {
                text.Append(AppText.F(
                    StartupRestoreTextKeys.RestorePolicySkippedFormat,
                    completion.PolicySkippedCount));
                advice = AppText.T(StartupRestoreTextKeys.RestorePolicyAdvice);
            }
            else if (completion.PolicyAffectedCount > 0 && completion.PolicyIncluded)
            {
                text.Append(AppText.F(
                    StartupRestoreTextKeys.RestorePolicyIncludedFormat,
                    completion.PolicyAffectedCount));
            }

            if (completion.BrowserIncomplete)
            {
                advice = BrowserCaptureWarningText.BuildAdvice(
                    completion.UnreadBrowserWindows,
                    completion.BrowserCaptureTimedOut);
            }
            if (advice.Length > 0)
            {
                text.Append(separator).Append(advice);
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// 【2026-11-06 用户确认方案 A】最后一页之后先固定 settle，再读一次标题：
    ///   · 读到真实标签标题 → 缩一次；
    ///   · 读不到（页面还没加载完）→ 窗口先保持原样，恢复完成提示照常在 3 秒弹出
    ///     （不再为等待页面而推迟），同时排一个后台补缩：再等 3 秒读一次，
    ///     读到就静默补缩一次，仍读不到就彻底放手（不再提示、不再重试）。
    ///
    /// 页面没就绪不是失败：网页确实打开了，只是没读到标题，不敢替用户缩下去。所以：
    ///   · 只有“保存时是最小化”的窗口才提示（保存为普通/最大化的窗口本来就不缩）；
    ///   · 用户自己关窗或手动最小化走已有的关窗/接管提示，不重复说“未就绪”；
    ///   · 提示文案里的秒数只写在这里，浏览器收尾和完成文案共用同一份，改一处两边一起变。
    /// </summary>
    public static class BrowserWindowNotReadyNotice
    {
        /// <summary>
        /// 最后一页之后先等窗口稳定的时间（毫秒）；等完只检查一次，不再继续等。
        /// 3 秒是 10-25 第 65 批 / 11-04 分类之后一直用的值；11-05 R+ 曾压到 1.5 秒，
        /// 用户 2026-11-06 要求回到 3 秒。
        /// </summary>
        public const int SettleMilliseconds = 3000;

        /// <summary>同上，给提示文案用。改 <see cref="SettleMilliseconds"/> 时必须一起改。</summary>
        public const string SettleSecondsText = "3";

        /// <summary>
        /// 3 秒没读到标题时的后台补缩等待（毫秒）：横幅先弹，后台再等 3 秒读一次；
        /// 读到才补缩一次，读不到就彻底放手。只给代码用，不写进提示文案。
        /// </summary>
        public const int LateRetryMilliseconds = 3000;

        /// <summary>同上，给日志用。改 <see cref="LateRetryMilliseconds"/> 时必须一起改。</summary>
        public const string LateRetrySecondsText = "3";

        /// <summary>横幅/托盘/状态栏共用的一句计数说明（由调用方补前面的分隔符）。</summary>
        public static string Summary(int windowCount)
        {
            if (windowCount <= 0)
            {
                return string.Empty;
            }
            return AppText.F(
                StartupRestoreTextKeys.CaptureNotReadySummaryFormat,
                windowCount,
                SettleSecondsText);
        }

        /// <summary>
        /// “为什么没缩”的说明（横幅第二行 / 状态栏 / 悬浮提示）。
        /// 用户 2026-11-05 要求只保留到“已先保持正常窗口。”为止，
        /// 不再写“没有缩小、没有还原……”和“在任务栏把它最小化即可”。
        /// </summary>
        public static string Advice(int windowCount)
        {
            if (windowCount <= 0)
            {
                return string.Empty;
            }
            return AppText.F(
                StartupRestoreTextKeys.CaptureNotReadyAdviceFormat,
                windowCount,
                SettleSecondsText);
        }
    }
}
