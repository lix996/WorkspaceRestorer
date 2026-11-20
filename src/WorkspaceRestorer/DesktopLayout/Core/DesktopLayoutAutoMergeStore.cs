using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;

namespace WorkspaceRestorer.Desktop
{
    [DataContract]
    internal sealed class DesktopLayoutIconTimeEntry
    {
        [DataMember(Order = 1)]
        public string Identity { get; set; }

        [DataMember(Order = 2)]
        public long CreatedUtcTicks { get; set; }
    }

    [DataContract]
    internal sealed class DesktopLayoutAutoMergeEntry
    {
        [DataMember(Order = 1)]
        public string LayoutId { get; set; }

        [DataMember(Order = 2)]
        public List<string> Identities { get; set; }

        /// <summary>
        /// 上次保存/扫描时每个图标对应的文件创建时间。
        /// 用来识别“同名文件删掉又新建”；旧版 sidecar 没有这个成员，按缺失处理。
        /// </summary>
        [DataMember(Order = 3)]
        public List<DesktopLayoutIconTimeEntry> IconCreatedUtc { get; set; }
    }

    [DataContract]
    internal sealed class DesktopLayoutAutoMergeCatalog
    {
        public DesktopLayoutAutoMergeCatalog()
        {
            Entries = new List<DesktopLayoutAutoMergeEntry>();
        }

        [DataMember(Order = 1)]
        public List<DesktopLayoutAutoMergeEntry> Entries { get; set; }
    }

    /// <summary>
    /// “自动并集”身份清单的 sidecar。
    ///
    /// 为什么不塞进 DesktopTopologyLayout：那个 DataContract 是按字段顺序读的，
    /// 增加成员会让旧布局档案直接反序列化失败（2026-10-24 实际踩到，文件夹迁移还被误触发）。
    /// 这里单独存一份，旧版本忽略它即可；清单只影响编辑页把哪些图标归入“新图标”，
    /// 不影响恢复位置本身。
    /// </summary>
    internal sealed class DesktopLayoutAutoMergeStore
    {
        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly DataContractSerializer serializer =
            new DataContractSerializer(typeof(DesktopLayoutAutoMergeCatalog));

        internal DesktopLayoutAutoMergeStore(string catalogPath)
        {
            if (string.IsNullOrWhiteSpace(catalogPath))
            {
                throw new ArgumentException("A layout catalog path is required.", "catalogPath");
            }
            path = Path.GetFullPath(catalogPath) + ".auto-merged.xml";
        }

