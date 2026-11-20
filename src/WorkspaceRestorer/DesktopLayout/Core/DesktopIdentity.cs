using System;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// 桌面图标的稳定标识。
    ///
    /// 【为什么不能带序号】旧版对拿不到路径的图标用 `name:名称:序号`。
    /// 序号来自桌面 ListView 的顺序，**新增或移动一个图标后，后面所有图标的序号都会变**，
    /// 于是保存的布局会把一堆老图标当成“新图标”，恢复时也可能对错人。
    /// 桌面上的显示名称本身是唯一的（资源管理器不允许重名），所以名称就够了。
    /// 这里把旧档案里的 `name:名称:序号` 统一归一成 `name:名称`，读取时向后兼容。
    /// </summary>
    internal static class DesktopIdentity
    {
        internal static string Normalize(string identity)
        {
            string value = (identity ?? string.Empty).Trim();
            if (value.Length == 0 || !value.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            int lastColon = value.LastIndexOf(':');
            if (lastColon <= 5 || lastColon >= value.Length - 1)
            {
                return value;
            }
            int number;
            if (int.TryParse(value.Substring(lastColon + 1), out number))
            {
                return value.Substring(0, lastColon);
            }
            return value;
        }

        internal static bool Equals(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
    }
}
