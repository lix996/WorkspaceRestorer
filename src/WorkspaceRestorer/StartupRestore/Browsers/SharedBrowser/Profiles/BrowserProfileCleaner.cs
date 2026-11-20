using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 清理 Chromium 默认配置里的崩溃恢复标记。
    ///
    /// Edge 启动增强/上次强制结束会把 profile.exit_type 留成 Crashed；
    /// 这时即使先执行 --no-startup-window，第一条 --new-window 仍可能把旧
    /// 会话（实测 RunningHub 9 页）拉进方案窗口，导致 8 页窗口变成 15 页。
    /// 在没有可见浏览器窗口、并已结束后台进程后，把 exit_type 改回 Normal，
    /// 让 Edge 本次启动不要恢复旧会话。
    /// </summary>
    internal static class BrowserProfileCleaner
    {
        internal static bool TrySuppressCrashRestore(string browserPath)
        {
            if (string.IsNullOrWhiteSpace(browserPath))
            {
                return false;
            }
            BrowserCatalog.Definition definition = BrowserCatalog.Find(browserPath, null);
            if (definition == null || !definition.Chromium)
            {
                return false;
            }

            string preferencesPath = GetPreferencesPath(definition.Id);
            if (string.IsNullOrWhiteSpace(preferencesPath) || !File.Exists(preferencesPath))
            {
                return false;
            }

            try
            {
                string text = File.ReadAllText(preferencesPath, Encoding.UTF8);
                string updated = Regex.Replace(
                    text,
                    "\"exit_type\"\\s*:\\s*\"[^\"]*\"",
                    "\"exit_type\":\"Normal\"",
                    RegexOptions.CultureInvariant);
                updated = Regex.Replace(
                    updated,
                    "\"exited_cleanly\"\\s*:\\s*(true|false)",
                    "\"exited_cleanly\":true",
                    RegexOptions.CultureInvariant);
                if (string.Equals(text, updated, StringComparison.Ordinal))
                {
                    return true;
                }

                string directory = Path.GetDirectoryName(preferencesPath);
                string temporary = Path.Combine(
                    directory,
                    "Preferences.csk-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(temporary, updated, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(preferencesPath))
                    {
                        File.Replace(temporary, preferencesPath, null);
                    }
                    else
                    {
                        File.Move(temporary, preferencesPath);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary))
                        {
                            File.Delete(temporary);
                        }
                    }
                    catch
                    {
                    }
                }
                AppLog.Write("[窗口恢复] 已清理浏览器崩溃恢复标记，避免旧会话抢方案窗口："
                    + preferencesPath);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 清理浏览器崩溃恢复标记失败，继续原流程", exception);
                return false;
            }
        }

        private static string GetPreferencesPath(string browserId)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.Equals(browserId, "edge", StringComparison.OrdinalIgnoreCase))
            {
                return MicrosoftEdgeProfileLocator.GetDefaultPreferencesPath();
            }
            string userData = null;
            if (string.Equals(browserId, "chrome", StringComparison.OrdinalIgnoreCase))
            {
                userData = Path.Combine(local, "Google", "Chrome", "User Data");
            }
            else if (string.Equals(browserId, "brave", StringComparison.OrdinalIgnoreCase))
            {
                userData = Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data");
            }
            else if (string.Equals(browserId, "vivaldi", StringComparison.OrdinalIgnoreCase))
            {
                userData = Path.Combine(local, "Vivaldi", "User Data");
            }
            else if (string.Equals(browserId, "chromium", StringComparison.OrdinalIgnoreCase))
            {
                userData = Path.Combine(local, "Chromium", "User Data");
            }
            else if (string.Equals(browserId, "opera", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(roaming, "Opera Software", "Opera Stable", "Preferences");
            }
            if (string.IsNullOrWhiteSpace(userData))
            {
                return null;
            }
            return Path.Combine(userData, "Default", "Preferences");
        }
    }
}
