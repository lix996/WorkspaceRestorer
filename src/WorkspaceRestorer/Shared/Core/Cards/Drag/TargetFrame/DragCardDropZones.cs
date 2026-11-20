using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Core.Cards.Drag
{
    /// <summary>
    /// 卡片落点判定（**从「启动与恢复」的 ProfileCardDropZones 复制**；边缘带/滞回/插入位置相同，
    /// 但“缝隙太大怎么办”分成 <see cref="DragMoveCardGapMode.StrictNone"/> 与
    /// <see cref="DragMoveCardGapMode.NearestSeam"/> 两种，不能再说“完全一致”）。
    ///
    /// 带子怎么分：靠近上/下边缘的一条窄带＝插入；中间大部分＝并入。
    /// 窄带宽度 = 卡片高 × 25%，夹在 [10, 24] 像素之间，且不超过卡片高的 35%。
    /// 再加 8 像素滞回：边界上怎么抖都不会在“插入/并入”之间来回翻（这是防闪的关键）。
    /// </summary>
    internal static class DragMoveCardZones
    {
        /// <summary>边缘插入带占卡片高度的比例。</summary>
        internal const double EdgeBandRatio = 0.25;

        /// <summary>边缘插入带最小像素。</summary>
        internal const double EdgeBandMin = 10.0;

        /// <summary>边缘插入带最大像素。</summary>
        internal const double EdgeBandMax = 24.0;

        /// <summary>滞回量（像素）。</summary>
        internal const double Hysteresis = 8.0;

        /// <summary>某张卡片的边缘插入带宽度。</summary>
        internal static double EdgeBand(double height)
        {
            if (height <= 0.0)
            {
                return EdgeBandMin;
            }
            double band = height * EdgeBandRatio;
            if (band < EdgeBandMin)
            {
                band = EdgeBandMin;
            }
            if (band > EdgeBandMax)
            {
                band = EdgeBandMax;
            }
            double cap = height * 0.35;
            if (band > cap)
            {
                band = cap;
            }
            return band;
        }

        /// <summary>
        /// 算落点；<paramref name="previous"/> 是上一次的结果（第一次传 null），用于滞回。
        /// 默认使用「自启动应用」的“列表里面一定能放”口径；「启动与恢复」显式传
        /// <see cref="DragMoveCardGapMode.StrictNone"/>。
        /// </summary>
        internal static DragMoveCardDropDecision Resolve(
            IList<DragMoveCardSlot> slots,
            double pointerY,
            DragMoveCardDropDecision previous)
        {
            return Resolve(slots, pointerY, previous, DragMoveCardGapMode.NearestSeam);
        }

        internal static DragMoveCardDropDecision Resolve(
            IList<DragMoveCardSlot> slots,
            double pointerY,
            DragMoveCardDropDecision previous,
            DragMoveCardGapMode gapMode)
        {
            if (slots == null || slots.Count == 0)
            {
                return DragMoveCardDropDecision.None();
            }
            DragMoveCardDropDecision raw = ResolveRaw(slots, pointerY, gapMode);
            if (previous != null
                && previous.Kind != DragMoveCardDropKind.None
                && HoldsPrevious(slots, pointerY, previous))
            {
                return previous;
            }
            return raw;
        }

        /// <summary>不带滞回的直接判定（默认“最近缝”口径）。</summary>
        internal static DragMoveCardDropDecision ResolveRaw(IList<DragMoveCardSlot> slots, double pointerY)
        {
            return ResolveRaw(slots, pointerY, DragMoveCardGapMode.NearestSeam);
        }

        internal static DragMoveCardDropDecision ResolveRaw(
            IList<DragMoveCardSlot> slots,
            double pointerY,
            DragMoveCardGapMode gapMode)
        {
            if (gapMode == DragMoveCardGapMode.StrictNone)
            {
                return ResolveRawStrict(slots, pointerY);
            }
            return ResolveRawNearest(slots, pointerY);
        }

        /// <summary>
        /// 「启动与恢复」原来的严格口径（从 `ProfileCardDropZones.ResolveRaw` 原样搬来）：
        /// 只有第一张上边缘带、卡片上/下边缘带、两张之间的卡缝、最后一张下边缘带才算落点；
        /// 缝隙太大、离第一张/最后一张太远 → `None`（松手取消）。
        /// </summary>
        private static DragMoveCardDropDecision ResolveRawStrict(
            IList<DragMoveCardSlot> slots,
            double pointerY)
        {
            if (slots == null || slots.Count == 0)
            {
                return DragMoveCardDropDecision.None();
            }

            // 第一张卡片之上：只有落在它的上边缘带里才算“插到最前面”。
            double firstBand = EdgeBand(slots[0].Height);
            if (pointerY < slots[0].Top)
            {
                if (pointerY >= slots[0].Top - firstBand)
                {
                    return InsertAt(slots, 0);
                }
                return DragMoveCardDropDecision.None();
            }

            for (int index = 0; index < slots.Count; index++)
            {
                DragMoveCardSlot slot = slots[index];
                double band = EdgeBand(slot.Height);
                if (pointerY <= slot.Top + band)
                {
                    // 上边缘窄带（也把“和上一张之间的卡缝”算进来）
                    return InsertAt(slots, index);
                }
                if (pointerY < slot.Bottom - band)
                {
                    // 卡片中心的大部分区域 → 并入这张
                    return new DragMoveCardDropDecision
                    {
                        Kind = DragMoveCardDropKind.Merge,
                        Index = index,
                        TargetId = slot.Id
                    };
                }
                if (pointerY < slot.Bottom)
                {
                    // 下边缘窄带 → 插到这张之后（＝下一张之前）
                    return InsertAt(slots, index + 1);
                }
                // 落在这一张下面：如果它和下一张之间的空档**比插入带还大**（例如中间夹着它展开的清单），
                // 那这块地方不猜落点 —— 与用户“移动到禁止区域＝取消”的约定一致。
                if (index + 1 < slots.Count)
                {
                    double nextTop = slots[index + 1].Top;
                    if (pointerY < nextTop - EdgeBand(slots[index + 1].Height))
                    {
                        return DragMoveCardDropDecision.None();
                    }
                }
            }

            // 最后一张之下：只有落在它的下边缘带里才算“插到最后”。
            DragMoveCardSlot last = slots[slots.Count - 1];
            if (pointerY <= last.Bottom + EdgeBand(last.Height))
            {
                return InsertAt(slots, slots.Count);
            }
            return DragMoveCardDropDecision.None();
        }

        /// <summary>
        /// 「自启动应用」的“列表里面一定能放”口径（2026-10-17 修“松开鼠标后完全卡住了”）：
        /// 卡片之间的空档＝插到下一张之前；最后一张下面＝插到最后面。
        /// “能不能放”只由指针在不在列表/放置条上决定（引擎用 `IsInsideDropArea` 判断）。
        /// </summary>
        private static DragMoveCardDropDecision ResolveRawNearest(
            IList<DragMoveCardSlot> slots,
            double pointerY)
        {
            if (slots == null || slots.Count == 0)
            {
                return DragMoveCardDropDecision.None();
            }

            // 第一张卡片之上：落在上边缘带里是“插到最前面”；再往上也还是插到最前面
            //（指针已经不在列表里时，引擎那边直接判“不能放”，不会用这个结果）。
            if (pointerY < slots[0].Top)
            {
                return InsertAt(slots, 0);
            }

            for (int index = 0; index < slots.Count; index++)
            {
                DragMoveCardSlot slot = slots[index];
                double band = EdgeBand(slot.Height);
                if (pointerY <= slot.Top + band)
                {
                    return InsertAt(slots, index);   // 上边缘窄带（含与上一张之间的卡缝）
                }
                if (pointerY < slot.Bottom - band)
                {
                    return new DragMoveCardDropDecision
                    {
                        Kind = DragMoveCardDropKind.Merge,
                        Index = index,
                        TargetId = slot.Id
                    };
                }
                if (pointerY < slot.Bottom)
                {
                    return InsertAt(slots, index + 1);   // 下边缘窄带 → 插到这张之后
                }
                // 落在这一张下面：**只有真的到了下一张卡片头上**才算“两张之间的空档”
                //（＝插到下一张之前，也就是这一张之后）。指针其实还在下一张卡片上就继续判下一张，
                // 否则会把“并进下一张”误判成“插到它前面”（Round105 的 T5 抓到过）。
                if (index + 1 < slots.Count)
                {
                    if (pointerY < slots[index + 1].Top)
                    {
                        return InsertAt(slots, index + 1);
                    }
                    continue;
                }
            }

            // 最后一张之下：插到最后面（列表里再空也算数；出了列表引擎会判“不能放”）。
            return InsertAt(slots, slots.Count);
        }

        /// <summary>上一次的落点还算不算数（滞回）：指针还在那个带子**放宽 8 像素**的范围里就保持不动。</summary>
        internal static bool HoldsPrevious(
            IList<DragMoveCardSlot> slots,
            double pointerY,
            DragMoveCardDropDecision previous)
        {
            if (slots == null || slots.Count == 0 || previous == null || previous.Index < 0)
            {
                return false;
            }

            if (previous.Kind == DragMoveCardDropKind.Insert)
            {
                if (previous.Index >= 0 && previous.Index < slots.Count)
                {
                    DragMoveCardSlot next = slots[previous.Index];
                    double band = EdgeBand(next.Height);
                    if (pointerY >= next.Top - Hysteresis && pointerY <= next.Top + band + Hysteresis)
                    {
                        return true;
                    }
                }
                if (previous.Index > 0 && previous.Index - 1 < slots.Count)
                {
                    DragMoveCardSlot prev = slots[previous.Index - 1];
                    double band = EdgeBand(prev.Height);
                    if (pointerY >= prev.Bottom - band - Hysteresis && pointerY <= prev.Bottom + Hysteresis)
                    {
                        return true;
                    }
                }
                return false;
            }

            if (previous.Kind == DragMoveCardDropKind.Merge)
            {
                if (previous.Index >= slots.Count)
                {
                    return false;
                }
                DragMoveCardSlot slot = slots[previous.Index];
                double band = EdgeBand(slot.Height);
                return pointerY >= slot.Top + band - Hysteresis
                    && pointerY <= slot.Bottom - band + Hysteresis;
            }

            return false;
        }

        private static DragMoveCardDropDecision InsertAt(IList<DragMoveCardSlot> slots, int index)
        {
            int clamped = index < 0 ? 0 : (index > slots.Count ? slots.Count : index);
            string anchor = null;
            if (clamped < slots.Count)
            {
                anchor = slots[clamped].Id;
            }
            else if (slots.Count > 0)
            {
                anchor = slots[slots.Count - 1].Id;
            }
            return new DragMoveCardDropDecision
            {
                Kind = DragMoveCardDropKind.Insert,
                Index = clamped,
                TargetId = anchor
            };
        }
    }
}
