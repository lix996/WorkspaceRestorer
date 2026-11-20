using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.RecycleBin.Core
{
    /// <summary>
    /// 回收站的**文件夹式**存取（用户要求 2026-10-08：回收站的数据也放进 workflows/回收站，
    /// 并且按三个工作区分段，这样可以直接删掉对应文件夹来手动清数据）。
    ///
    /// 落盘结构（&lt;数据根&gt; 见 AppPaths）：
    /// <code>
    /// 回收站\回收站设置.json                 保留天数
    /// 回收站\启动与恢复\&lt;条目名&gt;\条目.json    一条被删内容一个文件夹
    /// 回收站\图标位置保存\...
    /// 回收站\自启动应用\...
    /// </code>
    /// 旧版只有一个 `recycle-bin.json`：第一次读的时候自动拆成上面的结构，旧文件改名留档。
    /// （公开类型：测试需要直接验证“分段 + 一条一个文件夹”的落盘结构。）
    /// </summary>
    public sealed class RecycleBinFolderStore
    {
        private const string EntryFileName = "条目.json";

        private readonly object syncRoot = new object();

        /// <summary>
        /// 读出回收站。会依次尝试：新的文件夹结构 → 旧的单个 json（读成功后自动搬家）。
        /// 永远不返回 null。
        /// </summary>
        public RecycleBinCatalog Load()
        {
            lock (syncRoot)
            {
                RecycleBinCatalog catalog = LoadFromFolders();
                if (catalog != null && catalog.Entries.Count > 0)
                {
                    return catalog;
                }
                RecycleBinCatalog legacy = LoadFromLegacyFile();
                if (legacy != null)
                {
                    try
                    {
                        // 把旧数据立刻落成新的文件夹结构，并给旧文件改名留档。
                        Save(legacy);
                        RetireLegacyFile();
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("回收站搬家失败（内存里仍然可用）。", exception);
                    }
                    return legacy;
                }
                return catalog ?? new RecycleBinCatalog();
            }
        }

        /// <summary>把回收站整体写回磁盘：设置写一个 json，每条内容一个文件夹。</summary>
        public void Save(RecycleBinCatalog catalog)
        {
            if (catalog == null)
            {
                return;
            }
            lock (syncRoot)
            {
                AppPaths.EnsureDataDirectories();
                WriteRetention(catalog);

                HashSet<string> wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (catalog.Entries != null)
                {
                    for (int index = 0; index < catalog.Entries.Count; index++)
                    {
                        RecycleBinEntry entry = catalog.Entries[index];
                        if (entry == null)
                        {
                            continue;
                        }
                        string folderName = BuildEntryFolderName(entry, index);
                        wanted.Add(folderName);
                        WriteEntry(AppPaths.RecycleBinKindDirectory(entry.Kind), folderName, entry);
                    }
                }

                // 清掉已经不在清单里的条目文件夹（恢复/彻底删除之后）。
                RemoveObsoleteEntryFolders(wanted);
            }
        }

        private static void WriteRetention(RecycleBinCatalog catalog)
        {
            try
            {
                RetentionDocument document = new RetentionDocument();
                document.RetentionDays = catalog.RetentionDays;
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RetentionDocument));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, document);
                    WriteAtomic(AppPaths.RecycleBinFile, Encoding.UTF8.GetString(stream.ToArray()));
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("回收站保留天数写入失败。", exception);
            }
        }

        private static void WriteEntry(string kindDirectory, string folderName, RecycleBinEntry entry)
        {
            try
            {
                string directory = Path.Combine(kindDirectory, folderName);
                Directory.CreateDirectory(directory);
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RecycleBinEntry));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, entry);
                    WriteAtomic(Path.Combine(directory, EntryFileName), Encoding.UTF8.GetString(stream.ToArray()));
                }
                WriteEntryNote(directory, entry);
            }
            catch (Exception exception)
            {
                AppLog.Write("回收站条目写入失败：" + folderName, exception);
            }
        }

        private static void WriteEntryNote(string directory, RecycleBinEntry entry)
        {
            try
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("这是回收站里的一条内容，可以恢复也可以彻底删除（也可以直接删掉整个文件夹）。");
                builder.AppendLine("所属工作区：" + (string.IsNullOrWhiteSpace(entry.Kind) ? "未分类" : entry.Kind));
                builder.AppendLine("名称：" + (entry.Title ?? (entry.Profile != null ? entry.Profile.Name : "未命名")));
                if (!string.IsNullOrWhiteSpace(entry.Subtitle))
                {
                    builder.AppendLine("说明：" + entry.Subtitle);
                }
                builder.AppendLine("删除时间：" + WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(entry.DeletedAtUtc));
                builder.AppendLine("原始数据在 条目.json 里；删掉本文件夹等于把这条内容彻底删除。");
                string path = Path.Combine(directory, "说明.txt");
                string content = builder.ToString();
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                {
                    return;
                }
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }
            catch
            {
            }
        }

        private static void RemoveObsoleteEntryFolders(HashSet<string> wanted)
        {
            string[] kinds = new string[]
            {
                RecycleBinKind.StartupProfile,
                RecycleBinKind.DesktopLayout,
                RecycleBinKind.Application
            };
            for (int index = 0; index < kinds.Length; index++)
            {
                string root = AppPaths.RecycleBinKindDirectory(kinds[index]);
                if (!Directory.Exists(root))
                {
                    continue;
                }
                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(root);
                }
                catch
                {
                    continue;
                }
                for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
                {
                    string name = Path.GetFileName(directories[directoryIndex]);
                    if (wanted.Contains(name))
                    {
                        continue;
                    }
                    try
                    {
                        Directory.Delete(directories[directoryIndex], true);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static string BuildEntryFolderName(RecycleBinEntry entry, int index)
        {
            string title = entry.Title;
            if (string.IsNullOrWhiteSpace(title) && entry.Profile != null)
            {
                title = entry.Profile.Name;
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "未命名";
            }
            StringBuilder builder = new StringBuilder(title.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int characterIndex = 0; characterIndex < title.Length; characterIndex++)
            {
                char character = title[characterIndex];
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
            string safe = builder.ToString().Trim();
            if (safe.Length == 0)
            {
                safe = "未命名";
            }
            if (safe.Length > 60)
            {
                safe = safe.Substring(0, 60);
            }
            return safe + "_" + (index + 1).ToString("d2");
        }

        private static RecycleBinCatalog LoadFromFolders()
        {
            RecycleBinCatalog catalog = new RecycleBinCatalog();
            catalog.Entries = new List<RecycleBinEntry>();
            catalog.RetentionDays = ReadRetention();
            string[] kinds = new string[]
            {
                RecycleBinKind.StartupProfile,
                RecycleBinKind.DesktopLayout,
                RecycleBinKind.Application
            };
            for (int index = 0; index < kinds.Length; index++)
            {
                string root = AppPaths.RecycleBinKindDirectory(kinds[index]);
                if (!Directory.Exists(root))
                {
                    continue;
                }
                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(root);
                }
                catch
                {
                    continue;
                }
                Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
                for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
                {
                    RecycleBinEntry entry = ReadEntry(Path.Combine(directories[directoryIndex], EntryFileName));
                    if (entry == null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(entry.Kind))
                    {
                        entry.Kind = kinds[index];
                    }
                    catalog.Entries.Add(entry);
                }
            }
            return catalog;
        }

        private static RecycleBinEntry ReadEntry(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RecycleBinEntry));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    return serializer.ReadObject(stream) as RecycleBinEntry;
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("回收站条目读取失败：" + path, exception);
                return null;
            }
        }

        private static int ReadRetention()
        {
            try
            {
                if (!File.Exists(AppPaths.RecycleBinFile))
                {
                    return 30;
                }
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RetentionDocument));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(AppPaths.RecycleBinFile))))
                {
                    RetentionDocument document = serializer.ReadObject(stream) as RetentionDocument;
                    if (document != null && document.RetentionDays >= 0)
                    {
                        return document.RetentionDays;
                    }
                }
            }
            catch
            {
            }
            return 30;
        }

        private static RecycleBinCatalog LoadFromLegacyFile()
        {
            try
            {
                string path = AppPaths.LegacyRecycleBinFile;
                if (!File.Exists(path))
                {
                    return null;
                }
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RecycleBinCatalog));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path))))
                {
                    RecycleBinCatalog catalog = serializer.ReadObject(stream) as RecycleBinCatalog;
                    if (catalog != null && catalog.Entries == null)
                    {
                        catalog.Entries = new List<RecycleBinEntry>();
                    }
                    return catalog;
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("旧的 recycle-bin.json 读取失败。", exception);
                return null;
            }
        }

        private static void RetireLegacyFile()
        {
            try
            {
                string path = AppPaths.LegacyRecycleBinFile;
                if (!File.Exists(path))
                {
                    return;
                }
                string target = Path.Combine(AppPaths.LegacyBackupDirectory, "recycle-bin.json");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
                File.Move(path, target);
            }
            catch
            {
            }
        }

        private static void WriteAtomic(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }

        [DataContract]
        private sealed class RetentionDocument
        {
            public RetentionDocument()
            {
                RetentionDays = 15;
            }

            [DataMember(Order = 1)]
            public int RetentionDays { get; set; }
        }
    }
}
