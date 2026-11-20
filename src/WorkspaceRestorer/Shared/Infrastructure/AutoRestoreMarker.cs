using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// “打开程序时自动做的事”的**本次开机标记文件**（一个文件记下这次开机哪些自动功能已经做过）。
    ///
    /// 用户最新要求（2026-10-22）：
    /// “要改为电脑关机再打开（要考虑到某些电脑一开就是半年、多年不关机），才能触发
    ///  ‘启动程序后自动恢复工作方案’。否则，一到凌晨，就关闭软件再次打开时，就立刻恢复了。”
    ///
    /// 所以判断依据**不再是日期**，而是“这次 Windows 开机/登录会话”：
    ///  · 同一次开机里，不管跨不跨天、关几次程序再打开，都算已经做过；
    ///  · 只有电脑关机/重启（含 Windows 快速启动）后，登录会话或开机时刻变了，旧标记才作废，
    ///    下次打开重新做一次；
    ///  · 文件仍然保留在 `data\临时\启动程序后自动恢复\`，文件名仍按日期；
    ///    跨天后旧日期文件仍有效，不需要为了“换天”重建。
    ///
    /// 标记不只管“自动恢复工作方案”，也管“自动启动自启动方案”；两件事各记一笔。
    /// 旧版本没有 `已完成：` 行的标记，按“整套自动操作都做过了”兼容。
    /// 任何一步失败都不抛异常：这只是“少做一次”的优化，不能影响程序启动。
    /// </summary>
    public static class AutoRestoreMarker
    {
        private const string Extension = ".标记";
        private const string DoneLinePrefix = "已完成：";
        private const string SessionLinePrefix = "登录会话：";

        /// <summary>「启动与恢复」的自动恢复（打开程序时恢复工作方案）。</summary>
        public const string WorkProfileFeature = "工作方案";

        /// <summary>「自启动应用」的自动启动（打开程序时启动自启动方案）。</summary>
        public const string ApplicationsFeature = "自启动应用";

        /// <summary>本次开机是否已经自动恢复过工作方案（老名字，保持兼容）。</summary>
        public static bool HasRestoredToday()
        {
            return HasDoneToday(WorkProfileFeature);
        }

        /// <summary>记下“本次开机已经自动恢复过工作方案”（老名字，保持兼容）。</summary>
        public static void MarkRestoredToday()
        {
            MarkDoneToday(WorkProfileFeature);
        }

        /// <summary>
        /// 本次开机这一项自动功能是否已经做过。
        /// 判断依据是标记里的“本次开机/登录会话”，不是日期：
        /// 同一次开机里跨天不算重置；只有关机/重启后会话或开机时刻变了，旧标记才作废。
        /// </summary>
        public static bool HasDoneToday(string feature)
        {
            try
            {
                // 不再按“今天/日期”找文件：凌晨 00:00 后日期变了，但电脑并没有关机，
                // 同一次开机里仍然应该认为已经做过。找当前这次开机/登录会话里最新的标记。
                HashSet<string> done;
                bool legacy;
                string path = FindCurrentMarker(out done, out legacy);
                if (path == null)
                {
                    return false;
                }
                if (legacy)
                {
                    // 旧版本写的标记（没有“已完成：”明细）：整套自动操作都做过了。
                    return true;
                }
                return done.Contains(NormalizeFeature(feature));
            }
            catch (Exception exception)
            {
                AppLog.Write("读取“本次开机已做过”标记失败（按没做过处理）。", exception);
                return false;
            }
        }

        /// <summary>
        /// 找到“当前这次开机/登录会话”的标记文件。按最后写入时间取最新的一个。
        /// 跨天但没关机时，昨天的标记仍然有效；关机/重启（含快速启动）后会话/开机时刻变了，
        /// 旧标记就不再返回。
        /// </summary>
        private static string FindCurrentMarker(out HashSet<string> done, out bool legacy)
        {
            done = null;
            legacy = false;
            string directory = AppPaths.AutoRestoreMarkerDirectory;
            if (!Directory.Exists(directory))
            {
                return null;
            }
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*" + Extension, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return null;
            }
            string best = null;
            DateTime bestWrite = DateTime.MinValue;
            HashSet<string> bestDone = null;
            bool bestLegacy = false;
            for (int index = 0; index < files.Length; index++)
            {
                string file = files[index];
                if (!IsMarkerFromCurrentBoot(file))
                {
                    continue;
                }
                HashSet<string> fileDone;
                bool fileLegacy;
                if (!TryReadDoneFeatures(file, out fileDone, out fileLegacy))
                {
                    continue;
                }
                DateTime write = LastWriteTimeOf(file);
                if (best == null || write > bestWrite)
                {
                    best = file;
                    bestWrite = write;
                    bestDone = fileDone;
                    bestLegacy = fileLegacy;
                }
            }
            done = bestDone;
            legacy = bestLegacy;
            return best;
        }

        /// <summary>
        /// 标记是否属于当前这次开机/登录会话。
        /// 优先比较“登录会话”；没有会话信息时退回到“本次开机”时间比较。
        /// 这样跨天（哪怕开机半年）不会失效，只有电脑关机/重启后才会失效。
        /// </summary>
        private static bool IsMarkerFromCurrentBoot(string path)
        {
            try
            {
                string markerSession;
                bool hasSessionLine = TryReadSingleLine(path, SessionLinePrefix, out markerSession);
                string currentSession = AppPaths.LogonSessionId;
                if (hasSessionLine && !string.IsNullOrWhiteSpace(markerSession)
                    && !string.IsNullOrWhiteSpace(currentSession))
                {
                    // 每次关机再开机都会重新登录，LUID 一定变；同一次开机里只关程序再打开不变。
                    // 快速启动（BootTime 不变）也靠这一条识别。
                    return string.Equals(currentSession.Trim(), markerSession.Trim(),
                        StringComparison.OrdinalIgnoreCase);
                }

                string markerBootText;
                if (TryReadSingleLine(path, "本次开机：", out markerBootText))
                {
                    DateTime markerBoot;
                    if (DateTime.TryParseExact(markerBootText.Trim(), WorkspaceRestorer.Core.TimeText.SortableSecondFormat,
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out markerBoot))
                    {
                        // TickCount 反推的开机时间会有几秒抖动，给 20 秒容差。
                        return Math.Abs((AppPaths.BootTime - markerBoot).TotalSeconds) <= 20.0;
                    }
                }
                // 很旧的标记没有会话/开机行：用文件写入时间兜底。
                return LastWriteTimeOf(path) >= AppPaths.BootTime.AddSeconds(-5.0);
            }
            catch
            {
                return false;
            }
        }

        private static DateTime LastWriteTimeOf(string path)
        {
            try
            {
                return File.GetLastWriteTime(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
                AppLog.Write("已删除不属于当前这次开机的旧标记：" + path, null);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 记下“本次开机这一项已经做过了”；同一次开机里再记别的项时保留已有的记录。
        /// 写入今天的文件名，并清掉其它标记，保证最多一个文件。
        /// </summary>
        public static void MarkDoneToday(string feature)
        {
            try
            {
                AppPaths.EnsureDataDirectories();
                string directory = AppPaths.AutoRestoreMarkerDirectory;
                Directory.CreateDirectory(directory);
                string path = TodayPath();
                List<string> done = new List<string>();
                // 先读当前这次开机的有效标记（可能还是昨天的日期）；
                // 如果先 RemoveOtherMarkers 会把昨天的有效记录删掉，导致另一项被丢掉。
                HashSet<string> existing;
                bool legacy;
                if (FindCurrentMarker(out existing, out legacy) != null && existing != null)
                {
                    if (legacy)
                    {
                        // 旧格式没写明细，含义是“两件事都算做过了”。
                        existing.Add(WorkProfileFeature);
                        existing.Add(ApplicationsFeature);
                    }
                    done.AddRange(existing);
                }
                RemoveOtherMarkers(directory, TodayFileName());
                string wanted = NormalizeFeature(feature);
                for (int index = 0; index < done.Count; index++)
                {
                    if (string.Equals(done[index], wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        done.RemoveAt(index);
                        index--;
                    }
                }
                done.Add(wanted);
                // 保持固定顺序，文件内容稳定、方便排查。
                done.Sort(StringComparer.OrdinalIgnoreCase);

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("打开程序时自动执行的项目（同一次开机里做过的不再做第二遍）");
                builder.AppendLine("日期：" + WorkspaceRestorer.Core.TimeText.FormatDateStamp(DateTime.Now));
                builder.AppendLine("时间：" + WorkspaceRestorer.Core.TimeText.FormatClockSecond(DateTime.Now));
                builder.AppendLine("本次开机：" + WorkspaceRestorer.Core.TimeText.FormatSortableSecond(AppPaths.BootTime));
                string logonSession = AppPaths.LogonSessionId;
                if (!string.IsNullOrWhiteSpace(logonSession))
                {
                    builder.AppendLine(SessionLinePrefix + logonSession);
                }
                builder.AppendLine();
                for (int index = 0; index < done.Count; index++)
                {
                    builder.AppendLine(DoneLinePrefix + done[index]);
                }
                builder.AppendLine();
                builder.AppendLine("作用：同一次开机里关掉程序再打开，不会又把工作方案恢复一遍，也不会把自启动方案再启动一遍。");
                builder.AppendLine("只有电脑关机/重启后再打开，才会重新做一次；跨天但没关机不会重置（程序会自动作废旧标记）。");
                builder.AppendLine("想让下次打开时重新做：删掉这个文件夹里的文件即可（或直接删掉本文件）。");
                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            }
            catch (Exception exception)
            {
                AppLog.Write("写入“本次开机已做过”标记失败（不影响使用）。", exception);
            }
        }

        /// <summary>
        /// 清掉不属于当前这次开机/登录会话的标记；同一开机的旧日期标记保留（跨天不重置）。
        /// 程序启动时调用；关机/重启后旧标记会在这里被删掉，于是正常恢复一次。
        /// </summary>
        public static void CleanupStale()
        {
            try
            {
                string directory = AppPaths.AutoRestoreMarkerDirectory;
                if (!Directory.Exists(directory))
                {
                    return;
                }
                string[] files = Directory.GetFiles(directory, "*" + Extension,
                    SearchOption.TopDirectoryOnly);
                string keep = null;
                DateTime keepWrite = DateTime.MinValue;
                for (int index = 0; index < files.Length; index++)
                {
                    string file = files[index];
                    if (!IsMarkerFromCurrentBoot(file))
                    {
                        DeleteQuietly(file);
                        continue;
                    }
                    DateTime write = LastWriteTimeOf(file);
                    if (keep == null || write > keepWrite)
                    {
                        keep = file;
                        keepWrite = write;
                    }
                }
                // 同一次开机正常只会有一个；如果旧版本留下多个有效标记，保留最新的一份。
                for (int index = 0; index < files.Length; index++)
                {
                    if (!string.Equals(files[index], keep, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(files[index]))
                    {
                        DeleteQuietly(files[index]);
                    }
                }
            }
            catch (Exception exception)
            {
                AppLog.Write("清理过期的“本次开机已做过”标记失败（不影响使用）。", exception);
            }
        }

        /// <summary>今天的标记文件完整路径（测试与排查用）。</summary>
        public static string TodayPath()
        {
            return Path.Combine(AppPaths.AutoRestoreMarkerDirectory, TodayFileName());
        }

        private static string TodayFileName()
        {
            return WorkspaceRestorer.Core.TimeText.FormatDateStamp(DateTime.Now) + Extension;
        }

        private static string NormalizeFeature(string feature)
        {
            return string.IsNullOrWhiteSpace(feature) ? WorkProfileFeature : feature.Trim();
        }

        /// <summary>
        /// 读标记文件里“已完成：”的明细。
        /// 返回 false ＝ 文件读不出来；legacy ＝ 文件里一条明细都没有（旧版本写的）。
        /// </summary>
        /// <summary>读标记文件里某一行（“登录会话：…”）；没有就返回 false。</summary>
        private static bool TryReadSingleLine(string path, string prefix, out string value)
        {
            value = null;
            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (line == null)
                    {
                        continue;
                    }
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        value = trimmed.Substring(prefix.Length).Trim();
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryReadDoneFeatures(string path, out HashSet<string> done, out bool legacy)
        {
            done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            legacy = false;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch
            {
                return false;
            }
            bool any = false;
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line == null)
                {
                    continue;
                }
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(DoneLinePrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string name = trimmed.Substring(DoneLinePrefix.Length).Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                done.Add(name);
                any = true;
            }
            legacy = !any;
            return true;
        }

        private static void RemoveOtherMarkers(string directory, string keepName)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                return;
            }
            for (int index = 0; index < files.Length; index++)
            {
                string name = Path.GetFileName(files[index]);
                if (string.Equals(name, keepName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // 只动本组件自己写出来的标记文件，别的手工文件不碰。
                if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    File.Delete(files[index]);
                }
                catch
                {
                }
            }
        }

        /// <summary>给设置页/自检用：现在这个文件夹里有哪些文件。</summary>
        public static IList<string> ListMarkerFiles()
        {
            List<string> names = new List<string>();
            try
            {
                string directory = AppPaths.AutoRestoreMarkerDirectory;
                if (!Directory.Exists(directory))
                {
                    return names;
                }
                string[] files = Directory.GetFiles(directory);
                for (int index = 0; index < files.Length; index++)
                {
                    names.Add(Path.GetFileName(files[index]));
                }
            }
            catch
            {
            }
            return names;
        }
    }
}
