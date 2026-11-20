using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 把每个“显示器拓扑布局”镜像成一个可打开查看的文件夹（用户 2026-10-20）：
    ///   workflows\图标位置保存\布局\显示器\
    ///       布局.xml   —— 这一份拓扑的图标位置（与总档案同源，便于单独查看/拷贝）
    ///       桌面.png   —— 保存时截的桌面图片，供界面展开预览
    ///       布局Id.txt —— 这份布局的完整 Id（旧版本曾把短 Id 拼在文件夹名后面）
    ///
    /// 注意：真正恢复仍读 `布局档案.xml`；这里只是“给用户看的文件夹 + 图片”，
    /// 所以即使文件夹被删，也不会影响恢复（下次保存会重建）。
    /// </summary>
    public static class DesktopLayoutFolderStore
    {
        public const string LayoutFileName = "布局.xml";
        public const string PreviewFileName = "桌面.png";
        /// <summary>文件夹里单独记一份布局 Id；文件夹名只给用户看，不再拼短 Id。</summary>
        public const string IdFileName = "布局Id.txt";

        public static string RootDirectory
        {
            get { return AppPaths.DesktopLayoutsDirectory; }
        }

        public static string Describe(DesktopTopologyLayout layout)
        {
            DisplayTopology topology = layout == null ? null : layout.Topology;
            List<DisplayMonitor> monitors = topology == null ? null : topology.Monitors;
            if (monitors == null || monitors.Count == 0)
            {
                return "桌面布局";
            }
            if (monitors.Count == 1)
            {
                DesktopRect bounds = monitors[0].Bounds ?? new DesktopRect();
                return "单屏 " + Math.Max(0, bounds.Width) + "×" + Math.Max(0, bounds.Height);
            }
            StringBuilder builder = new StringBuilder();
            builder.Append(monitors.Count == 2 ? "双屏 " : (monitors.Count + " 屏 "));
            for (int index = 0; index < monitors.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(" + ");
                }
                DesktopRect bounds = monitors[index].Bounds ?? new DesktopRect();
                builder.Append(Math.Max(0, bounds.Width));
                builder.Append('×');
                builder.Append(Math.Max(0, bounds.Height));
            }
            return builder.ToString();
        }

        /// <summary>界面显示用的拓扑摘要；不参与文件夹命名，避免切换语言改变数据路径。</summary>
        public static string DescribeDisplay(DesktopTopologyLayout layout)
        {
            DisplayTopology topology = layout == null ? null : layout.Topology;
            List<DisplayMonitor> monitors = topology == null ? null : topology.Monitors;
            if (monitors == null || monitors.Count == 0)
            {
                return AppText.T(DesktopLayoutTextKeys.TopologyDesktop);
            }
            if (monitors.Count == 1)
            {
                DesktopRect bounds = monitors[0].Bounds ?? new DesktopRect();
                return AppText.F(
                    DesktopLayoutTextKeys.TopologySingleFormat,
                    Math.Max(0, bounds.Width),
                    Math.Max(0, bounds.Height));
            }
            StringBuilder builder = new StringBuilder();
            builder.Append(monitors.Count == 2
                ? AppText.T(DesktopLayoutTextKeys.TopologyDualPrefix)
                : AppText.F(DesktopLayoutTextKeys.TopologyMultiPrefixFormat, monitors.Count));
            for (int index = 0; index < monitors.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(" + ");
                }
                DesktopRect bounds = monitors[index].Bounds ?? new DesktopRect();
                builder.Append(Math.Max(0, bounds.Width));
                builder.Append('×');
                builder.Append(Math.Max(0, bounds.Height));
            }
            return builder.ToString();
        }

        public static string FolderPath(DesktopTopologyLayout layout)
        {
            string existing = FindExistingFolder(layout);
            return string.IsNullOrEmpty(existing) ? CanonicalFolderPath(layout) : existing;
        }

        /// <summary>
        /// 按整棵布局树算出规范目录：根布局在“布局”下，子布局/子文件夹在父文件夹里。
        /// 只用于新建和嵌套保存；已有目录仍按 Id 反查，避免改名/旧路径留下两份。
        /// </summary>
        public static string FolderPathFor(DesktopTopologyLayout layout, IList<DesktopTopologyLayout> allLayouts)
        {
            if (layout == null)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(layout.ParentId) || allLayouts == null)
            {
                return CanonicalFolderPath(layout);
            }
            DesktopTopologyLayout parent = FindById(allLayouts, layout.ParentId);
            if (parent == null)
            {
                return CanonicalFolderPath(layout);
            }
            string parentFolder = FolderPathFor(parent, allLayouts);
            if (string.IsNullOrWhiteSpace(parentFolder))
            {
                return CanonicalFolderPath(layout);
            }
            return Path.Combine(parentFolder, SafeFolderName(layout));
        }

        /// <summary>读目录里的 布局.xml 返回 Id；读不出来返回空字符串。</summary>
        public static string ReadLayoutIdAt(string dataPath)
        {
            DesktopTopologyLayout layout = ReadLayout(dataPath);
            return layout == null ? string.Empty : (layout.Id ?? string.Empty);
        }

        /// <summary>
        /// 只按 Id / 文件夹名里的短 Id 找镜像，不再按“图标内容相同”猜。
        /// 删除、改名必须用这个：图标相同的另一份旧布局不能被误删/误搬。
        /// </summary>
        public static string ExactFolderPath(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return null;
            }
            string canonical = CanonicalFolderPath(layout);
            if (Directory.Exists(canonical) && FolderMatchesLayout(layout, canonical))
            {
                return canonical;
            }
            if (!Directory.Exists(RootDirectory))
            {
                return null;
            }
            string shortId = ShortId(layout);
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(RootDirectory, "*", SearchOption.AllDirectories);
            }
            catch
            {
                return null;
            }
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < directories.Length; index++)
            {
                string folder = directories[index];
                string suffix = ParseShortId(Path.GetFileName(folder));
                if (!string.IsNullOrEmpty(shortId)
                    && string.Equals(shortId, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
                DesktopTopologyLayout folderLayout = ReadLayout(Path.Combine(folder, LayoutFileName));
                if (folderLayout != null
                    && !string.IsNullOrWhiteSpace(layout.Id)
                    && string.Equals(layout.Id, folderLayout.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
                string storedId = ReadStoredId(folder);
                if (!string.IsNullOrWhiteSpace(layout.Id)
                    && string.Equals(layout.Id, storedId, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
            }
            return null;
        }

        /// <summary>
        /// 覆盖保存只认这个路径：先找同一 Id 的文件夹，找不到才用新的规范路径；
        /// 不会因为“图标内容一样”把另一份历史布局的文件夹当成自己的。
        /// </summary>
        private static string StorageFolderPath(DesktopTopologyLayout layout)
        {
            string exact = ExactFolderPath(layout);
            return string.IsNullOrEmpty(exact) ? CanonicalFolderPath(layout) : exact;
        }

        /// <summary>
        /// 改名后把原文件夹搬到新名字对应的路径，保证一份布局始终只有一个文件夹。
        /// </summary>
        public static bool MoveToCanonicalFolder(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return false;
            }
            bool moved = EnsureCanonicalFolder(layout);
            if (moved)
            {
                Save(layout, false, false);
            }
            return moved;
        }

        /// <summary>
        /// 只在“同一 Id 的旧文件夹还叫旧名字”时把它搬到规范名字；
        /// 目标已存在就保持原状，绝不合并或覆盖另一份布局。
        /// </summary>
        private static bool EnsureCanonicalFolder(DesktopTopologyLayout layout)
        {
            string source = ExactFolderPath(layout);
            string destination = CanonicalFolderPath(layout);
            if (string.IsNullOrEmpty(source)
                || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (Directory.Exists(destination))
            {
                AppLog.Write("[图标位置保存] 目标文件夹已存在，保留原文件夹：" + destination);
                return false;
            }
            try
            {
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                Directory.Move(source, destination);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 搬布局文件夹失败：" + source + " -> " + destination, exception);
                return false;
            }
        }

        /// <summary>删除布局时连同它的镜像文件夹一起删；回收站里已经保存了完整数据。</summary>
        public static bool DeleteFolder(DesktopTopologyLayout layout)
        {
            string folder = ExactFolderPath(layout);
            if (string.IsNullOrEmpty(folder))
            {
                return true;
            }
            try
            {
                Directory.Delete(folder, true);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 删除布局文件夹失败：" + folder, exception);
                return false;
            }
        }

        /// <summary>
        /// 和“工作方案/自启动方案”的 Sync 清理一样：按当前档案名单对齐文件夹。
        /// 在档案里的布局文件夹保留（旧名字搬成规范名字），不在档案里的历史孤儿文件夹整个删掉。
        /// </summary>
        public static int Sync(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || !Directory.Exists(RootDirectory))
            {
                return 0;
            }
            if (HasNestedLayouts(catalog.Layouts))
            {
                // 嵌套布局不按“根目录一层”清理：子布局/子文件夹本来就不在根目录。
                // 只补**缺失或 Id 对不上**的 布局.xml；已经正确的绝不重写。
                // 以前这里无条件 Save，每读一次清单就把所有 77 KB 的 布局.xml 全写一遍，
                // 拖动时鼠标每移动一下都会触发，界面会非常卡。
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[index];
                    if (layout == null)
                    {
                        continue;
                    }
                    string folder = FolderPathFor(layout, catalog.Layouts);
                    if (string.IsNullOrWhiteSpace(folder)
                        || !Directory.Exists(folder)
                        || !File.Exists(Path.Combine(folder, LayoutFileName))
                        || !FolderMatchesLayout(layout, folder))
                    {
                        Save(layout, catalog.Layouts, false, false);
                    }
                }
                return 0;
            }
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null)
                {
                    continue;
                }
                MoveToCanonicalFolder(layout);
                string folder = ExactFolderPath(layout);
                if (!string.IsNullOrEmpty(folder))
                {
                    wanted.Add(Path.GetFullPath(folder));
                }
            }
            if (catalog.PortableSingleDisplayLayout != null)
            {
                DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
                MoveToCanonicalFolder(portable);
                string folder = ExactFolderPath(portable);
                if (!string.IsNullOrEmpty(folder))
                {
                    wanted.Add(Path.GetFullPath(folder));
                }
            }

            int removed = 0;
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(RootDirectory);
            }
            catch
            {
                return 0;
            }
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = directories[index];
                string full = Path.GetFullPath(directory);
                if (wanted.Contains(full))
                {
                    continue;
                }
                // 只清理由本程序写出的布局镜像，未知文件夹不碰。
                string layoutFile = Path.Combine(directory, LayoutFileName);
                if (!File.Exists(layoutFile))
                {
                    // 没有 布局.xml、里面（更深一层）也没有布局文件 → 这个文件夹程序完全认不出来。
                    // 按用户要求搬到 `备份\无法识别\图标位置保存\`，**只移不删**；
                    // “电脑A”这种装着子布局的分组文件夹（里面还有 布局.xml）原样保留。
                    if (ContainsLayoutFileBelow(directory))
                    {
                        continue;
                    }
                    string unrecognizedDestination;
                    if (WorkspaceRestorer.Infrastructure.PlanMirrors.MirrorIntake.MoveToUnrecognized(
                        directory,
                        AppPaths.DesktopLayoutKind,
                        out unrecognizedDestination))
                    {
                        AppLog.Write("[图标位置保存] 认不出来的布局文件夹已搬到“无法识别”："
                            + directory + " -> " + unrecognizedDestination);
                        removed++;
                    }
                    continue;
                }
                // 布局文件读不出来时**绝不删**：旧版本/字段升级会让旧 XML 暂时读失败，
                // 这时它只是“程序看不懂”，不是“用户不要了”。删掉会把截图一起丢掉。
                if (ReadLayout(layoutFile) == null)
                {
                    AppLog.Write("[图标位置保存] 布局文件夹读不出来，保留不删：" + directory);
                    continue;
                }
                // 有桌面截图的孤儿文件夹也不自动删：程序回收站只存布局 XML，
                // Directory.Delete 又不进 Windows 回收站，删掉图片就无法找回。
                if (HasPreviewAt(directory))
                {
                    AppLog.Write("[图标位置保存] 未引用布局文件夹含桌面截图，保留不删：" + directory);
                    continue;
                }
                try
                {
                    Directory.Delete(directory, true);
                    removed++;
                }
                catch (Exception exception)
                {
                    AppLog.Write("[图标位置保存] 清理未引用布局文件夹失败：" + directory, exception);
                }
            }
            if (removed > 0)
            {
                AppLog.Write("[图标位置保存] Sync 清理未引用布局文件夹 " + removed + " 个。");
            }
            return removed;
        }

        /// <summary>更深的层级里有没有 布局.xml（有就说明这个文件夹是分组文件夹，不是认不出来的垃圾）。</summary>
        private static bool ContainsLayoutFileBelow(string directory)
        {
            try
            {
                string[] directories = Directory.GetDirectories(directory);
                for (int index = 0; index < directories.Length; index++)
                {
                    if (File.Exists(Path.Combine(directories[index], LayoutFileName)))
                    {
                        return true;
                    }
                    if (ContainsLayoutFileBelow(directories[index]))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 找出“不在档案里、但还有 布局.xml”的历史文件夹。
        /// 调用方先把它们写进回收站，再删；不要再直接删历史数据。
        /// </summary>
        public static IList<DesktopLayoutOrphan> FindUnreferencedFolders(DesktopLayoutCatalog catalog)
        {
            List<DesktopLayoutOrphan> result = new List<DesktopLayoutOrphan>();
            if (catalog == null || !Directory.Exists(RootDirectory))
            {
                return result;
            }
            if (HasNestedLayouts(catalog.Layouts))
            {
                // 嵌套时根目录不是唯一层级，按一层扫描会把整个“电脑A”误判成孤儿。
                return result;
            }
            HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null)
                {
                    continue;
                }
                MoveToCanonicalFolder(layout);
                string folder = ExactFolderPath(layout);
                if (!string.IsNullOrEmpty(folder))
                {
                    wanted.Add(Path.GetFullPath(folder));
                }
            }
            if (catalog.PortableSingleDisplayLayout != null)
            {
                DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
                MoveToCanonicalFolder(portable);
                string folder = ExactFolderPath(portable);
                if (!string.IsNullOrEmpty(folder))
                {
                    wanted.Add(Path.GetFullPath(folder));
                }
            }
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(RootDirectory);
            }
            catch
            {
                return result;
            }
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = directories[index];
                if (wanted.Contains(Path.GetFullPath(directory)))
                {
                    continue;
                }
                string xml = Path.Combine(directory, LayoutFileName);
                if (!File.Exists(xml))
                {
                    continue;
                }
                DesktopTopologyLayout layout = ReadLayout(xml);
                if (layout == null)
                {
                    continue;
                }
                // 含截图的孤儿不交给 Preserve 删除；否则回收站只有 XML，图片会一起没。
                if (HasPreviewAt(directory))
                {
                    AppLog.Write("[图标位置保存] 历史布局文件夹含桌面截图，保留不删：" + directory);
                    continue;
                }
                DesktopLayoutOrphan orphan = new DesktopLayoutOrphan();
                orphan.FolderPath = directory;
                orphan.Layout = layout;
                result.Add(orphan);
            }
            return result;
        }

        public static bool DeleteFolderAt(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return true;
            }
            try
            {
                Directory.Delete(folder, true);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 删除历史布局文件夹失败：" + folder, exception);
                return false;
            }
        }

        public static string LayoutPath(DesktopTopologyLayout layout)
        {
            return Path.Combine(FolderPath(layout), LayoutFileName);
        }

        public static string PreviewPath(DesktopTopologyLayout layout)
        {
            return Path.Combine(FolderPath(layout), PreviewFileName);
        }

        /// <summary>
        /// 老档案的 Id 是空的，文件夹名里只有前 8 位；新版 Id 又可能带 layout- 前缀。
        /// 所以不能只看“当前算法算出来的文件夹名”，要按短 Id / XML 里的 Id / 图标内容
        /// 反查已经存在的文件夹，否则用户明明有 桌面.png，界面也会说没有。
        /// </summary>
        private static string FindExistingFolder(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return null;
            }
            string canonical = CanonicalFolderPath(layout);
            if (Directory.Exists(canonical) && FolderMatchesLayout(layout, canonical))
            {
                return canonical;
            }
            if (!Directory.Exists(RootDirectory))
            {
                return null;
            }
            string shortId = ShortId(layout);
            string best = null;
            DateTime bestWrite = DateTime.MinValue;
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(RootDirectory, "*", SearchOption.AllDirectories);
            }
            catch
            {
                return null;
            }
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < directories.Length; index++)
            {
                string folder = directories[index];
                if (string.Equals(folder, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    // 同名文件夹不是这份布局的，不能按“图标一样”抢过来。
                    continue;
                }
                string suffix = ParseShortId(Path.GetFileName(folder));
                if (!string.IsNullOrEmpty(shortId)
                    && string.Equals(shortId, suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
                DesktopTopologyLayout folderLayout = ReadLayout(Path.Combine(folder, LayoutFileName));
                string storedId = ReadStoredId(folder);
                if (!string.IsNullOrWhiteSpace(layout.Id)
                    && string.Equals(layout.Id, storedId, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
                if (folderLayout == null)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(layout.Id)
                    && string.Equals(layout.Id, folderLayout.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return folder;
                }
                if (!SameIconsAndGrid(layout, folderLayout))
                {
                    continue;
                }
                DateTime write;
                try
                {
                    write = Directory.GetLastWriteTimeUtc(folder);
                }
                catch
                {
                    write = DateTime.MinValue;
                }
                if (best == null || write > bestWrite)
                {
                    best = folder;
                    bestWrite = write;
                }
            }
            return best;
        }

        /// <summary>
        /// 一次性把“布局”文件夹里还没进 布局档案.xml 的布局并回档案。
        /// 只用于 FormatVersion&lt;2 的迁移：迁移后写 v2，删除过的布局不会因为
        /// 文件夹里还留着而被反复复活。
        /// </summary>
        public static bool ImportMissingLayouts(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || catalog.Layouts == null || !Directory.Exists(RootDirectory))
            {
                return false;
            }
            bool changed = false;
            string[] directories;
            try
            {
                directories = Directory.GetDirectories(RootDirectory);
            }
            catch
            {
                return false;
            }
            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < directories.Length; index++)
            {
                string folder = directories[index];
                DesktopTopologyLayout layout = ReadLayout(Path.Combine(folder, LayoutFileName));
                if (layout == null)
                {
                    continue;
                }
                string suffix = ParseShortId(Path.GetFileName(folder));
                if (string.IsNullOrWhiteSpace(layout.Id))
                {
                    string signature = layout.TopologySignature;
                    if (string.IsNullOrWhiteSpace(signature) && layout.Topology != null)
                    {
                        signature = layout.Topology.Signature;
                    }
                    if (!string.IsNullOrWhiteSpace(signature)
                        && !string.IsNullOrEmpty(suffix)
                        && signature.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        layout.Id = signature;
                    }
                    else if (!string.IsNullOrWhiteSpace(suffix))
                    {
                        layout.Id = suffix;
                    }
                    else
                    {
                        layout.Id = "folder-" + Guid.NewGuid().ToString("N");
                    }
                }
                if (FindById(catalog, layout.Id) != null)
                {
                    continue;
                }
                if (ContainsEquivalent(catalog, layout))
                {
                    continue;
                }
                catalog.Layouts.Add(layout);
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// 写文件夹里的“布局.xml”；<paramref name="capturePreview"/> 时顺手截一张桌面图。
        /// 截图失败不影响布局文件写入。
        /// </summary>
        public static bool Save(DesktopTopologyLayout layout, bool capturePreview, bool showDesktop)
        {
            return Save(layout, null, capturePreview, showDesktop);
        }

        /// <summary>
        /// 嵌套保存：<paramref name="allLayouts"/> 不为空时按树计算目录。
        /// 文件夹也写 布局.xml（IsFolder=true），但不写预览图。
        /// </summary>
        public static bool Save(
            DesktopTopologyLayout layout,
            IList<DesktopTopologyLayout> allLayouts,
            bool capturePreview,
            bool showDesktop)
        {
            if (layout == null)
            {
                return false;
            }
            string folder;
            if (allLayouts == null)
            {
                // 覆盖保存时如果旧文件夹还是旧名字，先搬成当前名字，仍然只有一个。
                EnsureCanonicalFolder(layout);
                folder = StorageFolderPath(layout);
            }
            else
            {
                // 已经有一份同一 Id 的文件夹时，继续用它：
                // ParentId 现在只是卡片分组（展示层），如果目录移动失败/还没移动，
                // 不能按新父级再建一份空文件夹，把原来的 桌面.png 留在旧目录里。
                string existing = ExactFolderPath(layout);
                folder = string.IsNullOrEmpty(existing)
                    ? FolderPathFor(layout, allLayouts)
                    : existing;
            }
            Directory.CreateDirectory(folder);
            bool wroteLayout = false;
            try
            {
                DataContractSerializer serializer = new DataContractSerializer(typeof(DesktopTopologyLayout));
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Indent = true;
                settings.Encoding = new UTF8Encoding(false);
                string temporary = Path.Combine(folder, LayoutFileName + ".tmp." + Guid.NewGuid().ToString("N"));
                try
                {
                    using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        serializer.WriteObject(writer, layout);
                    }
                    string target = Path.Combine(folder, LayoutFileName);
                    if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                    File.Move(temporary, target);
                    wroteLayout = true;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary))
                        {
                            File.Delete(temporary);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 写布局文件夹失败：" + folder, exception);
            }

            if (wroteLayout)
            {
                WriteIdFile(folder, layout);
            }

            if (capturePreview && !layout.IsFolder)
            {
                string previewPath = Path.Combine(folder, PreviewFileName);
                string error;
                if (!DesktopScreenCapture.TryCapture(previewPath, showDesktop, out error))
                {
                    AppLog.Write("[图标位置保存] 桌面截图失败（布局仍然保存）：" + error);
                }
            }
            return wroteLayout;
        }

        public static bool HasPreview(DesktopTopologyLayout layout)
        {
            try
            {
                string path = PreviewPath(layout);
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 目录里有没有可用的 桌面.png。清理历史孤儿文件夹前必须先看这个：
        /// 图片不在回收站里，Directory.Delete 也不会进 Windows 回收站，删了就真没了。
        /// </summary>
        public static bool HasPreviewAt(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }
            try
            {
                string path = Path.Combine(folder, PreviewFileName);
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 复制一份布局时把源文件夹的「桌面.png」一起带过去。
        /// 预览图只是给用户看的；复制失败不影响布局数据（返回 false，调用方照常返回新布局）。
        /// </summary>
        public static bool CopyPreview(DesktopTopologyLayout source, DesktopTopologyLayout target)
        {
            if (source == null || target == null)
            {
                return false;
            }
            try
            {
                string sourcePath = PreviewPath(source);
                if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 0)
                {
                    return false;
                }
                string targetPath = PreviewPath(target);
                string targetFolder = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                }
                File.Copy(sourcePath, targetPath, true);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 复制布局预览图失败（布局数据仍然保留）", exception);
                return false;
            }
        }

        private static string CanonicalFolderPath(DesktopTopologyLayout layout)
        {
            return Path.Combine(RootDirectory, SafeFolderName(layout));
        }

        private static string SafeFolderName(DesktopTopologyLayout layout)
        {
            string name = layout != null && !string.IsNullOrWhiteSpace(layout.Name)
                ? layout.Name.Trim()
                : Describe(layout);
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(name.Length);
            for (int index = 0; index < name.Length; index++)
            {
                char value = name[index];
                builder.Append(Array.IndexOf(invalid, value) >= 0 ? '_' : value);
            }
            string safe = builder.ToString().Trim().TrimEnd('.');
            if (safe.Length > 100)
            {
                safe = safe.Substring(0, 100).TrimEnd();
            }
            if (string.IsNullOrEmpty(safe))
            {
                safe = "桌面布局";
            }
            return safe;
        }

        private static DesktopTopologyLayout FindById(IList<DesktopTopologyLayout> layouts, string id)
        {
            if (layouts == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            for (int index = 0; index < layouts.Count; index++)
            {
                DesktopTopologyLayout layout = layouts[index];
                if (layout != null && string.Equals(layout.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return layout;
                }
            }
            return null;
        }

        private static string ShortId(DesktopTopologyLayout layout)
        {
            string id = layout == null ? string.Empty : (layout.Id ?? string.Empty);
            return id.Length > 8 ? id.Substring(0, 8) : id;
        }

        private static string ParseShortId(string folderName)
        {
            if (string.IsNullOrEmpty(folderName))
            {
                return string.Empty;
            }
            int open = folderName.LastIndexOf('[');
            int close = folderName.LastIndexOf(']');
            if (open < 0 || close <= open + 1)
            {
                return string.Empty;
            }
            return folderName.Substring(open + 1, close - open - 1).Trim();
        }

        private static bool HasNestedLayouts(IList<DesktopTopologyLayout> layouts)
        {
            if (layouts == null)
            {
                return false;
            }
            for (int index = 0; index < layouts.Count; index++)
            {
                if (layouts[index] != null && !string.IsNullOrWhiteSpace(layouts[index].ParentId))
                {
                    return true;
                }
            }
            return false;
        }

        private static void WriteIdFile(string folder, DesktopTopologyLayout layout)
        {
            if (string.IsNullOrWhiteSpace(folder) || layout == null || string.IsNullOrWhiteSpace(layout.Id))
            {
                return;
            }
            try
            {
                string idPath = Path.Combine(folder, IdFileName);
                string content = layout.Id.Trim();
                if (File.Exists(idPath))
                {
                    try
                    {
                        if (string.Equals(File.ReadAllText(idPath).Trim(), content, StringComparison.Ordinal))
                        {
                            return;
                        }
                    }
                    catch
                    {
                    }
                }
                File.WriteAllText(idPath, content, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 写布局 Id 文件失败：" + folder, exception);
            }
        }

        private static string ReadStoredId(string folder)
        {
            try
            {
                string idPath = Path.Combine(folder, IdFileName);
                if (!File.Exists(idPath))
                {
                    return string.Empty;
                }
                string text = File.ReadAllText(idPath).Trim();
                int line = text.IndexOfAny(new char[] { '\r', '\n' });
                if (line >= 0)
                {
                    text = text.Substring(0, line).Trim();
                }
                return text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool FolderMatchesLayout(DesktopTopologyLayout layout, string folder)
        {
            if (layout == null || string.IsNullOrWhiteSpace(layout.Id) || string.IsNullOrEmpty(folder))
            {
                return false;
            }
            string storedId = ReadStoredId(folder);
            if (!string.IsNullOrEmpty(storedId)
                && string.Equals(layout.Id, storedId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            DesktopTopologyLayout folderLayout = ReadLayout(Path.Combine(folder, LayoutFileName));
            return folderLayout != null
                && !string.IsNullOrWhiteSpace(folderLayout.Id)
                && string.Equals(layout.Id, folderLayout.Id, StringComparison.OrdinalIgnoreCase);
        }

        private static DesktopTopologyLayout ReadLayout(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            DataContractSerializer serializer = new DataContractSerializer(typeof(DesktopTopologyLayout));
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return serializer.ReadObject(stream) as DesktopTopologyLayout;
                }
            }
            catch (Exception first)
            {
                // 2026-10-20 16:47 的文件夹是在 Id/Name/SortOrder/CreatedUtc 加进模型之前写的。
                // DataContractSerializer 要求成员按顺序且值类型成员不能缺；补默认值后重读。
                try
                {
                    using (MemoryStream repaired = RepairLegacyLayoutXml(path))
                    {
                        return serializer.ReadObject(repaired) as DesktopTopologyLayout;
                    }
                }
                catch (Exception second)
                {
                    AppLog.Write("[图标位置保存] 读取布局文件夹失败：" + path
                        + "（原始错误：" + first.Message + "；补字段后：" + second.Message + "）", second);
                    return null;
                }
            }
        }

        private static MemoryStream RepairLegacyLayoutXml(string path)
        {
            XmlDocument document = new XmlDocument();
            // 技术身份改名后，旧布局.xml 的 DataContract 命名空间还是旧名字；
            // 只在内存里换成新命名空间再读，绝不改写用户的布局文件。
            string xml = File.ReadAllText(path);
            xml = xml.Replace(
                WorkspaceRestorer.Infrastructure.LegacyIdentity.AppUserModelId,
                WorkspaceRestorer.Infrastructure.ProductIdentity.TechnicalName + ".Desktop");
            document.LoadXml(xml);
            XmlElement root = document.DocumentElement;
            if (root == null)
            {
                throw new InvalidDataException("布局.xml 没有根元素。");
            }
            string ns = root.NamespaceURI;
            string updated = ChildText(root, "_x003C_UpdatedUtc_x003E_k__BackingField");
            if (string.IsNullOrWhiteSpace(updated))
            {
                updated = DateTime.UtcNow.ToString("o");
            }
            if (Child(root, "_x003C_CreatedUtc_x003E_k__BackingField") == null)
            {
                XmlElement created = document.CreateElement("_x003C_CreatedUtc_x003E_k__BackingField", ns);
                created.InnerText = updated;
                root.PrependChild(created);
            }

            XmlElement icons = Child(root, "_x003C_Icons_x003E_k__BackingField");
            XmlNode insertAfter = icons;
            insertAfter = EnsureChild(document, root, insertAfter, ns, "_x003C_Id_x003E_k__BackingField", string.Empty);
            insertAfter = EnsureChild(document, root, insertAfter, ns, "_x003C_Name_x003E_k__BackingField", string.Empty);
            EnsureChild(document, root, insertAfter, ns, "_x003C_SortOrder_x003E_k__BackingField", "0");

            MemoryStream stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;
            return stream;
        }

        private static XmlNode EnsureChild(
            XmlDocument document, XmlElement root, XmlNode insertAfter, string ns, string name, string value)
        {
            XmlElement existing = Child(root, name);
            if (existing != null)
            {
                return existing;
            }
            XmlElement element = document.CreateElement(name, ns);
            element.InnerText = value;
            if (insertAfter != null)
            {
                root.InsertAfter(element, insertAfter);
            }
            else
            {
                root.AppendChild(element);
            }
            return element;
        }

        private static XmlElement Child(XmlElement root, string name)
        {
            if (root == null)
            {
                return null;
            }
            for (int index = 0; index < root.ChildNodes.Count; index++)
            {
                XmlElement element = root.ChildNodes[index] as XmlElement;
                if (element != null && string.Equals(element.LocalName, name, StringComparison.Ordinal))
                {
                    return element;
                }
            }
            return null;
        }

        private static string ChildText(XmlElement root, string name)
        {
            XmlElement element = Child(root, name);
            return element == null ? string.Empty : element.InnerText;
        }

        private static DesktopTopologyLayout FindById(DesktopLayoutCatalog catalog, string id)
        {
            if (catalog == null || catalog.Layouts == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout != null && string.Equals(layout.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return layout;
                }
            }
            return null;
        }

        /// <summary>同一次保存既进了布局档案、又写了文件夹：内容完全一样就当同一份，不重复导入。</summary>
        private static bool ContainsEquivalent(DesktopLayoutCatalog catalog, DesktopTopologyLayout candidate)
        {
            if (catalog == null || catalog.Layouts == null || candidate == null)
            {
                return false;
            }
            string fingerprint = Fingerprint(candidate);
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout != null && string.Equals(Fingerprint(layout), fingerprint, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            if (catalog.PortableSingleDisplayLayout != null
                && string.Equals(Fingerprint(catalog.PortableSingleDisplayLayout), fingerprint, StringComparison.Ordinal))
            {
                return true;
            }
            return false;
        }

        private static bool SameIconsAndGrid(DesktopTopologyLayout left, DesktopTopologyLayout right)
        {
            return left != null && right != null
                && string.Equals(Fingerprint(left), Fingerprint(right), StringComparison.Ordinal);
        }

        private static string Fingerprint(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return string.Empty;
            }
            StringBuilder builder = new StringBuilder();
            builder.Append(layout.TopologySignature ?? string.Empty);
            builder.Append('|').Append(layout.GridWidth).Append('x').Append(layout.GridHeight);
            List<string> icons = new List<string>();
            if (layout.Icons != null)
            {
                for (int index = 0; index < layout.Icons.Count; index++)
                {
                    DesktopIconPlacement icon = layout.Icons[index];
                    if (icon == null)
                    {
                        continue;
                    }
                    icons.Add(DesktopIdentity.Normalize(icon.Identity ?? string.Empty)
                        + "|" + icon.X + "|" + icon.Y);
                }
            }
            icons.Sort(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < icons.Count; index++)
            {
                builder.Append('\n').Append(icons[index]);
            }
            return builder.ToString();
        }
    }

    /// <summary>Sync 发现的历史布局文件夹：先写回收站，再删除。</summary>
    public sealed class DesktopLayoutOrphan
    {
        public string FolderPath { get; set; }
        public DesktopTopologyLayout Layout { get; set; }
    }
}
