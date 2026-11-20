using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「启动与恢复」的跳转能力，供「自启动应用」转接调用。
    ///
    /// 用户 2026-10-31 原话：
    ///   “自启动应用，点击跳转的逻辑，也要链接启动与恢复……我尝试点击网页跳转的时候，
    ///    居然无法跳转，而是打开了网页（我从启动与恢复复制方案到自启动应用）。”
    ///
    /// 这里**不复制**任何窗口查找/浏览器标签逻辑，只把「自启动应用」适配出来的
    /// `SessionItem` 清单交给本工作区现有的 `JumpToSavedItemWindowCore`：
    ///   · 网页 → `JumpToSavedBrowserTab`（按网址/保存的标签序号/名字精确定位）；
    ///   · 文件夹 → Explorer 路径匹配；
    ///   · 文档 → `DocumentWindowFinder`；
    ///   · 应用 → 进程 exe + 标题；
    ///   · 找不到才 `OpenSavedItem`，并沿用原来的“1.6 秒后再聚焦”。
    ///
    /// 两个工作区仍然是两套数据；这里只是“跳去哪”的只读调用，不写任何方案。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 用一份外部适配好的 `SessionItem` 清单执行“先跳、没有再开”。
        /// 调用方自己保证清单里的父子 Id 已对上；本方法不改数据。
        /// </summary>
        internal void JumpToExternalSessionItem(
            IList<SessionItem> sourceItems,
            SessionItem item)
        {
            JumpToSavedItemWindowCore(sourceItems, item);
        }
    }
}
