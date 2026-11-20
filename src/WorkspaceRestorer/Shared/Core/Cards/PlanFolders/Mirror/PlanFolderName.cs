using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WorkspaceRestorer.Core.Cards.PlanFolders.Mirror
{
    /// <summary>
    /// 【公共区域 · 方案镜像】只算文件夹名的纯规则，不碰磁盘、不弹框、不读数据。
    ///
    /// 工作方案和自启动方案共用同一套命名：
    ///  · 空名字用各自的默认名（工作方案 / 自启动方案）；
    ///  · Windows 不允许的字符换成下划线；
    ///  · 去掉首尾空白和末尾点号，最多 80 个字；
    ///  · 同名按 Windows 新建文件夹的习惯顺延成“名字（2）”。
    ///
    /// 这里既不是“磁盘设施”，也不是某个工作区的界面，所以放在 Core\Cards\PlanFolders\Mirror。
    /// </summary>
    public static class PlanFolderName
    {
        /// <summary>
        /// 按节点顺序算每一份方案的文件夹名。
        /// null 节点保留一个空名字，调用方用下标对齐，避免错位写到别人的目录。
        /// </summary>
        public static List<string> BuildFolderNames<T>(
            IList<T> nodes,
            Func<T, string> nameOf,
            string fallbackName) where T : class
        {
            List<string> names = new List<string>();
            if (nodes == null)
            {
                return names;
            }

            Dictionary<string, int> usedNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < nodes.Count; index++)
            {
                T node = nodes[index];
                if (node == null)
                {
                    names.Add(string.Empty);
                    continue;
                }

                string name = nameOf == null ? null : nameOf(node);
                names.Add(MakeUniqueFolderName(SafeFolderName(name, fallbackName), usedNames));
            }
            return names;
        }

        /// <summary>把方案名变成 Windows 能建出来的文件夹名。</summary>
        public static string SafeFolderName(string name, string fallbackName)
        {
            string fallback = string.IsNullOrWhiteSpace(fallbackName) ? "方案" : fallbackName.Trim();
            string text = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char character = text[index];
                bool bad = false;
                for (int invalidIndex = 0; invalidIndex < invalid.Length; invalidIndex++)
                {
                    if (invalid[invalidIndex] == character)
                    {
                        bad = true;
                        break;
                    }
                }
                builder.Append(bad ? '_' : character);
            }

            string safe = builder.ToString().Trim().TrimEnd('.');
            if (safe.Length == 0)
            {
                safe = fallback;
            }
            if (safe.Length > 80)
            {
                safe = safe.Substring(0, 80);
            }
            return safe;
        }

        /// <summary>同名顺延成“名字（2）”“名字（3）”，大小写不敏感。</summary>
        public static string MakeUniqueFolderName(string baseName, Dictionary<string, int> usedNames)
        {
            if (!usedNames.ContainsKey(baseName))
            {
                usedNames[baseName] = 1;
                return baseName;
            }

            int next = usedNames[baseName] + 1;
            string candidate = baseName + "（" + next + "）";
            while (usedNames.ContainsKey(candidate))
            {
                next++;
                candidate = baseName + "（" + next + "）";
            }
            usedNames[baseName] = next;
            usedNames[candidate] = 1;
            return candidate;
        }
    }
}
