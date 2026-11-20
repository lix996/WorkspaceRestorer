using System;
using System.Collections.Generic;
using System.IO;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 浏览器差异的唯一清单。主流程只通过这里判断浏览器，
    /// 不再在 Capture/Restore 里散落 chrome/msedge/firefox 名字。
    /// 第一批只收拢现有行为；UC/夸克等后续新增在这里加定义。
    /// </summary>
    internal static class BrowserCatalog
    {
        internal sealed class Definition
        {
            internal string Id;
            internal string DisplayName;
            internal string[] ProcessNames;
            internal bool Chromium;
            internal string NewWindowSwitch;
            internal string[] TitleSuffixes;
            internal bool ChromiumTitle;
        }

        private static readonly Definition[] Definitions = new Definition[]
        {
            MicrosoftEdgeBrowserDefinition.Create(),
            new Definition
            {
                Id = "chrome", DisplayName = "Google Chrome", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "chrome" },
                TitleSuffixes = new[] { " - Google Chrome" }, ChromiumTitle = true
            },
            new Definition
            {
                Id = "firefox", DisplayName = "Mozilla Firefox", Chromium = false,
                NewWindowSwitch = "-new-window",
                ProcessNames = new[] { "firefox" },
                TitleSuffixes = new[] { " - Mozilla Firefox" }, ChromiumTitle = false
            },
            new Definition
            {
                Id = "brave", DisplayName = "Brave", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "brave" },
                TitleSuffixes = new[] { " - Brave" }, ChromiumTitle = true
            },
            new Definition
            {
                Id = "opera", DisplayName = "Opera", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "opera" },
                TitleSuffixes = new[] { " - Opera" }, ChromiumTitle = true
            },
            new Definition
            {
                Id = "vivaldi", DisplayName = "Vivaldi", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "vivaldi" },
                TitleSuffixes = new[] { " - Vivaldi" }, ChromiumTitle = true
            },
            new Definition
            {
                Id = "chromium", DisplayName = "Chromium", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "chromium" },
                TitleSuffixes = new[] { " - Chromium" }, ChromiumTitle = true
            },
            new Definition
            {
                Id = "arc", DisplayName = "Arc", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "arc" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "ie", DisplayName = "Internet Explorer", Chromium = false,
                NewWindowSwitch = "-new",
                ProcessNames = new[] { "iexplore" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "sogou", DisplayName = "搜狗浏览器", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "sogouexplorer" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "qq", DisplayName = "QQ浏览器", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "qqbrowser" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "360", DisplayName = "360浏览器", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "360chrome" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "liebao", DisplayName = "猎豹浏览器", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "liebao" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "maxthon", DisplayName = "傲游浏览器", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "maxthon" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "yandex", DisplayName = "Yandex", Chromium = true,
                NewWindowSwitch = "--new-window",
                ProcessNames = new[] { "yandex" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            },
            new Definition
            {
                Id = "waterfox", DisplayName = "Waterfox", Chromium = false,
                NewWindowSwitch = "-new-window",
                ProcessNames = new[] { "waterfox" },
                TitleSuffixes = new string[0], ChromiumTitle = false
            }
        };

        internal static Definition Find(string executablePath, string processName)
        {
            string name = NormalizeProcessName(executablePath, processName);
            if (name.Length == 0)
            {
                return null;
            }
            for (int index = 0; index < Definitions.Length; index++)
            {
                Definition definition = Definitions[index];
                for (int processIndex = 0; processIndex < definition.ProcessNames.Length; processIndex++)
                {
                    if (string.Equals(definition.ProcessNames[processIndex], name, StringComparison.OrdinalIgnoreCase))
                    {
                        return definition;
                    }
                }
            }
            return null;
        }

        internal static bool IsSupportedBrowser(string executablePath, string processName)
        {
            return Find(executablePath, processName) != null;
        }

        internal static string GetDisplayName(string executablePath, string processName)
        {
            Definition definition = Find(executablePath, processName);
            if (definition != null)
            {
                return definition.DisplayName;
            }
            string name = NormalizeProcessName(executablePath, processName);
            return name.Length == 0 ? "浏览器" : name;
        }

        internal static bool IsChromiumPath(string executablePath)
        {
            string name = NormalizeProcessName(executablePath, null);
            Definition definition = Find(null, name);
            return definition != null && definition.Chromium;
        }

        internal static string GetNewWindowSwitch(string executablePath)
        {
            string name = NormalizeProcessName(executablePath, null);
            Definition definition = Find(null, name);
            return definition == null ? "--new-window" : definition.NewWindowSwitch;
        }

        internal static bool LooksLikeBrowserWindowTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }
            if (title.IndexOf("和另外", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            for (int index = 0; index < Definitions.Length; index++)
            {
                string[] suffixes = Definitions[index].TitleSuffixes;
                for (int suffixIndex = 0; suffixIndex < suffixes.Length; suffixIndex++)
                {
                    if (title.IndexOf(suffixes[suffixIndex], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        internal static bool IsChromiumBrowserTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }
            for (int index = 0; index < Definitions.Length; index++)
            {
                Definition definition = Definitions[index];
                if (!definition.ChromiumTitle)
                {
                    continue;
                }
                for (int suffixIndex = 0; suffixIndex < definition.TitleSuffixes.Length; suffixIndex++)
                {
                    if (title.IndexOf(definition.TitleSuffixes[suffixIndex], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static string NormalizeProcessName(string executablePath, string processName)
        {
            string name = processName;
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(executablePath))
            {
                try
                {
                    name = Path.GetFileNameWithoutExtension(executablePath);
                }
                catch
                {
                    name = null;
                }
            }
            return name == null ? string.Empty : name.Trim().ToLowerInvariant();
        }
    }
}
