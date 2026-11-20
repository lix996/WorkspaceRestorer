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
using System.Windows.Input;
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
using WorkspaceRestorer.Core.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Drag;

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
        public StartupRegistrationInfo SetAutoStart(bool enabled)
        {
            ThrowIfDisposed();
            // 开机自启动只做一件事：开机后自动打开本程序。与自动恢复彼此独立。
            bool effectiveEnabled = enabled;
            Settings.AutoStart = effectiveEnabled;
            SaveSettings();
            string executablePath;
            try
            {
                executablePath = Process.GetCurrentProcess().MainModule.FileName;
            }
            catch
            {
                executablePath = Assembly.GetEntryAssembly() == null ? null : Assembly.GetEntryAssembly().Location;
            }

            if (startupRegistrationService == null)
            {
                StartupStatus = new StartupRegistrationInfo
                {
                    IsEnabled = false,
                    WasSuppressed = true,
                    Error = AppText.T(ShellTextKeys.ErrorStartupRegistrationServiceUnavailable)
                };
                return StartupStatus;
            }

            try
            {
                object value = Invoke(startupRegistrationService, "SetEnabled", effectiveEnabled);
                StartupStatus = value as StartupRegistrationInfo ?? new StartupRegistrationInfo
                {
                    IsEnabled = effectiveEnabled,
                    Command = executablePath
                };
            }
            catch (Exception exception)
            {
                StartupStatus = new StartupRegistrationInfo
                {
                    IsEnabled = false,
                    Error = exception.Message
                };
                AppLog.Write("Unable to update Windows startup registration.", exception);
            }
            return StartupStatus;

        }

        public void SetAutoRestore(bool enabled)
        {
            ThrowIfDisposed();
            Settings.AutoRestore = enabled;
            if (!enabled)
            {
                CancelRestore();
                CancellationTokenSource layoutCancellation = startupLayoutCancellation;
                startupLayoutCancellation = null;
                if (layoutCancellation != null)
                {
                    layoutCancellation.Cancel();
                }
                // 开机自启动是独立开关，这里不再连带关闭它。
                StatusText = AppText.T(ShellTextKeys.StatusAutoRestoreOff);
            }
            else
            {
                SaveSettings();
                StatusText = AppText.T(ShellTextKeys.StatusAutoRestoreOn);
            }
            OnPropertyChanged("Settings");
        }

        /// <summary>图标位置保存自己的自动恢复开关（与启动与恢复/自启动应用完全独立）。</summary>
        public void SetLayoutAutoRestore(bool enabled)
        {
            ThrowIfDisposed();
            Settings.LayoutAutoRestore = enabled;
            if (!enabled)
            {
                CancellationTokenSource layoutCancellation = startupLayoutCancellation;
                startupLayoutCancellation = null;
                if (layoutCancellation != null)
                {
                    layoutCancellation.Cancel();
                }
                StatusText = AppText.T(ShellTextKeys.StatusLayoutAutoRestoreOff);
            }
            else
            {
                StatusText = AppText.T(ShellTextKeys.StatusLayoutAutoRestoreOn);
            }
            SaveSettings();
            OnPropertyChanged("Settings");
        }

        public void UpdateLayoutTiming(int initialDelaySeconds)
        {
            ThrowIfDisposed();
            Settings.LayoutInitialDelaySeconds = Clamp(initialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusLayoutTimingUpdated);
            OnPropertyChanged("Settings");
        }

        public void ResetLayoutSettings()
        {
            ThrowIfDisposed();
            ControllerSettings defaults = new ControllerSettings();
            Settings.LayoutAutoRestore = defaults.LayoutAutoRestore;
            Settings.LayoutInitialDelaySeconds = defaults.LayoutInitialDelaySeconds;
            Settings.NewLayoutName = defaults.NewLayoutName;
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusLayoutSettingsReset);
            OnPropertyChanged("Settings");
        }

        /// <summary>设置 → 启动与恢复 → 新工作方案名；null/空白 = 用默认名。</summary>
        public void UpdateNewStartupProfileName(string name)
        {
            ThrowIfDisposed();
            Settings.NewStartupProfileName = NormalizeNameSetting(name);
            SaveSettings();
        }

        /// <summary>设置 → 自启动应用 → 新自启动方案名；null/空白 = 用默认名。</summary>
        public void UpdateNewApplicationPlanName(string name)
        {
            ThrowIfDisposed();
            Settings.NewApplicationPlanName = NormalizeNameSetting(name);
            SaveSettings();
        }

        /// <summary>设置 → 图标位置保存 → 新图标布局名；null/空白 = 用默认名。</summary>
        public void UpdateNewLayoutName(string name)
        {
            ThrowIfDisposed();
            Settings.NewLayoutName = NormalizeNameSetting(name);
            SaveSettings();
        }

        private static string NormalizeNameSetting(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            return name.Trim();
        }

        public StartupRegistrationInfo RefreshStartupStatus()
        {
            if (startupRegistrationService == null)
            {
                return StartupStatus;
            }
            try
            {
                object value = Invoke(startupRegistrationService, "GetStatus");
                StartupRegistrationInfo info = value as StartupRegistrationInfo;
                if (info != null)
                {
                    StartupStatus = info;
                    // A suppressed status is returned by the test/isolation
                    // guard.  It does not describe the real registry state and
                    // must not overwrite the user's persisted preference.
                    if (!info.WasSuppressed
                        && string.IsNullOrWhiteSpace(info.Error)
                        && Settings.AutoStart != info.IsEnabled)
                    {
                        Settings.AutoStart = info.IsEnabled;
                        SaveSettings();
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to read Windows startup registration.", exception);
            }
            return StartupStatus;
        }

        public void SaveSettings()
        {
            try
            {
                settingsStore.Save(Settings ?? new ControllerSettings());
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save application settings.", exception);
            }
        }

        /// <summary>
        /// 保存并立即切换界面语言。只接受已验收的语言；切换失败时保持原语言。
        /// 已经打开的窗口、提示和菜单通过 AppText.LanguageChanged 同步刷新，不重启程序。
        /// </summary>
        public void UpdateLanguage(string languageCode)
        {
            ThrowIfDisposed();
            WorkspaceRestorer.Core.Localization.LanguageDefinition selected =
                LanguageCatalog.Find(languageCode);
            if (selected == null || !selected.UiReady)
            {
                return;
            }

            string previous = Settings.Language;
            Settings.Language = selected.Tag;
            if (!AppText.TrySetLanguage(selected.Tag))
            {
                Settings.Language = previous;
                return;
            }
            SaveSettings();
            StatusText = AppText.T(SettingsTextKeys.LanguageAppliedHint);
        }

        /// <summary>保存主题。主题由界面在保存后立即应用。</summary>
        public void UpdateTheme(string theme)
        {
            ThrowIfDisposed();
            if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Theme = "dark";
            }
            else if (string.Equals(theme, "system", StringComparison.OrdinalIgnoreCase))
            {
                Settings.Theme = "system";
            }
            else
            {
                Settings.Theme = "light";
            }
            SaveSettings();
            ThemeService.Apply(ThemeModes.Parse(Settings.Theme));
        }

        /// <summary>只重置“通用”这一节：语言和主题，不动方案、布局和关于信息；两者都立即应用。</summary>
        public void ResetAppearanceSettings()
        {
            ThrowIfDisposed();
            ControllerSettings defaults = new ControllerSettings();
            Settings.Language = defaults.Language;
            Settings.Theme = defaults.Theme;
            SaveSettings();
            AppText.TrySetLanguage(defaults.Language);
            ThemeService.Apply(ThemeModes.Parse(Settings.Theme));
            StatusText = AppText.T(SettingsTextKeys.AppearanceResetHint);
        }

        /// <summary>
        /// Restores only application preferences to their documented system
        /// defaults.  Saved workspaces and desktop layouts are intentionally
        /// left untouched.  Auto-start is also disabled through the same
        /// guarded service used by the checkbox, so test mode never writes the
        /// real Windows registry.
        /// </summary>
        public void ResetSettingsToDefaults()
        {
            ThrowIfDisposed();
            Settings = new ControllerSettings();
            // 重置所有设置也要把快捷键恢复默认；只重置目录不够，页面/路由下一次按键必须看到默认值。
            WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(null);
            // 拖动前缀也回到默认 Shift + 左键。
            DragGestureSettings.Configure(Key.None, ModifierKeys.Shift, MouseButton.Left);
            SaveSettings();
            SetAutoStart(Settings.AutoStart);
            StatusText = AppText.T(ShellTextKeys.StatusDefaultsRestored);
        }

        /// <summary>
        /// 设置页改一整条命令的快捷键（修改/删除都先算出完整列表，再一次性保存）。
        /// 任何一条非法/冲突/写盘失败都整体回滚，界面上不会出现“看着改了、实际没生效”。
        /// </summary>
        internal bool TryUpdateShortcutBindings(
            string commandId,
            IList<WorkspaceRestorer.UI.Shared.ShortcutBinding> bindings,
            out string error)
        {
            error = null;
            ThrowIfDisposed();
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousSettings = Settings.ShortcutBindings;
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousEffective =
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.ExportBindings();
            if (!WorkspaceRestorer.UI.Shared.ShortcutCatalog.TryReplaceCommandBindings(
                commandId, bindings, out error))
            {
                // 目录在失败时不会改；这里只保证 settings 没被碰过。
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(previousSettings);
                return false;
            }
            Settings.ShortcutBindings = WorkspaceRestorer.UI.Shared.ShortcutCatalog.ExportBindings();
            try
            {
                settingsStore.Save(Settings);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save shortcut bindings.", exception);
                Settings.ShortcutBindings = previousSettings;
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(
                    previousSettings ?? previousEffective);
                error = AppText.T(SettingsTextKeys.ShortcutSaveFailed);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 设置 → 快捷键 → 分页条“每页”条数。只接受 5/10/25/50，其它值回到默认 10；
        /// 跟其它设置一起写 settings.json，下次打开按保存的值建分页条。
        /// </summary>
        public void UpdateShortcutPageSize(int pageSize)
        {
            ThrowIfDisposed();
            int normalized = ShortcutPageSettings.NormalizePageSize(pageSize);
            if (Settings.ShortcutPageSize == normalized)
            {
                return;
            }
            Settings.ShortcutPageSize = normalized;
            SaveSettings();
        }

        /// <summary>
        /// 只把一条命令恢复默认快捷键（快捷键页每行的“恢复”图标用）。
        /// 保存失败时把目录和设置都回滚，不留下“界面回默认、磁盘还是旧值”的状态。
        /// </summary>
        internal bool TryResetShortcutCommand(string commandId, out string error)
        {
            error = null;
            ThrowIfDisposed();
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousSettings = Settings.ShortcutBindings;
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousEffective =
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.ExportBindings();
            if (!WorkspaceRestorer.UI.Shared.ShortcutCatalog.TryResetCommandToDefault(commandId, out error))
            {
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(previousSettings);
                return false;
            }
            Settings.ShortcutBindings = WorkspaceRestorer.UI.Shared.ShortcutCatalog.ExportBindings();
            try
            {
                settingsStore.Save(Settings);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to reset shortcut binding.", exception);
                Settings.ShortcutBindings = previousSettings;
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(
                    previousSettings ?? previousEffective);
                error = AppText.T(SettingsTextKeys.ShortcutSaveFailed);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 设置页改“拖动”这一行：前缀组合键 + 鼠标键。
        /// 校验、保存失败都整体回滚；鼠标键由调用方决定（没点左/右键按钮时传原来那个）。
        /// </summary>
        internal bool TryUpdateDragGesture(
            Key key,
            ModifierKeys modifiers,
            MouseButton button,
            out string error)
        {
            error = null;
            ThrowIfDisposed();
            string validation;
            if (!DragGestureSettings.TryValidatePrefix(key, modifiers, out validation))
            {
                error = validation;
                return false;
            }

            Key previousKey = DragGestureSettings.PrefixKey;
            ModifierKeys previousModifiers = DragGestureSettings.PrefixModifiers;
            MouseButton previousButton = DragGestureSettings.MouseButton;
            string previousSettingsKey = Settings.DragGestureKey;
            int? previousSettingsModifiers = Settings.DragGestureModifiers;
            string previousSettingsButton = Settings.DragGestureMouseButton;

            DragGestureSettings.Configure(key, modifiers, button);
            Settings.DragGestureKey = key == Key.None
                ? DragGestureSettings.NoKeyText
                : key.ToString();
            Settings.DragGestureModifiers =
                (int)WorkspaceRestorer.UI.Shared.ShortcutKeyText.NormalizeModifiers(modifiers);
            Settings.DragGestureMouseButton = button == MouseButton.Right
                ? DragGestureSettings.MouseButtonRightText
                : DragGestureSettings.MouseButtonLeftText;
            try
            {
                settingsStore.Save(Settings);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save drag gesture.", exception);
                DragGestureSettings.Configure(previousKey, previousModifiers, previousButton);
                Settings.DragGestureKey = previousSettingsKey;
                Settings.DragGestureModifiers = previousSettingsModifiers;
                Settings.DragGestureMouseButton = previousSettingsButton;
                error = AppText.T(SettingsTextKeys.DragGestureErrorSave);
                return false;
            }
            return true;
        }

        /// <summary>恢复默认快捷键（只恢复绑定，不动其它设置）。</summary>
        internal void ResetShortcutBindings()
        {
            ThrowIfDisposed();
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousSettings = Settings.ShortcutBindings;
            List<WorkspaceRestorer.UI.Shared.ShortcutBindingData> previousEffective =
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.ExportBindings();
            string previousDragKey = Settings.DragGestureKey;
            int? previousDragModifiers = Settings.DragGestureModifiers;
            string previousDragButton = Settings.DragGestureMouseButton;
            Key previousDragKeyValue = DragGestureSettings.PrefixKey;
            ModifierKeys previousDragModifiersValue = DragGestureSettings.PrefixModifiers;
            MouseButton previousDragButtonValue = DragGestureSettings.MouseButton;

            Settings.ShortcutBindings = null;
            WorkspaceRestorer.UI.Shared.ShortcutCatalog.ResetToDefaults();
            // 快捷键页里的“拖动”也是手势绑定：重置所有快捷键时一起回到 Shift + 左键。
            Settings.DragGestureKey = null;
            Settings.DragGestureModifiers = null;
            Settings.DragGestureMouseButton = null;
            DragGestureSettings.Configure(Key.None, ModifierKeys.Shift, MouseButton.Left);
            try
            {
                settingsStore.Save(Settings);
                StatusText = AppText.T(SettingsTextKeys.ShortcutResetAllDone);
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to reset all shortcut bindings.", exception);
                Settings.ShortcutBindings = previousSettings;
                WorkspaceRestorer.UI.Shared.ShortcutCatalog.Configure(
                    previousSettings ?? previousEffective);
                Settings.DragGestureKey = previousDragKey;
                Settings.DragGestureModifiers = previousDragModifiers;
                Settings.DragGestureMouseButton = previousDragButton;
                DragGestureSettings.Configure(
                    previousDragKeyValue, previousDragModifiersValue, previousDragButtonValue);
                StatusText = AppText.T(SettingsTextKeys.ShortcutSaveFailed);
            }
        }

        /// <summary>
        /// Short compatibility alias for UI or automation callers.
        /// </summary>
        public void ResetSettings()
        {
            ResetSettingsToDefaults();
        }

        /// <summary>
        /// Resets only the 总设置 section (the login-startup switch).  Listed
        /// separately from the restore options so each settings page can be reset
        /// on its own.
        /// </summary>
        public void ResetGeneralSettings()
        {
            ThrowIfDisposed();
            // 总设置这一节放"作用于全部"的选项与"开机时自动启动"（它只负责开机拉起程序）。
            bool hadAutoStart = Settings.AutoStart;
            ControllerSettings defaults = new ControllerSettings();
            Settings.AutoStart = false;
            Settings.MaximumCpuPercent = defaults.MaximumCpuPercent;
            Settings.LoadWaitTimeoutSeconds = defaults.LoadWaitTimeoutSeconds;
            Settings.MaximumMemoryPercent = defaults.MaximumMemoryPercent;
            Settings.MemoryWaitTimeoutSeconds = defaults.MemoryWaitTimeoutSeconds;
            // 拖动速度、优先启动项也属于总设置；重置总设置不能漏掉它们。
            Settings.DragScrollSpeed = defaults.DragScrollSpeed;
            Settings.ApplicationsFirst = defaults.ApplicationsFirst;
            SaveSettings();
            if (hadAutoStart)
            {
                SetAutoStart(false);
            }
            StatusText = AppText.T(ShellTextKeys.StatusGeneralSettingsReset);
        }

        /// <summary>
        /// Resets only the 启动与恢复 section: automatic restore, the load gate,
        /// terminal/local-URL scanning and the waiting/interval options.
        /// 拖动速度、优先启动项属于总设置，不能在这里顺手改掉。
        /// </summary>
        public void ResetStartupSettings()
        {
            ThrowIfDisposed();
            ControllerSettings defaults = new ControllerSettings();
            Settings.AutoRestore = defaults.AutoRestore;
            Settings.WaitForLoad = defaults.WaitForLoad;
            Settings.ScanTerminalsAndLocalUrls = defaults.ScanTerminalsAndLocalUrls;
            Settings.InitialDelaySeconds = defaults.InitialDelaySeconds;
            Settings.ItemDelaySeconds = defaults.ItemDelaySeconds;
            Settings.WebPageDelaySeconds = defaults.WebPageDelaySeconds;
            Settings.NewStartupProfileName = defaults.NewStartupProfileName;
            // CPU 上限/等待上限已移到「总设置」，由 ResetGeneralSettings 负责。
            // 开机自启动/关闭、拖动速度、优先启动项也由"总设置"负责，这里不碰。
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusStartupSettingsReset);
        }

        /// <summary>Resets only the 自启动应用 section (its own auto-run/wait/interval options).</summary>
        public void ResetApplicationsSettings()
        {
            ThrowIfDisposed();
            ControllerSettings defaults = new ControllerSettings();
            Settings.ApplicationsAutoRestore = defaults.ApplicationsAutoRestore;
            Settings.ApplicationsWaitForLoad = defaults.ApplicationsWaitForLoad;
            Settings.ApplicationsInitialDelaySeconds = defaults.ApplicationsInitialDelaySeconds;
            Settings.ApplicationsItemDelaySeconds = defaults.ApplicationsItemDelaySeconds;
            Settings.ApplicationsWebPageDelaySeconds = defaults.ApplicationsWebPageDelaySeconds;
            Settings.NewApplicationPlanName = defaults.NewApplicationPlanName;
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusApplicationsSettingsReset);
        }

        public void UpdateTiming(int initialDelaySeconds, int itemDelaySeconds)
        {
            UpdateTiming(initialDelaySeconds, itemDelaySeconds, Settings.WebPageDelaySeconds);
        }

        public void UpdateTiming(int initialDelaySeconds, int itemDelaySeconds, double webPageDelaySeconds)
        {
            Settings.InitialDelaySeconds = Clamp(initialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
            Settings.ItemDelaySeconds = Clamp(itemDelaySeconds, 0, 3600);
            Settings.WebPageDelaySeconds = ClampDouble(webPageDelaySeconds, 0, 3600);
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusTimingUpdated);
        }

        /// <summary>
        /// Sets how fast the list auto-scrolls while an entry is dragged into one
        /// of the "drag here to scroll" strips (1.0 = the default speed).
        /// </summary>
        /// <summary>更新 CPU 上限与最长等待秒数（设置页用）。</summary>
        public void UpdateLoadSettings(double cpuPercent, int timeoutSeconds)
        {
            ThrowIfDisposed();
            Settings.MaximumCpuPercent = ClampDouble(cpuPercent, 5, 100);
            Settings.LoadWaitTimeoutSeconds = Clamp(timeoutSeconds, 0, 3600);
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusCpuLimitUpdated);
        }

        /// <summary>更新内存上限与内存等待上限（总设置用）。</summary>
        public void UpdateMemorySettings(double memoryPercent, int timeoutSeconds)
        {
            ThrowIfDisposed();
            Settings.MaximumMemoryPercent = ClampDouble(memoryPercent, 5, 100);
            Settings.MemoryWaitTimeoutSeconds = Clamp(timeoutSeconds, 0, 3600);
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusMemoryLimitUpdated);
        }
        public void UpdateDragScrollSpeed(double speed)
        {
            Settings.DragScrollSpeed = ClampDouble(speed, 0.5, 3.0);
            SaveSettings();
            StatusText = AppText.T(ShellTextKeys.StatusDragSpeedUpdated);
        }

        /// <summary>
        /// 「自启动应用」分区的开关（用户第 50 条：设置要参考启动与恢复那一节）：
        /// 打开程序时是否自动把当前自启动方案里勾选的项目启动一遍。
        /// 与「启动与恢复」的 AutoRestore 互相独立；两个都打开时先做哪个由
        /// <see cref="UpdateApplicationsFirst"/>（总设置 → 优先启动项）决定。
        /// </summary>
        /// <summary>「自启动应用」自己的等待/间隔（用户 2026-10-19 要求）。</summary>
        public void UpdateApplicationsTiming(int initialDelaySeconds, int itemDelaySeconds, double webPageDelaySeconds)
        {
            Settings.ApplicationsInitialDelaySeconds = Clamp(initialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
            Settings.ApplicationsItemDelaySeconds = Clamp(itemDelaySeconds, 0, 3600);
            Settings.ApplicationsWebPageDelaySeconds = ClampDouble(webPageDelaySeconds, 0, 3600);
            SaveSettings();
        }

        /// <summary>「自启动应用」：是否在高负载时等待。</summary>
        public void SetApplicationsWaitForLoad(bool enabled)
        {
            Settings.ApplicationsWaitForLoad = enabled;
            SaveSettings();
        }

        public void SetApplicationsAutoRestore(bool enabled)
        {
            ThrowIfDisposed();
            Settings.ApplicationsAutoRestore = enabled;
            SaveSettings();
            StatusText = enabled
                ? AppText.T(ShellTextKeys.StatusApplicationsAutoRestoreOn)
                : AppText.T(ShellTextKeys.StatusApplicationsAutoRestoreOff);
        }

        /// <summary>
        /// 「启动与恢复」：保存方案时是否也扫描终端窗口与本地网址（默认关）。
        /// 打开后会照常保存它们，但恢复不了 —— 保存完成时会在提示里点名说清楚。
        /// </summary>
        public void SetScanTerminalsAndLocalUrls(bool enabled)
        {
            ThrowIfDisposed();
            Settings.ScanTerminalsAndLocalUrls = enabled;
            SaveSettings();
            StatusText = enabled
                ? AppText.T(ShellTextKeys.StatusScanTerminalsOn)
                : AppText.T(ShellTextKeys.StatusScanTerminalsOff);
        }

        /// <summary>
        /// 设置“优先启动项”（用户要求 2026-10-10 03:12）：两个分区都开了自动恢复时先做哪一个。
        /// true = 先「自启动应用」；false = 先「启动与恢复」（默认）。
        /// </summary>
        public void UpdateApplicationsFirst(bool applicationsFirst)
        {
            Settings.ApplicationsFirst = applicationsFirst;
            SaveSettings();
            StatusText = applicationsFirst
                ? AppText.T(ShellTextKeys.StatusPriorityApplications)
                : AppText.T(ShellTextKeys.StatusPriorityStartup);
        }

        private ControllerSettings LoadSettings()
        {
            try
            {
                ControllerSettings value = settingsStore.Load();
                if (value == null)
                {
                    return new ControllerSettings();
                }
                // 2026-10-20：旧设置里可能存着大于 60 秒的“启动前等待”；读取时夹回 0–60，
                // 界面和实际恢复都按新上限走（下次保存会把 60 写回磁盘）。
                value.InitialDelaySeconds = Clamp(value.InitialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
                value.ApplicationsInitialDelaySeconds = Clamp(value.ApplicationsInitialDelaySeconds, 0, ControllerSettings.InitialDelayMaximumSeconds);
                value.MaximumCpuPercent = ClampDouble(value.MaximumCpuPercent, 5, 100);
                value.LoadWaitTimeoutSeconds = Clamp(value.LoadWaitTimeoutSeconds, 0, 3600);
                value.MaximumMemoryPercent = ClampDouble(value.MaximumMemoryPercent, 5, 100);
                value.MemoryWaitTimeoutSeconds = Clamp(value.MemoryWaitTimeoutSeconds, 0, 3600);
                if (string.IsNullOrWhiteSpace(value.Language))
                {
                    value.Language = "zh-CN";
                }
                if (string.IsNullOrWhiteSpace(value.Theme))
                {
                    value.Theme = "light";
                }
                // settings.json 被手改成 100 之类时，读回也回到 5/10/25/50 里的值。
                value.ShortcutPageSize = ShortcutPageSettings.NormalizePageSize(
                    value.ShortcutPageSize);
                return value;
            }
            catch
            {
                return new ControllerSettings();
            }
        }

    }
}
