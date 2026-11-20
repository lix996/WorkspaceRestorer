using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 后台串行取桌面图标。
    ///
    /// 用户要求（2026-11-04）：“能不能先显示图标？让用户选择？因为用户找桌面的东西，
    /// 很大概率是通过图标来找。” 扫描后格子先出现，图标再由本类一个个取、一个个回填，
    /// UI 线程不再被 75 次 shell 调用堵住。
    ///
    /// Shell 的大图标通道（IShellItemImageFactory）要走 COM，所以后台线程显式
    /// CoInitializeEx(STA)；取到的 ImageSource 都已 Freeze，可安全跨线程回 UI。
    /// </summary>
    internal static class DesktopIconLoader
    {
        private const uint CoInitApartmentThreaded = 0x2;

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        /// <summary>
        /// 起一个后台 STA 线程，按列表顺序取图标；每取到一个就回调一次。
        /// <paramref name="isCancelled"/> 为 true 时立刻停止（重新扫描/关掉结果后旧任务不再回填）。
        /// </summary>
        public static void Load(
            IList<DesktopEntry> entries,
            int pixelSize,
            Action<DesktopEntry, ImageSource> onLoaded,
            Func<bool> isCancelled)
        {
            if (entries == null || entries.Count == 0 || onLoaded == null)
            {
                return;
            }
            Thread thread = new Thread(new ThreadStart(delegate
            {
                int comResult = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
                try
                {
                    for (int index = 0; index < entries.Count; index++)
                    {
                        if (isCancelled != null && isCancelled())
                        {
                            return;
                        }
                        DesktopEntry entry = entries[index];
                        if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
                        {
                            continue;
                        }
                        ImageSource image = ShellIconService.GetIcon(entry.Path, pixelSize);
                        if (image == null)
                        {
                            continue;
                        }
                        try
                        {
                            onLoaded(entry, image);
                        }
                        catch
                        {
                            // 回调只负责投递到 UI 线程；界面已关或版本过期时丢弃即可。
                        }
                    }
                }
                finally
                {
                    if (comResult >= 0)
                    {
                        CoUninitialize();
                    }
                }
            }));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Name = "DesktopIconLoader";
            thread.Start();
        }
    }
}
