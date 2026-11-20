using System;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 托盘菜单用到的动作和可用性判断。这里只存委托，不实现保存/恢复逻辑。
    /// </summary>
    internal sealed class TrayMenuActions
    {
        internal Action ShowMain { get; set; }

        internal Func<bool> CanSaveProfile { get; set; }

        internal Action SaveProfile { get; set; }

        internal Func<bool> CanSaveLayout { get; set; }

        internal Action SaveLayout { get; set; }

        internal Action Exit { get; set; }
    }
}
