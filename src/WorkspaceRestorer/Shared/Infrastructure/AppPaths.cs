using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 所有数据文件的位置。**用户可读、可手动清理**是这里的第一原则：
    /// 三个工作区各自一个文件夹，回收站也按同样三段分开，删掉哪个工作区的文件夹
    /// 就等于清掉那个工作区的数据。
    ///
    /// 目录结构（`<数据根>` 见 <see cref="DataRoot"/>）：
    /// <code>
    /// workflows\                         三个工作区的数据
    ///   StartupRestore\工作方案.json     工作方案目录（名称、顺序、当前方案）
    ///   StartupRestore\StartupProfiles\  每份工作方案一个文件夹
    ///   DesktopLayout\布局档案.xml       桌面图标布局
    ///   DesktopLayout\Layouts\           每份布局一个文件夹
    ///   Applications\                    自启动应用
    /// RecycleBin\                        删掉的内容先放这里，可恢复
    ///   StartupRestore\、DesktopLayout\、Applications\
    /// Settings\settings.json             全局设置
    /// Logs\                              运行日志
    /// Backup\                            覆盖前的自动备份（意外清空还能找回）
    /// Temp\                              打开程序时的本次开机标记
    /// </code>
    /// 说明.txt 是这台电脑的本地文档，不参与运行时目录创建。
    /// </summary>
    internal static class AppPaths
    {
        private const string DataDirectoryEnvironmentVariable = "WORKSPACE_RESTORER_DATA_DIR";
        private const string DisableOsChangesEnvironmentVariable = "WORKSPACE_RESTORER_DISABLE_OS_CHANGES";

        /// <summary>
        /// 固定目录名。具体用哪一套由构建变体决定（`yuanwenjian\variants\*.json`），
        /// 生成到 `ActiveDataLayout`：en-US 是 StartupRestore/StartupProfiles…，
        /// zh-CN 是 启动与恢复/工作方案…。代码里不要再写死这些名字。
        /// 回收站、无法识别目录里的分段名也复用这三个常量。
        /// </summary>
        public const string WorkflowsDirectoryName = ActiveDataLayout.Workflows;
        public const string StartupRestoreDirectoryName = ActiveDataLayout.StartupRestore;
        public const string DesktopLayoutDirectoryName = ActiveDataLayout.DesktopLayout;
        public const string ApplicationsDirectoryName = ActiveDataLayout.Applications;
        public const string StartupProfilesDirectoryName = ActiveDataLayout.StartupProfiles;
        public const string LayoutsDirectoryName = ActiveDataLayout.Layouts;
        public const string ApplicationPlansDirectoryName = ActiveDataLayout.ApplicationPlans;
        public const string RecycleBinDirectoryName = ActiveDataLayout.RecycleBin;
        public const string SettingsDirectoryName = ActiveDataLayout.Settings;
        public const string LogsDirectoryName = ActiveDataLayout.Logs;
        public const string BackupDirectoryName = ActiveDataLayout.Backup;
        public const string TempDirectoryName = ActiveDataLayout.Temp;
        public const string AutoRestoreDirectoryName = ActiveDataLayout.AutoRestore;
        public const string StartupProfileBackupsDirectoryName = ActiveDataLayout.StartupProfileBackups;
        public const string LegacyBackupDirectoryName = ActiveDataLayout.Legacy;

        /// <summary>
        /// 认不出来的旧文件夹搬到这里（按工作区分三段），程序不删、只挪。
        /// 程序**不会**主动创建这个目录，等真有搬不动的文件夹时才建。
        /// </summary>
        public const string UnrecognizedDirectoryName = ActiveDataLayout.Unrecognized;

        internal const string StartupRestoreKind = "启动与恢复";
        internal const string DesktopLayoutKind = "图标位置保存";
        internal const string ApplicationsKind = "自启动应用";

        /// <summary>
        /// 数据根。测试/自检用 WORKSPACE_RESTORER_DATA_DIR 覆盖；
        /// 否则跟程序放在一起（exe 同级的 data 文件夹），这样整个交付文件夹可以打包带走。
        ///
        /// 【2026-10-19 用户要求】真实运行时所有“这台机器的私人数据”都放进 `data\default\`，
        /// 这样把成品文件夹交给别人之前，**删掉 `data\default` 这一个文件夹**就等于清空全部私人数据：
        /// workflows（工作方案 / 图标位置 / 自启动方案）、回收站、日志、设置、备份、临时都在里面。
        /// `data\说明.txt` 留在外面，程序不再生成/改写它（用户原话：“说明.txt就不需要重新生成了”）。
        ///
        /// 自检/探针用 `WORKSPACE_RESTORER_DATA_DIR` 指定目录时**直接用那个目录**（不再套一层 default），
        /// 这样几十个探针里写死的 workflows\ / 设置\ / 日志\ 路径都不用改。
        /// </summary>
        public static string DataRoot
        {
            get
            {
                string overridePath = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(overridePath))
                {
                    return Path.GetFullPath(overridePath);
                }
                return Path.Combine(DataContainer, "default");
            }
        }

        /// <summary>
        /// 真实运行时的 `data` 容器目录（exe 同级）；`DataRoot` 是它下面的 `default`。
        /// 只有“没有用环境变量指定数据目录”时才有意义（迁移逻辑也只在那种情况下跑）。
        /// </summary>
        private static string DataContainer
        {
            get { return Path.Combine(BaseDirectory, "data"); }
        }

        /// <summary>程序所在目录（EXE 同级）。</summary>
        public static string BaseDirectory
        {
            get
            {
                try
                {
                    string executable = System.Reflection.Assembly.GetEntryAssembly() == null
                        ? null
                        : System.Reflection.Assembly.GetEntryAssembly().Location;
                    if (!string.IsNullOrWhiteSpace(executable))
                    {
                        return Path.GetDirectoryName(executable);
                    }
                }
                catch
                {
                }
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        public static string WorkflowsRoot { get { return Path.Combine(DataRoot, WorkflowsDirectoryName); } }
        public static string RecycleBinRoot { get { return Path.Combine(DataRoot, RecycleBinDirectoryName); } }

        public static string StartupRestoreDirectory { get { return Path.Combine(WorkflowsRoot, StartupRestoreDirectoryName); } }
        public static string DesktopLayoutDirectory { get { return Path.Combine(WorkflowsRoot, DesktopLayoutDirectoryName); } }
        public static string ApplicationsDirectory { get { return Path.Combine(WorkflowsRoot, ApplicationsDirectoryName); } }

        /// <summary>「自启动应用」的方案清单（与「启动与恢复」的方案文件分开存）。</summary>
        public static string ApplicationPlansFile { get { return Path.Combine(ApplicationsDirectory, "自启动方案.json"); } }

        /// <summary>每份工作方案一个文件夹（手动删除用）。</summary>
        public static string StartupProfilesFolderRoot { get { return Path.Combine(StartupRestoreDirectory, StartupProfilesDirectoryName); } }

        public static string DesktopLayoutsDirectory { get { return Path.Combine(DesktopLayoutDirectory, LayoutsDirectoryName); } }

        /// <summary>「自启动应用」每份方案一个文件夹（手动删除用）。</summary>
        public static string ApplicationPlansFolderRoot { get { return Path.Combine(ApplicationsDirectory, ApplicationPlansDirectoryName); } }

        public static string SettingsFile { get { return Path.Combine(DataRoot, SettingsDirectoryName, "settings.json"); } }
        public static string SessionFile { get { return Path.Combine(DataRoot, SettingsDirectoryName, "last-session.json"); } }
        public static string WindowStateFile { get { return Path.Combine(DataRoot, SettingsDirectoryName, "window-state.json"); } }

        public static string StartupProfilesFile { get { return Path.Combine(StartupRestoreDirectory, "工作方案.json"); } }
        public static string DesktopLayoutFile { get { return Path.Combine(DesktopLayoutDirectory, "布局档案.xml"); } }
        public static string RecycleBinFile { get { return Path.Combine(RecycleBinRoot, "回收站设置.json"); } }
        public static string RecycleBinKindDirectory(string kind)
        {
            return Path.Combine(RecycleBinRoot, KindDirectoryName(kind));
        }

        /// <summary>“工作区种类”对应的目录名（回收站和“无法识别”目录共用同一套分段名）。</summary>
        private static string KindDirectoryName(string kind)
        {
            if (string.Equals(kind, StartupRestoreKind, StringComparison.Ordinal))
            {
                return StartupRestoreDirectoryName;
            }
            if (string.Equals(kind, DesktopLayoutKind, StringComparison.Ordinal))
            {
                return DesktopLayoutDirectoryName;
            }
            if (string.Equals(kind, ApplicationsKind, StringComparison.Ordinal))
            {
                return ApplicationsDirectoryName;
            }
            return "Other";
        }

        public static string LogDirectory { get { return Path.Combine(DataRoot, LogsDirectoryName); } }
        public static string BackupDirectory { get { return Path.Combine(DataRoot, BackupDirectoryName); } }

        /// <summary>
        /// 程序图标（交付文件，不是生成物）：`data\app.ico`。
        /// 源码里的唯一图标源是 `yuanwenjian\assets\app.ico`，Build.ps1 把它复制到这里。
        /// 它放在 **data 根下**（不是 data\default 里）：打包前删 `default` 清私人数据时不会把图标一起删掉。
        /// 探针用 `WORKSPACE_RESTORER_DATA_DIR` 跑时，找不到 `data\app.ico` 就再看那个目录里有没有。
        /// </summary>
        public static string AppIconFile
        {
            get
            {
                string besideData = Path.Combine(DataContainer, "app.ico");
                if (File.Exists(besideData))
                {
                    return besideData;
                }
                string inDataRoot = Path.Combine(DataRoot, "app.ico");
                if (File.Exists(inDataRoot))
                {
                    return inDataRoot;
                }
                return besideData;
            }
        }

        public static string StartupProfileBackupsDirectory
        {
            get { return Path.Combine(BackupDirectory, StartupProfileBackupsDirectoryName); }
        }

        public static string LegacyBackupDirectory
        {
            get { return Path.Combine(BackupDirectory, LegacyBackupDirectoryName); }
        }

        /// <summary>
        /// “无法识别”的旧文件夹的存放处：`备份\无法识别\`（英文产物是 `Backup\Unrecognized\`）。
        /// 里面按三个工作区分三段，程序只往这里搬，永远不删。
        /// 这个目录按需创建：没有认不出来的文件夹时，它不存在。
        /// </summary>
        public static string UnrecognizedBackupDirectory
        {
            get { return Path.Combine(BackupDirectory, UnrecognizedDirectoryName); }
        }

        /// <summary>“无法识别”目录里对应某个工作区的那一段。</summary>
        public static string UnrecognizedKindDirectory(string kind)
        {
            return Path.Combine(UnrecognizedBackupDirectory, KindDirectoryName(kind));
        }

        /// <summary>
        /// 临时文件夹（用户要求 2026-10-10 16:48）：
        /// “在 …\zuizhongchanwu 内生成一个临时文件，为了后续好维护和分类，
        ///  临时文件里还要有一个文件夹，专门存放‘启动程序后自动恢复工作方案’的临时文件”。
        /// 所以结构是 `临时\启动程序后自动恢复\`，里面按日期一个标记文件，
        /// **最多只留一个**；跨天但没关机时旧日期文件仍有效，只有关机/重启后才作废。
        /// </summary>
        public static string TempDirectory { get { return Path.Combine(DataRoot, TempDirectoryName); } }

        /// <summary>“启动程序后自动恢复工作方案”的标记文件所在文件夹。</summary>
        public static string AutoRestoreMarkerDirectory
        {
            get { return Path.Combine(TempDirectory, AutoRestoreDirectoryName); }
        }

        /// <summary>“搬到 workflows 之前”的旧版文件位置，只用于一次性迁移。</summary>
        public static string LegacyStartupProfilesFile { get { return Path.Combine(DataRoot, "startup-profiles.json"); } }
        public static string LegacyDesktopLayoutFile { get { return Path.Combine(DataRoot, "desktop-layouts.xml"); } }
        public static string LegacyRecycleBinFile { get { return Path.Combine(DataRoot, "recycle-bin.json"); } }
        public static string LegacySettingsFile { get { return Path.Combine(DataRoot, "settings.json"); } }
        public static string LegacySessionFile { get { return Path.Combine(DataRoot, "last-session.json"); } }
        public static string LegacyWindowStateFile { get { return Path.Combine(DataRoot, "window-state.json"); } }
        public static string LegacyLogDirectory { get { return Path.Combine(DataRoot, "logs"); } }

        /// <summary>
        /// 本次 Windows 启动时刻（程序启动时算一次就够）。
        /// 用户第 60 条：“有可能要过夜通宵的使用电脑，改为电脑重启（关机后再重新打开），
        /// 程序会自动把旧标记删掉，于是会正常恢复一次。”
        /// 判断办法：环境变量 `WORKSPACE_RESTORER_BOOT_TIME`（自检用，格式 ISO 8601）优先；
        /// 否则取 64 位 `GetTickCount64`（系统已运行的毫秒数）反推 —— 这个值从 Windows 启动开始算，
        /// 通宵跨天、关机重开都能正确反映“这一次开机是什么时候”，也不会在开机 49.7 天后回绕。
        /// </summary>
        public static DateTime BootTime
        {
            get
            {
                string overrideText = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_BOOT_TIME");
                DateTime parsed;
                if (!string.IsNullOrWhiteSpace(overrideText)
                    && DateTime.TryParse(overrideText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out parsed))
                {
                    return parsed;
                }
                try
                {
                    // Environment.TickCount 是 32 位，约 49.7 天就回绕；用户明确提到
                    // “某些电脑一开就是半年、多年不关机”，必须用 64 位开机毫秒数。
                    ulong uptime = GetTickCount64();
                    if (uptime > 0)
                    {
                        return DateTime.Now.AddMilliseconds(-(double)uptime);
                    }
                    return DateTime.Now.AddMilliseconds(-Environment.TickCount);
                }
                catch
                {
                    try
                    {
                        return DateTime.Now.AddMilliseconds(-Environment.TickCount);
                    }
                    catch
                    {
                        return DateTime.Now;
                    }
                }
            }
        }

        /// <summary>
        /// 当前 Windows **登录会话** 的 Id（TokenStatistics.AuthenticationId）。
        /// 用途：判断“电脑有没有关机/重启过”。Windows 的“快速启动”（Fast Startup）关机后
        /// 内核启动时间（BootTime）不会变，仅靠 BootTime 判断会误以为没重启；但每次关机再开机
        /// 都会重新登录，登录会话 LUID 一定变化。同一次开机里只关程序再打开，LUID 不变。
        /// 环境变量 `WORKSPACE_RESTORER_LOGON_SESSION` 可覆盖（自检模拟重启用）。
        /// </summary>
        public static string LogonSessionId
        {
            get
            {
                string overrideText = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_LOGON_SESSION");
                if (!string.IsNullOrWhiteSpace(overrideText))
                {
                    return overrideText.Trim();
                }
                try
                {
                    IntPtr token = WindowsIdentity.GetCurrent().Token;
                    int size = Marshal.SizeOf(typeof(TokenStatistics));
                    IntPtr buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        int returned;
                        if (!GetTokenInformation(token, 10 /* TokenStatistics */, buffer, size, out returned))
                        {
                            return null;
                        }
                        TokenStatistics statistics = (TokenStatistics)Marshal.PtrToStructure(buffer, typeof(TokenStatistics));
                        return statistics.AuthenticationId.HighPart.ToString("X8") + ":"
                            + statistics.AuthenticationId.LowPart.ToString("X8");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenStatistics
        {
            public Luid TokenId;
            public Luid AuthenticationId;
            public long ExpirationTime;
            public int TokenType;
            public int ImpersonationLevel;
            public uint DynamicCharged;
            public uint DynamicAvailable;
            public uint GroupCount;
            public uint PrivilegeCount;
            public Luid ModifiedId;
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        public static bool OsChangesDisabled
        {
            get
            {
                return string.Equals(
                    Environment.GetEnvironmentVariable(DisableOsChangesEnvironmentVariable),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 把数据目录建齐。
        ///
        /// 【2026-10-19 用户要求：成品交付前能“一键删干净”】
        /// 真实运行时数据根是 `<exe>\data\default`：workflows、回收站、日志、设置、备份、临时全在它下面。
        /// 把这个 `default` 文件夹整个删掉，就等于清空这台机器的私人数据；下次打开程序会**把缺的文件夹
        /// 重新建出来**（整个删掉、只删了其中某个子文件夹，都会补回来），只是**不再生成 说明.txt**
        /// （用户原话：“说明.txt就不需要重新生成了”）。
        ///
        /// 老旧结构（`data\` 下面直接放着 workflows 等）只迁移一次：第一次运行新版本时整份搬进 `data\default\`，
        /// 免得用户原有方案“看起来丢了”。`default` 已经存在（哪怕是空的）就不再搬，否则用户删完 default
        /// 又被旧文件填回来。
        /// </summary>
        public static void EnsureDataDirectories()
        {
            Directory.CreateDirectory(DataRoot);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable)))
            {
                MigrateLegacyDataIntoDefaultFolder();
            }
            DataFolderMigration.EnsureMigrated();

            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(WorkflowsRoot);
            Directory.CreateDirectory(StartupRestoreDirectory);
            Directory.CreateDirectory(DesktopLayoutDirectory);
            Directory.CreateDirectory(ApplicationsDirectory);
            Directory.CreateDirectory(StartupProfilesFolderRoot);
            Directory.CreateDirectory(DesktopLayoutsDirectory);
            Directory.CreateDirectory(ApplicationPlansFolderRoot);
            Directory.CreateDirectory(RecycleBinRoot);
            Directory.CreateDirectory(RecycleBinKindDirectory(StartupRestoreKind));
            Directory.CreateDirectory(RecycleBinKindDirectory(DesktopLayoutKind));
            Directory.CreateDirectory(RecycleBinKindDirectory(ApplicationsKind));
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
            Directory.CreateDirectory(TempDirectory);
            Directory.CreateDirectory(AutoRestoreMarkerDirectory);
            Directory.CreateDirectory(StartupProfileBackupsDirectory);
            Directory.CreateDirectory(LegacyBackupDirectory);
        }

        /// <summary>
        /// 一次性迁移：旧结构（`data\workflows`、`data\回收站`、`data\日志`、`data\设置`、`data\备份`、`data\临时`，
        /// 以及更早的散装文件 startup-profiles.json / settings.json / …）整份搬进 `data\default\`。
        ///
        /// 判定规则：
        ///  · 每个候选各自判断：`data\default\<名字>` 没有就搬过去；已经有了就**合并**（不覆盖新文件），
        ///    这样上次搬到一半（某个文件夹被占用）也能在下次启动时补完；
        ///  · 说明.txt 只是普通文件，跟着目录整体搬，不删、不改内容。
        ///  · `data` 根下没有可搬的东西（例如用户刚删完 default 重新打开）时什么都不做。
        /// 搬的时候先试 `Directory.Move`；被占用/跨卷等原因失败就退化成“逐文件复制 + 删旧”，
        /// 目标已经存在的（上一次搬到一半）按合并处理 —— 用户数据宁可多留一份，也不能因为一个文件夹被占用就“看起来丢了”。
        /// </summary>
        private static void MigrateLegacyDataIntoDefaultFolder()
        {
            try
            {
                string container = DataContainer;
                string target = Path.Combine(container, "default");
                if (!Directory.Exists(container))
                {
                    return;
                }

                // 候选名按“所有已定义的目录名方案”展开：旧版是中文名，新版是英文名，
                // 两份都可能留在 data 根下，各自搬进 data\default 后由 DataFolderMigration 统一改名。
                List<string> candidateList = new List<string>();
                candidateList.AddRange(DataLayouts.NamesAt(
                    delegate(DataLayout layout) { return layout.Workflows; }, true));
                AddRootCandidates(candidateList, delegate(DataLayout layout) { return layout.RecycleBin; });
                AddRootCandidates(candidateList, delegate(DataLayout layout) { return layout.Logs; });
                AddRootCandidates(candidateList, delegate(DataLayout layout) { return layout.Settings; });
                AddRootCandidates(candidateList, delegate(DataLayout layout) { return layout.Backup; });
                AddRootCandidates(candidateList, delegate(DataLayout layout) { return layout.Temp; });
                candidateList.Add("logs");
                candidateList.AddRange(new string[]
                {
                    "startup-profiles.json", "desktop-layouts.xml", "recycle-bin.json",
                    "settings.json", "last-session.json", "window-state.json"
                });
                string[] candidates = candidateList.ToArray();

                // 注意：这里**不**因为“default 已经存在”就整体返回 —— 上一次可能只搬成功一部分
                // （实测有一个文件夹被占用，Directory.Move 报“访问被拒绝”，剩下的就留在 data 根下）。
                // 每个候选各自判断：目标没有就搬过去，目标已有就合并（目标里的同名文件不覆盖）。
                bool any = false;
                for (int index = 0; index < candidates.Length; index++)
                {
                    string source = Path.Combine(container, candidates[index]);
                    if (Directory.Exists(source) || File.Exists(source))
                    {
                        any = true;
                        break;
                    }
                }
                if (!any)
                {
                    return;
                }

                Directory.CreateDirectory(target);
                bool movedSomething = false;
                for (int index = 0; index < candidates.Length; index++)
                {
                    string name = candidates[index];
                    string source = Path.Combine(container, name);
                    string destination = Path.Combine(target, name);
                    try
                    {
                        if (Directory.Exists(source))
                        {
                            MoveOrMergeDirectory(source, destination);
                            movedSomething = true;
                        }
                        else if (File.Exists(source) && !File.Exists(destination))
                        {
                            File.Move(source, destination);
                            movedSomething = true;
                        }
                    }
                    catch (Exception exception)
                    {
                        // 单个搬不过去不影响程序继续运行（剩下的文件还留在原处，行为与旧版一致）。
                        DataFolderMigration.WriteBootstrapLog("数据目录搬家失败：" + source, exception);
                    }
                }
                if (movedSomething)
                {
                    DataFolderMigration.WriteBootstrapLog("已把旧的数据目录搬进 data\\default。");
                }
            }
            catch (Exception exception)
            {
                DataFolderMigration.WriteBootstrapLog("数据目录搬家失败。", exception);
            }
        }

        /// <summary>把某个固定目录在所有变体下的名字都加进候选（去重）。</summary>
        private static void AddRootCandidates(List<string> candidates, Func<DataLayout, string> selector)
        {
            List<string> names = DataLayouts.NamesAt(selector, true);
            for (int index = 0; index < names.Count; index++)
            {
                if (!candidates.Contains(names[index]))
                {
                    candidates.Add(names[index]);
                }
            }
        }

        /// <summary>把一个文件夹搬进目标位置：能直接 Move 就 Move，不行就逐层复制再删旧（目标已有的文件不覆盖）。</summary>
        private static void MoveOrMergeDirectory(string source, string destination)
        {
            if (!Directory.Exists(destination))
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (Exception)
                {
                    // 落到下面的“复制 + 删旧”。
                }
            }

            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.GetDirectories(source))
            {
                string childDestination = Path.Combine(destination, Path.GetFileName(directory));
                MoveOrMergeDirectory(directory, childDestination);
            }
            foreach (string file in Directory.GetFiles(source))
            {
                string fileName = Path.GetFileName(file);
                string childDestination = Path.Combine(destination, fileName);
                if (!File.Exists(childDestination))
                {
                    try { File.Copy(file, childDestination, false); } catch (Exception) { continue; }
                }
                try { File.Delete(file); } catch (Exception) { }
            }
            try { Directory.Delete(source, false); } catch (Exception) { }
        }
    }
}
