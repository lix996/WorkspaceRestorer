using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    public interface ISessionCaptureService
    {
        SessionSnapshot Capture();

        SessionSnapshot Capture(SessionCaptureOptions options);
    }

    public interface ISessionRestoreService
    {
        Task<RestoreResult> RestoreAsync(
            SessionSnapshot snapshot,
            RestoreOptions options,
            IProgress<RestoreProgress> progress,
            CancellationToken cancellationToken);
    }

    public interface IProcessLauncher
    {
        LaunchResult Launch(SessionItem item);

        LaunchResult LaunchPath(string path);
    }

    /// <summary>
    /// 可选扩展：允许带窗口状态启动路径（自启动应用的项目也用它恢复最小化）。
    /// 测试用的精简 launcher 不实现这个扩展，仍走原来的 LaunchPath(path)。
    /// </summary>
    internal interface IWindowPlacementProcessLauncher
    {
        LaunchResult LaunchPath(string path, SavedWindowPlacement placement, string windowTitle);
    }

    public interface ISystemLoadMonitor
    {
        Task<SystemLoadSnapshot> ReadAsync(TimeSpan sampleInterval, CancellationToken cancellationToken);
    }

    public interface IDesktopApplicationService
    {
        IList<DesktopEntry> Scan();

        LaunchResult Launch(DesktopEntry entry);
    }
}
