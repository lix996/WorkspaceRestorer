using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.Services;
namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        /// <summary>Ctrl+C：复制当前选中的图标布局（不修改任何布局数据）。</summary>
        private void CopyActiveLayoutToClipboard()
        {
            DesktopTopologyLayout active = null;
            try
            {
                active = controller.GetDesktopLayouts().FirstOrDefault(delegate(DesktopTopologyLayout item)
                {
                    return item != null
                        && string.Equals(item.Id, controller.ActiveDesktopLayoutId, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 复制布局时读取清单失败", exception);
            }
            if (active == null)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.ClipboardNoSelection);
                return;
            }
            string text = DesktopLayoutClipboard.Serialize(active);
            if (string.IsNullOrWhiteSpace(text))
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.ClipboardCopyFailed);
                return;
            }
            copiedLayoutText = text;
            string clipboardError;
            bool clipboardOk = ClipboardText.TrySetText(text, out clipboardError);
            if (!clipboardOk)
            {
                AppLog.Write("[剪贴板] 复制图标布局到系统剪贴板失败：" + clipboardError);
            }
            int count = active.Icons == null ? 0 : active.Icons.Count;
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.ClipboardCopiedFormat,
                active.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                count)
                + (clipboardOk ? string.Empty : AppText.T(DesktopLayoutTextKeys.ClipboardBusySuffix));
        }

        /// <summary>Ctrl+V：把剪贴板里的布局快照粘成一份新布局（可 Ctrl+Z 撤销）。</summary>
        private void PasteLayoutFromClipboard()
        {
            string clipboardNow = null;
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    clipboardNow = System.Windows.Clipboard.GetText();
                }
            }
            catch
            {
                clipboardNow = null;
            }
            string text = string.IsNullOrWhiteSpace(clipboardNow) ? copiedLayoutText : clipboardNow;
            // 先看“出身标记”：布局、工作方案、自启动方案各自只能在所属工作区里粘贴。
            // 这里必须用布局自己的解析器：`StartupProfileClipboard` 把“启动与恢复”当自己，
            // 会把 `CSK1|图标位置保存` 误判成“别的工作区”。
            string foreign = DesktopLayoutClipboard.ForeignWorkspaceOf(text);
            if (foreign != null)
            {
                statusText.Text = AppText.F(DesktopLayoutTextKeys.ClipboardForeignFormat, foreign);
                return;
            }
            DesktopTopologyLayout source = DesktopLayoutClipboard.Deserialize(text);
            if (source == null)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.ClipboardNothingToPaste);
                return;
            }
            byte[] archiveBefore = ReadDesktopLayoutArchiveBytes();
            string baseName = ((source.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback)) + AppText.T(DesktopLayoutTextKeys.CopySuffix)).Trim();
            DesktopTopologyLayout created = controller.CreateDesktopLayoutCopy(source, baseName);
            if (created == null)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.ClipboardPasteFailed);
                return;
            }
            // 【2026-10-24】粘贴出来的布局保持默认收起：用户明确要求“复制粘贴后文件夹不要默认展开”。
            // 这里不再把它加进 expandedLayoutSignatures；只有用户点箭头才展开。
            RefreshDesktopLayoutCards();
            int count = created.Icons == null ? 0 : created.Icons.Count;
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.ClipboardPastedFormat,
                created.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                count);
            ShowBanner(
                AppText.F(
                    DesktopLayoutTextKeys.ClipboardPasteNotificationFormat,
                    created.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                    count),
                AppTheme.SuccessSurface,
                AppTheme.Positive);
            PushDesktopLayoutSaveUndo(archiveBefore, created);
            // 新布局默认在列表最末尾；下一拍滚过去，让用户看得到。
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                if (layoutCardsScroll != null)
                {
                    layoutCardsScroll.ScrollToEnd();
                }
            }));
        }

        private async void BeginLayoutEditAsync(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return;
            }
            // 同一个布局已经开着：激活它，不重复开第二份，避免两个窗口互相覆盖。
            // 如果它被最小化了，必须把原窗口还原（不能 Close 后重开，否则未保存的
            // 拖动会丢，用户也会以为布局被重置）。
            DesktopLayoutEditorWindow existing = FindOpenLayoutEditor(layout);
            if (existing != null)
            {
                bool wasMinimized = existing.WindowState == WindowState.Minimized;
                existing.RestoreAndActivate();
                statusText.Text = AppText.T(wasMinimized
                    ? DesktopLayoutTextKeys.EditorAlreadyOpenRestored
                    : DesktopLayoutTextKeys.EditorAlreadyOpenSwitched);
                return;
            }
            DesktopLayoutEditorWindow editor = null;
            try
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorOpening);
                // 编辑窗口立即用“已保存的布局”打开；capture 放到 Show 之后再启动。
                // CaptureDesktopSnapshotWithoutSavingAsync 的同步前段本身就可能卡 1~2 秒，
                // 放在 Open 前面会让窗口迟迟不出现。
                DesktopShellSnapshot editorSnapshot = BuildLayoutEditSnapshot(layout, null);
                string previewPath = DesktopLayoutFolderStore.PreviewPath(layout);
                if (!File.Exists(previewPath)) { previewPath = null; }
                if (!OpenLayoutEditor(editorSnapshot, layout, 0, 0, null, previewPath,
                        out editor))
                {
                    return;
                }
                // 让编辑窗先完成一帧渲染，再开始可能同步阻塞的桌面扫描。
                await System.Threading.Tasks.Task.Yield();
                // 多个窗口连续打开时共用同一份“当前桌面”读取结果，避免第二次
                // 撞上 controller 的 layoutCaptureInProgress 而返回 null。
                Task<DesktopShellSnapshot> captureTask = GetSharedLayoutCaptureTask();
                DesktopShellSnapshot before = await captureTask;
                if (!openLayoutEditors.Contains(editor))
                {
                    return;
                }
                if (before == null)
                {
                    sharedLayoutCaptureTask = null;
                    editor.CloseWithoutPrompt();
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorCannotReadIcons);
                    return;
                }
                string currentSignature = before.Topology == null ? string.Empty : (before.Topology.Signature ?? string.Empty);
                string savedSignature = layout.TopologySignature ?? string.Empty;
                bool displayMismatch = !string.IsNullOrWhiteSpace(savedSignature)
                    && !string.IsNullOrWhiteSpace(currentSignature)
                    && !string.Equals(savedSignature, currentSignature, StringComparison.OrdinalIgnoreCase);
                if (openLayoutEditors.Contains(editor))
                {
                    editor.ApplyCurrentDesktopSnapshot(before);
                    if (displayMismatch)
                    {
                        // 显示器被拔掉/换成笔记本时，不再关闭编辑页。
                        // 画布、网格和落点继续用保存时的尺寸，当前屏幕只负责显示。
                        editor.ShowSavedCanvasNotice(
                            AppText.T(DesktopLayoutTextKeys.EditorTopologyChangedHint));
                        statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorTopologyChangedOpened);
                    }
                }
                if (!displayMismatch)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorOpenedBackground);
                }
            }
            catch (Exception exception)
            {
                sharedLayoutCaptureTask = null;
                if (editor != null && openLayoutEditors.Contains(editor))
                {
                    // 读取/打开途中异常时不要留下一个永远带加载遮罩的编辑窗。
                    editor.CloseWithoutPrompt();
                }
                statusText.Text = AppText.F(DesktopLayoutTextKeys.EditorOpenFailedFormat, exception.Message);
            }
        }

        private DesktopLayoutEditorWindow FindOpenLayoutEditor(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return null;
            }
            for (int index = 0; index < openLayoutEditors.Count; index++)
            {
                DesktopLayoutEditorWindow editor = openLayoutEditors[index];
                if (editor == null)
                {
                    continue;
                }
                DesktopTopologyLayout openLayout = editor.Layout;
                if (openLayout == null)
                {
                    continue;
                }
                if (object.ReferenceEquals(openLayout, layout))
                {
                    return editor;
                }
                if (!string.IsNullOrEmpty(openLayout.Id) && !string.IsNullOrEmpty(layout.Id)
                    && string.Equals(openLayout.Id, layout.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return editor;
                }
            }
            return null;
        }

        private Task<DesktopShellSnapshot> GetSharedLayoutCaptureTask()
        {
            bool stale = sharedLayoutCaptureTask == null
                || (sharedLayoutCaptureTask.IsCompleted
                    && (DateTime.UtcNow - sharedLayoutCaptureStartedUtc).TotalSeconds > 3.0);
            if (stale)
            {
                sharedLayoutCaptureStartedUtc = DateTime.UtcNow;
                sharedLayoutCaptureTask =
                    controller.CaptureDesktopSnapshotWithoutSavingAsync();
            }
            return sharedLayoutCaptureTask;
        }

        private bool OpenLayoutEditor(DesktopShellSnapshot before, DesktopTopologyLayout layout,
            int newCount, int missingCount, IList<string> missingIdentities, string screenshotPath,
            out DesktopLayoutEditorWindow openedEditor)
        {
            openedEditor = null;
            byte[] archiveBefore = ReadDesktopLayoutArchiveBytes();
            // 没有布局预览图时不再同步截整张桌面：编辑页首帧会被截屏卡 1~2 秒。
            // 编辑页背景优先用当前壁纸，没有壁纸时才退到渐变底。
            if (string.IsNullOrWhiteSpace(screenshotPath))
            {
                screenshotPath = null;
            }
            DesktopLayoutEditorWindow editor = null;
            try
            {
                editor = new DesktopLayoutEditorWindow(
                    this, controller, before, layout, newCount, missingCount,
                    missingIdentities, screenshotPath);
                int slot = openLayoutEditors.Count;
                // 第一个窗口保持居中；后续窗口每开一个往右下错 26px，避免完全叠在一起。
                if (slot > 0)
                {
                    Rect workArea = SystemParameters.WorkArea;
                    double offset = (slot % 6 + 1) * 26.0;
                    editor.Left = Math.Max(workArea.Left + 8.0,
                        Math.Min(workArea.Right - editor.Width - 8.0, editor.Left + offset));
                    editor.Top = Math.Max(workArea.Top + 8.0,
                        Math.Min(workArea.Bottom - editor.Height - 8.0, editor.Top + offset));
                }
                openLayoutEditors.Add(editor);
                openedEditor = editor;
                DesktopLayoutEditorWindow editorToClose = editor;
                editor.Closed += delegate
                {
                    openLayoutEditors.Remove(editorToClose);
                    if (editorToClose.SavedLayout)
                    {
                        // 布局档案已改；编辑页现在只保存布局、不动当前桌面。
                        // 让下一次新窗口重新读取当前桌面，避免沿用旧快照。
                        sharedLayoutCaptureTask = null;
                        RefreshLayoutSummary();
                        int remaining = openLayoutEditors.Count;
                        statusText.Text = remaining > 0
                            ? AppText.F(
                                DesktopLayoutTextKeys.EditorSavedRemainingFormat,
                                layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                                remaining)
                            : AppText.F(
                                DesktopLayoutTextKeys.EditorSavedFormat,
                                layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback));
                        // 编辑窗保存完就关了；主窗口可能还在后面。除了底部状态，
                        // 再弹一次和其他工作区一致的绿色保存成功提示。
                        ShowLayoutSavedBanner(editorToClose.Layout);
                        Activate();
                        PushDesktopLayoutSaveUndo(archiveBefore, layout);
                    }
                    else
                    {
                        int remaining = openLayoutEditors.Count;
                        statusText.Text = remaining > 0
                            ? AppText.F(
                                DesktopLayoutTextKeys.EditorCancelledRemainingFormat,
                                remaining)
                            : AppText.T(DesktopLayoutTextKeys.EditorCancelled);
                    }
                };
                // 非模态显示：编辑页单独出现在任务栏，主窗口仍可操作；
                // 最小化编辑页后可继续用主窗口，和普通多窗口软件一致。
                editor.Show();
                editor.Activate();
                statusText.Text = AppText.T(openLayoutEditors.Count > 1
                    ? DesktopLayoutTextKeys.EditorOpenedMultiple
                    : DesktopLayoutTextKeys.EditorOpenedSingle);
                return true;
            }
            catch
            {
                if (editor != null)
                {
                    openLayoutEditors.Remove(editor);
                }
                openedEditor = null;
                throw;
            }
        }

        private static DesktopShellSnapshot BuildLayoutEditSnapshot(
            DesktopTopologyLayout layout,
            DesktopShellSnapshot current)
        {
            DesktopShellSnapshot result = new DesktopShellSnapshot();
            result.Topology = current != null ? current.Topology
                : (layout != null && layout.Topology != null ? layout.Topology : new DisplayTopology());
            result.GridWidth = layout == null || layout.GridWidth <= 0 ? 75 : layout.GridWidth;
            result.GridHeight = layout == null || layout.GridHeight <= 0 ? 75 : layout.GridHeight;
            result.CapturedUtc = DateTime.UtcNow;
            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (layout != null && layout.Icons != null)
            {
                for (int index = 0; index < layout.Icons.Count; index++)
                {
                    DesktopIconPlacement saved = layout.Icons[index];
                    // 已删除的图标也保留在编辑页（红色描边），保存时才移除。
                    DesktopIconPlacement item = ClonePlacement(saved);
                    if (item == null)
                    {
                        continue;
                    }
                    result.Icons.Add(item);
                    if (!string.IsNullOrEmpty(item.Identity))
                    {
                        identities.Add(DesktopIdentity.Normalize(item.Identity));
                    }
                }
            }
            if (current != null && current.Icons != null)
            {
                for (int index = 0; index < current.Icons.Count; index++)
                {
                    DesktopIconPlacement item = ClonePlacement(current.Icons[index]);
                    if (item == null || string.IsNullOrEmpty(item.Identity))
                    {
                        continue;
                    }
                    string identity = DesktopIdentity.Normalize(item.Identity);
                    if (identities.Contains(identity))
                    {
                        continue;
                    }
                    result.Icons.Add(item);
                    identities.Add(identity);
                }
            }
            return result;
        }

        private static DesktopIconPlacement ClonePlacement(DesktopIconPlacement source)
        {
            if (source == null)
            {
                return null;
            }
            DesktopIconPlacement copy = new DesktopIconPlacement();
            copy.Identity = source.Identity;
            copy.DisplayName = source.DisplayName;
            copy.CreatedUtc = source.CreatedUtc;
            copy.MonitorKey = source.MonitorKey;
            copy.X = source.X;
            copy.Y = source.Y;
            copy.MonitorOffsetX = source.MonitorOffsetX;
            copy.MonitorOffsetY = source.MonitorOffsetY;
            copy.NormalizedX = source.NormalizedX;
            copy.NormalizedY = source.NormalizedY;
            return copy;
        }
    }
}
