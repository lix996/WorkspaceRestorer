using System;
using System.Collections.Generic;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI.Shared
{
    /// <summary>
    /// 【公共区域】全局撤销 / 重做栈 —— 整个软件**只有这一份**。
    ///
    /// 用户要求（2026-10-17）：
    /// “快捷键的代码要放到公共文件夹内，能够让：启动与恢复，图标位置保存，自启动应用，回收站等
    ///  都能用上，是作用于软件的全局。”
    ///
    /// 所以撤销这件事也必须在公共区域：四个工作区（启动与恢复 / 图标位置保存 / 自启动应用 / 回收站）
    /// 谁改了数据，就往这里压一条记录，Ctrl+Z 撤的就是**最近那一条**（不管它属于哪个工作区）。
    /// 每条记录都带 <see cref="Entry.Scope"/>（哪个工作区），所以：
    ///   · 状态栏会写清“已撤销（在「自启动应用」里）：…”，不会让人以为按了没反应；
    ///   · 日志里能查到每一步是谁压进来的、什么时候被撤销的（排查“Ctrl+Z 没反应”靠它）。
    ///
    /// 用法（各工作区都一样，只有一句）：
    /// <code>
    /// PushUndo("删除自启动项目", 撤销时要做的事, 重做时要做的事);
    /// </code>
    /// 主窗口里的 <c>MainWindow.PushUndo</c> 是唯一入口，它会把“当前工作区”标签一起写进来。
    /// </summary>
    internal sealed class UndoStack
    {
        /// <summary>最多记多少步（超出就把最老的丢掉）。</summary>
        internal const int MaximumEntries = 100;

        /// <summary>一条可撤销的记录：说明 + 撤销动作 + 重做动作 + 它属于哪个工作区。</summary>
        internal sealed class Entry
        {
            public string Scope { get; set; }
            public string Description { get; set; }
            public Action Undo { get; set; }
            public Action Redo { get; set; }
        }

        private readonly List<Entry> undoHistory = new List<Entry>();
        private readonly List<Entry> redoHistory = new List<Entry>();

        /// <summary>状态栏出口：由主窗口接到界面上那一行状态文字。</summary>
        public Action<string> StatusSink { get; set; }

        /// <summary>还能撤销几步。</summary>
        public int UndoCount
        {
            get { return undoHistory.Count; }
        }

        /// <summary>还能重做几步。</summary>
        public int RedoCount
        {
            get { return redoHistory.Count; }
        }

        /// <summary>下一步撤销的是什么（没有就是 null）。调试和自检用。</summary>
        public Entry NextUndo
        {
            get { return undoHistory.Count == 0 ? null : undoHistory[undoHistory.Count - 1]; }
        }

        /// <summary>下一步重做的是什么（没有就是 null）。</summary>
        public Entry NextRedo
        {
            get { return redoHistory.Count == 0 ? null : redoHistory[redoHistory.Count - 1]; }
        }

        /// <summary>
        /// 记一条可撤销的操作。<paramref name="scope"/> 是工作区名（“自启动应用”这种），
        /// 只用来写状态栏与日志，不影响撤销顺序。
        /// </summary>
        public void Push(string scope, string description, Action undo, Action redo)
        {
            if (undo == null)
            {
                return;   // 没有撤销动作的操作不进栈（例如“立即恢复”这种不可逆的外部动作）
            }
            Entry entry = new Entry();
            entry.Scope = string.IsNullOrWhiteSpace(scope)
                ? AppText.T(ShellTextKeys.UndoScopeUntitled)
                : scope.Trim();
            entry.Description = string.IsNullOrWhiteSpace(description)
                ? AppText.T(ShellTextKeys.UndoDescriptionFallback)
                : description.Trim();
            entry.Undo = undo;
            entry.Redo = redo;
            undoHistory.Add(entry);
            if (undoHistory.Count > MaximumEntries)
            {
                undoHistory.RemoveAt(0);
            }
            // 新动作一来，之前“撤销过”的那些就不该再重做了（与所有编辑器的行为一致）。
            redoHistory.Clear();
            AppLog.Write("[撤销] 记录「" + entry.Scope + "」" + entry.Description
                + "（现在可撤销 " + undoHistory.Count + " 步）");
        }

        /// <summary>
        /// 撤销最近一条。<paramref name="currentScope"/> 是**当前**所在的工作区，
        /// 只用来判断状态栏要不要注明“这条是在别的页面里做的”。
        /// </summary>
        public bool Undo(string currentScope)
        {
            if (undoHistory.Count == 0)
            {
                Status(AppText.T(ShellTextKeys.UndoNothingToUndo));
                AppLog.Write("[撤销] 栈是空的（当前工作区=" + (currentScope ?? "?") + "），没有可撤销的操作");
                return false;
            }
            Entry entry = undoHistory[undoHistory.Count - 1];
            undoHistory.RemoveAt(undoHistory.Count - 1);
            try
            {
                entry.Undo();
                redoHistory.Add(entry);
                string note = ScopeNote(entry, currentScope);
                Status(string.IsNullOrEmpty(note)
                    ? AppText.F(ShellTextKeys.UndoDoneFormat, entry.Description)
                    : AppText.F(ShellTextKeys.UndoDoneInScopeFormat, entry.Description, note));
                AppLog.Write("[撤销] 执行「" + entry.Scope + "」" + entry.Description
                    + "（还剩 " + undoHistory.Count + " 步可撤销，当前工作区=" + (currentScope ?? "?") + "）");
                return true;
            }
            catch (Exception exception)
            {
                Status(AppText.F(ShellTextKeys.UndoFailedFormat, exception.Message));
                AppLog.Write("[撤销] 「" + entry.Scope + "」" + entry.Description + " 执行失败", exception);
                return false;
            }
        }

        /// <summary>重做最近一条被撤销的操作。</summary>
        public bool Redo(string currentScope)
        {
            if (redoHistory.Count == 0)
            {
                Status(AppText.T(ShellTextKeys.UndoNothingToRedo));
                AppLog.Write("[撤销] 重做栈是空的（当前工作区=" + (currentScope ?? "?") + "）");
                return false;
            }
            Entry entry = redoHistory[redoHistory.Count - 1];
            redoHistory.RemoveAt(redoHistory.Count - 1);
            if (entry.Redo == null)
            {
                Status(AppText.T(ShellTextKeys.UndoCannotRedo));
                AppLog.Write("[撤销] 「" + entry.Scope + "」" + entry.Description + " 没有重做动作");
                return false;
            }
            try
            {
                entry.Redo();
                undoHistory.Add(entry);
                string note = ScopeNote(entry, currentScope);
                Status(string.IsNullOrEmpty(note)
                    ? AppText.F(ShellTextKeys.UndoRedoneFormat, entry.Description)
                    : AppText.F(ShellTextKeys.UndoRedoneInScopeFormat, entry.Description, note));
                AppLog.Write("[撤销] 重做「" + entry.Scope + "」" + entry.Description
                    + "（现在可撤销 " + undoHistory.Count + " 步）");
                return true;
            }
            catch (Exception exception)
            {
                Status(AppText.F(ShellTextKeys.UndoRedoFailedFormat, exception.Message));
                AppLog.Write("[撤销] 「" + entry.Scope + "」" + entry.Description + " 重做失败", exception);
                return false;
            }
        }

        /// <summary>清空（切换数据目录、重置之类时用）。</summary>
        public void Clear()
        {
            undoHistory.Clear();
            redoHistory.Clear();
        }

        /// <summary>条目不属于“当前工作区”时，状态栏要注明一句，免得以为按了没反应。</summary>
        private static string ScopeNote(Entry entry, string currentScope)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Scope))
            {
                return string.Empty;
            }
            if (!string.IsNullOrEmpty(currentScope)
                && string.Equals(entry.Scope, currentScope, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
            return AppText.F(ShellTextKeys.UndoScopeNoteFormat, entry.Scope);
        }

        private void Status(string text)
        {
            Action<string> sink = StatusSink;
            if (sink != null)
            {
                sink(text);
            }
        }
    }
}
