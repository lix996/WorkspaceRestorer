namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 卡片/行右侧动作区的固定槽位。
    ///
    /// 槽位只描述“这是什么动作”，不执行动作：删除、保存、恢复真正做什么，
    /// 仍由各工作区自己的页面代码绑定。公共件只负责顺序、尺寸、间距和空位。
    /// </summary>
    internal enum ActionSlot
    {
        Move,
        Edit,
        Add,
        AddFolder,
        Save,
        Restore,
        Minimize,
        Delete
    }

    /// <summary>三种工作区里出现的卡片/行类型。</summary>
    internal enum ActionBarKind
    {
        ProfileCard,
        ApplicationCard,
        LayoutCard,
        LayoutFolderCard,
        ItemRow,
        GroupRow,
        ShortcutCommandRow,
        ShortcutBindingRow,
        ShortcutBindingWithCommandRow,
        /// <summary>快捷键页的“拖动”行：只有编辑和恢复，没有新增/删除。</summary>
        DragGestureRow
    }
}
