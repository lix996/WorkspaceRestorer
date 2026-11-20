using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 取资源管理器用的那套图标，供列表显示用。
    ///
    /// 用户 2026-10-17 的话：“自启动应用，扫描桌面时，要出现图标”，
    /// 后来又补了一句“桌面上的应用要带有大图标（就是看着图标选就行了）”。
    /// 所以这里有两种取法，按要显示的像素尺寸自动挑：
    ///  · 小图标（≤32，列表里的行）：`SHGetFileInfo(SHGFI_ICON)`，32×32；
    ///  · 大图标（&gt;32，卡片的格子）：`IShellItemImageFactory.GetImage` —— 这就是资源管理器
    ///    自己画大图标/缩略图的那条路，能拿到该程序真正的高分辨率图标（不会把 32×32 放大成糊的）。
    ///
    /// 两种取法对 `.lnk` / `.url` 都会**解析成目标程序**的图标（跟桌面上一模一样，不用自己读快捷方式），
    /// 文件夹是文件夹图标，exe / bat / 普通文件按系统关联取。
    /// 只读系统图标，不碰文件内容、不启动程序。
    ///
    /// 缓存：一次扫描就是几十个格子，每次重画都去问 shell 会明显掉帧，所以按“尺寸 + 路径”缓存
    /// （**连“取不到”也缓存**，避免一条坏路径每次都去问一遍）。返回的 <see cref="ImageSource"/> 都已 Freeze。
    /// </summary>
    public static class ShellIconService
    {
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiUseFileAttributes = 0x000000010;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileAttributeDirectory = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string TypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;

            public NativeSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [Flags]
        private enum ShellItemImageFlags
        {
            ResizeToFit = 0x00000000,
            BiggerSizeOk = 0x00000001,
            IconOnly = 0x00000004,
            CropToSquare = 0x00000020
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string path,
            uint fileAttributes,
            ref ShellFileInfo info,
            uint infoSize,
            uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string path,
            IntPtr bindContext,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object shellItem);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        private static readonly Dictionary<string, ImageSource> Cache =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private static readonly object CacheLock = new object();

        /// <summary>取某个路径的 shell 图标（按需要显示的像素尺寸挑取法）；取不到返回 <c>null</c>。</summary>
        public static ImageSource GetIcon(string path, int pixelSize)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            int size = pixelSize <= 0 ? 32 : pixelSize;
            string key = size + "|" + path.Trim();
            lock (CacheLock)
            {
                ImageSource cached;
                if (Cache.TryGetValue(key, out cached))
                {
                    return cached;
                }
                ImageSource loaded = size > 32 ? LoadLarge(path, size) : LoadSmall(path);
                if (loaded == null && size > 32)
                {
                    loaded = LoadSmall(path);   // 大图标拿不到就退回 32×32，别让格子空着
                }
                Cache[key] = loaded;
                return loaded;
            }
        }

        /// <summary>列表行用的小图标（32×32）。</summary>
        public static ImageSource GetIcon(string path)
        {
            return GetIcon(path, 32);
        }

        private static ImageSource LoadSmall(string path)
        {
            uint attributes = 0;
            uint flags = ShgfiIcon;   // 不给 SMALLICON：要大图标，缩到列表尺寸比放大 16×16 清楚
            bool exists = Exists(path);
            if (!exists)
            {
                // 路径已经不在了（桌面项被删/改名）：按扩展名给一个通用图标，别让这一行没图标。
                flags |= ShgfiUseFileAttributes;
                attributes = LooksLikeFolder(path) ? FileAttributeDirectory : FileAttributeNormal;
            }
            ShellFileInfo info = new ShellFileInfo();
            try
            {
                IntPtr result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf(typeof(ShellFileInfo)), flags);
                if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
                {
                    return null;
                }
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    info.Icon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (source != null)
                {
                    source.Freeze();
                }
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (info.Icon != IntPtr.Zero)
                {
                    DestroyIcon(info.Icon);
                }
            }
        }

        private static ImageSource LoadLarge(string path, int pixelSize)
        {
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid interfaceId = typeof(IShellItemImageFactory).GUID;
                object shellItem;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out shellItem);
                IShellItemImageFactory factory = shellItem as IShellItemImageFactory;
                if (factory == null)
                {
                    return null;
                }
                factory.GetImage(
                    new NativeSize(pixelSize, pixelSize),
                    ShellItemImageFlags.IconOnly | ShellItemImageFlags.BiggerSizeOk,
                    out bitmap);
                if (bitmap == IntPtr.Zero)
                {
                    return null;
                }
                BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (source != null)
                {
                    source.Freeze();
                }
                return source;
            }
            catch
            {
                // 路径不合法、COM 失败、图标库坏掉……都退回小图标，绝不让界面因为图标崩掉。
                return null;
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }
            }
        }

        private static bool Exists(string path)
        {
            try
            {
                return File.Exists(path) || Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeFolder(string path)
        {
            return path.EndsWith("\\", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal);
        }
    }
}
