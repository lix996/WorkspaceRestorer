using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【公共区域 · 2026-10-19 用户要求】可选中复制的只读文字。
    ///
    /// 2026-11-15 P1：从只读 TextBox 改为 TextBlock 实现。
    /// 原因：设置页有 40 多个这种文字控件，TextBox 在语言切换时第一次换文字
    /// 会触发昂贵的文本编辑/布局路径（每个约 30–65ms），导致启动后第一次切语言
    /// 卡约 1 秒。TextBlock 没有编辑层，换文字只走普通文本布局。
    ///
    /// 保留原来承诺的行为：
    ///   · 鼠标拖选、Ctrl+C、右键「复制 / 全选」；
    ///   · 点空白处取消所有选中；
    ///   · 读屏/自动化仍把它报成一段 Text，而不是输入框。
    /// </summary>
    internal sealed class SelectableText : TextBlock
    {
        /// <summary>活着的实例（弱引用）：点空白时要一次性把**所有**文字的选中都清掉。</summary>
        private static readonly List<WeakReference> LiveTexts = new List<WeakReference>();

        private static bool cancelHandlerInstalled;

        private TextPointer selectionAnchor;
        private TextPointer selectionEnd;
        private bool dragging;
        private AdornerLayer adornerLayer;
        private SelectionAdorner selectionAdorner;
        private DependencyPropertyDescriptor textDescriptor;

        public SelectableText()
        {
            Focusable = true;
            // 不抢 Tab 顺序：键盘 Tab 仍然按按钮走，不停在说明文字上。
            KeyboardNavigation.SetIsTabStop(this, false);
            Cursor = Cursors.IBeam;
            // 透明背景让整块文字区域都能接收鼠标，而不是只有字形本身。
            Background = Brushes.Transparent;
            Padding = new Thickness(0);
            TextWrapping = TextWrapping.Wrap;
            VerticalAlignment = VerticalAlignment.Center;
            // 不用系统虚线焦点框：这是文字，不是可操作控件。
            FocusVisualStyle = null;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            HookTextChanged();
            SizeChanged += delegate
            {
                if (selectionAdorner != null)
                {
                    selectionAdorner.InvalidateVisual();
                }
            };

            lock (LiveTexts)
            {
                LiveTexts.Add(new WeakReference(this));
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            HookTextChanged();
            // 右键菜单按需挂一次；文字随语言变化时，菜单在打开时再刷新 Header。
            Shared.ContextMenus.EnsureSelectableTextMenu(this);
            if (HasSelection)
            {
                EnsureSelectionAdorner();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs args)
        {
            dragging = false;
            RemoveSelectionAdorner();
            ClearSelection();
            if (textDescriptor != null)
            {
                textDescriptor.RemoveValueChanged(this, OnTextValueChanged);
                textDescriptor = null;
            }
        }

        private void HookTextChanged()
        {
            if (textDescriptor != null)
            {
                return;
            }
            textDescriptor = DependencyPropertyDescriptor.FromProperty(
                TextBlock.TextProperty,
                typeof(SelectableText));
            if (textDescriptor != null)
            {
                textDescriptor.AddValueChanged(this, OnTextValueChanged);
            }
        }

        private void OnTextValueChanged(object sender, EventArgs e)
        {
            // 文字被 LocalizedText / 页面重新赋值后，旧 TextPointer 已经失效。
            ClearSelection();
        }

        /// <summary>当前选中的字符数；没有选中时为 0。</summary>
        internal int SelectionLength
        {
            get { return SelectedText.Length; }
        }

        /// <summary>当前选中的文字；没有选中时为空串。</summary>
        internal string SelectedText
        {
            get
            {
                TextRange range = GetSelectionRange();
                if (range == null)
                {
                    return string.Empty;
                }
                // TextBlock 的 TextRange 会带一个段尾换行符，复制/计数时不要把它算进去。
                string value = range.Text;
                return string.IsNullOrEmpty(value)
                    ? string.Empty
                    : value.TrimEnd('\r', '\n');
            }
        }

        internal bool HasSelection
        {
            get
            {
                TextPointer start = NormalizePosition(selectionAnchor);
                TextPointer end = NormalizePosition(selectionEnd);
                // 选区锚点可以在终点之后（从右往左拖）：这里只判断“两个位置是否
                // 不同”，排序交给 GetSelectionRange。不能再写 start < end，否则
                // 反向拖选会被当成空选区，鼠标一松就被 ClearSelection 清掉。
                return start != null
                    && end != null
                    && start.CompareTo(end) != 0;
            }
        }

        /// <summary>按字符偏移选择一段文字；供 InfoDialog 预热/清空使用，也便于测试。</summary>
        internal void Select(int start, int length)
        {
            if (ContentStart == null || ContentEnd == null)
            {
                return;
            }
            int total = Text == null ? 0 : Text.Length;
            if (total <= 0)
            {
                ClearSelection();
                return;
            }
            if (start < 0)
            {
                start = 0;
            }
            if (start > total)
            {
                start = total;
            }
            if (length < 0)
            {
                length = 0;
            }
            if (length > total - start)
            {
                length = total - start;
            }

            // TextBlock 的 ContentStart 前面还有一个段落边界符号；字符串索引 0
            // 实际对应 offset 1，所以这里统一 +1。
            TextPointer startPointer = ContentStart.GetPositionAtOffset(
                start + 1,
                LogicalDirection.Forward);
            if (startPointer == null)
            {
                return;
            }
            TextPointer endPointer = length == 0
                ? startPointer
                : ContentStart.GetPositionAtOffset(start + length + 1, LogicalDirection.Forward);
            if (endPointer == null)
            {
                endPointer = ContentEnd;
            }
            SetSelection(startPointer, endPointer);
        }

        internal void SelectAllText()
        {
            if (ContentStart == null || ContentEnd == null || string.IsNullOrEmpty(Text))
            {
                ClearSelection();
                return;
            }
            SetSelection(ContentStart, ContentEnd);
        }

        /// <summary>
        /// 选中第一视觉行。供说明框预热使用；不再写死“前 8 个字符”，
        /// 选区会跟随实际字体、换行和宽度。
        /// </summary>
        internal void SelectFirstLine()
        {
            if (ContentStart == null || ContentEnd == null || string.IsNullOrEmpty(Text))
            {
                ClearSelection();
                return;
            }
            TextPointer start = ContentStart.GetLineStartPosition(0);
            if (start == null)
            {
                start = ContentStart;
            }
            TextPointer end = start.GetLineStartPosition(1);
            if (end == null)
            {
                end = ContentEnd;
            }
            SetSelection(start, end);
        }

        internal void CopySelection()
        {
            string selected = SelectedText;
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }
            try
            {
                Clipboard.SetText(selected);
            }
            catch (Exception)
            {
                // 剪贴板被别的程序占用时不能影响界面。
            }
        }

        internal void ClearSelection()
        {
            bool hadSelection = selectionAnchor != null || selectionEnd != null;
            selectionAnchor = null;
            selectionEnd = null;
            dragging = false;
            if (hadSelection)
            {
                InvalidateSelectionVisual();
            }
        }

        private void ClearSelectionOnly()
        {
            if (!HasSelection)
            {
                return;
            }
            selectionAnchor = null;
            selectionEnd = null;
            InvalidateSelectionVisual();
        }

        /// <summary>
        /// 在 `Window` 上装一个全局左键 Preview 处理：**点空白＝取消选中**。
        /// 由 `AppTheme.Apply` 调一次（全程序只装一次）；主窗口、弹窗、搜索窗都覆盖。
        /// </summary>
        internal static void InstallSelectionCancelHandler()
        {
            if (cancelHandlerInstalled)
            {
                return;
            }
            cancelHandlerInstalled = true;
            EventManager.RegisterClassHandler(
                typeof(Window),
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnWindowPreviewMouseLeftButtonDown),
                true);
        }

        /// <summary>清掉所有文字的选中高亮（点空白、点别的控件时用）。</summary>
        internal static void ClearAllSelections()
        {
            List<SelectableText> alive = CollectAlive();
            for (int index = 0; index < alive.Count; index++)
            {
                alive[index].ClearSelectionOnly();
            }
        }

        /// <summary>清掉除 `keep` 以外所有文字的选中（点到另一段文字时，只留它自己的高亮）。</summary>
        private static void ClearSelectionsExcept(SelectableText keep)
        {
            List<SelectableText> alive = CollectAlive();
            for (int index = 0; index < alive.Count; index++)
            {
                SelectableText text = alive[index];
                if (!object.ReferenceEquals(text, keep))
                {
                    text.ClearSelectionOnly();
                }
            }
        }

        private static List<SelectableText> CollectAlive()
        {
            List<SelectableText> alive = new List<SelectableText>();
            lock (LiveTexts)
            {
                for (int index = LiveTexts.Count - 1; index >= 0; index--)
                {
                    SelectableText text = LiveTexts[index].Target as SelectableText;
                    if (text == null)
                    {
                        LiveTexts.RemoveAt(index);
                        continue;
                    }
                    alive.Add(text);
                }
            }
            return alive;
        }

        private static void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e == null || e.ChangedButton != MouseButton.Left)
            {
                return;
            }
            DependencyObject source = e.OriginalSource as DependencyObject;
            SelectableText clickedText = FindAncestor<SelectableText>(source);
            if (clickedText != null)
            {
                // 点在文字自己身上：它自己的选中交给鼠标拖选，只清掉别的文字的旧高亮。
                ClearSelectionsExcept(clickedText);
                return;
            }

            // 点在任何不是文字的地方（空白、卡片、按钮、列表行…）＝取消所有文字的选中。
            ClearAllSelections();

            // 顺手把键盘焦点从只读文字上收掉：否则它会一直“拿着”焦点，全局 Ctrl+C / Ctrl+V
            // 全被它吃掉，用户会觉得操作很卡（用户 2026-10-19 原话）。
            if (Keyboard.FocusedElement is SelectableText)
            {
                Keyboard.ClearFocus();
            }
        }

        /// <summary>顺着视觉树往上找第一个 `T`（找不到就顺着逻辑树再找一次）。</summary>
        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                T match = node as T;
                if (match != null)
                {
                    return match;
                }
                DependencyObject parent = null;
                try
                {
                    parent = VisualTreeHelper.GetParent(node);
                }
                catch (Exception)
                {
                    parent = null;
                }
                if (parent == null)
                {
                    try
                    {
                        parent = LogicalTreeHelper.GetParent(node);
                    }
                    catch (Exception)
                    {
                        parent = null;
                    }
                }
                node = parent;
            }
            return null;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            if (e == null)
            {
                return;
            }
            Focus();
            if (e.ClickCount == 2)
            {
                SelectWordAt(e.GetPosition(this));
                e.Handled = true;
                return;
            }

            TextPointer position = GetPositionFromPointSafe(e.GetPosition(this));
            if (position == null)
            {
                return;
            }
            dragging = true;
            SetSelection(position, position);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging || e == null || e.LeftButton != MouseButtonState.Pressed)
            {
                return;
            }
            TextPointer position = GetPositionFromPointSafe(e.GetPosition(this));
            if (position == null)
            {
                return;
            }
            selectionEnd = position;
            InvalidateSelectionVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!dragging)
            {
                return;
            }
            dragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
            if (!HasSelection)
            {
                ClearSelection();
            }
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            dragging = false;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (e == null)
            {
                return;
            }
            Focus();

            TextPointer position = GetPositionFromPointSafe(e.GetPosition(this));
            if (position == null)
            {
                return;
            }
            // 右键落在已有选区内：保留选区，复制仍然可用；落在选区外：
            // 先清旧选区，再按鼠标下的实际文字位置选整词，不再使用固定偏移。
            if (SelectionContains(position))
            {
                return;
            }
            ClearSelection();
            SelectWordAt(position);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e == null)
            {
                return;
            }
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (control && e.Key == Key.C)
            {
                if (HasSelection)
                {
                    CopySelection();
                    e.Handled = true;
                }
                return;
            }
            if (control && e.Key == Key.A)
            {
                SelectAllText();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && HasSelection)
            {
                ClearSelection();
                e.Handled = true;
            }
        }

        private TextPointer GetPositionFromPointSafe(Point point)
        {
            try
            {
                return NormalizePosition(GetPositionFromPoint(point, true));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 鼠标命中的 TextPointer 可能带 Backward 方向；反向拖选时把它直接存进
        /// 选区，会让 TextRange/行首计算走不同路径。统一成 Forward，方向由
        /// CompareTo/TextRange 决定，而不是由指针自带的 LogicalDirection 决定。
        /// </summary>
        private static TextPointer NormalizePosition(TextPointer position)
        {
            if (position == null)
            {
                return null;
            }
            try
            {
                TextPointer normalized = position.GetPositionAtOffset(0, LogicalDirection.Forward);
                return normalized ?? position;
            }
            catch (Exception)
            {
                return position;
            }
        }

        private bool SelectionContains(TextPointer position)
        {
            if (position == null)
            {
                return false;
            }
            TextRange range = GetSelectionRange();
            return range != null
                && position.CompareTo(range.Start) >= 0
                && position.CompareTo(range.End) <= 0;
        }

        private void SelectWordAt(Point point)
        {
            TextPointer position = GetPositionFromPointSafe(point);
            if (position != null)
            {
                SelectWordAt(position);
            }
        }

        private void SelectWordAt(TextPointer position)
        {
            if (position == null || ContentStart == null || string.IsNullOrEmpty(Text))
            {
                return;
            }
            int offset = ContentStart.GetOffsetToPosition(position);
            if (offset < 0)
            {
                offset = 0;
            }
            if (offset > Text.Length)
            {
                offset = Text.Length;
            }
            int start = offset;
            while (start > 0 && IsWordCharacter(Text[start - 1]))
            {
                start--;
            }
            int end = offset;
            while (end < Text.Length && IsWordCharacter(Text[end]))
            {
                end++;
            }
            if (start < end)
            {
                Select(start, end - start);
            }
        }

        private static bool IsWordCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private void SetSelection(TextPointer start, TextPointer end)
        {
            selectionAnchor = NormalizePosition(start);
            selectionEnd = NormalizePosition(end);
            if (selectionAnchor == null || selectionEnd == null)
            {
                selectionAnchor = null;
                selectionEnd = null;
                return;
            }
            EnsureSelectionAdorner();
            InvalidateSelectionVisual();
        }

        private TextRange GetSelectionRange()
        {
            TextPointer start = NormalizePosition(selectionAnchor);
            TextPointer end = NormalizePosition(selectionEnd);
            if (start == null || end == null)
            {
                return null;
            }
            try
            {
                return start.CompareTo(end) <= 0
                    ? new TextRange(start, end)
                    : new TextRange(end, start);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void EnsureSelectionAdorner()
        {
            if (adornerLayer != null)
            {
                return;
            }
            try
            {
                adornerLayer = AdornerLayer.GetAdornerLayer(this);
                if (adornerLayer == null)
                {
                    return;
                }
                selectionAdorner = new SelectionAdorner(this);
                adornerLayer.Add(selectionAdorner);
            }
            catch (Exception)
            {
                adornerLayer = null;
                selectionAdorner = null;
            }
        }

        private void RemoveSelectionAdorner()
        {
            try
            {
                if (adornerLayer != null && selectionAdorner != null)
                {
                    adornerLayer.Remove(selectionAdorner);
                }
            }
            catch (Exception)
            {
            }
            adornerLayer = null;
            selectionAdorner = null;
        }

        private void InvalidateSelectionVisual()
        {
            if (selectionAdorner != null)
            {
                selectionAdorner.InvalidateVisual();
            }
        }

        /// <summary>无障碍：仍然报成 Text，而不是输入框。</summary>
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new SelectableTextAutomationPeer(this);
        }

        private sealed class SelectableTextAutomationPeer : TextBlockAutomationPeer
        {
            public SelectableTextAutomationPeer(SelectableText owner)
                : base(owner)
            {
            }

            protected override AutomationControlType GetAutomationControlTypeCore()
            {
                return AutomationControlType.Text;
            }

            protected override string GetClassNameCore()
            {
                return "SelectableText";
            }

            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
                SelectableText owner = Owner as SelectableText;
                return owner == null ? string.Empty : (owner.Text ?? string.Empty);
            }
        }

        /// <summary>在文字上画半透明选中底色；不参与命中测试，不影响父级点击。</summary>
        private sealed class SelectionAdorner : Adorner
        {
            private readonly SelectableText owner;

            public SelectionAdorner(SelectableText owner)
                : base(owner)
            {
                this.owner = owner;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                if (drawingContext == null || owner == null || !owner.HasSelection)
                {
                    return;
                }
                TextRange range = owner.GetSelectionRange();
                if (range == null || range.Start.CompareTo(range.End) >= 0)
                {
                    return;
                }

                Color accent = AppTheme.Accent.Color;
                SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(72, accent.R, accent.G, accent.B));
                brush.Freeze();

                // 不能按字符逐块画：行尾的下一个插入点已经在新行，按 X 差算宽度会
                // 只剩几个像素，导致每行最后一个字看起来没选中。这里按视觉行裁剪，
                // 一行只画一个连续矩形，空格、标点和行尾都由整行覆盖。
                TextPointer lineStart = range.Start.GetLineStartPosition(0);
                if (lineStart == null)
                {
                    lineStart = range.Start;
                }
                int guard = 0;
                while (lineStart != null
                    && lineStart.CompareTo(range.End) < 0
                    && guard++ < 1000)
                {
                    TextPointer nextLine = lineStart.GetLineStartPosition(1);
                    TextPointer lineEnd = nextLine ?? owner.ContentEnd;
                    TextPointer start = range.Start.CompareTo(lineStart) > 0
                        ? range.Start
                        : lineStart;
                    TextPointer end = range.End.CompareTo(lineEnd) < 0
                        ? range.End
                        : lineEnd;
                    if (start.CompareTo(end) < 0)
                    {
                        Rect rect = GetLineRect(start, end);
                        if (!rect.IsEmpty && rect.Width > 0.0 && rect.Height > 0.0)
                        {
                            drawingContext.DrawRectangle(brush, null, rect);
                        }
                    }
                    if (nextLine == null)
                    {
                        break;
                    }
                    lineStart = nextLine;
                }
            }

            /// <summary>
            /// 选中区间在一行内的连续矩形。start 是行内第一个字，end 是行内
            /// 最后一个字之后的位置；用 Backward 取最后一个字的真实右边界。
            /// </summary>
            private Rect GetLineRect(TextPointer start, TextPointer end)
            {
                Rect startRect = start.GetCharacterRect(LogicalDirection.Forward);
                if (startRect.IsEmpty)
                {
                    TextPointer first = start.GetPositionAtOffset(1, LogicalDirection.Forward);
                    if (first != null)
                    {
                        startRect = first.GetCharacterRect(LogicalDirection.Forward);
                    }
                }
                if (startRect.IsEmpty)
                {
                    startRect = end.GetCharacterRect(LogicalDirection.Backward);
                }
                if (startRect.IsEmpty)
                {
                    return Rect.Empty;
                }

                TextRange lineRange = new TextRange(start, end);
                string lineText = lineRange.Text;
                lineText = lineText == null ? string.Empty : lineText.TrimEnd('\r', '\n');
                double width = 0.0;
                if (!string.IsNullOrEmpty(lineText))
                {
                    try
                    {
                        FormattedText formatted = new FormattedText(
                            lineText,
                            CultureInfo.CurrentUICulture,
                            owner.FlowDirection,
                            new Typeface(owner.FontFamily, owner.FontStyle, owner.FontWeight, owner.FontStretch),
                            owner.FontSize,
                            Brushes.Black,
                            VisualTreeHelper.GetDpi(owner).PixelsPerDip);
                        width = formatted.WidthIncludingTrailingWhitespace;
                    }
                    catch (Exception)
                    {
                        width = 0.0;
                    }
                }

                // 先按“本行实际选中的文字”量宽度：行尾最后一个字不再依赖
                // 下一行行首的插入点矩形，也不会只画 4px。
                if (width > 0.0)
                {
                    double left = owner.FlowDirection == FlowDirection.RightToLeft
                        ? startRect.Right - width
                        : startRect.Left;
                    return new Rect(left, startRect.Top, width, startRect.Height);
                }

                // 极端情况下（例如本行只有不可见内容）退回字符矩形。
                Rect endRect = end.GetCharacterRect(LogicalDirection.Backward);
                if (endRect.IsEmpty)
                {
                    TextPointer last = end.GetPositionAtOffset(-1, LogicalDirection.Backward);
                    if (last != null && last.CompareTo(start) >= 0)
                    {
                        endRect = last.GetCharacterRect(LogicalDirection.Forward);
                    }
                }
                if (endRect.IsEmpty)
                {
                    return Rect.Empty;
                }
                double fallbackLeft = Math.Min(startRect.Left, endRect.Left);
                double fallbackRight = Math.Max(startRect.Right, endRect.Right);
                double fallbackTop = Math.Min(startRect.Top, endRect.Top);
                double fallbackHeight = Math.Max(startRect.Height, endRect.Height);
                if (fallbackRight <= fallbackLeft)
                {
                    fallbackRight = fallbackLeft + 1.0;
                }
                return new Rect(
                    fallbackLeft,
                    fallbackTop,
                    fallbackRight - fallbackLeft,
                    fallbackHeight);
            }
        }
    }
}
