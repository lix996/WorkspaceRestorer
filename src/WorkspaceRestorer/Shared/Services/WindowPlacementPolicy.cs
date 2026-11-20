using System;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.Services
{
    internal enum WindowPlacementPolicyAction
    {
        Keep,
        Clamp,
        Skip
    }

    internal sealed class WindowPlacementPolicyDecision
    {
        internal WindowPlacementPolicyAction Action { get; set; }
        internal SavedWindowPlacement Placement { get; set; }
        internal string Reason { get; set; }
    }

    /// <summary>
    /// 窗口恢复策略的抽象口。
    ///
    /// 具体规则（隐藏窗口过滤、恢复前体检、恢复后巡检）属于「启动与恢复」，
    /// 由 `StartupRestore/Windows/` 实现；本接口只放在 Shared，让
    /// `ProcessLauncher` / `WindowPlacementService` 能调用策略，而**不反向依赖**
    /// 具体工作区。这个方向不能反过来：Shared 不能 using StartupRestore。
    /// </summary>
    internal interface IWindowPlacementPolicy
    {
        WindowPlacementPolicyDecision Inspect(SavedWindowPlacement placement);

        void OnWindowPlaced(
            IntPtr windowHandle,
            SavedWindowPlacement placement,
            string label);

        void Reset();
    }

    /// <summary>
    /// 当前窗口恢复策略。由 StartupRestore 的 `SessionRestoreService` 构造时注册；
    /// 没注册时使用空策略，保证单独测试/手动启动不依赖界面初始化顺序。
    /// </summary>
    internal static class WindowPlacementPolicy
    {
        private static IWindowPlacementPolicy current = new NullWindowPlacementPolicy();

        internal static IWindowPlacementPolicy Current
        {
            get { return current; }
            set { current = value ?? new NullWindowPlacementPolicy(); }
        }

        private sealed class NullWindowPlacementPolicy : IWindowPlacementPolicy
        {
            public WindowPlacementPolicyDecision Inspect(SavedWindowPlacement placement)
            {
                return new WindowPlacementPolicyDecision
                {
                    Action = WindowPlacementPolicyAction.Keep,
                    Placement = placement
                };
            }

            public void OnWindowPlaced(
                IntPtr windowHandle,
                SavedWindowPlacement placement,
                string label)
            {
            }

            public void Reset()
            {
            }
        }
    }
}
