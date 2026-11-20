using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 编辑页的纯 UI 状态（目前只有“新图标”标签位置），单独放一个 sidecar 文件。
    /// 不塞进 DesktopTopologyLayout：那个 DataContract 一旦加必填成员，旧布局 XML 就会读不出来。
    /// </summary>
    [DataContract]
    public sealed class DesktopLayoutEditorState
    {
        [DataMember(Order = 1)]
        public string LayoutId { get; set; }

        [DataMember(Order = 2)]
        public double X { get; set; }

        [DataMember(Order = 3)]
        public double Y { get; set; }
    }

    [DataContract]
    public sealed class DesktopLayoutEditorStateCatalog
    {
        public DesktopLayoutEditorStateCatalog()
        {
            Entries = new List<DesktopLayoutEditorState>();
        }

        [DataMember(Order = 1)]
        public List<DesktopLayoutEditorState> Entries { get; set; }
    }

    public sealed class DesktopLayoutEditorStateStore
    {
        private readonly object syncRoot = new object();
        private readonly string path;
        private readonly DataContractSerializer serializer;

        public DesktopLayoutEditorStateStore()
            : this(DefaultPath())
        {
        }

        public DesktopLayoutEditorStateStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An editor state path is required.", "path");
            }
            this.path = Path.GetFullPath(path);
            serializer = new DataContractSerializer(typeof(DesktopLayoutEditorStateCatalog));
        }

        private static string DefaultPath()
        {
            string catalog = AppPaths.DesktopLayoutFile;
            string folder = Path.GetDirectoryName(catalog);
            return Path.Combine(string.IsNullOrEmpty(folder) ? AppPaths.DataRoot : folder, "编辑器状态.xml");
        }

        public bool TryGet(string layoutId, out double x, out double y)
        {
            x = 0.0;
            y = 0.0;
            if (string.IsNullOrWhiteSpace(layoutId))
            {
                return false;
            }
            lock (syncRoot)
            {
                DesktopLayoutEditorStateCatalog catalog = LoadCore();
                for (int index = 0; index < catalog.Entries.Count; index++)
                {
                    DesktopLayoutEditorState entry = catalog.Entries[index];
                    if (entry != null && string.Equals(entry.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase))
                    {
                        x = entry.X;
                        y = entry.Y;
                        return true;
                    }
                }
            }
            return false;
        }

        public void Save(string layoutId, double x, double y)
        {
            if (string.IsNullOrWhiteSpace(layoutId))
            {
                return;
            }
            lock (syncRoot)
            {
                DesktopLayoutEditorStateCatalog catalog = LoadCore();
                DesktopLayoutEditorState target = null;
                for (int index = 0; index < catalog.Entries.Count; index++)
                {
                    DesktopLayoutEditorState entry = catalog.Entries[index];
                    if (entry != null && string.Equals(entry.LayoutId, layoutId, StringComparison.OrdinalIgnoreCase))
                    {
                        target = entry;
                        break;
                    }
                }
                if (target == null)
                {
                    target = new DesktopLayoutEditorState { LayoutId = layoutId };
                    catalog.Entries.Add(target);
                }
                target.X = Clamp01(x);
                target.Y = Clamp01(y);
                SaveCore(catalog);
            }
        }

        private DesktopLayoutEditorStateCatalog LoadCore()
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new DesktopLayoutEditorStateCatalog();
                }
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    DesktopLayoutEditorStateCatalog catalog =
                        serializer.ReadObject(stream) as DesktopLayoutEditorStateCatalog;
                    if (catalog == null)
                    {
                        return new DesktopLayoutEditorStateCatalog();
                    }
                    if (catalog.Entries == null)
                    {
                        catalog.Entries = new List<DesktopLayoutEditorState>();
                    }
                    return catalog;
                }
            }
            catch
            {
                // UI 位置坏了不该影响编辑功能；退回默认位置。
                return new DesktopLayoutEditorStateCatalog();
            }
        }

        private void SaveCore(DesktopLayoutEditorStateCatalog catalog)
        {
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, catalog);
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporary, path);
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

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0.0;
            }
            return Math.Max(0.0, Math.Min(1.0, value));
        }
    }
}
