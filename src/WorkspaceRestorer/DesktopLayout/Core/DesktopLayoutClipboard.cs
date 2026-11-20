using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 【图标位置保存】Ctrl+C / Ctrl+V 的剪贴板格式。
    ///
    /// 和「启动与恢复」的 `StartupProfileClipboard` 用同一个 `CSK1|工作区名` 标记：
    /// 复制时写一段自包含的布局 JSON，粘贴时先验标记再反序列化。
    /// 这样布局不会被粘进工作方案，工作方案/自启动方案也不会被误当成布局。
    /// </summary>
    public static class DesktopLayoutClipboard
    {
        public const string ClipboardHeaderPrefix = "CSK1|";
        public const string WorkspaceName = "图标位置保存";

        /// <summary>把一份布局序列化成带工作区标记的文本（失败返回 null，不抛异常）。</summary>
        public static string Serialize(DesktopTopologyLayout layout)
        {
            if (layout == null)
            {
                return null;
            }
            try
            {
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(DesktopTopologyLayout));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, layout);
                    return ClipboardHeaderPrefix + WorkspaceName + "\n"
                        + Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>这段文本是不是「图标位置保存」复制出去的（标记必须是本工作区）。</summary>
        public static bool IsOwnPayload(string text)
        {
            string workspace = HeaderWorkspaceOf(text);
            return !string.IsNullOrEmpty(workspace)
                && string.Equals(workspace, WorkspaceName, StringComparison.Ordinal);
        }

        /// <summary>
        /// 是别的工作区复制来的吗？返回那个工作区名；本工作区/没有标记返回 null。
        /// 与 `StartupProfileClipboard.ForeignWorkspaceOf` 的语义保持一致。
        /// </summary>
        public static string ForeignWorkspaceOf(string text)
        {
            string workspace = HeaderWorkspaceOf(text);
            if (string.IsNullOrEmpty(workspace)
                || string.Equals(workspace, WorkspaceName, StringComparison.Ordinal))
            {
                return null;
            }
            return workspace;
        }

        /// <summary>还原布局快照；只接受本工作区标记，避免把任意 JSON 解析成布局。</summary>
        public static DesktopTopologyLayout Deserialize(string text)
        {
            if (!IsOwnPayload(text))
            {
                return null;
            }
            try
            {
                string payload = StripHeader(text);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }
                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(DesktopTopologyLayout));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(payload)))
                {
                    DesktopTopologyLayout layout = serializer.ReadObject(stream) as DesktopTopologyLayout;
                    if (layout == null)
                    {
                        return null;
                    }
                    // 空壳/损坏内容不当成布局：至少要有名字或图标/拓扑。
                    bool hasName = !string.IsNullOrWhiteSpace(layout.Name);
                    bool hasIcons = layout.Icons != null && layout.Icons.Count > 0;
                    bool hasTopology = layout.Topology != null
                        && layout.Topology.Monitors != null
                        && layout.Topology.Monitors.Count > 0;
                    return hasName || hasIcons || hasTopology ? layout : null;
                }
            }
            catch
            {
                return null;
            }
        }

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
            return end < 0 ? string.Empty : trimmed.Substring(end + 1);
        }

        private static string HeaderWorkspaceOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            string trimmed = text.TrimStart();
            if (!trimmed.StartsWith(ClipboardHeaderPrefix, StringComparison.Ordinal))
            {
                return null;
            }
            int end = trimmed.IndexOf('\n');
            if (end < 0)
            {
                return null;
            }
            string name = trimmed.Substring(
                ClipboardHeaderPrefix.Length,
                end - ClipboardHeaderPrefix.Length).Trim();
            return name.Length == 0 ? null : name;
        }
    }
}
