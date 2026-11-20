using System;
using System.Collections.Generic;
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.Core.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Drag;

namespace WorkspaceRestorer.Models
{
    /// <summary>卡片拖动的落点类型。</summary>
    public enum ProfileCardDropKind
    {
        /// <summary>没有合法落点（指针在列表外 / 两张卡片之间那片空白太大）。</summary>
        None = 0,

        /// <summary>插到某张卡片之前或之后（调整顺序）。</summary>
        Insert = 1,

        /// <summary>并入某张卡片（合并成一份）。</summary>
        Merge = 2
    }

    /// <summary>
    /// 一张方案卡片在列表里占的纵向范围（**profileHost 内容坐标**，不是窗口坐标）。
    /// 用内容坐标是为了“滚动不影响判定”：卡片能跟着列表滚，但它在内容里的位置不变，
    /// 于是“指针落在哪张卡片的哪个带子里”这件事不会因为滚动而反复变。
    /// </summary>
    public sealed class ProfileCardSlot
    {
        public string Id;
        public double Left;
        public double Width;
        public double Top;
        public double Height;

        public double Bottom
        {
            get { return Top + Height; }
        }
    }

    /// <summary>一次落点判定的结果（`Insert` 用 `Index` 表示“插到第 Index 张之前”，`Index == Count` 表示插到最后）。</summary>
    public sealed class ProfileCardDropDecision
    {
        public ProfileCardDropKind Kind;
        public int Index;
        public string TargetId;

        public static ProfileCardDropDecision None()
        {
            return new ProfileCardDropDecision { Kind = ProfileCardDropKind.None, Index = -1, TargetId = null };
        }

        public bool SameTarget(ProfileCardDropDecision other)
        {
            if (other == null)
            {
                return Kind == ProfileCardDropKind.None;
            }
            return Kind == other.Kind && Index == other.Index;
        }
    }

    /// <summary>
    /// 方案卡片落点判定的**测试/兼容适配层**。
    ///
    /// 2026-11-01 起启动界面已经直接由公共 `DragMoveSession` 调
    /// `DragMoveCardZones`（`StrictNone` 口径），本文件不再参与生产拖动流程；
    /// 保留它只因为现有纯逻辑测试仍用 `ProfileCardSlot` 这套数据形状。
    /// 新代码不要再往这里加落点逻辑，统一改公共 `DragMoveCardZones`。
    /// </summary>
    public static class ProfileCardDropZones
    {
        /// <summary>边缘插入带占卡片高度的比例（大厂常见 25%）。</summary>
        public const double EdgeBandRatio = DragMoveCardZones.EdgeBandRatio;

        /// <summary>边缘插入带最小像素：太小的卡片也要点得中。</summary>
        public const double EdgeBandMin = DragMoveCardZones.EdgeBandMin;

        /// <summary>边缘插入带最大像素：卡片很高时插入带别宽到把中间挤没。</summary>
        public const double EdgeBandMax = DragMoveCardZones.EdgeBandMax;

        /// <summary>滞回量（像素）：已经选中的落点要“再多走这么多”才会被换掉。</summary>
        public const double Hysteresis = DragMoveCardZones.Hysteresis;

        /// <summary>某张卡片的边缘插入带宽度（像素）。</summary>
        public static double EdgeBand(double height)
        {
            return DragMoveCardZones.EdgeBand(height);
        }

        /// <summary>
        /// 算落点。`previous` 是上一次的结果（第一次传 null），用于滞回。
        /// 固定使用「启动与恢复」的严格口径（`StrictNone`）。
        /// </summary>
        public static ProfileCardDropDecision Resolve(
            IList<ProfileCardSlot> slots,
            double pointerY,
            ProfileCardDropDecision previous)
        {
            DragMoveCardDropDecision decision = DragMoveCardZones.Resolve(
                ToShared(slots),
                pointerY,
                ToShared(previous),
                DragMoveCardGapMode.StrictNone);
            return FromShared(decision);
        }

        /// <summary>不带滞回的直接判定（「启动与恢复」严格口径；测试会直接调它）。</summary>
        public static ProfileCardDropDecision ResolveRaw(
            IList<ProfileCardSlot> slots,
            double pointerY)
        {
            DragMoveCardDropDecision decision = DragMoveCardZones.ResolveRaw(
                ToShared(slots),
                pointerY,
                DragMoveCardGapMode.StrictNone);
            return FromShared(decision);
        }

        /// <summary>
        /// 上一次的落点还算不算数（滞回）。判据是“指针还在上一次那个带子**放宽 Hysteresis 像素**的范围里”。
        /// </summary>
        public static bool HoldsPrevious(
            IList<ProfileCardSlot> slots,
            double pointerY,
            ProfileCardDropDecision previous)
        {
            return DragMoveCardZones.HoldsPrevious(
                ToShared(slots),
                pointerY,
                ToShared(previous));
        }

        private static List<DragMoveCardSlot> ToShared(IList<ProfileCardSlot> slots)
        {
            if (slots == null)
            {
                return null;
            }
            List<DragMoveCardSlot> converted = new List<DragMoveCardSlot>(slots.Count);
            for (int index = 0; index < slots.Count; index++)
            {
                ProfileCardSlot slot = slots[index];
                if (slot == null)
                {
                    continue;
                }
                converted.Add(new DragMoveCardSlot
                {
                    Id = slot.Id,
                    Left = slot.Left,
                    Width = slot.Width,
                    Top = slot.Top,
                    Height = slot.Height
                });
            }
            return converted;
        }

        private static DragMoveCardDropDecision ToShared(ProfileCardDropDecision decision)
        {
            if (decision == null)
            {
                return null;
            }
            DragMoveCardDropKind kind;
            if (decision.Kind == ProfileCardDropKind.Insert)
            {
                kind = DragMoveCardDropKind.Insert;
            }
            else if (decision.Kind == ProfileCardDropKind.Merge)
            {
                kind = DragMoveCardDropKind.Merge;
            }
            else
            {
                kind = DragMoveCardDropKind.None;
            }
            return new DragMoveCardDropDecision
            {
                Kind = kind,
                Index = decision.Index,
                TargetId = decision.TargetId
            };
        }

        private static ProfileCardDropDecision FromShared(DragMoveCardDropDecision decision)
        {
            if (decision == null)
            {
                return null;
            }
            ProfileCardDropKind kind;
            if (decision.Kind == DragMoveCardDropKind.Insert)
            {
                kind = ProfileCardDropKind.Insert;
            }
            else if (decision.Kind == DragMoveCardDropKind.Merge)
            {
                kind = ProfileCardDropKind.Merge;
            }
            else
            {
                kind = ProfileCardDropKind.None;
            }
            return new ProfileCardDropDecision
            {
                Kind = kind,
                Index = decision.Index,
                TargetId = decision.TargetId
            };
        }
    }
}
