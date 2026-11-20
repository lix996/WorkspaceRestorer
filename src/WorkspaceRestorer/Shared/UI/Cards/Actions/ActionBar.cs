using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Cards.Actions
{
    /// <summary>
    /// 卡片/行右侧动作区的唯一排布器。
    ///
    /// 卡片：按 <see cref="ActionBarSpec"/> 的顺序紧凑排；没有的动作不占位，
    ///       但移动永远最左、删除永远最右。
    /// 行/整组：三格固定宽度（移动 26、最小化 32、删除 32），没有最小化就补
    ///       同宽透明空位；因此浏览器父行、网页子行、整组父行的右边线一致。
    ///
    /// 按钮效果不在这里：调用方拿到 Add 返回的 Button 后，自己绑 Click/拖动。
    /// </summary>
    internal sealed class ActionBar
    {
        private readonly ActionBarKind kind;
        private readonly ActionSlot[] order;
        private readonly Dictionary<ActionSlot, Button> buttons =
            new Dictionary<ActionSlot, Button>();
        private StackPanel panel;

        private ActionBar(ActionBarKind kind)
        {
            this.kind = kind;
            order = ActionBarSpec.For(kind);
        }

        internal static ActionBar Create(ActionBarKind kind)
        {
            return new ActionBar(kind);
        }

        internal Button Add(ActionSlot slot, Button button)
        {
            if (button == null)
            {
                throw new ArgumentNullException("button");
            }
            if (!ActionBarSpec.Contains(kind, slot))
            {
                throw new InvalidOperationException(
                    "动作 " + slot + " 不属于 " + kind + " 的标准顺序。");
            }
            if (buttons.ContainsKey(slot))
            {
                throw new InvalidOperationException("动作 " + slot + " 已经添加过。");
            }

            buttons[slot] = button;
            return button;
        }

        internal StackPanel Build()
        {
            if (panel != null)
            {
                return panel;
            }

            panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool fixedSlots = ActionBarSpec.IsFixedSlotBar(kind);
            bool first = true;
            for (int index = 0; index < order.Length; index++)
            {
                ActionSlot slot = order[index];
                Button button;
                if (buttons.TryGetValue(slot, out button))
                {
                    Prepare(button, slot, first);
                    panel.Children.Add(button);
                    first = false;
                }
                else if (fixedSlots)
                {
                    Border placeholder = CreatePlaceholder(slot, first);
                    panel.Children.Add(placeholder);
                    first = false;
                }
            }
            return panel;
        }

        private void Prepare(Button button, ActionSlot slot, bool first)
        {
            button.Width = ActionMetrics.WidthOf(slot, kind);
            button.Height = ActionMetrics.ButtonSize;
            button.MinHeight = ActionMetrics.ButtonSize;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(first ? 0 : ActionMetrics.Gap, 0, 0, 0);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.FocusVisualStyle = null;
        }

        private Border CreatePlaceholder(ActionSlot slot, bool first)
        {
            return new Border
            {
                Width = ActionMetrics.WidthOf(slot, kind),
                Height = ActionMetrics.ButtonSize,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Margin = new Thickness(first ? 0 : ActionMetrics.Gap, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
