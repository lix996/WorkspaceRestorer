using System.Windows.Controls;

namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 动作按钮的唯一外观工厂。
    ///
    /// 这里只决定“长什么样、用什么图标、提示是什么”，不绑定 Click。
    /// 删除/保存/恢复真正调哪个控制器方法，仍在三个工作区原来的文件里。
    /// </summary>
    internal static class ActionButtons
    {
        internal static Button Move(ToolTipContent content)
        {
            return Create("\uE700", content);
        }

        internal static Button Move(TooltipDefinition definition)
        {
            return Create("\uE700", definition);
        }

        /// <summary>
        /// E80A 是布局页使用的 3×3 网格图标，不是铅笔。快捷键“修改”必须一眼看出
        /// 是编辑，所以这里单独用现成的铅笔字形 E70F，外观仍走同一个 IconButton。
        /// </summary>
        internal static Button EditPencil(TooltipDefinition definition)
        {
            return Create("\uE70F", definition);
        }

        internal static Button Edit(ToolTipContent content)
        {
            return Create("\uE80A", content);
        }

        internal static Button Edit(TooltipDefinition definition)
        {
            return Create("\uE80A", definition);
        }

        internal static Button Add(ToolTipContent content)
        {
            return Create("\uE710", content);
        }

        internal static Button Add(TooltipDefinition definition)
        {
            return Create("\uE710", definition);
        }

        internal static Button AddFolder(ToolTipContent content)
        {
            return Create("\uE8B7", content);
        }

        internal static Button AddFolder(TooltipDefinition definition)
        {
            return Create("\uE8B7", definition);
        }

        internal static Button Save(ToolTipContent content)
        {
            return Create("\uE74E", content);
        }

        internal static Button Save(TooltipDefinition definition)
        {
            return Create("\uE74E", definition);
        }

        internal static Button Restore(ToolTipContent content)
        {
            return Create("\uE777", content);
        }

        internal static Button Restore(TooltipDefinition definition)
        {
            return Create("\uE777", definition);
        }

        internal static Button Delete(ToolTipContent content)
        {
            return Create("\uE74D", content);
        }

        internal static Button Delete(TooltipDefinition definition)
        {
            return Create("\uE74D", definition);
        }

        internal static Button DragHandle(ToolTipContent content)
        {
            return DragHandleButton.Create(content);
        }

        internal static Button DragHandle(TooltipDefinition definition)
        {
            return DragHandleButton.Create(definition);
        }

        private static Button Create(string glyph, TooltipDefinition definition)
        {
            Button button = UiFactory.IconButton(glyph, definition);
            button.FocusVisualStyle = null;
            return button;
        }

        private static Button Create(string glyph, ToolTipContent content)
        {
            Button button = UiFactory.IconButton(glyph, content);
            // 这些按钮都是鼠标动作，去掉键盘焦点虚线框；键盘操作仍走页面快捷键。
            button.FocusVisualStyle = null;
            return button;
        }
    }
}
