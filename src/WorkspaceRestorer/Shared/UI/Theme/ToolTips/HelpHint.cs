using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【2026-10-19 用户要求】“是不是可以把这种交互代码放置到公共区域，
    /// 所有带有问号的详细介绍，都可以使用对应的代码，这样改的话，只用改一次，就可以作用所有这种代码。”
    ///
    /// 这是全程序**唯一**的“小字 + 问号”交互：
    ///   · 小字是一句话概括（可选中复制）；
    ///   · 问号紧贴在小字后面：悬停出详细悬浮提示（放左侧，不会跑出窗口）；
    ///   · 点击问号弹出 `InfoDialog`：正文可按住左键选中复制，点弹框外面 / 右上角 × / Esc 关闭。
    ///
    /// 设置行、回收站的“操作”说明、快捷键清单、回收站保留天数旁问号都走这里。
    /// 【即时语言】创建时只登记 HelpDefinition；切换语言后 LocalizedText 更新小字，
    /// 本文件重新解析问号的提示、无障碍名字，点击时再解析一次弹框正文。
    /// </summary>
    internal static class HelpHint
    {
        private sealed class HelpBinding
        {
            public WeakReference Button;
            public HelpDefinition Definition;
        }

        private static readonly object Sync = new object();
        private static readonly List<HelpBinding> Bindings = new List<HelpBinding>();
        private static bool hooked;

        /// <summary>公共定义版本：文案在显示前按当前语言解析。</summary>
        public static Button CreateHelpButton(HelpDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }
            EnsureHooked();
            Button help = BuildHelpButtonShell();
            help.Click += delegate
            {
                HelpContent current = TooltipResolver.Resolve(definition);
                Window window = Window.GetWindow(help);
                InfoDialog.Show(window, help, current.Title, current.Detail);
            };
            Register(help, definition);
            ApplyDefinition(help, definition);
            return help;
        }

        /// <summary>公共定义版本：整行“小字 + 问号”。</summary>
        public static Grid BuildHintLine(HelpDefinition definition)
        {
            if (definition == null)
            {
                return null;
            }
            Grid line = BuildHintLineShell();
            if (!string.IsNullOrEmpty(definition.ShortKey))
            {
                SelectableText hintBlock = new SelectableText
                {
                    FontSize = AppFonts.TooltipHint,
                    Foreground = AppTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    // 关键：Auto 列里不能让小字横向 Stretch，否则它会把整行都占满，
                    // 问号被推到最右边甚至被右侧按钮裁掉。左对齐后问号始终紧跟在小字后面。
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 420
                };
                LocalizedText.Text(hintBlock, definition.ShortKey);
                Grid.SetColumn(hintBlock, 0);
                line.Children.Add(hintBlock);
                line.SizeChanged += delegate
                {
                    double limit = Math.Max(120, line.ActualWidth - 30);
                    if (Math.Abs(hintBlock.MaxWidth - limit) > 0.5)
                    {
                        hintBlock.MaxWidth = limit;
                    }
                };
            }
            if (!string.IsNullOrEmpty(definition.DetailKey))
            {
                Button help = CreateHelpButton(definition);
                Grid.SetColumn(help, 1);
                line.Children.Add(help);
            }
            return line;
        }

        /// <summary>公共内容版本：标题、一句话小字、详细说明从 HelpContent 取。</summary>
        public static Button CreateHelpButton(HelpContent content)
        {
            if (content == null)
            {
                return null;
            }
            return CreateHelpButton(content.Title, content.Detail);
        }

        /// <summary>公共内容版本：整行“小字 + 问号”。</summary>
        public static Grid BuildHintLine(HelpContent content)
        {
            if (content == null)
            {
                return null;
            }
            return BuildHintLine(content.Title, content.Short, content.Detail);
        }

        /// <summary>只创建问号按钮（调用方已经自己排好小字时用）。</summary>
        public static Button CreateHelpButton(string title, string detail)
        {
            Button help = BuildHelpButtonShell();
            AccessibleToolTip.Attach(
                help,
                AppText.F(CommonTextKeys.HelpAccessibleNameFormat, title),
                title,
                detail,
                null);
            // 问号一般贴着内容区右边，提示放左边才不会跑出窗口。
            ToolTipService.SetPlacement(help, PlacementMode.Left);
            ToolTipService.SetHorizontalOffset(help, -8);
            help.Click += delegate
            {
                Window window = Window.GetWindow(help);
                InfoDialog.Show(window, help, title, detail);
            };
            return help;
        }

        /// <summary>
        /// 创建一整行“一句话小字 + 紧跟问号”。小字最多占满这一行（超出自动换行），
        /// 问号始终紧贴在小字后面；后面剩余空白由星号列吸收。
        /// </summary>
        public static Grid BuildHintLine(string title, string hint, string detail)
        {
            Grid line = BuildHintLineShell();

            if (!string.IsNullOrWhiteSpace(hint))
            {
                SelectableText hintBlock = new SelectableText
                {
                    Text = hint,
                    FontSize = AppFonts.TooltipHint,
                    Foreground = AppTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    // 关键：Auto 列里不能让小字横向 Stretch，否则它会把整行都占满，
                    // 问号被推到最右边甚至被右侧按钮裁掉。左对齐后问号始终紧跟在小字后面。
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    // 先给个上限，Layout 完再按实际行宽收紧（见下面的 SizeChanged）。
                    MaxWidth = 420
                };
                Grid.SetColumn(hintBlock, 0);
                line.Children.Add(hintBlock);
                line.SizeChanged += delegate
                {
                    double limit = Math.Max(120, line.ActualWidth - 30);
                    if (Math.Abs(hintBlock.MaxWidth - limit) > 0.5)
                    {
                        hintBlock.MaxWidth = limit;
                    }
                };
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                Button help = CreateHelpButton(title, detail);
                Grid.SetColumn(help, 1);
                line.Children.Add(help);
            }
            return line;
        }

        private static Grid BuildHintLineShell()
        {
            Grid line = new Grid { Margin = new Thickness(0, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return line;
        }

        private static Button BuildHelpButtonShell()
        {
            Button help = new Button
            {
                Style = (Style)Application.Current.Resources["HelpButton"],
                Margin = new Thickness(4, 1, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Content = new TextBlock
                {
                    Text = "?",
                    FontSize = AppFonts.HelpGlyph,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTipService.SetPlacement(help, PlacementMode.Left);
            ToolTipService.SetHorizontalOffset(help, -8);
            return help;
        }

        private static void ApplyDefinition(Button help, HelpDefinition definition)
        {
            HelpContent current = TooltipResolver.Resolve(definition);
            AccessibleToolTip.Attach(
                help,
                AppText.F(CommonTextKeys.HelpAccessibleNameFormat, current.Title),
                current.Title,
                current.Detail,
                null);
        }

        private static void Register(Button help, HelpDefinition definition)
        {
            HelpBinding binding = new HelpBinding();
            binding.Button = new WeakReference(help);
            binding.Definition = definition;
            lock (Sync)
            {
                for (int index = Bindings.Count - 1; index >= 0; index--)
                {
                    object existing = Bindings[index].Button.Target;
                    if (existing == null || object.ReferenceEquals(existing, help))
                    {
                        Bindings.RemoveAt(index);
                    }
                }
                Bindings.Add(binding);
            }
        }

        private static void EnsureHooked()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            AppText.LanguageChanged += delegate { RefreshAll(); };
        }

        private static void RefreshAll()
        {
            List<HelpBinding> alive = new List<HelpBinding>();
            lock (Sync)
            {
                for (int index = Bindings.Count - 1; index >= 0; index--)
                {
                    if (Bindings[index].Button.Target == null)
                    {
                        Bindings.RemoveAt(index);
                    }
                    else
                    {
                        alive.Add(Bindings[index]);
                    }
                }
            }
            for (int index = 0; index < alive.Count; index++)
            {
                Button button = alive[index].Button.Target as Button;
                if (button != null)
                {
                    ApplyDefinition(button, alive[index].Definition);
                }
            }
        }
    }
}
