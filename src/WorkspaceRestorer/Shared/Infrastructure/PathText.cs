using System;
using System.IO;

namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// Windows 路径字符串规范化。只做字符串处理，不读文件、不判断文件是否存在。
    ///
    /// 规则：
    ///   · null / 空串 / 全空白 → string.Empty；
    ///   · Path.GetFullPath 转绝对路径；
    ///   · 去掉末尾的目录分隔符，但保留盘符/UNC 根（C:\ 不能变成 C:）；
    ///   · 非法/过长路径 → 返回 Trim 后的原字符串，不抛异常；
    ///   · 不改变大小写；大小写比较由调用方决定。
    /// </summary>
    internal static class PathText
    {
        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return TrimTrailingSeparators(Path.GetFullPath(trimmed));
            }
            catch
            {
                // 与旧实现一致：非法路径不抛给调用方，退回可读的原字符串。
                return trimmed;
            }
        }

        /// <summary>
        /// 先展开 %VAR% 再规范化；只给原来就展开环境变量的调用方使用，
        /// 避免让本来按字面路径处理的调用方改变行为。
        /// </summary>
        internal static string NormalizeExpanded(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Normalize(Environment.ExpandEnvironmentVariables(trimmed));
            }
            catch
            {
                return Normalize(trimmed);
            }
        }

        private static string TrimTrailingSeparators(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return string.Empty;
            }

            string root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                return fullPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            // C:\、\\server\share\ 这类根路径要原样保留；
            // 直接 TrimEnd 会把 C:\ 削成 C:，含义就变了。
            if (fullPath.Length <= root.Length)
            {
                return fullPath;
            }

            int end = fullPath.Length;
            while (end > root.Length
                && (fullPath[end - 1] == Path.DirectorySeparatorChar
                    || fullPath[end - 1] == Path.AltDirectorySeparatorChar))
            {
                end--;
            }
            return fullPath.Substring(0, end);
        }
    }
}
