using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Applications.Core;
using WorkspaceRestorer.Applications.Deletion;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.StartupRestore.Core;
using WorkspaceRestorer.StartupRestore.Deletion;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// Owns the application lifecycle.  Window code only observes this object
    /// and invokes commands, which keeps long-running restore work off the UI
    /// thread and makes the core easy to exercise without creating a window.
    /// </summary>
    // partial：StartupRestore/Core 里有一个薄薄的“自启动应用恢复转接”分部。
    // 代码仍归启动与恢复文件夹，Applications 只调用公开入口。
    public sealed partial class ApplicationController : INotifyPropertyChanged, IDisposable
    {
        private RecycleBinCatalog LoadRecycleBinCatalog()
        {
            RecycleBinCatalog catalog = null;
            try
            {
                catalog = recycleBinStore.Load();
            }
            catch
            {
                catalog = null;
            }
            if (catalog == null)
            {
                catalog = new RecycleBinCatalog();
            }
            if (catalog.Entries == null)
            {
                catalog.Entries = new List<RecycleBinEntry>();
            }
            // 0 是合法值：表示永久保留。不能再把用户明确保存的 0 改回 15。
            // 旧文件没有这个字段时由 RecycleBinCatalog 构造函数给出默认 15。
            // 【2026-10-19 修】这里原来直接调 RefreshRecycleBinRemainingDays()，但那时字段
            // `recycleBinCatalog` 还是 null（本方法只是“读出来”，赋值发生在调用方），
            // 于是从磁盘读回来的条目 RemainingDays 一直是 0 —— 界面上每一条都显示
            // “0 天后彻底删除”（红色，很吓人）。先把字段指过去再算。
            recycleBinCatalog = catalog;
            RefreshRecycleBinRemainingDays();
            return catalog;
        }

        private bool PersistRecycleBin()
        {
            try
            {
                recycleBinStore.Save(recycleBinCatalog);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Write("Unable to save the recycle bin.", exception);
                return false;
            }
        }

        /// <summary>
        /// 2026-10-23：Sync 发现的历史布局文件夹不能直接删；先把完整布局写进回收站，
        /// 再删文件夹。这样“点删除 / 自动清理 / 历史孤儿”都走同一条可恢复路径。
        /// </summary>
        private bool PreserveUnreferencedLayoutFolders(DesktopLayoutCatalog catalog)
        {
            if (catalog == null || recycleBinCatalog == null)
            {
                return false;
            }
            IList<DesktopLayoutOrphan> orphans = DesktopLayoutFolderStore.FindUnreferencedFolders(catalog);
            if (orphans == null || orphans.Count == 0)
            {
                return true;
            }
            if (recycleBinCatalog.Entries == null)
            {
                recycleBinCatalog.Entries = new List<RecycleBinEntry>();
            }
            int added = 0;
            for (int index = 0; index < orphans.Count; index++)
            {
                DesktopLayoutOrphan orphan = orphans[index];
                if (orphan == null || orphan.Layout == null)
                {
                    continue;
                }
                string payload = SerializeDesktopLayout(orphan.Layout);
                bool exists = false;
                for (int entryIndex = 0; entryIndex < recycleBinCatalog.Entries.Count; entryIndex++)
                {
                    RecycleBinEntry existing = recycleBinCatalog.Entries[entryIndex];
                    if (existing != null
                        && string.Equals(existing.Kind, RecycleBinKind.DesktopLayout, StringComparison.Ordinal)
                        && string.Equals(existing.Payload, payload, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    RecycleBinEntry entry = new RecycleBinEntry();
                    entry.Kind = RecycleBinKind.DesktopLayout;
                    entry.Title = string.IsNullOrWhiteSpace(orphan.Layout.Name)
                        ? AppText.T(ShellTextKeys.FallbackLayout)
                        : orphan.Layout.Name;
                    entry.Subtitle = AppText.T(ShellTextKeys.StatusRecycleLayoutFolderHistory);
                    entry.Payload = payload;
                    entry.DeletedAtUtc = DateTime.UtcNow;
                    recycleBinCatalog.Entries.Add(entry);
                    added++;
                }
            }
            if (added > 0)
            {
                if (!PersistRecycleBin())
                {
                    AppLog.Write("[图标位置保存] 历史布局回收站条目写盘失败，暂不删除文件夹。");
                    return false;
                }
                OnPropertyChanged("RecycleBinEntries");
                AppLog.Write("[图标位置保存] Sync 发现历史布局文件夹，已移入回收站 " + added + " 个。");
            }
            for (int index = 0; index < orphans.Count; index++)
            {
                DesktopLayoutOrphan orphan = orphans[index];
                if (orphan != null && !string.IsNullOrWhiteSpace(orphan.FolderPath))
                {
                    DesktopLayoutFolderStore.DeleteFolderAt(orphan.FolderPath);
                }
            }
            return true;
        }

        private void RefreshRecycleBinRemainingDays()
        {
            if (recycleBinCatalog == null || recycleBinCatalog.Entries == null)
            {
                return;
            }
            int retention = recycleBinCatalog.RetentionDays;
            if (retention <= 0)
            {
                for (int index = 0; index < recycleBinCatalog.Entries.Count; index++)
                {
                    if (recycleBinCatalog.Entries[index] != null)
                    {
                        recycleBinCatalog.Entries[index].RemainingDays = -1;
                    }
                }
                return;
            }
            for (int index = 0; index < recycleBinCatalog.Entries.Count; index++)
            {
                RecycleBinEntry entry = recycleBinCatalog.Entries[index];
                if (entry == null)
                {
                    continue;
                }
                // 落地/读回时的时间要自愈并统一成 UTC：
                //  · 旧数据或异常数据可能没有 DeletedAtUtc（默认值=0001 年）→ 视为刚删除；
                //  · DataContractJsonSerializer 读 /Date(...)/ 会给出 Local 种类，
                //    直接与 UtcNow 相减会差出一个时区，导致"0 天后彻底删除"。
                DateTime deleted = entry.DeletedAtUtc;
                if (deleted == default(DateTime) || deleted.Year < 2000)
                {
                    deleted = DateTime.UtcNow;
                    entry.DeletedAtUtc = deleted;
                }
                else if (deleted.Kind == DateTimeKind.Local)
                {
                    deleted = deleted.ToUniversalTime();
                    entry.DeletedAtUtc = deleted;
                }
                else if (deleted.Kind == DateTimeKind.Unspecified)
                {
                    deleted = DateTime.SpecifyKind(deleted, DateTimeKind.Utc);
                    entry.DeletedAtUtc = deleted;
                }
                double days = retention - (DateTime.UtcNow - deleted).TotalDays;
                entry.RemainingDays = (int)Math.Ceiling(Math.Max(0.0, days));
            }
        }

        /// <summary>把整棵工作方案子树放进一条回收站记录。</summary>
        private bool AddProfileTreeToRecycleBin(
            StartupProfile root,
            IList<StartupProfile> subtree,
            string originalParentId,
            int originalSiblingIndex,
            string originalPath)
        {
            if (root == null || subtree == null || subtree.Count == 0 || recycleBinCatalog == null)
            {
                return false;
            }
            string payload = SerializeProfileTree(subtree);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }
            int itemCount = 0;
            for (int index = 0; index < subtree.Count; index++)
            {
                if (subtree[index] != null && subtree[index].Items != null)
                {
                    itemCount += subtree[index].Items.Count;
                }
            }
            RecycleBinEntry entry = new RecycleBinEntry
            {
                DeletedAtUtc = DateTime.UtcNow,
                Kind = RecycleBinKind.StartupProfile,
                Title = root.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                Subtitle = subtree.Count > 1
                    ? AppText.F(
                        ShellTextKeys.StatusRecycleProfileSubtitleFormat,
                        subtree.Count,
                        itemCount)
                    : AppText.F(ShellTextKeys.StatusRecycleStartupListFormat, itemCount),
                IsPlanTree = true,
                PlanTreePayload = payload,
                PlanTreeRootId = root.Id,
                OriginalParentId = originalParentId,
                OriginalSiblingIndex = originalSiblingIndex,
                OriginalPath = originalPath
            };
            recycleBinCatalog.Entries.Add(entry);
            RefreshRecycleBinRemainingDays();
            if (!PersistRecycleBin())
            {
                recycleBinCatalog.Entries.Remove(entry);
                RefreshRecycleBinRemainingDays();
                return false;
            }
            OnPropertyChanged("RecycleBinEntries");
            return true;
        }

        private static string SerializeProfileTree(IList<StartupProfile> nodes)
        {
            if (nodes == null)
            {
                return null;
            }
            try
            {
                List<StartupProfile> copy = new List<StartupProfile>();
                for (int index = 0; index < nodes.Count; index++)
                {
                    if (nodes[index] != null)
                    {
                        copy.Add(nodes[index]);
                    }
                }
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<StartupProfile>));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
                {
                    serializer.WriteObject(stream, copy);
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("工作方案子树序列化失败。", exception);
                return null;
            }
        }

        private static List<StartupProfile> DeserializeProfileTree(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<StartupProfile>));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(payload)))
                {
                    return serializer.ReadObject(stream) as List<StartupProfile>;
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("工作方案子树反序列化失败。", exception);
                return null;
            }
        }

        private bool AddToRecycleBin(StartupProfile profile)
        {
            if (profile == null || recycleBinCatalog == null)
            {
                return false;
            }
            RecycleBinEntry entry = new RecycleBinEntry
            {
                Profile = profile,
                DeletedAtUtc = DateTime.UtcNow,
                Kind = RecycleBinKind.StartupProfile,
                Title = profile.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile),
                Subtitle = AppText.F(
                    ShellTextKeys.StatusRecycleStartupListFormat,
                    profile.Items == null ? 0 : profile.Items.Count)
            };
            recycleBinCatalog.Entries.Add(entry);
            RefreshRecycleBinRemainingDays();
            if (!PersistRecycleBin())
            {
                // 写盘失败就不能继续删原方案，否则重启后两边都没有了。
                recycleBinCatalog.Entries.Remove(entry);
                RefreshRecycleBinRemainingDays();
                return false;
            }
            OnPropertyChanged("RecycleBinEntries");
            return true;
        }

        /// <summary>
        /// 把一份自启动方案放进回收站（用户第 91 条“并进后来源方案进回收站”要真的成立）。
        /// 真因（2026-10-11 自检）：`DeleteApplicationPlan` 原来**只是从列表里移除**，
        /// 根本没写回收站，于是“可以在回收站里恢复”的提示是假的。
        /// </summary>
        private bool AddApplicationPlanTreeToRecycleBin(
            ApplicationPlan root,
            IList<ApplicationPlan> subtree,
            string originalParentId,
            int originalSiblingIndex,
            string originalPath)
        {
            if (root == null || subtree == null || subtree.Count == 0 || recycleBinCatalog == null)
            {
                return false;
            }
            string payload = SerializeApplicationPlanTree(subtree);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }
            int itemCount = 0;
            for (int index = 0; index < subtree.Count; index++)
            {
                if (subtree[index] != null && subtree[index].Items != null)
                {
                    itemCount += subtree[index].Items.Count;
                }
            }
            RecycleBinEntry entry = new RecycleBinEntry
            {
                DeletedAtUtc = DateTime.UtcNow,
                Kind = RecycleBinKind.Application,
                Title = root.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan),
                Subtitle = subtree.Count > 1
                    ? AppText.F(
                        ShellTextKeys.StatusRecyclePlanSubtitleFormat,
                        subtree.Count,
                        itemCount)
                    : AppText.F(ShellTextKeys.StatusRecyclePlanItemsFormat, itemCount),
                IsPlanTree = true,
                PlanTreePayload = payload,
                PlanTreeRootId = root.Id,
                OriginalParentId = originalParentId,
                OriginalSiblingIndex = originalSiblingIndex,
                OriginalPath = originalPath
            };
            recycleBinCatalog.Entries.Add(entry);
            RefreshRecycleBinRemainingDays();
            if (!PersistRecycleBin())
            {
                recycleBinCatalog.Entries.Remove(entry);
                RefreshRecycleBinRemainingDays();
                return false;
            }
            OnPropertyChanged("RecycleBinEntries");
            return true;
        }

        private static string SerializeApplicationPlanTree(IList<ApplicationPlan> nodes)
        {
            if (nodes == null)
            {
                return null;
            }
            try
            {
                List<ApplicationPlan> copy = new List<ApplicationPlan>();
                for (int index = 0; index < nodes.Count; index++)
                {
                    if (nodes[index] != null)
                    {
                        copy.Add(nodes[index]);
                    }
                }
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<ApplicationPlan>));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
                {
                    serializer.WriteObject(stream, copy);
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("自启动方案子树序列化失败。", exception);
                return null;
            }
        }

        private static List<ApplicationPlan> DeserializeApplicationPlanTree(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(List<ApplicationPlan>));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(payload)))
                {
                    return serializer.ReadObject(stream) as List<ApplicationPlan>;
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("自启动方案子树反序列化失败。", exception);
                return null;
            }
        }

        private bool AddApplicationPlanToRecycleBin(ApplicationPlan plan)
        {
            if (plan == null || recycleBinCatalog == null)
            {
                return false;
            }
            RecycleBinEntry entry = new RecycleBinEntry
            {
                DeletedAtUtc = DateTime.UtcNow,
                Kind = RecycleBinKind.Application,
                Title = plan.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan),
                Subtitle = AppText.F(
                    ShellTextKeys.StatusRecyclePlanItemsFormat,
                    plan.Items == null ? 0 : plan.Items.Count),
                // 回收站条目靠 Payload 存原始数据；自启动方案用 JSON 存一份，
                // 这样即使将来支持“从回收站还原自启动方案”也拿得到内容。
                Payload = SerializeApplicationPlan(plan)
            };
            recycleBinCatalog.Entries.Add(entry);
            RefreshRecycleBinRemainingDays();
            if (!PersistRecycleBin())
            {
                recycleBinCatalog.Entries.Remove(entry);
                RefreshRecycleBinRemainingDays();
                return false;
            }
            OnPropertyChanged("RecycleBinEntries");
            return true;
        }

        private static ApplicationPlan DeserializeApplicationPlan(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ApplicationPlan));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream(
                    System.Text.Encoding.UTF8.GetBytes(payload)))
                {
                    return serializer.ReadObject(stream) as ApplicationPlan;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把一份回收站里的自启动方案恢复回列表（2026-10-23）。</summary>
        private bool RestoreApplicationPlanTreeFromRecycle(RecycleBinEntry entry)
        {
            if (entry == null || recycleBinCatalog == null)
            {
                return false;
            }
            List<ApplicationPlan> nodes = DeserializeApplicationPlanTree(entry.PlanTreePayload);
            if (nodes == null || nodes.Count == 0)
            {
                StatusText = AppText.T(ShellTextKeys.ErrorPlanTreeCorrupt);
                return false;
            }
            ApplicationPlan root = null;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null
                    && string.Equals(nodes[index].Id, entry.PlanTreeRootId, StringComparison.OrdinalIgnoreCase))
                {
                    root = nodes[index];
                    break;
                }
            }
            if (root == null)
            {
                root = nodes[0];
            }
            if (root == null)
            {
                return false;
            }

            bool idConflict = false;
            for (int index = 0; index < nodes.Count; index++)
            {
                ApplicationPlan node = nodes[index];
                if (node == null)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(node.Id) || FindApplicationPlan(node) != null)
                {
                    idConflict = true;
                    break;
                }
            }
            if (idConflict)
            {
                Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < nodes.Count; index++)
                {
                    ApplicationPlan node = nodes[index];
                    if (node == null)
                    {
                        continue;
                    }
                    string newId = Guid.NewGuid().ToString("N");
                    if (!string.IsNullOrWhiteSpace(node.Id))
                    {
                        idMap[node.Id] = newId;
                    }
                    node.Id = newId;
                }
                for (int index = 0; index < nodes.Count; index++)
                {
                    ApplicationPlan node = nodes[index];
                    if (node == null || string.IsNullOrWhiteSpace(node.ParentId))
                    {
                        continue;
                    }
                    string mapped;
                    if (idMap.TryGetValue(node.ParentId, out mapped))
                    {
                        node.ParentId = mapped;
                    }
                }
            }

            ApplicationPlan originalParent = string.IsNullOrWhiteSpace(entry.OriginalParentId)
                ? null
                : PlanFolderTree.FindById(applicationPlans, entry.OriginalParentId);
            root.ParentId = originalParent == null ? null : originalParent.Id;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null)
                {
                    if (nodes[index].Items == null)
                    {
                        nodes[index].Items = new List<ApplicationPlanItem>();
                    }
                    applicationPlans.Add(nodes[index]);
                }
            }
            PlanFolderTree.Normalize(applicationPlans);
            if (originalParent != null)
            {
                PlanFolderTree.PlaceAtSiblingIndex(applicationPlans, root, entry.OriginalSiblingIndex);
            }
            if (applicationPlanCatalog != null)
            {
                applicationPlanCatalog.ActivePlanId = root.Id;
            }
            recycleBinCatalog.Entries.Remove(entry);
            RefreshRecycleBinRemainingDays();
            PersistApplicationPlans();
            PersistRecycleBin();
            OnPropertyChanged("ApplicationPlans");
            OnPropertyChanged("RecycleBinEntries");
            bool parentMissing = !string.IsNullOrWhiteSpace(entry.OriginalParentId) && originalParent == null;
            StatusText = parentMissing
                ? AppText.F(
                    ShellTextKeys.StatusTreeRestoredToRootFormat,
                    root.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan))
                : AppText.F(
                    ShellTextKeys.StatusPlanRestoredFromRecycleFormat,
                    root.Name ?? AppText.T(ShellTextKeys.FallbackStartupPlan));
            return true;
        }

        private bool RestoreApplicationPlanFromRecycle(RecycleBinEntry entry)
        {
            if (entry == null || recycleBinCatalog == null)
            {
                return false;
            }
            ApplicationPlan plan = DeserializeApplicationPlan(entry.Payload);
            if (plan == null)
            {
                StatusText = AppText.T(ShellTextKeys.ErrorPlanCorrupt);
                return false;
            }
            if (string.IsNullOrWhiteSpace(plan.Id)
                || FindApplicationPlan(plan) != null)
            {
                plan.Id = Guid.NewGuid().ToString("N");
            }
            if (string.IsNullOrWhiteSpace(plan.Name))
            {
                plan.Name = AppText.T(ShellTextKeys.FallbackStartupPlan);
            }
            if (plan.Items == null)
            {
                plan.Items = new List<ApplicationPlanItem>();
            }
            applicationPlans.Add(plan);
            if (applicationPlanCatalog != null)
            {
                applicationPlanCatalog.ActivePlanId = plan.Id;
            }
            recycleBinCatalog.Entries.Remove(entry);
            RefreshRecycleBinRemainingDays();
            PersistApplicationPlans();
            PersistRecycleBin();
            OnPropertyChanged("ApplicationPlans");
            OnPropertyChanged("RecycleBinEntries");
            StatusText = AppText.F(ShellTextKeys.StatusPlanRestoredFromRecycleFormat, plan.Name);
            return true;
        }

        private static string SerializeApplicationPlan(ApplicationPlan plan)
        {
            if (plan == null)
            {
                return null;
            }
            try
            {
                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ApplicationPlan));
                using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
                {
                    serializer.WriteObject(stream, plan);
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>彻底删除超过保留天数的回收站项目；返回删除的数量。</summary>
        public int PurgeExpiredRecycleBinEntries()
        {
            if (recycleBinCatalog == null || recycleBinCatalog.Entries == null)
            {
                return 0;
            }
            int retention = recycleBinCatalog.RetentionDays;
            if (retention <= 0)
            {
                // 0 = 永久保留，不做自动清理。
                return 0;
            }
            int removed = 0;
            for (int index = recycleBinCatalog.Entries.Count - 1; index >= 0; index--)
            {
                RecycleBinEntry entry = recycleBinCatalog.Entries[index];
                bool expired = entry == null
                    || (entry.DeletedAtUtc != default(DateTime) && entry.DeletedAtUtc.Year >= 2000
                        && (DateTime.UtcNow - (entry.DeletedAtUtc.Kind == DateTimeKind.Local
                            ? entry.DeletedAtUtc.ToUniversalTime()
                            : entry.DeletedAtUtc)).TotalDays >= retention);
                if (expired)
                {
                    recycleBinCatalog.Entries.RemoveAt(index);
                    removed++;
                }
            }
            if (removed > 0)
            {
                RefreshRecycleBinRemainingDays();
                PersistRecycleBin();
                OnPropertyChanged("RecycleBinEntries");
            }
            return removed;
        }

        /// <summary>设置回收站保留天数（0 表示永久保留）。</summary>
        public void UpdateRecycleBinRetention(int days)
        {
            ThrowIfDisposed();
            if (recycleBinCatalog == null)
            {
                recycleBinCatalog = new RecycleBinCatalog();
            }
            recycleBinCatalog.RetentionDays = Clamp(days, 0, 3650);
            RefreshRecycleBinRemainingDays();
            PersistRecycleBin();
            OnPropertyChanged("RecycleBinEntries");
            StatusText = recycleBinCatalog.RetentionDays <= 0
                ? AppText.T(ShellTextKeys.StatusRecycleRetentionPermanent)
                : AppText.F(
                    ShellTextKeys.StatusRecycleRetentionSetFormat,
                    recycleBinCatalog.RetentionDays);
        }

        /// <summary>把回收站里的方案恢复到工作方案列表（Id 冲突时换一个新 Id）。</summary>
        private bool RestoreDesktopLayoutFromRecycle(RecycleBinEntry entry)
        {
            if (entry == null || recycleBinCatalog == null)
            {
                return false;
            }
            DesktopTopologyLayout restored = DeserializeDesktopLayout(entry.Payload);
            if (restored == null)
            {
                StatusText = AppText.T(ShellTextKeys.ErrorLayoutCorrupt);
                return false;
            }
            DesktopLayoutCatalog catalog = LoadLayoutCatalog();
            // 尽量保留原 Id：恢复后仍然只有原来那一个文件夹，不会因为换 Id
            // 又生成一份。只有原 Id 已被占用时才换新 Id。
            string originalId = restored.Id;
            if (string.IsNullOrWhiteSpace(originalId)
                || DesktopLayoutRepository.FindById(catalog, originalId) != null)
            {
                restored.Id = Guid.NewGuid().ToString("N");
            }
            else
            {
                restored.Id = originalId;
            }
            restored.Name = UniqueLayoutName(
                catalog,
                string.IsNullOrWhiteSpace(restored.Name)
                    ? AppText.T(ShellTextKeys.FallbackLayout)
                    : restored.Name);
            int maxOrder = 0;
            for (int index = 0; index < catalog.Layouts.Count; index++)
            {
                if (catalog.Layouts[index].SortOrder > maxOrder)
                {
                    maxOrder = catalog.Layouts[index].SortOrder;
                }
            }
            restored.SortOrder = maxOrder + 1;
            catalog.Layouts.Add(restored);
            PlanFolderTree.Normalize(catalog.Layouts);
            if (!restored.IsFolder)
            {
                catalog.ActiveLayoutId = restored.Id;
                DesktopLayoutRepository.SetChoice(catalog, restored.TopologySignature, restored.Id);
            }
            SaveLayoutCatalog(catalog);
            DesktopLayoutFolderStore.Save(restored, catalog.Layouts, false, false);
            recycleBinCatalog.Entries.Remove(entry);
            RefreshRecycleBinRemainingDays();
            PersistRecycleBin();
            OnPropertyChanged("RecycleBinEntries");
            StatusText = AppText.F(ShellTextKeys.StatusLayoutRestoredFromRecycleFormat, restored.Name);
            return true;
        }

        /// <summary>
        /// 从回收站恢复一整棵工作方案子树。
        ///
        /// 关键规则：原父方案不存在（删了/永久删了/还在回收站）就放根目录，
        /// 绝不为了恢复子方案而自动重建大方案。
        /// </summary>
        private bool RestoreProfileTreeFromRecycle(RecycleBinEntry entry)
        {
            if (entry == null || recycleBinCatalog == null)
            {
                return false;
            }
            List<StartupProfile> nodes = DeserializeProfileTree(entry.PlanTreePayload);
            if (nodes == null || nodes.Count == 0)
            {
                StatusText = AppText.T(ShellTextKeys.ErrorProfileTreeCorrupt);
                return false;
            }
            StartupProfile root = null;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null
                    && string.Equals(nodes[index].Id, entry.PlanTreeRootId, StringComparison.OrdinalIgnoreCase))
                {
                    root = nodes[index];
                    break;
                }
            }
            if (root == null)
            {
                root = nodes[0];
            }
            if (root == null)
            {
                return false;
            }

            bool idConflict = false;
            for (int index = 0; index < nodes.Count; index++)
            {
                StartupProfile node = nodes[index];
                if (node == null)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(node.Id) || FindProfile(node) != null)
                {
                    idConflict = true;
                    break;
                }
            }
            if (idConflict)
            {
                Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < nodes.Count; index++)
                {
                    StartupProfile node = nodes[index];
                    if (node == null)
                    {
                        continue;
                    }
                    string newId = Guid.NewGuid().ToString("N");
                    if (!string.IsNullOrWhiteSpace(node.Id))
                    {
                        idMap[node.Id] = newId;
                    }
                    node.Id = newId;
                }
                for (int index = 0; index < nodes.Count; index++)
                {
                    StartupProfile node = nodes[index];
                    if (node == null || string.IsNullOrWhiteSpace(node.ParentId))
                    {
                        continue;
                    }
                    string mapped;
                    if (idMap.TryGetValue(node.ParentId, out mapped))
                    {
                        node.ParentId = mapped;
                    }
                }
            }

            StartupProfile originalParent = FindProfileParentOrNull(entry.OriginalParentId);
            root.ParentId = originalParent == null ? null : originalParent.Id;
            for (int index = 0; index < nodes.Count; index++)
            {
                if (nodes[index] != null)
                {
                    startupProfiles.Add(nodes[index]);
                }
            }
            PlanFolderTree.Normalize(startupProfiles);
            if (originalParent != null)
            {
                PlanFolderTree.PlaceAtSiblingIndex(startupProfiles, root, entry.OriginalSiblingIndex);
            }

            profileCatalog.ActiveProfileId = root.Id;
            RememberActiveProfileId();
            LastSnapshot = root.ToSnapshot();
            SaveLastSnapshot(LastSnapshot);
            ProgressMaximum = LastSnapshot.Items == null ? 0 : LastSnapshot.Items.Count;
            recycleBinCatalog.Entries.Remove(entry);
            RefreshRecycleBinRemainingDays();
            PersistProfiles();
            PersistRecycleBin();
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            OnPropertyChanged("ActiveProfile");
            OnPropertyChanged("HasSnapshot");
            OnPropertyChanged("RecycleBinEntries");
            bool parentMissing = !string.IsNullOrWhiteSpace(entry.OriginalParentId) && originalParent == null;
            StatusText = parentMissing
                ? AppText.F(
                    ShellTextKeys.StatusTreeRestoredToRootFormat,
                    root.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile))
                : AppText.F(
                    ShellTextKeys.StatusRestoredFromRecycleFormat,
                    root.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
            return true;
        }

        public bool RestoreRecycleBinEntry(RecycleBinEntry entry)
        {
            ThrowIfDisposed();
            if (entry != null && entry.IsPlanTree
                && string.Equals(entry.Kind, RecycleBinKind.StartupProfile, StringComparison.Ordinal))
            {
                return RestoreProfileTreeFromRecycle(entry);
            }
            if (entry != null && entry.IsPlanTree
                && string.Equals(entry.Kind, RecycleBinKind.Application, StringComparison.Ordinal))
            {
                return RestoreApplicationPlanTreeFromRecycle(entry);
            }
            if (entry != null && string.Equals(entry.Kind, RecycleBinKind.Application, StringComparison.Ordinal))
            {
                return RestoreApplicationPlanFromRecycle(entry);
            }
            if (entry != null && string.Equals(entry.Kind, RecycleBinKind.DesktopLayout, StringComparison.Ordinal))
            {
                return RestoreDesktopLayoutFromRecycle(entry);
            }
            if (entry == null || entry.Profile == null || recycleBinCatalog == null)
            {
                return false;
            }
            StartupProfile profile = entry.Profile;
            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            while (startupProfiles.FirstOrDefault(delegate(StartupProfile candidate)
            {
                return candidate != null
                    && string.Equals(candidate.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
            }) != null)
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }
            startupProfiles.Add(profile);
            recycleBinCatalog.Entries.Remove(entry);
            RefreshRecycleBinRemainingDays();
            PersistProfiles();
            PersistRecycleBin();
            OnPropertyChanged("RecycleBinEntries");
            OnPropertyChanged("Profiles");
            OnPropertyChanged("StartupProfiles");
            StatusText = AppText.F(
                ShellTextKeys.StatusRestoredFromRecycleFormat,
                profile.Name ?? AppText.T(ShellTextKeys.FallbackWorkProfile));
            return true;
        }

        /// <summary>彻底删除回收站里的方案（不可恢复）。</summary>
        public int DeleteRecycleBinEntriesForever(IList<RecycleBinEntry> entries)
        {
            ThrowIfDisposed();
            if (entries == null || recycleBinCatalog == null)
            {
                return 0;
            }
            int removed = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index] != null && recycleBinCatalog.Entries.Remove(entries[index]))
                {
                    removed++;
                }
            }
            if (removed > 0)
            {
                RefreshRecycleBinRemainingDays();
                PersistRecycleBin();
                OnPropertyChanged("RecycleBinEntries");
                StatusText = AppText.F(ShellTextKeys.StatusRecycleDeletedFormat, removed);
            }
            return removed;
        }

        /// <summary>
        /// 把被“彻底删除”的回收站条目原样放回去（**撤销“彻底删除”专用**，2026-10-17）。
        /// 条目对象里还留着它自己的原始数据（Profile / Payload），所以放回去就是恢复，
        /// 方案文件夹由 <see cref="PersistRecycleBin"/> 按条目重新镜像出来。
        /// </summary>
        public int RestoreRecycleBinEntries(IList<RecycleBinEntry> entries)
        {
            ThrowIfDisposed();
            if (entries == null || recycleBinCatalog == null)
            {
                return 0;
            }
            if (recycleBinCatalog.Entries == null)
            {
                recycleBinCatalog.Entries = new List<RecycleBinEntry>();
            }
            int restored = 0;
            for (int index = 0; index < entries.Count; index++)
            {
                RecycleBinEntry entry = entries[index];
                if (entry == null || recycleBinCatalog.Entries.Contains(entry))
                {
                    continue;
                }
                recycleBinCatalog.Entries.Add(entry);
                restored++;
            }
            if (restored > 0)
            {
                RefreshRecycleBinRemainingDays();
                PersistRecycleBin();
                OnPropertyChanged("RecycleBinEntries");
                AppLog.Write("[回收站] 撤销彻底删除：放回 " + restored + " 条");
            }
            return restored;
        }

        /// <summary>
        /// 把一条回收站记录**直接拿掉**（不写“已删除”结论）。
        /// 用在“撤销删除方案”上：那次删除产生的回收站记录要一并撤掉，
        /// 否则方案回到了列表里，回收站里还留着一份一模一样的副本。
        /// </summary>
        public bool DropRecycleBinEntry(RecycleBinEntry entry)
        {
            ThrowIfDisposed();
            if (entry == null || recycleBinCatalog == null || recycleBinCatalog.Entries == null)
            {
                return false;
            }
            if (!recycleBinCatalog.Entries.Remove(entry))
            {
                return false;
            }
            RefreshRecycleBinRemainingDays();
            PersistRecycleBin();
            OnPropertyChanged("RecycleBinEntries");
            return true;
        }

        /// <summary>清空回收站（全部彻底删除）。</summary>
        public int EmptyRecycleBin()
        {
            ThrowIfDisposed();
            if (recycleBinCatalog == null || recycleBinCatalog.Entries == null)
            {
                return 0;
            }
            int removed = recycleBinCatalog.Entries.Count;
            recycleBinCatalog.Entries.Clear();
            PersistRecycleBin();
            OnPropertyChanged("RecycleBinEntries");
            StatusText = removed == 0
                ? AppText.T(ShellTextKeys.StatusRecycleEmpty)
                : AppText.F(ShellTextKeys.StatusRecycleEmptiedFormat, removed);
            return removed;
        }

    }
}
