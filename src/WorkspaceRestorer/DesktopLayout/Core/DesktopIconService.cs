using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkspaceRestorer.Desktop
{
    /// <summary>
    /// Captures and restores the Explorer desktop ListView.  The implementation
    /// intentionally uses late-bound Shell.Application automation so that the
    /// product does not need a Shell32 interop assembly at build time.  The
    /// ListView path is used for positions because it is available on both
    /// Windows 10 and Windows 11; shell automation supplies stable identities.
    /// </summary>
    public class DesktopIconService
    {
        private const int SsfDesktop = 0;
        private const int LvmFirst = 0x1000;
        private const int LvmGetItemCount = LvmFirst + 4;
        private const int LvmGetItemPosition = LvmFirst + 16;
        private const int LvmSetItemPosition = LvmFirst + 15;
        // LVM_SETITEMPOSITION32 is WM_USER + 49 (the +50 message is
        // LVM_GETITEMSPACING on the current common-controls headers).
        private const int LvmSetItemPosition32 = LvmFirst + 49;
        private const int LvmGetItemSpacing = LvmFirst + 51;
        private const int LvmGetItemTextW = LvmFirst + 115;
        private const int LvsAutoArrange = 0x0100;
        private const int GwlStyle = -16;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        private readonly DisplayTopologyService topologyService;

        public DesktopIconService()
            : this(new DisplayTopologyService())
        {
        }

        public DesktopIconService(DisplayTopologyService topologyService)
        {
            this.topologyService = topologyService ?? new DisplayTopologyService();
        }

        /// <summary>Capture the current display topology and desktop items.</summary>
        public virtual DesktopShellSnapshot Capture()
        {
            DisplayTopology topology;
            try
            {
                topology = topologyService.Capture();
            }
            catch
            {
                topology = new DisplayTopology();
            }

            return Capture(topology);
        }

        /// <summary>Capture using a caller supplied topology (useful for tests).</summary>
        public virtual DesktopShellSnapshot Capture(DisplayTopology topology)
        {
            if (topology == null)
            {
                topology = new DisplayTopology();
            }

            DesktopShellSnapshot snapshot = new DesktopShellSnapshot();
            snapshot.Topology = topology;
            snapshot.CapturedUtc = DateTime.UtcNow;

            IntPtr listView = FindDesktopListView();
            Dictionary<string, DesktopFileInfo> desktopFiles = EnumerateDesktopFiles();
            List<ShellItemInfo> shellItems = EnumerateDesktopItems(desktopFiles);
            if (listView == IntPtr.Zero)
            {
                // Explorer can be restarting (or a headless test host can be
                // running).  Identities are still useful even when positions
                // are temporarily unavailable.
                for (int index = 0; index < shellItems.Count; index++)
                {
                    DesktopIconPlacement placement = CreatePlacement(shellItems[index], 0, 0, topology);
                    snapshot.Icons.Add(placement);
                }
                snapshot.AutoArrangeEnabled = false;
                return snapshot;
            }

            int count = SafeGetItemCount(listView);
            snapshot.AutoArrangeEnabled = IsAutoArrangeEnabled(listView);
            int gridWidth;
            int gridHeight;
            GetItemSpacing(listView, out gridWidth, out gridHeight);
            snapshot.GridWidth = gridWidth;
            snapshot.GridHeight = gridHeight;
            if (snapshot.GridWidth <= 0)
            {
                snapshot.GridWidth = 75;
            }
            if (snapshot.GridHeight <= 0)
            {
                snapshot.GridHeight = 75;
            }

            List<string> listViewNames = new List<string>();
            for (int index = 0; index < count; index++)
            {
                Point point;
                if (!TryGetItemPosition(listView, index, out point))
                {
                    point = new Point(0, 0);
                }

                string listName = GetItemText(listView, index);
                listViewNames.Add(listName);
                ShellItemInfo item = FindShellItem(shellItems, index, listName);
                if (item == null)
                {
                    item = new ShellItemInfo();
                    item.Name = string.IsNullOrEmpty(listName) ? "Desktop item " + index.ToString(CultureInfo.InvariantCulture) : listName;
                    item.Identity = BuildFallbackIdentity(item.Name, index);
                    FillDesktopFileInfo(item, desktopFiles);
                }

                snapshot.Icons.Add(CreatePlacement(item, point.X, point.Y, topology));
            }

            // A shell view can expose an item through automation while the
            // ListView is in the middle of a refresh.  Keep those identities in
            // the snapshot with a safe, non-overlapping position instead of
            // silently losing them.
            for (int index = 0; index < shellItems.Count; index++)
            {
                ShellItemInfo item = shellItems[index];
                if (ContainsIdentity(snapshot.Icons, item.Identity))
                {
                    continue;
                }

                Point spare = FindSparePoint(snapshot.Icons, topology, snapshot.GridWidth, snapshot.GridHeight);
                snapshot.Icons.Add(CreatePlacement(item, spare.X, spare.Y, topology));
            }

            return snapshot;
        }

        /// <summary>
        /// Applies a previously calculated plan.  TEST_MODE and the explicit
        /// disable policy never call a mutating Win32 API; they return a plan
        /// result so callers can display/verify what would have happened.
        /// </summary>
        public virtual DesktopRestoreResult Restore(DesktopRestorePlan plan)
        {
            DesktopRestoreResult result = new DesktopRestoreResult();
            if (plan == null)
            {
                result.ErrorMessage = "Restore plan is null.";
                return result;
            }

            result.RequestedCount = plan.Items == null ? 0 : plan.Items.Count;
            IntPtr listView = FindDesktopListView();
            result.AutoArrangeEnabled = listView != IntPtr.Zero && IsAutoArrangeEnabled(listView);

            if (IsMutationDisabled())
            {
                result.SkippedByPolicy = true;
                result.Applied = false;
                AddAllMissingAsDryRun(result, plan);
                return result;
            }

            if (listView == IntPtr.Zero)
            {
                result.ErrorMessage = "Explorer desktop ListView is not available.";
                AddAllMissingAsDryRun(result, plan);
                return result;
            }

            try
            {
                List<ShellItemInfo> shellItems = EnumerateDesktopItems(EnumerateDesktopFiles());
                int count = SafeGetItemCount(listView);
                Dictionary<string, int> identityToIndex = BuildIdentityIndex(shellItems, listView, count);

                // Explorer's auto-arrange flag makes LVM_SETITEMPOSITION a no-op
                // or immediately reflows the icons.  Remove only the style bit;
                // the caller can save the next snapshot to preserve the user's
                // chosen mode.  This is deliberately skipped in dry-run mode.
                if (result.AutoArrangeEnabled)
                {
                    SetAutoArrange(listView, false);
                    result.AutoArrangeEnabled = false;
                }

                if (plan.Items != null)
                {
                    for (int index = 0; index < plan.Items.Count; index++)
                    {
                        DesktopRestoreItem item = plan.Items[index];
                        int listIndex;
                        string identity = item == null ? string.Empty : DesktopIdentity.Normalize(item.Identity);
                        if (item == null || string.IsNullOrEmpty(identity)
                            || !identityToIndex.TryGetValue(identity, out listIndex))
                        {
                            if (item != null && !string.IsNullOrEmpty(item.Identity))
                            {
                                result.MissingIdentities.Add(item.Identity);
                            }
                            continue;
                        }
                        // 未放置的新图标哨兵：不是真实坐标，保持它现在的位置。
                        if (item.X < -999000 || item.Y < -999000)
                        {
                            continue;
                        }

                        if (TrySetItemPosition(listView, listIndex, item.X, item.Y))
                        {
                            result.AppliedCount++;
                        }
                        else
                        {
                            result.MissingIdentities.Add(item.Identity);
                        }
                    }
                }

                result.Applied = result.AppliedCount > 0 || result.RequestedCount == 0;
                return result;
            }
            catch (Exception exception)
            {
                result.ErrorMessage = exception.Message;
                result.Applied = result.AppliedCount > 0;
                return result;
            }
        }

        /// <summary>Convenience entry point for clients that already have items.</summary>
        public virtual DesktopRestoreResult RestoreItems(IList<DesktopRestoreItem> items)
        {
            DesktopRestorePlan plan = new DesktopRestorePlan();
            if (items != null)
            {
                plan.Items = new List<DesktopRestoreItem>(items);
            }
            return Restore(plan);
        }

        public virtual bool IsAutoArrangeEnabled()
        {
            IntPtr listView = FindDesktopListView();
            return listView != IntPtr.Zero && IsAutoArrangeEnabled(listView);
        }

        /// <summary>Returns the desktop ListView handle, primarily for diagnostics.</summary>
        public virtual IntPtr GetDesktopListViewHandle()
        {
            return FindDesktopListView();
        }

        private static bool IsMutationDisabled()
        {
            return IsTruthy(Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DISABLE_OS_CHANGES"))
                || IsTruthy(Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"));
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim();
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddAllMissingAsDryRun(DesktopRestoreResult result, DesktopRestorePlan plan)
        {
            if (plan.Items == null)
            {
                return;
            }
            for (int index = 0; index < plan.Items.Count; index++)
            {
                DesktopRestoreItem item = plan.Items[index];
                if (item != null && !string.IsNullOrEmpty(item.Identity))
                {
                    result.MissingIdentities.Add(item.Identity);
                }
            }
        }

        private static DesktopIconPlacement CreatePlacement(ShellItemInfo item, int x, int y, DisplayTopology topology)
        {
            DesktopIconPlacement placement = new DesktopIconPlacement();
            placement.Identity = item.Identity ?? string.Empty;
            placement.DisplayName = item.Name ?? string.Empty;
            placement.CreatedUtc = item.CreatedUtc;
            placement.X = x;
            placement.Y = y;

            Point screenPoint = ToScreenPoint(topology, x, y);
            DisplayMonitor monitor = FindMonitorForPoint(topology, screenPoint.X, screenPoint.Y);
            if (monitor == null && topology != null)
            {
                monitor = topology.GetPrimaryMonitor();
            }
            if (monitor != null)
            {
                placement.MonitorKey = monitor.StableKey ?? string.Empty;
                placement.MonitorOffsetX = screenPoint.X - monitor.Bounds.Left;
                placement.MonitorOffsetY = screenPoint.Y - monitor.Bounds.Top;
                placement.NormalizedX = monitor.Bounds.Width <= 0 ? 0.0 : (double)placement.MonitorOffsetX / monitor.Bounds.Width;
                placement.NormalizedY = monitor.Bounds.Height <= 0 ? 0.0 : (double)placement.MonitorOffsetY / monitor.Bounds.Height;
            }
            return placement;
        }

        private static DisplayMonitor FindMonitorForPoint(DisplayTopology topology, int x, int y)
        {
            if (topology == null || topology.Monitors == null)
            {
                return null;
            }
            for (int index = 0; index < topology.Monitors.Count; index++)
            {
                DisplayMonitor monitor = topology.Monitors[index];
                if (monitor != null && monitor.Bounds != null && monitor.Bounds.Contains(x, y))
                {
                    return monitor;
                }
            }
            return null;
        }

        private static Point ToScreenPoint(DisplayTopology topology, int viewX, int viewY)
        {
            if (topology == null || topology.VirtualBounds == null)
            {
                return new Point(viewX, viewY);
            }
            return new Point(viewX + topology.VirtualBounds.Left, viewY + topology.VirtualBounds.Top);
        }

        private static Point ToViewPoint(DisplayTopology topology, int screenX, int screenY)
        {
            if (topology == null || topology.VirtualBounds == null)
            {
                return new Point(screenX, screenY);
            }
            return new Point(screenX - topology.VirtualBounds.Left, screenY - topology.VirtualBounds.Top);
        }

        private static Point FindSparePoint(IList<DesktopIconPlacement> placements, DisplayTopology topology, int gridWidth, int gridHeight)
        {
            DisplayMonitor monitor = topology == null ? null : topology.GetPrimaryMonitor();
            DesktopRect area = monitor == null ? (topology == null ? new DesktopRect(0, 0, 1920, 1080) : topology.VirtualBounds) : monitor.WorkingArea;
            if (area == null || area.Width <= 0 || area.Height <= 0)
            {
                area = new DesktopRect(0, 0, 1920, 1080);
            }
            if (gridWidth <= 0)
            {
                gridWidth = 75;
            }
            if (gridHeight <= 0)
            {
                gridHeight = 75;
            }

            int columns = Math.Max(1, area.Width / gridWidth);
            int rows = Math.Max(1, area.Height / gridHeight);
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    int screenX = area.Left + column * gridWidth;
                    int screenY = area.Top + row * gridHeight;
                    Point viewPoint = ToViewPoint(topology, screenX, screenY);
                    bool occupied = false;
                    if (placements != null)
                    {
                        for (int p = 0; p < placements.Count; p++)
                        {
                            DesktopIconPlacement existing = placements[p];
                            if (existing != null && Math.Abs(existing.X - viewPoint.X) < Math.Max(1, gridWidth / 2)
                                && Math.Abs(existing.Y - viewPoint.Y) < Math.Max(1, gridHeight / 2))
                            {
                                occupied = true;
                                break;
                            }
                        }
                    }
                    if (!occupied)
                    {
                        return viewPoint;
                    }
                }
            }
            return ToViewPoint(topology, area.Left, area.Top);
        }

        private static bool ContainsIdentity(IList<DesktopIconPlacement> placements, string identity)
        {
            if (placements == null || string.IsNullOrEmpty(identity))
            {
                return false;
            }
            for (int index = 0; index < placements.Count; index++)
            {
                if (placements[index] != null && DesktopIdentity.Equals(placements[index].Identity, identity))
                {
                    return true;
                }
            }
            return false;
        }

        private static ShellItemInfo FindShellItem(IList<ShellItemInfo> shellItems, int index, string displayName)
        {
            if (shellItems == null || shellItems.Count == 0)
            {
                return null;
            }
            if (index >= 0 && index < shellItems.Count)
            {
                ShellItemInfo direct = shellItems[index];
                if (string.IsNullOrEmpty(displayName) || string.Equals(direct.Name, displayName, StringComparison.OrdinalIgnoreCase))
                {
                    return direct;
                }
            }
            if (!string.IsNullOrEmpty(displayName))
            {
                for (int itemIndex = 0; itemIndex < shellItems.Count; itemIndex++)
                {
                    if (string.Equals(shellItems[itemIndex].Name, displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        return shellItems[itemIndex];
                    }
                }
            }
            return index >= 0 && index < shellItems.Count ? shellItems[index] : null;
        }

        /// <summary>
        /// 按显示名把桌面文件/文件夹对到真实路径和创建时间。
        /// Shell.Application 的 Path 偶尔为空（桌面刚刷新、虚拟项、权限隔离），
        /// 这时直接枚举用户桌面和公共桌面兜底；拿不到创建时间不影响位置保存。
        /// </summary>
        private static void FillDesktopFileInfo(
            ShellItemInfo item,
            Dictionary<string, DesktopFileInfo> desktopFiles)
        {
            if (item == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(item.Path))
            {
                DateTime created = ReadCreationUtc(item.Path);
                if (created != DateTime.MinValue)
                {
                    item.CreatedUtc = created;
                    return;
                }
            }
            if (desktopFiles == null || string.IsNullOrWhiteSpace(item.Name))
            {
                return;
            }
            DesktopFileInfo info;
            if (desktopFiles.TryGetValue(item.Name.Trim(), out info) && info != null)
            {
                item.Path = info.Path;
                item.CreatedUtc = info.CreatedUtc;
            }
        }

        private static DateTime ReadCreationUtc(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DateTime.MinValue;
            }
            try
            {
                if (Directory.Exists(path))
                {
                    return Directory.GetCreationTimeUtc(path);
                }
                if (File.Exists(path))
                {
                    return File.GetCreationTimeUtc(path);
                }
            }
            catch
            {
            }
            return DateTime.MinValue;
        }

        private static Dictionary<string, DesktopFileInfo> EnumerateDesktopFiles()
        {
            Dictionary<string, DesktopFileInfo> result =
                new Dictionary<string, DesktopFileInfo>(StringComparer.OrdinalIgnoreCase);
            List<string> directories = new List<string>();
            AddExistingDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            AddExistingDirectory(directories, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            string oneDrive = Environment.GetEnvironmentVariable("OneDrive");
            if (!string.IsNullOrWhiteSpace(oneDrive))
            {
                AddExistingDirectory(directories, Path.Combine(oneDrive, "Desktop"));
                AddExistingDirectory(directories, Path.Combine(oneDrive, "桌面"));
            }
            for (int directoryIndex = 0; directoryIndex < directories.Count; directoryIndex++)
            {
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(directories[directoryIndex]);
                }
                catch
                {
                    continue;
                }
                for (int index = 0; index < entries.Length; index++)
                {
                    string path = entries[index];
                    try
                    {
                        string name = Path.GetFileName(path);
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }
                        DesktopFileInfo info = new DesktopFileInfo();
                        info.Path = path;
                        info.CreatedUtc = ReadCreationUtc(path);
                        AddDesktopFileKey(result, name, info);
                        // Windows 默认隐藏已知扩展名：桌面显示“新建文本文档”，
                        // 实际文件是“新建文本文档.txt”，两种名字都要能查到。
                        string withoutExtension = Path.GetFileNameWithoutExtension(path);
                        if (!string.IsNullOrWhiteSpace(withoutExtension)
                            && !string.Equals(withoutExtension, name, StringComparison.OrdinalIgnoreCase))
                        {
                            AddDesktopFileKey(result, withoutExtension, info);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            return result;
        }

        private static void AddExistingDirectory(List<string> directories, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }
            for (int index = 0; index < directories.Count; index++)
            {
                if (string.Equals(directories[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            directories.Add(path);
        }

        private static void AddDesktopFileKey(
            Dictionary<string, DesktopFileInfo> files,
            string key,
            DesktopFileInfo info)
        {
            if (files == null || info == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }
            // 用户桌面优先；公共桌面只补用户桌面没有的名字。
            if (!files.ContainsKey(key))
            {
                files.Add(key, info);
            }
        }

        private static List<ShellItemInfo> EnumerateDesktopItems(
            Dictionary<string, DesktopFileInfo> desktopFiles)
        {
            List<ShellItemInfo> result = new List<ShellItemInfo>();
            object shell = null;
            object folder = null;
            object items = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return result;
                }
                shell = Activator.CreateInstance(shellType);
                folder = Invoke(shell, "NameSpace", SsfDesktop);
                if (folder == null)
                {
                    return result;
                }
                items = GetProperty(folder, "Items");
                int count = ToInt(GetProperty(items, "Count"));
                for (int index = 0; index < count; index++)
                {
                    object item = null;
                    try
                    {
                        item = Invoke(items, "Item", index);
                        if (item == null)
                        {
                            continue;
                        }
                        string name = Convert.ToString(GetProperty(item, "Name"), CultureInfo.InvariantCulture) ?? string.Empty;
                        string path = Convert.ToString(GetProperty(item, "Path"), CultureInfo.InvariantCulture) ?? string.Empty;
                        ShellItemInfo info = new ShellItemInfo();
                        info.Name = name;
                        info.Path = path;
                        info.Identity = BuildIdentity(path, name, index);
                        FillDesktopFileInfo(info, desktopFiles);
                        result.Add(info);
                    }
                    catch
                    {
                        // One malformed/virtual shell item must not hide the
                        // remaining desktop entries.
                    }
                    finally
                    {
                        ReleaseCom(item);
                    }
                }
            }
            catch
            {
                // Explorer may be unavailable during logon/restart.  Returning
                // an empty list lets the caller retry on the next capture.
            }
            finally
            {
                ReleaseCom(items);
                ReleaseCom(folder);
                ReleaseCom(shell);
            }
            return result;
        }

        private static string BuildIdentity(string path, string name, int index)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                path = path.Trim();
                try
                {
                    if (path.StartsWith("::", StringComparison.Ordinal))
                    {
                        return "shell:" + path.ToUpperInvariant();
                    }
                    return "path:" + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
                }
                catch
                {
                    return "path:" + path.ToUpperInvariant();
                }
            }
            return BuildFallbackIdentity(name, index);
        }

        private static string BuildFallbackIdentity(string name, int index)
        {
            // 名称在桌面上是唯一的；不要带 ListView 序号，序号会随新增/移动图标漂移。
            string value = (name ?? string.Empty).Trim().ToUpperInvariant();
            return "name:" + value;
        }

        private static object GetProperty(object target, string name)
        {
            if (target == null)
            {
                return null;
            }
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);
        }

        private static object Invoke(object target, string name, params object[] arguments)
        {
            if (target == null)
            {
                return null;
            }
            try
            {
                return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, arguments, CultureInfo.InvariantCulture);
            }
            catch (MissingMethodException)
            {
                return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture);
            }
        }

        private static int ToInt(object value)
        {
            if (value == null)
            {
                return 0;
            }
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0;
            }
        }

        private static void ReleaseCom(object value)
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

        private static IntPtr FindDesktopListView()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr view = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            if (list != IntPtr.Zero)
            {
                return list;
            }

            // On Windows 10/11 the view is commonly hosted by a WorkerW.
            List<IntPtr> workers = new List<IntPtr>();
            Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                IntPtr candidateView = Native.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                IntPtr candidateList = Native.FindWindowEx(candidateView, IntPtr.Zero, "SysListView32", null);
                if (candidateList != IntPtr.Zero)
                {
                    workers.Add(candidateList);
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return workers.Count == 0 ? IntPtr.Zero : workers[0];
        }

        private static int SafeGetItemCount(IntPtr listView)
        {
            try
            {
                IntPtr value = Native.SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero);
                long count = value.ToInt64();
                if (count < 0 || count > 100000)
                {
                    return 0;
                }
                return (int)count;
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsAutoArrangeEnabled(IntPtr listView)
        {
            long style = GetWindowLongPtr(listView, GwlStyle).ToInt64();
            return (style & LvsAutoArrange) != 0;
        }

        private static void SetAutoArrange(IntPtr listView, bool enabled)
        {
            long style = GetWindowLongPtr(listView, GwlStyle).ToInt64();
            long updated = enabled ? (style | LvsAutoArrange) : (style & ~((long)LvsAutoArrange));
            SetWindowLongPtr(listView, GwlStyle, new IntPtr(updated));
            Native.SetWindowPos(listView, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
        }

        private static void GetItemSpacing(IntPtr listView, out int width, out int height)
        {
            width = 0;
            height = 0;
            IntPtr packed = Native.SendMessage(listView, LvmGetItemSpacing, IntPtr.Zero, IntPtr.Zero);
            long value = packed.ToInt64();
            width = unchecked((short)(value & 0xffff));
            height = unchecked((short)((value >> 16) & 0xffff));
        }

        private static bool TryGetItemPosition(IntPtr listView, int index, out Point point)
        {
            point = new Point();
            uint processId;
            Native.GetWindowThreadProcessId(listView, out processId);
            IntPtr process = Native.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return false;
            }
            IntPtr remote = IntPtr.Zero;
            try
            {
                remote = Native.VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(8), MemCommit | MemReserve, PageReadWrite);
                if (remote == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr result = Native.SendMessage(listView, LvmGetItemPosition, new IntPtr(index), remote);
                if (result == IntPtr.Zero)
                {
                    // Some Explorer builds return zero even on success, so
                    // trust ReadProcessMemory and validate the number of bytes.
                }
                byte[] bytes = new byte[8];
                UIntPtr read;
                if (!Native.ReadProcessMemory(process, remote, bytes, new UIntPtr((uint)bytes.Length), out read)
                    || read.ToUInt32() < 8)
                {
                    return false;
                }
                point.X = BitConverter.ToInt32(bytes, 0);
                point.Y = BitConverter.ToInt32(bytes, 4);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (remote != IntPtr.Zero)
                {
                    Native.VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
                }
                Native.CloseHandle(process);
            }
        }

        private static string GetItemText(IntPtr listView, int index)
        {
            uint processId;
            Native.GetWindowThreadProcessId(listView, out processId);
            IntPtr process = Native.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return string.Empty;
            }
            IntPtr remoteItem = IntPtr.Zero;
            IntPtr remoteText = IntPtr.Zero;
            try
            {
                const int charCount = 512;
                int itemSize = Marshal.SizeOf(typeof(RemoteLvItem));
                remoteItem = Native.VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)itemSize), MemCommit | MemReserve, PageReadWrite);
                remoteText = Native.VirtualAllocEx(process, IntPtr.Zero, new UIntPtr((uint)(charCount * 2)), MemCommit | MemReserve, PageReadWrite);
                if (remoteItem == IntPtr.Zero || remoteText == IntPtr.Zero)
                {
                    return string.Empty;
                }
                RemoteLvItem item = new RemoteLvItem();
                item.iItem = index;
                item.pszText = remoteText;
                item.cchTextMax = charCount;
                byte[] itemBytes = StructureToBytes(item);
                UIntPtr written;
                if (!Native.WriteProcessMemory(process, remoteItem, itemBytes, new UIntPtr((uint)itemBytes.Length), out written))
                {
                    return string.Empty;
                }
                Native.SendMessage(listView, LvmGetItemTextW, new IntPtr(index), remoteItem);
                byte[] textBytes = new byte[charCount * 2];
                UIntPtr read;
                if (!Native.ReadProcessMemory(process, remoteText, textBytes, new UIntPtr((uint)textBytes.Length), out read))
                {
                    return string.Empty;
                }
                return Encoding.Unicode.GetString(textBytes).TrimEnd('\0');
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (remoteText != IntPtr.Zero)
                {
                    Native.VirtualFreeEx(process, remoteText, UIntPtr.Zero, MemRelease);
                }
                if (remoteItem != IntPtr.Zero)
                {
                    Native.VirtualFreeEx(process, remoteItem, UIntPtr.Zero, MemRelease);
                }
                Native.CloseHandle(process);
            }
        }

        private static byte[] StructureToBytes(RemoteLvItem item)
        {
            int size = Marshal.SizeOf(typeof(RemoteLvItem));
            byte[] bytes = new byte[size];
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(item, memory, false);
                Marshal.Copy(memory, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(memory);
            }
            return bytes;
        }

        private static Dictionary<string, int> BuildIdentityIndex(IList<ShellItemInfo> shellItems, IntPtr listView, int listCount)
        {
            Dictionary<string, int> result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < listCount; index++)
            {
                string name = GetItemText(listView, index);
                ShellItemInfo item = FindShellItem(shellItems, index, name);
                string identity = DesktopIdentity.Normalize(item == null ? BuildFallbackIdentity(name, index) : item.Identity);
                if (!string.IsNullOrEmpty(identity) && !result.ContainsKey(identity))
                {
                    result.Add(identity, index);
                }
            }
            // If remote ListView memory is inaccessible, shell order remains a
            // useful fallback and is what Explorer normally exposes.
            if (result.Count == 0 && shellItems != null)
            {
                for (int index = 0; index < shellItems.Count && index < listCount; index++)
                {
                    string identity = DesktopIdentity.Normalize(shellItems[index].Identity);
                    if (!result.ContainsKey(identity))
                    {
                        result.Add(identity, index);
                    }
                }
            }
            return result;
        }

        private static bool TrySetItemPosition(IntPtr listView, int index, int x, int y)
        {
            uint processId;
            Native.GetWindowThreadProcessId(listView, out processId);
            IntPtr process = Native.OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
            if (process != IntPtr.Zero)
            {
                IntPtr remote = IntPtr.Zero;
                try
                {
                    remote = Native.VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(8), MemCommit | MemReserve, PageReadWrite);
                    if (remote != IntPtr.Zero)
                    {
                        byte[] bytes = new byte[8];
                        Buffer.BlockCopy(BitConverter.GetBytes(x), 0, bytes, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(y), 0, bytes, 4, 4);
                        UIntPtr written;
                        if (Native.WriteProcessMemory(process, remote, bytes, new UIntPtr((uint)bytes.Length), out written)
                            && written.ToUInt32() >= 8)
                        {
                            // The 32-bit form takes a pointer to POINT in the
                            // ListView process, which is required for monitor
                            // coordinates outside the signed 16-bit range.
                            Native.SendMessage(listView, LvmSetItemPosition32, new IntPtr(index), remote);
                            Point check;
                            if (TryGetItemPosition(listView, index, out check)
                                && Math.Abs(check.X - x) <= 1 && Math.Abs(check.Y - y) <= 1)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (remote != IntPtr.Zero)
                    {
                        Native.VirtualFreeEx(process, remote, UIntPtr.Zero, MemRelease);
                    }
                    Native.CloseHandle(process);
                }
            }

            // Older common-controls versions only implement the 16-bit message.
            int packed16 = ((y & 0xffff) << 16) | (x & 0xffff);
            IntPtr result = Native.SendMessage(listView, LvmSetItemPosition, new IntPtr(index), new IntPtr(packed16));
            return result != IntPtr.Zero;
        }

        private static IntPtr GetWindowLongPtr(IntPtr window, int index)
        {
            return IntPtr.Size == 8 ? Native.GetWindowLongPtr64(window, index) : new IntPtr(Native.GetWindowLong32(window, index));
        }

        private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value)
        {
            return IntPtr.Size == 8 ? Native.SetWindowLongPtr64(window, index, value) : new IntPtr(Native.SetWindowLong32(window, index, value.ToInt32()));
        }

        private sealed class ShellItemInfo
        {
            internal string Identity;
            internal string Name;
            internal string Path;
            internal DateTime CreatedUtc;
        }

        private sealed class DesktopFileInfo
        {
            internal string Path;
            internal DateTime CreatedUtc;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RemoteLvItem
        {
            internal uint mask;
            internal int iItem;
            internal int iSubItem;
            internal uint state;
            internal uint stateMask;
            internal IntPtr pszText;
            internal int cchTextMax;
            internal int iImage;
            internal IntPtr lParam;
            internal int iIndent;
            internal int iGroupId;
            internal uint cColumns;
            internal IntPtr puColumns;
            internal IntPtr piColFmt;
            internal int iGroup;
        }

        private struct Point
        {
            internal int X;
            internal int Y;

            internal Point(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        private static class Native
        {
            internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr FindWindow(string className, string windowName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string windowName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
            internal static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

            [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
            internal static extern int GetWindowLong32(IntPtr window, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
            internal static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

            [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
            internal static extern int SetWindowLong32(IntPtr window, int index, int value);

            [DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint freeType);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);
        }
    }
}
