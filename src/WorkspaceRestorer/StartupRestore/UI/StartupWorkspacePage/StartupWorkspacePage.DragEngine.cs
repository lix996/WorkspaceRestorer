using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.Core.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Surface;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「启动与恢复」与公共拖动引擎 `DragMoveSession` 的接线。
    ///
    /// 用户 2026-11-01 要求：不要再维护“一套启动私有流程 + 一套公共流程”。
    /// 起拖、跟手、上下放置条、自动滚动、插入线、禁止光标、看门狗、松手判定
    /// 全部交给公共 `DragMoveSession`；本文件只保留“启动与恢复的业务语义”：
    ///  · 方案树父子层级、后代替换禁止；
    ///  · 拖到卡片中间＝移入，边缘＝同级换位；
    ///  · 顶部/底部＝根目录最前/最后；
    ///  · 项目行落点、跨方案搬移、撤销。
    /// </summary>
    public sealed partial class MainWindow
    {
        private DragMoveSession startupCardDragSession;
        private DragMoveSession startupItemDragSession;

        /// <summary>方案卡片的标题条/外层整块登记（拖动专用；由 BuildProfileRow 登记）。</summary>
        private readonly DragCardElementRegistry startupCardElements =
            new DragCardElementRegistry();

        /// <summary>卡片拖动时被拖方案整棵子树的矩形（窗口坐标），禁止落点。</summary>
        private readonly List<Rect> startupCardForbiddenRects = new List<Rect>();

        /// <summary>与 `DragMoveCardSlot` 同下标的方案 Id。</summary>
        private readonly List<string> startupCardSlotIds = new List<string>();

        private NativeItemRow startupItemDropRow;
        private bool startupCardDragCancelled;
        /// <summary>本次卡片拖动按下的鼠标键（整卡取设置，≡ 手柄固定左键）。</summary>
        private MouseButton profileDragMouseButton = MouseButton.Left;
        /// <summary>拖动期间临时取消“当前方案”的蓝色描边，避免源卡片和目标框叠成一个框。</summary>
        private Border startupCardDragSourceBorder;

        // ================================================================ 卡片拖动

        /// <summary>按住贴边手柄：只记来源与起点，过阈值后由 `ContinueStartupCardHandleDrag` 起拖。</summary>
        private void BeginProfileDragFromHandle(StartupProfile profile, MouseButtonEventArgs args)
        {
            profileDragSource = profile;
            profileDragMouseButton = MouseButton.Left;   // ≡ 手柄始终左键直接拖
            profileDragStartPoint = args.GetPosition(this);
        }

        /// <summary>
        /// 卡片标题区按下：只有“拖动前缀 + 设置里的鼠标键”同时满足才武装整卡拖动；
        /// 不满足时（例如直接按右键）不抢右键菜单，卡片仍可用 ≡ 手柄拖。
        /// </summary>
        private void ArmStartupCardDrag(StartupProfile profile, MouseButton button, MouseButtonEventArgs args)
        {
            bool armed = args != null
                && DragGestureSettings.MatchesMouseButton(button)
                && DragGestureSettings.MatchesPrefix()
                && !IsActionControl(args.OriginalSource as DependencyObject);
            profileDragSource = armed ? profile : null;
            profileDragMouseButton = armed ? button : MouseButton.Left;
            if (armed)
            {
                profileDragStartPoint = args.GetPosition(this);
            }
        }

        /// <summary>整行按下：与卡片同一套手势判断（前缀 + 鼠标键）。</summary>
        private void ArmStartupItemRowDrag(MouseButton button, MouseButtonEventArgs args)
        {
            nativeItemDragJustEnded = false;
            rowDragArmed = args != null
                && DragGestureSettings.MatchesMouseButton(button)
                && DragGestureSettings.MatchesPrefix()
                && !IsActionControl(args.OriginalSource as DependencyObject);
            if (rowDragArmed)
            {
                rowDragMouseButton = button;
                dragStartPoint = args.GetPosition(this);
                dragSourceItem = null;
                dragActive = false;
            }
        }

        /// <summary>
        /// 把方案移到清单最前（direction = -1）或最后（direction = 1）。
        /// 顶部/底部/列表下方空白共用这一个入口；根级方案也必须真的换位，不能“假成功”。
        /// </summary>
        private void MoveProfileToEdge(StartupProfile profile, int direction)
        {
            if (profile == null || direction == 0)
            {
                return;
            }
            string oldParentId = profile.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, profile);
            if (!controller.MoveProfileToRootEdge(profile, direction))
            {
                statusText.Text = AppText.F(
                    direction < 0
                        ? StartupRestoreTextKeys.CardAlreadyFirstFormat
                        : StartupRestoreTextKeys.CardAlreadyLastFormat,
                    profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
                return;
            }
            string newParentId = profile.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, profile);
            RefreshProfileList();
            PushUndo(
                direction < 0
                    ? AppText.F(
                        StartupRestoreTextKeys.CardUndoMoveFrontFormat,
                        profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback))
                    : AppText.F(
                        StartupRestoreTextKeys.CardUndoMoveBackFormat,
                        profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback)),
                delegate
                {
                    controller.MoveProfileToPlacement(profile, oldParentId, oldIndex);
                    RefreshProfileList();
                },
                delegate
                {
                    controller.MoveProfileToPlacement(profile, newParentId, newIndex);
                    RefreshProfileList();
                });
        }

        private DragMoveSession StartupCardDragEngine()
        {
            if (startupCardDragSession != null)
            {
                return startupCardDragSession;
            }
            DragMoveHost host = new DragMoveHost();
            host.Window = this;
            host.Layer = dragInsertionLayer;
            host.Ghost = dragGhost;
            host.List = profileScrollViewer;
            host.TopPad = profileDropPadTop;
            host.BottomPad = profileDropPad;
            host.CardHost = profileHost;
            host.Mode = DragMoveMode.CardZones;
            host.CardGapMode = DragMoveCardGapMode.StrictNone;
            host.LogTag = "[卡片拖动]";
            host.DescribeMoving = delegate
            {
                string fallback = AppText.T(CommonTextKeys.WorkProfileFallback);
                return profileDragSource == null
                    ? fallback
                    : (profileDragSource.Name ?? fallback);
            };
            host.GetCardSlots = CaptureStartupCardSlots;
            host.FindCardElement = delegate(string id)
            {
                return startupCardElements.FindHeader(id);
            };
            host.FindCardShiftElement = delegate(string id)
            {
                return startupCardElements.FindBlock(id);
            };
            host.DescribeCard = delegate(string id)
            {
                StartupProfile profile = FindStartupProfileById(id);
                return profile == null
                    ? "?"
                    : (profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
            };
            host.DescribeMergeHint = delegate(string moving, string target)
            {
                return AppText.F(StartupRestoreTextKeys.CardMergeHintFormat, target);
            };
            host.IsDropPointForbidden = IsStartupCardDropForbidden;
            host.ApplyCardDrop = ApplyStartupCardDrop;
            host.ApplyEdgeDrop = delegate(int direction)
            {
                if (profileDragSource != null)
                {
                    MoveProfileToEdge(profileDragSource, direction);
                }
            };
            host.ShowHint = ShowStartupDragHint;
            host.ScrollSpeedFactor = delegate { return controller.Settings.DragScrollSpeed; };
            host.OnCancelled = delegate
            {
                startupCardDragCancelled = true;
                suppressProfileSelectOnClickUp = false;
                RestoreStartupCardSourceAppearance();
                profileDragSource = null;
                startupCardForbiddenRects.Clear();
                startupCardSlotIds.Clear();
            };
            host.OnFinished = delegate
            {
                startupCardForbiddenRects.Clear();
                startupCardSlotIds.Clear();
                // 正常松手要吃掉紧随其后的 MouseUp 点击；被取消的那次已经手动清成 false。
                if (!startupCardDragCancelled)
                {
                    suppressProfileSelectOnClickUp = true;
                }
                RestoreStartupCardSourceAppearance();
                profileDragSource = null;
                startupCardDragCancelled = false;
                startupItemDropRow = null;
            };
            startupCardDragSession = new DragMoveSession(host);
            return startupCardDragSession;
        }

        /// <summary>
        /// 超过最小拖动距离后真正起拖。`source` 是卡片头（浮动方框宽度/锚点按它算）。
        /// </summary>
        private bool StartStartupCardDrag(StartupProfile profile, FrameworkElement source)
        {
            if (profile == null || source == null || startupCardDragSession != null && startupCardDragSession.IsActive)
            {
                return false;
            }
            profileDragSource = profile;
            startupCardDragCancelled = false;
            suppressProfileSelectOnClickUp = true;
            CancelPendingProfileSelect();
            if (source is Border)
            {
                startupCardDragSourceBorder = (Border)source;
                CardSurface.SetHoverEnabled(startupCardDragSourceBorder, false);
                // 源卡片如果是“当前方案”，拖它的时候先把蓝色描边收掉：
                // 否则源卡片描边 + 目标高亮框 + 浮动方框会叠成“一个大框套两张卡”。
                CardSurface.SetActive(startupCardDragSourceBorder, false);
            }
            if (!StartupCardDragEngine().Start(
                source,
                profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback),
                profileDragMouseButton))
            {
                RestoreStartupCardSourceAppearance();
                profileDragSource = null;
                suppressProfileSelectOnClickUp = false;
                return false;
            }
            return true;
        }

        private void RestoreStartupCardSourceAppearance()
        {
            Border border = startupCardDragSourceBorder;
            startupCardDragSourceBorder = null;
            if (border == null)
            {
                return;
            }
            CardSurface.SetHoverEnabled(border, true);
            StartupProfile active = controller.ActiveProfile;
            CardSurface.SetActive(border,
                active != null && profileDragSource != null
                && string.Equals(active.Id, profileDragSource.Id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>卡片头/手柄的移动：超过阈值才起拖；起拖后由公共引擎接管。</summary>
        private void ContinueStartupCardDrag(StartupProfile profile, FrameworkElement source, MouseEventArgs args)
        {
            if (profile == null || source == null || args == null)
            {
                return;
            }
            if (startupCardDragSession != null && startupCardDragSession.IsActive)
            {
                return;
            }
            if (!DragGestureSettings.IsMouseButtonPressed(args, profileDragMouseButton)
                || !object.ReferenceEquals(profileDragSource, profile))
            {
                return;
            }
            Point now = args.GetPosition(this);
            if (Math.Abs(now.X - profileDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - profileDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            StartStartupCardDrag(profile, source);
        }

        /// <summary>贴边手柄的移动：手柄本身就是“要拖”，过阈值后用卡片头作为浮动方框来源。</summary>
        private void ContinueStartupCardHandleDrag(StartupProfile profile, MouseEventArgs args)
        {
            if (profile == null || args == null)
            {
                return;
            }
            if (startupCardDragSession != null && startupCardDragSession.IsActive)
            {
                return;
            }
            if (args.LeftButton != MouseButtonState.Pressed
                || !object.ReferenceEquals(profileDragSource, profile))
            {
                return;
            }
            Point now = args.GetPosition(this);
            if (Math.Abs(now.X - profileDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(now.Y - profileDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            Border card = startupCardElements.FindHeader(profile.Id) as Border;
            if (card != null)
            {
                StartStartupCardDrag(profile, card);
            }
        }

        private void FinishStartupCardDrag()
        {
            if (startupCardDragSession != null && startupCardDragSession.IsActive)
            {
                startupCardDragSession.Finish();
            }
        }

        /// <summary>起拖时冻结卡片矩形；Header 算槽位，Block 算禁止区。</summary>
        private IList<DragMoveCardSlot> CaptureStartupCardSlots()
        {
            List<DragMoveCardSlot> slots = new List<DragMoveCardSlot>();
            startupCardForbiddenRects.Clear();
            startupCardSlotIds.Clear();
            if (profileHost == null)
            {
                return slots;
            }

            StartupProfile dragged = profileDragSource;
            DragCardSlotCapture capture = DragCardSlotCollector.Capture(
                startupCardElements,
                profileHost,
                this,
                dragged == null ? null : dragged.Id,
                delegate(string id)
                {
                    return dragged != null
                        && PlanFolderTree.IsDescendantId(controller.StartupProfiles, id, dragged.Id);
                },
                false);

            startupCardForbiddenRects.AddRange(capture.ForbiddenRects);
            startupCardSlotIds.AddRange(capture.SlotIds);
            return capture.Slots;
        }

        private bool IsStartupCardDropForbidden(Point windowPoint)
        {
            if (startupCardForbiddenRects.Count == 0)
            {
                return false;
            }
            for (int index = 0; index < startupCardForbiddenRects.Count; index++)
            {
                if (startupCardForbiddenRects[index].Contains(windowPoint))
                {
                    return true;
                }
            }
            return false;
        }

        private void ApplyStartupCardDrop(DragMoveCardDropDecision decision)
        {
            if (decision == null || profileDragSource == null || decision.Kind == DragMoveCardDropKind.None)
            {
                return;
            }
            if (decision.Kind == DragMoveCardDropKind.Merge)
            {
                StartupProfile target = FindStartupProfileById(decision.TargetId);
                if (target != null)
                {
                    MergeProfileInto(profileDragSource.Id, target);
                }
                return;
            }
            if (decision.Kind != DragMoveCardDropKind.Insert || startupCardSlotIds.Count == 0)
            {
                return;
            }
            string anchorId;
            bool insertAfter;
            if (decision.Index >= startupCardSlotIds.Count)
            {
                anchorId = startupCardSlotIds[startupCardSlotIds.Count - 1];
                insertAfter = true;
            }
            else
            {
                anchorId = startupCardSlotIds[Math.Max(0, decision.Index)];
                insertAfter = false;
            }
            StartupProfile anchor = FindStartupProfileById(anchorId);
            if (anchor != null)
            {
                MoveProfileRelativeTo(profileDragSource, anchor, insertAfter);
            }
        }

        private StartupProfile FindStartupProfileById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            IList<StartupProfile> profiles = controller.StartupProfiles;
            if (profiles == null)
            {
                return null;
            }
            for (int index = 0; index < profiles.Count; index++)
            {
                if (profiles[index] != null
                    && string.Equals(profiles[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return profiles[index];
                }
            }
            return null;
        }

        private void MoveProfileRelativeTo(StartupProfile profile, StartupProfile anchor, bool insertAfter)
        {
            if (profile == null || anchor == null)
            {
                return;
            }
            string oldParentId = profile.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, profile);
            if (!controller.MoveProfileRelative(profile, anchor, insertAfter))
            {
                return;
            }
            string newParentId = profile.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, profile);
            RefreshProfileList();
            PushUndo(
                AppText.T(StartupRestoreTextKeys.CardUndoReorder),
                delegate
                {
                    controller.MoveProfileToPlacement(profile, oldParentId, oldIndex);
                    RefreshProfileList();
                },
                delegate
                {
                    controller.MoveProfileToPlacement(profile, newParentId, newIndex);
                    RefreshProfileList();
                });
        }

        private void ShowStartupDragHint(string text)
        {
            if (statusText == null || string.IsNullOrEmpty(text))
            {
                return;
            }
            if (!string.Equals(statusText.Text, text, StringComparison.Ordinal))
            {
                statusText.Text = text;
                AccessibleToolTip.Attach(statusText, StartupRestoreToolTips.DragHint(text));
            }
        }

        // ================================================================ 项目行拖动

        private DragMoveSession StartupItemDragEngine()
        {
            if (startupItemDragSession != null)
            {
                return startupItemDragSession;
            }
            DragMoveHost host = new DragMoveHost();
            host.Window = this;
            host.Layer = dragInsertionLayer;
            host.Ghost = dragGhost;
            host.List = profileScrollViewer;
            host.TopPad = profileDropPadTop;
            host.BottomPad = profileDropPad;
            host.Mode = DragMoveMode.InsertRow;
            host.LogTag = "[拖动]";
            host.DescribeMoving = delegate
            {
                string fallback = AppText.T(StartupRestoreTextKeys.FallbackItemFallback);
                return dragSourceItem == null
                    ? fallback
                    : (SessionItemDisplayName(dragSourceItem) ?? fallback);
            };
            host.FindRowAt = FindStartupItemDragRowAt;
            host.ApplyDrop = delegate(DragMoveRow row, bool insertAfter)
            {
                NativeItemRow target = row == null ? null : row.Payload as NativeItemRow;
                if (target != null)
                {
                    ApplyNativeItemDrop(dragSourceProfile, dragSourceItem, target, insertAfter);
                }
            };
            host.ApplyEdgeDrop = delegate(int direction)
            {
                if (dragSourceProfile != null && dragSourceItem != null)
                {
                    MoveItemToEdge(dragSourceProfile, dragSourceItem, direction);
                }
            };
            host.FindRowCardAt = FindStartupItemCardAt;
            host.DescribeRowCardHint = delegate(DragMoveCardSlot card, Point point)
            {
                StartupProfile target = card == null ? null : FindStartupProfileById(card.Id);
                return target == null ? null : BuildNativeItemCardDropHint(target, point);
            };
            host.ApplyRowCardDrop = delegate(DragMoveCardSlot card, Point point)
            {
                StartupProfile target = card == null ? null : FindStartupProfileById(card.Id);
                if (target != null)
                {
                    ApplyNativeItemDropToCard(dragSourceProfile, dragSourceItem, target, point);
                }
            };
            host.FindCardElement = delegate(string id)
            {
                return startupCardElements.FindHeader(id);
            };
            host.DescribeCard = delegate(string id)
            {
                StartupProfile profile = FindStartupProfileById(id);
                return profile == null
                    ? "?"
                    : (profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
            };
            host.DescribeRowHint = delegate(DragMoveRow row, bool insertAfter)
            {
                NativeItemRow target = row == null ? null : row.Payload as NativeItemRow;
                return BuildStartupItemDropHint(target, insertAfter);
            };
            host.ShowHint = ShowStartupDragHint;
            host.ScrollSpeedFactor = delegate { return controller.Settings.DragScrollSpeed; };
            host.OnCancelled = delegate
            {
                ClearStartupItemDragState();
                nativeItemDragJustEnded = true;
            };
            host.OnFinished = delegate
            {
                ClearStartupItemDragState();
                nativeItemDragJustEnded = true;
            };
            startupItemDragSession = new DragMoveSession(host);
            return startupItemDragSession;
        }

        /// <summary>项目行起拖判定：超过系统最小拖动距离后交给公共引擎。</summary>
        private void BeginItemReorderDrag(StartupProfile profile, UIElement source, SessionItem item, Border border, MouseEventArgs args)
        {
            if (source == null || item == null || args == null
                || !DragGestureSettings.IsMouseButtonPressed(args, rowDragMouseButton))
            {
                return;
            }
            if (startupItemDragSession != null && startupItemDragSession.IsActive)
            {
                return;
            }
            Point position = args.GetPosition(this);
            if ((Math.Abs(position.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(position.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
                || (dragSourceItem != null && object.ReferenceEquals(dragSourceItem, item)))
            {
                return;
            }
            StartStartupItemDrag(profile, item, border);
        }

        /// <summary>页面按钮/行松手：引擎自己的窗口钩子也会收尾，这里是幂等的兜底。</summary>
        private void EndNativeItemDrag()
        {
            FinishStartupItemDrag();
        }

        private void StartStartupItemDrag(StartupProfile profile, SessionItem item, Border source)
        {
            if (profile == null || item == null || source == null || startupItemDragSession != null && startupItemDragSession.IsActive)
            {
                return;
            }
            dragSourceProfile = profile;
            dragSourceItem = item;
            dragActive = true;
            startupItemDropRow = null;
            if (!StartupItemDragEngine().Start(
                source,
                SessionItemDisplayName(item) ?? AppText.T(StartupRestoreTextKeys.FallbackItemFallback),
                rowDragMouseButton))
            {
                ClearStartupItemDragState();
            }
        }

        private void ClearStartupItemDragState()
        {
            dragSourceProfile = null;
            dragSourceItem = null;
            dragActive = false;
            rowDragArmed = false;
            startupItemDropRow = null;
        }

        private void FinishStartupItemDrag()
        {
            if (startupItemDragSession != null && startupItemDragSession.IsActive)
            {
                startupItemDragSession.Finish();
            }
        }

        /// <summary>
        /// 项目行的落点：先取最内层行；带 6/3 像素粘滞；顶层项落在分组子行上时提升为父行；
        /// 子项跨方案＝禁止（返回 null，由引擎显示禁止光标）。
        /// </summary>
        private DragMoveRow FindStartupItemDragRowAt(Point windowPoint)
        {
            NativeItemRow target = FindNativeItemRowAt(windowPoint);
            if (target == null
                && startupItemDropRow != null
                && IsPointWithinNativeRow(startupItemDropRow, windowPoint, 6.0))
            {
                target = startupItemDropRow;
                EnsureRowGeometry(target);
            }
            if (target != null
                && startupItemDropRow != null
                && !object.ReferenceEquals(target.Item, startupItemDropRow.Item)
                && IsPointWithinNativeRow(startupItemDropRow, windowPoint, 3.0))
            {
                target = startupItemDropRow;
                EnsureRowGeometry(target);
            }
            if (target != null && !IsChildEntry(dragSourceItem) && IsChildEntry(target.Item))
            {
                NativeItemRow group = FindNativeItemRowForItem(FindParentWindow(target.Profile, target.Item));
                if (group != null)
                {
                    target = group;
                    EnsureRowGeometry(target);
                }
            }
            if (target != null
                && dragSourceProfile != null
                && target.Profile != null
                && !object.ReferenceEquals(target.Profile, dragSourceProfile)
                && IsChildEntry(dragSourceItem))
            {
                return null;
            }
            if (target != null && dragSourceItem != null
                && object.ReferenceEquals(target.Item, dragSourceItem))
            {
                // 压在来源自己那一行上：没有落点（松手取消），不要画“插到自己前后”。
                startupItemDropRow = null;
                return null;
            }
            startupItemDropRow = target;
            if (target == null || target.Item == null)
            {
                return null;
            }
            EnsureRowGeometry(target);
            return new DragMoveRow
            {
                Payload = target,
                Owner = target.Profile,
                Element = target.Row,
                Label = SessionItemDisplayName(target.Item),
                Top = target.RowTranslateY,
                Height = target.RowHeight
            };
        }

        private DragMoveCardSlot FindStartupItemCardAt(Point windowPoint)
        {
            if (IsChildEntry(dragSourceItem))
            {
                return null;
            }
            StartupProfile target = FindNativeItemCardTarget(windowPoint, dragSourceProfile);
            if (target == null)
            {
                return null;
            }
            return new DragMoveCardSlot
            {
                Id = target.Id
            };
        }

        private string BuildStartupItemDropHint(NativeItemRow target, bool insertAfter)
        {
            string fallback = AppText.T(StartupRestoreTextKeys.FallbackThisItem);
            string moving = dragSourceItem == null
                ? fallback
                : (SessionItemDisplayName(dragSourceItem) ?? fallback);
            if (target == null || target.Item == null)
            {
                return AppText.F(StartupRestoreTextKeys.ItemDropCancelFormat, moving);
            }
            return AppText.F(
                insertAfter
                    ? StartupRestoreTextKeys.ItemDropInsertAfterFormat
                    : StartupRestoreTextKeys.ItemDropInsertBeforeFormat,
                moving,
                ItemPositionOf(target, insertAfter),
                SessionItemDisplayName(target.Item));
        }
    }
}
