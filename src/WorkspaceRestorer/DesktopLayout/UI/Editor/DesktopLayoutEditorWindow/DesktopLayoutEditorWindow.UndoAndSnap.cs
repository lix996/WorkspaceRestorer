using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WorkspaceRestorer.Desktop;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;
using Microsoft.Win32;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 图标布局编辑器（弹出页）。
    ///
    /// 【2026-10-20 用户要求】“编辑后，布局要和截图的小一致，图标也是（就是等比例缩放）”。
    /// 所以这里不再把截图按格子裁成一块块壁纸方块，而是：
    ///   · 画布 = 桌面的真实像素坐标（virtualBounds），桌面上每个格子的位置就是截图里的位置；
    ///   · 背景 = 当前壁纸（没有壁纸时用截图模糊版），不放带图标的截图，避免和编辑后的图标重影；
    ///   · 图标 = 从 Windows 取透明背景的大图标，按网格宽度等比缩放，文字也按同一比例缩放；
    ///   · 默认“适应窗口”，一打开就能看到整张桌面，和卡片截图是同一个比例。
    /// </summary>
    internal sealed partial class DesktopLayoutEditorWindow : Window
    {

        private DesktopIconPositionsState CapturePositions()
        {
            int count = snapshot.Icons.Count;
            DesktopIconPositionsState state = new DesktopIconPositionsState();
            state.Items = new DesktopIconPlacement[count];
            state.X = new int[count];
            state.Y = new int[count];
            state.UnplacedNewItems = new HashSet<DesktopIconPlacement>(unplacedNewItems);
            for (int index = 0; index < count; index++)
            {
                DesktopIconPlacement item = snapshot.Icons[index];
                state.Items[index] = item;
                if (item != null)
                {
                    state.X[index] = item.X;
                    state.Y[index] = item.Y;
                }
            }
            return state;
        }

        private void PushUndoState(DesktopIconPositionsState state)
        {
            if (state == null)
            {
                return;
            }
            undoStates.Add(state);
            if (undoStates.Count > 100)
            {
                undoStates.RemoveAt(0);
            }
            redoStates.Clear();
        }

        private void RestorePositions(DesktopIconPositionsState positions)
        {
            if (positions == null || positions.Items == null)
            {
                return;
            }
            for (int index = 0; index < positions.Items.Length; index++)
            {
                DesktopIconPlacement item = positions.Items[index];
                if (item == null)
                {
                    continue;
                }
                item.X = positions.X[index];
                item.Y = positions.Y[index];
                Border tile;
                if (tileByItem.TryGetValue(item, out tile))
                {
                    Canvas.SetLeft(tile, item.X - virtualBounds.Left);
                    Canvas.SetTop(tile, item.Y - virtualBounds.Top);
                    // 撤销“把新图标拖进画布”后，它要回到标签里：
                    // 隐藏画布 tile，但保留对象，重做/再次拖入时直接显示。
                    tile.Visibility = positions.UnplacedNewItems != null
                        && positions.UnplacedNewItems.Contains(item)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }
                UpdatePlacementOffsets(item);
            }
            unplacedNewItems.Clear();
            if (positions.UnplacedNewItems != null)
            {
                foreach (DesktopIconPlacement item in positions.UnplacedNewItems)
                {
                    if (item != null)
                    {
                        unplacedNewItems.Add(item);
                    }
                }
            }
            RebuildNewIconCards();
            state.MarkLayoutChanged();
        }

        private void UndoLayoutEdit()
        {
            if (undoStates.Count == 0)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorNothingToUndo);
                return;
            }
            redoStates.Add(CapturePositions());
            DesktopIconPositionsState state = undoStates[undoStates.Count - 1];
            undoStates.RemoveAt(undoStates.Count - 1);
            RestorePositions(state);
            statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorUndone);
        }

        private void RedoLayoutEdit()
        {
            if (redoStates.Count == 0)
            {
                statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorNothingToRedo);
                return;
            }
            undoStates.Add(CapturePositions());
            DesktopIconPositionsState state = redoStates[redoStates.Count - 1];
            redoStates.RemoveAt(redoStates.Count - 1);
            RestorePositions(state);
            statusText.Text = AppText.T(DesktopLayoutTextKeys.EditorRedone);
        }

        private static bool IsTextEditing()
        {
            return Keyboard.FocusedElement is TextBox;
        }

        private void SnapTile(
            Border tile,
            DesktopIconPlacement item,
            double startLeft,
            double startTop)
        {
            double left = Canvas.GetLeft(tile);
            double top = Canvas.GetTop(tile);
            if (double.IsNaN(left))
            {
                left = 0;
            }
            if (double.IsNaN(top))
            {
                top = 0;
            }
            // 拖拽释放同样只按当前网格吸附，不再保留拖动前的子格偏移。
            // 旧偏移会让图标落在“看起来差半格”的位置，和相邻图标重叠。
            left = gridOriginX
                + Math.Round((left - gridOriginX) / gridWidth) * gridWidth;
            top = gridOriginY
                + Math.Round((top - gridOriginY) / gridHeight) * gridHeight;
            left = Clamp(left, 0, Math.Max(0, canvasWidth - gridWidth));
            top = Clamp(top, 0, Math.Max(0, canvasHeight - gridHeight));

            // 桌面上一个格子只能放一个图标；拖到已占用的格子时，
            // 自动放到最近的空格，避免保存后两个图标叠在同一个位置。
            // 但如果只是点了一下没拖动，就保持在原格，不能把图标“挤”到旁边。
            bool reallyMoved = Math.Abs(left - startLeft) > 0.5 || Math.Abs(top - startTop) > 0.5;
            double freeLeft;
            double freeTop;
            if (TryFindFreeCell(left, top, item, reallyMoved, startLeft, startTop, out freeLeft, out freeTop))
            {
                left = freeLeft;
                top = freeTop;
            }

            Canvas.SetLeft(tile, left);
            Canvas.SetTop(tile, top);
            item.X = (int)Math.Round(left) + virtualBounds.Left;
            item.Y = (int)Math.Round(top) + virtualBounds.Top;
            UpdatePlacementOffsets(item);
        }

        private bool TryFindFreeCell(
            double baseLeft,
            double baseTop,
            DesktopIconPlacement self,
            bool avoidOldCell,
            double oldLeft,
            double oldTop,
            out double freeLeft,
            out double freeTop)
        {
            freeLeft = baseLeft;
            freeTop = baseTop;
            bool oldCellIsFree = false;
            const int maxRadius = 16;
            for (int radius = 0; radius <= maxRadius; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        if (radius > 0 && Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                        {
                            continue;
                        }
                        double left = baseLeft + dx * gridWidth;
                        double top = baseTop + dy * gridHeight;
                        if (left < -0.5 || top < -0.5
                            || left + gridWidth > canvasWidth + 0.5
                            || top + gridHeight > canvasHeight + 0.5)
                        {
                            continue;
                        }
                        bool isOldCell = Math.Abs(left - oldLeft) < 0.5 && Math.Abs(top - oldTop) < 0.5;
                        if (isOldCell && avoidOldCell)
                        {
                            if (!IsCellOccupied(left, top, self))
                            {
                                oldCellIsFree = true;
                            }
                            continue;
                        }
                        if (!IsCellOccupied(left, top, self))
                        {
                            freeLeft = left;
                            freeTop = top;
                            return true;
                        }
                    }
                }
            }
            // 附近只有原位是空的：退回原位，至少不会和别的图标重叠。
            if (oldCellIsFree)
            {
                freeLeft = oldLeft;
                freeTop = oldTop;
                return true;
            }
            return false;
        }

        private bool IsCellOccupied(double left, double top, DesktopIconPlacement self)
        {
            if (snapshot.Icons == null)
            {
                return false;
            }
            for (int index = 0; index < snapshot.Icons.Count; index++)
            {
                DesktopIconPlacement other = snapshot.Icons[index];
                if (other == null || object.ReferenceEquals(other, self))
                {
                    continue;
                }
                double otherLeft = other.X - virtualBounds.Left;
                double otherTop = other.Y - virtualBounds.Top;
                if (Math.Abs(otherLeft - left) < gridWidth * 0.5
                    && Math.Abs(otherTop - top) < gridHeight * 0.5)
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdatePlacementOffsets(DesktopIconPlacement item)
        {
            DisplayTopology topology = snapshot.Topology;
            DisplayMonitor monitor = FindMonitor(topology, item.X, item.Y);
            if (monitor == null || monitor.Bounds == null)
            {
                return;
            }
            item.MonitorKey = monitor.StableKey;
            item.MonitorOffsetX = item.X - monitor.Bounds.Left;
            item.MonitorOffsetY = item.Y - monitor.Bounds.Top;
            item.NormalizedX = monitor.Bounds.Width <= 0 ? 0.0 : (double)item.MonitorOffsetX / monitor.Bounds.Width;
            item.NormalizedY = monitor.Bounds.Height <= 0 ? 0.0 : (double)item.MonitorOffsetY / monitor.Bounds.Height;
        }

        private static DisplayMonitor FindMonitor(DisplayTopology topology, int x, int y)
        {
            if (topology == null)
            {
                return null;
            }
            if (topology.Monitors != null)
            {
                for (int index = 0; index < topology.Monitors.Count; index++)
                {
                    DisplayMonitor monitor = topology.Monitors[index];
                    if (monitor != null && monitor.Bounds != null && monitor.Bounds.Contains(x, y))
                    {
                        return monitor;
                    }
                }
            }
            return topology.GetPrimaryMonitor();
        }

        /// <summary>
        /// 快捷方式的名字经常是“目标.exe - 快捷方式”。Windows 桌面会把过长的目标名
        /// 截成“Computer...”，第二行保留“- 快捷方式”；直接交给 TextBlock 换行会把
        /// 单词从中间劈开（ComputerS / tartupKee...），看起来不像桌面。这里按 Windows
        /// 的规则预排两行：第一行按宽度截断，第二行放“- 快捷方式”。
        /// </summary>
        private TextBlock CreateDesktopLabel(string text, double width, bool preformatted, Brush foreground)
        {
            return new TextBlock
            {
                Text = text,
                Width = width,
                FontFamily = AppFonts.DesktopLabel,
                FontSize = labelFontSize,
                FontWeight = FontWeights.Normal,
                Foreground = foreground,
                TextAlignment = TextAlignment.Center,
                TextWrapping = preformatted ? TextWrapping.NoWrap : TextWrapping.Wrap,
                TextTrimming = preformatted ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                LineHeight = labelFontSize * 1.14,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = labelFontSize * 1.14 * 2.0,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0)
            };
        }

        private static string FormatDesktopLabel(string text, double maxWidth, double fontSize)
        {
            if (string.IsNullOrWhiteSpace(text) || MeasureLabel(text, fontSize) <= maxWidth)
            {
                return text;
            }
            int separator = text.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0)
            {
                string first = text.Substring(0, separator).TrimEnd();
                string second = text.Substring(separator).TrimStart();
                if (first.Length > 0 && second.Length > 0)
                {
                    return FitLabelLine(first, maxWidth, fontSize)
                        + "\n" + FitLabelLine(second, maxWidth, fontSize);
                }
            }
            return text;
        }

        private static string FitLabelLine(string text, double maxWidth, double fontSize)
        {
            if (MeasureLabel(text, fontSize) <= maxWidth)
            {
                return text;
            }
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                string candidate = text.Substring(0, middle).TrimEnd() + "\u2026";
                if (MeasureLabel(candidate, fontSize) <= maxWidth)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return text.Substring(0, low).TrimEnd() + "\u2026";
        }

        private static double MeasureLabel(string text, double fontSize)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            Typeface typeface = new Typeface(
                AppFonts.DesktopLabel,
                FontStyles.Normal,
                FontWeights.Normal,
                FontStretches.Normal);
            FormattedText formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                Brushes.White,
                1.0);
            return formatted.WidthIncludingTrailingWhitespace;
        }

        private static double PositiveModulo(double value, double divisor)
        {
            if (divisor <= 0)
            {
                return 0;
            }
            double result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            return value > maximum ? maximum : value;
        }

    }
}
