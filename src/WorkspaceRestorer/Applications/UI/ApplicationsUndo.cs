using System;
using System.Collections.Generic;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.RecycleBin.Core;

namespace WorkspaceRestorer.UI
{
    // 工作区：自启动应用 —— **撤销记录**（用户 2026-10-17 报“自启动应用使用撤销快捷键却无法撤销”）。
    //
    // 为什么单独一个文件：自启动应用的撤销走“整体快照”这一条路，和「启动与恢复」里
    // 那种“每个操作各写一个反动作”的写法不一样，集中放一处才好维护。
    //
    // 背景（查出来的真因）：以前这个页面只有两处会进撤销栈——
    //   · 点卡片标题切换当前方案（第 51 条要求加的）；
    //   · 项目行上的 🗑 删除项目。
    // 而用户最常做的另外六件事**一条记录都没进栈**，所以按 Ctrl+Z 只会得到
    // “没有可撤销的操作。”，看起来就是“撤销坏了”。这六件事是：
    //   · 扫描结果/浏览/挑文件夹/粘贴加入项目     · 项目行上勾选/取消勾选（启用/停用）
    //   · 拖动调整启动顺序、拖到最前最后         · 双击改名
    //   · 新建空白方案                           · 删除方案
    // 现在它们**全部**通过 `PushApplicationPlansUndo(...)` 进全局撤销栈，
    // 撤销/重做就是把“改之前/改之后”的整份快照放回去，绝不会漏。
    //
    // 撤销栈本身在公共区域：Shared/UI/Shell/UndoStack.cs（四个工作区共用一份）。
    public sealed partial class MainWindow
    {
        /// <summary>自启动应用的一份快照：所有方案 + 当前方案 Id（都是**副本**，不会被后续编辑改到）。</summary>
        private sealed class ApplicationPlansSnapshot
        {
            public List<ApplicationPlan> Plans { get; set; }
            public string ActivePlanId { get; set; }
        }

        /// <summary>拍一张现在的快照（深拷贝）。</summary>
        private ApplicationPlansSnapshot CaptureApplicationPlansSnapshot()
        {
            ApplicationPlansSnapshot snapshot = new ApplicationPlansSnapshot();
            snapshot.Plans = new List<ApplicationPlan>();
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            if (plans != null)
            {
                for (int index = 0; index < plans.Count; index++)
                {
                    if (plans[index] != null)
                    {
                        snapshot.Plans.Add(CloneApplicationPlan(plans[index]));
                    }
                }
            }
            ApplicationPlan active = controller.ActiveApplicationPlan;
            snapshot.ActivePlanId = active == null ? null : active.Id;
            return snapshot;
        }

        /// <summary>
        /// 把“改之前的快照”记进全局撤销栈。
        /// 在**改完**之后调用：撤销＝把 before 放回去；重做＝把“现在”的快照放回去。
        /// </summary>
        private void PushApplicationPlansUndo(string description, ApplicationPlansSnapshot before)
        {
            PushApplicationPlansUndo(description, before, null);
        }

        /// <summary>
        /// 同上；<paramref name="binEntryToDropOnUndo"/> 是这次操作**顺手删进回收站**的那条记录。
        /// 撤销时把它一并拿掉（方案已经回到列表里，回收站不该再留一份一模一样的副本）；
        /// 重做时不去补它 —— 重新执行“删除”自然会再产生一条。
        /// </summary>
        private void PushApplicationPlansUndo(string description, ApplicationPlansSnapshot before, RecycleBinEntry binEntryToDropOnUndo)
        {
            if (before == null)
            {
                return;
            }
            ApplicationPlansSnapshot after = CaptureApplicationPlansSnapshot();
            PushUndo(
                description,
                delegate
                {
                    ApplyApplicationPlansSnapshot(before);
                    if (binEntryToDropOnUndo != null)
                    {
                        controller.DropRecycleBinEntry(binEntryToDropOnUndo);
                    }
                },
                delegate { ApplyApplicationPlansSnapshot(after); });
        }

        /// <summary>按快照还原自启动方案并重画卡片。</summary>
        private void ApplyApplicationPlansSnapshot(ApplicationPlansSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            // 必须传副本：撤销栈里存的快照要一直保持“当时的样子”，
            // 否则放回去之后用户继续编辑会把其他步骤的快照一起改掉。
            List<ApplicationPlan> copies = new List<ApplicationPlan>();
            for (int index = 0; index < snapshot.Plans.Count; index++)
            {
                copies.Add(CloneApplicationPlan(snapshot.Plans[index]));
            }
            controller.ApplyApplicationPlansSnapshot(copies, snapshot.ActivePlanId);
            RefreshApplicationPlanList();
        }

