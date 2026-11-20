using System;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public interface IStartupRegistrationService
    {
        StartupRegistrationInfo GetStatus();

        StartupRegistrationInfo SetEnabled(bool enabled);
    }

    public sealed class StartupRegistrationService : IStartupRegistrationService
    {
        public const string RunSubKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string RunValueName = ProductIdentity.StartupRunValueName;
        public const string LegacyRunValueName = LegacyIdentity.StartupRunValueName;
        public const string StartupArgument = "--startup";

        private readonly string executablePath;

        public StartupRegistrationService()
            : this(null)
        {
        }

        public StartupRegistrationService(string executablePath)
        {
            this.executablePath = string.IsNullOrWhiteSpace(executablePath)
                ? ResolveCurrentExecutablePath()
                : executablePath.Trim();
        }

        public bool IsEnabled
        {
            get { return GetStatus().IsEnabled; }
        }

        public StartupRegistrationInfo GetStatus()
        {
            StartupRegistrationInfo result = NewInfo();
            if (ExternalEffectsDisabled() || OsMutationGuard.IsDisabled)
            {
                result.WasSuppressed = true;
                result.Error = null;
                return result;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunSubKey, false))
                {
                    if (key == null)
                    {
                        return result;
                    }

                    object value = key.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        value = key.GetValue(LegacyRunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    }
                    string command = value == null ? null : Convert.ToString(value);
                    result.IsEnabled = !string.IsNullOrWhiteSpace(command);
                    if (!string.IsNullOrWhiteSpace(command))
                    {
                        result.Command = command;
                    }
                }
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
            }

            return result;
        }

        public StartupRegistrationInfo Enable()
        {
            return SetEnabled(true);
        }

        public StartupRegistrationInfo Disable()
        {
            return SetEnabled(false);
        }

        public StartupRegistrationInfo SetEnabled(bool enabled)
        {
            StartupRegistrationInfo result = NewInfo();
            if (ExternalEffectsDisabled() || OsMutationGuard.IsDisabled)
            {
                result.WasSuppressed = true;
                return result;
            }

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                result.Error = "The current executable path is unavailable.";
                return result;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunSubKey))
                {
                    if (key == null)
                    {
                        result.Error = "Windows did not provide the current-user startup registry key.";
                        return result;
                    }

                    if (enabled)
                    {
                        string command = BuildCommand(executablePath);
                        key.SetValue(RunValueName, command, RegistryValueKind.String);
                        object written = key.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (written == null)
                        {
                            result.Error = "Windows did not keep the startup registration value.";
                            return result;
                        }
                        // 新值确认写入后才删旧值；中途失败时旧开机项仍然有效。
                        key.DeleteValue(LegacyRunValueName, false);
                        result.IsEnabled = true;
                        result.Command = command;
                    }
                    else
                    {
                        key.DeleteValue(RunValueName, false);
                        key.DeleteValue(LegacyRunValueName, false);
                        result.IsEnabled = false;
                        result.Command = null;
                    }
                }
            }
            catch (Exception exception)
            {
                result.Error = exception.Message;
            }

            return result;
        }

        // Kept as an overload for callers that resolve services reflectively and
        // already have the final executable path available (for example, the WPF
        // controller when running from a single-file deployment).
        public StartupRegistrationInfo SetEnabled(bool enabled, string executablePathOverride)
        {
            if (string.IsNullOrWhiteSpace(executablePathOverride)
                || string.Equals(executablePathOverride.Trim(), executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return SetEnabled(enabled);
            }

            return new StartupRegistrationService(executablePathOverride).SetEnabled(enabled);
        }

        private StartupRegistrationInfo NewInfo()
        {
            return new StartupRegistrationInfo
            {
                IsEnabled = false,
                WasSuppressed = false,
                Command = null,
                Error = null
            };
        }

        private static string BuildCommand(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\" " + StartupArgument;
        }

        /// <summary>
        /// 把旧版的开机启动值一次性迁移到新身份。
        /// 先写新值并确认成功，再删旧值；任何一步失败都保留旧值，避免用户开机项凭空消失。
        /// </summary>
        internal static void MigrateLegacyRegistration()
        {
            if (ExternalEffectsDisabled() || OsMutationGuard.IsDisabled)
            {
                return;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunSubKey, true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    object legacyValue = key.GetValue(
                        LegacyRunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (legacyValue == null)
                    {
                        return;
                    }

                    object currentValue = key.GetValue(
                        RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string currentCommand = currentValue == null ? null : Convert.ToString(currentValue);
                    if (string.IsNullOrWhiteSpace(currentCommand))
                    {
                        string executable = ResolveCurrentExecutablePath();
                        if (string.IsNullOrWhiteSpace(executable))
                        {
                            return;
                        }

                        string command = BuildCommand(executable);
                        key.SetValue(RunValueName, command, RegistryValueKind.String);
                        object written = key.GetValue(
                            RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (written == null)
                        {
                            return;
                        }
                    }

                    key.DeleteValue(LegacyRunValueName, false);
                    AppLog.Write("已把旧版开机启动项迁移到 " + RunValueName + "。");
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("旧版开机启动项迁移失败；旧值会保留，程序继续运行。", exception);
            }
        }

        private static string ResolveCurrentExecutablePath()
        {
            try
            {
                string path = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch
            {
            }

            try
            {
                Assembly entry = Assembly.GetEntryAssembly();
                return entry == null ? null : entry.Location;
            }
            catch
            {
                return null;
            }
        }

        private static bool ExternalEffectsDisabled()
        {
            return OsMutationGuard.IsDisabled
                || IsTruthy("WORKSPACE_RESTORER_TEST_MODE")
                || IsTruthy("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS");
        }

        private static bool IsTruthy(string variableName)
        {
            string value = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }
}
