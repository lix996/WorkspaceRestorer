using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 一条“网页归属”选择：某个浏览器窗口里的某个标签。
    /// 只在手动设置归属时使用，不参与自动捕获结果的序列化。
    /// </summary>
    internal sealed class BrowserPageBindingOption
    {
        internal IntPtr WindowHandle { get; set; }

        internal string WindowTitle { get; set; }

        /// <summary>当前枚举顺序（0 开始），只用于显示，不直接当成保存序号。</summary>
        internal int WindowOrdinal { get; set; }

        internal bool WindowMinimized { get; set; }

        internal int TabIndex { get; set; }

        internal string TabName { get; set; }

        internal string TabUrl { get; set; }

        internal bool IsActive { get; set; }

        internal List<BrowserTabFingerprint> Tabs { get; set; }

        internal bool IsRecommended { get; set; }

        /// <summary>同一个窗口里只要有一个标签是推荐项，整窗选项都排到前面。</summary>
        internal bool WindowRecommended { get; set; }

        internal string DisplayText
        {
            get
            {
                string tab = string.IsNullOrWhiteSpace(TabName)
                    ? AppText.T(StartupRestoreTextKeys.FallbackTabTitleUnknown)
                    : TabName;
                return AppText.F(
                    StartupRestoreTextKeys.BrowserBindingOptionFormat,
                    WindowOrdinal + 1,
                    WindowTitle ?? AppText.T(StartupRestoreTextKeys.FallbackBrowserWindowFallback),
                    WindowMinimized
                        ? AppText.T(StartupRestoreTextKeys.BrowserBindingMinimizedSuffix)
                        : string.Empty,
                    TabIndex + 1,
                    tab);
            }
        }
    }

    /// <summary>
    /// “设置网页归属…”对话框：列出当前所有浏览器窗口/标签，让用户明确选一次。
    /// 只负责选，不写方案；写回由两个工作区各自的 Apply… 方法完成。
    /// </summary>
    internal sealed class BrowserPageBindingDialog : Window
    {
        private readonly ListBox list;

        internal BrowserPageBindingDialog(IList<BrowserPageBindingOption> options)
        {
            Title = AppText.T(StartupRestoreTextKeys.BrowserBindingTitle);
            Width = 920;
            Height = 620;
            MinWidth = 720;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            Grid root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock hint = new TextBlock
            {
                Text = AppText.T(StartupRestoreTextKeys.BrowserBindingHint),
                TextWrapping = TextWrapping.Wrap,
                Foreground = AppTheme.TextSecondary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(hint, 0);
            root.Children.Add(hint);

            list = new ListBox
            {
                FontSize = 14,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 12)
            };
            int recommendedIndex = -1;
            if (options != null)
            {
                for (int index = 0; index < options.Count; index++)
                {
                    BrowserPageBindingOption option = options[index];
                    if (option == null)
                    {
                        continue;
                    }
                    ListBoxItem item = new ListBoxItem
                    {
                        Content = option.DisplayText,
                        Tag = option,
                        Padding = new Thickness(10, 8, 10, 8)
                    };
                    if (!string.IsNullOrWhiteSpace(option.TabUrl))
                    {
                        AccessibleToolTip.AttachDescription(item, ToolTipContent.Plain(option.TabUrl));
                    }
                    list.Items.Add(item);
                    if (option.IsRecommended && recommendedIndex < 0)
                    {
                        recommendedIndex = list.Items.Count - 1;
                    }
                }
            }
            list.SelectedIndex = recommendedIndex >= 0 ? recommendedIndex : (list.Items.Count > 0 ? 0 : -1);
            Grid.SetRow(list, 1);
            root.Children.Add(list);

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            Button ok = new Button
            {
                Content = AppText.T(StartupRestoreTextKeys.BrowserBindingUse),
                Width = 180,
                Height = 34,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            Button cancel = new Button
            {
                Content = AppText.T(StartupRestoreTextKeys.BrowserBindingCancel),
                Width = 90,
                Height = 34,
                IsCancel = true
            };
            ok.Click += delegate
            {
                if (list.SelectedItem != null)
                {
                    DialogResult = true;
                }
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);
            Content = root;
        }

        internal BrowserPageBindingOption SelectedOption
        {
            get
            {
                ListBoxItem item = list.SelectedItem as ListBoxItem;
                return item == null ? null : item.Tag as BrowserPageBindingOption;
            }
        }
    }
}
