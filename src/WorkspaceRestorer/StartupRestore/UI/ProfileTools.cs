using System;
using System.Collections.Generic;
using WorkspaceRestorer.Core;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    // 工作区「启动与恢复」——工作方案工具：
    //  · 搜索工作方案（原来那个"扫描并新建方案"和"保存当前工作"重复了，改成搜索）
    //  · 追加当前时间（把"本机时区名 + 正式日期时间"接到本方案名字后面，例如
    //    北京时间 2026/9/24日 1:52分；只改当前方案名，不写进设置里的“新工作方案名”）
    public sealed partial class MainWindow
    {
        // 时间格式、时区名和旧时间戳剥离都在公共 TimeText 里；本文件不再保留副本。

        /// <summary>把当前时间接到当前工作方案名字后面；再点一次只更新时间，不会叠加。</summary>
        private void AppendCurrentTimeToProfileName()
        {
            StartupProfile profile = controller.ActiveProfile;
            if (profile == null)
            {
                statusText.Text = AppText.T(StartupRestoreTextKeys.StatusNoActiveProfile);
                return;
            }
            string oldName = profile.Name;
            // 先把上一次追加的时间去掉，再追加新的：反复点只会更新时间，不会越叠越长。
            string newName = TimeText.AppendStamp(
                oldName,
                AppText.T(CommonTextKeys.WorkProfileFallback),
                DateTime.Now,
                TimeZoneInfo.Local,
                AppText.CurrentLanguage.Tag);
            if (controller.RenameProfile(profile, newName) == null)
            {
                statusText.Text = AppText.T(StartupRestoreTextKeys.StatusAppendFailed);
                return;
            }
            // 只刷新方案列表（结构没变时就是就地改标题）。
            // 设置里的“新工作方案名”不受影响：追加时间/改名只改当前方案。
            RefreshProfileList();
            // 记进撤销栈，Ctrl+Z 就能把名字还原（Ctrl+Shift+Z 重做）
            PushUndo(
                AppText.T(StartupRestoreTextKeys.StatusUndoAppendTime),
                delegate { ApplyProfileName(profile, oldName); },
                delegate { ApplyProfileName(profile, newName); });
            statusText.Text = AppText.F(StartupRestoreTextKeys.StatusAppendDoneFormat, newName);
        }

        /// <summary>
        /// 「启动与恢复」的“搜索方案”入口：按名字**或日期**查找已有的工作方案
        /// （用户 2026-10-08 要求把两种搜索合在一起）。
        /// 弹窗外壳（标题 / 说明 / 搜索框 / 结果列表 / 高亮）已按用户 2026-10-19 的要求
        /// 搬进公共区的 `Shared/UI/Controls/SearchDialog.cs`，这里只剩本工作区自己的两件事：
        /// **搜什么数据**（`ShowProfileSearchDialog`）与**点一条之后做什么**（切成当前方案）。
        /// 弹窗下面原来那排按钮（新建空方案 / 扫描并新建方案 / 关闭）**已按用户要求删除**，
        /// 对应的处理代码也一并删掉，不留僵尸代码。
        /// </summary>
        private void SearchProfiles()
        {
            // 【2026-10-17 第 46 批】原来的说明又长又绕（“按名字查找已有的工作方案；勾选下面的……”
            // 用户说“太复杂了，润色一下，通俗易懂”）。这里改成两句大白话，并把用户最关心的
            // “多个词空格分隔”直接写进来。
            ShowProfileSearchDialog(
                AppText.T(StartupRestoreTextKeys.SearchTitle),
                AppText.T(StartupRestoreTextKeys.SearchDescription));
        }

        /// <summary>
        /// 「启动与恢复」的搜索：数据源是工作方案清单。
        /// 「同时按时间搜索」勾选框、结果两行+高亮、多词空格分隔都在公共件里 ——
        /// 这里只提供“这一条叫什么、什么时候保存”和“点一条做什么”。
        /// </summary>
        private void ShowProfileSearchDialog(string title, string description)
        {
            ShowSearchDialog(
                title,
                description,
                delegate(string keyword, bool includeTime)
                {
                    List<SearchHit> hits = new List<SearchHit>();
                    List<StartupProfile> matches = StartupProfileSearch.Filter(controller.StartupProfiles, keyword, includeTime);
                    for (int index = 0; index < matches.Count; index++)
                    {
                        StartupProfile match = matches[index];
                        SearchHit hit = new SearchHit();
                        hit.Title = match.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
                        string savedAt = StartupProfileCardText.FormatSavedAt(
                            match, AppText.CurrentLanguageTag);
                        // 【2026-10-17 第 45 批】结果行第二行固定显示日期（用户要求“后面要显示日期”），
                        // 命中的那部分由 `ProfileSearchHighlights.TimeRanges` 决定要不要亮。
                        string prefix = AppText.F(
                            StartupRestoreTextKeys.SearchDetailFormat,
                            match.Items == null ? 0 : match.Items.Count);
                        string detail = savedAt.Length == 0 ? prefix : prefix + " · " + savedAt;
                        hit.Detail = detail;
                        ProfileSearchHighlights lights = StartupProfileSearch.Highlight(match, keyword, includeTime);
                        hit.TitleSegments = BuildHighlightSegments(hit.Title, lights.NameRanges, 0);
                        // 时间区间的下标是相对“时间串”的，拼到第二行里要整体后移 prefix + " · " 那么多个字。
                        int offset = savedAt.Length == 0 ? 0 : prefix.Length + 3;
                        hit.DetailSegments = savedAt.Length == 0
                            ? BuildHighlightSegments(detail, null, 0)
                            : BuildHighlightSegments(detail, lights.TimeRanges, -offset);
                        hit.Tag = match;
                        hits.Add(hit);
                    }
                    return hits;
                },
                delegate(SearchHit hit)
                {
                    StartupProfile captured = hit == null ? null : hit.Tag as StartupProfile;
                    if (captured == null)
                    {
                        return;
                    }
                    SelectProfile(captured);
                    RefreshProfileList();
                });
        }
    }
}
