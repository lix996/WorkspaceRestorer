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

        private static void RunMarkersAndSearchTests()
        {
            Run("今日标记：自启动应用也认这个标记（同一天再打开不重复启动，2026-10-19）", delegate
            {
                // 用户原话：“临时里明明有今天的标记文件了（这个标记功能，要适用于全部的打开应用的重启功能），
                // 但是重新关闭应用再打开，自启动应用居然还能重启。”
                ResetAutoRestoreMarkers();
                string originalSession = Environment.GetEnvironmentVariable(
                    "WORKSPACE_RESTORER_LOGON_SESSION");
                Environment.SetEnvironmentVariable(
                    "WORKSPACE_RESTORER_LOGON_SESSION", "marker-test-session");
                TestDesktopApplicationService applicationService = new TestDesktopApplicationService();
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    new RecordingRestoreService(),
                    applicationService,
                    null,
                    null);
                try
                {
                    ApplicationPlan plan = controller.CreateApplicationPlan("标记测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "甲", TargetPath = "C:\\a.exe", IsEnabled = true });
                    controller.SetActiveApplicationPlan(plan);
                    controller.Settings.ApplicationsAutoRestore = true;
                    controller.Settings.AutoRestore = false;

                    controller.BeginStartup();
                    Assert(applicationService.LaunchCallCount == 1, "第一次打开没有按开关启动自启动方案。" );
                    Assert(WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature),
                        "启动完没有在当天标记里记上“已完成：自启动应用”。" );
                    Assert(!WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.WorkProfileFeature),
                        "只做了自启动方案，却把工作方案也记成做过了（会把自动恢复挡掉）。" );

                    // 2026-10-22：把标记改成“昨天的日期”，但登录会话/开机时刻不变。
                    // 跨天但没有关机时仍应算做过，不能再触发一次。
                    string todayMarker = WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath();
                    string markerDirectory = Path.GetDirectoryName(todayMarker);
                    string markerContent = File.ReadAllText(todayMarker);
                    string yesterdayMarker = Path.Combine(markerDirectory,
                        DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd") + ".标记");
                    File.WriteAllText(yesterdayMarker, markerContent);
                    File.Delete(todayMarker);
                    Assert(WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature),
                        "跨天但没关机，标记被日期变化重置了（凌晨关软件再开就会误恢复）。" );
                    File.Delete(yesterdayMarker);
                    File.WriteAllText(todayMarker, markerContent);

                    // 模拟换了登录会话（关机/重启/快速启动）：旧标记必须失效。
                    Environment.SetEnvironmentVariable(
                        "WORKSPACE_RESTORER_LOGON_SESSION", "marker-test-reboot");
                    Assert(!WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature),
                        "登录会话变了（模拟关机重启），旧标记仍然算做过。" );
                    Environment.SetEnvironmentVariable(
                        "WORKSPACE_RESTORER_LOGON_SESSION", "marker-test-session");

                    controller.BeginStartup();
                    Assert(applicationService.LaunchCallCount == 1,
                        "同一天第二次打开又把自启动方案启动了一遍（标记没生效）。" );
                    Assert(controller.StatusText.IndexOf("本次开机已经自动启动过", StringComparison.Ordinal) >= 0,
                        "第二次打开应当说明“本次开机已经自动启动过”，实际：" + controller.StatusText);

                    // 手动点「立即恢复」是用户主动操作，不写“今天已经做过”的标记。
                    ResetAutoRestoreMarkers();
                    controller.StartApplicationsOnLaunch(true);
                    Assert(!WorkspaceRestorer.Infrastructure.AutoRestoreMarker.HasDoneToday(
                            WorkspaceRestorer.Infrastructure.AutoRestoreMarker.ApplicationsFeature),
                        "手动「立即恢复」不该写“今天已经做过”的标记。" );
                }
                finally
                {
                    controller.Dispose();
                    ResetAutoRestoreMarkers();
                    Environment.SetEnvironmentVariable(
                        "WORKSPACE_RESTORER_LOGON_SESSION", originalSession);
                }
            });

            Run("今日标记：工作方案与自启动应用各记一笔，互不遮挡（2026-10-19）", delegate
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
                    controller.UpdateTiming(0, 0);
                    StartupProfile profile = controller.CreateProfile("标记测试方案");
                    profile.Items.Add(new SessionItem { DisplayName = "测试项", ExecutablePath = "C:\\test.exe" });
                    controller.SetActiveProfile(profile);
                    ApplicationPlan plan = controller.CreateApplicationPlan("标记测试自启动");
                    plan.Items = new List<ApplicationPlanItem>();
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "甲", TargetPath = "C:\\a.exe", IsEnabled = true });
                    controller.SetActiveApplicationPlan(plan);
                    controller.Settings.AutoRestore = true;
                    controller.Settings.ApplicationsAutoRestore = true;

                    controller.BeginStartup();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                    while (restoreService.CallCount == 0 && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(20);
                    }
                    Assert(restoreService.CallCount == 1, "两个开关都开着，打开程序却没有恢复工作方案。" );
                    Assert(applicationService.LaunchCallCount == 1, "两个开关都开着，打开程序却没有启动自启动方案。" );

                    controller.BeginStartup();
                    Thread.Sleep(150);
                    Assert(restoreService.CallCount == 1, "同一天第二次打开又恢复了一遍工作方案。" );
                    Assert(applicationService.LaunchCallCount == 1, "同一天第二次打开又启动了一遍自启动方案。" );
                }
                finally
                {
                    controller.Dispose();
                    ResetAutoRestoreMarkers();
                }
            });

            Run("今日标记：用户删掉临时文件后，下次打开算本次开机第一次（2026-11-17）", delegate
            {
                // 用户要求：临时文件被删掉就等于没有记录，视为本次开机第一次打开，
                // 自动恢复会再做一次，并重新生成临时文件。
                ResetAutoRestoreMarkers();
                string originalSession = Environment.GetEnvironmentVariable(
                    "WORKSPACE_RESTORER_LOGON_SESSION");
                Environment.SetEnvironmentVariable(
                    "WORKSPACE_RESTORER_LOGON_SESSION", "marker-deleted-test-session");
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
                    StartupProfile profile = controller.CreateProfile("删标记测试");
                    profile.Items.Add(new SessionItem { DisplayName = "测试项", ExecutablePath = "C:\\test.exe" });
                    controller.SetActiveProfile(profile);
                    controller.Settings.AutoRestore = true;
                    controller.Settings.ApplicationsAutoRestore = false;

                    controller.BeginStartup();
                    Assert(restoreService.CallCount == 1, "第一次打开没有自动恢复。" );
                    Assert(File.Exists(WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath()),
                        "第一次打开后没有生成临时标记文件。" );

                    // 模拟用户在资源管理器里手动删掉临时文件。
                    ResetAutoRestoreMarkers();
                    Assert(!File.Exists(WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath()),
                        "测试准备失败：临时标记文件没删掉。" );

                    controller.BeginStartup();
                    Assert(restoreService.CallCount == 2,
                        "临时文件被删掉后，下次打开没有按“本次开机第一次”重新自动恢复。" );
                    Assert(File.Exists(WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath()),
                        "重新自动恢复后没有重新生成临时标记文件。" );
                }
                finally
                {
                    controller.Dispose();
                    ResetAutoRestoreMarkers();
                    Environment.SetEnvironmentVariable(
                        "WORKSPACE_RESTORER_LOGON_SESSION", originalSession);
                }
            });

            Run("今日标记：旧版本写的标记（没有“已完成：”明细）当作今天都做过了（2026-10-19）", delegate
            {
                ResetAutoRestoreMarkers();
                string todayPath = WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath();
                Directory.CreateDirectory(Path.GetDirectoryName(todayPath));
                File.WriteAllText(todayPath, "这一天的“启动程序后自动恢复工作方案”已经执行过了。", new UTF8Encoding(false));

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
                    controller.UpdateTiming(0, 0);
                    StartupProfile profile = controller.CreateProfile("旧标记测试");
                    profile.Items.Add(new SessionItem { DisplayName = "测试项", ExecutablePath = "C:\\test.exe" });
                    controller.SetActiveProfile(profile);
                    ApplicationPlan plan = controller.CreateApplicationPlan("旧标记测试");
                    plan.Items = new List<ApplicationPlanItem>();
                    plan.Items.Add(new ApplicationPlanItem { DisplayName = "甲", TargetPath = "C:\\a.exe", IsEnabled = true });
                    controller.SetActiveApplicationPlan(plan);
                    controller.Settings.AutoRestore = true;
                    controller.Settings.ApplicationsAutoRestore = true;

                    controller.BeginStartup();
                    Thread.Sleep(200);
                    Assert(restoreService.CallCount == 0, "升级前写的标记没生效：又恢复了一遍工作方案。" );
                    Assert(applicationService.LaunchCallCount == 0,
                        "升级前写的标记没管自启动应用：又启动了一遍（用户看到的就是这个）。" );
                }
                finally
                {
                    controller.Dispose();
                    ResetAutoRestoreMarkers();
                }
            });

            Run("开机自启动与自动恢复是两个独立开关", delegate
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
                    controller.SetAutoRestore(false);
                    controller.SetAutoStart(true);
                    Assert(controller.Settings.AutoStart, "未开启自动恢复时也应当允许开机自启动。" );
                    controller.BeginStartup();
                    Thread.Sleep(50);
                    Assert(restoreService.CallCount == 0, "关闭自动恢复后，打开程序仍然触发了恢复。" );

                    controller.SetAutoRestore(true);
                    controller.SetAutoStart(true);
                    Assert(controller.Settings.AutoStart, "开启自动恢复后无法选择登录自启动。" );
                    controller.SetAutoRestore(false);
                    Assert(controller.Settings.AutoStart, "取消自动恢复不应连带关闭开机自启动。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("没有用户选择时默认最新方案，有用户选择时按用户的来", delegate
            {
                // 用户要求（2026-10-04 23:16）：启动与恢复要默认最新的工作方案，
                // 如果用户改过（选过别的方案）就按用户的选择。
                DateTime baseTime = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
                StartupProfileCatalog catalog = new StartupProfileCatalog();
                catalog.Profiles.Add(new StartupProfile { Id = "p1", Name = "旧方案", CreatedAtUtc = baseTime, UpdatedAtUtc = baseTime });
                catalog.Profiles.Add(new StartupProfile { Id = "p2", Name = "新方案", CreatedAtUtc = baseTime, UpdatedAtUtc = baseTime.AddHours(3) });
                catalog.Profiles.Add(new StartupProfile { Id = "p3", Name = "中间方案", CreatedAtUtc = baseTime, UpdatedAtUtc = baseTime.AddHours(1) });
                catalog.ActiveProfileId = null;

                Assert(StartupProfileDefaults.ApplyActiveProfileId(catalog), "没有当前方案时应当自动选出一个。" );
                AssertEqual("p2", catalog.ActiveProfileId ?? string.Empty);
                AssertEqual("p2", StartupProfileDefaults.ResolveActiveProfileId(catalog) ?? string.Empty);

                // 用户选过的方案优先，哪怕它不是最新的。
                catalog.ActiveProfileId = "p1";
                Assert(!StartupProfileDefaults.ApplyActiveProfileId(catalog), "用户已选方案时不应被改掉。" );
                AssertEqual("p1", catalog.ActiveProfileId ?? string.Empty);

                // 用户选过的方案被删掉后，回落到最新的那个。
                catalog.ActiveProfileId = "已删除的方案";
                Assert(StartupProfileDefaults.ApplyActiveProfileId(catalog), "用户选过的方案不存在时应当回落到最新方案。" );
                AssertEqual("p2", catalog.ActiveProfileId ?? string.Empty);

                // 空列表不报错，返回 null。
                StartupProfileCatalog empty = new StartupProfileCatalog();
                AssertEqual(string.Empty, StartupProfileDefaults.ResolveActiveProfileId(empty) ?? string.Empty);
                AssertEqual(string.Empty, StartupProfileDefaults.ResolveActiveProfileId(null) ?? string.Empty);

                // “最新”还要看条目里记录的启动时间：方案自身的 UpdatedAtUtc 可能只是
                // 勾选/排序刷新出来的，条目时间才是真正的保存时间。
                StartupProfileCatalog itemsCatalog = new StartupProfileCatalog();
                StartupProfile olderShell = new StartupProfile { Id = "a", Name = "外壳时间新", CreatedAtUtc = baseTime, UpdatedAtUtc = baseTime.AddDays(5) };
                StartupProfile reallyNewer = new StartupProfile { Id = "b", Name = "真的更新", CreatedAtUtc = baseTime, UpdatedAtUtc = baseTime.AddHours(1) };
                reallyNewer.Items.Add(new SessionItem { Id = "i1", StartTimeUtc = baseTime.AddDays(9) });
                itemsCatalog.Profiles.Add(olderShell);
                itemsCatalog.Profiles.Add(reallyNewer);
                itemsCatalog.ActiveProfileId = null;
                Assert(StartupProfileDefaults.ApplyActiveProfileId(itemsCatalog), "应当按条目时间选出真正最新的方案。" );
                AssertEqual("b", itemsCatalog.ActiveProfileId ?? string.Empty);
            });

            Run("方案卡片的时间文本：本机时区的“追加当前时间”格式（不写死北京时间）", delegate
            {
                // 用户要求（2026-10-04 23:16）：“在启动项目后面加上日期”。
                // 用户要求（2026-10-16）：“下面小行的格式用追加当前时间的格式，
                // 并且一定是电脑上当前时间的格式（如果是中国，那就是北京时间，如果是美国，
                // 那就是美国那边的时间，千万不要固定写死成北京时间）。”
                // → 卡片第二行 = “N 个启动项目 · <时区名> yyyy/M/d日 H:mm分”，时区取本机。
                StartupProfile profile = new StartupProfile { Id = "p", Name = "方案" };
                profile.UpdatedAtUtc = new DateTime(2026, 10, 4, 23, 16, 0, DateTimeKind.Utc);
                string text = StartupProfileCardText.BuildCountText(profile);
                string expected = "0 个启动项目 · " + FormatComputerLocalStampByReflection(profile.UpdatedAtUtc);
                AssertEqual(expected, text);
                Assert(text.Contains(" · "), "项目数与时间之间应当用居中点分隔。" );

                // 【关键】绝不允许写死成北京时间：本机时区不是中国时区时，
                // 文本里就不该出现“北京时间”。
                if (!string.Equals(TimeZoneInfo.Local.Id, "China Standard Time", StringComparison.OrdinalIgnoreCase))
                {
                    Assert(text.IndexOf("北京时间", StringComparison.Ordinal) < 0,
                        "本机时区不是中国时区，却硬写成了“北京时间”：" + text);
                }
                // 时区名必须跟着本机走。
                Assert(text.Contains(FormatZoneLabelByReflection(TimeZoneInfo.Local)),
                    "时间文本里的时区名必须来自本机时区：" + text);

                // 旧记录没有时间时不显示日期，但依然显示项目数。
                StartupProfile legacy = new StartupProfile { Id = "legacy", Name = "旧方案" };
                legacy.UpdatedAtUtc = default(DateTime);
                AssertEqual("0 个启动项目", StartupProfileCardText.BuildCountText(legacy));
            });

            Run("公共时间格式：北京时间、非中国时区、旧后缀和反复追加都只有一套规则", delegate
            {
                TimeZoneInfo china = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                DateTime local = new DateTime(2026, 11, 4, 15, 3, 0, DateTimeKind.Unspecified);
                AssertEqual("北京时间 2026/11/4日 15:03分", TimeText.FormatLocalStamp(local, china));

                // 非中国时区不能写死“北京时间”，标签来自本机时区。
                TimeZoneInfo pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
                string pacificText = TimeText.FormatLocalStamp(local, pacific);
                Assert(pacificText.IndexOf("北京时间", StringComparison.Ordinal) < 0,
                    "非中国时区不应出现“北京时间”： " + pacificText);
                Assert(pacificText.Contains(TimeText.FormatZoneLabel(pacific)),
                    "时间文本里应包含本机时区名： " + pacificText);

                // 三种历史后缀都要能剥掉，反复追加不叠加。
                AssertEqual("方案", TimeText.StripStamp("方案_北京时间 2026/11/4日 15:03分"));
                AssertEqual("方案", TimeText.StripStamp("方案_2026/11/4 15:03"));
                AssertEqual("方案", TimeText.StripStamp("方案_北京时间2026.11.04日，15:03分"));
                string once = TimeText.AppendStamp("方案", "工作方案", local, china);
                string twice = TimeText.AppendStamp(once, "工作方案", local.AddMinutes(1), china);
                AssertEqual("方案_北京时间 2026/11/4日 15:04分", twice);
                AssertEqual(1, twice.Split('_').Length - 1);

                // 默认值和 UTC 转换：没有时间就不显示，不抛异常。
                AssertEqual(string.Empty, TimeText.FormatUtcAsComputerLocalStamp(default(DateTime)));
                DateTime utc = new DateTime(2026, 11, 4, 7, 3, 0, DateTimeKind.Utc);
                AssertEqual(
                    TimeText.FormatComputerLocalStamp(utc.ToLocalTime()),
                    TimeText.FormatUtcAsComputerLocalStamp(utc));

                // 启动与恢复卡片和公共方法必须完全一致（自启动应用也走同一个方法）。
                StartupProfile profile = new StartupProfile { Id = "time", Name = "时间" };
                profile.UpdatedAtUtc = utc;
                AssertEqual(
                    TimeText.FormatUtcAsComputerLocalStamp(utc),
                    StartupProfileCardText.FormatSavedAt(profile));
            });

            Run("工作方案可以复制粘贴成独立的一份", delegate
            {
                // 用户要求（2026-10-08）：“工作方案可以 ctrl+c，ctrl+v，复制粘贴”。
                StartupProfile source = new StartupProfile { Id = "src", Name = "工作方案甲" };
                source.Items.Add(new SessionItem { Id = "i1", DisplayName = "中转站", TargetPath = @"C:\Users\MR\Desktop\中转站" });
                source.Items.Add(new SessionItem { Id = "i2", DisplayName = "网页", Url = "https://example.com", Kind = SessionItemKind.WebPage, ParentId = "i1" });
                source.Resequence();

                string text = StartupProfileClipboard.Serialize(source);
                Assert(!string.IsNullOrEmpty(text), "序列化结果不应为空。" );

                StartupProfile copy = StartupProfileClipboard.DeserializeAsCopy(text, null);
                Assert(copy != null, "应当能还原出一份新方案。" );
                Assert(copy.Id != source.Id, "副本必须是新的方案 Id。" );
                AssertEqual("工作方案甲 副本", copy.Name);
                AssertEqual(2, copy.Items.Count);
                AssertEqual("中转站", copy.Items[0].DisplayName ?? string.Empty);
                AssertEqual("https://example.com", copy.Items[1].Url ?? string.Empty);
                // 【2026-10-18 修】以前这里断言 ParentId 还是 "i1" —— 那正是 bug 本身：
                // 副本里所有条目都换了新 Id，ParentId 却还指着原件的 Id，于是网页找不到父项，
                // 粘出来就只剩一个空浏览器。正确口径是：子项的 ParentId 必须等于**副本里那个父项**的 Id。
                AssertEqual(copy.Items[0].Id, copy.Items[1].ParentId ?? string.Empty);
                Assert(copy.Items[1].ParentId != "i1", "副本的 ParentId 不能再指向原件的条目 Id。" );
                Assert(copy.Items[0].Id != source.Items[0].Id, "副本的条目也要是新的 Id（改副本不能影响原件）。" );
                Assert(copy.Items[0].Sequence == 1 && copy.Items[1].Sequence == 2, "副本应当重新编号。" );

                // 指定名字时用指定的名字。
                StartupProfile named = StartupProfileClipboard.DeserializeAsCopy(text, "我的副本");
                Assert(named != null && named.Name == "我的副本", "应当能指定副本名字。" );

                // 不像本程序复制的内容要安全地失败（不能抛异常）。
                Assert(StartupProfileClipboard.DeserializeAsCopy("这不是方案", null) == null, "非法内容应返回 null。" );
                Assert(StartupProfileClipboard.DeserializeAsCopy("", null) == null, "空内容应返回 null。" );

                // 改副本不影响原件。
                copy.Items[0].DisplayName = "改过的名字";
                AssertEqual("中转站", source.Items[0].DisplayName ?? string.Empty);
            });

            Run("复制粘贴：两个浏览器各自的网页都要带过来（不能只剩空浏览器）", delegate
            {
                // 用户 2026-10-18 原话：
                // “复制粘贴功能，在启动与恢复上…没法完全复制，例如有两个edge浏览器，各打开了多个网址，
                //  那么复制后，某个浏览器内就没有网址了，只显示单独的浏览器。”
                StartupProfile source = new StartupProfile { Id = "twoBrowsers", Name = "两个浏览器" };
                source.Items.Add(new SessionItem
                {
                    Id = "edgeWin1", Kind = SessionItemKind.Application, DisplayName = "Microsoft Edge (1)",
                    ExecutablePath = "C:\\missing-edge.exe", BrowserName = "Microsoft Edge",
                    BrowserGroupKey = "edge#0", BrowserWindowIndex = 0, CaptureSource = "BrowserWindow"
                });
                source.Items.Add(new SessionItem
                {
                    Id = "tabA", Kind = SessionItemKind.WebPage, DisplayName = "页 A", Url = "https://example.test/a",
                    ParentId = "edgeWin1", BrowserName = "Microsoft Edge", BrowserGroupKey = "edge#0",
                    BrowserWindowIndex = 0, BrowserTabIndex = 0, BrowserExecutablePath = "C:\\missing-edge.exe"
                });
                source.Items.Add(new SessionItem
                {
                    Id = "tabB", Kind = SessionItemKind.WebPage, DisplayName = "页 B", Url = "https://example.test/b",
                    ParentId = "edgeWin1", BrowserName = "Microsoft Edge", BrowserGroupKey = "edge#0",
                    BrowserWindowIndex = 0, BrowserTabIndex = 1, BrowserExecutablePath = "C:\\missing-edge.exe"
                });
                source.Items.Add(new SessionItem
                {
                    Id = "edgeWin2", Kind = SessionItemKind.Application, DisplayName = "Microsoft Edge (2)",
                    ExecutablePath = "C:\\missing-edge.exe", BrowserName = "Microsoft Edge",
                    BrowserGroupKey = "edge#1", BrowserWindowIndex = 1, CaptureSource = "BrowserWindow"
                });
                source.Items.Add(new SessionItem
                {
                    Id = "tabC", Kind = SessionItemKind.WebPage, DisplayName = "页 C", Url = "https://example.test/c",
                    ParentId = "edgeWin2", BrowserName = "Microsoft Edge", BrowserGroupKey = "edge#1",
                    BrowserWindowIndex = 1, BrowserTabIndex = 0, BrowserExecutablePath = "C:\\missing-edge.exe"
                });
                source.Items.Add(new SessionItem
                {
                    Id = "tabD", Kind = SessionItemKind.WebPage, DisplayName = "页 D", Url = "https://example.test/d",
                    ParentId = "edgeWin2", BrowserName = "Microsoft Edge", BrowserGroupKey = "edge#1",
                    BrowserWindowIndex = 1, BrowserTabIndex = 1, BrowserExecutablePath = "C:\\missing-edge.exe"
                });
                source.Resequence();

                string text = StartupProfileClipboard.Serialize(source);
                StartupProfile copy = StartupProfileClipboard.DeserializeAsCopy(text, null);
                Assert(copy != null, "应当能还原出副本。" );
                AssertEqual(6, copy.Items.Count);

                List<SessionItem> parents = copy.Items.FindAll(delegate(SessionItem item)
                {
                    return item != null && item.Kind == SessionItemKind.Application;
                });
                AssertEqual(2, parents.Count);
                for (int index = 0; index < parents.Count; index++)
                {
                    SessionItem parent = parents[index];
                    List<SessionItem> children = copy.Items.FindAll(delegate(SessionItem item)
                    {
                        return item != null
                            && item.Kind == SessionItemKind.WebPage
                            && string.Equals(item.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase);
                    });
                    AssertEqual(2, children.Count);
                }
                List<SessionItem> pages = copy.Items.FindAll(delegate(SessionItem item)
                {
                    return item != null && item.Kind == SessionItemKind.WebPage;
                });
                AssertEqual(4, pages.Count);
                for (int index = 0; index < pages.Count; index++)
                {
                    Assert(!string.IsNullOrWhiteSpace(pages[index].ParentId), "副本里的网页必须仍然挂在某个浏览器下。" );
                    Assert(!string.Equals(pages[index].ParentId, "edgeWin1", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pages[index].ParentId, "edgeWin2", StringComparison.OrdinalIgnoreCase),
                        "副本里的 ParentId 不能再指向原件的浏览器 id。" );
                }
                // 两组的网址一个都不能少（用户看到的“某个浏览器里没网址了”就是这里少了）。
                AssertEqual(1, copy.Items.FindAll(delegate(SessionItem item)
                {
                    return item != null && string.Equals(item.Url, "https://example.test/a", StringComparison.Ordinal);
                }).Count);
                AssertEqual(1, copy.Items.FindAll(delegate(SessionItem item)
                {
                    return item != null && string.Equals(item.Url, "https://example.test/d", StringComparison.Ordinal);
                }).Count);
            });

            Run("搜索工作方案：名字和日期合在一起搜", delegate
            {
                // 用户要求（2026-10-08）：把"按名字搜"和"按日期搜"合在一起。
                StartupProfile a = new StartupProfile { Id = "a", Name = "comfyui 工作流" };
                a.UpdatedAtUtc = new DateTime(2026, 10, 8, 15, 16, 0, DateTimeKind.Utc);
                StartupProfile b = new StartupProfile { Id = "b", Name = "AI 方案" };
                b.UpdatedAtUtc = new DateTime(2026, 9, 24, 5, 30, 0, DateTimeKind.Utc);
                List<StartupProfile> all = new List<StartupProfile>();
                all.Add(a);
                all.Add(b);

                Assert(StartupProfileSearch.Matches(a, "comfy"), "名字里的一段文字应当能搜到。" );
                Assert(StartupProfileSearch.Matches(a, "COMFY"), "名字搜索应当忽略大小写。" );
                Assert(!StartupProfileSearch.Matches(b, "comfy"), "不匹配的方案不应该被搜出来。" );
                Assert(StartupProfileSearch.Matches(a, string.Empty), "关键字为空时应当全部匹配。" );
                Assert(StartupProfileSearch.Matches(a, "   "), "只有空格时也应当全部匹配。" );

                string cardStamp = StartupProfileCardText.FormatSavedAt(a);
                Assert(StartupProfileSearch.Matches(a, cardStamp), "卡片上显示的日期整串应当能搜到。" );
                // 新格式是“北京时间 2026/10/8日 23:16分”，取其中的日期部分来搜。
                // 各种写法（2026/10/8、2026-10-08、20261008）都应当命中。
                Assert(StartupProfileSearch.Matches(a, "2026/10/8"), "斜杠日期应当能搜到。" );
                Assert(StartupProfileSearch.Matches(a, "2026-10-08"), "横杠日期应当能搜到。" );
                Assert(StartupProfileSearch.Matches(a, "20261008"), "纯数字日期应当能搜到。" );
                Assert(StartupProfileSearch.Matches(a, "10-08"), "只写月-日（4 位数字）应当能搜到。" );
                Assert(StartupProfileSearch.Matches(a, "2026年10月8日"), "中文年月日应当能搜到。" );
                Assert(!StartupProfileSearch.Matches(b, "2026/10/8"), "另一天的方案不应该被搜出来。" );

                List<StartupProfile> hit = StartupProfileSearch.Filter(all, "2026/10/8");
                AssertEqual(1, hit.Count);
                AssertEqual("a", hit[0].Id ?? string.Empty);
                AssertEqual(2, StartupProfileSearch.Filter(all, string.Empty).Count);
            });

            Run("搜索时间可以勾选：不勾只按名字，勾上以后 15 能搜到 15日/15时/15分", delegate
            {
                // 用户要求（2026-10-17）：“搜索，加上可以搜索时间，下面添加一个勾选选项（默认不勾选），
                // 可以通过勾选时间进行搜索，例如搜索 15，那么就能弹出 15 日/月/分等的工作方案。”
                // 时间是本地时间，所以这里都用本地时间构造再转成 UTC，换时区跑也稳。
                StartupProfile day15 = new StartupProfile { Id = "day15", Name = "甲方案" };
                day15.UpdatedAtUtc = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
                StartupProfile hour15 = new StartupProfile { Id = "hour15", Name = "乙方案" };
                hour15.UpdatedAtUtc = new DateTime(2026, 10, 2, 15, 20, 0, DateTimeKind.Local).ToUniversalTime();
                StartupProfile minute5 = new StartupProfile { Id = "minute5", Name = "丙方案" };
                minute5.UpdatedAtUtc = new DateTime(2026, 10, 3, 9, 5, 0, DateTimeKind.Local).ToUniversalTime();
                List<StartupProfile> all = new List<StartupProfile>();
                all.Add(day15);
                all.Add(hour15);
                all.Add(minute5);

                // ① 不勾（默认）：只按名字，纯数字关键字搜不到时间。
                Assert(!StartupProfileSearch.Matches(day15, "15", false), "不勾时间时，15 不应该按日期命中。" );
                Assert(!StartupProfileSearch.Matches(day15, "2026-10-15", false), "不勾时间时，完整日期也不应该命中。" );
                Assert(StartupProfileSearch.Matches(day15, "甲", false), "不勾时间时，名字搜索必须照旧。" );
                AssertEqual(0, StartupProfileSearch.Filter(all, "15", false).Count);

                // ② 勾上：15 命中“15 日”“15 时”，也命中 2026 年的后两位。
                Assert(StartupProfileSearch.Matches(day15, "15", true), "勾上时间后，15 应当命中 15 日。" );
                Assert(StartupProfileSearch.Matches(hour15, "15", true), "勾上时间后，15 应当命中 15 时。" );
                Assert(!StartupProfileSearch.Matches(minute5, "15", true), "丙方案（3 日 9:05）不该被 15 命中。" );
                Assert(StartupProfileSearch.Matches(minute5, "5", true), "勾上时间后，5 应当命中 5 分。" );
                Assert(!StartupProfileSearch.Matches(minute5, "5", false), "不勾时间时，5 不应该按分钟命中。" );
                Assert(StartupProfileSearch.Matches(day15, "10", true), "10 应当命中 10 月。" );
                Assert(StartupProfileSearch.Matches(day15, "26", true), "26 应当命中 2026 年（后两位）。" );
                Assert(StartupProfileSearch.Matches(day15, "2026", true), "2026 应当命中整年。" );
                Assert(StartupProfileSearch.Matches(day15, "2026-10-15", true), "勾上时间后，完整日期必须能命中。" );
                Assert(!StartupProfileSearch.Matches(hour15, "2026-10-15", true), "另一天的方案不能被同一天的日期命中。" );
                AssertEqual(2, StartupProfileSearch.Filter(all, "15", true).Count);

                // ③ 关键字为空时两种模式都全部匹配；只有空白也一样。
                AssertEqual(3, StartupProfileSearch.Filter(all, string.Empty, false).Count);
                AssertEqual(3, StartupProfileSearch.Filter(all, "   ", false).Count);
            });

            Run("搜索多个词要同时命中：方案 15分 只找名字带“方案”且保存时间是 15 分的那份", delegate
            {
                // 用户要求（2026-10-17 第 45 批）：“搜索还可以多个词组合搜索，分隔通过空格分隔，
                // 例如：搜索『方案 15分』就跳出对应的工作方案，日期为 15 分的。”
                StartupProfile planA = new StartupProfile { Id = "a", Name = "方案A" };
                planA.UpdatedAtUtc = new DateTime(2026, 10, 17, 9, 15, 0, DateTimeKind.Local).ToUniversalTime();
                StartupProfile planB = new StartupProfile { Id = "b", Name = "方案B" };
                planB.UpdatedAtUtc = new DateTime(2026, 10, 17, 9, 40, 0, DateTimeKind.Local).ToUniversalTime();
                StartupProfile other = new StartupProfile { Id = "c", Name = "别的东西" };
                other.UpdatedAtUtc = new DateTime(2026, 10, 17, 9, 15, 0, DateTimeKind.Local).ToUniversalTime();

                // ① 两个词是“并且”关系
                Assert(StartupProfileSearch.Matches(planA, "方案 15分", true), "“方案 15分”应当命中「方案A」（15 分）。" );
                Assert(!StartupProfileSearch.Matches(planB, "方案 15分", true), "「方案B」是 40 分，不该命中。" );
                Assert(!StartupProfileSearch.Matches(other, "方案 15分", true), "名字里没有“方案”，不该命中。" );
                Assert(!StartupProfileSearch.Matches(planA, "方案 15分", false), "不勾时间时，15分 没有名字可匹配，不该命中。" );
                Assert(StartupProfileSearch.Matches(planA, "方案", false), "不勾时间时，只搜名字仍然要能用。" );

                // ② 空格、全角空格、逗号都算分隔符
                AssertEqual(2, StartupProfileSearch.SplitTerms("  方案\t15分　").Count);
                AssertEqual(2, StartupProfileSearch.SplitTerms("方案，15分").Count);
                AssertEqual(0, StartupProfileSearch.SplitTerms("   ").Count);

                // ③ 带单位的时间词：分 / 时 / 日 / 月 / 年
                Assert(StartupProfileSearch.Matches(planA, "15分", true), "15分 应当命中 9:15。" );
                Assert(!StartupProfileSearch.Matches(planA, "45分", true), "45分 不该命中 9:15。" );
                Assert(StartupProfileSearch.Matches(planA, "9时", true), "9时 应当命中 9:15。" );
                Assert(StartupProfileSearch.Matches(planA, "17日", true), "17日 应当命中 10/17。" );
                Assert(StartupProfileSearch.Matches(planA, "10月", true), "10月 应当命中 10/17。" );
                Assert(StartupProfileSearch.Matches(planA, "2026年", true), "2026年 应当命中 2026 年。" );

                // ④ 命中区间（界面上要按这些区间加亮）
                ProfileSearchHighlights lights = StartupProfileSearch.Highlight(planA, "方案 15分", true);
                AssertEqual(1, lights.NameRanges.Count);
                AssertEqual(0, lights.NameRanges[0].Start);
                AssertEqual(2, lights.NameRanges[0].Length);
                Assert(lights.TimeRanges.Count >= 1, "时间串里应当有要加亮的一段。" );
                string stamp = StartupProfileCardText.FormatSavedAt(planA);
                bool digitsFound = false;
                for (int index = 0; index < lights.TimeRanges.Count; index++)
                {
                    SearchTextRange range = lights.TimeRanges[index];
                    if (range.Start >= 0 && range.Start + range.Length <= stamp.Length
                        && string.Equals(stamp.Substring(range.Start, range.Length), "15", StringComparison.Ordinal))
                    {
                        digitsFound = true;
                    }
                }
                Assert(digitsFound, "高亮区间里应当能找到时间串里的“15”。" );
                // 名字命中的那个词不应该再去高亮时间；时间命中的词才高亮时间。
                ProfileSearchHighlights nameOnly = StartupProfileSearch.Highlight(planA, "方案", true);
                AssertEqual(1, nameOnly.NameRanges.Count);
                AssertEqual(0, nameOnly.TimeRanges.Count);
            });

            Run("搜索规则三个工作区共用：自启动应用/回收站也按“勾选才搜时间”走", delegate
            {
                // 用户 2026-10-19 原话：“自启动应用…为什么没有搜索内没有对应的「同时按时间搜索」，
                // 后续搜索内添加新的功能时，例如其他的搜索方法，都要同步到其余两个上的。”
                // → 匹配规则只有一份（Shared/UI/Controls/SearchMatcher.cs），这里直接验它。
                Type matcher = productAssembly.GetType("WorkspaceRestorer.UI.Shared.SearchMatcher", true);
                Type targetType = productAssembly.GetType("WorkspaceRestorer.UI.Shared.SearchTarget", true);
                Assert(matcher != null, "缺少公共搜索规则 SearchMatcher。" );
                Assert(targetType != null, "缺少公共搜索目标 SearchTarget。" );
                MethodInfo matches = matcher.GetMethod("Matches", BindingFlags.Public | BindingFlags.Static);
                Assert(matches != null, "SearchMatcher 缺少 Matches。" );

                DateTime local = new DateTime(2026, 10, 19, 13, 51, 0, DateTimeKind.Local);
                DateTime utc = local.ToUniversalTime();
                object target = Activator.CreateInstance(targetType);
                targetType.GetField("Name").SetValue(target, "ComfyUI");
                targetType.GetField("TimeText").SetValue(target, "2026-10-19 13:51");
                targetType.GetField("SavedUtc").SetValue(target, utc);
                targetType.GetField("UpdatedUtc").SetValue(target, utc);
                Func<object, string, bool, bool> hit = delegate(object value, string keyword, bool includeTime)
                {
                    return (bool)matches.Invoke(null, new object[] { value, keyword, includeTime });
                };

                Assert(hit(target, "comfy", false), "名字搜索应当不区分大小写。" );
                Assert(!hit(target, "13:51", false), "不勾时间时，时间写法不该命中。" );
                Assert(hit(target, "13:51", true), "勾上时间后，13:51 应当命中 13:51。" );
                Assert(hit(target, "13时", true), "勾上时间后，13时 应当命中 13 点。" );
                Assert(hit(target, "51分", true), "勾上时间后，51分 应当命中 51 分。" );
                Assert(!hit(target, "45分", true), "45分 不该命中 13:51。" );
                Assert(hit(target, "10-19", true), "勾上时间后，10-19 应当命中保存日期。" );
                Assert(hit(target, "10月", true), "勾上时间后，10月 应当命中 10 月。" );
                Assert(!hit(target, "10月", false), "不勾时间时，10月 不该命中。" );
                // 多个词是“并且”关系，和其他两个工作区完全一致。
                Assert(hit(target, "ComfyUI 51分", true), "“ComfyUI 51分”应当同时命中名字与时间。" );
                Assert(!hit(target, "ComfyUI 45分", true), "“ComfyUI 45分”里时间对不上，不该命中。" );

                // 新界面时间串是“北京时间 2026/10/19日 13:51分”，仍然要能按
                // 横杠日期、斜杠日期和 13:51 命中（三个工作区共用）。
                object canonical = Activator.CreateInstance(targetType);
                targetType.GetField("Name").SetValue(canonical, "ComfyUI");
                targetType.GetField("TimeText").SetValue(canonical, TimeText.FormatUtcAsComputerLocalStamp(utc));
                targetType.GetField("SavedUtc").SetValue(canonical, utc);
                targetType.GetField("UpdatedUtc").SetValue(canonical, utc);
                Assert(hit(canonical, "2026-10-19", true), "中文时间串仍应能按横杠日期搜索。" );
                Assert(hit(canonical, "2026/10/19", true), "中文时间串仍应能按斜杠日期搜索。" );
                Assert(hit(canonical, "13:51", true), "中文时间串仍应能按 13:51 搜索。" );

                // 短数字只比“保存时间”，不会被“创建时间＝刚才”带偏（2026-10-19 在 15:xx 跑测试时踩到）。
                StartupProfile fresh = new StartupProfile { Id = "fresh", Name = "刚建" };
                fresh.UpdatedAtUtc = new DateTime(2026, 10, 3, 9, 5, 0, DateTimeKind.Local).ToUniversalTime();
                Assert(!StartupProfileSearch.Matches(fresh, "15", true),
                    "保存时间是 3 日 9:05 时，15 不该因为“创建时间是此刻”而命中。" );
                Assert(StartupProfileSearch.Matches(fresh, "5", true), "5 应当命中 5 分。" );
            });

        }
    }
}
