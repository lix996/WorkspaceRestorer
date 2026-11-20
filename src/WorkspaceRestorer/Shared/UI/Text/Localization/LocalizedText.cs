using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 让已经创建的 WPF 控件跟着当前语言即时更新。
    ///
    /// 页面构建时用 Text/Content/Title/AccessibleName 登记 key 和动态参数；
    /// 语言切换后 AppText 调用 RefreshAll，这里只重设属性，不重建窗口、不丢控件状态。
    ///
    /// 绑定表只持有控件的弱引用，窗口关闭后对应条目会在下一次刷新时自动清掉。
    /// </summary>
    internal static class LocalizedText
    {
        private sealed class Binding
        {
            public WeakReference Target;
            public DependencyProperty Property;
            public string Key;
            public object[] Arguments;
        }

        private static readonly object Sync = new object();
        private static readonly List<Binding> Bindings = new List<Binding>();

        internal static void Text(TextBlock target, string key, params object[] args)
        {
            Bind(target, TextBlock.TextProperty, key, args);
        }

        internal static void Text(TextBox target, string key, params object[] args)
        {
            Bind(target, TextBox.TextProperty, key, args);
        }

        internal static void Content(ContentControl target, string key, params object[] args)
        {
            Bind(target, ContentControl.ContentProperty, key, args);
        }

        internal static void Title(Window target, string key, params object[] args)
        {
            Bind(target, Window.TitleProperty, key, args);
        }

        internal static void AccessibleName(FrameworkElement target, string key, params object[] args)
        {
            Bind(target, AutomationProperties.NameProperty, key, args);
        }

        /// <summary>语言切换后由 AppText 调用；只更新还活着的控件。</summary>
        internal static void RefreshAll()
        {
            List<Binding> alive = new List<Binding>();
            lock (Sync)
            {
                for (int index = Bindings.Count - 1; index >= 0; index--)
                {
                    Binding binding = Bindings[index];
                    if (binding.Target.Target == null)
                    {
                        Bindings.RemoveAt(index);
                    }
                    else
                    {
                        alive.Add(binding);
                    }
                }
            }

            for (int index = 0; index < alive.Count; index++)
            {
                Apply(alive[index]);
            }
        }

        private static void Bind(
            DependencyObject target,
            DependencyProperty property,
            string key,
            object[] args)
        {
            if (target == null)
            {
                throw new ArgumentNullException("target");
            }
            if (property == null)
            {
                throw new ArgumentNullException("property");
            }
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            Binding binding = new Binding();
            binding.Target = new WeakReference(target);
            binding.Property = property;
            binding.Key = key;
            binding.Arguments = args;

            lock (Sync)
            {
                for (int index = Bindings.Count - 1; index >= 0; index--)
                {
                    object existing = Bindings[index].Target.Target;
                    if (existing == null)
                    {
                        Bindings.RemoveAt(index);
                        continue;
                    }
                    if (object.ReferenceEquals(existing, target)
                        && Bindings[index].Property == property)
                    {
                        Bindings.RemoveAt(index);
                    }
                }
                Bindings.Add(binding);
            }
            Apply(binding);
        }

        private static void Apply(Binding binding)
        {
            DependencyObject target = binding.Target.Target as DependencyObject;
            if (target == null)
            {
                return;
            }
            string value = binding.Arguments == null || binding.Arguments.Length == 0
                ? AppText.T(binding.Key)
                : AppText.F(binding.Key, binding.Arguments);
            target.SetValue(binding.Property, value);
        }
    }
}
