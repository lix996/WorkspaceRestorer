using System;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    /// <summary>
    /// 「启动与恢复」的窗口恢复策略实现。
    ///
    /// 这里只做三件事的接线，具体判断在：
    ///   · WindowVisibility：隐藏窗口/可见面积；
    ///   · WindowPlacementSanitizer：恢复前体检；
    ///   · WindowRestoreAudit：恢复后巡检。
    ///
    /// 因为「自启动应用」的恢复走同一个 SessionRestoreService，所以它自动获得
    /// 同一套策略，不需要在 Applications 里再复制一遍窗口代码。
    /// </summary>
    internal sealed class StartupRestoreWindowPlacementPolicy : IWindowPlacementPolicy
    {
        public WindowPlacementPolicyDecision Inspect(SavedWindowPlacement placement)
        {
            WindowPlacementSanitizeResult result = WindowPlacementSanitizer.Sanitize(placement);
            WindowPlacementPolicyAction action;
            switch (result.Action)
            {
                case WindowPlacementSanitizeAction.Skip:
                    action = WindowPlacementPolicyAction.Skip;
                    break;
                case WindowPlacementSanitizeAction.Clamp:
                    action = WindowPlacementPolicyAction.Clamp;
                    break;
                default:
                    action = WindowPlacementPolicyAction.Keep;
                    break;
            }
            return new WindowPlacementPolicyDecision
            {
                Action = action,
                Placement = result.Placement,
                Reason = result.Reason
            };
        }

        public void OnWindowPlaced(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            string label)
        {
            WindowRestoreAudit.Schedule(windowHandle, placement, label);
        }

        public void Reset()
        {
            WindowRestoreAudit.Reset();
        }
    }
}
