using System.Collections.Generic;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 恢复开始前的“用户已打开窗口”登记入口。
    ///
    /// 只登记非浏览器项（应用 / 文件夹 / 文档）。浏览器网页和浏览器窗口
    /// 故意不在这里：浏览器的窗口与网页不是一一对应，关一个窗口不能推断
    /// 整组网页不要，必须保持现有浏览器恢复逻辑。
    ///
    /// 这个接口是可选扩展，测试替身和只做单项启动的调用方不需要实现。
    /// </summary>
    internal interface IRestoreItemTrackingLauncher
    {
        void BeginRestoreItemTracking(IList<SessionItem> items);

        void EndRestoreItemTracking();
    }
}
