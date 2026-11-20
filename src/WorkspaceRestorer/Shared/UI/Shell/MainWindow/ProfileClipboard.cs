using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>
        /// Ctrl+C：把"当前工作方案"整份记下来（放进程序自己的剪贴板变量，同时写入系统剪贴板文本，
        /// 方便粘贴到别处查看/备份）。
        ///
        /// 【2026-10-18 用户报“快捷键，CTRL+c，ctrl+v，复制粘贴无效”】
        /// 这两个键在 `ShortcutCatalog` 里是“全部工作区”，但动作只有「启动与恢复」这一套 ——
        /// 在「自启动应用」里按 Ctrl+C 复制的是**工作方案**、按 Ctrl+V 是在**启动与恢复**里新建工作方案，
        /// 当前页面上什么都看不到，所以看起来“无效”。现在按工作区分派：
        /// 「自启动应用」走 `ApplicationsClipboard.cs` 里那一套（复制/粘贴**自启动方案**），
        /// 其它工作区明确提示“这里没有可复制的方案”，绝不偷偷改另一个工作区的数据。
        /// </summary>
        private void CopyActiveProfileToClipboard()
        {
            string workspace = activeWorkspaceName ?? string.Empty;
            if (string.Equals(activeWorkspaceKey, ShellTextKeys.NavLayoutTitle, StringComparison.Ordinal))
            {
                CopyActiveLayoutToClipboard();
                return;
            }
            if (string.Equals(activeWorkspaceKey, ShellTextKeys.NavApplicationsTitle, StringComparison.Ordinal))
            {
                CopyActiveApplicationPlanToClipboard();
                return;
            }
            if (!string.Equals(activeWorkspaceKey, ShellTextKeys.NavStartupTitle, StringComparison.Ordinal))
            {
                statusText.Text = AppText.F(
                    ShellTextKeys.ClipboardCopyNoContentFormat, workspace);
                return;
            }
            StartupProfile profile = controller.ActiveProfile;
            if (profile == null)
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardNoCurrentProfile);
                return;
            }
            string text = StartupProfileClipboard.Serialize(profile);
            if (string.IsNullOrEmpty(text))
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardCopyFailed);
                return;
            }
            copiedProfileText = text;
            string clipboardError;
            bool clipboardOk = ClipboardText.TrySetText(text, out clipboardError);
            if (!clipboardOk)
            {
                AppLog.Write("[剪贴板] 复制工作方案到系统剪贴板失败：" + clipboardError);
            }
            int count = profile.Items == null ? 0 : profile.Items.Count;
            statusText.Text = AppText.F(
                ShellTextKeys.ClipboardCopiedFormat,
                profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback),
                count)
                + (clipboardOk ? string.Empty : AppText.T(ShellTextKeys.ClipboardBusySuffix));
        }

        /// <summary>
        /// Ctrl+V：把复制过的工作方案粘贴成一份**独立的新方案**（可以撤销）。
        /// 名字重名时按 Windows 文件夹的做法顺延成"名字（2）"。
        ///
        /// 工作区分派同 <see cref="CopyActiveProfileToClipboard"/>：
        /// 「自启动应用」走自启动方案那一套，其它工作区只提示、不改任何数据。
        /// </summary>
        private void PasteProfileFromClipboard()
        {
            string workspace = activeWorkspaceName ?? string.Empty;
            if (string.Equals(activeWorkspaceKey, ShellTextKeys.NavLayoutTitle, StringComparison.Ordinal))
            {
                PasteLayoutFromClipboard();
                return;
            }
            if (string.Equals(activeWorkspaceKey, ShellTextKeys.NavApplicationsTitle, StringComparison.Ordinal))
            {
                PasteApplicationPlanFromClipboard();
                return;
            }
            if (!string.Equals(activeWorkspaceKey, ShellTextKeys.NavStartupTitle, StringComparison.Ordinal))
            {
                statusText.Text = AppText.F(
                    ShellTextKeys.ClipboardPasteNoContentFormat, workspace);
                return;
            }
            string text = copiedProfileText;
            if (string.IsNullOrEmpty(text))
            {
                try
                {
                    if (System.Windows.Clipboard.ContainsText())
                    {
                        text = System.Windows.Clipboard.GetText();
                    }
                }
                catch
                {
                    text = null;
                }
            }
            if (string.IsNullOrEmpty(text))
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardNothingCopied);
                return;
            }
            // 用户要求（2026-10-10 18:20）：复制粘贴只在**同一个工作区**里成立。
            // 如果剪贴板里是别的工作区（例如「图标位置保存」）复制的，就明确拒绝并说明，
            // 不要硬解析成一份空壳方案（那正是用户担心的“莫名的效果”）。
            //
            // 注意：程序优先用自己记住的 `copiedProfileText`，**也要先看系统剪贴板**——
            // 用户完全可能在别的工作区里按了 Ctrl+C（那时剪贴板换成了别的内容），
            // 只信内部记录就会绕过这道检查（自检里正是这样漏过去的）。
            string clipboardNow = null;
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    clipboardNow = System.Windows.Clipboard.GetText();
                }
            }
            catch
            {
                clipboardNow = null;
            }
            string foreign = StartupProfileClipboard.ForeignWorkspaceOf(clipboardNow)
                ?? StartupProfileClipboard.ForeignWorkspaceOf(text);
            if (foreign != null && string.Equals(foreign, ApplicationPlanClipboardWorkspace, StringComparison.Ordinal))
            {
                // 【2026-10-19 用户要求】允许的跨工作区：自启动方案 → 新工作方案（“相当于复制粘贴”）。
                // 取“带自启动应用标记”的那一份文本（系统剪贴板优先，它才是用户刚复制的）。
                string crossText = string.Equals(StartupProfileClipboard.ForeignWorkspaceOf(clipboardNow), foreign, StringComparison.Ordinal)
                    ? clipboardNow
                    : text;
                PasteApplicationPlanAsStartupProfile(crossText);
                return;
            }
            if (foreign != null)
            {
                statusText.Text = AppText.F(
                    ShellTextKeys.ClipboardForeignWorkspaceFormat,
                    WorkspaceDisplayName(foreign));
                return;
            }
            StartupProfile copy = StartupProfileClipboard.DeserializeAsCopy(text, null);
            if (copy == null)
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardPasteNotProfile);
                return;
            }
            StartupProfile created = controller.CreateProfile(MakeUniqueProfileName(copy.Name));
            if (created == null)
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardPasteCreateFailed);
                return;
            }
            created.ReplaceItems(copy.Items);
            controller.SetActiveProfile(created);
            RefreshProfileList();
            int count = created.Items == null ? 0 : created.Items.Count;
            statusText.Text = AppText.F(ShellTextKeys.ClipboardPastedFormat, created.Name, count);
            PushUndo(
                AppText.T(ShellTextKeys.ClipboardUndoPasteProfile),
                delegate
                {
                    controller.DeleteProfile(created);
                    RefreshProfileList();
                },
                delegate
                {
                    // 重做：再粘一份同样内容的（Id 会换新的，内容一致）。
                    StartupProfile again = controller.CreateProfile(MakeUniqueProfileName(copy.Name));
                    if (again != null)
                    {
                        again.ReplaceItems(copy.Items);
                        controller.SetActiveProfile(again);
                        RefreshProfileList();
                    }
                });
        }

        /// <summary>
        /// 【2026-10-19 用户要求】在「启动与恢复」里粘贴一份从「自启动应用」复制的方案：
        /// **整份（连同名字）复制成一份新的工作方案**。
        /// 这与工具栏/卡片「加入启动与恢复」是**同一个动作**（都走 `CreateProfileFromApplicationPlan`），
        /// 只是入口从按钮换成了 Ctrl+C / Ctrl+V —— 用户原话：
        /// “不再加入到选定的方案内了（相当于复制粘贴，后续快捷键的复制粘贴能用到……）”。
        /// </summary>
        private void PasteApplicationPlanAsStartupProfile(string text)
        {
            ApplicationPlan plan = ParseApplicationPlanCopy(text, true);
            if (plan == null)
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardPasteNotApplicationPlan);
                return;
            }
            string sourceName = string.IsNullOrWhiteSpace(plan.Name)
                ? AppText.T(CommonTextKeys.StartupPlanFallback)
                : plan.Name.Trim();
            StartupProfile created = controller.CreateProfileFromApplicationPlan(
                plan, MakeUniqueProfileName(sourceName));
            if (created == null)
            {
                statusText.Text = AppText.T(ShellTextKeys.ClipboardPasteCreateFailed);
                return;
            }
            RefreshProfileList();
            int count = created.Items == null ? 0 : created.Items.Count;
            controller.ReportStatus(AppText.F(
                ShellTextKeys.ClipboardPastedFromApplicationPlanFormat,
                sourceName, created.Name, count));
            PushUndo(
                AppText.T(ShellTextKeys.ClipboardUndoPasteApplicationPlan),
                delegate
                {
                    controller.DeleteProfile(created);
                    RefreshProfileList();
                },
                delegate
                {
                    StartupProfile again = controller.CreateProfileFromApplicationPlan(
                        plan, MakeUniqueProfileName(sourceName));
                    if (again != null)
                    {
                        RefreshProfileList();
                    }
                });
        }

        /// <summary>剪贴板标记里写的是固定的工作区名（不翻译），显示前换成当前语言的名字。</summary>
        private static string WorkspaceDisplayName(string protocolName)
        {
            if (string.Equals(protocolName, StartupProfileClipboard.WorkspaceName, StringComparison.Ordinal))
            {
                return AppText.T(ShellTextKeys.NavStartupTitle);
            }
            if (string.Equals(protocolName, ApplicationPlanClipboardWorkspace, StringComparison.Ordinal))
            {
                return AppText.T(ShellTextKeys.NavApplicationsTitle);
            }
            if (string.Equals(protocolName, DesktopLayoutClipboard.WorkspaceName, StringComparison.Ordinal))
            {
                return AppText.T(ShellTextKeys.NavLayoutTitle);
            }
            return protocolName;
        }

    }
}
