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
        private void MoveLayout(DesktopTopologyLayout layout, int delta, bool toFront, bool toBack = false)
        {
            if (layout == null)
            {
                return;
            }
            // 一次移动只加载一次档案：找根级顺序、移动、重建卡片都用这一份。
            DesktopLayoutCatalog catalog = controller.LoadLayoutCatalog();
            IList<DesktopTopologyLayout> all = controller.GetDesktopLayouts(catalog);
            IList<DesktopTopologyLayout> roots = PlanFolderTree.Roots(all);
            int oldIndex = -1;
            for (int index = 0; index < roots.Count; index++)
            {
                if (roots[index] != null
                    && string.Equals(roots[index].Id, layout.Id, StringComparison.OrdinalIgnoreCase))
                {
                    oldIndex = index;
                    break;
                }
            }
            if (oldIndex < 0)
            {
                // 子布局的边缘落点 = 移到根目录。
                MoveLayoutToRootWithUndo(layout, toFront ? 0 : int.MaxValue, catalog, all);
                return;
            }
            int newIndex = toFront ? 0 : (toBack ? roots.Count - 1 : oldIndex + delta);
            newIndex = Math.Max(0, Math.Min(roots.Count - 1, newIndex));
            if (newIndex == oldIndex)
            {
                statusText.Text = AppText.F(
                DesktopLayoutTextKeys.StatusAlreadyAtEdge,
                AppText.T(toFront || delta < 0
                    ? CommonTextKeys.PositionFront
                    : CommonTextKeys.PositionBack));
                return;
            }
            MoveLayoutToRootWithUndo(layout, newIndex, catalog, all);
        }

        private void MoveLayoutToRootWithUndo(
            DesktopTopologyLayout layout,
            int index,
            DesktopLayoutCatalog catalog,
            IList<DesktopTopologyLayout> all)
        {
            string oldParentId = layout.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(all, layout);
            if (!controller.MoveDesktopLayoutToPlacement(layout, null, index, catalog))
            {
                return;
            }
            DesktopTopologyLayout movedNow = FindLayoutByIdIn(all, layout.Id) ?? layout;
            string newParentId = movedNow.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(all, movedNow);
            RefreshDesktopLayoutCards(catalog);
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.StatusMovedFormat,
                layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback));
            PushUndo(
                AppText.T(DesktopLayoutTextKeys.UndoMove),
                delegate
                {
                    DesktopLayoutCatalog undoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(layout, oldParentId, oldIndex, undoCatalog);
                    RefreshDesktopLayoutCards(undoCatalog);
                },
                delegate
                {
                    DesktopLayoutCatalog redoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(layout, newParentId, newIndex, redoCatalog);
                    RefreshDesktopLayoutCards(redoCatalog);
                });
        }
        private void DeleteLayoutProfile(DesktopTopologyLayout profile)
        {
            if (profile == null)
            {
                return;
            }
            DesktopLayoutEditorWindow openEditor = FindOpenLayoutEditor(profile);
            if (openEditor != null)
            {
                openEditor.Activate();
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorOpenDeleteBlocked);
                return;
            }
            bool folder = profile.IsFolder;
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(folder
                    ? DesktopLayoutTextKeys.DeleteFolderTitle
                    : DesktopLayoutTextKeys.DeleteLayoutTitle),
                folder
                    ? AppText.F(
                        DesktopLayoutTextKeys.DeleteFolderPromptFormat,
                        profile.Name ?? AppText.T(DesktopLayoutTextKeys.FolderFallback))
                    : AppText.F(
                        DesktopLayoutTextKeys.DeleteLayoutPromptFormat,
                        profile.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback)),
                folder
                    ? AppText.T(DesktopLayoutTextKeys.DeleteFolderHelp)
                    : AppText.T(DesktopLayoutTextKeys.DeleteLayoutHelp)))
            {
                return;
            }
            if (controller.DeleteDesktopLayout(profile))
            {
                if (string.Equals(selectedLayoutFolderId, profile.Id, StringComparison.OrdinalIgnoreCase))
                {
                    selectedLayoutFolderId = profile.ParentId;
                }
                RefreshLayoutSummary();
            }
        }

        private void AppendCurrentTimeToLayoutName()
        {
            DesktopTopologyLayout active = controller.GetDesktopLayouts()
                .FirstOrDefault(delegate(DesktopTopologyLayout item)
                {
                    return item != null && string.Equals(item.Id, controller.ActiveDesktopLayoutId, StringComparison.OrdinalIgnoreCase);
                });
            if (active == null)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.SelectLayoutFirst);
                return;
            }
            string name = WorkspaceRestorer.Core.TimeText.AppendStampNow(
                active.Name,
                AppText.T(CommonTextKeys.LayoutFallback),
                DateTime.Now,
                AppText.CurrentLanguage.Tag);
            RenameLayoutWithUndo(active, name);
        }

        private void SearchDesktopLayouts()
        {
            ShowSearchDialog(
                AppText.T(DesktopLayoutTextKeys.SearchTitle),
                AppText.T(DesktopLayoutTextKeys.SearchDescription),
                delegate(string keyword, bool includeTime)
                {
                    List<SearchHit> hits = new List<SearchHit>();
                    IList<DesktopTopologyLayout> layouts = controller.GetDesktopLayouts();
                    for (int index = 0; index < layouts.Count; index++)
                    {
                        DesktopTopologyLayout layout = layouts[index];
                        if (layout == null)
                        {
                            continue;
                        }
                        string name = string.IsNullOrWhiteSpace(layout.Name)
                    ? AppText.T(DesktopLayoutTextKeys.LayoutFallback)
                    : layout.Name;
                        string saved = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                            layout.UpdatedUtc, AppText.CurrentLanguageTag);
                        Shared.SearchTarget target = new Shared.SearchTarget
                        {
                            Name = name,
                            TimeText = saved,
                            SavedUtc = layout.UpdatedUtc,
                            UpdatedUtc = layout.UpdatedUtc,
                            CreatedUtc = layout.CreatedUtc
                        };
                        if (!Shared.SearchMatcher.Matches(target, keyword, includeTime))
                        {
                            continue;
                        }
                        List<SearchTextRange> nameRanges = new List<SearchTextRange>();
                        List<SearchTextRange> timeRanges = new List<SearchTextRange>();
                        Shared.SearchMatcher.Highlight(target, keyword, includeTime, nameRanges, timeRanges);
                        int iconCount = layout.Icons == null ? 0 : layout.Icons.Count;
                        string path = PlanFolderTree.PathOf(layouts, layout, " / ");
                        string prefix = (string.IsNullOrWhiteSpace(path) ? name : path)
                + " · " + (layout.IsFolder
                    ? AppText.T(DesktopLayoutTextKeys.SearchDetailFolder)
                    : AppText.F(
                        DesktopLayoutTextKeys.SearchDetailIconsFormat,
                        iconCount,
                        DesktopLayoutFolderStore.DescribeDisplay(layout)));
                string timeLabel = AppText.T(DesktopLayoutTextKeys.SearchLastSavedFormat);
                        SearchHit hit = new SearchHit();
                        hit.Title = name;
                        hit.Detail = saved.Length == 0 ? prefix : prefix + timeLabel + saved;
                        hit.TitleSegments = BuildHighlightSegments(hit.Title, nameRanges, 0);
                        hit.DetailSegments = saved.Length == 0
                            ? BuildHighlightSegments(hit.Detail, null, 0)
                            : BuildHighlightSegments(hit.Detail, timeRanges, -(prefix.Length + timeLabel.Length));
                        hit.Tag = layout;
                        hits.Add(hit);
                    }
                    return hits;
                },
                delegate(SearchHit hit)
                {
                    DesktopTopologyLayout layout = hit == null ? null : hit.Tag as DesktopTopologyLayout;
                    if (layout == null)
                    {
                        return;
                    }
                    IList<DesktopTopologyLayout> ancestors = PlanFolderTree.Ancestors(
                        controller.GetDesktopLayouts(), layout);
                    for (int index = 0; index < ancestors.Count; index++)
                    {
                        if (ancestors[index] != null)
                        {
                            expandedLayoutSignatures.Add(ancestors[index].Id ?? string.Empty);
                        }
                    }
                    if (layout.IsFolder)
                    {
                        selectedLayoutFolderId = layout.Id;
                    }
                    else
                    {
                        selectedLayoutFolderId = layout.ParentId;
                        controller.SetActiveDesktopLayout(layout.Id);
                    }
                    expandedLayoutSignatures.Add(layout.Id ?? string.Empty);
                    RefreshDesktopLayoutCards();
                },
                AppText.T(DesktopLayoutTextKeys.SearchEmpty));
        }

        private async void OnDisplayChangeTimerTick(object sender, EventArgs e)
        {
            displayChangeTimer.Stop();
            if (layoutEditInProgress || allowClose || !controller.Settings.LayoutAutoRestore)
            {
                return;
            }
            int generation = ++layoutAutoRestoreGeneration;
            try
            {
                int seconds = Math.Max(0, Math.Min(
                    ApplicationController.ControllerSettings.InitialDelayMaximumSeconds,
                    controller.Settings.LayoutInitialDelaySeconds));
                if (seconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds));
                }
                if (generation != layoutAutoRestoreGeneration
                    || layoutEditInProgress
                    || allowClose
                    || !controller.Settings.LayoutAutoRestore)
                {
                    return;
                }
                await controller.RestoreDesktopLayoutAsync();
                RefreshLayoutSummary();
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(DesktopLayoutTextKeys.RestoreDesktopFailedFormat, exception.Message);
            }
        }
    }
}
