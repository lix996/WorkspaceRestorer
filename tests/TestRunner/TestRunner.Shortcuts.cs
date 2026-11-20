using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Windows.Input;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        /// <summary>
        /// 2026-11-16：快捷键从“一条命令一个键”改成“一条命令多条绑定”。
        /// 这些测试盯住用户要求的三件事：
        ///   1. 新增一条不会把原来的键位判成冲突；
        ///   2. 删除一条只少一条，同命令其它绑定还在；
        ///   3. 删空是有效状态，读回 settings.json 不能偷偷恢复默认。
        /// </summary>
        private static void RunShortcutCatalogTests()
        {
            Run("快捷键：默认绑定与多条绑定的新增/删除", delegate
            {
                AppText.Initialize(WorkspaceRestorer.Core.Localization.LanguageTags.ZhCn);
                ShortcutCatalog.Configure(null);
                IList<ShortcutCatalog.Shortcut> all = ShortcutCatalog.All();
                AssertEqual(7, all.Count);
                AssertEqual(4, ShortcutCatalog.Bindings().Count);
                Assert(ShortcutCatalog.Resolve(Key.Y, ModifierKeys.Control) == null,
                    "Ctrl+Y 不再是任何命令的快捷键。");
                ShortcutCatalog.Shortcut redoDefault = ShortcutCatalog.Find("redo");
                Assert(redoDefault != null && redoDefault.Bindings.Count == 1,
                    "重做默认只保留一条绑定。");
                AssertEqual("Ctrl + Shift + Z", redoDefault.Bindings[0].Keys);
                ShortcutCatalog.Shortcut copy = ShortcutCatalog.Find("copy-profile");
                Assert(copy != null && copy.Bindings.Count == 1, "复制命令默认应只有一条 Ctrl+C。");
                AssertEqual("Ctrl + C", copy.Bindings[0].Keys);

                // 加一条 Ctrl+Shift+C：列表里原封不动的 Ctrl+C 不能被误判成冲突。
                List<ShortcutBinding> copyBindings = CloneForTest(copy.Bindings);
                copyBindings.Add(new ShortcutBinding(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
                string error;
                Assert(ShortcutCatalog.TryReplaceCommandBindings("copy-profile", copyBindings, out error),
                    "新增 Ctrl+Shift+C 失败：" + error);
                copy = ShortcutCatalog.Find("copy-profile");
                AssertEqual(2, copy.Bindings.Count);
                AssertEqual(5, ShortcutCatalog.Bindings().Count);

                // 同一命令内重复也不行。
                List<ShortcutBinding> duplicate = CloneForTest(copy.Bindings);
                duplicate.Add(new ShortcutBinding(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
                Assert(!ShortcutCatalog.TryReplaceCommandBindings("copy-profile", duplicate, out error),
                    "同一命令重复绑定应被拒绝。");
                Assert(error != null && error.Contains("Ctrl + Shift + C"),
                    "重复绑定的错误应指出具体键位。");

                // 删除第一条：只少这一条，第二条还在。
                copyBindings = CloneForTest(copy.Bindings);
                copyBindings.RemoveAt(0);
                Assert(ShortcutCatalog.TryReplaceCommandBindings("copy-profile", copyBindings, out error),
                    "删除一条绑定失败：" + error);
                copy = ShortcutCatalog.Find("copy-profile");
                AssertEqual(1, copy.Bindings.Count);
                AssertEqual("Ctrl + Shift + C", copy.Bindings[0].Keys);
                AssertEqual(4, ShortcutCatalog.Bindings().Count);
            });

            Run("快捷键：删空命令不会恢复默认，导出/读回保持为空", delegate
            {
                AppText.Initialize(WorkspaceRestorer.Core.Localization.LanguageTags.ZhCn);
                ShortcutCatalog.Configure(null);
                string error;
                Assert(ShortcutCatalog.TryReplaceCommandBindings(
                    "copy-profile", new List<ShortcutBinding>(), out error),
                    "删空复制命令失败：" + error);
                ShortcutCatalog.Shortcut copy = ShortcutCatalog.Find("copy-profile");
                Assert(copy != null && copy.Bindings.Count == 0, "删空后应显示 0 条绑定。");
                AssertEqual(3, ShortcutCatalog.Bindings().Count);

                List<ShortcutBindingData> exported = ShortcutCatalog.ExportBindings();
                ShortcutCatalog.Configure(exported);
                copy = ShortcutCatalog.Find("copy-profile");
                Assert(copy != null && copy.Bindings.Count == 0, "导出再读回不能把空命令恢复成默认。");
                AssertEqual(3, ShortcutCatalog.Bindings().Count);
                ShortcutCatalog.Configure(null);
            });

            Run("快捷键：单条命令恢复默认，不影响其它命令", delegate
            {
                AppText.Initialize(WorkspaceRestorer.Core.Localization.LanguageTags.ZhCn);
                ShortcutCatalog.Configure(null);
                string error;
                Assert(ShortcutCatalog.TryReplaceCommandBindings(
                    "copy-profile", new List<ShortcutBinding>(), out error),
                    "先删空复制命令失败：" + error);

                List<ShortcutBinding> redo = CloneForTest(ShortcutCatalog.Find("redo").Bindings);
                redo.Add(new ShortcutBinding(Key.R, ModifierKeys.Control | ModifierKeys.Alt));
                Assert(ShortcutCatalog.TryReplaceCommandBindings("redo", redo, out error),
                    "给重做加第二条绑定失败：" + error);
                AssertEqual(2, ShortcutCatalog.Find("redo").Bindings.Count);

                Assert(ShortcutCatalog.TryResetCommandToDefault("copy-profile", out error),
                    "恢复复制命令默认失败：" + error);
                ShortcutCatalog.Shortcut copy = ShortcutCatalog.Find("copy-profile");
                Assert(copy != null && copy.Bindings.Count == 1, "恢复后复制命令应回到一条默认绑定。");
                AssertEqual("Ctrl + C", copy.Bindings[0].Keys);
                AssertEqual(2, ShortcutCatalog.Find("redo").Bindings.Count);

                Assert(ShortcutCatalog.TryResetCommandToDefault("redo", out error),
                    "恢复重做默认失败：" + error);
                ShortcutCatalog.Shortcut redoRestored = ShortcutCatalog.Find("redo");
                Assert(redoRestored != null && redoRestored.Bindings.Count == 1,
                    "恢复后重做应只剩一条默认绑定。");
                AssertEqual("Ctrl + Shift + Z", redoRestored.Bindings[0].Keys);

                Assert(!ShortcutCatalog.TryResetCommandToDefault("missing-command", out error),
                    "未知命令不能恢复默认。");
                Assert(error != null && error.Length > 0, "未知命令应给出错误文字。");
                ShortcutCatalog.Configure(null);
            });

            Run("快捷键：键位格式、冲突与保留键", delegate
            {
                AppText.Initialize(WorkspaceRestorer.Core.Localization.LanguageTags.ZhCn);
                AssertEqual("Ctrl + Alt + Shift + C",
                    ShortcutKeyText.Format(
                        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift,
                        Key.C));
                AssertEqual("Ctrl + Shift + Z",
                    ShortcutKeyText.Format(ModifierKeys.Control | ModifierKeys.Shift, Key.Z));

                string error;
                // 2026-11-16 用户拍板：允许单主键，只提示、不拦截。
                Assert(ShortcutKeyText.TryValidate(Key.C, ModifierKeys.None, out error),
                    "没有修饰键的单主键也允许保存。");
                Assert(ShortcutKeyText.NeedsTypingWarning(Key.C, ModifierKeys.None),
                    "单主键要提示打字可能误触。");
                Assert(!ShortcutKeyText.NeedsTypingWarning(Key.C, ModifierKeys.Control),
                    "带修饰键的组合不需要提示。");
                Assert(!ShortcutKeyText.TryValidate(Key.Return, ModifierKeys.Control, out error),
                    "Enter 应保留给确认。");
                Assert(!ShortcutKeyText.TryValidate(Key.Escape, ModifierKeys.Control, out error),
                    "Esc 应保留给取消。");
                Assert(ShortcutKeyText.TryValidate(
                    Key.C,
                    ModifierKeys.Control | ModifierKeys.Alt,
                    out error), "Ctrl+Alt+C 应合法。");

                ShortcutCatalog.Configure(null);
                Assert(!ShortcutCatalog.ValidateCandidate(
                    "copy-profile", Key.V, ModifierKeys.Control, -1, out error),
                    "Ctrl+V 已被粘贴命令占用，必须判冲突。");
                Assert(error != null && error.Contains("Ctrl + V"), "冲突提示应带具体键位。");
                Assert(ShortcutCatalog.ValidateCandidate(
                    "copy-profile", Key.C, ModifierKeys.Control | ModifierKeys.Alt, -1, out error),
                    "Ctrl+Alt+C 没有被占用，应合法：" + error);
            });

            Run("拖动：默认 Shift + 左键，纯修饰键/字母前缀都允许", delegate
            {
                AppText.Initialize(WorkspaceRestorer.Core.Localization.LanguageTags.ZhCn);
                DragGestureSettings.ConfigureFromSettings(null, null, null);
                Assert(DragGestureSettings.PrefixKey == Key.None, "默认拖动没有额外主键。");
                Assert(DragGestureSettings.PrefixModifiers == ModifierKeys.Shift, "默认拖动用 Shift。");
                Assert(DragGestureSettings.MouseButton == MouseButton.Left, "默认拖动用左键。");
                AssertEqual("Shift + 拖动", DragGestureSettings.DisplayKeys("拖动"));

                string error;
                Assert(DragGestureSettings.TryValidatePrefix(Key.None, ModifierKeys.Shift, out error),
                    "纯修饰键应合法：" + error);
                Assert(!DragGestureSettings.TryValidatePrefix(Key.None, ModifierKeys.None, out error),
                    "没有任何前缀应拒绝。");
                Assert(!DragGestureSettings.PrefixHasMainKey(Key.None),
                    "纯修饰键不提示打字冲突。");
                Assert(DragGestureSettings.TryValidatePrefix(Key.A, ModifierKeys.None, out error),
                    "字母主键前缀应合法：" + error);
                Assert(DragGestureSettings.PrefixHasMainKey(Key.A), "字母主键前缀要提示。");
                Assert(!DragGestureSettings.TryValidatePrefix(Key.Escape, ModifierKeys.None, out error),
                    "Esc 不能当拖动前缀。");

                DragGestureSettings.Configure(Key.A, ModifierKeys.Control, MouseButton.Right);
                AssertEqual("Ctrl + A + 拖动", DragGestureSettings.DisplayKeys("拖动"));
                Assert(DragGestureSettings.MouseButton == MouseButton.Right, "右键拖动要生效。");

                // 只按组合键、没点左/右键按钮时，鼠标键必须保持原来那个。
                DragGestureSettings.ConfigureFromSettings("None", (int)ModifierKeys.Alt, "Right");
                Assert(DragGestureSettings.PrefixKey == Key.None, "纯修饰键读回后没有主键。");
                Assert(DragGestureSettings.PrefixModifiers == ModifierKeys.Alt, "Alt 前缀读回正确。");
                Assert(DragGestureSettings.MouseButton == MouseButton.Right, "右键设置读回正确。");
                AssertEqual("Alt + 拖动", DragGestureSettings.DisplayKeys("拖动"));

                DragGestureSettings.ConfigureFromSettings("bad-key", null, null);
                Assert(DragGestureSettings.PrefixKey == Key.None, "坏键名不能把程序带崩。");
                Assert(DragGestureSettings.PrefixModifiers == ModifierKeys.Shift, "坏键名回退 Shift。");
                Assert(DragGestureSettings.MouseButton == MouseButton.Left, "坏鼠标键回退左键。");
                DragGestureSettings.Configure(Key.None, ModifierKeys.Shift, MouseButton.Left);
            });

            Run("快捷键：settings.json 数据形状可序列化并读回", delegate
            {
                ApplicationController.ControllerSettings settings =
                    new ApplicationController.ControllerSettings();
                // 每页条数也要跟 settings.json 一起保存，并且旧设置没有这个成员时回默认 10。
                AssertEqual(10, settings.ShortcutPageSize);
                settings.ShortcutPageSize = 25;
                settings.ShortcutBindings = new List<ShortcutBindingData>();
                settings.ShortcutBindings.Add(new ShortcutBindingData
                {
                    CommandId = "copy-profile",
                    Key = Key.C.ToString(),
                    Modifiers = (int)(ModifierKeys.Control | ModifierKeys.Alt)
                });
                settings.ShortcutBindings.Add(new ShortcutBindingData
                {
                    CommandId = "copy-profile",
                    Key = Key.C.ToString(),
                    Modifiers = (int)ModifierKeys.Control
                });
                settings.DragGestureKey = "None";
                settings.DragGestureModifiers = (int)ModifierKeys.Alt;
                settings.DragGestureMouseButton = "Right";

                DataContractJsonSerializer serializer =
                    new DataContractJsonSerializer(typeof(ApplicationController.ControllerSettings));
                using (MemoryStream stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, settings);
                    stream.Position = 0;
                    ApplicationController.ControllerSettings restored =
                        (ApplicationController.ControllerSettings)serializer.ReadObject(stream);
                    Assert(restored != null && restored.ShortcutBindings != null,
                        "ShortcutBindings 没有读回来。");
                    AssertEqual(2, restored.ShortcutBindings.Count);
                    AssertEqual("copy-profile", restored.ShortcutBindings[0].CommandId);
                    AssertEqual("C", restored.ShortcutBindings[0].Key);
                    AssertEqual((int)(ModifierKeys.Control | ModifierKeys.Alt),
                        restored.ShortcutBindings[0].Modifiers);
                    AssertEqual("None", restored.DragGestureKey);
                    AssertEqual((int)ModifierKeys.Alt, restored.DragGestureModifiers.Value);
                    AssertEqual("Right", restored.DragGestureMouseButton);
                    AssertEqual(25, restored.ShortcutPageSize);
                }
            });

            ShortcutCatalog.Configure(null);
        }

        private static List<ShortcutBinding> CloneForTest(IList<ShortcutBinding> bindings)
        {
            List<ShortcutBinding> copy = new List<ShortcutBinding>();
            if (bindings == null)
            {
                return copy;
            }
            for (int index = 0; index < bindings.Count; index++)
            {
                if (bindings[index] != null)
                {
                    copy.Add(bindings[index].Clone());
                }
            }
            return copy;
        }
    }
}
