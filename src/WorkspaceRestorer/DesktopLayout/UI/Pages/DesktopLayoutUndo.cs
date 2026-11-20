using System;
using System.IO;
using System.Runtime.Serialization;
using System.Xml;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    // 工作区：图标位置保存 —— **撤销记录**（用户 2026-10-17：“快捷键…能够让…图标位置保存…都能用上”）。
    //
    // 这一页会改东西的只有两个按钮，各自的撤销方式不一样：
    //   · 保存图标布局：会**覆盖** `workflows\图标位置保存\布局档案.xml`。
    //       撤销＝把保存前那一刻的档案原文写回去（文件字节级回滚，最直接、不会失真）。
    //   · 恢复图标布局：会把桌面图标真的按档案摆一遍（**改的是桌面**，不是本程序的数据）。
    //       撤销＝恢复前先偷偷记一份“当前位置”（走 CaptureOnly 通道，不写档案），
    //             撤销时按那份快照把图标摆回去。
    //
    // 撤销栈本身在公共区域：Shared/UI/Shell/UndoStack.cs（四个工作区共用一份）。
    public sealed partial class MainWindow
    {
        /// <summary>读“布局档案.xml”的原始字节（文件不存在＝null）。撤销保存时要用它做对照。</summary>
        private static byte[] ReadDesktopLayoutArchiveBytes()
        {
            try
            {
                string path = AppPaths.DesktopLayoutFile;
                if (!File.Exists(path))
                {
                    return null;
                }
                return File.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 读布局档案失败（撤销将不可用）", exception);
                return null;
            }
        }

        /// <summary>把“布局档案.xml”写回成指定字节（原子替换：先写临时文件再换名，避免写坏档案）。</summary>
        private static void WriteDesktopLayoutArchiveBytes(byte[] content)
        {
            string path = AppPaths.DesktopLayoutFile;
            try
            {
                if (content == null)
                {
                    // 保存前本来没有档案。不能只删文件：下次 Load 会把它当成“档案丢了”，
                    // 又从“布局”文件夹迁移回来，撤销就等于没撤。写一份 v2 的空档案，
                    // 既表示“没有布局”，又不会再触发文件夹迁移。
                    DesktopLayoutCatalog empty = new DesktopLayoutCatalog();
                    empty.FormatVersion = 2;
                    empty.ActiveLayoutId = string.Empty;
                    new DesktopLayoutRepository(path).Save(empty);
                    AppLog.Write("[图标位置保存] 已把布局档案回滚到保存前（空档案 v2）");
                    return;
                }
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }
                string temp = path + ".undo." + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, content);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
                AppLog.Write("[图标位置保存] 已把布局档案回滚到保存前的内容（" + content.Length + " 字节）");
            }
            catch (Exception exception)
            {
                AppLog.Write("[图标位置保存] 回滚布局档案失败", exception);
            }
        }

        private static bool DesktopLayoutArchiveEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }
            if (left.Length != right.Length)
            {
                return false;
            }
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool DesktopLayoutArchiveContains(byte[] content, string layoutId)
        {
            if (content == null || content.Length == 0 || string.IsNullOrWhiteSpace(layoutId))
            {
                return false;
            }
            try
            {
                DataContractSerializer serializer = new DataContractSerializer(typeof(DesktopLayoutCatalog));
                using (MemoryStream stream = new MemoryStream(content))
                using (XmlDictionaryReader reader = XmlDictionaryReader.CreateTextReader(
                    stream, XmlDictionaryReaderQuotas.Max))
                {
                    DesktopLayoutCatalog catalog = serializer.ReadObject(reader) as DesktopLayoutCatalog;
                    return catalog != null
                        && DesktopLayoutRepository.FindById(catalog, layoutId) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 记一条“保存图标布局”的撤销记录。<paramref name="archiveBefore"/> 是保存前的档案原文；
        /// 如果保存前后档案一模一样（没有真正改动），就不记，免得 Ctrl+Z 出现一条白撤。
        /// </summary>
        private void PushDesktopLayoutSaveUndo(byte[] archiveBefore, DesktopTopologyLayout saved)
        {
            byte[] archiveAfter = ReadDesktopLayoutArchiveBytes();
            if (DesktopLayoutArchiveEquals(archiveBefore, archiveAfter))
            {
                return;
            }
            PushUndo(
                AppText.T(DesktopLayoutTextKeys.UndoSave),
                delegate
                {
                    WriteDesktopLayoutArchiveBytes(archiveBefore);
                    // 新保存撤销后，档案里没有这份布局了，对应的文件夹也要一起收掉；
                    // 否则又会出现“卡片没了、布局文件夹还在”的历史副本。
                    if (saved != null
                        && !DesktopLayoutArchiveContains(archiveBefore, saved.Id))
                    {
                        DesktopLayoutFolderStore.DeleteFolder(saved);
                    }
                    RefreshLayoutSummary();
                },
                delegate
                {
                    WriteDesktopLayoutArchiveBytes(archiveAfter);
                    if (saved != null)
                    {
                        DesktopLayoutFolderStore.Save(saved, false, false);
                    }
                    RefreshLayoutSummary();
                });
        }

        /// <summary>
        /// 记一条“恢复图标布局”的撤销记录：撤销＝按恢复前的快照把图标摆回去；
        /// 重做＝再按档案恢复一次。快照没取到就不记（宁可不记，也不记一条做不到的）。
        /// </summary>
        private void PushDesktopLayoutRestoreUndo(DesktopShellSnapshot beforeRestore)
        {
            if (beforeRestore == null || beforeRestore.Icons == null || beforeRestore.Icons.Count == 0)
            {
                AppLog.Write("[图标位置保存] 恢复前没能记下当前位置，这一步不提供撤销");
                return;
            }
            PushUndo(
                AppText.F(
                    DesktopLayoutTextKeys.UndoRestoreFormat,
                    beforeRestore.Icons.Count),
                delegate
                {
                    controller.ApplyDesktopIconSnapshot(beforeRestore);
                    RefreshLayoutSummary();
                },
                delegate
                {
                    // 重做＝再按档案恢复一次。恢复本身是异步的（要等桌面那份读取），
                    // 撤销/重做是同步入口，所以这里只“发起”，进度与结论照旧由状态栏显示。
#pragma warning disable 4014
                    controller.RestoreDesktopLayoutAsync();
#pragma warning restore 4014
                    RefreshLayoutSummary();
                });
        }
    }
}
