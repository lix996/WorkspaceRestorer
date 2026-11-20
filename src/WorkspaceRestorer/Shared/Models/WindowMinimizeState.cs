using System;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 「启动与恢复」和「自启动应用」共用的窗口最小化状态转换。
    ///
    /// 这里只改内存里的 SavedWindowPlacement，不写盘、不操作窗口；
    /// 两个工作区各自决定怎么保存、怎么撤销。
    /// </summary>
    public static class WindowMinimizeState
    {
        public static bool IsMinimized(SavedWindowPlacement placement)
        {
            return placement != null
                && string.Equals(placement.WindowState, "Minimized",
                    StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string state)
        {
            if (string.Equals(state, "Maximized", StringComparison.OrdinalIgnoreCase))
            {
                return "Maximized";
            }
            if (string.Equals(state, "Minimized", StringComparison.OrdinalIgnoreCase))
            {
                return "Minimized";
            }
            return "Normal";
        }

        /// <summary>
        /// 开启时记住原来的 Normal/Maximized；取消时恢复。
        /// 返回是否真的改变了状态。
        /// </summary>
        public static bool SetMinimized(SavedWindowPlacement placement, bool minimized)
        {
            if (placement == null)
            {
                return false;
            }

            if (minimized)
            {
                string current = Normalize(placement.WindowState);
                bool changed = !string.Equals(current, "Minimized", StringComparison.OrdinalIgnoreCase)
                    || !placement.MinimizeChoiceExplicit;
                if (!string.Equals(current, "Minimized", StringComparison.OrdinalIgnoreCase))
                {
                    placement.WindowStateBeforeMinimize = current;
                }
                placement.MinimizeChoiceExplicit = true;
                placement.WindowState = "Minimized";
                return changed;
            }

            string restore = Normalize(placement.WindowStateBeforeMinimize);
            string stateBefore = Normalize(placement.WindowState);
            bool wasExplicit = placement.MinimizeChoiceExplicit;
            placement.WindowState = string.Equals(restore, "Maximized", StringComparison.OrdinalIgnoreCase)
                ? "Maximized"
                : "Normal";
            placement.WindowStateBeforeMinimize = null;
            placement.MinimizeChoiceExplicit = false;
            return wasExplicit
                || !string.Equals(stateBefore, placement.WindowState, StringComparison.OrdinalIgnoreCase);
        }
    }
}
