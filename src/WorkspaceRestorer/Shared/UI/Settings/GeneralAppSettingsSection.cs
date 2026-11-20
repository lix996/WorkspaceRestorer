using System;
using System.Windows;
using System.Windows.Controls;
using WorkspaceRestorer.Core.Localization;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 设置页的“通用”分区：语言、主题和重置本节。
    /// 语言项从 LanguageCatalog 生成；语言和主题都立即生效，不要求重启。
    /// 关于已经拆成独立分区，见 AboutSection.cs。
    /// </summary>
    public sealed partial class MainWindow
    {
        private ComboBox languageBox;
        private ComboBox themeBox;

        private UIElement BuildGeneralAppSettingsSection()
        {
            StackPanel section = new StackPanel();
            // 【2026-11-15 用户要求】分区标题下一句话 + 问号，不再单独占一行说明。
            section.Children.Add(BuildSettingsSectionHeaderKey(
                SettingsTextKeys.GeneralTitle,
                SettingsTextKeys.GeneralSubtitle,
                SettingsTextKeys.GeneralSubtitle));
            section.Children.Add(BuildSettingsRow(
                GeneralSettingsToolTips.LanguageRow,
                BuildLanguageControl()));
            section.Children.Add(BuildSettingsRow(
                GeneralSettingsToolTips.ThemeRow,
                BuildThemeControl()));
            section.Children.Add(BuildSectionResetRow(FindSettingsSection("general-app")));
            return section;
        }

        private UIElement BuildLanguageControl()
        {
            languageBox = new ComboBox
            {
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center
            };
            LocalizedText.AccessibleName(languageBox, SettingsTextKeys.LanguageLabel);

            bool selected = false;
            foreach (LanguageDefinition definition in LanguageCatalog.Ready())
            {
                ComboBoxItem item = new ComboBoxItem
                {
                    Content = definition.NativeName,
                    Tag = definition.Tag
                };
                languageBox.Items.Add(item);
                if (!selected && LanguageTags.Same(definition.Tag, controller.Settings.Language))
                {
                    languageBox.SelectedItem = item;
                    selected = true;
                }
            }
            if (!selected && languageBox.Items.Count > 0)
            {
                languageBox.SelectedIndex = 0;
            }

            languageBox.SelectionChanged += delegate
            {
                if (loadingSettings)
                {
                    return;
                }
                ComboBoxItem item = languageBox.SelectedItem as ComboBoxItem;
                if (item == null)
                {
                    return;
                }
                // 不在 ComboBox 自己的 SelectionChanged 里拆掉/重建这个 ComboBox。
                // 只记下选择，等下拉关闭、当前输入事件结束后再真正切换；
                // 连续点多次时合并成最后一次，避免排队重建多次设置页。
                QueueLanguageChange(item.Tag as string);
            };
            return languageBox;
        }

        /// <summary>
        /// 延迟到 ContextIdle 再切语言。这样设置页重建时，触发它的 ComboBox
        /// 已经处理完这次选择，不会再出现“控件在自己的事件里被移除”的重入。
        /// </summary>
        private void QueueLanguageChange(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return;
            }
            if (string.Equals(languageCode, controller.Settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            pendingLanguageCode = languageCode;
            if (languageChangeScheduled)
            {
                return;
            }
            languageChangeScheduled = true;
            try
            {
                Dispatcher.BeginInvoke(
                    new Action(ApplyPendingLanguageChange),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);
            }
            catch
            {
                languageChangeScheduled = false;
                ApplyPendingLanguageChange();
            }
        }

        private void ApplyPendingLanguageChange()
        {
            languageChangeScheduled = false;
            string languageCode = pendingLanguageCode;
            pendingLanguageCode = null;
            if (string.IsNullOrWhiteSpace(languageCode))
            {
                return;
            }
            if (string.Equals(languageCode, controller.Settings.Language, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            controller.UpdateLanguage(languageCode);
        }

        private UIElement BuildThemeControl()
        {
            themeBox = new ComboBox
            {
                Width = 240,
                VerticalAlignment = VerticalAlignment.Center
            };
            LocalizedText.AccessibleName(themeBox, SettingsTextKeys.ThemeLabel);
            ComboBoxItem lightItem = new ComboBoxItem { Tag = "light" };
            LocalizedText.Content(lightItem, SettingsTextKeys.ThemeLight);
            themeBox.Items.Add(lightItem);
            ComboBoxItem darkItem = new ComboBoxItem { Tag = "dark" };
            LocalizedText.Content(darkItem, SettingsTextKeys.ThemeDark);
            themeBox.Items.Add(darkItem);
            ComboBoxItem systemItem = new ComboBoxItem { Tag = "system" };
            LocalizedText.Content(systemItem, SettingsTextKeys.ThemeSystem);
            themeBox.Items.Add(systemItem);
            themeBox.SelectionChanged += delegate
            {
                if (loadingSettings)
                {
                    return;
                }
                ComboBoxItem selected = themeBox.SelectedItem as ComboBoxItem;
                string code = selected == null ? "light" : (selected.Tag as string ?? "light");
                controller.UpdateTheme(code);
                statusText.Text = AppText.T(SettingsTextKeys.ThemeAppliedHint);
                RefreshAppearanceSettings();
            };
            return themeBox;
        }

        private void RefreshAppearanceSettings()
        {
            if (languageBox != null)
            {
                string code = controller.Settings.Language;
                for (int index = 0; index < languageBox.Items.Count; index++)
                {
                    ComboBoxItem item = languageBox.Items[index] as ComboBoxItem;
                    if (item != null && LanguageTags.Same(item.Tag as string, code))
                    {
                        languageBox.SelectedIndex = index;
                        break;
                    }
                }
            }
            if (themeBox != null)
            {
                string code = controller.Settings.Theme;
                for (int index = 0; index < themeBox.Items.Count; index++)
                {
                    ComboBoxItem item = themeBox.Items[index] as ComboBoxItem;
                    if (item != null && string.Equals(item.Tag as string, code, StringComparison.OrdinalIgnoreCase))
                    {
                        themeBox.SelectedIndex = index;
                        break;
                    }
                }
            }
        }
    }
}
