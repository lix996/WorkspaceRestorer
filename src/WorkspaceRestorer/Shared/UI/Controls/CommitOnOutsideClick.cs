using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 公共的“点空白处结束命名框编辑”：启动与恢复、自启动应用、图标位置保存共用。
    /// 只负责把“编辑状态”结束掉；名字怎么保存仍由各页面自己的回调决定。
    /// </summary>
    internal static class CommitOnOutsideClick
    {
        public static void Attach(FrameworkElement scope, TextBox editor, Action finish)
        {
            if (scope == null || editor == null || finish == null)
            {
                return;
            }
            scope.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (!editor.IsKeyboardFocusWithin)
                {
                    return;
                }
                DependencyObject target = args.OriginalSource as DependencyObject;
                if (target == null || IsInside(editor, target))
                {
                    return;
                }
                finish();
            };
        }

        public static void MoveFocusAfter(TextBox editor)
        {
            if (editor == null)
            {
                return;
            }
            try
            {
                editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            catch
            {
                try { Keyboard.ClearFocus(); } catch { }
            }
        }

        private static bool IsInside(DependencyObject ancestor, DependencyObject node)
        {
            while (node != null)
            {
                if (object.ReferenceEquals(node, ancestor))
                {
                    return true;
                }
                DependencyObject parent = null;
                try
                {
                    parent = VisualTreeHelper.GetParent(node);
                }
                catch
                {
                }
                if (parent == null)
                {
                    FrameworkElement element = node as FrameworkElement;
                    parent = element == null ? null : element.Parent;
                }
                node = parent;
            }
            return false;
        }
    }
}
