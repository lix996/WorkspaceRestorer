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
using WorkspaceRestorer.Core.Cards.Drag;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {

        private static void RunAssemblyAndDragVisualTests()
        {
            Run("产品程序集可以安全加载", delegate
            {
                Assert(File.Exists(assemblyPath), "产品程序集不存在。" );
                productAssembly = Assembly.LoadFrom(assemblyPath);
                AssertEqual("WorkspaceRestorer", productAssembly.GetName().Name);
                Assert(productAssembly.EntryPoint != null, "WinExe 缺少入口点。" );
            });

            Run("产品程序集引用 WPF", delegate
            {
                Assert(productAssembly != null, "产品程序集未加载。" );
                bool referencesWpf = productAssembly.GetReferencedAssemblies()
                    .Any(delegate(AssemblyName name) { return name.Name == "PresentationFramework"; });
                Assert(referencesWpf, "产品程序集没有引用 PresentationFramework。" );
            });

            Run("产品类型元数据完整", delegate
            {
                Assert(productAssembly != null, "产品程序集未加载。" );
                try
                {
                    Assert(productAssembly.GetTypes().Length > 0, "产品程序集不包含类型。" );
                }
                catch (ReflectionTypeLoadException exception)
                {
                    string loaderErrors = string.Join(" | ", exception.LoaderExceptions.Select(delegate(Exception item) { return item.Message; }).ToArray());
                    throw new InvalidOperationException("部分类型无法加载：" + loaderErrors, exception);
                }
            });

            Run("拖动视觉：浮动方框宽度", delegate
            {
                AssertEqual("240", DragGhostMetrics.WidthFor(0.0).ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("240", DragGhostMetrics.WidthFor(100.0).ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("484", DragGhostMetrics.WidthFor(500.0).ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("拖动视觉：分割辅助线几何", delegate
            {
                Rect row = DragInsertionGeometry.RowLine(new Rect(10.0, 20.0, 300.0, 40.0), false);
                AssertEqual("10", row.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("19", row.Y.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("2", row.Height.ToString("0.#", CultureInfo.InvariantCulture));

                bool straddled;
                double seam = DragInsertionGeometry.BetweenSeam(
                    100.0, 140.0, 130.0, 105.0, 135.0, true, out straddled);
                Assert(straddled, "夹着被拖卡片时应标记 straddled。");
                AssertEqual("136", seam.ToString("0.#", CultureInfo.InvariantCulture));

                Rect clipped = new Rect(5.0, 10.0, 500.0, 2.0);
                bool visible = DragInsertionGeometry.Clip(ref clipped, new Rect(100.0, 0.0, 200.0, 400.0));
                Assert(visible, "线在列表可视区内时应可见。");
                AssertEqual("100", clipped.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("200", clipped.Width.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("拖动视觉：高亮框与气泡位置", delegate
            {
                Rect frame = DragTargetGeometry.FrameRect(new Point(20.0, 30.0), 0.0, 0.0);
                AssertEqual("20", frame.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("40", frame.Width.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("20", frame.Height.ToString("0.#", CultureInfo.InvariantCulture));

                Point bubble = DragTargetGeometry.BubblePosition(100.0, 50.0, 240.0, 40.0, 160.0);
                AssertEqual("180", bubble.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("96", bubble.Y.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("拖动视觉：可放区判定", delegate
            {
                Rect list = new Rect(100.0, 100.0, 300.0, 200.0);
                Rect top = new Rect(100.0, 36.0, 300.0, 64.0);
                Rect bottom = new Rect(100.0, 300.0, 300.0, 64.0);
                Assert(DragDropArea.Contains(list, top, bottom, new Point(250.0, 200.0)), "列表内应可放。");
                Assert(DragDropArea.Contains(list, top, bottom, new Point(250.0, 40.0)), "上条内应可放。");
                Assert(DragDropArea.Contains(list, top, bottom, new Point(250.0, 360.0)), "下条内应可放。");
                Assert(!DragDropArea.Contains(list, top, bottom, new Point(20.0, 20.0)), "列表外空白不可放。");
            });

            Run("拖动视觉：大方案内的小方案跨父级移动", delegate
            {
                List<StartupProfile> profiles = new List<StartupProfile>();
                StartupProfile parent = new StartupProfile { Id = "p", Name = "大方案", IsFolder = true };
                StartupProfile first = new StartupProfile { Id = "c1", Name = "小方案一", ParentId = "p" };
                StartupProfile second = new StartupProfile { Id = "c2", Name = "小方案二", ParentId = "p" };
                StartupProfile root = new StartupProfile { Id = "r", Name = "根方案" };
                profiles.Add(parent);
                profiles.Add(first);
                profiles.Add(second);
                profiles.Add(root);
                Assert(PlanFolderTree.MoveRelative(profiles, first, root, true), "小方案应能移到根方案旁边。");
                AssertEqual(null, first.ParentId);
                Assert(PlanFolderTree.MoveRelative(profiles, first, second, true), "小方案应能回到另一个小方案旁边。");
                AssertEqual("p", first.ParentId);
                Assert(!PlanFolderTree.MoveInto(profiles, parent, first), "大方案不能移进自己的小方案。");
            });

        }
    }
}
