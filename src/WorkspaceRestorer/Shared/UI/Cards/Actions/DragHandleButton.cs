using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 行/卡片共用的“≡ 长按拖动”手柄。
    ///
    /// 只造按钮，不绑定拖动：各工作区仍把 PreviewMouseDown/Move/Up 接在自己的
    /// 拖动引擎上。这样手柄外观统一，拖动业务不跨层。
    /// </summary>
    internal static class DragHandleButton
    {
        internal static Button Create(TooltipDefinition definition)
        {
            Button button = Create((ToolTipContent)null);
            AccessibleToolTip.Attach(button, definition);
            return button;
        }

        internal static Button Create(ToolTipContent content)
        {
            Button button = new Button
            {
                Style = Application.Current == null
                    ? null
                    : (Style)Application.Current.Resources["IconButton"],
                FontFamily = AppFonts.Ui,
                Cursor = Cursors.SizeAll,
                Focusable = false,
                Content = new TextBlock
                {
                    Text = "\u2261",
                    FontFamily = AppFonts.Ui,
                    FontSize = 16,
                    Foreground = AppTheme.TextSecondary,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            AccessibleToolTip.Attach(button, content);
            return button;
        }
    }
}
