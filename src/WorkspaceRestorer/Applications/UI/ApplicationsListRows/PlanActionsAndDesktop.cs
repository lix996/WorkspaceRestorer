using System;
using System.Collections.Generic;
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
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.UI
{
    public sealed partial class MainWindow
    {
        /// <summary>删除一份自启动方案（带确认）。</summary>
        private void DeleteApplicationPlanWithConfirm(ApplicationPlan plan)
        {
            if (plan == null)
            {
                return;
            }
            int subtreeCount = PlanFolderTree.Subtree(controller.ApplicationPlans, plan).Count;
            string detail = subtreeCount > 1
                ? AppText.F(ApplicationsTextKeys.DialogDeletePlanWithChildrenFormat, subtreeCount - 1)
                : AppText.T(ApplicationsTextKeys.DialogDeletePlanOnly);
            // 公共确认框：**「是」在左、「否」在右**，右上角 × 就等于「否」（用户第 52、53 条要求）。
            if (!WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(ApplicationsTextKeys.DialogDeletePlanTitle),
                AppText.F(
                    ApplicationsTextKeys.DialogDeletePlanMessageFormat,
                    plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback)),
                detail))
            {
                return;
            }
            // 【2026-10-17】删除方案也要能 Ctrl+Z（用户报“自启动应用无法撤销”的其中一项）：
            // 记下删除前的快照 + 这次删除写进回收站的那条记录（撤销时一起撤掉，免得回收站留副本）。
            ApplicationPlansSnapshot beforeDelete = CaptureApplicationPlansSnapshot();
            string deletedName = plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
            controller.DeleteApplicationPlan(plan);
            RecycleBinEntry binEntry = FindRecycleBinEntryForPlan(plan);
            RefreshApplicationPlanList();
            statusText.Text = AppText.F(ApplicationsTextKeys.StatusPlanDeletedFormat, deletedName);
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoPlanDeletedFormat, deletedName), beforeDelete, binEntry);
        }

        /// <summary>把勾选/单条选中的桌面项加进**当前自启动方案**。</summary>
        private void AddDesktopEntriesToCurrentApplicationPlan(IList<DesktopEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }
            // 【2026-10-17】加入项目也要能 Ctrl+Z（以前扫描结果/浏览加完，按 Ctrl+Z 完全没反应）。
            ApplicationPlansSnapshot beforeAdd = CaptureApplicationPlansSnapshot();
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            if (plan == null)
            {
                // 还没有方案就顺手建一个，免得用户点了没反应。
                plan = controller.CreateApplicationPlan(GetNewApplicationPlanName());
            }
            int added = controller.AddDesktopEntriesToApplicationPlan(plan, entries);
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index] != null)
                {
                    entries[index].IsSelected = false;
                }
            }
            RefreshDesktopList();
            RefreshApplicationPlanList();
            if (added > 0)
            {
                PushApplicationPlansUndo(AppText.F(ApplicationsTextKeys.UndoAddedFormat, added), beforeAdd);
            }
        }

        private static string DesktopKey(DesktopEntry entry)
        {
            return entry == null ? string.Empty : (entry.Path ?? entry.DisplayName ?? string.Empty);
        }

        /// <summary>一个桌面项一行：序号 + 手柄 + 勾选 + 文本 + 删除 + 打开（外观照启动与恢复）。</summary>
        /// <summary>
        /// 图标容器：先放通用文件字形占位，后台取到真图标后由 <see cref="ApplyLoadedDesktopIcon"/> 替换。
        ///
        /// 图标仍来自 <see cref="WorkspaceRestorer.Services.ShellIconService"/>：`.lnk` / `.url` 是
        /// **目标程序**的图标，文件夹是文件夹图标，bat/exe/普通文件按系统关联取；
        /// 只是取图标从 UI 线程挪到了后台，格子先出现、图标再一个个补上。
        /// </summary>
        private static Grid BuildDesktopIconHost(DesktopEntry entry)
        {
            Grid host = new Grid
            {
                Width = DesktopTileIconSize,
                Height = DesktopTileIconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock fallback = new TextBlock
            {
                Text = "\uE7C3",
                FontFamily = AppTheme.IconFont,
                FontSize = Math.Max(16.0, DesktopTileIconSize * 0.7),
                Foreground = AppTheme.TextSecondary,
                Width = DesktopTileIconSize,
                Height = DesktopTileIconSize,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            // 给读屏和自检一个名字：图片本身没有可读文字，不设名字就只是一张“图”。
            LocalizedText.AccessibleName(
                fallback, ApplicationsTextKeys.DesktopIconNameGenericFormat, DesktopIconCaption(entry));
            host.Children.Add(fallback);
            return host;
        }

        /// <summary>后台图标取到后回 UI 线程替换占位字形（尺寸不变，不会把右边按钮挤动）。</summary>
        private void ApplyLoadedDesktopIcon(string key, ImageSource image, string displayName)
        {
            Grid host;
            if (image == null || string.IsNullOrEmpty(key)
                || !desktopIconHosts.TryGetValue(key, out host) || host == null)
            {
                return;
            }
            Image icon = new Image
            {
                Source = image,
                Width = DesktopTileIconSize,
                Height = DesktopTileIconSize,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            string caption = string.IsNullOrWhiteSpace(displayName)
                ? AppText.T(ApplicationsTextKeys.ItemThisFallback)
                : displayName;
            LocalizedText.AccessibleName(icon, ApplicationsTextKeys.DesktopIconNameFormat, caption);
            host.Children.Clear();
            host.Children.Add(icon);
        }

        private static string DesktopIconCaption(DesktopEntry entry)
        {
            return entry == null || string.IsNullOrWhiteSpace(entry.DisplayName)
                ? AppText.T(ApplicationsTextKeys.ItemThisFallback)
                : entry.DisplayName;
        }

        /// <summary>
        /// 一个候选格子：大图标 + 名字 + 路径，底部一排是「勾选框 / 只添加这一项 / 打开 / 从当前方案移除」。
        ///
        /// 用户 2026-10-17 的原话：“桌面上的应用要带有大图标（就是看着图标选就行了）”。
        /// 原来是一行一行的小字列表，62 项长得一模一样，只能靠读名字认；现在改成 128 宽的大图标格子，
        /// 图标走 <see cref="WorkspaceRestorer.Services.ShellIconService"/> 的**大图标**通道
        /// （向 shell 要 96×96，缩到 40 DIP 显示，150% 屏上正好不糊），
        /// **整块点一下就是选中/取消**（边框变主题色 + 底色变淡青），再点“添加选中的项目”一次加入方案。
        /// 勾选框与三个操作按钮都保留原名（读屏、键盘和自检都靠这些名字）。
        /// </summary>
        private Border BuildDesktopTile(DesktopEntry entry)
        {
            string key = DesktopKey(entry);
            bool selected = entry != null && entry.IsSelected;
            Border tile = new Border
            {
                Width = DesktopTileWidth,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(8, 8, 8, 4),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                Background = selected ? AppTheme.SelectionSurface : AppTheme.Surface,
                BorderBrush = selected ? AppTheme.Accent : AppTheme.Border,
                Cursor = Cursors.Hand,
                Tag = key
            };

            StackPanel column = new StackPanel();
            Grid iconHost = BuildDesktopIconHost(entry);
            iconHost.HorizontalAlignment = HorizontalAlignment.Center;
            iconHost.Margin = new Thickness(0, 0, 0, 4);
            column.Children.Add(iconHost);
            if (!string.IsNullOrEmpty(key))
            {
                desktopIconHosts[key] = iconHost;
            }

            column.Children.Add(new TextBlock
            {
                Text = entry == null
                    ? AppText.T(ApplicationsTextKeys.ItemUnnamed)
                    : (entry.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemUnnamed)),
                FontFamily = AppTheme.UiFont,
                FontSize = 12,
                Foreground = AppTheme.TextPrimary,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = 30,
                VerticalAlignment = VerticalAlignment.Center
            });
            column.Children.Add(new TextBlock
            {
                Text = entry == null ? string.Empty : DesktopTilePathLabel(entry.Path),
                FontFamily = AppTheme.UiFont,
                FontSize = 9.5,
                Foreground = AppTheme.TextSecondary,
                TextAlignment = TextAlignment.Center,
                // 路径只占一行（太长就省略号）：这里必须显式 NoWrap —— 全局 TextBlock 样式默认是换行的，
                // 一换行格子就会一个高一个矮，格子墙看着参差不齐。
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Height = 14,
                Margin = new Thickness(0, 1, 0, 0)
            });

            Grid actions = new Grid { Margin = new Thickness(0, 5, 0, 0) };
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            CheckBox pick = new CheckBox
            {
                IsChecked = selected,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AccessibleToolTip.Attach(pick, ApplicationsToolTips.DesktopPick);
            Grid.SetColumn(pick, 0);
            actions.Children.Add(pick);
            // 登记起来：全选/取消全选、加进方案后要把勾选状态同步到这些勾选框上（见 ApplyDesktopFilter）。
            if (!string.IsNullOrEmpty(key))
            {
                desktopTileChecks[key] = pick;
            }

            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(BuildDesktopTileAction("\uE710", ApplicationsToolTips.DesktopAddOne, delegate
            {
                if (entry != null)
                {
                    AddSingleDesktopEntryToActiveProfile(entry);
                }
            }));
            buttons.Children.Add(BuildDesktopTileAction("\uE8A7", ApplicationsToolTips.DesktopOpenTile, delegate
            {
                if (entry != null)
                {
                    OpenDesktopEntry(entry);
                }
            }));
            buttons.Children.Add(BuildDesktopTileAction("\uE74D", ApplicationsToolTips.DesktopRemove, delegate
            {
                if (entry != null)
                {
                    RemoveDesktopEntryFromProfile(entry);
                }
            }));
            Grid.SetColumn(buttons, 2);
            actions.Children.Add(buttons);
            column.Children.Add(actions);
            tile.Child = column;

            DesktopEntry captured = entry;
            Action applySelection = delegate
            {
                bool on = pick.IsChecked == true;
                if (captured != null)
                {
                    captured.IsSelected = on;
                }
                tile.BorderBrush = on ? AppTheme.Accent : AppTheme.Border;
                tile.Background = on ? AppTheme.SelectionSurface : AppTheme.Surface;
                UpdateDesktopScanSummary();
            };
            // 勾选框和整块点击走同一套视觉更新（勾选框本身也要能点，键盘/读屏都靠它）
            pick.Checked += delegate { applySelection(); };
            pick.Unchecked += delegate { applySelection(); };
            tile.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs args)
            {
                // 点在勾选框或那三个按钮上时不算“点格子”（否则会勾上又取消）
                if (IsActionControl(args.OriginalSource as DependencyObject))
                {
                    return;
                }
                pick.IsChecked = !(pick.IsChecked == true);
                args.Handled = true;
            };
            tile.MouseEnter += delegate
            {
                if (captured != null && captured.IsSelected)
                {
                    return;
                }
                tile.Background = AppTheme.HoverSurface;
            };
            tile.MouseLeave += delegate
            {
                tile.Background = captured != null && captured.IsSelected ? AppTheme.SelectionSurface : AppTheme.Surface;
            };
            return tile;
        }

        /// <summary>格子里那行小字：只留路径的**最后两段**（“…\Desktop\1111.txt”）。
        ///
        /// 格子只有 128 宽，整条路径一显示就被截成“C:\Users\MR\Desktop…”—— 末尾（真正能区分的那部分）
        /// 反而看不见。WPF 的 TextTrimming 只能截尾，所以这里自己把开头砍掉、留个省略号。
        /// </summary>
        private static string DesktopTilePathLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                string trimmed = path.TrimEnd('\\', '/');
                string name = System.IO.Path.GetFileName(trimmed);
                string directory = System.IO.Path.GetDirectoryName(trimmed);
                string parent = string.IsNullOrEmpty(directory) ? string.Empty : System.IO.Path.GetFileName(directory);
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent))
                {
                    return path;
                }
                string tail = parent + "\\" + name;
                return tail.Length + 3 >= path.Length ? path : "…\\" + tail;
            }
            catch
            {
                return path;
            }
        }

        /// <summary>格子里的小图标按钮（标准 38×38 塞不进 128 宽的格子，这里缩到 26×24）。</summary>
        private static Button BuildDesktopTileAction(string glyph, TooltipDefinition definition, Action click)
        {
            Button button = UiFactory.IconButton(glyph, definition);
            button.Width = 26;
            button.Height = 24;
            button.MinWidth = 26;
            button.MinHeight = 24;
            button.FontSize = 13;
            button.Margin = new Thickness(0);
            button.Click += delegate { click(); };
            return button;
        }

        /// <summary>这份自启动方案里有没有这个路径的项目（按路径比对、忽略大小写）。</summary>
        private static bool ApplicationPlanHasPath(ApplicationPlan plan, string path)
        {
            if (plan == null || plan.Items == null || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem item = plan.Items[index];
                if (item != null && string.Equals(item.TargetPath ?? string.Empty, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把该候选从**当前自启动方案**里移除（按路径匹配）。
        ///
        /// 【2026-10-17 第 50 批修的真 bug】原来这个方法移除的是 `controller.ActiveProfile` ——
        /// **「启动与恢复」的工作方案**：在自启动应用页上点“从当前方案移除”，
        /// 动的是另一个工作区的数据；而自启动方案里明明有这一项时它会说“当前方案里没有这一项”，
        /// 于是这个按钮永远不生效（与重建第 67 段那两处串区 bug 同一类）。
        /// 现在只动自启动方案，并按路径比对（名字可能重复，路径才是唯一依据）。
        /// </summary>
        private void RemoveDesktopEntryFromProfile(DesktopEntry entry)
        {
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            if (plan == null || plan.Items == null || entry == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusNoPlanToRemove);
                return;
            }
            ApplicationPlanItem target = null;
            string wantedPath = entry.Path ?? string.Empty;
            for (int index = 0; index < plan.Items.Count; index++)
            {
                ApplicationPlanItem item = plan.Items[index];
                if (item == null)
                {
                    continue;
                }
                string itemPath = item.TargetPath ?? string.Empty;
                if (!string.IsNullOrEmpty(itemPath)
                    && string.Equals(itemPath, wantedPath, StringComparison.OrdinalIgnoreCase))
                {
                    target = item;
                    break;
                }
            }
            if (target == null)
            {
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.StatusNotInPlanFormat,
                    entry.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemThatFallback));
                return;
            }
            string removedName = target.DisplayName ?? entry.DisplayName
                ?? AppText.T(ApplicationsTextKeys.ItemThatFallback);
            // 【2026-10-17】移除也要能 Ctrl+Z（从扫描结果格子上的 🗑 走的就是这里）。
            ApplicationPlansSnapshot beforeRemove = CaptureApplicationPlansSnapshot();
            if (!controller.RemoveApplicationPlanItem(plan, target))
            {
                statusText.Text = AppText.F(ApplicationsTextKeys.StatusRemoveFailedFormat, removedName);
                return;
            }
            statusText.Text = AppText.F(
                ApplicationsTextKeys.StatusRemovedFormat,
                plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback),
                removedName);
            RefreshApplicationPlanList();
            RefreshDesktopList();
            PushApplicationPlansUndo(
                AppText.F(ApplicationsTextKeys.UndoRemovedFormat, removedName), beforeRemove);
        }

        /// <summary>
        /// 「浏览…」选完文件之后：**直接把选中的项目加进当前自启动方案**。
        ///
        /// 【2026-10-17 第 50 批，用户第二次说“浏览没法添加”】第 49 批把“每次浏览都覆盖候选表”
        /// 改成了追加＋自动勾好，但流程还是两步：先放进扫描结果面板，再点“添加选中的项目”。
        /// 用户选完文件、方案里没有东西，结论自然就是“没法添加”。现在选完**直接进方案**：
        ///  · 没有方案就顺手建一个（名字取 设置 → 自启动应用 → 新自启动方案名）；
        ///  · 已有的同路径项目不会重复加（`AddDesktopEntriesToApplicationPlan` 按路径去重）；
        ///  · 把这个方案的卡片**展开**并滚到能看见的位置，状态栏说清加了几项、跳过几项。
        /// 一次选多个文件（对话框里按住 Ctrl）走的是同一段代码，所以是“多选一次加入”。
        /// </summary>
        private void AddBrowsedTargetsToCurrentApplicationPlan(string[] fileNames)
        {
            List<DesktopEntry> picked = new List<DesktopEntry>();
            if (fileNames != null)
            {
                for (int index = 0; index < fileNames.Length; index++)
                {
                    string path = fileNames[index];
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }
                    picked.Add(CreateBrowsedDesktopEntry(path));
                }
            }
            if (picked.Count == 0)
            {
                return;
            }
            ApplicationPlan plan = controller.ActiveApplicationPlan;
            // 【2026-10-17】浏览/挑文件夹/粘贴路径加进来也要能 Ctrl+Z。
            ApplicationPlansSnapshot beforeBrowsedAdd = CaptureApplicationPlansSnapshot();
            bool created = false;
            if (plan == null)
            {
                string name = GetNewApplicationPlanName();
                plan = controller.CreateApplicationPlan(name);
                created = plan != null;
            }
            if (plan == null)
            {
                statusText.Text = AppText.T(ApplicationsTextKeys.StatusBrowsedCreateFailed);
                return;
            }
            // 先算一下“会真正加进去几条”（控制器内部按路径去重，规则一样），
            // 这样状态栏的结论能连“跳过几个重复的”一起说清；结论交给控制器一起写，
            // 否则会被它自己那句笼统的话覆盖掉（第 50 批踩过）。
            int willAdd = 0;
            for (int index = 0; index < picked.Count; index++)
            {
                if (!ApplicationPlanHasPath(plan, picked[index].Path))
                {
                    willAdd++;
                }
            }
            int skipped = picked.Count - willAdd;
            string planName = plan.Name ?? AppText.T(ApplicationsTextKeys.PlanNameFallback);
            string successStatus = willAdd > 0
                ? AppText.F(
                    created
                        ? ApplicationsTextKeys.StatusBrowsedAddedNewPlanFormat
                        : ApplicationsTextKeys.StatusBrowsedAddedFormat,
                    willAdd,
                    planName,
                    skipped > 0
                        ? AppText.F(ApplicationsTextKeys.StatusBrowsedAddedSkippedFormat, skipped)
                        : string.Empty)
                : null;
            int added = controller.AddDesktopEntriesToApplicationPlan(
                plan,
                picked,
                successStatus,
                AppText.F(
                    ApplicationsTextKeys.StatusAllAlreadyInPlanFormat,
                    picked.Count,
                    planName));
            if (added > 0 && !string.IsNullOrWhiteSpace(plan.Id))
            {
                // 加完就展开这张卡片，否则用户看不到新加的行（默认是收起的）。
                applicationPlanExpansionStates[plan.Id] = true;
                controller.SetApplicationPlanExpanded(plan.Id, true);
            }
            RefreshApplicationPlanList();
            RefreshDesktopList();
            if (added > 0)
            {
                PushApplicationPlansUndo(
                    AppText.F(ApplicationsTextKeys.UndoBrowsedAddedFormat, added), beforeBrowsedAdd);
            }
            // 卡片重建后要等一次布局，滚动才滚得到底；用后台优先级排队，不打断当前输入。
            Dispatcher.BeginInvoke(
                new Action(delegate
                {
                    if (desktopPlanScroll != null)
                    {
                        desktopPlanScroll.ScrollToEnd();
                    }
                }),
                DispatcherPriority.Background);
        }

        /// <summary>把浏览选中/粘贴进来的路径包成一个桌面项候选（.lnk / .url 去掉扩展名当显示名）。</summary>
        private static DesktopEntry CreateBrowsedDesktopEntry(string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            bool shortcut = string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase);
            string displayName = shortcut
                ? System.IO.Path.GetFileNameWithoutExtension(path)
                : System.IO.Path.GetFileName(path);
            // 网址用它自己的主机名当显示名（GetFileName 对 "https://x.com/a" 只会给出 "a" 甚至空串）。
            Uri uri;
            if (Uri.TryCreate(path, UriKind.Absolute, out uri)
                && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "ftp", StringComparison.OrdinalIgnoreCase)))
            {
                displayName = uri.Host;
                extension = string.Empty;
                shortcut = false;
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = path;
            }
            return new DesktopEntry
            {
                DisplayName = displayName,
                Path = path,
                Extension = extension ?? string.Empty,
                Source = DesktopEntrySource.CurrentUser,
                IsShortcut = shortcut,
                IsSelected = false
            };
        }

        /// <summary>打开一个桌面项（与原来表格里的"打开"按钮行为一致）。</summary>
        private void OpenDesktopEntry(DesktopEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Path))
            {
                return;
            }
            try
            {
                // 测试/自检模式由 ShellOpener 统一抑制，不再在页面里各写一份判断。
                ShellOpenResult openResult = ShellOpener.Open(entry.Path);
                if (openResult.WasSuppressed)
                {
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusTestOpenFormat,
                        entry.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemThisFallback));
                    WorkspaceRestorer.Infrastructure.AppLog.Write(
                        "[跳转] 测试模式：不真的打开（" + (entry.DisplayName ?? "这一项") + "）");
                    return;
                }
                if (!openResult.Success)
                {
                    statusText.Text = AppText.F(
                        ApplicationsTextKeys.StatusOpenFailedFormat,
                        entry.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemThatFallback),
                        openResult.Error);
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    ApplicationsTextKeys.StatusOpenFailedFormat,
                    entry.DisplayName ?? AppText.T(ApplicationsTextKeys.ItemThatFallback),
                    exception.Message);
            }
        }

        /// <summary>
        /// “刚刚拖完一行”的时间：拖完松手那一下不能当成点击（否则拖完会顺手打开项目）。
        /// 只在一个很短的窗口内有效，避免在空白处松手（没有行收到 MouseUp）后把下一次正常点击也吞掉。
        /// </summary>
        private DateTime applicationRowDragEndedUtc = DateTime.MinValue;

        /// <summary>拖动刚结束后，紧跟着的那一次点击不执行跳转/打开；超时自动失效。</summary>
        private static readonly TimeSpan ApplicationRowDragClickGrace =
            TimeSpan.FromMilliseconds(250);

        /// <summary>“刚刚拖完一行”的标记：只在松手后的 250ms 内消费一次。</summary>
        private bool ConsumeApplicationRowDragJustEnded()
        {
            if (applicationRowDragEndedUtc == DateTime.MinValue)
            {
                return false;
            }
            TimeSpan elapsed = DateTime.UtcNow - applicationRowDragEndedUtc;
            // 无论是否命中都清掉：过期后继续留着没有任何意义。
            applicationRowDragEndedUtc = DateTime.MinValue;
            return elapsed >= TimeSpan.Zero && elapsed <= ApplicationRowDragClickGrace;
        }



    }
}
