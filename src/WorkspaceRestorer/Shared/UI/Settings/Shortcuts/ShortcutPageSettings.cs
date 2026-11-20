namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 快捷键分页的纯规则：可选的每页条数、默认值和非法值回退。
    ///
    /// 【2026-11-19】分页条和 settings.json 都要知道这几个值；放在快捷键目录，
    /// 由分页条和 ControllerSettings 共同引用，避免两处各写一套 5/10/25/50。
    /// 这里不碰 WPF，也不读写磁盘。
    /// </summary>
    internal static class ShortcutPageSettings
    {
        internal const int DefaultPageSize = 10;

        internal static readonly int[] PageSizeOptions = new int[] { 5, 10, 25, 50 };

        internal static int NormalizePageSize(int pageSize)
        {
            for (int index = 0; index < PageSizeOptions.Length; index++)
            {
                if (PageSizeOptions[index] == pageSize)
                {
                    return pageSize;
                }
            }
            return DefaultPageSize;
        }
    }
}
