using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI.Cards.Interaction;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「自启动应用」的点击跳转转接层（2026-10-31）。
    ///
    /// 用户原话：
    ///   “自启动应用，点击跳转的逻辑，也要链接启动与恢复……我尝试点击网页跳转的时候，
    ///    居然无法跳转，而是打开了网页（我从启动与恢复复制方案到自启动应用）。”
    ///
    /// 所以这里**不再自己找窗口/激活窗口**，只做两件事：
    ///   1. 把 `ApplicationPlanItem` 翻译成「启动与恢复」认识的 `SessionItem`；
    ///   2. 调用 `StartupRestore/UI/StartupJumpForApplications.cs` 的
    ///      `JumpToExternalSessionItem(...)`，复用那边的：
    ///        · 浏览器标签精确定位（按网址/保存的标签序号/名字）；
    ///        · Explorer 文件夹路径匹配、Explorer 子项解析；
    ///        · 文档按默认程序找已打开窗口；
    ///        · 应用按进程 exe + 标题找窗口；
    ///        · 找到后前置，找不到才打开，打开后 1.6 秒再聚焦。
    ///
    /// 数据层为什么不能直接按字段复制：
    ///   · `ApplicationPlanItem.Kind` 经常是默认值 0（`AddDesktopEntriesToApplicationPlan`
    ///     只写 DisplayName/TargetPath/IsEnabled，不写 Kind），所以这里按路径重新推断；
    ///   · `.lnk` / `.bat` 的“启动目标”和“跳转匹配目标”不是同一个：
    ///     跳转按快捷方式目标 exe / cmd.exe 找窗口，启动仍用原来的 TargetPath；
    ///     因此使用 `SessionItem.JumpExecutablePath`（运行时不落盘），
    ///     不改 `ExecutablePath`，避免丢掉快捷方式参数/工作目录。
    /// </summary>
    public sealed partial class MainWindow
    {
        private const string ApplicationJumpIdPrefix = "app-jump-";

        /// <summary>一次跳转用的适配结果：条目清单 + 原对象到 SessionItem 的映射。</summary>
        private sealed class ApplicationJumpSessionMap
        {
            internal readonly List<SessionItem> Items = new List<SessionItem>();
            internal readonly Dictionary<ApplicationPlanItem, SessionItem> BySource =
                new Dictionary<ApplicationPlanItem, SessionItem>();
            internal readonly Dictionary<string, SessionItem> BySourceId =
                new Dictionary<string, SessionItem>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>点击自启动方案里的一项：交给启动与恢复的跳转内核。</summary>
        private void JumpToApplicationItem(ApplicationPlan plan, ApplicationPlanItem item)
        {
            if (item == null)
            {
                return;
            }
            ApplicationJumpSessionMap map = BuildApplicationJumpSessionMap(plan);
            SessionItem target;
            if (!map.BySource.TryGetValue(item, out target))
            {
                target = CreateApplicationJumpSessionItem(item);
            }
            if (target == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoPathToOpen);
                return;
            }
            JumpToExternalSessionItem(map.Items, target);
        }

        /// <summary>
        /// 整份方案 → SessionItem 清单。必须整份转换：Explorer 父行要靠
        /// `ResolveExplorerChild` 在清单里找子文件夹；只转被点的那一条会丢父子关系。
        /// </summary>
        private ApplicationJumpSessionMap BuildApplicationJumpSessionMap(ApplicationPlan plan)
        {
            ApplicationJumpSessionMap map = new ApplicationJumpSessionMap();
            if (plan == null || plan.Items == null)
            {
                return map;
            }
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem source = plan.Items[index];
                if (source == null)
                {
                    continue;
                }
                SessionItem session = CreateApplicationJumpSessionItem(source);
                if (session == null)
                {
                    continue;
                }
                map.Items.Add(session);
                map.BySource[source] = session;
                if (!string.IsNullOrWhiteSpace(source.Id))
                {
                    map.BySourceId[source.Id] = session;
                }
            }

            // 第二趟再连父子：父项可能排在子项后面。
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem source = plan.Items[index];
                SessionItem session;
                if (source == null
                    || !map.BySource.TryGetValue(source, out session)
                    || string.IsNullOrWhiteSpace(source.ParentId))
                {
                    continue;
                }
                SessionItem parent;
                if (map.BySourceId.TryGetValue(source.ParentId, out parent))
                {
                    session.ParentId = parent.Id;
                }
            }
            return map;
        }

        /// <summary>
        /// 单条 `ApplicationPlanItem` → `SessionItem`。
        /// 判定顺序很关键：浏览器网页 → 网址/.url → 分组父项 → 文件夹 → 文件扩展名。
        /// </summary>
        private static SessionItem CreateApplicationJumpSessionItem(ApplicationPlanItem source)
        {
            if (source == null)
            {
                return null;
            }
            string raw = (source.TargetPath ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return null;
            }

            string expanded = raw;
            try
            {
                expanded = Environment.ExpandEnvironmentVariables(raw);
            }
            catch
            {
            }

            SessionItem item = new SessionItem
            {
                Id = ApplicationJumpIdPrefix
                    + (string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id),
                DisplayName = string.IsNullOrWhiteSpace(source.DisplayName) ? raw : source.DisplayName.Trim(),
                TargetPath = raw,
                WindowTitle = source.WindowTitle,
                IsEnabled = source.IsEnabled,
                StartTimeUtc = source.StartTimeUtc == default(DateTime) ? DateTime.UtcNow : source.StartTimeUtc,
                WindowPlacement = source.WindowPlacement == null ? null : source.WindowPlacement.Clone(),
                CaptureSource = "ApplicationPlan"
            };

            // 1) 从启动与恢复复制过来的浏览器页，字段最全，优先走精确标签跳转。
            if (source.IsBrowserPage || source.Kind == SessionItemKind.WebPage)
            {
                item.Kind = SessionItemKind.WebPage;
                item.Url = raw;
                item.BrowserExecutablePath = source.BrowserExecutablePath;
                item.BrowserName = source.BrowserName;
                item.BrowserGroupKey = source.BrowserGroupKey;
                item.BrowserWindowIndex = source.BrowserWindowIndex;
                item.BrowserTabIndex = source.BrowserTabIndex;
                item.BrowserTabIdentityCaptured = source.BrowserTabIdentityCaptured;
                item.IsActiveBrowserTab = source.IsActiveBrowserTab;
                item.BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs);
                item.BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot;
                item.BrowserBindingKind = source.BrowserBindingKind;
                item.BrowserBindingUpdatedAtUtc = source.BrowserBindingUpdatedAtUtc;
                item.CaptureSource = "ApplicationPlanBrowser";
                return item;
            }

            // 2) 普通网址 / .url：映射成 WebPage；没有浏览器字段时启动与恢复会退回收起打开。
            string url = TryGetApplicationJumpUrl(raw);
            if (!string.IsNullOrWhiteSpace(url))
            {
                item.Kind = SessionItemKind.WebPage;
                item.Url = url;
                item.TargetPath = raw;
                item.BrowserExecutablePath = source.BrowserExecutablePath;
                item.BrowserName = source.BrowserName;
                return item;
            }

            // 3) 分组父项。
            if (source.IsGroupParent)
            {
                if (source.IsBrowserWindow)
                {
                    item.Kind = SessionItemKind.Application;
                    item.CaptureSource = "BrowserWindow";
                    item.ExecutablePath = FirstNonEmpty(source.BrowserExecutablePath, raw);
                    item.BrowserExecutablePath = source.BrowserExecutablePath;
                    item.BrowserName = source.BrowserName;
                    item.BrowserGroupKey = source.BrowserGroupKey;
                    item.BrowserWindowIndex = source.BrowserWindowIndex;
                    item.BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs);
                    item.BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot;
                    item.BrowserBindingKind = source.BrowserBindingKind;
                    return item;
                }
                if (source.IsFolderGroup || Directory.Exists(expanded))
                {
                    // 文件夹父项直接按路径跳，比“按 explorer.exe 找任意窗口”精确。
                    item.Kind = SessionItemKind.ExplorerFolder;
                    item.CaptureSource = "ApplicationPlanGroup";
                    item.ExecutablePath = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe");
                    return item;
                }
                item.Kind = SessionItemKind.Application;
                item.CaptureSource = "ApplicationPlanGroup";
                item.ExecutablePath = IsExecutablePath(raw) ? raw : null;
                return item;
            }

            // 4) 文件夹（扫描/浏览加进来的文件夹 Kind 可能还是默认 0）。
            if (Directory.Exists(expanded) || source.Kind == SessionItemKind.ExplorerFolder)
            {
                item.Kind = SessionItemKind.ExplorerFolder;
                item.ExecutablePath = Environment.ExpandEnvironmentVariables("%WINDIR%\\explorer.exe");
                return item;
            }

            string extension = string.Empty;
            try
            {
                extension = Path.GetExtension(raw) ?? string.Empty;
            }
            catch
            {
            }

            // 5) 程序：.exe / .com 直接按进程 exe 找。
            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase))
            {
                item.Kind = SessionItemKind.Application;
                item.ExecutablePath = raw;
                return item;
            }

            // 6) .lnk / .bat / .cmd：启动用 TargetPath，跳转匹配用 JumpExecutablePath。
            if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                item.Kind = SessionItemKind.Document;
                item.JumpExecutablePath = ResolveApplicationJumpShortcut(raw);
                return item;
            }
            if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
            {
                item.Kind = SessionItemKind.Document;
                item.JumpExecutablePath = CommandProcessorPath();
                return item;
            }

            // 7) 普通文件：按文档窗口找。
            item.Kind = SessionItemKind.Document;
            return item;
        }

        /// <summary>
        /// 自启动应用网页行右键“设置网页归属…”：复用启动与恢复的窗口/标签枚举，
        /// 写回自启动方案自己的父项和绑定字段。
        /// </summary>
        private void SetApplicationBrowserPageBinding(ApplicationPlan plan, ApplicationPlanItem item)
        {
            if (plan == null || item == null || !item.IsBrowserPage)
            {
                return;
            }
            WorkspaceRestorer.Infrastructure.AppLog.Write(
                "[网页归属] 右键菜单已点击：" + (item.DisplayName ?? "网页"));
            SessionItem page = CreateApplicationJumpSessionItem(item);
            BrowserPageBindingOption option = ChooseBrowserPageBinding(page);
            if (option == null)
            {
                return;
            }

            string groupKey = item.BrowserGroupKey;
            if (string.IsNullOrWhiteSpace(groupKey))
            {
                groupKey = NormalizeExecutablePath(item.BrowserExecutablePath);
            }
            ApplicationPlanItem parent = FindApplicationBrowserParentForBinding(
                plan, item, groupKey, option.WindowTitle);
            if (parent == null)
            {
                int nextIndex = 0;
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem candidate = plan.Items[index];
                    if (candidate != null
                        && candidate.IsBrowserWindow
                        && !string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                        && string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase)
                        && candidate.BrowserWindowIndex >= nextIndex)
                    {
                        nextIndex = candidate.BrowserWindowIndex + 1;
                    }
                }
                string browserName = FirstNonEmpty(
                item.BrowserName,
                AppText.T(ApplicationsTextKeys.BrowserFallback));
                parent = new ApplicationPlanItem
                {
                    DisplayName = browserName + " (" + (nextIndex + 1) + ")",
                    TargetPath = FirstNonEmpty(item.BrowserExecutablePath, item.BrowserGroupKey),
                    Kind = SessionItemKind.Application,
                    IsEnabled = true,
                    IsGroupParent = true,
                    IsBrowserWindow = true,
                    BrowserGroupKey = groupKey,
                    BrowserExecutablePath = item.BrowserExecutablePath,
                    BrowserName = browserName,
                    BrowserWindowIndex = nextIndex,
                    WindowTitle = option.WindowTitle,
                    BrowserTabs = BrowserTabFingerprint.CloneList(option.Tabs),
                    BrowserWindowTitleSnapshot = option.WindowTitle,
                    BrowserBindingKind = BrowserPageBindingKind.UserBound,
                    BrowserBindingUpdatedAtUtc = DateTime.UtcNow
                };
                int pageIndex = plan.Items.IndexOf(item);
                if (pageIndex < 0)
                {
                    pageIndex = plan.Items.Count;
                }
                plan.Items.Insert(pageIndex, parent);
            }

            string resolvedUrl = ResolveBrowserTabUrlForBinding(option);
            List<BrowserTabFingerprint> tabs = BrowserTabFingerprint.CloneList(option.Tabs);
            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                option.TabUrl = resolvedUrl;
                for (int index = 0; index < tabs.Count; index++)
                {
                    if (tabs[index] != null && tabs[index].Index == option.TabIndex)
                    {
                        tabs[index].Url = resolvedUrl;
                        break;
                    }
                }
            }

            item.ParentId = parent.Id;
            item.BrowserWindowIndex = parent.BrowserWindowIndex;
            item.BrowserTabIndex = option.TabIndex;
            item.WindowTitle = option.TabName;
            if (!string.IsNullOrWhiteSpace(resolvedUrl))
            {
                item.TargetPath = resolvedUrl;
            }
            item.BrowserExecutablePath = FirstNonEmpty(
                item.BrowserExecutablePath, parent.BrowserExecutablePath);
            item.BrowserGroupKey = groupKey;
            item.BrowserTabs = tabs;
            item.BrowserWindowTitleSnapshot = option.WindowTitle;
            item.BrowserBindingKind = BrowserPageBindingKind.UserBound;
            item.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
            item.BrowserTabIdentityCaptured = true;
            item.IsActiveBrowserTab = option.IsActive;
            parent.BrowserTabs = BrowserTabFingerprint.CloneList(tabs);
            parent.BrowserWindowTitleSnapshot = option.WindowTitle;

            controller.SetApplicationPlanItemsOrder(plan, plan.Items);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(
                ApplicationsTextKeys.WebBindingSetFormat,
                item.DisplayName ?? AppText.T(ApplicationsTextKeys.WebPageFallback),
                parent.DisplayName ?? AppText.T(ApplicationsTextKeys.BrowserWindowFallback),
                option.TabIndex + 1);
        }

        private static ApplicationPlanItem FindApplicationBrowserParentForBinding(
            ApplicationPlan plan,
            ApplicationPlanItem page,
            string groupKey,
            string windowTitle)
        {
            if (plan == null || plan.Items == null || page == null)
            {
                return null;
            }
            ApplicationPlanItem byTitle = null;
            int titleHits = 0;
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem candidate = plan.Items[index];
                if (candidate == null
                    || !candidate.IsBrowserWindow
                    || string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                    || !string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (BrowserTabNameLooksLike(candidate.WindowTitle, windowTitle))
                {
                    titleHits++;
                    byTitle = candidate;
                }
            }
            if (titleHits == 1)
            {
                return byTitle;
            }
            if (!string.IsNullOrWhiteSpace(page.ParentId))
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem candidate = plan.Items[index];
                    if (candidate != null
                        && candidate.IsBrowserWindow
                        && string.Equals(candidate.Id, page.ParentId, StringComparison.OrdinalIgnoreCase)
                        && (string.IsNullOrWhiteSpace(candidate.WindowTitle)
                            || BrowserTabNameLooksLike(candidate.WindowTitle, windowTitle)))
                    {
                        return candidate;
                    }
                }
            }

            ApplicationPlanItem only = null;
            int parents = 0;
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem candidate = plan.Items[index];
                if (candidate != null
                    && candidate.IsBrowserWindow
                    && !string.IsNullOrWhiteSpace(candidate.BrowserGroupKey)
                    && string.Equals(candidate.BrowserGroupKey, groupKey, StringComparison.OrdinalIgnoreCase))
                {
                    parents++;
                    only = candidate;
                }
            }
            if (parents == 1
                && (string.IsNullOrWhiteSpace(only.WindowTitle)
                    || BrowserTabNameLooksLike(only.WindowTitle, windowTitle)))
            {
                return only;
            }
            return null;
        }

        private static string BuildApplicationBrowserAwareJumpHint(
            ApplicationPlan plan,
            ApplicationPlanItem item)
        {
            string hint = WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.JumpHint;
            if (item == null || !item.IsBrowserPage || plan == null || plan.Items == null)
            {
                return hint;
            }
            ApplicationPlanItem parent = null;
            if (!string.IsNullOrWhiteSpace(item.ParentId))
            {
                for (int index = 0; index < plan.Items.Count; index++)
                {
                    ApplicationPlanItem candidate = plan.Items[index];
                    if (candidate != null
                        && candidate.IsBrowserWindow
                        && string.Equals(candidate.Id, item.ParentId, StringComparison.OrdinalIgnoreCase))
                    {
                        parent = candidate;
                        break;
                    }
                }
            }
            if (parent == null)
            {
                return hint + AppText.T(ApplicationsTextKeys.BindingUnbound);
            }
            string tab = item.BrowserTabIndex >= 0
                ? AppText.F(ApplicationsTextKeys.BindingTabFormat, item.BrowserTabIndex + 1)
                : AppText.T(ApplicationsTextKeys.BindingTabUnknown);
            return hint + AppText.F(
                ApplicationsTextKeys.BindingOwnerFormat,
                parent.DisplayName ?? AppText.T(ApplicationsTextKeys.BrowserWindowFallback),
                tab,
                item.BrowserBindingKind == BrowserPageBindingKind.UserBound
                    ? AppText.T(ApplicationsTextKeys.BindingManualSuffix)
                    : string.Empty);
        }

        private static bool IsExecutablePath(string path)
        {
            string extension = Path.GetExtension(path) ?? string.Empty;
            return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase);
        }

        private static string CommandProcessorPath()
        {
            try
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            }
            catch
            {
                return Environment.ExpandEnvironmentVariables("%WINDIR%\\System32\\cmd.exe");
            }
        }

        /// <summary>网址或 .url 文件 → 网址；不是就返回 null。</summary>
        private static string TryGetApplicationJumpUrl(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
            if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        {
                            return trimmed.Substring(4).Trim();
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        /// <summary>.lnk 用 WScript.Shell 解析目标 exe；其它返回 null。</summary>
        private static string ResolveApplicationJumpShortcut(string path)
        {
            object shell = null;
            try
            {
                Type type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null)
                {
                    return null;
                }
                shell = Activator.CreateInstance(type);
                object shortcut = type.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                if (shortcut == null)
                {
                    return null;
                }
                object target = shortcut.GetType().InvokeMember("TargetPath",
                    System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
                string text = target as string;
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    {
                        System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                    }
                }
                catch
                {
                }
            }
        }

        private static string FirstNonEmpty(string first, string second)
        {
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
            return second;
        }
    }
}
