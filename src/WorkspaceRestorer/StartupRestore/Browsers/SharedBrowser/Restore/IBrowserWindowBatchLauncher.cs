using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// Optional restore-only extension implemented by the real launcher.  The
    /// public launcher contract stays small so test doubles and callers that
    /// only launch one item do not need browser-specific methods.
    /// </summary>
    internal interface IBrowserWindowBatchLauncher
    {
        void BeginBrowserRestore();

        /// <summary>
        /// 兼容旧调用方的整窗批量启动入口。B3 的恢复主循环已不再调用：
        /// 浏览器窗口必须逐页启动，用户中途关窗时才不会继续打开剩余网页。
        /// </summary>
        void PrepareBrowserWindowBatch(SessionItem firstPage, IList<SessionItem> pages);

        /// <summary>
        /// 逐页恢复（网页间隔 &gt; 0）时也要登记整窗页面标题：
        /// 第一页必须按标题认领正确的浏览器窗口，不能再用“同 exe 任意窗口”兜底。
        /// </summary>
        void PrepareBrowserWindowTitles(SessionItem firstPage, IList<SessionItem> pages);

        /// <summary>
        /// B3：这个网页所属的浏览器窗口，是否已经被用户关闭。
        /// 只认恢复时认领到的精确 HWND + PID；关闭后同组剩余网页全部跳过，
        /// 不再启动、也不在最后缩窗。其它浏览器窗口不受影响。
        /// </summary>
        bool IsBrowserWindowClosedByUser(SessionItem page);

        /// <summary>
        /// 用户在恢复中手动最小化了这个网页所属的浏览器窗口。
        /// 这是“用户接管”：同组剩余网页全部跳过，不还原、不激活、最后也不缩窗。
        /// 与“用户关窗”分开返回，完成提示才能说清是缩小而不是关闭。
        /// </summary>
        bool IsBrowserWindowUserMinimized(SessionItem page);

        /// <summary>
        /// 这个浏览器窗口的最后一页已经启动完：
        /// 保存为最小化的窗口只在这里缩一次（不在第一页缩，也不逐页反复缩）。
        /// 如果用户已经关闭窗口，这里不会再排缩窗任务。
        /// </summary>
        void CompleteBrowserWindowRestore(SessionItem firstPage, SessionItem lastPage);

        /// <summary>
        /// B1：所有项目启动完后，等已经排队的浏览器缩窗 worker 收尾。
        /// 现在 worker 最多只等 settle 3 秒 + 缩窗复验 0.7 秒；3 秒没读到标题时
        /// 立刻退出（补缩交给后台），所以完成提示不会被补缩拖到 6 秒。
        /// 取消恢复时用 token 立刻打断等待，随后 EndBrowserRestore 让旧 worker 过期。
        /// </summary>
        Task<bool> WaitForBrowserRestoreSettledAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 本次恢复里“保存为最小化的单页浏览器窗口，3 秒内没读到真实标题，
        /// 所以先保持原样、没有缩窗”的窗口数。收尾（WaitForBrowserRestoreSettledAsync）
        /// 之后再读；不是失败，完成提示要用它说明“页面未就绪、窗口保持原样”。
        /// 用户关窗/手动最小化的窗口不计在这里。
        /// </summary>
        int BrowserWindowNotReadyCount { get; }

        /// <summary>
        /// 用户取消恢复、或恢复中途失败时调用：让已排队的“3 秒后台补缩”立刻作废，
        /// 保证中止之后不会再有任何缩窗动作。正常完成不要调用（正常完成要让补缩跑完）。
        /// </summary>
        void AbortBrowserLateMinimize();

        void EndBrowserRestore();
    }
}
