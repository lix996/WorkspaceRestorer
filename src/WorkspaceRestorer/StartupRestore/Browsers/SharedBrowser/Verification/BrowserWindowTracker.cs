using System;
using System.Collections.Generic;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 恢复过程中“浏览器窗口 key -> 第一页认领到的 HWND”的唯一存放处，
    /// 以及本次恢复已经认领的窗口集合。
    /// 不放窗口位置/缩窗逻辑，只负责记忆、查询和认领，避免句柄状态散在 Shared。
    /// </summary>
    internal static class BrowserWindowTracker
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, IntPtr> Handles =
            new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ClaimLock = new object();
        private static readonly HashSet<IntPtr> Claimed = new HashSet<IntPtr>();


        internal static void Remember(string key, IntPtr handle)
        {
            if (string.IsNullOrWhiteSpace(key) || handle == IntPtr.Zero)
            {
                return;
            }
            lock (Sync)
            {
                Handles[key] = handle;
            }
        }

        internal static IntPtr Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return IntPtr.Zero;
            }
            lock (Sync)
            {
                IntPtr handle;
                return Handles.TryGetValue(key, out handle) ? handle : IntPtr.Zero;
            }
        }

        internal static HashSet<IntPtr> GetExcept(string key)
        {
            HashSet<IntPtr> result = new HashSet<IntPtr>();
            lock (Sync)
            {
                foreach (KeyValuePair<string, IntPtr> pair in Handles)
                {
                    if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                        && pair.Value != IntPtr.Zero)
                    {
                        result.Add(pair.Value);
                    }
                }
            }
            return result;
        }

        internal static List<IntPtr> Clear()
        {
            List<IntPtr> removed = new List<IntPtr>();
            lock (Sync)
            {
                removed.AddRange(Handles.Values);
                Handles.Clear();
            }
            lock (ClaimLock)
            {
                Claimed.Clear();
            }
            return removed;
        }

        internal static bool ClaimForRestore(string restoreKey, IntPtr windowHandle)
        {
            if (string.IsNullOrWhiteSpace(restoreKey) || windowHandle == IntPtr.Zero)
            {
                return false;
            }
            lock (ClaimLock)
            {
                RemoveClosedClaims();
                IntPtr previous = Get(restoreKey);
                if (previous != IntPtr.Zero
                    && previous != windowHandle
                    && !GetExcept(restoreKey).Contains(previous))
                {
                    Claimed.Remove(previous);
                }

                IntPtr claimed = Get(restoreKey);
                if (Claimed.Contains(windowHandle) && claimed != windowHandle)
                {
                    return false;
                }
                Set(restoreKey, windowHandle);
                Claimed.Add(windowHandle);
                return true;
            }
        }

        internal static bool IsClaimed(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            lock (ClaimLock)
            {
                RemoveClosedClaims();
                return Claimed.Contains(windowHandle);
            }
        }

        internal static bool TryClaim(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            lock (ClaimLock)
            {
                RemoveClosedClaims();
                return Claimed.Add(windowHandle);
            }
        }

        internal static void RemoveClaim(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }
            lock (ClaimLock)
            {
                Claimed.Remove(windowHandle);
            }
        }

        private static void RemoveClosedClaims()
        {
            List<IntPtr> closed = null;
            foreach (IntPtr windowHandle in Claimed)
            {
                if (!NativeMethods.IsWindow(windowHandle))
                {
                    if (closed == null)
                    {
                        closed = new List<IntPtr>();
                    }
                    closed.Add(windowHandle);
                }
            }
            if (closed == null)
            {
                return;
            }
            for (int index = 0; index < closed.Count; index++)
            {
                Claimed.Remove(closed[index]);
            }
        }

        internal static void Remove(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            lock (Sync)
            {
                Handles.Remove(key);
            }
        }

        internal static void Set(string key, IntPtr handle)
        {
            Remember(key, handle);
        }
    }
}
