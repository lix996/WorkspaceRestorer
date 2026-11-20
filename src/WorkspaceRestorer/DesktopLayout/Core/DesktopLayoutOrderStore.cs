using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 布局卡片的“展示位置”：同级前后顺序 + 挂在哪个布局文件夹下。
    ///
    /// 这两项只影响卡片怎么排、怎么分组，不参与桌面图标恢复；
    /// 所以拖动松手时只写这个几 KB 的 sidecar，不再为了换个顺序重写
    /// 400 KB 的 布局档案.xml 和每份 77 KB 的 布局.xml。
    /// sidecar 缺失、损坏或过期时，自动回退到 布局档案.xml 里的旧顺序。
    /// </summary>
    [DataContract]
    public sealed class DesktopLayoutOrderEntry
    {
        [DataMember(Order = 1)]
        public string LayoutId { get; set; }

        [DataMember(Order = 2)]
        public string ParentId { get; set; }

        [DataMember(Order = 3)]
        public int SortOrder { get; set; }
    }

    [DataContract]
    public sealed class DesktopLayoutOrderCatalog
    {
        public DesktopLayoutOrderCatalog()
        {
            FormatVersion = 1;
            Entries = new List<DesktopLayoutOrderEntry>();
        }

        [DataMember(Order = 1)]
        public int FormatVersion { get; set; }

        /// <summary>
        /// 写这份 sidecar 时 布局档案.xml 的最后写入时间（UTC ticks）。
        /// 档案之后又保存过就说明档案里已经包含最新顺序，这份 sidecar 作废。
        /// 这样不需要在“写档案”和“写 sidecar”之间做危险的两步删除。
        /// </summary>
        [DataMember(Order = 2)]
        public long CatalogWriteUtcTicks { get; set; }

        [DataMember(Order = 3)]
        public List<DesktopLayoutOrderEntry> Entries { get; set; }
    }

    public sealed class DesktopLayoutOrderStore
    {
        private readonly string catalogPath;
        private readonly string path;
        private readonly AtomicJsonStore<DesktopLayoutOrderCatalog> store;

        public DesktopLayoutOrderStore()
            : this(AppPaths.DesktopLayoutFile)
        {
        }

        public DesktopLayoutOrderStore(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                throw new ArgumentException("A layout catalog path is required.", "catalogPath");
            }
            this.catalogPath = Path.GetFullPath(catalogPath);
            path = PathFor(this.catalogPath);
            store = new AtomicJsonStore<DesktopLayoutOrderCatalog>(
                path,
                delegate { return new DesktopLayoutOrderCatalog(); });
        }

        /// <summary>
        /// 默认档案用中文名“布局排列.json”，和 布局档案.xml 并排；
        /// 测试/临时仓库用 “&lt;档案名&gt;.排列.json”，避免同目录多份档案互相覆盖。
        /// </summary>
        public static string PathFor(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                throw new ArgumentException("A layout catalog path is required.", "catalogPath");
            }
            string fullPath = Path.GetFullPath(catalogPath);
            string directory = Path.GetDirectoryName(fullPath);
            string name = Path.GetFileNameWithoutExtension(fullPath);
            string fileName = string.Equals(name, "布局档案", StringComparison.OrdinalIgnoreCase)
                ? "布局排列.json"
                : name + ".排列.json";
            return Path.Combine(directory ?? string.Empty, fileName);
        }

        public string OrderFilePath
        {
            get { return path; }
        }

        /// <summary>把 sidecar 里的顺序/父级盖到档案对象上；条目失效时保持档案原值。</summary>
        public void Apply(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || catalog.Layouts == null || catalog.Layouts.Count == 0)
            {
                return;
            }
            DesktopLayoutOrderCatalog stored = store.Load();
            if (stored == null || stored.Entries == null || stored.Entries.Count == 0)
            {
                return;
            }
            long archiveTicks = ArchiveWriteUtcTicks();
            if (archiveTicks > 0 && stored.CatalogWriteUtcTicks < archiveTicks)
            {
                // 档案比 sidecar 新：档案里已经包含拖动后的顺序（例如随后保存过图标），
                // 不能让旧 sidecar 再把顺序盖回去。
                return;
            }

            Dictionary<string, DesktopLayoutOrderEntry> entries =
                new Dictionary<string, DesktopLayoutOrderEntry>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < stored.Entries.Count; index++)
            {
                DesktopLayoutOrderEntry entry = stored.Entries[index];
                if (entry != null && !string.IsNullOrWhiteSpace(entry.LayoutId))
                {
                    entries[entry.LayoutId] = entry;
                }
            }
            if (entries.Count == 0)
            {
                return;
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout != null && !string.IsNullOrWhiteSpace(layout.Id))
                {
                    ids.Add(layout.Id);
                }
            }
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null || string.IsNullOrWhiteSpace(layout.Id))
                {
                    continue;
                }
                DesktopLayoutOrderEntry entry;
                if (!entries.TryGetValue(layout.Id, out entry) || entry == null)
                {
                    continue;
                }
                layout.SortOrder = entry.SortOrder;
                string parentId = (entry.ParentId ?? string.Empty).Trim();
                if (parentId.Length == 0
                    || string.Equals(parentId, layout.Id, StringComparison.OrdinalIgnoreCase)
                    || !ids.Contains(parentId))
                {
                    layout.ParentId = null;
                }
                else
                {
                    layout.ParentId = parentId;
                }
            }
        }

        /// <summary>只写几 KB 的展示顺序；不碰 布局档案.xml / 布局.xml / 布局目录。</summary>
        public void Persist(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                throw new ArgumentNullException("catalog");
            }
            DesktopLayoutOrderCatalog stored = new DesktopLayoutOrderCatalog();
            stored.CatalogWriteUtcTicks = ArchiveWriteUtcTicks();
            if (catalog.Layouts != null)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < catalog.Layouts.Count; index++)
                {
                    DesktopTopologyLayout layout = catalog.Layouts[index];
                    if (layout == null || string.IsNullOrWhiteSpace(layout.Id) || !seen.Add(layout.Id))
                    {
                        continue;
                    }
                    DesktopLayoutOrderEntry entry = new DesktopLayoutOrderEntry();
                    entry.LayoutId = layout.Id;
                    entry.ParentId = string.IsNullOrWhiteSpace(layout.ParentId) ? null : layout.ParentId;
                    entry.SortOrder = layout.SortOrder;
                    stored.Entries.Add(entry);
                }
            }
            store.Save(stored);
        }

        private long ArchiveWriteUtcTicks()
        {
            try
            {
                return File.Exists(catalogPath)
                    ? File.GetLastWriteTimeUtc(catalogPath).Ticks
                    : 0L;
            }
            catch
            {
                return 0L;
            }
        }
    }
}
