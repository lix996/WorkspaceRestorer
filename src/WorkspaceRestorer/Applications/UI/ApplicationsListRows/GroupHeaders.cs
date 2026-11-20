using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkspaceRestorer.Applications.Browsers;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.RecycleBin.Core;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Actions;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        /// <summary>方案卡片里的一行项目。</summary>
        /// <summary>
        /// 自启动应用里哪些项目有独立窗口、可以设“启动时最小化”：
        /// 文件夹、可执行文件、批处理、快捷方式；网址和普通文档不显示，避免设置了也无效。
        /// </summary>
        private static bool CanToggleApplicationPlanItemMinimized(ApplicationPlanItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return false;
            }
            string path;
            try
            {
                path = Environment.ExpandEnvironmentVariables(item.TargetPath.Trim());
            }
            catch
            {
                return false;
            }
            if (Directory.Exists(path))
            {
                return true;
            }
            if (File.Exists(path))
            {
                string extension = Path.GetExtension(path);
                return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        /// <summary>
        /// 自启动方案里的浏览器窗口父行：只展示网页归属和数量，不伪装成一个可启动的
        /// msedge.exe 项目；真正的启动由下面的网页标签项和浏览器批量恢复负责。
        /// </summary>
        private Border BuildApplicationBrowserGroupHeader(
            ApplicationPlan plan,
            ApplicationPlanItem parent,
            IList<ApplicationPlanItem> children,
            int ordinal,
            StackPanel groupChildren,
            bool expanded,
            string expansionStateKey)
        {
            ApplicationBrowserPlanGroup group = new ApplicationBrowserPlanGroup
            {
                Key = ApplicationBrowserPlanAdapter.GroupKey(parent),
                BrowserName = parent.BrowserName,
                BrowserExecutablePath = parent.BrowserExecutablePath,
                WindowIndex = parent.BrowserWindowIndex
            };
            if (children != null)
            {
                for (int index = 0; index < children.Count; index++)
                {
                    group.Items.Add(children[index]);
                }
            }
            if (string.IsNullOrWhiteSpace(group.BrowserName))
            {
                group.BrowserName = parent.DisplayName;
            }
            return BuildApplicationPlanGroupHeader(
                plan, parent, children, true, group.DisplayName,
                BuildApplicationGroupSupportingText(parent, children), ordinal,
                groupChildren, expanded, expansionStateKey);
        }

        private Border BuildApplicationFolderGroupHeader(
            ApplicationPlan plan,
            ApplicationPlanItem parent,
            IList<ApplicationPlanItem> children,
            int ordinal,
            StackPanel groupChildren,
            bool expanded,
            string expansionStateKey)
        {
            return BuildApplicationPlanGroupHeader(
                plan,
                parent,
                children,
                false,
                parent.DisplayName ?? AppText.T(ApplicationsTextKeys.GroupFolderFallback),
                BuildApplicationGroupSupportingText(parent, children),
                ordinal,
                groupChildren,
                expanded,
                expansionStateKey);
        }

        private Border BuildApplicationBrowserGroupHeader(
            ApplicationPlan plan,
            ApplicationBrowserPlanGroup group,
            int ordinal,
            StackPanel groupChildren,
            bool expanded,
            string expansionStateKey)
        {
            return BuildApplicationPlanGroupHeader(
                plan,
                null,
                group.Items,
                true,
                group.DisplayName,
                BuildApplicationGroupSupportingText(null, group.Items),
                ordinal,
                groupChildren,
                expanded,
                expansionStateKey);
        }

        /// <summary>
        /// 浏览器/文件夹父行的副标题照「启动与恢复」：优先用窗口标题，其次用第一个子项标题，
        /// 再拼上本机时区的时间戳；不是简单写“N 个网页”。
        /// </summary>
        private static string BuildApplicationGroupSupportingText(
            ApplicationPlanItem parent,
            IList<ApplicationPlanItem> children)
        {
            string text = parent == null ? null : parent.WindowTitle;
            if (string.IsNullOrWhiteSpace(text) && children != null)
            {
                for (int index = 0; index < children.Count; index++)
                {
                    ApplicationPlanItem child = children[index];
                    if (child == null)
                    {
                        continue;
                    }
                    text = string.IsNullOrWhiteSpace(child.DisplayName)
                        ? child.TargetPath
                        : child.DisplayName;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        break;
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                text = AppText.T(parent != null && parent.IsBrowserWindow
                ? ApplicationsTextKeys.GroupBrowserWindowFallback
                : ApplicationsTextKeys.GroupFolderFallback);
            }
            string stamp = parent == null
                ? string.Empty
                : WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                    parent.StartTimeUtc, AppText.CurrentLanguageTag);
            return string.IsNullOrWhiteSpace(stamp) ? text : text.Trim() + " · " + stamp;
        }

        /// <summary>
        /// 浏览器窗口/文件夹的统一父行：勾选整组、整组最小化、删除整组。
        /// 父行不是普通启动项，不会被当成 msedge.exe 或文件夹程序单独启动。
        /// </summary>
        private Border BuildApplicationPlanGroupHeader(
            ApplicationPlan plan,
            ApplicationPlanItem parent,
            IList<ApplicationPlanItem> children,
            bool browser,
            string displayName,
            string countText,
            int ordinal,
            StackPanel groupChildren,
            bool expanded,
            string expansionStateKey)
        {
            List<ApplicationPlanItem> groupItems = new List<ApplicationPlanItem>();
            if (parent != null)
            {
                groupItems.Add(parent);
            }
            if (children != null)
            {
                for (int index = 0; index < children.Count; index++)
                {
                    if (children[index] != null)
                    {
                        groupItems.Add(children[index]);
                    }
                }
            }

            Border header = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 5, 4, 5),
                Margin = new Thickness(0, 5, 0, 1)
            };

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UiFactory.RowExpandColumnWidth) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 右侧整块动作区一个 Auto 列：≡ → 最小化 → 删除由 Cards/Actions 固定，
            // 不再让整组父行自己写三列 32。
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 序号 + 箭头直接复用「启动与恢复」的同一个控件：
            // 图标字体、字形、54×24、悬停浅蓝、按下色都在 UiFactory 里，页面不再写死。
            TextBlock expandGlyph;
            Button expandButton = UiFactory.OrdinalExpandButton(ordinal, expanded, out expandGlyph);
            Action toggleExpanded = delegate
            {
                if (groupChildren == null)
                {
                    return;
                }
                expanded = !expanded;
                groupChildren.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                UiFactory.SetExpandGlyph(expandGlyph, expanded);
                if (!string.IsNullOrWhiteSpace(expansionStateKey))
                {
                    applicationGroupExpansionStates[expansionStateKey] = expanded;
                }
            };
            expandButton.Click += delegate { toggleExpanded(); };
            Grid.SetColumn(expandButton, 0);
            content.Children.Add(expandButton);

            if (parent != null)
            {
                applicationRowDragRows.Add(new WorkspaceRestorer.UI.Cards.Drag.DragMoveRow
                {
                    Payload = parent,
                    Owner = plan,
                    Element = header,
                    Label = displayName
                });
                header.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (!IsActionControl(args.OriginalSource as DependencyObject)
                        && DragGestureSettings.MatchesMouseButton(MouseButton.Left)
                        && DragGestureSettings.MatchesPrefix())
                    {
                        ArmApplicationRowDrag(plan, parent, header, MouseButton.Left);
                    }
                };
                header.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (!IsActionControl(args.OriginalSource as DependencyObject)
                        && DragGestureSettings.MatchesMouseButton(MouseButton.Right)
                        && DragGestureSettings.MatchesPrefix())
                    {
                        ArmApplicationRowDrag(plan, parent, header, MouseButton.Right);
                    }
                };
                header.PreviewMouseMove += delegate { ContinueApplicationRowDrag(); };
            }

            CheckBox enabled = new CheckBox
            {
                IsChecked = groupItems.TrueForAll(delegate(ApplicationPlanItem item) { return item.IsEnabled; }),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            AccessibleToolTip.Attach(enabled, ApplicationsToolTips.GroupEnable);
            enabled.Checked += delegate
            {
                ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
                for (int index = 0; index < groupItems.Count; index++)
                {
                    groupItems[index].IsEnabled = true;
                }
                controller.SetApplicationPlanItemsOrder(plan, plan.Items);
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusGroupEnabledFormat, displayName);
                PushApplicationPlansUndo(AppText.F(ApplicationsTextKeys.UndoGroupEnabledFormat, displayName), before);
            };
            enabled.Unchecked += delegate
            {
                ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
                for (int index = 0; index < groupItems.Count; index++)
                {
                    groupItems[index].IsEnabled = false;
                }
                controller.SetApplicationPlanItemsOrder(plan, plan.Items);
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusGroupDisabledFormat, displayName);
                PushApplicationPlansUndo(AppText.F(ApplicationsTextKeys.UndoGroupDisabledFormat, displayName), before);
            };
            Grid.SetColumn(enabled, 1);
            content.Children.Add(enabled);

            StackPanel labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            TextBlock groupHeading = new TextBlock
            {
                Text = displayName,
                FontFamily = AppTheme.UiFont,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            labels.Children.Add(groupHeading);
            labels.Children.Add(new TextBlock
            {
                Text = countText,
                FontFamily = AppTheme.UiFont,
                FontSize = 11,
                Foreground = AppTheme.TextSecondary,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            header.MouseEnter += delegate
            {
                header.Background = AppTheme.RowHoverSurface;
                groupHeading.Foreground = AppTheme.Accent;
                groupHeading.TextDecorations = TextDecorations.Underline;
            };
            header.MouseLeave += delegate
            {
                header.Background = AppTheme.RowSurface;
                groupHeading.Foreground = AppTheme.TextPrimary;
                groupHeading.TextDecorations = null;
            };
            Grid.SetColumn(labels, 2);
            content.Children.Add(labels);

            // 整组父行与普通项目行共用同一个动作区：≡ 拖动 → 最小化 → 删除。
            ActionBar actionBar = ActionBar.Create(ActionBarKind.GroupRow);
            Button groupGrip = actionBar.Add(
                ActionSlot.Move,
                ActionButtons.DragHandle(ApplicationsToolTips.GroupGrip));
            if (parent != null)
            {
                groupGrip.PreviewMouseLeftButtonDown += delegate
                {
                    ArmApplicationRowDrag(plan, parent, header, MouseButton.Left);
                };
            }

            bool initiallyMinimized = groupItems.Exists(delegate(ApplicationPlanItem item)
            {
                return WindowMinimizeState.IsMinimized(item.WindowPlacement);
            });
            Button minimize = null;
            minimize = WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.Create(
                initiallyMinimized,
                delegate
                {
                    bool target = !groupItems.Exists(delegate(ApplicationPlanItem item)
                    {
                        return WindowMinimizeState.IsMinimized(item.WindowPlacement);
                    });
                    ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
                    for (int index = 0; index < groupItems.Count; index++)
                    {
                        ApplicationPlanItem item = groupItems[index];
                        if (item.WindowPlacement == null)
                        {
                            item.WindowPlacement = new SavedWindowPlacement { HasBounds = false };
                        }
                        WindowMinimizeState.SetMinimized(item.WindowPlacement, target);
                    }
                    controller.SetApplicationPlanItemsOrder(plan, plan.Items);
                    WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.ApplyState(minimize, target);
                    statusText.Text = target
                        ? AppText.F(ApplicationsTextKeys.StatusGroupMinimizedFormat, displayName)
                        : AppText.F(ApplicationsTextKeys.StatusGroupUnminimizedFormat, displayName);
                    PushApplicationPlansUndo(
                        AppText.F(target
                    ? ApplicationsTextKeys.UndoGroupMinimizedFormat
                    : ApplicationsTextKeys.UndoGroupUnminimizedFormat,
                    displayName),
                        before);
                });
            actionBar.Add(ActionSlot.Minimize, minimize);

            Button removeGroup = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(ToolTipContent.Named(
                    AppText.T(ApplicationsTextKeys.GroupDeleteTip),
                    AppText.T(ApplicationsTextKeys.GroupDeleteHelp))));
            removeGroup.Click += delegate
            {
                if (groupItems.Count == 0)
                {
                    return;
                }
                ApplicationPlansSnapshot before = CaptureApplicationPlansSnapshot();
                // 一次调用把父项和整组子项一起删掉；不要在循环里一项一项删，
                // 否则每删一项都会落盘、刷新，而且父项/网页的连带关系容易断。
                ApplicationPlanItem selected = parent != null ? parent : groupItems[0];
                if (!controller.RemoveApplicationPlanItem(plan, selected))
                {
                    statusText.Text = AppText.T(ApplicationsTextKeys.StatusGroupGone);
                    return;
                }
                RefreshApplicationPlanList();
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusGroupDeletedFormat, displayName, groupItems.Count);
                PushApplicationPlansUndo(AppText.F(ApplicationsTextKeys.UndoGroupDeletedFormat, displayName), before);
            };
            Grid.SetColumn(actionBar.Build(), 3);
            content.Children.Add(actionBar.Build());

            header.Child = content;
            // 整组父行也有“移动到方案…”（整组一起走）；网页子项在菜单里只给不可点说明。
            if (parent != null)
            {
                header.ContextMenu = CreateApplicationItemContextMenu(plan, parent);
            }
            return header;
        }

    }
}
