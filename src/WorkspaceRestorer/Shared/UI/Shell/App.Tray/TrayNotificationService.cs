using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 托盘通知：优先走 Windows Toast，失败再回退到 NotifyIcon 气泡。
    /// 只做标题 + 正文的普通通知，不做 DSH 的按钮/回复/审批。
    /// </summary>
    internal sealed class TrayNotificationService : IDisposable
    {
        private const string AppUserModelId = ProductIdentity.AppUserModelId;
        private const string ShortcutFileName = "启动恢复器.lnk";
        private const string LegacyShortcutFileName = LegacyIdentity.StartMenuShortcutFileName;
        private const int ToastTimeoutMilliseconds = 8000;

        private const string ToastScript = @"param([string]$PayloadPath, [string]$AppId)
$ErrorActionPreference = 'Stop'
$data = Get-Content -LiteralPath $PayloadPath -Raw -Encoding UTF8 | ConvertFrom-Json
$title = [string]$data.title
$message = [string]$data.message
$safeTitle = [Security.SecurityElement]::Escape($title)
$safeMessage = [Security.SecurityElement]::Escape($message)
$xmlText = ""<?xml version='1.0' encoding='utf-8'?><toast><visual><binding template='ToastGeneric'><text>$safeTitle</text>""
if ($message) { $xmlText += ""<text>$safeMessage</text>"" }
$xmlText += '</binding></visual></toast>'
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml($xmlText)
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($AppId).Show($toast)
exit 0
";

        private readonly Action<string, string> balloonFallback;
        private readonly SynchronizationContext uiContext;
        private readonly object gate = new object();
        private bool toastIdentityReady;
        private bool toastIdentityFailed;
        private bool toastRuntimeFailed;
        private bool disposed;
        private Process currentToastProcess;

        internal TrayNotificationService(Action<string, string> balloonFallback)
        {
            this.balloonFallback = balloonFallback;
            this.uiContext = SynchronizationContext.Current;
        }

        /// <summary>
        /// 启动时先把开始菜单快捷方式迁到新名字；不等到第一条通知才改。
        /// 失败只记日志，不影响主窗口和托盘。
        /// </summary>
        internal void PrepareStartMenuShortcut()
        {
            if (disposed)
            {
                return;
            }
            try
            {
                EnsureToastIdentity();
            }
            catch (Exception exception)
            {
                AppLog.Write("[托盘通知] 启动时准备开始菜单快捷方式失败。", exception);
            }
        }

        internal void Notify(string title, string message)
        {
            if (disposed)
            {
                return;
            }
            string safeTitle = string.IsNullOrWhiteSpace(title)
                ? AppText.T(ShellTextKeys.WindowTitle)
                : title.Trim();
            string safeMessage = message ?? string.Empty;
            if (!TryStartToast(safeTitle, safeMessage))
            {
                PostFallback(safeTitle, safeMessage);
            }
        }

        public void Dispose()
        {
            disposed = true;
            Process process = null;
            lock (gate)
            {
                process = currentToastProcess;
                currentToastProcess = null;
            }
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
        }

        private bool TryStartToast(string title, string message)
        {
            if (toastRuntimeFailed)
            {
                return false;
            }
            if (!EnsureToastIdentity())
            {
                return false;
            }

            string scriptPath = null;
            string payloadPath = null;
            try
            {
                string powershellPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(powershellPath))
                {
                    throw new InvalidOperationException("没有找到 powershell.exe");
                }

                string tempBase = Path.Combine(
                    Path.GetTempPath(),
                    ProductIdentity.ToastTempPrefix + Guid.NewGuid().ToString("N"));
                scriptPath = tempBase + ".ps1";
                payloadPath = tempBase + ".json";

                File.WriteAllText(scriptPath, ToastScript, new UTF8Encoding(false));
                File.WriteAllText(payloadPath, BuildPayloadJson(title, message), new UTF8Encoding(false));

                Process process = new Process();
                process.StartInfo.FileName = powershellPath;
                process.StartInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "
                    + Quote(scriptPath) + " -PayloadPath " + Quote(payloadPath)
                    + " -AppId " + Quote(ProductIdentity.AppUserModelId);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                if (!process.Start())
                {
                    process.Dispose();
                    TryDelete(scriptPath);
                    TryDelete(payloadPath);
                    return false;
                }

                lock (gate)
                {
                    currentToastProcess = process;
                }

                string scriptForCleanup = scriptPath;
                string payloadForCleanup = payloadPath;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    WaitForToast(process, scriptForCleanup, payloadForCleanup, title, message);
                });
                return true;
            }
            catch (Exception exception)
            {
                toastRuntimeFailed = true;
                AppLog.Write("[托盘通知] 启动 Toast 失败，后续回退气泡。", exception);
                TryDelete(scriptPath);
                TryDelete(payloadPath);
                return false;
            }
        }

        private void WaitForToast(Process process, string scriptPath, string payloadPath, string title, string message)
        {
            bool exited = false;
            int exitCode = -1;
            try
            {
                exited = process.WaitForExit(ToastTimeoutMilliseconds);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                }
                else
                {
                    exitCode = process.ExitCode;
                }
            }
            catch
            {
            }
            finally
            {
                try { process.Dispose(); } catch { }
                lock (gate)
                {
                    if (object.ReferenceEquals(currentToastProcess, process))
                    {
                        currentToastProcess = null;
                    }
                }
                TryDelete(scriptPath);
                TryDelete(payloadPath);
            }

            if (exited && exitCode == 0)
            {
                AppLog.Write("[托盘通知] 已发送 Windows Toast。");
                return;
            }

            toastRuntimeFailed = true;
            AppLog.Write("[托盘通知] Windows Toast 不可用或超时，回退托盘气泡。退出代码："
                + exitCode.ToString());
            PostFallback(title, message);
        }

        private void PostFallback(string title, string message)
        {
            if (disposed || balloonFallback == null)
            {
                return;
            }
            if (uiContext != null)
            {
                uiContext.Post(delegate
                {
                    SafeBalloon(title, message);
                }, null);
                return;
            }

            Application application = Application.Current;
            if (application != null && application.Dispatcher != null)
            {
                application.Dispatcher.BeginInvoke(new Action(delegate
                {
                    SafeBalloon(title, message);
                }));
                return;
            }
            SafeBalloon(title, message);
        }

        private void SafeBalloon(string title, string message)
        {
            if (disposed)
            {
                return;
            }
            try
            {
                balloonFallback(title, message);
            }
            catch (Exception exception)
            {
                AppLog.Write("[托盘通知] 气泡回退也失败。", exception);
            }
        }

        private bool EnsureToastIdentity()
        {
            if (toastIdentityReady)
            {
                return true;
            }
            if (toastIdentityFailed)
            {
                return false;
            }

            lock (gate)
            {
                if (toastIdentityReady)
                {
                    return true;
                }
                if (toastIdentityFailed)
                {
                    return false;
                }
                try
                {
                    string exePath = Process.GetCurrentProcess().MainModule.FileName;
                    string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string shortcutPath = Path.Combine(
                        startMenu,
                        @"Microsoft\Windows\Start Menu\Programs\" + ShortcutFileName);
                    string shortcutDirectory = Path.GetDirectoryName(shortcutPath);
                    if (!Directory.Exists(shortcutDirectory))
                    {
                        Directory.CreateDirectory(shortcutDirectory);
                    }
                    CreateOrUpdateShortcut(shortcutPath, exePath);
                    SetShortcutAppUserModelId(shortcutPath, AppUserModelId);
                    RemoveLegacyStartMenuShortcut(startMenu);
                    toastIdentityReady = true;
                    return true;
                }
                catch (Exception exception)
                {
                    toastIdentityFailed = true;
                    AppLog.Write("[托盘通知] Toast 身份注册失败，后续使用托盘气泡。", exception);
                    return false;
                }
            }
        }

        private static void RemoveLegacyStartMenuShortcut(string startMenu)
        {
            string legacyPath = Path.Combine(
                startMenu,
                @"Microsoft\Windows\Start Menu\Programs\" + LegacyShortcutFileName);
            try
            {
                if (File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
            catch (Exception exception)
            {
                // 旧快捷方式删不掉不能挡住新快捷方式和 Toast 身份注册。
                AppLog.Write("[托盘通知] 旧开始菜单快捷方式清理失败。", exception);
            }
        }

        private static void CreateOrUpdateShortcut(string shortcutPath, string exePath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell 不可用");
            }

            object shellObject = Activator.CreateInstance(shellType);
            if (shellObject == null)
            {
                throw new InvalidOperationException("无法创建 WScript.Shell");
            }

            object shortcutObject = null;
            try
            {
                dynamic shell = shellObject;
                shortcutObject = shell.CreateShortcut(shortcutPath);
                dynamic shortcut = shortcutObject;
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.IconLocation = exePath + ",0";
                shortcut.Description = AppText.T(ShellTextKeys.WindowTitle);
                shortcut.Save();
            }
            finally
            {
                if (shortcutObject != null)
                {
                    try { Marshal.FinalReleaseComObject(shortcutObject); } catch { }
                }
                try { Marshal.FinalReleaseComObject(shellObject); } catch { }
            }
        }

        private static void SetShortcutAppUserModelId(string shortcutPath, string appId)
        {
            Guid iid = typeof(IPropertyStore).GUID;
            IPropertyStore store = null;
            int hr = SHGetPropertyStoreFromParsingName(shortcutPath, IntPtr.Zero, 2, ref iid, out store);
            if (hr != 0 || store == null)
            {
                throw new COMException("SHGetPropertyStoreFromParsingName failed: 0x" + hr.ToString("X8"));
            }

            try
            {
                PropertyKey key = new PropertyKey();
                key.fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
                key.pid = 5;
                PropVariant value = new PropVariant();
                value.vt = 31;
                value.pwszVal = Marshal.StringToCoTaskMemUni(appId);
                try
                {
                    int setHr = store.SetValue(ref key, ref value);
                    if (setHr != 0)
                    {
                        throw new COMException("IPropertyStore.SetValue failed: 0x" + setHr.ToString("X8"));
                    }
                    int commitHr = store.Commit();
                    if (commitHr != 0)
                    {
                        throw new COMException("IPropertyStore.Commit failed: 0x" + commitHr.ToString("X8"));
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(value.pwszVal);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        private static string BuildPayloadJson(string title, string message)
        {
            return "{\"title\":\"" + EscapeJson(title) + "\",\"message\":\"" + EscapeJson(message) + "\"}";
        }

        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder(value.Length + 8);
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty) + "\"";
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        private interface IPropertyStore
        {
            int GetCount(out uint propertyCount);

            int GetAt(uint propertyIndex, out PropertyKey key);

            int GetValue(ref PropertyKey key, out PropVariant value);

            int SetValue(ref PropertyKey key, ref PropVariant value);

            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)]
            public ushort vt;

            [FieldOffset(8)]
            public IntPtr pwszVal;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            IntPtr propertyStore,
            uint flags,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    }
}
