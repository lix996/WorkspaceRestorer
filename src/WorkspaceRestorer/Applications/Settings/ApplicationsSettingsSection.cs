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
    // 设置页的「自启动应用」分区（2026-10-19 用户要求：各工作区的设置代码放进该工作区自己的 Settings 文件夹）。
    // 页面骨架、公共设置行/分组标题/重置按钮仍在 Shared/UI/Settings/SettingsPage.cs。
    public sealed partial class MainWindow
    {
        private NamingSettingField applicationsNewNameField;

        /// <summary>「自启动应用」分区的页面内容。</summary>
        private UIElement BuildApplicationsSettingsSection()
        {
            PrepareSettingsCheckBox(applicationsAutoRestoreCheckBox);
            // 用户 2026-10-19：这一项改名为“启动程序后自动恢复工作方案”（和「启动与恢复」同名，
            // 但两边的值是独立的：这边管自启动方案，那边管工作方案）。
            AccessibleToolTip.Attach(applicationsAutoRestoreCheckBox, SettingsToolTips.ApplicationsAutoRestore);

            StackPanel section = new StackPanel();
            section.Children.Add(BuildSettingsSectionHeaderKey(SettingsTextKeys.NavApplications));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsAutoRestoreRow,
                applicationsAutoRestoreCheckBox));
            // 用户 2026-10-19：“自启动应用也要加上这个（高负载等待 / 启动前等待 / 项目间隔 / 网页间隔），
            // 代码写到自启动应用的文件夹上。” → 这一套值是自启动应用**自己**的，和「启动与恢复」互不影响。
            PrepareSettingsCheckBox(applicationsWaitForLoadCheckBox);
            AccessibleToolTip.Attach(applicationsWaitForLoadCheckBox, SettingsToolTips.ApplicationsWaitForLoad);
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsWaitForLoadRow,
                applicationsWaitForLoadCheckBox));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsInitialDelayRow,
                CreateNumberBoxHost(applicationsInitialDelayBox)));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsItemDelayRow,
                CreateNumberBoxHost(applicationsItemDelayBox)));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsWebDelayRow,
                CreateNumberBoxHost(applicationsWebPageDelayBox)));
            // 用户要求（2026-11-18）：新命名放到“重置本节”上面。
            if (applicationsNewNameField == null)
            {
                applicationsNewNameField = CreateNamingSettingBox(
                    SettingsToolTips.ApplicationsNewName,
                    CommitApplicationsNewName);
            }
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.ApplicationsNewNameRow,
                applicationsNewNameField.Host));
            section.Children.Add(BuildSectionResetRow(FindSettingsSection("applications")));

            applicationsAutoRestoreCheckBox.Checked += delegate
            {
                if (!suppressApplicationsAutoRestoreChange)
                {
                    controller.SetApplicationsAutoRestore(true);
                }
            };
            applicationsAutoRestoreCheckBox.Unchecked += delegate
            {
                if (!suppressApplicationsAutoRestoreChange)
                {
                    controller.SetApplicationsAutoRestore(false);
                }
            };
            applicationsWaitForLoadCheckBox.Checked += delegate
            {
                if (!suppressApplicationsWaitForLoadChange)
                {
                    controller.SetApplicationsWaitForLoad(true);
                }
            };
            applicationsWaitForLoadCheckBox.Unchecked += delegate
            {
                if (!suppressApplicationsWaitForLoadChange)
                {
                    controller.SetApplicationsWaitForLoad(false);
                }
            };
            return section;
        }

        /// <summary>把“新自启动方案名”输入框刷成当前值（没自定义时显示默认名）。</summary>
        private void RefreshApplicationsNamingRow()
        {
            if (applicationsNewNameField == null)
            {
                return;
            }
            applicationsNewNameField.Editor.Text = GetNewApplicationPlanName();
        }

        private void CommitApplicationsNewName()
        {
            if (applicationsNewNameField == null)
            {
                return;
            }
            // 和其他操作一样进全局撤销栈：值没变就不记。
            string newValue = NormalizeNamingSetting(
                applicationsNewNameField.Editor,
                AppText.T(ApplicationsTextKeys.PlanNameFallback));
            string oldValue = controller.Settings.NewApplicationPlanName;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                controller.UpdateNewApplicationPlanName(newValue);
                PushUndo(
                    AppText.T(SettingsToolTips.ApplicationsNewNameRow.TitleKey),
                    delegate
                    {
                        controller.UpdateNewApplicationPlanName(oldValue);
                        RefreshApplicationsNamingRow();
                    },
                    delegate
                    {
                        controller.UpdateNewApplicationPlanName(newValue);
                        RefreshApplicationsNamingRow();
                    });
            }
            RefreshApplicationsNamingRow();
        }

        /// <summary>新建自启动方案时用的名字：设置里自定义过就用它，否则用默认名「自启动方案」。</summary>
        private string GetNewApplicationPlanName()
        {
            string value = controller.Settings.NewApplicationPlanName;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
            return AppText.T(ApplicationsTextKeys.PlanNameFallback);
        }
    }
}
