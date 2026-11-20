using System;
using System.Collections.Generic;
using System.Windows;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 一张方案卡片在拖动时要用的两个元素：
    ///  · Header = 标题条：槽位、目标高亮框、起拖锚点；
    ///  · Block  = 含展开子卡片的外层整块：禁止区、腾开空间。
    ///
    /// 这两个元素必须分开登记。自启动应用 2026-11-01 的 bug 就是把 Block 当成了卡片本身，
    /// 父方案的槽位覆盖整棵子树，子卡片永远轮不到。
    /// </summary>
    internal sealed class DragCardElement
    {
        public string Id;
        public FrameworkElement Header;
        public FrameworkElement Block;
    }

    /// <summary>
    /// 【公共拖动 · 卡片元素登记】三个工作区在创建卡片时登记 Header / Block。
    ///
    /// 只做登记和查找，不画控件、不算落点、不改数据；页面刷新列表时先 Clear 再重建。
    /// </summary>
    internal sealed class DragCardElementRegistry
    {
        private readonly Dictionary<string, DragCardElement> byId =
            new Dictionary<string, DragCardElement>(StringComparer.OrdinalIgnoreCase);

        private readonly List<DragCardElement> ordered = new List<DragCardElement>();

        public void Clear()
        {
            byId.Clear();
            ordered.Clear();
        }

        public void Register(string id, FrameworkElement header, FrameworkElement block)
        {
            if (string.IsNullOrWhiteSpace(id) || (header == null && block == null))
            {
                return;
            }

            DragCardElement existing;
            if (byId.TryGetValue(id, out existing))
            {
                existing.Header = header;
                existing.Block = block;
                return;
            }

            DragCardElement element = new DragCardElement();
            element.Id = id;
            element.Header = header;
            element.Block = block;
            byId[id] = element;
            ordered.Add(element);
        }

        public bool TryGet(string id, out DragCardElement element)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                element = null;
                return false;
            }
            return byId.TryGetValue(id, out element);
        }

        public FrameworkElement FindHeader(string id)
        {
            DragCardElement element;
            return TryGet(id, out element) ? element.Header : null;
        }

        public FrameworkElement FindBlock(string id)
        {
            DragCardElement element;
            return TryGet(id, out element) ? element.Block : null;
        }

        /// <summary>按登记顺序返回当前卡片；拖动期间只读。</summary>
        public IList<DragCardElement> Items
        {
            get { return ordered; }
        }
    }
}
