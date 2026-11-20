using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Desktop;

namespace WorkspaceRestorer.UI
{
    /// <summary>Application entry point for the code-only WPF build.</summary>
    public sealed class App : Application
    {
        private SingleInstanceGuard instanceGuard;
        private EventWaitHandle activateSignal;
        private volatile bool shutdownSignalled;
        private TrayIconService trayIcon;
        private TrayMenuHost trayMenuHost;
        private ApplicationController controller;
        private MainWindow mainWindow;
        private DispatcherTimer backgroundSnapshotTimer;
        private bool exitRequested;
        private const string SingleInstanceSignalName = @"Local\" + ProductIdentity.TechnicalName + ".ActivateSignal";
        private const int ExitCheckpointTimeoutMilliseconds = 7000;

        public App()
        {
            // This must run before WPF creates a native window or a dialog.
            DpiAwareness.EnablePerMonitorV2();
            // AppTheme.Apply 必须等主题确定后再调用：WPF 会在样式密封时冻结
            // 主题 Brush，之后就不能再改颜色。启动流程见 OnStartup。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            SessionEnding += OnSessionEnding;
            Exit += OnApplicationExit;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        public ApplicationController Controller { get { return controller; } }

        internal bool IsExitRequested { get { return exitRequested; } }

        [STAThread]
        public static void Main(string[] args)
        {
            if (SessionCaptureProcess.TryRunWorker(args))
            {
                return;
            }

            App application = new App();
            application.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            instanceGuard = new SingleInstanceGuard(ProductIdentity.SingleInstanceMutexName);
            if (!instanceGuard.IsPrimaryInstance)
            {
                // Another keeper instance is already running (possibly hidden
                // in the tray).  Ask it to show and activate its window, like
                // a normal single-instance application, then exit.
                SignalPrimaryInstance();
                Shutdown();
                return;
            }

            try
            {
                bool signalCreated;
                activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceSignalName, out signalCreated);
            }
            catch
            {
                activateSignal = null;
            }
            StartActivationWatcher();

            try
            {
                // 数据跟着程序走（EXE 同级的 data 文件夹），所以要先确认这个位置能写：
                // 放在 C:\Program Files 这类需要管理员权限的目录时会写不进去，
                // 与其后面到处报错，不如现在就直说。
                DataLocationCheck.Ensure();
                StartupRegistrationService.MigrateLegacyRegistration();
                controller = new ApplicationController();
                // 主题颜色必须在 AppTheme.Apply 之前写入：样式一旦密封，WPF 会冻结
                // 这些 Brush，不能再就地改色。因此主题切换保存后要重启程序生效。
                ThemeService.Apply(ThemeModes.Parse(controller.Settings.Theme));
                AppTheme.Apply(this);
                mainWindow = new MainWindow(controller);
                MainWindow = mainWindow;
                mainWindow.LayoutSaved += OnLayoutSaved;
                TrayMenuActions trayActions = new TrayMenuActions();
                trayActions.ShowMain = delegate { ShowMainWindow(); };
                trayActions.CanSaveProfile = delegate
                {
                    return mainWindow != null && mainWindow.CanSaveActiveProfileFromTray();
                };
                trayActions.SaveProfile = delegate
                {
                    if (mainWindow != null)
                    {
                        mainWindow.SaveActiveProfileFromTray();
                    }
                };
                trayActions.CanSaveLayout = delegate
                {
                    return mainWindow != null && mainWindow.CanSaveActiveLayoutFromTray();
                };
                trayActions.SaveLayout = delegate
                {
                    if (mainWindow != null)
                    {
                        mainWindow.SaveActiveLayoutFromTray();
                    }
                };
                trayActions.Exit = delegate { ExitApplication(); };

                trayMenuHost = new TrayMenuHost(trayActions);
                trayIcon = new TrayIconService(
                    delegate { ShowMainWindow(); },
                    delegate
                    {
                        if (trayMenuHost != null)
                        {
                            trayMenuHost.ShowMenu();
                        }
                    });
                controller.RestoreCompleted += OnRestoreCompleted;
                controller.SaveCompleted += OnSaveCompleted;
                mainWindow.Show();
                // 主窗口真正显示后再补一次标题栏；部分 Windows 版本只在显示后接受 DWM 属性。
                WindowChromeTheme.Apply(mainWindow, ThemeService.EffectiveDark);
                mainWindow.Activate();
                // Periodic background re-capture is disabled on request: it
                // silently rewrote the saved profile and could drop items the
                // user had explicitly saved. The profile only changes on an
                // explicit "保存当前工作".
                backgroundSnapshotTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(30)
                };
                // 【2026-10-19 用户报 bug】选了「开机时自动启动」，也选了「启动程序后自动恢复工作方案」和
                // 「启动程序后自动启动自启动方案」，但开机后并没有恢复。
                // 根因就是原来这里：登录启动（带 --startup）被当成“绝不允许自动恢复”，
                // BeginStartup(false) 直接 return，两个开关在开机那一次等于没勾。
                // 现在**两种启动都按设置执行**：勾了就恢复/启动，没勾就什么都不做（开关仍然是唯一的闸门）。
                bool launchedAtLogin = IsStartupLaunch(e.Args);
                AppLog.Write("[启动] 本次是" + (launchedAtLogin ? "登录自启（--startup）" : "手动打开")
                    + "；AutoRestore=" + controller.Settings.AutoRestore
                    + "，ApplicationsAutoRestore=" + controller.Settings.ApplicationsAutoRestore);
                Dispatcher.BeginInvoke(new Action(delegate { controller.BeginStartup(); }));
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    AppText.F(ShellTextKeys.StartupFailedFormat, exception.Message),
                    AppText.T(ShellTextKeys.WindowTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown();
            }
        }
    
