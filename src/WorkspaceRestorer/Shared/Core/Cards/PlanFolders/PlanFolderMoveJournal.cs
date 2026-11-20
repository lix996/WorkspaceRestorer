using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Core.Cards.PlanFolders
{
    /// <summary>
    /// 一次“文件夹式移动”的日志。只记结构，不记启动项。
    ///
    /// 目录已经移动、总 JSON 还没写完时程序被关掉，下次启动先按这份日志修正父子关系，
    /// 再把总 JSON 补上。日志写成功后立即删除。
    /// </summary>
    [Serializable]
    public sealed class PlanFolderMoveJournalEntry
    {
        public string PlanId { get; set; }

        public string OldParentId { get; set; }

        public string NewParentId { get; set; }

        public string OldDirectory { get; set; }

        public string NewDirectory { get; set; }

        public int OldSiblingIndex { get; set; }

        public int NewSiblingIndex { get; set; }

        public DateTime RecordedUtc { get; set; }
    }

    [Serializable]
    public sealed class PlanFolderMoveJournalData
    {
        public PlanFolderMoveJournalData()
        {
            FormatVersion = PlanFolderMoveJournal.CurrentFormatVersion;
            Entries = new List<PlanFolderMoveJournalEntry>();
        }

        public int FormatVersion { get; set; }

        public List<PlanFolderMoveJournalEntry> Entries { get; set; }
    }

    /// <summary>
    /// 【公共区域 · 方案文件夹】移动日志的读写。
    ///
    /// 日志文件放在各工作区自己的根目录，例如：
    ///   workflows\启动与恢复\方案移动日志.json
    ///   workflows\自启动应用\方案移动日志.json
    ///   workflows\图标位置保存\布局移动日志.json
    /// </summary>
    public static class PlanFolderMoveJournal
    {
        public const int CurrentFormatVersion = 1;
        public const string FileName = "方案移动日志.json";

        public static string PathFor(string workspaceRoot, string fileName)
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return null;
            }
            return Path.Combine(workspaceRoot, string.IsNullOrWhiteSpace(fileName) ? FileName : fileName);
        }

        public static PlanFolderMoveJournalData Load(string path)
        {
            PlanFolderMoveJournalData journal = new PlanFolderMoveJournalData();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return journal;
            }
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(PlanFolderMoveJournalData));
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    PlanFolderMoveJournalData loaded = serializer.ReadObject(stream) as PlanFolderMoveJournalData;
                    if (loaded != null)
                    {
                        journal = loaded;
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[方案移动] 读取移动日志失败，按无日志继续：" + path, exception);
            }
            if (journal.Entries == null)
            {
                journal.Entries = new List<PlanFolderMoveJournalEntry>();
            }
            return journal;
        }

        public static void Append(string path, PlanFolderMoveJournalEntry entry)
        {
            if (string.IsNullOrWhiteSpace(path) || entry == null)
            {
                return;
            }
            PlanFolderMoveJournalData journal = Load(path);
            journal.FormatVersion = CurrentFormatVersion;
            journal.Entries.Add(entry);
            Save(path, journal);
        }

        public static void Save(string path, PlanFolderMoveJournalData journal)
        {
            if (string.IsNullOrWhiteSpace(path) || journal == null)
            {
                return;
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(PlanFolderMoveJournalData));
                using (FileStream stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    serializer.WriteObject(stream, journal);
                }
                if (File.Exists(path))
                {
                    string backup = path + ".bak";
                    File.Replace(temporary, path, backup, true);
                    try
                    {
                        if (File.Exists(backup))
                        {
                            File.Delete(backup);
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[方案移动] 写移动日志失败：" + path, exception);
                throw;
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

        public static void Clear(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("[方案移动] 删除已完成的移动日志失败：" + path, exception);
            }
        }

        /// <summary>
        /// 按日志把内存里的父子关系补回“移动后的样子”。
        /// 日志里记的是“已经移动过的结果”，所以只按 NewParentId / NewSiblingIndex 放。
        /// 新父项已经不在了就退回根级，绝不自动重建父项。
        /// </summary>
        public static int Apply<T>(
            IList<T> nodes,
            IList<PlanFolderMoveJournalEntry> entries,
            Func<T, string> idOf) where T : class, IPlanFolderNode
        {
            if (nodes == null || entries == null || entries.Count == 0 || idOf == null)
            {
                return 0;
            }
            int applied = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                PlanFolderMoveJournalEntry entry = entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.PlanId))
                {
                    continue;
                }
                T node = null;
                for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
                {
                    T candidate = nodes[nodeIndex];
                    if (candidate != null
                        && string.Equals(idOf(candidate), entry.PlanId, StringComparison.OrdinalIgnoreCase))
                    {
                        node = candidate;
                        break;
                    }
                }
                if (node == null)
                {
                    continue;
                }
                T parent = null;
                if (!string.IsNullOrWhiteSpace(entry.NewParentId))
                {
                    for (int parentIndex = 0; parentIndex < nodes.Count; parentIndex++)
                    {
                        T candidate = nodes[parentIndex];
                        if (candidate != null
                            && string.Equals(idOf(candidate), entry.NewParentId, StringComparison.OrdinalIgnoreCase))
                        {
                            parent = candidate;
                            break;
                        }
                    }
                }
                if (!PlanFolderTree.MoveInto(nodes, node, parent))
                {
                    continue;
                }
                if (entry.NewSiblingIndex >= 0)
                {
                    PlanFolderTree.PlaceAtSiblingIndex(nodes, node, entry.NewSiblingIndex);
                }
                applied++;
            }
            PlanFolderTree.Normalize(nodes);
            return applied;
        }
    }
}
