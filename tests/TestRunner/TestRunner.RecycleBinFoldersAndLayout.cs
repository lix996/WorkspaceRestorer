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
using WorkspaceRestorer.Core;
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

        private static void RunRecycleBinFoldersAndLayoutTests()
        {
            Run("回收站按工作区分段存成文件夹，并且能原样读回", delegate
            {
                // 用户要求（2026-10-08）：回收站的数据也要分段（三个工作区），
                // 一条内容一个文件夹，方便直接删文件夹来手动清理。
                WorkspaceRestorer.RecycleBin.Core.RecycleBinFolderStore store =
                    new WorkspaceRestorer.RecycleBin.Core.RecycleBinFolderStore();

                StartupProfile profile = new StartupProfile { Id = "d1", Name = "被删的方案" };
                profile.Items.Add(new SessionItem { Id = "di1", DisplayName = "中转站", TargetPath = @"C:\中转站" });
                RecycleBinCatalog catalog = new RecycleBinCatalog();
                catalog.RetentionDays = 12;
                catalog.Entries.Add(new RecycleBinEntry
                {
                    Profile = profile,
                    DeletedAtUtc = new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc),
                    Kind = RecycleBinKind.StartupProfile,
                    Title = "被删的方案",
                    Subtitle = "清单 1 项"
                });
                catalog.Entries.Add(new RecycleBinEntry
                {
                    DeletedAtUtc = new DateTime(2026, 10, 8, 4, 0, 0, DateTimeKind.Utc),
                    Kind = RecycleBinKind.DesktopLayout,
                    Title = "单屏布局",
                    Payload = "<layouts/>"
                });
                store.Save(catalog);

                string binRoot = AppPaths.RecycleBinRoot;
                Assert(Directory.Exists(Path.Combine(binRoot, AppPaths.StartupRestoreDirectoryName)), "回收站应当按工作区分段。" );
                Assert(Directory.Exists(Path.Combine(binRoot, AppPaths.DesktopLayoutDirectoryName)), "回收站应当按工作区分段。" );
                Assert(Directory.Exists(Path.Combine(binRoot, AppPaths.ApplicationsDirectoryName)), "回收站应当按工作区分段。" );
                Assert(File.Exists(Path.Combine(binRoot, "回收站设置.json")), "保留天数应当单独一个文件。" );
                string[] startupFolders = Directory.GetDirectories(Path.Combine(binRoot, AppPaths.StartupRestoreDirectoryName));
                Assert(startupFolders.Length == 1, "一条内容应当是一个文件夹。" );
                Assert(File.Exists(Path.Combine(startupFolders[0], "条目.json")), "条目文件夹里应当有 条目.json。" );

                RecycleBinCatalog reloaded = store.Load();
                AssertEqual(12, reloaded.RetentionDays);
                AssertEqual(2, reloaded.Entries.Count);
                RecycleBinEntry first = reloaded.Entries.FirstOrDefault(delegate(RecycleBinEntry e) { return e != null && e.Title == "被删的方案"; });
                Assert(first != null, "应当能读回“被删的方案”。" );
                Assert(first.Profile != null && first.Profile.Name == "被删的方案", "方案的原始数据应当完整保留。" );
                Assert(first.Kind == RecycleBinKind.StartupProfile, "类别应当保留。" );

                // 清空之后，条目文件夹也要跟着消失。
                catalog.Entries.Clear();
                store.Save(catalog);
                AssertEqual(0, Directory.GetDirectories(Path.Combine(binRoot, AppPaths.StartupRestoreDirectoryName)).Length);
                AssertEqual(0, store.Load().Entries.Count);
                // 清理现场，别影响后面的测试。
                try { Directory.Delete(binRoot, true); } catch { }
            });

            Run("方案文件夹名＝方案名；删掉文件夹等于清掉那份方案（带安全网）", delegate
            {
                // 用户要求（2026-10-09）：工作方案一份一个文件夹、按方案名命名，
                // 而且“能够直接删除对应的文件，达到能够手动清除数据方法”。
                List<StartupProfile> profiles = new List<StartupProfile>();
                profiles.Add(new StartupProfile { Id = "a", Name = "方案甲" });
                profiles.Add(new StartupProfile { Id = "b", Name = "方案乙" });
                profiles.Add(new StartupProfile { Id = "c", Name = "方案乙" });
                profiles.Add(new StartupProfile { Id = "d", Name = @"非法:名字/带*号" });

                List<string> names = WorkspaceRestorer.Infrastructure.StartupProfileFolderMirror.BuildFolderNames(profiles);
                AssertEqual(4, names.Count);
                AssertEqual("方案甲", names[0]);
                AssertEqual("方案乙", names[1]);
                AssertEqual("方案乙（2）", names[2]);
                Assert(names[3].IndexOf(':') < 0 && names[3].IndexOf('/') < 0 && names[3].IndexOf('*') < 0,
                    "非法字符要替换掉，否则建不了文件夹。" );

                // 没有任何文件夹时：不做删除（刚装好程序 / 全部被清空的安全网）。
                string root = AppPaths.StartupProfilesFolderRoot;
                if (Directory.Exists(root)) { Directory.Delete(root, true); }
                List<string> removed = WorkspaceRestorer.Infrastructure.StartupProfileFolderMirror.RemoveProfilesWhoseFolderWasDeleted(profiles);
                AssertEqual(0, removed.Count);
                AssertEqual(4, profiles.Count);

                // 建出甲的文件夹、缺乙的 → 只清掉乙（两份都叫方案乙，一起清）。
                Directory.CreateDirectory(Path.Combine(root, "方案甲"));
                Directory.CreateDirectory(Path.Combine(root, "非法_名字_带_号"));
                removed = WorkspaceRestorer.Infrastructure.StartupProfileFolderMirror.RemoveProfilesWhoseFolderWasDeleted(profiles);
                AssertEqual(2, removed.Count);
                AssertEqual(2, profiles.Count);
                AssertEqual("方案甲", profiles[0].Name ?? string.Empty);
                try { Directory.Delete(root, true); } catch { }
            });

            Run("浏览器窗口序号 0 也要写进文件（两个窗口不能恢复成一个）", delegate
            {
                // 用户 2026-10-15 反馈：“我保存前本来是两个打开的网页（edge），
                // 但是恢复后却只是一个页面，并把所有网页都放到同一个页面上”。
                // 根因：BrowserWindowIndex 原来是 EmitDefaultValue = false，
                // 而 int 默认值是 0，于是**第一个窗口的 0 被静默丢掉**，
                // 恢复侧到处用 >= 0 判断“有没有窗口身份”，缺了就把一个窗口拆成几个。
                SessionItem firstWindowPage = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第一个窗口的网页",
                    Url = "https://a.example.com/",
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 0
                };
                SessionItem secondWindowPage = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第二个窗口的网页",
                    Url = "https://b.example.com/",
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 0
                };

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SessionItem));
                string json;
                using (MemoryStream writeStream = new MemoryStream())
                {
                    serializer.WriteObject(writeStream, firstWindowPage);
                    json = Encoding.UTF8.GetString(writeStream.ToArray());
                }

                if (json.IndexOf("\"BrowserWindowIndex\":0", StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException(
                        "BrowserWindowIndex = 0 没有被写进 JSON（就是它导致两个浏览器窗口恢复成一个）：" + json);
                }
                if (json.IndexOf("\"BrowserTabIndex\":0", StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException(
                        "BrowserTabIndex = 0 没有被写进 JSON（第一个标签的身份丢了）：" + json);
                }

                SessionItem reloaded;
                using (MemoryStream readStream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    reloaded = (SessionItem)serializer.ReadObject(readStream);
                }
                AssertEqual(0, reloaded.BrowserWindowIndex);
                AssertEqual(0, reloaded.BrowserTabIndex);
                AssertEqual(1, secondWindowPage.BrowserWindowIndex);
            });

            Run("修好旧方案里丢失的窗口身份：同一窗口的网页不再被拆成多个窗口", delegate
            {
                // 旧文件里的样子：第一个窗口的条目 BrowserWindowIndex 丢了（读回来是 -1），
                // 它的网页也丢了；第二个窗口有 1。加载时要按 ParentId 修回来。
                List<SessionItem> items = new List<SessionItem>();

                SessionItem windowOne = new SessionItem
                {
                    Id = "win-1",
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge (1)",
                    BrowserGroupKey = @"C:\edge\msedge.exe",
                    BrowserWindowIndex = -1
                };
                SessionItem windowTwo = new SessionItem
                {
                    Id = "win-2",
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge (2)",
                    BrowserGroupKey = @"C:\edge\msedge.exe",
                    BrowserWindowIndex = 1
                };
                items.Add(windowOne);
                items.Add(windowTwo);
                items.Add(new SessionItem
                {
                    Id = "p1",
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第一窗口第 1 页",
                    Url = "https://a1.example.com/",
                    ParentId = "win-1",
                    BrowserGroupKey = @"C:\edge\msedge.exe",
                    BrowserWindowIndex = -1,
                    BrowserTabIndex = -1
                });
                items.Add(new SessionItem
                {
                    Id = "p2",
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第一窗口第 2 页",
                    Url = "https://a2.example.com/",
                    ParentId = "win-1",
                    BrowserGroupKey = @"C:\edge\msedge.exe",
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 1
                });
                items.Add(new SessionItem
                {
                    Id = "p3",
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第二窗口第 1 页",
                    Url = "https://b1.example.com/",
                    ParentId = "win-2",
                    BrowserGroupKey = @"C:\edge\msedge.exe",
                    BrowserWindowIndex = 1,
                    BrowserTabIndex = 0
                });

                int changed = RepairBrowserPageGrouping(items);
                Assert(changed > 0, "至少要修好一处，否则说明没有真的丢过身份。" );
                AssertEqual(0, windowOne.BrowserWindowIndex);
                AssertEqual(0, items[2].BrowserWindowIndex);
                AssertEqual(0, items[3].BrowserWindowIndex);
                AssertEqual(1, items[4].BrowserWindowIndex);

                // 同一个窗口里的两个标签必须互不相同，否则恢复时又会挤在一起。
                Assert(items[2].BrowserTabIndex != items[3].BrowserTabIndex,
                    "同一窗口内的标签序号不能重复。" );

                // 修两遍必须稳定（幂等），否则每次保存都会把文件改来改去。
                int again = RepairBrowserPageGrouping(items);
                AssertEqual(0, again);
                AssertEqual(0, items[2].BrowserWindowIndex);
                AssertEqual(1, items[4].BrowserWindowIndex);
            });

            Run("恢复失败的提示要说清是哪一项、为什么", delegate
            {
                // 用户 2026-10-15：“显示某些无法恢复，要在提示上说清楚到底是哪些没有恢复
                // （以及为什么无法恢复，你要检查）”。
                List<RestoreFailure> problems = RestoreFailureText.Describe(new[]
                    {
                        new RestoreItemResult
                        {
                            Item = new SessionItem
                            {
                                Kind = SessionItemKind.Application,
                                DisplayName = "图狗启动器",
                                ExecutablePath = @"D:\gone\tu.exe"
                            },
                            Success = false,
                            Error = @"The application executable does not exist: D:\gone\tu.exe"
                        },
                        new RestoreItemResult
                        {
                            Item = new SessionItem
                            {
                                Kind = SessionItemKind.WebPage,
                                DisplayName = "某个网页",
                                Url = "https://x.example.com/"
                            },
                            Success = false,
                            Error = "The saved web page URL is empty or unsupported."
                        },
                        new RestoreItemResult
                        {
                            Item = new SessionItem
                            {
                                Kind = SessionItemKind.ExplorerFolder,
                                DisplayName = "文档"
                            },
                            Success = true
                        }
                    });

                AssertEqual(2, problems.Count);
                AssertEqual("图狗启动器", problems[0].Name);
                AssertEqual("程序", problems[0].KindText);
                Assert(problems[0].Reason.IndexOf("不在这个位置", StringComparison.Ordinal) >= 0,
                    "程序不见了的说人话原因没出来：" + problems[0].Reason);
                Assert(problems[0].Reason.IndexOf(@"D:\gone\tu.exe", StringComparison.Ordinal) >= 0,
                    "原因里要带上到底是哪个路径。" );
                AssertEqual("网页", problems[1].KindText);
                Assert(problems[1].Reason.IndexOf("没有可用的网址", StringComparison.Ordinal) >= 0,
                    "网页没有网址的说人话原因没出来：" + problems[1].Reason);
            });

            Run("自启动方案复制回工作方案保留浏览器窗口和标签", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile source = controller.CreateProfile("网页复制测试");
                    SavedWindowPlacement placement = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        Left = 100,
                        Top = 120,
                        Width = 1200,
                        Height = 800,
                        WindowState = "Minimized",
                        WindowStateBeforeMinimize = "Normal"
                    };
                    SessionItem browser = new SessionItem
                    {
                        Id = "browser-window-0",
                        Kind = SessionItemKind.Application,
                        DisplayName = "Microsoft Edge (1)",
                        ExecutablePath = @"C:\Browsers\msedge.exe",
                        BrowserExecutablePath = @"C:\Browsers\msedge.exe",
                        BrowserName = "Microsoft Edge",
                        BrowserGroupKey = @"C:\Browsers\msedge.exe",
                        BrowserWindowIndex = 0,
                        WindowPlacement = placement,
                        StartsBrowserWindow = true
                    };
                    SessionItem pageOne = new SessionItem
                    {
                        Kind = SessionItemKind.WebPage,
                        DisplayName = "重复网址一",
                        Url = "https://same.example/page",
                        TargetPath = "https://same.example/page",
                        ParentId = browser.Id,
                        BrowserExecutablePath = browser.BrowserExecutablePath,
                        BrowserName = browser.BrowserName,
                        BrowserGroupKey = browser.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 0,
                        BrowserTabIdentityCaptured = true,
                        IsActiveBrowserTab = true
                    };
                    SessionItem pageTwo = new SessionItem
                    {
                        Kind = SessionItemKind.WebPage,
                        DisplayName = "重复网址二",
                        Url = "https://same.example/page",
                        TargetPath = "https://same.example/page",
                        ParentId = browser.Id,
                        BrowserExecutablePath = browser.BrowserExecutablePath,
                        BrowserName = browser.BrowserName,
                        BrowserGroupKey = browser.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 1,
                        BrowserTabIdentityCaptured = true
                    };
                    List<SessionItem> sourceItems = new List<SessionItem>();
                    sourceItems.Add(browser);
                    sourceItems.Add(pageOne);
                    sourceItems.Add(pageTwo);
                    source.ReplaceItems(sourceItems);

                    ApplicationPlan plan = controller.CreateApplicationPlanFromProfile(source, "网页副本");
                    Assert(plan != null, "工作方案没有转成自启动方案。" );
                    AssertEqual(3, plan.Items.Count);
                    List<ApplicationPlanItem> planPages = plan.Items
                        .Where(delegate(ApplicationPlanItem item) { return item.IsBrowserPage; })
                        .ToList();
                    List<ApplicationPlanItem> planParents = plan.Items
                        .Where(delegate(ApplicationPlanItem item) { return item.IsGroupParent; })
                        .ToList();
                    AssertEqual(1, planParents.Count);
                    AssertEqual(2, planPages.Count);
                    Assert(planParents[0].IsBrowserWindow, "浏览器父项没有标记成浏览器窗口。" );
                    AssertEqual(planParents[0].Id, planPages[0].ParentId);
                    AssertEqual(planParents[0].Id, planPages[1].ParentId);
                    AssertEqual(0, planPages[0].BrowserWindowIndex);
                    AssertEqual(0, planPages[0].BrowserTabIndex);
                    AssertEqual(1, planPages[1].BrowserTabIndex);
                    AssertEqual(planPages[0].BrowserGroupKey, planPages[1].BrowserGroupKey);

                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ApplicationPlanItem));
                    string json;
                    using (MemoryStream stream = new MemoryStream())
                    {
                        serializer.WriteObject(stream, planPages[0]);
                        json = Encoding.UTF8.GetString(stream.ToArray());
                    }
                    Assert(json.IndexOf("\"BrowserWindowIndex\":0", StringComparison.Ordinal) >= 0,
                        "浏览器窗口序号 0 没有写进自启动方案 JSON。" );
                    Assert(json.IndexOf("\"BrowserTabIndex\":0", StringComparison.Ordinal) >= 0,
                        "第一个标签的序号 0 没有写进自启动方案 JSON。" );

                    StartupProfile roundtrip = controller.CreateProfileFromApplicationPlan(plan, "网页回来");
                    Assert(roundtrip != null, "自启动方案没有复制回工作方案。" );
                    List<SessionItem> roundtripBrowserParents = roundtrip.Items
                        .Where(delegate(SessionItem item)
                        {
                            return item.Kind == SessionItemKind.Application
                                && !string.IsNullOrWhiteSpace(item.BrowserGroupKey);
                        })
                        .ToList();
                    AssertEqual(1, roundtripBrowserParents.Count);
                    List<SessionItem> roundtripPages = roundtrip.Items
                        .Where(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; })
                        .ToList();
                    AssertEqual(2, roundtripPages.Count);
                    AssertEqual(roundtripBrowserParents[0].Id, roundtripPages[0].ParentId);
                    AssertEqual(roundtripBrowserParents[0].Id, roundtripPages[1].ParentId);
                    AssertEqual(0, roundtripPages[0].BrowserWindowIndex);
                    AssertEqual(0, roundtripPages[0].BrowserTabIndex);
                    AssertEqual(1, roundtripPages[1].BrowserTabIndex);
                    AssertEqual("https://same.example/page", roundtripPages[0].Url);
                    AssertEqual("https://same.example/page", roundtripPages[1].Url);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用：删最后一个网页会连带空浏览器父项", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("删除网页测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    ApplicationPlanItem parent = new ApplicationPlanItem
                    {
                        Id = "win",
                        DisplayName = "Microsoft Edge (1)",
                        TargetPath = @"C:\Browsers\msedge.exe",
                        Kind = SessionItemKind.Application,
                        IsGroupParent = true,
                        IsBrowserWindow = true,
                        BrowserGroupKey = @"C:\Browsers\msedge.exe",
                        BrowserWindowIndex = 0,
                        BrowserExecutablePath = @"C:\Browsers\msedge.exe",
                        BrowserName = "Microsoft Edge",
                        IsEnabled = true
                    };
                    ApplicationPlanItem pageOne = new ApplicationPlanItem
                    {
                        Id = "p1",
                        DisplayName = "网页一",
                        TargetPath = "https://one.example",
                        Kind = SessionItemKind.WebPage,
                        IsBrowserPage = true,
                        ParentId = "win",
                        BrowserGroupKey = parent.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 0,
                        IsEnabled = true
                    };
                    ApplicationPlanItem pageTwo = new ApplicationPlanItem
                    {
                        Id = "p2",
                        DisplayName = "网页二",
                        TargetPath = "https://two.example",
                        Kind = SessionItemKind.WebPage,
                        IsBrowserPage = true,
                        ParentId = "win",
                        BrowserGroupKey = parent.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 1,
                        IsEnabled = true
                    };
                    plan.Items.Add(parent);
                    plan.Items.Add(pageOne);
                    plan.Items.Add(pageTwo);
                    controller.SetApplicationPlanItemsOrder(plan, plan.Items);

                    Assert(controller.RemoveApplicationPlanItem(plan, pageOne), "删除第一个网页失败。" );
                    AssertEqual(2, plan.Items.Count);
                    Assert(plan.Items.Contains(parent) && plan.Items.Contains(pageTwo),
                        "还有网页时不该删掉浏览器父项或另一个网页。" );

                    Assert(controller.RemoveApplicationPlanItem(plan, pageTwo), "删除最后一个网页失败。" );
                    AssertEqual(0, plan.Items.Count);
                    SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(plan);
                    AssertEqual(0, snapshot.Items.Count);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用：删浏览器父项会一次删掉整组网页", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("删除整组测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    ApplicationPlanItem parent = new ApplicationPlanItem
                    {
                        Id = "win",
                        DisplayName = "Microsoft Edge (1)",
                        TargetPath = @"C:\Browsers\msedge.exe",
                        Kind = SessionItemKind.Application,
                        IsGroupParent = true,
                        IsBrowserWindow = true,
                        BrowserGroupKey = @"C:\Browsers\msedge.exe",
                        BrowserWindowIndex = 0,
                        BrowserExecutablePath = @"C:\Browsers\msedge.exe",
                        BrowserName = "Microsoft Edge",
                        IsEnabled = true
                    };
                    ApplicationPlanItem page = new ApplicationPlanItem
                    {
                        Id = "p1",
                        DisplayName = "网页一",
                        TargetPath = "https://one.example",
                        Kind = SessionItemKind.WebPage,
                        IsBrowserPage = true,
                        ParentId = "win",
                        BrowserGroupKey = parent.BrowserGroupKey,
                        BrowserWindowIndex = 0,
                        BrowserTabIndex = 0,
                        IsEnabled = true
                    };
                    ApplicationPlanItem normal = new ApplicationPlanItem
                    {
                        Id = "app",
                        DisplayName = "记事本",
                        TargetPath = @"C:\Windows\notepad.exe",
                        Kind = SessionItemKind.Application,
                        IsEnabled = true
                    };
                    plan.Items.Add(parent);
                    plan.Items.Add(page);
                    plan.Items.Add(normal);
                    controller.SetApplicationPlanItemsOrder(plan, plan.Items);

                    Assert(controller.RemoveApplicationPlanItem(plan, parent), "删除浏览器父项失败。" );
                    AssertEqual(1, plan.Items.Count);
                    Assert(object.ReferenceEquals(plan.Items[0], normal), "整组删除误伤了普通启动项。" );
                    SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(plan);
                    AssertEqual(1, snapshot.Items.Count);
                    AssertEqual("记事本", snapshot.Items[0].DisplayName);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用：删文件夹最后一个子项会连带空文件夹父项", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new SessionRestoreService(new RecordingLauncher(), new FixedLoadMonitor()),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("文件夹删除测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    ApplicationPlanItem parent = new ApplicationPlanItem
                    {
                        Id = "folder-win",
                        DisplayName = "文件夹 (1)",
                        TargetPath = @"C:\Windows\explorer.exe",
                        Kind = SessionItemKind.Application,
                        IsGroupParent = true,
                        IsFolderGroup = true,
                        IsEnabled = true
                    };
                    ApplicationPlanItem child = new ApplicationPlanItem
                    {
                        Id = "folder-child",
                        DisplayName = "测试文件夹",
                        TargetPath = @"C:\TestFolder",
                        Kind = SessionItemKind.ExplorerFolder,
                        ParentId = "folder-win",
                        IsEnabled = true
                    };
                    plan.Items.Add(parent);
                    plan.Items.Add(child);
                    controller.SetApplicationPlanItemsOrder(plan, plan.Items);

                    Assert(controller.RemoveApplicationPlanItem(plan, child), "文件夹子项删除失败。" );
                    AssertEqual(0, plan.Items.Count);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动应用：旧的空浏览器父项不会进入恢复清单", delegate
            {
                ApplicationPlan plan = new ApplicationPlan { Name = "旧空壳测试" };
                plan.Items.Add(new ApplicationPlanItem
                {
                    Id = "empty-win",
                    DisplayName = "Microsoft Edge (1)",
                    TargetPath = @"C:\Browsers\msedge.exe",
                    Kind = SessionItemKind.Application,
                    IsGroupParent = true,
                    IsBrowserWindow = true,
                    BrowserGroupKey = @"C:\Browsers\msedge.exe",
                    BrowserWindowIndex = 0,
                    BrowserExecutablePath = @"C:\Browsers\msedge.exe",
                    BrowserName = "Microsoft Edge",
                    IsEnabled = true
                });
                plan.Items.Add(new ApplicationPlanItem
                {
                    Id = "empty-folder",
                    DisplayName = "文件夹 (1)",
                    TargetPath = @"C:\Windows\explorer.exe",
                    Kind = SessionItemKind.Application,
                    IsGroupParent = true,
                    IsFolderGroup = true,
                    IsEnabled = true
                });
                SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(plan);
                AssertEqual(0, snapshot.Items.Count);
            });

            Run("方案文件夹：无限嵌套、循环父项会退回根级", delegate
            {
                StartupProfile a = new StartupProfile { Name = "树-A" };
                StartupProfile b = new StartupProfile { Name = "树-B", ParentId = a.Id };
                StartupProfile c = new StartupProfile { Name = "树-C", ParentId = b.Id };
                List<StartupProfile> nodes = new List<StartupProfile> { a, b, c };
                PlanFolderTree.Normalize(nodes);
                AssertEqual(3, nodes.Count);
                AssertEqual(a.Id, b.ParentId);
                AssertEqual(b.Id, c.ParentId);
                AssertEqual(2, PlanFolderTree.Depth(nodes, c));

                // 人为造 A→C 循环：整理后必须退回根级，不能死循环，也不能丢节点。
                a.ParentId = c.Id;
                PlanFolderTree.Normalize(nodes);
                AssertEqual(3, nodes.Count);
                Assert(string.IsNullOrWhiteSpace(a.ParentId), "循环里的根节点应退回根级。" );
                AssertEqual(a.Id, b.ParentId);
                AssertEqual(b.Id, c.ParentId);
            });

            Run("方案文件夹：删除大方案连子方案一起进回收站并能整棵恢复", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile a = controller.CreateProfile("回收树-A-" + suffix);
                    StartupProfile b = controller.CreateProfile("回收树-B-" + suffix, a, false);
                    StartupProfile c = controller.CreateProfile("回收树-C-" + suffix, b, false);
                    b.Items.Add(new SessionItem { DisplayName = "B 项" });
                    c.Items.Add(new SessionItem { DisplayName = "C 项" });
                    b.ReplaceItems(b.Items);
                    c.ReplaceItems(c.Items);

                    controller.DeleteProfile(a);
                    Assert(FindProfileByName(controller, "回收树-A-" + suffix) == null, "大方案应被删除。" );
                    Assert(FindProfileByName(controller, "回收树-B-" + suffix) == null, "子方案应被连带删除。" );
                    Assert(FindProfileByName(controller, "回收树-C-" + suffix) == null, "孙方案应被连带删除。" );

                    RecycleBinEntry entry = null;
                    for (int index = controller.RecycleBinEntries.Count - 1; index >= 0; index--)
                    {
                        RecycleBinEntry candidate = controller.RecycleBinEntries[index];
                        if (candidate != null && candidate.IsPlanTree
                            && string.Equals(candidate.Title, "回收树-A-" + suffix, StringComparison.Ordinal))
                        {
                            entry = candidate;
                            break;
                        }
                    }
                    Assert(entry != null, "回收站里应有整棵树的记录。" );
                    Assert(controller.RestoreRecycleBinEntry(entry), "整棵恢复应成功。" );
                    StartupProfile restoredA = FindProfileByName(controller, "回收树-A-" + suffix);
                    StartupProfile restoredB = FindProfileByName(controller, "回收树-B-" + suffix);
                    StartupProfile restoredC = FindProfileByName(controller, "回收树-C-" + suffix);
                    Assert(restoredA != null && restoredB != null && restoredC != null, "恢复后三层都应在。" );
                    AssertEqual(restoredA.Id, restoredB.ParentId);
                    AssertEqual(restoredB.Id, restoredC.ParentId);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：父方案不在时子方案恢复到根目录，不重建父方案", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile parent = controller.CreateProfile("孤儿父-" + suffix);
                    StartupProfile child = controller.CreateProfile("孤儿子-" + suffix, parent, false);

                    RecycleBinEntry childEntry = null;
                    RecycleBinEntry parentEntry = null;
                    controller.DeleteProfile(child);
                    for (int index = controller.RecycleBinEntries.Count - 1; index >= 0; index--)
                    {
                        RecycleBinEntry candidate = controller.RecycleBinEntries[index];
                        if (candidate != null && candidate.IsPlanTree
                            && string.Equals(candidate.Title, "孤儿子-" + suffix, StringComparison.Ordinal))
                        {
                            childEntry = candidate;
                            break;
                        }
                    }
                    controller.DeleteProfile(parent);
                    for (int index = controller.RecycleBinEntries.Count - 1; index >= 0; index--)
                    {
                        RecycleBinEntry candidate = controller.RecycleBinEntries[index];
                        if (candidate != null && candidate.IsPlanTree
                            && string.Equals(candidate.Title, "孤儿父-" + suffix, StringComparison.Ordinal))
                        {
                            parentEntry = candidate;
                            break;
                        }
                    }
                    Assert(childEntry != null && parentEntry != null, "两条回收站记录都应存在。" );

                    Assert(controller.RestoreRecycleBinEntry(childEntry), "子方案单独恢复应成功。" );
                    StartupProfile restoredChild = FindProfileByName(controller, "孤儿子-" + suffix);
                    Assert(restoredChild != null, "子方案应回到列表。" );
                    Assert(string.IsNullOrWhiteSpace(restoredChild.ParentId),
                        "父方案不在时应恢复到根目录，不能重建父方案。" );
                    Assert(FindProfileByName(controller, "孤儿父-" + suffix) == null,
                        "不能为了恢复子方案而自动重建父方案。" );

                    Assert(controller.RestoreRecycleBinEntry(parentEntry), "父方案恢复应成功。" );
                    StartupProfile restoredParent = FindProfileByName(controller, "孤儿父-" + suffix);
                    StartupProfile stillChild = FindProfileByName(controller, "孤儿子-" + suffix);
                    Assert(restoredParent != null && stillChild != null, "两份都应存在。" );
                    Assert(string.IsNullOrWhiteSpace(stillChild.ParentId),
                        "子方案已经先恢复到根目录，父方案后来恢复也不应把它吸回去。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：空文件夹不会因为最后一个子方案被删而消失", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile folder = controller.CreateProfile("空文件夹-" + suffix, null, true);
                    StartupProfile child = controller.CreateProfile("空文件夹子-" + suffix, folder, false);
                    controller.DeleteProfile(child);
                    StartupProfile stillFolder = FindProfileByName(controller, "空文件夹-" + suffix);
                    Assert(stillFolder != null, "方案文件夹是实体，清空后应保留。" );
                    Assert(stillFolder.IsFolder, "应仍是纯文件夹。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：顶部/底部放置条真的把根方案移到最前/最后", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile a = controller.CreateProfile("端-A-" + suffix);
                    StartupProfile b = controller.CreateProfile("端-B-" + suffix);
                    StartupProfile c = controller.CreateProfile("端-C-" + suffix);

                    Assert(controller.MoveProfileToRootEdge(c, -1), "根方案应能移到最前。");
                    Assert(ProfileIndex(controller, c) < ProfileIndex(controller, a)
                        && ProfileIndex(controller, c) < ProfileIndex(controller, b),
                        "根方案应排到其它根方案之前。");

                    Assert(!controller.MoveProfileToRootEdge(c, -1), "已经在最前时应返回 false，不能写假撤销。");
                    Assert(controller.MoveProfileToRootEdge(c, 1), "根方案应能移到最后。");
                    Assert(ProfileIndex(controller, a) < ProfileIndex(controller, b)
                        && ProfileIndex(controller, b) < ProfileIndex(controller, c),
                        "根方案应排到其它根方案之后。");

                    StartupProfile child = controller.CreateProfile("端子-" + suffix, a, false);
                    Assert(controller.MoveProfileToRootEdge(child, -1), "子方案应能整棵移到根级最前。");
                    Assert(string.IsNullOrWhiteSpace(child.ParentId), "移出后父级应为空。");
                    Assert(ProfileIndex(controller, child) < ProfileIndex(controller, a),
                        "子方案移出后应排在原父方案之前。");
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：恢复顺序是自己的项目、再子方案从上到下递归", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile a = controller.CreateProfile("顺序-A-" + suffix);
                    StartupProfile b = controller.CreateProfile("顺序-B-" + suffix, a, false);
                    StartupProfile c = controller.CreateProfile("顺序-C-" + suffix, b, false);
                    StartupProfile d = controller.CreateProfile("顺序-D-" + suffix, a, false);
                    a.ReplaceItems(new List<SessionItem> { new SessionItem { DisplayName = "A1" }, new SessionItem { DisplayName = "A2" } });
                    b.ReplaceItems(new List<SessionItem> { new SessionItem { DisplayName = "B1" } });
                    c.ReplaceItems(new List<SessionItem> { new SessionItem { DisplayName = "C1" } });
                    d.ReplaceItems(new List<SessionItem> { new SessionItem { DisplayName = "D1" } });

                    SessionSnapshot snapshot = controller.BuildProfileRestoreSnapshot(a);
                    AssertEqual("A1,A2,B1,C1,D1", JoinItemNames(snapshot.Items));
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("自启动方案镜像：启动清单.txt 的最后保存时间也走公共格式", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    ApplicationPlan plan = controller.CreateApplicationPlan("时间镜像-" + suffix);
                    plan.UpdatedAtUtc = new DateTime(2026, 11, 4, 7, 3, 0, DateTimeKind.Utc);
                    ApplicationPlanFolderMirror.Sync(controller.ApplicationPlans);
                    string folder = ApplicationPlanFolderMirror.FolderPathFor(plan, controller.ApplicationPlans);
                    string listFile = Path.Combine(folder, "启动清单.txt");
                    Assert(File.Exists(listFile), "自启动方案镜像缺少启动清单.txt。" );
                    string text = File.ReadAllText(listFile, Encoding.UTF8);
                    string expected = TimeText.FormatUtcAsComputerLocalStamp(plan.UpdatedAtUtc);
                    Assert(text.Contains("最后保存：" + expected),
                        "启动清单.txt 的时间没有走公共格式：" + text);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：移动并入是改父级，不摊平也不删除来源", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    ApplicationPlan a = controller.CreateApplicationPlan("移入-A-" + suffix);
                    ApplicationPlan b = controller.CreateApplicationPlan("移入-B-" + suffix);
                    b.Items.Add(new ApplicationPlanItem { DisplayName = "B 项", TargetPath = @"C:\b.exe", Kind = SessionItemKind.Application });
                    string oldFolder = ApplicationPlanFolderMirror.FolderPathFor(b, controller.ApplicationPlans);
                    Assert(Directory.Exists(oldFolder), "移入前自启动方案目录应存在。" );
                    Assert(controller.MoveApplicationPlanInto(b, a), "移入应成功。" );
                    AssertEqual(a.Id, b.ParentId);
                    AssertEqual(1, b.Items.Count);
                    Assert(controller.FindApplicationPlan(b) != null, "来源方案不能被删除。" );
                    string newFolder = ApplicationPlanFolderMirror.FolderPathFor(b, controller.ApplicationPlans);
                    Assert(!Directory.Exists(oldFolder) && Directory.Exists(newFolder),
                        "自启动方案并入也应移动目录，不是复制。" );
                    Assert(controller.MoveApplicationPlanToPlacement(b, null, 0), "撤销移入应成功。" );
                    Assert(string.IsNullOrWhiteSpace(b.ParentId), "撤销后应回到根级。" );
                    Assert(Directory.Exists(oldFolder) && !Directory.Exists(newFolder),
                        "撤销后目录应搬回根目录。" );
                    Assert(controller.MoveApplicationPlanInto(b, a), "再次移入应成功。" );
                    Assert(!controller.MoveApplicationPlanInto(a, b), "父方案不能移进自己的子方案。" );
                    Assert(string.IsNullOrWhiteSpace(a.ParentId), "失败的移入不能改变父级。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案文件夹：并入移动的是目录，撤销能把目录移回原处", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    StartupProfile parent = controller.CreateProfile("目录父-" + suffix);
                    StartupProfile child = controller.CreateProfile("目录子-" + suffix);
                    child.ReplaceItems(new List<SessionItem>
                    {
                        new SessionItem { DisplayName = "目录子项", TargetPath = @"C:\Windows\System32\notepad.exe" }
                    });
                    string oldPath = StartupProfileFolderMirror.FolderPathFor(child, controller.StartupProfiles);
                    Assert(Directory.Exists(oldPath), "移动前子方案目录应存在。" );

                    Assert(controller.MoveProfileInto(child, parent), "移入应成功。" );
                    string newPath = StartupProfileFolderMirror.FolderPathFor(child, controller.StartupProfiles);
                    Assert(!Directory.Exists(oldPath), "移入后旧目录不应还在。" );
                    Assert(Directory.Exists(newPath), "移入后目标目录应存在。" );
                    Assert(File.Exists(Path.Combine(newPath, StartupProfileFolderMirror.DataFileName)),
                        "目标目录里应有 方案.json。" );

                    Assert(controller.MoveProfileToPlacement(child, null, 0), "撤销位置应成功。" );
                    Assert(Directory.Exists(oldPath), "撤销后原目录应回来。" );
                    Assert(!Directory.Exists(newPath), "撤销后移入目录不应残留。" );
                    Assert(string.IsNullOrWhiteSpace(child.ParentId), "撤销后子方案应回到根级。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("方案移动日志：程序被关掉后，下次启动按日志补回父子关系并清理日志", delegate
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                string parentId;
                string childId;
                using (ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null))
                {
                    StartupProfile parent = controller.CreateProfile("日志父-" + suffix);
                    StartupProfile child = controller.CreateProfile("日志子-" + suffix);
                    Assert(controller.MoveProfileInto(child, parent), "先正常移入。" );
                    parentId = parent.Id;
                    childId = child.Id;

                    // 模拟“目录已经搬了、总 JSON 已写、日志还没来得及删”的崩溃现场。
                    string journalPath = Path.Combine(AppPaths.StartupRestoreDirectory, "方案移动日志.json");
                    PlanFolderMoveJournalEntry entry = new PlanFolderMoveJournalEntry();
                    entry.PlanId = child.Id;
                    entry.OldParentId = null;
                    entry.NewParentId = parent.Id;
                    entry.OldDirectory = "old";
                    entry.NewDirectory = "new";
                    entry.OldSiblingIndex = 0;
                    entry.NewSiblingIndex = 0;
                    entry.RecordedUtc = DateTime.UtcNow;
                    PlanFolderMoveJournal.Append(journalPath, entry);
                    Assert(File.Exists(journalPath), "崩溃现场应有移动日志。" );
                }

                using (ApplicationController reloaded = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null))
                {
                    StartupProfile restoredChild = null;
                    for (int index = 0; index < reloaded.StartupProfiles.Count; index++)
                    {
                        StartupProfile candidate = reloaded.StartupProfiles[index];
                        if (candidate != null && string.Equals(candidate.Id, childId, StringComparison.OrdinalIgnoreCase))
                        {
                            restoredChild = candidate;
                            break;
                        }
                    }
                    Assert(restoredChild != null, "重新打开后子方案应还在。" );
                    AssertEqual(parentId, restoredChild.ParentId);
                    string journalPath = Path.Combine(AppPaths.StartupRestoreDirectory, "方案移动日志.json");
                    Assert(!File.Exists(journalPath), "补写成功后移动日志应删除。" );
                }
            });

            Run("图标布局文件夹：电脑A 可装子布局，删除文件夹时子布局进回收站", delegate
            {
                // 布局控制器需要真实的 DesktopLayoutCoordinator；测试模式仍隔离数据目录。
                ApplicationController controller = new ApplicationController();
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    DesktopTopologyLayout folder = controller.CreateDesktopLayoutFolder("电脑A-" + suffix, null);
                    DesktopTopologyLayout laptop = controller.CreateDesktopLayout("笔记本-" + suffix, null, false);
                    DesktopTopologyLayout monitor = controller.CreateDesktopLayout("接显示器-" + suffix, null, false);
                    Assert(folder != null && laptop != null && monitor != null, "布局和文件夹都应创建成功。" );
                    Assert(!controller.SetActiveDesktopLayout(folder.Id), "文件夹不能被设为当前可恢复布局。" );

                    IList<DesktopTopologyLayout> all = controller.GetDesktopLayouts();
                    for (int index = 0; index < all.Count; index++)
                    {
                        DesktopLayoutFolderStore.Save(all[index], all, false, false);
                    }
                    string laptopOld = DesktopLayoutFolderStore.FolderPathFor(laptop, all);
                    Assert(Directory.Exists(laptopOld), "移动前布局目录应存在：path=" + laptopOld
                        + " 存在=" + Directory.Exists(laptopOld)
                        + " all=" + all.Count
                        + " laptopParent=" + laptop.ParentId
                        + " folderPath=" + DesktopLayoutFolderStore.FolderPathFor(folder, all));

                    Assert(controller.MoveDesktopLayoutInto(laptop, folder), "布局移入文件夹应成功。" );
                    all = controller.GetDesktopLayouts();
                    laptop = DesktopLayoutRepository.FindById(
                        new DesktopLayoutCatalog { Layouts = new List<DesktopTopologyLayout>(all) }, laptop.Id);
                    Assert(laptop != null, "移入后应能重新找到布局。" );
                    AssertEqual(folder.Id, laptop.ParentId);
                    string laptopNew = DesktopLayoutFolderStore.FolderPathFor(laptop, all);
                    Assert(!Directory.Exists(laptopOld), "移入后旧布局目录不应还在。" );
                    Assert(Directory.Exists(laptopNew), "移入后布局应在文件夹目录里。" );
                    Assert(File.Exists(Path.Combine(laptopNew, DesktopLayoutFolderStore.LayoutFileName)),
                        "子布局目录里应有 布局.xml。" );
                    Assert(controller.MoveDesktopLayoutInto(monitor, folder), "第二份布局移入应成功。" );

                    // 重新打开控制器：磁盘上的 布局档案.xml 必须还能还原出 folder → 子布局。
                    using (ApplicationController reloaded = new ApplicationController())
                    {
                        IList<DesktopTopologyLayout> persisted = reloaded.GetDesktopLayouts();
                        DesktopTopologyLayout persistedLaptop = null;
                        DesktopTopologyLayout persistedFolder = null;
                        for (int index = 0; index < persisted.Count; index++)
                        {
                            if (persisted[index] == null)
                            {
                                continue;
                            }
                            if (string.Equals(persisted[index].Id, laptop.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                persistedLaptop = persisted[index];
                            }
                            if (string.Equals(persisted[index].Id, folder.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                persistedFolder = persisted[index];
                            }
                        }
                        Assert(persistedFolder != null && persistedFolder.IsFolder,
                            "重新读取后文件夹应仍是文件夹。" );
                        Assert(persistedLaptop != null && persistedLaptop.ParentId == folder.Id,
                            "重新读取后子布局的 ParentId 应仍指向文件夹。" );
                        Assert(Directory.Exists(DesktopLayoutFolderStore.FolderPathFor(persistedLaptop, persisted)),
                            "重新读取后子布局目录应仍在。" );
                    }

                    int before = controller.RecycleBinEntries.Count;
                    Assert(controller.DeleteDesktopLayout(folder), "删除布局文件夹应成功。" );
                    all = controller.GetDesktopLayouts();
                    Assert(DesktopLayoutRepository.FindById(
                        new DesktopLayoutCatalog { Layouts = new List<DesktopTopologyLayout>(all) }, folder.Id) == null,
                        "布局文件夹应已删除。" );
                    Assert(controller.RecycleBinEntries.Count >= before + 2, "文件夹里的两份布局都应进回收站。" );

                    RecycleBinEntry restoredEntry = null;
                    for (int index = controller.RecycleBinEntries.Count - 1; index >= 0; index--)
                    {
                        RecycleBinEntry entry = controller.RecycleBinEntries[index];
                        if (entry != null && string.Equals(entry.Title, laptop.Name, StringComparison.Ordinal))
                        {
                            restoredEntry = entry;
                            break;
                        }
                    }
                    Assert(restoredEntry != null, "回收站里应能找到笔记本布局。" );
                    Assert(controller.RestoreRecycleBinEntry(restoredEntry), "布局从回收站恢复应成功。" );
                    DesktopTopologyLayout restored = null;
                    all = controller.GetDesktopLayouts();
                    for (int index = 0; index < all.Count; index++)
                    {
                        if (all[index] != null && string.Equals(all[index].Name, laptop.Name, StringComparison.Ordinal))
                        {
                            restored = all[index];
                            break;
                        }
                    }
                    Assert(restored != null, "恢复后的布局应重新出现。" );
                    Assert(string.IsNullOrWhiteSpace(restored.ParentId),
                        "原文件夹已经不在了，子布局应恢复到根目录，不能自动重建文件夹。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("图标布局：重复读取只读档案，不再重写每份 布局.xml", delegate
            {
                ApplicationController controller = new ApplicationController();
                try
                {
                    DesktopTopologyLayout leaf = controller.CreateDesktopLayout(
                        "性能-" + Guid.NewGuid().ToString("N").Substring(0, 6), null, false);
                    Assert(leaf != null, "应能新建布局。" );
                    // 第一次读取会做一次目录维护；先让它跑完，再测之后的纯读取。
                    IList<DesktopTopologyLayout> all = controller.GetDesktopLayouts();
                    string folder = DesktopLayoutFolderStore.FolderPathFor(leaf, all);
                    string xml = Path.Combine(folder, DesktopLayoutFolderStore.LayoutFileName);
                    Assert(File.Exists(xml), "新建布局应立刻生成 布局.xml：path=" + xml);
                    DateTime before = File.GetLastWriteTimeUtc(xml);
                    Thread.Sleep(40);
                    for (int index = 0; index < 5; index++)
                    {
                        controller.GetDesktopLayouts();
                        controller.ActiveDesktopLayoutId.ToString();
                    }
                    DateTime after = File.GetLastWriteTimeUtc(xml);
                    Assert(after == before,
                        "重复读取不能重写 布局.xml（before=" + before.ToString("o")
                        + " after=" + after.ToString("o") + "）。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("图标布局：同级换序只写 布局排列.json，不重写档案和布局.xml", delegate
            {
                ApplicationController controller = new ApplicationController();
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    DesktopTopologyLayout first = controller.CreateDesktopLayout("排序A-" + suffix, null, false);
                    DesktopTopologyLayout second = controller.CreateDesktopLayout("排序B-" + suffix, null, false);
                    DesktopTopologyLayout third = controller.CreateDesktopLayout("排序C-" + suffix, null, false);
                    Assert(first != null && second != null && third != null, "三份布局都应创建成功。" );
                    IList<DesktopTopologyLayout> all = controller.GetDesktopLayouts();
                    string dataRoot = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DATA_DIR");
                    string catalogPath = AppPaths.DesktopLayoutFile;
                    string orderPath = DesktopLayoutOrderStore.PathFor(catalogPath);
                    Assert(File.Exists(catalogPath), "布局档案应存在：path=" + catalogPath);
                    // 记录所有单布局 XML 的时间，确认换序不碰它们。
                    List<string> layoutFiles = new List<string>();
                    for (int index = 0; index < all.Count; index++)
                    {
                        layoutFiles.Add(Path.Combine(
                            DesktopLayoutFolderStore.FolderPathFor(all[index], all),
                            DesktopLayoutFolderStore.LayoutFileName));
                    }
                    DateTime archiveBefore = File.GetLastWriteTimeUtc(catalogPath);
                    List<DateTime> layoutTimesBefore = new List<DateTime>();
                    for (int index = 0; index < layoutFiles.Count; index++)
                    {
                        Assert(File.Exists(layoutFiles[index]), "布局 XML 应存在：" + layoutFiles[index]);
                        layoutTimesBefore.Add(File.GetLastWriteTimeUtc(layoutFiles[index]));
                    }
                    Thread.Sleep(50);

                    Assert(controller.MoveDesktopLayoutRelative(third, first, false), "同级换序应成功。" );
                    Assert(File.Exists(orderPath), "换序后应生成 布局排列.json：path=" + orderPath);
                    Assert(archiveBefore.Ticks == File.GetLastWriteTimeUtc(catalogPath).Ticks,
                        "换序不应重写布局档案（before=" + archiveBefore.ToString("o") + "）。" );
                    for (int index = 0; index < layoutFiles.Count; index++)
                    {
                        Assert(layoutTimesBefore[index].Ticks == File.GetLastWriteTimeUtc(layoutFiles[index]).Ticks,
                            "换序不应重写单布局 XML：" + layoutFiles[index]);
                    }

                    using (ApplicationController reloaded = new ApplicationController())
                    {
                        IList<DesktopTopologyLayout> persisted = reloaded.GetDesktopLayouts();
                        List<string> ids = new List<string>();
                        for (int index = 0; index < persisted.Count; index++)
                        {
                            if (persisted[index] != null
                                && (string.Equals(persisted[index].Id, first.Id, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(persisted[index].Id, second.Id, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(persisted[index].Id, third.Id, StringComparison.OrdinalIgnoreCase)))
                            {
                                ids.Add(persisted[index].Id);
                            }
                        }
                        AssertEqual(3, ids.Count);
                        AssertEqual(third.Id, ids[0]);
                        AssertEqual(first.Id, ids[1]);
                        AssertEqual(second.Id, ids[2]);
                    }
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("图标布局：移入文件夹只写排列 sidecar，重启后父级仍保持", delegate
            {
                ApplicationController controller = new ApplicationController();
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    DesktopTopologyLayout folder = controller.CreateDesktopLayoutFolder("分组-" + suffix, null);
                    DesktopTopologyLayout leaf = controller.CreateDesktopLayout("子布局-" + suffix, null, false);
                    Assert(folder != null && leaf != null, "文件夹和子布局都应创建成功。" );
                    controller.GetDesktopLayouts();
                    string dataRoot = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DATA_DIR");
                    string catalogPath = AppPaths.DesktopLayoutFile;
                    string orderPath = DesktopLayoutOrderStore.PathFor(catalogPath);

                    Assert(controller.MoveDesktopLayoutInto(leaf, folder), "移入文件夹应成功。" );
                    Assert(File.Exists(orderPath), "移入后应生成 布局排列.json。" );
                    Assert(File.ReadAllText(orderPath).IndexOf(folder.Id, StringComparison.OrdinalIgnoreCase) >= 0,
                        "排列 sidecar 里应记录新的父文件夹 Id。" );
                    // 档案本身不应为了“纯展示父级”被重写；把档案单独复制出去读，
                    // 没有 sidecar 时 ParentId 仍是旧的根级。
                    string stalePath = Path.Combine(Path.GetTempPath(), "csk-stale-" + Guid.NewGuid().ToString("N") + ".xml");
                    try
                    {
                        File.Copy(catalogPath, stalePath, true);
                        DesktopLayoutRepository staleRepository = new DesktopLayoutRepository(stalePath);
                        DesktopLayoutCatalog staleCatalog = staleRepository.Load();
                        DesktopTopologyLayout staleLeaf = DesktopLayoutRepository.FindById(staleCatalog, leaf.Id);
                        Assert(staleLeaf != null, "复制出的档案里应能找到子布局。" );
                        Assert(string.IsNullOrWhiteSpace(staleLeaf.ParentId),
                            "布局档案不应为了纯展示父级被整份重写；实际 ParentId=" + staleLeaf.ParentId);
                    }
                    finally
                    {
                        try { File.Delete(stalePath); } catch { }
                        string staleOrder = DesktopLayoutOrderStore.PathFor(stalePath);
                        try { File.Delete(staleOrder); } catch { }
                    }

                    using (ApplicationController reloaded = new ApplicationController())
                    {
                        IList<DesktopTopologyLayout> persisted = reloaded.GetDesktopLayouts();
                        DesktopTopologyLayout persistedLeaf = null;
                        for (int index = 0; index < persisted.Count; index++)
                        {
                            if (persisted[index] != null
                                && string.Equals(persisted[index].Id, leaf.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                persistedLeaf = persisted[index];
                                break;
                            }
                        }
                        Assert(persistedLeaf != null, "重启后应还能找到子布局。" );
                        AssertEqual(folder.Id, persistedLeaf.ParentId);
                    }
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("图标布局：档案比 布局排列.json 新时忽略旧 sidecar", delegate
            {
                string sandbox = Path.Combine(Path.GetTempPath(), "csk-order-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(sandbox);
                try
                {
                    string catalogPath = Path.Combine(sandbox, "布局档案.xml");
                    DesktopLayoutRepository repository = new DesktopLayoutRepository(catalogPath);
                    DesktopLayoutCatalog catalog = new DesktopLayoutCatalog();
                    catalog.Layouts.Add(new DesktopTopologyLayout
                    {
                        Id = "order-a",
                        Name = "A",
                        SortOrder = 1,
                        UpdatedUtc = DateTime.UtcNow,
                        CreatedUtc = DateTime.UtcNow
                    });
                    catalog.Layouts.Add(new DesktopTopologyLayout
                    {
                        Id = "order-b",
                        Name = "B",
                        SortOrder = 2,
                        UpdatedUtc = DateTime.UtcNow,
                        CreatedUtc = DateTime.UtcNow
                    });
                    repository.Save(catalog);
                    // sidecar 记录成 B/A，随后档案又保存过一次（模拟保存图标），
                    // 档案时间比 sidecar 新，读取时必须用档案里的 A/B。
                    catalog.Layouts[0].SortOrder = 2;
                    catalog.Layouts[1].SortOrder = 1;
                    repository.SaveDisplayOrder(catalog);
                    File.SetLastWriteTimeUtc(catalogPath, DateTime.UtcNow.AddMinutes(1));

                    DesktopLayoutCatalog loaded = repository.Load();
                    Assert(loaded.Layouts.Count == 2, "应读回两份布局。" );
                    AssertEqual("A", loaded.Layouts[0].Name);
                    AssertEqual("B", loaded.Layouts[1].Name);
                }
                finally
                {
                    try { Directory.Delete(sandbox, true); } catch { }
                }
            });

            Run("图标布局：含桌面截图的历史孤儿文件夹不会被自动清理", delegate
            {
                string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                DesktopTopologyLayout orphan = new DesktopTopologyLayout();
                orphan.Id = "orphan-" + suffix;
                orphan.Name = "历史截图-" + suffix;
                orphan.TopologySignature = "ORPHAN-" + suffix;
                try
                {
                    Assert(DesktopLayoutFolderStore.Save(orphan, null, false, false),
                        "应能创建一份历史孤儿布局文件夹。" );
                    string folder = DesktopLayoutFolderStore.FolderPath(orphan);
                    Assert(Directory.Exists(folder), "历史孤儿文件夹应存在：" + folder);
                    string preview = Path.Combine(folder, DesktopLayoutFolderStore.PreviewFileName);
                    File.WriteAllBytes(preview, new byte[] { 1, 2, 3, 4 });

                    DesktopLayoutCatalog catalog = new DesktopLayoutRepository().Load();
                    IList<DesktopLayoutOrphan> orphans = DesktopLayoutFolderStore.FindUnreferencedFolders(catalog);
                    for (int index = 0; index < orphans.Count; index++)
                    {
                        Assert(!string.Equals(
                                Path.GetFullPath(orphans[index].FolderPath),
                                Path.GetFullPath(folder),
                                StringComparison.OrdinalIgnoreCase),
                            "含截图的孤儿不应进入待删除清单。" );
                    }

                    DesktopLayoutFolderStore.Sync(catalog);
                    Assert(Directory.Exists(folder), "含截图的孤儿文件夹不能被 Sync 删掉。" );
                    Assert(File.Exists(preview), "含截图的孤儿文件夹里的 桌面.png 必须保留。" );
                }
                finally
                {
                    try
                    {
                        string folder = DesktopLayoutFolderStore.FolderPath(orphan);
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, true);
                        }
                    }
                    catch
                    {
                    }
                }
            });

            Run("图标布局文件夹：不同电脑A/电脑B下可以各有一个同名‘笔记本’", delegate
            {
                DesktopTopologyLayout folderA = new DesktopTopologyLayout
                {
                    Id = "folder-a",
                    Name = "电脑A",
                    IsFolder = true,
                    SortOrder = 0
                };
                DesktopTopologyLayout folderB = new DesktopTopologyLayout
                {
                    Id = "folder-b",
                    Name = "电脑B",
                    IsFolder = true,
                    SortOrder = 1
                };
                DesktopTopologyLayout childA = new DesktopTopologyLayout
                {
                    Id = "child-a",
                    Name = "笔记本",
                    ParentId = "folder-a",
                    SortOrder = 0
                };
                DesktopTopologyLayout childB = new DesktopTopologyLayout
                {
                    Id = "child-b",
                    Name = "笔记本",
                    ParentId = "folder-b",
                    SortOrder = 1
                };
                DesktopLayoutCatalog catalog = new DesktopLayoutCatalog();
                catalog.Layouts.Add(folderA);
                catalog.Layouts.Add(folderB);
                catalog.Layouts.Add(childA);
                catalog.Layouts.Add(childB);
                DesktopLayoutRepository.Normalize(catalog);
                AssertEqual("笔记本", childA.Name);
                AssertEqual("笔记本", childB.Name);
                AssertEqual("folder-a", childA.ParentId);
                AssertEqual("folder-b", childB.ParentId);
            });

            Run("目录移动：Id 对不上时不移动目录，日志可读写并清理", delegate
            {
                string sandbox = Path.Combine(Path.GetTempPath(), "csk-move-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(sandbox);
                try
                {
                    string source = Path.Combine(sandbox, "旧目录");
                    string target = Path.Combine(sandbox, "新父", "旧目录");
                    Directory.CreateDirectory(source);
                    File.WriteAllText(Path.Combine(source, "方案.json"), "{\"Id\":\"other\"}");
                    PlanFolderDirectoryMoveResult rejected = PlanFolderDirectoryMover.Move(
                        source, target, "expected", "方案.json",
                        delegate(string path) { return "other"; });
                    Assert(!rejected.Moved, "Id 对不上时不能移动目录。" );
                    Assert(Directory.Exists(source), "Id 对不上时源目录应保留。" );

                    string journalPath = Path.Combine(sandbox, "方案移动日志.json");
                    PlanFolderMoveJournalEntry entry = new PlanFolderMoveJournalEntry
                    {
                        PlanId = "p1",
                        OldParentId = null,
                        NewParentId = "p0",
                        OldDirectory = source,
                        NewDirectory = target,
                        NewSiblingIndex = 2,
                        RecordedUtc = DateTime.UtcNow
                    };
                    PlanFolderMoveJournal.Append(journalPath, entry);
                    PlanFolderMoveJournalData journal = PlanFolderMoveJournal.Load(journalPath);
                    AssertEqual(1, journal.Entries.Count);
                    AssertEqual("p1", journal.Entries[0].PlanId);
                    PlanFolderMoveJournal.Clear(journalPath);
                    Assert(!File.Exists(journalPath), "写成功后应能删除移动日志。" );
                }
                finally
                {
                    try { Directory.Delete(sandbox, true); } catch { }
                }
            });

            Run("自启动方案：递归恢复先自己的项目再子方案", delegate
            {
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    string suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
                    ApplicationPlan a = controller.CreateApplicationPlan("恢复树-A-" + suffix);
                    ApplicationPlan b = controller.CreateApplicationPlan("恢复树-B-" + suffix, a, false);
                    ApplicationPlan c = controller.CreateApplicationPlan("恢复树-C-" + suffix, b, false);
                    a.Items.Add(NewApplicationItem("A1"));
                    a.Items.Add(NewApplicationItem("A2"));
                    b.Items.Add(NewApplicationItem("B1"));
                    c.Items.Add(NewApplicationItem("C1"));

                    SessionSnapshot snapshot = ApplicationPlanRestoreAdapter.BuildSnapshot(a, controller.ApplicationPlans);
                    AssertEqual("A1,A2,B1,C1", JoinItemNames(snapshot.Items));
                }
                finally
                {
                    controller.Dispose();
                }
            });

        }
    }
}
