using System;
using System.Windows;
using Microsoft.Win32;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 主题应用：解析跟随系统、原地更新共享语义 Brush、刷新资源字典和深色标题栏。
    /// Light/Dark/System 都即时生效，不再要求重启。
    /// </summary>
    internal static class ThemeService
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Personalize";
        private static bool installed;
        private static ThemeMode current = ThemeMode.Light;
        private static bool effectiveDark;

        internal static ThemeMode Current
        {
            get { return current; }
        }

        internal static bool EffectiveDark
        {
            get { return effectiveDark; }
        }

        internal static void Install()
        {
            if (installed)
            {
                return;
            }
            installed = true;
            WindowChromeTheme.Install();
            try
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
            catch (Exception)
            {
                // 测试/无桌面会话下没有系统事件；主题仍可手动切换。
            }
        }

        private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (current != ThemeMode.System || e == null || e.Category != UserPreferenceCategory.General)
            {
                return;
            }
            Application application = Application.Current;
            if (application == null || application.Dispatcher == null)
            {
                return;
            }
            try
            {
                application.Dispatcher.BeginInvoke(
                    new Action(delegate { Apply(ThemeMode.System); }),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception)
            {
            }
        }

        internal static void Apply(ThemeMode mode)
        {
            Install();
            current = mode;
            effectiveDark = mode == ThemeMode.Dark
                || (mode == ThemeMode.System && SystemUsesDark());

            // 【关键】令牌 Brush 是共享对象：这里原地改 Color，所有已经创建、
            // 隐藏或缓存了该 Brush 的控件都会跟着更新；资源字典只放克隆。
            ThemePalette.Apply(effectiveDark);
            Application application = Application.Current;
            if (application != null)
            {
                ThemeResources.Register(application.Resources);
            }
            WindowChromeTheme.ApplyToAllWindows(effectiveDark);
        }

        private static bool SystemUsesDark()
        {
            try
            {
                object value = Registry.GetValue(
                    "HKEY_CURRENT_USER\\" + PersonalizeKey,
                    "AppsUseLightTheme",
                    null);
                if (value is int)
                {
                    return (int)value == 0;
                }
                if (value is long)
                {
                    return (long)value == 0L;
                }
                if (value is string)
                {
                    int parsed;
                    if (int.TryParse((string)value, out parsed))
                    {
                        return parsed == 0;
                    }
                }
            }
            catch (Exception)
            {
                // 读不到系统主题就按浅色。
            }
            return false;
        }
    }
}