        internal void Apply(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || catalog.Layouts == null)
            {
                return;
            }
            Dictionary<string, DesktopLayoutAutoMergeEntry> entries = LoadCore();
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                DesktopTopologyLayout layout = catalog.Layouts[index];
                if (layout == null)
                {
                    continue;
                }
                layout.AutoMergedIdentities = new List<string>();
                DesktopLayoutAutoMergeEntry storedEntry;
                if (string.IsNullOrWhiteSpace(layout.Id)
                    || !entries.TryGetValue(layout.Id, out storedEntry)
                    || storedEntry == null)
                {
                    continue;
                }
                HashSet<string> iconIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (layout.Icons != null)
                {
                    for (int iconIndex = 0; iconIndex < layout.Icons.Count; iconIndex++)
                    {
                        DesktopIconPlacement icon = layout.Icons[iconIndex];
                        if (icon != null && !string.IsNullOrEmpty(icon.Identity))
                        {
                            iconIdentities.Add(DesktopIdentity.Normalize(icon.Identity));
                        }
                    }
                }
                if (storedEntry.IconCreatedUtc != null && layout.Icons != null)
                {
                    for (int timeIndex = 0; timeIndex < storedEntry.IconCreatedUtc.Count; timeIndex++)
                    {
                        DesktopLayoutIconTimeEntry time = storedEntry.IconCreatedUtc[timeIndex];
                        if (time == null || string.IsNullOrEmpty(time.Identity)
                            || time.CreatedUtcTicks <= 0 || time.CreatedUtcTicks > DateTime.MaxValue.Ticks)
                        {
                            continue;
                        }
                        string identity = DesktopIdentity.Normalize(time.Identity);
                        for (int iconIndex = 0; iconIndex < layout.Icons.Count; iconIndex++)
                        {
                            DesktopIconPlacement icon = layout.Icons[iconIndex];
                            if (icon != null && DesktopIdentity.Equals(icon.Identity, identity))
                            {
                                icon.CreatedUtc = new DateTime(time.CreatedUtcTicks, DateTimeKind.Utc);
                                break;
                            }
                        }
                    }
                }
                List<string> stored = storedEntry.Identities;
                if (stored == null)
                {
                    continue;
                }
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int identityIndex = 0; identityIndex < stored.Count; identityIndex++)
                {
                    string identity = DesktopIdentity.Normalize(stored[identityIndex]);
                    if (!string.IsNullOrEmpty(identity)
                        && iconIdentities.Contains(identity)
                        && seen.Add(identity))
                    {
                        layout.AutoMergedIdentities.Add(identity);
                    }
                }
            }
        }

        internal void Persist(DesktopLayoutCatalog catalog)
        {
            if (catalog == null)
            {
                return;
            }
            lock (syncRoot)
            {
                DesktopLayoutAutoMergeCatalog stored = new DesktopLayoutAutoMergeCatalog();
                if (catalog.Layouts != null)
                {
                    for (int index = 0; index < catalog.Layouts.Count; index++)
                    {
                        DesktopTopologyLayout layout = catalog.Layouts[index];
                        if (layout == null || string.IsNullOrWhiteSpace(layout.Id))
                        {
                            continue;
                        }
                        HashSet<string> iconIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        List<DesktopLayoutIconTimeEntry> times = new List<DesktopLayoutIconTimeEntry>();
                        if (layout.Icons != null)
                        {
                            for (int iconIndex = 0; iconIndex < layout.Icons.Count; iconIndex++)
                            {
                                DesktopIconPlacement icon = layout.Icons[iconIndex];
                                if (icon == null || string.IsNullOrEmpty(icon.Identity))
                                {
                                    continue;
                                }
                                iconIdentities.Add(DesktopIdentity.Normalize(icon.Identity));
                                if (icon.CreatedUtc != DateTime.MinValue)
                                {
                                    DesktopLayoutIconTimeEntry time = new DesktopLayoutIconTimeEntry();
                                    time.Identity = icon.Identity;
                                    time.CreatedUtcTicks = icon.CreatedUtc.ToUniversalTime().Ticks;
                                    times.Add(time);
                                }
                            }
                        }
                        List<string> identities = new List<string>();
                        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (layout.AutoMergedIdentities != null)
                        {
                            for (int identityIndex = 0; identityIndex < layout.AutoMergedIdentities.Count; identityIndex++)
                            {
                                string identity = DesktopIdentity.Normalize(layout.AutoMergedIdentities[identityIndex]);
                                if (!string.IsNullOrEmpty(identity)
                                    && iconIdentities.Contains(identity)
                                    && seen.Add(identity))
                                {
                                    identities.Add(identity);
                                }
                            }
                        }
                        if (identities.Count == 0 && times.Count == 0)
                        {
                            continue;
                        }
                        DesktopLayoutAutoMergeEntry entry = new DesktopLayoutAutoMergeEntry();
                        entry.LayoutId = layout.Id;
                        entry.Identities = identities;
                        entry.IconCreatedUtc = times;
                        stored.Entries.Add(entry);
                    }
                }
                if (stored.Entries.Count == 0)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                    catch
                    {
                    }
                    return;
                }
                string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
                try
                {
                    using (FileStream stream = new FileStream(
                        temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        serializer.WriteObject(stream, stored);
                    }
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    File.Move(temporary, path);
                }
                catch
                {
                    // 自动并集标记只是编辑页提示，写失败不能影响真正的位置保存。
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
        }

        private Dictionary<string, DesktopLayoutAutoMergeEntry> LoadCore()
        {
            Dictionary<string, DesktopLayoutAutoMergeEntry> result =
                new Dictionary<string, DesktopLayoutAutoMergeEntry>(StringComparer.OrdinalIgnoreCase);
            lock (syncRoot)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        return result;
                    }
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        DesktopLayoutAutoMergeCatalog catalog =
                            serializer.ReadObject(stream) as DesktopLayoutAutoMergeCatalog;
                        if (catalog == null || catalog.Entries == null)
                        {
                            return result;
                        }
                        for (int index = 0; index < catalog.Entries.Count; index++)
                        {
                            DesktopLayoutAutoMergeEntry entry = catalog.Entries[index];
                            if (entry != null && !string.IsNullOrWhiteSpace(entry.LayoutId))
                            {
                                result[entry.LayoutId] = entry;
                            }
                        }
                    }
                }
                catch
                {
                    // sidecar 损坏时按“没有自动并集标记”处理；位置保存不受影响。
                }
            }
            return result;
        }
    }
}
