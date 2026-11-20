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

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                Passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception exception)
            {
                Failures.Add(name + "：" + exception.Message);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(string.Format("期望“{0}”，实际“{1}”。", expected, actual));
            }
        }

        private static void AssertEqual(int expected, int actual)
        {
            if (expected != actual)
            {
                throw new InvalidOperationException(string.Format("期望“{0}”，实际“{1}”。", expected, actual));
            }
        }

        /// <summary>卡片落点断言的简写（kind + index 一起比，报错时能直接看出是哪一条用例）。</summary>
        private static void AssertDrop(
            ProfileCardDropDecision decision,
            ProfileCardDropKind kind,
            int index,
            string message)
        {
            if (decision == null)
            {
                throw new InvalidOperationException(message + "：判定结果为空。");
            }
            if (decision.Kind != kind || decision.Index != index)
            {
                throw new InvalidOperationException(string.Format(
                    "{0}：期望 {1}/{2}，实际 {3}/{4}。",
                    message,
                    kind,
                    index,
                    decision.Kind,
                    decision.Index));
            }
        }

        /// <summary>窄带宽度断言（浮点数按 0.001 容差比）。</summary>
        private static void AssertEdgeBand(double height, double expected)
        {
            double actual = ProfileCardDropZones.EdgeBand(height);
            if (Math.Abs(actual - expected) > 0.001)
            {
                throw new InvalidOperationException(string.Format(
                    "卡片高 {0} 的插入窄带期望 {1} 像素，实际 {2} 像素。",
                    height,
                    expected,
                    actual));
            }
        }

        private static DisplayTopology CreateTopology(string stableKey, bool primary, int width, int height)
        {
            DisplayTopology topology = new DisplayTopology { Signature = stableKey + "-" + width + "x" + height };
            topology.Monitors.Add(new DisplayMonitor
            {
                StableKey = stableKey,
                IsPrimary = primary,
                Bounds = new DesktopRect(0, 0, width, height),
                WorkingArea = new DesktopRect(0, 0, width, height - 40)
            });
            topology.VirtualBounds = new DesktopRect(0, 0, width, height);
            return topology;
        }

        private static MutableCaptureService CreateBrowserCaptureService(bool includeSecondPage)
        {
            MutableCaptureService capture = new MutableCaptureService();
            SessionItem browser = new SessionItem
            {
                Id = "template-browser",
                Kind = SessionItemKind.Application,
                DisplayName = "Edge",
                ExecutablePath = "C:\\Browsers\\msedge.exe",
                BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                BrowserWindowIndex = 0,
                CaptureSource = "VisibleBrowserWindow"
            };
            capture.Items.Add(browser);
            capture.Items.Add(new SessionItem
            {
                Id = "template-page-one",
                Kind = SessionItemKind.WebPage,
                DisplayName = "项目主页",
                Url = "https://example.test/project",
                TargetPath = "https://example.test/project",
                BrowserExecutablePath = browser.ExecutablePath,
                BrowserGroupKey = browser.BrowserGroupKey,
                BrowserName = "Microsoft Edge",
                ParentId = browser.Id,
                BrowserWindowIndex = 0,
                BrowserTabIndex = 0,
                IsActiveBrowserTab = true,
                CaptureSource = "BrowserAddressBar"
            });
            if (includeSecondPage)
            {
                capture.Items.Add(new SessionItem
                {
                    Id = "template-page-two",
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "文档",
                    Url = "https://example.test/docs",
                    TargetPath = "https://example.test/docs",
                    BrowserExecutablePath = browser.ExecutablePath,
                    BrowserGroupKey = browser.BrowserGroupKey,
                    BrowserName = "Microsoft Edge",
                ParentId = browser.Id,
                BrowserWindowIndex = 0,
                BrowserTabIndex = 1,
                CaptureSource = "BrowserAddressBar"
                });
            }
            return capture;
        }

        private sealed class MutableCaptureService : ISessionCaptureService
        {
            public readonly List<SessionItem> Items = new List<SessionItem>();

            // Optional metadata used by tests that model the isolated
            // browser worker returning only its application/folder baseline.
            // Defaults preserve the ordinary in-memory capture behavior.
            public bool BrowserCaptureIncomplete { get; set; }

            public string CaptureWarning { get; set; }

            public SessionSnapshot Capture()
            {
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.BrowserCaptureIncomplete = BrowserCaptureIncomplete;
                snapshot.CaptureWarning = CaptureWarning;
                Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < Items.Count; index++)
                {
                    SessionItem source = Items[index];
                    if (source != null && !string.IsNullOrWhiteSpace(source.Id))
                    {
                        idMap[source.Id] = Guid.NewGuid().ToString("N");
                    }
                }

                for (int index = 0; index < Items.Count; index++)
                {
                    SessionItem source = Items[index];
                    if (source == null)
                    {
                        continue;
                    }
                    SessionItem copy = new SessionItem
                    {
                        Id = idMap.ContainsKey(source.Id) ? idMap[source.Id] : Guid.NewGuid().ToString("N"),
                        Kind = source.Kind,
                        Sequence = source.Sequence,
                        IsEnabled = source.IsEnabled,
                        DisplayName = source.DisplayName,
                        WindowTitle = source.WindowTitle,
                        ExecutablePath = source.ExecutablePath,
                        Arguments = source.Arguments,
                        WorkingDirectory = source.WorkingDirectory,
                        TargetPath = source.TargetPath,
                        CapturedProcessId = source.CapturedProcessId,
                        CaptureSource = source.CaptureSource,
                        Url = source.Url,
                        BrowserName = source.BrowserName,
                        BrowserExecutablePath = source.BrowserExecutablePath,
                        BrowserGroupKey = source.BrowserGroupKey,
                        ParentId = source.ParentId,
                        BrowserWindowIndex = source.BrowserWindowIndex,
                        BrowserTabIndex = source.BrowserTabIndex,
                        IsActiveBrowserTab = source.IsActiveBrowserTab,
                        BrowserTabIdentityCaptured = source.BrowserTabIdentityCaptured,
                         ApplicationWindowIndex = source.ApplicationWindowIndex,
                         WindowPlacement = source.WindowPlacement,
                         StartTimeUtc = source.StartTimeUtc
                    };
                    if (!string.IsNullOrWhiteSpace(copy.ParentId) && idMap.ContainsKey(copy.ParentId))
                    {
                        copy.ParentId = idMap[copy.ParentId];
                    }
                    snapshot.Items.Add(copy);
                }
                return snapshot;
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                return Capture();
            }
        }

        private sealed class OptionRecordingCaptureService : ISessionCaptureService
        {
            public int OptionsCaptureCount { get; private set; }

            public SessionCaptureOptions LastOptions { get; private set; }

            public SessionSnapshot Capture()
            {
                return new SessionSnapshot();
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                OptionsCaptureCount++;
                LastOptions = options;
                return new SessionSnapshot();
            }
        }

        private sealed class BlockingLayoutCoordinator : IDisposable
        {
            private readonly ManualResetEvent releaseEvent = new ManualResetEvent(false);
            private int captureCount;
            private int saveCount;

            public BlockingLayoutCoordinator()
            {
                Entered = new ManualResetEvent(false);
                Completed = new ManualResetEvent(false);
            }

            public ManualResetEvent Entered { get; private set; }

            public ManualResetEvent Completed { get; private set; }

            public int SaveCount { get { return saveCount; } }

            public DesktopShellSnapshot CaptureOnly()
            {
                int ordinal = Interlocked.Increment(ref captureCount);
                if (ordinal == 1)
                {
                    Entered.Set();
                    releaseEvent.WaitOne(3000);
                }
                Completed.Set();
                DesktopShellSnapshot snapshot = new DesktopShellSnapshot();
                snapshot.Icons.Add(new DesktopIconPlacement
                {
                    Identity = "test:desktop-icon",
                    DisplayName = "测试图标"
                });
                return snapshot;
            }

            public void SaveSnapshot(DesktopShellSnapshot snapshot)
            {
                Interlocked.Increment(ref saveCount);
            }

            public void Release()
            {
                releaseEvent.Set();
            }

            public void Dispose()
            {
                Entered.Dispose();
                Completed.Dispose();
                releaseEvent.Dispose();
            }
        }

        private sealed class BlockingCaptureService : ISessionCaptureService, IDisposable
        {
            private readonly ManualResetEvent releaseEvent = new ManualResetEvent(false);

            public BlockingCaptureService()
            {
                Entered = new ManualResetEvent(false);
            }

            public ManualResetEvent Entered { get; private set; }

            public SessionCaptureOptions LastOptions { get; private set; }

            public SessionSnapshot Capture()
            {
                return Capture(new SessionCaptureOptions());
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                LastOptions = options;
                Entered.Set();
                releaseEvent.WaitOne();
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    DisplayName = "阻塞捕获项目",
                    ExecutablePath = "C:\\blocked-capture.exe"
                });
                return snapshot;
            }

            public void Release()
            {
                releaseEvent.Set();
            }

            public void Dispose()
            {
                Release();
                Entered.Dispose();
                releaseEvent.Dispose();
            }
        }

        private sealed class ThrowingCaptureService : ISessionCaptureService
        {
            public int OptionsCaptureCount { get; private set; }

            public SessionSnapshot Capture()
            {
                return Capture(new SessionCaptureOptions());
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                OptionsCaptureCount++;
                throw new InvalidOperationException("模拟捕获失败");
            }
        }

        private sealed class ResultCaptureService : ISessionCaptureService
        {
            private readonly SessionSnapshot result;

            public ResultCaptureService(SessionSnapshot result)
            {
                this.result = result;
            }

            public SessionCaptureOptions LastOptions { get; private set; }

            public SessionSnapshot Capture()
            {
                return Capture(new SessionCaptureOptions());
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                LastOptions = options;
                return result;
            }
        }

        private sealed class RecordingLauncher : IProcessLauncher
        {
            public readonly List<SessionItem> Items = new List<SessionItem>();

            public LaunchResult Launch(SessionItem item)
            {
                Items.Add(item);
                return new LaunchResult { Success = true, WasSuppressed = true };
            }

            public LaunchResult LaunchPath(string path)
            {
                return new LaunchResult { Success = true, WasSuppressed = true };
            }
        }

        /// <summary>
        /// Records launches but reports them as real (not suppressed), so the
        /// between-item delays in the restore loop are actually exercised.
        /// </summary>
        private sealed class ActiveRecordingLauncher : IProcessLauncher
        {
            public readonly List<SessionItem> Items = new List<SessionItem>();

            public LaunchResult Launch(SessionItem item)
            {
                Items.Add(item);
                return new LaunchResult { Success = true };
            }

            public LaunchResult LaunchPath(string path)
            {
                Items.Add(new SessionItem { Kind = SessionItemKind.Document, TargetPath = path });
                return new LaunchResult { Success = true };
            }
        }

        private sealed class SequenceLoadMonitor : ISystemLoadMonitor
        {
            private readonly Queue<SystemLoadSnapshot> snapshots;
            private SystemLoadSnapshot last;

            public SequenceLoadMonitor(IEnumerable<SystemLoadSnapshot> values)
            {
                snapshots = new Queue<SystemLoadSnapshot>();
                if (values != null)
                {
                    foreach (SystemLoadSnapshot value in values)
                    {
                        snapshots.Enqueue(value);
                    }
                }
            }

            public Task<SystemLoadSnapshot> ReadAsync(TimeSpan sampleInterval, CancellationToken cancellationToken)
            {
                if (snapshots.Count > 0)
                {
                    last = snapshots.Dequeue();
                }
                if (last == null)
                {
                    last = new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 1, MemoryPercent = 1 };
                }
                return Task.FromResult(last);
            }
        }

        private sealed class FixedLoadMonitor : ISystemLoadMonitor
        {
            public Task<SystemLoadSnapshot> ReadAsync(TimeSpan sampleInterval, CancellationToken cancellationToken)
            {
                return Task.FromResult(new SystemLoadSnapshot { IsAvailable = true, CpuPercent = 1, MemoryPercent = 1 });
            }
        }

        private sealed class TestCaptureService : ISessionCaptureService
        {
            private readonly List<SessionItem> capturedItems;

            public TestCaptureService()
            {
                capturedItems = new List<SessionItem>();
            }

            public TestCaptureService(IList<SessionItem> items)
            {
                capturedItems = items == null ? new List<SessionItem>() : new List<SessionItem>(items);
            }

            public SessionSnapshot Capture()
            {
                return new SessionSnapshot { Items = new List<SessionItem>(capturedItems) };
            }

            public SessionSnapshot Capture(SessionCaptureOptions options)
            {
                return Capture();
            }
        }

        private sealed class RecordingDesktopApplicationService : IDesktopApplicationService
        {
            public DesktopEntry LastEntry { get; private set; }

            public IList<DesktopEntry> Scan()
            {
                return new List<DesktopEntry>();
            }

            public LaunchResult Launch(DesktopEntry entry)
            {
                LastEntry = entry;
                return new LaunchResult { Success = true };
            }
        }

        private sealed class TestDesktopApplicationService : IDesktopApplicationService
        {
            public int LaunchCallCount { get; private set; }

            public IList<DesktopEntry> Scan()
            {
                return new List<DesktopEntry>();
            }

            public LaunchResult Launch(DesktopEntry entry)
            {
                LaunchCallCount++;
                return new LaunchResult { Success = false, Error = "测试启动已抑制。" };
            }
        }

        private sealed class RecordingRestoreService : ISessionRestoreService
        {
            public int CallCount { get; private set; }

            public Task<RestoreResult> RestoreAsync(
                SessionSnapshot snapshot,
                RestoreOptions options,
                IProgress<RestoreProgress> progress,
                CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(new RestoreResult
                {
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// 记录转接清单与参数，并返回每一项都成功的恢复结果。
        /// 用于验证“自启动方案 → 共用恢复服务”这一层，不启动任何外部程序。
        /// </summary>
        private sealed class CapturingRestoreService : ISessionRestoreService
        {
            public int CallCount { get; private set; }

            public SessionSnapshot LastSnapshot { get; private set; }

            public RestoreOptions LastOptions { get; private set; }

            public Task<RestoreResult> RestoreAsync(
                SessionSnapshot snapshot,
                RestoreOptions options,
                IProgress<RestoreProgress> progress,
                CancellationToken cancellationToken)
            {
                CallCount++;
                LastSnapshot = snapshot;
                LastOptions = options;
                RestoreResult result = new RestoreResult
                {
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow
                };
                if (snapshot != null && snapshot.Items != null)
                {
                    for (int index = 0; index < snapshot.Items.Count; index++)
                    {
                        result.Items.Add(new RestoreItemResult
                        {
                            Item = snapshot.Items[index],
                            AttemptedAtUtc = DateTime.UtcNow,
                            Success = true
                        });
                    }
                }
                return Task.FromResult(result);
            }
        }
        /// <summary>清掉“今日已做过”标记（测试之间互不影响）。</summary>
        private static void ResetAutoRestoreMarkers()
        {
            try
            {
                string todayPath = WorkspaceRestorer.Infrastructure.AutoRestoreMarker.TodayPath();
                string directory = Path.GetDirectoryName(todayPath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }
                string[] files = Directory.GetFiles(directory, "*.标记");
                for (int index = 0; index < files.Length; index++)
                {
                    try { File.Delete(files[index]); } catch { }
                }
            }
            catch
            {
            }
        }

        private static ApplicationPlanItem NewApplicationItem(string name)
        {
            return new ApplicationPlanItem
            {
                DisplayName = name,
                TargetPath = @"C:\test\" + name + ".exe",
                Kind = SessionItemKind.Application,
                IsEnabled = true
            };
        }

        private static string JoinItemNames(IList<SessionItem> items)
        {
            List<string> names = new List<string>();
            for (int index = 0; index < (items == null ? 0 : items.Count); index++)
            {
                if (items[index] != null)
                {
                    names.Add(items[index].DisplayName ?? string.Empty);
                }
            }
            return string.Join(",", names.ToArray());
        }

        private static int ProfileIndex(ApplicationController controller, StartupProfile profile)
        {
            for (int index = 0; index < controller.StartupProfiles.Count; index++)
            {
                if (object.ReferenceEquals(controller.StartupProfiles[index], profile)
                    || (profile != null && controller.StartupProfiles[index] != null
                        && string.Equals(controller.StartupProfiles[index].Id, profile.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    return index;
                }
            }
            return -1;
        }

        private static StartupProfile FindProfileByName(ApplicationController controller, string name)
        {
            for (int index = 0; index < controller.StartupProfiles.Count; index++)
            {
                StartupProfile candidate = controller.StartupProfiles[index];
                if (candidate != null && string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// 修好旧方案里丢失的窗口身份：直接调产品的修复方法。
        /// 用反射是因为 BrowserPageGrouping 是内部实现，不该为了测试放宽可见性。
        /// </summary>
        private static int RepairBrowserPageGrouping(IList<SessionItem> items)
        {
            Type groupingType = typeof(SessionItem).Assembly.GetType(
                "WorkspaceRestorer.Services.BrowserPageGrouping",
                false);
            Assert(groupingType != null, "找不到 BrowserPageGrouping 类型。" );
            MethodInfo method = groupingType.GetMethod(
                "Repair",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert(method != null, "找不到 BrowserPageGrouping.Repair。" );
            return (int)method.Invoke(null, new object[] { items });
        }

        /// <summary>
        /// 调产品里那份“按本机时区格式化时间”的内部实现（TimeText 是 internal，
        /// 用反射调，避免为了测试放宽产品代码的可见性）。
        /// </summary>
        private static string CallTimeText(string methodName, object[] arguments)
        {
            Type toolsType = typeof(SessionItem).Assembly.GetType(
                "WorkspaceRestorer.Core.TimeText",
                false);
            Assert(toolsType != null, "找不到 TimeText 类型。" );
            // TimeText 现在有带语言代码的重载；按参数个数选旧的无语言重载，
            // 不能直接用 GetMethod(name)，否则会抛 AmbiguousMatchException。
            MethodInfo method = toolsType
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(delegate(MethodInfo item)
                {
                    return item.Name == methodName
                        && item.GetParameters().Length == arguments.Length;
                });
            Assert(method != null, "找不到 TimeText." + methodName + "。" );
            object value = method.Invoke(null, arguments);
            return value as string;
        }

        private static string FormatComputerLocalStampByReflection(DateTime utcTime)
        {
            return CallTimeText("FormatUtcAsComputerLocalStamp", new object[] { utcTime });
        }

        private static string FormatZoneLabelByReflection(TimeZoneInfo zone)
        {
            return CallTimeText("FormatZoneLabel", new object[] { zone });
        }
    }
}
