using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Core;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.StartupRestore.Core;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// Owns the application lifecycle.  Window code only observes this object
    /// and invokes commands, which keeps long-running restore work off the UI
    /// thread and makes the core easy to exercise without creating a window.
    /// </summary>
    // partial：StartupRestore/Core 里有一个薄薄的“自启动应用恢复转接”分部。
    // 代码仍归启动与恢复文件夹，Applications 只调用公开入口。
    public sealed partial class ApplicationController : INotifyPropertyChanged, IDisposable
    {
        public DesktopShellSnapshot CaptureDesktopLayout()
        {
            ThrowIfDisposed();
            return CaptureDesktopLayoutInternal(true, true);
        }

        /// <summary>
        /// Captures the Explorer icon layout away from the WPF dispatcher.
        /// Native desktop enumeration can wait while Explorer refreshes, so
        /// explicit save commands use this asynchronous wrapper as well.
        /// </summary>
        public Task<DesktopShellSnapshot> CaptureDesktopLayoutAsync()
        {
            ThrowIfDisposed();
            return CaptureDesktopLayoutAsync(true, DesktopLayoutCaptureTimeoutMilliseconds, true);
        }

        private Task<DesktopShellSnapshot> CaptureDesktopLayoutAsync(bool announce)
        {
            return CaptureDesktopLayoutAsync(announce, DesktopLayoutCaptureTimeoutMilliseconds, true);
        }

        // 保留原来的二参数入口：后台/关机前的保存走它（持久化），
        // 测试也通过反射找 `(bool, int)` 这个签名验证超时逻辑。
        private Task<DesktopShellSnapshot> CaptureDesktopLayoutAsync(bool announce, int timeoutMilliseconds)
        {
            return CaptureDesktopLayoutAsync(announce, timeoutMilliseconds, true);
        }

        private async Task<DesktopShellSnapshot> CaptureDesktopLayoutAsync(
            bool announce,
            int timeoutMilliseconds,
            bool persist)
        {
            if (disposed)
            {
                return null;
            }
            if (Interlocked.CompareExchange(ref layoutCaptureInProgress, 1, 0) != 0)
            {
                if (announce && !disposed)
                {
                    StatusText = AppText.T(ShellTextKeys.StatusLayoutCaptureBusy);
                }
                return null;
            }

            try
            {
                Task<DesktopShellSnapshot> worker = StartDesktopLayoutWorker();
                Task completed = await Task.WhenAny(
                    worker,
                    Task.Delay(Math.Max(500, timeoutMilliseconds))).ConfigureAwait(true);
                if (completed != worker)
                {
                    if (announce && !disposed)
                    {
                        StatusText = AppText.T(ShellTextKeys.StatusLayoutCaptureTimeout);
                    }
                    AppLog.Write("Desktop layout capture exceeded its time limit and was ignored.");
                    return null;
                }

                DesktopShellSnapshot snapshot = await worker.ConfigureAwait(true);
                if (snapshot != null && !disposed)
                {
                    // Persist only after the bounded capture has completed.
                    // A worker that outlives the timeout can therefore never
                    // write a late or stale layout over a newer capture.
                    // 【2026-10-20 修】“恢复图标布局”撤销用的 CaptureDesktopSnapshotWithoutSavingAsync
                    // 也必须走这里，但 persist=false —— 它只取当前位置，绝不能把当前桌面写回档案，
                    // 否则恢复会先覆盖要恢复的档案，再按覆盖后的内容恢复（等于没恢复）。
                    if (persist)
                    {
                        Invoke(layoutCoordinator, "SaveSnapshot", snapshot);
                    }
                    if (announce && !disposed)
                    {
                        StatusText = persist
                            ? AppText.F(
                                ShellTextKeys.StatusLayoutSavedFormat,
                                snapshot.Icons == null ? 0 : snapshot.Icons.Count)
                            : AppText.F(
                                ShellTextKeys.StatusLayoutPositionsReadFormat,
                                snapshot.Icons == null ? 0 : snapshot.Icons.Count);
                    }
                }
                return disposed ? null : snapshot;
            }
            catch (Exception exception)
            {
                if (announce && !disposed)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusLayoutSaveFailedFormat, exception.Message);
                }
                AppLog.Write("Unable to capture desktop layout asynchronously.", exception);
                return null;
            }
            finally
            {
                // A timed-out worker is deliberately abandoned.  Releasing
                // the gate keeps the UI usable even if Explorer never returns;
                // the worker has no callback that can commit a late result.
                Interlocked.Exchange(ref layoutCaptureInProgress, 0);
            }
        }

        private Task<DesktopShellSnapshot> StartDesktopLayoutWorker()
        {
            TaskCompletionSource<DesktopShellSnapshot> completion = new TaskCompletionSource<DesktopShellSnapshot>();
            Thread worker = new Thread(delegate()
            {
                DesktopShellSnapshot result = null;
                try
                {
                    // The worker is capture-only.  Saving is performed by the
                    // awaiting caller after the timeout check has succeeded.
                    result = CaptureDesktopLayoutInternal(false, false);
                }
                catch (Exception exception)
                {
                    AppLog.Write("Unable to capture desktop layout on the worker thread.", exception);
                }
                finally
                {
                    completion.TrySetResult(result);
                }
            });
            worker.IsBackground = true;
            worker.Name = "WorkspaceRestorer.DesktopLayout";
            try
            {
                worker.SetApartmentState(ApartmentState.STA);
            }
            catch
            {
                // The runtime may reject an apartment change on a restricted
                // host.  The worker can still perform the native fallback.
            }
            try
            {
                worker.Start();
            }
            catch
            {
                completion.TrySetResult(null);
                throw;
            }
            return completion.Task;
        }

        private DesktopShellSnapshot CaptureDesktopLayoutInternal(bool announce, bool persist)
        {
            try
            {
                object value;
                if (persist)
                {
                    value = Invoke(layoutCoordinator, "CaptureAndMerge");
                    if (value == null)
                    {
                        value = Invoke(layoutCoordinator, "CaptureCurrent");
                    }
                    if (value == null)
                    {
                        object topologyService = CreateService("WorkspaceRestorer.Desktop.DisplayTopologyService");
                        object topology = Invoke(topologyService, "Capture");
                        value = Invoke(layoutCoordinator, "CaptureAndSave", topology);
                    }
                }
                else
                {
                    // CaptureOnly never calls DesktopLayoutRepository.Save.
                    // Do not fall back to CaptureCurrent/CaptureAndSave here,
                    // since both persist and would reintroduce late writes.
                    value = Invoke(layoutCoordinator, "CaptureOnly");
                }
                DesktopShellSnapshot snapshot = value as DesktopShellSnapshot;
                if (snapshot != null)
                {
                    if (announce)
                    {
                        StatusText = AppText.F(
                            ShellTextKeys.StatusLayoutSavedFormat,
                            snapshot.Icons == null ? 0 : snapshot.Icons.Count);
                    }
                    return snapshot;
                }

                object iconService = CreateService("WorkspaceRestorer.Desktop.DesktopIconService");
                value = Invoke(iconService, "Capture");
                snapshot = value as DesktopShellSnapshot;
                if (announce)
                {
                    StatusText = snapshot == null
                        ? AppText.T(ShellTextKeys.StatusLayoutUnreadable)
                        : AppText.T(ShellTextKeys.StatusLayoutSaved);
                }
                return snapshot;
            }
            catch (Exception exception)
            {
                if (announce)
                {
                    StatusText = AppText.F(ShellTextKeys.StatusLayoutSaveFailedFormat, exception.Message);
                }
                AppLog.Write("Unable to capture desktop layout.", exception);
                return null;
            }
        }

        /// <summary>
        /// 只为“撤销恢复图标布局”取一份**当前位置**的快照：走 CaptureOnly 通道，**不写档案**
        /// （2026-10-17 用户要求快捷键作用于全局，图标位置保存也要能撤销）。
        /// 用异步+超时的那一套，读取失败就返回 null，撤销记录干脆不记（总比记一条错的强）。
        /// </summary>
        public Task<DesktopShellSnapshot> CaptureDesktopSnapshotWithoutSavingAsync()
        {
            ThrowIfDisposed();
            return CaptureDesktopLayoutAsync(false, DesktopLayoutCaptureTimeoutMilliseconds, false);
        }

        /// <summary>
        /// 读取已保存的桌面布局（每个显示器拓扑一份），给「图标位置保存」的文件夹卡片用。
        /// 只读；异常时返回空清单，界面显示空状态即可。
        /// </summary>
        public IList<DesktopTopologyLayout> GetDesktopLayouts()
        {
            ThrowIfDisposed();
            try
            {
                return GetDesktopLayouts(LoadLayoutCatalog());
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to read the desktop layout catalog.", exception);
                return new List<DesktopTopologyLayout>();
            }
        }

        /// <summary>
        /// 用调用方已经加载好的档案取出卡片清单（去重 + 兼容可移植布局），不再读盘。
        ///
        /// 一次拖放只加载一次档案时用这个重载：以前 GetDesktopLayouts / ActiveDesktopLayoutId /
        /// 移动前后各取一次，同一份 `布局档案.xml` 在一次动作里要被完整解析七八遍
        /// （用户 2026-11-20 实测：一次拖放读 5.36 MB、松手后卡 105 ms）。
        /// </summary>
        internal IList<DesktopTopologyLayout> GetDesktopLayouts(DesktopLayoutCatalog catalog)
        {
            List<DesktopTopologyLayout> result = new List<DesktopTopologyLayout>();
            if (catalog == null)
            {
                return result;
            }
            // 目录维护已经在 LoadLayoutCatalog 的“第一次读取”里做过；
            // 这里不能再 Sync，否则每张卡片刷新/每次拖动提示都会重写全部 布局.xml。
            // 用户 2026-10-21：同一显示器组合可以有多份命名布局。
            // 原来这里按 TopologySignature 去重，导致多份布局只剩第一张卡片。
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (catalog.Layouts != null)
            {
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[index];
                    if (layout == null)
                    {
                        continue;
                    }
                    string id = layout.Id ?? string.Empty;
                    if (ids.Add(id))
                    {
                        result.Add(layout);
                    }
                }
            }
            // 兼容只有 PortableSingleDisplayLayout 的旧档案。
            if (catalog.PortableSingleDisplayLayout != null)
            {
                string portableId = catalog.PortableSingleDisplayLayout.Id ?? string.Empty;
                if (ids.Add(portableId))
                {
                    result.Add(catalog.PortableSingleDisplayLayout);
                }
            }
            return result;
        }

        /// <summary>
        /// 保存刚捕获的布局对应的“文件夹 + 桌面截图”。
        /// 截图失败不算保存失败（布局档案已经写好）；返回是否找到了对应布局。
        /// </summary>
        public bool SaveDesktopLayoutPreview(DesktopShellSnapshot snapshot, bool showDesktop)
        {
            ThrowIfDisposed();
            if (snapshot == null || snapshot.Topology == null)
            {
                return false;
            }
            try
            {
                string signature = snapshot.Topology.Signature ?? string.Empty;
                IList<DesktopTopologyLayout> layouts = GetDesktopLayouts();
                for (int index = 0; index < layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = layouts[index];
                    if (layout == null)
                    {
                        continue;
                    }
                    if (string.Equals(layout.TopologySignature ?? string.Empty, signature, StringComparison.OrdinalIgnoreCase))
                    {
                        DesktopLayoutFolderStore.Save(layout, layouts, true, showDesktop);
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save the desktop layout folder/preview.", exception);
            }
            return false;
        }

        /// <summary>
        /// 加载并整理布局档案。**一次用户动作只该调用一次**：加载后把同一个 catalog
        /// 传给 GetDesktopLayouts(catalog) / GetActiveLayoutId(catalog) / 移动重载，
        /// 不要再在每个刷新点各读一遍（那会让同一份 615 KB 档案在一次拖放里解析七八遍）。
        /// </summary>
        internal DesktopLayoutCatalog LoadLayoutCatalog()
        {
            DesktopLayoutCatalog catalog = Invoke(layoutCoordinator, "LoadCatalog") as DesktopLayoutCatalog;
            catalog = DesktopLayoutRepository.Normalize(catalog ?? new DesktopLayoutCatalog());
            if (!layoutStorageMaintained)
            {
                // 移动日志恢复 / 历史目录回收 / Sync 只做一次。
                // 之后的所有读取都只读档案，不再写“布局”目录里的 布局.xml。
                RecoverLayoutMoveJournal(catalog);
                EnsureLayoutStorageMaintained(catalog);
            }
            return catalog;
        }

        /// <summary>
        /// 第一次读取布局时把“布局”目录对齐一次：先回收历史孤儿，再补缺失的 布局.xml。
        /// 失败也把标志置上，避免每次刷新都重试同一批磁盘操作；下次启动再试。
        /// </summary>
        private void EnsureLayoutStorageMaintained(DesktopLayoutCatalog catalog)
        {
            if (layoutStorageMaintained || catalog == null)
            {
                return;
            }
            if (recycleBinCatalog == null)
            {
                // 没有回收站清单时不能先删孤儿：保留标志，等构造完成后下一次读取再做。
                return;
            }
            layoutStorageMaintained = true;
            try
            {
                // Preserve 自己会删除已成功写进回收站的孤儿；它返回 false 表示写回收站失败，
                // 这时绝不能再让 Sync 删目录（否则会丢数据）。
                if (!PreserveUnreferencedLayoutFolders(catalog))
                {
                    return;
                }
                DesktopLayoutFolderStore.Sync(catalog);
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 首次同步布局文件夹失败（不影响卡片读取）", exception);
            }
        }

        private bool SaveLayoutCatalog(DesktopLayoutCatalog catalog)
        {
            try
            {
                Invoke(layoutCoordinator, "SaveCatalog", catalog);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 保存布局档案失败。", exception);
                return false;
            }
        }

        /// <summary>
        /// 只保存卡片顺序/父文件夹到 sidecar；失败时调用方再退回完整档案保存，
        /// 保证“拖了但没记住”不会静默发生。
        /// </summary>
        private bool SaveLayoutDisplayOrder(DesktopLayoutCatalog catalog)
        {
            try
            {
                Invoke(layoutCoordinator, "SaveDisplayOrder", catalog);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 保存布局排列失败，退回完整档案保存。", exception);
                return false;
            }
        }

        public string ActiveDesktopLayoutId
        {
            get
            {
                try
                {
                    return GetActiveLayoutId(LoadLayoutCatalog());
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>从已经加载好的档案里读“当前布局”；同一次动作里不再为它读一遍档案。</summary>
        internal string GetActiveLayoutId(DesktopLayoutCatalog catalog)
        {
            return catalog == null ? string.Empty : (catalog.ActiveLayoutId ?? string.Empty);
        }

        public bool SetActiveDesktopLayout(string layoutId)
        {
            ThrowIfDisposed();
            try
            {
                DesktopLayoutCatalog catalog = LoadLayoutCatalog();
                DesktopTopologyLayout layout = DesktopLayoutRepository.FindById(catalog, layoutId);
                if (layout == null || layout.IsFolder)
                {
                    // 文件夹只用于分组；当前布局必须是一份可恢复的叶子布局。
                    return false;
                }
                catalog.ActiveLayoutId = layout.Id;
                DesktopLayoutRepository.SetChoice(catalog, layout.TopologySignature, layout.Id);
                SaveLayoutCatalog(catalog);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to select the desktop layout.", exception);
                return false;
            }
        }

        /// <summary>
        /// 记住编辑页「新图标」标签的位置（按布局 Id 存在 sidecar“编辑器状态.xml”）。
        /// 不写 DesktopTopologyLayout：旧布局 XML 没有新字段，不能让 DataContract 变必填。
        /// </summary>
        public void SaveDesktopLayoutEditorDockPosition(DesktopTopologyLayout layout, double normalizedX, double normalizedY)
        {
            ThrowIfDisposed();
            if (layout == null || string.IsNullOrWhiteSpace(layout.Id))
            {
                return;
            }
            try
            {
                new DesktopLayoutEditorStateStore().Save(layout.Id, normalizedX, normalizedY);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save the desktop layout editor dock position.", exception);
            }
        }

        public bool TryGetDesktopLayoutEditorDockPosition(DesktopTopologyLayout layout, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (layout == null || string.IsNullOrWhiteSpace(layout.Id))
            {
                return false;
            }
            try
            {
                return new DesktopLayoutEditorStateStore().TryGet(layout.Id, out x, out y);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to read the desktop layout editor dock position.", exception);
                return false;
            }
        }

        public DesktopTopologyLayout CreateDesktopLayout(string name)
        {
            return CreateDesktopLayout(name, null, false);
        }

        public DesktopTopologyLayout CreateDesktopLayoutFolder(string name, DesktopTopologyLayout parent)
        {
            return CreateDesktopLayout(name, parent, true);
        }

        /// <summary>
        /// 新建布局或布局文件夹。parent 为空 = 根级；parent 必须是文件夹。
        /// 只有叶子布局会成为“当前可恢复布局”，纯文件夹只用于分组。
        /// </summary>
        public DesktopTopologyLayout CreateDesktopLayout(string name, DesktopTopologyLayout parent, bool isFolder)
        {
            ThrowIfDisposed();
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            DesktopTopologyLayout parentStored = parent == null
                ? null
                : DesktopLayoutRepository.FindById(catalog, parent.Id);
            if (parentStored != null && !parentStored.IsFolder)
            {
                StatusText = AppText.T(ShellTextKeys.StatusLayoutNeedsFolder);
                return null;
            }
            string parentId = parentStored == null ? null : parentStored.Id;
            string unique = UniqueLayoutName(catalog, name, null, parentId);
            DesktopTopologyLayout layout = new DesktopTopologyLayout();
            layout.Id = Guid.NewGuid().ToString("N");
            layout.Name = unique;
            layout.IsFolder = isFolder;
            layout.ParentId = parentId;
            layout.CreatedUtc = DateTime.UtcNow;
            layout.UpdatedUtc = DateTime.UtcNow;
            int maxOrder = 0;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout sibling = catalog.Layouts[index];
                if (sibling != null
                    && string.Equals(sibling.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && sibling.SortOrder > maxOrder)
                {
                    maxOrder = sibling.SortOrder;
                }
            }
            layout.SortOrder = maxOrder + 1;
            catalog.Layouts.Add(layout);
            PlanFolderTree.Normalize(catalog.Layouts);
            if (!isFolder)
            {
                catalog.ActiveLayoutId = layout.Id;
            }
            SaveLayoutCatalog(catalog);
            // 叶子布局也立刻建目录：以前依赖每次读取时的 Sync 来补，
            // 现在 Sync 只做一次，新建空布局必须自己落盘，否则要等第一次保存才出现文件夹。
            DesktopLayoutFolderStore.Save(layout, catalog.Layouts, false, false);
            if (isFolder)
            {
                StatusText = AppText.F(ShellTextKeys.StatusLayoutFolderCreatedFormat, layout.Name);
            }
            return layout;
        }

        /// <summary>
        /// 把一份布局（Ctrl+C 的剪贴板快照）粘成一份**独立的新布局**：
        /// 新 Id、名字顺延、保留拓扑/图标坐标/网格；能复制到桌面预览图就一起复制。
        /// </summary>
        public DesktopTopologyLayout CreateDesktopLayoutCopy(DesktopTopologyLayout source, string name)
        {
            ThrowIfDisposed();
            if (source == null)
            {
                return null;
            }
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            // 用和回收站相同的 JSON 往返做深拷贝，避免复制后两份布局共享同一个 Icons/Topology 列表。
            DesktopTopologyLayout copy = DeserializeDesktopLayout(SerializeDesktopLayout(source));
            if (copy == null)
            {
                StatusText = AppText.T(ShellTextKeys.StatusLayoutCopyFailed);
                return null;
            }
            string baseName = string.IsNullOrWhiteSpace(name)
                ? (source.Name ?? AppText.T(ShellTextKeys.FallbackLayout))
                    + AppText.T(ShellTextKeys.FallbackCopySuffix)
                : name.Trim();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.ParentId = source.ParentId;
            copy.Name = UniqueLayoutName(catalog, baseName, null, copy.ParentId);
            copy.CreatedUtc = DateTime.UtcNow;
            copy.UpdatedUtc = copy.CreatedUtc;
            // 新布局里的每个坐标都是“明确保存”的结果，不再沿用原布局的“自动并集”标记。
            copy.AutoMergedIdentities = new List<string>();
            int maxOrder = 0;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                if (catalog.Layouts[index] != null && catalog.Layouts[index].SortOrder > maxOrder)
                {
                    maxOrder = catalog.Layouts[index].SortOrder;
                }
            }
            copy.SortOrder = maxOrder + 1;
            catalog.Layouts.Add(copy);
            catalog.ActiveLayoutId = copy.Id;
            DesktopLayoutRepository.SetChoice(catalog, copy.TopologySignature, copy.Id);
            SaveLayoutCatalog(catalog);
            // 布局档案先落盘，再写用户能打开的镜像文件夹；写文件夹失败不影响恢复数据。
            try
            {
                DesktopLayoutFolderStore.Save(copy, catalog.Layouts, false, false);
                DesktopLayoutFolderStore.CopyPreview(source, copy);
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 复制布局文件夹/预览图失败（布局档案已保存）", exception);
            }
            return copy;
        }

        public bool RenameDesktopLayout(DesktopTopologyLayout layout, string name)
        {
            ThrowIfDisposed();
            if (layout == null || string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            DesktopTopologyLayout stored = DesktopLayoutRepository.FindById(catalog, layout.Id);
            if (stored == null)
            {
                return false;
            }
            // 目录可能还停在旧名字/旧父级；先按 Id 找实际目录，不能只按当前树算路径。
            string oldDirectory = DesktopLayoutFolderStore.ExactFolderPath(stored);
            if (string.IsNullOrWhiteSpace(oldDirectory))
            {
                oldDirectory = DesktopLayoutFolderStore.FolderPathFor(stored, catalog.Layouts);
            }
            stored.Name = UniqueLayoutName(catalog, name.Trim(), stored.Id, stored.ParentId);
            layout.Name = stored.Name;
            string newDirectory = DesktopLayoutFolderStore.FolderPathFor(stored, catalog.Layouts);
            bool journalWritten = false;
            if (!string.IsNullOrWhiteSpace(oldDirectory)
                && !string.IsNullOrWhiteSpace(newDirectory)
                && !string.Equals(
                    Path.GetFullPath(oldDirectory),
                    Path.GetFullPath(newDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                PlanFolderMoveJournalEntry entry = new PlanFolderMoveJournalEntry();
                entry.PlanId = stored.Id;
                entry.OldParentId = stored.ParentId;
                entry.NewParentId = stored.ParentId;
                entry.OldDirectory = oldDirectory;
                entry.NewDirectory = newDirectory;
                entry.OldSiblingIndex = PlanFolderTree.SiblingIndex(catalog.Layouts, stored);
                entry.NewSiblingIndex = entry.OldSiblingIndex;
                entry.RecordedUtc = DateTime.UtcNow;
                try
                {
                    PlanFolderMoveJournal.Append(LayoutMoveJournalPath, entry);
                    journalWritten = true;
                }
                catch (Exception exception)
                {
                    AppLog.Write("[图标位置保存] 改名移动日志写入失败，本次只更新数据。", exception);
                }
                if (journalWritten)
                {
                    PlanFolderDirectoryMover.Move(
                        oldDirectory,
                        newDirectory,
                        stored.Id,
                        DesktopLayoutFolderStore.LayoutFileName,
                        DesktopLayoutFolderStore.ReadLayoutIdAt);
                }
            }
            bool saved = SaveLayoutCatalog(catalog);
            // 一份布局只有一个文件夹：改名时把旧文件夹搬成新名字，不能下次保存再新建一份。
            DesktopLayoutFolderStore.Save(stored, catalog.Layouts, false, false);
            if (saved && journalWritten)
            {
                PlanFolderMoveJournal.Clear(LayoutMoveJournalPath);
            }
            return saved;
        }

        public bool DeleteDesktopLayout(DesktopTopologyLayout layout)
        {
            ThrowIfDisposed();
            if (layout == null || recycleBinCatalog == null)
            {
                return false;
            }
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            DesktopTopologyLayout stored = DesktopLayoutRepository.FindById(catalog, layout.Id);
            bool storedIsPortableOnly = false;
            if (stored == null && catalog.PortableSingleDisplayLayout != null
                && string.Equals(catalog.PortableSingleDisplayLayout.Id, layout.Id, StringComparison.OrdinalIgnoreCase))
            {
                // 旧档案可能只有 PortableSingleDisplayLayout，没有进 Layouts；
                // 这张卡仍然显示，也要能删除。
                stored = catalog.PortableSingleDisplayLayout;
                storedIsPortableOnly = true;
            }
            if (stored == null)
            {
                return false;
            }
            if (stored.IsFolder)
            {
                return DeleteDesktopLayoutFolder(stored, catalog);
            }
            RecycleBinEntry entry = new RecycleBinEntry();
            entry.Kind = RecycleBinKind.DesktopLayout;
            entry.Title = stored.Name ?? AppText.T(ShellTextKeys.FallbackLayout);
            entry.Subtitle = AppText.F(
                ShellTextKeys.StatusLayoutIconCountFormat,
                DesktopLayoutFolderStore.DescribeDisplay(stored),
                stored.Icons == null ? 0 : stored.Icons.Count);
            entry.DeletedAtUtc = DateTime.UtcNow;
            entry.Payload = SerializeDesktopLayout(stored);
            recycleBinCatalog.Entries.Add(entry);
            RefreshRecycleBinRemainingDays();
            // 先把回收站条目真正写到磁盘，再动布局档案/文件夹。
            // 写盘失败就撤掉内存里的条目并保留布局，不能出现“文件夹已删、回收站没记录”。
            if (!PersistRecycleBin())
            {
                recycleBinCatalog.Entries.Remove(entry);
                RefreshRecycleBinRemainingDays();
                StatusText = AppText.F(
                    ShellTextKeys.StatusLayoutDeleteRecycleFailedFormat,
                    entry.Title ?? AppText.T(ShellTextKeys.FallbackLayout));
                return false;
            }
            if (!storedIsPortableOnly)
            {
                catalog.Layouts.Remove(stored);
            }
            // 自动布局同时还会克隆在 PortableSingleDisplayLayout 里；只删 Layouts 的话，
            // GetDesktopLayouts 会把这个克隆当成“第四张卡”又显示出来（用户反馈删不掉）。
            bool automatic = !string.IsNullOrWhiteSpace(stored.Id)
                && stored.Id.StartsWith("layout-", StringComparison.OrdinalIgnoreCase);
            if (catalog.PortableSingleDisplayLayout != null
                && (string.Equals(catalog.PortableSingleDisplayLayout.Id, stored.Id, StringComparison.OrdinalIgnoreCase)
                    || (automatic && string.Equals(
                        catalog.PortableSingleDisplayLayout.TopologySignature,
                        stored.TopologySignature,
                        StringComparison.OrdinalIgnoreCase))))
            {
                catalog.PortableSingleDisplayLayout = null;
            }
            if (string.Equals(catalog.ActiveLayoutId, stored.Id, StringComparison.OrdinalIgnoreCase))
            {
                catalog.ActiveLayoutId = catalog.Layouts.Count == 0 ? string.Empty : catalog.Layouts[0].Id;
            }
            SaveLayoutCatalog(catalog);
            // 回收站条目里已经保存了完整布局数据；镜像文件夹不再留在“布局”里，
            // 否则会变成永远不显示、也不会自动清理的历史副本。
            DesktopLayoutFolderStore.DeleteFolder(stored);
            OnPropertyChanged("RecycleBinEntries");
            StatusText = AppText.F(ShellTextKeys.StatusLayoutMovedToRecycleFormat, entry.Title);
            return true;
        }

        /// <summary>
        /// 删除布局文件夹：里面的每份叶子布局各进一条回收站记录；文件夹本身不是可恢复布局。
        /// 恢复叶子时原文件夹还在就回原处，不在就回根目录（不自动重建文件夹）。
        /// </summary>
        private bool DeleteDesktopLayoutFolder(DesktopTopologyLayout folder, DesktopLayoutCatalog catalog)
        {
            List<DesktopTopologyLayout> subtree = PlanFolderTree.Subtree(catalog.Layouts, folder);
            if (subtree.Count == 0)
            {
                return false;
            }
            string folderPath = DesktopLayoutFolderStore.FolderPathFor(folder, catalog.Layouts);
            List<RecycleBinEntry> added = new List<RecycleBinEntry>();
            for (int index = 0; index < subtree.Count; index++)
            {
                DesktopTopologyLayout node = subtree[index];
                if (node == null || node.IsFolder)
                {
                    continue;
                }
                RecycleBinEntry entry = new RecycleBinEntry();
                entry.Kind = RecycleBinKind.DesktopLayout;
                entry.Title = node.Name ?? AppText.T(ShellTextKeys.FallbackLayout);
                entry.Subtitle = AppText.F(
                    ShellTextKeys.StatusLayoutIconCountFormat,
                    DesktopLayoutFolderStore.DescribeDisplay(node),
                    node.Icons == null ? 0 : node.Icons.Count);
                entry.DeletedAtUtc = DateTime.UtcNow;
                entry.Payload = SerializeDesktopLayout(node);
                recycleBinCatalog.Entries.Add(entry);
                added.Add(entry);
            }
            RefreshRecycleBinRemainingDays();
            if (!PersistRecycleBin())
            {
                for (int index = 0; index < added.Count; index++)
                {
                    recycleBinCatalog.Entries.Remove(added[index]);
                }
                RefreshRecycleBinRemainingDays();
                StatusText = AppText.F(
                    ShellTextKeys.StatusLayoutFolderDeleteRecycleFailedFormat,
                    folder.Name ?? AppText.T(ShellTextKeys.FallbackLayoutFolder));
                return false;
            }
            PlanFolderTree.RemoveSubtree(catalog.Layouts, folder);
            if (string.Equals(catalog.ActiveLayoutId, folder.Id, StringComparison.OrdinalIgnoreCase))
            {
                catalog.ActiveLayoutId = string.Empty;
            }
            if (string.IsNullOrWhiteSpace(catalog.ActiveLayoutId))
            {
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    if (catalog.Layouts[index] != null && !catalog.Layouts[index].IsFolder)
                    {
                        catalog.ActiveLayoutId = catalog.Layouts[index].Id;
                        break;
                    }
                }
            }
            SaveLayoutCatalog(catalog);
            DesktopLayoutFolderStore.DeleteFolderAt(folderPath);
            OnPropertyChanged("RecycleBinEntries");
            StatusText = AppText.F(
                ShellTextKeys.StatusLayoutFolderDeletedFormat,
                folder.Name ?? AppText.T(ShellTextKeys.FallbackLayoutFolder),
                added.Count);
            return true;
        }

        public void ReorderDesktopLayouts(IList<string> idsInOrder)
        {
            if (idsInOrder == null)
            {
                return;
            }
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            for (int index = 0; index < idsInOrder.Count; index++)
            {
                DesktopTopologyLayout layout = DesktopLayoutRepository.FindById(catalog, idsInOrder[index]);
                if (layout != null)
                {
                    layout.SortOrder = index + 1;
                }
            }
            if (!SaveLayoutDisplayOrder(catalog))
            {
                SaveLayoutCatalog(catalog);
            }
        }

        /// <summary>
        /// 把布局/布局文件夹移进一个文件夹（target 为空 = 移到根级）。
        /// 和两个方案工作区一样：只移动目录 + 写移动日志，不重写整棵镜像。
        /// </summary>
        public bool MoveDesktopLayoutInto(DesktopTopologyLayout source, DesktopTopologyLayout target)
        {
            ThrowIfDisposed();
            return MoveDesktopLayoutInto(source, target, LoadLayoutCatalog());
        }

        /// <summary>
        /// 用调用方已经加载好的档案移动（一次拖放只加载一次档案；重载内部绝不再读盘）。
        /// </summary>
        internal bool MoveDesktopLayoutInto(
            DesktopTopologyLayout source,
            DesktopTopologyLayout target,
            DesktopLayoutCatalog catalog)
        {
            ThrowIfDisposed();
            if (catalog == null)
            {
                return false;
            }
            DesktopTopologyLayout storedSource = DesktopLayoutRepository.FindById(catalog, source == null ? null : source.Id);
            DesktopTopologyLayout storedTarget = target == null
                ? null
                : DesktopLayoutRepository.FindById(catalog, target.Id);
            if (storedSource == null || (target != null && storedTarget == null))
            {
                return false;
            }
            if (storedTarget != null && !storedTarget.IsFolder)
            {
                StatusText = AppText.T(ShellTextKeys.StatusMoveIntoLayoutFolderOnly);
                return false;
            }
            if (!PlanFolderTree.CanMove(catalog.Layouts, storedSource, storedTarget))
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotMoveLayoutIntoSelf);
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureDesktopLayoutMove(storedSource, catalog.Layouts);
            if (!PlanFolderTree.MoveInto(catalog.Layouts, storedSource, storedTarget))
            {
                return false;
            }
            bool saved = CommitDesktopLayoutMove(before, storedSource, catalog);
            StatusText = storedTarget == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedToRootFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackLayout))
                : AppText.F(
                    ShellTextKeys.StatusMovedIntoFormat,
                    storedSource.Name ?? AppText.T(ShellTextKeys.FallbackLayout),
                    storedTarget.Name ?? AppText.T(ShellTextKeys.FallbackLayoutFolder));
            return saved;
        }

        public bool MoveDesktopLayoutRelative(DesktopTopologyLayout source, DesktopTopologyLayout target, bool after)
        {
            ThrowIfDisposed();
            return MoveDesktopLayoutRelative(source, target, after, LoadLayoutCatalog());
        }

        /// <summary>用调用方已经加载好的档案调整同级顺序（重载内部不读盘）。</summary>
        internal bool MoveDesktopLayoutRelative(
            DesktopTopologyLayout source,
            DesktopTopologyLayout target,
            bool after,
            DesktopLayoutCatalog catalog)
        {
            ThrowIfDisposed();
            if (catalog == null)
            {
                return false;
            }
            DesktopTopologyLayout storedSource = DesktopLayoutRepository.FindById(catalog, source == null ? null : source.Id);
            DesktopTopologyLayout storedTarget = DesktopLayoutRepository.FindById(catalog, target == null ? null : target.Id);
            if (storedSource == null || storedTarget == null)
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureDesktopLayoutMove(storedSource, catalog.Layouts);
            if (!PlanFolderTree.MoveRelative(catalog.Layouts, storedSource, storedTarget, after))
            {
                return false;
            }
            bool saved = CommitDesktopLayoutMove(before, storedSource, catalog);
            StatusText = AppText.T(ShellTextKeys.StatusLayoutOrderAdjusted);
            return saved;
        }

        public bool MoveDesktopLayoutToRoot(DesktopTopologyLayout source)
        {
            return MoveDesktopLayoutInto(source, null);
        }

        /// <summary>撤销/重做用：把布局放回指定父文件夹和同级位置。</summary>
        public bool MoveDesktopLayoutToPlacement(DesktopTopologyLayout layout, string parentId, int siblingIndex)
        {
            ThrowIfDisposed();
            return MoveDesktopLayoutToPlacement(layout, parentId, siblingIndex, LoadLayoutCatalog());
        }

        /// <summary>用调用方已经加载好的档案放回指定位置（重载内部不读盘）。</summary>
        internal bool MoveDesktopLayoutToPlacement(
            DesktopTopologyLayout layout,
            string parentId,
            int siblingIndex,
            DesktopLayoutCatalog catalog)
        {
            ThrowIfDisposed();
            if (catalog == null)
            {
                return false;
            }
            DesktopTopologyLayout stored = DesktopLayoutRepository.FindById(catalog, layout == null ? null : layout.Id);
            DesktopTopologyLayout parent = string.IsNullOrWhiteSpace(parentId)
                ? null
                : DesktopLayoutRepository.FindById(catalog, parentId);
            if (stored == null)
            {
                return false;
            }
            if (parent != null && !parent.IsFolder)
            {
                parent = null;
            }
            if (!PlanFolderTree.CanMove(catalog.Layouts, stored, parent))
            {
                return false;
            }
            PlanFolderMoveSnapshot before = CaptureDesktopLayoutMove(stored, catalog.Layouts);
            if (!PlanFolderTree.MoveInto(catalog.Layouts, stored, parent))
            {
                return false;
            }
            if (siblingIndex >= 0)
            {
                PlanFolderTree.PlaceAtSiblingIndex(catalog.Layouts, stored, siblingIndex);
            }
            bool saved = CommitDesktopLayoutMove(before, stored, catalog);
            StatusText = parent == null
                ? AppText.F(
                    ShellTextKeys.StatusMovedBackToRootFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackLayout))
                : AppText.F(
                    ShellTextKeys.StatusMovedBackIntoFormat,
                    stored.Name ?? AppText.T(ShellTextKeys.FallbackLayout),
                    parent.Name ?? AppText.T(ShellTextKeys.FallbackLayoutFolder));
            return saved;
        }

        private PlanFolderMoveSnapshot CaptureDesktopLayoutMove(
            DesktopTopologyLayout layout,
            IList<DesktopTopologyLayout> allLayouts)
        {
            PlanFolderMoveSnapshot snapshot = new PlanFolderMoveSnapshot();
            if (layout == null)
            {
                return snapshot;
            }
            snapshot.PlanId = layout.Id;
            snapshot.ParentId = layout.ParentId;
            snapshot.SiblingIndex = PlanFolderTree.SiblingIndex(allLayouts, layout);
            snapshot.DirectoryPath = DesktopLayoutFolderStore.FolderPathFor(layout, allLayouts);
            return snapshot;
        }

        private bool CommitDesktopLayoutMove(
            PlanFolderMoveSnapshot before,
            DesktopTopologyLayout layout,
            DesktopLayoutCatalog catalog)
        {
            if (before == null || layout == null || catalog == null)
            {
                return false;
            }
            DesktopTopologyLayout parent = string.IsNullOrWhiteSpace(layout.ParentId)
                ? null
                : DesktopLayoutRepository.FindById(catalog, layout.ParentId);
            string parentDirectory = parent == null
                ? DesktopLayoutFolderStore.RootDirectory
                : DesktopLayoutFolderStore.FolderPathFor(parent, catalog.Layouts);
            string destination = string.IsNullOrWhiteSpace(before.DirectoryPath)
                || string.IsNullOrWhiteSpace(parentDirectory)
                ? null
                : PlanFolderDirectoryMover.BuildDestinationPath(parentDirectory, before.DirectoryPath);
            bool sameDirectory = !string.IsNullOrWhiteSpace(destination)
                && string.Equals(
                    Path.GetFullPath(before.DirectoryPath),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase);
            bool journalWritten = false;
            if (!sameDirectory && !string.IsNullOrWhiteSpace(destination))
            {
                PlanFolderMoveJournalEntry entry = new PlanFolderMoveJournalEntry();
                entry.PlanId = layout.Id;
                entry.OldParentId = before.ParentId;
                entry.NewParentId = layout.ParentId;
                entry.OldDirectory = before.DirectoryPath;
                entry.NewDirectory = destination;
                entry.OldSiblingIndex = before.SiblingIndex;
                entry.NewSiblingIndex = PlanFolderTree.SiblingIndex(catalog.Layouts, layout);
                entry.RecordedUtc = DateTime.UtcNow;
                try
                {
                    PlanFolderMoveJournal.Append(LayoutMoveJournalPath, entry);
                    journalWritten = true;
                }
                catch (Exception exception)
                {
                    AppLog.Write("[图标位置保存] 移动日志写入失败，本次只更新数据。", exception);
                }
                if (journalWritten)
                {
                    PlanFolderDirectoryMover.Move(
                        before.DirectoryPath,
                        destination,
                        layout.Id,
                        DesktopLayoutFolderStore.LayoutFileName,
                        DesktopLayoutFolderStore.ReadLayoutIdAt);
                }
            }
            // 旧父级和新父级的同级序号都要整理；只改内存，不写 XML。
            NormalizeLayoutSiblingOrders(catalog, before.ParentId);
            NormalizeLayoutSiblingOrders(catalog, layout.ParentId);
            // 拖动只改“卡片怎么排/挂在哪个文件夹”这层展示信息：
            // 写几 KB 的 布局排列.json 就够了，不再重写 400 KB 档案和 77 KB 单布局 XML。
            bool saved = SaveLayoutDisplayOrder(catalog);
            if (!saved)
            {
                // sidecar 写失败时才退回完整档案，保证这次拖动仍能落盘。
                saved = SaveLayoutCatalog(catalog);
            }
            if (saved && journalWritten)
            {
                PlanFolderMoveJournal.Clear(LayoutMoveJournalPath);
            }
            return saved;
        }

        private static void NormalizeLayoutSiblingOrders(DesktopLayoutCatalog catalog, string parentId)
        {
            if (catalog == null || catalog.Layouts == null)
            {
                return;
            }
            IList<DesktopTopologyLayout> siblings = string.IsNullOrWhiteSpace(parentId)
                ? PlanFolderTree.Roots(catalog.Layouts)
                : PlanFolderTree.ChildrenOf(catalog.Layouts, parentId);
            for (int index = 0; index < siblings.Count; index++)
            {
                if (siblings[index] != null)
                {
                    siblings[index].SortOrder = index;
                }
            }
        }

        private void RecoverLayoutMoveJournal(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || catalog.Layouts == null)
            {
                return;
            }
            PlanFolderMoveJournalData journal = PlanFolderMoveJournal.Load(LayoutMoveJournalPath);
            if (journal.Entries == null || journal.Entries.Count == 0)
            {
                return;
            }
            int applied = PlanFolderMoveJournal.Apply(
                catalog.Layouts,
                journal.Entries,
                delegate(DesktopTopologyLayout layout) { return layout == null ? null : layout.Id; });
            if (applied > 0)
            {
                try
                {
                    Invoke(layoutCoordinator, "SaveCatalog", catalog);
                }
                catch (Exception exception)
                {
                    AppLog.Write("[图标位置保存] 启动修复布局移动日志时保存失败，保留移动日志。", exception);
                    return;
                }
            }
            PlanFolderMoveJournal.Clear(LayoutMoveJournalPath);
            AppLog.Write("[图标位置保存] 启动修复布局移动日志 " + applied + " 条。");
        }

        public async Task<DesktopTopologyLayout> SaveDesktopLayoutToProfileAsync(DesktopTopologyLayout profile, bool capturePreview, bool showDesktop)
        {
            ThrowIfDisposed();
            if (profile == null || profile.IsFolder)
            {
                return null;
            }
            DesktopShellSnapshot snapshot = await CaptureDesktopSnapshotWithoutSavingAsync().ConfigureAwait(true);
            if (snapshot == null)
            {
                return null;
            }
            return await SaveDesktopSnapshotToProfileAsync(snapshot, profile, capturePreview, showDesktop).ConfigureAwait(true);
        }

        /// <summary>把已经捕获好的快照存进指定命名布局（编辑布局“完成”时用，避免二次扫描）。</summary>
        public async Task<DesktopTopologyLayout> SaveDesktopSnapshotToProfileAsync(
            DesktopShellSnapshot snapshot,
            DesktopTopologyLayout profile,
            bool capturePreview,
            bool showDesktop)
        {
            ThrowIfDisposed();
            if (snapshot == null || profile == null || profile.IsFolder)
            {
                return null;
            }
            await Task.Yield();
            DesktopTopologyLayout saved = Invoke(layoutCoordinator, "SaveSnapshotAs", snapshot, profile.Id, profile.Name) as DesktopTopologyLayout;
            if (saved != null)
            {
                // 写“布局”文件夹和 4K 桌面截图可能要一两秒，放到后台线程，
                // 否则第一次点“保存图标布局”时 UI 线程会被截屏卡住。
                IList<DesktopTopologyLayout> allLayouts = GetDesktopLayouts();
                await Task.Run(delegate
                {
                    try
                    {
                        DesktopLayoutFolderStore.Save(saved, allLayouts, capturePreview, showDesktop);
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("[图标位置保存] 后台写布局文件夹/截图失败（布局档案已保存）", exception);
                    }
                }).ConfigureAwait(true);
                profile.Name = saved.Name;
                profile.TopologySignature = saved.TopologySignature;
                profile.Topology = saved.Topology;
                profile.Icons = saved.Icons;
                // 保存成功后这份布局里的图标都已经是“明确保存”的；
                // 内存里的卡片对象也要同步清掉自动并集标记，否则立刻重开编辑页
                // 还会把刚摆好的图标当成新图标。
                profile.AutoMergedIdentities = saved.AutoMergedIdentities == null
                    ? new List<string>()
                    : new List<string>(saved.AutoMergedIdentities);
                profile.UpdatedUtc = saved.UpdatedUtc;
            }
            return saved;
        }

        public async Task<DesktopRestoreResult> RestoreDesktopLayoutAsync(DesktopTopologyLayout layout)
        {
            ThrowIfDisposed();
            if (layout == null || layout.IsFolder)
            {
                return null;
            }
            // 【2026-10-24】先确认能开始恢复，再改当前布局；否则双击/并发时会出现
            // “恢复被拒绝，但当前布局已经切过去”的半截状态。
            if (restoringDesktopLayout || IsRestoring || sessionCaptureInProgress != 0)
            {
                StatusText = AppText.T(ShellTextKeys.StatusCannotSwitchLayoutBusy);
                return null;
            }
            SetActiveDesktopLayout(layout.Id);
            return await RestoreDesktopLayoutAsync().ConfigureAwait(true);
        }

        private static string UniqueLayoutName(
            DesktopLayoutCatalog catalog,
            string name,
            string exceptId = null,
            string parentId = null)
        {
            string baseName = string.IsNullOrWhiteSpace(name)
                ? AppText.T(ShellTextKeys.FallbackLayout)
                : name.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (true)
            {
                bool exists = false;
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout item = catalog.Layouts[index];
                    if (item == null
                        || (exceptId != null && string.Equals(item.Id, exceptId, StringComparison.OrdinalIgnoreCase))
                        || !string.Equals(item.ParentId ?? string.Empty, parentId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    return candidate;
                }
                candidate = baseName + "（" + suffix + "）";
                suffix++;
            }
        }

        private static string SerializeDesktopLayout(DesktopTopologyLayout layout)
        {
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(DesktopTopologyLayout));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
                {
                    serializer.WriteObject(stream, layout);
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DesktopTopologyLayout DeserializeDesktopLayout(string payload)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(DesktopTopologyLayout));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload)))
                {
                    return serializer.ReadObject(stream) as DesktopTopologyLayout;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 把一份图标位置快照直接摆回桌面（撤销“恢复图标布局”用）。
        /// 坐标来自快照里的 X/Y（虚拟桌面视图坐标，与 DesktopRestoreItem 同一套）。
        /// </summary>
        public DesktopRestoreResult ApplyDesktopIconSnapshot(DesktopShellSnapshot snapshot)
        {
            ThrowIfDisposed();
            if (snapshot == null || snapshot.Icons == null || snapshot.Icons.Count == 0)
            {
                return null;
            }
            try
            {
                List<DesktopRestoreItem> items = new List<DesktopRestoreItem>();
                for (int index = 0; index < snapshot.Icons.Count; index++)
                {
                    DesktopIconPlacement placement = snapshot.Icons[index];
                    if (placement == null || string.IsNullOrEmpty(placement.Identity))
                    {
                        continue;
                    }
                    // “新图标”未放置的哨兵不是桌面坐标；撤销恢复时也跳过，
                    // 否则会把它摆到屏幕左上角。
                    if (placement.X < -999000 || placement.Y < -999000)
                    {
                        continue;
                    }
                    DesktopRestoreItem item = new DesktopRestoreItem();
                    item.Identity = placement.Identity;
                    item.DisplayName = placement.DisplayName;
                    item.X = placement.X;
                    item.Y = placement.Y;
                    item.TargetMonitorKey = placement.MonitorKey ?? string.Empty;
                    items.Add(item);
                }
                if (items.Count == 0)
                {
                    return null;
                }
                object iconService = CreateService("WorkspaceRestorer.Desktop.DesktopIconService");
                object value = Invoke(iconService, "RestoreItems", items);
                AppLog.Write("[图标位置保存] 按快照摆回图标位置：提交 " + items.Count + " 项");
                return value as DesktopRestoreResult;
            }
            catch (Exception exception)
            {
                StatusText = AppText.F(ShellTextKeys.StatusUndoLayoutRestoreFailedFormat, exception.Message);
                AppLog.Write("Unable to apply desktop icon snapshot.", exception);
                return null;
            }
        }

        public async Task<DesktopRestoreResult> RestoreDesktopLayoutAsync()
        {            ThrowIfDisposed();
            if (restoringDesktopLayout)
            {
                return null;
            }

            restoringDesktopLayout = true;
            try
            {
                DesktopRestoreResult result = await Task.Run(delegate
                {
                    object value = Invoke(layoutCoordinator, "RestoreBestMatch");
                    if (value == null)
                    {
                        value = Invoke(layoutCoordinator, "RestoreCurrent");
                    }
                    return value as DesktopRestoreResult;
                }).ConfigureAwait(true);
                StatusText = DesktopLayoutRestoreText.ForStatus(result);
                return result;
            }
            catch (Exception exception)
            {
                StatusText = DesktopLayoutRestoreText.ForFailure(exception.Message);
                AppLog.Write("Unable to restore desktop layout.", exception);
                return null;
            }
            finally
            {
                restoringDesktopLayout = false;
            }
        }

    }
}
