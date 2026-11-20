using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Core;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.StartupRestore.Core;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// Owns the application lifecycle.  Window code only observes this object
    /// and invokes commands, which keeps long-running restore work off the UI
    /// thread and makes the core easy to exercise without creating a window.
    /// </summary>
    // partial：StartupRestore/Core 里有一个薄薄的“自启动应用恢复转接”分部。
    // 代码仍归启动与恢复文件夹，Applications 只调用公开入口。
    public sealed partial class ApplicationController : INotifyPropertyChanged, IDisposable
    {
        private static bool ContainsEquivalentSessionItem(IList<SessionItem> items, SessionItem candidate)
        {
            return FindEquivalentSessionItem(items, candidate) != null;
        }

        private static SessionItem FindEquivalentSessionItem(IList<SessionItem> items, SessionItem candidate)
        {
            if (candidate == null || items == null)
            {
                return null;
            }

            string candidatePath = !string.IsNullOrWhiteSpace(candidate.TargetPath)
                ? candidate.TargetPath
                : candidate.ExecutablePath;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem existing = items[index];
                if (existing == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.Id)
                    && string.Equals(candidate.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }

                // Explorer parents and folder entries are a paired
                // presentation structure.  Never collapse an Explorer
                // parent into another application that happens to launch the
                // same explorer.exe path, and keep duplicate folder windows
                // distinct when they show the same directory.
                if (IsExplorerWindowItem(candidate)
                    || IsExplorerWindowItem(existing)
                    || candidate.Kind == SessionItemKind.ExplorerFolder
                    || existing.Kind == SessionItemKind.ExplorerFolder)
                {
                    if (IsExplorerWindowItem(candidate) && IsExplorerWindowItem(existing))
                    {
                        if (candidate.ApplicationWindowIndex >= 0
                            && existing.ApplicationWindowIndex >= 0
                            && candidate.ApplicationWindowIndex == existing.ApplicationWindowIndex)
                        {
                            return existing;
                        }
                        if (!string.IsNullOrWhiteSpace(candidate.WindowTitle)
                            && string.Equals(
                                candidate.WindowTitle.Trim(),
                                (existing.WindowTitle ?? string.Empty).Trim(),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return existing;
                        }
                    }
                    else if (candidate.Kind == SessionItemKind.ExplorerFolder
                        && existing.Kind == SessionItemKind.ExplorerFolder)
                    {
                        string candidateFolder = NormalizeComparablePath(candidate.TargetPath);
                        string existingFolder = NormalizeComparablePath(existing.TargetPath);
                        if (!string.IsNullOrWhiteSpace(candidateFolder)
                            && string.Equals(candidateFolder, existingFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            if (candidate.ApplicationWindowIndex >= 0
                                && existing.ApplicationWindowIndex >= 0
                                && candidate.ApplicationWindowIndex != existing.ApplicationWindowIndex
                                && CountExplorerFolderMatches(items, candidateFolder) > 1)
                            {
                                continue;
                            }
                            return existing;
                        }
                    }
                    continue;
                }

                // Browser pages are identified by URL, not by the browser
                // executable shared by every tab.  Without this branch a
                // periodic capture would collapse all pages into one item.
                if (candidate.Kind == SessionItemKind.WebPage
                    || existing.Kind == SessionItemKind.WebPage)
                {
                    if (candidate.Kind == SessionItemKind.WebPage
                        && existing.Kind == SessionItemKind.WebPage
                        && HasBrowserTabIdentity(candidate)
                        && HasBrowserTabIdentity(existing)
                        && candidate.BrowserWindowIndex == existing.BrowserWindowIndex
                        && candidate.BrowserTabIndex == existing.BrowserTabIndex
                        && string.Equals(GetBrowserGroupIdentity(candidate), GetBrowserGroupIdentity(existing), StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }

                    if (candidate.Kind == SessionItemKind.WebPage
                        && existing.Kind == SessionItemKind.WebPage
                        && (!HasBrowserTabIdentity(candidate) || !HasBrowserTabIdentity(existing))
                        && !string.IsNullOrWhiteSpace(candidate.Url)
                        && string.Equals(candidate.Url.Trim(), (existing.Url ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }
                    continue;
                }

                if (IsBrowserWindowItem(candidate) || IsBrowserWindowItem(existing))
                {
                    if (IsBrowserWindowItem(candidate)
                        && IsBrowserWindowItem(existing)
                        && candidate.BrowserWindowIndex == existing.BrowserWindowIndex
                        && string.Equals(
                            GetBrowserGroupIdentity(candidate),
                            GetBrowserGroupIdentity(existing),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return existing;
                    }
                    continue;
                }

                if (IsVisualStudioCodeItem(candidate) || IsVisualStudioCodeItem(existing))
                {
                    if (IsVisualStudioCodeItem(candidate) && IsVisualStudioCodeItem(existing))
                    {
                        bool candidateHasPath = !string.IsNullOrWhiteSpace(candidate.TargetPath);
                        bool existingHasPath = !string.IsNullOrWhiteSpace(existing.TargetPath);
                        if (candidateHasPath && existingHasPath)
                        {
                            if (string.Equals(
                                NormalizeComparablePath(candidate.TargetPath),
                                NormalizeComparablePath(existing.TargetPath),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                // Same-folder duplicate windows are still
                                // distinct when both captures expose their
                                // window ordinal. Different workspaces always
                                // match by path, so a changed process order
                                // cannot transfer saved state between them.
                                if (candidate.ApplicationWindowIndex >= 0
                                    && existing.ApplicationWindowIndex >= 0
                                    && candidate.ApplicationWindowIndex != existing.ApplicationWindowIndex)
                                {
                                    // Only use the ordinal as a tie-breaker
                                    // when the saved profile really contains
                                    // more than one window for this folder.
                                    // For a unique workspace, path identity
                                    // must survive a changed process/window
                                    // enumeration order.
                                    int samePathCount = CountCodeWorkspaceMatches(items, candidate.TargetPath);
                                    if (samePathCount > 1)
                                    {
                                        continue;
                                    }
                                }
                                return existing;
                            }

                            // Both paths are known and differ. Do not match by
                            // ordinal: process enumeration order is not a
                            // stable identity and could transfer a disabled
                            // state or saved position to another workspace.
                            continue;
                        }

                        if (candidate.ApplicationWindowIndex >= 0
                            && existing.ApplicationWindowIndex >= 0
                            && candidate.ApplicationWindowIndex == existing.ApplicationWindowIndex)
                        {
                            return existing;
                        }
                    }
                    continue;
                }

                string existingPath = !string.IsNullOrWhiteSpace(existing.TargetPath)
                    ? existing.TargetPath
                    : existing.ExecutablePath;
                if (!string.IsNullOrWhiteSpace(candidatePath)
                    && string.Equals(NormalizeComparablePath(candidatePath), NormalizeComparablePath(existingPath), StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }

            return null;
        }

        private static bool HasBrowserTabIdentity(SessionItem item)
        {
            return item != null
                && item.Kind == SessionItemKind.WebPage
                && (item.BrowserTabIdentityCaptured
                    || string.Equals(item.CaptureSource, "BrowserAddressBar+TabAutomation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.CaptureSource, "BrowserKeyboard", StringComparison.OrdinalIgnoreCase))
                && item.BrowserWindowIndex >= 0
                && item.BrowserTabIndex >= 0;
        }

        private static int CountCodeWorkspaceMatches(IList<SessionItem> items, string workspacePath)
        {
            if (items == null || string.IsNullOrWhiteSpace(workspacePath))
            {
                return 0;
            }

            string normalized = NormalizeComparablePath(workspacePath);
            int count = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (IsVisualStudioCodeItem(item)
                    && !string.IsNullOrWhiteSpace(item.TargetPath)
                    && string.Equals(
                        normalized,
                        NormalizeComparablePath(item.TargetPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }

        private static int CountExplorerFolderMatches(IList<SessionItem> items, string folderPath)
        {
            if (items == null || string.IsNullOrWhiteSpace(folderPath))
            {
                return 0;
            }

            string normalized = NormalizeComparablePath(folderPath);
            int count = 0;
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item != null
                    && item.Kind == SessionItemKind.ExplorerFolder
                    && !string.IsNullOrWhiteSpace(item.TargetPath)
                    && string.Equals(normalized, NormalizeComparablePath(item.TargetPath), StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }

        private static SessionItem FindDesktopEntryItem(IList<SessionItem> items, DesktopEntry entry)
        {
            if (items == null || entry == null)
            {
                return null;
            }

            string selectedPath = NormalizeComparablePath(entry.Path);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return null;
            }

            for (int index = 0; index < items.Count; index++)
            {
                SessionItem item = items[index];
                if (item == null)
                {
                    continue;
                }

                string itemPath = !string.IsNullOrWhiteSpace(item.TargetPath)
                    ? item.TargetPath
                    : item.ExecutablePath;
                if (string.Equals(selectedPath, NormalizeComparablePath(itemPath), StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static string GetParentDirectory(string path)
        {
            try
            {
                string parent = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(path));
                return string.IsNullOrWhiteSpace(parent) ? null : parent;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeComparablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return path.Trim();
            }
        }

        private StartupProfile FindProfile(StartupProfile profile)
        {
            if (profile == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(profile.Id))
            {
                StartupProfile byId = startupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
                {
                    return candidate != null && string.Equals(candidate.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
                });
                if (byId != null)
                {
                    return byId;
                }
            }

            return startupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
            {
                return object.ReferenceEquals(candidate, profile);
            });
        }

        /// <summary>
        /// 用户要求（2026-10-10 23:30）：“我拖动 a 方案的其中一个启动项时，可以加到 b 方案内，
        /// 那么 a 方案就没有这个启动项” —— 把一个启动项从一份方案**搬到另一份方案**。
        /// `insertAfter` 决定插在目标项之后还是之前（null 目标＝追加到末尾）。
        /// 返回是否真的动了数据。
        /// </summary>
        public bool MoveItemBetweenProfiles(StartupProfile from, StartupProfile to, SessionItem item, SessionItem target, bool insertAfter)
        {
            ThrowIfDisposed();
            StartupProfile source = FindProfile(from);
            StartupProfile destination = FindProfile(to);
            if (source == null || destination == null || item == null)
            {
                return false;
            }
            if (object.ReferenceEquals(source, destination))
            {
                return false;
            }
            List<SessionItem> sourceItems = new List<SessionItem>(source.Items);
            int index = sourceItems.IndexOf(item);
            if (index < 0)
            {
                return false;
            }
            List<SessionItem> destinationItems = new List<SessionItem>(destination.Items);
            // 【2026-11-01】整组搬运规则只有一份（StartupRestore/Core/PlanItemGroupMove）：
            //   · 拖浏览器窗口/文件夹父行＝父项 + 全部子项成块搬过去，顺序保持；
            //   · 拖子项＝拒绝（用户要求：子项只能整组走）；
            //   · 目标里已有同身份项＝整组拒绝，不重复放。
            List<SessionItem> newSource;
            List<SessionItem> newDestination;
            string reason;
            if (!PlanItemGroupMove.TryBuild(
                sourceItems,
                destinationItems,
                item,
                target,
                insertAfter,
                DescribeSessionItemForGroupMove,
                ContainsSameTarget,
                out newSource,
                out newDestination,
                out reason))
            {
                return false;
            }
            // 整组一次落盘：先写目标、再写来源，中途失败也不会把项目弄丢（最坏是两份都有）。
            // 不在子项循环里逐项写盘。
            destination.ReplaceItems(newDestination);
            source.ReplaceItems(newSource);
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            return true;
        }

        /// <summary>把 SessionItem 翻译成整组搬运规则认的两个字段。</summary>
        private static PlanItemGroupMove.NodeInfo DescribeSessionItemForGroupMove(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }
            return new PlanItemGroupMove.NodeInfo
            {
                Id = item.Id,
                ParentId = item.ParentId
            };
        }

        private static bool ContainsSameTarget(IList<SessionItem> items, SessionItem candidate)
        {
            if (items == null || candidate == null)
            {
                return false;
            }
            string candidatePath = NormalizeForCompare(candidate.TargetPath);
            string candidateUrl = NormalizeForCompare(candidate.Url);
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem existing = items[index];
                if (existing == null)
                {
                    continue;
                }
                string path = NormalizeForCompare(existing.TargetPath);
                if (candidatePath.Length > 0 && string.Equals(path, candidatePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string url = NormalizeForCompare(existing.Url);
                if (candidateUrl.Length > 0 && string.Equals(url, candidateUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeForCompare(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        }

        /// <summary>把一份方案里的全部项目并进另一份方案（拖动方案卡片到另一张卡片上）。返回并入的项数。</summary>
        public int MergeProfileInto(StartupProfile source, StartupProfile target)
        {
            ThrowIfDisposed();
            StartupProfile from = FindProfile(source);
            StartupProfile into = FindProfile(target);
            if (from == null || into == null || object.ReferenceEquals(from, into))
            {
                return 0;
            }
            List<SessionItem> destinationItems = new List<SessionItem>(into.Items);
            List<SessionItem> sourceItems = new List<SessionItem>(from.Items);
            int added = 0;
            for (int index = 0; index < sourceItems.Count; index++)
            {
                SessionItem item = sourceItems[index];
                if (item == null || ContainsSameTarget(destinationItems, item))
                {
                    continue;
                }
                destinationItems.Add(item);
                added++;
            }
            into.ReplaceItems(destinationItems);
            PersistProfiles();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            return added;
        }

        private bool IsActiveProfile(StartupProfile profile)
        {
            return profile != null
                && profileCatalog != null
                && string.Equals(profileCatalog.ActiveProfileId, profile.Id, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeProfileName(string name)
        {
            return string.IsNullOrWhiteSpace(name)
                ? AppText.T(ShellTextKeys.FallbackWorkProfile)
                : name.Trim();
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("ApplicationController");
            }
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static double ClampDouble(double value, double minimum, double maximum)
        {
            if (double.IsNaN(value))
            {
                return minimum;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static int RemainingMilliseconds(DateTime deadlineUtc)
        {
            double remaining = (deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            if (remaining <= 0)
            {
                return 0;
            }
            if (remaining >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return Math.Max(1, (int)Math.Ceiling(remaining));
        }

        private static string BuildCaptureStatus(string message, SessionSnapshot snapshot)
        {
            string result = message ?? string.Empty;
            if (snapshot != null
                && snapshot.BrowserCaptureIncomplete
                && !string.IsNullOrWhiteSpace(snapshot.CaptureWarning))
            {
                result += AppText.T(ShellTextKeys.StatusDetailSeparator)
                    + snapshot.CaptureWarning.Trim();
            }
            return result;
        }

        /// <summary>把一句提示追加到快照的“保存提示”上（已有内容用“；”接在后面）。</summary>
        /// <summary>
        /// 【2026-10-19】把“扫描终端与本地网址”这次取舍的结果记到快照上（只在内存里，不落盘）。
        /// 保存完成的横幅、托盘气泡、状态栏据此如实说明“跳过了几项 / 保留了几项”，
        /// 不会再被当成“有些网页没读到 → 把最小化的浏览器窗口显示出来”。
        /// </summary>
        private static void ApplyPolicyOutcome(SessionSnapshot snapshot, TerminalAndLocalUrlPolicy.PolicyOutcome outcome)
        {
            if (snapshot == null)
            {
                return;
            }
            snapshot.PolicyAffectedCount = outcome.AffectedCount;
            snapshot.PolicySkippedCount = outcome.SkippedCount;
            snapshot.PolicyIncluded = outcome.Included;
            snapshot.PolicyHint = string.IsNullOrWhiteSpace(outcome.Hint) ? null : outcome.Hint.Trim();
        }

        private static void AppendCaptureWarning(SessionSnapshot snapshot, string warning)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(warning))
            {
                return;
            }
            snapshot.CaptureWarning = string.IsNullOrWhiteSpace(snapshot.CaptureWarning)
                ? warning.Trim()
                : snapshot.CaptureWarning.Trim()
                    + AppText.T(ShellTextKeys.StatusDetailSeparator)
                    + warning.Trim();
        }

        private static void ObserveTask(Task task, string failureMessage)
        {
            if (task == null)
            {
                return;
            }

            task.ContinueWith(delegate(Task completed)
            {
                if (completed == null || !completed.IsFaulted)
                {
                    return;
                }
                try
                {
                    AppLog.Write(failureMessage, completed.Exception);
                }
                catch
                {
                    // Logging must never turn a fire-and-forget callback into
                    // an unobserved exception during application shutdown.
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static ISessionRestoreService CreateSessionRestoreService()
        {
            object value = CreateService("WorkspaceRestorer.Services.SessionRestoreService");
            return value as ISessionRestoreService ?? new DisabledRestoreService();
        }

        private static IDesktopApplicationService CreateDesktopApplicationService()
        {
            object value = CreateService("WorkspaceRestorer.Services.DesktopApplicationService");
            return value as IDesktopApplicationService ?? new DisabledDesktopApplicationService();
        }

        private static object CreateLayoutCoordinator()
        {
            return CreateService("WorkspaceRestorer.Desktop.DesktopLayoutCoordinator");
        }

        private static object CreateService(string fullName)
        {
            try
            {
                Type type = Type.GetType(fullName + ", WorkspaceRestorer", false);
                if (type == null)
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    type = assembly.GetType(fullName, false);
                }
                if (type == null)
                {
                    return null;
                }
                ConstructorInfo parameterless = type.GetConstructor(Type.EmptyTypes);
                if (parameterless != null)
                {
                    return parameterless.Invoke(null);
                }

                // Common service constructors are (IProcessLauncher,
                // ISystemLoadMonitor) or (DesktopIconService, repository).
                List<object> dependencies = new List<object>();
                if (fullName.IndexOf("SessionRestoreService", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dependencies.Add(CreateService("WorkspaceRestorer.Services.ProcessLauncher"));
                    dependencies.Add(CreateService("WorkspaceRestorer.Services.SystemLoadMonitor"));
                }
                else if (fullName.IndexOf("DesktopLayoutCoordinator", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    dependencies.Add(CreateService("WorkspaceRestorer.Desktop.DesktopIconService"));
                    dependencies.Add(CreateService("WorkspaceRestorer.Desktop.DesktopLayoutRepository"));
                }

                foreach (ConstructorInfo constructor in type.GetConstructors())
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    if (parameters.Length != dependencies.Count)
                    {
                        continue;
                    }
                    bool compatible = true;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        if (dependencies[index] == null || !parameters[index].ParameterType.IsInstanceOfType(dependencies[index]))
                        {
                            compatible = false;
                            break;
                        }
                    }
                    if (compatible)
                    {
                        return constructor.Invoke(dependencies.ToArray());
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to create optional service " + fullName + ".", exception);
            }
            return null;
        }

        private static object Invoke(object target, string methodName, params object[] arguments)
        {
            if (target == null)
            {
                return null;
            }
            MethodInfo selected = null;
            MethodInfo[] methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            for (int index = 0; index < methods.Length; index++)
            {
                MethodInfo candidate = methods[index];
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                    || candidate.GetParameters().Length != (arguments == null ? 0 : arguments.Length))
                {
                    continue;
                }
                ParameterInfo[] parameters = candidate.GetParameters();
                bool compatible = true;
                for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    object argument = arguments[parameterIndex];
                    if (argument != null && !parameters[parameterIndex].ParameterType.IsInstanceOfType(argument))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (compatible)
                {
                    selected = candidate;
                    break;
                }
            }
            return selected == null ? null : selected.Invoke(target, arguments);
        }

    }
}
