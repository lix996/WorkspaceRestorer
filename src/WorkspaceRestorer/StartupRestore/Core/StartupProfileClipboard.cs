using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 工作方案的"复制 / 粘贴"（用户要求 2026-10-08：工作方案可以 Ctrl+C、Ctrl+V）。
    ///
    /// 只做两件事：把一份方案序列化成一段自包含的 JSON 文本、再还原成一份**全新 Id** 的方案。
    /// 之所以走 JSON 而不是直接复制对象引用：粘贴出来的方案必须是独立的一份，
    /// 改名、增删条目都不能影响被复制的那份（用对象引用会连在一起）。
    ///
    /// 数据格式与 startup-profiles.json 里的单个方案一致，所以用同一套 DataContract，
    /// 旧版本也能读。
    /// </summary>
    public static class StartupProfileClipboard
    {
        /// <summary>
        /// 剪贴板内容的“出身标记”（用户要求 2026-10-10 18:20）：
        /// “一些快捷键，例如复制粘贴，最好是相同的能够复制粘贴，防止代码出现问题，
        ///  例如后续的图标位置保存，不小心把关于图标位置的文件夹复制到启动与恢复，这样会出现什么莫名的效果。”
        ///
        /// 所以复制时在 JSON 前面加一行标记：`CSK1|启动与恢复`。
        /// 粘贴时先看这行标记 —— 不是本工作区的东西就**明确拒绝并说明**，
        /// 而不是硬解析成一份空壳方案（那正是用户担心的“莫名的效果”）。
        /// </summary>
        public const string ClipboardHeaderPrefix = "CSK1|";

        /// <summary>本组件负责的工作区名（写在标记里）。</summary>
        public const string WorkspaceName = "启动与恢复";

        /// <summary>把一份方案序列化成文本（失败返回 null，不抛异常）。</summary>
        public static string Serialize(StartupProfile profile)
        {
            if (profile == null)
            {
                return null;
            }
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(StartupProfile));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, profile);
                    return ClipboardHeaderPrefix + WorkspaceName + "\n" + Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 这段剪贴板文本是从别的**工作区**复制来的吗？
        /// 返回那个工作区的名字；不是别的（本工作区、或没有标记）就返回 null。
        /// </summary>
        public static string ForeignWorkspaceOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(ClipboardHeaderPrefix, StringComparison.Ordinal))
            {
                return null;   // 没有标记：可能是手工贴的 JSON，交给原来的解析逻辑
            }
            int end = trimmed.IndexOf('\n');
            if (end < 0)
            {
                return null;
            }
            string name = trimmed.Substring(ClipboardHeaderPrefix.Length, end - ClipboardHeaderPrefix.Length).Trim();
            if (name.Length == 0 || string.Equals(name, WorkspaceName, StringComparison.Ordinal))
            {
                return null;
            }
            return name;
        }

        /// <summary>去掉剪贴板标记，只留 JSON（没有标记就原样返回）。</summary>
        public static string StripHeader(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(ClipboardHeaderPrefix, StringComparison.Ordinal))
            {
                return text;
            }
            int end = trimmed.IndexOf('\n');
            return end < 0 ? text : trimmed.Substring(end + 1);
        }

        /// <summary>
        /// 把文本还原成一份新方案：**新的 Id、新的条目 Id**，
        /// 名字用调用方给的名字（为空时用"原名 副本"）。解析失败返回 null。
        /// </summary>
        /// <param name="keepSourceName">
        /// true＝跨工作区复制时用：名字沿用原件（不加“ 副本”），重名由调用方顺延；
        /// false＝同一个工作区里 Ctrl+V 复制出副本：名字加“ 副本”。
        /// </param>
        public static StartupProfile DeserializeAsCopy(string text, string newName, bool keepSourceName = false)
        {
            text = StripHeader(text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            StartupProfile parsed = null;
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(StartupProfile));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    parsed = serializer.ReadObject(stream) as StartupProfile;
                }
            }
            catch (Exception)
            {
                return null;
            }
            if (parsed == null)
            {
                return null;
            }

            // 兜底（用户第 66 条：“不小心把关于图标位置的文件夹复制到启动与恢复，
            // 这样会出现什么莫名的效果，这种都要考虑”）：
            // 即使某段 JSON 碰巧能被解析成 StartupProfile，只要它“什么都不像”——
            // 没有名字、没有 Id、也没有任何项目 —— 就当它不是工作方案，直接拒绝，
            // 免得粘出一份空壳方案。
            bool hasIdentity = !string.IsNullOrWhiteSpace(parsed.Id)
                || !string.IsNullOrWhiteSpace(parsed.Name);
            bool hasItems = parsed.Items != null && parsed.Items.Count > 0;
            if (!hasIdentity && !hasItems)
            {
                return null;
            }

            string sourceName = parsed.Name;
            StartupProfile copy = new StartupProfile();
            if (string.IsNullOrWhiteSpace(newName) && keepSourceName)
            {
                copy.Name = string.IsNullOrWhiteSpace(sourceName)
                    ? AppText.T(CommonTextKeys.WorkProfileFallback)
                    : sourceName.Trim();
            }
            else
            {
                copy.Name = string.IsNullOrWhiteSpace(newName)
                    ? (string.IsNullOrWhiteSpace(sourceName)
                        ? AppText.T(CommonTextKeys.WorkProfileFallback) + AppText.T(StartupRestoreTextKeys.FallbackCopySuffix)
                        : sourceName + AppText.T(StartupRestoreTextKeys.FallbackCopySuffix))
                    : newName.Trim();
            }

            copy.CreatedAtUtc = DateTime.UtcNow;
            copy.UpdatedAtUtc = copy.CreatedAtUtc;
            copy.Items = new List<SessionItem>();
            if (parsed.Items != null)
            {
                // 【2026-10-18 用户报“复制不全：某个浏览器里没有网址，只剩一个空浏览器”】
                // 真因：`CloneItem` 给每个条目换了新 Id，但 `ParentId` 原样搬过去 ——
                // 粘出来的网页指向的是**原件**那个父项 Id（副本里根本不存在这个 Id），
                // 于是那一组浏览器/资源管理器下的网页全部“找不到父亲”，看起来就是一个空壳。
                // 现在分两趟：先给每个条目发新 Id 并记住 旧→新 的对照，再把 ParentId 换成新 Id；
                // 对照里没有的（父项被删了/跨方案搬来的脏数据）就清空，绝不留悬空引用。
                Dictionary<string, string> idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<SessionItem> clones = new List<SessionItem>();
                for (int index = 0; index < parsed.Items.Count; index++)
                {
                    SessionItem item = CloneItem(parsed.Items[index]);
                    if (item != null)
                    {
                        clones.Add(item);
                        string oldId = parsed.Items[index].Id;
                        if (!string.IsNullOrWhiteSpace(oldId))
                        {
                            idMap[oldId] = item.Id;
                        }
                    }
                }
                for (int index = 0; index < clones.Count; index++)
                {
                    SessionItem clone = clones[index];
                    if (!string.IsNullOrWhiteSpace(clone.ParentId))
                    {
                        string mapped;
                        clone.ParentId = idMap.TryGetValue(clone.ParentId, out mapped) ? mapped : null;
                    }
                    copy.Items.Add(clone);
                }
            }
            if (parsed.ExcludedItemKeys != null)
            {
                copy.ExcludedItemKeys = new List<string>(parsed.ExcludedItemKeys);
            }
            copy.Resequence();
            return copy;
        }

        /// <summary>复制一个条目：新 Id，其他字段原样带过去（含网页的 URL 与父项关系）。</summary>
        private static SessionItem CloneItem(SessionItem source)
        {
            if (source == null)
            {
                return null;
            }
            SessionItem clone = new SessionItem();
            clone.Id = Guid.NewGuid().ToString("N");
            clone.Kind = source.Kind;
            clone.Sequence = source.Sequence;
            clone.IsEnabled = source.IsEnabled;
            clone.StartTimeUtc = source.StartTimeUtc == default(DateTime) ? DateTime.UtcNow : source.StartTimeUtc;
            clone.DisplayName = source.DisplayName;
            clone.WindowTitle = source.WindowTitle;
            clone.ExecutablePath = source.ExecutablePath;
            clone.Arguments = source.Arguments;
            clone.WorkingDirectory = source.WorkingDirectory;
            clone.TargetPath = source.TargetPath;
            clone.CapturedProcessId = 0;
            clone.CaptureSource = source.CaptureSource;
            clone.Url = source.Url;
            clone.BrowserName = source.BrowserName;
            clone.BrowserExecutablePath = source.BrowserExecutablePath;
            clone.ParentId = source.ParentId;
            clone.BrowserGroupKey = source.BrowserGroupKey;
            clone.BrowserWindowIndex = source.BrowserWindowIndex;
            clone.BrowserTabIndex = source.BrowserTabIndex;
            clone.IsActiveBrowserTab = source.IsActiveBrowserTab;
            clone.BrowserTabIdentityCaptured = source.BrowserTabIdentityCaptured;
            clone.BrowserTabs = BrowserTabFingerprint.CloneList(source.BrowserTabs);
            clone.BrowserWindowTitleSnapshot = source.BrowserWindowTitleSnapshot;
            clone.BrowserBindingKind = source.BrowserBindingKind;
            clone.BrowserBindingUpdatedAtUtc = source.BrowserBindingUpdatedAtUtc;
            clone.ApplicationWindowIndex = source.ApplicationWindowIndex;
            clone.WindowPlacement = source.WindowPlacement;
            return clone;
        }
    }
}
