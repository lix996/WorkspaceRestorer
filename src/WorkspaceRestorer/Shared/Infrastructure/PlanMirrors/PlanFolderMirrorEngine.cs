using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.Core.Cards.PlanFolders.Mirror;

namespace WorkspaceRestorer.Infrastructure.PlanMirrors
{
    /// <summary>
    /// 【公共区域 · 方案镜像】把一份“扁平清单 + ParentId”的树写成
    /// “一份方案一个文件夹”的通用引擎。
    ///
    /// 这里既不是“数据和算法”，也不是 WPF 界面：
    ///  · 它要建目录、写文件、删目录、记日志，是文件系统设施；
    ///  · 所以放 Shared\Infrastructure\PlanMirrors；
    ///  · 纯命名和相对路径在 Shared\Core\Cards\PlanFolders\Mirror；
    ///  · 两个工作区各自的中文清单文字仍留在自己的公开类里。
    ///
    /// 用户明确要求「工作方案」和「自启动应用」不要过度组合：
    /// 这只是把重复的磁盘流程收成一份，两个公开类、两套数据、两套清单文字仍然分开。
    /// </summary>
    internal sealed class PlanFolderMirrorEngine<T> where T : class, IPlanFolderNode
    {
        private readonly string subject;
        private readonly string fallbackName;
        private readonly string listFileName;
        private readonly string dataFileName;
        private readonly Func<T, string> nameOf;
        private readonly Func<T, string> buildListText;

        public PlanFolderMirrorEngine(
            string subject,
            string fallbackName,
            string listFileName,
            string dataFileName,
            Func<T, string> nameOf,
            Func<T, string> buildListText)
        {
            this.subject = subject;
            this.fallbackName = fallbackName;
            this.listFileName = listFileName;
            this.dataFileName = dataFileName;
            this.nameOf = nameOf;
            this.buildListText = buildListText;
        }

        /// <summary>整棵树同步：没有嵌套时一层一个目录，有嵌套时按真正的父子目录写。</summary>
        public void Sync(IList<T> nodes, string root)
        {
            if (HasNested(nodes))
            {
                SyncNested(nodes, root);
                return;
            }

            try
            {
                AppPaths.EnsureDataDirectories();
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }
                Directory.CreateDirectory(root);

                List<string> folderNames = BuildFolderNames(nodes);
                HashSet<string> wanted = new HashSet<string>(folderNames, StringComparer.OrdinalIgnoreCase);
                if (nodes != null)
                {
                    for (int index = 0; index < nodes.Count; index++)
                    {
                        T node = nodes[index];
                        if (node == null || index >= folderNames.Count)
                        {
                            continue;
                        }
                        WriteNode(root, folderNames[index], node);
                    }
                }
                RemoveStaleFolders(root, wanted, nodes);
            }
            catch (Exception exception)
            {
                AppLog.Write(subject + "镜像文件夹写入失败（不影响程序使用）。", exception);
            }
        }

        /// <summary>一份方案在镜像树里的完整目录（按它当前的 ParentId 和同级顺序算）。</summary>
        public string FolderPathFor(T node, IList<T> allNodes, string root)
        {
            List<string> segments = PlanMirrorPath.BuildRelativeSegments(
                node,
                allNodes,
                nameOf,
                fallbackName);
            return PlanMirrorPath.BuildPath(root, segments);
        }

        /// <summary>只重写一份方案的镜像目录（移动后调用，不重写整棵树）。</summary>
        public void SyncMoved(T node, IList<T> allNodes, string root)
        {
            if (node == null || allNodes == null)
            {
                return;
            }
            try
            {
                string folder = FolderPathFor(node, allNodes, root);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    WriteAt(folder, node);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write(
                    subject + "移动后更新单份文件夹失败（不影响数据）：" + NameOrFallback(node),
                    exception);
            }
        }

