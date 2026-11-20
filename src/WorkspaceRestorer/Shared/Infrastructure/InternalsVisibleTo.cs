using System.Runtime.CompilerServices;
using WorkspaceRestorer.Infrastructure;

// B3 的“关闭浏览器窗口 = 跳过该窗口组剩余网页”需要测试替身直接实现
// 内部恢复接口，才能在不启动真实浏览器、不碰用户窗口的前提下覆盖
// “同组跳过、其他窗口继续”的完整恢复循环。这里只把内部成员开放给
// 同仓库的测试程序集，不改变产品运行时的可见性。
[assembly: InternalsVisibleTo(ProductIdentity.TestAssemblyName)]
