using System;
using System.Windows;
using System.Windows.Controls;

namespace WorkspaceRestorer.UI
{
    // 公共设置：内存上限 / 内存等待上限。
    // 用户 2026-10-31 要求：和 CPU 一样放在「总设置」，启动与恢复、自启动应用共用；
    // 默认 75%，超过后暂停恢复并询问；等待到上限仍高就自动暂停。
    public sealed partial class MainWindow
    {
        private TextBox memoryLimitBox;
        private TextBox memoryWaitBox;

        private void InitializeCommonMemorySettings()
        {
            memoryLimitBox = CreateNumberBox();
            AccessibleToolTip.Attach(memoryLimitBox, SettingsToolTips.MemoryLimit);
            memoryWaitBox = CreateNumberBox();
            AccessibleToolTip.Attach(memoryWaitBox, SettingsToolTips.MemoryWait);
            ParameterField.Register(this, memoryLimitBox, CommitCommonMemorySettings);
            ParameterField.Register(this, memoryWaitBox, CommitCommonMemorySettings);
        }

        /// <summary>总设置里的两行内存设置（所有启动类工作区共用）。</summary>
        private UIElement BuildCommonMemorySettingsRows()
        {
            StackPanel rows = new StackPanel();
            rows.Children.Add(BuildSettingsRow(
                SettingsToolTips.MemoryLimitRow,
                CreateNumberBoxHost(memoryLimitBox)));
            rows.Children.Add(BuildSettingsRow(
                SettingsToolTips.MemoryWaitRow,
                CreateNumberBoxHost(memoryWaitBox)));
            return rows;
        }

        private void RefreshCommonMemorySettings()
        {
            if (memoryLimitBox != null)
            {
                memoryLimitBox.Text = controller.Settings.MaximumMemoryPercent.ToString(
                    "0.#", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (memoryWaitBox != null)
            {
                memoryWaitBox.Text = controller.Settings.MemoryWaitTimeoutSeconds.ToString();
            }
        }

        private void CommitCommonMemorySettings()
        {
            double memoryLimit = ParseDecimal(memoryLimitBox.Text, controller.Settings.MaximumMemoryPercent);
            int memoryWait = ParseNumber(memoryWaitBox.Text, controller.Settings.MemoryWaitTimeoutSeconds);
            controller.UpdateMemorySettings(memoryLimit, memoryWait);
            RefreshSettings();
        }
    }
}
