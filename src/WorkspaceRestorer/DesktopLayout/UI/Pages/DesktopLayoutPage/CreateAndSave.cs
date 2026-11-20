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
        /// <summary>右键/空白处“新建布局文件夹…”：总是问名字，默认电脑A。</summary>
        private void CreateLayoutFolderInteractive()
        {
            string name = WorkspaceRestorer.UI.Shared.NamePromptDialog.Ask(
                Window.GetWindow(this),
                AppText.T(DesktopLayoutTextKeys.NewFolderTitle),
                AppText.T(DesktopLayoutTextKeys.NewFolderPrompt),
                AppText.T(DesktopLayoutTextKeys.NewFolderDefault));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            CreateLayoutFolderCore(name.Trim());
        }

        private void CreateLayoutFolderCore(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            DesktopTopologyLayout parent = FindLayoutById(selectedLayoutFolderId);
            if (parent != null && !parent.IsFolder)
            {
                parent = null;
            }
            DesktopTopologyLayout created = controller.CreateDesktopLayoutFolder(name, parent);
            if (created == null)
            {
                return;
            }
            selectedLayoutFolderId = created.Id;
            expandedLayoutSignatures.Add(created.Id ?? string.Empty);
            RefreshDesktopLayoutCards();
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.StatusFolderCreatedFormat, created.Name);
        }

        private void CreateLayoutInFolder(DesktopTopologyLayout folder, bool isFolder)
        {
            if (folder == null || !folder.IsFolder)
            {
                return;
            }
            string title = isFolder
                ? AppText.T(DesktopLayoutTextKeys.ChildTitleFolder)
                : AppText.T(DesktopLayoutTextKeys.ChildTitleLayout);
            string name = WorkspaceRestorer.UI.Shared.NamePromptDialog.Ask(
                Window.GetWindow(this),
                title,
                AppText.F(
                    DesktopLayoutTextKeys.ChildPromptFormat,
                    folder.Name ?? AppText.T(DesktopLayoutTextKeys.FolderFallback)),
                isFolder
                    ? AppText.T(DesktopLayoutTextKeys.ChildOkFolder)
                    : AppText.T(DesktopLayoutTextKeys.ChildOkLayout));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            DesktopTopologyLayout created = isFolder
                ? controller.CreateDesktopLayoutFolder(name, folder)
                : controller.CreateDesktopLayout(name, folder, false);
            if (created == null)
            {
                return;
            }
            selectedLayoutFolderId = isFolder ? created.Id : folder.Id;
            expandedLayoutSignatures.Add(folder.Id ?? string.Empty);
            if (isFolder)
            {
                expandedLayoutSignatures.Add(created.Id ?? string.Empty);
            }
            RefreshDesktopLayoutCards();
            statusText.Text = AppText.F(
                isFolder
                    ? DesktopLayoutTextKeys.StatusChildFolderCreatedFormat
                    : DesktopLayoutTextKeys.StatusChildLayoutCreatedFormat,
                created.Name);
        }

        private DesktopTopologyLayout FindLayoutById(string id)
        {
            return FindLayoutByIdIn(controller.GetDesktopLayouts(), id);
        }

        private static DesktopTopologyLayout FindLayoutByIdIn(IList<DesktopTopologyLayout> layouts, string id)
        {
            if (layouts == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            for (int index = 0; index < layouts.Count; index++)
            {
                if (layouts[index] != null
                    && string.Equals(layouts[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return layouts[index];
                }
            }
            return null;
        }

        private void CreateLayoutFromNameBox()
        {
            // 名字来自 设置 → 图标位置保存 → 新图标布局名（没自定义时用默认名）。
            string name = GetNewLayoutName();
            DesktopTopologyLayout parent = FindLayoutById(selectedLayoutFolderId);
            DesktopTopologyLayout created = controller.CreateDesktopLayout(name, parent, false);
            if (created == null)
            {
                return;
            }
            if (parent != null)
            {
                expandedLayoutSignatures.Add(parent.Id ?? string.Empty);
            }
            // 把焦点交给列表，方便接着按 Ctrl+Z。
            if (layoutCardsScroll != null)
            {
                layoutCardsScroll.Focus();
            }
            RefreshDesktopLayoutCards();
            statusText.Text = AppText.F(DesktopLayoutTextKeys.StatusLayoutCreatedFormat, created.Name);
        }

        internal bool CanSaveActiveLayoutFromTray()
        {
            if (layoutSaveInProgress
                || layoutRestoreInProgress
                || controller.IsRestoring
                || controller.IsRestoringDesktopLayout)
            {
                return false;
            }
            DesktopTopologyLayout active = FindActiveLayoutForTray();
            return active != null && FindOpenLayoutEditor(active) == null;
        }

        internal void SaveActiveLayoutFromTray()
        {
            DesktopTopologyLayout active = FindActiveLayoutForTray();
            if (active == null)
            {
                return;
            }
            SaveLayoutProfileAsync(active);
        }

        private DesktopTopologyLayout FindActiveLayoutForTray()
        {
            string activeId = controller.ActiveDesktopLayoutId;
            if (string.IsNullOrWhiteSpace(activeId))
            {
                return null;
            }
            DesktopTopologyLayout active = FindLayoutById(activeId);
            return active != null && !active.IsFolder ? active : null;
        }

        private async void SaveLayoutProfileAsync(DesktopTopologyLayout profile)
        {
            if (layoutSaveInProgress || profile == null)
            {
                return;
            }
            if (layoutRestoreInProgress || controller.IsRestoringDesktopLayout)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusRestoringBeforeSave);
                return;
            }
            // 【2026-10-24】同一份布局的编辑页还开着时，不允许主页面把当前桌面存进这份布局：
            // 编辑页之后会用打开时的旧快照保存，覆盖掉这里刚存的新内容。与改名/删除保持同一口径。
            DesktopLayoutEditorWindow openEditor = FindOpenLayoutEditor(profile);
            if (openEditor != null)
            {
                openEditor.Activate();
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorOpenSaveBlocked);
                return;
            }
            layoutSaveInProgress = true;
            try
            {
                byte[] archiveBefore = ReadDesktopLayoutArchiveBytes();
                await SaveLayoutProfileCoreAsync(profile, archiveBefore);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(DesktopLayoutTextKeys.SaveFailedFormat, exception.Message);
            }
            finally
            {
                layoutSaveInProgress = false;
            }
        }

        private async Task<bool> SaveLayoutProfileCoreAsync(DesktopTopologyLayout profile, byte[] archiveBefore)
        {
            if (profile == null)
            {
                return false;
            }
            controller.SetActiveDesktopLayout(profile.Id);
            DesktopTopologyLayout saved = await controller.SaveDesktopLayoutToProfileAsync(profile, true, !IsLayoutTestMode());
            if (saved == null)
            {
                return false;
            }
            RefreshLayoutSummary();
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.StatusSavedFormat,
                saved.Name,
                saved.Icons == null ? 0 : saved.Icons.Count);
            ShowLayoutSavedBanner(saved);
            try
            {
                if (LayoutSaved != null)
                {
                    LayoutSaved(saved);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 布局保存后的托盘通知失败。", exception);
            }
            PushDesktopLayoutSaveUndo(archiveBefore, saved);
            return true;
        }

        /// <summary>
        /// 和「启动与恢复」的“会话已保存”、自启动方案的保存提示一致：
        /// 在项目页面中间弹出一次绿色完成提示，用户不用去底部状态栏找。
        /// </summary>
        private void ShowLayoutSavedBanner(DesktopTopologyLayout saved)
        {
            if (saved == null)
            {
                return;
            }
            int count = saved.Icons == null ? 0 : saved.Icons.Count;
            ShowBanner(
                AppText.F(
                    DesktopLayoutTextKeys.NotificationSavedTitleFormat,
                    saved.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                    count),
                AppTheme.SuccessSurface,
                AppTheme.Positive);
        }

        private async void SaveDesktopLayoutAsync()
        {
            if (layoutSaveInProgress)
            {
                return;
            }
            if (layoutRestoreInProgress || controller.IsRestoringDesktopLayout)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusRestoringBeforeSave);
                return;
            }
            layoutSaveInProgress = true;
            try
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusSaving);
                // 名字来自 设置 → 图标位置保存 → 新图标布局名（没自定义时用默认名）。
                string name = GetNewLayoutName();
                // 顶部“保存图标布局”和「启动与恢复」的“保存当前方案”一致：
                // 用设置里的名字新建一份，不覆盖当前选中的那一份。
                // 如果当前选中了布局文件夹，新布局直接放进这个文件夹。
                byte[] archiveBefore = ReadDesktopLayoutArchiveBytes();
                DesktopTopologyLayout parent = FindLayoutById(selectedLayoutFolderId);
                DesktopTopologyLayout created = controller.CreateDesktopLayout(name, parent, false);
                if (created == null)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusCannotCreate);
                    return;
                }
                if (parent != null)
                {
                    expandedLayoutSignatures.Add(parent.Id ?? string.Empty);
                }
                bool saved = await SaveLayoutProfileCoreAsync(created, archiveBefore);
                if (saved)
                {
                    // 保存后只更新状态，不再动设置里的命名框。
                    statusText.Text = AppText.F(DesktopLayoutTextKeys.StatusSavedNewFormat, created.Name);
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(DesktopLayoutTextKeys.SaveFailedFormat, exception.Message);
            }
            finally
            {
                layoutSaveInProgress = false;
            }
        }

        private async void RestoreLayoutProfileAsync(DesktopTopologyLayout profile)
        {
            if (profile == null)
            {
                return;
            }
            await RestoreDesktopLayoutCoreAsync(profile);
        }

        /// <summary>
        /// 图标位置恢复的唯一入口（顶部“立即恢复”和卡片“恢复此布局”都走这里）：
        /// 先确认没有别的保存/恢复在跑，再截“恢复前快照”，恢复成功后才记撤销。
        /// </summary>
        private async Task RestoreDesktopLayoutCoreAsync(DesktopTopologyLayout profile)
        {
            if (layoutRestoreInProgress)
            {
                return;
            }
            if (layoutSaveInProgress)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusSavingBeforeRestore);
                return;
            }
            if (controller.IsRestoring || controller.IsRestoringDesktopLayout)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusRestoringNoRepeat);
                return;
            }
            layoutRestoreInProgress = true;
            try
            {
                string name = profile == null
                ? AppText.T(DesktopLayoutTextKeys.CurrentLayoutFallback)
                : (profile.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback));
                statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusRecordingForUndo);
                DesktopShellSnapshot beforeRestore = await controller.CaptureDesktopSnapshotWithoutSavingAsync();
                if (beforeRestore == null)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.StatusCannotReadIcons);
                    return;
                }
                statusText.Text = AppText.F(DesktopLayoutTextKeys.StatusRestoringFormat, name);
                DesktopRestoreResult result = profile == null
                    ? await controller.RestoreDesktopLayoutAsync()
                    : await controller.RestoreDesktopLayoutAsync(profile);
                RefreshLayoutSummary();
                if (result != null && result.Applied)
                {
                    PushDesktopLayoutRestoreUndo(beforeRestore);
                }
                else if (result == null)
                {
                    statusText.Text = DesktopLayoutRestoreText.ForNoExecution();
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(DesktopLayoutTextKeys.RestoreFailedFormat, exception.Message);
            }
            finally
            {
                layoutRestoreInProgress = false;
            }
        }

    }
}
