using System;

namespace WorkspaceRestorer.Desktop
{
    public static class DesktopEnvironmentPolicy
    {
        public const string DisableOsChangesVariable = "WORKSPACE_RESTORER_DISABLE_OS_CHANGES";

        public static bool OsChangesDisabled
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(DisableOsChangesVariable);
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
}
