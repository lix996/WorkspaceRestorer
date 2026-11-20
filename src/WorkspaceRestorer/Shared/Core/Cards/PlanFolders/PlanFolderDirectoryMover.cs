using System;
using System.IO;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Core.Cards.PlanFolders
{
    public sealed class PlanFolderDirectoryMoveResult
    {
        public PlanFolderDirectoryMoveResult()
        {
            SourceDirectory = string.Empty;
            DestinationDirectory = string.Empty;
            Message = string.Empty;
        }

        /// <summary>目录真的移动了。false 时调用方应按“只改数据”处理。</summary>
        public bool Moved { get; set; }

        /// <summary>源目录和目标目录相同，本来就在正确位置。</summary>
        public bool AlreadyInPlace { get; set; }

        public string SourceDirectory { get; set; }

        public string DestinationDirectory { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// 【公共区域 · 方案文件夹】真正的“移动文件夹”。
    ///
    /// 用户要求（2026-11-01）：并入要像文件夹的并入一样，不能卡。
    /// 所以跨父项移动时只做一次 `Directory.Move`，不复制目录、不重写整份镜像。
    /// 总 JSON 和移动日志由各工作区负责，这里只动磁盘目录。
    /// </summary>
    public static class PlanFolderDirectoryMover
    {
        /// <summary>
        /// 算出目标目录：保留源文件夹名；同名已存在时按 Windows 习惯顺延成“名字（2）”。
        /// </summary>
        public static string BuildDestinationPath(string destinationParentDirectory, string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(destinationParentDirectory) || string.IsNullOrWhiteSpace(sourceDirectory))
            {
                return null;
            }
            string sourceName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                sourceName = "方案";
            }
            string candidate = Path.Combine(destinationParentDirectory, sourceName);
            if (!Directory.Exists(candidate)
                || string.Equals(
                    Path.GetFullPath(candidate),
                    Path.GetFullPath(sourceDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
            for (int suffix = 2; suffix < 1000; suffix++)
            {
                candidate = Path.Combine(destinationParentDirectory, sourceName + "（" + suffix + "）");
                if (!Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            return Path.Combine(destinationParentDirectory, sourceName + "（" + Guid.NewGuid().ToString("N").Substring(0, 6) + "）");
        }

        /// <summary>
        /// 把源目录移动到目标目录。移动前校验源目录里 `方案.json`/`布局.xml` 的 Id，
        /// 对不上就不动目录（宁可不搬，也不能把另一份方案搬走）。
        /// </summary>
        public static PlanFolderDirectoryMoveResult Move(
            string sourceDirectory,
            string destinationDirectory,
            string expectedId,
            string dataFileName,
            Func<string, string> readId)
        {
            PlanFolderDirectoryMoveResult result = new PlanFolderDirectoryMoveResult();
            result.SourceDirectory = sourceDirectory ?? string.Empty;
            result.DestinationDirectory = destinationDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourceDirectory)
                || string.IsNullOrWhiteSpace(destinationDirectory))
            {
                result.Message = "源目录或目标目录为空。";
                return result;
            }
            if (string.Equals(
                Path.GetFullPath(sourceDirectory),
                Path.GetFullPath(destinationDirectory),
                StringComparison.OrdinalIgnoreCase))
            {
                result.AlreadyInPlace = true;
                result.Message = "已经在目标位置。";
                return result;
            }
            if (!Directory.Exists(sourceDirectory))
            {
                result.Message = "源目录不存在，只更新数据。";
                return result;
            }
            if (Directory.Exists(destinationDirectory))
            {
                result.Message = "目标目录已存在，只更新数据。";
                return result;
            }
            if (readId != null && !string.IsNullOrWhiteSpace(dataFileName))
            {
                string dataPath = Path.Combine(sourceDirectory, dataFileName);
                string actualId = null;
                try
                {
                    actualId = readId(dataPath);
                }
                catch (Exception exception)
                {
                    AppLog.Write("[方案移动] 读取源目录 Id 失败，放弃移动目录：" + dataPath, exception);
                }
                if (!string.IsNullOrWhiteSpace(expectedId)
                    && !string.Equals(actualId, expectedId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "源目录里的 Id 对不上，只更新数据。";
                    return result;
                }
            }
            try
            {
                string parent = Path.GetDirectoryName(destinationDirectory);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                Directory.Move(sourceDirectory, destinationDirectory);
                result.Moved = true;
                result.Message = "已移动目录。";
            }
            catch (Exception exception)
            {
                result.Message = "移动目录失败，只更新数据：" + exception.Message;
                AppLog.Write("[方案移动] 移动目录失败：" + sourceDirectory + " -> " + destinationDirectory, exception);
            }
            return result;
        }

    }
}
