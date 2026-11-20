using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 把恢复出来的浏览器窗口归到用户**原来固定的那个任务栏图标**下面。
    ///
    /// Windows 任务栏按窗口的 AppUserModelID（AUMID）分组，不按 exe 路径。
    /// Edge 直接启动的窗口一般是通用 AUMID（例如 MSEdge），而用户固定的
    /// Edge 快捷方式可能带另一个 AUMID；两者不一致时任务栏会显示两个 Edge：
    /// 固定图标只是一个启动入口，恢复窗口跑到“N 个运行窗口”那一个图标下。
    /// 之前手动最小化能看到固定图标下有页面，说明窗口的 AUMID 和固定快捷方式
    /// 一致；所以这里读取**用户固定快捷方式自己的 AppID**，再写到恢复窗口中。
    ///
    /// 本类不修改任何快捷方式/注册表：只读固定快捷方式，运行时设置窗口属性。
    /// 找不到固定快捷方式、没有 AppID 或多个同 exe 固定项无法判断时返回 null，
    /// 保持系统默认分组，宁可不改也不乱归组。
    /// </summary>
    internal static class TaskbarIdentityService
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, string> PinnedAppIdCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Guid PropertyStoreId =
            new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

        private static readonly Guid AppUserModelIdKeyFormat =
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
        private const uint AppUserModelIdKeyPid = 5;
        private const ushort VariantTypeString = 31;

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid FormatId;
            internal uint PropertyId;
        }

        // 64 位上 PROPVARIANT 头部是 8 字节，LPWSTR 放在偏移 8。
        // 这里只处理 VT_LPWSTR，其余类型只读 vt。
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            internal ushort VariantType;
            internal ushort Reserved1;
            internal ushort Reserved2;
            internal ushort Reserved3;
            internal IntPtr Pointer;
            internal IntPtr Pointer2;
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint index, out PropertyKey key);
            void GetValue(ref PropertyKey key, out PropVariant value);
            void SetValue(ref PropertyKey key, ref PropVariant value);
            void Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetPropertyStoreFromParsingName(
            string path,
            IntPtr bindContext,
            uint flags,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("shell32.dll")]
        private static extern int SHGetPropertyStoreForWindow(
            IntPtr windowHandle,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);

        /// <summary>新一轮恢复开始时清缓存：用户可能刚增删固定项。</summary>
        internal static void ClearCache()
        {
            lock (CacheLock)
            {
                PinnedAppIdCache.Clear();
            }
        }

        /// <summary>
        /// 返回用户固定任务栏里与这个浏览器 exe 对应的 AppID。
        /// 多个匹配项时不猜，返回 null，避免把 Profile 1 的窗口归到 Default 的固定项。
        /// </summary>
        internal static string ResolvePinnedAppId(string executablePath)
        {
            string normalized = PathText.NormalizeExpanded(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            lock (CacheLock)
            {
                string cached;
                if (PinnedAppIdCache.TryGetValue(normalized, out cached))
                {
                    return cached;
                }

                string appId = ResolvePinnedAppIdCore(normalized);
                PinnedAppIdCache[normalized] = appId;
                return appId;
            }
        }

        private static string ResolvePinnedAppIdCore(string normalizedExecutable)
        {
            List<string> shortcuts = EnumeratePinnedShortcuts();
            List<string> matchedAppIds = new List<string>();
            for (int index = 0; index < shortcuts.Count; index++)
            {
                string target;
                string arguments;
                if (!TryReadShortcut(shortcuts[index], out target, out arguments))
                {
                    continue;
                }
                if (!TargetMatches(target, normalizedExecutable))
                {
                    continue;
                }

                string appId = ReadShortcutAppId(shortcuts[index]);
                if (string.IsNullOrWhiteSpace(appId))
                {
                    continue;
                }
                bool duplicate = false;
                for (int cursor = 0; cursor < matchedAppIds.Count; cursor++)
                {
                    if (string.Equals(matchedAppIds[cursor], appId, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    matchedAppIds.Add(appId.Trim());
                }
            }

            if (matchedAppIds.Count == 1)
            {
                AppLog.Write("[窗口恢复] 读取到固定任务栏 AppID：" + matchedAppIds[0]);
                return matchedAppIds[0];
            }
            if (matchedAppIds.Count > 1)
            {
                AppLog.Write("[窗口恢复] 发现多个同 exe 的固定任务栏项，无法安全判断，保持系统默认分组。");
                return null;
            }
            return null;
        }

        private static List<string> EnumeratePinnedShortcuts()
        {
            List<string> result = new List<string>();
            AddShortcuts(result, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            AddShortcuts(result, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            return result;
        }

        private static void AddShortcuts(List<string> result, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }
            string directory = Path.Combine(
                root,
                "Microsoft",
                "Internet Explorer",
                "Quick Launch",
                "User Pinned",
                "TaskBar");
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }
                string[] files = Directory.GetFiles(directory, "*.lnk");
                for (int index = 0; index < files.Length; index++)
                {
                    if (!string.IsNullOrWhiteSpace(files[index]))
                    {
                        result.Add(files[index]);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TryReadShortcut(string path, out string target, out string arguments)
        {
            target = null;
            arguments = null;
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return false;
                }
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { path });
                if (shortcut == null)
                {
                    return false;
                }
                target = Convert.ToString(shortcut.GetType().InvokeMember(
                    "TargetPath",
                    BindingFlags.GetProperty,
                    null,
                    shortcut,
                    null));
                arguments = Convert.ToString(shortcut.GetType().InvokeMember(
                    "Arguments",
                    BindingFlags.GetProperty,
                    null,
                    shortcut,
                    null));
                return !string.IsNullOrWhiteSpace(target);
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseComObject(shortcut);
                ReleaseComObject(shell);
            }
        }

        private static bool TargetMatches(string target, string normalizedExecutable)
        {
            string normalizedTarget = PathText.NormalizeExpanded(target);
            if (string.IsNullOrWhiteSpace(normalizedTarget))
            {
                return false;
            }
            if (string.Equals(normalizedTarget, normalizedExecutable, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            try
            {
                string targetName = Path.GetFileNameWithoutExtension(normalizedTarget);
                string executableName = Path.GetFileNameWithoutExtension(normalizedExecutable);
                if (string.Equals(targetName, executableName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                // Edge/Chrome 有些固定项指向 msedge_proxy.exe / chrome_proxy.exe；
                // 允许“以浏览器名开头”的代理 exe，但长度太短的不猜，避免误匹配。
                return targetName != null
                    && executableName != null
                    && targetName.Length >= 5
                    && executableName.Length >= 5
                    && (targetName.StartsWith(executableName, StringComparison.OrdinalIgnoreCase)
                        || executableName.StartsWith(targetName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        private static string ReadShortcutAppId(string path)
        {
            IPropertyStore store = null;
            try
            {
                Guid interfaceId = PropertyStoreId;
                int result = SHGetPropertyStoreFromParsingName(
                    path,
                    IntPtr.Zero,
                    0,
                    ref interfaceId,
                    out store);
                if (result != 0 || store == null)
                {
                    return null;
                }
                PropertyKey key = CreateAppUserModelIdKey();
                PropVariant value;
                store.GetValue(ref key, out value);
                string appId = ReadString(value);
                PropVariantClear(ref value);
                return appId;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseComObject(store);
            }
        }

        internal static string ReadWindowAppId(IntPtr windowHandle)
        {
            IPropertyStore store = null;
            try
            {
                Guid interfaceId = PropertyStoreId;
                int result = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out store);
                if (result != 0 || store == null)
                {
                    return null;
                }
                PropertyKey key = CreateAppUserModelIdKey();
                PropVariant value;
                store.GetValue(ref key, out value);
                string appId = ReadString(value);
                PropVariantClear(ref value);
                return appId;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseComObject(store);
            }
        }

        /// <summary>把窗口的任务栏身份设成固定快捷方式的 AppID；成功返回 true。</summary>
        internal static bool ApplyToWindow(IntPtr windowHandle, string appId)
        {
            if (windowHandle == IntPtr.Zero
                || string.IsNullOrWhiteSpace(appId)
                || !NativeMethods.IsWindow(windowHandle))
            {
                return false;
            }

            IPropertyStore store = null;
            IntPtr stringPointer = IntPtr.Zero;
            try
            {
                Guid interfaceId = PropertyStoreId;
                int result = SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out store);
                if (result != 0 || store == null)
                {
                    return false;
                }
                PropertyKey key = CreateAppUserModelIdKey();
                PropVariant value = new PropVariant();
                value.VariantType = VariantTypeString;
                stringPointer = Marshal.StringToCoTaskMemUni(appId.Trim());
                value.Pointer = stringPointer;
                store.SetValue(ref key, ref value);
                store.Commit();
                string readBack = ReadWindowAppId(windowHandle);
                bool success = string.Equals(readBack, appId.Trim(), StringComparison.OrdinalIgnoreCase);
                if (success)
                {
                    AppLog.Write("[窗口恢复] 浏览器窗口已归到固定任务栏身份：HWND="
                        + windowHandle + " AppID=" + appId);
                }
                else
                {
                    AppLog.Write("[窗口恢复] 浏览器窗口任务栏身份设置未生效：HWND="
                        + windowHandle + " 期望=" + appId + " 实际=" + (readBack ?? "(空)"));
                }
                return success;
            }
            catch (Exception exception)
            {
                AppLog.Write("[窗口恢复] 设置浏览器任务栏身份失败", exception);
                return false;
            }
            finally
            {
                if (stringPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(stringPointer);
                }
                ReleaseComObject(store);
            }
        }

        private static PropertyKey CreateAppUserModelIdKey()
        {
            PropertyKey key = new PropertyKey();
            key.FormatId = AppUserModelIdKeyFormat;
            key.PropertyId = AppUserModelIdKeyPid;
            return key;
        }

        private static string ReadString(PropVariant value)
        {
            if (value.VariantType != VariantTypeString || value.Pointer == IntPtr.Zero)
            {
                return null;
            }
            return Marshal.PtrToStringUni(value.Pointer);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.FinalReleaseComObject(value);
                }
                catch
                {
                }
            }
        }
    }
}
