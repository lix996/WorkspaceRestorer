namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 【编辑器生命周期状态】只保存“编辑页现在处于什么阶段”，不碰任何控件。
    ///
    /// 以前 saving/savedLayout/closed/userMoved/layoutDirty/desktopLoadCompleted 散落在
    /// 6000 行的 DesktopLayoutEditorWindow 里，异步加载、拖动、关闭确认各自读其中几个，
    /// 很容易出现“第一次一个状态、第二次另一个状态”。集中到这里以后，
    /// 关闭确认、保存、加载完成都从同一份状态判断。
    /// </summary>
    internal sealed class DesktopLayoutEditorState
    {
        // Closed 会被后台图标加载线程读取，保持 volatile 语义。
        private volatile bool closed;

        /// <summary>正在保存布局（保存期间禁止关闭/拖动/再保存）。</summary>
        public bool Saving { get; set; }

        /// <summary>已经成功保存并正在关闭。</summary>
        public bool SavedLayout { get; set; }

        /// <summary>窗口已经关闭；后台线程/延迟回调据此停止。</summary>
        public bool Closed
        {
            get { return closed; }
            set { closed = value; }
        }

        /// <summary>程序主动关闭（读取桌面失败等），不再弹“未保存”确认。</summary>
        public bool CloseWithoutPrompt { get; set; }

        /// <summary>用户是否在编辑页里动过图标/新图标（状态栏文案用）。</summary>
        public bool UserMoved { get; set; }

        /// <summary>布局内容是否真的变了（关闭确认只看这一项）。</summary>
        public bool LayoutDirty { get; set; }

        /// <summary>当前桌面图标是否已经读完。</summary>
        public bool DesktopLoadCompleted { get; set; }

        /// <summary>画布 tile 是否已经开始分批构建（一次性入口）。</summary>
        public bool IconTileBuildStarted { get; set; }

        /// <summary>用户改动布局内容：同时置 UserMoved / LayoutDirty，避免两个标记不同步。</summary>
        public void MarkLayoutChanged()
        {
            UserMoved = true;
            LayoutDirty = true;
        }

        /// <summary>关闭前是否需要询问“未保存”。</summary>
        public bool HasUnsavedLayoutChanges
        {
            get { return LayoutDirty && !SavedLayout; }
        }
    }
}
