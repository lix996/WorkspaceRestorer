using System;
using System.IO;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunPathTextTests()
        {
            Run("路径规范化：null/空串/空白都是空串", delegate
            {
                AssertEqual(string.Empty, PathText.Normalize(null));
                AssertEqual(string.Empty, PathText.Normalize(string.Empty));
                AssertEqual(string.Empty, PathText.Normalize("   "));
                AssertEqual(string.Empty, PathText.NormalizeExpanded(null));
                AssertEqual(string.Empty, PathText.NormalizeExpanded("   "));
            });

            Run("路径规范化：去掉末尾的两种分隔符", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "csk-pathtext-normalize");
                string expected = PathText.Normalize(directory);
                AssertEqual(expected, PathText.Normalize(directory + "\\"));
                AssertEqual(expected, PathText.Normalize(directory + "/"));
                AssertEqual(expected, PathText.Normalize(directory + "\\\\"));
            });

            Run("路径规范化：盘符根不被削成 C:", delegate
            {
                string root = Path.GetPathRoot(Path.GetFullPath(Environment.SystemDirectory));
                Assert(!string.IsNullOrEmpty(root), "拿不到系统盘根路径。");
                AssertEqual(root, PathText.Normalize(root));
                AssertEqual(root, PathText.Normalize(root + "."));
            });

            Run("路径规范化：UNC 根不被削坏", delegate
            {
                AssertEqual(@"\\server\share", PathText.Normalize(@"\\server\share\"));
                AssertEqual(@"\\server\share", PathText.Normalize(@"\\server\share"));
            });

            Run("路径规范化：相对路径转绝对路径", delegate
            {
                string relative = Path.Combine(".", "csk-pathtext-relative");
                string full = Path.GetFullPath(relative);
                AssertEqual(PathText.Normalize(full), PathText.Normalize(relative));
            });

            Run("路径规范化：非法路径不抛异常", delegate
            {
                string invalid = "\0";
                AssertEqual(invalid.Trim(), PathText.Normalize(invalid));
            });

            Run("路径规范化：%TEMP% 只在 NormalizeExpanded 展开", delegate
            {
                string temp = Environment.GetEnvironmentVariable("TEMP");
                Assert(!string.IsNullOrWhiteSpace(temp), "TEMP 环境变量为空。");
                AssertEqual(
                    PathText.Normalize(temp),
                    PathText.NormalizeExpanded("%TEMP%"));
            });

            Run("路径规范化：不改变路径大小写", delegate
            {
                string mixed = Path.Combine(Path.GetTempPath(), "PathTextCaseProbe");
                string normalized = PathText.Normalize(mixed);
                Assert(normalized.Contains("PathTextCaseProbe"), "Normalize 不应改变路径大小写。");
            });
        }
    }
}
