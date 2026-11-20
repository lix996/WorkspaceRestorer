using System;

namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 每种卡片/行的标准按钮顺序。
    ///
    /// 顺序只写一次：移动永远最左，删除永远最右；卡片按实际有的动作紧凑排，
    /// 行/整组固定保留“移动、最小化、删除”三格，没有最小化就补同宽空位，
    /// 这样浏览器父行、网页子行、整组父行的删除按钮不会左右跳。
    /// </summary>
    internal static class ActionBarSpec
    {
        internal static ActionSlot[] For(ActionBarKind kind)
        {
            switch (kind)
            {
                case ActionBarKind.ProfileCard:
                    return new[]
                    {
                        ActionSlot.Move,
                        ActionSlot.Save,
                        ActionSlot.Restore,
                        ActionSlot.Delete
                    };
                case ActionBarKind.ApplicationCard:
                    return new[]
                    {
                        ActionSlot.Move,
                        ActionSlot.Add,
                        ActionSlot.Restore,
                        ActionSlot.Delete
                    };
                case ActionBarKind.LayoutCard:
                    return new[]
                    {
                        ActionSlot.Move,
                        ActionSlot.Edit,
                        ActionSlot.Save,
                        ActionSlot.Restore,
                        ActionSlot.Delete
                    };
                case ActionBarKind.LayoutFolderCard:
                    return new[]
                    {
                        ActionSlot.Move,
                        ActionSlot.Add,
                        ActionSlot.AddFolder,
                        ActionSlot.Delete
                    };
                case ActionBarKind.ItemRow:
                case ActionBarKind.GroupRow:
                    return new[]
                    {
                        ActionSlot.Move,
                        ActionSlot.Minimize,
                        ActionSlot.Delete
                    };
                case ActionBarKind.ShortcutCommandRow:
                    // 快捷键命令头：加号新增一条绑定，恢复把这条命令回默认；
                    // 编辑/删除属于具体绑定，这里没有就补灰色禁用位，保证四格对齐。
                    return new[]
                    {
                        ActionSlot.Add,
                        ActionSlot.Restore,
                        ActionSlot.Edit,
                        ActionSlot.Delete
                    };
                case ActionBarKind.ShortcutBindingRow:
                    // 后续绑定行只有自己的编辑和删除；命令级动作补灰色禁用位，保持四格对齐。
                    return new[]
                    {
                        ActionSlot.Add,
                        ActionSlot.Restore,
                        ActionSlot.Edit,
                        ActionSlot.Delete
                    };
                case ActionBarKind.ShortcutBindingWithCommandRow:
                    // 第一条绑定行：命令级的新增/恢复 + 本条的编辑/删除，全部排成一排。
                    return new[]
                    {
                        ActionSlot.Add,
                        ActionSlot.Restore,
                        ActionSlot.Edit,
                        ActionSlot.Delete
                    };
                case ActionBarKind.DragGestureRow:
                    // 拖动只有一条手势：编辑 + 恢复默认；加号/垃圾桶补灰色禁用位，保持四格对齐。
                    return new[]
                    {
                        ActionSlot.Add,
                        ActionSlot.Restore,
                        ActionSlot.Edit,
                        ActionSlot.Delete
                    };
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
        }

        internal static bool IsFixedSlotBar(ActionBarKind kind)
        {
            return kind == ActionBarKind.ItemRow || kind == ActionBarKind.GroupRow;
        }

        internal static bool Contains(ActionBarKind kind, ActionSlot slot)
        {
            ActionSlot[] order = For(kind);
            for (int index = 0; index < order.Length; index++)
            {
                if (order[index] == slot)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
