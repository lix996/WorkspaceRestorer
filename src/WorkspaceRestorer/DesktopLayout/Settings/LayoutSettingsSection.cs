using System;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Models;

namespace WorkspaceRestorer.UI
{
    // 设置页的「图标位置保存」分区（2026-10-23：不再依赖启动与恢复的自动恢复开关）。
    public sealed partial class MainWindow
    {
        private readonly CheckBox layoutAutoRestoreCheckBox = new CheckBox();
        private TextBox layoutInitialDelayBox;
        private NamingSettingField layoutNewNameField;
        private bool suppressLayoutAutoRestoreChange;

        private UIElement BuildLayoutSettingsSection()
        {
            PrepareSettingsCheckBox(layoutAutoRestoreCheckBox);
            AccessibleToolTip.Attach(layoutAutoRestoreCheckBox, SettingsToolTips.LayoutAutoRestore);
            if (layoutInitialDelayBox == null)
            {
                layoutInitialDelayBox = CreateNumberBox();
                AccessibleToolTip.Attach(layoutInitialDelayBox, SettingsToolTips.LayoutInitialDelay);
                ParameterField.Register(this, layoutInitialDelayBox, CommitLayoutTiming);
            }

            StackPanel section = new StackPanel();
            section.Children.Add(BuildSettingsSectionHeaderKey(SettingsTextKeys.NavLayout));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.LayoutAutoRestoreRow,
                layoutAutoRestoreCheckBox));
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.LayoutInitialDelayRow,
                CreateNumberBoxHost(layoutInitialDelayBox)));
            // 用户要求（2026-11-18）：新命名放到“重置本节”上面。
            if (layoutNewNameField == null)
            {
                layoutNewNameField = CreateNamingSettingBox(
                    SettingsToolTips.LayoutNewName,
                    CommitLayoutNewName);
            }
            section.Children.Add(BuildSettingsRow(
                SettingsToolTips.LayoutNewNameRow,
                layoutNewNameField.Host));
            section.Children.Add(BuildSectionResetRow(FindSettingsSection("layout")));

            layoutAutoRestoreCheckBox.Checked += delegate
            {
                if (!suppressLayoutAutoRestoreChange)
                {
                    controller.SetLayoutAutoRestore(true);
                }
            };
            layoutAutoRestoreCheckBox.Unchecked += delegate
            {
                if (!suppressLayoutAutoRestoreChange)
                {
                    controller.SetLayoutAutoRestore(false);
                }
            };
            return section;
        }

        private void RefreshLayoutSettings()
        {
            suppressLayoutAutoRestoreChange = true;
            try
            {
                if (layoutAutoRestoreCheckBox != null)
                {
                    layoutAutoRestoreCheckBox.IsChecked = controller.Settings.LayoutAutoRestore;
                }
                if (layoutInitialDelayBox != null)
                {
                    layoutInitialDelayBox.Text = controller.Settings.LayoutInitialDelaySeconds.ToString();
                }
                RefreshLayoutNamingRow();
            }
            finally
            {
                suppressLayoutAutoRestoreChange = false;
            }
        }

        /// <summary>把“新图标布局名”输入框刷成当前值（没自定义时显示默认名）。</summary>
        private void RefreshLayoutNamingRow()
        {
            if (layoutNewNameField == null)
            {
                return;
            }
            layoutNewNameField.Editor.Text = GetNewLayoutName();
        }

        private void CommitLayoutNewName()
        {
            if (layoutNewNameField == null)
            {
                return;
            }
            // 和其他操作一样进全局撤销栈：值没变就不记。
            string newValue = NormalizeNamingSetting(
                layoutNewNameField.Editor,
                AppText.T(DesktopLayoutTextKeys.LayoutFallback));
            string oldValue = controller.Settings.NewLayoutName;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                controller.UpdateNewLayoutName(newValue);
                PushUndo(
                    AppText.T(SettingsToolTips.LayoutNewNameRow.TitleKey),
                    delegate
                    {
                        controller.UpdateNewLayoutName(oldValue);
                        RefreshLayoutNamingRow();
                    },
                    delegate
                    {
                        controller.UpdateNewLayoutName(newValue);
                        RefreshLayoutNamingRow();
                    });
            }
            RefreshLayoutNamingRow();
        }

        /// <summary>保存新图标布局时用的名字：设置里自定义过就用它，否则用默认名「图标布局」。</summary>
        private string GetNewLayoutName()
        {
            string value = controller.Settings.NewLayoutName;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
            return AppText.T(DesktopLayoutTextKeys.LayoutFallback);
        }

        private void CommitLayoutTiming()
        {
            int seconds = ParseNumber(layoutInitialDelayBox.Text,
                controller.Settings.LayoutInitialDelaySeconds,
                ApplicationController.ControllerSettings.InitialDelayMaximumSeconds);
            controller.UpdateLayoutTiming(seconds);
            RefreshSettings();
        }
    }
}
