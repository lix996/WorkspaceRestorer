using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WorkspaceRestorer.Applications.Restore;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {

        private static void RunRestoreAndCaptureTests()
        {
            Run("资源管理器父项只作为文件夹分组展示", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem explorer = new SessionItem
                {
                    Id = "explorer-window-one",
                    Sequence = 1,
                    Kind = SessionItemKind.Application,
                    DisplayName = "文件资源管理器 (1)",
                    ExecutablePath = "C:\\Windows\\explorer.exe",
                    ApplicationWindowIndex = 0,
                    CaptureSource = "ExplorerWindow"
                };
                snapshot.Items.Add(explorer);
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 2,
                    Kind = SessionItemKind.ExplorerFolder,
                    DisplayName = "项目",
                    TargetPath = "C:\\Work\\Project",
                    ExecutablePath = explorer.ExecutablePath,
                    ApplicationWindowIndex = 0,
                    ParentId = explorer.Id,
                    CaptureSource = "ExplorerFolderEntry"
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 3,
                    Kind = SessionItemKind.ExplorerFolder,
                    DisplayName = "文档",
                    TargetPath = "C:\\Work\\Docs",
                    ExecutablePath = explorer.ExecutablePath,
                    ApplicationWindowIndex = 0,
                    ParentId = explorer.Id,
                    CaptureSource = "ExplorerFolderEntry"
                });
                RestoreResult result = service.RestoreAsync(
                    snapshot,
                    new RestoreOptions { InitialDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Items.Count == 2, "资源管理器父项没有被文件夹子项替代。");
                Assert(launcher.Items.Count == 2, "恢复时额外启动了空白资源管理器父项。");
                Assert(launcher.Items.All(delegate(SessionItem item) { return item.Kind == SessionItemKind.ExplorerFolder; }), "恢复列表包含资源管理器父项。");
            });

            Run("资源管理器父项停用时跳过全部文件夹", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem explorer = new SessionItem
                {
                    Id = "disabled-explorer-window",
                    Kind = SessionItemKind.Application,
                    IsEnabled = false,
                    ExecutablePath = "C:\\Windows\\explorer.exe",
                    ApplicationWindowIndex = 0,
                    CaptureSource = "ExplorerWindow"
                };
                snapshot.Items.Add(explorer);
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.ExplorerFolder,
                    IsEnabled = true,
                    ParentId = explorer.Id,
                    TargetPath = "C:\\Work\\Project",
                    ExecutablePath = explorer.ExecutablePath,
                    ApplicationWindowIndex = 0,
                    CaptureSource = "ExplorerFolderEntry"
                });
                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions { InitialDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(launcher.Items.Count == 0, "停用资源管理器父项后文件夹仍被恢复。");
            });

            Run("浏览器按窗口分组并分别恢复首个已选网页", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem firstWindow = new SessionItem
                {
                    Id = "edge-window-one",
                    Sequence = 1,
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge (1)",
                    ExecutablePath = "C:\\Browsers\\msedge.exe",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 0,
                    WindowPlacement = new SavedWindowPlacement { HasBounds = true, Left = 20, Top = 30 }
                };
                SessionItem secondWindow = new SessionItem
                {
                    Id = "edge-window-two",
                    Sequence = 2,
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge (2)",
                    ExecutablePath = firstWindow.ExecutablePath,
                    BrowserGroupKey = firstWindow.BrowserGroupKey,
                    BrowserWindowIndex = 1,
                    WindowPlacement = new SavedWindowPlacement { HasBounds = true, Left = 980, Top = 40 }
                };
                snapshot.Items.Add(firstWindow);
                snapshot.Items.Add(secondWindow);
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 3,
                    Kind = SessionItemKind.WebPage,
                    Url = "https://chatgptplus.cn/#/",
                    ParentId = firstWindow.Id,
                    BrowserExecutablePath = firstWindow.ExecutablePath,
                    BrowserGroupKey = firstWindow.BrowserGroupKey,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 0
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 4,
                    Kind = SessionItemKind.WebPage,
                    Url = "https://rawchat.cn/",
                    ParentId = secondWindow.Id,
                    BrowserExecutablePath = secondWindow.ExecutablePath,
                    BrowserGroupKey = secondWindow.BrowserGroupKey,
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 3
                });

                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions { InitialDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(launcher.Items.Count == 2, "两个浏览器窗口的父项没有被各自网页替代。" );
                Assert(launcher.Items.All(delegate(SessionItem item) { return item.StartsBrowserWindow; }), "每个浏览器窗口的第一个已选网页都应新建窗口。" );
                Assert(launcher.Items[0].BrowserWindowIndex == 0 && launcher.Items[1].BrowserWindowIndex == 1, "浏览器窗口编号没有按分组保留。" );
                Assert(launcher.Items[1].WindowPlacement != null && launcher.Items[1].WindowPlacement.Left == 980, "第二个浏览器窗口的位置没有传给首个已选网页。" );
            });

            Run("同一浏览器窗口恢复时只把第一页当窗口起点（2026-10-25）", delegate
            {
                // 用户现场：两个 Edge 窗口，分别 8 页和 6 页，恢复成了十几个
                // 单页浏览器窗口。父窗口位置会被复制给每一个网页；旧逻辑把
                // “有位置”误当成“这是窗口第一页”，于是每页都 --new-window。
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem parent = new SessionItem
                {
                    Id = "edge-window-many-tabs",
                    Sequence = 1,
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge",
                    ExecutablePath = "C:\\Browsers\\msedge.exe",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 0,
                    WindowPlacement = new SavedWindowPlacement { HasBounds = true, Left = 10, Top = 20 }
                };
                snapshot.Items.Add(parent);
                for (int index = 0; index < 3; index++)
                {
                    snapshot.Items.Add(new SessionItem
                    {
                        Sequence = 2 + index,
                        Kind = SessionItemKind.WebPage,
                        DisplayName = "标签" + index,
                        Url = "https://example.test/tab" + index,
                        ParentId = parent.Id,
                        BrowserGroupKey = parent.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = index
                    });
                }

                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions
                    {
                        InitialDelay = TimeSpan.Zero,
                        ItemDelay = TimeSpan.Zero,
                        WebPageDelay = TimeSpan.Zero,
                        WaitForAcceptableLoad = false
                    },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual(3, launcher.Items.Count);
                Assert(launcher.Items[0].StartsBrowserWindow, "同一个窗口的第一个标签应负责新建窗口。" );
                Assert(!launcher.Items[1].StartsBrowserWindow && !launcher.Items[2].StartsBrowserWindow,
                    "同一个窗口的后续标签不能再被当成窗口起点。" );
                Assert(launcher.Items[1].WindowPlacement != null,
                    "后续标签仍应继承父窗口位置（只有第一页能用它新建窗口）。" );
            });

            Run("恢复期只有浏览器窗口第一页允许新建窗口", delegate
            {
                Type processLauncher = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher", true);
                MethodInfo shouldStart = processLauncher == null
                    ? null
                    : processLauncher.GetMethod(
                        "ShouldStartNewBrowserWindow",
                        BindingFlags.NonPublic | BindingFlags.Static);
                Assert(shouldStart != null, "找不到 ProcessLauncher.ShouldStartNewBrowserWindow。" );
                Func<SessionItem, bool, bool> decide = delegate(SessionItem item, bool restoring)
                {
                    return (bool)shouldStart.Invoke(null, new object[] { item, restoring });
                };

                SessionItem laterTab = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 1,
                    WindowPlacement = new SavedWindowPlacement { HasBounds = true, Left = 10, Top = 20 }
                };
                Assert(!decide(laterTab, true),
                    "恢复时同一窗口的第 2 个标签仍然被判定为新建窗口（会恢复成满屏浏览器）。" );
                Assert(decide(laterTab, false),
                    "手工单独打开网页的旧行为被误改：单页仍应新建窗口。" );

                SessionItem firstTab = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    StartsBrowserWindow = true,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 5,
                    WindowPlacement = new SavedWindowPlacement { HasBounds = true }
                };
                Assert(decide(firstTab, true),
                    "被标记为窗口起点的标签（即使标签序号不是 0）必须新建窗口。" );
            });

            Run("交错浏览器窗口仍严格按保存 Sequence 恢复", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 1,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "窗口一-标签一",
                    Url = "https://example.test/window-one/one",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 0
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 2,
                    Kind = SessionItemKind.Application,
                    DisplayName = "中间应用",
                    ExecutablePath = "C:\\missing-middle.exe"
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 3,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "窗口二-标签一",
                    Url = "https://example.test/window-two/one",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 0
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 4,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "窗口一-标签二",
                    Url = "https://example.test/window-one/two",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 1
                });

                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions { InitialDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(launcher.Items.Count == 4, "交错浏览器窗口恢复项目数量不正确。" );
                Assert(launcher.Items[0].DisplayName == "窗口一-标签一", "第一个网页没有按 Sequence 恢复。" );
                Assert(launcher.Items[1].DisplayName == "中间应用", "中间应用被浏览器窗口分组逻辑提前或延后。" );
                Assert(launcher.Items[2].DisplayName == "窗口二-标签一", "第二个浏览器窗口没有按 Sequence 恢复。" );
                Assert(launcher.Items[3].DisplayName == "窗口一-标签二", "同窗口第二个网页没有保留全局 Sequence。" );
            });

            Run("停用浏览器窗口会跳过其全部网页", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem parent = new SessionItem
                {
                    Id = "disabled-edge-window",
                    Kind = SessionItemKind.Application,
                    IsEnabled = false,
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                    BrowserWindowIndex = 0
                };
                snapshot.Items.Add(parent);
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    IsEnabled = true,
                    ParentId = parent.Id,
                    BrowserGroupKey = parent.BrowserGroupKey,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 0,
                    Url = "https://example.test/disabled"
                });
                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions { InitialDelay = TimeSpan.Zero, ItemDelay = TimeSpan.Zero },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(launcher.Items.Count == 0, "停用浏览器窗口后其网页仍被恢复。" );
            });

            Run("后台保存禁止启动并禁止切换浏览器标签", delegate
            {
                OptionRecordingCaptureService capture = new OptionRecordingCaptureService();
                RecordingRestoreService restore = new RecordingRestoreService();
                TestDesktopApplicationService desktop = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(capture, restore, desktop, null, null);
                try
                {
                    controller.CaptureBackgroundSnapshot();
                    // The background snapshot runs on a worker, so wait briefly
                    // for it to be recorded before asserting.
                    DateTime waitUntil = DateTime.UtcNow.AddSeconds(3);
                    while (capture.OptionsCaptureCount == 0 && DateTime.UtcNow < waitUntil)
                    {
                        Thread.Sleep(25);
                    }
                    Assert(capture.OptionsCaptureCount == 1, "后台保存没有走被动捕获入口。" );
                    Assert(capture.LastOptions != null && !capture.LastOptions.IncludeBrowserPages, "后台保存仍在扫描浏览器网页。" );
                    Assert(!capture.LastOptions.AllowBrowserTabActivation, "后台保存仍允许切换浏览器标签。" );
                    Assert(restore.CallCount == 0, "后台保存错误触发了应用、网页、文件或文件夹恢复。" );
                    Assert(desktop.LaunchCallCount == 0, "后台保存错误打开了桌面项目。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("网页标签激活只由显式保存开启", delegate
            {
                OptionRecordingCaptureService capture = new OptionRecordingCaptureService();
                RecordingRestoreService restore = new RecordingRestoreService();
                TestDesktopApplicationService desktop = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(capture, restore, desktop, null, null);
                try
                {
                    SessionCaptureOptions defaults = new SessionCaptureOptions();
                    Assert(!defaults.AllowBrowserTabActivation, "普通捕获默认不应激活浏览器标签。");
                    controller.CaptureSnapshot();
                    Assert(capture.OptionsCaptureCount == 1, "显式保存没有使用带选项的捕获入口。");
                    Assert(capture.LastOptions != null && capture.LastOptions.IncludeBrowserPages, "显式保存没有读取网页项目。");
                    Assert(capture.LastOptions.AllowBrowserTabActivation, "显式保存没有明确开启网页标签读取。");
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("异步捕获不会阻塞界面且禁止并发保存", delegate
            {
                BlockingCaptureService capture = new BlockingCaptureService();
                RecordingRestoreService restore = new RecordingRestoreService();
                TestDesktopApplicationService desktop = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(capture, restore, desktop, null, null);
                StartupProfile createdProfile = null;
                Task<SessionSnapshot> first = null;
                try
                {
                    createdProfile = controller.CreateProfile("异步阻塞测试");
                    DateTime startedAt = DateTime.UtcNow;
                    first = controller.CaptureSnapshotAsync();
                    TimeSpan callDuration = DateTime.UtcNow - startedAt;
                    Assert(callDuration < TimeSpan.FromSeconds(1), "异步捕获调用在启动工作线程时阻塞过久。" );
                    Assert(capture.Entered.WaitOne(2000), "异步捕获没有在独立工作线程中开始。" );
                    Assert(!first.IsCompleted, "捕获服务阻塞时异步任务不应提前完成。" );
                    Assert(capture.LastOptions != null && capture.LastOptions.IncludeBrowserPages,
                        "异步显式捕获没有启用网页项目读取。" );
                    Assert(capture.LastOptions.AllowBrowserTabActivation,
                        "异步显式捕获没有传递网页标签读取选项。" );

                    Task<SessionSnapshot> second = controller.CaptureSnapshotAsync();
                    Assert(second.IsCompleted, "已有捕获进行时，第二次调用不应再次排队。" );
                    Assert(second.GetAwaiter().GetResult() == null, "并发捕获调用应返回空结果。" );
                    Assert(controller.StatusText.IndexOf("保存任务正在进行", StringComparison.OrdinalIgnoreCase) >= 0,
                        "并发捕获没有给出正在进行的状态提示。" );

                    capture.Release();
                    Assert(first.Wait(3000), "释放捕获服务后异步任务没有完成。" );
                    Assert(first.GetAwaiter().GetResult() != null, "异步捕获成功后没有返回会话快照。" );
                    Assert(restore.CallCount == 0 && desktop.LaunchCallCount == 0,
                        "异步捕获不应触发恢复或桌面项目启动。" );
                }
                finally
                {
                    capture.Release();
                    // The worker is deliberately allowed to finish before
                    // deleting the profile.  Otherwise its late continuation
                    // can race cleanup and write into a profile that the test
                    // is in the process of removing.
                    if (first != null)
                    {
                        try
                        {
                            first.Wait(3000);
                        }
                        catch
                        {
                            // The assertion path above reports an unfinished
                            // worker; cleanup must still release its gate.
                        }
                    }
                    if (createdProfile != null)
                    {
                        controller.DeleteProfile(createdProfile);
                    }
                    controller.Dispose();
                    capture.Dispose();
                }
            });

            Run("异步捕获失败不会抛出且超时设置有界", delegate
            {
                ThrowingCaptureService capture = new ThrowingCaptureService();
                RecordingRestoreService restore = new RecordingRestoreService();
                TestDesktopApplicationService desktop = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(capture, restore, desktop, null, null);
                try
                {
                    Task<SessionSnapshot> operation = controller.CaptureSnapshotAsync();
                    Assert(operation.Wait(3000), "捕获服务异常时异步任务没有在限定时间内结束。" );
                    Assert(operation.GetAwaiter().GetResult() == null, "捕获异常时不应提交空会话。" );
                    Assert(capture.OptionsCaptureCount == 1, "捕获异常没有经过异步工作线程。" );
                    Assert(controller.StatusText.IndexOf("无法读取当前窗口", StringComparison.OrdinalIgnoreCase) >= 0,
                        "捕获异常没有更新失败状态。" );
                    Assert(restore.CallCount == 0 && desktop.LaunchCallCount == 0,
                        "捕获异常不应触发恢复或桌面项目启动。" );

                    Type controllerType = typeof(ApplicationController);
                    FieldInfo timeoutField = controllerType.GetField(
                        "InteractiveCaptureTimeoutMilliseconds",
                        BindingFlags.NonPublic | BindingFlags.Static);
                    Assert(timeoutField != null && timeoutField.IsLiteral,
                        "异步捕获缺少固定超时边界。" );
                    object rawTimeout = timeoutField.GetRawConstantValue();
                    Assert(rawTimeout is int, "异步捕获超时边界必须是整数毫秒。" );
                    int timeout = (int)rawTimeout;
                    // Still bounded, but the upper bound has to allow reading a
                    // session whose browser windows hold twenty-odd tabs each:
                    // without a debug port every tab is read one at a time, which
                    // needs far more than the old 30 s ceiling.
                    Assert(timeout >= 1000 && timeout <= 300000,
                        "异步捕获超时应处于 1 秒至 5 分钟的有界范围内。" );
                    MethodInfo asyncCapture = controllerType.GetMethod(
                        "CaptureSnapshotAsync",
                        BindingFlags.Public | BindingFlags.Instance);
                    Assert(asyncCapture != null
                        && asyncCapture.ReturnType == typeof(Task<SessionSnapshot>),
                        "异步捕获 API 必须返回 Task<SessionSnapshot>。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("异步保存会提交捕获结果到工作方案", delegate
            {
                SessionSnapshot expected = new SessionSnapshot();
                expected.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "异步保存项目",
                    ExecutablePath = "C:\\async-save.exe"
                });
                ResultCaptureService capture = new ResultCaptureService(expected);
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                StartupProfile profile = null;
                try
                {
                    profile = controller.CreateProfile("异步保存测试");
                    Task<SessionSnapshot> operation = controller.SaveCurrentWorkToProfileAsync(profile);
                    Assert(operation.Wait(3000), "异步工作方案保存没有在限定时间内完成。" );
                    SessionSnapshot result = operation.GetAwaiter().GetResult();
                    Assert(result != null && result.Items != null && result.Items.Count == 1,
                        "异步保存没有返回捕获到的项目。" );
                    Assert(profile.Items != null && profile.Items.Count == 1
                        && profile.Items[0].DisplayName == "异步保存项目",
                        "异步保存没有将捕获结果提交到工作方案。" );
                    Assert(capture.LastOptions != null && capture.LastOptions.AllowBrowserTabActivation,
                        "异步保存没有使用显式保存的捕获选项。" );
                }
                finally
                {
                    if (profile != null)
                    {
                        controller.DeleteProfile(profile);
                    }
                    controller.Dispose();
                }
            });

            Run("桌面布局超时不会迟到写入且并发门可复用", delegate
            {
                BlockingLayoutCoordinator layout = new BlockingLayoutCoordinator();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    layout);
                Task<DesktopShellSnapshot> timedOut = null;
                try
                {
                    MethodInfo captureMethod = typeof(ApplicationController).GetMethod(
                        "CaptureDesktopLayoutAsync",
                        BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(bool), typeof(int) },
                        null);
                    Assert(captureMethod != null, "缺少可测试的异步桌面布局捕获入口。");

                    timedOut = (Task<DesktopShellSnapshot>)captureMethod.Invoke(
                        controller,
                        new object[] { false, 500 });
                    Assert(layout.Entered.WaitOne(1500), "桌面布局 worker 没有进入 fake 捕获器。");
                    Assert(timedOut.Wait(2000), "桌面布局超时任务没有及时结束。");
                    Assert(timedOut.GetAwaiter().GetResult() == null, "超时桌面布局不应返回快照。");
                    AssertEqual(0, layout.SaveCount);

                    // Let the abandoned worker finish.  Its late result must
                    // remain read-only and never reach SaveSnapshot.
                    layout.Release();
                    Assert(layout.Completed.WaitOne(2000), "迟到桌面布局 worker 没有完成。");
                    AssertEqual(0, layout.SaveCount);

                    Task<DesktopShellSnapshot> retry = (Task<DesktopShellSnapshot>)captureMethod.Invoke(
                        controller,
                        new object[] { false, 1000 });
                    Assert(retry.Wait(2000), "桌面布局并发门释放后无法重试。");
                    Assert(retry.GetAwaiter().GetResult() != null, "第二次桌面布局捕获没有返回快照。");
                    AssertEqual(1, layout.SaveCount);
                }
                finally
                {
                    layout.Release();
                    if (timedOut != null)
                    {
                        try { timedOut.Wait(2000); } catch { }
                    }
                    controller.Dispose();
                    layout.Dispose();
                }
            });

            Run("控制器释放会取消捕获辅助进程", delegate
            {
                Type processType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.SessionCaptureProcess",
                    true);
                FieldInfo versionField = processType.GetField(
                    "cancellationVersion",
                    BindingFlags.NonPublic | BindingFlags.Static);
                PropertyInfo countProperty = processType.GetProperty(
                    "ActiveCaptureCount",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(versionField != null, "捕获辅助进程缺少退出取消版本。");
                Assert(countProperty != null, "捕获辅助进程缺少活动计数。");

                long before = (long)versionField.GetValue(null);
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    controller.Dispose();
                }
                finally
                {
                    controller.Dispose();
                }

                long after = (long)versionField.GetValue(null);
                Assert(after > before, "控制器释放没有使捕获取消版本递增。");
                AssertEqual(0, (int)countProperty.GetValue(null, null));
            });

            Run("新建工作方案捕获不会改动当前方案", delegate
            {
                SessionSnapshot expected = new SessionSnapshot();
                expected.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "新方案捕获项目",
                    ExecutablePath = "C:\\new-profile-capture.exe"
                });
                ResultCaptureService capture = new ResultCaptureService(expected);
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                StartupProfile current = null;
                Task<SessionSnapshot> operation = null;
                try
                {
                    int profileCountBefore = controller.Profiles.Count;
                    current = controller.CreateProfile("当前方案隔离-" + Guid.NewGuid().ToString("N"));
                    SessionItem existing = new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        DisplayName = "当前方案原有项目",
                        ExecutablePath = "C:\\existing-profile-item.exe"
                    };
                    current.ReplaceItems(new SessionItem[] { existing });
                    controller.SetActiveProfile(current);
                    string activeIdBefore = controller.ActiveProfile == null ? null : controller.ActiveProfile.Id;
                    string lastItemBefore = controller.LastSnapshot == null
                        || controller.LastSnapshot.Items == null
                        || controller.LastSnapshot.Items.Count == 0
                        ? null
                        : controller.LastSnapshot.Items[0].DisplayName;

                    operation = controller.CaptureSnapshotForNewProfileAsync();
                    Assert(operation.Wait(3000), "新建工作方案捕获没有在限定时间内完成。" );
                    SessionSnapshot result = operation.GetAwaiter().GetResult();
                    Assert(result != null && result.Items != null && result.Items.Count == 1,
                        "新建工作方案捕获没有返回扫描结果。" );
                    Assert(controller.Profiles.Count == profileCountBefore + 1,
                        "仅捕获新方案时不应悄悄创建额外工作方案。" );
                    Assert(controller.ActiveProfile != null
                        && string.Equals(controller.ActiveProfile.Id, activeIdBefore, StringComparison.OrdinalIgnoreCase),
                        "新建工作方案捕获改变了当前活动方案。" );
                    Assert(current.Items != null && current.Items.Count == 1
                        && string.Equals(current.Items[0].DisplayName, "当前方案原有项目", StringComparison.Ordinal),
                        "新建工作方案捕获覆盖了当前方案清单。" );
                    Assert(controller.LastSnapshot != null && controller.LastSnapshot.Items != null
                        && controller.LastSnapshot.Items.Count == 1
                        && string.Equals(controller.LastSnapshot.Items[0].DisplayName, lastItemBefore, StringComparison.Ordinal),
                        "新建工作方案捕获更新了当前会话快照。" );
                }
                finally
                {
                    if (operation != null)
                    {
                        try { operation.Wait(3000); } catch { }
                    }
                    if (current != null)
                    {
                        controller.DeleteProfile(current);
                    }
                    controller.Dispose();
                }
            });

            Run("迟到捕获结果不提交且并发门可复用", delegate
            {
                BlockingCaptureService capture = new BlockingCaptureService();
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                StartupProfile profile = null;
                Task<SessionSnapshot> operation = null;
                bool controllerDisposed = false;
                try
                {
                    profile = controller.CreateProfile("迟到捕获隔离-" + Guid.NewGuid().ToString("N"));
                    SessionItem existing = new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        DisplayName = "原有项目",
                        ExecutablePath = "C:\\late-result-existing.exe"
                    };
                    profile.ReplaceItems(new SessionItem[] { existing });
                    controller.SetActiveProfile(profile);
                    operation = controller.CaptureSnapshotAsync();
                    Assert(capture.Entered.WaitOne(2000), "阻塞捕获没有启动。" );

                    // Disposing while the provider is blocked models a
                    // timed-out/abandoned worker.  Its eventual result must
                    // be ignored, while the worker's finally block releases
                    // the gate for future instances.
                    controller.Dispose();
                    controllerDisposed = true;
                    capture.Release();
                    Assert(operation.Wait(3000), "释放迟到捕获后任务没有收尾。" );
                    Assert(operation.GetAwaiter().GetResult() == null,
                        "控制器已释放后迟到捕获结果不应返回或提交。" );
                    Assert(profile.Items != null && profile.Items.Count == 1
                        && string.Equals(profile.Items[0].DisplayName, "原有项目", StringComparison.Ordinal),
                        "迟到捕获结果覆盖了已有工作方案内容。" );

                    FieldInfo gateField = typeof(ApplicationController).GetField(
                        "sessionCaptureInProgress",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert(gateField != null, "异步捕获缺少并发门字段。" );
                    AssertEqual(0, (int)gateField.GetValue(controller));
                }
                finally
                {
                    capture.Release();
                    if (operation != null)
                    {
                        try { operation.Wait(3000); } catch { }
                    }
                    // The controller was disposed above to exercise the late
                    // result guard.  Reload a short-lived controller only for
                    // removing the persisted test profile; the disposed
                    // instance must never receive a late mutation.
                    if (profile != null && controllerDisposed)
                    {
                        ApplicationController cleanupController = new ApplicationController(
                            new TestCaptureService(),
                            new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                            new TestDesktopApplicationService(),
                            null,
                            null);
                        try
                        {
                            StartupProfile persisted = cleanupController.Profiles.FirstOrDefault(delegate(StartupProfile item)
                            {
                                return item != null && string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
                            });
                            if (persisted != null)
                            {
                                cleanupController.DeleteProfile(persisted);
                            }
                        }
                        finally
                        {
                            cleanupController.Dispose();
                        }
                    }
                    else if (profile != null)
                    {
                        controller.DeleteProfile(profile);
                    }
                    controller.Dispose();
                    capture.Dispose();
                }
            });

            Run("网页捕获不完整时保留旧网页清单", delegate
            {
                MutableCaptureService capture = CreateBrowserCaptureService(false);
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                StartupProfile profile = null;
                try
                {
                    profile = controller.CreateProfile("网页不完整隔离-" + Guid.NewGuid().ToString("N"));
                    controller.SetActiveProfile(profile);
                    SessionSnapshot initial = controller.CaptureSnapshot();
                    Assert(initial != null && initial.Items.Any(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.WebPage
                            && string.Equals(item.Url, "https://example.test/project", StringComparison.OrdinalIgnoreCase);
                    }), "测试前置网页没有保存。" );

                    // Simulate a baseline-only capture from the isolated
                    // worker: the browser parent is visible, but URL reads
                    // were incomplete.
                    capture.Items.RemoveAll(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.WebPage;
                    });
                    capture.BrowserCaptureIncomplete = true;
                    capture.CaptureWarning = "测试：网页读取不完整";
                    SessionSnapshot recaptured = controller.CaptureSnapshot();
                    Assert(recaptured != null && recaptured.BrowserCaptureIncomplete,
                        "不完整网页捕获没有保留标记。" );
                    Assert(recaptured.Items.Any(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.WebPage
                            && string.Equals(item.Url, "https://example.test/project", StringComparison.OrdinalIgnoreCase);
                    }), "网页捕获不完整时丢失了上次保存的网页。" );
                    Assert(controller.StatusText.IndexOf("网页读取不完整", StringComparison.OrdinalIgnoreCase) >= 0,
                        "网页捕获不完整时没有提示用户。" );
                }
                finally
                {
                    if (profile != null)
                    {
                        controller.DeleteProfile(profile);
                    }
                    controller.Dispose();
                }
            });

            Run("布局仓库支持单屏回退与拓扑并集", delegate
            {
                string dataDirectory = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DATA_DIR");
                string layoutPath = Path.Combine(dataDirectory, "layout-test.xml");
                DesktopLayoutRepository repository = new DesktopLayoutRepository(layoutPath);
                DesktopLayoutCoordinator coordinator = new DesktopLayoutCoordinator(new DesktopIconService(), repository);
                DisplayTopology laptop = CreateTopology("LAPTOP", true, 1366, 768);
                DesktopShellSnapshot laptopSnapshot = new DesktopShellSnapshot { Topology = laptop };
                laptopSnapshot.Icons.Add(new DesktopIconPlacement { Identity = "path:C:\\A.LNK", DisplayName = "A", MonitorKey = "LAPTOP", X = 20, Y = 20, MonitorOffsetX = 20, MonitorOffsetY = 20, NormalizedX = 0.02, NormalizedY = 0.03 });
                coordinator.SaveSnapshot(laptopSnapshot);

                DisplayTopology docked = CreateTopology("LAPTOP", true, 1366, 768);
                docked.Monitors.Add(new DisplayMonitor { StableKey = "EXTERNAL", IsPrimary = false, Bounds = new DesktopRect(1366, 0, 2560, 1440), WorkingArea = new DesktopRect(1366, 0, 2560, 1400) });
                docked.Signature = "DOCKED";
                DesktopShellSnapshot dockedSnapshot = new DesktopShellSnapshot { Topology = docked };
                dockedSnapshot.Icons.Add(new DesktopIconPlacement { Identity = "path:C:\\A.LNK", DisplayName = "A", MonitorKey = "LAPTOP", X = 20, Y = 20, MonitorOffsetX = 20, MonitorOffsetY = 20, NormalizedX = 0.02, NormalizedY = 0.03 });
                dockedSnapshot.Icons.Add(new DesktopIconPlacement { Identity = "path:C:\\NEW.LNK", DisplayName = "New", MonitorKey = "EXTERNAL", X = 1450, Y = 30, MonitorOffsetX = 84, MonitorOffsetY = 30, NormalizedX = 0.03, NormalizedY = 0.02 });
                coordinator.SaveSnapshot(dockedSnapshot);

                DesktopRestorePlan plan = coordinator.BuildRestorePlan(laptop);
                Assert(!plan.UsedPortableFallback, "有笔记本精确布局时不应再走 portable 兜底。");
                Assert(plan.Items.Count == 2, "外接模式新增图标没有并入单屏布局。");
                DesktopRestoreItem newItem = plan.Items.First(delegate(DesktopRestoreItem item) { return item.Identity.EndsWith("NEW.LNK", StringComparison.OrdinalIgnoreCase); });
                Assert(newItem != null && newItem.X >= laptop.GetPrimaryMonitor().WorkingArea.Left && newItem.X < laptop.GetPrimaryMonitor().WorkingArea.Right, "新增图标没有落在笔记本可见工作区。");
                DesktopLayoutCatalog catalog = repository.Load();
                Assert(catalog.PortableSingleDisplayLayout != null && catalog.PortableSingleDisplayLayout.Icons.Count == 2, "便携布局图标集合没有保存并集。");
                DesktopTopologyLayout indexedPortable = catalog.Layouts.FirstOrDefault(delegate(DesktopTopologyLayout item)
                {
                    DisplayMonitor primary = item == null || item.Topology == null ? null : item.Topology.GetPrimaryMonitor();
                    return primary != null && string.Equals(primary.StableKey, "LAPTOP", StringComparison.OrdinalIgnoreCase);
                });
                Assert(indexedPortable != null && indexedPortable.Icons.Count == 2, "Layouts 中的单屏索引没有同步便携布局。");

                // 2026-10-20 用户场景：笔记本单屏 ↔ 外接显示器单屏，两个都是“单屏”，
                // 必须按各自拓扑签名恢复，不能被 portable 槽位互相覆盖。
                DisplayTopology external = CreateTopology("EXTERNAL", true, 3840, 2160);
                DesktopShellSnapshot externalSnapshot = new DesktopShellSnapshot { Topology = external };
                externalSnapshot.Icons.Add(new DesktopIconPlacement
                {
                    Identity = "path:C:\\A.LNK", DisplayName = "A", MonitorKey = "EXTERNAL",
                    X = 1000, Y = 30, MonitorOffsetX = 1000, MonitorOffsetY = 30,
                    NormalizedX = 1000.0 / 3840.0, NormalizedY = 30.0 / 2160.0
                });
                coordinator.SaveSnapshot(externalSnapshot);
                DesktopRestorePlan laptopAgain = coordinator.BuildRestorePlan(laptop);
                DesktopRestoreItem laptopA = laptopAgain.Items.First(delegate(DesktopRestoreItem item) { return item.Identity.EndsWith("A.LNK", StringComparison.OrdinalIgnoreCase); });
                Assert(!laptopAgain.UsedPortableFallback && string.Equals(laptopAgain.SourceTopologySignature, laptop.Signature, StringComparison.OrdinalIgnoreCase), "切回笔记本屏时没有用笔记本拓扑的精确布局。");
                Assert(laptopA != null && laptopA.X < 500 && laptopA.Y < 200, "切回笔记本屏后恢复成了外接显示器的位置。");
                DesktopRestorePlan externalPlan = coordinator.BuildRestorePlan(external);
                DesktopRestoreItem externalA = externalPlan.Items.First(delegate(DesktopRestoreItem item) { return item.Identity.EndsWith("A.LNK", StringComparison.OrdinalIgnoreCase); });
                Assert(!externalPlan.UsedPortableFallback && string.Equals(externalPlan.SourceTopologySignature, external.Signature, StringComparison.OrdinalIgnoreCase), "切回外接显示器时没有用外接拓扑的精确布局。");
                Assert(externalA != null && externalA.X >= 1000 && externalA.Y >= 20, "切回外接显示器后没有恢复外接显示器自己的位置。");                // 2026-10-20 用户问题：在小屏上新加图标，切回大屏还在不在？
                // 保存小屏时把新身份并进所有布局，大屏布局恢复计划里必须有它。
                DesktopShellSnapshot smallNewSnapshot = new DesktopShellSnapshot { Topology = laptop };
                smallNewSnapshot.Icons.Add(new DesktopIconPlacement { Identity = "path:C:\\A.LNK", DisplayName = "A", MonitorKey = "LAPTOP", X = 20, Y = 20, MonitorOffsetX = 20, MonitorOffsetY = 20, NormalizedX = 0.02, NormalizedY = 0.03 });
                smallNewSnapshot.Icons.Add(new DesktopIconPlacement { Identity = "path:C:\\SMALLNEW.LNK", DisplayName = "SmallNew", MonitorKey = "LAPTOP", X = 120, Y = 120, MonitorOffsetX = 120, MonitorOffsetY = 120, NormalizedX = 0.09, NormalizedY = 0.16 });
                coordinator.SaveSnapshot(smallNewSnapshot);
                DesktopRestorePlan externalAfterSmallNew = coordinator.BuildRestorePlan(external);
                DesktopRestoreItem smallNewOnLarge = externalAfterSmallNew.Items.FirstOrDefault(delegate(DesktopRestoreItem item) { return item != null && item.Identity.EndsWith("SMALLNEW.LNK", StringComparison.OrdinalIgnoreCase); });
                Assert(smallNewOnLarge != null, "小屏上新加的图标没有并进大屏布局，切回大屏会看不到。");

            });

            Run("布局自动并集图标仍归入新图标", delegate
            {
                string dataDirectory = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DATA_DIR");
                string layoutPath = Path.Combine(dataDirectory,
                    "layout-auto-merge-test-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    DesktopLayoutRepository repository = new DesktopLayoutRepository(layoutPath);
                    DesktopLayoutCoordinator coordinator = new DesktopLayoutCoordinator(
                        new DesktopIconService(), repository);
                    DisplayTopology laptop = CreateTopology("LAPTOP", true, 1366, 768);
                    DesktopShellSnapshot firstSnapshot = new DesktopShellSnapshot { Topology = laptop };
                    firstSnapshot.Icons.Add(new DesktopIconPlacement
                    {
                        Identity = "name:A.TXT", DisplayName = "A.txt", MonitorKey = "LAPTOP",
                        X = 20, Y = 20, MonitorOffsetX = 20, MonitorOffsetY = 20
                    });
                    coordinator.SaveSnapshotAs(firstSnapshot, "named-a", "工作布局 A");

                    DesktopShellSnapshot secondSnapshot = new DesktopShellSnapshot { Topology = laptop };
                    secondSnapshot.Icons.Add(new DesktopIconPlacement
                    {
                        Identity = "name:A.TXT", DisplayName = "A.txt", MonitorKey = "LAPTOP",
                        X = 20, Y = 20, MonitorOffsetX = 20, MonitorOffsetY = 20
                    });
                    secondSnapshot.Icons.Add(new DesktopIconPlacement
                    {
                        Identity = "name:NEW.TXT", DisplayName = "新文件.txt", MonitorKey = "LAPTOP",
                        X = 100, Y = 100, MonitorOffsetX = 100, MonitorOffsetY = 100
                    });
                    coordinator.SaveSnapshotAs(secondSnapshot, "named-b", "工作布局 B");

                    DesktopLayoutCatalog catalog = repository.Load();
                    DesktopTopologyLayout layoutA = DesktopLayoutRepository.FindById(catalog, "named-a");
                    DesktopTopologyLayout layoutB = DesktopLayoutRepository.FindById(catalog, "named-b");
                    Assert(layoutA != null && layoutA.Icons.Any(delegate(DesktopIconPlacement item)
                    {
                        return item != null && item.Identity == "name:NEW.TXT";
                    }), "另一份布局保存的新文件没有并进这份布局，切回显示器时会找不到。");
                    Assert(layoutA.AutoMergedIdentities != null
                        && layoutA.AutoMergedIdentities.Any(delegate(string identity)
                        {
                            return string.Equals(identity, "name:NEW.TXT", StringComparison.OrdinalIgnoreCase);
                        }), "自动并进布局的新文件没有标记为“新图标”，编辑时会直接铺到预桌面。");
                    Assert(layoutB != null && (layoutB.AutoMergedIdentities == null
                        || !layoutB.AutoMergedIdentities.Any(delegate(string identity)
                        {
                            return string.Equals(identity, "name:NEW.TXT", StringComparison.OrdinalIgnoreCase);
                        })), "用户明确保存的布局不应把自己的新文件标记成自动并集。");

                    // 用户对这份布局点“完成并保存”后，图标就是明确保存的；
                    // 自动并集标记必须清空，否则下次编辑还会把它当成新图标。
                    coordinator.SaveSnapshotAs(secondSnapshot, "named-a", "工作布局 A");
                    catalog = repository.Load();
                    layoutA = DesktopLayoutRepository.FindById(catalog, "named-a");
                    Assert(layoutA != null && (layoutA.AutoMergedIdentities == null
                        || layoutA.AutoMergedIdentities.Count == 0),
                        "明确保存布局后没有清除自动并集标记。");
                }
                finally
                {
                    try
                    {
                        if (File.Exists(layoutPath))
                        {
                            File.Delete(layoutPath);
                        }
                    }
                    catch
                    {
                    }
                }
            });

            Run("同名文件重建后重新归入新图标", delegate
            {
                DateTime layoutSaved = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
                DateTime recreated = new DateTime(2026, 10, 23, 15, 44, 0, DateTimeKind.Utc);
                DesktopIconPlacement saved = new DesktopIconPlacement
                {
                    Identity = "name:12333333.TXT",
                    DisplayName = "12333333.txt"
                };
                DesktopIconPlacement live = new DesktopIconPlacement
                {
                    Identity = "name:12333333.TXT",
                    DisplayName = "12333333.txt",
                    CreatedUtc = recreated
                };
                Assert(DesktopIconFreshness.RecreatedAfterLayout(
                    live, saved, layoutSaved, layoutSaved.AddDays(2)),
                    "同名文件删除后重建，仍被当成旧图标，会继续占旧布局位置。");

                live.CreatedUtc = layoutSaved.AddDays(-1);
                Assert(!DesktopIconFreshness.RecreatedAfterLayout(
                    live, saved, layoutSaved, layoutSaved.AddDays(2)),
                    "旧文件只是被布局保存过，不应被误判成新图标。");

                saved.CreatedUtc = recreated;
                live.CreatedUtc = recreated;
                Assert(!DesktopIconFreshness.RecreatedAfterLayout(
                    live, saved, layoutSaved, layoutSaved.AddDays(2)),
                    "创建时间相同的老图标不应被误判成新图标。");

                live.CreatedUtc = recreated.AddMinutes(2);
                Assert(DesktopIconFreshness.RecreatedAfterLayout(
                    live, saved, layoutSaved, layoutSaved.AddDays(2)),
                    "创建时间晚于上次记录的同名文件没有归入新图标。");

                // 刚建好的布局马上保存当前桌面：不能用布局创建时间把刚保存的图标判成新图标。
                saved.CreatedUtc = DateTime.MinValue;
                live.CreatedUtc = recreated;
                Assert(!DesktopIconFreshness.RecreatedAfterLayout(
                    live, saved, recreated.AddMinutes(-10), recreated.AddMinutes(1)),
                    "新建后立即保存的布局，把刚保存的图标误判成了新图标。");
            });

        }
    }
}
