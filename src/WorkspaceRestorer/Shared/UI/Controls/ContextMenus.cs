using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkspaceRestorer.UI.Cards.Interaction;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 右键菜单里的一项：普通项（文字 + 动作 + 可选提示）或分隔线。
    /// 只是“怎么摆菜单”的数据，具体做什么仍由各工作区自己的方法负责。
    /// </summary>
    internal sealed class ContextMenuItem
    {
        private ContextMenuItem()
        {
        }

        internal string Header { get; private set; }

        internal Action Action { get; private set; }

        internal string Help { get; private set; }

        internal bool IsSeparator { get; private set; }

        /// <summary>普通菜单项。help 会作为该菜单项的工具提示（不传就没有）。</summary>
        internal static ContextMenuItem Item(string header, Action action, string help = null)
        {
            return new ContextMenuItem
            {
                Header = header,
                Action = action,
                Help = help
            };
        }

        /// <summary>分隔线。</summary>
        internal static ContextMenuItem Separator()
        {
            return new ContextMenuItem { IsSeparator = true };
        }
    }

    /// <summary>
    /// 右键菜单的公共装配件（2026-10-19 用户要求把“可右键的代码”统一放公共文件夹）。
    ///
    /// 用户原话：
    ///   “将可右键的代码也放入到公共文件夹内（其余的，例如启动与恢复，自启动应用对应的代码可以去除了，
    ///    但去除后要自己测试……右键某个地方或某个方案就是可以调用本身可以做的东西，但那些可以做的东西，
    ///    还是在原来的文件夹内，例如启动与恢复，右键某个工作方案可以恢复此工作方案等，恢复工作方案的代码
    ///    还是在启动与恢复的文件夹内，但是右键可以调用功能的代码，放到公共文件夹内，并分好类。”
    ///
    /// 所以这里**只负责“造菜单 + 挂到控件上”**：菜单项的文字、分隔线、启用状态、以及
    /// 挂到哪些控件（卡片、行、标题）都在这里；点下去真正执行的动作由各工作区自己传进来
    /// （例如「启动与恢复」的恢复仍调用它自己的 `RestoreProfile`）。
    ///
    /// 分类：
    ///   · 通用装配：<see cref="Create"/>、<see cref="Attach"/>；
    ///   · 文本框标准菜单：<see cref="CreateTextMenu"/>/<see cref="EnsureTextMenu"/>（剪切/复制/粘贴/全选），
    ///     以前 AppTheme、InlineRename、启动与恢复各写了一份，现在只留这一份。
    /// </summary>
    internal static class ContextMenus
    {
        /// <summary>
        /// 按顺序造一份菜单；`null` 项会被跳过，连续的 `null`/分隔线不会重复出现。
        /// </summary>
        internal static ContextMenu Create(params ContextMenuItem[] items)
        {
            ContextMenu menu = new ContextMenu();
            if (items == null)
            {
                return menu;
            }
            bool lastWasSeparator = true;   // 开头不要分隔线
            for (int index = 0; index < items.Length; index++)
            {
                ContextMenuItem spec = items[index];
                if (spec == null)
                {
                    continue;
                }
                if (spec.IsSeparator)
                {
                    if (lastWasSeparator)
                    {
                        continue;
                    }
                    menu.Items.Add(new Separator());
                    lastWasSeparator = true;
                    continue;
                }
                MenuItem item = new MenuItem { Header = spec.Header ?? string.Empty };
                Action action = spec.Action;
                if (action != null)
                {
                    item.Click += delegate { action(); };
                }
                else
                {
                    item.IsEnabled = false;
                }
                if (!string.IsNullOrWhiteSpace(spec.Help))
                {
                    AccessibleToolTip.AttachDescription(item, spec.Help, null);
                }
                menu.Items.Add(item);
                lastWasSeparator = false;
            }
            while (menu.Items.Count > 0 && menu.Items[menu.Items.Count - 1] is Separator)
            {
                menu.Items.RemoveAt(menu.Items.Count - 1);
            }
            return menu;
        }

        /// <summary>
        /// 把同一份菜单挂到控件上。右键落在子控件（例如标题文字）时，WPF 会往上找最近的 ContextMenu，
        /// 所以只要挂在外层 Border 上一般就够；这里额外支持多挂几个元素（标题、整行）。
        /// </summary>
        internal static void Attach(FrameworkElement owner, ContextMenu menu, params FrameworkElement[] alsoOn)
        {
            if (menu == null)
            {
                return;
            }
            if (owner != null)
            {
                owner.ContextMenu = menu;
            }
            if (alsoOn == null)
            {
                return;
            }
            for (int index = 0; index < alsoOn.Length; index++)
            {
                if (alsoOn[index] != null)
                {
                    alsoOn[index].ContextMenu = menu;
                }
            }
        }

        /// <summary>
        /// 文本框的标准右键菜单（剪切 / 复制 / 粘贴 / 全选），每次新建一份（CommandTarget 指向这个文本框）。
        /// </summary>
        internal static ContextMenu CreateTextMenu(TextBox box)
        {
            ContextMenu menu = new ContextMenu();
            if (box == null)
            {
                return menu;
            }
            menu.Items.Add(new MenuItem { Header = AppText.T(ShellTextKeys.TextMenuCut), Command = ApplicationCommands.Cut, CommandTarget = box });
            menu.Items.Add(new MenuItem { Header = AppText.T(ShellTextKeys.TextMenuCopy), Command = ApplicationCommands.Copy, CommandTarget = box });
            menu.Items.Add(new MenuItem { Header = AppText.T(ShellTextKeys.TextMenuPaste), Command = ApplicationCommands.Paste, CommandTarget = box });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = AppText.T(ShellTextKeys.TextMenuSelectAll), Command = ApplicationCommands.SelectAll, CommandTarget = box });
            return menu;
        }

        /// <summary>
        /// 文本框还没有自己的右键菜单时才挂上一份标准的（AppTheme 里那个全局钩子用）。
        /// </summary>
        internal static void EnsureTextMenu(TextBox box)
        {
            if (box == null || box.ContextMenu != null)
            {
                return;
            }
            box.ContextMenu = CreateTextMenu(box);
        }

        /// <summary>
        /// 只读可选文字（<see cref="SelectableText"/>）的右键菜单：只给「复制」和「全选」。
        /// 剪切、粘贴对只读文字没有意义，放上去反而让人以为能改。
        /// </summary>
        internal static void EnsureSelectableTextMenu(SelectableText text)
        {
            if (text == null || text.ContextMenu != null)
            {
                return;
            }
            ContextMenu menu = new ContextMenu();
            MenuItem copy = new MenuItem();
            copy.Click += delegate { text.CopySelection(); };
            MenuItem selectAll = new MenuItem();
            selectAll.Click += delegate { text.SelectAllText(); };
            menu.Items.Add(copy);
            menu.Items.Add(new Separator());
            menu.Items.Add(selectAll);
            // 菜单项文字在建菜单时不能写死：语言切换后再次打开必须显示新语言。
            menu.Opened += delegate
            {
                copy.Header = AppText.T(ShellTextKeys.TextMenuCopy);
                copy.IsEnabled = text.SelectionLength > 0;
                selectAll.Header = AppText.T(ShellTextKeys.TextMenuSelectAll);
                selectAll.IsEnabled = !string.IsNullOrEmpty(text.Text);
            };
            text.ContextMenu = menu;
        }
    }
}
