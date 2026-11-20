using System;
using System.IO;
using System.Windows;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 数据目录可写性检查。数据现在**跟着程序走**（EXE 同级的 data 文件夹，
    /// 见 AppPaths.DataRoot），好处是整个文件夹打包给别人就能直接用；
    /// 代价是如果程序被放在 `C:\Program Files` 这类需要管理员权限的目录里，
    /// 数据会写不进去。这种情况下直接弹一句人话提示，而不是后面到处报错。
    /// </summary>
    internal static class DataLocationCheck
    {
        /// <summary>检查并建好数据目录；实在不能写就提示并结束程序。</summary>
        public static void Ensure()
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                string probe = Path.Combine(AppPaths.DataRoot, ".可写检查.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    AppText.F(
                        CommonTextKeys.DataCannotWriteFormat,
                        AppPaths.DataRoot,
                        exception.Message),
                    AppText.T(CommonTextKeys.DataCannotWriteTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw;
            }
        }
    }
}
