using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.Core.Cards.PlanFolders;
using WorkspaceRestorer.Services;
using WorkspaceRestorer.UI.Cards.Actions;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Surface;
using WorkspaceRestorer.UI.Cards.Interaction;
using WorkspaceRestorer.UI.Cards.Folders;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 方案卡片与项目行的构建、网页/文件夹子项显示（从 StartupWorkspacePage.cs 纯搬迁）。
    /// </summary>
    public sealed partial class MainWindow
    {
        private Border BuildProfileRow(StartupProfile profile)
        {
            return BuildProfileRow(profile, 0);
        }

        private Border BuildProfileRow(StartupProfile profile, int depth)
        {
            bool active = controller.ActiveProfile != null
                && string.Equals(controller.ActiveProfile.Id, profile.Id, StringComparison.OrdinalIgnoreCase);
            bool expanded;
            if (string.IsNullOrWhiteSpace(profile.Id) || !profileExpansionStates.TryGetValue(profile.Id, out expanded))
            {
                // 用户要求（2026-10-08）：**默认收起**，不再"因为它是当前方案就自动展开"。
                // 复制方案、保存当前方案、重启程序都保持收起；只有用户自己点过展开/收起，
                // 才按用户的来（用 profileExpansionStates 记住）。
                expanded = false;
                if (!string.IsNullOrWhiteSpace(profile.Id))
                {
                    profileExpansionStates[profile.Id] = expanded;
                }
            }
            StackPanel container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            Border headerBorder = WorkspaceRestorer.UI.Cards.Surface.CardSurface.Create(active, true);
            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel expandedHost = new StackPanel
            {
                Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
            };
            StackPanel itemHost = new StackPanel
            {
                Margin = new Thickness(42, 4, 0, 0)
            };
            List<StartupProfile> childProfiles = new List<StartupProfile>(controller.ChildProfiles(profile));
            // 【性能】收起的卡片只建卡片头。子方案和启动项行都在第一次展开时才建，
            // 否则“很多方案、每个方案几十项”时，即使全部收起也要造几千个控件。
            bool contentBuilt = false;
            Action ensureExpandedContent = null;
            ensureExpandedContent = delegate
            {
                if (contentBuilt)
                {
                    return;
                }
                contentBuilt = true;
                if (childProfiles.Count > 0)
                {
                    StackPanel childHost;
                    Grid childIndent = PlanFolderPresenter.CreateChildHost(out childHost);
                    childIndent.Margin = new Thickness(42, 4, 0, 2);
                    for (int childIndex = 0; childIndex < childProfiles.Count; childIndex++)
                    {
                        childHost.Children.Add(BuildProfileRow(childProfiles[childIndex], depth + 1));
                    }
                    expandedHost.Children.Add(childIndent);
                }
                expandedHost.Children.Add(itemHost);
                if (profile.Items == null || profile.Items.Count == 0)
                {
                    if (childProfiles.Count == 0)
                    {
                        itemHost.Children.Add(UiFactory.SupportingText(profile.IsFolder
                            ? AppText.T(StartupRestoreTextKeys.CardFolderEmptyHint)
                            : AppText.T(StartupRestoreTextKeys.CardPlanEmptyHint)));
                    }
                    return;
                }
                HashSet<string> childIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < profile.Items.Count; index++)
                {
                    SessionItem candidate = profile.Items[index];
                    if (candidate != null && HasParentInProfile(profile, candidate))
                    {
                        childIds.Add(candidate.Id);
                    }
                }
                int topOrdinal = 0;
                for (int index = 0; index < profile.Items.Count; index++)
                {
                    SessionItem item = profile.Items[index];
                    if (item != null && !childIds.Contains(item.Id))
                    {
                        topOrdinal++;
                        itemHost.Children.Add(BuildProfileItemRow(profile, item, false, topOrdinal));
                    }
                }
            };

            Button expandButton = UiFactory.IconButton(expanded ? "\uE70D" : "\uE76C", StartupRestoreToolTips.ProfileExpand);
            expandButton.Width = 30;
            expandButton.Height = 30;
            expandButton.Click += delegate
            {
                expanded = !expanded;
                if (!string.IsNullOrWhiteSpace(profile.Id))
                {
                    profileExpansionStates[profile.Id] = expanded;
                }
                if (expanded)
                {
                    ensureExpandedContent();
                }
                expandButton.Content = UiFactory.IconContent(expanded ? "\uE70D" : "\uE76C");
                expandButton.FontFamily = AppTheme.IconFont;   // 箭头也必须用图标字体
                expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            };
            Grid.SetColumn(expandButton, 0);
            header.Children.Add(expandButton);

            // 【2026-10-19 用户要求】标题用 Grid 而不是横向 StackPanel：
            // 横向 StackPanel 会把文字按“需要的宽度”排出去，窗口一窄就顶到右边按钮下面（看着像重叠）。
            // Grid 的星号列会把文字限制在可用宽度里，配合 TextTrimming 显示省略号。
            Grid title = new Grid { VerticalAlignment = VerticalAlignment.Center };
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock titleIcon = new TextBlock
            {
                Text = UiFactory.IconGlyph("\uE8B7"),
                FontFamily = AppTheme.IconFont,
                FontSize = 20,
                Foreground = AppTheme.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(titleIcon, 0);
            title.Children.Add(titleIcon);
            StackPanel titleText = new StackPanel { MinWidth = 0 };
            Grid.SetColumn(titleText, 1);
            title.Children.Add(titleText);
            TextBlock profileNameBlock = new TextBlock
            {
                Text = profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Cursor = Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };
            // 外观完全走 AppTheme 里那套统一的 TextBox 样式（圆角、内边距、最小高度、
            // 垂直居中都在样式里），这里只说"单行、要横向滚动、不换行"；
            // 尺寸在进入编辑时按标题区实际宽度算，不写死。
            TextBox profileNameEditor = new TextBox
            {
                Text = profile.Name ?? string.Empty,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 200,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                // 单行、不改字体：长名字靠"按住左键往外拖时自动跟着滚"来选到底
                // （和成熟的文本框一致，见下面的 PreviewMouseMove）。
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = false
            };

            // Keep the profile name as the accessible name (a screen reader must
            // announce it); only attach the rename hint as help text.
            AccessibleToolTip.AttachDescription(profileNameBlock, StartupRestoreToolTips.ProfileName);
            profileNameBlock.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                if (args.ClickCount == 2)
                {
                    CancelPendingProfileSelect();
                    return;
                }
                if (suppressProfileSelectOnClickUp)
                {
                    suppressProfileSelectOnClickUp = false;
                    return;
                }
                ScheduleProfileSelect(profile);
            };
            // 编辑器要有自己的标准文本菜单，否则右键弹出来的是方案菜单。
            // 【2026-10-19】文本框标准菜单统一由公共件造（剪切/复制/粘贴/全选）。
            profileNameEditor.ContextMenu = Shared.ContextMenus.CreateTextMenu(profileNameEditor);
            // 成熟文本框的行为：按住左键拖到编辑框边缘时，内容自动跟着横向滚动，
            // 这样被挡住的部分也能一路选中。WPF 的 TextBox 默认不做这件事，
            // 这里取模板内的 PART_ContentHost（标准滚动宿主）自己滚。
            profileNameEditor.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (args.LeftButton != MouseButtonState.Pressed)
                {
                    return;
                }
                TextBox box = sender as TextBox;
                if (box == null || box.Template == null)
                {
                    return;
                }
                ScrollViewer host = box.Template.FindName("PART_ContentHost", box) as ScrollViewer;

                if (host == null || host.ScrollableWidth <= 0.0)
                {
                    return;
                }
                Point point = args.GetPosition(box);
                const double edge = 16.0;
                const double step = 20.0;
                if (point.X > box.ActualWidth - edge)
                {
                    host.ScrollToHorizontalOffset(Math.Min(host.ScrollableWidth, host.HorizontalOffset + step));
                }
                else if (point.X < edge)
                {
                    host.ScrollToHorizontalOffset(Math.Max(0.0, host.HorizontalOffset - step));
                }
            };
            // 光标进到编辑器里就保持住：只按一下别处（甚至同一张卡片上的按钮）不应该
            // 立刻提交并收起编辑器，否则根本来不及选字、更别说按住拖选。
            profileNameEditor.PreviewMouseLeftButtonDown += delegate { profileNameEditor.Focus(); };
            // The editor overlays the name in the same cell; toggling visibility
            // avoids re-parenting an element (which threw "already a child").
            Grid profileNameHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            profileNameHost.Children.Add(profileNameBlock);
            profileNameHost.Children.Add(profileNameEditor);
            // 【2026-10-18 用户要求】把「启动与恢复」这套双击改名搬进公共区
            // （`Shared/UI/Cards/Interaction/InlineRename.cs`），三个工作区共用同一份代码。
            // 这里只负责“改完以后怎么落地”：改控制器数据 + 就地更新标题 + 进撤销栈。
            Action beginProfileRename = InlineRename.Attach(profileNameBlock, profileNameEditor, 120, delegate(string newName)
            {
                if (string.IsNullOrWhiteSpace(newName))
                {
                    statusText.Text = AppText.T(StartupRestoreTextKeys.CardRenameEmpty);
                    return false;
                }
                string oldName = profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
                try
                {
                    controller.RenameProfile(profile, newName);
                }
                catch (Exception exception)
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.CardRenameFailedFormat, exception.Message);
                    return false;
                }
                // 就地更新标题（重建列表会让界面闪一下，还会把正在编辑的编辑器拆掉）。
                profileNameBlock.Text = newName;
                UpdateProfileTitleInPlace(profile);
                PushUndo(
                    AppText.T(StartupRestoreTextKeys.CardUndoRename),
                    delegate { ApplyProfileName(profile, oldName); },
                    delegate { ApplyProfileName(profile, newName); });
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.CardRenamedFormat, newName);
                return true;
            }, headerBorder, headerBorder);
            titleText.Children.Add(profileNameHost);
            TextBlock profileCountBlock = new TextBlock
            {
                Text = StartupProfileCardText.BuildCountText(
                    profile, childProfiles.Count, AppText.CurrentLanguageTag),
                FontSize = 11,
                Foreground = AppTheme.TextSecondary
            };
            // 【2026-10-17】这一行显示的就是“N 个启动项目 · 最后保存时间”，**本身就是给用户/读屏听的内容**，
            // 不能让固定文案“启动项目数与保存时间”盖掉它（否则读屏只会念那句固定文案，数字和时间听不到）。
            // 所以这里只挂悬浮提示与帮助文本，不覆盖无障碍名字 —— UIA 会用控件自己的文字当名字。
            AccessibleToolTip.AttachDescription(
                profileCountBlock,
                StartupRestoreToolTips.ProfileCount(StartupProfileCardText.BuildCountToolTip(
                    profile, AppText.CurrentLanguageTag)));
            titleText.Children.Add(profileCountBlock);
            if (!string.IsNullOrWhiteSpace(profile.Id))
            {
                // 登记标题与条目数文本：结构没变时就地改字，不重建整表（重建会闪屏）。
                profileTitleBlocks[profile.Id] = profileNameBlock;
                profileCountBlocks[profile.Id] = profileCountBlock;
                // 标题条/外层整块在方法末尾统一登记到公共拖动卡片表。
            }
            title.Cursor = Cursors.Hand;
            title.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                args.Handled = true;
                if (suppressProfileSelectOnClickUp)
                {
                    suppressProfileSelectOnClickUp = false;
                    return;
                }
                SelectProfile(profile);
            };
            Grid.SetColumn(title, 1);
            header.Children.Add(title);

            // 顺序、尺寸、间距统一由 Cards/Actions 决定：
            // 移动 ≡ → 保存 → 恢复 → 删除。这里只绑“点下去做什么”。
            ActionBar actionBar = ActionBar.Create(ActionBarKind.ProfileCard);
            Button move = actionBar.Add(ActionSlot.Move, ActionButtons.Move(StartupRestoreToolTips.ProfileMove));
            Button save = actionBar.Add(ActionSlot.Save, ActionButtons.Save(StartupRestoreToolTips.ProfileSave));
            Button restore = actionBar.Add(ActionSlot.Restore, ActionButtons.Restore(StartupRestoreToolTips.ProfileRestore));
            Button remove = actionBar.Add(ActionSlot.Delete, ActionButtons.Delete(StartupRestoreToolTips.ProfileDelete));
            if (profile.IsFolder)
            {
                save.IsEnabled = false;
                AccessibleToolTip.Attach(save, StartupRestoreToolTips.FolderCannotSave);
                if (childProfiles.Count == 0)
                {
                    restore.IsEnabled = false;
                }
            }
            move.Cursor = Cursors.SizeAll;
            move.Tag = "profile-drag-handle";
            save.Click += delegate { SaveProfile(profile); };
            restore.Click += delegate { RestoreProfile(profile); };
            remove.Click += delegate { DeleteProfile(profile); };
            // 长按拖动：与"按住卡片拖动"完全同一套逻辑（同一份 DataObject 与浮动卡片），
            // 只是把触发点挪到这个贴边的手柄上，鼠标不用先对准卡片标题区。
            move.PreviewMouseLeftButtonDown += delegate(object moveSender, MouseButtonEventArgs moveArgs)
            {
                BeginProfileDragFromHandle(profile, moveArgs);
            };
            move.PreviewMouseMove += delegate(object moveSender, MouseEventArgs moveArgs)
            {
                ContinueStartupCardHandleDrag(profile, moveArgs);
            };
            move.PreviewMouseLeftButtonUp += delegate { FinishStartupCardDrag(); };
            Grid.SetColumn(actionBar.Build(), 2);
            header.Children.Add(actionBar.Build());
            headerBorder.Child = header;
            // 合并落点用**一条明确的“并进这份方案”放置条**，不再靠“落在卡片中间多少比例”去猜。
            // 真因（2026-10-11 实测日志）：`args.GetPosition(header)` 里的 header 是卡片**内部那个
            // 38 像素高的子网格**，比例算出来永远是 0.2 左右，于是“拖到卡片中间”永远被判定成“调顺序”，
            // 用户因此反馈“两个都没法实现”。
            // Clicking anywhere on the folder header (except its action
            // buttons) switches the current work folder, matching the
            // Explorer-like behaviour.
            headerBorder.Cursor = Cursors.Hand;
            headerBorder.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (suppressProfileSelectOnClickUp)
                {
                    suppressProfileSelectOnClickUp = false;
                    return;
                }
                if (IsActionControl(args.OriginalSource as DependencyObject))
                {
                    return;
                }
                SelectProfile(profile);
            };
            // Dragging a folder header (anywhere but its buttons) reorders the
            // work folders.  A small movement threshold keeps click-to-switch
            // and double-click-to-rename working.
            headerBorder.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmStartupCardDrag(profile, MouseButton.Left, args);
            };
            headerBorder.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmStartupCardDrag(profile, MouseButton.Right, args);
            };
            // 起拖、跟手、松手、看门狗全部由公共 `DragMoveSession` 接管；
            // 这里只负责“过阈值后把来源卡片交给引擎”。
            headerBorder.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                ContinueStartupCardDrag(profile, headerBorder, args);
            };
            headerBorder.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                FinishStartupCardDrag();
            };
            headerBorder.PreviewMouseRightButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                FinishStartupCardDrag();
            };

            // 右键工作方案：把"能做的都写上去"（用户要求 2026-10-08）——打开/展开、恢复、
            // 保存当前方案到此、重命名、设为当前、粘贴路径或网址新增、删除（进回收站）。
            // 【2026-10-19】菜单“长什么样、挂在哪些控件上”统一由公共件 `ContextMenus` 负责；
            // 每个菜单项点下去做什么，仍然调用本工作区自己的方法（恢复/保存/重命名/设为当前/删除…）。
            ContextMenu profileMenu = Shared.ContextMenus.Create(
                Shared.ContextMenuItem.Item(
                    expanded
                        ? AppText.T(StartupRestoreTextKeys.CardMenuCollapse)
                        : AppText.T(StartupRestoreTextKeys.CardMenuExpand),
                    delegate
                    {
                        expanded = !expanded;
                        if (!string.IsNullOrWhiteSpace(profile.Id))
                        {
                            profileExpansionStates[profile.Id] = expanded;
                        }
                        if (expanded)
                        {
                            ensureExpandedContent();
                        }
                        expandButton.Content = UiFactory.IconContent(expanded ? "\uE70D" : "\uE76C");
                        expandButton.FontFamily = AppTheme.IconFont;
                        expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuRestore),
                    delegate { RestoreProfile(profile); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuSaveHere),
                    delegate { SaveProfile(profile); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuRename),
                    delegate { if (beginProfileRename != null) beginProfileRename(); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuSetCurrent),
                    delegate { SelectProfile(profile); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuPasteAdd),
                    delegate { AddItemByClipboard(profile); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuNewChildPlan),
                    delegate { CreateChildProfileWithPrompt(profile, false); }),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuNewChildFolder),
                    delegate { CreateChildProfileWithPrompt(profile, true); }),
                Shared.ContextMenuItem.Separator(),
                Shared.ContextMenuItem.Item(
                    AppText.T(StartupRestoreTextKeys.CardMenuDelete),
                    delegate { DeleteProfile(profile); }));
            // 拖动排序：拖动时必须有可用的拖动来源；菜单不参与拖动判定。
            Shared.ContextMenus.Attach(headerBorder, profileMenu, title);
            container.Children.Add(headerBorder);

            if (expanded)
            {
                ensureExpandedContent();
            }
            expandedHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            container.Children.Add(expandedHost);
            // 点卡片空白区也选中这张卡片；但点到子卡片时最近一张卡片的 Tag 不是自己，
            // 不会冒泡改成选中父卡片。
            container.Background = System.Windows.Media.Brushes.Transparent;
            container.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                if (IsActionControl(args.OriginalSource as DependencyObject)
                    || suppressProfileSelectOnClickUp)
                {
                    return;
                }
                object owner = PlanFolderPresenter.FindOwningCardTag(args.OriginalSource as DependencyObject);
                if (object.ReferenceEquals(owner, profile))
                {
                    SelectProfile(profile);
                }
            };
            // 外层这一块的 Tag = 这份方案：卡片点击/拖动仍按块判断归属。
            // Header 和 Block 分开登记：Header 算槽位/目标框，Block 算禁止区/腾空间。
            Border block = new Border { Child = container, Tag = profile };
            if (!string.IsNullOrWhiteSpace(profile.Id))
            {
                startupCardElements.Register(profile.Id, headerBorder, block);
            }
            return block;
        }

        private Border BuildProfileItemRow(StartupProfile profile, SessionItem item, bool nested, int ordinal)
        {
            bool hasChildren = HasChildrenInProfile(profile, item);
            Border border = new Border
            {
                Background = AppTheme.RowSurface,
                BorderBrush = AppTheme.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(8),
                // 父行右边距 4；嵌套子行的外框已经在上层父行的内边距里，
                // 这里不能再加一次 4，否则子行删除按钮会比父行往左缩 4px。
                Padding = new Thickness(6, 5, nested ? 0 : 4, 5)
            };
            Grid row = new Grid();
                        // 第一列放"序号（+可选箭头）"：不带箭头的行也要放得下序号，
            // 原来子项只有 24px，正好把序号裁掉，所以副项看不到编号。
            // 带箭头的行要给“序号 + 箭头”按钮（54px）留出整格，避免箭头被列的右边界裁掉。
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hasChildren ? UiFactory.RowExpandColumnWidth : 30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // 右侧整块动作区一个 Auto 列：内部三格由 Cards/Actions 固定，
            // 不再让每个页面自己写 26/32/Auto 三列。
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel leading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // 序号放在最前面（在展开箭头之前），列宽统一，主项之间因此严格对齐；
            // 主项与子项字号一致，子项只多缩进一点以区分层级。
            TextBlock ordinalText = new TextBlock
            {
                Text = ordinal + ".",
                FontFamily = AppTheme.UiFont,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.TextSecondary,
                MinWidth = 26,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(nested ? 2 : 4, 0, 6, 0)
            };
            if (!hasChildren)
            {
                leading.Children.Add(ordinalText);
            }
            if (hasChildren)
            {
                string expansionKey = ItemExpansionKey(profile, item);
                bool childrenExpanded;
                if (!browserExpansionStates.TryGetValue(expansionKey, out childrenExpanded))
                {
                    childrenExpanded = true;
                    browserExpansionStates[expansionKey] = childrenExpanded;
                }
                // 和自启动应用共用同一份“序号 + 箭头”控件：
                // 尺寸、字体、悬停色都只在 UiFactory 里定义，页面里不再写死任何颜色/字形。
                TextBlock arrowGlyph;
                Button childExpandButton = UiFactory.OrdinalExpandButton(
                    ordinal, childrenExpanded, out arrowGlyph);
                childExpandButton.Click += delegate
                {
                    childrenExpanded = !childrenExpanded;
                    browserExpansionStates[expansionKey] = childrenExpanded;
                    UiFactory.SetExpandGlyph(arrowGlyph, childrenExpanded);
                    ToggleNestedRows(border, childrenExpanded);
                };
                leading.Children.Add(childExpandButton);
                // 数字区域也能像箭头一样点击：鼠标变手型，点它等同于点箭头。
            }

            Grid.SetColumn(leading, 0);
            row.Children.Add(leading);
            CheckBox enabled = new CheckBox
            {
                IsChecked = item.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            AccessibleToolTip.Attach(enabled, StartupRestoreToolTips.ItemEnable);
            itemEnabledCheckBoxes[ItemEnabledKey(profile, item)] = enabled;
            enabled.Checked += delegate
            {
                if (syncingItemEnabledState)
                {
                    return;
                }
                PushUndo(
                    AppText.T(StartupRestoreTextKeys.ItemUndoEnable),
                    delegate
                    {
                        controller.SetProfileItemEnabled(profile, item, false);
                        SyncItemEnabledCheckBox(profile, item);
                    },
                    delegate
                    {
                        controller.SetProfileItemEnabled(profile, item, true);
                        SyncItemEnabledCheckBox(profile, item);
                    });
                controller.SetProfileItemEnabled(profile, item, true);
            };
            enabled.Unchecked += delegate
            {
                if (syncingItemEnabledState)
                {
                    return;
                }
                PushUndo(
                    AppText.T(StartupRestoreTextKeys.ItemUndoDisable),
                    delegate
                    {
                        controller.SetProfileItemEnabled(profile, item, true);
                        SyncItemEnabledCheckBox(profile, item);
                    },
                    delegate
                    {
                        controller.SetProfileItemEnabled(profile, item, false);
                        SyncItemEnabledCheckBox(profile, item);
                    });
                controller.SetProfileItemEnabled(profile, item, false);
            };
            Grid.SetColumn(enabled, 1);
            row.Children.Add(enabled);
            bool showFullValue = item.Kind == SessionItemKind.WebPage
                || item.Kind == SessionItemKind.ExplorerFolder;
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            TextBlock headingText = new TextBlock
            {
                Text = ItemHeading(item),
                Foreground = AppTheme.TextPrimary,
                TextTrimming = showFullValue ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = showFullValue ? TextWrapping.Wrap : TextWrapping.NoWrap
            };
            text.Children.Add(headingText);
            text.Children.Add(new TextBlock
            {
                Text = ItemSupportingText(item),
                Foreground = AppTheme.TextSecondary,
                FontSize = 11,
                TextTrimming = showFullValue ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                TextWrapping = showFullValue ? TextWrapping.Wrap : TextWrapping.NoWrap
            });
            Grid.SetColumn(text, 2);
            row.Children.Add(text);
            // Hover highlight: a blue tint over the whole row and a blue
            // underline on the heading show this entry is clickable (jump to
            // its window).  It applies whenever the mouse is over the row.

            // 右侧动作区：≡ 拖动 → 最小化（没有就补空位）→ 删除。
            // 尺寸、顺序、空位都由 Cards/Actions 统一；这里只绑拖动/删除效果。
            ActionBar actionBar = ActionBar.Create(ActionBarKind.ItemRow);
            Button grip = actionBar.Add(
                ActionSlot.Move,
                ActionButtons.DragHandle(StartupRestoreToolTips.ItemGrip));
            // The drag cursor is deliberately left to Windows: returning
            // "None" from the drop target is what makes the system paint its
            // own "cannot drop here" cursor.  Suppressing the default cursors
            // (UseDefaultCursors = false) blocked that, and setting a cursor by
            // hand does not work at all while the OLE drag loop owns the mouse.
            grip.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                nativeItemDragJustEnded = false;   // 新的一次按下：清掉“刚刚拖完”的标记
                rowDragMouseButton = MouseButton.Left;   // ≡ 手柄始终左键直接拖
                dragStartPoint = args.GetPosition(this);
                dragSourceItem = null;
                dragActive = false;
            };
            // 把这一行登记到“原生拖动落点表”里：拖动时才能用鼠标位置反查落点。
            // 【2026-10-24 修“文件夹行把整组都算成自己”】落点命中只认**这一行标题本身**（row），
            // 不认装着子项的外层 border：父行的 border 把整组子项都包在里面，指针落在子项左边的
            // 缩进空白、子项之间的缝里时也会命中父行，导致“明明在空白处，却不显示禁止符号”。
            // Container 仍保留外层 border，画插入线、取整组位置时还要用它。
            nativeItemRows.Add(new NativeItemRow { Profile = profile, Item = item, Row = row, Container = border });
            // 松手交给原生拖动收尾（OLE 版已经不再启动）。
            grip.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                EndNativeItemDrag();
            };
            row.PreviewMouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                EndNativeItemDrag();
            };
            grip.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                BeginItemReorderDrag(profile, grip, item, border, args);
            };
            grip.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                EndNativeItemDrag();
            };
            // 按住拖动前缀，整行也能起拖，不必去按 ≡ 手柄；
            // 前缀和鼠标键都来自设置页的“拖动”行（默认 Shift + 左键）。
            row.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmStartupItemRowDrag(MouseButton.Left, args);
            };
            row.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                ArmStartupItemRowDrag(MouseButton.Right, args);
            };
            row.PreviewMouseMove += delegate(object sender, MouseEventArgs args)
            {
                if (!rowDragArmed)
                {
                    return;
                }
                if (!DragGestureSettings.IsMouseButtonPressed(args, rowDragMouseButton))
                {
                    rowDragArmed = false;
                    return;
                }
                BeginItemReorderDrag(profile, row, item, border, args);
            };
            row.PreviewMouseLeftButtonUp += delegate { rowDragArmed = false; };
            row.PreviewMouseRightButtonUp += delegate { rowDragArmed = false; };
            // 不适用于“没有独立窗口”的行（网页、文件、有子项的浏览器/资源管理器父行）时，
            // ActionBar 会自动补一个同宽空位，删除按钮不会左右跳。
            if (CanToggleItemMinimized(profile, item))
            {
                actionBar.Add(ActionSlot.Minimize, CreateItemMinimizeButton(profile, item));
            }

            Button remove = actionBar.Add(
                ActionSlot.Delete,
                ActionButtons.Delete(StartupRestoreToolTips.ItemDelete));
            remove.Click += delegate { RemoveProfileItem(profile, item); };
            Grid.SetColumn(actionBar.Build(), 3);
            row.Children.Add(actionBar.Build());
            // 点击整行＝跳转。**交互层搬到了公共区** `Shared/UI/Cards/Interaction/ClickToJump.cs`
            // （用户 2026-10-18：只搬“点击跳转和交互”，窗口激活/标签页定位/Explorer 子项这些深层逻辑不搬，
            //   这样以后「图标位置保存」变成“跳转到图片”时只换 Action、不改交互）。
            // 这里只提供“跳去哪”：还是本工作区自己那套 `JumpToSavedItemWindow`，一行没动。
            WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.AttachRow(
                row,
                text,
                WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.BuildRowToolTip(
                    BuildBrowserAwareJumpHint(profile, item)),
                delegate
                {
                    // 【拖动结束不能当成点击】用户 2026-10-16：“无法移动，光有分割线，松开鼠标变成跳转”。
                    if (dragActive || nativeItemDragJustEnded)
                    {
                        dragActive = false;
                        nativeItemDragJustEnded = false;
                        return true;
                    }
                    return false;
                },
                item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThisItem),
                delegate { JumpToSavedItemWindow(profile, item); },
                delegate(string message) { statusText.Text = message; },
                // ≡ 手柄只给拖动用：点一下它不算“点了整行”（两边同一套判定）。
                WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.IgnoreInside(grip),
                // 悬停高亮也走公共件（用户 2026-10-18：这种交互显示要放到公共文件夹内），
                // 颜色与原来 `AttachJumpHover` 一模一样：#FAFBFC → #E7F1F8、名字变主题色 + 下划线；
                // 拖动中不亮（只让插入线提示落点）。
                border,
                headingText,
                AppTheme.RowSurface,
                AppTheme.RowHoverSurface,
                AppTheme.TextPrimary,
                AppTheme.Accent,
                delegate { return dragSourceItem != null || dragActive; });

            // Drag target: open a gap and show the insertion line before or
            // after the hovered row, and insert there on drop. The line alone is
            // the indicator; there is deliberately no highlight box around the
            // hovered row.


            StackPanel rowAndChildren = new StackPanel();
            rowAndChildren.Children.Add(row);
            if (hasChildren)
            {
                string expansionKey = ItemExpansionKey(profile, item);
                bool childrenExpanded = browserExpansionStates.ContainsKey(expansionKey) && browserExpansionStates[expansionKey];
                // 左边只缩进子项；不要把整块子项往左缩窄，否则右侧的
                // 最小化/删除按钮会比父行短一截，看起来没有对齐。
                // 用“固定缩进列 + 剩余宽度列”的结构，子行右边缘仍和父行一致。
                Grid childrenHost = new Grid
                {
                    Visibility = childrenExpanded ? Visibility.Visible : Visibility.Collapsed
                };
                childrenHost.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(nested ? 18 : 24)
                });
                childrenHost.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
                StackPanel children = new StackPanel();
                Grid.SetColumn(children, 1);
                int childOrdinal = 0;
                for (int index = 0; index < profile.Items.Count; index++)
                {
                    SessionItem child = profile.Items[index];
                    if (child != null && IsChildOf(item, child))
                    {
                        childOrdinal++;
                        children.Children.Add(BuildProfileItemRow(profile, child, true, childOrdinal));
                    }
                }
                childrenHost.Children.Add(children);
                rowAndChildren.Children.Add(childrenHost);
            }
            border.Child = rowAndChildren;
            // 右键菜单（2026-11-01 统一）：浏览器行保留“新增网页”，网页行保留“设置归属/打开”，
            // 所有行末尾都多一项“移动到方案…”（子项只给不可点说明）。
            ContextMenu itemContextMenu;
            if (item.Kind == SessionItemKind.Application
                && !string.IsNullOrWhiteSpace(item.BrowserGroupKey))
            {
                itemContextMenu = Shared.ContextMenus.Create(
                    Shared.ContextMenuItem.Item(
                        AppText.T(StartupRestoreTextKeys.ItemContextAddWebPage),
                        delegate { AddWebPageToBrowser(profile, item); }));
            }
            else if (item.Kind == SessionItemKind.WebPage)
            {
                itemContextMenu = Shared.ContextMenus.Create(
                    Shared.ContextMenuItem.Item(
                        AppText.T(StartupRestoreTextKeys.ItemContextSetBinding),
                        delegate
                        {
                            BrowserPageBindingOption option = ChooseBrowserPageBinding(item);
                            if (option != null)
                            {
                                ApplyBrowserPageBinding(profile, item, option);
                            }
                        },
                        AppText.T(StartupRestoreTextKeys.ItemContextSetBindingHelp)),
                    Shared.ContextMenuItem.Item(
                        AppText.T(StartupRestoreTextKeys.ItemContextOpenWebPage),
                        delegate { OpenSavedItem(item); }));
            }
            else
            {
                itemContextMenu = Shared.ContextMenus.Create();
            }
            if (itemContextMenu.Items.Count > 0)
            {
                itemContextMenu.Items.Add(new Separator());
            }
            itemContextMenu.Items.Add(BuildMoveItemToProfileMenu(profile, item));
            Shared.ContextMenus.Attach(border, itemContextMenu);
            return border;
        }

        private static string BuildBrowserAwareJumpHint(StartupProfile profile, SessionItem item)
        {
            string hint = WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.JumpHint;
            if (item == null || item.Kind != SessionItemKind.WebPage)
            {
                return hint;
            }
            SessionItem parent = profile == null
                ? null
                : FindBrowserWindowParent(profile.Items, item);
            if (parent == null || string.IsNullOrWhiteSpace(parent.DisplayName))
            {
                return hint + AppText.T(StartupRestoreTextKeys.ItemJumpHintUnbound);
            }
            string tab = item.BrowserTabIndex >= 0
                ? AppText.F(StartupRestoreTextKeys.ItemJumpHintTabFormat, item.BrowserTabIndex + 1)
                : AppText.T(StartupRestoreTextKeys.ItemJumpHintTabUnknown);
            return hint + AppText.F(
                StartupRestoreTextKeys.ItemJumpHintOwnerFormat,
                parent.DisplayName,
                tab,
                item.BrowserBindingKind == BrowserPageBindingKind.UserBound
                    ? AppText.T(StartupRestoreTextKeys.ItemJumpHintManualSuffix)
                    : string.Empty);
        }

        private void AddWebPageToBrowser(StartupProfile profile, SessionItem browserParent)
        {
            if (browserParent == null)
            {
                return;
            }
            PasteItemDialog dialog = new PasteItemDialog { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
            {
                return;
            }
            // 【2026-10-18 用户反馈】粘贴进来的网址常带双引号（"https://…" / “https://…” / <https://…>），
            // 以前直接 Uri.TryCreate 就判成“不是网址”。现在统一走 WebAddressTools 先剥外层符号再判定。
            string preparedUrl;
            if (!WebAddressTools.TryPrepareWebAddress(dialog.Value, out preparedUrl))
            {
                MessageBox.Show(
                    this,
                    AppText.T(StartupRestoreTextKeys.ItemInvalidUrl),
                    AppText.T(StartupRestoreTextKeys.ItemAddWebPageTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            Uri uri;
            if (!Uri.TryCreate(preparedUrl, UriKind.Absolute, out uri))
            {
                uri = null;
            }

            SessionItem page = new SessionItem
            {
                Kind = SessionItemKind.WebPage,
                DisplayName = uri == null || string.IsNullOrWhiteSpace(uri.Host) ? preparedUrl : uri.Host,
                Url = preparedUrl,
                TargetPath = preparedUrl,
                ParentId = browserParent.Id,
                BrowserGroupKey = browserParent.BrowserGroupKey,
                BrowserExecutablePath = !string.IsNullOrWhiteSpace(browserParent.BrowserExecutablePath)
                    ? browserParent.BrowserExecutablePath
                    : browserParent.ExecutablePath,
                BrowserWindowIndex = browserParent.BrowserWindowIndex,
                IsEnabled = true
            };
            List<SessionItem> before = SnapshotItems(profile);
            if (controller.AddSessionItemToProfile(profile, page) == null)
            {
                return;
            }
            PushItemListUndo(
                profile,
                before,
                SnapshotItems(profile),
                AppText.T(StartupRestoreTextKeys.ItemUndoAddWebPage));
            RefreshProfileList();
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.ItemAddedWebPageFormat, preparedUrl);
        }

        private static void ToggleNestedRows(Border border, bool visible)
        {
            if (border == null)
            {
                return;
            }
            StackPanel content = border.Child as StackPanel;
            if (content == null || content.Children.Count < 2)
            {
                return;
            }
            UIElement nestedRows = content.Children[1] as UIElement;
            if (nestedRows != null)
            {
                nestedRows.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string ItemExpansionKey(StartupProfile profile, SessionItem item)
        {
            string profileId = profile == null || string.IsNullOrWhiteSpace(profile.Id) ? string.Empty : profile.Id;
            string itemIdentity = string.Empty;
            if (item != null)
            {
                if (string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase))
                {
                    // Explorer parents all share explorer.exe.  Use the
                    // captured window ordinal so each folder window keeps
                    // its own expanded/collapsed state.
                    itemIdentity = "explorer-window|" + item.ApplicationWindowIndex;
                }
                else if (!string.IsNullOrWhiteSpace(item.BrowserGroupKey))
                {
                    itemIdentity = item.BrowserGroupKey + "|window|" + item.BrowserWindowIndex;
                }
                else
                {
                    itemIdentity = !string.IsNullOrWhiteSpace(item.ExecutablePath)
                        ? item.ExecutablePath
                        : item.Id;
                }
            }
            return profileId + ":" + (itemIdentity ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static bool HasParentInProfile(StartupProfile profile, SessionItem item)
        {
            if (profile == null
                || profile.Items == null
                || item == null
                || (item.Kind != SessionItemKind.WebPage && item.Kind != SessionItemKind.ExplorerFolder))
            {
                return false;
            }
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem candidate = profile.Items[index];
                if (candidate != null && candidate.Kind == SessionItemKind.Application && IsChildOf(candidate, item))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasChildrenInProfile(StartupProfile profile, SessionItem item)
        {
            if (profile == null || profile.Items == null || item == null || item.Kind != SessionItemKind.Application)
            {
                return false;
            }
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem candidate = profile.Items[index];
                if (candidate != null && IsChildOf(item, candidate))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsChildOf(SessionItem parent, SessionItem child)
        {
            if (parent == null
                || child == null
                || parent.Kind != SessionItemKind.Application
                || (child.Kind != SessionItemKind.WebPage && child.Kind != SessionItemKind.ExplorerFolder))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(child.ParentId) && !string.IsNullOrWhiteSpace(parent.Id))
            {
                return string.Equals(child.ParentId, parent.Id, StringComparison.OrdinalIgnoreCase);
            }

            if (child.Kind == SessionItemKind.ExplorerFolder)
            {
                // Explorer folder entries are grouped only with the Explorer
                // window that produced them.  Matching by explorer.exe alone
                // would incorrectly place every folder under every window.
                if (!string.Equals(parent.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (child.ApplicationWindowIndex >= 0 && parent.ApplicationWindowIndex >= 0)
                {
                    return child.ApplicationWindowIndex == parent.ApplicationWindowIndex;
                }
                return !string.IsNullOrWhiteSpace(child.ExecutablePath)
                    && !string.IsNullOrWhiteSpace(parent.ExecutablePath)
                    && string.Equals(child.ExecutablePath, parent.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(child.BrowserGroupKey) && !string.IsNullOrWhiteSpace(parent.BrowserGroupKey))
            {
                bool sameWindow = child.BrowserWindowIndex < 0
                    || parent.BrowserWindowIndex < 0
                    || child.BrowserWindowIndex == parent.BrowserWindowIndex;
                return sameWindow
                    && string.Equals(child.BrowserGroupKey, parent.BrowserGroupKey, StringComparison.OrdinalIgnoreCase);
            }
            return !string.IsNullOrWhiteSpace(child.BrowserExecutablePath)
                && !string.IsNullOrWhiteSpace(parent.ExecutablePath)
                && string.Equals(child.BrowserExecutablePath, parent.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        private static string ItemIcon(SessionItemKind kind)
        {
            switch (kind)
            {
                case SessionItemKind.Application:
                    return "\uE74E";
                case SessionItemKind.ExplorerFolder:
                    return "\uE8B7";
                case SessionItemKind.Document:
                    return "\uE8A5";
                case SessionItemKind.WebPage:
                    return "\uE774";
                default:
                    return "\uE74E";
            }
        }

        private static string ItemHeading(SessionItem item)
        {
            string heading = SessionItemDisplayName(item);
            if (item != null && item.Kind == SessionItemKind.WebPage)
            {
                heading = string.IsNullOrWhiteSpace(item.Url)
                    ? (string.IsNullOrWhiteSpace(item.TargetPath) ? heading : item.TargetPath)
                    : item.Url;
                return heading;
            }
            if (item != null
                && (item.Kind == SessionItemKind.ExplorerFolder
                    || string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(item.BrowserGroupKey)
                        && item.Kind == SessionItemKind.Application)))
            {
                return heading;
            }
            return item == null || item.Sequence <= 0 ? heading : item.Sequence + ". " + heading;
        }

        private static string ItemSupportingText(SessionItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            // 用户 2026-10-16（第 1 条）：“下面小行的格式用追加当前时间的格式，
            // 并且一定是电脑上当前时间的格式（在中国就是北京时间，在美国就是美国那边的时间，
            // 千万不要固定写死成北京时间）。”
            // → 每一条小行都在原来的说明后面接上「它是什么时候被记录的」，
            //   时区一律取这台电脑自己的时区（见 TimeText.FormatComputerLocalStamp）。
            string stamp = WorkspaceRestorer.Core.TimeText.FormatUtcAsComputerLocalStamp(
                item.StartTimeUtc,
                AppText.CurrentLanguageTag);

            if (item.Kind == SessionItemKind.WebPage)
            {
                string pageName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? AppText.T(StartupRestoreTextKeys.FallbackWebPageFallback)
                    : item.DisplayName;
                return AppendStampToSupportingText(pageName, stamp);
            }
            if (item.Kind == SessionItemKind.ExplorerFolder)
            {
                string folder = string.IsNullOrWhiteSpace(item.TargetPath)
                    ? AppText.T(StartupRestoreTextKeys.FallbackFolderPathUnknown)
                    : item.TargetPath;
                return AppendStampToSupportingText(folder, stamp);
            }
            if (string.Equals(item.CaptureSource, "VisualStudioCodeWindow", StringComparison.OrdinalIgnoreCase))
            {
                string path = string.IsNullOrWhiteSpace(item.TargetPath)
                    ? AppText.T(StartupRestoreTextKeys.FallbackPathUnknown)
                    : item.TargetPath;
                if (item.WindowPlacement != null && item.WindowPlacement.HasBounds)
                {
                    path = AppText.F(
                        StartupRestoreTextKeys.ItemVscodeLocationFormat,
                        path,
                        item.WindowPlacement.Left,
                        item.WindowPlacement.Top);
                }
                return AppendStampToSupportingText(path, stamp);
            }
            if (!string.IsNullOrWhiteSpace(item.WindowTitle))
            {
                return AppendStampToSupportingText(item.WindowTitle, stamp);
            }
            return AppendStampToSupportingText(SessionItemKindText(item.Kind), stamp);
        }

        /// <summary>把小行的时间戳接在说明后面（时间戳为空就原样返回）。</summary>
        private static string AppendStampToSupportingText(string text, string stamp)
        {
            if (string.IsNullOrWhiteSpace(stamp))
            {
                return text;
            }
            return (string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim() + " · ") + stamp;
        }

        private void RemoveItemExpansionStates(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }
            string prefix = profileId + ":";
            List<string> keys = browserExpansionStates.Keys.ToList();
            for (int index = 0; index < keys.Count; index++)
            {
                if (keys[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    browserExpansionStates.Remove(keys[index]);
                }
            }
        }

        private void RemoveBrowserExpansionStates(StartupProfile profile)
        {
            if (profile == null || profile.Items == null)
            {
                return;
            }
            for (int index = 0; index < profile.Items.Count; index++)
            {
                SessionItem item = profile.Items[index];
                if (item != null && item.Kind == SessionItemKind.Application && !string.IsNullOrWhiteSpace(item.Id))
                {
                    browserExpansionStates.Remove(ItemExpansionKey(profile, item));
                }
            }
        }

        private static string SessionItemKindText(SessionItemKind kind)
        {
            switch (kind)
            {
                case SessionItemKind.Application:
                    return AppText.T(StartupRestoreTextKeys.FallbackSessionKindApplication);
                case SessionItemKind.ExplorerFolder:
                    return AppText.T(StartupRestoreTextKeys.FallbackSessionKindExplorerFolder);
                case SessionItemKind.Document:
                    return AppText.T(StartupRestoreTextKeys.FallbackSessionKindDocument);
                case SessionItemKind.WebPage:
                    return AppText.T(StartupRestoreTextKeys.FallbackSessionKindWebPage);
                default:
                    return kind.ToString();
            }
        }

        private static string SessionItemDisplayName(SessionItem item)
        {
            if (item == null)
            {
                return AppText.T(StartupRestoreTextKeys.FallbackItemFallback);
            }
            if (!string.IsNullOrWhiteSpace(item.DisplayName))
            {
                // 名字里可能带着旧序号（"13. QQ"）：显示时去掉，序号统一由左侧序号列给出，
                // 这样拖动调整顺序后编号才会跟着更新，不会出现"1. 15. QQ"。
                return System.Text.RegularExpressions.Regex.Replace(
                    item.DisplayName, "^\\s*\\d+\\s*[.、]\\s*", string.Empty);
            }
            if (!string.IsNullOrWhiteSpace(item.WindowTitle))
            {
                return item.WindowTitle;
            }
            if (!string.IsNullOrWhiteSpace(item.TargetPath))
            {
                return item.TargetPath;
            }
            return string.IsNullOrWhiteSpace(item.ExecutablePath)
                ? AppText.T(StartupRestoreTextKeys.FallbackItemFallback)
                : item.ExecutablePath;
        }

    }
}
