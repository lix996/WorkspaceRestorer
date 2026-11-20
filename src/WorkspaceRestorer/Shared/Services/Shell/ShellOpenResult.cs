using System;

namespace WorkspaceRestorer.Services
{
    /// <summary>Shell 打开路径的结果。</summary>
    public enum ShellOpenStatus
    {
        /// <summary>真的交给了 Windows Shell。</summary>
        Opened,

        /// <summary>测试/隔离模式：没有真的打开，但调用方可以按“已处理”继续。</summary>
        Suppressed,

        /// <summary>没有打开：路径为空、目标不存在或系统报错。</summary>
        Failed
    }

    /// <summary>
    /// 【公共件】ShellOpener 的返回值。
    ///
    /// 为什么单独一个类型：调用方既要区分“真的打开”和“测试模式抑制”，
    /// 又要把系统错误原样显示到状态栏；只返回 bool 会把这三种情况混在一起。
    /// </summary>
    public sealed class ShellOpenResult
    {
        private ShellOpenResult(ShellOpenStatus status, string error)
        {
            Status = status;
            Error = error ?? string.Empty;
        }

        public ShellOpenStatus Status { get; private set; }

        public string Error { get; private set; }

        /// <summary>打开成功，或被测试模式安全抑制，都算“已处理”。</summary>
        public bool Success
        {
            get { return Status == ShellOpenStatus.Opened || Status == ShellOpenStatus.Suppressed; }
        }

        public bool WasSuppressed
        {
            get { return Status == ShellOpenStatus.Suppressed; }
        }

        public static ShellOpenResult Opened()
        {
            return new ShellOpenResult(ShellOpenStatus.Opened, null);
        }

        public static ShellOpenResult Suppressed()
        {
            return new ShellOpenResult(ShellOpenStatus.Suppressed, null);
        }

        public static ShellOpenResult Failed(string error)
        {
            return new ShellOpenResult(ShellOpenStatus.Failed, error);
        }
    }
}
