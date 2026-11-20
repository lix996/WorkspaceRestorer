using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Core.Cards.PlanFolders
{
    /// <summary>
    /// 方案文件夹的数据版本与旧数据迁移。
    ///
    /// 旧文件没有 ParentId/IsFolder：读出来就是根级普通方案；
    /// 新文件版本号升到 2，但 DataContract 是向前兼容的，旧版程序仍能读，
    /// 只是它不认识嵌套关系。这里不做降级承诺，只保证升级不丢方案。
    /// </summary>
    public static class PlanFolderMigration
    {
        public const int CurrentSchemaVersion = 2;

        public static int NormalizeProfiles<T>(IList<T> nodes) where T : class, IPlanFolderNode
        {
            return PlanFolderTree.Normalize(nodes);
        }
    }
}
