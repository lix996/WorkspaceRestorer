using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    /// <summary>恢复完成提示的语气；界面只按这个值选颜色，不再自己判断。</summary>
    internal enum RestoreNoticeKind
    {
        Success,
        Warning,
        Error,
        Neutral
    }

    /// <summary>
    /// 一份已经排好版的恢复完成文案。
    ///
    /// Text 给横幅/托盘，Detail 给底部状态栏和悬浮提示；Persistent 只在
    /// CPU/内存超时自动停止时为 true。
    /// </summary>
    internal sealed class RestoreNoticeText
    {
        internal string Summary { get; set; }

        internal string Detail { get; set; }

        internal bool Persistent { get; set; }

        internal RestoreNoticeKind Kind { get; set; }
    }
}
