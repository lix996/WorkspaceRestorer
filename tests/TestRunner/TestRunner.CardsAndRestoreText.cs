using System;
using System.Collections.Generic;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.UI.Cards.Actions;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunCardsAndRestoreTextTests()
        {
            Run("卡片动作区：三种卡片的顺序固定", delegate
            {
                AssertSlots(
                    ActionBarKind.ProfileCard,
                    ActionSlot.Move,
                    ActionSlot.Save,
                    ActionSlot.Restore,
                    ActionSlot.Delete);
                AssertSlots(
                    ActionBarKind.ApplicationCard,
                    ActionSlot.Move,
                    ActionSlot.Add,
                    ActionSlot.Restore,
                    ActionSlot.Delete);
                AssertSlots(
                    ActionBarKind.LayoutCard,
                    ActionSlot.Move,
                    ActionSlot.Edit,
                    ActionSlot.Save,
                    ActionSlot.Restore,
                    ActionSlot.Delete);
                AssertSlots(
                    ActionBarKind.LayoutFolderCard,
                    ActionSlot.Move,
                    ActionSlot.Add,
                    ActionSlot.AddFolder,
                    ActionSlot.Delete);
            });

            Run("卡片动作区：行固定三格，缺最小化也留同宽空位", delegate
            {
                AssertSlots(
                    ActionBarKind.ItemRow,
                    ActionSlot.Move,
                    ActionSlot.Minimize,
                    ActionSlot.Delete);
                AssertSlots(
                    ActionBarKind.GroupRow,
                    ActionSlot.Move,
                    ActionSlot.Minimize,
                    ActionSlot.Delete);
                AssertWidth(26.0, ActionSlot.Move, ActionBarKind.ItemRow);
                AssertWidth(32.0, ActionSlot.Minimize, ActionBarKind.ItemRow);
                AssertWidth(32.0, ActionSlot.Delete, ActionBarKind.ItemRow);
                AssertWidth(32.0, ActionSlot.Move, ActionBarKind.ProfileCard);
            });

            Run("恢复完成文案：启动与恢复成功/跳过", delegate
            {
                RestoreCompletion completion = NewRestore(RestoreSource.StartupRestore, 10, 8);
                completion.AlreadyOpenCount = 1;
                completion.UserClosedCount = 1;
                completion.BrowserWindowClosedCount = 1;

                RestoreNoticeText notice = RestoreText.ForBanner(completion);
                Assert(notice.Kind == RestoreNoticeKind.Success, "成功提示应为 Success 语气。");
                AssertEqual(
                    "恢复完成：成功 8 / 10 项，1 项已在运行已跳过，1 项用户关闭已跳过，1 个网页因浏览器窗口关闭已跳过",
                    notice.Summary);
                AssertEqual(notice.Summary, notice.Detail);
                Assert(!notice.Persistent, "成功提示不应常驻。");
            });

            Run("恢复完成文案：失败名单进摘要，完整清单进详情", delegate
            {
                RestoreCompletion completion = NewRestore(RestoreSource.StartupRestore, 10, 7);
                completion.FailedCount = 2;
                completion.BrowserWindowClosedCount = 1;
                completion.Failures = new List<RestoreFailure>
                {
                    new RestoreFailure { Name = "记事本", KindText = "应用", Reason = "找不到窗口" },
                    new RestoreFailure { Name = "网页 A", KindText = "网页", Reason = "窗口已关闭" }
                };

                RestoreNoticeText notice = RestoreText.ForBanner(completion);
                Assert(notice.Kind == RestoreNoticeKind.Error, "失败提示应为 Error 语气。");
                AssertEqual(
                    "恢复完成：成功 7 / 10 项，2 项未能恢复：记事本（找不到窗口）；网页 A（窗口已关闭）；另有 1 个网页因浏览器窗口关闭已跳过",
                    notice.Summary);
                AssertEqual(
                    "有 2 项未能恢复 —— 1. [应用] 记事本 —— 找不到窗口； 2. [网页] 网页 A —— 窗口已关闭",
                    notice.Detail);
            });

            Run("恢复完成文案：托盘沿用短句和分号", delegate
            {
                RestoreCompletion completion = NewRestore(RestoreSource.StartupRestore, 10, 9);
                completion.UserClosedCount = 1;
                AssertEqual(
                    "成功 9 / 10 项，全部恢复完成；1 项用户关闭已跳过",
                    RestoreText.ForTray(completion));

                completion.FailedCount = 1;
                AssertEqual(
                    "成功 9 / 10 项，1 项未能恢复；1 项用户关闭已跳过",
                    RestoreText.ForTray(completion));
            });

            Run("恢复完成文案：手动取消和自动停止", delegate
            {
                RestoreCompletion cancelled = NewRestore(RestoreSource.StartupRestore, 10, 4);
                cancelled.IsCancelled = true;
                RestoreNoticeText manual = RestoreText.ForBanner(cancelled);
                Assert(manual.Kind == RestoreNoticeKind.Neutral, "手动取消应为 Neutral 语气。");
                AssertEqual("已取消恢复：已完成 4 / 10 项", manual.Summary);
                Assert(!manual.Persistent, "手动取消不应常驻。");

                RestoreCompletion auto = NewRestore(RestoreSource.StartupRestore, 10, 4);
                auto.IsCancelled = true;
                auto.IsAutoStopped = true;
                auto.CancelReason = "内存占用超过设置上限，已自动暂停。";
                RestoreNoticeText stopped = RestoreText.ForBanner(auto);
                Assert(stopped.Kind == RestoreNoticeKind.Error, "自动停止应为 Error 语气。");
                AssertEqual(auto.CancelReason, stopped.Summary);
                Assert(stopped.Persistent, "自动停止提示应常驻。");
            });

            Run("恢复完成文案：自启动应用走自己的目录，句子暂与启动与恢复一致", delegate
            {
                RestoreCompletion startup = NewRestore(RestoreSource.StartupRestore, 5, 4);
                RestoreCompletion applications = NewRestore(RestoreSource.Applications, 5, 4);
                AssertEqual(
                    RestoreText.ForBanner(startup).Summary,
                    RestoreText.ForBanner(applications).Summary);
                AssertEqual(
                    "恢复完成：成功 4 / 5 项",
                    RestoreText.ForBanner(applications).Summary);
            });

            Run("恢复完成文案：图标布局状态", delegate
            {
                AssertEqual(
                    "桌面图标布局已恢复，共应用 5 项",
                    DesktopLayoutRestoreText.ForStatus(new DesktopRestoreResult
                    {
                        Applied = true,
                        AppliedCount = 5
                    }));
                AssertEqual(
                    "桌面图标布局未应用",
                    DesktopLayoutRestoreText.ForStatus(new DesktopRestoreResult()));
                AssertEqual(
                    "没有匹配的桌面图标布局",
                    DesktopLayoutRestoreText.ForStatus(null));
            });
        }

        private static RestoreCompletion NewRestore(
            RestoreSource source,
            int total,
            int success)
        {
            return new RestoreCompletion
            {
                Source = source,
                TotalCount = total,
                SuccessCount = success,
                Failures = new List<RestoreFailure>()
            };
        }

        private static void AssertWidth(double expected, ActionSlot slot, ActionBarKind kind)
        {
            double actual = ActionMetrics.WidthOf(slot, kind);
            if (Math.Abs(actual - expected) > 0.001)
            {
                throw new InvalidOperationException(
                    kind + " 的 " + slot + " 宽度期望 " + expected + "，实际 " + actual + "。");
            }
        }

        private static void AssertSlots(ActionBarKind kind, params ActionSlot[] expected)
        {
            ActionSlot[] actual = ActionBarSpec.For(kind);
            AssertEqual(expected.Length, actual.Length);
            for (int index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    throw new InvalidOperationException(
                        kind + " 第 " + (index + 1) + " 个动作期望 " + expected[index]
                        + "，实际 " + actual[index] + "。");
                }
            }
        }
    }
}
