using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunShortcutPagerTests()
        {
            Run("快捷键分页：页数、页码窗口与边界夹取", delegate
            {
                AssertEqual(10, ShortcutSettingsPager.DefaultPageSize);
                AssertEqual(5, ShortcutSettingsPager.PageLinkSize);

                AssertEqual(0, ShortcutSettingsPager.ComputePageCount(0, 10));
                AssertEqual(1, ShortcutSettingsPager.ComputePageCount(7, 10));
                AssertEqual(1, ShortcutSettingsPager.ComputePageCount(10, 10));
                AssertEqual(2, ShortcutSettingsPager.ComputePageCount(11, 10));
                AssertEqual(12, ShortcutSettingsPager.ComputePageCount(120, 10));
                AssertEqual(1, ShortcutSettingsPager.ComputePageCount(50, 50));
                AssertEqual(2, ShortcutSettingsPager.ComputePageCount(51, 50));
                AssertEqual(5, ShortcutSettingsPager.ComputePageCount(120, 25));
                AssertEqual(3, ShortcutSettingsPager.ComputePageCount(120, 50));

                AssertEqual(1, ShortcutSettingsPager.ClampPage(0, 5));
                AssertEqual(1, ShortcutSettingsPager.ClampPage(1, 5));
                AssertEqual(5, ShortcutSettingsPager.ClampPage(99, 5));
                AssertEqual(1, ShortcutSettingsPager.ClampPage(3, 0));

                AssertPageLinksEqual(
                    new int[] { 1 },
                    ShortcutSettingsPager.ComputePageLinks(1, 1, 5));
                AssertPageLinksEqual(
                    new int[] { 1, 2, 3 },
                    ShortcutSettingsPager.ComputePageLinks(2, 3, 5));
                AssertPageLinksEqual(
                    new int[] { 1, 2, 3, 4, 5 },
                    ShortcutSettingsPager.ComputePageLinks(1, 10, 5));
                AssertPageLinksEqual(
                    new int[] { 3, 4, 5, 6, 7 },
                    ShortcutSettingsPager.ComputePageLinks(5, 10, 5));
                AssertPageLinksEqual(
                    new int[] { 6, 7, 8, 9, 10 },
                    ShortcutSettingsPager.ComputePageLinks(10, 10, 5));
                AssertPageLinksEqual(
                    new int[] { 2, 3, 4, 5, 6 },
                    ShortcutSettingsPager.ComputePageLinks(6, 6, 5));
                AssertPageLinksEqual(
                    new int[0],
                    ShortcutSettingsPager.ComputePageLinks(1, 0, 5));

                AssertEqual(1, ShortcutSettingsPager.ComputePageForIndex(0, 50));
                AssertEqual(1, ShortcutSettingsPager.ComputePageForIndex(49, 50));
                AssertEqual(2, ShortcutSettingsPager.ComputePageForIndex(50, 50));
                AssertEqual(3, ShortcutSettingsPager.ComputePageForIndex(50, 25));
                AssertEqual(1, ShortcutSettingsPager.ComputePageForIndex(-1, 50));

                AssertEqual(0, ShortcutSettingsPager.GetPageStart(1, 50, 7));
                AssertEqual(50, ShortcutSettingsPager.GetPageStart(2, 50, 51));
                AssertEqual(50, ShortcutSettingsPager.GetPageStart(3, 25, 51));
                AssertEqual(0, ShortcutSettingsPager.GetPageStart(1, 50, 0));
                AssertEqual(0, ShortcutSettingsPager.GetPageLength(1, 50, 0));
                AssertEqual(7, ShortcutSettingsPager.GetPageLength(1, 50, 7));
                AssertEqual(1, ShortcutSettingsPager.GetPageLength(2, 50, 51));
                AssertEqual(1, ShortcutSettingsPager.GetPageLength(3, 25, 51));
            });

            Run("快捷键分页条：翻页、每页条数与边界禁用", delegate
            {
                EnsureTestApplication();

                ShortcutSettingsPager pager = new ShortcutSettingsPager();
                AssertEqual(10, pager.PageSize);
                AssertEqual(0, pager.PageCount);
                pager.SetState(120, 25, 1);

                AssertEqual(120, pager.TotalCount);
                AssertEqual(25, pager.PageSize);
                AssertEqual(5, pager.PageCount);
                AssertEqual(1, pager.CurrentPage);

                Button first = FindPagerElement(pager, "ShortcutPagerFirst") as Button;
                Button previous = FindPagerElement(pager, "ShortcutPagerPrev") as Button;
                Button next = FindPagerElement(pager, "ShortcutPagerNext") as Button;
                Button last = FindPagerElement(pager, "ShortcutPagerLast") as Button;
                ComboBox rows = FindPagerElement(pager, "ShortcutPagerRows") as ComboBox;
                Assert(first != null && previous != null && next != null && last != null,
                    "分页条四个箭头没有全部创建。");
                Assert(rows != null, "分页条“每页”下拉框没有创建。");
                Assert(rows.Height >= 36.0,
                    "每页下拉框高度不足，实际 " + rows.Height.ToString("0.0"));
                Assert(rows.Width >= 88.0,
                    "每页下拉框宽度不足，实际 " + rows.Width.ToString("0.0"));
                AssertEqual(4, rows.Items.Count);
                AssertEqual(5, Convert.ToInt32(rows.Items[0]));
                AssertEqual(10, Convert.ToInt32(rows.Items[1]));
                AssertEqual(25, Convert.ToInt32(rows.Items[2]));
                AssertEqual(50, Convert.ToInt32(rows.Items[3]));

                Assert(!first.IsEnabled, "第一页时“第一页”应禁用。");
                Assert(!previous.IsEnabled, "第一页时“上一页”应禁用。");
                Assert(next.IsEnabled, "第一页时“下一页”应可用。");
                Assert(last.IsEnabled, "第一页时“最后一页”应可用。");

                Button page1 = FindPagerElement(pager, "ShortcutPagerPage1") as Button;
                Button page2 = FindPagerElement(pager, "ShortcutPagerPage2") as Button;
                Button page5 = FindPagerElement(pager, "ShortcutPagerPage5") as Button;
                Assert(page1 != null && page2 != null && page5 != null,
                    "120 条 / 每页 25 条时应显示 1–5 五个页码。");
                Assert(page1.FontWeight == FontWeights.SemiBold,
                    "当前页 1 应加粗。");
                Assert(page2.FontWeight == FontWeights.Normal,
                    "非当前页 2 不应加粗。");

                int pageChangedCount = 0;
                pager.PageChanged += delegate { pageChangedCount++; };
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertEqual(1, pageChangedCount);
                AssertEqual(2, pager.CurrentPage);
                Assert(page2.FontWeight == FontWeights.SemiBold,
                    "翻到第 2 页后页码 2 应加粗。");

                last.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                AssertEqual(5, pager.CurrentPage);
                Assert(first.IsEnabled, "最后一页时“第一页”应可用。");
                Assert(previous.IsEnabled, "最后一页时“上一页”应可用。");
                Assert(!next.IsEnabled, "最后一页时“下一页”应禁用。");
                Assert(!last.IsEnabled, "最后一页时“最后一页”应禁用。");

                int pageSizeChangedCount = 0;
                pager.PageSizeChanged += delegate { pageSizeChangedCount++; };
                rows.SelectedItem = 50;
                AssertEqual(1, pageSizeChangedCount);
                AssertEqual(50, pager.PageSize);
                AssertEqual(3, pager.PageCount);
                AssertEqual(3, pager.CurrentPage);
                Assert(first.IsEnabled, "每页 50 条、第 3/3 页时“第一页”应可用。");
                Assert(!next.IsEnabled, "每页 50 条、第 3/3 页时“下一页”应禁用。");
                Button page3AfterResize = FindPagerElement(pager, "ShortcutPagerPage3") as Button;
                Assert(page3AfterResize != null, "每页 50 条时应显示 1–3 三个页码。");
                Assert(page3AfterResize.FontWeight == FontWeights.SemiBold,
                    "改成每页 50 条后应夹到第 3 页。");

                rows.SelectedItem = 25;
                AssertEqual(5, pager.PageCount);
                AssertEqual(3, pager.CurrentPage);
                Button page3BackTo25 = FindPagerElement(pager, "ShortcutPagerPage3") as Button;
                Assert(page3BackTo25 != null, "切回每页 25 条后应重新显示 1–5 五个页码。");
                Assert(page3BackTo25.FontWeight == FontWeights.SemiBold,
                    "切回每页 25 条后应保持第 3 页。");

                pager.SetState(0, 10, 1);
                AssertEqual(0, pager.PageCount);
                AssertEqual(1, pager.CurrentPage);
                Assert(FindPagerElement(pager, "ShortcutPagerPage1") == null,
                    "没有数据时不应显示页码按钮。");
                Assert(!first.IsEnabled && !previous.IsEnabled && !next.IsEnabled && !last.IsEnabled,
                    "没有数据时四个箭头都应禁用。");

                // settings.json 被手改成不支持的条数时，界面必须回到默认 10，不能空一页。
                pager.SetState(120, 100, 1);
                AssertEqual(10, pager.PageSize);
                AssertEqual(12, pager.PageCount);
                AssertEqual(10, Convert.ToInt32(rows.SelectedItem));
            });
        }

        private static void EnsureTestApplication()
        {
            Application application = Application.Current;
            if (application != null)
            {
                return;
            }
            application = new Application();
            ThemeService.Apply(ThemeMode.Light);
            AppTheme.Apply(application);
            AppText.Initialize(LanguageTags.ZhCn);
        }

        private static DependencyObject FindPagerElement(DependencyObject root, string automationId)
        {
            FrameworkElement element = root as FrameworkElement;
            if (element != null
                && string.Equals(
                    AutomationProperties.GetAutomationId(element),
                    automationId,
                    StringComparison.Ordinal))
            {
                return element;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject found = FindPagerElement(
                    VisualTreeHelper.GetChild(root, index), automationId);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static void AssertPageLinksEqual(int[] expected, int[] actual)
        {
            if (actual == null)
            {
                throw new InvalidOperationException("页码窗口为空。");
            }
            if (expected.Length != actual.Length)
            {
                throw new InvalidOperationException(string.Format(
                    "页码窗口数量期望 {0}，实际 {1}。",
                    expected.Length,
                    actual.Length));
            }
            for (int index = 0; index < expected.Length; index++)
            {
                if (expected[index] != actual[index])
                {
                    throw new InvalidOperationException(string.Format(
                        "页码窗口第 {0} 位期望 {1}，实际 {2}。",
                        index + 1,
                        expected[index],
                        actual[index]));
                }
            }
        }
    }
}
