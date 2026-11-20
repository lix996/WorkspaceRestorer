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
using WorkspaceRestorer.Browsers;
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

        private static void RunLoadAndSettingsTests()
        {
            Run("负载门：内存高后降下来会继续", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SequenceLoadMonitor loadMonitor = new SequenceLoadMonitor(new[]
                {
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 1, MemoryPercent = 90 },
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 1, MemoryPercent = 10 }
                });
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "测试项", ExecutablePath = "C:\\missing.exe" });
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero,
                    MaximumCpuPercent = 100,
                    MaximumMemoryPercent = 75,
                    MemoryWaitTimeout = TimeSpan.FromSeconds(2)
                };
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(!result.IsCancelled, "内存降下来后不应取消恢复。");
                AssertEqual(1, launcher.Items.Count);
            });

            Run("负载门：内存一直高，到上限自动停止", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SequenceLoadMonitor loadMonitor = new SequenceLoadMonitor(new[]
                {
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 1, MemoryPercent = 90 }
                });
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "测试项", ExecutablePath = "C:\\missing.exe" });
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero,
                    MaximumCpuPercent = 100,
                    MaximumMemoryPercent = 75,
                    MemoryWaitTimeout = TimeSpan.FromMilliseconds(1)
                };
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.IsCancelled && result.IsAutoStopped, "内存超时后应自动停止。");
                Assert(result.CancelReason != null && result.CancelReason.Contains("75"), "停止原因要写用户设置的内存上限。");
                AssertEqual(0, launcher.Items.Count);
            });

            Run("负载门：CPU 一直高，到上限自动停止（不再强行继续）", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SequenceLoadMonitor loadMonitor = new SequenceLoadMonitor(new[]
                {
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 90, MemoryPercent = 1 }
                });
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "测试项", ExecutablePath = "C:\\missing.exe" });
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero,
                    MaximumCpuPercent = 75,
                    LoadWaitTimeout = TimeSpan.FromMilliseconds(1),
                    MaximumMemoryPercent = 100
                };
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.IsCancelled && result.IsAutoStopped, "CPU 超时后应自动停止。");
                Assert(result.CancelReason != null && result.CancelReason.Contains("CPU"), "停止原因要说明 CPU。");
                AssertEqual(0, launcher.Items.Count);
            });

            Run("负载门：CPU 和内存都高时同时写进原因", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SequenceLoadMonitor loadMonitor = new SequenceLoadMonitor(new[]
                {
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 90, MemoryPercent = 90 }
                });
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "测试项", ExecutablePath = "C:\\missing.exe" });
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero,
                    MaximumCpuPercent = 60,
                    LoadWaitTimeout = TimeSpan.FromMilliseconds(1),
                    MaximumMemoryPercent = 70,
                    MemoryWaitTimeout = TimeSpan.FromMilliseconds(20)
                };
                System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                stopwatch.Stop();
                Assert(result.IsCancelled && result.IsAutoStopped, "两项都超时后应自动停止。");
                Assert(result.CancelReason != null
                    && result.CancelReason.Contains("60")
                    && result.CancelReason.Contains("70"), "停止原因要同时写 CPU/内存的用户上限。");
                Assert(stopwatch.ElapsedMilliseconds >= 10,
                    "两项都超限时等待时间应取较长值（不是 CPU 的 1ms）。");
                AssertEqual(0, launcher.Items.Count);
            });

            Run("负载门：立即恢复后本次不再被高负载挡住", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SequenceLoadMonitor loadMonitor = new SequenceLoadMonitor(new[]
                {
                    new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 90, MemoryPercent = 90 }
                });
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "测试项", ExecutablePath = "C:\\missing.exe" });
                RestoreLoadGate gate = new RestoreLoadGate();
                gate.ContinueForRestore();
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero,
                    MaximumCpuPercent = 60,
                    LoadWaitTimeout = TimeSpan.FromMilliseconds(1),
                    MaximumMemoryPercent = 70,
                    MemoryWaitTimeout = TimeSpan.FromMilliseconds(1),
                    LoadGate = gate
                };
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                Assert(!result.IsCancelled, "点过立即恢复后不应再暂停。");
                AssertEqual(1, launcher.Items.Count);
            });

            Run("内存设置默认 75% / 120 秒", delegate
            {
                ApplicationController.ControllerSettings settings =
                    new ApplicationController.ControllerSettings();
                Assert(Math.Abs(settings.MaximumMemoryPercent - 75.0) < 0.001,
                    "内存上限默认值不是 75%。");
                AssertEqual(120, settings.MemoryWaitTimeoutSeconds);
                RestoreOptions options = new RestoreOptions();
                Assert(Math.Abs(options.MaximumMemoryPercent - 75.0) < 0.001,
                    "RestoreOptions 内存上限默认值不是 75%。");
                AssertEqual(120, (int)Math.Round(options.MemoryWaitTimeout.TotalSeconds));
            });

            Run("窗口级最小化状态会应用到文件夹/网页子项", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SavedWindowPlacement windowPlacement = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 100,
                    Top = 120,
                    Width = 900,
                    Height = 700,
                    WindowState = "Minimized",
                    WindowStateBeforeMinimize = "Normal",
                    MinimizeChoiceExplicit = true
                };
                snapshot.Items.Add(new SessionItem
                {
                    Id = "explorer-parent",
                    Kind = SessionItemKind.Application,
                    DisplayName = "文件资源管理器 (1)",
                    CaptureSource = "ExplorerWindow",
                    ExecutablePath = "C:\\Windows\\explorer.exe",
                    WindowPlacement = windowPlacement,
                    Sequence = 1
                });
                snapshot.Items.Add(new SessionItem
                {
                    Id = "folder-child",
                    Kind = SessionItemKind.ExplorerFolder,
                    ParentId = "explorer-parent",
                    DisplayName = "项目文件夹",
                    TargetPath = "C:\\Project",
                    WindowPlacement = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        Left = 10,
                        Top = 20,
                        WindowState = "Normal"
                    },
                    Sequence = 2
                });
                snapshot.Items.Add(new SessionItem
                {
                    Id = "browser-parent",
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge",
                    BrowserGroupKey = "edge",
                    BrowserExecutablePath = "C:\\missing-browser.exe",
                    WindowPlacement = windowPlacement,
                    Sequence = 3
                });
                snapshot.Items.Add(new SessionItem
                {
                    Id = "page-child",
                    Kind = SessionItemKind.WebPage,
                    ParentId = "browser-parent",
                    BrowserGroupKey = "edge",
                    DisplayName = "项目主页",
                    Url = "https://example.test/project",
                    Sequence = 4
                });

                service.RestoreAsync(
                    snapshot,
                    new RestoreOptions
                    {
                        InitialDelay = TimeSpan.Zero,
                        ItemDelay = TimeSpan.Zero,
                        WaitForAcceptableLoad = false,
                        CpuSampleInterval = TimeSpan.Zero,
                        LoadPollInterval = TimeSpan.Zero
                    },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();

                SessionItem folder = launcher.Items.FirstOrDefault(delegate(SessionItem candidate)
                {
                    return candidate != null && candidate.Kind == SessionItemKind.ExplorerFolder;
                });
                SessionItem page = launcher.Items.FirstOrDefault(delegate(SessionItem candidate)
                {
                    return candidate != null && candidate.Kind == SessionItemKind.WebPage;
                });
                Assert(folder != null && folder.WindowPlacement != null,
                    "文件夹子项没有进入恢复列表/没有窗口状态。" );
                AssertEqual("Minimized", folder.WindowPlacement.WindowState);
                AssertEqual("Normal", folder.WindowPlacement.WindowStateBeforeMinimize);
                Assert(page != null && page.WindowPlacement != null,
                    "网页子项没有进入恢复列表/没有窗口状态。" );
                AssertEqual("Minimized", page.WindowPlacement.WindowState);
            });

            Run("网页项目保留 URL 并按顺序恢复", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 2,
                    Kind = SessionItemKind.Application,
                    DisplayName = "浏览器",
                    ExecutablePath = "C:\\missing-browser.exe"
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 1,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "项目主页",
                    Url = "https://example.test/project",
                    BrowserName = "Microsoft Edge",
                    BrowserExecutablePath = "C:\\missing-browser.exe"
                });
                RestoreResult result = service.RestoreAsync(
                    snapshot,
                    new RestoreOptions
                    {
                        InitialDelay = TimeSpan.Zero,
                        ItemDelay = TimeSpan.Zero,
                        WaitForAcceptableLoad = false
                    },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Items.Count == 2, "网页恢复结果没有包含全部项目。");
                Assert(launcher.Items.Count == 2, "网页恢复没有按顺序调用启动器。");
                Assert(launcher.Items[0].Kind == SessionItemKind.WebPage, "网页没有按 Sequence 优先恢复。");
                AssertEqual("https://example.test/project", launcher.Items[0].Url);
                AssertEqual("Microsoft Edge", launcher.Items[0].BrowserName);
            });

            Run("终端与本地网址：默认不扫，勾选后保留并提示无法恢复（2026-10-19）", delegate
            {
                // 用户要求：启动与恢复设置里加“扫描终端与本地网址”，默认不勾；勾选时扫描并在提示里说明哪些恢复不了。
                Type policy = productAssembly.GetType("WorkspaceRestorer.Models.TerminalAndLocalUrlPolicy", true);
                Assert(policy != null, "缺少终端/本地网址策略 TerminalAndLocalUrlPolicy。" );
                MethodInfo apply = policy.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static);
                MethodInfo isTerminal = policy.GetMethod("IsTerminalItem", BindingFlags.Public | BindingFlags.Static);
                MethodInfo isLocalUrl = policy.GetMethod("IsLocalUrl", BindingFlags.Public | BindingFlags.Static);
                Assert(apply != null && isTerminal != null && isLocalUrl != null, "策略方法缺失。" );

                Type snapshotType = productAssembly.GetType("WorkspaceRestorer.Models.SessionSnapshot", true);
                Type itemType = productAssembly.GetType("WorkspaceRestorer.Models.SessionItem", true);
                Type kindType = productAssembly.GetType("WorkspaceRestorer.Models.SessionItemKind", true);
                Func<string, string, object> makeItem = delegate(string id, string name)
                {
                    object item = Activator.CreateInstance(itemType);
                    itemType.GetProperty("Id").SetValue(item, id, null);
                    itemType.GetProperty("DisplayName").SetValue(item, name, null);
                    return item;
                };
                Func<object, string, int, object> makeKindItem = delegate(object item, string kindName, int sequence)
                {
                    object kind = Enum.Parse(kindType, kindName);
                    itemType.GetProperty("Kind").SetValue(item, kind, null);
                    itemType.GetProperty("Sequence").SetValue(item, sequence, null);
                    return item;
                };
                Func<object, string, string> getProperty = delegate(object target, string property)
                {
                    object value = itemType.GetProperty(property).GetValue(target, null);
                    return value == null ? string.Empty : value.ToString();
                };
                Func<object> buildSnapshot = delegate
                {
                    object snapshot = Activator.CreateInstance(snapshotType);
                    System.Collections.IList items = (System.Collections.IList)snapshotType.GetProperty("Items").GetValue(snapshot, null);
                    object terminal = makeKindItem(makeItem("t1", "Windows Terminal"), "Application", 1);
                    itemType.GetProperty("ExecutablePath").SetValue(terminal, "C:\\tools\\WindowsTerminal.exe", null);
                    items.Add(terminal);
                    object browser = makeKindItem(makeItem("b1", "Microsoft Edge"), "Application", 2);
                    itemType.GetProperty("BrowserGroupKey").SetValue(browser, "edge#0", null);
                    items.Add(browser);
                    object localPage = makeKindItem(makeItem("p1", "ComfyUI"), "WebPage", 3);
                    itemType.GetProperty("ParentId").SetValue(localPage, "b1", null);
                Assert((bool)isLocalUrl.Invoke(null, new object[] { "http://localhost:3080" }), "localhost 应判成本地网址。" );
                    itemType.GetProperty("Url").SetValue(localPage, "http://127.0.0.1:8188/", null);
                    items.Add(localPage);
                    object code = makeKindItem(makeItem("a1", "Visual Studio Code"), "Application", 4);
                    itemType.GetProperty("ExecutablePath").SetValue(code, "C:\\tools\\Code.exe", null);
                    items.Add(code);
                    return snapshot;
                };
                Func<object, string> countHint = delegate(object snapshot)
                {
                    object[] arguments = new object[] { snapshot, false };
                    object hint = apply.Invoke(null, arguments);
                    return hint as string;
                };

                object noPathItem = makeKindItem(makeItem("x", "terminal"), "Application", 1);
                Assert(!(bool)isTerminal.Invoke(null, new object[] { noPathItem }), "没有可执行文件路径时不应判成终端。" );
                Assert((bool)isLocalUrl.Invoke(null, new object[] { "http://127.0.0.1:8188/" }), "127.0.0.1 应判成本地网址。" );
                Assert(!(bool)isLocalUrl.Invoke(null, new object[] { "https://example.com/x" }), "外网网址不能判成本地网址。" );

                // ① 不勾选：终端 + 本地网址 + 因此空壳的浏览器父项都被去掉，正常项保留，并提示“已跳过”。
                object snapshotOff = buildSnapshot();
                object[] offArgs = new object[] { snapshotOff, false };
                string offHint = (string)apply.Invoke(null, offArgs);
                System.Collections.IList remaining = (System.Collections.IList)snapshotType.GetProperty("Items").GetValue(snapshotOff, null);
                AssertEqual(1, remaining.Count);
                AssertEqual("Visual Studio Code", getProperty(remaining[0], "DisplayName"));
                Assert(offHint != null && offHint.IndexOf("已跳过", StringComparison.Ordinal) >= 0, "不勾选时提示要说清“已跳过 N 项”。" );

                // ② 勾选：全部保留，并在提示里点名“无法恢复”。
                object snapshotOn = buildSnapshot();
                object[] onArgs = new object[] { snapshotOn, true };
                string onHint = (string)apply.Invoke(null, onArgs);
                System.Collections.IList kept = (System.Collections.IList)snapshotType.GetProperty("Items").GetValue(snapshotOn, null);
                AssertEqual(4, kept.Count);
                Assert(onHint != null && onHint.IndexOf("无法恢复", StringComparison.Ordinal) >= 0,
                    "勾选时提示必须说清“这些无法恢复”。实际：" + onHint);
                Assert(onHint.IndexOf("Windows Terminal", StringComparison.Ordinal) >= 0
                    || onHint.IndexOf("ComfyUI", StringComparison.Ordinal) >= 0,
                    "提示里最好点名是哪些项目。" );
            });

            Run("终端与本地网址：不带协议的 127.0.0.1:8188 / localhost:8188 也要认（2026-10-19）", delegate
            {
                // 用户报 bug 原话：“启动与恢复的设置那，我当时明明关闭了图2的扫描终端与网址，
                // 但是还是扫描了本地的 comfyui 网址。”
                // 真机数据里 ComfyUI / dsh 存的是**不带协议**的地址，旧实现解析不出来，所以开关形同虚设。
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("127.0.0.1:8188"), "127.0.0.1:8188 没认成本地网址。" );
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("127.0.0.1:3080"), "127.0.0.1:3080 没认成本地网址。" );
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("localhost:8188"), "localhost:8188 没认成本地网址。" );
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("[::1]:8188"), "[::1]:8188 没认成本地网址。" );
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("http://127.0.0.1:8188/"), "带协议的 127.0.0.1 没认成本地网址。" );
                Assert(TerminalAndLocalUrlPolicy.IsLocalUrl("file:///C:/x.html"), "file: 地址没认成本地网址。" );
                Assert(!TerminalAndLocalUrlPolicy.IsLocalUrl("https://example.com/a"), "公网地址被当成本地网址了。" );
                Assert(!TerminalAndLocalUrlPolicy.IsLocalUrl("example.com:8080/x"), "普通主机名被当成本地网址了。" );
                // 用户亲手挑/复制进来的项不算“扫描到的”，保存时不能悄悄删掉。
                SessionItem chosen = new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "Windows Terminal",
                    ExecutablePath = "C:\\tools\\WindowsTerminal.exe",
                    CaptureSource = "DesktopSelection"
                };
                Assert(!TerminalAndLocalUrlPolicy.IsExcludedKind(chosen),
                    "用户亲手挑选的终端也被当成“扫描到的”了（保存时会被删掉）。" );
            });

            Run("提示要如实说明：终端/本地网址是按设置跳过的，不是“网页没读到”（2026-10-19）", delegate
            {
                // 用户原话：“由于我没有在设置那勾选“扫描终端与本地网址”那么这里的提示也要更新，
                // 要如实说明情况，不要误导用户，恢复时的提示也要更新（自启动应用也要同步更新此类问题）。”
                SaveCompletion skipped = new SaveCompletion
                {
                    ItemCount = 20,
                    PolicyAffectedCount = 5,
                    PolicySkippedCount = 5,
                    PolicyIncluded = false
                };
                string banner = SaveCompletionText.Build(skipped, true);
                Assert(banner.IndexOf("已保存 20 项", StringComparison.Ordinal) >= 0, "横幅要先说保存了几项。" );
                Assert(banner.IndexOf("5 项终端/本地网址按当前设置跳过", StringComparison.Ordinal) >= 0,
                    "横幅必须说清是“按设置跳过 5 项终端/本地网址”。实际：" + banner);
                Assert(banner.IndexOf("最小化", StringComparison.Ordinal) < 0,
                    "设置没勾时不该再劝用户去显示最小化的浏览器窗口。实际：" + banner);
                Assert(banner.IndexOf("勾选「扫描终端与本地网址」", StringComparison.Ordinal) >= 0,
                    "横幅要告诉用户怎么才能把这些也保存。" );

                SaveCompletion kept = new SaveCompletion { ItemCount = 12, PolicyAffectedCount = 3, PolicyIncluded = true };
                string keptText = SaveCompletionText.Build(kept, true);
                Assert(keptText.IndexOf("3 项是终端/本地网址", StringComparison.Ordinal) >= 0,
                    "勾选时要说清保留了几项终端/本地网址。实际：" + keptText);
                Assert(keptText.IndexOf("恢复不了", StringComparison.Ordinal) >= 0, "勾选时要说清这些恢复不了。" );
                Assert(keptText.IndexOf("最小化", StringComparison.Ordinal) < 0, "勾选时也不该提“最小化”。" );

                SaveCompletion unread = new SaveCompletion
                {
                    ItemCount = 8,
                    Incomplete = true,
                    BrowserIncomplete = true,
                    UnreadBrowserWindows = new List<BrowserUnreadWindowInfo>
                    {
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (1)",
                            UnreadPageCount = 1,
                            Minimized = true
                        }
                    }
                };
                string unreadText = SaveCompletionText.Build(unread, true);
                AssertEqual(
                    "已保存 8 项\nMicrosoft Edge (1) 还有 1 个标签页没读到（最小化窗口）；"
                        + "已保存的网页不会丢，稍后再保存一次即可。",
                    unreadText);
                Assert(unreadText.IndexOf("把它显示出来", StringComparison.Ordinal) < 0,
                    "B 不能再让用户手动显示最小化窗口；程序自己会还原读取再缩回。实际：" + unreadText);

                SaveCompletion both = new SaveCompletion
                {
                    ItemCount = 8,
                    BrowserIncomplete = true,
                    PolicyAffectedCount = 2,
                    PolicySkippedCount = 2,
                    UnreadBrowserWindows = new List<BrowserUnreadWindowInfo>
                    {
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (2)",
                            UnreadPageCount = 2,
                            Minimized = false
                        }
                    }
                };
                string bothText = SaveCompletionText.Build(both, true);
                Assert(bothText.IndexOf("终端/本地网址", StringComparison.Ordinal) >= 0
                    && bothText.IndexOf("Microsoft Edge (2)", StringComparison.Ordinal) >= 0,
                    "两件事都有时，两句话都要说，并且要带窗口名。实际：" + bothText);
                Assert(SaveCompletionText.Build(both, false).IndexOf("\n", StringComparison.Ordinal) < 0,
                    "托盘气泡是一行，不能带换行。" );
            });

            Run("保存提示：空白标签不报、窗口名进横幅、C/D 兜底", delegate
            {
                // A：只有空白新标签页 → BrowserIncomplete=false，文案里不应出现“没读到”。
                SaveCompletion clean = new SaveCompletion { ItemCount = 27 };
                AssertEqual("已保存 27 项", SaveCompletionText.Build(clean, true));

                // C：普通窗口没读到，带窗口名，不再说“显示最小化窗口”。
                SaveCompletion normal = new SaveCompletion
                {
                    ItemCount = 26,
                    Incomplete = true,
                    BrowserIncomplete = true,
                    UnreadBrowserWindows = new List<BrowserUnreadWindowInfo>
                    {
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (2)",
                            UnreadPageCount = 1,
                            Minimized = false
                        }
                    }
                };
                string normalText = SaveCompletionText.Build(normal, true);
                AssertEqual(
                    "已保存 26 项\nMicrosoft Edge (2) 还有 1 个标签页没读到（可能还没加载完）；"
                        + "等页面加载完再保存一次即可（已保存的网页不会丢）。",
                    normalText);
                Assert(normalText.IndexOf("显示", StringComparison.Ordinal) < 0,
                    "C 不能出现“显示”窗口的建议。实际：" + normalText);

                // D：超时兜底。
                SaveCompletion timeout = new SaveCompletion
                {
                    ItemCount = 26,
                    Incomplete = true,
                    BrowserIncomplete = true,
                    BrowserCaptureTimedOut = true
                };
                string timeoutText = SaveCompletionText.Build(timeout, true);
                Assert(timeoutText.IndexOf("网页读取超时", StringComparison.Ordinal) >= 0,
                    "D 要说清超时。实际：" + timeoutText);

                // 多个窗口：只列前两个名字，后面写“等 N 个窗口”。
                SaveCompletion many = new SaveCompletion
                {
                    ItemCount = 20,
                    Incomplete = true,
                    BrowserIncomplete = true,
                    UnreadBrowserWindows = new List<BrowserUnreadWindowInfo>
                    {
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (1)",
                            UnreadPageCount = 1,
                            Minimized = true
                        },
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (2)",
                            UnreadPageCount = 1,
                            Minimized = false
                        },
                        new BrowserUnreadWindowInfo
                        {
                            DisplayName = "Microsoft Edge (3)",
                            UnreadPageCount = 2,
                            Minimized = false
                        }
                    }
                };
                string manyText = SaveCompletionText.Build(many, true);
                Assert(manyText.IndexOf(
                    "Microsoft Edge (1)、Microsoft Edge (2) 等 3 个窗口",
                    StringComparison.Ordinal) >= 0,
                    "多窗口要列两个名字并写“等 N 个窗口”。实际：" + manyText);
            });

            Run("空白标签判断：只认新标签页，不误伤“无标题”真实页", delegate
            {
                Assert(BrowserTitleProfile.IsBlankBrowserTabName("新建标签页 - 内存使用率 - 109 MB"),
                    "带内存尾巴的新建标签页没认出来。" );
                Assert(BrowserTitleProfile.IsBlankBrowserTabName("新标签页"), "新标签页没认出来。" );
                Assert(BrowserTitleProfile.IsBlankBrowserTabName("New Tab - Microsoft Edge"),
                    "New Tab 没认出来。" );
                Assert(BrowserTitleProfile.IsBlankBrowserTabName("about:blank"), "about:blank 没认出来。" );
                Assert(!BrowserTitleProfile.IsBlankBrowserTabName("无标题 - 内存使用率 - 19 MB"),
                    "“无标题”是真实页，不能当空白忽略。" );
                Assert(!BrowserTitleProfile.IsBlankBrowserTabName(
                    "DeepSeek - 探索未至之境 - 内存使用率 - 74 MB"),
                    "真实 DeepSeek 页被当成空白了。" );
                Assert(BrowserTitleProfile.IsBlankNewTabUrl("edge://newtab/"),
                    "edge://newtab/ 没认出来。" );
                Assert(BrowserTitleProfile.IsBlankNewTabUrl("chrome://newtab"),
                    "chrome://newtab 没认出来。" );
                Assert(!BrowserTitleProfile.IsBlankNewTabUrl("https://chat.deepseek.com/sign_in"),
                    "真实网址被当成空白新标签页。" );

                List<string> names = new List<string>
                {
                    "新建标签页 - 内存使用率 - 109 MB",
                    "DeepSeek - 探索未至之境 - 内存使用率 - 74 MB"
                };
                AssertEqual(1, BrowserCaptureCounts.CountIgnoredBlankTabs(
                    names,
                    new List<int> { 1 }));
            });

            Run("终端/本地网址的取舍要带数量，恢复失败时也要如实说原因（2026-10-19）", delegate
            {
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "Windows Terminal",
                    TargetPath = "C:\\Windows\\System32\\cmd.exe"
                });
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "ComfyUI",
                    Url = "127.0.0.1:8188",
                    TargetPath = "127.0.0.1:8188"
                });
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "记事本",
                    TargetPath = "C:\\Windows\\System32\\notepad.exe"
                });

                TerminalAndLocalUrlPolicy.PolicyOutcome skippedOutcome =
                    TerminalAndLocalUrlPolicy.ApplyDetailed(snapshot, false);
                AssertEqual(2, skippedOutcome.AffectedCount);
                AssertEqual(2, skippedOutcome.SkippedCount);
                AssertEqual(1, snapshot.Items.Count);
                Assert(skippedOutcome.Hint != null
                    && skippedOutcome.Hint.IndexOf("已跳过 2 项", StringComparison.Ordinal) >= 0,
                    "没勾设置时要给出“已跳过 2 项”的提示。实际：" + skippedOutcome.Hint);

                SessionSnapshot keptSnapshot = new SessionSnapshot();
                keptSnapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "ComfyUI",
                    Url = "127.0.0.1:8188",
                    TargetPath = "127.0.0.1:8188"
                });
                TerminalAndLocalUrlPolicy.PolicyOutcome keptOutcome =
                    TerminalAndLocalUrlPolicy.ApplyDetailed(keptSnapshot, true);
                AssertEqual(1, keptOutcome.AffectedCount);
                AssertEqual(0, keptOutcome.SkippedCount);
                Assert(keptOutcome.Included, "勾选时要标成“保留”。" );
                AssertEqual(1, keptSnapshot.Items.Count);

                // 恢复失败原因：终端/本地网址要说清“启动与恢复恢复不了这类东西”，不能只丢英文。
                RestoreItemResult terminalResult = new RestoreItemResult();
                terminalResult.Item = new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "dsh",
                    TargetPath = "C:\\Windows\\System32\\cmd.exe"
                };
                terminalResult.Success = false;
                terminalResult.Error = "The application executable does not exist: C:\\gone.exe";
                string reason = RestoreFailureText.DescribeReason(terminalResult);
                Assert(reason.IndexOf("终端窗口 / 本地网址", StringComparison.Ordinal) >= 0,
                    "终端项的恢复失败原因没说清是“终端/本地网址”。实际：" + reason);
                Assert(reason.IndexOf("启动与恢复", StringComparison.Ordinal) >= 0,
                    "要说明是「启动与恢复」恢复不了这类东西。" );

                // 自启动应用那边也用同一套中文翻译（不再贴英文原文）。
                string translated = RestoreFailureText.DescribeError("The application executable does not exist: C:\\gone.exe");
                Assert(translated.IndexOf("程序已经不在这个位置了", StringComparison.Ordinal) >= 0,
                    "启动器的英文错误要翻译成中文。实际：" + translated);

                // 恢复卡片的说明：有终端/本地网址时必须当场说清；自启动应用那边不出现这段。
                string cardNormal = RestoreCardText.Detail(false, 0);
                Assert(cardNormal.IndexOf("终端窗口或本地网址", StringComparison.Ordinal) < 0,
                    "没有这类项目时不该多嘴。" );
                string cardPolicy = RestoreCardText.Detail(false, 2);
                Assert(cardPolicy.IndexOf("2 项终端窗口或本地网址", StringComparison.Ordinal) >= 0,
                    "恢复卡片要说清“这份方案里有 2 项终端/本地网址”。实际：" + cardPolicy);
                Assert(cardPolicy.IndexOf("恢复不了它们", StringComparison.Ordinal) >= 0,
                    "恢复卡片要说明这类东西恢复不了。" );
                Assert(cardPolicy.IndexOf("自启动应用", StringComparison.Ordinal) >= 0,
                    "恢复卡片要告诉用户想常驻去哪加。" );
                string cardApps = RestoreCardText.Detail(true, 3);
                Assert(cardApps.IndexOf("终端窗口或本地网址", StringComparison.Ordinal) < 0,
                    "自启动方案的恢复卡片不该说“终端/本地网址恢复不了”（那边本来就该能启动）。" );
            });

            Run("保存时按设置滤掉终端/本地网址（所有保存路径都算，2026-10-19）", delegate
            {
                // 用户报的第二个 bug：开关关着，ComfyUI 还是被扫进方案。
                // 除了“认不出不带协议的地址”，另一个原因是过滤只加在“保存当前工作”这一条路上。
                // 现在统一在公共提交口 `CommitCapturedSnapshot` 过滤，这条“扫描→提交”的路也要干净。
                List<SessionItem> captured = new List<SessionItem>();
                captured.Add(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "ComfyUI",
                    Url = "127.0.0.1:8188",
                    TargetPath = "127.0.0.1:8188",
                    Sequence = 1
                });
                captured.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "记事本",
                    ExecutablePath = "C:\\Windows\\notepad.exe",
                    TargetPath = "C:\\Windows\\notepad.exe",
                    Sequence = 2
                });
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(captured),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    StartupProfile profile = controller.CreateProfile("过滤测试");
                    controller.SetActiveProfile(profile);
                    controller.Settings.ScanTerminalsAndLocalUrls = false;
                    SessionSnapshot saved = controller.CaptureSnapshot();
                    Assert(saved != null, "保存没有返回结果。" );
                    AssertEqual(1, saved.Items.Count);
                    Assert(profile.Items.Count == 1, "方案里仍然留着本地网址（ComfyUI 127.0.0.1:8188）。" );
                    Assert(controller.StatusText.IndexOf("已跳过", StringComparison.Ordinal) >= 0,
                        "保存提示里应说清“已跳过 N 项终端/本地网址”，实际：" + controller.StatusText);

                    // 勾上以后照旧保留（并在提示里说明这些无法恢复）。
                    controller.Settings.ScanTerminalsAndLocalUrls = true;
                    SessionSnapshot kept = controller.CaptureSnapshot();
                    Assert(kept != null && kept.Items.Count == 2,
                        "勾选“扫描终端与本地网址”后应当保留本地网址，实际 " +
                        (kept == null || kept.Items == null ? "null" : kept.Items.Count.ToString()) + " 项。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("保存后状态栏要说明“已跳过几项终端/本地网址”（2026-10-19）", delegate
            {
                // 真机探针 Round43 第二段发现的问题：过滤本身对了，但“保存当前方案”这条流程
                // 提交完又把状态栏文字覆盖成“工作方案已保存…”，用户看不到“为什么少了几项”。
                List<SessionItem> captured = new List<SessionItem>();
                captured.Add(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "ComfyUI",
                    Url = "127.0.0.1:8188",
                    TargetPath = "127.0.0.1:8188",
                    Sequence = 1
                });
                captured.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "记事本",
                    ExecutablePath = "C:\\Windows\\notepad.exe",
                    TargetPath = "C:\\Windows\\notepad.exe",
                    Sequence = 2
                });
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(captured),
                    new RecordingRestoreService(),
                    new TestDesktopApplicationService(),
                    null,
                    null);
                try
                {
                    controller.Settings.ScanTerminalsAndLocalUrls = false;
                    StartupProfile profile = controller.CreateProfile("提示测试");
                    controller.SetActiveProfile(profile);
                    Task<SessionSnapshot> saving = controller.SaveCurrentWorkToProfileAsync(profile);
                    Assert(saving.Wait(TimeSpan.FromSeconds(15)), "保存没有在 15 秒内结束。" );
                    Assert(controller.StatusText.IndexOf("已跳过", StringComparison.Ordinal) >= 0,
                        "保存后的状态栏没有说明“已跳过 N 项终端/本地网址”，实际：" + controller.StatusText);
                }
                finally
                {
                    controller.Dispose();
                }
            });

            Run("剪贴板里是复制的工作方案时，不能被当成路径/网址（2026-10-18）", delegate
            {
                // 用户截图现场：在「启动与恢复」按过 Ctrl+C 后剪贴板是 `CSK1|启动与恢复{...}`，
                // 到「自启动应用」右键粘贴新增，旧实现甩出一整段 JSON 说“这不是能用的路径或网址”。
                Type mainWindow = productAssembly.GetType("WorkspaceRestorer.UI.MainWindow", true);
                MethodInfo createPasted = mainWindow == null
                    ? null
                    : mainWindow.GetMethod("CreatePastedSessionItem", BindingFlags.NonPublic | BindingFlags.Static);
                Assert(createPasted != null, "找不到 CreatePastedSessionItem。" );
                string payload = "CSK1|启动与恢复{\"Id\":\"p1\",\"Name\":\"工作方案甲\",\"Items\":[]}";
                object[] arguments = new object[] { payload, false, null };
                object item = createPasted.Invoke(null, arguments);
                Assert(item == null, "复制的工作方案文本不能被当成路径/网址建出项目。" );
                string error = arguments[2] as string;
                Assert(!string.IsNullOrWhiteSpace(error), "应当给出为什么不能新增的说明。" );
                Assert(error.IndexOf("工作方案", StringComparison.Ordinal) >= 0,
                    "说明里要讲清“这是复制的工作方案内容”，实际：" + error);
                Assert(error.IndexOf("不是文件夹/文件/网址", StringComparison.Ordinal) >= 0,
                    "说明里要讲清它不是什么，实际：" + error);

                // 对照：正常网址照旧能建出项目。
                object[] urlArguments = new object[] { "\"https://example.test/check\"", false, null };
                object urlItem = createPasted.Invoke(null, urlArguments);
                Assert(urlItem != null, "正常网址应当能建出项目。" );
                AssertEqual("https://example.test/check",
                    (string)urlItem.GetType().GetProperty("Url").GetValue(urlItem, null));
            });

            Run("带引号/书名号/Markdown 的网址也要认得（2026-10-18）", delegate
            {
                // 用户原话：“复制的网址带有双引号都无法检测到，并添加到方案内，增加合适的功能，让软件能够识别到。”
                Type tools = productAssembly.GetType(
                    "WorkspaceRestorer.Infrastructure.WebAddressTools", true);
                Assert(tools != null, "缺少统一网址规则 WebAddressTools。" );
                MethodInfo normalize = tools.GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo isWeb = tools.GetMethod("IsWebAddress", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo prepare = tools.GetMethod("TryPrepareWebAddress", BindingFlags.NonPublic | BindingFlags.Static);
                Assert(normalize != null && isWeb != null && prepare != null, "WebAddressTools 缺少 Normalize / IsWebAddress / TryPrepareWebAddress。" );

                Func<string, string> norm = delegate(string raw)
                {
                    return (string)normalize.Invoke(null, new object[] { raw });
                };
                Func<string, bool> web = delegate(string raw)
                {
                    return (bool)isWeb.Invoke(null, new object[] { raw });
                };
                Func<string, string> prep = delegate(string raw)
                {
                    object[] arguments = new object[] { raw, null };
                    bool ok = (bool)prepare.Invoke(null, arguments);
                    return ok ? (string)arguments[1] : null;
                };

                // ① 各种“人复制过来”的引号变体都要剥掉。
                AssertEqual("https://example.test/a", norm("\"https://example.test/a\""));
                AssertEqual("https://example.test/b", norm("“https://example.test/b”"));
                AssertEqual("https://example.test/c", norm("<https://example.test/c>"));
                AssertEqual("https://example.test/d", norm("[标题](https://example.test/d)"));
                AssertEqual("https://example.test/e", norm("'https://example.test/e'"));
                AssertEqual("https://example.test/f", norm("  https://example.test/f  "));
                AssertEqual("https://example.test/g", norm("\u200Bhttps://example.test/g"));
                AssertEqual("https://example.test/h", norm("\"“https://example.test/h”\""));

                // ② 剥完以后必须认成网址（以前这里全是 false，用户就是卡在这一步）。
                Assert(web("\"https://example.test/a\""), "带直引号的网址应当被认成网址。" );
                Assert(web("“https://example.test/b”"), "带中文引号的网址应当被认成网址。" );
                Assert(web("<https://example.test/c>"), "带书名号的网址应当被认成网址。" );
                Assert(web("[标题](https://example.test/d)"), "Markdown 链接应当被认成网址。" );

                // ③ 没写协议的补成 http://（存下来以后交给浏览器才不会当成搜索词）。
                AssertEqual("http://example.test/plain", prep("\"example.test/plain\""));
                AssertEqual("http://127.0.0.1:3080", prep("“127.0.0.1:3080”"));
                AssertEqual("https://example.test/a", prep("\"https://example.test/a\""));

                // ④ 不能把正常网址弄坏：Wikipedia 这种自带右括号的必须原样保留。
                AssertEqual("https://en.wikipedia.org/wiki/Foo_(bar)", norm("\"https://en.wikipedia.org/wiki/Foo_(bar)\""));
                Assert(web("\"https://en.wikipedia.org/wiki/Foo_(bar)\""), "带括号的维基网址也要认得。" );

                // ⑤ 明明不是网址的不能被硬认成网址。
                Assert(!web("\"今天天气不错\""), "普通文字不能被认成网址。" );
                Assert(prep("\"今天天气不错\"") == null, "普通文字整理后应当返回 null（表示不是网址）。" );

                // ⑥ 真正入口：启动与恢复“粘贴路径/网址新增”用的 CreatePastedSessionItem。
                Type mainWindow = productAssembly.GetType("WorkspaceRestorer.UI.MainWindow", true);
                MethodInfo createPasted = mainWindow == null
                    ? null
                    : mainWindow.GetMethod("CreatePastedSessionItem", BindingFlags.NonPublic | BindingFlags.Static);
                Assert(createPasted != null, "找不到 CreatePastedSessionItem。" );
                object[] callArguments = new object[] { "\"https://example.test/pasted\"", false, null };
                object item = createPasted.Invoke(null, callArguments);
                Assert(item != null, "带引号的网址应当能建出一个网页项目。" );
                if (item != null)
                {
                    Type itemType = item.GetType();
                    AssertEqual("WebPage", itemType.GetProperty("Kind").GetValue(item, null).ToString());
                    AssertEqual("https://example.test/pasted",
                        (string)itemType.GetProperty("Url").GetValue(item, null));
                    AssertEqual("example.test",
                        (string)itemType.GetProperty("DisplayName").GetValue(item, null));
                }
            });

            Run("网页间隔设置让网页逐个启动", delegate
            {
                Func<TimeSpan, TimeSpan> measure = delegate(TimeSpan webPageDelay)
                {
                    ActiveRecordingLauncher launcher = new ActiveRecordingLauncher();
                    SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                    SessionSnapshot snapshot = new SessionSnapshot();
                    for (int index = 1; index <= 3; index++)
                    {
                        snapshot.Items.Add(new SessionItem
                        {
                            Sequence = index,
                            Kind = SessionItemKind.WebPage,
                            DisplayName = "网页" + index,
                            Url = "https://example.test/" + index,
                            BrowserName = "Microsoft Edge",
                            BrowserExecutablePath = "C:\\missing-browser.exe",
                            BrowserGroupKey = "edge",
                            BrowserWindowIndex = 0,
                            BrowserTabIndex = index - 1
                        });
                    }
                    DateTime started = DateTime.UtcNow;
                    service.RestoreAsync(
                        snapshot,
                        new RestoreOptions
                        {
                            InitialDelay = TimeSpan.Zero,
                            ItemDelay = TimeSpan.Zero,
                            WebPageDelay = webPageDelay,
                            WaitForAcceptableLoad = false
                        },
                        null,
                        CancellationToken.None).GetAwaiter().GetResult();
                    AssertEqual(3, launcher.Items.Count);
                    return DateTime.UtcNow - started;
                };

                TimeSpan withoutGap = measure(TimeSpan.Zero);
                TimeSpan withGap = measure(TimeSpan.FromMilliseconds(150));
                Assert(
                    withGap - withoutGap >= TimeSpan.FromMilliseconds(200),
                    "网页间隔没有生效：间隔 0 用时 " + (int)withoutGap.TotalMilliseconds
                        + " 毫秒，间隔 150 毫秒用时 " + (int)withGap.TotalMilliseconds + " 毫秒。" );
            });

            Run("浏览器父项不会额外打开空白页", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                SessionRestoreService service = new SessionRestoreService(launcher, new FixedLoadMonitor());
                SessionSnapshot snapshot = new SessionSnapshot();
                SessionItem browser = new SessionItem
                {
                    Id = "edge-parent",
                    Sequence = 1,
                    Kind = SessionItemKind.Application,
                    DisplayName = "Microsoft Edge",
                    ExecutablePath = "C:\\Browsers\\msedge.exe",
                    BrowserGroupKey = "C:\\Browsers\\msedge.exe"
                };
                snapshot.Items.Add(browser);
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 2,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第一页",
                    Url = "https://example.test/one",
                    ParentId = browser.Id,
                    BrowserGroupKey = browser.BrowserGroupKey,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 0
                });
                snapshot.Items.Add(new SessionItem
                {
                    Sequence = 3,
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "第二页",
                    Url = "https://example.test/two",
                    ParentId = browser.Id,
                    BrowserGroupKey = browser.BrowserGroupKey,
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 1
                });
                RestoreResult result = service.RestoreAsync(
                    snapshot,
                    new RestoreOptions
                    {
                        InitialDelay = TimeSpan.Zero,
                        ItemDelay = TimeSpan.Zero,
                        WaitForAcceptableLoad = false
                    },
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                Assert(result.Items.Count == 2, "浏览器父项没有在网页恢复时被过滤。" );
                Assert(launcher.Items.Count == 2, "恢复显式网页时额外启动了浏览器空白父项。" );
                Assert(launcher.Items.All(delegate(SessionItem item) { return item.Kind == SessionItemKind.WebPage; }), "恢复列表包含非网页父项。" );
            });

        }
    }
}
