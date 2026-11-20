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
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.UI.Cards.Drag;
using WorkspaceRestorer.UI.Cards.Interaction;
using WorkspaceRestorer.UI.Cards.Actions;
namespace WorkspaceRestorer.UI
{
    // 工作区：启动与恢复 —— 页面与工作方案/条目清单
    // （从 MainWindow.cs 拆分出来，便于各自维护；同为 partial class MainWindow。）
    public sealed partial class MainWindow
    {
        /// <summary>
        /// 方案卡片的标题文本，按方案 Id 索引：改名时就地改字，不重建整个列表。
        /// </summary>
        private readonly Dictionary<string, TextBlock> profileTitleBlocks =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>方案卡片的条目数文本（"N 个启动项目"），同样用于就地更新。</summary>
        private readonly Dictionary<string, TextBlock> profileCountBlocks =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>清单行的"是否启动此项目"勾选框，按 方案Id|项目Id 索引：撤销/重做时就地同步。</summary>
        private readonly Dictionary<string, CheckBox> itemEnabledCheckBoxes =
            new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        /// <summary>清单行的“恢复时最小化”开关，按 方案Id|项目Id 索引：撤销/重做时就地同步。</summary>
        private readonly Dictionary<string, Button> itemMinimizeButtons =
            new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);

        /// <summary>正在就地同步勾选框：此时不要把它当成用户操作（否则会重复压撤销栈或递归）。</summary>
        private bool syncingItemEnabledState;

        /// <summary>
        /// 上一次重建时的"列表结构签名"（方案 Id 顺序 + 每个方案的条目数 + 当前方案标记）。
        /// 结构没变就只就地更新文字，不重建卡片行——重建会让整个列表闪一下，
        /// 改名、勾选、撤销这类"内容没变"的操作不该付这个代价。
        /// </summary>
        private string profileStructureSignature;