        private static ApplicationPlan CloneApplicationPlan(ApplicationPlan plan)
        {
            ApplicationPlan copy = new ApplicationPlan();
            if (plan == null)
            {
                return copy;
            }
            copy.Id = plan.Id;
            copy.Name = plan.Name;
            copy.CreatedAtUtc = plan.CreatedAtUtc;
            copy.UpdatedAtUtc = plan.UpdatedAtUtc;
            copy.ParentId = plan.ParentId;
            copy.IsFolder = plan.IsFolder;
            copy.Items = new List<ApplicationPlanItem>();
            if (plan.Items != null)
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    copy.Items.Add(CloneApplicationPlanItem(plan.Items[index]));
                }
            }
            return copy;
        }

        /// <summary>撤销记录里怎么称呼一个项目：优先用名字，没有名字就用路径。</summary>
        private static string ApplicationItemLabel(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return AppText.T(ApplicationsTextKeys.UndoItemFallback);
            }
            string name = string.IsNullOrWhiteSpace(item.DisplayName) ? item.TargetPath : item.DisplayName;
            return string.IsNullOrWhiteSpace(name)
                ? AppText.T(ApplicationsTextKeys.UndoItemFallback)
                : name;
        }

        /// <summary>同上，但带上它原来排第几（撤销记录里写“第 2 项”更容易对得上）。</summary>
        private static string ApplicationItemLabel(ApplicationPlanItem item, int index)
        {
            string name = ApplicationItemLabel(item);
            return index >= 0
                ? AppText.F(ApplicationsTextKeys.UndoItemOrdinalFormat, name, index + 1)
                : name;
        }

        private static ApplicationPlanItem CloneApplicationPlanItem(ApplicationPlanItem item)        {
            ApplicationPlanItem copy = new ApplicationPlanItem();
            if (item == null)
            {
                return copy;
            }
            copy.Id = item.Id;
            copy.DisplayName = item.DisplayName;
            copy.TargetPath = item.TargetPath;
            copy.IsEnabled = item.IsEnabled;
            copy.Note = item.Note;
            copy.WindowPlacement = item.WindowPlacement == null
                ? null
                : item.WindowPlacement.Clone();
            copy.BrowserGroupKey = item.BrowserGroupKey;
            copy.BrowserExecutablePath = item.BrowserExecutablePath;
            copy.BrowserName = item.BrowserName;
            copy.BrowserWindowIndex = item.BrowserWindowIndex;
            copy.BrowserTabIndex = item.BrowserTabIndex;
            copy.BrowserTabIdentityCaptured = item.BrowserTabIdentityCaptured;
            copy.IsActiveBrowserTab = item.IsActiveBrowserTab;
            copy.BrowserTabs = BrowserTabFingerprint.CloneList(item.BrowserTabs);
            copy.BrowserWindowTitleSnapshot = item.BrowserWindowTitleSnapshot;
            copy.BrowserBindingKind = item.BrowserBindingKind;
            copy.BrowserBindingUpdatedAtUtc = item.BrowserBindingUpdatedAtUtc;
            copy.IsBrowserPage = item.IsBrowserPage;
            copy.Kind = item.Kind;
            copy.ParentId = item.ParentId;
            copy.IsGroupParent = item.IsGroupParent;
            copy.IsBrowserWindow = item.IsBrowserWindow;
            copy.IsFolderGroup = item.IsFolderGroup;
            copy.WindowTitle = item.WindowTitle;
            copy.StartTimeUtc = item.StartTimeUtc;
            return copy;
        }

        /// <summary>
        /// 删除方案时“那次删除产生的回收站记录”。撤销删除时要把这条记录一并拿掉，
        /// 否则方案回到列表里、回收站里还留着一份一模一样的副本（用户会以为是两份）。
        /// </summary>
        private RecycleBinEntry FindRecycleBinEntryForPlan(ApplicationPlan plan)
        {
            if (plan == null)
            {
                return null;
            }
            IList<RecycleBinEntry> entries = controller.RecycleBinEntries;
            if (entries == null)
            {
                return null;
            }
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                RecycleBinEntry entry = entries[index];
                if (entry == null)
                {
                    continue;
                }
                if (string.Equals(entry.Kind, RecycleBinKind.Application, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Title, plan.Name ?? string.Empty, StringComparison.CurrentCultureIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }
    }
}
