using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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

        private static void RunBrowserAndWindowRestoreTests()
        {
            Run("浏览器单窗口名称和多标签数量识别", delegate
            {
                Type captureType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.SessionCaptureService",
                    true);
                MethodInfo normalizeMethod = captureType.GetMethod(
                    "NormalizeBrowserWindowDisplayNames",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo countMethod = captureType.GetMethod(
                    "ParseBrowserTabCountFromTitle",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(normalizeMethod != null, "缺少浏览器窗口名称归一化逻辑。" );
                Assert(countMethod != null, "缺少浏览器标题标签数量解析逻辑。" );

                List<SessionItem> singleWindow = new List<SessionItem>
                {
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        BrowserName = "Microsoft Edge",
                        DisplayName = "Microsoft Edge (1)",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = 0
                    }
                };
                normalizeMethod.Invoke(null, new object[] { singleWindow });
                AssertEqual("Microsoft Edge", singleWindow[0].DisplayName);

                List<SessionItem> legacySingleWindow = new List<SessionItem>
                {
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        DisplayName = "Microsoft Edge (1)",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = 0
                    }
                };
                normalizeMethod.Invoke(null, new object[] { legacySingleWindow });
                AssertEqual("Microsoft Edge", legacySingleWindow[0].DisplayName);

                List<SessionItem> multipleWindows = new List<SessionItem>
                {
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        BrowserName = "Microsoft Edge",
                        DisplayName = "Microsoft Edge (1)",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = 0
                    },
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        BrowserName = "Microsoft Edge",
                        DisplayName = "Microsoft Edge (2)",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = 1
                    }
                };
                normalizeMethod.Invoke(null, new object[] { multipleWindows });
                AssertEqual("Microsoft Edge (1)", multipleWindows[0].DisplayName);
                AssertEqual("Microsoft Edge (2)", multipleWindows[1].DisplayName);

                List<SessionItem> legacyMultipleWindows = new List<SessionItem>
                {
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        BrowserName = "Microsoft Edge",
                        DisplayName = "Microsoft Edge",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = -1
                    },
                    new SessionItem
                    {
                        Kind = SessionItemKind.Application,
                        BrowserName = "Microsoft Edge",
                        DisplayName = "Microsoft Edge",
                        BrowserGroupKey = "C:\\Browsers\\msedge.exe",
                        BrowserWindowIndex = -1
                    }
                };
                normalizeMethod.Invoke(null, new object[] { legacyMultipleWindows });
                AssertEqual("Microsoft Edge (1)", legacyMultipleWindows[0].DisplayName);
                AssertEqual("Microsoft Edge (2)", legacyMultipleWindows[1].DisplayName);

                int chineseCount = (int)countMethod.Invoke(
                    null,
                    new object[] { "API 密钥 - 玖猫的Api 和另外 3 个页面 - Microsoft Edge" });
                int englishCount = (int)countMethod.Invoke(
                    null,
                    new object[] { "Dashboard and 3 more tabs - Microsoft Edge" });
                AssertEqual(4, chineseCount);
                AssertEqual(4, englishCount);
            });

            Run("网页跳转：标签名字是网址时按 URL 识别", delegate
            {
                Type webAddressType = productAssembly.GetType(
                    "WorkspaceRestorer.Infrastructure.WebAddressTools",
                    true);
                MethodInfo canonicalMethod = webAddressType.GetMethod(
                    "CanonicalizeForMatch",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo tabUrlMatchMethod = webAddressType.GetMethod(
                    "TabNameMatchesUrl",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(canonicalMethod != null, "缺少 URL 规范化逻辑。" );
                Assert(tabUrlMatchMethod != null, "缺少标签名字与 URL 比较逻辑。" );

                string savedUrl = "https://keycrop.net/login?redirect=%2Faccount";
                string tabName = "keycrop.net/login?redirect=%2Faccount";
                string canonical = (string)canonicalMethod.Invoke(null, new object[] { savedUrl });
                AssertEqual("keycrop.net/login?redirect=/account", canonical);
                bool matched = (bool)tabUrlMatchMethod.Invoke(
                    null, new object[] { tabName, savedUrl });
                Assert(matched, "标签名字是省略协议的同一网址时必须能识别。" );
                bool titleMatched = (bool)tabUrlMatchMethod.Invoke(
                    null, new object[] { "Sign in to KeyCrop - 内存使用率 - 83.2 MB", savedUrl });
                Assert(!titleMatched, "普通页面标题不能被误判成网址。" );
            });

            Run("网页跳转：整窗标签指纹随 JSON 保存", delegate
            {
                SessionItem page = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    DisplayName = "Sign in to KeyCrop",
                    Url = "https://keycrop.net/login?redirect=%2Faccount",
                    BrowserTabIndex = 6,
                    BrowserBindingKind = BrowserPageBindingKind.Captured,
                    BrowserWindowTitleSnapshot = "Edge 测试窗口",
                    BrowserTabs = new List<BrowserTabFingerprint>
                    {
                        new BrowserTabFingerprint
                        {
                            Index = 6,
                            Title = "Sign in to KeyCrop",
                            Url = "https://keycrop.net/login?redirect=%2Faccount",
                            IsActive = false
                        }
                    }
                };
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SessionItem));
                string json;
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, page);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }
                Assert(json.IndexOf("\"BrowserTabs\"", StringComparison.Ordinal) >= 0,
                    "SessionItem JSON 没有保存 BrowserTabs。" );
                Assert(json.IndexOf("\"BrowserWindowTitleSnapshot\"", StringComparison.Ordinal) >= 0,
                    "SessionItem JSON 没有保存窗口标题快照。" );

                SessionItem restored;
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    restored = (SessionItem)serializer.ReadObject(stream);
                }
                Assert(restored.BrowserTabs != null && restored.BrowserTabs.Count == 1,
                    "SessionItem 读回后丢失 BrowserTabs。" );
                AssertEqual(6, restored.BrowserTabs[0].Index);
                AssertEqual("https://keycrop.net/login?redirect=%2Faccount",
                    restored.BrowserTabs[0].Url);
                AssertEqual((int)BrowserPageBindingKind.Captured, (int)restored.BrowserBindingKind);

                ApplicationPlanItem planItem = new ApplicationPlanItem
                {
                    DisplayName = "Sign in to KeyCrop",
                    TargetPath = page.Url,
                    IsBrowserPage = true,
                    BrowserTabIndex = 6,
                    StartTimeUtc = DateTime.UtcNow,
                    BrowserTabs = BrowserTabFingerprint.CloneList(page.BrowserTabs)
                };
                DataContractJsonSerializer planSerializer =
                    new DataContractJsonSerializer(typeof(ApplicationPlanItem));
                using (MemoryStream stream = new MemoryStream())
                {
                    planSerializer.WriteObject(stream, planItem);
                    string planJson = Encoding.UTF8.GetString(stream.ToArray());
                    Assert(planJson.IndexOf("\"BrowserTabs\"", StringComparison.Ordinal) >= 0,
                        "ApplicationPlanItem JSON 没有保存 BrowserTabs。" );
                }
            });

            Run("窗口恢复保留保存的屏幕位置、状态和 JSON 数据", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo positionMethod = serviceType.GetMethod(
                    "ResolveSavedScreenPosition",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(positionMethod != null, "缺少保存窗口屏幕坐标解析逻辑。" );

                SavedWindowPlacement maximizedPlacement = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = -1440,
                    Top = 80,
                    Width = 1200,
                    Height = 800,
                    WindowState = "Maximized",
                    WindowStateBeforeMinimize = "Normal",
                    MinimizeChoiceExplicit = true
                };
                int[] maximizedPosition = (int[])positionMethod.Invoke(
                    null,
                    new object[] { maximizedPlacement });
                Assert(maximizedPosition != null && maximizedPosition.Length == 2,
                    "窗口坐标解析结果必须包含保存的 X/Y。" );
                AssertEqual(-1440, maximizedPosition[0]);
                AssertEqual(80, maximizedPosition[1]);
                AssertEqual("Maximized", maximizedPlacement.WindowState);

                SavedWindowPlacement minimizedPlacement = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 760,
                    Top = 420,
                    WindowState = "Minimized"
                };
                int[] minimizedPosition = (int[])positionMethod.Invoke(
                    null,
                    new object[] { minimizedPlacement });
                Assert(minimizedPosition != null && minimizedPosition.Length == 2,
                    "旧位置记录解析结果不能缺少坐标。" );
                AssertEqual(760, minimizedPosition[0]);
                AssertEqual(420, minimizedPosition[1]);
                AssertEqual("Minimized", minimizedPlacement.WindowState);
                Assert(minimizedPosition[0] != 0 || minimizedPosition[1] != 0,
                    "旧版保存的位置不能被换算成屏幕原点。" );

                Type placementType = typeof(SavedWindowPlacement);
                Assert(placementType.GetProperty("Width") != null, "窗口尺寸 Width 字段缺失。" );
                Assert(placementType.GetProperty("Height") != null, "窗口尺寸 Height 字段缺失。" );
                Assert(placementType.GetProperty("WindowStateBeforeMinimize") != null,
                    "缺少“最小化前状态”字段，取消最小化无法恢复最大化。" );
                Assert(placementType.GetProperty("MinimizeChoiceExplicit") != null,
                    "缺少“用户手动最小化”标记，保存当前方案会覆盖用户选择。" );
                Assert(placementType.GetProperty("NormalizedWidth") == null, "归一化宽度字段仍残留。" );
                Assert(placementType.GetProperty("NormalizedHeight") == null, "归一化高度字段仍残留。" );
                Assert(placementType.GetProperty("NormalizedLeft") == null, "归一化左坐标字段仍残留。" );
                Assert(placementType.GetProperty("NormalizedTop") == null, "归一化顶坐标字段仍残留。" );
                Assert(placementType.GetProperty("MonitorKey") == null, "窗口显示器标识字段仍残留。" );

                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(SavedWindowPlacement));
                string json;
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, maximizedPlacement);
                    json = Encoding.UTF8.GetString(stream.ToArray());
                }
                Assert(json.IndexOf("\"Left\":-1440", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存窗口的 X 坐标。" );
                Assert(json.IndexOf("\"Top\":80", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存窗口的 Y 坐标。" );
                Assert(json.IndexOf("\"Width\":1200", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存窗口的宽度。" );
                Assert(json.IndexOf("\"Height\":800", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存窗口的高度。" );
                Assert(json.IndexOf("\"WindowState\":\"Maximized\"", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存窗口的最大化状态。" );
                Assert(json.IndexOf("\"WindowStateBeforeMinimize\":\"Normal\"", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存最小化前状态。" );
                Assert(json.IndexOf("\"MinimizeChoiceExplicit\":true", StringComparison.Ordinal) >= 0,
                    "JSON 没有保存用户手动最小化标记。" );
                Assert(json.IndexOf("Normalized", StringComparison.Ordinal) < 0,
                    "JSON 不应包含归一化字段。" );

                SavedWindowPlacement roundTripPlacement;
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    roundTripPlacement = serializer.ReadObject(stream) as SavedWindowPlacement;
                }
                Assert(roundTripPlacement != null, "窗口位置 JSON 反序列化结果为空。" );
                Assert(roundTripPlacement.HasBounds, "JSON 往返后 HasBounds 没有保留。" );
                AssertEqual(-1440, roundTripPlacement.Left);
                AssertEqual(80, roundTripPlacement.Top);
                AssertEqual(1200, roundTripPlacement.Width);
                AssertEqual(800, roundTripPlacement.Height);
                AssertEqual("Maximized", roundTripPlacement.WindowState);
                AssertEqual("Normal", roundTripPlacement.WindowStateBeforeMinimize);
                Assert(roundTripPlacement.MinimizeChoiceExplicit,
                    "JSON 往返后用户手动最小化标记没有保留。" );

                const string legacyJson =
                    "{\"HasBounds\":true,\"Left\":760,\"Top\":420,"
                    + "\"WindowState\":\"Minimized\",\"MonitorKey\":\"legacy\","
                    + "\"NormalizedLeft\":0.5,\"NormalizedTop\":0.5,"
                    + "\"Width\":800,\"Height\":600}";
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(legacyJson)))
                {
                    SavedWindowPlacement legacyPlacement = serializer.ReadObject(stream) as SavedWindowPlacement;
                    Assert(legacyPlacement != null, "旧版窗口位置 JSON 无法读取。" );
                    AssertEqual(760, legacyPlacement.Left);
                    AssertEqual(420, legacyPlacement.Top);
                    AssertEqual("Minimized", legacyPlacement.WindowState);
                    Assert(legacyPlacement.WindowStateBeforeMinimize == null,
                        "旧版 JSON 不应凭空产生“最小化前状态”。" );
                    Assert(!legacyPlacement.MinimizeChoiceExplicit,
                        "旧版 JSON 不应凭空产生用户手动最小化标记。" );
                }
            });

            Run("屏幕变小或换屏后窗口一定被拉回可见区（外面最多 16 像素）", delegate
            {
                Type fitType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowScreenFit",
                    true);
                MethodInfo fitMethod = fitType.GetMethod(
                    "Fit",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(fitMethod != null, "缺少“把窗口拉回可见区”的算法。" );

                // 笔记本屏（1920x1080，任务栏 40 像素）的工作区。
                int[] laptop = new[] { 0, 0, 1920, 1040 };

                // ① 完全在里面：一个像素都不许动（用户自己摆的位置要保住）。
                int[] untouched = (int[])fitMethod.Invoke(null, new object[] { 100, 80, 1200, 800, laptop });
                AssertEqual(100, untouched[0]);
                AssertEqual(80, untouched[1]);
                AssertEqual(1200, untouched[2]);
                AssertEqual(800, untouched[3]);

                // ② 外接屏（2560 宽）拔掉，窗原来摆在 x=2400：已经完全贴回右边。
                int[] pulledBack = (int[])fitMethod.Invoke(null, new object[] { 2400, 120, 1200, 800, laptop });
                AssertEqual(1920 - 1200, pulledBack[0]);
                AssertEqual(120, pulledBack[1]);
                AssertEqual(1200, pulledBack[2]);
                AssertEqual(800, pulledBack[3]);

                // ③ 窗比屏幕还大（2560x1440 摆到 1920x1040 上）：缩到屏幕大小并贴左上角，
                //    否则右边 640 像素永远在屏幕外、拖不回来（这正是用户报的现象）。
                int[] shrunk = (int[])fitMethod.Invoke(null, new object[] { 3000, -100, 2560, 1440, laptop });
                AssertEqual(0, shrunk[0]);
                AssertEqual(0, shrunk[1]);
                AssertEqual(1920, shrunk[2]);
                AssertEqual(1040, shrunk[3]);

                // ④ 故意挂出去一点点（16 像素以内）：保持原样；超过 16 像素就完全贴回 0。
                int[] slight = (int[])fitMethod.Invoke(null, new object[] { -16, 100, 1200, 800, laptop });
                AssertEqual(-16, slight[0]);
                int[] tooFar = (int[])fitMethod.Invoke(null, new object[] { -400, 100, 1200, 800, laptop });
                AssertEqual(0, tooFar[0]);

                // ⑤ 跑到下面（换屏后窗口整块落在屏幕外）：完全拉回屏幕内。
                int[] below = (int[])fitMethod.Invoke(null, new object[] { 200, 2000, 1200, 800, laptop });
                AssertEqual(1040 - 800, below[1]);

                // ⑥ 上边越界：标题栏必须重新回到屏幕里（上边一像素都不许露）。
                int[] above = (int[])fitMethod.Invoke(null, new object[] { 200, -600, 1200, 800, laptop });
                AssertEqual(0, above[1]);

                // ⑦ 副屏在左边：负坐标是合法的，不能被夹成 0。
                int[] leftMonitor = new[] { -1920, 0, 0, 1080 };
                int[] onLeftMonitor = (int[])fitMethod.Invoke(null, new object[] { -1800, 100, 1200, 800, leftMonitor });
                AssertEqual(-1800, onLeftMonitor[0]);
                AssertEqual(100, onLeftMonitor[1]);

                // ⑧ 旧记录没有宽高：只按 220 估算可见性，不能把 220 当成真实大小写回去
                //    （调用方那边靠“原宽高为 0 就不改大小”保证，这里只验证估算结果仍可见）。
                int[] unknownSize = (int[])fitMethod.Invoke(null, new object[] { 1000, 2000, 0, 0, laptop });
                AssertEqual(1040 - 220, unknownSize[1]);

                // ⑨ 工作区数据坏掉：原样返回，绝不把窗口挪到奇怪的地方。
                int[] broken = (int[])fitMethod.Invoke(null, new object[] { 300, 400, 800, 600, new[] { 0, 0, 0, 0 } });
                AssertEqual(300, broken[0]);
                AssertEqual(400, broken[1]);

                // ⑩ 通例：一堆真实可能出现的“换屏后”组合，逐个检查四条边界的硬约束。
                int[][] workAreas = new[]
                {
                    laptop,
                    new[] { 0, 0, 1366, 728 },                    // 更小的老笔记本屏
                    new[] { 0, 0, 2560, 1400 },                   // 又换回大屏
                    new[] { -1920, 0, 0, 1080 },                  // 副屏在左
                    new[] { 1920, 0, 3840, 1040 }                 // 副屏在右
                };
                int[][] savedRects = new[]
                {
                    new[] { 3000, 120, 1200, 800 },               // 完全在已拔掉的右屏上
                    new[] { 2400, -300, 1600, 1000 },             // 右屏 + 顶部越界
                    new[] { -300, 900, 1400, 900 },               // 左下角越界
                    new[] { 100, 100, 1200, 800 },                // 正常
                    new[] { -1900, 20, 1500, 900 },               // 左副屏
                    new[] { 5000, 5000, 400, 300 },               // 远在屏幕外
                    new[] { 0, 0, 4000, 3000 }                    // 比任何屏幕都大
                };
                foreach (int[] area in workAreas)
                {
                    foreach (int[] rect in savedRects)
                    {
                        int[] fitted = (int[])fitMethod.Invoke(
                            null,
                            new object[] { rect[0], rect[1], rect[2], rect[3], area });
                        int areaWidth = area[2] - area[0];
                        int areaHeight = area[3] - area[1];
                        string where = string.Format(
                            "（保存 {0},{1} {2}x{3} / 工作区 {4},{5}-{6},{7} → {8},{9} {10}x{11}）",
                            rect[0], rect[1], rect[2], rect[3],
                            area[0], area[1], area[2], area[3],
                            fitted[0], fitted[1], fitted[2], fitted[3]);
                        Assert(fitted[2] <= areaWidth && fitted[2] > 0, "窗口宽度没有收进屏幕" + where);
                        Assert(fitted[3] <= areaHeight && fitted[3] > 0, "窗口高度没有收进屏幕" + where);
                        Assert(area[0] - fitted[0] <= 16, "左边露在屏幕外超过 16 像素" + where);
                        Assert(fitted[0] + fitted[2] - area[2] <= 16, "右边露在屏幕外超过 16 像素" + where);
                        Assert(area[1] - fitted[1] <= 0, "窗口上边跑到屏幕上边以外（标题栏会看不见）" + where);
                        Assert(fitted[1] + fitted[3] - area[3] <= 16, "下边露在屏幕外超过 16 像素" + where);
                    }
                }

                // ⑪ “关闭到托盘”的 -32000 哨兵值：不能只拉回到左边露 16 像素，
                //    否则窗口会贴着屏幕左缘，看起来像卡出去一半。
                int[] hiddenSentinel = (int[])fitMethod.Invoke(
                    null,
                    new object[] { -32000, -32000, 1042, 731, laptop });
                AssertEqual(0, hiddenSentinel[0]);
                AssertEqual(0, hiddenSentinel[1]);
                AssertEqual(1042, hiddenSentinel[2]);
                AssertEqual(731, hiddenSentinel[3]);

                MethodInfo hiddenMethod = fitType.Assembly.GetType(
                    "WorkspaceRestorer.Services.WindowVisibility",
                    true).GetMethod(
                    "IsHiddenRect",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(hiddenMethod != null, "缺少“隐藏到屏幕外”判定。" );
                Assert((bool)hiddenMethod.Invoke(null, new object[] { -32000, -32000, -30958, -31269 }),
                    "整块在 -30000 以外的矩形必须判为隐藏哨兵。" );
                Assert(!(bool)hiddenMethod.Invoke(null, new object[] { -1800, 100, -600, 900 }),
                    "左边副屏的合法负坐标不能被当成隐藏哨兵。" );
            });

            Run("恢复前体检：隐藏条目跳过、最小化哨兵和远距离越界先夹回屏幕", delegate
            {
                // 用户 2026-10-31：窗口不能再被放到屏幕外。恢复前先体检保存坐标，
                // 不能等启动后再拉。
                Type sanitizerType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementSanitizer",
                    true);
                MethodInfo sanitize = sanitizerType.GetMethod(
                    "Sanitize",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(sanitize != null, "缺少恢复前坐标体检。" );

                Func<SavedWindowPlacement, object> run = delegate(SavedWindowPlacement placement)
                {
                    return sanitize.Invoke(null, new object[] { placement });
                };
                Func<object, string> actionOf = delegate(object result)
                {
                    return result.GetType().GetProperty(
                        "Action",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .GetValue(result, null).ToString();
                };
                Func<object, SavedWindowPlacement> placementOf = delegate(object result)
                {
                    return (SavedWindowPlacement)result.GetType().GetProperty(
                        "Placement",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .GetValue(result, null);
                };

                SavedWindowPlacement hidden = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = -32000,
                    Top = -32000,
                    Width = 1042,
                    Height = 731,
                    WindowState = "Normal"
                };
                object hiddenResult = run(hidden);
                AssertEqual("Skip", actionOf(hiddenResult));
                AssertEqual(-32000, placementOf(hiddenResult).Left);

                SavedWindowPlacement minimized = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = -32000,
                    Top = -32000,
                    Width = 1042,
                    Height = 731,
                    WindowState = "Minimized"
                };
                object minimizedResult = run(minimized);
                AssertEqual("Clamp", actionOf(minimizedResult));
                Assert(placementOf(minimizedResult).Left > -30000,
                    "最小化窗口的哨兵坐标也必须被修到可见位置。" );

                SavedWindowPlacement farAway = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 100000,
                    Top = 100000,
                    Width = 1042,
                    Height = 731,
                    WindowState = "Normal"
                };
                object farResult = run(farAway);
                AssertEqual("Clamp", actionOf(farResult));
                Assert(placementOf(farResult).Left < 100000,
                    "远距离越界的保存坐标必须被夹回屏幕。" );

                SavedWindowPlacement normal = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 100,
                    Top = 80,
                    Width = 1200,
                    Height = 800,
                    WindowState = "Normal"
                };
                AssertEqual("Keep", actionOf(run(normal)));
            });

            Run("卡片落点：中间并入、上下窄边与卡缝插入，并且鼠标停着不会翻来翻去（防闪）", delegate
            {
                // 用户 2026-10-17 报的真问题：“移动到两份方案中间、停 1 秒不松手，辅助分割线一直闪”。
                // 这里把那段纯几何逻辑（StartupRestore/Core/ProfileCardDropZones.cs）当成规格来测。
                ProfileCardSlot first = new ProfileCardSlot { Id = "a", Left = 0, Width = 800, Top = 0, Height = 60 };
                ProfileCardSlot second = new ProfileCardSlot { Id = "b", Left = 0, Width = 800, Top = 68, Height = 60 };
                ProfileCardSlot third = new ProfileCardSlot { Id = "c", Left = 0, Width = 800, Top = 136, Height = 60 };
                List<ProfileCardSlot> slots = new List<ProfileCardSlot> { first, second, third };

                // ① 窄带宽度：60 高的卡片 → 15 像素（25%）；很矮/很高的卡片也有上下限。
                AssertEdgeBand(60.0, 15.0);
                AssertEdgeBand(400.0, 24.0);
                AssertEdgeBand(20.0, 7.0);

                // ② 第一张的上边缘窄带 / 之上太远 / 中间 / 下边缘窄带
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, -10.0), ProfileCardDropKind.Insert, 0, "第一张上边缘窄带里 = 插到最前面");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, -40.0), ProfileCardDropKind.None, -1, "离第一张太远 = 没有落点");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 5.0), ProfileCardDropKind.Insert, 0, "第一张顶部 5 像素 = 插到最前面");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 30.0), ProfileCardDropKind.Merge, 0, "卡片正中 = 并入这张");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 52.0), ProfileCardDropKind.Insert, 1, "卡片底部 8 像素 = 插到它后面");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 64.0), ProfileCardDropKind.Insert, 1, "两张卡片之间的卡缝 = 单纯插入（不是并入）");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 190.0), ProfileCardDropKind.Insert, 3, "最后一张的下边缘 = 插到最后");
                AssertDrop(ProfileCardDropZones.ResolveRaw(slots, 400.0), ProfileCardDropKind.None, -1, "列表下面很远 = 没有落点");

                // ③ 滞回：先落在“并入”上，指针再往边缘走 4 像素，不能立刻翻成插入。
                ProfileCardDropDecision merge = ProfileCardDropZones.Resolve(slots, 44.0, null);
                AssertDrop(merge, ProfileCardDropKind.Merge, 0, "指针压在卡片中下部 = 并入");
                ProfileCardDropDecision held = ProfileCardDropZones.Resolve(slots, 47.0, merge);
                AssertDrop(held, ProfileCardDropKind.Merge, 0, "往边缘走了 3 像素（还在滞回里）→ 必须还是并入");
                ProfileCardDropDecision switched = ProfileCardDropZones.Resolve(slots, 60.0, held);
                AssertDrop(switched, ProfileCardDropKind.Insert, 1, "走到下边缘带里才换成插入");

                // ④ 【防闪的核心】同一个指针位置反复算，结果必须一模一样（旧实现就是因为
                //    “显示插入线 → 布局被推 6 像素 → 落点变了 → 撤线 → 又变回来”而死循环）。
                ProfileCardDropDecision stable = ProfileCardDropZones.Resolve(slots, 16.0, null);
                ProfileCardDropKind stableKind = stable.Kind;
                int stableIndex = stable.Index;
                for (int i = 0; i < 30; i++)
                {
                    stable = ProfileCardDropZones.Resolve(slots, 16.0, stable);
                    AssertDrop(stable, stableKind, stableIndex,
                        "同一位置第 " + (i + 2) + " 次仍然应该是同一个落点");
                }

                // ⑤ 指针在边界上来回抖（±2 像素）：落点一次都不许变。
                ProfileCardDropDecision jitter = null;
                double[] jitterYs = { 14.0, 16.0, 14.5, 15.5, 13.0, 17.0, 15.0, 16.5 };
                for (int round = 0; round < 6; round++)
                {
                    for (int i = 0; i < jitterYs.Length; i++)
                    {
                        jitter = ProfileCardDropZones.Resolve(slots, jitterYs[i], jitter);
                        if (round > 0)
                        {
                            AssertDrop(jitter, ProfileCardDropKind.Insert, 0, "边界抖动时落点不许翻（第 " + (round + 1) + " 轮 y=" + jitterYs[i] + "）");
                        }
                    }
                }

                // ⑥ 空列表不许炸。
                AssertDrop(ProfileCardDropZones.ResolveRaw(new List<ProfileCardSlot>(), 10.0), ProfileCardDropKind.None, -1, "列表为空时没有落点");
            });

            Run("多屏拓扑变化：三屏→单屏、单屏→双屏、屏序对调、复制模式都要放对地方", delegate
            {
                // 用户 2026-10-18 原话：
                // “要兼顾屏幕变小/变大（笔记本外接显示器，外接显示器断掉转笔记本，或者显示器大小变化），
                //  以及双屏，三屏幕，多屏的应用存放位置（扩展或复制），还有他们相互转换例如三屏变单屏等，
                //  但是单屏变双屏，那么就在主屏幕上。”
                Type monitorType = productAssembly.GetType("WorkspaceRestorer.Services.MonitorWorkArea", true);
                Type resultType = productAssembly.GetType("WorkspaceRestorer.Services.MonitorFitResult", true);
                Type fitType = productAssembly.GetType("WorkspaceRestorer.Services.MonitorTopologyFit", true);
                Assert(monitorType != null && resultType != null && fitType != null, "缺少多屏拓扑算法（MonitorTopologyFit）。" );
                MethodInfo decide = fitType.GetMethod("Decide", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo normalize = fitType.GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static);
                Assert(decide != null && normalize != null, "缺少 Decide / Normalize。" );
                Type monitorListType = typeof(List<>).MakeGenericType(monitorType);
                MethodInfo listAdd = monitorListType.GetMethod("Add");

                Func<int, int, int, int, bool, object> makeMonitor = delegate(int left, int top, int right, int bottom, bool primary)
                {
                    object monitor = Activator.CreateInstance(monitorType);
                    monitorType.GetField("Left").SetValue(monitor, left);
                    monitorType.GetField("Top").SetValue(monitor, top);
                    monitorType.GetField("Right").SetValue(monitor, right);
                    monitorType.GetField("Bottom").SetValue(monitor, bottom);
                    monitorType.GetField("IsPrimary").SetValue(monitor, primary);
                    return monitor;
                };
                Func<object[], object> makeList = delegate(object[] items)
                {
                    object list = Activator.CreateInstance(monitorListType);
                    foreach (object item in items)
                    {
                        listAdd.Invoke(list, new[] { item });
                    }
                    return list;
                };
                Func<object, object, object, object, object, int, int, int, object> callDecide =
                    delegate(object list, object l, object t, object w, object h, int index, int count, int placed)
                {
                    return decide.Invoke(null, new[] { list, l, t, w, h, (object)index, (object)count, (object)placed });
                };
                Func<object, string, int> readInt = delegate(object target, string field)
                {
                    return (int)resultType.GetField(field).GetValue(target);
                };
                Func<object, string, bool> readBool = delegate(object target, string field)
                {
                    return (bool)resultType.GetField(field).GetValue(target);
                };

                // 笔记本主屏 1920x1080（工作区 1040）+ 右副屏 2560x1440 + 更右的第三块
                object laptopScreen = makeMonitor(0, 0, 1920, 1040, true);
                object rightScreen = makeMonitor(1920, 0, 4480, 1400, false);
                object thirdScreen = makeMonitor(4480, 0, 6400, 1040, false);

                // ① 三屏 → 单屏：原来在第三块屏上的窗口必须回到唯一的屏里，且整块可见。
                object threeList = makeList(new[] { laptopScreen, rightScreen, thirdScreen });
                object oneList = makeList(new[] { laptopScreen });
                // 现在的屏幕只有一块（oneList）——这才是“三屏变单屏”。
                object toSingle = callDecide(oneList, 5000, 200, 1200, 800, 2, 3, 0);
                Assert(toSingle != null, "三屏→单屏应当给出落点。" );
                AssertEqual(0, readInt(toSingle, "MonitorIndex"));
                Assert(readBool(toSingle, "TopologyChanged"), "三屏→单屏必须标记为拓扑变化。" );
                Assert(readBool(toSingle, "PlacedOnPrimary"), "三屏→单屏应当放到主屏。" );
                int singleLeft = readInt(toSingle, "Left");
                int singleTop = readInt(toSingle, "Top");
                int singleWidth = readInt(toSingle, "Width");
                int singleHeight = readInt(toSingle, "Height");
                Assert(singleLeft >= -16 && singleLeft + singleWidth <= 1920 + 16, "三屏→单屏后窗口必须横向可见。" );
                Assert(singleTop >= 0 && singleTop + singleHeight <= 1040 + 16, "三屏→单屏后窗口必须纵向可见。" );

                // ② 拓扑变少屏时，第二个窗口要阶梯错开（不能全叠在同一点）。
                object toSingleSecond = callDecide(oneList, 5000, 200, 1200, 800, 2, 3, 1);
                Assert(readInt(toSingleSecond, "Left") != singleLeft || readInt(toSingleSecond, "Top") != singleTop,
                    "拓扑变少屏时第二个窗口应当错开。" );

                // ①b 三屏还是三屏（没变）：按保存时的屏序号对应，不该标记拓扑变化。
                object sameThree = callDecide(threeList, 5000, 200, 1200, 800, 2, 3, 0);
                AssertEqual(2, readInt(sameThree, "MonitorIndex"));
                Assert(!readBool(sameThree, "TopologyChanged"), "屏数没变不应标记拓扑变化。" );

                // ③ 单屏 → 双屏：按用户要求放主屏，位置不因为多了一块屏而乱跑。
                object oneOnly = makeList(new[] { laptopScreen });
                object nowTwo = makeList(new[] { laptopScreen, rightScreen });
                object toPrimary = callDecide(oneOnly, 100, 80, 1200, 800, 0, 1, 0);
                Assert(toPrimary != null, "单屏→双屏应当给出落点。" );
                AssertEqual(100, readInt(toPrimary, "Left"));
                AssertEqual(80, readInt(toPrimary, "Top"));

                // ④ 屏数没变、保存位置仍然落在某块屏里：**原地不动**，一个像素都不改。
                object leftSideScreen = makeMonitor(-1920, 0, 0, 1080, false);
                object twoAfterSwap = makeList(new[] { laptopScreen, leftSideScreen });
                object stillInside = callDecide(twoAfterSwap, 100, 80, 1200, 800, 1, 2, 0);
                Assert(!readBool(stillInside, "TopologyChanged"), "屏数没变不应标记拓扑变化。" );
                AssertEqual(100, readInt(stillInside, "Left"));
                AssertEqual(80, readInt(stillInside, "Top"));

                // ④b 屏数没变但保存的位置已经不在任何一块屏里（排列换了）：按屏序号对应，保证可见。
                object swapped = callDecide(twoAfterSwap, 2100, 100, 1200, 800, 1, 2, 0);
                Assert(!readBool(swapped, "TopologyChanged"), "屏数没变不应标记拓扑变化。" );
                int swappedLeft = readInt(swapped, "Left");
                int swappedWidth = readInt(swapped, "Width");
                Assert(swappedLeft >= -16 && swappedLeft + swappedWidth <= 1920 + 16,
                    "原位置没了以后，窗口必须被放到一块真实存在的屏上。" );

                // ⑤ 复制模式：两块屏坐标完全一样 → 只算一块屏，不会把窗口“算到第二块”去。
                object mirroredScreen = makeMonitor(0, 0, 1920, 1040, false);
                object duplicate = makeList(new[] { laptopScreen, mirroredScreen });
                object normalized = normalize.Invoke(null, new object[] { duplicate });
                int normalizedCount = (int)normalized.GetType().GetProperty("Count").GetValue(normalized, null);
                AssertEqual(1, normalizedCount);
                // 复制模式下，保存时其实只有“一块屏”（同样的坐标只算一块），恢复时也应当原地不动。
                object inDuplicate = callDecide(duplicate, 100, 80, 1200, 800, 0, 1, 0);
                Assert(inDuplicate != null, "复制模式也应当给出落点。" );
                AssertEqual(100, readInt(inDuplicate, "Left"));
                AssertEqual(80, readInt(inDuplicate, "Top"));
                Assert(!readBool(inDuplicate, "TopologyChanged"), "复制模式（同坐标）不算拓扑变化。" );

                // ⑥ 大屏 → 小屏（分辨率变了）：缩小并贴回可见区（WindowScreenFit 的规则）。
                object smallScreen = makeMonitor(0, 0, 1366, 728, true);
                object shrunk = callDecide(makeList(new[] { smallScreen }), 3000, 200, 1200, 800, 0, 1, 0);
                Assert(readInt(shrunk, "Left") >= -16 && readInt(shrunk, "Left") + readInt(shrunk, "Width") <= 1366 + 16,
                    "大屏→小屏后窗口必须横向可见。" );
                Assert(readInt(shrunk, "Top") >= 0, "大屏→小屏后标题栏必须可见。" );
            });

            Run("窗口恢复映射最小化/最大化/普通显示命令", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo method = serviceType.GetMethod(
                    "ResolveShowCommand",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(method != null, "缺少窗口显示命令映射逻辑。" );

                AssertEqual(3, (int)method.Invoke(null, new object[] { "Maximized" }));
                AssertEqual(2, (int)method.Invoke(null, new object[] { "Minimized" }));
                AssertEqual(1, (int)method.Invoke(null, new object[] { "Normal" }));
                AssertEqual(1, (int)method.Invoke(null, new object[] { null }));
                AssertEqual(1, (int)method.Invoke(null, new object[] { string.Empty }));
            });

            Run("文件夹保存为最小化时保留最小化", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo method = serviceType.GetMethod(
                    "IsMinimizedFolder",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(method != null, "缺少文件夹最小化恢复判断。" );

                string folder = Path.Combine(Path.GetTempPath(),
                    "csk-folder-minimize-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    SavedWindowPlacement minimized = new SavedWindowPlacement
                    {
                        HasBounds = true,
                        WindowState = "Minimized"
                    };
                    Assert((bool)method.Invoke(null, new object[] { minimized, folder }),
                        "有坐标的文件夹保存为最小化时没有进入延迟最小化路径。" );

                    minimized.WindowState = "Normal";
                    Assert(!(bool)method.Invoke(null, new object[] { minimized, folder }),
                        "正常文件夹被误判成最小化恢复。" );

                    minimized.WindowState = "Minimized";
                    string file = Path.Combine(folder, "note.txt");
                    File.WriteAllText(file, "x");
                    Assert(!(bool)method.Invoke(null, new object[] { minimized, file }),
                        "普通文件被误判成文件夹最小化恢复。" );
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
            });

            Run("VS Code 工作区身份加载前不使用弱窗口匹配", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo method = serviceType.GetMethod(
                    "ShouldAllowWeakWindowMatch",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(method != null, "缺少双窗口弱匹配延迟逻辑。" );
                bool earlyWorkspaceFallback = (bool)method.Invoke(
                    null,
                    new object[] { "C:\\Work\\Alpha", 0 });
                bool eventualWorkspaceFallback = (bool)method.Invoke(
                    null,
                    new object[] { "C:\\Work\\Alpha", 149 });
                bool ordinaryWindowFallback = (bool)method.Invoke(
                    null,
                    new object[] { null, 0 });
                Assert(!earlyWorkspaceFallback, "工作区标题尚未加载时不能立即认领任意 Code 窗口。" );
                Assert(eventualWorkspaceFallback, "强身份始终不可用时仍需有限兜底。" );
                Assert(ordinaryWindowFallback, "没有路径身份的普通窗口应继续使用原有匹配方式。" );
            });

            Run("浏览器窗口只认标签标题，不会把还原页面当成目标窗口", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo method = serviceType.GetMethod(
                    "BrowserWindowTitleMatches",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(method != null, "缺少浏览器窗口标题匹配逻辑。" );

                List<string> savedTitles = new List<string>
                {
                    "软件安装管家 - 内存使用率 - 229 MB",
                    "DeepSeek 开放平台 - 内存使用率 - 110 MB"
                };
                bool realBrowserWindow = (bool)method.Invoke(
                    null,
                    new object[]
                    {
                        "软件安装管家 和另外 7 个页面 - 用户配置 1 - Microsoft Edge",
                        savedTitles
                    });
                Assert(realBrowserWindow, "完整 Edge 窗口标题没有按标签标题认领。" );

                bool restoreBubble = (bool)method.Invoke(
                    null,
                    new object[] { "还原页面", savedTitles });
                Assert(!restoreBubble, "Edge 的“还原页面”辅助窗口不能被当成目标浏览器窗口。" );

                bool activeOtherTab = (bool)method.Invoke(
                    null,
                    new object[]
                    {
                        "DeepSeek 开放平台 和另外 7 个页面 - 用户配置 1 - Microsoft Edge",
                        savedTitles
                    });
                Assert(activeOtherTab, "窗口当前激活的是同组另一个标签时也应认领。" );
            });

            Run("最小化浏览器恢复先按最小化前状态显示，不在第一页缩", delegate
            {
                Type launcherType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher",
                    true);
                MethodInfo createDisplay = launcherType.GetMethod(
                    "CreateDisplayPlacement",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(createDisplay != null, "缺少浏览器“最小化前状态”显示逻辑。" );

                SavedWindowPlacement finalPlacement = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 120,
                    Top = 80,
                    Width = 900,
                    Height = 700,
                    WindowState = "Minimized",
                    WindowStateBeforeMinimize = "Maximized"
                };
                SavedWindowPlacement display = (SavedWindowPlacement)createDisplay.Invoke(
                    null,
                    new object[] { finalPlacement });
                Assert(display != null, "最小化浏览器没有生成显示用的窗口状态。" );
                AssertEqual("Maximized", display.WindowState);
                AssertEqual(120, display.Left);
                AssertEqual(80, display.Top);
                AssertEqual(900, display.Width);
                AssertEqual(700, display.Height);
                AssertEqual("Minimized", finalPlacement.WindowState);

                SavedWindowPlacement noBefore = new SavedWindowPlacement
                {
                    HasBounds = true,
                    WindowState = "Minimized"
                };
                SavedWindowPlacement normalDisplay = (SavedWindowPlacement)createDisplay.Invoke(
                    null,
                    new object[] { noBefore });
                AssertEqual("Normal", normalDisplay.WindowState);
            });

            Run("逐页恢复也登记整窗标题，首屏才能认领正确浏览器窗口", delegate
            {
                Type launcherType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher",
                    true);
                Type interfaceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.IBrowserWindowBatchLauncher",
                    true);
                MethodInfo prepare = null;
                foreach (MethodInfo candidate in interfaceType.GetMethods())
                {
                    if (candidate.Name == "PrepareBrowserWindowTitles")
                    {
                        prepare = candidate;
                    }
                }
                Assert(prepare != null, "缺少浏览器窗口标题登记入口。" );

                object launcher = Activator.CreateInstance(launcherType);
                SessionItem firstPage = new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    BrowserExecutablePath = "C:\\Edge\\msedge.exe",
                    BrowserWindowIndex = 3,
                    WindowTitle = "第一页",
                    DisplayName = "第一页"
                };
                List<SessionItem> pages = new List<SessionItem>
                {
                    firstPage,
                    new SessionItem
                    {
                        Kind = SessionItemKind.WebPage,
                        BrowserExecutablePath = "C:\\Edge\\msedge.exe",
                        BrowserWindowIndex = 3,
                        WindowTitle = "第二页 - 内存使用率 - 10 MB",
                        DisplayName = "第二页"
                    }
                };
                prepare.Invoke(launcher, new object[] { firstPage, pages });

                FieldInfo titlesField = launcherType.GetField(
                    "pendingBrowserWindowTitles",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert(titlesField != null, "找不到浏览器窗口标题缓存。" );
                System.Collections.IDictionary titles =
                    titlesField.GetValue(launcher) as System.Collections.IDictionary;
                string key = "C:\\Edge\\msedge.exe|window|3";
                Assert(titles != null && titles.Contains(key),
                    "逐页恢复没有登记这个浏览器窗口的标题。" );
                System.Collections.IList stored = titles[key] as System.Collections.IList;
                Assert(stored != null && stored.Count == 3,
                    "整窗标题没有全部登记（WindowTitle + DisplayName 都要收）。" );
                Assert(stored.Contains("第一页")
                    && stored.Contains("第二页 - 内存使用率 - 10 MB")
                    && stored.Contains("第二页"),
                    "整窗标题列表不完整。" );

                FieldInfo countsField = launcherType.GetField(
                    "pendingBrowserPageCounts",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert(countsField != null, "找不到浏览器窗口页数缓存。" );
                System.Collections.IDictionary counts =
                    countsField.GetValue(launcher) as System.Collections.IDictionary;
                Assert(counts != null && counts.Contains(key) && (int)counts[key] == 2,
                    "逐页恢复没有登记这个浏览器窗口的保存页数。" );
                FieldInfo lastTitlesField = launcherType.GetField(
                    "pendingBrowserLastPageTitles",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert(lastTitlesField != null, "找不到最后一页标题缓存。" );
                System.Collections.IDictionary lastTitles =
                    lastTitlesField.GetValue(launcher) as System.Collections.IDictionary;
                Assert(lastTitles != null && lastTitles.Contains(key)
                    && string.Equals((string)lastTitles[key], "第二页 - 内存使用率 - 10 MB", StringComparison.Ordinal),
                    "最后一页标题登记不正确。" );
            });

            Run("浏览器窗口页数/当前标签解析（最后一页与页数是最终校验依据）", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo countMethod = serviceType.GetMethod(
                    "TryReadBrowserPageCount",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo activeMethod = serviceType.GetMethod(
                    "GetBrowserActiveTitle",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo blankMethod = serviceType.GetMethod(
                    "IsBlankBrowserWindowTitle",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(countMethod != null, "缺少浏览器窗口页数解析。" );
                Assert(activeMethod != null, "缺少当前标签标题解析。" );
                Assert(blankMethod != null, "缺少空白/初始化浏览器窗口识别。" );

                Func<string, object[]> countArgs = delegate(string title)
                {
                    return new object[] { title, 0 };
                };
                object[] many = countArgs("CSK Acceptance W1 T8 和另外 7 个页面 - 用户配置 1 - Microsoft​ Edge");
                Assert((bool)countMethod.Invoke(null, many), "多标签 Edge 标题没有解析出页数。" );
                AssertEqual(8, (int)many[1]);

                object[] single = countArgs("CSK Acceptance W1 T1 - 用户配置 1 - Microsoft​ Edge");
                Assert((bool)countMethod.Invoke(null, single), "单标签 Edge 标题没有解析出 1 页。" );
                AssertEqual(1, (int)single[1]);

                object[] phrase = countArgs("A 和另外 B 和另外 2 个页面 - 用户配置 1 - Microsoft​ Edge");
                Assert((bool)countMethod.Invoke(null, phrase), "网页标题本身含“和另外”时没有取最后的后缀。" );
                AssertEqual(3, (int)phrase[1]);

                object[] invalid = countArgs("A 和另外 没有数字 - 用户配置 1 - Microsoft​ Edge");
                Assert(!(bool)countMethod.Invoke(null, invalid), "页数后缀没有数字时不应瞎猜。" );

                object[] english = countArgs("CSK Acceptance and 7 more pages - Profile 1 - Microsoft​ Edge");
                Assert((bool)countMethod.Invoke(null, english), "英文 Edge 的 and N more pages 没有解析。" );
                AssertEqual(8, (int)english[1]);
                object[] englishTabs = countArgs("CSK Acceptance and 2 more tabs - Profile 1 - Google Chrome");
                Assert((bool)countMethod.Invoke(null, englishTabs), "英文 Chrome 的 and N more tabs 没有解析。" );
                AssertEqual(3, (int)englishTabs[1]);
                object[] firefox = countArgs("CSK Acceptance - Mozilla Firefox");
                Assert(!(bool)countMethod.Invoke(null, firefox),
                    "Firefox 多标签标题没有页数标记，必须返回未知，不能瞎猜成 1。" );

                AssertEqual(
                    "软件安装管家软件资源电脑软件下载设计软件下载教程目录（9月更新）",
                    (string)activeMethod.Invoke(
                        null,
                        new object[] { "软件安装管家软件资源电脑软件下载设计软件下载教程目录（9月更新） 和另外 7 个页面 - 用户配置 1 - Microsoft​ Edge" }));
                Assert((bool)blankMethod.Invoke(null, new object[] { "新建标签页 - 用户配置 1 - Microsoft​ Edge" }),
                    "新建标签页应被当成空白初始化窗口。" );
                Assert(!(bool)blankMethod.Invoke(null, new object[] { "CSK Acceptance W1 T1 - 用户配置 1 - Microsoft​ Edge" }),
                    "正常网页标题不应被当成空白窗口。" );
                Assert(!(bool)blankMethod.Invoke(null, new object[] { "无标题 和另外 6 个页面 - 用户配置 1 - Microsoft​ Edge" }),
                    "有 7 个标签但当前标签暂时无标题时，不能把整窗当空白初始化窗口。" );
            });

            Run("浏览器最小化遇到无效窗口/空白窗口时不误报成功", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo matchesMethod = serviceType.GetMethod(
                    "BrowserWindowMatchesExpectation",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo applyMethod = serviceType.GetMethod(
                    "ApplyMinimizedToBrowserWindow",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(matchesMethod != null, "缺少浏览器窗口最终校验。" );
                Assert(applyMethod != null, "缺少浏览器最小化结果。" );

                List<string> titles = new List<string> { "第一页" };
                object[] matchArgs = new object[] { IntPtr.Zero, titles, "第一页", 1, null, 0 };
                Assert(!(bool)matchesMethod.Invoke(null, matchArgs),
                    "无效 HWND 不应通过浏览器窗口最终校验。" );

                SavedWindowPlacement placement = new SavedWindowPlacement
                {
                    HasBounds = true,
                    Left = 10,
                    Top = 20,
                    Width = 800,
                    Height = 600,
                    WindowState = "Minimized"
                };
                object outcome = applyMethod.Invoke(
                    null,
                    new object[] { IntPtr.Zero, placement, titles, "第一页", 1, null });
                Assert(outcome != null && string.Equals(outcome.ToString(), "WindowLost", StringComparison.Ordinal),
                    "无效窗口最小化必须返回 WindowLost，不能当成成功。" );

                MethodInfo existingMethod = serviceType.GetMethod(
                    "FindExistingBrowserWindowWithExpectedPages",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(existingMethod != null, "缺少已有浏览器窗口复核。" );
                object[] existingArgs = new object[]
                {
                    "C:\\Edge\\msedge.exe",
                    titles,
                    "第一页",
                    1,
                    new HashSet<IntPtr>(),
                    null,
                    0
                };
                Assert((IntPtr)existingMethod.Invoke(null, existingArgs) == IntPtr.Zero,
                    "没有恢复前已有窗口时不能误报“页面已在旧窗口”。" );
            });

            Run("B1 收尾门禁：空闲时立即完成，完成后关闭会话", delegate
            {
                Type launcherType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher",
                    true);
                Type interfaceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.IBrowserWindowBatchLauncher",
                    true);
                Type progressStateType = productAssembly.GetType(
                    "WorkspaceRestorer.Models.RestoreProgressState",
                    true);
                MethodInfo beginMethod = interfaceType.GetMethod("BeginBrowserRestore");
                MethodInfo waitMethod = interfaceType.GetMethod("WaitForBrowserRestoreSettledAsync");
                MethodInfo endMethod = interfaceType.GetMethod("EndBrowserRestore");
                Assert(beginMethod != null && waitMethod != null && endMethod != null,
                    "缺少恢复收尾门禁接口（Begin/Wait/End）。" );
                Assert(Enum.IsDefined(progressStateType, "Finalizing"),
                    "恢复进度缺少“收尾中”状态。" );

                object launcher = Activator.CreateInstance(launcherType);
                beginMethod.Invoke(launcher, null);
                Task<bool> idleTask = (Task<bool>)waitMethod.Invoke(
                    launcher,
                    new object[] { CancellationToken.None });
                Assert(idleTask.Wait(2000), "没有缩窗任务时，收尾门禁不应卡住。" );
                Assert(idleTask.Result, "没有缩窗任务时，收尾门禁应返回 true。" );
                endMethod.Invoke(launcher, null);

                // End 之后再次 Begin 必须从干净状态开始（上一轮的 worker/计数器不能带过来）。
                beginMethod.Invoke(launcher, null);
                Task<bool> secondIdle = (Task<bool>)waitMethod.Invoke(
                    launcher,
                    new object[] { CancellationToken.None });
                Assert(secondIdle.Wait(2000) && secondIdle.Result,
                    "第二次恢复的收尾门禁没有从干净状态开始。" );
                endMethod.Invoke(launcher, null);
            });

            Run("B2 用户关闭只跳过非浏览器项，浏览器保持现有逻辑", delegate
            {
                Type launcherType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher",
                    true);
                MethodInfo shouldTrack = launcherType.GetMethod(
                    "ShouldTrackRestoreStartItem",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(shouldTrack != null, "缺少 B2 的“是否登记”判定。" );

                Func<SessionItem, bool> track = delegate(SessionItem item)
                {
                    return (bool)shouldTrack.Invoke(null, new object[] { item });
                };

                Assert(track(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    ExecutablePath = "C:\\Tools\\QQ.exe",
                    DisplayName = "QQ"
                }), "普通应用应参与 B2 提前关闭跳过。" );
                Assert(track(new SessionItem
                {
                    Kind = SessionItemKind.Document,
                    TargetPath = "C:\\Docs\\a.txt",
                    DisplayName = "a.txt"
                }), "文档应参与 B2 提前关闭跳过。" );
                Assert(track(new SessionItem
                {
                    Kind = SessionItemKind.ExplorerFolder,
                    TargetPath = "C:\\Work",
                    DisplayName = "Work"
                }), "文件夹应参与 B2 提前关闭跳过。" );
                Assert(!track(new SessionItem
                {
                    Kind = SessionItemKind.WebPage,
                    BrowserExecutablePath = "C:\\Edge\\msedge.exe",
                    BrowserWindowIndex = 0,
                    BrowserTabIndex = 1,
                    Url = "https://example.com"
                }), "网页不能参与 B2：关一个网页不能跳过整组网页。" );
                Assert(!track(new SessionItem
                {
                    Kind = SessionItemKind.Application,
                    BrowserGroupKey = "C:\\Edge\\msedge.exe",
                    BrowserWindowIndex = 0,
                    DisplayName = "Microsoft Edge"
                }), "浏览器父窗口不能参与 B2：浏览器恢复逻辑保持原样。" );

                MethodInfo userClosedFactory = launcherType.GetMethod(
                    "UserClosed",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(userClosedFactory != null, "缺少“用户关闭跳过”的结果工厂。" );
                LaunchResult closed = (LaunchResult)userClosedFactory.Invoke(null, null);
                Assert(closed.Success, "用户关闭跳过不应算失败。" );
                Assert(closed.WasUserClosed, "结果里没有标记用户关闭跳过。" );
                Assert(!closed.WasAlreadyOpen, "用户关闭跳过不能混成“本来就开着”。" );
            });

            Run("B2 完成模型单独统计用户关闭跳过", delegate
            {
                RestoreCompletion completion = new RestoreCompletion();
                AssertEqual(0, completion.UserClosedCount);
                completion.UserClosedCount = 2;
                AssertEqual(2, completion.UserClosedCount);
                AssertEqual(0, completion.BrowserWindowClosedCount);
                completion.BrowserWindowClosedCount = 3;
                AssertEqual(3, completion.BrowserWindowClosedCount);
                RestoreItemResult item = new RestoreItemResult();
                Assert(!item.WasUserClosed, "新恢复结果不应默认标记用户关闭。" );
                item.WasUserClosed = true;
                Assert(item.WasUserClosed, "恢复结果没有保留用户关闭标记。" );
                Assert(!item.WasBrowserWindowClosed,
                    "新恢复结果不应默认标记浏览器窗口关闭。" );
                item.WasBrowserWindowClosed = true;
                Assert(item.WasBrowserWindowClosed,
                    "恢复结果没有保留浏览器窗口关闭标记。" );
            });

            Run("B3 浏览器窗口改为逐页启动，不再把整窗网页一次性交给浏览器", delegate
            {
                BrowserWindowCloseTestLauncher launcher =
                    new BrowserWindowCloseTestLauncher();
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(CreateBrowserPage("A1", "edge-a", 0, 0, 1));
                snapshot.Items.Add(CreateBrowserPage("A2", "edge-a", 0, 1, 2));

                RestoreResult result = new SessionRestoreService(
                    launcher, new FixedLoadMonitor()).RestoreAsync(
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

                AssertEqual(2, launcher.Items.Count);
                AssertEqual(0, launcher.BatchPreparationCount);
                AssertEqual(1, launcher.TitlePreparationCount);
                AssertEqual(2, result.Items.Count);
                Assert(result.Items[0].Success && result.Items[1].Success,
                    "逐页恢复的两页都应成功。" );
                Assert(!result.Items[0].WasBrowserWindowClosed
                    && !result.Items[1].WasBrowserWindowClosed,
                    "没有关窗时不能误标记浏览器窗口关闭。" );
            });

            Run("B3 关闭浏览器窗口只跳过同组剩余网页，其他窗口继续", delegate
            {
                BrowserWindowCloseTestLauncher launcher =
                    new BrowserWindowCloseTestLauncher();
                launcher.CloseAfterFirstPageGroup = "edge-a";
                SessionSnapshot snapshot = new SessionSnapshot();
                // 两个窗口的网页交错保存，模拟真实恢复顺序：关掉 A 不能影响 B。
                snapshot.Items.Add(CreateBrowserPage("A1", "edge-a", 0, 0, 1));
                snapshot.Items.Add(CreateBrowserPage("B1", "edge-b", 1, 0, 2));
                snapshot.Items.Add(CreateBrowserPage("A2", "edge-a", 0, 1, 3));
                snapshot.Items.Add(CreateBrowserPage("B2", "edge-b", 1, 1, 4));

                RestoreResult result = new SessionRestoreService(
                    launcher, new FixedLoadMonitor()).RestoreAsync(
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

                AssertEqual(0, launcher.BatchPreparationCount);
                AssertEqual(2, launcher.TitlePreparationCount);
                AssertEqual(3, launcher.Items.Count);
                AssertEqual("A1", launcher.Items[0].DisplayName);
                AssertEqual("B1", launcher.Items[1].DisplayName);
                AssertEqual("B2", launcher.Items[2].DisplayName);
                Assert(!launcher.Items.Exists(delegate(SessionItem item)
                {
                    return item != null && item.DisplayName == "A2";
                }), "A 窗口关闭后不能再启动 A2。" );

                AssertEqual(4, result.Items.Count);
                Assert(result.Items[2].WasBrowserWindowClosed,
                    "A2 应标记为浏览器窗口关闭跳过。" );
                Assert(result.Items[2].Success,
                    "浏览器窗口关闭跳过不应算失败。" );
                Assert(!result.Items[2].WasUserClosed,
                    "浏览器网页关闭不能混成 B2 的非浏览器用户关闭。" );
                Assert(!result.Items[1].WasBrowserWindowClosed
                    && !result.Items[3].WasBrowserWindowClosed,
                    "B 窗口不应受 A 窗口关闭影响。" );

                AssertEqual(2, launcher.CompletedGroups.Count);
                Assert(launcher.CompletedGroups.Contains("edge-a|0"),
                    "A 窗口关闭后也要走收尾，但收尾必须知道窗口已关闭。" );
                Assert(launcher.CompletedGroups.Contains("edge-b|1"),
                    "B 窗口应正常收尾。" );
            });

            Run("B3 关窗判定只认精确 HWND，连续两次失效才生效", delegate
            {
                IntPtr firstWindow = CreateWindowEx(
                    0, "STATIC", "CSK B3 close guard", 0,
                    0, 0, 80, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr secondWindow = CreateWindowEx(
                    0, "STATIC", "CSK B3 other window", 0,
                    0, 0, 80, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert(firstWindow != IntPtr.Zero && secondWindow != IntPtr.Zero,
                    "无法创建隔离测试窗口，B3 关窗判定测试未执行。" );
                try
                {
                    BrowserWindowCloseGuard guard =
                        new BrowserWindowCloseGuard("edge-a|0");
                    Assert(guard.Attach(firstWindow), "有效精确窗口应能认领。" );
                    Assert(!guard.Attach(secondWindow),
                        "已认领窗口后不能改认另一个窗口。" );
                    Assert(!guard.ConfirmClosedByUser(0),
                        "窗口还开着时不能误判成用户关闭。" );
                    Assert(!guard.IsClosedByUser,
                        "窗口还开着时不能标记为已关闭。" );

                    Assert(DestroyWindow(firstWindow), "销毁隔离测试窗口失败。" );
                    firstWindow = IntPtr.Zero;
                    Assert(!guard.ObserveOnce(),
                        "窗口刚失效一次时不能立刻下结论。" );
                    Assert(guard.ObserveOnce(),
                        "连续两次失效后才应确认用户关闭。" );
                    Assert(guard.IsClosedByUser,
                        "关窗状态没有保存下来。" );
                    Assert(!guard.IsAliveNow(),
                        "已关闭窗口不能再被当成可控制窗口。" );
                }
                finally
                {
                    if (firstWindow != IntPtr.Zero)
                    {
                        DestroyWindow(firstWindow);
                    }
                    if (secondWindow != IntPtr.Zero)
                    {
                        DestroyWindow(secondWindow);
                    }
                }
            });

            Run("B3 浏览器自己替换 HWND 时接替新窗口，不误判成用户关窗", delegate
            {
                IntPtr oldWindow = CreateWindowEx(
                    0, "STATIC", "CSK B3 old window", 0,
                    0, 0, 80, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                IntPtr replacement = CreateWindowEx(
                    0, "STATIC", "CSK B3 replacement window", 0,
                    0, 0, 80, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert(oldWindow != IntPtr.Zero && replacement != IntPtr.Zero,
                    "无法创建隔离测试窗口，B3 替换窗口测试未执行。" );
                try
                {
                    BrowserWindowCloseGuard guard =
                        new BrowserWindowCloseGuard("edge-a|0");
                    Assert(guard.Attach(oldWindow), "有效精确窗口应能认领。" );
                    Assert(DestroyWindow(oldWindow), "销毁旧测试窗口失败。" );
                    oldWindow = IntPtr.Zero;

                    // 模拟 Edge 用新 HWND 替换引导窗口：同一进程、旧句柄已失效。
                    Assert(guard.TryAdoptReplacement(replacement),
                        "旧窗口失效后，同进程的新窗口应能被接替。" );
                    Assert(guard.IsAliveNow(),
                        "接替后的新窗口应继续被视为可控制窗口。" );
                    Assert(!guard.IsClosedByUser,
                        "浏览器自己替换窗口不能误判成用户关窗。" );

                    Assert(DestroyWindow(replacement),
                        "销毁替换测试窗口失败。" );
                    replacement = IntPtr.Zero;
                    Assert(guard.ConfirmClosedByUser(0),
                        "接替后的新窗口也关闭后，应确认用户关窗。" );
                }
                finally
                {
                    if (oldWindow != IntPtr.Zero)
                    {
                        DestroyWindow(oldWindow);
                    }
                    if (replacement != IntPtr.Zero)
                    {
                        DestroyWindow(replacement);
                    }
                }
            });

            Run("B3 ProcessLauncher 只跳过被关闭的那个浏览器窗口组", delegate
            {
                IntPtr window = CreateWindowEx(
                    0, "STATIC", "CSK B3 launcher group", 0,
                    0, 0, 80, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Assert(window != IntPtr.Zero,
                    "无法创建隔离测试窗口，B3 窗口组隔离测试未执行。" );
                ProcessLauncher launcher = new ProcessLauncher();
                IBrowserWindowBatchLauncher batch = launcher;
                try
                {
                    batch.BeginBrowserRestore();
                    SessionItem pageA = CreateBrowserPage("A1", "edge-a", 0, 0, 1);
                    SessionItem pageB = CreateBrowserPage("B1", "edge-b", 1, 0, 2);
                    batch.PrepareBrowserWindowTitles(
                        pageA, new List<SessionItem> { pageA });

                    Type contextType = typeof(ProcessLauncher).GetNestedType(
                        "BrowserWindowRestoreContext",
                        BindingFlags.NonPublic);
                    Assert(contextType != null,
                        "找不到浏览器恢复上下文类型，B3 窗口组隔离测试未执行。" );
                    object context = Activator.CreateInstance(contextType, true);
                    BrowserWindowCloseGuard guard =
                        new BrowserWindowCloseGuard("edge-a|window|0");
                    Assert(guard.Attach(window), "有效精确窗口应能认领。" );

                    FieldInfo keyField = contextType.GetField(
                        "Key", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo handleField = contextType.GetField(
                        "WindowHandle", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo generationField = contextType.GetField(
                        "Generation", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo guardField = contextType.GetField(
                        "CloseGuard", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo executableField = contextType.GetField(
                        "ExecutablePath", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo titlesField = contextType.GetField(
                        "Titles", BindingFlags.NonPublic | BindingFlags.Instance);
                    FieldInfo existingField = contextType.GetField(
                        "ExistingWindows", BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert(keyField != null && handleField != null && generationField != null
                        && guardField != null && executableField != null
                        && titlesField != null && existingField != null,
                        "浏览器恢复上下文字段不完整，B3 窗口组隔离测试未执行。" );

                    int generation = (int)typeof(ProcessLauncher).GetField(
                        "browserRestoreGeneration",
                        BindingFlags.NonPublic | BindingFlags.Instance).GetValue(launcher);
                    string key = "edge-a|window|0";
                    keyField.SetValue(context, key);
                    handleField.SetValue(context, window);
                    generationField.SetValue(context, generation);
                    guardField.SetValue(context, guard);
                    executableField.SetValue(context, null);
                    titlesField.SetValue(context, new List<string> { "A1" });
                    existingField.SetValue(context, new HashSet<IntPtr>());

                    FieldInfo restoresField = typeof(ProcessLauncher).GetField(
                        "browserWindowRestores",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    Assert(restoresField != null,
                        "找不到浏览器恢复窗口表，B3 窗口组隔离测试未执行。" );
                    System.Collections.IDictionary restores =
                        (System.Collections.IDictionary)restoresField.GetValue(launcher);
                    restores[key] = context;

                    Assert(!batch.IsBrowserWindowClosedByUser(pageA),
                        "窗口还开着时不能跳过该组网页。" );
                    Assert(DestroyWindow(window), "销毁隔离测试窗口失败。" );
                    window = IntPtr.Zero;
                    Assert(batch.IsBrowserWindowClosedByUser(pageA),
                        "A 窗口关闭后应跳过 A 组网页。" );
                    Assert(!batch.IsBrowserWindowClosedByUser(pageB),
                        "A 窗口关闭不能影响 B 组。" );
                }
                finally
                {
                    batch.EndBrowserRestore();
                    if (window != IntPtr.Zero)
                    {
                        DestroyWindow(window);
                    }
                }
            });

            Run("C 标题页数校验失败时只兜底精确同进程窗口", delegate
            {
                Type serviceType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.WindowPlacementService",
                    true);
                MethodInfo forceMethod = serviceType.GetMethod(
                    "TryForceMinimizeBrowserWindow",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(forceMethod != null, "缺少 C 的精确句柄兜底最小化。" );

                object invalid = forceMethod.Invoke(
                    null,
                    new object[] { IntPtr.Zero, "C:\\Edge\\msedge.exe", null });
                Assert(!(bool)invalid, "无效 HWND 不能兜底最小化。" );

                IntPtr console = GetConsoleWindow();
                if (console != IntPtr.Zero)
                {
                    object wrongProcess = forceMethod.Invoke(
                        null,
                        new object[] { console, "C:\\Windows\\System32\\notepad.exe", null });
                    Assert(!(bool)wrongProcess,
                        "进程对不上时不能按 HWND 兜底最小化。" );
                }
            });

            Run("A 网页跳转：先只读定位，再激活标签", delegate
            {
                Type windowType = productAssembly.GetType(
                    "WorkspaceRestorer.UI.MainWindow",
                    true);
                MethodInfo locate = windowType.GetMethod(
                    "TryLocateCandidateForJump",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo retry = windowType.GetMethod(
                    "TryLocateSavedTabWithRetry",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(locate != null, "缺少网页候选窗口定位。" );
                Assert(retry != null, "缺少不激活窗口的只读标签定位重试。" );
            });

            Run("同名网页消歧：保存 5 个标签，窗口 1/5 → 选 5 标签窗口（2026-11-06）", delegate
            {
                List<BrowserJumpWindowFacts> facts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 1
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    }
                };
                int index;
                BrowserJumpWindowChoice choice =
                    BrowserJumpWindowIdentity.Decide(facts, 5, out index);
                AssertEqual((int)BrowserJumpWindowChoice.PageCount, (int)choice);
                AssertEqual(1, index);
            });

            Run("同名网页消歧：反向保存 1 个标签，窗口 1/5 → 选 1 标签窗口", delegate
            {
                List<BrowserJumpWindowFacts> facts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 1
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    }
                };
                int index;
                BrowserJumpWindowChoice choice =
                    BrowserJumpWindowIdentity.Decide(facts, 1, out index);
                AssertEqual((int)BrowserJumpWindowChoice.PageCount, (int)choice);
                AssertEqual(0, index);
            });

            Run("同名网页消歧：父窗口标题唯一时优先父标题", delegate
            {
                List<BrowserJumpWindowFacts> facts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        ParentTitleMatches = true,
                        PageCountKnown = true,
                        PageCount = 5
                    }
                };
                int index;
                BrowserJumpWindowChoice choice =
                    BrowserJumpWindowIdentity.Decide(facts, 5, out index);
                AssertEqual((int)BrowserJumpWindowChoice.ParentTitle, (int)choice);
                AssertEqual(1, index);
            });

            Run("同名网页消歧：两个窗口标签数相同且父标题不唯一 → 交给用户选", delegate
            {
                List<BrowserJumpWindowFacts> facts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    }
                };
                int index;
                BrowserJumpWindowChoice choice =
                    BrowserJumpWindowIdentity.Decide(facts, 5, out index);
                AssertEqual((int)BrowserJumpWindowChoice.Ambiguous, (int)choice);
                AssertEqual(-1, index);
            });

            Run("同名网页消歧：只有一个窗口可能装着这页 → 保持原逻辑", delegate
            {
                List<BrowserJumpWindowFacts> facts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = false,
                        PageCountKnown = true,
                        PageCount = 1
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 5
                    }
                };
                int index;
                BrowserJumpWindowChoice choice =
                    BrowserJumpWindowIdentity.Decide(facts, 5, out index);
                AssertEqual((int)BrowserJumpWindowChoice.None, (int)choice);
                AssertEqual(-1, index);
            });

            Run("同名网页消歧：标签数差 1 个能容忍，差 2 个以上不猜", delegate
            {
                List<BrowserJumpWindowFacts> nearFacts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 4
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 8
                    }
                };
                int nearIndex;
                BrowserJumpWindowChoice nearChoice =
                    BrowserJumpWindowIdentity.Decide(nearFacts, 5, out nearIndex);
                AssertEqual((int)BrowserJumpWindowChoice.PageCount, (int)nearChoice);
                AssertEqual(0, nearIndex);

                List<BrowserJumpWindowFacts> farFacts = new List<BrowserJumpWindowFacts>
                {
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 3
                    },
                    new BrowserJumpWindowFacts
                    {
                        ContainsSavedPage = true,
                        PageCountKnown = true,
                        PageCount = 8
                    }
                };
                int farIndex;
                BrowserJumpWindowChoice farChoice =
                    BrowserJumpWindowIdentity.Decide(farFacts, 5, out farIndex);
                AssertEqual((int)BrowserJumpWindowChoice.Ambiguous, (int)farChoice);
                AssertEqual(-1, farIndex);

                AssertEqual(-1, BrowserJumpWindowIdentity.SelectUniqueByPageCount(
                    new List<int> { -1, -1 }, 5));
                AssertEqual(0, BrowserJumpWindowIdentity.SelectUniqueByPageCount(
                    new List<int> { 5, 6 }, 5));
                AssertEqual(-1, BrowserJumpWindowIdentity.SelectUniqueByPageCount(
                    new List<int> { 5, 5 }, 5));
            });

            Run("同名网页消歧：活动页标题相近但不是同一页要能区分", delegate
            {
                Assert(BrowserTitleProfile.SameActivePageTitle(
                    "DeepSeek - 探索未至之境 - 和另外 4 个页面 - 用户配置 1 - Microsoft Edge",
                    "DeepSeek - 探索未至之境 - 内存使用率 - 72.7 MB"),
                    "同一页带页数/内存后缀时必须认成同一页。" );
                Assert(!BrowserTitleProfile.SameActivePageTitle(
                    "DeepSeek 开放平台 - 和另外 4 个页面 - 用户配置 1 - Microsoft Edge",
                    "DeepSeek - 探索未至之境 - 内存使用率 - 72.7 MB"),
                    "只是名字相近的不同网页不能被当成同一页。" );
            });

            Run("浏览器缩窗决策：无窗口/用户接管/标签证据/标题兜底", delegate
            {
                AssertEqual(
                    "Skip",
                    BrowserMinimizeDecision.Decide(
                        false, false, false, false, false, false).ToString());
                AssertEqual(
                    "Skip",
                    BrowserMinimizeDecision.Decide(
                        true, false, true, true, true, true).ToString());
                AssertEqual(
                    "Minimize",
                    BrowserMinimizeDecision.Decide(
                        true, false, false, true, false, false).ToString());
                AssertEqual(
                    "Minimize",
                    BrowserMinimizeDecision.Decide(
                        true, false, false, false, true, false).ToString());
                AssertEqual(
                    "Minimize",
                    BrowserMinimizeDecision.Decide(
                        true, false, false, false, false, true).ToString());
                AssertEqual(
                    "Skip",
                    BrowserMinimizeDecision.Decide(
                        true, false, false, false, false, false).ToString());
            });

            Run("浏览器就绪门：URL 多重集合匹配忽略查询串和末尾斜杠", delegate
            {
                List<string> expected = new List<string>
                {
                    "https://a.test/x/?utm=1",
                    "https://b.test/y/"
                };
                List<string> actual = new List<string>
                {
                    "https://a.test/x",
                    "https://other.test/z",
                    "https://b.test/y"
                };
                AssertEqual(2, BrowserReadinessProbe.CountMatchingUrls(expected, actual));

                actual.RemoveAt(2);
                AssertEqual(1, BrowserReadinessProbe.CountMatchingUrls(expected, actual));

                List<string> duplicateExpected = new List<string>
                {
                    "https://a.test/x",
                    "https://a.test/x"
                };
                AssertEqual(
                    1,
                    BrowserReadinessProbe.CountMatchingUrls(
                        duplicateExpected,
                        new List<string> { "https://a.test/x" }));
                Assert(BrowserReadinessProbe.UrlMatches(
                        "chatgpt.com",
                        "https://chatgpt.com/"),
                    "无协议地址栏应能匹配保存网址。" );
                Assert(!BrowserReadinessProbe.UrlMatches(
                        "example.test",
                        "https://chatgpt.com/"),
                    "不同站点不能算地址栏就绪。" );
            });

            Run("单页浏览器首屏也要认领窗口，多页和单页走同一条路", delegate
            {
                Assert(ProcessLauncher.ShouldAttemptInitialBrowserWindowClaim(1, false),
                    "单页首屏不能跳过窗口认领。" );
                Assert(ProcessLauncher.ShouldAttemptInitialBrowserWindowClaim(2, false),
                    "多页首屏仍要认领窗口。" );
                Assert(!ProcessLauncher.ShouldAttemptInitialBrowserWindowClaim(1, true),
                    "已经认领过窗口时不要重复认领。" );
            });

            Run("恢复完成文案区分“用户最小化”和“浏览器窗口关闭”", delegate
            {
                RestoreCompletion completion = new RestoreCompletion
                {
                    TotalCount = 2,
                    SuccessCount = 2,
                    BrowserWindowClosedCount = 1,
                    BrowserWindowUserMinimizedCount = 1
                };
                string status = RestoreText.ForStatus(completion);
                Assert(status.Contains("因浏览器窗口关闭已跳过"),
                    "状态栏缺少关窗跳过文案。" );
                Assert(status.Contains("因用户已最小化浏览器窗口而跳过"),
                    "状态栏缺少用户最小化跳过文案。" );
            });

            Run("恢复服务保持顺序并支持测试抑制", delegate
            {
                RecordingLauncher launcher = new RecordingLauncher();
                FixedLoadMonitor loadMonitor = new FixedLoadMonitor();
                SessionRestoreService service = new SessionRestoreService(launcher, loadMonitor);
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(new SessionItem { Sequence = 2, DisplayName = "第二项", ExecutablePath = "C:\\missing-two.exe" });
                snapshot.Items.Add(new SessionItem { Sequence = 1, DisplayName = "第一项", ExecutablePath = "C:\\missing-one.exe" });
                RestoreOptions options = new RestoreOptions
                {
                    InitialDelay = TimeSpan.Zero,
                    ItemDelay = TimeSpan.Zero,
                    WaitForAcceptableLoad = true,
                    CpuSampleInterval = TimeSpan.Zero,
                    LoadPollInterval = TimeSpan.Zero
                };
                RestoreResult result = service.RestoreAsync(snapshot, options, null, CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(2, launcher.Items.Count);
                AssertEqual("第一项", launcher.Items[0].DisplayName);
                AssertEqual("第二项", launcher.Items[1].DisplayName);
                Assert(result.Items.Count == 2, "恢复结果没有包含两项。");
                Assert(result.Items[0].WasSuppressed, "测试模式下外部启动没有被抑制。");
            });

            Run("方案 A：窗口稳定 3 秒后检查一次，未就绪排 3 秒后后台补缩", delegate
            {
                // 用户 2026-11-06 确认：横幅仍在 3 秒弹出，不等补缩；
                // 3 秒没读到标题时 worker 立刻退出，后台再等 3 秒读一次，
                // 读到才补缩，读不到彻底放手。
                Assert(3000 == BrowserWindowNotReadyNotice.SettleMilliseconds,
                    "窗口收尾的固定 settle 应为 3 秒。" );
                AssertEqual("3", BrowserWindowNotReadyNotice.SettleSecondsText);
                Assert(3000 == BrowserWindowNotReadyNotice.LateRetryMilliseconds,
                    "后台补缩应再等 3 秒。" );
                AssertEqual("3", BrowserWindowNotReadyNotice.LateRetrySecondsText);

                Type launcherType = productAssembly.GetType(
                    "WorkspaceRestorer.Services.ProcessLauncher",
                    true);
                FieldInfo settleTimeoutField = launcherType.GetField(
                    "BrowserRestoreSettleTimeoutMilliseconds",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert(settleTimeoutField != null, "缺少浏览器收尾硬上限常量。" );
                Assert(10000 == (int)settleTimeoutField.GetRawConstantValue(),
                    "窗口收尾现在只需几秒，硬上限不该再是 32 秒。" );

                // 保存时是最小化 + 窗口还在 + 用户没缩没关 → 要提示。
                Assert(ProcessLauncher.ShouldReportBrowserWindowNotReady(true, true, false, false),
                    "保存为最小化的窗口没读到标题时，应计入“页面未就绪”。" );
                // 保存为普通/最大化：本来就不缩，不该提示。
                Assert(!ProcessLauncher.ShouldReportBrowserWindowNotReady(false, true, false, false),
                    "保存为普通/最大化的窗口不能提示“页面未就绪”。" );
                // 用户自己最小化：走已有的“用户接管”提示，不能混成“未就绪”。
                Assert(!ProcessLauncher.ShouldReportBrowserWindowNotReady(true, true, true, false),
                    "用户已手动最小化时不能再提示“页面未就绪”。" );
                // 用户关窗：走已有的“浏览器窗口关闭”提示。
                Assert(!ProcessLauncher.ShouldReportBrowserWindowNotReady(true, true, false, true),
                    "用户已关窗时不能再提示“页面未就绪”。" );
                // 窗口已经没了：谈不上“保持原样”。
                Assert(!ProcessLauncher.ShouldReportBrowserWindowNotReady(true, false, false, false),
                    "窗口不在时不能提示“窗口保持原样”。" );

                // 后台补缩只在“保存最小化 + 窗口还在 + 没读到 + 用户没关没缩”时排。
                Assert(ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        true, true, false, false, false),
                    "3 秒没读到标题、用户没接管时应排后台补缩。" );
                Assert(!ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        false, true, false, false, false),
                    "保存为普通/最大化的窗口不该排后台补缩。" );
                Assert(!ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        true, false, false, false, false),
                    "窗口已经不在时不该排后台补缩。" );
                Assert(!ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        true, true, true, false, false),
                    "已经读到标题时不该再排后台补缩。" );
                Assert(!ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        true, true, false, true, false),
                    "用户已关窗时不该再排后台补缩。" );
                Assert(!ProcessLauncher.ShouldScheduleLateBrowserMinimize(
                        true, true, false, false, true),
                    "用户已手动最小化时不该再排后台补缩。" );
            });

            Run("恢复服务把“页面未就绪”窗口数回填到结果", delegate
            {
                BrowserWindowCloseTestLauncher launcher =
                    new BrowserWindowCloseTestLauncher();
                launcher.NotReadyCount = 1;
                SessionSnapshot snapshot = new SessionSnapshot();
                snapshot.Items.Add(CreateBrowserPage("GPT", "edge-gpt", 0, 0, 1));

                RestoreResult result = new SessionRestoreService(
                    launcher, new FixedLoadMonitor()).RestoreAsync(
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

                AssertEqual(1, result.BrowserWindowNotReadyCount);

                // 没有未就绪窗口时不能凭空多报一个。
                BrowserWindowCloseTestLauncher idle = new BrowserWindowCloseTestLauncher();
                SessionSnapshot single = new SessionSnapshot();
                single.Items.Add(CreateBrowserPage("GPT", "edge-gpt", 0, 0, 1));
                RestoreResult idleResult = new SessionRestoreService(
                    idle, new FixedLoadMonitor()).RestoreAsync(
                        single,
                        new RestoreOptions
                        {
                            InitialDelay = TimeSpan.Zero,
                            ItemDelay = TimeSpan.Zero,
                            WebPageDelay = TimeSpan.Zero,
                            WaitForAcceptableLoad = false
                        },
                        null,
                        CancellationToken.None).GetAwaiter().GetResult();
                AssertEqual(0, idleResult.BrowserWindowNotReadyCount);
            });

            Run("完成提示写明“页面未就绪、窗口保持原样”，两个工作区共用", delegate
            {
                RestoreCompletion completion = new RestoreCompletion
                {
                    Source = RestoreSource.StartupRestore,
                    TotalCount = 1,
                    SuccessCount = 1,
                    BrowserWindowNotReadyCount = 1
                };
                RestoreNoticeText notice = RestoreText.ForBanner(completion);
                AssertEqual("Warning", notice.Kind.ToString());
                Assert(notice.Summary.Contains("页面未就绪"),
                    "横幅没有说明页面未就绪。" );
                Assert(notice.Summary.Contains("浏览器窗口保持原样"),
                    "横幅没有说明窗口保持原样。" );
                Assert(notice.Summary.Contains("3 秒"),
                    "横幅没有写清楚等了几秒。" );
                // 用户 2026-11-05 要求：只说到“已先保持正常窗口。”为止，
                // 不要“没有缩小、没有还原……”和“在任务栏把它最小化即可”。
                Assert(notice.Summary.Contains("已先保持正常窗口。"),
                    "横幅没有说明窗口已先保持正常状态。" );
                Assert(!notice.Summary.Contains("任务栏") && !notice.Summary.Contains("没有还原"),
                    "横幅不该再带“没有还原/在任务栏最小化”这类多余说明。" );

                string tray = RestoreText.ForTray(completion);
                Assert(tray.Contains("页面未就绪"),
                    "托盘气泡没有说明页面未就绪。" );
                Assert(!tray.Contains("全部恢复完成"),
                    "有页面未就绪时不能同时说“全部恢复完成”。" );

                string status = RestoreText.ForStatus(completion);
                Assert(status.Contains("页面未就绪"),
                    "状态栏没有说明页面未就绪。" );

                // 自启动应用走同一个完成模型，必须拿到同一句提示。
                RestoreCompletion applicationCompletion = new RestoreCompletion
                {
                    Source = RestoreSource.Applications,
                    TotalCount = 1,
                    SuccessCount = 1,
                    BrowserWindowNotReadyCount = 1
                };
                RestoreNoticeText applicationNotice = RestoreText.ForBanner(applicationCompletion);
                AssertEqual("Warning", applicationNotice.Kind.ToString());
                Assert(applicationNotice.Summary.Contains("页面未就绪"),
                    "自启动应用的横幅没有转接“页面未就绪”。" );
                Assert(RestoreText.ForTray(applicationCompletion).Contains("页面未就绪"),
                    "自启动应用的托盘气泡没有转接“页面未就绪”。" );
                Assert(RestoreText.ForStatus(applicationCompletion).Contains("页面未就绪"),
                    "自启动应用的状态栏没有转接“页面未就绪”。" );

                // 没有未就绪窗口时保持原来的成功语气。
                RestoreCompletion clean = new RestoreCompletion
                {
                    TotalCount = 1,
                    SuccessCount = 1
                };
                RestoreNoticeText cleanNotice = RestoreText.ForBanner(clean);
                AssertEqual("Success", cleanNotice.Kind.ToString());
                Assert(!cleanNotice.Summary.Contains("页面未就绪"),
                    "没有未就绪窗口时不能提示“页面未就绪”。" );
            });

            Run("自启动应用恢复完成事件带“页面未就绪”计数并按自启动应用路由", delegate
            {
                NotReadyRestoreService restoreService = new NotReadyRestoreService();
                restoreService.NotReadyCount = 1;
                ApplicationController controller = new ApplicationController(
                    new TestCaptureService(),
                    restoreService,
                    new TestDesktopApplicationService(),
                    null,
                    null);
                List<RestoreCompletion> completions = new List<RestoreCompletion>();
                controller.RestoreCompleted += delegate(RestoreCompletion completion)
                {
                    completions.Add(completion);
                };
                try
                {
                    SessionSnapshot snapshot = new SessionSnapshot();
                    snapshot.Items.Add(CreateBrowserPage("GPT", "edge-gpt", 0, 0, 1));
                    controller.RestoreExternalSnapshotAsync(
                        snapshot,
                        new RestoreOptions
                        {
                            InitialDelay = TimeSpan.Zero,
                            ItemDelay = TimeSpan.Zero,
                            WebPageDelay = TimeSpan.Zero,
                            WaitForAcceptableLoad = false
                        },
                        true,
                        true).GetAwaiter().GetResult();

                    AssertEqual(1, completions.Count);
                    RestoreCompletion completion = completions[0];
                    AssertEqual("Applications", completion.Source.ToString());
                    AssertEqual(1, completion.BrowserWindowNotReadyCount);
                    RestoreNoticeText notice = RestoreText.ForBanner(completion);
                    AssertEqual("Warning", notice.Kind.ToString());
                    Assert(notice.Summary.Contains("页面未就绪"),
                        "自启动应用的完成横幅没有说“页面未就绪”。" );
                }
                finally
                {
                    controller.Dispose();
                }
            });

        }

        private static SessionItem CreateBrowserPage(
            string displayName,
            string groupKey,
            int windowIndex,
            int tabIndex,
            int sequence)
        {
            return new SessionItem
            {
                Sequence = sequence,
                Kind = SessionItemKind.WebPage,
                DisplayName = displayName,
                WindowTitle = displayName,
                Url = "https://example.test/" + displayName,
                BrowserName = "Test Browser",
                BrowserExecutablePath = "C:\\missing-edge.exe",
                BrowserGroupKey = groupKey,
                BrowserWindowIndex = windowIndex,
                BrowserTabIndex = tabIndex
            };
        }

        /// <summary>
        /// B3 测试替身：不启动任何真实浏览器，只验证恢复主循环在
        /// “第一页启动后关窗”时是否跳过同组剩余网页、是否让另一组继续。
        /// </summary>
        private sealed class BrowserWindowCloseTestLauncher :
            IProcessLauncher,
            IBrowserWindowBatchLauncher
        {
            public readonly List<SessionItem> Items = new List<SessionItem>();
            public readonly List<string> CompletedGroups = new List<string>();
            private readonly HashSet<string> closedGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string CloseAfterFirstPageGroup { get; set; }
            public int BatchPreparationCount { get; private set; }
            public int TitlePreparationCount { get; private set; }

            public LaunchResult Launch(SessionItem item)
            {
                Items.Add(item);
                if (item != null
                    && item.StartsBrowserWindow
                    && !string.IsNullOrWhiteSpace(CloseAfterFirstPageGroup)
                    && string.Equals(
                        item.BrowserGroupKey,
                        CloseAfterFirstPageGroup,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 模拟用户在第一页启动后把整个浏览器窗口打叉。
                    closedGroups.Add(GetGroupKey(item));
                }
                return new LaunchResult { Success = true };
            }

            public LaunchResult LaunchPath(string path)
            {
                return new LaunchResult { Success = true };
            }

            public void BeginBrowserRestore()
            {
            }

            public void PrepareBrowserWindowBatch(
                SessionItem firstPage,
                IList<SessionItem> pages)
            {
                BatchPreparationCount++;
            }

            public void PrepareBrowserWindowTitles(
                SessionItem firstPage,
                IList<SessionItem> pages)
            {
                TitlePreparationCount++;
            }

            public bool IsBrowserWindowClosedByUser(SessionItem page)
            {
                return page != null && closedGroups.Contains(GetGroupKey(page));
            }

            public bool IsBrowserWindowUserMinimized(SessionItem page)
            {
                return false;
            }

            public void CompleteBrowserWindowRestore(
                SessionItem firstPage,
                SessionItem lastPage)
            {
                if (firstPage != null)
                {
                    CompletedGroups.Add(GetGroupKey(firstPage));
                }
            }

            public Task<bool> WaitForBrowserRestoreSettledAsync(
                CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public void EndBrowserRestore()
            {
            }

            /// <summary>取消/失败时叫停后台补缩；测试替身不需要改任何状态。</summary>
            public void AbortBrowserLateMinimize()
            {
            }

            /// <summary>模拟“这一个窗口期限内没读到标题、所以保持原样”。</summary>
            public int NotReadyCount { get; set; }

            public int BrowserWindowNotReadyCount
            {
                get { return NotReadyCount; }
            }

            private static string GetGroupKey(SessionItem item)
            {
                return (item.BrowserGroupKey ?? string.Empty)
                    + "|" + item.BrowserWindowIndex;
            }
        }

        /// <summary>
        /// 自启动应用转接测试替身：只回一个“成功但有一个单页窗口页面未就绪”的结果，
        /// 用来确认完成事件确实带着计数、并且按自启动应用那一路取文案。
        /// </summary>
        private sealed class NotReadyRestoreService : ISessionRestoreService
        {
            public int NotReadyCount { get; set; }

            public Task<RestoreResult> RestoreAsync(
                SessionSnapshot snapshot,
                RestoreOptions options,
                IProgress<RestoreProgress> progress,
                CancellationToken cancellationToken)
            {
                RestoreResult result = new RestoreResult
                {
                    StartedAtUtc = DateTime.UtcNow,
                    FinishedAtUtc = DateTime.UtcNow,
                    BrowserWindowNotReadyCount = NotReadyCount
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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            int exStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr windowHandle);
    }
}
