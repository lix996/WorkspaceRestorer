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
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {

        private static void RunProfilesAndApplicationsTests()
        {
            Run("自启动服务在测试模式不写注册表", delegate
            {
                StartupRegistrationService service = new StartupRegistrationService("C:\\test\\WorkspaceRestorer.exe");
                StartupRegistrationInfo info = service.SetEnabled(true);
                Assert(info.WasSuppressed, "测试模式下自启动注册没有被抑制。");
            });

            Run("工作方案支持命名与逐项删除", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                int baselineProfileCount = controller.Profiles.Count;
                List<string> baselineProfileIds = controller.Profiles
                    .Where(delegate(StartupProfile item) { return item != null && !string.IsNullOrWhiteSpace(item.Id); })
                    .Select(delegate(StartupProfile item) { return item.Id; })
                    .ToList();
                string createdProfileId = null;
                try
                {
                    StartupProfile profile = controller.CreateProfile("编程工作");
                    createdProfileId = profile == null ? null : profile.Id;
                    Assert(profile != null && profile.Name == "编程工作", "工作方案没有按名称创建。");
                    SessionItem first = new SessionItem { Sequence = 1, DisplayName = "编辑器", ExecutablePath = "C:\\editor.exe" };
                    SessionItem second = new SessionItem { Sequence = 2, DisplayName = "项目文件夹", TargetPath = "C:\\Project" };
                    profile.Items.Add(first);
                    profile.Items.Add(second);
                    controller.SetActiveProfile(profile);
                    Assert(object.ReferenceEquals(profile, controller.ActiveProfile), "活动工作方案没有切换。");
                    controller.Dispose();
                    controller = new ApplicationController(
                        new TestCaptureService(),
                        new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                        new TestDesktopApplicationService(),
                        null,
                        null);
                    profile = controller.Profiles.FirstOrDefault(delegate(StartupProfile item)
                    {
                        return item != null && string.Equals(item.Id, createdProfileId, StringComparison.OrdinalIgnoreCase);
                    });
                    Assert(profile != null && profile.Name == "编程工作" && profile.Items.Count == 2, "工作方案没有从磁盘持久化加载。");
                    Assert(controller.RemoveProfileItem(profile, first), "清单项目没有删除。");
                    Assert(profile.Items.Count == 1 && profile.Items[0].Sequence == 1, "删除后清单顺序没有重排。");
                    controller.RenameProfile(profile, "会议工作");
                    Assert(profile.Name == "会议工作", "工作方案没有重命名。");
                    Assert(controller.DeleteProfile(profile), "工作方案没有删除。");
                    Assert(controller.Profiles.Count == baselineProfileCount,
                        "删除测试方案后不应影响测试前已有的工作方案。");
                    for (int index = 0; index < baselineProfileIds.Count; index++)
                    {
                        string baselineId = baselineProfileIds[index];
                        Assert(controller.Profiles.Any(delegate(StartupProfile item)
                        {
                            return item != null && string.Equals(item.Id, baselineId, StringComparison.OrdinalIgnoreCase);
                        }), "删除测试方案时误删了测试前已有的工作方案。");
                    }
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(createdProfileId))
                    {
                        StartupProfile persisted = controller.Profiles.FirstOrDefault(delegate(StartupProfile item)
                        {
                            return item != null && string.Equals(item.Id, createdProfileId, StringComparison.OrdinalIgnoreCase);
                        });
                        if (persisted != null)
                        {
                            controller.DeleteProfile(persisted);
                        }
                    }
                    controller.Dispose();
                }
            });

            Run("删除浏览器最后一个网页会连同空浏览器父项一起删除", delegate
            {
                MutableCaptureService capture = CreateBrowserCaptureService(false);
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("网页删除测试");
                    controller.SetActiveProfile(profile);
                    SessionSnapshot initial = controller.CaptureSnapshot();
                    Assert(initial != null && initial.Items.Count == 2, "测试网页会话没有完整捕获浏览器与网页。" );
                    SessionItem page = profile.Items.First(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; });
                    Assert(page != null, "测试方案中缺少网页项目。" );
                    Assert(controller.RemoveProfileItem(profile, page), "网页项目删除失败。" );
                    Assert(!profile.Items.Any(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; }), "网页删除后仍留在方案清单。" );
                    Assert(!profile.Items.Any(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.Application
                            && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
                    }), "最后一个网页删掉后，空浏览器父项还留在方案里。" );
                    SessionSnapshot recaptured = controller.CaptureSnapshot();
                    Assert(!recaptured.Items.Any(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; }), "后台捕获重新加入了已删除网页。" );
                    Assert(!recaptured.Items.Any(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.Application
                            && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
                    }), "后台捕获重新加入了空浏览器父项。" );
                    Assert(profile.ExcludedItemKeys.Any(delegate(string key) { return key.StartsWith("web-page|", StringComparison.OrdinalIgnoreCase); }), "网页删除没有记录持久化排除键。" );
                    Assert(profile.ExcludedItemKeys.Any(delegate(string key) { return key.StartsWith("browser-window|", StringComparison.OrdinalIgnoreCase); }), "空浏览器父项删除没有记录窗口排除键。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("删除文件夹父项的最后一个子项也会连同空父项一起删除", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("文件夹删除测试");
                    SessionItem folderWindow = new SessionItem
                    {
                        Id = "folder-win",
                        Kind = SessionItemKind.Application,
                        DisplayName = "文件资源管理器 (1)",
                        ExecutablePath = @"C:\Windows\explorer.exe",
                        CaptureSource = "ExplorerWindow",
                        ApplicationWindowIndex = 0,
                        WindowTitle = "测试文件夹",
                        IsEnabled = true
                    };
                    SessionItem folder = new SessionItem
                    {
                        Id = "folder-child",
                        Kind = SessionItemKind.ExplorerFolder,
                        DisplayName = "测试文件夹",
                        TargetPath = @"C:\TestFolder",
                        ExecutablePath = @"C:\Windows\explorer.exe",
                        ParentId = "folder-win",
                        IsEnabled = true
                    };
                    List<SessionItem> items = new List<SessionItem>();
                    items.Add(folderWindow);
                    items.Add(folder);
                    profile.ReplaceItems(items);

                    Assert(controller.RemoveProfileItem(profile, folder), "文件夹子项删除失败。" );
                    AssertEqual(0, profile.Items.Count);
                    Assert(profile.ExcludedItemKeys.Any(delegate(string key)
                    {
                        return key.StartsWith("explorer-window|", StringComparison.OrdinalIgnoreCase);
                    }), "最后一个文件夹删掉后，空资源管理器父项没有记录排除键。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("删除浏览器父项会级联删除网页并跨重载保持", delegate
            {
                MutableCaptureService capture = CreateBrowserCaptureService(true);
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("浏览器级联删除测试");
                    controller.SetActiveProfile(profile);
                    controller.CaptureSnapshot();
                    SessionItem browser = profile.Items.First(delegate(SessionItem item)
                    {
                        return item.Kind == SessionItemKind.Application && item.BrowserGroupKey != null;
                    });
                    Assert(browser != null, "测试方案中缺少浏览器父项目。" );
                    Assert(controller.RemoveProfileItem(profile, browser), "浏览器父项目删除失败。" );
                    Assert(!profile.Items.Any(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; }), "删除浏览器父项目后仍残留网页子项目。" );
                    Assert(profile.ExcludedItemKeys.Any(delegate(string key)
                    {
                        return key.StartsWith("browser-group|", StringComparison.OrdinalIgnoreCase)
                            || key.StartsWith("browser-window|", StringComparison.OrdinalIgnoreCase);
                    }), "删除浏览器父项目没有记录浏览器窗口/分组排除键。" );
                    SessionSnapshot recaptured = controller.CaptureSnapshot();
                    Assert(!recaptured.Items.Any(delegate(SessionItem item) { return item.BrowserGroupKey != null || item.Kind == SessionItemKind.WebPage; }), "后台捕获重新加入了已删除浏览器及网页。" );
                }
                finally
                {
                    controller.Dispose();
                }

                ApplicationController reloaded = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = reloaded.ActiveProfile;
                    Assert(profile != null && profile.ExcludedItemKeys.Any(delegate(string key)
                    {
                        return key.StartsWith("browser-group|", StringComparison.OrdinalIgnoreCase)
                            || key.StartsWith("browser-window|", StringComparison.OrdinalIgnoreCase);
                    }), "浏览器窗口/分组排除键没有从磁盘加载。" );
                    SessionSnapshot recaptured = reloaded.CaptureSnapshot();
                    Assert(!recaptured.Items.Any(delegate(SessionItem item) { return item.BrowserGroupKey != null || item.Kind == SessionItemKind.WebPage; }), "重载后捕获重新加入了已删除浏览器内容。" );
                    Assert(reloaded.DeleteProfile(profile), "清理浏览器级联删除测试方案失败。" );
                }
                finally
                {
                    reloaded.Dispose();
                }
            });

            Run("浏览器多标签按窗口与标签身份合并", delegate
            {
                MutableCaptureService capture = CreateBrowserCaptureService(true);
                for (int index = 0; index < capture.Items.Count; index++)
                {
                    if (capture.Items[index].Kind == SessionItemKind.WebPage)
                    {
                        capture.Items[index].BrowserTabIdentityCaptured = true;
                        capture.Items[index].CaptureSource = "BrowserAddressBar+TabAutomation";
                    }
                }
                // Two tabs may intentionally point at the same URL.  Their
                // tab identity, rather than URL alone, must carry enable state
                // across the next periodic capture.
                capture.Items[2].Url = capture.Items[1].Url;
                capture.Items[2].TargetPath = capture.Items[1].TargetPath;
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("多标签测试");
                    controller.SetActiveProfile(profile);
                    controller.CaptureSnapshot();
                    List<SessionItem> pages = profile.Items
                        .Where(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; })
                        .OrderBy(delegate(SessionItem item) { return item.BrowserTabIndex; })
                        .ToList();
                    Assert(pages.Count == 2, "相同 URL 的两个浏览器标签不应被捕获服务折叠。" );
                    Assert(pages[0].BrowserWindowIndex == 0 && pages[0].BrowserTabIndex == 0, "第一个标签身份没有保存。" );
                    Assert(pages[1].BrowserWindowIndex == 0 && pages[1].BrowserTabIndex == 1, "第二个标签身份没有保存。" );
                    Assert(controller.SetProfileItemEnabled(profile, pages[1], false), "第二个标签停用失败。" );
                    controller.CaptureSnapshot();
                    pages = profile.Items
                        .Where(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; })
                        .OrderBy(delegate(SessionItem item) { return item.BrowserTabIndex; })
                        .ToList();
                    Assert(pages.Count == 2, "第二次捕获错误删除了重复 URL 标签。" );
                    Assert(pages[0].IsEnabled, "第一个标签被错误继承为停用。" );
                    Assert(!pages[1].IsEnabled, "第二个标签的停用设置没有按标签身份继承。" );
                    Assert(controller.RemoveProfileItem(profile, pages[1]), "第二个标签删除失败。" );
                    controller.CaptureSnapshot();
                    pages = profile.Items
                        .Where(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; })
                        .ToList();
                    Assert(pages.Count == 1 && pages[0].BrowserTabIndex == 0, "删除一个重复 URL 标签时错误移除了另一个标签。" );
                    Assert(profile.ExcludedItemKeys.Any(delegate(string key) { return key.StartsWith("web-page-tab|", StringComparison.OrdinalIgnoreCase); }), "标签级网页删除没有写入标签身份排除键。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("VS Code 多窗口保留路径和窗口位置", delegate
            {
                MutableCaptureService capture = new MutableCaptureService();
                capture.Items.Add(new SessionItem
                {
                    Id = "code-window-one",
                    Kind = SessionItemKind.Application,
                    DisplayName = "Visual Studio Code",
                    ExecutablePath = "C:\\Program Files\\Microsoft VS Code\\Code.exe",
                    TargetPath = "C:\\Work\\Alpha",
                    Arguments = "--new-window \"C:\\Work\\Alpha\"",
                    ApplicationWindowIndex = 0,
                    CaptureSource = "VisualStudioCodeWindow",
                    WindowPlacement = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        Left = 20,
                        Top = 30,
                        WindowState = "Maximized"
                    }
                });
                capture.Items.Add(new SessionItem
                {
                    Id = "code-window-two",
                    Kind = SessionItemKind.Application,
                    DisplayName = "Visual Studio Code",
                    ExecutablePath = "C:\\Program Files\\Microsoft VS Code\\Code.exe",
                    TargetPath = "C:\\Work\\Beta",
                    Arguments = "--new-window \"C:\\Work\\Beta\"",
                    ApplicationWindowIndex = 1,
                    CaptureSource = "VisualStudioCodeWindow",
                    WindowPlacement = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        Left = 980,
                        Top = 40,
                        WindowState = "Minimized"
                    }
                });

                RecordingLauncher launcher = new RecordingLauncher();
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(launcher, new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("代码双窗口");
                    controller.SetActiveProfile(profile);
                    controller.CaptureSnapshot();
                    List<SessionItem> windows = profile.Items
                        .Where(delegate(SessionItem item) { return string.Equals(item.CaptureSource, "VisualStudioCodeWindow", StringComparison.OrdinalIgnoreCase); })
                        .OrderBy(delegate(SessionItem item) { return item.ApplicationWindowIndex; })
                        .ToList();
                    Assert(windows.Count == 2, "两个 VS Code 窗口被错误合并为一个项目。" );
                    AssertEqual("C:\\Work\\Alpha", windows[0].TargetPath);
                    AssertEqual("C:\\Work\\Beta", windows[1].TargetPath);
                    Assert(windows[0].WindowPlacement != null && windows[0].WindowPlacement.Left == 20, "第一个 VS Code 窗口位置没有保存。" );
                    Assert(windows[1].WindowPlacement != null && windows[1].WindowPlacement.Left == 980, "第二个 VS Code 窗口位置没有保存。" );
                    Assert(windows[0].WindowPlacement != null
                        && windows[0].WindowPlacement.Top == 30
                        && string.Equals(windows[0].WindowPlacement.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase),
                        "第一个 VS Code 窗口的位置或最大化状态没有保存。" );
                    Assert(windows[1].WindowPlacement != null
                        && windows[1].WindowPlacement.Top == 40
                        && string.Equals(windows[1].WindowPlacement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase),
                        "第二个 VS Code 窗口的位置或最小化状态没有保存。" );

                    // Reload from the JSON store so this verifies the
                    // serialized placement instead of the capture object's
                    // in-memory reference.
                    controller.Dispose();
                    controller = new ApplicationController(
                        capture,
                        new SessionRestoreService(launcher, new FixedLoadMonitor()),
                        new TestDesktopApplicationService(),
                        null,
                        null);
                    profile = controller.ActiveProfile;
                    windows = profile.Items
                        .Where(delegate(SessionItem item) { return string.Equals(item.CaptureSource, "VisualStudioCodeWindow", StringComparison.OrdinalIgnoreCase); })
                        .OrderBy(delegate(SessionItem item) { return item.ApplicationWindowIndex; })
                        .ToList();
                    Assert(windows.Count == 2
                        && windows[0].WindowPlacement != null
                        && windows[0].WindowPlacement.Left == 20
                        && windows[0].WindowPlacement.Top == 30
                        && string.Equals(windows[0].WindowPlacement.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase),
                        "窗口位置或最大化状态没有经方案 JSON 持久化。" );
                    Assert(windows[1].WindowPlacement != null
                        && windows[1].WindowPlacement.Left == 980
                        && windows[1].WindowPlacement.Top == 40
                        && string.Equals(windows[1].WindowPlacement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase),
                        "第二个窗口的位置或最小化状态没有经方案 JSON 持久化。" );
                    Assert(controller.SetProfileItemEnabled(profile, windows[1], false), "第二个 VS Code 窗口停用失败。" );
                    controller.CaptureSnapshot();
                    windows = profile.Items
                        .Where(delegate(SessionItem item) { return string.Equals(item.CaptureSource, "VisualStudioCodeWindow", StringComparison.OrdinalIgnoreCase); })
                        .OrderBy(delegate(SessionItem item) { return item.ApplicationWindowIndex; })
                        .ToList();
                    Assert(windows.Count == 2 && windows[0].IsEnabled && !windows[1].IsEnabled, "VS Code 窗口启用设置没有按窗口身份继承。" );

                    RestoreResult restored = controller.RestoreProfileAsync(profile, true).GetAwaiter().GetResult();
                    Assert(restored != null && launcher.Items.Count == 1, "恢复时没有跳过已停用的 VS Code 窗口。" );
                    AssertEqual("C:\\Work\\Alpha", launcher.Items[0].TargetPath);
                    Assert(launcher.Items[0].WindowPlacement != null && launcher.Items[0].WindowPlacement.Left == 20, "恢复调用没有携带 VS Code 窗口位置。" );
                    Assert(launcher.Items[0].WindowPlacement != null
                        && launcher.Items[0].WindowPlacement.Top == 30
                        && string.Equals(launcher.Items[0].WindowPlacement.WindowState, "Maximized", StringComparison.OrdinalIgnoreCase),
                        "恢复调用没有携带 VS Code 窗口的最大化状态。" );

                    // Windows may enumerate separate Electron processes in a
                    // different order on the next checkpoint. The workspace
                    // path must keep the disabled state attached to Beta,
                    // even when its capture-wide window index changes.
                    SessionItem firstCaptured = capture.Items[0];
                    capture.Items[0] = capture.Items[1];
                    capture.Items[1] = firstCaptured;
                    capture.Items[0].ApplicationWindowIndex = 0;
                    capture.Items[1].ApplicationWindowIndex = 1;
                    controller.CaptureSnapshot();
                    SessionItem alphaAfterReorder = profile.Items.First(delegate(SessionItem item)
                    {
                        return string.Equals(item.TargetPath, "C:\\Work\\Alpha", StringComparison.OrdinalIgnoreCase);
                    });
                    SessionItem betaAfterReorder = profile.Items.First(delegate(SessionItem item)
                    {
                        return string.Equals(item.TargetPath, "C:\\Work\\Beta", StringComparison.OrdinalIgnoreCase);
                    });
                    Assert(alphaAfterReorder != null && alphaAfterReorder.IsEnabled, "VS Code Alpha 的启用状态没有按路径继承。" );
                    Assert(betaAfterReorder != null && !betaAfterReorder.IsEnabled, "VS Code Beta 的停用状态没有按路径继承。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            // 设置页分成了四个分区，每个分区都能单独重置；重置一节不能动到别的
            // 分区（用户明确要求），下面三条各盯一个方向。
            Run("分区重置：启动与恢复只重置这一节", delegate
            {
                using (ApplicationController controller = new ApplicationController())
                {
                    controller.UpdateTiming(33, 7, 1.5);
                    controller.UpdateDragScrollSpeed(2.5);
                    controller.SetScanTerminalsAndLocalUrls(true);
                    AssertEqual("33", controller.Settings.InitialDelaySeconds.ToString());
                    AssertEqual("2.5", controller.Settings.DragScrollSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    Assert(controller.Settings.ScanTerminalsAndLocalUrls, "扫描终端与本地网址应先打开，才能验证重置。");

                    controller.ResetStartupSettings();

                    AssertEqual("5", controller.Settings.InitialDelaySeconds.ToString());
                    AssertEqual("3", controller.Settings.ItemDelaySeconds.ToString());
                    AssertEqual("0.5", controller.Settings.WebPageDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    // 拖动速度属于总设置，不能被启动与恢复的重置顺手改掉。
                    AssertEqual("2.5", controller.Settings.DragScrollSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    Assert(!controller.Settings.ScanTerminalsAndLocalUrls, "“扫描终端与本地网址”应恢复为默认关闭。");
                    Assert(controller.Settings.WaitForLoad, "“高负载时等待”应恢复为默认开启。");
                    // 2026-10-19 用户默认：启动程序后自动恢复工作方案 = 打开（没有工作方案时自然什么都不恢复）。
                    Assert(controller.Settings.AutoRestore, "“登录后自动恢复工作方案”应恢复为默认打开。");
                }
            });

            Run("分区重置：总设置只重置这一节，不动启动与恢复", delegate
            {
                using (ApplicationController controller = new ApplicationController())
                {
                    controller.UpdateTiming(42, 9, 2);
                    controller.UpdateDragScrollSpeed(2.5);
                    controller.SetScanTerminalsAndLocalUrls(true);
                    controller.UpdateApplicationsFirst(false);
                    controller.UpdateRecycleBinRetention(7);

                    controller.ResetGeneralSettings();

                    Assert(!controller.Settings.AutoStart, "总设置重置后“登录时自动启动”应为关闭。");
                    AssertEqual("42", controller.Settings.InitialDelaySeconds.ToString());
                    AssertEqual("9", controller.Settings.ItemDelaySeconds.ToString());
                    AssertEqual("2", controller.Settings.WebPageDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Assert(controller.Settings.ScanTerminalsAndLocalUrls, "总设置重置不应碰“扫描终端与本地网址”。");
                    // 拖动速度、优先启动项属于总设置，应恢复默认；回收站天数不属于总设置，应保持不变。
                    AssertEqual("1", controller.Settings.DragScrollSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                    Assert(controller.Settings.ApplicationsFirst, "优先启动项应恢复默认（先自启动应用）。");
                    AssertEqual("7", controller.RecycleBinRetentionDays.ToString());
                }
            });

            Run("分区重置：自启动应用只重置这一节", delegate
            {
                using (ApplicationController controller = new ApplicationController())
                {
                    controller.SetApplicationsAutoRestore(false);
                    controller.SetApplicationsWaitForLoad(false);
                    controller.UpdateApplicationsTiming(30, 8, 2);
                    controller.UpdateDragScrollSpeed(2.5);

                    controller.ResetApplicationsSettings();

                    Assert(controller.Settings.ApplicationsAutoRestore, "自启动应用自动恢复应恢复默认开启。");
                    Assert(controller.Settings.ApplicationsWaitForLoad, "自启动应用高负载等待应恢复默认开启。");
                    AssertEqual("5", controller.Settings.ApplicationsInitialDelaySeconds.ToString());
                    AssertEqual("3", controller.Settings.ApplicationsItemDelaySeconds.ToString());
                    AssertEqual("0.5", controller.Settings.ApplicationsWebPageDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    AssertEqual("2.5", controller.Settings.DragScrollSpeed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
                }
            });

            // 回收站：删除先入回收站、可恢复、可彻底删除、可清空、保留天数可设置。
            Run("删除工作方案先进回收站并可恢复", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    // 同一次测试运行里别的用例也会删除方案，所以这里只看相对变化。
                    int binBefore = controller.RecycleBinEntries.Count;
                    int profilesBefore = controller.StartupProfiles.Count;
                    StartupProfile profile = controller.CreateProfile("回收站测试");
                    profile.Items.Add(new SessionItem { DisplayName = "测试项", ExecutablePath = "C:\\test.exe" });

                    controller.DeleteProfile(profile);
                    // 全局回收站：入站时带类别与显示信息，其它工作区以后也这样接进来。
                    RecycleBinEntry newest = controller.RecycleBinEntries[controller.RecycleBinEntries.Count - 1];
                    AssertEqual(RecycleBinKind.StartupProfile, newest.Kind);
                    AssertEqual("回收站测试", newest.Title);
                    Assert(newest.Subtitle != null && newest.Subtitle.Contains("启动清单"), "副标题应说明清单项数。" );
                    AssertEqual((profilesBefore).ToString(), controller.StartupProfiles.Count.ToString());
                    AssertEqual((binBefore + 1).ToString(), controller.RecycleBinEntries.Count.ToString());
                    Assert(controller.RecycleBinRetentionDays >= 1, "保留天数应默认为正数。");

                    controller.UpdateRecycleBinRetention(7);
                    AssertEqual("7", controller.RecycleBinRetentionDays.ToString());
                    // 0 = 永久保留，不能再被 Clamp 成 1 天。
                    controller.UpdateRecycleBinRetention(0);
                    AssertEqual("0", controller.RecycleBinRetentionDays.ToString());
                    controller.UpdateRecycleBinRetention(7);

                    RecycleBinEntry entry = controller.RecycleBinEntries[controller.RecycleBinEntries.Count - 1];
                    Assert(controller.RestoreRecycleBinEntry(entry), "恢复应当成功。");
                    AssertEqual(binBefore.ToString(), controller.RecycleBinEntries.Count.ToString());
                    AssertEqual((profilesBefore + 1).ToString(), controller.StartupProfiles.Count.ToString());
                    bool restoredFound = false;
                    for (int index = 0; index < controller.StartupProfiles.Count; index++)
                    {
                        if (string.Equals(controller.StartupProfiles[index].Name, "回收站测试", StringComparison.Ordinal))
                        {
                            restoredFound = true;
                        }
                    }
                    Assert(restoredFound, "恢复后应能在工作方案列表里找到它。" );

                    StartupProfile again = null;
                    for (int index = 0; index < controller.StartupProfiles.Count; index++)
                    {
                        if (string.Equals(controller.StartupProfiles[index].Name, "回收站测试", StringComparison.Ordinal))
                        {
                            again = controller.StartupProfiles[index];
                        }
                    }
                    controller.DeleteProfile(again);
                    RecycleBinEntry latest = controller.RecycleBinEntries[controller.RecycleBinEntries.Count - 1];
                    List<RecycleBinEntry> one = new List<RecycleBinEntry>();
                    one.Add(latest);
                    int removedForever = controller.DeleteRecycleBinEntriesForever(one);
                    AssertEqual("1", removedForever.ToString());
                    AssertEqual(binBefore.ToString(), controller.RecycleBinEntries.Count.ToString());

                    StartupProfile second = controller.CreateProfile("清空测试");
                    controller.DeleteProfile(second);
                    int emptied = controller.EmptyRecycleBin();
                    Assert(emptied >= 1, "清空回收站至少应删除一项。" );
                    AssertEqual("0", controller.RecycleBinEntries.Count.ToString());
                }
                finally
                {
                    controller.Dispose();
                }
            });
            Run("启动项可以搬到另一份方案，方案也可以整份并入另一份", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    // 用户第 86 条：a 方案的某一项拖到 b 方案 → b 有这一项、a 没了；
                    //              a 方案整份拖到 b 方案 → 合并进 b。
                    StartupProfile alpha = controller.CreateProfile("方案甲");
                    StartupProfile beta = controller.CreateProfile("方案乙");
                    SessionItem one = new SessionItem { Kind = SessionItemKind.Application, DisplayName = "甲一", TargetPath = @"C:\a1.exe" };
                    SessionItem two = new SessionItem { Kind = SessionItemKind.Application, DisplayName = "甲二", TargetPath = @"C:\a2.exe" };
                    List<SessionItem> alphaItems = new List<SessionItem>();
                    alphaItems.Add(one);
                    alphaItems.Add(two);
                    controller.SetProfileItemsOrder(alpha, alphaItems);
                    SessionItem betaAnchor = new SessionItem { Kind = SessionItemKind.Application, DisplayName = "乙一", TargetPath = @"C:\b1.exe" };
                    List<SessionItem> betaItems = new List<SessionItem>();
                    betaItems.Add(betaAnchor);
                    controller.SetProfileItemsOrder(beta, betaItems);

                    // ① 把“甲一”搬到“方案乙”，插在“乙一”后面
                    Assert(controller.MoveItemBetweenProfiles(alpha, beta, one, betaAnchor, true), "跨方案搬项目应当成功。");
                    StartupProfile alphaAfter = FindProfileByName(controller, "方案甲");
                    StartupProfile betaAfter = FindProfileByName(controller, "方案乙");
                    AssertEqual("1", alphaAfter.Items.Count.ToString());
                    AssertEqual("甲二", alphaAfter.Items[0].DisplayName);
                    AssertEqual("2", betaAfter.Items.Count.ToString());
                    AssertEqual("乙一", betaAfter.Items[0].DisplayName);
                    AssertEqual("甲一", betaAfter.Items[1].DisplayName);

                    // ② 自己搬给自己应当被拒绝
                    Assert(!controller.MoveItemBetweenProfiles(betaAfter, betaAfter, two, null, false),
                        "同一份方案内部不该走这个接口。");

                    // ③ 整份并入：再造一份“方案丙”，并进“方案乙”
                    StartupProfile gamma = controller.CreateProfile("方案丙");
                    SessionItem g1 = new SessionItem { Kind = SessionItemKind.Application, DisplayName = "丙一", TargetPath = @"C:\c1.exe" };
                    SessionItem duplicate = new SessionItem { Kind = SessionItemKind.Application, DisplayName = "乙一复制", TargetPath = @"C:\b1.exe" };
                    List<SessionItem> gammaItems = new List<SessionItem>();
                    gammaItems.Add(g1);
                    gammaItems.Add(duplicate);
                    controller.SetProfileItemsOrder(gamma, gammaItems);
                    int added = controller.MergeProfileInto(gamma, betaAfter);
                    AssertEqual("1", added.ToString());          // 只有“丙一”是新的（另一个同路径，不重复放）
                    AssertEqual("3", betaAfter.Items.Count.ToString());
                    AssertEqual("丙一", betaAfter.Items[2].DisplayName);

                    // ④ 自己并给自己不动数据
                    AssertEqual("0", controller.MergeProfileInto(betaAfter, betaAfter).ToString());
                }
                finally
                {
                    controller.Dispose();
                }
            });
            Run("自启动应用的启动项也能搬到别的方案，方案也能整份并入", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    // 用户第 89 条：自启动应用也要能“a 方案的项拖到 b 方案”与“a 方案并进 b 方案”。
                    ApplicationPlan alpha = controller.CreateApplicationPlan("应用甲");
                    ApplicationPlan beta = controller.CreateApplicationPlan("应用乙");
                    ApplicationPlanItem a1 = new ApplicationPlanItem { Id = "i1", DisplayName = "甲一", TargetPath = @"C:\a1.exe", IsEnabled = true };
                    ApplicationPlanItem a2 = new ApplicationPlanItem { Id = "i2", DisplayName = "甲二", TargetPath = @"C:\a2.exe", IsEnabled = true };
                    List<ApplicationPlanItem> alphaItems = new List<ApplicationPlanItem>();
                    alphaItems.Add(a1);
                    alphaItems.Add(a2);
                    controller.SetApplicationPlanItemsOrder(alpha, alphaItems);
                    ApplicationPlanItem b1 = new ApplicationPlanItem { Id = "i3", DisplayName = "乙一", TargetPath = @"C:\b1.exe", IsEnabled = true };
                    List<ApplicationPlanItem> betaItems = new List<ApplicationPlanItem>();
                    betaItems.Add(b1);
                    controller.SetApplicationPlanItemsOrder(beta, betaItems);

                    // ① 把“甲一”搬到“应用乙”，插在“乙一”后面
                    Assert(controller.MoveApplicationItemBetweenPlans(alpha, beta, a1, b1, true), "跨方案搬启动项应当成功。");
                    AssertEqual("1", alpha.Items.Count.ToString());
                    AssertEqual("甲二", alpha.Items[0].DisplayName);
                    AssertEqual("2", beta.Items.Count.ToString());
                    AssertEqual("乙一", beta.Items[0].DisplayName);
                    AssertEqual("甲一", beta.Items[1].DisplayName);

                    // ② 同一份方案内部搬应当被拒绝
                    Assert(!controller.MoveApplicationItemBetweenPlans(beta, beta, b1, null, false), "同一份方案内部不该走这个接口。");

                    // ③ 整份并入：同路径的项不重复放
                    ApplicationPlan gamma = controller.CreateApplicationPlan("应用丙");
                    ApplicationPlanItem c1 = new ApplicationPlanItem { Id = "i4", DisplayName = "丙一", TargetPath = @"C:\c1.exe", IsEnabled = true };
                    ApplicationPlanItem dup = new ApplicationPlanItem { Id = "i5", DisplayName = "乙一复制", TargetPath = @"C:\b1.exe", IsEnabled = true };
                    List<ApplicationPlanItem> gammaItems = new List<ApplicationPlanItem>();
                    gammaItems.Add(c1);
                    gammaItems.Add(dup);
                    controller.SetApplicationPlanItemsOrder(gamma, gammaItems);
                    int added = controller.MergeApplicationPlanInto(gamma, beta);
                    AssertEqual("1", added.ToString());
                    AssertEqual("3", beta.Items.Count.ToString());
                    AssertEqual("丙一", beta.Items[2].DisplayName);
                    AssertEqual("0", controller.MergeApplicationPlanInto(beta, beta).ToString());
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("启动项整组搬到别的方案：父项+子项一起走，子项不能单独跨方案", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile alpha = controller.CreateProfile("整组甲");
                    StartupProfile beta = controller.CreateProfile("整组乙");
                    SessionItem parent = new SessionItem
                    {
                        Id = "p1",
                        Kind = SessionItemKind.Application,
                        DisplayName = "Edge",
                        TargetPath = @"C:\msedge.exe"
                    };
                    SessionItem child1 = new SessionItem
                    {
                        Id = "c1",
                        ParentId = "p1",
                        Kind = SessionItemKind.WebPage,
                        DisplayName = "网页一",
                        Url = "https://a.example/"
                    };
                    SessionItem child2 = new SessionItem
                    {
                        Id = "c2",
                        ParentId = "p1",
                        Kind = SessionItemKind.WebPage,
                        DisplayName = "网页二",
                        Url = "https://b.example/"
                    };
                    List<SessionItem> alphaItems = new List<SessionItem>();
                    alphaItems.Add(parent);
                    alphaItems.Add(child1);
                    alphaItems.Add(child2);
                    controller.SetProfileItemsOrder(alpha, alphaItems);
                    SessionItem anchor = new SessionItem
                    {
                        Id = "b1",
                        Kind = SessionItemKind.Application,
                        DisplayName = "乙一",
                        TargetPath = @"C:\b1.exe"
                    };
                    List<SessionItem> betaItems = new List<SessionItem>();
                    betaItems.Add(anchor);
                    controller.SetProfileItemsOrder(beta, betaItems);

                    // ① 子项单独跨方案 → 拒绝，两个方案都不动。
                    Assert(!controller.MoveItemBetweenProfiles(alpha, beta, child1, anchor, false),
                        "子项不能单独跨方案。");
                    AssertEqual("3", alpha.Items.Count.ToString());
                    AssertEqual("1", beta.Items.Count.ToString());

                    // ② 拖父行 → 父项 + 全部子项成块走，顺序保持，插在“乙一”后面。
                    Assert(controller.MoveItemBetweenProfiles(alpha, beta, parent, anchor, true),
                        "整组跨方案应当成功。");
                    AssertEqual("0", alpha.Items.Count.ToString());
                    AssertEqual("4", beta.Items.Count.ToString());
                    AssertEqual("乙一", beta.Items[0].DisplayName);
                    AssertEqual("Edge", beta.Items[1].DisplayName);
                    AssertEqual("网页一", beta.Items[2].DisplayName);
                    AssertEqual("网页二", beta.Items[3].DisplayName);

                    // ③ 搬回去：仍是一整组。
                    Assert(controller.MoveItemBetweenProfiles(beta, alpha, parent, null, false),
                        "整组搬回应当成功。");
                    AssertEqual("3", alpha.Items.Count.ToString());
                    AssertEqual("Edge", alpha.Items[0].DisplayName);
                    AssertEqual("网页一", alpha.Items[1].DisplayName);
                    AssertEqual("网页二", alpha.Items[2].DisplayName);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用整组搬到别的方案：父项+子项一起走，子项不能单独跨方案", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan alpha = controller.CreateApplicationPlan("整组应用甲");
                    ApplicationPlan beta = controller.CreateApplicationPlan("整组应用乙");
                    ApplicationPlanItem parent = new ApplicationPlanItem
                    {
                        Id = "p1",
                        DisplayName = "Edge",
                        TargetPath = @"C:\msedge.exe",
                        IsEnabled = true
                    };
                    ApplicationPlanItem child1 = new ApplicationPlanItem
                    {
                        Id = "c1",
                        ParentId = "p1",
                        DisplayName = "网页一",
                        TargetPath = "https://a.example/",
                        IsEnabled = true
                    };
                    ApplicationPlanItem child2 = new ApplicationPlanItem
                    {
                        Id = "c2",
                        ParentId = "p1",
                        DisplayName = "网页二",
                        TargetPath = "https://b.example/",
                        IsEnabled = true
                    };
                    List<ApplicationPlanItem> alphaItems = new List<ApplicationPlanItem>();
                    alphaItems.Add(parent);
                    alphaItems.Add(child1);
                    alphaItems.Add(child2);
                    controller.SetApplicationPlanItemsOrder(alpha, alphaItems);
                    ApplicationPlanItem anchor = new ApplicationPlanItem
                    {
                        Id = "b1",
                        DisplayName = "乙一",
                        TargetPath = @"C:\b1.exe",
                        IsEnabled = true
                    };
                    List<ApplicationPlanItem> betaItems = new List<ApplicationPlanItem>();
                    betaItems.Add(anchor);
                    controller.SetApplicationPlanItemsOrder(beta, betaItems);

                    Assert(!controller.MoveApplicationItemBetweenPlans(alpha, beta, child1, anchor, false),
                        "子项不能单独跨方案。");
                    AssertEqual("3", alpha.Items.Count.ToString());
                    AssertEqual("1", beta.Items.Count.ToString());

                    Assert(controller.MoveApplicationItemBetweenPlans(alpha, beta, parent, anchor, true),
                        "整组跨方案应当成功。");
                    AssertEqual("0", alpha.Items.Count.ToString());
                    AssertEqual("4", beta.Items.Count.ToString());
                    AssertEqual("乙一", beta.Items[0].DisplayName);
                    AssertEqual("Edge", beta.Items[1].DisplayName);
                    AssertEqual("网页一", beta.Items[2].DisplayName);
                    AssertEqual("网页二", beta.Items[3].DisplayName);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用可切换最小化并把窗口状态交给启动器", delegate
            {
                RecordingDesktopApplicationService applicationService =
                    new RecordingDesktopApplicationService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    applicationService,
                    null,
                    null);
                ApplicationPlan plan = null;
                try
                {
                    plan = controller.CreateApplicationPlan("最小化启动测试");
                    ApplicationPlanItem item = new ApplicationPlanItem
                    {
                        Id = "app-minimize",
                        DisplayName = "测试应用",
                        TargetPath = "C:\\test-window-app.exe",
                        IsEnabled = true,
                        WindowPlacement = new SavedWindowPlacement
                        {
                            HasBounds = true,
                            Left = 30,
                            Top = 40,
                            Width = 800,
                            Height = 600,
                            WindowState = "Maximized"
                        }
                    };
                    plan.Items.Add(item);
                    controller.SetApplicationPlanItemsOrder(plan, plan.Items);
                    controller.SetActiveApplicationPlan(plan);

                    Assert(controller.SetApplicationPlanItemWindowMinimized(plan, item, true),
                        "自启动应用项目没有被设为启动时最小化。" );
                    AssertEqual("Minimized", item.WindowPlacement.WindowState);
                    AssertEqual("Maximized", item.WindowPlacement.WindowStateBeforeMinimize);
                    Assert(item.WindowPlacement.MinimizeChoiceExplicit,
                        "自启动应用没有记录用户手动最小化标记。" );

                    LaunchResult launch = controller.LaunchApplicationPlanItem(item);
                    Assert(launch != null && launch.Success,
                        "带窗口状态的自启动项目没有启动成功。" );
                    Assert(applicationService.LastEntry != null,
                        "启动器没有收到自启动项目。" );
                    Assert(applicationService.LastEntry.WindowPlacement != null
                        && string.Equals(
                            applicationService.LastEntry.WindowPlacement.WindowState,
                            "Minimized",
                            StringComparison.OrdinalIgnoreCase),
                        "自启动项目的最小化状态没有传给启动器。" );

                    Assert(controller.SetApplicationPlanItemWindowMinimized(plan, item, false),
                        "取消自启动应用最小化失败。" );
                    AssertEqual("Maximized", item.WindowPlacement.WindowState);
                    Assert(item.WindowPlacement.WindowStateBeforeMinimize == null
                        && !item.WindowPlacement.MinimizeChoiceExplicit,
                        "取消自启动应用最小化后没有清掉最小化前状态/手动标记。" );
                }
                finally
                {
                    if (plan != null)
                    {
                        controller.DeleteApplicationPlan(plan);
                    }
                    controller.Dispose();
                }
            });

            Run("通用设置可持久化并恢复系统默认", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    // 2026-10-19 用户默认：打开（没有工作方案时什么都不恢复）。
    Assert(controller.Settings.AutoRestore, "新安装默认应自动恢复工作会话（没有工作方案时自然不动）。");
                    Assert(!controller.Settings.AutoStart, "新安装默认不应加入登录启动项。");
                    // 2026-10-20 用户要求：两个“启动前等待”范围 0–60 秒（默认值在重置后断言）。
                    controller.Settings.AutoRestore = true;
                    controller.Settings.WaitForLoad = false;
                    controller.UpdateTiming(120, 7);
                    AssertEqual(60, controller.Settings.InitialDelaySeconds);
                    controller.UpdateTiming(0, 7);
                    AssertEqual(0, controller.Settings.InitialDelaySeconds);
                    controller.UpdateApplicationsTiming(120, 3, 0.5);
                    AssertEqual(60, controller.Settings.ApplicationsInitialDelaySeconds);
                    controller.UpdateApplicationsTiming(0, 3, 0.5);
                    AssertEqual(0, controller.Settings.ApplicationsInitialDelaySeconds);
                    controller.UpdateTiming(42, 7);
                    controller.SaveSettings();
                }
                finally
                {
                    controller.Dispose();
                }

                ApplicationController persisted = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    Assert(persisted.Settings.AutoRestore, "自动恢复选择没有持久化。");
                    Assert(!persisted.Settings.WaitForLoad, "负载等待选择没有持久化。");
                    AssertEqual(42, persisted.Settings.InitialDelaySeconds);
                    AssertEqual(7, persisted.Settings.ItemDelaySeconds);
                    persisted.ResetSettingsToDefaults();
                    Assert(persisted.Settings.AutoRestore, "重置后自动恢复应为默认打开。");
                    Assert(!persisted.Settings.AutoStart, "重置后登录自启动仍处于开启状态。");
                    Assert(persisted.Settings.WaitForLoad, "重置后负载等待默认值不正确。");
                    AssertEqual(5, persisted.Settings.InitialDelaySeconds);
                    AssertEqual(5, persisted.Settings.ApplicationsInitialDelaySeconds);
                    AssertEqual(3, persisted.Settings.ItemDelaySeconds);
                }
                finally
                {
                    persisted.Dispose();
                }

                ApplicationController reloaded = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    Assert(reloaded.Settings.AutoRestore, "重置后的自动恢复选择没有按默认 true 写回磁盘。");
                    Assert(reloaded.Settings.WaitForLoad, "重置后的负载等待选择没有写回磁盘。");
                    AssertEqual(5, reloaded.Settings.InitialDelaySeconds);
                    AssertEqual(5, reloaded.Settings.ApplicationsInitialDelaySeconds);
                    AssertEqual(3, reloaded.Settings.ItemDelaySeconds);
                }
                finally
                {
                    reloaded.Dispose();
                }
            });

            Run("桌面项目必须明确选择后才加入工作方案", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("选择测试");
                    DesktopEntry selected = new DesktopEntry
                    {
                        DisplayName = "测试应用",
                        Path = "C:\\test-app.exe",
                        Extension = ".exe",
                        IsSelected = true
                    };
                    DesktopEntry notSelected = new DesktopEntry
                    {
                        DisplayName = "未选择应用",
                        Path = "C:\\not-selected.exe",
                        Extension = ".exe",
                        IsSelected = false
                    };

                    Assert(controller.AddDesktopEntriesToProfile(profile, new[] { selected, notSelected }) == 1, "只有明确选择的桌面项目才应加入方案。" );
                    Assert(profile.Items.Count == 1, "加入方案时包含了未选择的项目。" );
                    Assert(profile.Items[0].CaptureSource == "DesktopSelection", "桌面选择项目来源没有记录。" );
                    Assert(controller.AddDesktopEntriesToProfile(profile, new[] { selected }) == 0, "重复桌面项目被重复加入方案。" );
                    controller.SetProfileItemEnabled(profile, profile.Items[0], false);
                    controller.CaptureSnapshot();
                    Assert(!controller.ActiveProfile.Items[0].IsEnabled, "后台捕获覆盖了用户停用的项目。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });
            Run("启动与恢复行可切换最小化并记住最小化前状态", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                string profileId = null;
                try
                {
                    StartupProfile profile = controller.CreateProfile("最小化状态测试");
                    profileId = profile.Id;
                    SessionItem item = new SessionItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = SessionItemKind.Application,
                        DisplayName = "测试应用",
                        ExecutablePath = "C:\\test-window-app.exe",
                        TargetPath = "C:\\test-window-app.exe",
                        IsEnabled = true,
                        WindowPlacement = new SavedWindowPlacement
                        {
                            HasBounds = true,
                            Left = 120,
                            Top = 80,
                            Width = 900,
                            Height = 640,
                            WindowState = "Normal"
                        }
                    };
                    profile.Items.Add(item);
                    controller.SetActiveProfile(profile);

                    Assert(controller.SetProfileItemWindowMinimized(profile, item, true),
                        "普通窗口没有被设为恢复时最小化。" );
                    AssertEqual("Minimized", item.WindowPlacement.WindowState);
                    AssertEqual("Normal", item.WindowPlacement.WindowStateBeforeMinimize);
                    Assert(item.WindowPlacement.MinimizeChoiceExplicit,
                        "用户手动最小化没有标记为明确选择。" );
                    AssertEqual(120, item.WindowPlacement.Left);
                    AssertEqual(900, item.WindowPlacement.Width);

                    Assert(controller.SetProfileItemWindowMinimized(profile, item, false),
                        "取消最小化失败。" );
                    AssertEqual("Normal", item.WindowPlacement.WindowState);
                    Assert(item.WindowPlacement.WindowStateBeforeMinimize == null,
                        "取消最小化后没有清掉最小化前状态。" );
                    Assert(!item.WindowPlacement.MinimizeChoiceExplicit,
                        "取消最小化后仍保留手动最小化标记。" );

                    item.WindowPlacement.WindowState = "Maximized";
                    Assert(controller.SetProfileItemWindowMinimized(profile, item, true),
                        "最大化窗口没有被设为恢复时最小化。" );
                    AssertEqual("Maximized", item.WindowPlacement.WindowStateBeforeMinimize);
                    Assert(controller.SetProfileItemWindowMinimized(profile, item, false),
                        "最大化窗口取消最小化失败。" );
                    AssertEqual("Maximized", item.WindowPlacement.WindowState);

                    // 保存/重新加载后，“最小化前是最大化”仍要保留。
                    Assert(controller.SetProfileItemWindowMinimized(profile, item, true),
                        "重新设为最小化失败。" );
                    controller.Dispose();
                    controller = new ApplicationController(
                        new TestCaptureService(),
                        new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                        new TestDesktopApplicationService(),
                        null,
                        null);
                    StartupProfile reloaded = controller.StartupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
                    {
                        return candidate != null && string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase);
                    });
                    Assert(reloaded != null && reloaded.Items != null && reloaded.Items.Count > 0,
                        "最小化状态没有随方案写盘。" );
                    SessionItem reloadedItem = reloaded.Items.FirstOrDefault(delegate(SessionItem candidate)
                    {
                        return candidate != null && string.Equals(candidate.DisplayName, "测试应用", StringComparison.Ordinal);
                    });
                    Assert(reloadedItem != null
                        && reloadedItem.WindowPlacement != null
                        && string.Equals(reloadedItem.WindowPlacement.WindowState, "Minimized", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(reloadedItem.WindowPlacement.WindowStateBeforeMinimize, "Maximized", StringComparison.OrdinalIgnoreCase)
                        && reloadedItem.WindowPlacement.MinimizeChoiceExplicit,
                        "重新加载后最小化状态/最小化前状态/手动标记没有保留。" );
                }
                finally
                {
                    if (profileId != null)
                    {
                        StartupProfile stored = controller.StartupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
                        {
                            return candidate != null && string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase);
                        });
                        if (stored != null)
                        {
                            controller.DeleteProfile(stored);
                        }
                    }
                    controller.Dispose();
                }
            });

            Run("保存当前方案不覆盖用户手动最小化选择", delegate
            {
                MutableCaptureService capture = new MutableCaptureService();
                capture.Items.Add(new SessionItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = SessionItemKind.Application,
                    DisplayName = "测试应用",
                    ExecutablePath = "C:\\test-window-app.exe",
                    TargetPath = "C:\\test-window-app.exe",
                    IsEnabled = true,
                    WindowPlacement = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        Left = 500,
                        Top = 300,
                        Width = 1000,
                        Height = 700,
                        WindowState = "Normal"
                    }
                });
                ApplicationController controller = new ApplicationController(
                    capture,
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                string profileId = null;
                try
                {
                    StartupProfile profile = controller.CreateProfile("保存不覆盖最小化测试");
                    profileId = profile.Id;
                    SessionItem item = new SessionItem
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = SessionItemKind.Application,
                        DisplayName = "测试应用",
                        ExecutablePath = "C:\\test-window-app.exe",
                        TargetPath = "C:\\test-window-app.exe",
                        IsEnabled = true,
                        WindowPlacement = new SavedWindowPlacement
                        {
                            HasBounds = true,
                            Left = 120,
                            Top = 80,
                            Width = 900,
                            Height = 640,
                            WindowState = "Minimized",
                            WindowStateBeforeMinimize = "Maximized",
                            MinimizeChoiceExplicit = true
                        }
                    };
                    profile.Items.Add(item);
                    controller.SetActiveProfile(profile);

                    Assert(controller.SetProfileItemWindowMinimized(profile, item, true),
                        "测试前置最小化失败。" );
                    controller.CaptureSnapshot();
                    SessionItem merged = controller.ActiveProfile.Items.FirstOrDefault(delegate(SessionItem candidate)
                    {
                        return candidate != null && string.Equals(candidate.DisplayName, "测试应用", StringComparison.Ordinal);
                    });
                    Assert(merged != null && merged.WindowPlacement != null,
                        "保存当前方案后项目/窗口状态丢失。" );
                    AssertEqual("Minimized", merged.WindowPlacement.WindowState);
                    AssertEqual("Maximized", merged.WindowPlacement.WindowStateBeforeMinimize);
                    Assert(merged.WindowPlacement.MinimizeChoiceExplicit,
                        "保存当前方案后用户手动最小化标记没有保留。" );
                    AssertEqual(500, merged.WindowPlacement.Left);
                    AssertEqual(1000, merged.WindowPlacement.Width);
                }
                finally
                {
                    if (profileId != null)
                    {
                        StartupProfile stored = controller.StartupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
                        {
                            return candidate != null && string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase);
                        });
                        if (stored != null)
                        {
                            controller.DeleteProfile(stored);
                        }
                    }
                    controller.Dispose();
                }
            });

            Run("打开程序时按开关自动恢复工作方案（手动打开与登录自启一样）", delegate
            {
                ResetAutoRestoreMarkers();
                RecordingRestoreService restoreService = new RecordingRestoreService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    restoreService,
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    controller.UpdateTiming(0, 0);
                    StartupProfile profile = controller.CreateProfile("启动恢复测试");
                    profile.Items.Add(new SessionItem { DisplayName = "测试项", ExecutablePath = "C:\\test.exe" });
                    controller.SetActiveProfile(profile);
                    // 开关关着 → 打开程序时什么都不做（手动打开、开机自启都一样）。
                    controller.Settings.AutoRestore = false;
                    controller.Settings.ApplicationsAutoRestore = false;
                    controller.BeginStartup();
                    Thread.Sleep(150);
                    Assert(restoreService.CallCount == 0, "开关没打开，打开程序时却自动恢复了。" );
                    // 开关打开 → 立刻恢复（2026-10-19 用户报 bug：开机那一次原来被整段跳过）。
                    controller.Settings.AutoRestore = true;
                    controller.Settings.ApplicationsAutoRestore = true;
                    controller.BeginStartup();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                    while (restoreService.CallCount == 0 && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(20);
                    }
                    Assert(restoreService.CallCount == 1, "勾了「启动程序后自动恢复工作方案」，打开程序却没有恢复。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("打开程序时按开关自动启动自启动方案（开机自启那一路也算）", delegate
            {
                ResetAutoRestoreMarkers();
                RecordingRestoreService restoreService = new RecordingRestoreService();
                TestDesktopApplicationService applicationService = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    restoreService,
                    applicationService,
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("开机启动测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "记事本", TargetPath = "C:\\test-notepad.exe", IsEnabled = true });
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "没勾的", TargetPath = "C:\\test-skip.exe", IsEnabled = false });
                    controller.SetActiveApplicationPlan(plan);
                    controller.Settings.ApplicationsAutoRestore = true;
                    controller.Settings.AutoRestore = false;
                    controller.BeginStartup();
                    Assert(applicationService.LaunchCallCount == 1,
                        "勾了「启动程序后自动启动自启动方案」，打开程序却没有启动（只该启动勾选的那一条）。");
                }
                finally
                {
                    controller.Dispose();
                    ResetAutoRestoreMarkers();
                }
            });

            Run("自启动方案立即恢复会发 RestoreStarted / RestoreCompleted（和启动与恢复同一套交互）", delegate
            {
                RecordingRestoreService restoreService = new RecordingRestoreService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    restoreService,
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("交互测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "甲", TargetPath = "C:\\a.exe", IsEnabled = true });
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "乙", TargetPath = "C:\\b.exe", IsEnabled = true });
                    controller.SetActiveApplicationPlan(plan);
                    int startedCount = 0;
                    int completedCount = 0;
                    bool runningFlagSeen = false;
                    RestoreCompletion completion = null;
                    controller.RestoreStarted += delegate
                    {
                        startedCount++;
                        runningFlagSeen = controller.ApplicationsRestoreInProgress;
                    };
                    controller.RestoreCompleted += delegate(RestoreCompletion value)
                    {
                        completedCount++;
                        completion = value;
                    };
                    controller.StartApplicationsOnLaunch(true);
                    Assert(startedCount == 1, "没有发 RestoreStarted（恢复卡片不会弹出来）。");
                    Assert(runningFlagSeen, "RestoreStarted 时 ApplicationsRestoreInProgress 不是 true（卡片会显示成“正在恢复工作会话”）。");
                    Assert(completedCount == 1, "没有发 RestoreCompleted（卡片关不掉、也没有完成横幅）。");
                    Assert(completion != null && completion.TotalCount == 2, "完成事件里的总项数不对。");
                    Assert(completion != null && (completion.SuccessCount + completion.FailedCount + completion.AlreadyOpenCount) <= completion.TotalCount,
                        "完成事件的各项计数不合理。");
                    // 2026-10-19 补齐：完成横幅要说清“到底是哪些没启动、为什么”（以前只有数字，明细是空的）。
                    Assert(completion != null && completion.FailedCount == 2, "测试用的启动服务一定失败，失败数应是 2。");
                    Assert(completion != null && completion.Failures != null && completion.Failures.Count == 2,
                        "完成事件里没有“哪些没启动”的明细（横幅只能显示一个数字）。");
                    Assert(completion != null && completion.Failures != null
                        && completion.Failures[0] != null
                        && completion.Failures[0].Name == "甲",
                        "明细里第一项应写项目名“甲”。");
                    Assert(completion != null && completion.Failures != null
                        && completion.Failures[0] != null
                        && !string.IsNullOrWhiteSpace(completion.Failures[0].Reason),
                        "明细里要写清失败原因。");
                    Assert(!controller.ApplicationsRestoreInProgress, "跑完之后 ApplicationsRestoreInProgress 还留着 true。");
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动方案转接：父子、标签顺序、最小化状态完整进入恢复清单", delegate
            {
                ApplicationPlan plan = new ApplicationPlan { Name = "转接适配器测试" };
                ApplicationPlanItem parent = new ApplicationPlanItem
                {
                    Id = "win",
                    DisplayName = "Microsoft Edge (1)",
                    TargetPath = "C:\\Edge\\msedge.exe",
                    Kind = SessionItemKind.Application,
                    IsGroupParent = true,
                    IsBrowserWindow = true,
                    BrowserGroupKey = "edge-1",
                    BrowserWindowIndex = 1,
                    BrowserExecutablePath = "C:\\Edge\\msedge.exe",
                    BrowserName = "Microsoft Edge",
                    IsEnabled = true
                };
                parent.WindowPlacement = new SavedWindowPlacement
                {
                    HasBounds = false,
                    WindowState = "Minimized",
                    WindowStateBeforeMinimize = "Normal"
                };
                ApplicationPlanItem second = new ApplicationPlanItem
                {
                    Id = "p2",
                    DisplayName = "第二页",
                    TargetPath = "https://b.example/2",
                    Kind = SessionItemKind.WebPage,
                    IsBrowserPage = true,
                    ParentId = "win",
                    BrowserGroupKey = "edge-1",
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 9,
                    IsEnabled = true
                };
                ApplicationPlanItem first = new ApplicationPlanItem
                {
                    Id = "p1",
                    DisplayName = "第一页",
                    TargetPath = "https://a.example/1",
                    Kind = SessionItemKind.WebPage,
                    IsBrowserPage = true,
                    ParentId = "win",
                    BrowserGroupKey = "edge-1",
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 0,
                    IsEnabled = true
                };
                plan.Items.Add(parent);
                plan.Items.Add(second);
                plan.Items.Add(first);

                SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(plan);
                Assert(snapshot != null && snapshot.Items.Count == 3, "恢复清单应有 3 项。");
                Assert(snapshot.Items[0].Kind == SessionItemKind.Application, "第一项应是浏览器父项。");
                Assert(snapshot.Items[1].DisplayName == "第二页" && snapshot.Items[2].DisplayName == "第一页",
                    "网页顺序应保持方案列表顺序（第二页在前）。");
                Assert(snapshot.Items[1].BrowserTabIndex == 0 && snapshot.Items[2].BrowserTabIndex == 1,
                    "BrowserTabIndex 应按列表顺序重排，不能沿用旧的 9/0。");
                Assert(snapshot.Items[1].ParentId == snapshot.Items[0].Id
                    && snapshot.Items[2].ParentId == snapshot.Items[0].Id,
                    "网页应挂到同一个浏览器父项下。");
                Assert(snapshot.Items[1].Sequence == 2 && snapshot.Items[2].Sequence == 3,
                    "Sequence 应保持列表顺序。");
                Assert(snapshot.Items[1].WindowPlacement != null
                    && snapshot.Items[1].WindowPlacement.WindowState == "Minimized",
                    "父行最小化状态应传给网页。");
                Assert(object.ReferenceEquals(
                    snapshot.Items[1].WindowPlacement, snapshot.Items[0].WindowPlacement),
                    "网页应和父项共用同一个归一后的 WindowPlacement（父状态覆盖子项）。");
            });

            Run("自启动方案立即恢复走共用恢复服务，不新建工作方案", delegate
            {
                CapturingRestoreService restoreService = new CapturingRestoreService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    restoreService,
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("共用恢复测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    ApplicationPlanItem parent = new ApplicationPlanItem
                    {
                        DisplayName = "Microsoft Edge",
                        TargetPath = "C:\\Edge\\msedge.exe",
                        Kind = SessionItemKind.Application,
                        IsGroupParent = true,
                        IsBrowserWindow = true,
                        BrowserGroupKey = "edge",
                        BrowserWindowIndex = 0,
                        BrowserExecutablePath = "C:\\Edge\\msedge.exe",
                        BrowserName = "Microsoft Edge",
                        IsEnabled = true
                    };
                    plan.Items.Add(parent);
                    plan.Items.Add(new ApplicationPlanItem
                    {
                        DisplayName = "网页甲",
                        TargetPath = "https://a.example/1",
                        Kind = SessionItemKind.WebPage,
                        IsBrowserPage = true,
                        ParentId = parent.Id,
                        BrowserGroupKey = "edge",
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 0,
                        IsEnabled = true
                    });
                    controller.SetActiveApplicationPlan(plan);
                    controller.Settings.ApplicationsInitialDelaySeconds = 0;
                    controller.Settings.ApplicationsItemDelaySeconds = 2;
                    controller.Settings.ApplicationsWebPageDelaySeconds = 0.5;
                    controller.Settings.ApplicationsWaitForLoad = false;
                    int profilesBefore = controller.StartupProfiles.Count;
                    int started = 0;
                    int completed = 0;
                    bool runningSeen = false;
                    controller.RestoreStarted += delegate
                    {
                        started++;
                        runningSeen = controller.ApplicationsRestoreInProgress;
                    };
                    controller.RestoreCompleted += delegate { completed++; };

                    RestoreResult result = ApplicationPlanRestoreBridge
                        .RestoreAsync(controller, plan, false)
                        .GetAwaiter().GetResult();

                    Assert(restoreService.CallCount == 1, "应调用共用 SessionRestoreService 一次。");
                    Assert(restoreService.LastSnapshot != null && restoreService.LastSnapshot.Items.Count == 2,
                        "传给恢复服务的清单应包含父项和网页。");
                    Assert(restoreService.LastOptions != null
                        && restoreService.LastOptions.ItemDelay.TotalSeconds == 2
                        && restoreService.LastOptions.WebPageDelay.TotalSeconds == 0.5
                        && !restoreService.LastOptions.WaitForAcceptableLoad,
                        "应使用自启动应用自己的项目/网页间隔和负载等待设置。");
                    Assert(started == 1 && runningSeen, "恢复开始时应设置 ApplicationsRestoreInProgress，卡片标题才会显示自启动方案。");
                    Assert(completed == 1, "恢复完成事件应触发一次。");
                    Assert(result != null && result.Items.Count == 2, "恢复结果应带回两项。");
                    Assert(controller.StartupProfiles.Count == profilesBefore,
                        "转接恢复不应新建「启动与恢复」的工作方案。");
                    Assert(!controller.ApplicationsRestoreInProgress,
                        "恢复结束后 ApplicationsRestoreInProgress 应复位。");
                    Assert(controller.StatusText.IndexOf("已按自启动方案", StringComparison.Ordinal) >= 0,
                        "完成后状态栏应说明这是自启动方案恢复，实际：" + controller.StatusText);
                }
                finally
                {
                    controller.Dispose();
                }
            });

        }
    }
}
