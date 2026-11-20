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
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshAll();
            // 【2026-10-20】空闲时预热说明框，避免第一次点问号时闪/卡（见 InfoDialog.Prewarm）。
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate { InfoDialog.Prewarm(this); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch (Exception prewarmError)
            {
                AppLog.Write("[窗口] 预热调用失败", prewarmError);
            }
            // 【2026-10-17 补】第一次运行（或者用户手动删掉了「设置」文件夹）时，
            // settings.json 以前要等到用户改过某个设置才会出现 —— 于是「设置」文件夹里
            // 的“说明.txt”写着有三个文件、实际只看到一个。这里在启动时补写一份默认设置。
            // （Round90 自检：删掉整个「设置」文件夹后重启，settings.json / 说明.txt 都应回来。）
            try
            {
                if (!System.IO.File.Exists(WorkspaceRestorer.Infrastructure.AppPaths.SettingsFile))
                {
                    controller.SaveSettings();
                }
            }
            catch
            {
            }
            if (restoreWindowMaximized)
            {
                restoreWindowMaximized = false;
                WindowState = WindowState.Maximized;
            }
        }

        private MainWindowState LoadWindowState()
        {
            try
            {
                MainWindowState state = windowStateStore == null ? null : windowStateStore.Load();
                if (state != null
                    && state.Width >= MinWidth
                    && state.Height >= MinHeight
                    && state.Width <= 8000
                    && state.Height <= 8000)
                {
                    return state;
                }
            }
            catch
            {
            }
            return null;
        }

        private void RestartWindowStateSave()
        {
            if (windowStateSaveTimer == null || !IsLoaded || WindowState == WindowState.Minimized)
            {
                return;
            }
            windowStateSaveTimer.Stop();
            windowStateSaveTimer.Start();
        }

        private void SaveWindowState()
        {
            try
            {
                if (windowStateSaveTimer != null)
                {
                    windowStateSaveTimer.Stop();
                }
                if (!IsLoaded || WindowState == WindowState.Minimized)
                {
                    return;
                }

                MainWindowState state = new MainWindowState();
                bool maximized = WindowState == WindowState.Maximized;
                double width;
                double height;
                double left;
                double top;
                if (maximized)
                {
                    Rect bounds = RestoreBounds;
                    width = bounds.Width;
                    height = bounds.Height;
                    left = bounds.X;
                    top = bounds.Y;
                }
                else
                {
                    width = ActualWidth > 0 ? ActualWidth : Width;
                    height = ActualHeight > 0 ? ActualHeight : Height;
                    left = Left;
                    top = Top;
                }
                if (width < MinWidth) { width = MinWidth; }
                if (height < MinHeight) { height = MinHeight; }
                state.Width = width;
                state.Height = height;
                state.Left = left;
                state.Top = top;
                state.HasPosition = true;
                state.Maximized = maximized;
                windowStateStore.Save(state);
            }
            catch
            {
            }
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            // 标题栏句柄这时才稳定；Loaded 时再补一次，兼容部分 Windows 版本。
            WindowChromeTheme.Apply(this, ThemeService.EffectiveDark);
            HwndSource source = PresentationSource.FromVisual(this) as HwndSource;
            if (source != null)
            {
                source.AddHook(WindowProcedure);
            }
        }

        private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // WM_DISPLAYCHANGE can arrive several times while a dock is
            // settling. Debounce it so Explorer has time to rebuild the
            // desktop view before applying the matching topology snapshot.
            if (message == 0x007E && controller.Settings.LayoutAutoRestore)
            {
                displayChangeTimer.Stop();
                displayChangeTimer.Start();
            }
            return IntPtr.Zero;
        }


        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!allowClose && Application.Current is App && !((App)Application.Current).IsExitRequested)
            {
                e.Cancel = true;
                // Closing to the tray never re-scans or rewrites the saved
                // profile (the "关闭到托盘时不自动捕获").  Only an explicit
                // "保存当前工作" changes the profile.
                Hide();
            }
            else
            {
                SaveWindowState();
                displayChangeTimer.Stop();
                controller.PropertyChanged -= OnControllerPropertyChanged;
                controller.RestoreCompleted -= OnRestoreCompleted;
                controller.SaveCompleted -= OnSaveCompleted;
                controller.SaveStarted -= OnSaveStarted;
                controller.RestoreStarted -= OnRestoreStarted;
                AppText.LanguageChanged -= OnLanguageChanged;
            }
        }

        /// <summary>
        /// 语言切换完成后刷新那些不能靠单个属性绑定更新的骨架。
        /// 语言下拉框已经改成离开选择事件后再切换，所以这里不会再出现
        /// “在 ComboBox 自己的 SelectionChanged 里把 ComboBox 拆掉”的重入。
        /// </summary>
        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            if (!Dispatcher.CheckAccess())
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(delegate { OnLanguageChanged(sender, e); }));
                }
                catch
                {
                }
                return;
            }

            ApplyLanguageChanged();
        }

        private void ApplyLanguageChanged()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            Title = AppText.T(ShellTextKeys.WindowTitle);
            // 旧语言的一次性状态/横幅不留在屏幕上，避免“英文界面里夹一句中文”。
            if (restoreNotice != null)
            {
                restoreNotice.Visibility = Visibility.Collapsed;
            }
            if (statusText != null)
            {
                statusText.Text = string.Empty;
            }

            // 三个工作区先记脏；只有当前可见的那个立即刷新，
            // 其余等用户切过去时由 PrepareWorkspaceForDisplay 补刷。
            startupWorkspaceLanguageDirty = true;
            applicationsWorkspaceLanguageDirty = true;
            layoutWorkspaceLanguageDirty = true;

            if (activeSurfaceId == "settings")
            {
                RefreshSettingsPanelNavigationOnly();
                // 命名框已搬进设置：没自定义过的默认名要跟着语言一起变。
                RefreshStartupNamingRow();
                RefreshApplicationsNamingRow();
                RefreshLayoutNamingRow();
            }
            else if (activeSurfaceId == "applications")
            {
                applicationsWorkspaceLanguageDirty = false;
                RefreshApplicationPlanList();
            }
            else if (activeSurfaceId == "layout")
            {
                layoutWorkspaceLanguageDirty = false;
                RefreshDesktopLayoutCards();
            }
            else if (activeSurfaceId == "recycle")
            {
                RefreshRecycleBinPage();
            }
            else
            {
                startupWorkspaceLanguageDirty = false;
                RefreshProfileList();
            }
        }

        /// <summary>
        /// 切到一个工作区前，如果它在语言切换时被标记过，先按当前语言补刷一次。
        /// 这样语言切换本身只处理当前页，隐藏页的刷新成本推迟到真正打开时。
        /// </summary>
        private void PrepareWorkspaceForDisplay(string surfaceId)
        {
            activeSurfaceId = surfaceId;
            if (surfaceId == "startup" && startupWorkspaceLanguageDirty)
            {
                startupWorkspaceLanguageDirty = false;
                RefreshProfileList();
            }
            else if (surfaceId == "applications" && applicationsWorkspaceLanguageDirty)
            {
                applicationsWorkspaceLanguageDirty = false;
                RefreshApplicationPlanList();
            }
            else if (surfaceId == "layout" && layoutWorkspaceLanguageDirty)
            {
                layoutWorkspaceLanguageDirty = false;
                RefreshDesktopLayoutCards();
            }
        }

        public void AllowClose()
        {
            allowClose = true;
        }

        /// <summary>
        /// Shows and forcefully activates the window.  Used when a second
        /// instance of the EXE is launched while this one is already running
        /// (possibly hidden in the tray) so the window jumps to the front like
        /// a normal single-instance application.
        /// </summary>
        public void ActivateFromLaunch()
        {
            try
            {
                BringToFront(this);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (IsLoaded && !IsActive)
                    {
                        Topmost = true;
                        Topmost = false;
                        Activate();
                        Focus();
                    }
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            catch
            {
            }
        }

        private void OnControllerPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return;
            }
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    {
                        return;
                    }
                    if (e.PropertyName == "LastSnapshot" || e.PropertyName == "HasSnapshot")
                    {
                        RefreshSessionList();
                        RefreshProfileList();
                    }
                    if (e.PropertyName == "StatusText" || e.PropertyName == "CountdownSeconds" || e.PropertyName == "ProgressValue" || e.PropertyName == "ProgressMaximum")
                    {
                        RefreshProgress();
                    }
                    if (e.PropertyName == "LoadPromptVisible" || e.PropertyName == "LoadPromptText")
                    {
                        RefreshLoadPrompt();
                    }
                    if (e.PropertyName == "Profiles" || e.PropertyName == "StartupProfiles" || e.PropertyName == "ActiveProfile")
                    {
                        RefreshSessionList();
                        RefreshProfileList();
                    }
                    if (e.PropertyName == "StartupStatus" || e.PropertyName == "Settings")
                    {
                        RefreshSettings();
                    }
                }));
            }
            catch
            {
                // A property notification can race with WPF dispatcher
                // teardown.  It must not turn a normal close into a crash.
            }
        }

    }
}
