using System;
using System.Collections.Generic;

using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.RecycleBin.Core
{
    /// <summary>
    /// 回收站的类别名。回收站是全局功能：任何工作区把东西删进来时，只要用这里的
    /// 常量（或自己的新常量）填 Kind，回收站就会自动按类别分组显示，
    /// 以后新增"可删除内容"不需要改回收站的代码。
    /// </summary>
    public static class RecycleBinKind
    {
        public const string StartupProfile = "启动与恢复";
        public const string DesktopLayout = "图标位置保存";
        public const string Application = "自启动应用";

        /// <summary>界面上分组的固定顺序：三个工作区在前，未知类别排最后。</summary>
        public static readonly string[] DisplayOrder = new string[]
        {
            StartupProfile,
            Application,
            DesktopLayout
        };

        public static int OrderOf(string kind)
        {
            for (int index = 0; index < DisplayOrder.Length; index++)
            {
                if (string.Equals(DisplayOrder[index], kind, StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return DisplayOrder.Length;
        }
    }
}