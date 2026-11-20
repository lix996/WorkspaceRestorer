using System;
using System.Globalization;
using System.Windows;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunDesktopLayoutEditorDragTests()
        {
            Run("编辑页拖动边界：边界内 1:1 跟手", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(0.0, 0.0, 1000.0, 800.0);
                bounds.Begin(100.0, 100.0, 40.0, 40.0, area);
                bounds.MoveTo(130.0, 120.0, area);
                AssertEqual("70", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("60", bounds.Y.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("编辑页拖动边界：贴边反向立即跟手", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(0.0, 0.0, 1000.0, 800.0);
                bounds.Begin(900.0, 300.0, 850.0, 300.0, area);
                bounds.MoveTo(980.0, 300.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                bounds.MoveTo(970.0, 300.0, area);
                AssertEqual("882", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("编辑页拖动边界：越界钉住，回界内当帧 1:1", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(0.0, 0.0, 1000.0, 800.0);
                bounds.Begin(900.0, 300.0, 850.0, 300.0, area);
                bounds.MoveTo(1100.0, 300.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("True", bounds.IsPinnedX.ToString());
                bounds.MoveTo(1050.0, 300.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                // 回到界内：当帧直接接住，不再分帧追 48 DIP。
                bounds.MoveTo(600.0, 300.0, area);
                AssertEqual("550", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("False", bounds.IsPinnedX.ToString());
            });

            Run("编辑页拖动边界：回到边界内当帧接住", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(0.0, 0.0, 1000.0, 800.0);
                bounds.Begin(900.0, 300.0, 850.0, 300.0, area);
                bounds.MoveTo(1100.0, 300.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                bounds.MoveTo(1000.0, 300.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                bounds.MoveTo(990.0, 300.0, area);
                AssertEqual("882", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("编辑页拖动边界：沿边移动另一轴仍跟手", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(0.0, 0.0, 1000.0, 800.0);
                bounds.Begin(900.0, 300.0, 850.0, 300.0, area);
                bounds.MoveTo(1100.0, 400.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("400", bounds.Y.ToString("0.#", CultureInfo.InvariantCulture));
                bounds.MoveTo(1100.0, 450.0, area);
                AssertEqual("892", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("450", bounds.Y.ToString("0.#", CultureInfo.InvariantCulture));
            });

            Run("编辑页拖动边界：非零原点、窄矩形与松手夹取一致", delegate
            {
                EditorDragBounds bounds = new EditorDragBounds(108.0, 54.0);
                Rect area = new Rect(120.0, 80.0, 300.0, 200.0);
                bounds.Begin(130.0, 90.0, 130.0, 80.0, area);
                bounds.MoveTo(0.0, 0.0, area);
                AssertEqual("120", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("80", bounds.Y.ToString("0.#", CultureInfo.InvariantCulture));
                bounds.MoveTo(1000.0, 1000.0, area);
                AssertEqual("312", bounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("226", bounds.Y.ToString("0.#", CultureInfo.InvariantCulture));

                double x = 1000.0;
                double y = 1000.0;
                EditorDragBounds.ClampTo(area, 108.0, 54.0, ref x, ref y);
                AssertEqual("312", x.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("226", y.ToString("0.#", CultureInfo.InvariantCulture));

                // 矩形比物体还窄时，物体钉在左/上边界，不能出现负坐标。
                Rect narrow = new Rect(50.0, 60.0, 80.0, 40.0);
                EditorDragBounds narrowBounds = new EditorDragBounds(108.0, 54.0);
                narrowBounds.Begin(60.0, 70.0, 60.0, 70.0, narrow);
                narrowBounds.MoveTo(-100.0, -100.0, narrow);
                AssertEqual("50", narrowBounds.X.ToString("0.#", CultureInfo.InvariantCulture));
                AssertEqual("60", narrowBounds.Y.ToString("0.#", CultureInfo.InvariantCulture));
            });
        }
    }
}
