using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 快捷键页的操作层：
    ///   点铅笔/键帽 → 打开录键弹窗；点加号 → 新增一条；点垃圾桶 → 删除这一条；
    ///   点恢复 → 只把这条命令恢复默认；页面底部 → 重置所有快捷键。
    /// 键位校验、冲突判断和落盘都不在这里复制，统一走 ShortcutCatalog / ApplicationController。
    /// </summary>
    public sealed partial class MainWindow
    {
        private void BeginEditShortcutBinding(ShortcutCatalog.Shortcut shortcut, int bindingIndex)
        {
            if (shortcut == null || !shortcut.IsEditable
                || bindingIndex < 0 || bindingIndex >= shortcut.Bindings.Count)
            {
                return;
            }
            ShortcutBinding current = shortcut.Bindings[bindingIndex];
            ShortcutCaptureDialog dialog = new ShortcutCaptureDialog(
                this,
                shortcut.Id,
                shortcut.Title,
                bindingIndex,
                false,
                current == null ? Key.None : current.Key,
                current == null ? ModifierKeys.None : current.Modifiers);
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            List<ShortcutBinding> next = CloneShortcutBindings(shortcut.Bindings);
            next[bindingIndex] = new ShortcutBinding(dialog.SelectedKey, dialog.SelectedModifiers);
            ApplyShortcutBindings(shortcut, next, SettingsTextKeys.ShortcutSaved);
        }

        private void BeginAddShortcutBinding(ShortcutCatalog.Shortcut shortcut)
        {
            if (shortcut == null || !shortcut.IsEditable)
            {
                return;
            }
            ShortcutCaptureDialog dialog = new ShortcutCaptureDialog(
                this,
                shortcut.Id,
                shortcut.Title,
                -1,
                true,
                Key.None,
                ModifierKeys.None);
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            List<ShortcutBinding> next = CloneShortcutBindings(shortcut.Bindings);
            next.Add(new ShortcutBinding(dialog.SelectedKey, dialog.SelectedModifiers));
            ApplyShortcutBindings(shortcut, next, SettingsTextKeys.ShortcutSaved);
        }

        private void DeleteShortcutBinding(ShortcutCatalog.Shortcut shortcut, int bindingIndex)
        {
            if (shortcut == null || !shortcut.IsEditable
                || bindingIndex < 0 || bindingIndex >= shortcut.Bindings.Count)
            {
                return;
            }
            List<ShortcutBinding> next = CloneShortcutBindings(shortcut.Bindings);
            next.RemoveAt(bindingIndex);
            ApplyShortcutBindings(shortcut, next, SettingsTextKeys.ShortcutDeleted);
        }

        private void ResetShortcutCommand(ShortcutCatalog.Shortcut shortcut)
        {
            if (shortcut == null || !shortcut.IsEditable)
            {
                return;
            }
            string error;
            if (!controller.TryResetShortcutCommand(shortcut.Id, out error))
            {
                ShowShortcutPageError(string.IsNullOrWhiteSpace(error)
                    ? AppText.T(SettingsTextKeys.ShortcutSaveFailed)
                    : error);
                return;
            }
            RefreshShortcutSettingsSection();
            if (statusText != null)
            {
                statusText.Text = AppText.T(SettingsTextKeys.ShortcutRestored);
            }
        }

        /// <summary>
        /// “拖动”这一行：先按组合键，再点左键/右键拖动；
        /// 没点鼠标键按钮时传 null，调用方保留 settings 里原来的鼠标键。
        /// </summary>
        private void BeginEditDragGesture()
        {
            DragGestureDialog dialog = new DragGestureDialog(
                this,
                DragGestureSettings.PrefixKey,
                DragGestureSettings.PrefixModifiers,
                DragGestureSettings.MouseButton);
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            MouseButton button = dialog.SelectedMouseButton.HasValue
                ? dialog.SelectedMouseButton.Value
                : DragGestureSettings.MouseButton;
            string error;
            if (!controller.TryUpdateDragGesture(
                dialog.SelectedKey, dialog.SelectedModifiers, button, out error))
            {
                ShowShortcutPageError(string.IsNullOrWhiteSpace(error)
                    ? AppText.T(SettingsTextKeys.DragGestureErrorSave)
                    : error);
                return;
            }
            RefreshShortcutSettingsSection();
            if (statusText != null)
            {
                statusText.Text = AppText.T(SettingsTextKeys.DragGestureSaved);
            }
        }

        private void ResetDragGesture()
        {
            string error;
            if (!controller.TryUpdateDragGesture(
                Key.None, ModifierKeys.Shift, MouseButton.Left, out error))
            {
                ShowShortcutPageError(string.IsNullOrWhiteSpace(error)
                    ? AppText.T(SettingsTextKeys.DragGestureErrorSave)
                    : error);
                return;
            }
            RefreshShortcutSettingsSection();
            if (statusText != null)
            {
                statusText.Text = AppText.T(SettingsTextKeys.DragGestureRestored);
            }
        }

        private void ResetAllShortcuts()
        {
            if (!ConfirmDialog.Ask(
                this,
                AppText.T(SettingsTextKeys.ShortcutResetAllConfirmTitle),
                AppText.T(SettingsTextKeys.ShortcutResetAllConfirmMessage)))
            {
                return;
            }
            controller.ResetShortcutBindings();
            RefreshShortcutSettingsSection();
        }

        private void ApplyShortcutBindings(
            ShortcutCatalog.Shortcut shortcut,
            List<ShortcutBinding> next,
            string successKey)
        {
            if (shortcut == null || next == null)
            {
                return;
            }
            string error;
            if (!controller.TryUpdateShortcutBindings(shortcut.Id, next, out error))
            {
                ShowShortcutPageError(string.IsNullOrWhiteSpace(error)
                    ? AppText.T(SettingsTextKeys.ShortcutSaveFailed)
                    : error);
                return;
            }
            RefreshShortcutSettingsSection();
            if (statusText != null)
            {
                statusText.Text = AppText.T(successKey);
            }
        }

        private static List<ShortcutBinding> CloneShortcutBindings(IList<ShortcutBinding> bindings)
        {
            List<ShortcutBinding> copy = new List<ShortcutBinding>();
            if (bindings == null)
            {
                return copy;
            }
            for (int index = 0; index < bindings.Count; index++)
            {
                if (bindings[index] != null)
                {
                    copy.Add(bindings[index].Clone());
                }
            }
            return copy;
        }
    }
}