        private Border BuildStartupWorkspace()
        {
            Grid content = new Grid();
            // Right-click anywhere in this workspace's blank area offers the
            // paste-a-path/URL add action for the active profile.
            content.ContextMenu = CreateBlankContextMenu();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            StackPanel heading = new StackPanel();
            // 【2026-11-15 用户要求】大标题下面只留一句话 + 问号，旧的长说明收进问号；
            // 这样“当前方案已保存…”能紧跟着标题往上提，不再被两三行说明顶下去。
            heading.Children.Add(UiFactory.HeadingWithHintKey(
                StartupRestoreTextKeys.PageTitle,
                StartupRestoreTextKeys.PageShort,
                StartupRestoreTextKeys.PageSubtitle,
                18));
            // 【2026-10-19 用户反馈】“提示字并不连贯”：方案摘要必须跟标题/说明连在一起，
            // 不能为了填右边的空白把它甩到按钮那一行的最右边。这里放回标题下面。
            heading.Children.Add(sessionSummary);

            WrapPanel commands = UiFactory.PrimaryActionRow();
            Button captureButton = UiFactory.CommandButtonKey("\uE74E", StartupRestoreTextKeys.ActionCapture, true);
            Button restoreButton = UiFactory.CommandButtonKey("\uE777", StartupRestoreTextKeys.ActionRestore, false);
            captureButton.Click += delegate { SaveCurrentWork(); };
            restoreButton.Click += delegate { RestoreCurrentSessionAsync(); };
            AccessibleToolTip.Attach(captureButton, StartupRestoreToolTips.CaptureCurrent);
            AccessibleToolTip.Attach(restoreButton, StartupRestoreToolTips.RestoreNow);
            UiFactory.AddPrimaryAction(commands, captureButton);
            UiFactory.AddPrimaryAction(commands, restoreButton);
            // 【2026-10-19 用户要求】这里原来的「取消恢复」按钮删掉了：
            // “将启动与恢复，自启动应用内的按钮「取消恢复」删了，相关的按钮代码也去掉，但功能保存，
            //  因为去掉恢复的按钮根本用不上，都是点击恢复后，在提示窗口都有一个取消恢复，
            //  导致按钮的功能重复了且无用。”
            // 取消功能本身没动：恢复一开始就会弹出恢复卡片（右下角那块），卡片上自带「取消恢复」，
            // 调的是 controller.CancelRestore()（自启动应用那边是 CancelApplicationsStart()）。
            // 宽窗口：标题+说明+摘要连成一块在左，主按钮靠右；窄窗口：按钮自动落到标题下面。
            // 按钮和提示文字都保持“一整块”，不再为了铺满宽度把一句话拆到两个角落。
            sessionSummary.VerticalAlignment = VerticalAlignment.Center;
            sessionSummary.TextWrapping = TextWrapping.Wrap;
            // 【2026-10-19 用户要求】主按钮固定在“标题 + 小字说明”下面，不再跑到右上角；
            // 窗口缩小时小字可以被裁，但这两个按钮的位置不变。
            StackPanel headerBlock = new StackPanel();
            headerBlock.Children.Add(heading);
            headerBlock.Children.Add(commands);
            Grid.SetRow(headerBlock, 0);
            content.Children.Add(headerBlock);

            Grid profileArea = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            profileArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            profileArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            // 列表下面留一块固定的白色空白区：它是真实布局的一部分（所以不用浮层
            // 也能看见），拖动时按住这里列表会向下滚，松手＝把条目移到最末尾。
            profileArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // 用户要求（2026-11-18）：命名框搬进设置后，搜索和追加时间单独留在第二行，
            // 不要并进上面的“保存/立即恢复”那一行。
            WrapPanel secondaryCommands = UiFactory.Toolbar();
            secondaryCommands.Margin = new Thickness(0);
            Button searchProfileButton = UiFactory.CommandButtonKey("\uE721", StartupRestoreTextKeys.ActionSearch, false);
            searchProfileButton.Margin = new Thickness(0, 0, 4, 8);
            searchProfileButton.Click += delegate { SearchProfiles(); };
            AccessibleToolTip.Attach(searchProfileButton, StartupRestoreToolTips.Search);
            secondaryCommands.Children.Add(searchProfileButton);
            Button appendTimeButton = UiFactory.CommandButtonKey("\uE823", StartupRestoreTextKeys.ActionAppendTime, false);
            appendTimeButton.Margin = new Thickness(0, 0, 4, 8);
            appendTimeButton.Click += delegate { AppendCurrentTimeToProfileName(); };
            AccessibleToolTip.Attach(appendTimeButton, StartupRestoreToolTips.AppendTime);
            secondaryCommands.Children.Add(appendTimeButton);
            Grid.SetRow(secondaryCommands, 0);
            profileArea.Children.Add(secondaryCommands);

            // 方案清单。
            ScrollViewer profileScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 12, 0, 0),
                Focusable = true,
                FocusVisualStyle = null
            };
            profileScrollViewer = profileScroll;
            profileScroll.Content = profileHost;
            Grid.SetRow(profileScroll, 1);
            profileArea.Children.Add(profileScroll);

            profileDropPad = DragDropPadFactory.CreateBottom(
                AppText.T(StartupRestoreTextKeys.PageDropPadBottom));
            profileDropPad.VerticalAlignment = VerticalAlignment.Bottom;
            // 列表本身有 12 DIP 上边距；把顶部带子下沿贴到列表顶边，避免中间留 12 DIP 死区。
            profileDropPad.Margin = new Thickness(6, 0, 6, 0);
            AccessibleToolTip.Attach(profileDropPad, StartupRestoreToolTips.DropPadBottom);
            Grid.SetRow(profileDropPad, 2);
            profileArea.Children.Add(profileDropPad);

            // 与底部白区完全对称的"顶部放置区"：拖到这里列表向上滚，松手移到清单最前面。
            profileDropPadTop = DragDropPadFactory.CreateTop(
                AppText.T(StartupRestoreTextKeys.PageDropPadTop));
            // CreateTop 的默认负边距是 -64，但列表还有 12 DIP 上边距；改成 -52，
            // 让带子的下沿正好贴住列表顶边，显示区和生效区完全一致。
            profileDropPadTop.Margin = new Thickness(6, -(DragScrollZone.ZoneHeight - 12.0), 6, 0);
            profileDropPadTop.Opacity = 1.0;
            AccessibleToolTip.Attach(profileDropPadTop, StartupRestoreToolTips.DropPadTop);
            Grid.SetRow(profileDropPadTop, 1);
            profileArea.Children.Add(profileDropPadTop);

            Grid.SetRow(profileArea, 2);
            content.Children.Add(profileArea);
            return WorkspaceBorder(content);
        }

        private async Task<bool> CreateStartupProfileAsync()
        {
            if (saveOperationInProgress)
            {
                return false;
            }
            saveOperationInProgress = true;
            try
            {
                // 名字来自 设置 → 启动与恢复 → 新工作方案名（没自定义时用默认名）。
                string name = GetNewStartupProfileName();
                // 重名处理：跟 Windows 新建文件夹一样，名字被占用就顺延成“名字（2）”“名字（3）”，
                // 不覆盖、也不出现两个同名方案。
                name = MakeUniqueProfileName(name);
                SessionSnapshot snapshot = await controller.CaptureSnapshotForNewProfileAsync();
                if (snapshot == null)
                {
                    return false;
                }

                // 2026-10-23 用户要求：工作方案和图标位置保存必须彻底分开。
                // “保存当前方案”只保存应用/文件夹/网页，不再顺带 CaptureDesktopLayoutAsync，
                // 否则会自动生成/更新「图标位置保存」里的布局文件夹。
                // SaveStartupProfile copies the captured ordered items into the
                // newly named folder.  Creating an empty profile first would leave
                // the visible folder with no startup entries.
                StartupProfile profile = controller.SaveStartupProfile(name, snapshot);
                if (profile == null)
                {
                    return false;
                }
                // 重名顺延只影响真正存下来的那份方案叫什么（见上面 MakeUniqueProfileName），
                // 设置里的名字保持不变。
                RefreshProfileList();
                return true;
            }
            finally
            {
                saveOperationInProgress = false;
            }
        }

        /// <summary>没有指定名字时用的基础名（重名时自动变成“工作方案（2）”）。</summary>
        private static string DefaultProfileName
        {
            get { return AppText.T(CommonTextKeys.WorkProfileFallback); }
        }

        /// <summary>结尾的“（N）”序号，用于把“名字（2）”拆成“名字”+ 2。</summary>
        private static readonly System.Text.RegularExpressions.Regex ProfileNumberedSuffix =
            new System.Text.RegularExpressions.Regex(
                @"^(.*?)（(\d+)）$",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Windows 文件夹式的唯一命名：想要的名字已被占用时，依次尝试“名字（2）”“名字（3）”……；
        /// 如果名字本身就以“（N）”结尾，就从 N+1 接着试，不会出现“名字（2）（2）”。
        /// </summary>
        private string MakeUniqueProfileName(string desired)
        {
            string baseName = string.IsNullOrWhiteSpace(desired) ? DefaultProfileName : desired.Trim();
            if (!ProfileNameExists(baseName))
            {
                return baseName;
            }
            int next = 2;
            System.Text.RegularExpressions.Match numbered = ProfileNumberedSuffix.Match(baseName);
            if (numbered.Success)
            {
                int parsed;
                if (int.TryParse(numbered.Groups[2].Value, out parsed) && parsed >= 1)
                {
                    baseName = numbered.Groups[1].Value.Trim();
                    next = parsed + 1;
                }
            }
            for (int number = next; number < next + 1000; number++)
            {
                string candidate = baseName + "（" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "）";
                if (!ProfileNameExists(candidate))
                {
                    return candidate;
                }
            }
            return baseName + "（" + Guid.NewGuid().ToString("N").Substring(0, 4) + "）";
        }

        /// <summary>是否已经有同名方案（忽略大小写与首尾空格）。</summary>
        private bool ProfileNameExists(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }
            IList<StartupProfile> profiles = controller.Profiles;
            if (profiles == null)
            {
                return false;
            }
            for (int index = 0; index < profiles.Count; index++)
            {
                StartupProfile profile = profiles[index];
                if (profile != null
                    && string.Equals((profile.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>改一个方案的名字（并把输入框、清单与状态栏同步好）。</summary>
        private void ApplyProfileName(StartupProfile profile, string name)
        {
            if (profile == null)
            {
                return;
            }
            controller.RenameProfile(profile, name);
            // 就地改标题，不重建整个列表：这条路径由"重命名/追加时间的撤销、重做"调用，
            // 每次重建会让界面闪一下，Ctrl+Z 连按尤其明显。
            UpdateProfileTitleInPlace(profile);
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.CardNamedFormat,
                profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
        }

        private void SelectProfile(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            // 【2026-10-24】所有“立即选择”的入口（标题、计数区、整张卡片、搜索结果……）
            // 都必须先取消上一张卡片的 260ms 延迟选择。以前这些入口各自漏了取消，
            // 快速点“甲名字 → 乙计数区”时，乙先选中，随后甲的定时器又把当前方案切回甲。
            CancelPendingProfileSelect();
            // 选中子方案时展开所有祖先，保证它真的露出来。
            List<StartupProfile> ancestors = PlanFolderTree.Ancestors(controller.StartupProfiles, profile);
            for (int index = 0; index < ancestors.Count; index++)
            {
                if (ancestors[index] != null && !string.IsNullOrWhiteSpace(ancestors[index].Id))
                {
                    profileExpansionStates[ancestors[index].Id] = true;
                }
            }
            controller.SetActiveProfile(profile);
            RefreshProfileList();
        }

        private void ScheduleProfileSelect(StartupProfile profile)
        {
            // A single click switches, but a double click must rename without
            // switching, so the switch waits briefly for a possible second click.
            CancelPendingProfileSelect();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            pendingProfileSelectTimer = timer;
            timer.Tick += delegate
            {
                timer.Stop();
                if (object.ReferenceEquals(pendingProfileSelectTimer, timer))
                {
                    pendingProfileSelectTimer = null;
                }
                SelectProfile(profile);
            };
            timer.Start();
        }

        private void CancelPendingProfileSelect()
        {
            if (pendingProfileSelectTimer != null)
            {
                pendingProfileSelectTimer.Stop();
                pendingProfileSelectTimer = null;
            }
        }

        internal bool CanSaveActiveProfileFromTray()
        {
            StartupProfile active = controller.ActiveProfile;
            return active != null
                && !active.IsFolder
                && !saveOperationInProgress
                && !controller.IsRestoring
                && !controller.IsRestoringDesktopLayout;
        }

        internal void SaveActiveProfileFromTray()
        {
            if (!CanSaveActiveProfileFromTray())
            {
                return;
            }
            SaveProfile(controller.ActiveProfile);
        }

        private async void SaveProfile(StartupProfile profile)
        {
            if (profile == null || saveOperationInProgress)
            {
                return;
            }
            saveOperationInProgress = true;
            try
            {
                SessionSnapshot snapshot = await controller.SaveCurrentWorkToProfileAsync(profile);
                if (snapshot != null)
                {
                    // 工作方案保存不再连带保存桌面图标布局；
                    // 布局只能由「图标位置保存 → 保存图标布局」显式触发。
                    RefreshProfileList();
                }
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.StatusCaptureFailedFormat, exception.Message);
            }
            finally
            {
                saveOperationInProgress = false;
            }
        }

        private void RestoreProfile(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            RestoreProfileAsync(profile);
        }

        private async void RestoreProfileAsync(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            try
            {
                await controller.RestoreProfileAsync(profile, false);
                if (!IsLoaded)
                {
                    return;
                }
                SelectProfile(profile);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.StatusRestoreFailedFormat, exception.Message);
            }
        }

        private async void RestoreCurrentSessionAsync()
        {
            try
            {
                await controller.RestoreAsync(false);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.StatusRestoreSessionFailedFormat, exception.Message);
                AppLog.Write("Unable to restore the current session from the main window.", exception);
            }
        }

        /// <summary>
        /// 把一份方案移进另一份，变成它的子方案（文件夹形式）。
        /// 不再复制启动项、不再把来源方案放进回收站：来源方案和它的整棵子树原样保留。
        /// </summary>
        private void MergeProfileInto(string sourceId, StartupProfile target)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || target == null)
            {
                return;
            }
            StartupProfile source = null;
            for (int index = 0; index < controller.Profiles.Count; index++)
            {
                StartupProfile candidate = controller.Profiles[index];
                if (candidate != null && string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase))
                {
                    source = candidate;
                    break;
                }
            }
            if (source == null || object.ReferenceEquals(source, target))
            {
                return;
            }
            string oldParentId = source.ParentId;
            int oldIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, source);
            if (!controller.MoveProfileInto(source, target))
            {
                return;
            }
            string newParentId = source.ParentId;
            int newIndex = PlanFolderTree.SiblingIndex(controller.StartupProfiles, source);
            StartupProfile sourceForUndo = source;
            profileExpansionStates[target.Id] = true;
            RefreshProfileList();
            string sourceName = source.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
            string targetName = target.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
            PushUndo(
                AppText.F(StartupRestoreTextKeys.CardUndoMovedIntoFormat, sourceName, targetName),
                delegate
                {
                    controller.MoveProfileToPlacement(sourceForUndo, oldParentId, oldIndex);
                    RefreshProfileList();
                },
                delegate
                {
                    controller.MoveProfileToPlacement(sourceForUndo, newParentId, newIndex);
                    RefreshProfileList();
                });
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.CardMovedIntoFormat, sourceName, targetName);
        }

        /// <summary>在某个方案下面新建子方案/纯文件夹（右键菜单）。</summary>
        private void CreateChildProfileWithPrompt(StartupProfile parent, bool isFolder)
        {
            if (parent == null)
            {
                return;
            }
            string title = isFolder
                ? AppText.T(StartupRestoreTextKeys.CardNewChildTitleFolder)
                : AppText.T(StartupRestoreTextKeys.CardNewChildTitlePlan);
            string name = WorkspaceRestorer.UI.Shared.NamePromptDialog.Ask(
                Window.GetWindow(this),
                title,
                AppText.F(
                    StartupRestoreTextKeys.CardNewChildPromptFormat,
                    parent.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback)),
                isFolder
                    ? AppText.T(StartupRestoreTextKeys.CardNewChildOkFolder)
                    : AppText.T(StartupRestoreTextKeys.CardNewChildOkPlan));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }
            StartupProfile created = controller.CreateProfile(name, parent, isFolder);
            if (created == null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(parent.Id))
            {
                profileExpansionStates[parent.Id] = true;
            }
            SelectProfile(created);
            statusText.Text = (isFolder
                ? AppText.F(StartupRestoreTextKeys.CardChildCreatedFolderFormat, created.Name)
                : AppText.F(StartupRestoreTextKeys.CardChildCreatedPlanFormat, created.Name));
        }

        private void DeleteProfile(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            int subtreeCount = PlanFolderTree.Subtree(controller.StartupProfiles, profile).Count;
            string retention = controller.RecycleBinRetentionDescription;
            string detail = subtreeCount > 1
                ? AppText.F(
                    StartupRestoreTextKeys.CardDeleteDetailWithChildrenFormat,
                    subtreeCount - 1,
                    retention)
                : AppText.F(StartupRestoreTextKeys.CardDeleteDetailFormat, retention);
            // 公共确认框：「是」在左、「否」在右，右上角 × = 否（用户第 52、53 条）。
            if (WorkspaceRestorer.UI.Shared.ConfirmDialog.Ask(
                Window.GetWindow(this),
                AppText.T(StartupRestoreTextKeys.CardDeleteTitle),
                AppText.F(
                    StartupRestoreTextKeys.CardDeletePromptFormat,
                    profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback)),
                detail))
            {
                controller.DeleteProfile(profile);
                if (!string.IsNullOrWhiteSpace(profile.Id))
                {
                    profileExpansionStates.Remove(profile.Id);
                    RemoveItemExpansionStates(profile.Id);
                }
                RemoveBrowserExpansionStates(profile);
                RefreshProfileList();
            }
        }

        private void RemoveProfileItem(StartupProfile profile, SessionItem item)
        {
            if (profile == null || item == null)
            {
                return;
            }
            // 【2026-11-01 用户要求】删除清单里的一个小项不再弹确认框：
            // 这个「删除启动项」确认框只属于“删除整份工作方案”（见上面的 DeleteProfile）。
            // 小项删除仍然进撤销栈，误删按 Ctrl+Z 就能原样回来。
            // 【2026-10-17 修】这里原来只有一个确认框，确认之后**没有任何代码** ——
            // 于是「删除启动项目」是个死按钮（点「是」也不删）；「自启动应用」页里
            // “从当前方案移除”也走这个方法，同样不生效。真正的删除在控制器里
            // （ApplicationController.RemoveProfileItem：移除条目 + 记排除键 + 持久化），
            // 这里补上调用，并把“条目清单 + 排除键”两个方向都记进撤销栈。
            List<SessionItem> before = SnapshotItems(profile);
            List<string> excludedBefore = profile.ExcludedItemKeys == null
                ? new List<string>()
                : new List<string>(profile.ExcludedItemKeys);
            if (!controller.RemoveProfileItem(profile, item))
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.CardGoneFormat,
                    profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
                return;
            }
            List<SessionItem> after = SnapshotItems(profile);
            List<string> excludedAfter = profile.ExcludedItemKeys == null
                ? new List<string>()
                : new List<string>(profile.ExcludedItemKeys);
            System.Action<List<SessionItem>, List<string>> apply =
                delegate(List<SessionItem> items, List<string> excluded)
                {
                    // 排除键必须跟着一起还原：否则撤销回来的项目在下一次
                    // “保存当前方案”时又被排除键过滤掉，看着就像撤销没生效。
                    profile.ExcludedItemKeys = new List<string>(excluded);
                    controller.SetProfileItemsOrder(profile, items);
                    RefreshProfileList();
                };
            PushUndo(
                AppText.T(StartupRestoreTextKeys.CardUndoDeleteItem),
                delegate { apply(before, excludedBefore); },
                delegate { apply(after, excludedAfter); });
            RefreshProfileList();
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.CardDeletedItemFormat,
                profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback),
                SessionItemDisplayName(item));
        }

        private void RefreshProfileList()
        {
            if (profileHost == null)
            {
                return;
            }
            if (profileRefreshActive)
            {
                // 重建期间属性又变了：记一笔，等当前重建结束补跑一次（多次请求合并成一次）。
                profileRefreshQueued = true;
                return;
            }
            profileRefreshActive = true;
            try
            {
            // Rebuilding the visual rows is intentional because profiles and
            // their item lists are persisted POCOs.  Keep expansion in the
            // window instead of deriving it from ActiveProfile; otherwise a
            // property refresh would unexpectedly reopen a collapsed folder.
            HashSet<string> currentProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (controller.StartupProfiles != null)
            {
                for (int index = 0; index < controller.StartupProfiles.Count; index++)
                {
                    StartupProfile profile = controller.StartupProfiles[index];
                    if (profile != null && !string.IsNullOrWhiteSpace(profile.Id))
                    {
                        currentProfileIds.Add(profile.Id);
                    }
                }
            }
            List<string> expansionIds = profileExpansionStates.Keys.ToList();
            for (int index = 0; index < expansionIds.Count; index++)
            {
                if (!currentProfileIds.Contains(expansionIds[index]))
                {
                    profileExpansionStates.Remove(expansionIds[index]);
                }
            }

            HashSet<string> currentItemExpansionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int profileIndex = 0; profileIndex < (controller.StartupProfiles == null ? 0 : controller.StartupProfiles.Count); profileIndex++)
            {
                StartupProfile profile = controller.StartupProfiles[profileIndex];
                if (profile == null || profile.Items == null)
                {
                    continue;
                }
                for (int itemIndex = 0; itemIndex < profile.Items.Count; itemIndex++)
                {
                    SessionItem item = profile.Items[itemIndex];
                    if (item != null && !string.IsNullOrWhiteSpace(profile.Id) && !string.IsNullOrWhiteSpace(item.Id))
                    {
                        currentItemExpansionKeys.Add(ItemExpansionKey(profile, item));
                    }
                }
            }
            List<string> itemExpansionIds = browserExpansionStates.Keys.ToList();
            for (int index = 0; index < itemExpansionIds.Count; index++)
            {
                if (!currentItemExpansionKeys.Contains(itemExpansionIds[index]))
                {
                    browserExpansionStates.Remove(itemExpansionIds[index]);
                }
            }

            // 结构没变（方案集合、每张卡片的条目数、当前方案都没变）就只就地更新文字：
            // 改名、勾选/取消勾选、追加时间这类操作都会走到这里，重建整表只会让界面闪。
            string signature = BuildProfileStructureSignature();
            if (profileTitleBlocks.Count > 0
                && string.Equals(signature, profileStructureSignature, StringComparison.Ordinal))
            {
                UpdateProfileTextsInPlace();
                return;
            }
            profileStructureSignature = signature;

            double scrollOffset = profileScrollViewer == null ? 0.0 : profileScrollViewer.VerticalOffset;
            profileTitleBlocks.Clear();
            profileCountBlocks.Clear();
            startupCardElements.Clear();
            itemEnabledCheckBoxes.Clear();
            itemMinimizeButtons.Clear();
            // 【2026-10-24】这些登记表也必须跟着整表重建一起清：
            // 旧 NativeItemRow 若不清理，FindNativeItemRowForItem 可能返回已经脱离 visual tree 的父行，
            // 拖顶层项目到子项上时就会拿旧行画插入线/算落点。
            nativeItemRows.Clear();
            profileHost.Children.Clear();
            IList<StartupProfile> rootProfiles = controller.RootProfiles;
            if (rootProfiles == null || rootProfiles.Count == 0)
            {
                Border empty = new Border
                {
                    Background = AppTheme.PanelSurface,
                    BorderBrush = AppTheme.Border,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                StackPanel emptyContent = new StackPanel();
                emptyContent.Children.Add(UiFactory.HeadingKey(StartupRestoreTextKeys.PageEmptyHeading, 14));
                // 【2026-11-18】命名框搬进设置后，真实入口只剩两个：
                //   ① 顶栏「保存当前方案」（按 设置 → 启动与恢复 → 新工作方案名 另存为新方案）；
                //   ② 空白处右键「新建空白方案」（同样用设置里的名字）。
                emptyContent.Children.Add(UiFactory.SupportingTextKey(StartupRestoreTextKeys.PageEmptyHint));
                empty.Child = emptyContent;
                profileHost.Children.Add(empty);
                return;
            }

            for (int index = 0; index < rootProfiles.Count; index++)
            {
                StartupProfile profile = rootProfiles[index];
                if (profile != null)
                {
                    profileHost.Children.Add(BuildProfileRow(profile));
                }
            }

            RestoreProfileScrollOffset(scrollOffset);
            }
            finally
            {
                profileRefreshActive = false;
                if (profileRefreshQueued)
                {
                    profileRefreshQueued = false;
                    // 用 Background 排到当前事件之后，避免在 finally 里递归重建。
                    if (IsLoaded && Dispatcher != null && !Dispatcher.HasShutdownStarted)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshProfileList));
                    }
                }
            }
        }

        /// <summary>
        /// 列表结构签名：方案 Id 顺序 + 每个方案的**条目顺序** + 条目数 + 当前方案标记。
        ///
        /// 【2026-10-16 修的真 bug —— 用户：“拖动时，到了我想要的位置，松开鼠标，发现没能到达那个位置”】
        /// 原来这里的签名只有 `方案Id|条目数|index`，**不含条目顺序**。
        /// 而“拖动排序”恰好是**只改顺序、不改数量**的操作 → 签名前后一模一样 →
        /// 下面的 `RefreshProfileList` 就走进“结构没变，只就地改文字”那条路（`UpdateProfileTextsInPlace`），
        /// **一行控件都不重建** → 数据（工作方案.json）已经排好序了，屏幕却永远停在旧顺序。
        /// 用户看到的就是“松手以后东西没到我拖到的位置”——因为界面上它确实**没动**。
        /// 更糟的是：屏幕顺序与内存顺序从此错开，之后再拖，落点索引是按内存算的，
        /// 用户却是照着屏幕瞄的 → 越拖越偏。
        /// 所以签名必须把 `父Id + 条目Id 的顺序` 也算进去：改名、勾选不会变（仍走就地更新，不闪），
        /// 只有真正的结构调整（增删条目、调整顺序、换分组、换当前方案）才触发整表重建。
        /// </summary>
        private string BuildProfileStructureSignature()
        {
            IList<StartupProfile> profiles = controller.StartupProfiles;
            if (profiles == null || profiles.Count == 0)
            {
                return "<empty>";
            }
            System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
            System.Text.StringBuilder order = new System.Text.StringBuilder();
            for (int index = 0; index < profiles.Count; index++)
            {
                StartupProfile profile = profiles[index];
                if (profile == null)
                {
                    continue;
                }
                order.Length = 0;
                if (profile.Items != null)
                {
                    for (int itemIndex = 0; itemIndex < profile.Items.Count; itemIndex++)
                    {
                        SessionItem item = profile.Items[itemIndex];
                        if (item == null)
                        {
                            order.Append("-,");
                            continue;
                        }
                        order.Append(item.Id ?? "-").Append('^').Append(item.ParentId ?? "-").Append(',');
                    }
                }
                parts.Add((profile.Id ?? string.Empty) + "|" + (profile.ParentId ?? string.Empty)
                    + "|" + (profile.IsFolder ? "folder" : "plan") + "|"
                    + (profile.Items == null ? 0 : profile.Items.Count) + "|" + index
                    + "|" + order.ToString());
            }
            StartupProfile active = controller.ActiveProfile;
            parts.Add("active=" + (active == null ? string.Empty : (active.Id ?? string.Empty)));
            return string.Join(";", parts.ToArray());
        }

        /// <summary>结构没变时：只刷新每张卡片的方案名与条目数，不重建任何控件。</summary>
        private void UpdateProfileTextsInPlace()
        {
            IList<StartupProfile> profiles = controller.StartupProfiles;
            if (profiles == null)
            {
                return;
            }
            for (int index = 0; index < profiles.Count; index++)
            {
                RefreshProfileTitle(profiles[index], true);
            }
        }

        /// <summary>就地刷新一张卡片的标题（可选连条目数一起刷新）。</summary>
        private void RefreshProfileTitle(StartupProfile profile, bool includeItemCount)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
            {
                return;
            }
            string name = string.IsNullOrWhiteSpace(profile.Name)
                ? DefaultProfileName
                : profile.Name;
            TextBlock titleBlock;
            if (profileTitleBlocks.TryGetValue(profile.Id, out titleBlock)
                && titleBlock != null
                && !string.Equals(titleBlock.Text, name, StringComparison.Ordinal))
            {
                titleBlock.Text = name;
            }
            if (!includeItemCount)
            {
                return;
            }
            TextBlock countBlock;
            if (profileCountBlocks.TryGetValue(profile.Id, out countBlock) && countBlock != null)
            {
                string count = StartupProfileCardText.BuildCountText(
                    profile,
                    controller.ChildProfiles(profile).Count,
                    AppText.CurrentLanguageTag);
                if (!string.Equals(countBlock.Text, count, StringComparison.Ordinal))
                {
                    countBlock.Text = count;
                }
                // 与建卡片时保持一致：只更新提示/帮助文本，不覆盖无障碍名字（见 BuildProfileRow 里的说明）。
                AccessibleToolTip.AttachDescription(
                    countBlock,
                    StartupRestoreToolTips.ProfileCount(StartupProfileCardText.BuildCountToolTip(
                        profile, AppText.CurrentLanguageTag)));
            }
        }

        /// <summary>
        /// 改名后只更新对应卡片的标题，不重建列表（重建会闪屏）。
        /// 设置里的“新工作方案名”不受影响：那个名字只管以后新建的方案。
        /// </summary>
        private void UpdateProfileTitleInPlace(StartupProfile profile)
        {
            RefreshProfileTitle(profile, false);
        }

        /// <summary>清单行勾选框的索引键（方案 Id + 项目 Id）。</summary>
        private static string ItemEnabledKey(StartupProfile profile, SessionItem item)
        {
            string profileId = profile == null || string.IsNullOrWhiteSpace(profile.Id) ? string.Empty : profile.Id;
            string itemId = item == null || string.IsNullOrWhiteSpace(item.Id) ? string.Empty : item.Id;
            return profileId + "|" + itemId;
        }

        /// <summary>撤销/重做启停时只就地改那一个勾选框，避免整表重建。</summary>
        private void SyncItemEnabledCheckBox(StartupProfile profile, SessionItem item)
        {
            if (item == null)
            {
                return;
            }
            CheckBox box;
            if (!itemEnabledCheckBoxes.TryGetValue(ItemEnabledKey(profile, item), out box) || box == null)
            {
                return;
            }
            bool shouldCheck = item.IsEnabled;
            if (box.IsChecked == shouldCheck)
            {
                return;
            }
            syncingItemEnabledState = true;
            try
            {
                box.IsChecked = shouldCheck;
            }
            finally
            {
                syncingItemEnabledState = false;
            }
        }

        /// <summary>重建后把滚动位置放回去（不恢复到一半就跳走，减少视觉抖动）。</summary>
        private void RestoreProfileScrollOffset(double offset)
        {
            if (profileScrollViewer == null || offset <= 0.0)
            {
                return;
            }
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(delegate
                {
                    if (profileScrollViewer != null)
                    {
                        profileScrollViewer.ScrollToVerticalOffset(offset);
                    }
                }));
        }

        private static bool IsItemMinimized(SessionItem item)
        {
            return item != null && WindowMinimizeState.IsMinimized(item.WindowPlacement);
        }

        /// <summary>
        /// 行里显示“最小化”开关的条件：必须有独立窗口。
        /// 文件夹/浏览器这类“一个窗口里多个页面”的条目，开关放在窗口父行上；
        /// 子页面/子文件夹跟随父窗口，不能单独最小化，否则合并/拖动后状态会互相打架。
        /// 网页行本身没有独立窗口；文件行目前恢复时也不处理窗口位置，所以都不显示。
        /// </summary>
        private bool CanToggleItemMinimized(StartupProfile profile, SessionItem item)
        {
            if (item == null)
            {
                return false;
            }
            if (item.Kind == SessionItemKind.Application)
            {
                // 包括“文件资源管理器 (1)”“浏览器窗口”这类有子项的父行：
                // 开关控制整个窗口，子项不再重复显示。
                return true;
            }
            if (item.Kind == SessionItemKind.ExplorerFolder)
            {
                // 直接从路径粘贴进来的独立文件夹没有父窗口，仍显示；
                // 作为某个资源管理器窗口子项的文件夹不显示。
                return !HasParentInProfile(profile, item);
            }
            return false;
        }

        private void SyncItemMinimizeButton(StartupProfile profile, SessionItem item)
        {
            if (item == null)
            {
                return;
            }
            Button button;
            if (!itemMinimizeButtons.TryGetValue(ItemEnabledKey(profile, item), out button)
                || button == null)
            {
                return;
            }
            ApplyItemMinimizeButtonState(button, IsItemMinimized(item));
        }

        private static void ApplyItemMinimizeButtonState(Button button, bool minimized)
        {
            WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.ApplyState(button, minimized);
        }

        private Button CreateItemMinimizeButton(StartupProfile profile, SessionItem item)
        {
            Button button = WorkspaceRestorer.UI.Cards.Actions.WindowMinimizeToggleButton.Create(
                IsItemMinimized(item),
                delegate
                {
                    bool wasMinimized = IsItemMinimized(item);
                    bool target = !wasMinimized;
                    if (!controller.SetProfileItemWindowMinimized(profile, item, target))
                    {
                        statusText.Text = AppText.T(StartupRestoreTextKeys.ItemMinimizeUnavailable);
                        return;
                    }
                    PushUndo(
                        target
                            ? AppText.T(StartupRestoreTextKeys.ItemUndoMinimizeOn)
                            : AppText.T(StartupRestoreTextKeys.ItemUndoMinimizeOff),
                        delegate
                        {
                            controller.SetProfileItemWindowMinimized(profile, item, wasMinimized);
                            SyncItemMinimizeButton(profile, item);
                            statusText.Text = wasMinimized
                                ? AppText.F(StartupRestoreTextKeys.ItemMinimizeRestoredOnFormat, SessionItemDisplayName(item))
                                : AppText.F(StartupRestoreTextKeys.ItemMinimizeRestoredOffFormat, SessionItemDisplayName(item));
                        },
                        delegate
                        {
                            controller.SetProfileItemWindowMinimized(profile, item, target);
                            SyncItemMinimizeButton(profile, item);
                            statusText.Text = target
                                ? AppText.F(StartupRestoreTextKeys.ItemMinimizeSetFormat, SessionItemDisplayName(item))
                                : AppText.F(StartupRestoreTextKeys.ItemMinimizeUnsetFormat, SessionItemDisplayName(item));
                        });
                    SyncItemMinimizeButton(profile, item);
                    statusText.Text = target
                        ? AppText.F(StartupRestoreTextKeys.ItemMinimizeSetUndoFormat, SessionItemDisplayName(item))
                        : AppText.F(StartupRestoreTextKeys.ItemMinimizeUnsetUndoFormat, SessionItemDisplayName(item));
                });
            itemMinimizeButtons[ItemEnabledKey(profile, item)] = button;
            return button;
        }

        /// <summary>
        /// 拖动方案卡片继续判定：位移超过系统阈值才真正开始拖（这样"点一下切换方案""双击改名"
        /// 都还能用）。卡片本体和贴边手柄共用这一套判定。
        /// </summary>

        private void RefreshSessionList()
        {
            sessionList.ItemsSource = controller.LastSnapshot == null || controller.LastSnapshot.Items == null
                ? new List<SessionItem>()
                : controller.LastSnapshot.Items;
            StartupProfile active = controller.ActiveProfile;
            int count = active == null || active.Items == null
                ? (controller.LastSnapshot == null || controller.LastSnapshot.Items == null ? 0 : controller.LastSnapshot.Items.Count)
                : active.Items.Count;
            sessionSummary.Text = count == 0
                ? AppText.T(StartupRestoreTextKeys.PageSessionEmpty)
                : AppText.F(StartupRestoreTextKeys.PageSessionSavedFormat, count);
        }


        /// <summary>
        /// Places the floating drag card: its X stays where the row it came from
        /// is (the card reads like a copy of that entry), while Y follows the
        /// pointer over the whole window, so it also keeps moving when the
        /// pointer leaves the list.  Y stops at the window edges.
        /// </summary>
        /// <summary>
        /// 点到行/卡片里的小控件时不算“点整行”。
        /// 【2026-10-18】判定逻辑搬到公共区 <see cref="WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.IsActionControl"/>，
        /// 这里保留一个同名转发（本文件十几处调用不用改）。
        /// </summary>
        private static bool IsActionControl(DependencyObject origin)
        {
            return WorkspaceRestorer.UI.Cards.Interaction.ClickToJump.IsActionControl(origin);
        }

        private void JumpToSavedItemWindow(StartupProfile profile, SessionItem item)
        {
            JumpToSavedItemWindowCore(profile == null ? null : profile.Items, item);
        }

        /// <summary>
        /// 跳转内核：只认 `SessionItem` 清单，不要求是真工作方案。
        /// 「启动与恢复」传 profile.Items；「自启动应用」通过
        /// `StartupJumpForApplications.cs` 的转接入口传它自己的适配清单。
        /// </summary>
        private void JumpToSavedItemWindowCore(IList<SessionItem> sourceItems, SessionItem item)
        {
            if (item == null || !IsLoaded)
            {
                return;
            }

            // Explorer "文件资源管理器 (N)" rows group a folder child.  Jump
            // to that child folder so the correct window opens instead of a
            // blank Explorer.
            SessionItem effective = ResolveExplorerChild(sourceItems, item);
            if (effective == null)
            {
                effective = item;
            }

            // A saved web page should jump to the exact tab that shows the
            // page, not merely to a browser window.
            if (effective.Kind == SessionItemKind.WebPage)
            {
                JumpToSavedBrowserTab(sourceItems, effective);
                return;
            }

            IntPtr handle = FindSavedItemWindow(effective);
            if (handle == IntPtr.Zero)
            {
                // No window is open for this entry: open it so the user can
                // directly look at what it refers to, then focus it when it
                // appears.
                OpenSavedItem(effective);
                return;
            }
            if (!ActivateForeignWindow(handle))
            {
                // The window exists but could not be raised (an elevated window
                // refuses ShowWindow from a non-elevated process, e.g. a
                // minimized Task Manager). Launch the app again: the OS brings
                // its single existing window to the front.
                ForceLaunchSavedItem(effective);
                return;
            }
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.JumpedWindowFormat,
                effective.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWindowFallback));
        }

        /// <summary>
        /// 跳到某一条已保存网页对应的标签页。
        ///
        /// 用户 2026-10-15：“点击工作方案内对应的跳转地址，却不能准确跳转到对应网址上”。
        /// 用户 2026-10-16（第二次反馈）：“例如图1这三个网址，是同一个网址且比较相似，
        /// 但是点击这三个，都只会跳到第一个网址，而不是对应的页面。”
        ///
        /// 全部原因（三次反馈累计查出来的）：
        ///  1. 这里原来自己写了一份 `Descendants + TabItem` 查询，会把**网页里的 ARIA 标签**
        ///     也算成浏览器标签（实测同一个窗口真标签 4 个、查询结果 6 个）。
        ///     → 统一用 <see cref="SessionCaptureService.FindBrowserTabs"/>（保存侧同一份实现）。
        ///  2. 保存下来的标签名带浏览器附加的后缀（“- 内存使用率 - 400 MB”），精确匹配不中，
        ///     而原来的 URL 兜底依赖调试端口（9222），普通启动的 Edge 根本没开。
        ///  3. **真正致命的第 3 条**：三个标签的标题前缀完全一样
        ///     （都叫“RunningHub：高可用性云端ComfyUI，在线创作AI应用…”），
        ///     而“按名字找”是**遇到第一个包含关系就返回**，于是三条都跳到第一个标签。
        ///     名字在这种情况下**根本无法区分**——唯一能区分的是网址。
        ///
        /// 现在的做法（按可靠度从高到低，**不再“第一个命中就返回”**）：
        ///  a. 每个窗口先看它是哪个标签处于选中状态（选中标签的地址栏可以直接读到网址）；
        ///     选中的标签网址 == 保存的网址 → 直接用（最可靠）；
        ///  b. 保存的标签序号 + 该标签名字对得上 → 用；
        ///  c. 标签名字对得上 → 用（含去后缀比对）；
        ///  d. 名字对得上但**序号对不上**、或者同一窗口里出现多个同名标签 →
        ///     **不再乱跳**，给用户说清楚“有多个同名标签，按位置/网址才能区分”，
        ///     并提供一次“按网址找”的机会（需要浏览器开着调试端口）。
        /// </summary>

        private static SessionItem ResolveExplorerChild(IList<SessionItem> items, SessionItem item)
        {
            if (item == null
                || item.Kind != SessionItemKind.Application
                || !string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }

            if (items == null)
            {
                return item;
            }
            for (int index = 0; index < items.Count; index++)
            {
                SessionItem candidate = items[index];
                if (candidate != null
                    && candidate.Kind == SessionItemKind.ExplorerFolder
                    && !string.IsNullOrWhiteSpace(item.Id)
                    && !string.IsNullOrWhiteSpace(candidate.ParentId)
                    && string.Equals(candidate.ParentId, item.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            return item;
        }

        private void OpenSavedItem(SessionItem item)
        {
            try
            {
                LaunchResult result = new ProcessLauncher().Launch(item);
                if (result == null || !result.Success)
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.JumpOpenFailedFormat,
                        item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThatItem),
                        result == null
                            ? AppText.T(StartupRestoreTextKeys.FallbackUnknownError)
                            : result.Error);
                    return;
                }
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.JumpOpenedFormat,
                    item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWindowFallback));
                ScheduleFocusSavedItem(item);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.JumpOpenFailedFormat,
                    item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThatItem),
                    exception.Message);
            }
        }

        private void ForceLaunchSavedItem(SessionItem item)
        {
            try
            {
                string executable = ResolveSavedItemExecutable(item);
                if (string.IsNullOrWhiteSpace(executable))
                {
                    OpenSavedItem(item);
                    return;
                }
                string fileName = Environment.ExpandEnvironmentVariables(executable.Trim());
                ShellOpenResult openResult = ShellOpener.Open(fileName, item.Arguments, null);
                if (openResult.WasSuppressed)
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.JumpSuppressedFormat,
                        item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWindowFallback));
                    return;
                }
                if (!openResult.Success)
                {
                    statusText.Text = AppText.F(
                        StartupRestoreTextKeys.JumpOpenFailedFormat,
                        item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThatItem),
                        openResult.Error);
                    return;
                }
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.JumpOpenedFrontFormat,
                    item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackWindowFallback));
                ScheduleFocusSavedItem(item);
            }
            catch (Exception exception)
            {
                statusText.Text = AppText.F(
                    StartupRestoreTextKeys.JumpOpenFailedFormat,
                    item.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThatItem),
                    exception.Message);
            }
        }

        private void ScheduleFocusSavedItem(SessionItem item)
        {
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            timer.Tick += delegate
            {
                timer.Stop();
                IntPtr handle = FindSavedItemWindow(item);
                if (handle != IntPtr.Zero)
                {
                    ActivateForeignWindow(handle);
                }
            };
            timer.Start();
        }

        private static IntPtr FindExplorerWindowByFolderPath(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return IntPtr.Zero;
            }
            string target;
            try
            {
                target = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return IntPtr.Zero;
            }

            object shell = null;
            object windows = null;
            IntPtr result = IntPtr.Zero;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null)
                {
                    return IntPtr.Zero;
                }
                shell = Activator.CreateInstance(shellType);
                windows = InvokeComMember(shell, "Windows", BindingFlags.InvokeMethod, null);
                int count = Convert.ToInt32(InvokeComMember(windows, "Count", BindingFlags.GetProperty, null));
                for (int index = 0; index < count; index++)
                {
                    object window = null;
                    object document = null;
                    object folder = null;
                    object self = null;
                    try
                    {
                        window = InvokeComMember(windows, "Item", BindingFlags.InvokeMethod, new object[] { index });
                        long handleValue = Convert.ToInt64(
                            InvokeComMember(window, "HWND", BindingFlags.GetProperty, null),
                            System.Globalization.CultureInfo.InvariantCulture);
                        try
                        {
                            document = InvokeComMember(window, "Document", BindingFlags.GetProperty, null);
                            folder = InvokeComMember(document, "Folder", BindingFlags.GetProperty, null);
                            self = InvokeComMember(folder, "Self", BindingFlags.GetProperty, null);
                            string path = Convert.ToString(InvokeComMember(self, "Path", BindingFlags.GetProperty, null));
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                string normalized;
                                try
                                {
                                    normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                }
                                catch
                                {
                                    normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                }
                                if (string.Equals(normalized, target, StringComparison.OrdinalIgnoreCase))
                                {
                                    result = new IntPtr(handleValue);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        ReleaseComMember(self);
                        ReleaseComMember(folder);
                        ReleaseComMember(document);
                        ReleaseComMember(window);
                    }
                    if (result != IntPtr.Zero)
                    {
                        break;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                ReleaseComMember(windows);
                ReleaseComMember(shell);
            }
            return result;
        }

        private static object InvokeComMember(object target, string name, BindingFlags flags, object[] arguments)
        {
            return target.GetType().InvokeMember(name, flags, null, target, arguments, null, null, null);
        }

        private static void ReleaseComMember(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try { Marshal.FinalReleaseComObject(value); }
                catch { }
            }
        }

        private static IntPtr FindSavedItemWindow(SessionItem item)
        {
            if (item == null)
            {
                return IntPtr.Zero;
            }

            // Explorer folder windows are matched by the folder path first:
            // several Explorer windows can share similar titles, and a path
            // match always selects the exact window.
            if (item.Kind == SessionItemKind.ExplorerFolder
                && !string.IsNullOrWhiteSpace(item.TargetPath))
            {
                IntPtr byPath = FindExplorerWindowByFolderPath(item.TargetPath);
                if (byPath != IntPtr.Zero)
                {
                    return byPath;
                }
            }

            // 用户 2026-10-15：“不单单是网址，后续像图2这种 txt 文件，里面多个打开也是一样。”
            // 文件（txt/doc/xlsx…）原来完全没有“找已经打开的那个窗口”这一步：
            // 它没有 ExecutablePath，下面按可执行文件匹配必然返回 0，
            // 于是每次点都重新打开一个，而不是跳到已经开着的那一个。
            // 这里按“窗口标题里有没有这个文件名”来找（记事本/写字板/Office 的标题都带文件名），
            // 并用 Shell 关联拿到默认程序、只在这个程序的窗口里找，避免撞名。
            if (item.Kind == SessionItemKind.Document && !string.IsNullOrWhiteSpace(item.TargetPath))
            {
                IntPtr byDocument = FindDocumentWindowByPath(item.TargetPath);
                if (byDocument != IntPtr.Zero)
                {
                    return byDocument;
                }
            }

            string executablePath = ResolveJumpExecutable(item);
            string normalized = NormalizeExecutablePath(executablePath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return IntPtr.Zero;
            }

            string matchTitle = item == null ? null : item.WindowTitle;
            if (string.IsNullOrWhiteSpace(matchTitle))
            {
                matchTitle = item == null ? null : item.DisplayName;
            }
            matchTitle = matchTitle == null ? null : matchTitle.Trim();

            IntPtr firstMatch = IntPtr.Zero;
            IntPtr titleMatch = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = delegate(IntPtr windowHandle, IntPtr parameter)
            {
                if (!NativeMethods.IsWindowVisible(windowHandle)
                    || windowHandle == NativeMethods.GetShellWindow()
                    || windowHandle == NativeMethods.GetDesktopWindow())
                {
                    return true;
                }
                if (!ProcessExecutableMatches(windowHandle, normalized))
                {
                    return true;
                }
                if (firstMatch == IntPtr.Zero)
                {
                    firstMatch = windowHandle;
                }
                if (titleMatch == IntPtr.Zero && !string.IsNullOrWhiteSpace(matchTitle))
                {
                    string current = ReadNativeWindowTitle(windowHandle);
                    if (!string.IsNullOrWhiteSpace(current)
                        && current.IndexOf(matchTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        titleMatch = windowHandle;
                    }
                }
                return true;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return titleMatch != IntPtr.Zero ? titleMatch : firstMatch;
        }

        /// <summary>
        /// 找“这个文件已经打开的那个窗口”。
        /// 实现在公共区 <see cref="DocumentWindowFinder"/>，恢复侧的“已经打开就跳过”
        /// 也调同一份，避免两处判断不一致。
        /// </summary>
        private static IntPtr FindDocumentWindowByPath(string filePath)
        {
            return DocumentWindowFinder.FindWindowShowing(filePath);
        }

        /// <summary>
        /// 跳转匹配专用：优先用 `JumpExecutablePath`（自启动应用的 `.lnk` 目标 /
        /// `.bat` 的 cmd.exe），否则回退到正常的 `ResolveSavedItemExecutable`。
        /// 启动/强制前置仍用原方法，保证 `.lnk` 还是按快捷方式打开。
        /// </summary>
        private static string ResolveJumpExecutable(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(item.JumpExecutablePath))
            {
                try
                {
                    return Environment.ExpandEnvironmentVariables(item.JumpExecutablePath.Trim());
                }
                catch
                {
                    return item.JumpExecutablePath.Trim();
                }
            }
            return ResolveSavedItemExecutable(item);
        }

        private static string ResolveSavedItemExecutable(SessionItem item)
        {
            if (item == null)
            {
                return null;
            }
            string value = item.BrowserExecutablePath;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = item.ExecutablePath;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            try
            {
                return Environment.ExpandEnvironmentVariables(value.Trim());
            }
            catch
            {
                return value.Trim();
            }
        }

        private static string FirstNonEmptyValue(params string[] values)
        {
            if (values != null)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    if (!string.IsNullOrWhiteSpace(values[index]))
                    {
                        return values[index].Trim();
                    }
                }
            }
            return null;
        }

        private static string NormalizeExecutablePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return value.Trim();
            }
        }

        private static bool ProcessExecutableMatches(IntPtr windowHandle, string expected)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == 0 || processId > int.MaxValue)
            {
                return false;
            }
            string actual = GetProcessExecutablePath((int)processId);
            if (!string.IsNullOrWhiteSpace(actual))
            {
                return string.Equals(
                    NormalizeExecutablePath(actual),
                    expected,
                    StringComparison.OrdinalIgnoreCase);
            }

            // Elevated / protected processes (for example Task Manager) deny
            // their image path to a non-elevated reader. Fall back to the
            // process name so the window can still be found, restored and
            // brought to the front instead of appearing "unopenable".
            string expectedName;
            try
            {
                expectedName = System.IO.Path.GetFileNameWithoutExtension(expected);
            }
            catch
            {
                expectedName = null;
            }
            if (string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string GetProcessExecutablePath(int processId)
        {
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.ProcessQueryLimitedInformation,
                false,
                (uint)processId);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    StringBuilder path = new StringBuilder(32768);
                    int pathLength = path.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref pathLength))
                    {
                        return path.ToString();
                    }
                }
                catch
                {
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.MainModule != null)
                    {
                        return process.MainModule.FileName;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string ReadNativeWindowTitle(IntPtr windowHandle)
        {
            try
            {
                int length = NativeMethods.GetWindowTextLength(windowHandle);
                if (length <= 0)
                {
                    return null;
                }
                StringBuilder value = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(windowHandle, value, value.Capacity);
                string text = value.ToString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static bool ActivateForeignWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                if (NativeMethods.IsIconic(windowHandle))
                {
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.SwRestore);
                }

                // Attach to the target window's thread so SetForegroundWindow
                // cannot be refused by the Windows foreground lock.
                uint targetThread;
                NativeMethods.GetWindowThreadProcessId(windowHandle, out targetThread);
                uint currentThread = NativeMethods.GetCurrentThreadId();
                bool attached = targetThread != 0
                    && currentThread != 0
                    && targetThread != currentThread
                    && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
                try
                {
                    NativeMethods.SetForegroundWindow(windowHandle);
                    NativeMethods.BringWindowToTop(windowHandle);
                }
                finally
                {
                    if (attached)
                    {
                        NativeMethods.AttachThreadInput(currentThread, targetThread, false);
                    }
                }

                // A higher-integrity (elevated) window refuses ShowWindow from a
                // non-elevated process, so it stays iconic. Report that so the
                // caller can fall back to launching the app again, which lets
                // the OS bring its single existing window up.
                return !NativeMethods.IsIconic(windowHandle);
            }
            catch
            {
                return false;
            }
        }


        private static string TruncateText(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }
            return value.Substring(0, Math.Max(1, maxLength - 1)) + "…";
        }

        private void AddItemByClipboard(StartupProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            PasteItemDialog dialog = new PasteItemDialog { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
            {
                return;
            }

            string error;
            SessionItem item = CreatePastedSessionItem(dialog.Value.Trim(), dialog.OpenMinimized, out error);
            if (item == null)
            {
                MessageBox.Show(
                    this,
                    error ?? AppText.T(StartupRestoreTextKeys.DialogInvalidTarget),
                    AppText.T(StartupRestoreTextKeys.DialogAddFailedTitle),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            List<SessionItem> beforeAdd = SnapshotItems(profile);
            if (controller.AddSessionItemToProfile(profile, item) == null)
            {
                return;
            }
            PushItemListUndo(
                profile, beforeAdd, SnapshotItems(profile),
                AppText.T(StartupRestoreTextKeys.DialogUndoAddItem));
            RefreshProfileList();
            statusText.Text = AppText.F(
                StartupRestoreTextKeys.DialogAddedItemFormat,
                profile.Name ?? AppText.T(StartupRestoreTextKeys.FallbackPlanFallback),
                item.DisplayName ?? item.TargetPath ?? item.Url);
        }

        private static GridView CreateSessionView()
        {
            GridView view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = AppText.T(StartupRestoreTextKeys.ColumnOrder), Width = 50, DisplayMemberBinding = new Binding("Sequence") });
            view.Columns.Add(new GridViewColumn { Header = AppText.T(StartupRestoreTextKeys.ColumnItem), Width = 170, DisplayMemberBinding = new Binding("DisplayName") });
            view.Columns.Add(new GridViewColumn { Header = AppText.T(StartupRestoreTextKeys.ColumnKind), Width = 95, DisplayMemberBinding = new Binding("Kind") });
            view.Columns.Add(new GridViewColumn { Header = AppText.T(StartupRestoreTextKeys.ColumnWindowPath), Width = 310, DisplayMemberBinding = new Binding("WindowTitle") });
            return view;
        }
    }
}
