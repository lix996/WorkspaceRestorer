using System;

namespace WorkspaceRestorer.Services
{
    public static class OsMutationGuard
    {
        public const string DisableEnvironmentVariable = "WORKSPACE_RESTORER_DISABLE_OS_CHANGES";

        public static bool IsDisabled
        {
            get
            {
                string value = Environment.GetEnvironmentVariable(DisableEnvironmentVariable);
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
