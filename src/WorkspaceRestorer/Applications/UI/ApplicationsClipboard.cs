using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    // 工作区：自启动应用 —— **Ctrl+C / Ctrl+V**（用户 2026-10-18 原话：“快捷键，CTRL+c，ctrl+v，复制粘贴无效”）
    //
    // 【为什么以前“无效”】
    // `Shared/UI/Shell/ShortcutCatalog` 早就把 Ctrl+C / Ctrl+V 定义成“全部工作区”，
    // 但 `MainWindow.WireGlobalShortcuts` 里绑的动作是「启动与恢复」那一套：
    //   · `CopyActiveProfileToClipboard` 复制的是 `controller.ActiveProfile`，也就是**工作方案**；
    //   · 序列化时固定打上 `CSK1|启动与恢复` 标记；
    //   · `PasteProfileFromClipboard` 粘出来的是**工作方案**（`controller.CreateProfile`）。
    // 所以在「自启动应用」里按 Ctrl+C / Ctrl+V：
    //   复制的是「启动与恢复」的当前工作方案，粘贴是在「启动与恢复」里新建一份工作方案 ——
    //   当前页面上什么都看不到，用户看到的就是“复制粘贴无效”。
    //   （2026-10-17 23:13:52 那次真的粘出了「工作方案 副本」，只是它跑到另一个工作区去了。）
    //
    // 【现在的口径（与「启动与恢复」完全对称）】
    //   · 在「自启动应用」按 Ctrl+C：复制**当前自启动方案**（连同里面的启动项目）；
    //   · 按 Ctrl+V：粘成一份**独立的**自启动方案（新方案 Id、新项目 Id、名字按 Windows 方式顺延），
    //     立刻落盘、卡片列表刷新、并进全局撤销栈（Ctrl+Z 能撤）；
    //   · 跨工作区照样明确拒绝：在「启动与恢复」按 Ctrl+V 不能粘自启动方案，反过来也一样。
    public sealed partial class MainWindow
    {
        /// <summary>剪贴板内容的出身标记前缀（与 `StartupProfileClipboard.ClipboardHeaderPrefix` 同一套格式）。</summary>
        private const string ApplicationPlanClipboardPrefix = "CSK1|";

        /// <summary>本工作区写在标记里的名字（用来挡住跨工作区粘贴）。</summary>
        private const string ApplicationPlanClipboardWorkspace = "自启动应用";

        /// <summary>上一次 Ctrl+C 复制下来的自启动方案文本（系统剪贴板被别的程序改掉也能用）。</summary>
        private string copiedApplicationPlanText;

        // ================================================================ Ctrl+C

        /// <summary>Ctrl+C：把当前自启动方案整份记下来（同时写入系统剪贴板文本，方便贴到别处看）。</summary>
        private void CopyActiveApplicationPlanToClipboard()
        {
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            if (plan == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoCurrentPlanCopy);
                return;
            }
            string text = SerializeApplicationPlan(plan);
            if (string.IsNullOrEmpty(text))
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusClipboardCopyFailed);
                return;
            }
            copiedApplicationPlanText = text;
            string clipboardError;
            bool clipboardOk = ClipboardText.TrySetText(text, out clipboardError);
            if (!clipboardOk)
            {
                AppLog.Write("[剪贴板] 复制自启动方案到系统剪贴板失败：" + clipboardError);
            }
            int count = plan.Items == null ? 0 : plan.Items.Count;
            statusText.Text = AppText.F(
                ApplicationsTextKeys.StatusCopiedFormat,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                count)
                + (clipboardOk ? string.Empty : AppText.T(ApplicationsTextKeys.StatusClipboardBusySuffix));
        }

        // ================================================================ Ctrl+V

        /// <summary>Ctrl+V：把复制过的自启动方案粘成一份独立的新方案（可以 Ctrl+Z 撤销）。</summary>
        private void PasteApplicationPlanFromClipboard()
        {
            string text = copiedApplicationPlanText;
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
            if (string.IsNullOrEmpty(text))
            {
                text = clipboardNow;
            }
            if (string.IsNullOrEmpty(text))
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNothingCopied);
                return;
            }
            // 跨工作区保护（与「启动与恢复」同一条规矩）：剪贴板里是别的工作区的东西就明确拒绝，
            // 不要硬解析成一份空壳方案。**先看系统剪贴板**：用户可能刚从别的页面复制过。
            string foreign = WorkspaceNameOfClipboardText(clipboardNow);
            if (foreign == null)
            {
                foreign = WorkspaceNameOfClipboardText(text);
            }
            if (foreign != null && !string.Equals(foreign, ApplicationPlanClipboardWorkspace, StringComparison.Ordinal))
            {
                // 【2026-10-19 用户要求】允许的跨工作区：把「启动与恢复」复制的方案整份粘成
                // 一份新自启动方案（“反之如此”）。真正的转换在控制器里。
                if (string.Equals(foreign, StartupProfileClipboard.WorkspaceName, StringComparison.Ordinal))
                {
                    string crossText = string.Equals(WorkspaceNameOfClipboardText(clipboardNow), foreign, StringComparison.Ordinal)
                        ? clipboardNow
                        : text;
                    PasteStartupProfileAsApplicationPlan(crossText);
                    return;
                }
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusForeignWorkspaceFormat, foreign);
                return;
            }
            ApplicationPlan copy = ParseApplicationPlanCopy(text);
            if (copy == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusPasteNotPlan);
                return;
            }
            string name = MakeUniqueApplicationPlanName(copy.Name);
            // 与“新建空白方案”同一套：先拍快照 → 新建 → 填项目 → 刷新 → 记撤销。
            ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
            ApplicationPlan created = controller.CreateApplicationPlan(name);
            if (created == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusPasteCreateFailed);
                return;
            }
            created.Items = new List<ApplicationPlanItem>();
            if (copy.Items != null)
            {
                for (int index = 0; index < copy.Items.Count; index++)
                {
                    ApplicationPlanItem item = CloneApplicationPlanItemForPaste(copy.Items[index]);
                    if (item != null)
                    {
                        created.Items.Add(item);
                    }
                }
            }
            // SetActive 会落盘 + 同步方案镜像文件夹（刚新建的那一份还没内容，要再存一次）。
            controller.SetActiveApplicationPlan(created);
            RefreshApplicationPlanList();
            int count = created.Items.Count;
            statusText.Text = AppText.F(ApplicationsTextKeys.StatusPastedFormat, created.Name, count);
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoPasteFormat, created.Name),
                before);
        }

        // ================================================================ 跨工作区粘贴（启动与恢复 → 自启动应用）

        /// <summary>
        /// 【2026-10-19 用户要求】在「自启动应用」里粘贴一份从「启动与恢复」复制的方案：
        /// **整份（连同名字）复制成一份新的自启动方案**（“反之如此”）。
        /// 只搬“能启动的东西”：程序取可执行路径、文件夹/文档取目标路径、网页取网址；
        /// 没路径的条目跳过，数量在状态栏里说清楚。转换在控制器里（`CreateApplicationPlanFromProfile`）。
        /// </summary>
        private void PasteStartupProfileAsApplicationPlan(string text)
        {
            StartupProfile profile = StartupProfileClipboard.DeserializeAsCopy(text, null, true);
            if (profile == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusPasteProfileNotPlan);
                return;
            }
            string sourceName = string.IsNullOrWhiteSpace(profile.Name)
                ? AppText.T(CommonTextKeys.WorkProfileFallback)
                : profile.Name.Trim();
            ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
            ApplicationPlan created = controller.CreateApplicationPlanFromProfile(
                profile, MakeUniqueApplicationPlanName(sourceName));
            if (created == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusPasteCreateFailed);
                return;
            }
            RefreshApplicationPlanList();
            int count = created.Items == null ? 0 : created.Items.Count;
            int skipped = controller.LastPlanCopySkippedCount;
            controller.ReportStatus(skipped > 0
                ? AppText.F(
                    ApplicationsTextKeys.StatusPastedFromProfileSkippedFormat,
                    sourceName, created.Name, count, skipped)
                : AppText.F(
                    ApplicationsTextKeys.StatusPastedFromProfileFormat,
                    sourceName, created.Name, count));
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoPasteFromProfileFormat, created.Name),
                before);
        }

        // ================================================================ 序列化 / 解析

        /// <summary>把一份自启动方案序列化成文本（带工作区标记；失败返回 null，不抛异常）。</summary>
        private static string SerializeApplicationPlan(ApplicationPlan plan)
        {
            if (plan == null)
            {
                return null;
            }
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ApplicationPlan));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, plan);
                    return ApplicationPlanClipboardPrefix + ApplicationPlanClipboardWorkspace + "\n"
                        + Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>剪贴板文本里标记的工作区名；没有标记（用户手工贴的 JSON）返回 null。</summary>
        private static string WorkspaceNameOfClipboardText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(ApplicationPlanClipboardPrefix, StringComparison.Ordinal))
            {
                return null;
            }
            int end = trimmed.IndexOf('\n');
            if (end < 0)
            {
                return null;
            }
            string name = trimmed.Substring(
                ApplicationPlanClipboardPrefix.Length, end - ApplicationPlanClipboardPrefix.Length).Trim();
            return name.Length == 0 ? null : name;
        }

        /// <summary>去掉剪贴板标记，只留 JSON（没有标记就原样返回）。</summary>
        private static string StripApplicationPlanClipboardHeader(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(ApplicationPlanClipboardPrefix, StringComparison.Ordinal))
            {
                return text;
            }
            int end = trimmed.IndexOf('\n');
            return end < 0 ? text : trimmed.Substring(end + 1);
        }

        /// <summary>把文本还原成一份**副本**方案：新方案 Id、新项目 Id，名字加“副本”（重名由调用方顺延）。</summary>
        private static ApplicationPlan ParseApplicationPlanCopy(string text, bool keepSourceName = false)
        {
            text = StripApplicationPlanClipboardHeader(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            ApplicationPlan parsed = null;
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(ApplicationPlan));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    parsed = serializer.ReadObject(stream) as ApplicationPlan;
                }
            }
            catch (Exception)
            {
                return null;
            }
            if (parsed == null)
            {
                return null;
            }
            // 兜底（与「启动与恢复」同一条）：什么都不像就不是本程序的方案，别粘出一份空壳。
            bool hasIdentity = !string.IsNullOrWhiteSpace(parsed.Id) || !string.IsNullOrWhiteSpace(parsed.Name);
            bool hasItems = parsed.Items != null && parsed.Items.Count > 0;
            if (!hasIdentity && !hasItems)
            {
                return null;
            }
            ApplicationPlan copy = new ApplicationPlan();
            string sourceName = string.IsNullOrWhiteSpace(parsed.Name)
                ? AppText.T(ApplicationsTextKeys.PlanNameFallback)
                : parsed.Name.Trim();
            copy.Name = keepSourceName
                ? sourceName
                : (sourceName + AppText.T(ApplicationsTextKeys.CopySuffix));
            copy.CreatedAtUtc = DateTime.UtcNow;
            copy.UpdatedAtUtc = copy.CreatedAtUtc;
            copy.Items = new List<ApplicationPlanItem>();
            if (parsed.Items != null)
            {
                for (int index = 0; index < parsed.Items.Count; index++)
                {
                    ApplicationPlanItem item = CloneApplicationPlanItemForPaste(parsed.Items[index]);
                    if (item != null)
                    {
                        copy.Items.Add(item);
                    }
                }
            }
            return copy;
        }

        /// <summary>
        /// 复制一个启动项给“粘贴”用：**必须换新 Id**（撤销栈里那份 `CloneApplicationPlanItem`
        /// 是原样保留 Id 的，那是给快照用的，别混用）。
        /// </summary>
        private static ApplicationPlanItem CloneApplicationPlanItemForPaste(ApplicationPlanItem item)
        {
            if (item == null)
            {
                return null;
            }
            ApplicationPlanItem copy = new ApplicationPlanItem();
            copy.Id = Guid.NewGuid().ToString("N");
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
    }
}
