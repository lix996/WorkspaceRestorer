using System;
using System.Windows;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI
{
    // 公共设置：CPU 上限 / CPU 等待上限。
    // 2026-10-23 用户要求：从「启动与恢复」移到总设置，启动与恢复、自启动应用（以及以后新增的启动类功能）共用；
    // 控件创建、回填、提交都放公共 Settings 文件夹，后续新增工作区不用再复制。
    public sealed partial class MainWindow
    {
        private TextBox cpuLimitBox;
        private TextBox loadWaitBox;

        private void InitializeCommonCpuSettings()
        {
            cpuLimitBox = CreateNumberBox();
            AccessibleToolTip.Attach(cpuLimitBox, SettingsToolTips.CpuLimit);
            loadWaitBox = CreateNumberBox();
            AccessibleToolTip.Attach(loadWaitBox, SettingsToolTips.CpuWait);
            ParameterField.Register(this, cpuLimitBox, CommitCommonCpuSettings);
            ParameterField.Register(this, loadWaitBox, CommitCommonCpuSettings);
        }

        /// <summary>总设置里的两行 CPU 设置（所有启动类工作区共用）。</summary>
        private UIElement BuildCommonCpuSettingsRows()
        {
            StackPanel rows = new StackPanel();
            rows.Children.Add(BuildSettingsRow(
                SettingsToolTips.CpuLimitRow,
                CreateNumberBoxHost(cpuLimitBox)));
            rows.Children.Add(BuildSettingsRow(
                SettingsToolTips.CpuWaitRow,
                CreateNumberBoxHost(loadWaitBox)));
            return rows;
        }

        private void RefreshCommonCpuSettings()
        {
            if (cpuLimitBox != null)
            {
                cpuLimitBox.Text = controller.Settings.MaximumCpuPercent.ToString(
                    "0.#", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (loadWaitBox != null)
            {
                loadWaitBox.Text = controller.Settings.LoadWaitTimeoutSeconds.ToString();
            }
        }

        private void CommitCommonCpuSettings()
        {
            double cpuLimit = ParseDecimal(cpuLimitBox.Text, controller.Settings.MaximumCpuPercent);
            int loadWait = ParseNumber(loadWaitBox.Text, controller.Settings.LoadWaitTimeoutSeconds);
            controller.UpdateLoadSettings(cpuLimit, loadWait);
            RefreshSettings();
        }
    }
}
