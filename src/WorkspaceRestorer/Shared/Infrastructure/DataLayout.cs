using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 一套“固定数据目录名”。同一个程序可以按两套目录名构建：
    ///  · en-US：StartupRestore / StartupProfiles / Layouts …（默认，给 GitHub 的英文产物）
    ///  · zh-CN：启动与恢复 / 工作方案 / 布局 …（原来一直用的中文名，需要时才构建）
    ///
    /// 名字的唯一来源是 `yuanwenjian\variants\&lt;变体&gt;.json`，构建时生成
    /// `DataLayoutTable` 和 `ActiveDataLayout`（都在 build\app\generated 里，不进版本库）。
    /// 代码里**只认这里的键**，不要再写死目录名字符串；这样改一次两个产物都受益。
    ///
    /// 用户自己起的方案名 / 布局名不在这里，永远原样保留。
    /// </summary>
    internal sealed class DataLayout
    {
        public string Variant;

        public string Workflows;

        public string StartupRestore;

        public string DesktopLayout;

        public string Applications;

        public string StartupProfiles;

        public string Layouts;

        public string ApplicationPlans;

        public string RecycleBin;

        public string Settings;

        public string Logs;

        public string Backup;

        public string Temp;

        public string AutoRestore;

        public string StartupProfileBackups;

        public string Legacy;

        public string Unrecognized;
    }

    /// <summary>
    /// 所有已定义的目录名方案（构建时把所有 variants\*.json 都生成进来，
    /// 不只是当前构建的那一套），以及当前构建正在用的那一套。
    ///
    /// 为什么要带上“另一套”：换版本时数据目录要能从另一套名字迁过来，
    /// 迁移程序必须知道对方叫什么，否则只能靠写死的中文表，换一套就失效。
    /// </summary>
    internal static class DataLayouts
    {
        public static string ActiveVariant
        {
            get { return ActiveDataLayout.Variant; }
        }

        public static DataLayout Active
        {
            get { return DataLayoutTable.Find(ActiveDataLayout.Variant); }
        }

        public static DataLayout[] All
        {
            get { return DataLayoutTable.All; }
        }

        /// <summary>某一层里所有“同一个目录在不同方案下的名字”，去重、去掉空值。</summary>
        public static List<string> NamesAt(Func<DataLayout, string> selector, bool includeActive)
        {
            List<string> names = new List<string>();
            if (selector == null)
            {
                return names;
            }

            DataLayout[] all = All;
            for (int index = 0; index < all.Length; index++)
            {
                string name = selector(all[index]);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }
                bool already = false;
                for (int existing = 0; existing < names.Count; existing++)
                {
                    if (string.Equals(names[existing], name, StringComparison.OrdinalIgnoreCase))
                    {
                        already = true;
                        break;
                    }
                }
                if (!already)
                {
                    names.Add(name);
                }
            }

            if (!includeActive && selector(Active) != null)
            {
                string active = selector(Active);
                for (int index = names.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(names[index], active, StringComparison.OrdinalIgnoreCase))
                    {
                        names.RemoveAt(index);
                    }
                }
            }
            return names;
        }
    }
}
