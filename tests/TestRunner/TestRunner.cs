using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WorkspaceRestorer.Applications.Restore;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static readonly List<string> Failures = new List<string>();
        private static int Passed;
        private static string assemblyPath;
        private static Assembly productAssembly;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("用法：WorkspaceRestorer.Tests.exe <待测程序集路径>");
                return 2;
            }

            Run("测试模式已开启", delegate
            {
                AssertEqual("1", Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_TEST_MODE"));
                AssertEqual("1", Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DISABLE_EXTERNAL_EFFECTS"));
            });

            Run("测试数据目录与桌面隔离", delegate
            {
                string dataDirectory = Environment.GetEnvironmentVariable("WORKSPACE_RESTORER_DATA_DIR");
                Assert(!string.IsNullOrEmpty(dataDirectory), "缺少 WORKSPACE_RESTORER_DATA_DIR。");
                Assert(Directory.Exists(dataDirectory), "测试数据目录不存在。");

                string dataPath = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop))
                {
                    string desktopPath = Path.GetFullPath(desktop).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    Assert(!dataPath.StartsWith(desktopPath, StringComparison.OrdinalIgnoreCase), "测试数据目录不能位于真实桌面。" );
                }
            });
            assemblyPath = Path.GetFullPath(args[0]);
            string productAssemblyName = AssemblyName.GetAssemblyName(assemblyPath).Name;
            // The test executable lives under build/tests while the product
            // is copied to zuizhongchanwu.  Resolve the product by the actual
            // assembly name in the file being tested, so a future identity
            // rename cannot leave the tests bound to a stale literal.
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs eventArgs)
            {
                AssemblyName requested = new AssemblyName(eventArgs.Name);
                if (string.Equals(requested.Name, productAssemblyName, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(assemblyPath))
                {
                    return Assembly.LoadFrom(assemblyPath);
                }
                return null;
            };
            productAssembly = null;

            RunDataFolderMigrationTests();
            RunMirrorIntakeTests();
            RunAssemblyAndDragVisualTests();
            RunDesktopLayoutEditorDragTests();
            RunBrowserAndWindowRestoreTests();
            RunLoadAndSettingsTests();
            RunRestoreAndCaptureTests();
            RunProfilesAndApplicationsTests();
            RunMarkersAndSearchTests();
            RunRecycleBinFoldersAndLayoutTests();
            RunCardsAndRestoreTextTests();
            RunShellAndMirrorLayerTests();
            RunPathTextTests();
            RunShortcutCatalogTests();
            RunLocalizationTests();
            RunThemeTokenTests();
            RunSettingsUiSmokeTests();
            RunShortcutPagerTests();

            if (Failures.Count == 0)
            {
                Console.WriteLine("[PASS] {0} 项测试全部通过。", Passed);
                return 0;
            }

            Console.Error.WriteLine("[FAIL] {0} 项测试失败：", Failures.Count);
            foreach (string failure in Failures)
            {
                Console.Error.WriteLine("  - " + failure);
            }
            return 1;
        }
    }
}
