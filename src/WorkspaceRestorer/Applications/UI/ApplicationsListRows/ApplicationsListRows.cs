using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;

namespace WorkspaceRestorer.UI
{
    // 工作区：自启动应用 —— 列表部分（卡片 + 行 + 移动 + 删除）。
    //
    // 放在"自启动应用"自己的文件夹里：这一段 UI 与启动与恢复是**两套独立实现**
    // （按用户要求：操作相同、内容略有不同，且不修改启动与恢复的任何代码）。
    // 删除动作**直接调用**启动与恢复已有的 RemoveProfileItem（同一个 partial class），
    // 因此"删除项目 + Ctrl+Z 撤销"的行为与那边完全一致。
    //
    // 已搬过来的能力（与启动与恢复对齐）：
    //   行 = 序号 + 拖动手柄(≡) + 勾选框 + 名称 + 「来源 · 路径」+ 删除(🗑) + 打开
    //   · 移动：按住手柄(≡) 拖动，或按住 Shift 在整行上拖动；拖动时显示插入线；
    //          拖到列表上/下边缘会**自动滚动**，松手放到最前/最后
    //   · 顺序：只影响本页显示顺序（不落库），可 Ctrl+Z 撤销 / Ctrl+Shift+Z 重做
    //   · 删除：从**当前工作方案**里移除对应项目（调用启动与恢复的 RemoveProfileItem）
    public sealed partial class MainWindow
    {
        private readonly Dictionary<string, bool> applicationGroupExpansionStates =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private WrapPanel desktopRowHost;
        private ScrollViewer desktopScroll;
        /// <summary>列表标题（“桌面上的应用…（共 N 项）”）。</summary>
        /// <remarks>
        /// 这个数字**必须每次刷新都重算**：原来它只在建界面那一刻取一次
        /// （那时还没扫描，永远是 0），于是出现“标题写共 0 项、下面却列了 62 项”的怪现象。
        /// </remarks>
        private TextBlock desktopListHeader;
        /// <summary>扫描结果整块面板（“关掉扫描结果”把它收起来）。</summary>
        private Border desktopScanCard;
        /// <summary>扫描结果面板上那句“扫描结果共 N 项，已勾选 M 项…”。</summary>
        private SelectableText desktopScanSummary;
        /// <summary>「自启动应用」方案卡片的宿主面板。</summary>
        private StackPanel applicationPlanHost;
        /// <summary>方案卡片那一段的滚动视图（没有方案时整段收起）。</summary>
        private ScrollViewer desktopPlanScroll;
        /// <summary>方案的展开状态（按方案 Id 记，用户点过就按用户的来；默认收起）。</summary>
        /// 【2026-10-17 删除】这里原来有一个 `applicationPlanRestoreCancelled` 字段，
        /// 注释写着“「取消恢复」标记：恢复循环每一项都检查它”，但**全工程只在
        /// CancelApplicationPlanRestore 里给它赋过 true，从来没有人读** ——
        /// 真正生效的取消标记在 controller 里（`applicationsStartCancelled`，
        /// 由 `CancelApplicationsStart()` 立起来、恢复循环每一项检查）。
        /// 留着一个没人读的“标记”只会让人误以为取消已经接好了，按“不留僵尸代码”删掉。
        private readonly Dictionary<string, bool> applicationPlanExpansionStates =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Border desktopTopPad;
        private Border desktopBottomPad;

        /// <summary>候选格子的宽度（大图标格子，见 <see cref="BuildDesktopTile"/>）。</summary>
        private const double DesktopTileWidth = 128.0;
        /// <summary>格子里的图标显示尺寸（DIP）。</summary>
        private const double DesktopTileIconSize = 40.0;
        /// <summary>向 shell 要的图标像素尺寸：要比显示尺寸大，150% 屏上缩下来才不糊。</summary>
        private const int DesktopTileIconPixels = 96;

        /// <summary>
        /// 候选格子的字典（路径 → 格子）与“上一批候选的指纹”。
        ///
        /// 【2026-10-17 第 51 批，用户：“搜索有问题，太卡了”】
        /// 原来搜索框每敲一个字符都调 <see cref="RefreshDesktopList"/>，而它是**整批重建**：
        /// 62 个格子（每个格子 = 边框 + 图标 + 两段文字 + 勾选框 + 三个图标按钮 + 四个悬浮提示）
        /// 全部扔掉重造，一次敲键就是上千个控件，所以越打字越卡。
        /// 现在分成两步：
        ///  · 候选**没变**时一个格子都不造，只改每个格子的 `Visibility`（过滤）；
        ///  · 只有“扫描了一次新的桌面 / 候选被清空”这类真正换了数据的情况才重建（指纹比对）。
        /// </summary>
        private readonly Dictionary<string, Border> desktopTiles = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        /// <summary>候选格子的图标容器（路径 → 容器）：图标在后台取，取到后回填到这里。</summary>
        private readonly Dictionary<string, Grid> desktopIconHosts = new Dictionary<string, Grid>(StringComparer.OrdinalIgnoreCase);
        /// <summary>每次重建候选格子都 +1；后台图标任务发现版本过期就停，避免给旧格子填图。</summary>
        private int desktopIconLoadVersion;
        /// <summary>格子里的勾选框（路径 → 勾选框）：全选/取消全选、加进方案后要把界面上的勾同步过来。</summary>
        private readonly Dictionary<string, CheckBox> desktopTileChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        private string desktopTilesSignature = string.Empty;
        /// <summary>“没有匹配的项目”那一行（搜索词谁都不匹配时给个提示，别把整块面板收起来）。</summary>
        private TextBlock desktopNoMatchHint;
        /// <summary>
        /// 是否把“搜索/重建”的耗时写进程序日志（设环境变量 WORKSPACE_RESTORER_LOG_SEARCH_TIMING=1 打开）。
        /// 用户 2026-10-17 说“搜索有问题，太卡了”，探针要能用**程序自己量的毫秒数**做断言，
        /// 而不是让探针在 UIA 里一圈圈 dump 控件（那本身要一两秒，量出来的根本不是界面耗时）。
        /// </summary>
        private static readonly bool LogSearchTiming = string.Equals(
            Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_LOG_SEARCH_TIMING"),
            "1",
            StringComparison.Ordinal);
        /// <summary>最后一次过滤后可见的候选数（汇总句与标题用）。</summary>
        private int desktopVisibleCount;

    }
}
