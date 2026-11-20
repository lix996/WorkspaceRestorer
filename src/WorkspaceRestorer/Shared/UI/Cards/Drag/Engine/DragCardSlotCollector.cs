using System;
using System.Collections.Generic;
using System.Windows;
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>一次卡片拖动开始时的槽位/禁止区快照。</summary>
    internal sealed class DragCardSlotCapture
    {
        public DragCardSlotCapture()
        {
            Slots = new List<DragMoveCardSlot>();
            ForbiddenRects = new List<Rect>();
            SlotIds = new List<string>();
        }

        /// <summary>内容坐标（相对 cardHost），已排除被拖卡片自己。</summary>
        public List<DragMoveCardSlot> Slots;

        /// <summary>窗口坐标（相对 windowRoot）的禁止落点矩形。</summary>
        public List<Rect> ForbiddenRects;

        /// <summary>与 Slots 同序的 Id；启动与恢复按 Index 找回锚点时用。</summary>
        public List<string> SlotIds;
    }

    /// <summary>
    /// 【公共拖动 · 卡片槽位采集】三个工作区共用同一套口径：
    ///  · Header 算槽位（父卡片头不会覆盖展开出来的子卡片）；
    ///  · Block 算禁止区（被拖方案自己的后代整棵都禁止）；
    ///  · 指针命中哪张卡片、能不能并入仍由页面业务回调决定。
    ///
    /// 只做几何，不改数据、不弹框、不碰撤销。
    /// </summary>
    internal static class DragCardSlotCollector
    {
        public static DragCardSlotCapture Capture(
            DragCardElementRegistry registry,
            FrameworkElement cardHost,
            FrameworkElement windowRoot,
            string draggedId,
            Predicate<string> isForbidden,
            bool allowBlockFallback)
        {
            DragCardSlotCapture capture = new DragCardSlotCapture();
            if (registry == null || cardHost == null)
            {
                return capture;
            }

            IList<DragCardElement> elements = registry.Items;
            for (int index = 0; index < elements.Count; index++)
            {
                DragCardElement element = elements[index];
                if (element == null || string.IsNullOrWhiteSpace(element.Id))
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(draggedId)
                    && string.Equals(element.Id, draggedId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FrameworkElement block = element.Block == null ? element.Header : element.Block;
                bool forbidden = isForbidden != null && isForbidden(element.Id);
                if (forbidden)
                {
                    AddForbiddenRect(capture, block, windowRoot);
                    continue;
                }

                FrameworkElement slotElement = element.Header;
                if (slotElement == null && allowBlockFallback)
                {
                    slotElement = element.Block;
                }
                if (slotElement == null || !slotElement.IsVisible)
                {
                    continue;
                }
                if (slotElement.ActualWidth <= 1.0 || slotElement.ActualHeight <= 1.0)
                {
                    continue;
                }

                Point origin;
                try
                {
                    origin = slotElement.TranslatePoint(new Point(0.0, 0.0), cardHost);
                }
                catch
                {
                    continue;
                }

                DragMoveCardSlot slot = new DragMoveCardSlot();
                slot.Id = element.Id;
                slot.Left = origin.X;
                slot.Width = slotElement.ActualWidth;
                slot.Top = origin.Y;
                slot.Height = slotElement.ActualHeight;
                capture.Slots.Add(slot);
            }

            capture.Slots.Sort(delegate(DragMoveCardSlot left, DragMoveCardSlot right)
            {
                return left.Top.CompareTo(right.Top);
            });
            for (int index = 0; index < capture.Slots.Count; index++)
            {
                capture.SlotIds.Add(capture.Slots[index].Id);
            }
            return capture;
        }

        private static void AddForbiddenRect(
            DragCardSlotCapture capture,
            FrameworkElement block,
            FrameworkElement windowRoot)
        {
            if (capture == null || block == null || windowRoot == null || !block.IsVisible)
            {
                return;
            }
            if (block.ActualWidth <= 1.0 || block.ActualHeight <= 1.0)
            {
                return;
            }
            try
            {
                Point origin = block.TranslatePoint(new Point(0.0, 0.0), windowRoot);
                capture.ForbiddenRects.Add(new Rect(
                    origin.X, origin.Y, block.ActualWidth, block.ActualHeight));
            }
            catch
            {
            }
        }
    }
}
