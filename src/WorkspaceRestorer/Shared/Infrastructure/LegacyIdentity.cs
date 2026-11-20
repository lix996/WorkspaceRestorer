namespace WorkspaceRestorer.Infrastructure
{
    /// <summary>
    /// 旧版技术身份，只用于一次性迁移和回滚。
    /// 正常业务代码不要引用这里；新身份统一走 ProductIdentity。
    /// </summary>
    internal static class LegacyIdentity
    {
        internal const string TechnicalName = "ComputerStartupKeeper";
        internal const string AppUserModelId = TechnicalName + ".Desktop";
        internal const string StartupRunValueName = TechnicalName;
        internal const string StartMenuShortcutFileName = "电脑启动保存器.lnk";
    }
}
