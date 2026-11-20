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
using WorkspaceRestorer.Core.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Surface;
namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        private WorkspaceRestorer.UI.Cards.Drag.DragMoveSession LayoutCardDragEngine()
        {
            if (layoutCardDragSession != null)
            {
                return layoutCardDragSession;
            }
            WorkspaceRestorer.UI.Cards.Drag.DragMoveHost host =
                new WorkspaceRestorer.UI.Cards.Drag.DragMoveHost();
            host.Window = this;
            host.Layer = dragInsertionLayer;
            host.Ghost = dragGhost;
            host.List = layoutCardsScroll;
            host.TopPad = layoutTopPad;
            host.BottomPad = layoutBottomPad;
            host.CardHost = layoutCardsHost;
            host.Mode = WorkspaceRestorer.UI.Cards.Drag.DragMoveMode.CardZones;
            // 普通卡片拖到文件夹中间 = 放进文件夹；文件夹也可以再套子文件夹。
            host.AllowCardMerge = true;
            host.CanMergeCard = delegate(string id)
            {
                return !string.IsNullOrWhiteSpace(id) && layoutMergeTargetIds.Contains(id);
            };
            host.LogTag = "[布局卡片拖动]";
            host.DescribeMoving = delegate
            {
                if (layoutCardDrag == null)
                {
                    return AppText.T(DesktopLayoutTextKeys.LayoutFallback);
                }
                return layoutCardDrag.Name
                    ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback);
            };
            host.GetCardSlots = CaptureLayoutCardSlots;
            // 目标框继续框整张卡（保持图标位置保存现有观感）；槽位已改用标题条。
            host.FindCardElement = delegate(string id)
            {
                return layoutCardVisuals.FindBlock(id);
            };
            host.FindCardShiftElement = delegate(string id)
            {
                return layoutCardVisuals.FindBlock(id);
            };
            host.DescribeCard = delegate(string id)
            {
                // 拖动时鼠标每移动一下都会问“这张卡叫什么”。这里必须查内存缓存，
                // 不能再 GetDesktopLayouts()（那会完整读档案，旧版还会 Sync 写盘）。
                string name;
                if (!string.IsNullOrWhiteSpace(id)
                    && layoutCardNames.TryGetValue(id, out name)
                    && !string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
                return "?";
            };
            host.IsDropPointForbidden = IsLayoutCardDropForbidden;
            host.DescribeMergeHint = delegate(string moving, string target)
            {
                return AppText.F(
                    DesktopLayoutTextKeys.DragMergeHintFormat, moving, target);
            };
            host.ApplyCardDrop = ApplyLayoutCardDrop;
            host.ApplyEdgeDrop = delegate(int direction)
            {
                if (layoutCardDrag == null)
                {
                    return;
                }
                MoveLayout(layoutCardDrag, 0, direction < 0, direction > 0);
            };
            host.OnFinished = delegate
            {
                layoutCardDrag = null;
                layoutCardDragJustEnded = true;
                layoutCardDragEndedUtc = DateTime.UtcNow;
                // 拖动结束恢复 hover；否则源卡片会一直保留拖动开始时的灰色底。
                SetLayoutCardHoverEnabled(true);
            };
            host.ScrollSpeedFactor = delegate { return controller.Settings.DragScrollSpeed; };
            layoutCardDragSession = new WorkspaceRestorer.UI.Cards.Drag.DragMoveSession(host);
            return layoutCardDragSession;
        }

        private IList<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> CaptureLayoutCardSlots()
        {
            return CaptureLayoutCardSlots(controller.GetDesktopLayouts());
        }

        /// <summary>
        /// 用调用方已经加载好的布局清单收集拖动槽位：同一次拖放里不再重复读档案。
        /// </summary>
        private IList<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> CaptureLayoutCardSlots(
            IList<DesktopTopologyLayout> all)
        {
            List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> slots =
                new List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot>();
            layoutCardForbiddenRects.Clear();
            if (layoutCardsHost == null)
            {
                return slots;
            }

            WorkspaceRestorer.UI.Cards.Drag.DragCardSlotCapture capture =
                WorkspaceRestorer.UI.Cards.Drag.DragCardSlotCollector.Capture(
                    layoutCardVisuals,
                    layoutCardsHost,
                    this,
                    layoutCardDrag == null ? null : layoutCardDrag.Id,
                    delegate(string id)
                    {
                        return layoutCardDrag != null
                            && PlanFolderTree.IsDescendantId(all, id, layoutCardDrag.Id);
                    },
                    true);

            layoutCardForbiddenRects.AddRange(capture.ForbiddenRects);
            return capture.Slots;
        }

        /// <summary>指针是否落在被拖布局自己的后代卡片上（禁止落点）。坐标是窗口坐标。</summary>
        private bool IsLayoutCardDropForbidden(Point pointer)
        {
            for (int index = 0; index < layoutCardForbiddenRects.Count; index++)
            {
                if (layoutCardForbiddenRects[index].Contains(pointer))
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyLayoutCardDrop(WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropDecision decision)
        {
            if (layoutCardDrag == null || decision == null)
            {
                return;
            }
            // 一次松手只加载一次档案：清单、落点槽位、移动、重建卡片都用这一份。
            DesktopLayoutCatalog catalog;
            try
            {
                catalog = controller.LoadLayoutCatalog();
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 读取布局清单失败", exception);
                return;
            }
            List<DesktopTopologyLayout> all = controller.GetDesktopLayouts(catalog)
                .Where(delegate(DesktopTopologyLayout item) { return item != null; })
                .ToList();
            DesktopTopologyLayout moving = all.FirstOrDefault(delegate(DesktopTopologyLayout item)
            {
                return string.Equals(item.Id, layoutCardDrag.Id, StringComparison.OrdinalIgnoreCase);
            });
            DesktopTopologyLayout target = string.IsNullOrWhiteSpace(decision.TargetId)
                ? null
                : all.FirstOrDefault(delegate(DesktopTopologyLayout item)
                {
                    return string.Equals(item.Id, decision.TargetId, StringComparison.OrdinalIgnoreCase);
                });
            if (moving == null)
            {
                return;
            }
            if (decision.Kind == WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropKind.Merge)
            {
                if (target == null)
                {
                    return;
                }
                if (!target.IsFolder)
                {
                    statusText.Text = AppText.T(DesktopLayoutTextKeys.DragNotFolder);
                    return;
                }
                MoveLayoutIntoWithUndo(moving, target, catalog, all);
                return;
            }
            if (decision.Kind != WorkspaceRestorer.Core.Cards.Drag.DragMoveCardDropKind.Insert || target == null)
            {
                return;
            }
            List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot> slots = new List<WorkspaceRestorer.Core.Cards.Drag.DragMoveCardSlot>(CaptureLayoutCardSlots(all));
            int targetSlot = -1;
            for (int index = 0; index < slots.Count; index++)
            {
                if (string.Equals(slots[index].Id, target.Id, StringComparison.OrdinalIgnoreCase))
                {
                    targetSlot = index;
                    break;
                }
            }
            bool after = targetSlot >= 0 && decision.Index > targetSlot;
            MoveLayoutRelativeWithUndo(moving, target, after, catalog, all);
        }

        /// <summary>右键菜单“移入布局文件夹”用：自己加载一次档案，再交给同一套移动流程。</summary>
        private void MoveLayoutIntoWithUndo(DesktopTopologyLayout moving, DesktopTopologyLayout folder)
        {
            DesktopLayoutCatalog catalog = controller.LoadLayoutCatalog();
            MoveLayoutIntoWithUndo(moving, folder, catalog, controller.GetDesktopLayouts(catalog));
        }

        private void MoveLayoutIntoWithUndo(
            DesktopTopologyLayout moving,
            DesktopTopologyLayout folder,
            DesktopLayoutCatalog catalog,
            IList<DesktopTopologyLayout> all)
        {
            string oldParentId = moving.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(all, moving);
            if (!controller.MoveDesktopLayoutInto(moving, folder, catalog))
            {
                return;
            }
            // 同一份 catalog：控制器原地改的就是 all 里的那个对象，这里不再重新加载档案。
            DesktopTopologyLayout movedNow = FindLayoutByIdIn(all, moving.Id) ?? moving;
            string newParentId = movedNow.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(all, movedNow);
            expandedLayoutSignatures.Add(folder.Id ?? string.Empty);
            RefreshDesktopLayoutCards(catalog);
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.DragMovedIntoFolderFormat,
                moving.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                folder.Name ?? AppText.T(DesktopLayoutTextKeys.FolderFallback));
            PushUndo(
                AppText.F(
                    DesktopLayoutTextKeys.UndoMoveIntoFolderFormat,
                    moving.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback)),
                delegate
                {
                    DesktopLayoutCatalog undoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(moving, oldParentId, oldIndex, undoCatalog);
                    RefreshDesktopLayoutCards(undoCatalog);
                },
                delegate
                {
                    DesktopLayoutCatalog redoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(moving, newParentId, newIndex, redoCatalog);
                    RefreshDesktopLayoutCards(redoCatalog);
                });
        }

        private void MoveLayoutRelativeWithUndo(
            DesktopTopologyLayout moving,
            DesktopTopologyLayout target,
            bool after,
            DesktopLayoutCatalog catalog,
            IList<DesktopTopologyLayout> all)
        {
            string oldParentId = moving.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(all, moving);
            if (!controller.MoveDesktopLayoutRelative(moving, target, after, catalog))
            {
                return;
            }
            DesktopTopologyLayout movedNow = FindLayoutByIdIn(all, moving.Id) ?? moving;
            string newParentId = movedNow.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(all, movedNow);
            RefreshDesktopLayoutCards(catalog);
            statusText.Text = AppText.F(
                DesktopLayoutTextKeys.StatusMovedFormat,
                moving.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback));
            PushUndo(
                AppText.T(DesktopLayoutTextKeys.UndoDragMove),
                delegate
                {
                    DesktopLayoutCatalog undoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(moving, oldParentId, oldIndex, undoCatalog);
                    RefreshDesktopLayoutCards(undoCatalog);
                },
                delegate
                {
                    DesktopLayoutCatalog redoCatalog = controller.LoadLayoutCatalog();
                    controller.MoveDesktopLayoutToPlacement(moving, newParentId, newIndex, redoCatalog);
                    RefreshDesktopLayoutCards(redoCatalog);
                });
        }

        /// <summary>卡片标题区按下：满足“拖动前缀 + 鼠标键”才武装整卡拖动（≡ 手柄仍左键直接拖）。</summary>
        private void ArmLayoutCardSurfaceDrag(
            DesktopTopologyLayout layout,
            MouseButton button,
            MouseButtonEventArgs args)
        {
            if (layout == null || args == null
                || IsInsideButton(args.OriginalSource as DependencyObject)
                || !DragGestureSettings.MatchesMouseButton(button)
                || !DragGestureSettings.MatchesPrefix())
            {
                return;
            }
            layoutCardDragMouseButton = button;
            layoutCardDragPress = args.GetPosition(this);
            layoutCardDragCandidate = layout;
        }

        /// <summary>整卡拖动：超过系统阈值后交给公共引擎。</summary>
        private void ContinueLayoutCardSurfaceDrag(DesktopTopologyLayout layout, MouseEventArgs args)
        {
            if (layout == null || args == null
                || layoutCardDragCandidate == null
                || !object.ReferenceEquals(layoutCardDragCandidate, layout))
            {
                return;
            }
            if (!DragGestureSettings.IsMouseButtonPressed(args, layoutCardDragMouseButton))
            {
                layoutCardDragCandidate = null;
                return;
            }
            Point now = args.GetPosition(this);
            if (Math.Abs(now.X - layoutCardDragPress.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - layoutCardDragPress.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            layoutCardDragCandidate = null;
            BeginLayoutCardDrag(layout, layoutCardDragMouseButton);
        }

        private void BeginLayoutCardDrag(DesktopTopologyLayout layout, MouseButton mouseButton)
        {
            if (layout == null)
            {
                return;
            }
            Border card = layoutCardVisuals.FindBlock(layout.Id ?? string.Empty) as Border;
            if (card == null)
            {
                return;
            }
            // 浮动方框用标题条起拖（和「启动与恢复」的卡片手感一致，宽度不会被展开预览撑大）；
            // 落点几何/“腾开空间”仍然用外层 card，展开时整张卡一起让位。
            Border header = layoutCardVisuals.FindHeader(layout.Id ?? string.Empty) as Border;
            Border source = header == null ? card : header;
            layoutCardDrag = layout;
            layoutCardDragJustEnded = false;
            layoutCardDragEndedUtc = DateTime.MinValue;
            layoutMergeTargetIds.Clear();
            // 起拖时用刷新阶段缓存的文件夹 Id，不再为了“谁可并入”读一遍布局档案。
            foreach (string folderId in layoutFolderIds)
            {
                if (!string.IsNullOrWhiteSpace(folderId))
                {
                    layoutMergeTargetIds.Add(folderId);
                }
            }
            if (!LayoutCardDragEngine().Start(
                source,
                layout.Name ?? AppText.T(DesktopLayoutTextKeys.LayoutFallback),
                mouseButton))
            {
                layoutCardDrag = null;
                return;
            }
            SetLayoutCardHoverEnabled(false);
        }

        /// <summary>
        /// 拖动期间关掉所有布局标题条的 hover 变色，并立即把当前已亮起的卡片恢复成 active/inactive 底色。
        /// 结束后重新打开，下一次鼠标移入仍按正常规则高亮。
        /// </summary>
        private void SetLayoutCardHoverEnabled(bool enabled)
        {
            // 用刷新时缓存的当前布局 Id：拖动开始/结束各读一遍档案没有意义（一次拖放会多读两份）。
            string activeId = layoutActiveId;
            foreach (WorkspaceRestorer.UI.Cards.Drag.DragCardElement element in layoutCardVisuals.Items)
            {
                Border header = element.Header as Border;
                if (header == null)
                {
                    continue;
                }
                WorkspaceRestorer.UI.Cards.Surface.CardSurface.SetHoverEnabled(header, enabled);
                WorkspaceRestorer.UI.Cards.Surface.CardSurface.SetActive(
                    header,
                    string.Equals(element.Id, activeId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static bool IsInsideButton(DependencyObject source)
        {
            DependencyObject node = source;
            while (node != null)
            {
                if (node is Button)
                {
                    return true;
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        private static BitmapImage TryLoadLayoutPreview(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }
                BitmapImage image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 读取桌面预览图失败：" + path, exception);
                return null;
            }
        }

    }
}
