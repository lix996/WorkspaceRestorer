using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Windows.Input;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 一条用户快捷键在 settings.json 里的保存形状。
    /// 键名用字符串保存（例如 "C" / "D1" / "OemPlus"），不依赖枚举数字；
    /// 修饰键用 <see cref="ModifierKeys"/> 的位组合。
    /// </summary>
    [DataContract]
    public sealed class ShortcutBindingData
    {
        [DataMember(Order = 1)]
        public string CommandId { get; set; }

        [DataMember(Order = 2)]
        public string Key { get; set; }

        [DataMember(Order = 3)]
        public int Modifiers { get; set; }
    }

    /// <summary>
    /// 一条真正参与判断的快捷键（一个命令可以有多条）。
    /// 设置页的键帽、路由判断、冲突校验都用这个对象，保证“看到的就是按到的”。
    /// </summary>
    internal sealed class ShortcutBinding
    {
        internal ShortcutBinding()
        {
            Key = Key.None;
            Modifiers = ModifierKeys.None;
        }

        internal ShortcutBinding(Key key, ModifierKeys modifiers)
        {
            Key = key;
            Modifiers = ShortcutKeyText.NormalizeModifiers(modifiers);
        }

        internal Key Key { get; set; }

        internal ModifierKeys Modifiers { get; set; }

        /// <summary>设置页、浮窗和日志里显示的键位文字，例如 “Ctrl + Alt + C”。</summary>
        internal string Keys
        {
            get { return ShortcutKeyText.Format(Modifiers, Key); }
        }

        internal ShortcutBinding Clone()
        {
            return new ShortcutBinding(Key, Modifiers);
        }

        internal bool SameAs(Key key, ModifierKeys modifiers)
        {
            return Key == key && Modifiers == ShortcutKeyText.NormalizeModifiers(modifiers);
        }
    }

    /// <summary>
    /// 快捷键的键盘文字与合法性规则。只处理物理按键，不接收手打文字。
    ///
    /// 规则（和设置页文案一致）：
    ///   · 0～3 个修饰键（Ctrl / Alt / Shift）+ 1 个主键；允许字母/数字单独当快捷键，
    ///     但设置页会用橙色提示“打字时可能误触”，只提示、不拦截；
    ///   · Enter / Esc / Tab 以及单独按修饰键不能作为命令快捷键（它们要留给界面）；
    ///   · 同一组键不能同时绑到两个命令上。
    /// </summary>
    internal static class ShortcutKeyText
    {
        private const ModifierKeys SupportedModifiers =
            ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift;

        internal static ModifierKeys NormalizeModifiers(ModifierKeys modifiers)
        {
            return modifiers & SupportedModifiers;
        }

        internal static int ModifierCount(ModifierKeys modifiers)
        {
            int count = 0;
            ModifierKeys normalized = NormalizeModifiers(modifiers);
            if ((normalized & ModifierKeys.Control) != 0) { count++; }
            if ((normalized & ModifierKeys.Alt) != 0) { count++; }
            if ((normalized & ModifierKeys.Shift) != 0) { count++; }
            return count;
        }

        internal static string Format(ModifierKeys modifiers, Key key)
        {
            List<string> parts = new List<string>();
            ModifierKeys normalized = NormalizeModifiers(modifiers);
            // 和大多数软件一致：Ctrl、Alt、Shift 的顺序固定，不按用户按下的顺序跳来跳去。
            if ((normalized & ModifierKeys.Control) != 0) { parts.Add("Ctrl"); }
            if ((normalized & ModifierKeys.Alt) != 0) { parts.Add("Alt"); }
            if ((normalized & ModifierKeys.Shift) != 0) { parts.Add("Shift"); }
            if (key != Key.None)
            {
                parts.Add(KeyName(key));
            }
            return string.Join(" + ", parts.ToArray());
        }

        internal static string FormatModifiers(ModifierKeys modifiers)
        {
            return Format(modifiers, Key.None);
        }

        internal static string KeyName(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return ((char)('A' + (key - Key.A))).ToString();
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((char)('0' + (key - Key.D0))).ToString();
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
            {
                return "Num " + (key - Key.NumPad0);
            }
            if (key >= Key.F1 && key <= Key.F24)
            {
                return "F" + (key - Key.F1 + 1);
            }
            switch (key)
            {
                case Key.None: return string.Empty;
                case Key.Cancel: return "Cancel";
                case Key.Back: return "Backspace";
                case Key.Tab: return "Tab";
                case Key.LineFeed: return "LineFeed";
                case Key.Clear: return "Clear";
                case Key.Return: return "Enter";
                case Key.Pause: return "Pause";
                case Key.Capital: return "Caps Lock";
                case Key.KanaMode: return "Kana";
                case Key.JunjaMode: return "Junja";
                case Key.FinalMode: return "Final";
                case Key.HanjaMode: return "Hanja";
                case Key.Escape: return "Esc";
                case Key.ImeConvert: return "IME Convert";
                case Key.ImeNonConvert: return "IME NonConvert";
                case Key.ImeAccept: return "IME Accept";
                case Key.ImeModeChange: return "IME Mode";
                case Key.Space: return "Space";
                case Key.Prior: return "Page Up";
                case Key.Next: return "Page Down";
                case Key.End: return "End";
                case Key.Home: return "Home";
                case Key.Left: return "Left";
                case Key.Up: return "Up";
                case Key.Right: return "Right";
                case Key.Down: return "Down";
                case Key.Select: return "Select";
                case Key.Print: return "Print";
                case Key.Execute: return "Execute";
                case Key.Snapshot: return "Print Screen";
                case Key.Insert: return "Insert";
                case Key.Delete: return "Delete";
                case Key.Help: return "Help";
                case Key.LWin: return "Left Win";
                case Key.RWin: return "Right Win";
                case Key.Apps: return "Menu";
                case Key.Sleep: return "Sleep";
                case Key.Multiply: return "Num *";
                case Key.Add: return "Num +";
                case Key.Separator: return "Separator";
                case Key.Subtract: return "Num -";
                case Key.Decimal: return "Num .";
                case Key.Divide: return "Num /";
                case Key.OemSemicolon: return ";";
                case Key.OemPlus: return "=";
                case Key.OemComma: return ",";
                case Key.OemMinus: return "-";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.OemTilde: return "`";
                case Key.OemOpenBrackets: return "[";
                case Key.OemPipe: return "\\";
                case Key.OemCloseBrackets: return "]";
                case Key.OemQuotes: return "'";
                case Key.Oem8: return "OEM 8";
                case Key.OemBackslash: return "\\";
                case Key.VolumeMute: return "Volume Mute";
                case Key.VolumeDown: return "Volume Down";
                case Key.VolumeUp: return "Volume Up";
                case Key.MediaNextTrack: return "Next Track";
                case Key.MediaPreviousTrack: return "Previous Track";
                case Key.MediaStop: return "Stop";
                case Key.MediaPlayPause: return "Play / Pause";
                case Key.LaunchMail: return "Mail";
                case Key.SelectMedia: return "Select Media";
                case Key.LaunchApplication1: return "App 1";
                case Key.LaunchApplication2: return "App 2";
                case Key.BrowserBack: return "Browser Back";
                case Key.BrowserForward: return "Browser Forward";
                case Key.BrowserRefresh: return "Browser Refresh";
                case Key.BrowserStop: return "Browser Stop";
                case Key.BrowserSearch: return "Browser Search";
                case Key.BrowserFavorites: return "Browser Favorites";
                case Key.BrowserHome: return "Browser Home";
                default:
                    return key.ToString();
            }
        }

        internal static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftShift || key == Key.RightShift
                || key == Key.LWin || key == Key.RWin;
        }

        /// <summary>这些键不参与命令绑定：它们要留给键盘焦点/确认/取消。</summary>
        internal static bool IsReservedKey(Key key)
        {
            return key == Key.Return || key == Key.Escape || key == Key.Tab
                || key == Key.System || key == Key.ImeProcessed
                || key == Key.DeadCharProcessed;
        }

        /// <summary>这一组键能不能作为命令快捷键；不能时返回给用户看的原因。</summary>
        internal static bool TryValidate(Key key, ModifierKeys modifiers, out string error)
        {
            error = null;
            if (key == Key.None)
            {
                error = AppText.T(SettingsTextKeys.ShortcutErrorNoKey);
                return false;
            }
            if (IsModifierKey(key))
            {
                error = AppText.T(SettingsTextKeys.ShortcutErrorOnlyModifier);
                return false;
            }
            if (IsReservedKey(key))
            {
                error = AppText.F(SettingsTextKeys.ShortcutErrorReservedFormat, KeyName(key));
                return false;
            }
            return true;
        }

        /// <summary>
        /// 只有一个字母/数字主键、没有任何修饰键：合法，但打字时容易误触。
        /// 设置页据此显示橙色提示；不阻止保存（用户 2026-11-16 拍板“只提示、不拦截”）。
        /// </summary>
        internal static bool NeedsTypingWarning(Key key, ModifierKeys modifiers)
        {
            return key != Key.None && ModifierCount(modifiers) == 0;
        }

        /// <summary>
        /// 把设置里存下来的键名转回 WPF Key。只接受枚举里真实存在的名字；
        /// 旧版本/手改坏的条目直接跳过，不让程序启动就炸。
        /// </summary>
        internal static bool TryParseKey(string text, out Key key)
        {
            key = Key.None;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            try
            {
                object parsed = Enum.Parse(typeof(Key), text.Trim(), true);
                if (parsed == null || !(parsed is Key))
                {
                    return false;
                }
                key = (Key)parsed;
                return key != Key.None && Enum.IsDefined(typeof(Key), key);
            }
            catch
            {
                return false;
            }
        }
    }
}
