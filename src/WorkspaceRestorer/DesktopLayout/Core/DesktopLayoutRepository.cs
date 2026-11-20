using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// Small, versioned XML repository for desktop layouts.  A repository is
    /// deliberately independent from AppPaths so callers and tests can choose
    /// an isolated data directory.
    /// </summary>
    public sealed class DesktopLayoutRepository
    {
        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly DataContractSerializer serializer;
        private readonly DesktopLayoutAutoMergeStore autoMergeStore;
        private readonly DesktopLayoutOrderStore orderStore;

        public DesktopLayoutRepository()
            : this(AppPaths.DesktopLayoutFile)
        {
        }

        public DesktopLayoutRepository(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A layout storage path is required.", "path");
            }
            this.path = System.IO.Path.GetFullPath(path);
            serializer = new DataContractSerializer(typeof(DesktopLayoutCatalog));
            autoMergeStore = new DesktopLayoutAutoMergeStore(this.path);
            orderStore = new DesktopLayoutOrderStore(this.path);
        }

        public string Path
        {
            get { return path; }
        }

        public string OrderPath
        {
            get { return orderStore.OrderFilePath; }
        }

        public DesktopLayoutCatalog Load()
        {
            lock (syncRoot)
            {
                // 用户 2026-10-21：重启后布局卡片少了几份，但“布局”文件夹里还留着
                // 布局.xml / 桌面.png。原档案只认 布局档案.xml，文件夹只是镜像；
                // 一旦总档案丢失/少写，文件夹里的旧布局就永远看不见。
                // FormatVersion<2 时做一次迁移：把文件夹里还没进档案的布局并回去，
                // 然后写 v2。之后删除布局不会被“文件夹还在”反复复活。
                bool needsFolderMigration = !File.Exists(path);
                DesktopLayoutCatalog catalog;
                if (!File.Exists(path))
                {
                    catalog = new DesktopLayoutCatalog();
                }
                else
                {
                    try
                    {
                        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            DesktopLayoutCatalog value = serializer.ReadObject(stream) as DesktopLayoutCatalog;
                            if (value == null)
                            {
                                catalog = new DesktopLayoutCatalog();
                                needsFolderMigration = true;
                            }
                            else
                            {
                                needsFolderMigration = value.FormatVersion < 2;
                                catalog = value;
                            }
                        }
                    }
                    catch
                    {
                        // 损坏的档案不应该让恢复停摆；先按空档案读，再用文件夹迁移补。
                        catalog = new DesktopLayoutCatalog();
                        needsFolderMigration = true;
                    }
                }

                catalog = Normalize(catalog);
                // 卡片展示顺序/父级先按 sidecar 覆盖，再整理一次树；
                // sidecar 比档案旧时 Apply 会自己忽略，退回档案里的旧顺序。
                orderStore.Apply(catalog);
                catalog = Normalize(catalog);
                // “自动并集”标记存在独立 sidecar 里，不能写进布局档案的 DataContract，
                // 否则旧档案会因为字段顺序变化读不出来。
                autoMergeStore.Apply(catalog);
                if (needsFolderMigration && IsDefaultCatalogPath())
                {
                    try
                    {
                        if (DesktopLayoutFolderStore.ImportMissingLayouts(catalog))
                        {
                            catalog = Normalize(catalog);
                            Save(catalog);
                            AppLog.Write("[图标位置保存] 已从“布局”文件夹恢复缺失布局，档案升级为 v2。");
                        }
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("[图标位置保存] 从布局文件夹恢复失败（不影响现有档案）", exception);
                    }
                }
                return Normalize(catalog);
            }
        }

        private bool IsDefaultCatalogPath()
        {
            try
            {
                return string.Equals(path,
                    System.IO.Path.GetFullPath(AppPaths.DesktopLayoutFile),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public void Save(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }

            // 这里保存调用方给的对象：调用方要么刚 Load（已叠加 sidecar），
            // 要么是 sidecar 写失败后的回退保存。绝不能在这里再叠加旧 sidecar，
            // 否则回退保存会把这次拖动覆盖回上一次的顺序。
            DesktopLayoutCatalog normalized = Normalize(catalog);
            lock (syncRoot)
            {
                string directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = path + ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                try
                {
                    XmlWriterSettings settings = new XmlWriterSettings();
                    settings.Indent = true;
                    settings.Encoding = new System.Text.UTF8Encoding(false);
                    settings.CloseOutput = true;
                    using (FileStream stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (XmlWriter writer = XmlWriter.Create(stream, settings))
                    {
                        serializer.WriteObject(writer, normalized);
                    }
                    ReplaceFile(temporaryPath);
                    autoMergeStore.Persist(normalized);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>
        /// 只持久化“卡片前后顺序 + 父文件夹”这一层展示信息。
        /// 拖动松手走这里：几 KB JSON，不重写 布局档案.xml，也不重写每份 布局.xml。
        /// </summary>
        public void SaveDisplayOrder(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }
            lock (syncRoot)
            {
                orderStore.Persist(catalog);
            }
        }

        public void SaveLayout(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                throw new ArgumentNullException("layout");
            }
            DesktopLayoutCatalog catalog = Load();
            Upsert(catalog, layout);
            Save(catalog);
        }

        public DesktopTopologyLayout Find(string topologySignature)
        {
            DesktopLayoutCatalog catalog = Load();
            if (catalog.Layouts == null || string.IsNullOrEmpty(topologySignature))
            {
                return null;
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout != null && string.Equals(layout.TopologySignature, topologySignature, StringComparison.OrdinalIgnoreCase))
                {
                    return layout;
                }
            }
            return null;
        }

        public static DesktopLayoutCatalog Normalize(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                catalog = new DesktopLayoutCatalog();
            }
            if (catalog.FormatVersion < 2)
            {
                // v2：已经做过“布局文件夹 → 档案”的一次性迁移。
                catalog.FormatVersion = 2;
            }
            if (catalog.Layouts == null)
            {
                catalog.Layouts = new List<DesktopTopologyLayout>();
            }
            for (int index = catalog.Layouts.Count - 1; index >= 0; index--)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null)
                {
                    catalog.Layouts.RemoveAt(index);
                    continue;
                }
                NormalizeLayout(layout);
            }
            NormalizeProfiles(catalog);
            bool hasNestedLayouts = false;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                if (catalog.Layouts[index] != null
                    && !string.IsNullOrWhiteSpace(catalog.Layouts[index].ParentId))
                {
                    hasNestedLayouts = true;
                    break;
                }
            }
            if (hasNestedLayouts)
            {
                // 纯扁平布局仍按原来的 SortOrder 顺序显示；只有真的出现嵌套时才整理树。
                // 每个父级下面先按 SortOrder 排，再按前序写回，避免“树对了但兄弟顺序乱”。
                catalog.Layouts = OrderNestedLayouts(catalog.Layouts);
                PlanFolderTree.Normalize(catalog.Layouts);
                // 有子项的节点必须是文件夹：旧数据/手改 XML 可能留下 IsFolder=false 的父节点，
                // 页面会把它当普通布局（不画子项），子布局也就“消失”了。
                HashSet<string> parentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[index];
                    if (layout != null && !string.IsNullOrWhiteSpace(layout.ParentId))
                    {
                        parentIds.Add(layout.ParentId);
                    }
                }
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[index];
                    if (layout != null && !layout.IsFolder && parentIds.Contains(layout.Id))
                    {
                        layout.IsFolder = true;
                    }
                }
                RenumberSiblings(catalog.Layouts);
            }
            else
            {
                // 扁平布局也要让清单顺序跟 SortOrder 一致：
                // sidecar 里只存了 SortOrder，界面却按这份清单算“同级第几个”，
                // 不排序的话重启后撤销/重做会算错位置。
                SortSiblings(catalog.Layouts);
            }
            if (catalog.PortableSingleDisplayLayout != null)
            {
                DesktopTopologyLayout portable = catalog.PortableSingleDisplayLayout;
                NormalizeLayout(portable);
                if (string.IsNullOrWhiteSpace(portable.Name))
                {
                    portable.Name = DesktopLayoutFolderStore.Describe(portable);
                }
                if (string.IsNullOrWhiteSpace(portable.Id))
                {
                    portable.Id = string.IsNullOrWhiteSpace(portable.TopologySignature)
                        ? "layout-portable-" + Guid.NewGuid().ToString("N")
                        : "layout-" + portable.TopologySignature;
                }
            }
            NormalizeActiveChoices(catalog);
            return catalog;
        }

        private static List<DesktopTopologyLayout> OrderNestedLayouts(IList<DesktopTopologyLayout> layouts)
        {
            List<DesktopTopologyLayout> result = new List<DesktopTopologyLayout>();
            if (layouts == null)
            {
                return result;
            }
            List<DesktopTopologyLayout> roots = PlanFolderTree.Roots(layouts);
            SortSiblings(roots);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<DesktopTopologyLayout> stack = new Stack<DesktopTopologyLayout>();
            for (int index = roots.Count - 1; index >= 0; index--)
            {
                stack.Push(roots[index]);
            }
            while (stack.Count > 0)
            {
                DesktopTopologyLayout layout = stack.Pop();
                if (layout == null
                    || (!string.IsNullOrWhiteSpace(layout.Id) && !visited.Add(layout.Id)))
                {
                    continue;
                }
                result.Add(layout);
                List<DesktopTopologyLayout> children = PlanFolderTree.ChildrenOf(layouts, layout.Id);
                SortSiblings(children);
                for (int index = children.Count - 1; index >= 0; index--)
                {
                    stack.Push(children[index]);
                }
            }
            for (int index = 0; index < layouts.Count; index++)
            {
                if (layouts[index] != null && !result.Contains(layouts[index]))
                {
                    result.Add(layouts[index]);
                }
            }
            return result;
        }

        private static void SortSiblings(List<DesktopTopologyLayout> siblings)
        {
            siblings.Sort(delegate(DesktopTopologyLayout left, DesktopTopologyLayout right)
            {
                int order = (left == null ? 0 : left.SortOrder).CompareTo(right == null ? 0 : right.SortOrder);
                if (order != 0)
                {
                    return order;
                }
                return string.Compare(
                    left == null ? string.Empty : left.Name,
                    right == null ? string.Empty : right.Name,
                    StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static void RenumberSiblings(IList<DesktopTopologyLayout> layouts)
        {
            if (layouts == null)
            {
                return;
            }
            Dictionary<string, int> next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < layouts.Count; index++)
            {
                DesktopTopologyLayout layout = layouts[index];
                if (layout == null)
                {
                    continue;
                }
                string parentKey = layout.ParentId ?? string.Empty;
                int order;
                next.TryGetValue(parentKey, out order);
                layout.SortOrder = order;
                next[parentKey] = order + 1;
            }
        }

        /// <summary>老档案没有 Id/Name/SortOrder：按拓扑签名生成稳定 Id，名字用“单屏/双屏 + 分辨率”。</summary>
        private static void NormalizeProfiles(DesktopLayoutCatalog catalog)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (string.IsNullOrWhiteSpace(layout.Id))
                {
                    string baseId = string.IsNullOrWhiteSpace(layout.TopologySignature)
                        ? ("layout-" + index)
                        : ("layout-" + layout.TopologySignature);
                    string candidate = baseId;
                    int suffix = 2;
                    while (!ids.Add(candidate))
                    {
                        candidate = baseId + "-" + suffix;
                        suffix++;
                    }
                    layout.Id = candidate;
                }
                else
                {
                    string candidate = layout.Id;
                    int suffix = 2;
                    while (!ids.Add(candidate))
                    {
                        candidate = layout.Id + "-" + suffix;
                        suffix++;
                    }
                    layout.Id = candidate;
                }

                if (string.IsNullOrWhiteSpace(layout.Name))
                {
                    layout.Name = DesktopLayoutFolderStore.Describe(layout);
                }
                // 重名只在同一个父级里顺延；不同“电脑A/电脑B”下都可以有自己的“笔记本”。
                string parentKey = layout.ParentId ?? string.Empty;
                string nameKey = parentKey + "\n" + layout.Name;
                int count;
                nameCounts.TryGetValue(nameKey, out count);
                count++;
                nameCounts[nameKey] = count;
                if (count > 1)
                {
                    string baseName = layout.Name;
                    layout.Name = baseName + " (" + count + ")";
                    nameCounts[parentKey + "\n" + layout.Name] = 1;
                }

                if (layout.SortOrder <= 0)
                {
                    layout.SortOrder = index;
                }
                if (layout.CreatedUtc == default(DateTime))
                {
                    layout.CreatedUtc = layout.UpdatedUtc == default(DateTime) ? DateTime.UtcNow : layout.UpdatedUtc;
                }
                if (layout.UpdatedUtc == default(DateTime))
                {
                    layout.UpdatedUtc = layout.CreatedUtc;
                }
            }
        }

        private static void NormalizeActiveChoices(DesktopLayoutCatalog catalog)
        {
            if (catalog.ActiveLayoutChoices == null)
            {
                catalog.ActiveLayoutChoices = new List<DesktopLayoutChoice>();
            }
            for (int index = catalog.ActiveLayoutChoices.Count - 1; index >= 0; index--)
            {
                DesktopLayoutChoice choice = catalog.ActiveLayoutChoices[index];
                DesktopTopologyLayout chosen = choice == null || string.IsNullOrWhiteSpace(choice.LayoutId)
                    ? null
                    : FindById(catalog, choice.LayoutId);
                if (chosen == null || chosen.IsFolder)
                {
                    catalog.ActiveLayoutChoices.RemoveAt(index);
                }
            }

            DesktopTopologyLayout active = FindById(catalog, catalog.ActiveLayoutId);
            if (active != null && active.IsFolder)
            {
                // 文件夹只用于分组，不能作为“当前可恢复布局”。
                active = null;
                catalog.ActiveLayoutId = null;
            }
            if (active == null && catalog.Layouts.Count > 0)
            {
                DesktopTopologyLayout first = null;
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout candidate = catalog.Layouts[index];
                    if (candidate == null || candidate.IsFolder)
                    {
                        continue;
                    }
                    if (first == null || candidate.SortOrder < first.SortOrder)
                    {
                        first = candidate;
                    }
                }
                active = first;
                if (active != null)
                {
                    catalog.ActiveLayoutId = active.Id;
                }
            }
            if (active != null)
            {
                SetChoice(catalog, active.TopologySignature, active.Id);
            }
        }

        public static DesktopTopologyLayout FindById(DesktopLayoutCatalog catalog, string id)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(id) || catalog.Layouts == null)
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

        public static void SetChoice(DesktopLayoutCatalog catalog, string topologySignature, string layoutId)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(layoutId))
            {
                return;
            }
            if (catalog.ActiveLayoutChoices == null)
            {
                catalog.ActiveLayoutChoices = new List<DesktopLayoutChoice>();
            }
            string signature = topologySignature ?? string.Empty;
            for (int index = 0; index < catalog.ActiveLayoutChoices.Count; index++)
            {
                DesktopLayoutChoice choice = catalog.ActiveLayoutChoices[index];
                if (choice != null && string.Equals(choice.TopologySignature ?? string.Empty, signature, StringComparison.OrdinalIgnoreCase))
                {
                    choice.LayoutId = layoutId;
                    return;
                }
            }
            DesktopLayoutChoice added = new DesktopLayoutChoice();
            added.TopologySignature = signature;
            added.LayoutId = layoutId;
            catalog.ActiveLayoutChoices.Add(added);
        }

        public static string FindChoice(DesktopLayoutCatalog catalog, string topologySignature)
        {
            if (catalog == null || catalog.ActiveLayoutChoices == null)
            {
                return string.Empty;
            }
            string signature = topologySignature ?? string.Empty;
            for (int index = 0; index < catalog.ActiveLayoutChoices.Count; index++)
            {
                DesktopLayoutChoice choice = catalog.ActiveLayoutChoices[index];
                if (choice != null && string.Equals(choice.TopologySignature ?? string.Empty, signature, StringComparison.OrdinalIgnoreCase))
                {
                    return choice.LayoutId ?? string.Empty;
                }
            }
            return string.Empty;
        }

        private static void NormalizeLayout(DesktopTopologyLayout layout)
        {
            if (layout.Topology == null)
            {
                layout.Topology = new DisplayTopology();
            }
            if (layout.Topology.Monitors == null)
            {
                layout.Topology.Monitors = new List<DisplayMonitor>();
            }
            if (layout.Topology.VirtualBounds == null)
            {
                layout.Topology.VirtualBounds = new DesktopRect();
            }
            if (layout.Icons == null)
            {
                layout.Icons = new List<DesktopIconPlacement>();
            }
            if (layout.AutoMergedIdentities == null)
            {
                layout.AutoMergedIdentities = new List<string>();
            }
            HashSet<string> iconIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < layout.Icons.Count; index++)
            {
                DesktopIconPlacement icon = layout.Icons[index];
                if (icon != null)
                {
                    // 兼容旧档案的 name:名称:序号，见 DesktopIdentity 的说明。
                    icon.Identity = DesktopIdentity.Normalize(icon.Identity);
                    if (!string.IsNullOrEmpty(icon.Identity))
                    {
                        iconIdentities.Add(icon.Identity);
                    }
                }
            }
            // 自动并集清单只保留仍然在这份布局里的身份：
            // 用户已经显式保存、删除或扫描移除的图标不能继续被当成“新图标”。
            List<string> autoMerged = new List<string>();
            HashSet<string> seenAutoMerged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < layout.AutoMergedIdentities.Count; index++)
            {
                string identity = DesktopIdentity.Normalize(layout.AutoMergedIdentities[index]);
                if (!string.IsNullOrEmpty(identity)
                    && iconIdentities.Contains(identity)
                    && seenAutoMerged.Add(identity))
                {
                    autoMerged.Add(identity);
                }
            }
            layout.AutoMergedIdentities = autoMerged;
            if (layout.GridWidth <= 0)
            {
                layout.GridWidth = 75;
            }
            if (layout.GridHeight <= 0)
            {
                layout.GridHeight = 75;
            }
            if (string.IsNullOrEmpty(layout.TopologySignature))
            {
                layout.TopologySignature = layout.Topology.Signature ?? string.Empty;
            }
        }

        private static void Upsert(DesktopLayoutCatalog catalog, DesktopTopologyLayout layout)
        {
            Normalize(catalog);
            NormalizeLayout(layout);
            if (string.IsNullOrWhiteSpace(layout.Id) && !string.IsNullOrWhiteSpace(layout.TopologySignature))
            {
                layout.Id = "layout-" + layout.TopologySignature;
            }
            // 用户 2026-10-21：同一显示器组合可以有多份命名布局。
            // 有 Id 时只更新同 Id 的那一份，绝不能按拓扑签名把别的命名布局顶掉。
            if (!string.IsNullOrWhiteSpace(layout.Id))
            {
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout existing = catalog.Layouts[index];
                    if (existing != null && string.Equals(existing.Id, layout.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        catalog.Layouts[index] = layout;
                        if (layout.Topology != null && layout.Topology.IsSingleDisplay)
                        {
                            catalog.PortableSingleDisplayLayout = layout;
                        }
                        return;
                    }
                }
            }
            else
            {
                string signature = layout.TopologySignature ?? string.Empty;
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout existing = catalog.Layouts[index];
                    if (existing != null && string.IsNullOrWhiteSpace(existing.Id)
                        && string.Equals(existing.TopologySignature, signature, StringComparison.OrdinalIgnoreCase))
                    {
                        catalog.Layouts[index] = layout;
                        return;
                    }
                }
            }
            catalog.Layouts.Add(layout);
            if (layout.Topology != null && layout.Topology.IsSingleDisplay)
            {
                catalog.PortableSingleDisplayLayout = layout;
            }
        }

        private void ReplaceFile(string temporaryPath)
        {
            if (!File.Exists(path))
            {
                File.Move(temporaryPath, path);
                return;
            }

            string backupPath = path + ".bak";
            try
            {
                // 用户要求：每次保存覆盖 .bak，只保留上一版。
                File.Replace(temporaryPath, path, backupPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                File.Copy(temporaryPath, path, true);
                File.Delete(temporaryPath);
            }
        }
    }
}
