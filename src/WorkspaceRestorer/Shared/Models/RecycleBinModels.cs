using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.RecycleBin.Core
{
    /// <summary>
    /// 一个被删除的工作方案：它先放到回收站里，过了保留天数才真正删掉。
    /// </summary>
    [DataContract]
    public sealed class RecycleBinEntry
    {
        [DataMember(Order = 1)]
        public StartupProfile Profile { get; set; }

        [DataMember(Order = 2)]
        public DateTime DeletedAtUtc { get; set; }

        /// <summary>
        /// 这一项属于哪个工作区（"启动与恢复" / "图标位置保存" / "自启动应用"…）。
        /// 回收站是全局的：以后任何工作区想支持"删除进回收站"，只要填这个类别和
        /// 下面几个显示字段，再把原始数据放进 Payload 即可，回收站代码不用改。
        /// </summary>
        [DataMember(Order = 3, EmitDefaultValue = false)]
        public string Kind { get; set; }

        [DataMember(Order = 4, EmitDefaultValue = false)]
        public string Title { get; set; }

        [DataMember(Order = 5, EmitDefaultValue = false)]
        public string Subtitle { get; set; }

        /// <summary>原始数据（JSON）。回收时由该类别自己的代码负责还原。</summary>
        [DataMember(Order = 6, EmitDefaultValue = false)]
        public string Payload { get; set; }

        /// <summary>
        /// 方案文件夹/嵌套方案整棵子树的 JSON（工作区自己的方案类型）。
        /// 旧回收站条目没有这个字段，仍按 Profile / Payload 单份恢复。
        /// </summary>
        [DataMember(Order = 7, EmitDefaultValue = false)]
        public string PlanTreePayload { get; set; }

        /// <summary>被删除子树的根方案 Id。</summary>
        [DataMember(Order = 8, EmitDefaultValue = false)]
        public string PlanTreeRootId { get; set; }

        /// <summary>删除时的父方案 Id；空 = 原来就在根目录。</summary>
        [DataMember(Order = 9, EmitDefaultValue = false)]
        public string OriginalParentId { get; set; }

        /// <summary>删除时在同类中的位置（0 开始）。</summary>
        [DataMember(Order = 10, EmitDefaultValue = false)]
        public int OriginalSiblingIndex { get; set; }

        /// <summary>给人看的原路径，例如“大方案 A / 小方案 A1”。</summary>
        [DataMember(Order = 11, EmitDefaultValue = false)]
        public string OriginalPath { get; set; }

        /// <summary>true = 这条是“整棵方案子树”的新格式。</summary>
        [DataMember(Order = 12, EmitDefaultValue = false)]
        public bool IsPlanTree { get; set; }

        /// <summary>显示用：还剩几天被彻底删除；-1 表示永久保留（保留天数为 0）。</summary>
        [IgnoreDataMember]
        public int RemainingDays { get; set; }
    }

    /// <summary>回收站文件的内容：保留天数 + 已删除的方案。</summary>
    [DataContract]
    public sealed class RecycleBinCatalog
    {
        public RecycleBinCatalog()
        {
            RetentionDays = 15;
            Entries = new List<RecycleBinEntry>();
        }

        /// <summary>删除后保留多少天；到期自动彻底删除。0 表示永久保留。</summary>
        [DataMember(Order = 1)]
        public int RetentionDays { get; set; }

        [DataMember(Order = 2)]
        public List<RecycleBinEntry> Entries { get; set; }
    }
}
