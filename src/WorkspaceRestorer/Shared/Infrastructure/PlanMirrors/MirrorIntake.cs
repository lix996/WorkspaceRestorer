using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Infrastructure.PlanMirrors
{
    /// <summary>
    /// 镜像目录里“程序还不认识的文件夹”的收编与隔离。
    ///
    /// 用户的实际用法（2026-11-20 提的）：把一整个方案文件夹从别的电脑 /
    /// 另一套目录结构复制进 `workflows\...\`，重启程序就应该能看到它。
    /// 所以启动时先扫一遍镜像根：
    ///  · 能读出来、Id 也不在清单里 → 让用户三选一：导入 / 暂不导入 / 移到“无法识别”；
    ///  · 读不出来（没有 方案.json / JSON 坏了 / 没有 Id）→ **搬**到 `备份\无法识别\&lt;工作区&gt;\`，
    ///    永不删除；用户还可以去那里手动翻出来。
    ///
    /// 收编是“只读 + 追加”：不动清单里已有的方案，也不改已经在用的文件夹名。
    /// 用户选「暂不导入」只记到本次 Windows 登录会话；重启后再问，文件夹内容变了也再问。
    /// </summary>
    internal sealed class MirrorIntakeFolder<T> where T : class, IPlanFolderNode
    {
        public string Directory;
        public string Name;
        public T Node;
    }

    internal static class MirrorIntake
    {
        /// <summary>用户说过“不要导入”的清单文件（放在数据根）。</summary>
        private const string IgnoreFileName = ".mirror-intake.txt";

        /// <summary>永久忽略：删除方案后文件夹没删掉等场景，内容不变就不再问。</summary>
        private const string PermanentPrefix = "P|";

        /// <summary>暂不导入：只在写入时的这次 Windows 登录会话里生效，重启后重新问。</summary>
        private const string DeferredPrefix = "D|";

        private const int MaxDepth = 32;

        /// <summary>
        /// 扫镜像根（递归）：
        /// <paramref name="adoptable"/> 是能收编的方案（父在前、子在后）;
        /// <paramref name="unrecognized"/> 是认不出来的文件夹（只登记最外层那一个）。
        /// </summary>
        public static void Scan<T>(
            IList<T> known,
            string root,
            Func<string, T> readNode,
            List<MirrorIntakeFolder<T>> adoptable,
            List<string> unrecognized)
            where T : class, IPlanFolderNode
        {
            if (adoptable == null || unrecognized == null || string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            string[] directories;
            try
            {
                if (!Directory.Exists(root))
                {
                    return;
                }
                directories = Directory.GetDirectories(root);
            }
            catch
            {
                return;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < directories.Length; index++)
            {
                ScanDirectory(directories[index], known, readNode, adoptable, unrecognized, 0);
            }
        }

        /// <summary>把确认收编的方案追加进清单（父级按文件夹层级还原，不会造出不存在的父方案）。</summary>
        public static void Adopt<T>(List<MirrorIntakeFolder<T>> folders, IList<T> catalog)
            where T : class, IPlanFolderNode
        {
            if (folders == null || folders.Count == 0 || catalog == null)
            {
                return;
            }

            Dictionary<string, string> idByDirectory =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < folders.Count; index++)
            {
                MirrorIntakeFolder<T> folder = folders[index];
                if (folder == null || folder.Node == null || string.IsNullOrWhiteSpace(folder.Node.Id))
                {
                    continue;
                }
                idByDirectory[NormalizePath(folder.Directory)] = folder.Node.Id;
            }

            for (int index = 0; index < folders.Count; index++)
            {
                MirrorIntakeFolder<T> folder = folders[index];
                if (folder == null || folder.Node == null)
                {
                    continue;
                }
                string parentId = null;
                string parentDirectory = Path.GetDirectoryName(folder.Directory);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    string found;
                    if (idByDirectory.TryGetValue(NormalizePath(parentDirectory), out found))
                    {
                        parentId = found;
                    }
                }
                // 父文件夹不是方案（普通分类文件夹）→ 放根级，不硬造父方案。
                folder.Node.ParentId = parentId;
                catalog.Add(folder.Node);
            }

            PlanFolderTree.Normalize(catalog);
        }

        /// <summary>
        /// 把认不出来的文件夹搬到 `备份\无法识别\&lt;工作区&gt;\`。
        /// 只移动、不删除；同名时顺延成“名字（2）”。
        /// </summary>
        public static bool MoveToUnrecognized(string directory, string kind, out string destination)
        {
            destination = null;
            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return false;
                }

                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                string targetRoot = AppPaths.UnrecognizedKindDirectory(kind);
                Directory.CreateDirectory(targetRoot);
                string candidate = Path.Combine(targetRoot, name);
                int suffix = 2;
                while (Directory.Exists(candidate) || File.Exists(candidate))
                {
                    candidate = Path.Combine(targetRoot, name + "（" + suffix + "）");
                    suffix++;
                }

                Directory.Move(directory, candidate);
                destination = candidate;
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("把认不出来的文件夹搬到“无法识别”失败，保持原位：" + directory, exception);
                return false;
            }
        }

        /// <summary>“用户说过不要导入”的签名集合。文件夹内容变了（时间变了）就重新问。</summary>
        public static HashSet<string> LoadIgnored()
        {
            HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(AppPaths.DataRoot, IgnoreFileName);
                if (!File.Exists(path))
                {
                    return ignored;
                }
                string[] lines = File.ReadAllLines(path);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (!string.IsNullOrWhiteSpace(line)
                        && (line.StartsWith(PermanentPrefix, StringComparison.OrdinalIgnoreCase)
                            || line.StartsWith(DeferredPrefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        ignored.Add(line.Trim());
                    }
                    // 旧版没有 P|/D| 前缀的记录直接作废：升级后让用户用新的三选一重新决定一次。
                }
            }
            catch
            {
            }
            return ignored;
        }

        /// <summary>
        /// 删除方案后镜像文件夹没删掉（被占用等）时，把它的当前签名记成“永久不要导入”，
        /// 避免下次启动把它又当成新方案问一遍。内容变了仍会重新问。
        /// </summary>
        public static void RememberIgnored(string kind, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }
            try
            {
                HashSet<string> ignored = LoadIgnored();
                string record = PermanentPrefix + Signature(kind, directory);
                if (ignored.Add(record))
                {
                    SaveIgnored(ignored);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("记录“不要导入”签名失败（不影响程序使用）。", exception);
            }
        }

        /// <summary>
        /// 用户选择「暂不导入」：把当前签名记到本次登录会话。
        /// 同一次开机内不再问；重启（登录会话变化）或文件夹内容变化后会重新问。
        /// 不直接写盘，由调用方对一批候选统一 SaveIgnored。
        /// </summary>
        public static void Defer(string kind, string directory, HashSet<string> ignored)
        {
            if (ignored == null || string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            string signature = Signature(kind, directory);
            RemoveDeferredEntries(ignored, signature);
            ignored.Add(DeferredPrefix + signature + "|" + CurrentSessionKey());
        }

        /// <summary>
        /// 导入或移走之后清掉这个文件夹的忽略/暂不导入记录。
        /// 不直接写盘，由调用方统一 SaveIgnored。
        /// </summary>
        public static void Forget(string kind, string directory, HashSet<string> ignored)
        {
            if (ignored == null || string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            // 导入/移走会改变目录名或最后写入时间，不能只用当前 Signature 精确匹配；
            // 按“工作区 + 相对路径”清掉这个文件夹的所有旧记录（不同时间戳、不同会话）。
            string prefix = SignaturePrefix(kind, directory);
            List<string> remove = new List<string>();
            foreach (string record in ignored)
            {
                string body = StripRecordPrefix(record);
                if (!string.IsNullOrEmpty(body)
                    && body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    remove.Add(record);
                }
            }
            for (int index = 0; index < remove.Count; index++)
            {
                ignored.Remove(remove[index]);
            }
        }

        private static string StripRecordPrefix(string record)
        {
            if (string.IsNullOrEmpty(record))
            {
                return record;
            }
            if (record.StartsWith(PermanentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return record.Substring(PermanentPrefix.Length);
            }
            if (record.StartsWith(DeferredPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return record.Substring(DeferredPrefix.Length);
            }
            return record;
        }

        /// <summary>
        /// 把父文件夹和子文件夹同时出现在候选里时，只保留最外层父文件夹。
        /// 返回顺序保持原候选顺序；被父文件夹带走的子文件夹不再单独操作。
        /// </summary>
        public static List<MirrorIntakeFolder<T>> SelectTopMostFolders<T>(
            IList<MirrorIntakeFolder<T>> folders)
            where T : class, IPlanFolderNode
        {
            List<MirrorIntakeFolder<T>> result = new List<MirrorIntakeFolder<T>>();
            if (folders == null || folders.Count == 0)
            {
                return result;
            }

            // 先按路径长度从短到长判断，保证父文件夹先进入 accepted；
            // 最后再按原顺序输出，日志和界面顺序不跳。
            List<MirrorIntakeFolder<T>> ordered = new List<MirrorIntakeFolder<T>>();
            for (int index = 0; index < folders.Count; index++)
            {
                if (folders[index] != null)
                {
                    ordered.Add(folders[index]);
                }
            }
            ordered.Sort(delegate(MirrorIntakeFolder<T> left, MirrorIntakeFolder<T> right)
            {
                int lengthCompare = NormalizePath(left.Directory).Length.CompareTo(
                    NormalizePath(right.Directory).Length);
                return lengthCompare != 0
                    ? lengthCompare
                    : string.Compare(left.Directory, right.Directory, StringComparison.OrdinalIgnoreCase);
            });

            HashSet<string> accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < ordered.Count; index++)
            {
                string path = NormalizePath(ordered[index].Directory);
                bool covered = false;
                foreach (string parent in accepted)
                {
                    if (IsAncestorPath(parent, path))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
                {
                    accepted.Add(path);
                }
            }

            for (int index = 0; index < folders.Count; index++)
            {
                if (folders[index] != null
                    && accepted.Contains(NormalizePath(folders[index].Directory)))
                {
                    result.Add(folders[index]);
                }
            }
            return result;
        }

        private static void RemoveDeferredEntries(HashSet<string> ignored, string signature)
        {
            string prefix = DeferredPrefix + signature + "|";
            List<string> remove = new List<string>();
            foreach (string record in ignored)
            {
                if (!string.IsNullOrEmpty(record)
                    && record.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    remove.Add(record);
                }
            }
            for (int index = 0; index < remove.Count; index++)
            {
                ignored.Remove(remove[index]);
            }
        }

        private static bool IsAncestorPath(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child))
            {
                return false;
            }
            string parentPath = parent.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return child.Length > parentPath.Length
                && child.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
        }

        private static string CurrentSessionKey()
        {
            string session = AppPaths.LogonSessionId;
            if (!string.IsNullOrWhiteSpace(session))
            {
                return session.Trim();
            }
            // 极少数拿不到 LUID 的环境：退回本次开机时间，仍能跨程序重启保持稳定。
            return "boot:" + AppPaths.BootTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        public static void SaveIgnored(HashSet<string> signatures)
        {
            try
            {
                string path = Path.Combine(AppPaths.DataRoot, IgnoreFileName);
                if (signatures == null || signatures.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return;
                }
                List<string> lines = new List<string>(signatures);
                lines.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(path, lines.ToArray(), new System.Text.UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                AppLog.Write("保存“不导入”清单失败（不影响程序使用）。", exception);
            }
        }

        /// <summary>文件夹签名：工作区 + 相对路径 + 最后写入时间。任一处变了就重新问一次。</summary>
        public static string Signature(string kind, string directory)
        {
            string relative = RelativePath(directory);
            try
            {
                return kind + "|" + relative + "|" + Directory.GetLastWriteTimeUtc(directory).Ticks;
            }
            catch
            {
                return kind + "|" + relative;
            }
        }

        private static string SignaturePrefix(string kind, string directory)
        {
            return kind + "|" + RelativePath(directory) + "|";
        }

        private static string RelativePath(string directory)
        {
            string relative = directory ?? string.Empty;
            try
            {
                string root = AppPaths.DataRoot;
                if (!string.IsNullOrEmpty(root)
                    && relative.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                }
            }
            catch
            {
            }
            return relative;
        }

        /// <summary>按签名过滤掉用户已经明确说过“不要”的文件夹。</summary>
        public static List<MirrorIntakeFolder<T>> ExcludeIgnored<T>(
            List<MirrorIntakeFolder<T>> folders,
            string kind,
            HashSet<string> ignored,
            out List<string> skipped)
            where T : class, IPlanFolderNode
        {
            skipped = new List<string>();
            List<MirrorIntakeFolder<T>> result = new List<MirrorIntakeFolder<T>>();
            if (folders == null)
            {
                return result;
            }
            for (int index = 0; index < folders.Count; index++)
            {
                MirrorIntakeFolder<T> folder = folders[index];
                if (folder == null)
                {
                    continue;
                }
                string signature = Signature(kind, folder.Directory);
                if (IsIgnored(ignored, kind, folder.Directory))
                {
                    skipped.Add(signature);
                    continue;
                }
                result.Add(folder);
            }
            return result;
        }

        private static bool IsIgnored(HashSet<string> ignored, string kind, string directory)
        {
            if (ignored == null || ignored.Count == 0)
            {
                return false;
            }
            string signature = Signature(kind, directory);
            if (ignored.Contains(PermanentPrefix + signature))
            {
                return true;
            }
            return ignored.Contains(DeferredPrefix + signature + "|" + CurrentSessionKey());
        }

        private static bool ScanDirectory<T>(
            string directory,
            IList<T> known,
            Func<string, T> readNode,
            List<MirrorIntakeFolder<T>> adoptable,
            List<string> unrecognized,
            int depth)
            where T : class, IPlanFolderNode
        {
            if (depth > MaxDepth)
            {
                // 太深了：当成“看不懂但别动”，不搬也不收。
                return true;
            }

            T node = readNode == null ? null : readNode(directory);
            if (node != null)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    // 有 方案.json 但没有 Id：算认不出来（原样搬走，不删）。
                    AddUnrecognizedOnce(unrecognized, directory);
                    return false;
                }
                if (PlanFolderTree.FindById(known, node.Id) != null || ContainsAdopted(adoptable, node.Id))
                {
                    // 已经登记过的方案：它的镜像由程序自己维护，不往里收。
                    return true;
                }

                MirrorIntakeFolder<T> folder = new MirrorIntakeFolder<T>();
                folder.Directory = directory;
                folder.Name = Path.GetFileName(directory);
                folder.Node = node;
                adoptable.Add(folder);

                string[] children = GetDirectories(directory);
                for (int index = 0; index < children.Length; index++)
                {
                    ScanDirectory(children[index], known, readNode, adoptable, unrecognized, depth + 1);
                }
                return true;
            }

            // 自己不是方案：先看里面有没有方案（有就只是个分类文件夹，保留）。
            List<string> childUnrecognized = new List<string>();
            bool anyAdoptable = false;
            string[] childDirectories = GetDirectories(directory);
            for (int index = 0; index < childDirectories.Length; index++)
            {
                if (ScanDirectory(childDirectories[index], known, readNode, adoptable, childUnrecognized, depth + 1))
                {
                    anyAdoptable = true;
                }
            }
            if (anyAdoptable)
            {
                // 这一层是用户的分类文件夹：里面的散文件夹各自登记，本层不动。
                for (int index = 0; index < childUnrecognized.Count; index++)
                {
                    AddUnrecognizedOnce(unrecognized, childUnrecognized[index]);
                }
                return true;
            }

            // 自己不是方案、里面也没有方案 → 整个文件夹都认不出来（搬走，不删）。
            AddUnrecognizedOnce(unrecognized, directory);
            return false;
        }

        private static bool ContainsAdopted<T>(List<MirrorIntakeFolder<T>> adoptable, string id)
            where T : class, IPlanFolderNode
        {
            for (int index = 0; index < adoptable.Count; index++)
            {
                T node = adoptable[index] == null ? null : adoptable[index].Node;
                if (node != null && string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddUnrecognizedOnce(List<string> unrecognized, string directory)
        {
            for (int index = 0; index < unrecognized.Count; index++)
            {
                if (string.Equals(unrecognized[index], directory, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            unrecognized.Add(directory);
        }

        private static string[] GetDirectories(string directory)
        {
            try
            {
                string[] directories = Directory.GetDirectories(directory);
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                return directories;
            }
            catch
            {
                return new string[0];
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                return path ?? string.Empty;
            }
        }
    }
}