        /// <summary>写到指定目录（目录移动时可能为了避开同名旧目录而用了“名字（2）”）。</summary>
        public void WriteAtDirectory(T node, string directory)
        {
            if (node == null || string.IsNullOrWhiteSpace(directory))
            {
                return;
            }
            try
            {
                WriteAt(directory, node);
            }
            catch (Exception exception)
            {
                AppLog.Write(
                    subject + "更新单份文件夹失败（不影响数据）：" + NameOrFallback(node),
                    exception);
            }
        }

        /// <summary>按当前同级顺序算每份方案的文件夹名；重名顺延成“名字（2）”。</summary>
        public List<string> BuildFolderNames(IList<T> nodes)
        {
            return PlanFolderName.BuildFolderNames(nodes, nameOf, fallbackName);
        }

        /// <summary>
        /// 用户手动删掉了某份方案的文件夹时，把它从清单里去掉，返回被去掉的方案本体。
        ///
        /// 安全网：只有父文件夹存在、而且里面至少还有一个方案文件夹时才按文件夹删方案。
        /// 这样刚装好程序（一个文件夹都没有）、或者整个文件夹被清空时，
        /// 不会把清单里的方案一口气全删掉。
        ///
        /// 有嵌套时不能按“一层文件夹名”判断：子方案目录不在根目录里，
        /// 会被误判成“文件夹被删”而整批移除。嵌套的手动删除检测后续单独做，
        /// 现在宁可只重建镜像，也不能误删用户方案。
        /// </summary>
        public List<T> TakeMissingNodes(IList<T> nodes, string root)
        {
            List<T> removed = new List<T>();
            if (HasNested(nodes))
            {
                return removed;
            }

            try
            {
                if (nodes == null || nodes.Count == 0 || !Directory.Exists(root))
                {
                    return removed;
                }

                HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string[] directories = Directory.GetDirectories(root);
                for (int index = 0; index < directories.Length; index++)
                {
                    existing.Add(Path.GetFileName(directories[index]));
                }
                if (existing.Count == 0)
                {
                    // 一个都没剩下：宁可当成“还没建过”，也不清空用户的方案。
                    return removed;
                }

                List<string> folderNames = BuildFolderNames(nodes);
                for (int index = nodes.Count - 1; index >= 0; index--)
                {
                    T node = nodes[index];
                    if (node == null || index >= folderNames.Count)
                    {
                        continue;
                    }
                    string folderName = folderNames[index];
                    if (folderName.Length == 0 || existing.Contains(folderName))
                    {
                        continue;
                    }
                    removed.Add(node);
                    nodes.RemoveAt(index);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write(subject + "按文件夹清理时出错（本次不做任何删除）。", exception);
                removed.Clear();
            }
            return removed;
        }

        private void WriteNode(string root, string folderName, T node)
        {
            WriteAt(Path.Combine(root, folderName), node);
        }

        private void WriteAt(string directory, T node)
        {
            Directory.CreateDirectory(directory);
            WriteListFile(directory, node);
            WriteDataFile(directory, node);
        }

        private bool HasNested(IList<T> nodes)
        {
            if (nodes == null)
            {
                return false;
            }
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null && !string.IsNullOrWhiteSpace(nodes[index].ParentId))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 有嵌套时按真正的目录树写：
        /// 大方案 A\小方案 A1\小小方案 A1a\…，每一层仍然有 启动清单.txt + 方案.json。
        /// </summary>
        private void SyncNested(IList<T> nodes, string root)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                if (string.IsNullOrWhiteSpace(root))
                {
                    return;
                }
                Directory.CreateDirectory(root);

                List<T> ordered = new List<T>(nodes);
                PlanFolderTree.Normalize(ordered);
                List<T> roots = PlanFolderTree.Roots(ordered);
                List<string> rootNames = BuildFolderNames(roots);
                HashSet<string> wantedRoots = new HashSet<string>(rootNames, StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < roots.Count; index++)
                {
                    if (roots[index] == null || index >= rootNames.Count)
                    {
                        continue;
                    }
                    WriteTree(
                        Path.Combine(root, rootNames[index]),
                        roots[index],
                        ordered,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }
                RemoveStaleFolders(root, wantedRoots, ordered);
            }
            catch (Exception exception)
            {
                AppLog.Write(subject + "嵌套文件夹写入失败（不影响程序使用）。", exception);
            }
        }

