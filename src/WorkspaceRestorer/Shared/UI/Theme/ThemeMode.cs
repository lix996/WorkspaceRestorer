using System;

namespace WorkspaceRestorer.UI
{
    /// <summary>界面主题：浅色、深色、跟随 Windows 应用主题。</summary>
    internal enum ThemeMode
    {
        Light = 0,
        Dark = 1,
        System = 2
    }

    internal static class ThemeModes
    {
        internal static ThemeMode Parse(string value)
        {
            if (string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase))
            {
                return ThemeMode.Dark;
            }
            if (string.Equals(value, "system", StringComparison.OrdinalIgnoreCase))
            {
                return ThemeMode.System;
            }
            return ThemeMode.Light;
        }

        internal static string ToCode(ThemeMode mode)
        {
            if (mode == ThemeMode.Dark)
            {
                return "dark";
            }
            if (mode == ThemeMode.System)
            {
                return "system";
            }
            return "light";
        }
    }
}
