using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkspaceRestorer.Models;
using WorkspaceRestorer.UI.Shared;
using WorkspaceRestorer.UI.Cards.Drag;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 「启动与恢复」项目行拖动的业务语义（落点、跨方案搬移、整组移动、撤销）。
    /// 起拖/跟手/放置条/插入线/禁止光标/看门狗已经全部在公共 DragMoveSession，
    /// 本文件不再保存任何“第二套拖动流程”。
    /// </summary>
    public sealed partial class MainWindow
    {
        internal sealed class NativeItemRow
        {
            public StartupProfile Profile;
            public SessionItem Item;
            /// <summary>命中测试只认这一行标题的 Grid，不认装着子项的外层 border。</summary>
            public FrameworkElement Row;
            public Border Container;
            public double RowTranslateY;
            public double RowHeight;
        }

        /// <summary>拖动过程中，鼠标下有可能成为落点的项目行。</summary>
        internal readonly List<NativeItemRow> nativeItemRows = new List<NativeItemRow>();

        /// <summary>行标题 Grid 外面还有 border 的 5 像素上下内边距，视觉上仍属于这一行。</summary>
        private const double NativeRowHitPadding = 6.0;

        /// <summary>“刚刚完成一次项目行拖动”的标记：别把松手当成“点击行 → 跳转”。</summary>
        internal bool nativeItemDragJustEnded;

        /// <summary>
        /// 松手后这一项会变成第几位（1 起算）。插到目标之前 = 目标下标+1；
        /// 插到目标之后 = 目标下标+2（被拖项先从原位置拿走，再插回去）。
        /// </summary>
        private int ItemPositionOf(NativeItemRow target, bool insertAfter)
        {
            try
            {
                StartupProfile profile = target == null ? null : target.Profile;
                if (profile == null || profile.Items == null || target.Item == null)
                {
                    return 1;
                }
                int index = profile.Items.IndexOf(target.Item);
                if (index < 0)
                {
                    return 1;
                }
                return insertAfter ? index + 2 : index + 1;
            }
            catch
            {
                return 1;
            }
        }
		private NativeItemRow FindNativeItemRowAt(Point windowPoint)
		{
			NativeItemRow best = null;
			double bestArea = double.MaxValue;
			double bestTop = 0.0;
			double bestHeight = 0.0;
			for (int index = 0; index < nativeItemRows.Count; index++)
			{
				NativeItemRow candidate = nativeItemRows[index];
				if (candidate == null || candidate.Row == null || !candidate.Row.IsVisible)
				{
					continue;
				}
				Point topLeft;
				try
				{
					topLeft = candidate.Row.TranslatePoint(new Point(0.0, 0.0), this);
				}
				catch
				{
					continue;
				}
				double width = Math.Max(1.0, candidate.Row.ActualWidth);
				double height = Math.Max(1.0, candidate.Row.ActualHeight);
				Rect bounds = new Rect(topLeft.X, topLeft.Y, width, height);
				bounds.Inflate(NativeRowHitPadding, NativeRowHitPadding);
				if (!bounds.Contains(windowPoint))
				{
					continue;
				}
				double area = width * height;
				if (best == null || area < bestArea)
				{
					best = candidate;
					bestArea = area;
					bestTop = topLeft.Y;
					bestHeight = height;
				}
			}
			if (best != null)
			{
				// 只给**选中的那一行**写坐标：接缝（上缝/下缝）判断用的就是这两个值。
				best.RowTranslateY = bestTop;
				best.RowHeight = bestHeight;
			}
			return best;
		}

		/// <summary>指针是否在某一行的矩形内（<paramref name="margin"/> 为向外放宽的像素）。</summary>
		private bool IsPointWithinNativeRow(NativeItemRow row, Point windowPoint, double margin)
		{
			if (row == null || row.Row == null || !row.Row.IsVisible)
			{
				return false;
			}
			try
			{
				Point topLeft = row.Row.TranslatePoint(new Point(0.0, 0.0), this);
				Rect bounds = new Rect(
					topLeft.X,
					topLeft.Y,
					Math.Max(1.0, row.Row.ActualWidth),
					Math.Max(1.0, row.Row.ActualHeight));
				bounds.Inflate(NativeRowHitPadding + Math.Max(0.0, margin), NativeRowHitPadding + Math.Max(0.0, margin));
				return bounds.Contains(windowPoint);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>把一行的当前坐标/高度算出来存到它身上（接缝判断读的就是这两个字段）。</summary>
		private void EnsureRowGeometry(NativeItemRow row)
		{
			if (row == null || row.Row == null)
			{
				return;
			}
			try
			{
				Point topLeft = row.Row.TranslatePoint(new Point(0.0, 0.0), this);
				row.RowTranslateY = topLeft.Y;
				row.RowHeight = Math.Max(1.0, row.Row.ActualHeight);
			}
			catch
			{
			}
		}

		/// <summary>按“项目”找它登记的行（跨分组提升落点时要用）。</summary>
		private NativeItemRow FindNativeItemRowForItem(SessionItem item)
		{
			if (item == null)
			{
				return null;
			}
			for (int index = 0; index < nativeItemRows.Count; index++)
			{
				NativeItemRow row = nativeItemRows[index];
				if (row == null || !object.ReferenceEquals(row.Item, item))
				{
					continue;
				}
				// 【2026-10-24】只认还挂在当前列表里的行：
				// 重建列表后旧行可能仍被登记着，返回它会拿脱离 visual tree 的父行去画插入线/算落点。
				if (row.Row == null
					|| row.Row.Parent == null
					|| (profileHost != null && !profileHost.IsAncestorOf(row.Row)))
				{
					continue;
				}
				return row;
			}
			return null;
		}

		/// <summary>把当前拖动的小项放到目标行上（同一个方案＝调顺序；不同方案＝搬过去）。</summary>
		private void ApplyNativeItemDrop(StartupProfile from, SessionItem moving, NativeItemRow target, bool insertAfter)
		{
			try
			{
				if (target.Profile != null && !object.ReferenceEquals(target.Profile, from))
				{
					if (controller.MoveItemBetweenProfiles(from, target.Profile, moving, target.Item, insertAfter))
					{
						// 【2026-10-17 补】跨方案搬移也要能撤销：方案内部调顺序走的是
						// `ApplyItemDrop`，那里有撤销；这一支以前漏了，用户按 Ctrl+Z 没反应。
						// （撤销是“搬回原方案”；原方案里的具体位置不保证一模一样 ——
						//   `MoveItemBetweenProfiles` 只能追加到末尾或插在某项前后。）
						StartupProfile sourceProfile = from;
						StartupProfile destinationProfile = target.Profile;
						SessionItem movedItem = moving;
						SessionItem anchor = target.Item;
						bool insertAfterTarget = insertAfter;
						PushUndo(
							AppText.T(StartupRestoreTextKeys.ItemUndoMoveBetween),
							delegate
							{
								controller.MoveItemBetweenProfiles(destinationProfile, sourceProfile, movedItem, null, false);
								RefreshProfileList();
							},
							delegate
							{
								controller.MoveItemBetweenProfiles(sourceProfile, destinationProfile, movedItem, anchor, insertAfterTarget);
								RefreshProfileList();
							});
						statusText.Text = AppText.F(
							StartupRestoreTextKeys.ItemMovedBetweenFormat,
							moving.DisplayName ?? AppText.T(StartupRestoreTextKeys.FallbackThisItem),
							target.Profile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
					}
					else
					{
						statusText.Text = AppText.T(StartupRestoreTextKeys.ItemMoveAlreadyThere);
					}
					RefreshProfileList();
					return;
				}
				// 同一个方案内部：走原来的落点判定（保留“并入浏览器窗口/整组移动”等既有行为）。
				ApplyItemDrop(from, moving, target.Item, insertAfter);
			}
			catch (Exception ex)
			{
				statusText.Text = AppText.F(StartupRestoreTextKeys.ItemDragFailedFormat, ex.Message);
			}
		}

		// ================================================================ 卡片落点（2026-11-01）

		/// <summary>指针下的方案卡片：只认别的、非文件夹的方案。</summary>
		private StartupProfile FindNativeItemCardTarget(Point windowPoint, StartupProfile exclude)
		{
			if (profileHost == null)
			{
				return null;
			}
			StartupProfile best = null;
			double bestArea = double.MaxValue;
			System.Collections.Generic.IList<StartupProfile> profiles = controller.StartupProfiles;
			if (profiles == null)
			{
				return null;
			}
			for (int index = 0; index < profiles.Count; index++)
			{
				StartupProfile candidate = profiles[index];
				if (candidate == null || candidate.IsFolder || object.ReferenceEquals(candidate, exclude))
				{
					continue;
				}
				Border card = startupCardElements.FindHeader(candidate.Id ?? string.Empty) as Border;
				if (card == null || !card.IsVisible)
				{
					continue;
				}
				Point topLeft;
				try
				{
					topLeft = card.TranslatePoint(new Point(0.0, 0.0), this);
				}
				catch
				{
					continue;
				}
				double width = Math.Max(1.0, card.ActualWidth);
				double height = Math.Max(1.0, card.ActualHeight);
				if (!new Rect(topLeft.X, topLeft.Y, width, height).Contains(windowPoint))
				{
					continue;
				}
				double area = width * height;
				if (best != null && area >= bestArea)
				{
					continue;
				}
				best = candidate;
				bestArea = area;
			}
			return best;
		}

		/// <summary>
		/// 卡片落点：取目标方案里离指针最近的那一项做定位项；收起/空方案返回 null＝末尾。
		/// 最近项是子项时提升到它所属的父项（不把顶层项插进组内部）。
		/// </summary>
		private void ResolveNativeItemCardAnchor(
			StartupProfile target, Point cursor, out SessionItem anchor, out bool insertAfter)
		{
			anchor = null;
			insertAfter = false;
			if (target == null)
			{
				return;
			}
			double bestDistance = double.MaxValue;
			for (int index = 0; index < nativeItemRows.Count; index++)
			{
				NativeItemRow row = nativeItemRows[index];
				if (row == null || row.Row == null || !row.Row.IsVisible)
				{
					continue;
				}
				if (!object.ReferenceEquals(row.Profile, target))
				{
					continue;
				}
				Point topLeft;
				try
				{
					topLeft = row.Row.TranslatePoint(new Point(0.0, 0.0), this);
				}
				catch
				{
					continue;
				}
				double height = Math.Max(1.0, row.Row.ActualHeight);
				double center = topLeft.Y + height / 2.0;
				bool overRow = cursor.Y >= topLeft.Y && cursor.Y <= topLeft.Y + height;
				double distance = overRow ? 0.0 : Math.Abs(cursor.Y - center);
				if (distance >= bestDistance)
				{
					continue;
				}
				bestDistance = distance;
				SessionItem resolved = row.Item;
				bool afterItem = cursor.Y > center;
				if (resolved != null && !string.IsNullOrWhiteSpace(resolved.ParentId))
				{
					SessionItem parent = FindParentWindow(target, resolved);
					if (parent != null)
					{
						resolved = parent;
						afterItem = true;
					}
				}
				anchor = resolved;
				insertAfter = afterItem;
			}
		}

		private string BuildNativeItemCardDropHint(StartupProfile target, Point cursor)
		{
			SessionItem anchor;
			bool insertAfter;
			ResolveNativeItemCardAnchor(target, cursor, out anchor, out insertAfter);
			string name = target.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback);
			if (anchor == null)
			{
				return AppText.F(StartupRestoreTextKeys.ItemDropCardEndFormat, name);
			}
			return AppText.F(
				insertAfter
					? StartupRestoreTextKeys.ItemDropCardAfterFormat
					: StartupRestoreTextKeys.ItemDropCardBeforeFormat,
				name,
				SessionItemDisplayName(anchor));
		}

		/// <summary>松手落在方案卡片上：整组/单项搬到该方案（一次落盘 + 一步撤销）。</summary>
		private void ApplyNativeItemDropToCard(
			StartupProfile from, SessionItem moving, StartupProfile to, Point cursor)
		{
			if (from == null || moving == null || to == null || object.ReferenceEquals(from, to))
			{
				return;
			}
			SessionItem anchor;
			bool insertAfter;
			ResolveNativeItemCardAnchor(to, cursor, out anchor, out insertAfter);
			bool group = HasChildrenInProfile(from, moving);
			StartupProfile sourceProfile = from;
			StartupProfile destinationProfile = to;
			SessionItem movedItem = moving;
			SessionItem anchorItem = anchor;
			bool insertAfterTarget = insertAfter;
			try
			{
				if (controller.MoveItemBetweenProfiles(sourceProfile, destinationProfile, movedItem, anchorItem, insertAfterTarget))
				{
					PushUndo(
						AppText.T(StartupRestoreTextKeys.ItemUndoMoveBetween),
						delegate
						{
							controller.MoveItemBetweenProfiles(destinationProfile, sourceProfile, movedItem, null, false);
							RefreshProfileList();
						},
						delegate
						{
							controller.MoveItemBetweenProfiles(sourceProfile, destinationProfile, movedItem, anchorItem, insertAfterTarget);
							RefreshProfileList();
						});
					statusText.Text = AppText.F(
						group
							? StartupRestoreTextKeys.ItemMovedGroupBetweenFormat
							: StartupRestoreTextKeys.ItemMovedBetweenFormat,
						SessionItemDisplayName(movedItem),
						destinationProfile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
				}
				else
				{
					statusText.Text = AppText.T(StartupRestoreTextKeys.ItemMoveAlreadyThere);
				}
			}
			catch (Exception ex)
			{
				statusText.Text = AppText.F(StartupRestoreTextKeys.ItemDragFailedFormat, ex.Message);
			}
			RefreshProfileList();
		}

		/// <summary>卡片落点高亮：只改外框，松手/取消时恢复。</summary>
		private MenuItem BuildMoveItemToProfileMenu(StartupProfile source, SessionItem item)
		{
			MenuItem move = new MenuItem { Header = AppText.T(StartupRestoreTextKeys.ItemMoveMenu) };
			if (source == null || item == null)
			{
				move.IsEnabled = false;
				return move;
			}
			if (!string.IsNullOrWhiteSpace(item.ParentId))
			{
				MenuItem note = new MenuItem
				{
					Header = AppText.T(StartupRestoreTextKeys.ItemMoveChildOnly)
				};
				note.IsEnabled = false;
				move.Items.Add(note);
				return move;
			}
			int count = 0;
			System.Collections.Generic.IList<StartupProfile> profiles = controller.StartupProfiles;
			for (int index = 0; profiles != null && index < profiles.Count; index++)
			{
				StartupProfile target = profiles[index];
				if (target == null || target.IsFolder || object.ReferenceEquals(target, source))
				{
					continue;
				}
				StartupProfile captured = target;
				MenuItem entry = new MenuItem
				{
					Header = captured.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback)
				};
				entry.Click += delegate { MoveItemToProfileEnd(source, item, captured); };
				move.Items.Add(entry);
				count++;
			}
			if (count == 0)
			{
				MenuItem none = new MenuItem
				{
					Header = AppText.T(StartupRestoreTextKeys.ItemMoveNoOther)
				};
				none.IsEnabled = false;
				move.Items.Add(none);
			}
			return move;
		}

		/// <summary>右键“移动到方案…”：追加到目标方案末尾，并进撤销栈。</summary>
		private void MoveItemToProfileEnd(StartupProfile from, SessionItem moving, StartupProfile to)
		{
			if (from == null || moving == null || to == null || object.ReferenceEquals(from, to))
			{
				return;
			}
			StartupProfile sourceProfile = from;
			StartupProfile destinationProfile = to;
			SessionItem movedItem = moving;
			bool group = HasChildrenInProfile(from, moving);
			if (controller.MoveItemBetweenProfiles(sourceProfile, destinationProfile, movedItem, null, false))
			{
				PushUndo(
					AppText.T(StartupRestoreTextKeys.ItemUndoMoveBetween),
					delegate
					{
						controller.MoveItemBetweenProfiles(destinationProfile, sourceProfile, movedItem, null, false);
						RefreshProfileList();
					},
					delegate
					{
						controller.MoveItemBetweenProfiles(sourceProfile, destinationProfile, movedItem, null, false);
						RefreshProfileList();
					});
				statusText.Text = AppText.F(
					group
						? StartupRestoreTextKeys.ItemMovedGroupBetweenEndFormat
						: StartupRestoreTextKeys.ItemMovedBetweenEndFormat,
					SessionItemDisplayName(movedItem),
					destinationProfile.Name ?? AppText.T(CommonTextKeys.WorkProfileFallback));
			}
			else
			{
				statusText.Text = AppText.T(StartupRestoreTextKeys.ItemMoveAlreadyThere);
			}
			RefreshProfileList();
		}

		private static bool SameItemGroup(SessionItem first, SessionItem second)
		{
			string text = first == null ? null : first.ParentId;
			string text2 = second == null ? null : second.ParentId;
			if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(text2))
			{
				return true;
			}
			if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(text2))
			{
				return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static bool CanHostChildren(SessionItem item)
		{
			if (item == null || item.Kind != 0)
			{
				return false;
			}
			if (!string.IsNullOrWhiteSpace(item.BrowserGroupKey))
			{
				return true;
			}
			return string.Equals(item.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsChildEntry(SessionItem item)
		{
			if (item != null)
			{
				if (item.Kind != SessionItemKind.WebPage)
				{
					return item.Kind == SessionItemKind.ExplorerFolder;
				}
				return true;
			}
			return false;
		}

		private static bool CanJoinWindow(SessionItem child, SessionItem window)
		{
			if (child == null || !CanHostChildren(window))
			{
				return false;
			}
			if (child.Kind == SessionItemKind.WebPage)
			{
				return !string.IsNullOrWhiteSpace(window.BrowserGroupKey);
			}
			if (child.Kind == SessionItemKind.ExplorerFolder)
			{
				return string.Equals(window.CaptureSource, "ExplorerWindow", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private static List<SessionItem> ChildrenInList(IList<SessionItem> items, SessionItem parent)
		{
			List<SessionItem> list = new List<SessionItem>();
			if (items == null || parent == null)
			{
				return list;
			}
			for (int i = 0; i < items.Count; i++)
			{
				SessionItem sessionItem = items[i];
				if (sessionItem != null && !object.ReferenceEquals(sessionItem, parent) && IsChildOf(parent, sessionItem))
				{
					list.Add(sessionItem);
				}
			}
			return list;
		}

		private static SessionItem FindParentWindow(StartupProfile profile, SessionItem child)
		{
			if (profile == null || profile.Items == null || child == null)
			{
				return null;
			}
			if (!string.IsNullOrWhiteSpace(child.ParentId))
			{
				for (int i = 0; i < profile.Items.Count; i++)
				{
					SessionItem sessionItem = profile.Items[i];
					if (sessionItem != null && string.Equals(sessionItem.Id, child.ParentId, StringComparison.OrdinalIgnoreCase))
					{
						return sessionItem;
					}
				}
			}
			for (int j = 0; j < profile.Items.Count; j++)
			{
				SessionItem sessionItem2 = profile.Items[j];
				if (sessionItem2 != null && !object.ReferenceEquals(sessionItem2, child) && IsChildOf(sessionItem2, child))
				{
					return sessionItem2;
				}
			}
			return null;
		}

		/// <summary>
		/// 判断“把 dragged 放到 target 上”属于哪一种落点。
		///
		/// 【2026-10-16 新增 detachChild —— 用户第 2 条】
		/// 用户原话：“类似这种一个打开多个的，例如文件夹，一个打开两个，
		///           我想可以移动某个文件时，就不合在一起。”
		/// 并明确答复：**拖出去就独立成一项**，而且**不限文件夹和网页**（任何子项都能拖出去）。
		///
		/// 【以前为什么做不到】子项拖到“普通行”上时，这里最后是 `return false` —— 什么都不做；
		/// 只有“该窗口只剩 1 个子项”时才会等于移动整组。所以一个带 2 个子项的文件夹窗口，
		/// 里面的子项**根本拖不出去**（用户图 1 那种情况）。
		///
		/// 现在：子项落到“不属于自己那一组、又不是能接纳它的窗口”的位置时 → `detachChild = true`，
		/// 由 `ApplyItemDrop` 把它**从分组里摘出来、变成顶级项**（清掉 ParentId）。
		/// 同组内换位、并进另一个浏览器/文件夹窗口、独子等于移动整组，这三条老行为都不变。
		/// </summary>
		private static bool ClassifyItemDrop(StartupProfile profile, SessionItem dragged, SessionItem target, out SessionItem joinWindow, out SessionItem groupAnchor, out bool detachChild)
		{
			joinWindow = null;
			groupAnchor = null;
			detachChild = false;
			if (profile == null || dragged == null || target == null || object.ReferenceEquals(dragged, target))
			{
				return false;
			}
			if (CanHostChildren(dragged))
			{
				return SameItemGroup(dragged, target);
			}
			if (IsChildEntry(dragged))
			{
				SessionItem sessionItem = FindParentWindow(profile, dragged);
				SessionItem sessionItem2 = (IsChildEntry(target) ? FindParentWindow(profile, target) : target);
				if (sessionItem2 != null && CanJoinWindow(dragged, sessionItem2) && sessionItem2.Kind == SessionItemKind.Application)
				{
					joinWindow = sessionItem2;
					return true;
				}
				if (SameItemGroup(dragged, target))
				{
					groupAnchor = target;
					return true;
				}
				if (sessionItem2 != null && CanHostChildren(sessionItem2))
				{
					// 目标是一个“能装子项、但装不下这一项”的窗口（例如把文件夹子项拖到浏览器窗口上）：
					// 不能并进去，那就让它**在这个窗口的位置上独立成一项**。
					detachChild = true;
					return true;
				}
				if (sessionItem != null && ChildrenInList(profile.Items, sessionItem).Count <= 1 && sessionItem2 != null && !object.ReferenceEquals(sessionItem2, dragged))
				{
					// 该窗口只剩这一个子项：按用户早先的要求（第 106 条），拖它等于拖整个窗口。
					groupAnchor = sessionItem2;
					return true;
				}
				detachChild = true;
				return true;
			}
			if (string.IsNullOrWhiteSpace(target.ParentId))
			{
				return SameItemGroup(dragged, target);
			}
			return false;
		}

		/// <summary>
		/// 把一个子项从它的分组里摘出来、变成**顶级项**，并插到目标项的前/后。
		///
		/// 【为什么不能只清 ParentId】`StartupWorkspacePage.IsChildOf` 的判定分两步：
		///   ① `子项.ParentId` 非空 → 直接按 ParentId 比；
		///   ② **ParentId 为空时会“回退到同一个窗口”**：文件夹子项看
		///      `父项.CaptureSource == "ExplorerWindow"` + 两边 `ApplicationWindowIndex` 相同
		///      （或 `ExecutablePath` 相同）；网页子项看两边 `BrowserGroupKey` 相同
		///      （或 `BrowserExecutablePath` 相同）。
		/// 所以只清 ParentId 的话，清完**照样算那个窗口的子项**——界面不会有任何变化
		///（Round82 第一次跑就是这样：文件里 ParentId 已经是空，屏幕上它仍然缩进在父项下面）。
		///
		/// 因此这里把“窗口身份”一并清掉，让它对任何父项都不再匹配。
		/// 恢复不受影响：`ProcessLauncher` 打开文件夹用的是 `TargetPath`
		///（`explorer.exe &lt;文件夹&gt;`），打开网页用的是网址/BrowserExecutablePath 的缺省回退，
		/// 都不依赖被清掉的这几个字段。
		/// </summary>
		/// <summary>
		/// 把一个子项从它的分组里摘出来，并**给它配一个新的“所属窗口”**，一起插到目标位置。
		///
		/// 【用户 2026-10-16 的补充要求】
		///   “网址或文件等，单独拖出来时，要配有拖出来的对应的浏览器或文件夹
		///    （例如是从 edge 浏览器拖出来的，网址就挂在 edge 浏览器下，文件夹同理）。”
		///
		/// 【为什么不能只是当个顶级项】网页/文件夹在恢复时**必须有一个窗口可挂**：
		/// 光把 ParentId 清掉，它就成了一个没有窗口的孤立网页/文件夹，恢复时无处安放。
		/// 所以拖出来时**新建一个同类型的窗口项**（浏览器→新的 Edge 窗口；文件夹→新的资源管理器窗口），
		/// 让它挂在这个新窗口下面 —— 这就是“独立成一项”的正确含义：**独立成一组**。
		///
		/// 【ParentId 只要“非空且不等于原父项”就够了】`IsChildOf` 第一步就比较 ParentId；
		/// 之前那版把 ParentId 清成 null，反而触发了它第 ② 步的“同一个窗口”回退匹配，
		/// 于是界面里它照样缩进在原窗口下面（Round82 第一次跑就是这么挂的）。
		/// </summary>
		private static List<SessionItem> BuildDetachChild(StartupProfile profile, IList<SessionItem> items, SessionItem child, SessionItem target, bool insertAfter)
		{
			List<SessionItem> list = new List<SessionItem>(items);
			int index = list.IndexOf(child);
			if (index < 0)
			{
				return list;
			}
			SessionItem oldParent = FindParentWindow(profile, child);
			list.RemoveAt(index);
			SessionItem window = BuildDetachedWindowItem(list, child, oldParent);
			child.ParentId = window.Id;
			if (child.Kind == SessionItemKind.WebPage)
			{
				// 把窗口身份对齐到新窗口：恢复时才知道“用哪个浏览器、算哪个窗口的一组标签”。
				child.BrowserGroupKey = window.BrowserGroupKey;
				child.BrowserWindowIndex = window.BrowserWindowIndex;
				child.IsActiveBrowserTab = false;
				child.BrowserTabs = null;
				child.BrowserWindowTitleSnapshot = null;
				child.BrowserBindingKind = BrowserPageBindingKind.UserBound;
				child.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
			}
			else
			{
				child.ApplicationWindowIndex = window.ApplicationWindowIndex;
			}
			int anchor = list.IndexOf(target);
			int at = anchor < 0 ? Math.Min(index, list.Count) : (insertAfter ? (anchor + 1) : anchor);
			at = Math.Max(0, Math.Min(at, list.Count));
			list.Insert(at, window);
			list.Insert(Math.Min(at + 1, list.Count), child);
			return list;
		}

		/// <summary>为一个被拖出来的子项新建“所属窗口”项（浏览器窗口 / 资源管理器窗口）。</summary>
		private static SessionItem BuildDetachedWindowItem(IList<SessionItem> items, SessionItem child, SessionItem oldParent)
		{
			SessionItem window = new SessionItem();
			window.Id = Guid.NewGuid().ToString("N");
			window.Kind = SessionItemKind.Application;
			window.IsEnabled = true;
			window.StartTimeUtc = child.StartTimeUtc == default(DateTime) ? DateTime.UtcNow : child.StartTimeUtc;
			if (child.Kind == SessionItemKind.WebPage)
			{
				string oldName = oldParent == null ? null : oldParent.DisplayName;
				string browserExe = !string.IsNullOrWhiteSpace(child.BrowserExecutablePath)
					? child.BrowserExecutablePath
					: (oldParent == null ? null : oldParent.ExecutablePath);
				window.DisplayName = BuildDetachedWindowName(
					items,
					oldName,
					child.BrowserName,
					AppText.T(StartupRestoreTextKeys.FallbackBrowserFallback));
				window.BrowserGroupKey = Guid.NewGuid().ToString("N");
				window.BrowserExecutablePath = browserExe;
				window.ExecutablePath = browserExe;
				window.TargetPath = browserExe;
				window.BrowserWindowIndex = -1;
				window.BrowserTabIndex = -1;
				window.CaptureSource = "BrowserWindow";
			}
			else
			{
				string explorerExe = !string.IsNullOrWhiteSpace(child.ExecutablePath)
					? child.ExecutablePath
					: "explorer.exe";
				string explorerName = AppText.T(StartupRestoreTextKeys.FallbackFileExplorerFallback);
				window.DisplayName = BuildDetachedWindowName(
					items,
					oldParent == null ? null : oldParent.DisplayName,
					explorerName,
					explorerName);
				window.CaptureSource = "ExplorerWindow";
				window.ExecutablePath = explorerExe;
				window.TargetPath = child.TargetPath;
				window.ApplicationWindowIndex = -1;
			}
			return window;
		}

		/// <summary>
		/// 新窗口的名字：沿用原窗口的基础名（去掉末尾的“(n)”）＋下一个可用编号，
		/// 与捕获时 `BuildBrowserWindowDisplayName` 的风格一致（例：Microsoft Edge (3)）。
		/// </summary>
		private static string BuildDetachedWindowName(IList<SessionItem> items, string oldParentName, string fallbackName, string lastFallback)
		{
			string basePart = StripTrailingOrdinal(oldParentName);
			if (string.IsNullOrWhiteSpace(basePart))
			{
				basePart = StripTrailingOrdinal(fallbackName);
			}
			if (string.IsNullOrWhiteSpace(basePart))
			{
				basePart = lastFallback;
			}
			int max = 0;
			for (int i = 0; i < items.Count; i++)
			{
				string name = items[i] == null ? null : items[i].DisplayName;
				if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(")", StringComparison.Ordinal))
				{
					continue;
				}
				if (name.Length <= basePart.Length
					|| !name.StartsWith(basePart, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				int open = name.LastIndexOf('(');
				if (open <= 0)
				{
					continue;
				}
				int number;
				if (int.TryParse(name.Substring(open + 1, name.Length - open - 2).Trim(), out number) && number > max)
				{
					max = number;
				}
			}
			return basePart + " (" + (max + 1) + ")";
		}

		/// <summary>去掉名字末尾的“(数字)”（例：Microsoft Edge (2) → Microsoft Edge）。</summary>
		private static string StripTrailingOrdinal(string value)
		{
			if (string.IsNullOrWhiteSpace(value))
			{
				return string.Empty;
			}
			string text = value.Trim();
			if (!text.EndsWith(")", StringComparison.Ordinal))
			{
				return text;
			}
			int open = text.LastIndexOf('(');
			if (open <= 0)
			{
				return text;
			}
			int number;
			if (!int.TryParse(text.Substring(open + 1, text.Length - open - 2).Trim(), out number))
			{
				return text;
			}
			return text.Substring(0, open).Trim();
		}

		private bool ApplyItemDrop(StartupProfile profile, SessionItem dragged, SessionItem target, bool insertAfter)
		{
			SessionItem joinWindow;
			SessionItem groupAnchor;
			bool detachChild;
			if (!ClassifyItemDrop(profile, dragged, target, out joinWindow, out groupAnchor, out detachChild))
			{
				return false;
			}
			List<SessionItem> list = SnapshotItemsDeep(profile);
			List<SessionItem> items = new List<SessionItem>(profile.Items);
			string undoText;
			string statusFormat;
			object[] statusArgs;
			if (detachChild)
			{
				// 【用户第 2 条 + 2026-10-16 补充】把子项从分组里摘出来，并给它配一个新的所属窗口
				//（网址 → 新的浏览器窗口；文件夹 → 新的资源管理器窗口），两者一起插到目标位置。
				items = BuildDetachChild(profile, items, dragged, target, insertAfter);
				string draggedName = SessionItemDisplayName(dragged);
				undoText = AppText.F(StartupRestoreTextKeys.ItemUndoDragDetachedFormat, draggedName);
				statusFormat = StartupRestoreTextKeys.ItemDragDetachedFormat;
				statusArgs = new object[] { draggedName };
			}
			else if (joinWindow != null)
			{
				items = BuildJoinWindow(profile, items, dragged, joinWindow, target, insertAfter);
				string draggedName = SessionItemDisplayName(dragged);
				string windowName = SessionItemDisplayName(joinWindow);
				undoText = AppText.F(StartupRestoreTextKeys.ItemUndoDragJoinedFormat, draggedName, windowName);
				statusFormat = StartupRestoreTextKeys.ItemDragJoinedFormat;
				statusArgs = new object[] { draggedName, windowName };
			}
			else if (IsChildEntry(dragged) && !SameItemGroup(dragged, target))
			{
				SessionItem sessionItem = FindParentWindow(profile, dragged);
				if (sessionItem == null)
				{
					return false;
				}
				items = BuildMoveGroup(items, sessionItem, groupAnchor ?? target, insertAfter);
				string windowName = SessionItemDisplayName(sessionItem);
				undoText = AppText.F(StartupRestoreTextKeys.ItemUndoDragMovedGroupFormat, windowName);
				statusFormat = StartupRestoreTextKeys.ItemDragMovedGroupFormat;
				statusArgs = new object[] { windowName };
			}
			else
			{
				items = BuildMoveWithin(items, dragged, target, insertAfter);
				undoText = AppText.T(StartupRestoreTextKeys.ItemDragReorder);
				statusFormat = StartupRestoreTextKeys.ItemDragReorderedFormat;
				statusArgs = new object[0];
			}
			List<SessionItem> list2 = CloneItemList(items);
			if (SameItemList(list, list2))
			{
				return false;
			}
			controller.SetProfileItemsOrder(profile, items);
			RefreshProfileList();
			PushItemListUndo(profile, list, list2, undoText);
			statusText.Text = statusArgs.Length == 0
				? AppText.T(statusFormat)
				: AppText.F(statusFormat, statusArgs);
			return true;
		}

		private static List<SessionItem> BuildMoveWithin(IList<SessionItem> items, SessionItem toMove, SessionItem target, bool insertAfter)
		{
			List<SessionItem> list = new List<SessionItem>(items);
			int num = list.IndexOf(toMove);
			if (num < 0)
			{
				return list;
			}
			list.RemoveAt(num);
			int num2 = list.IndexOf(target);
			if (num2 < 0)
			{
				list.Insert(Math.Min(num, list.Count), toMove);
				return list;
			}
			list.Insert(insertAfter ? (num2 + 1) : num2, toMove);
			return list;
		}

		private static List<SessionItem> BuildMoveGroup(IList<SessionItem> items, SessionItem window, SessionItem anchor, bool insertAfter)
		{
			List<SessionItem> list = new List<SessionItem>(items);
			List<SessionItem> list2 = new List<SessionItem>();
			if (list.IndexOf(window) >= 0)
			{
				list2.Add(window);
			}
			for (int i = 0; i < list.Count; i++)
			{
				SessionItem sessionItem = list[i];
				if (sessionItem != null && !object.ReferenceEquals(sessionItem, window) && IsChildOf(window, sessionItem))
				{
					list2.Add(sessionItem);
				}
			}
			if (list2.Count == 0 || list2.Contains(anchor))
			{
				return list;
			}
			foreach (SessionItem item in list2)
			{
				list.Remove(item);
			}
			int num = list.IndexOf(anchor);
			if (num < 0)
			{
				return list;
			}
			int index = (insertAfter ? (num + 1) : num);
			list.InsertRange(index, list2);
			return list;
		}

		private static List<SessionItem> BuildJoinWindow(StartupProfile profile, IList<SessionItem> items, SessionItem child, SessionItem window, SessionItem droppedOn, bool insertAfter)
		{
			List<SessionItem> list = new List<SessionItem>(items);
			SessionItem sessionItem = FindParentWindow(profile, child);
			child.ParentId = window.Id;
			if (child.Kind == SessionItemKind.WebPage)
			{
				if (!string.IsNullOrWhiteSpace(window.BrowserGroupKey))
				{
					child.BrowserGroupKey = window.BrowserGroupKey;
				}
				if (!string.IsNullOrWhiteSpace(window.ExecutablePath))
				{
					child.BrowserExecutablePath = window.ExecutablePath;
				}
				if (string.IsNullOrWhiteSpace(child.BrowserName) && !string.IsNullOrWhiteSpace(window.BrowserName))
				{
					child.BrowserName = window.BrowserName;
				}
				child.BrowserWindowIndex = window.BrowserWindowIndex;
				child.IsActiveBrowserTab = false;
				child.BrowserTabs = BrowserTabFingerprint.CloneList(window.BrowserTabs);
				child.BrowserWindowTitleSnapshot = window.BrowserWindowTitleSnapshot;
				child.BrowserBindingKind = BrowserPageBindingKind.UserBound;
				child.BrowserBindingUpdatedAtUtc = DateTime.UtcNow;
			}
			else if (child.Kind == SessionItemKind.ExplorerFolder)
			{
				child.ApplicationWindowIndex = window.ApplicationWindowIndex;
				if (!string.IsNullOrWhiteSpace(window.ExecutablePath))
				{
					child.ExecutablePath = window.ExecutablePath;
				}
			}
			list.Remove(child);
			int num = ((droppedOn == null || object.ReferenceEquals(droppedOn, window)) ? (-1) : list.IndexOf(droppedOn));
			int val;
			if (num >= 0 && IsChildOf(window, droppedOn))
			{
				val = (insertAfter ? (num + 1) : num);
			}
			else
			{
				List<SessionItem> list2 = ChildrenInList(list, window);
				val = ((list2.Count != 0) ? (insertAfter ? (list.IndexOf(list2[list2.Count - 1]) + 1) : list.IndexOf(list2[0])) : (list.IndexOf(window) + 1));
			}
			list.Insert(Math.Min(Math.Max(val, 0), list.Count), child);
			ResequenceWindowTabs(list, window);
			if (sessionItem != null && !object.ReferenceEquals(sessionItem, window) && CanHostChildren(sessionItem) && ChildrenInList(list, sessionItem).Count == 0)
			{
				list.Remove(sessionItem);
			}
			return list;
		}

		private bool MoveItemToEdge(StartupProfile profile, SessionItem dragged, int direction)
		{
			if (profile == null || profile.Items == null || dragged == null || direction == 0)
			{
				return false;
			}
			List<SessionItem> list = SnapshotItemsDeep(profile);
			List<SessionItem> list2 = new List<SessionItem>(profile.Items);
			if (IsChildEntry(dragged))
			{
				SessionItem sessionItem = FindParentWindow(profile, dragged);
				if (sessionItem != null && ChildrenInList(list2, sessionItem).Count <= 1)
				{
					list2 = BuildMoveGroupToEdge(list2, sessionItem, direction);
				}
				else if (sessionItem != null)
				{
					list2.Remove(dragged);
					List<SessionItem> list3 = ChildrenInList(list2, sessionItem);
					int val = ((list3.Count == 0) ? (list2.IndexOf(sessionItem) + 1) : ((direction >= 0) ? (list2.IndexOf(list3[list3.Count - 1]) + 1) : list2.IndexOf(list3[0])));
					list2.Insert(Math.Min(Math.Max(val, 0), list2.Count), dragged);
					ResequenceWindowTabs(list2, sessionItem);
				}
			}
			else
			{
				list2 = BuildMoveGroupToEdge(list2, dragged, direction);
			}
			List<SessionItem> list4 = CloneItemList(list2);
			if (SameItemList(list, list4))
			{
				return false;
			}
			controller.SetProfileItemsOrder(profile, list2);
			RefreshProfileList();
			string itemName = SessionItemDisplayName(dragged);
			PushItemListUndo(
				profile,
				list,
				list4,
				AppText.T(direction < 0
					? StartupRestoreTextKeys.ItemUndoMoveFrontFormat
					: StartupRestoreTextKeys.ItemUndoMoveEndFormat));
			statusText.Text = AppText.F(
				direction < 0
					? StartupRestoreTextKeys.ItemMovedToFrontFormat
					: StartupRestoreTextKeys.ItemMovedToEndFormat,
				itemName);
			return true;
		}

		private static List<SessionItem> BuildMoveGroupToEdge(IList<SessionItem> items, SessionItem entry, int direction)
		{
			List<SessionItem> list = new List<SessionItem>(items);
			List<SessionItem> list2 = new List<SessionItem>();
			if (list.IndexOf(entry) >= 0)
			{
				list2.Add(entry);
			}
			for (int i = 0; i < list.Count; i++)
			{
				SessionItem sessionItem = list[i];
				if (sessionItem != null && !object.ReferenceEquals(sessionItem, entry) && IsChildOf(entry, sessionItem))
				{
					list2.Add(sessionItem);
				}
			}
			foreach (SessionItem item in list2)
			{
				list.Remove(item);
			}
			if (direction < 0)
			{
				list.InsertRange(0, list2);
			}
			else
			{
				list.AddRange(list2);
			}
			return list;
		}

		private static List<SessionItem> CloneItemList(IList<SessionItem> items)
		{
			List<SessionItem> list = new List<SessionItem>();
			if (items == null)
			{
				return list;
			}
			for (int i = 0; i < items.Count; i++)
			{
				list.Add(CloneItem(items[i]));
			}
			return list;
		}

		private static List<SessionItem> SnapshotItemsDeep(StartupProfile profile)
		{
			List<SessionItem> list = new List<SessionItem>();
			if (profile == null || profile.Items == null)
			{
				return list;
			}
			for (int i = 0; i < profile.Items.Count; i++)
			{
				list.Add(CloneItem(profile.Items[i]));
			}
			return list;
		}

		private static SessionItem CloneItem(SessionItem item)
		{
			if (item == null)
			{
				return null;
			}
			SessionItem sessionItem = new SessionItem();
			sessionItem.Id = item.Id;
			sessionItem.Kind = item.Kind;
			sessionItem.Sequence = item.Sequence;
			sessionItem.IsEnabled = item.IsEnabled;
			sessionItem.StartTimeUtc = item.StartTimeUtc;
			sessionItem.DisplayName = item.DisplayName;
			sessionItem.WindowTitle = item.WindowTitle;
			sessionItem.ExecutablePath = item.ExecutablePath;
			sessionItem.Arguments = item.Arguments;
			sessionItem.WorkingDirectory = item.WorkingDirectory;
			sessionItem.TargetPath = item.TargetPath;
			sessionItem.CapturedProcessId = item.CapturedProcessId;
			sessionItem.CaptureSource = item.CaptureSource;
			sessionItem.Url = item.Url;
			sessionItem.BrowserName = item.BrowserName;
			sessionItem.BrowserExecutablePath = item.BrowserExecutablePath;
			sessionItem.ParentId = item.ParentId;
			sessionItem.BrowserGroupKey = item.BrowserGroupKey;
			sessionItem.BrowserWindowIndex = item.BrowserWindowIndex;
			sessionItem.BrowserTabIndex = item.BrowserTabIndex;
			sessionItem.IsActiveBrowserTab = item.IsActiveBrowserTab;
			sessionItem.BrowserTabIdentityCaptured = item.BrowserTabIdentityCaptured;
			sessionItem.BrowserTabs = BrowserTabFingerprint.CloneList(item.BrowserTabs);
			sessionItem.BrowserWindowTitleSnapshot = item.BrowserWindowTitleSnapshot;
			sessionItem.BrowserBindingKind = item.BrowserBindingKind;
			sessionItem.BrowserBindingUpdatedAtUtc = item.BrowserBindingUpdatedAtUtc;
			sessionItem.ApplicationWindowIndex = item.ApplicationWindowIndex;
			sessionItem.WindowPlacement = item.WindowPlacement;
			return sessionItem;
		}

		private static bool SameItemList(IList<SessionItem> first, IList<SessionItem> second)
		{
			if (first == null || second == null || first.Count != second.Count)
			{
				return false;
			}
			for (int i = 0; i < first.Count; i++)
			{
				SessionItem sessionItem = first[i];
				SessionItem sessionItem2 = second[i];
				if (sessionItem == null || sessionItem2 == null)
				{
					if (!object.ReferenceEquals(sessionItem, sessionItem2))
					{
						return false;
					}
				}
				else if (!string.Equals(sessionItem.Id, sessionItem2.Id, StringComparison.OrdinalIgnoreCase) || !string.Equals(sessionItem.ParentId, sessionItem2.ParentId, StringComparison.OrdinalIgnoreCase) || !string.Equals(sessionItem.BrowserGroupKey, sessionItem2.BrowserGroupKey, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}
			return true;
		}


		private static void ResequenceWindowTabs(List<SessionItem> items, SessionItem window)
		{
			// 算法已经抽到 Shared/Core/BrowserPlan/BrowserTabOrder，自启动应用的转接清单也调用它。
			BrowserTabOrder.NormalizeWindowTabs(items, window);
		}
    }
}
