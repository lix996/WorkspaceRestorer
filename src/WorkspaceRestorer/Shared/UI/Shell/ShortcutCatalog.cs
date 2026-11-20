using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】全局快捷键目录 —— **唯一定义处**（命令 Id、名称、说明、默认键位、生效范围）。
    ///
    /// 用户要求（2026-10-10 / 2026-10-17）：快捷键要作用于整个软件，代码放在公共区域，
    /// 设置页显示的就是程序真正认的那一套。
    ///
    /// 2026-11-16 用户要求：快捷键页按 ComfyUI 的方式编辑——每条绑定单独一行，
    /// 点铅笔弹出录键窗口，加号新增绑定，垃圾桶删除单条，恢复图标把单条命令恢复默认。
    /// 所以这里的数据分成两层：
    ///   · <see cref="Shortcut"/>：命令/手势这一行（标题、说明、默认绑定）；
    ///   · <see cref="ShortcutBinding"/>：真正参与判断的一条键位（一条命令可以有多条）。
    ///
    /// 整条链路仍然只有三处：
    ///   1. **这里**：命令与默认键位；
    ///   2. <see cref="GlobalShortcutRouter"/>：按当前绑定判断按键；
    ///   3. <c>MainWindow.WireGlobalShortcuts</c>：把命令 Id 接到动作。
    /// 设置页的“快捷键”直接读这里，用户改完写回 <c>settings.json</c>。
    /// </summary>
    internal static class ShortcutCatalog
    {
        /// <summary>四个工作区通用（显示在设置页每一条的标题后面）。</summary>
        internal const string AllWorkspaces = "settings.shortcuts.scopeAll";

        internal const string GroupCommand = "command";
        internal const string GroupGesture = "gesture";
        internal const string GroupDisplay = "display";

        /// <summary>快捷键页里“拖动”这一行的命令 Id（不是键盘命令，但同一张表里编辑）。</summary>
        internal const string DragGestureId = "drag-gesture";

        private static readonly object Sync = new object();
        private static List<Shortcut> definitions;
        private static Dictionary<string, List<ShortcutBinding>> currentBindings;

        /// <summary>一条快捷键命令/手势：显示用的元数据 + 当前绑定 / 默认绑定。</summary>
        internal sealed class Shortcut
        {
            internal Shortcut()
            {
                Bindings = new List<ShortcutBinding>();
                DefaultBindings = new List<ShortcutBinding>();
                Key = Key.None;
                Modifiers = ModifierKeys.None;
            }

            /// <summary>命令 Id（路由用的键名）。空 = 这一条只是“说明”，没有对应的可执行命令。</summary>
            public string Id { get; set; }

            /// <summary>command / gesture / display；设置页据此决定是否显示编辑按钮。</summary>
            public string Group { get; set; }

            /// <summary>设置页显示的键位文字（命令行由多条绑定拼成，手势/说明行直接写）。</summary>
            public string Keys { get; set; }

            /// <summary>键位文字需要翻译时用 key；为空表示 Keys 是语言无关的按键名。</summary>
            public string KeysKey { get; set; }

            /// <summary>设置页显示的标题 key（较完整的一句话）。</summary>
            public string TitleKey { get; set; }

            /// <summary>设置页快捷键清单里的一句话概括 key（长说明放进问号里）。</summary>
            public string SummaryKey { get; set; }

            /// <summary>设置页显示的说明 key。</summary>
            public string DescriptionKey { get; set; }

            /// <summary>生效范围 key（显示用）。</summary>
            public string ScopeKey { get; set; }

            /// <summary>按当前语言解析的标题；日志、快捷键浮窗和读屏共用。</summary>
            public string Title
            {
                get { return AppText.T(TitleKey); }
            }

            /// <summary>按当前语言解析的一句话概括。</summary>
            public string Summary
            {
                get
                {
                    return AppText.T(string.IsNullOrWhiteSpace(SummaryKey) ? TitleKey : SummaryKey);
                }
            }

            /// <summary>按当前语言解析的完整说明。</summary>
            public string Description
            {
                get { return AppText.T(DescriptionKey); }
            }

            /// <summary>按当前语言解析的生效范围。</summary>
            public string Scope
            {
                get { return string.IsNullOrWhiteSpace(ScopeKey) ? string.Empty : AppText.T(ScopeKey); }
            }

            /// <summary>命令当前的全部绑定（设置页按这个逐个画键帽）。</summary>
            internal List<ShortcutBinding> Bindings { get; set; }

            /// <summary>命令的默认绑定；“恢复默认”用。</summary>
            internal List<ShortcutBinding> DefaultBindings { get; set; }

            /// <summary>只用于“已经解析出是哪一条绑定”的临时对象；设置页看 Bindings。</summary>
            public Key Key { get; set; }

            /// <summary>只用于“已经解析出是哪一条绑定”的临时对象。</summary>
            public ModifierKeys Modifiers { get; set; }

            /// <summary>焦点在文本框里时让给文本框（Ctrl+C / Ctrl+V 必须是普通复制粘贴）。</summary>
            public bool SkipWhenEditingText { get; set; }

            /// <summary>正在拖动列表项时忽略这个快捷键（拖动中撤销会把界面和数据撕开）。</summary>
            public bool IgnoreWhileDragging { get; set; }

            /// <summary>这一条是真的按键绑定，还是只是说明。</summary>
            public bool HasBinding
            {
                get { return !string.IsNullOrEmpty(Id) && Key != Key.None; }
            }

            /// <summary>命令行＝可以在右侧修改/删除；手势和说明行本轮只展示。</summary>
            public bool IsEditable
            {
                get { return string.Equals(Group, GroupCommand, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Id); }
            }

            internal Shortcut CloneMetadata()
            {
                Shortcut clone = new Shortcut();
                clone.Id = Id;
                clone.Group = Group;
                clone.Keys = Keys;
                clone.KeysKey = KeysKey;
                clone.TitleKey = TitleKey;
                clone.SummaryKey = SummaryKey;
                clone.DescriptionKey = DescriptionKey;
                clone.ScopeKey = ScopeKey;
                clone.SkipWhenEditingText = SkipWhenEditingText;
                clone.IgnoreWhileDragging = IgnoreWhileDragging;
                // 设置页要拿默认绑定判断“恢复”是否可用；不能只复制元数据。
                clone.DefaultBindings = CloneBindings(DefaultBindings);
                return clone;
            }
        }

        static ShortcutCatalog()
        {
            BuildDefinitions();
            ResetToDefaults();
        }

        /// <summary>程序当前支持的全部快捷键（顺序＝设置页里显示的顺序）。</summary>
        public static IList<Shortcut> All()
        {
            lock (Sync)
            {
                List<Shortcut> list = new List<Shortcut>();
                for (int index = 0; index < definitions.Count; index++)
                {
                    Shortcut definition = definitions[index];
                    Shortcut row = definition.CloneMetadata();
                    if (string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                    {
                        List<ShortcutBinding> bindings;
                        if (!currentBindings.TryGetValue(definition.Id, out bindings))
                        {
                            bindings = new List<ShortcutBinding>();
                        }
                        row.Bindings = CloneBindings(bindings);
                        row.Keys = JoinBindingKeys(row.Bindings);
                    }
                    list.Add(row);
                }
                return list;
            }
        }

        /// <summary>
        /// 只看“真的要做按键判断”的那些（路由用）。一个命令有多条绑定时，
        /// 每条绑定各返回一个对象，Keys 是那条绑定自己的键位（浮窗/日志不会写成另一条）。
        /// </summary>
        public static IList<Shortcut> Bindings()
        {
            lock (Sync)
            {
                List<Shortcut> list = new List<Shortcut>();
                for (int index = 0; index < definitions.Count; index++)
                {
                    Shortcut definition = definitions[index];
                    if (!string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    List<ShortcutBinding> bindings;
                    if (!currentBindings.TryGetValue(definition.Id, out bindings))
                    {
                        continue;
                    }
                    for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                    {
                        ShortcutBinding binding = bindings[bindingIndex];
                        if (binding == null || binding.Key == Key.None)
                        {
                            continue;
                        }
                        Shortcut resolved = definition.CloneMetadata();
                        resolved.Key = binding.Key;
                        resolved.Modifiers = binding.Modifiers;
                        resolved.Keys = binding.Keys;
                        resolved.Bindings = new List<ShortcutBinding>();
                        resolved.Bindings.Add(binding.Clone());
                        list.Add(resolved);
                    }
                }
                return list;
            }
        }

        /// <summary>按命令 Id 找一条（日志里写“Ctrl + Z”之类用）；返回当前全部绑定。没有就返回 null。</summary>
        public static Shortcut Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            IList<Shortcut> all = All();
            for (int index = 0; index < all.Count; index++)
            {
                if (all[index] != null && string.Equals(all[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return all[index];
                }
            }
            return null;
        }

        /// <summary>
        /// 从 settings.json 恢复当前绑定。传 null = 从未自定义过，使用默认绑定；
        /// 传空列表 = 用户把所有快捷键都删了（有效状态，不能再偷偷恢复默认）。
        /// 坏条目、未知命令、重复键都会被跳过，避免一个手改坏的 JSON 让程序起不来。
        /// </summary>
        internal static void Configure(IList<ShortcutBindingData> settings)
        {
            lock (Sync)
            {
                if (settings == null)
                {
                    // null 不是“所有命令都没有快捷键”，而是“还没自定义过” → 用默认绑定。
                    Dictionary<string, List<ShortcutBinding>> defaults = CreateEmptyBindingMap();
                    for (int index = 0; index < definitions.Count; index++)
                    {
                        Shortcut definition = definitions[index];
                        if (string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                        {
                            defaults[definition.Id] = CloneBindings(definition.DefaultBindings);
                        }
                    }
                    currentBindings = defaults;
                    return;
                }
                Dictionary<string, List<ShortcutBinding>> next = CreateEmptyBindingMap();
                if (settings != null)
                {
                    for (int index = 0; index < settings.Count; index++)
                    {
                        ShortcutBindingData data = settings[index];
                        if (data == null || string.IsNullOrWhiteSpace(data.CommandId))
                        {
                            continue;
                        }
                        string commandId = FindCommandId(data.CommandId);
                        if (commandId == null)
                        {
                            continue;
                        }
                        Key key;
                        if (!ShortcutKeyText.TryParseKey(data.Key, out key))
                        {
                            continue;
                        }
                        ModifierKeys modifiers = ShortcutKeyText.NormalizeModifiers((ModifierKeys)data.Modifiers);
                        string ignored;
                        if (!ShortcutKeyText.TryValidate(key, modifiers, out ignored))
                        {
                            continue;
                        }
                        List<ShortcutBinding> target = next[commandId];
                        if (ContainsKey(next, key, modifiers, commandId, -1))
                        {
                            continue;
                        }
                        target.Add(new ShortcutBinding(key, modifiers));
                    }
                }
                currentBindings = next;
            }
        }

        /// <summary>把当前全部命令绑定导出成 settings.json 的形状（按命令顺序）。</summary>
        internal static List<ShortcutBindingData> ExportBindings()
        {
            lock (Sync)
            {
                List<ShortcutBindingData> list = new List<ShortcutBindingData>();
                for (int index = 0; index < definitions.Count; index++)
                {
                    Shortcut definition = definitions[index];
                    if (!string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    List<ShortcutBinding> bindings;
                    if (!currentBindings.TryGetValue(definition.Id, out bindings))
                    {
                        continue;
                    }
                    for (int bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
                    {
                        ShortcutBinding binding = bindings[bindingIndex];
                        if (binding == null || binding.Key == Key.None)
                        {
                            continue;
                        }
                        list.Add(new ShortcutBindingData
                        {
                            CommandId = definition.Id,
                            Key = binding.Key.ToString(),
                            Modifiers = (int)binding.Modifiers
                        });
                    }
                }
                return list;
            }
        }

        /// <summary>
        /// 设置页“恢复”按钮用：当前绑定与默认绑定是否相同。
        /// 按集合比较、顺序无关；只改其中一条也算自定义，恢复按钮保持可用。
        /// </summary>
        internal static bool IsAtDefaultBindings(Shortcut shortcut)
        {
            if (shortcut == null)
            {
                return false;
            }
            List<ShortcutBinding> current = shortcut.Bindings;
            List<ShortcutBinding> defaults = shortcut.DefaultBindings;
            int currentCount = current == null ? 0 : current.Count;
            int defaultCount = defaults == null ? 0 : defaults.Count;
            if (currentCount != defaultCount)
            {
                return false;
            }
            if (currentCount == 0)
            {
                return true;
            }

            bool[] matched = new bool[defaultCount];
            for (int index = 0; index < currentCount; index++)
            {
                ShortcutBinding candidate = current[index];
                if (candidate == null)
                {
                    return false;
                }
                bool found = false;
                for (int defaultIndex = 0; defaultIndex < defaultCount; defaultIndex++)
                {
                    if (matched[defaultIndex] || defaults[defaultIndex] == null)
                    {
                        continue;
                    }
                    if (defaults[defaultIndex].Key == candidate.Key
                        && defaults[defaultIndex].Modifiers == candidate.Modifiers)
                    {
                        matched[defaultIndex] = true;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>恢复全部命令的默认快捷键（不影响其它设置）。</summary>
        internal static void ResetToDefaults()
        {
            lock (Sync)
            {
                Dictionary<string, List<ShortcutBinding>> next = CreateEmptyBindingMap();
                for (int index = 0; index < definitions.Count; index++)
                {
                    Shortcut definition = definitions[index];
                    if (!string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    next[definition.Id] = CloneBindings(definition.DefaultBindings);
                }
                currentBindings = next;
            }
        }

        /// <summary>
        /// 只把一条命令恢复默认（快捷键页每行的“恢复”图标用）。
        /// 其它命令的绑定、其它设置都不动；失败时当前绑定原样保留。
        /// </summary>
        internal static bool TryResetCommandToDefault(string commandId, out string error)
        {
            error = null;
            lock (Sync)
            {
                string resolvedId = FindCommandId(commandId);
                if (resolvedId == null)
                {
                    error = AppText.T(SettingsTextKeys.ShortcutErrorUnknownCommand);
                    return false;
                }
                List<ShortcutBinding> defaults = new List<ShortcutBinding>();
                for (int index = 0; index < definitions.Count; index++)
                {
                    Shortcut definition = definitions[index];
                    if (string.Equals(definition.Id, resolvedId, StringComparison.OrdinalIgnoreCase))
                    {
                        defaults = CloneBindings(definition.DefaultBindings);
                        break;
                    }
                }
                currentBindings[resolvedId] = defaults;
                return true;
            }
        }

        /// <summary>
        /// 设置页一次改一整条命令的绑定列表（修改/删除都走这里）。
        /// 先整体校验再替换，失败时当前绑定原样不动。
        /// </summary>
        internal static bool TryReplaceCommandBindings(
            string commandId,
            IList<ShortcutBinding> bindings,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(commandId))
            {
                error = AppText.T(SettingsTextKeys.ShortcutErrorUnknownCommand);
                return false;
            }
            lock (Sync)
            {
                string resolvedId = FindCommandId(commandId);
                if (resolvedId == null)
                {
                    error = AppText.T(SettingsTextKeys.ShortcutErrorUnknownCommand);
                    return false;
                }
                List<ShortcutBinding> candidate = bindings == null
                    ? new List<ShortcutBinding>()
                    : new List<ShortcutBinding>();
                if (bindings != null)
                {
                    for (int index = 0; index < bindings.Count; index++)
                    {
                        ShortcutBinding binding = bindings[index];
                        if (binding == null || binding.Key == Key.None)
                        {
                            continue;
                        }
                        string validation;
                        if (!ShortcutKeyText.TryValidate(binding.Key, binding.Modifiers, out validation))
                        {
                            error = validation;
                            return false;
                        }
                        if (ContainsKeyInList(candidate, binding.Key, binding.Modifiers))
                        {
                            error = AppText.F(
                                SettingsTextKeys.ShortcutErrorDuplicateFormat,
                                ShortcutKeyText.Format(binding.Modifiers, binding.Key),
                                AppText.T(TitleKeyOf(resolvedId)));
                            return false;
                        }
                        // 这里是在“替换整条命令的列表”：这条命令现有的绑定马上就会被
                        // 整个换掉，不能拿它自己的旧键位来判冲突（否则把 Ctrl+C 改成
                        // Ctrl+Shift+C 时，列表里没改的旧键位会被误判成“已被占用”）。
                        // 同一个候选列表内部的重复在上面已经查过。
                        if (ContainsKeyExceptCommand(
                            currentBindings, binding.Key, binding.Modifiers, resolvedId))
                        {
                            error = BuildConflictError(resolvedId, binding.Key, binding.Modifiers);
                            return false;
                        }
                        candidate.Add(binding.Clone());
                    }
                }
                currentBindings[resolvedId] = candidate;
                return true;
            }
        }

        /// <summary>检查一个候选键位能否用在这个命令上（编辑时忽略被替换的那一条）。</summary>
        internal static bool ValidateCandidate(
            string commandId,
            Key key,
            ModifierKeys modifiers,
            int ignoredBindingIndex,
            out string error)
        {
            error = null;
            string validation;
            if (!ShortcutKeyText.TryValidate(key, modifiers, out validation))
            {
                error = validation;
                return false;
            }
            lock (Sync)
            {
                string resolvedId = FindCommandId(commandId);
                if (resolvedId == null)
                {
                    error = AppText.T(SettingsTextKeys.ShortcutErrorUnknownCommand);
                    return false;
                }
                if (ContainsKey(currentBindings, key, modifiers, resolvedId, ignoredBindingIndex))
                {
                    error = BuildConflictError(resolvedId, key, modifiers);
                    return false;
                }
            }
            return true;
        }

        /// <summary>当前按键是不是某条命令的绑定（只判断、不执行；自检与界面提示可用）。</summary>
        internal static Shortcut Resolve(Key key, ModifierKeys modifiers)
        {
            IList<Shortcut> bindings = Bindings();
            for (int index = 0; index < bindings.Count; index++)
            {
                Shortcut shortcut = bindings[index];
                if (shortcut != null && shortcut.Key == key && shortcut.Modifiers == modifiers)
                {
                    return shortcut;
                }
            }
            return null;
        }

        private static void BuildDefinitions()
        {
            definitions = new List<Shortcut>();

            Shortcut copy = new Shortcut
            {
                Id = "copy-profile",
                Group = GroupCommand,
                TitleKey = "settings.shortcuts.copyProfile.title",
                SummaryKey = "settings.shortcuts.copyProfile.summary",
                DescriptionKey = "settings.shortcuts.copyProfile.description",
                ScopeKey = AllWorkspaces,
                SkipWhenEditingText = true
            };
            copy.DefaultBindings.Add(new ShortcutBinding(Key.C, ModifierKeys.Control));
            definitions.Add(copy);

            Shortcut paste = new Shortcut
            {
                Id = "paste-profile",
                Group = GroupCommand,
                TitleKey = "settings.shortcuts.pasteProfile.title",
                SummaryKey = "settings.shortcuts.pasteProfile.summary",
                DescriptionKey = "settings.shortcuts.pasteProfile.description",
                ScopeKey = AllWorkspaces,
                SkipWhenEditingText = true
            };
            paste.DefaultBindings.Add(new ShortcutBinding(Key.V, ModifierKeys.Control));
            definitions.Add(paste);

            Shortcut undo = new Shortcut
            {
                Id = "undo",
                Group = GroupCommand,
                TitleKey = "settings.shortcuts.undo.title",
                SummaryKey = "settings.shortcuts.undo.summary",
                DescriptionKey = "settings.shortcuts.undo.description",
                ScopeKey = AllWorkspaces,
                IgnoreWhileDragging = true
            };
            undo.DefaultBindings.Add(new ShortcutBinding(Key.Z, ModifierKeys.Control));
            definitions.Add(undo);

            Shortcut redo = new Shortcut
            {
                Id = "redo",
                Group = GroupCommand,
                TitleKey = "settings.shortcuts.redo.title",
                SummaryKey = "settings.shortcuts.redo.summary",
                DescriptionKey = "settings.shortcuts.redo.description",
                ScopeKey = AllWorkspaces,
                IgnoreWhileDragging = true
            };
            redo.DefaultBindings.Add(new ShortcutBinding(Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
            definitions.Add(redo);

            definitions.Add(new Shortcut
            {
                Id = DragGestureId,
                Group = GroupGesture,
                TitleKey = "settings.shortcuts.shiftDrag.title",
                SummaryKey = "settings.shortcuts.shiftDrag.summary",
                DescriptionKey = "settings.shortcuts.shiftDrag.description",
                ScopeKey = AllWorkspaces
            });
            definitions.Add(new Shortcut
            {
                Group = GroupDisplay,
                Keys = "Enter",
                TitleKey = "settings.shortcuts.confirmEnter.title",
                SummaryKey = "settings.shortcuts.confirmEnter.summary",
                DescriptionKey = "settings.shortcuts.confirmEnter.description",
                ScopeKey = AllWorkspaces
            });
            definitions.Add(new Shortcut
            {
                Group = GroupDisplay,
                Keys = "Esc",
                TitleKey = "settings.shortcuts.closeEsc.title",
                SummaryKey = "settings.shortcuts.closeEsc.summary",
                DescriptionKey = "settings.shortcuts.closeEsc.description",
                ScopeKey = AllWorkspaces
            });
        }

        private static Dictionary<string, List<ShortcutBinding>> CreateEmptyBindingMap()
        {
            Dictionary<string, List<ShortcutBinding>> map =
                new Dictionary<string, List<ShortcutBinding>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < definitions.Count; index++)
            {
                Shortcut definition = definitions[index];
                if (string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal))
                {
                    map[definition.Id] = new List<ShortcutBinding>();
                }
            }
            return map;
        }

        private static string FindCommandId(string id)
        {
            for (int index = 0; index < definitions.Count; index++)
            {
                Shortcut definition = definitions[index];
                if (string.Equals(definition.Group, GroupCommand, StringComparison.Ordinal)
                    && string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return definition.Id;
                }
            }
            return null;
        }

        private static string TitleKeyOf(string commandId)
        {
            for (int index = 0; index < definitions.Count; index++)
            {
                if (string.Equals(definitions[index].Id, commandId, StringComparison.OrdinalIgnoreCase))
                {
                    return definitions[index].TitleKey;
                }
            }
            return commandId;
        }

        private static string JoinBindingKeys(IList<ShortcutBinding> bindings)
        {
            StringBuilder text = new StringBuilder();
            if (bindings == null)
            {
                return string.Empty;
            }
            for (int index = 0; index < bindings.Count; index++)
            {
                ShortcutBinding binding = bindings[index];
                if (binding == null || binding.Key == Key.None)
                {
                    continue;
                }
                if (text.Length > 0)
                {
                    text.Append(" / ");
                }
                text.Append(binding.Keys);
            }
            return text.ToString();
        }

        private static List<ShortcutBinding> CloneBindings(IList<ShortcutBinding> bindings)
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

        /// <summary>
        /// 只查“别的命令”有没有占用这个键位。用于整条命令替换：本命令的旧绑定会被整体换掉，
        /// 不参与冲突判断，否则列表里没改的旧键位会把自己判成冲突。
        /// </summary>
        private static bool ContainsKeyExceptCommand(
            Dictionary<string, List<ShortcutBinding>> map,
            Key key,
            ModifierKeys modifiers,
            string ignoredCommandId)
        {
            foreach (KeyValuePair<string, List<ShortcutBinding>> pair in map)
            {
                if (string.Equals(pair.Key, ignoredCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (ContainsKeyInList(pair.Value, key, modifiers, -1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsKey(
            Dictionary<string, List<ShortcutBinding>> map,
            Key key,
            ModifierKeys modifiers,
            string ignoredCommandId,
            int ignoredBindingIndex)
        {
            foreach (KeyValuePair<string, List<ShortcutBinding>> pair in map)
            {
                if (string.Equals(pair.Key, ignoredCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    if (ContainsKeyInList(pair.Value, key, modifiers, ignoredBindingIndex))
                    {
                        return true;
                    }
                    continue;
                }
                if (ContainsKeyInList(pair.Value, key, modifiers, -1))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsKeyInList(
            IList<ShortcutBinding> bindings,
            Key key,
            ModifierKeys modifiers)
        {
            return ContainsKeyInList(bindings, key, modifiers, -1);
        }

        private static bool ContainsKeyInList(
            IList<ShortcutBinding> bindings,
            Key key,
            ModifierKeys modifiers,
            int ignoredIndex)
        {
            if (bindings == null)
            {
                return false;
            }
            for (int index = 0; index < bindings.Count; index++)
            {
                if (index == ignoredIndex || bindings[index] == null)
                {
                    continue;
                }
                if (bindings[index].Key == key && bindings[index].Modifiers == modifiers)
                {
                    return true;
                }
            }
            return false;
        }

        private static string BuildConflictError(string commandId, Key key, ModifierKeys modifiers)
        {
            string keys = ShortcutKeyText.Format(modifiers, key);
            IList<Shortcut> all = All();
            for (int index = 0; index < all.Count; index++)
            {
                Shortcut row = all[index];
                if (row == null || row.Bindings == null)
                {
                    continue;
                }
                for (int bindingIndex = 0; bindingIndex < row.Bindings.Count; bindingIndex++)
                {
                    ShortcutBinding binding = row.Bindings[bindingIndex];
                    if (binding != null && binding.Key == key && binding.Modifiers == modifiers)
                    {
                        return AppText.F(
                            SettingsTextKeys.ShortcutErrorConflictFormat,
                            keys,
                            AppText.T(row.TitleKey));
                    }
                }
            }
            return AppText.F(SettingsTextKeys.ShortcutErrorConflictFormat, keys, AppText.T(TitleKeyOf(commandId)));
        }
    }
}
