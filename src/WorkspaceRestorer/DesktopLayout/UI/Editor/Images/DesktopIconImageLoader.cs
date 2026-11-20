using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkspaceRestorer.Desktop;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 编辑页用的图标图片加载器。
    ///
    /// 【为什么不用截图裁图标】截图里连着壁纸，裁出来是一块块蓝底方块，
    /// 图标一大就变成“马赛克”，而且缩放后和卡片截图的比例对不上。
    /// 这里改成从 Windows 图标库取**透明背景**的 256×256 图标：
    ///   · 文件/文件夹/快捷方式 → 桌面上的同名文件 → SHGetFileInfo；
    ///   · 此电脑/回收站/控制面板/网络 → 对应的 shell 命名空间；
    ///   · 找不到 → shell32 的通用程序图标。
    /// 取到的图标会裁掉四周透明留白，保证每个图标在格子里视觉大小一致。
    /// </summary>
    internal static class DesktopIconImageLoader
    {
        private const uint ShgfiIcon = 0x00000100;
        private const uint ShgfiLargeIcon = 0x00000000;
        private const uint ShgfiPidl = 0x00000008;
        private const uint ShgfiSysIconIndex = 0x00004000;
        private const int ShilJumbo = 0x4;
        private const int IldTransparent = 0x1;

        private static readonly object Gate = new object();
        private static bool initialized;
        private static Dictionary<string, string> desktopFiles;
        private static List<string> desktopDirectories;
        private static Dictionary<string, string> systemShellPaths;
        private static Dictionary<string, BitmapSource> cache;
        private static BitmapSource fallbackIcon;

        internal static BitmapSource Load(DesktopIconPlacement item)
        {
            EnsureInitialized();
            string key = BuildCacheKey(item);
            lock (Gate)
            {
                BitmapSource cached;
                if (cache.TryGetValue(key, out cached))
                {
                    return cached;
                }
            }

            string target;
            lock (Gate)
            {
                target = ResolveTarget(item);
            }

            BitmapSource image = null;
            if (!string.IsNullOrWhiteSpace(target))
            {
                image = ExtractJumbo(target);
            }
            if (image == null)
            {
                lock (Gate)
                {
                    if (fallbackIcon == null)
                    {
                        fallbackIcon = LoadFallbackIcon();
                    }
                    image = fallbackIcon;
                }
            }

            if (image != null)
            {
                lock (Gate)
                {
                    if (!cache.ContainsKey(key))
                    {
                        cache[key] = image;
                    }
                }
            }
            return image;
        }

        internal static bool IsShortcut(DesktopIconPlacement item)
        {
            EnsureInitialized();
            string target;
            lock (Gate)
            {
                target = ResolveTarget(item);
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }
            return target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                || target.EndsWith(".url", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCacheKey(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(item.Identity))
            {
                return item.Identity.Trim().ToUpperInvariant();
            }
            return (item.DisplayName ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (initialized)
                {
                    return;
                }
                desktopFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                desktopDirectories = new List<string>();
                string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                AddDesktopDirectory(userDesktop);
                AddDesktopDirectory(publicDesktop);

                systemShellPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AddSystemPath("此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                AddSystemPath("此计算机", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                AddSystemPath("这台电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                AddSystemPath("This PC", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                AddSystemPath("Computer", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
                AddSystemPath("回收站", "::{645FF040-5081-101B-9F08-00AA002F954E}");
                AddSystemPath("Recycle Bin", "::{645FF040-5081-101B-9F08-00AA002F954E}");
                AddSystemPath("网络", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
                AddSystemPath("Network", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}");
                AddSystemPath("控制面板", "::{26EE0668-A00A-44D7-9371-BEB064C98683}");
                AddSystemPath("Control Panel", "::{26EE0668-A00A-44D7-9371-BEB064C98683}");
                AddSystemPath("用户文件夹", "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}");
                AddSystemPath("Users", "::{59031A47-3F72-44A7-89C5-5595FE6B30EE}");

                cache = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
                initialized = true;
            }
        }

        private static void AddDesktopDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }
            if (!desktopDirectories.Contains(directory))
            {
                desktopDirectories.Add(directory);
            }
            try
            {
                FileSystemInfo[] entries = new DirectoryInfo(directory).GetFileSystemInfos();
                for (int index = 0; index < entries.Length; index++)
                {
                    FileSystemInfo entry = entries[index];
                    if (entry == null)
                    {
                        continue;
                    }
                    AddDesktopName(entry.Name, entry.FullName);
                    string extension = (entry.Extension ?? string.Empty).ToLowerInvariant();
                    if (extension == ".lnk" || extension == ".url")
                    {
                        AddDesktopName(Path.GetFileNameWithoutExtension(entry.Name), entry.FullName);
                    }
                }
            }
            catch
            {
                // 桌面目录读不到时只是少了图标，不影响编辑。
            }
        }

        private static void AddDesktopName(string name, string path)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            if (!desktopFiles.ContainsKey(name))
            {
                desktopFiles[name] = path;
            }
        }

        private static void AddSystemPath(string name, string shellPath)
        {
            systemShellPaths[name] = shellPath;
        }

        private static string ResolveTarget(DesktopIconPlacement item)
        {
            if (item == null)
            {
                return null;
            }
            string identity = (item.Identity ?? string.Empty).Trim();
            if (identity.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                return identity.Substring(5).Trim();
            }
            if (identity.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                return identity.Substring(6).Trim();
            }

            string displayName = (item.DisplayName ?? string.Empty).Trim();
            string resolved = ResolveName(displayName);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            string identityName = ExtractIdentityName(identity);
            if (!string.IsNullOrWhiteSpace(identityName)
                && !string.Equals(identityName, displayName, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveName(identityName);
            }
            return null;
        }

        private static string ResolveName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            name = name.Trim();
            string path;
            if (systemShellPaths.TryGetValue(name, out path))
            {
                return path;
            }
            if (desktopFiles.TryGetValue(name, out path))
            {
                return path;
            }
            if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && desktopFiles.TryGetValue(name + ".lnk", out path))
            {
                return path;
            }
            if (!name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                && desktopFiles.TryGetValue(name + ".url", out path))
            {
                return path;
            }

            // 桌面图标缓存是编辑器打开时建的；如果用户随后才新建 123.txt，
            // 只查缓存会找不到，新图标就退化成通用图标，看起来和桌面不一样。
            // 这里对缓存未命中的名字直接再扫一遍桌面目录（数量很少）。
            for (int index = 0; index < desktopDirectories.Count; index++)
            {
                string directory = desktopDirectories[index];
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    AddDesktopName(name, candidate);
                    return candidate;
                }
                if (!name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    string shortcut = candidate + ".lnk";
                    if (File.Exists(shortcut))
                    {
                        AddDesktopName(name + ".lnk", shortcut);
                        return shortcut;
                    }
                }
                if (!name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    string internet = candidate + ".url";
                    if (File.Exists(internet))
                    {
                        AddDesktopName(name + ".url", internet);
                        return internet;
                    }
                }
            }
            return null;
        }

        private static string ExtractIdentityName(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)
                || !identity.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string rest = identity.Substring(5).Trim();
            int colon = rest.LastIndexOf(':');
            if (colon > 0 && colon < rest.Length - 1)
            {
                int number;
                if (int.TryParse(rest.Substring(colon + 1), out number))
                {
                    rest = rest.Substring(0, colon);
                }
            }
            return rest;
        }

        private static BitmapSource ExtractJumbo(string target)
        {
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attributes;
                int parseResult = SHParseDisplayName(target, IntPtr.Zero, out pidl, 0, out attributes);
                if (parseResult == 0 && pidl != IntPtr.Zero)
                {
                    SHFILEINFO info = new SHFILEINFO();
                    IntPtr result = SHGetFileInfoPidl(
                        pidl,
                        0,
                        ref info,
                        (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                        ShgfiPidl | ShgfiSysIconIndex);
                    if (result != IntPtr.Zero)
                    {
                        BitmapSource image = GetFromSystemImageList(info.iIcon);
                        if (image != null)
                        {
                            return image;
                        }
                    }
                }
            }
            catch
            {
                // 下面再试一次 32×32 的大图标。
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
            return ExtractLargeIcon(target);
        }

        private static BitmapSource GetFromSystemImageList(int iconIndex)
        {
            IImageList list = null;
            IntPtr icon = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                int result = SHGetImageList(ShilJumbo, ref iid, out list);
                if (result != 0 || list == null)
                {
                    return null;
                }
                if (list.GetIcon(iconIndex, IldTransparent, out icon) != 0 || icon == IntPtr.Zero)
                {
                    return null;
                }
                return ConvertIcon(icon);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (icon != IntPtr.Zero)
                {
                    DestroyIcon(icon);
                }
                if (list != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(list);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static BitmapSource ExtractLargeIcon(string target)
        {
            IntPtr icon = IntPtr.Zero;
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                IntPtr result = SHGetFileInfoPath(
                    target,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    ShgfiIcon | ShgfiLargeIcon);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                {
                    return null;
                }
                icon = info.hIcon;
                return ConvertIcon(icon);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (icon != IntPtr.Zero)
                {
                    DestroyIcon(icon);
                }
            }
        }

        private static BitmapSource ConvertIcon(IntPtr icon)
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return CropTransparentMargins(source);
        }

        /// <summary>
        /// 256×256 的图标四周经常留着一大圈透明边，直接显示会显得比别的图标小。
        /// 按 alpha 找到实际内容范围，再裁成正方形，视觉大小就一致了。
        /// </summary>
        private static BitmapSource CropTransparentMargins(BitmapSource source)
        {
            try
            {
                int width = source.PixelWidth;
                int height = source.PixelHeight;
                if (width <= 4 || height <= 4)
                {
                    return source;
                }
                FormatConvertedBitmap converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                int stride = width * 4;
                byte[] pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                int minX = width;
                int minY = height;
                int maxX = -1;
                int maxY = -1;
                for (int y = 0; y < height; y++)
                {
                    int offset = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte alpha = pixels[offset + x * 4 + 3];
                        if (alpha > 24)
                        {
                            if (x < minX)
                            {
                                minX = x;
                            }
                            if (x > maxX)
                            {
                                maxX = x;
                            }
                            if (y < minY)
                            {
                                minY = y;
                            }
                            if (y > maxY)
                            {
                                maxY = y;
                            }
                        }
                    }
                }
                if (maxX < minX || maxY < minY)
                {
                    return source;
                }
                int contentWidth = maxX - minX + 1;
                int contentHeight = maxY - minY + 1;
                if (contentWidth < 4 || contentHeight < 4)
                {
                    return source;
                }
                int side = (int)Math.Ceiling(Math.Max(contentWidth, contentHeight) * 1.04);
                if (side <= 0)
                {
                    return source;
                }
                side = Math.Min(side, Math.Min(width, height));
                int centerX = (minX + maxX) / 2;
                int centerY = (minY + maxY) / 2;
                int left = Math.Max(0, Math.Min(width - side, centerX - side / 2));
                int top = Math.Max(0, Math.Min(height - side, centerY - side / 2));
                if (left == 0 && top == 0 && side >= width && side >= height)
                {
                    return source;
                }
                CroppedBitmap crop = new CroppedBitmap(source, new Int32Rect(left, top, side, side));
                crop.Freeze();
                return crop;
            }
            catch
            {
                return source;
            }
        }

        private static BitmapSource LoadFallbackIcon()
        {
            try
            {
                string shell = Path.Combine(Environment.SystemDirectory, "shell32.dll");
                BitmapSource icon = ExtractLargeIcon(shell);
                if (icon != null)
                {
                    return icon;
                }
            }
            catch
            {
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            internal IntPtr hIcon;
            internal int iIcon;
            internal uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            internal string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            internal string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfoPath(
            string path,
            uint fileAttributes,
            ref SHFILEINFO fileInfo,
            uint fileInfoSize,
            uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfoPidl(
            IntPtr pidl,
            uint fileAttributes,
            ref SHFILEINFO fileInfo,
            uint fileInfoSize,
            uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            string name,
            IntPtr bindContext,
            out IntPtr pidl,
            uint attributesIn,
            out uint attributesOut);

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int imageList, ref Guid iid, out IImageList list);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [ComImport]
        [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            int Add(IntPtr bitmap, IntPtr mask, ref int index);
            int ReplaceIcon(int index, IntPtr icon, ref int newIndex);
            int SetOverlayImage(int index, int overlay);
            int Replace(int index, IntPtr bitmap, IntPtr mask);
            int AddMasked(IntPtr bitmap, int maskColor, ref int index);
            int Draw(IntPtr drawParams);
            int Remove(int index);
            int GetIcon(int index, int flags, out IntPtr icon);
        }
    }
}
