using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 托盘图标本身：NotifyIcon、图标、双击打开主窗口、右键菜单入口、气泡兜底和释放。
    /// 菜单内容不在这里，WPF 菜单由 TrayMenuHost 负责。
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        private readonly NotifyIcon notifyIcon;
        private readonly Icon ownedIcon;
        private readonly TrayNotificationService notification;
        private bool disposed;

        public TrayIconService(Action show, Action showMenu)
        {
            notifyIcon = new NotifyIcon();
            // 【2026-10-19 用户要求】托盘图标也用交付文件 `data\图标.ico`；读不到就退回系统默认图标。
            Icon trayImage = null;
            try
            {
                string iconPath = WorkspaceRestorer.Infrastructure.AppPaths.AppIconFile;
                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    trayImage = new Icon(iconPath);
                }
            }
            catch (Exception)
            {
                trayImage = null;
            }
            ownedIcon = trayImage;
            notifyIcon.Icon = trayImage ?? SystemIcons.Application;
            notifyIcon.Text = AppText.T(ShellTextKeys.WindowTitle);
            AppText.LanguageChanged += OnLanguageChanged;
            notifyIcon.Visible = true;
            notifyIcon.DoubleClick += delegate { Invoke(show); };
            notifyIcon.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right)
                {
                    Invoke(showMenu);
                }
            };

            notification = new TrayNotificationService(ShowBalloon);
            notification.PrepareStartMenuShortcut();
        }

        /// <summary>系统 Toast 优先，失败时由通知服务回退到托盘气泡。</summary>
        public void Notify(string title, string message)
        {
            if (disposed || notification == null)
            {
                return;
            }
            notification.Notify(title, message);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            AppText.LanguageChanged -= OnLanguageChanged;
            if (notification != null)
            {
                notification.Dispose();
            }
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            if (ownedIcon != null)
            {
                try { ownedIcon.Dispose(); } catch (Exception) { }
            }
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            if (!disposed)
            {
                notifyIcon.Text = AppText.T(ShellTextKeys.WindowTitle);
            }
        }

        private void ShowBalloon(string title, string message)
        {
            if (disposed || !notifyIcon.Visible)
            {
                return;
            }
            try
            {
                notifyIcon.BalloonTipTitle = title ?? string.Empty;
                notifyIcon.BalloonTipText = message ?? string.Empty;
                notifyIcon.ShowBalloonTip(4000, title ?? string.Empty, message ?? string.Empty, ToolTipIcon.None);
            }
            catch
            {
                // Notification support is optional (for example in a remote
                // desktop session); a missing balloon must not stop the app.
            }
        }

        private static void Invoke(Action action)
        {
            if (action == null)
            {
                return;
            }
            try
            {
                action();
            }
            catch
            {
                // Tray callbacks are deliberately best effort; the window
                // remains usable even if a command cannot be completed.
            }
        }
    }
}
