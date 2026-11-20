using System;
using System.Collections.Generic;
using WorkspaceRestorer.RecycleBin.Core;

namespace WorkspaceRestorer.UI
{
    // 工作区：回收站 —— **撤销记录**（用户 2026-10-17：“快捷键…能够让…回收站等都能用上”）。
    //
    // 回收站里会改数据的只有两类动作：
    //   · 恢复选中 / 恢复这一项   → 2026-10-23 用户明确要求：恢复不提供 Ctrl+Z（三个工作区统一）；
    //   · 彻底删除选中 / 彻底删除这一项 → 撤销＝把条目对象原样放回回收站（条目的原始数据
    //                               Profile / Payload 还在对象里，文件夹会按条目重新镜像出来）。
    // 撤销栈本身在公共区域：Shared/UI/Shell/UndoStack.cs（四个工作区共用一份）。
    public sealed partial class MainWindow
    {
        /// <summary>撤销“彻底删除”：把被删掉的回收站条目原样放回去；重做＝再彻底删一次。</summary>
        private void PushRecycleBinDeleteUndo(string description, IList<RecycleBinEntry> deleted)
        {
            if (deleted == null || deleted.Count == 0)
            {
                return;
            }
            // 拷一份：调用方之后可能还会改这个列表。
            List<RecycleBinEntry> copies = new List<RecycleBinEntry>();
            for (int index = 0; index < deleted.Count; index++)
            {
                if (deleted[index] != null)
                {
                    copies.Add(deleted[index]);
                }
            }
            if (copies.Count == 0)
            {
                return;
            }
            PushUndo(
                description,
                delegate
                {
                    controller.RestoreRecycleBinEntries(copies);
                    RefreshRecycleBinPage();
                },
                delegate
                {
                    controller.DeleteRecycleBinEntriesForever(copies);
                    RefreshRecycleBinPage();
                });
        }
    }
}