        public void ExitApplication()
        {
            if (exitRequested)
            {
                return;
            }
            exitRequested = true;
            StopBackgroundSnapshotTimer();
            // No automatic exit checkpoint: the saved profile is only changed
            // by an explicit "保存当前工作", so quitting the keeper never
            // re-scans or rewrites the session.
            Shutdown();
        }

        private async void OnBackgroundSnapshotTimerTick(object sender, EventArgs e)
        {
            if (controller == null)
            {
                return;
            }
            try
            {
                await controller.CaptureBackgroundSnapshotAsync();
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to run the periodic background snapshot from the dispatcher.", exception);
            }
        }

        private static bool IsStartupLaunch(string[] args)
        {
            if (args == null)
            {
                return false;
            }

            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--startup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // PresentationFramework can raise NotSupportedException from its
            // hover popup timer while a window is being hidden or destroyed.
            // This is a non-fatal tooltip failure; keep the keeper alive and
            // leave the user-visible state intact.
            NotSupportedException notSupported = e.Exception as NotSupportedException;
            string stack = e.Exception == null ? string.Empty : (e.Exception.StackTrace ?? string.Empty);
            if (notSupported != null && stack.IndexOf("PopupControlService", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                AppLog.Write("WPF tooltip popup was unavailable; the tooltip was skipped.", e.Exception);
                e.Handled = true;
            }
        }

        private void StopBackgroundSnapshotTimer()
        {
            if (backgroundSnapshotTimer != null)
            {
                backgroundSnapshotTimer.Stop();
                backgroundSnapshotTimer.Tick -= OnBackgroundSnapshotTimerTick;
                backgroundSnapshotTimer = null;
            }
        }

        private void ShowMainWindow()
        {
            if (mainWindow == null)
            {
                return;
            }
            // Forceful show + activation (restores from tray/minimized, raises
            // to the foreground with the foreground lock handled).
            mainWindow.ActivateFromLaunch();
        }

        private void OnRestoreCompleted(RestoreCompletion completion)
        {
            if (completion == null)
            {
                return;
            }
            // The open main window draws its own completion banner.  Only
            // notify through the tray when the window is hidden or minimized,
            // for example when a tray restore runs or the window is in the
            // tray at logon.
            bool windowShown = mainWindow != null
                && mainWindow.IsVisible
                && mainWindow.WindowState != WindowState.Minimized;
            if (windowShown || trayIcon == null)
            {
                return;
            }

            // 托盘只负责显示；句子按 Source 从 Text/Restore 的模块目录取。
            trayIcon.Notify(
                AppText.T(ShellTextKeys.TrayRestoreCompleteTitle),
                RestoreText.ForTray(completion));
        }

        private void OnSaveCompleted(SaveCompletion completion)
        {
            if (completion == null)
            {
                return;
            }
            // The open main window draws its own save banner and comes to the
            // front.  Only notify through the tray when it is hidden.
            bool windowShown = mainWindow != null
                && mainWindow.IsVisible
                && mainWindow.WindowState != WindowState.Minimized;
            if (windowShown || trayIcon == null)
            {
                return;
            }

            string message;
            if (completion.Cancelled)
            {
                message = AppText.T(ShellTextKeys.TraySaveCancelled);
            }
            else if (completion.Failed)
            {
                message = AppText.T(ShellTextKeys.TraySaveFailed);
            }
            else if (completion.Incomplete || completion.PolicyAffectedCount > 0)
            {
                // 用户要求（2026-10-09）：托盘气泡也照横幅的口气——先结论、再“怎么办”。
                // 【2026-10-19】与横幅共用 `SaveCompletionText`：终端/本地网址被设置跳过时如实说清，
                // 不再一律说“有些网页没读到”。
                message = SaveCompletionText.Build(completion, false);
            }
            else
            {
                message = AppText.F(ShellTextKeys.TraySavedCountFormat, completion.ItemCount);
            }
            trayIcon.Notify(AppText.T(ShellTextKeys.TraySavedTitle), message);
        }

        private void OnLayoutSaved(DesktopTopologyLayout layout)
        {
            if (layout == null || trayIcon == null)
            {
                return;
            }
            // 主窗口可见时页面自己会弹横幅；只有隐藏到托盘时才需要额外通知。
            bool windowShown = mainWindow != null
                && mainWindow.IsVisible
                && mainWindow.WindowState != WindowState.Minimized;
            if (windowShown)
            {
                return;
            }
            int count = layout.Icons == null ? 0 : layout.Icons.Count;
            trayIcon.Notify(
                AppText.T(ShellTextKeys.TrayLayoutSavedTitle),
                AppText.F(
                    ShellTextKeys.TrayLayoutSavedFormat,
                    layout.Name ?? AppText.T(ShellTextKeys.LayoutUntitled),
                    count));
        }

        private void StartActivationWatcher()
        {
            EventWaitHandle handle = activateSignal;
            if (handle == null)
            {
                return;
            }
            Thread watcher = new Thread(delegate()
            {
                while (true)
                {
                    bool signaled = false;
                    try
                    {
                        signaled = handle.WaitOne(500);
                    }
                    catch
                    {
                        break;
                    }
                    if (shutdownSignalled)
                    {
                        break;
                    }
                    if (signaled)
                    {
                        try
                        {
                            Dispatcher.BeginInvoke(new Action(ShowMainWindow));
                        }
                        catch
                        {
                        }
                    }
                }
            });
            watcher.IsBackground = true;
            watcher.Name = ProductIdentity.TechnicalName + ".ActivationSignal";
            watcher.Start();
        }

        private static void SignalPrimaryInstance()
        {
            // Always try to raise the existing window directly: the freshly
            // double-clicked instance is the foreground process, so it has the
            // user-input right to place ANY of its peers' windows on top. This
            // is the only reliable way to lift a window that is merely COVERED
            // (still visible, but behind a browser window), which the in-app
            // SetForegroundWindow path cannot do because that thread is not the
            // foreground process.
            DirectActivatePrimaryWindow();

            try
            {
                using (EventWaitHandle existing = EventWaitHandle.OpenExisting(SingleInstanceSignalName))
                {
                    existing.Set();
                }
            }
            catch
            {
                // No primary instance is listening yet; the fresh instance
                // that holds the guard will show its own window.
            }
        }

        private static void DirectActivatePrimaryWindow()
        {
            string exeName;
            try
            {
                exeName = Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location);
            }
            catch
            {
                exeName = ProductIdentity.TechnicalName;
            }
            if (string.IsNullOrWhiteSpace(exeName))
            {
                exeName = ProductIdentity.TechnicalName;
            }

            try
            {
                Process[] processes = Process.GetProcessesByName(exeName);
                for (int i = 0; i < processes.Length; i++)
                {
                    Process process = processes[i];
                    try
                    {
                        IntPtr handle = process.MainWindowHandle;
                        if (handle != IntPtr.Zero)
                        {
                            // If it is only minimized to the taskbar, restore it first.
                            NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);

                            // The freshly double-clicked instance is the foreground
                            // process, so it may place a peer's window on top.
                            NativeMethods.SetForegroundWindow(handle);
                            NativeMethods.BringWindowToTop(handle);

                            // Guaranteed raise: a brief topmost pulse lifts the
                            // window above everything even when the caller has no
                            // foreground permission (e.g. launched without a click).
                            // Restoring non-topmost keeps it normal for the user.
                            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                                                      NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove);
                            NativeMethods.SetWindowPos(handle, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                                                      NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove);

                            process.Dispose();
                            return;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // Enumeration failed; rely on the signal path.
            }
        }

        private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
        {
            // Let the window close as part of Windows logoff/shutdown instead
            // of interpreting the shell request as a user tray hide.  No
            // automatic shutdown re-capture: the saved profile is only ever
            // changed by an explicit "保存当前工作", so a reboot never drops
            // a folder the user already saved.
            exitRequested = true;
            StopBackgroundSnapshotTimer();
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            StopBackgroundSnapshotTimer();
            shutdownSignalled = true;
            if (activateSignal != null)
            {
                try { activateSignal.Set(); } catch { }
                activateSignal.Dispose();
                activateSignal = null;
            }
            if (trayMenuHost != null)
            {
                trayMenuHost.Dispose();
                trayMenuHost = null;
            }
            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
            if (controller != null)
            {
                controller.Dispose();
                controller = null;
            }
            if (instanceGuard != null)
            {
                instanceGuard.Dispose();
                instanceGuard = null;
            }
        }
    }
}
