using System;
using System.Collections.Generic;
using WorkspaceRestorer.Browsers;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Applications.Browsers
{
    /// <summary>自启动方案界面里的一个浏览器窗口分组：一个父行 + 多个网页子行。</summary>
    internal sealed class ApplicationBrowserPlanGroup
    {
        internal string Key;
        internal string BrowserName;
        internal string BrowserExecutablePath;
        internal int WindowIndex;
        internal List<ApplicationPlanItem> Items = new List<ApplicationPlanItem>();

        internal string DisplayName
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(BrowserName) ? "浏览器" : BrowserName.Trim();
                return WindowIndex >= 0 ? (name + " (" + (WindowIndex + 1) + ")") : name;
            }
        }
    }

    /// <summary>
    /// 从平面自启动项目里识别浏览器网页分组；只读，不改变项目顺序。
    /// 与 ApplicationBrowserPlanAdapter 共用同一套 BrowserGroupKey/窗口序号。
    /// </summary>
    internal static class ApplicationBrowserPlanGrouping
    {
        internal static List<ApplicationBrowserPlanGroup> Collect(IList<ApplicationPlanItem> items)
        {
            List<ApplicationBrowserPlanGroup> groups = new List<ApplicationBrowserPlanGroup>();
            Dictionary<string, ApplicationBrowserPlanGroup> byKey =
                new Dictionary<string, ApplicationBrowserPlanGroup>(StringComparer.OrdinalIgnoreCase);
            if (items == null)
            {
                return groups;
            }
            for (int index = 0; index < items.Count; index++)
            {
                ApplicationPlanItem item = items[index];
                if (!ApplicationBrowserPlanAdapter.IsBrowserPage(item))
                {
                    continue;
                }
                string key = ApplicationBrowserPlanAdapter.GroupKey(item);
                ApplicationBrowserPlanGroup group;
                if (!byKey.TryGetValue(key, out group))
                {
                    group = new ApplicationBrowserPlanGroup
                    {
                        Key = key,
                        BrowserName = item.BrowserName,
                        BrowserExecutablePath = item.BrowserExecutablePath,
                        WindowIndex = item.BrowserWindowIndex
                    };
                    if (string.IsNullOrWhiteSpace(group.BrowserName))
                    {
                        string executable = string.IsNullOrWhiteSpace(item.BrowserExecutablePath)
                            ? item.BrowserGroupKey
                            : item.BrowserExecutablePath;
                        group.BrowserName = string.IsNullOrWhiteSpace(executable)
                            ? "浏览器"
                            : BrowserCatalog.GetDisplayName(executable, null);
                    }
                    byKey[key] = group;
                    groups.Add(group);
                }
                group.Items.Add(item);
            }
            return groups;
        }

        internal static string KeyOf(ApplicationPlanItem item)
        {
            return ApplicationBrowserPlanAdapter.GroupKey(item);
        }
    }
}
