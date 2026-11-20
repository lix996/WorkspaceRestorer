using System;
using System.Windows.Input;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI.Cards.Drag
{
    /// <summary>
    /// 【公共区域】拖动起手手势：组合键前缀 + 鼠标键。
    ///
    /// 2026-11-16 用户要求：
    ///   · 按住组合键后，列表项/方案卡片可以整块拖，不必先按 ≡ 手柄；
    ///   · 组合键可以改成单个字母，也可以只用一个修饰键（纯修饰键不提示）；
    ///   · 鼠标键可以选左键或右键；没选时保持上一次的鼠标键，不能写死左键；
    ///   · ≡ 手柄始终左键直接拖，不受这里影响。
    ///
    /// 数据仍由 ApplicationController 从 settings.json 读入/写出，
    /// 这里只保存“当前生效的手势”，并给五个拖动入口提供同一份判断，
    /// 避免每个工作区各写一份 Shift 判断。
    /// </summary>
    internal static class DragGestureSettings
    {
        /// <summary>settings.json 里“没有主键，只有修饰键”的写法。</summary>
        internal const string NoKeyText = "None";
        internal const string MouseButtonLeftText = "Left";
        internal const string MouseButtonRightText = "Right";

        private static Key prefixKey = Key.None;
        private static ModifierKeys prefixModifiers = ModifierKeys.Shift;
        private static MouseButton mouseButton = MouseButton.Left;

        /// <summary>前缀里额外要按住的字母/数字主键；None = 只需要修饰键。</summary>
        internal static Key PrefixKey
        {
            get { return prefixKey; }
        }

        internal static ModifierKeys PrefixModifiers
        {
            get { return prefixModifiers; }
        }

        /// <summary>整块拖动要求按下的鼠标键；≡ 手柄不受它影响。</summary>
        internal static MouseButton MouseButton
        {
            get { return mouseButton; }
        }

        /// <summary>当前是否还是默认手势 Shift + 左键（设置页据此把“恢复”置灰）。</summary>
        internal static bool IsDefault
        {
            get
            {
                return prefixKey == Key.None
                    && prefixModifiers == ModifierKeys.Shift
                    && mouseButton == MouseButton.Left;
            }
        }

        internal static void Configure(Key key, ModifierKeys modifiers, MouseButton button)
        {
            prefixKey = key == Key.None ? Key.None : key;
            prefixModifiers = ShortcutKeyText.NormalizeModifiers(modifiers);
            mouseButton = button == MouseButton.Right ? MouseButton.Right : MouseButton.Left;
        }

        /// <summary>从 settings.json 的三项旧值恢复；空值分别回退到 Shift / 左键。</summary>
        internal static void ConfigureFromSettings(
            string keyText,
            int? modifiers,
            string mouseButtonText)
        {
            Key key = Key.None;
            if (!string.IsNullOrWhiteSpace(keyText)
                && !string.Equals(keyText, NoKeyText, StringComparison.OrdinalIgnoreCase)
                && !ShortcutKeyText.TryParseKey(keyText, out key))
            {
                // 手改坏的键名不能让程序起不来：退回“只有修饰键”。
                key = Key.None;
            }
            ModifierKeys normalized = modifiers.HasValue
                ? (ModifierKeys)modifiers.Value
                : ModifierKeys.Shift;
            Configure(key, normalized, ParseMouseButton(mouseButtonText));
        }

        internal static MouseButton ParseMouseButton(string text)
        {
            return string.Equals(text, MouseButtonRightText, StringComparison.OrdinalIgnoreCase)
                ? MouseButton.Right
                : MouseButton.Left;
        }

        /// <summary>当前键盘状态是否满足拖动前缀（起拖那一刻调用一次）。</summary>
        internal static bool MatchesPrefix()
        {
            if ((Keyboard.Modifiers & prefixModifiers) != prefixModifiers)
            {
                return false;
            }
            if (prefixKey != Key.None && !Keyboard.IsKeyDown(prefixKey))
            {
                return false;
            }
            return true;
        }

        internal static bool MatchesMouseButton(MouseButton button)
        {
            return button == mouseButton;
        }

        /// <summary>这次鼠标移动事件里，配置的鼠标键是否仍然按着。</summary>
        internal static bool IsMouseButtonPressed(MouseEventArgs args, MouseButton button)
        {
            if (args == null)
            {
                return false;
            }
            return button == MouseButton.Right
                ? args.RightButton == MouseButtonState.Pressed
                : args.LeftButton == MouseButtonState.Pressed;
        }

        /// <summary>物理按键状态（与窗口焦点、鼠标捕获无关；看门狗/武装判断用）。</summary>
        internal static bool IsMouseButtonDownPhysical(MouseButton button)
        {
            return button == MouseButton.Right
                ? NativeMethods.IsRightMouseButtonDown()
                : NativeMethods.IsLeftMouseButtonDown();
        }

        /// <summary>
        /// 拖动前缀合法性：必须至少有一个修饰键或一个主键；
        /// Enter/Esc/Tab 留给界面，单独一个 Ctrl/Alt/Shift 是合法的（纯修饰键）。
        /// </summary>
        internal static bool TryValidatePrefix(Key key, ModifierKeys modifiers, out string error)
        {
            error = null;
            ModifierKeys normalized = ShortcutKeyText.NormalizeModifiers(modifiers);
            if (key == Key.None && ShortcutKeyText.ModifierCount(normalized) == 0)
            {
                error = AppText.T(SettingsTextKeys.DragGestureErrorNoPrefix);
                return false;
            }
            if (key != Key.None && ShortcutKeyText.IsModifierKey(key))
            {
                error = AppText.T(SettingsTextKeys.ShortcutErrorOnlyModifier);
                return false;
            }
            if (key != Key.None && ShortcutKeyText.IsReservedKey(key))
            {
                error = AppText.F(
                    SettingsTextKeys.ShortcutErrorReservedFormat,
                    ShortcutKeyText.KeyName(key));
                return false;
            }
            return true;
        }

        /// <summary>前缀里有字母/数字等主键时，起拖要和打字、快捷键抢键状态，需要提示。</summary>
        internal static bool PrefixHasMainKey(Key key)
        {
            return key != Key.None;
        }

        /// <summary>设置页那一行的键位文字，例如 “Shift + 拖动” 或 “Shift + A + 拖动”。</summary>
        internal static string DisplayKeys(string dragWord)
        {
            string prefix = ShortcutKeyText.Format(prefixModifiers, prefixKey);
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return dragWord ?? string.Empty;
            }
            return prefix + " + " + (dragWord ?? string.Empty);
        }
    }
}
