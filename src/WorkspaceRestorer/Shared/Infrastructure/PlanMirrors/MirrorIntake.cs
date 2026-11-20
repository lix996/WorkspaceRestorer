using System;
using System.Collections.Generic;
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
    ///  · 能读出来、Id 也不在清单里 → 让用户确认后**收编**进清单（只新增，不改已有）；
    ///  · 读不出来（没有 方案.json / JSON 坏了 / 没有 Id）→ **搬**到 `备份\无法识别\&lt;工作区&gt;\`，
    ///    永不删除；用户还可以去那里手动翻出来。
    ///
    /// 收编是“只读 + 追加”：不动清单里已有的方案，也不改已经在用的文件夹名。
    /// </summary>
    internal sealed class MirrorIntakeFolder<T> where T : class, IPlanFolderNode
    {
        public string Directory;
        public string Name;
        public T Node;
    }

    internal static class MirrorIntake
    {
        /// <summary>用户选了「否」之后记住“这个文件夹我就不要导”的清单文件（放在数据根）。</summary>
        private const string IgnoreFileName = ".mirror-intake.txt";

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
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        ignored.Add(line.Trim());
                    }
                }
            }
            catch
            {
            }
            return ignored;
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
            string relative = directory ?? string.Empty;
            try
            {
                string root = AppPaths.DataRoot;
                if (!string.IsNullOrEmpty(root)
                    && relative.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relative = relative.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar);
                }
                return kind + "|" + relative + "|" + Directory.GetLastWriteTimeUtc(directory).Ticks;
            }
            catch
            {
                return kind + "|" + relative;
            }
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
                if (ignored != null && ignored.Contains(signature))
                {
                    skipped.Add(signature);
                    continue;
                }
                result.Add(folder);
            }
            return result;
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
