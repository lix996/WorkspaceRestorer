using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「自启动应用」的右键菜单（2026-10-19：菜单装配搬到公共件 `Shared/UI/Controls/ContextMenus.cs`，
    /// 这里只保留“菜单项点下去做什么”，动作仍调用本工作区自己的方法）。
    ///
    /// 对照「启动与恢复」：
    ///   · 空白处：新建空白方案 / 往当前方案粘贴路径·网址新增…（弹同一个对话框）
    ///   · 方案卡片：展开·收起 / 恢复此方案 / 加入启动与恢复（本方案）/ 重命名方案… / 设为当前方案 /
    ///     往此方案粘贴… / 删除此方案（可在回收站恢复）
    ///   · 「挑文件夹」「扫描桌面」不在右键菜单里（工具栏上有同名按钮）。
    /// </summary>
    public sealed partial class MainWindow
    {
        /// <summary>自启动应用 · 空白处右键。</summary>
        private ContextMenu CreateApplicationsBlankContextMenu()
        {
            return Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuNewBlankPlan), delegate { CreateBlankApplicationPlan(); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuPasteToCurrentPlan), delegate { AddPastedTargetToApplicationPlan(null); }));
        }

        /// <summary>自启动应用 · 方案卡片右键（与「启动与恢复」的卡片菜单一一对应）。</summary>
        private ContextMenu CreateApplicationsPlanContextMenu(
            ApplicationPlan plan, bool expanded, Action toggleExpansion, Action beginRename)
        {
            ApplicationPlan captured = plan;
            return Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(expanded ? AppText.T(ApplicationsTextKeys.MenuCollapsePlan) : AppText.T(ApplicationsTextKeys.MenuExpandPlan), delegate
                {
                    if (toggleExpansion != null)
                    {
                        toggleExpansion();
                    }
                }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuRestorePlan), delegate
                {
                    controller.SetActiveApplicationPlan(captured);
                    RefreshApplicationPlanList();
                    RestoreApplicationPlanNow();
                }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuAddToStartupRestore), delegate
                {
                    controller.SetActiveApplicationPlan(captured);
                    CopyApplicationPlanToStartupWorkspace(captured);
                    RefreshApplicationPlanList();
                }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuRenamePlan), delegate
                {
                    if (beginRename != null)
                    {
                        beginRename();
                    }
                }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuSetCurrentPlan), delegate
                {
                    controller.SetActiveApplicationPlan(captured);
                    RefreshApplicationPlanList();
                    statusText.Text = AppText.F(
                    ApplicationsTextKeys.MenuCurrentPlanFormat,
                    captured.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuPasteToThisPlan), delegate { AddPastedTargetToApplicationPlan(captured); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuNewChildPlan), delegate { CreateChildApplicationPlanWithPrompt(captured, false); }),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuNewChildFolder), delegate { CreateChildApplicationPlanWithPrompt(captured, true); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuDeletePlan), delegate { DeleteApplicationPlanWithConfirm(captured); }));
        }

        /// <summary>自启动应用 · 网页行右键：重新设置这条网页归属的窗口/标签。</summary>
        private ContextMenu CreateApplicationsPageContextMenu(
            ApplicationPlan plan,
            ApplicationPlanItem item)
        {
            return Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuSetWebOwner), delegate
                {
                    SetApplicationBrowserPageBinding(plan, item);
                }, AppText.T(ApplicationsTextKeys.MenuSetWebOwnerHelp)),
                Shared.ContextMenuItem.Item(AppText.T(ApplicationsTextKeys.MenuOpenWebPage), delegate
                {
                    JumpToApplicationItem(plan, item);
                }));
        }

        /// <summary>
        /// 自启动应用 · 项目行/整组父行右键（2026-11-01）：原有网页项 + 「移动到方案…」。
        /// 网页子项只给不可点的说明（用户要求：子项只能整组走，不弹提示也不误操作）。
        /// </summary>
        private ContextMenu CreateApplicationItemContextMenu(ApplicationPlan plan, ApplicationPlanItem item)
        {
            ContextMenu menu = item != null && item.IsBrowserPage
                ? CreateApplicationsPageContextMenu(plan, item)
                : Shared.ContextMenus.Create();
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(BuildMoveApplicationItemMenu(plan, item));
            return menu;
        }

        /// <summary>“移动到方案…”子菜单：只列别的方案（不含方案文件夹、不含自己）。</summary>
        private MenuItem BuildMoveApplicationItemMenu(ApplicationPlan source, ApplicationPlanItem item)
        {
            MenuItem move = new MenuItem { Header = AppText.T(ApplicationsTextKeys.MenuMoveToPlan) };
            if (source == null || item == null)
            {
                move.IsEnabled = false;
                return move;
            }
            if (!string.IsNullOrWhiteSpace(item.ParentId))
            {
                MenuItem note = new MenuItem { Header = AppText.T(ApplicationsTextKeys.MenuChildGroupOnly) };
                note.IsEnabled = false;
                move.Items.Add(note);
                return move;
            }
            int count = 0;
            IList<ApplicationPlan> plans = controller.ApplicationPlans;
            for (int index = 0; index < plans.Count; index++)
            {
                ApplicationPlan target = plans[index];
                if (target == null || target.IsFolder || object.ReferenceEquals(target, source))
                {
                    continue;
                }
                ApplicationPlan captured = target;
                MenuItem entry = new MenuItem { Header = captured.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback) };
                entry.Click += delegate { MoveApplicationItemToPlanEnd(source, item, captured); };
                move.Items.Add(entry);
                count++;
            }
            if (count == 0)
            {
                MenuItem none = new MenuItem { Header = AppText.T(ApplicationsTextKeys.MenuNoOtherPlans) };
                none.IsEnabled = false;
                move.Items.Add(none);
            }
            return move;
        }

        /// <summary>右键“移动到方案…”：追加到目标方案末尾（不进拖动引擎，也能进撤销栈）。</summary>
        private void MoveApplicationItemToPlanEnd(
            ApplicationPlan from, ApplicationPlanItem moving, ApplicationPlan to)
        {
            if (from == null || moving == null || to == null || object.ReferenceEquals(from, to))
            {
                return;
            }
            ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
            bool group = moving.IsGroupParent;
            if (controller.MoveApplicationItemBetweenPlans(from, to, moving, null, false))
            {
                string label = ApplicationItemLabel(moving);
                statusText.Text = AppText.F(
                    group ? ApplicationsTextKeys.StatusMovedGroupFormat : ApplicationsTextKeys.StatusMovedItemFormat,
                    label,
                    to.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback));
                PushApplicationPlansUndo(
                    AppText.F(
                        group ? ApplicationsTextKeys.UndoMovedGroupFormat : ApplicationsTextKeys.UndoMovedItemFormat,
                        to.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                        label),
                    before);
            }
            else
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusAlreadyInTarget);
            }
            RefreshApplicationPlanList();
        }

        /// <summary>
        /// 粘贴路径/网址新增（和「启动与恢复」一样：先弹出输入对话框，用户确认后再加）。
        /// plan 为 null 表示加进当前方案；指定方案时会先把它切成当前方案。
        /// </summary>
        private void CreateChildApplicationPlanWithPrompt(ApplicationPlan parent, bool isFolder)
        {
            if (parent == null)
            {
                return;
            }
            string name = WorkspaceRestorer.UI.Shared.NamePromptDialog.Ask(
                Window.GetWindow(this),
                isFolder
                    ? AppText.T(ApplicationsTextKeys.NewChildTitleFolder)
                    : AppText.T(ApplicationsTextKeys.NewChildTitlePlan),
                AppText.F(
                    ApplicationsTextKeys.NewChildPromptFormat,
                    parent.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback)),
                isFolder
                    ? AppText.T(ApplicationsTextKeys.NewChildOkFolder)
                    : AppText.T(ApplicationsTextKeys.NewChildOkPlan));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            ApplicationPlan created = controller.CreateApplicationPlan(name, parent, isFolder);
            if (created == null)
            {
                return;
            }
            applicationPlanExpansionStates[parent.Id] = true;
            controller.SetActiveApplicationPlan(created);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                    isFolder
                        ? ApplicationsTextKeys.StatusChildCreatedFolderFormat
                        : ApplicationsTextKeys.StatusChildCreatedPlanFormat,
                    created.Name);
        }

        private void AddPastedTargetToApplicationPlan(ApplicationPlan plan)
        {
            PasteItemDialog dialog = new PasteItemDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            string text = dialog.Value;
            if (string.IsNullOrWhiteSpace(text))
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusEmptyTarget);
                return;
            }
            if (plan != null && !object.ReferenceEquals(plan, controller.ActiveApplicationPlan))
            {
                controller.SetActiveApplicationPlan(plan);
                RefreshApplicationPlanList();
            }
            AddPastedTargetText(text);
        }
    }
}
