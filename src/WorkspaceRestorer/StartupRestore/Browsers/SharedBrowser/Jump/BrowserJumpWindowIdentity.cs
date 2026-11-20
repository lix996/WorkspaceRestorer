using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 跳转时一个浏览器窗口的“身份事实”。全部来自已经读到的窗口标题和保存数据，
    /// 不碰 HWND、不读 UIA，方便单测。
    /// </summary>
    internal sealed class BrowserJumpWindowFacts
    {
        /// <summary>当前活动页就是保存页，或活动页是保存窗口里的某个标签。</summary>
        public bool ContainsSavedPage;

        /// <summary>当前窗口标题像“点的那条网页的保存父窗口标题”。</summary>
        public bool ParentTitleMatches;

        /// <summary>窗口标题里能读出标签数（“和另外 N 个页面”+1，单页=1）。</summary>
        public bool PageCountKnown;

        /// <summary>当前窗口标签数；未知时为 -1。</summary>
        public int PageCount;
    }

    /// <summary>
    /// 多个窗口都可能装着同一条网页时，应该跳哪一个。
    /// </summary>
    internal enum BrowserJumpWindowChoice
    {
        /// <summary>只有一个窗口可能装着这页：交给原来的单窗口逻辑，行为不变。</summary>
        None = 0,

        /// <summary>点的那条网页的保存父窗口标题，现在只在一个窗口上出现。</summary>
        ParentTitle = 1,

        /// <summary>当前窗口标签数和保存时唯一吻合（允许差 1 个）。</summary>
        PageCount = 2,

        /// <summary>分不出：弹选择框让用户选，绝不静默跳。</summary>
        Ambiguous = 3
    }

    /// <summary>
    /// 同名网页的窗口身份判定：先看保存父窗口标题，再看整窗标签数，都不行就让用户选。
    ///
    /// 为什么不继续加标题分：两个窗口都显示同一页时，“标题像保存页”只能证明
    /// 这页在这个窗口里，不能证明用户点的是哪个窗口；再调权重只会换一种错法。
    /// 标签数是保存时整窗指纹的一部分，能从窗口标题直接读，最小化窗口也能用。
    /// </summary>
    internal static class BrowserJumpWindowIdentity
    {
        internal static BrowserJumpWindowChoice Decide(
            IList<BrowserJumpWindowFacts> facts,
            int savedPageCount,
            out int index)
        {
            index = -1;
            if (facts == null)
            {
                return BrowserJumpWindowChoice.None;
            }

            List<int> eligible = new List<int>();
            for (int cursor = 0; cursor < facts.Count; cursor++)
            {
                BrowserJumpWindowFacts fact = facts[cursor];
                if (fact != null && fact.ContainsSavedPage)
                {
                    eligible.Add(cursor);
                }
            }
            if (eligible.Count < 2)
            {
                return BrowserJumpWindowChoice.None;
            }

            // 1. 保存父窗口标题只在一个窗口上出现：这是最直接的身份证据。
            int parentHit = -1;
            int parentHits = 0;
            for (int cursor = 0; cursor < eligible.Count; cursor++)
            {
                BrowserJumpWindowFacts fact = facts[eligible[cursor]];
                if (fact.ParentTitleMatches)
                {
                    parentHits++;
                    parentHit = eligible[cursor];
                }
            }
            if (parentHits == 1)
            {
                index = parentHit;
                return BrowserJumpWindowChoice.ParentTitle;
            }

            // 2. 整窗标签数：和保存时完全相等，或唯一差 1 个（保存后关掉/多开一个）。
            List<int> pageCounts = new List<int>(eligible.Count);
            for (int cursor = 0; cursor < eligible.Count; cursor++)
            {
                BrowserJumpWindowFacts fact = facts[eligible[cursor]];
                pageCounts.Add(fact.PageCountKnown && fact.PageCount > 0 ? fact.PageCount : -1);
            }
            int shapeHit = SelectUniqueByPageCount(pageCounts, savedPageCount);
            if (shapeHit >= 0)
            {
                index = eligible[shapeHit];
                return BrowserJumpWindowChoice.PageCount;
            }

            // 3. 分不出：交给选择框，不再靠分数猜。
            return BrowserJumpWindowChoice.Ambiguous;
        }

        /// <summary>
        /// 在候选窗口标签数里找一个唯一赢家：优先唯一“完全相等”，
        /// 没有相等才接受唯一“差 1”；两个都差 1、或全未知，都返回 -1。
        /// </summary>
        internal static int SelectUniqueByPageCount(
            IList<int> pageCounts,
            int savedPageCount)
        {
            if (pageCounts == null || savedPageCount <= 0)
            {
                return -1;
            }

            int exactIndex = -1;
            int exactHits = 0;
            int nearIndex = -1;
            int nearHits = 0;
            for (int cursor = 0; cursor < pageCounts.Count; cursor++)
            {
                int count = pageCounts[cursor];
                if (count <= 0)
                {
                    continue;
                }
                int distance = Math.Abs(count - savedPageCount);
                if (distance == 0)
                {
                    exactHits++;
                    exactIndex = cursor;
                }
                else if (distance == 1)
                {
                    nearHits++;
                    nearIndex = cursor;
                }
            }
            if (exactHits == 1)
            {
                return exactIndex;
            }
            if (exactHits == 0 && nearHits == 1)
            {
                return nearIndex;
            }
            return -1;
        }
    }
}
