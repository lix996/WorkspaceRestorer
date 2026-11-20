using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Cards.Interaction
{
    /// <summary>
    /// 公共的“就地改名”：双击一段文字 → 就地变成输入框 → 回车确认、Esc 取消、点到别处提交。
    ///
    /// 用户要求（2026-10-10 01:41）：“自启动应用，双击也要能够修改名字，或许你可以把这部分代码，
    /// 先复制，放到公共区域，后续‘图标位置保存’也会用上的。”
    ///
    /// 这里只做**界面行为**，不碰业务数据：提交时把新名字通过回调交出去，由调用方决定怎么保存。
    ///
    /// 尺寸约定（照「启动与恢复」踩过的坑）：编辑器宽度**不能**按标题文字的自然宽度算——
    /// 名字一长（例如带时间戳）编辑器就只有原标题那么宽、新名字被截断，看起来像“双击后编辑框缩小了”。
    /// 正确算法：编辑器左边缘到**右侧那排按钮左边缘**之间的距离减 8。
    /// </summary>
    internal static class InlineRename
    {
        /// <summary>窗口级鼠标钩子与它所在的窗口（编辑器收起时要摘掉，免得越挂越多）。</summary>
        private static MouseButtonEventHandler windowHookRef;
        private static Window windowHookHost;

        /// <summary>
        /// 把一段文字变成可就地改名的文字，返回“从代码里开始改名”的动作。
        /// 调用方自己持有这个 Action（例如右键菜单直接捕获它），
        /// 不再放进静态字典，卡片回收时动作跟着回收。
        /// </summary>
        public static Action Attach(TextBlock nameBlock, TextBox editor, int maxLength, Func<string, bool> commit)
        {
            return Attach(nameBlock, editor, maxLength, commit, null);
        }

        /// <summary>
        /// 同上，但可以指定“还在编辑区之内”的容器（例如整张卡片）。
        /// 焦点交到卡片里的别的控件上时**不要**提交——否则会误提交。
        /// </summary>
        public static Action Attach(TextBlock nameBlock, TextBox editor, int maxLength, Func<string, bool> commit, DependencyObject stayInside)
        {
            return Attach(nameBlock, editor, maxLength, commit, stayInside, null);
        }

        /// <summary>
        /// 同上，再指定“双击这一整条标题行也能开始改名”（一般是卡片头部那层 Border/Grid）。
        /// </summary>
        public static Action Attach(
            TextBlock nameBlock, TextBox editor, int maxLength, Func<string, bool> commit,
            DependencyObject stayInside, DependencyObject doubleClickSurface)
        {
            if (nameBlock == null || editor == null || commit == null)
            {
                return null;
            }
            if (maxLength > 0)
            {
                editor.MaxLength = maxLength;
            }
            editor.Visibility = Visibility.Collapsed;
            editor.AcceptsReturn = false;
            editor.TextWrapping = TextWrapping.NoWrap;

            bool renaming = false;

            Action finish = null;
            finish = delegate
            {
                if (!renaming)
                {
                    return;
                }
                renaming = false;
                string name = editor.Text == null ? string.Empty : editor.Text.Trim();
                bool accepted = commit(name);
                editor.Visibility = Visibility.Collapsed;
                nameBlock.Visibility = Visibility.Visible;
                DetachWindowHook();
                if (accepted)
                {
                    nameBlock.Text = name;
                }
            };

            // 点空白处也要提交：WPF 里点在一块**不接收焦点**的 TextBlock 上不会产生失焦事件，
            // 于是“双击改名 → 点旁边空白”会一直停在编辑状态（自检里真的遇到过：改完名字点空白，
            // 名字根本没提交）。所以额外挂一下窗口级的鼠标按下，只要点到编辑区外面就提交。
            MouseButtonEventHandler windowHook = null;
            windowHook = delegate(object sender, MouseButtonEventArgs args)
            {
                if (!renaming)
                {
                    return;
                }
                DependencyObject target = args.OriginalSource as DependencyObject;
                if (target != null)
                {
                    if (IsInside(editor, target))
                    {
                        return;
                    }
                    if (stayInside != null && IsInside(stayInside, target))
                    {
                        return;
                    }
                }
                finish();
            };
            windowHookRef = windowHook;

            Action startRename = delegate
            {
                if (renaming)
                {
                    return;
                }
                BeginRename(nameBlock, editor, delegate { renaming = true; }, finish);
                AttachWindowHook(editor, windowHook);
            };
            nameBlock.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount != 2)
                {
                    return;
                }
                args.Handled = true;
                startRename();
            };
            // 回车/Esc 必须用 **PreviewKeyDown（隧道）** 拦下来。
            // 用普通 KeyDown 时，只要编辑器当时没拿到焦点，回车就会冒泡到下面的按钮上
            // ——自检里真的发生过：在就地改名框里按回车，结果触发了“新建空白方案”，
            // 白白多出一份方案（状态栏写着“已新建自启动方案”）。
            editor.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    args.Handled = true;
                    finish();
                }
                else if (args.Key == Key.Escape)
                {
                    args.Handled = true;
                    // 取消：不改数据、把原文放回去。
                    renaming = false;
                    editor.Text = nameBlock.Text;
                    editor.Visibility = Visibility.Collapsed;
                    nameBlock.Visibility = Visibility.Visible;
                }
            };
            // 焦点离开编辑器就提交（与「启动与恢复」一致：点到别处即确认）。
            // **必须**先判断“新焦点是不是还在同一张卡片里”：UIA/自动化在给输入框设值时会让焦点
            // 短暂跳一下，如果这时就提交，就会用旧文本提交、并留下一个还开着的编辑器
            // （自检里出现过“状态栏写着重命名为旧名字、编辑框里还是新名字”的怪象）。
            editor.LostKeyboardFocus += delegate(object sender, KeyboardFocusChangedEventArgs args)
            {
                if (!renaming)
                {
                    return;
                }
                DependencyObject target = args.NewFocus as DependencyObject;
                if (target != null)
                {
                    if (IsInside(editor, target))
                    {
                        return;
                    }
                    if (stayInside != null && IsInside(stayInside, target))
                    {
                        return;
                    }
                }
                finish();
            };
            // 【2026-10-18】双击**整条标题行**也能改名（不一定正好点在名字文字上）。
            // 只在最外层接一次；标题行里的按钮会先把自己的点击吃掉，不会误触发。
            UIElement doubleClickElement = doubleClickSurface as UIElement;
            if (doubleClickElement != null)
            {
                doubleClickElement.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (args.ClickCount != 2)
                    {
                        return;
                    }
                    args.Handled = true;
                    startRename();
                };
            }
            editor.PreviewMouseLeftButtonDown += delegate { editor.Focus(); };
            // 成熟文本框的行为：按住左键拖到编辑框边缘时，内容自动跟着横向滚动，
            // 这样被挡住的部分也能一路选中。WPF 的 TextBox 默认不做这件事，
            // 这里取模板内的 PART_ContentHost（标准滚动宿主）自己滚。
            // （用户第 48 条要求“双击命名也要向启动与恢复统一”，这就是当初缺的那一条。）
            editor.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }
                TextBox box = sender as TextBox;
                if (box == null || box.Template == null)
                {
                    return;
                }
                ScrollViewer host = box.Template.FindName("PART_ContentHost", box) as ScrollViewer;
                if (host == null || host.ScrollableWidth <= 0.0)
                {
                    return;
                }
                Point point = args.GetPosition(box);
                const double edge = 16.0;
                const double step = 20.0;
                if (point.X > box.ActualWidth - edge)
                {
                    host.ScrollToHorizontalOffset(Math.Min(host.ScrollableWidth, host.HorizontalOffset + step));
                }
                else if (point.X < edge)
                {
                    host.ScrollToHorizontalOffset(Math.Max(0.0, host.HorizontalOffset - step));
                }
            };
            return startRename;
        }

        private static void AttachWindowHook(TextBox editor, MouseButtonEventHandler hook)
        {
            if (hook == null)
            {
                return;
            }
            DetachWindowHook();
            Window window = Window.GetWindow(editor);
            if (window == null)
            {
                return;
            }
            windowHookHost = window;
            window.PreviewMouseLeftButtonDown += hook;
            window.Deactivated += OnWindowDeactivated;
        }

        private static void DetachWindowHook()
        {
            if (windowHookHost == null || windowHookRef == null)
            {
                return;
            }
            try
            {
                windowHookHost.PreviewMouseLeftButtonDown -= windowHookRef;
                windowHookHost.Deactivated -= OnWindowDeactivated;
            }
            catch
            {
            }
            windowHookHost = null;
        }

        private static void OnWindowDeactivated(object sender, EventArgs args)
        {
            // 窗口失去激活（用户点到别的程序）时不要提交，只是别再重复挂。
            DetachWindowHook();
        }

        private static void BeginRename(TextBlock nameBlock, TextBox editor, Action markActive, Action finish)
        {
            markActive();
            editor.Text = nameBlock.Text ?? string.Empty;
            nameBlock.Visibility = Visibility.Collapsed;
            editor.Visibility = Visibility.Visible;
            // 【2026-10-19】编辑框自己的标准右键菜单由公共件提供（否则右键弹出的是卡片菜单）。
            editor.ContextMenu = Shared.ContextMenus.CreateTextMenu(editor);
            ResizeEditor(editor);
            // 【2026-10-18 用户截图“右边圆角被切平”】刚把编辑器从 Collapsed 打开时，
            // 右侧按钮那排的 ActualWidth 往往还是 0 —— 宽度算不出来会退回 MinWidth（看着又窄又怪）。
            // 等这一轮布局排完再量一次；之后若卡片尺寸变化（窗口缩放）也再量一次。
            editor.Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    if (editor.Visibility == Visibility.Visible)
                    {
                        ResizeEditor(editor);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
            editor.Focus();
            editor.SelectAll();
        }

        /// <summary>
        /// 编辑器宽度＝“编辑器左边缘 → 本行右侧按钮左边缘”的距离（减 8）。
        /// 找不到右侧按钮时退回“卡片可用宽度”，不会退化成一个字宽。
        /// </summary>
        private static void ResizeEditor(TextBox editor)
        {
            try
            {
                Border card = FindAncestor<Border>(editor);
                Grid headerRow = FindHeaderRow(editor);
                double width = 0.0;

                // ① 先按“编辑器左边缘 → 右侧按钮面板左边缘”的可用宽度算。
                if (headerRow != null)
                {
                    FrameworkElement actions = headerRow.Children[headerRow.Children.Count - 1] as FrameworkElement;
                    if (actions != null && actions.ActualWidth > 0.0)
                    {
                        Point editorOrigin = editor.TranslatePoint(new Point(0.0, 0.0), headerRow);
                        Point actionsOrigin = actions.TranslatePoint(new Point(0.0, 0.0), headerRow);
                        width = actionsOrigin.X - editorOrigin.X - 8.0;
                    }
                }

                // ② 再用“卡片内边距以内的剩余宽度”封顶。
                // 【2026-10-18 用户报“左边圆角对、右边不对（被切平了）”】真因：
                //    宽度退回了“整卡宽度-176”那个兜底值，比卡片可用宽度还大，
                //    编辑器右边顶出卡片内边距、右圆角被卡片裁掉，看起来只剩左边圆角。
                //    现在无论哪条分支，都再用卡片内边距封一次顶，右圆角一定露在卡片里。
                if (card != null && card.ActualWidth > 0.0)
                {
                    Point cardOrigin = editor.TranslatePoint(new Point(0.0, 0.0), card);
                    double limit = card.ActualWidth - card.Padding.Right - cardOrigin.X - 8.0;
                    if (limit > 0.0 && (width <= 0.0 || limit < width))
                    {
                        width = limit;
                    }
                }

                if (width > 80.0)
                {
                    editor.Width = width;
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 找“卡片头部那一行”的 Grid：从编辑器往上找**第一层有三个以上子元素、且最后一个不是编辑器自己**的 Grid。
        /// 【2026-10-18】原来直接取最近的 Grid —— 编辑器外面就套着一个两格的 nameHost，
        /// 于是“右侧按钮”被当成编辑器自己，宽度算成 0 再退回兜底值，右边就顶出卡片了。
        /// </summary>
        private static Grid FindHeaderRow(DependencyObject start)
        {
            DependencyObject node = start;
            while (node != null)
            {
                Grid grid = node as Grid;
                if (grid != null && grid.Children.Count >= 3)
                {
                    FrameworkElement last = grid.Children[grid.Children.Count - 1] as FrameworkElement;
                    if (last != null && !IsInside(last, start) && !IsInside(start, last))
                    {
                        return grid;
                    }
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            DependencyObject node = start;
            while (node != null)
            {
                T hit = node as T;
                if (hit != null)
                {
                    return hit;
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static bool IsInside(DependencyObject container, DependencyObject candidate)
        {
            DependencyObject node = candidate;
            while (node != null)
            {
                if (object.ReferenceEquals(node, container))
                {
                    return true;
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }
}
