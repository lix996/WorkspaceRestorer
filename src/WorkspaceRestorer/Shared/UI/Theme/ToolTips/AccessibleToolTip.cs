using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    internal static class AccessibleToolTip
    {
        private sealed class DefinitionBinding
        {
            public WeakReference Element;
            public TooltipDefinition Definition;
            public bool DescriptionOnly;
        }

        private static readonly object DefinitionSync = new object();
        private static readonly List<DefinitionBinding> DefinitionBindings = new List<DefinitionBinding>();
        private static bool definitionHookInstalled;

        // Mirrors the ComfyUI tooltip, which caps itself at "max-width: 30vw":
        // 30% of the WINDOW (the viewport), not of the monitor. That keeps the
        // popup small, guarantees it cannot stretch past the UI, and adapts to
        // any screen automatically because the window is already screen sized.
        // 【2026-10-19】提示卡片宽度：窗口宽度的 34%，并夹在 260–420 之间 ——
        // 太窄会一行三四个字，太宽又不精致（用户要求“更高级的外表”）。
        private const double WidthFractionOfWindow = 0.34;

        private const double MinimumContentWidth = 260;

        private const double MaximumContentWidth = 420;

        public static void Attach(FrameworkElement element, string accessibleName, string description)
        {
            Attach(element, accessibleName, description, null);
        }

        /// <summary>
        /// 挂一条语言无关的提示定义。定义只存 key，显示前由 TooltipResolver 解析，
        /// 页面不再出现中文/英文提示文案。
        /// </summary>
        public static void Attach(FrameworkElement element, TooltipDefinition definition)
        {
            if (definition == null)
            {
                return;
            }
            EnsureDefinitionHook();
            Attach(element, TooltipResolver.Resolve(definition));
            RegisterDefinition(element, definition, false);
        }

        /// <summary>
        /// 只挂提示与 HelpText、不覆盖无障碍名字的定义版本。
        /// 用于“文字本身就是内容”的控件（横幅、覆盖层、搜索时间框）。
        /// </summary>
        public static void AttachDescription(FrameworkElement element, TooltipDefinition definition)
        {
            if (definition == null)
            {
                return;
            }
            EnsureDefinitionHook();
            ToolTipContent content = TooltipResolver.Resolve(definition);
            if (content == null)
            {
                return;
            }
            AttachDescription(element, content.Description, content.Suggestion);
            RegisterDefinition(element, definition, true);
        }

        /// <summary>
        /// 挂一条公共内容（推荐）：是否覆盖无障碍名字由 ToolTipContent.SetAccessibleName 决定。
        /// </summary>
        public static void Attach(FrameworkElement element, ToolTipContent content)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }
            if (content == null)
            {
                return;
            }
            AttachContent(element, content, content.SetAccessibleName);
        }

        /// <summary>
        /// 只挂提示/HelpText、不覆盖无障碍名字的公共内容版本。
        /// </summary>
        public static void AttachDescription(FrameworkElement element, ToolTipContent content)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }
            if (content == null)
            {
                return;
            }
            AttachContent(element, content, false);
        }

        private static void RegisterDefinition(
            FrameworkElement element,
            TooltipDefinition definition,
            bool descriptionOnly)
        {
            DefinitionBinding binding = new DefinitionBinding();
            binding.Element = new WeakReference(element);
            binding.Definition = definition;
            binding.DescriptionOnly = descriptionOnly;
            lock (DefinitionSync)
            {
                for (int index = DefinitionBindings.Count - 1; index >= 0; index--)
                {
                    object existing = DefinitionBindings[index].Element.Target;
                    if (existing == null || object.ReferenceEquals(existing, element))
                    {
                        DefinitionBindings.RemoveAt(index);
                    }
                }
                DefinitionBindings.Add(binding);
            }
        }

        private static void EnsureDefinitionHook()
        {
            if (definitionHookInstalled)
            {
                return;
            }
            definitionHookInstalled = true;
            AppText.LanguageChanged += delegate { RefreshDefinitions(); };
        }

        private static void RefreshDefinitions()
        {
            List<DefinitionBinding> alive = new List<DefinitionBinding>();
            lock (DefinitionSync)
            {
                for (int index = DefinitionBindings.Count - 1; index >= 0; index--)
                {
                    if (DefinitionBindings[index].Element.Target == null)
                    {
                        DefinitionBindings.RemoveAt(index);
                    }
                    else
                    {
                        alive.Add(DefinitionBindings[index]);
                    }
                }
            }
            for (int index = 0; index < alive.Count; index++)
            {
                FrameworkElement element = alive[index].Element.Target as FrameworkElement;
                if (element == null)
                {
                    continue;
                }
                ToolTipContent content = TooltipResolver.Resolve(alive[index].Definition);
                if (content == null)
                {
                    continue;
                }
                if (alive[index].DescriptionOnly)
                {
                    AttachDescription(element, content.Description, content.Suggestion);
                }
                else
                {
                    AttachContent(element, content, content.SetAccessibleName);
                }
            }
        }

        private static void AttachContent(FrameworkElement element, ToolTipContent content, bool setAccessibleName)
        {
            element.ToolTip = BuildContent(
                content.Description,
                content.Suggestion,
                element,
                content.Title);
            ToolTipTheme.ApplyDefaults(element);
            AutomationProperties.SetHelpText(
                element,
                string.IsNullOrWhiteSpace(content.HelpText)
                    ? BuildHelpText(content.Description, content.Suggestion)
                    : content.HelpText);
            if (setAccessibleName && !string.IsNullOrWhiteSpace(content.AccessibleName))
            {
                AutomationProperties.SetName(element, content.AccessibleName);
            }
        }

        /// <summary>
        /// 给 DataTemplate 里的 FrameworkElementFactory 挂提示。
        ///
        /// DataTemplate 创建的是模板工厂而不是控件实例，不能直接调 Attach；
        /// 这里把提示文本、无障碍名字/帮助文本和 ToolTipService 参数一次性写进工厂，
        /// 页面里仍然不需要知道 ToolTip 的细节。
        /// </summary>
        internal static void AttachFactory(FrameworkElementFactory factory, TooltipDefinition definition)
        {
            if (definition == null)
            {
                return;
            }
            AttachFactory(factory, TooltipResolver.Resolve(definition));
        }

        internal static void AttachFactory(FrameworkElementFactory factory, ToolTipContent content)
        {
            if (factory == null || content == null)
            {
                return;
            }
            AttachFactory(
                factory,
                content.SetAccessibleName ? content.AccessibleName : null,
                content.Description,
                string.IsNullOrWhiteSpace(content.HelpText)
                    ? BuildHelpText(content.Description, content.Suggestion)
                    : content.HelpText);
        }

        internal static void AttachFactory(
            FrameworkElementFactory factory,
            string accessibleName,
            string description,
            string helpText)
        {
            if (factory == null)
            {
                return;
            }
            factory.SetValue(FrameworkElement.ToolTipProperty, description);
            if (!string.IsNullOrWhiteSpace(accessibleName))
            {
                factory.SetValue(AutomationProperties.NameProperty, accessibleName);
            }
            factory.SetValue(
                AutomationProperties.HelpTextProperty,
                string.IsNullOrWhiteSpace(helpText) ? BuildHelpText(description, null) : helpText);
            factory.SetValue(ToolTipService.PlacementProperty, PlacementMode.Bottom);
            factory.SetValue(ToolTipService.HorizontalOffsetProperty, 4.0);
            factory.SetValue(ToolTipService.InitialShowDelayProperty, ToolTipTheme.InitialShowDelayMilliseconds);
            factory.SetValue(ToolTipService.BetweenShowDelayProperty, ToolTipTheme.BetweenShowDelayMilliseconds);
            factory.SetValue(ToolTipService.ShowDurationProperty, ToolTipTheme.ShowDurationMilliseconds);
            factory.SetValue(ToolTipService.ShowOnDisabledProperty, true);
        }

        /// <summary>
        /// 只挂悬浮提示与“帮助文本”，**不覆盖无障碍名字**。
        ///
        /// 什么时候用：文字本身就是要给读屏听的内容（例如方案卡片第二行
        /// “21 个启动项目 · 北京时间 2026/10/16日 15:30分”）。
        /// `Attach` 会用固定文案当无障碍名字，于是读屏只会念“启动项目数与保存时间”，
        /// 具体的数字和时间反而听不到（2026-10-17 全按钮自测发现；也是老探针 Round1 读不到日期格式的真因）。
        /// 这种元素应该让 UIA 用**它自己的文字**当名字，只额外补一个提示。
        /// </summary>
        public static void AttachDescription(FrameworkElement element, string description, string suggestion)
        {
            AttachDescription(element, description, suggestion, ToolTipTheme.ShowDurationMilliseconds);
        }

        /// <summary>
        /// 同上，但可以指定停留时间（例如恢复完成横幅的长报告要停 60 秒）。
        /// 位置、延迟、字体和卡片外观仍由公共件统一决定。
        /// </summary>
        public static void AttachDescription(
            FrameworkElement element,
            string description,
            string suggestion,
            int showDurationMilliseconds)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }
            element.ToolTip = BuildContent(description, suggestion, element);
            ToolTipTheme.ApplyDefaults(element);
            if (showDurationMilliseconds > 0)
            {
                ToolTipService.SetShowDuration(element, showDurationMilliseconds);
            }
            // 关键：不调用 AutomationProperties.SetName —— 让 UIA 用控件自己的文字当名字。
            AutomationProperties.SetHelpText(element, BuildHelpText(description, suggestion));
        }

        /// <summary>
        /// 和 <see cref="AttachDescription(FrameworkElement,string,string)"/> 一样，
        /// 但把同一句帮助文本写到内部文字块上。
        ///
        /// 用于“点击整行”这类控件：UIA 树里被读到的是行里的 TextBlock，
        /// 只给外层容器写 HelpText，读屏和自检会读不到鼠标悬停时显示的内容。
        /// </summary>
        public static void AttachDescriptionDeep(FrameworkElement element, string description, string suggestion)
        {
            AttachDescription(element, description, suggestion);
            SetHelpTextDeep(element, BuildHelpText(description, suggestion));
        }

        /// <summary>
        /// 带“建议”的悬浮提示（用户要求 2026-10-10 15:56：
        /// “自启动应用的悬浮提示要有建议”）。
        ///
        /// 说明文字讲“这个控件做什么”，建议文字讲“**你最好怎么做**”，
        /// 在弹窗里以单独一行 `建议：…` 显示（用强调色，和说明区分开），
        /// 无障碍帮助文本里也会带上，读屏用户同样听得到。
        /// </summary>
        public static void Attach(FrameworkElement element, string accessibleName, string description, string suggestion)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }

            // Let ToolTipService own the popup lifetime.  Manually toggling a
            // ToolTip.IsOpen while the service has a pending hover timer can
            // make PresentationFramework throw when a window is hidden or
            // destroyed during a click.
            element.ToolTip = BuildContent(description, suggestion, element, accessibleName);
            ToolTipTheme.ApplyDefaults(element);
            AutomationProperties.SetName(element, accessibleName);
            AutomationProperties.SetHelpText(element, BuildHelpText(description, suggestion));
        }

        /// <summary>
        /// 和 <see cref="Attach(FrameworkElement,string,string,string)"/> 一样，但悬浮提示的**标题**
        /// 可以单独给（例如问号按钮：读屏名字是“查看「CPU 上限」的说明”，气泡标题只要“CPU 上限”）。
        /// </summary>
        public static void Attach(FrameworkElement element, string accessibleName, string tooltipTitle, string description, string suggestion)
        {
            if (element == null)
            {
                throw new ArgumentNullException("element");
            }
            element.ToolTip = BuildContent(description, suggestion, element, tooltipTitle);
            ToolTipTheme.ApplyDefaults(element);
            AutomationProperties.SetName(element, accessibleName);
            AutomationProperties.SetHelpText(element, BuildHelpText(description, suggestion));
        }

        /// <summary>无障碍帮助文本 = 说明 + 建议（读屏用户也能听到建议）。</summary>
        internal static string BuildHelpText(string description, string suggestion)
        {
            if (string.IsNullOrWhiteSpace(suggestion))
            {
                return description;
            }
            string suggestionText = AppText.F(CommonTextKeys.SuggestionFormat, suggestion);
            if (string.IsNullOrWhiteSpace(description))
            {
                return suggestionText;
            }
            return description + " " + suggestionText;
        }

        /// <summary>把帮助文本写进内部所有 TextBlock（点击整行时 UIA 读到的是这些文字块）。</summary>
        private static void SetHelpTextDeep(DependencyObject root, string text)
        {
            if (root == null || string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            TextBlock block = root as TextBlock;
            if (block != null)
            {
                try
                {
                    AutomationProperties.SetHelpText(block, text);
                }
                catch
                {
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                SetHelpTextDeep(VisualTreeHelper.GetChild(root, index), text);
            }
        }

        /// <summary>
        /// Builds a compact hover popup: it hugs its text and only wraps once the
        /// text would exceed 30% of the owning window's width.
        /// </summary>
        internal static object BuildContent(string description)
        {
            return BuildContent(description, null, null);
        }

        internal static object BuildContent(string description, FrameworkElement owner)
        {
            return BuildContent(description, null, owner);
        }

        internal static object BuildContent(string description, string suggestion, FrameworkElement owner)
        {
            return BuildContent(description, suggestion, owner, null);
        }

        /// <summary>
        /// 悬浮提示的内容：**标题（可选）+ 说明 + 建议**。
        ///
        /// 外观由 `ToolTipTheme` 负责（白色圆角卡片 + 投影），
        /// 这里只排内容：标题加粗一行、说明正常色、建议用强调色并标“建议：”，
        /// 一眼能分清“这是什么 / 有什么影响 / 推荐怎么填”。
        /// </summary>
        internal static object BuildContent(string description, string suggestion, FrameworkElement owner, string title)
        {
            if (string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(suggestion))
            {
                return string.Empty;
            }

            StackPanel content = new StackPanel();
            double width = ResolveContentWidth(owner);
            // 【2026-11-19】提示里的文字对齐不跟宿主控件走：数字框是 Center、命名框是 Right，
            // 会被继承进提示，把标题/正文/建议带偏。有粗体标题的提示统一左对齐（和参考图一致）；
            // 没有标题的提示保持现状，不强制。
            bool forceLeft = !string.IsNullOrWhiteSpace(title);
            if (forceLeft)
            {
                content.Children.Add(new TextBlock
                {
                    Text = title,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Left,
                    MaxWidth = width,
                    FontFamily = AppFonts.Ui,
                    FontSize = AppFonts.TooltipTitle,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = AppTheme.TextPrimary,
                    LineHeight = 18,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
            if (!string.IsNullOrWhiteSpace(description))
            {
                TextBlock descriptionBlock = new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = width,
                    FontFamily = AppFonts.Ui,
                    FontSize = AppFonts.TooltipBody,
                    Foreground = AppTheme.TextPrimary,
                    LineHeight = 18
                };
                if (forceLeft)
                {
                    descriptionBlock.TextAlignment = TextAlignment.Left;
                }
                content.Children.Add(descriptionBlock);
            }
            if (!string.IsNullOrWhiteSpace(suggestion))
            {
                TextBlock suggestionBlock = new TextBlock
                {
                    Text = AppText.F(CommonTextKeys.SuggestionFormat, suggestion),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = width,
                    FontFamily = AppFonts.Ui,
                    FontSize = AppFonts.TooltipHint,
                    LineHeight = 17,
                    // 用强调色，和上面的“说明”明显分开，一眼能看出哪句是建议。
                    Foreground = AppTheme.Accent,
                    Margin = new Thickness(0, string.IsNullOrWhiteSpace(description) ? 0 : 5, 0, 0)
                };
                if (forceLeft)
                {
                    suggestionBlock.TextAlignment = TextAlignment.Left;
                }
                content.Children.Add(suggestionBlock);
            }
            ToolTip tip = new ToolTip
            {
                Content = content
            };
            // While a panel is being constructed the element has no window yet,
            // so the width above can only be a guess (it falls back to the
            // screen). Recompute from the real window when the popup opens, so
            // the cap is always 30% of the window that is actually on screen.
            tip.Opened += delegate
            {
                try
                {
                    double current = ResolveContentWidth(owner);
                    for (int index = 0; index < content.Children.Count; index++)
                    {
                        TextBlock block = content.Children[index] as TextBlock;
                        if (block != null && current > 0 && Math.Abs(current - block.MaxWidth) > 0.5)
                        {
                            block.MaxWidth = current;
                        }
                    }
                }
                catch
                {
                }
            };
            return tip;
        }

        private static double ResolveContentWidth(FrameworkElement owner)
        {
            // Device independent units, exactly like the CSS viewport width.
            double windowWidth = 0;
            try
            {
                Window window = owner == null ? null : Window.GetWindow(owner);
                if (window == null && Application.Current != null)
                {
                    // The element is not in the tree yet while a panel is being
                    // constructed; the main window is the closest equivalent.
                    window = Application.Current.MainWindow;
                }
                if (window != null)
                {
                    windowWidth = window.ActualWidth;
                }
            }
            catch
            {
            }

            if (windowWidth <= 1)
            {
                windowWidth = SystemParameters.WorkArea.Width;
            }
            if (double.IsNaN(windowWidth) || double.IsInfinity(windowWidth) || windowWidth <= 1)
            {
                return MinimumContentWidth;
            }

            double contentWidth = windowWidth * WidthFractionOfWindow;
            if (double.IsNaN(contentWidth) || double.IsInfinity(contentWidth) || contentWidth < MinimumContentWidth)
            {
                return MinimumContentWidth;
            }
            if (contentWidth > MaximumContentWidth)
            {
                return MaximumContentWidth;
            }
            return contentWidth;
        }
    }
}