        private void WriteTree(
            string directory,
            T node,
            IList<T> allNodes,
            HashSet<string> visited)
        {
            if (node == null || string.IsNullOrWhiteSpace(node.Id) || !visited.Add(node.Id))
            {
                return;
            }

            WriteAt(directory, node);
            List<T> children = PlanFolderTree.ChildrenOf(allNodes, node.Id);
            List<string> childNames = BuildFolderNames(children);
            HashSet<string> wantedChildren = new HashSet<string>(childNames, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < children.Count; index++)
            {
                if (children[index] == null || index >= childNames.Count)
                {
                    continue;
                }
                WriteTree(
                    Path.Combine(directory, childNames[index]),
                    children[index],
                    allNodes,
                    visited);
            }
            RemoveStaleFolders(directory, wantedChildren, allNodes);
        }


        private void WriteListFile(string directory, T node)
        {
            PlanMirrorFile.WriteIfChanged(
                Path.Combine(directory, listFileName),
                buildListText == null ? string.Empty : buildListText(node));
        }

        private void WriteDataFile(string directory, T node)
        {
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, node);
                    PlanMirrorFile.WriteIfChanged(
                        Path.Combine(directory, dataFileName),
                        Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
            catch (Exception exception)
            {
                AppLog.Write(
                    subject + "原始数据写入失败：" + FolderNameOrEmpty(directory),
                    exception);
            }
        }

        /// <summary>
        /// 读镜像目录里的 方案.json（“把外面复制进来的文件夹收编”用）。
        /// 读不出来（没有文件、JSON 坏了）返回 null —— 调用方必须按“无法识别”处理，绝不删除。
        /// </summary>
        public T ReadNodeAt(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }
            string path = Path.Combine(directory, dataFileName);
            if (!File.Exists(path))
            {
                return null;
            }
            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    return serializer.ReadObject(stream) as T;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 删掉“已经登记过的方案的旧文件夹”（方案改名/移动后留下的那一个）。
        ///
        /// 只删这一种：文件夹里的 方案.json 能读出来、而且 Id 就在清单里。
        /// 读不出来的、Id 不在清单里的一律**留着**——用户把一整个方案文件夹复制进来时，
        /// 绝不能被同步当成垃圾删掉；留给启动时的“收编 / 无法识别”流程处理。
        /// </summary>
        private void RemoveStaleFolders(string directory, HashSet<string> wanted, IList<T> knownNodes)
        {
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(directory);
            }
            catch
            {
                return;
            }

            for (int index = 0; index < directories.Length; index++)
            {
                string name = Path.GetFileName(directories[index]);
                if (wanted.Contains(name))
                {
                    continue;
                }
                T node = ReadNodeAt(directories[index]);
                if (node == null || string.IsNullOrWhiteSpace(node.Id))
                {
                    continue;
                }
                if (PlanFolderTree.FindById(knownNodes, node.Id) == null)
                {
                    continue;
                }
                try
                {
                    Directory.Delete(directories[index], true);
                }
                catch
                {
                }
            }
        }

        private string NameOrFallback(T node)
        {
            string name = node == null || nameOf == null ? null : nameOf(node);
            return string.IsNullOrWhiteSpace(name) ? fallbackName : name;
        }

        private static string FolderNameOrEmpty(string directory)
        {
            return string.IsNullOrWhiteSpace(directory) ? string.Empty : Path.GetFileName(directory);
        }
    }
}
