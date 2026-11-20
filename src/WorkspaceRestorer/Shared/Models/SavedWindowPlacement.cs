using System;
using System.Runtime.Serialization;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// 顶层窗口的位置、大小和显示状态。
    ///
    /// 这是「启动与恢复」和「自启动应用」共用的窗口信息：
    ///   · 启动与恢复保存的是当前打开窗口的完整位置；
    ///   · 自启动应用的每个启动项也可以带一份，用来在启动后恢复最小化/最大化状态。
    ///
    /// 历史字段 HasBounds 保留作为兼容标记；坐标和 Normal/Maximized/Minimized
    /// 以及宽高一起持久化，恢复时才能把窗口放回保存时的尺寸。
    /// </summary>
    [DataContract]
    public sealed class SavedWindowPlacement
    {
        public SavedWindowPlacement()
        {
            WindowState = "Normal";
            // 【2026-10-18】旧存档没有下面两个字段 → 默认“不知道”（-1 / 0），
            // 恢复时退回“离保存坐标最近的那块屏”，行为与以前一致。
            MonitorIndex = -1;
        }

        [DataMember(Order = 1)]
        public bool HasBounds { get; set; }

        [DataMember(Order = 2)]
        public int Left { get; set; }

        [DataMember(Order = 3)]
        public int Top { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string WindowState { get; set; }

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public int Width { get; set; }

        [DataMember(Order = 6, EmitDefaultValue = false)]
        public int Height { get; set; }

        /// <summary>
        /// 保存时这个窗口落在**第几块屏**（显示器按“左上角从左到右、再从上到下”排序，0 起）。
        /// -1 ＝ 旧存档没有这个信息（恢复时退回“离坐标最近的那块屏”）。
        /// 【2026-10-18 用户要求】“双屏、三屏、多屏的应用存放位置……还有他们相互转换”
        /// —— 不记序号就没法在“屏数不变但排列换了”时把它放回对应那块屏。
        /// </summary>
        [DataMember(Order = 7, EmitDefaultValue = false)]
        public int MonitorIndex { get; set; }

        /// <summary>
        /// 保存时一共**几块屏**。0 ＝ 旧存档没有这个信息。
        /// 【用途】现在是几块 vs 保存时几块 → 单屏变多屏放主屏；多屏变少屏放主屏并阶梯错开。
        /// </summary>
        [DataMember(Order = 8, EmitDefaultValue = false)]
        public int MonitorCount { get; set; }

        /// <summary>
        /// 用户点“最小化”之前这个窗口的状态：Normal 或 Maximized。
        /// 取消最小化时恢复它；旧方案没有这个字段时回退到 Normal。
        /// </summary>
        [DataMember(Order = 9, EmitDefaultValue = false)]
        public string WindowStateBeforeMinimize { get; set; }

        /// <summary>
        /// 用户是否在方案行里手动点过“最小化/取消最小化”。
        /// true 时“保存当前方案”只更新位置/大小，不覆盖用户明确选择的状态；
        /// false 时按当前实际窗口状态重新捕获。
        /// </summary>
        [DataMember(Order = 10, EmitDefaultValue = false)]
        public bool MinimizeChoiceExplicit { get; set; }

        /// <summary>
        /// 深拷贝。撤销快照/复制启动项时必须用副本，不能共享同一个对象：
        /// 否则用户切换最小化会把手里的“撤销前快照”一起改掉，Ctrl+Z 看起来没反应。
        /// </summary>
        public SavedWindowPlacement Clone()
        {
            return new SavedWindowPlacement
            {
                HasBounds = HasBounds,
                Left = Left,
                Top = Top,
                WindowState = WindowState,
                Width = Width,
                Height = Height,
                MonitorIndex = MonitorIndex,
                MonitorCount = MonitorCount,
                WindowStateBeforeMinimize = WindowStateBeforeMinimize,
                MinimizeChoiceExplicit = MinimizeChoiceExplicit
            };
        }
    }
}
