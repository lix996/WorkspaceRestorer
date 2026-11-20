using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.UI
{
    // 设置页的「启动与恢复」分区（2026-10-19 用户要求：各工作区的设置代码放进该工作区自己的 Settings 文件夹）。
    // 页面骨架、公共设置行/分组标题/重置按钮仍在 Shared/UI/Settings/SettingsPage.cs。
    public sealed partial class MainWindow
    {
        private NamingSettingField startupNewNameField;

        private UIElement BuildStartupSettingsSection()
        {
            PrepareSettingsCheckBox(autoRestoreCheckBox);
            PrepareSettingsCheckBox(loadGateCheckBox);
            PrepareSettingsCheckBox(scanTerminalsCheckBox);
            StackPanel section = new StackPanel();
            section.Children.Add(BuildSettingsSectionHeaderKey(SettingsTextKeys.NavStartup));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupAutoRestoreRow,
                autoRestoreCheckBox));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ScanTerminalsRow,
                scanTerminalsCheckBox));
            // 用户要求（2026-10-10 18:32）：“总设置里的回收站天数……我只要保留回收站里的天数设置，
            // 总设置里的不需要，对应的代码也要删除，防止出现僵尸代码。”
            // → 这一行整条删掉；「回收站」页面里那一份是唯一入口。
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.LoadGateRow,
                loadGateCheckBox));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupInitialDelayRow,
                CreateNumberBoxHost(initialDelayBox)));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupItemDelayRow,
                CreateNumberBoxHost(itemDelayBox)));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupWebDelayRow,
                CreateNumberBoxHost(webPageDelayBox)));
            // 用户要求（2026-11-18）：新命名放到“重置本节”上面。
            if (startupNewNameField == null)
            {
                startupNewNameField = CreateNamingSettingBox(
                    SettingsToolTips.ProfileName,
                    CommitStartupNewName);
            }
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.StartupNewNameRow,
                startupNewNameField.Host));
            section.Children.Add(BuildSectionResetRow(FindSettingsSection("startup")));
            return section;
        }

        /// <summary>把“新工作方案名”输入框刷成当前值（没自定义时显示默认名）。</summary>
        private void RefreshStartupNamingRow()
        {
            if (startupNewNameField == null)
            {
                return;
            }
            startupNewNameField.Editor.Text = GetNewStartupProfileName();
        }

        private void CommitStartupNewName()
        {
            if (startupNewNameField == null)
            {
                return;
            }
            // 和其他操作一样进全局撤销栈：值没变就不记。
            string newValue = NormalizeNamingSetting(startupNewNameField.Editor, DefaultProfileName);
            string oldValue = controller.Settings.NewStartupProfileName;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                controller.UpdateNewStartupProfileName(newValue);
                PushUndo(
                    AppText.T(SettingsToolTips.StartupNewNameRow.TitleKey),
                    delegate
                    {
                        controller.UpdateNewStartupProfileName(oldValue);
                        RefreshStartupNamingRow();
                    },
                    delegate
                    {
                        controller.UpdateNewStartupProfileName(newValue);
                        RefreshStartupNamingRow();
                    });
            }
            RefreshStartupNamingRow();
        }

        /// <summary>新建工作方案时用的名字：设置里自定义过就用它，否则用默认名「工作方案」。</summary>
        private string GetNewStartupProfileName()
        {
            string value = controller.Settings.NewStartupProfileName;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
            return DefaultProfileName;
        }
    }
}
