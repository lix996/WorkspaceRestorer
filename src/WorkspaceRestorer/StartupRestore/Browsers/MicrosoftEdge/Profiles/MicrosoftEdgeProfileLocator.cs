using System;
using System.IO;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>Microsoft Edge 默认用户配置的路径；只属于 Edge。</summary>
    internal static class MicrosoftEdgeProfileLocator
    {
        internal static string GetDefaultPreferencesPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Preferences");
        }
    }
}
