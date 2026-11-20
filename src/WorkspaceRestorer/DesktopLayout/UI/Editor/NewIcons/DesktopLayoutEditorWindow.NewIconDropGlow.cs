using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using WorkspaceRestorer.Desktop;

namespace WorkspaceRestorer.UI
{
    internal sealed partial class DesktopLayoutEditorWindow
    {
        // ===== 2026-10-25：拖动预桌面图标/新图标卡片到“新图标标签”的交互 =====
        //
        // 需求（用户原话）：
        //   · 拖拽图标到新图标标签（含展开面板）时要“在鼠标下面”，并且标签/展开面板的
        //     边缘要微微亮起，告诉用户“已经进入可放入区域”；
        //   · 拖动不能只在预桌面范围内，整页都要跟手；松手不在预桌面内时，落到预桌面
        //     靠鼠标最近的边缘。
        //
        // 这个文件只做交互层：
        //   · 缓存标签/展开面板的窗口坐标矩形（拖动期间不再每 16ms 做 TransformToAncestor）；
        //   · 标签 + 展开面板的边缘发光；
        //   · 画布 tile 拖动时复用 NewIconTray 的全窗口浮卡。
        // 真正的落点计算仍在 DesktopLayoutEditorWindow / NewIconTray 里。

        private DropShadowEffect newIconTagBaseShadow;
        private bool newIconTagBaseShadowCaptured;
        private Brush newIconDockContentBaseBorder;
        private bool newIconDockContentBaseBorderCaptured;
        private DropShadowEffect newIconDropTagGlow;
        private DropShadowEffect newIconDropPanelGlow;
        private bool newIconDropGlowActive;
        private Rect newIconDropTagRect = Rect.Empty;
        private Rect newIconDropContentRect = Rect.Empty;
        private bool newIconDropRectsValid;

        /// <summary>
        /// 拖动开始时缓存标签和展开面板的窗口坐标矩形。
        /// 拖动期间标签不会移动，缓存一次就够；避免每 16ms 做一次
        /// TransformToAncestor + ActualWidth，4K/150% 下这是“卡”的来源之一。
        /// </summary>
        private void CaptureNewIconDropTargets()
        {
            newIconDropRectsValid = false;
            newIconDropTagRect = Rect.Empty;
            newIconDropContentRect = Rect.Empty;
            try
            {
                if (newIconTag != null && newIconTag.Visibility == Visibility.Visible)
                {
                    Point origin = newIconTag.TransformToAncestor(this).Transform(new Point(0.0, 0.0));
                    newIconDropTagRect = new Rect(
                        origin,
                        new Size(newIconTag.ActualWidth, newIconTag.ActualHeight));
                }
                if (newIconDockContent != null
                    && newIconDockContent.Visibility == Visibility.Visible)
                {
                    Point origin = newIconDockContent.TransformToAncestor(this)
                        .Transform(new Point(0.0, 0.0));
                    newIconDropContentRect = new Rect(
                        origin,
                        new Size(newIconDockContent.ActualWidth, newIconDockContent.ActualHeight));
                }
                newIconDropRectsValid = !newIconDropTagRect.IsEmpty
                    || !newIconDropContentRect.IsEmpty;
            }
            catch
            {
                newIconDropRectsValid = false;
            }
        }

        private void ClearNewIconDropTargets()
        {
            newIconDropRectsValid = false;
            newIconDropTagRect = Rect.Empty;
            newIconDropContentRect = Rect.Empty;
        }

        /// <summary>
        /// 鼠标（窗口坐标）是否在标签或展开面板上。
        /// 优先用拖动开始时缓存的矩形；缓存无效时退回实时测量。
        /// </summary>
        private bool IsNewIconDropPoint(Point windowPoint)
        {
            if (newIconDropRectsValid)
            {
                if (!newIconDropTagRect.IsEmpty && newIconDropTagRect.Contains(windowPoint))
                {
                    return true;
                }
                if (!newIconDropContentRect.IsEmpty
                    && newIconDropContentRect.Contains(windowPoint))
                {
                    return true;
                }
                return false;
            }

            try
            {
                if (newIconTag != null && newIconTag.Visibility == Visibility.Visible)
                {
                    Point origin = newIconTag.TransformToAncestor(this).Transform(new Point(0.0, 0.0));
                    if (new Rect(origin,
                        new Size(newIconTag.ActualWidth, newIconTag.ActualHeight)).Contains(windowPoint))
                    {
                        return true;
                    }
                }
                if (newIconDockContent != null
                    && newIconDockContent.Visibility == Visibility.Visible)
                {
                    Point origin = newIconDockContent.TransformToAncestor(this)
                        .Transform(new Point(0.0, 0.0));
                    if (new Rect(origin,
                        new Size(newIconDockContent.ActualWidth, newIconDockContent.ActualHeight))
                        .Contains(windowPoint))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// 拖动悬停在新图标标签/展开面板上时，给两者的外边缘加一圈很淡的强调色光。
        /// 收起时只亮标签；展开时标签和展开面板都亮。
        /// </summary>
        private void SetNewIconDropHighlight(bool active)
        {
            if (newIconTag == null)
            {
                return;
            }

            if (!newIconTagBaseShadowCaptured)
            {
                newIconTagBaseShadow = newIconTag.Effect as DropShadowEffect;
                newIconTagBaseShadowCaptured = true;
            }
            if (!newIconDockContentBaseBorderCaptured && newIconDockContent != null)
            {
                newIconDockContentBaseBorder = newIconDockContent.BorderBrush;
                newIconDockContentBaseBorderCaptured = true;
            }
            if (newIconDropTagGlow == null)
            {
                newIconDropTagGlow = CreateNewIconDropGlow();
            }
            if (newIconDropPanelGlow == null)
            {
                newIconDropPanelGlow = CreateNewIconDropGlow();
            }

            newIconDropGlowActive = active;
            if (active)
            {
                newIconTag.Effect = newIconDropTagGlow;
                newIconTag.BorderBrush = AppTheme.Accent;
                if (newIconDockContent != null)
                {
                    newIconDockContent.Effect = newIconDropPanelGlow;
                    newIconDockContent.BorderBrush = AppTheme.Accent;
                }
                AnimateNewIconDropGlow(newIconDropTagGlow, 0.55, true, null);
                AnimateNewIconDropGlow(newIconDropPanelGlow, 0.55, true, null);
            }
            else
            {
                newIconTag.BorderBrush = EditorPalette.NewIconBorder;
                if (newIconDockContent != null)
                {
                    newIconDockContent.BorderBrush = newIconDockContentBaseBorder
                        ?? AppTheme.Border;
                }
                AnimateNewIconDropGlow(newIconDropTagGlow, 0.0, false, delegate
                {
                    if (!newIconDropGlowActive)
                    {
                        newIconTag.Effect = newIconTagBaseShadow;
                        if (newIconDockContent != null)
                        {
                            newIconDockContent.Effect = null;
                        }
                    }
                });
                AnimateNewIconDropGlow(newIconDropPanelGlow, 0.0, false, null);
            }
        }

        private static DropShadowEffect CreateNewIconDropGlow()
        {
            return new DropShadowEffect
            {
                Color = Color.FromRgb(14, 116, 144),
                ShadowDepth = 0,
                Direction = 0,
                BlurRadius = 18,
                Opacity = 0.0
            };
        }

        private static void AnimateNewIconDropGlow(
            DropShadowEffect effect,
            double target,
            bool active,
            Action completed)
        {
            if (effect == null)
            {
                return;
            }
            DoubleAnimation fade = new DoubleAnimation(
                target,
                TimeSpan.FromMilliseconds(active ? 120 : 160));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (completed != null)
            {
                fade.Completed += delegate { completed(); };
            }
            effect.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
        }

        // ===== 画布 tile 拖动复用新图标卡片的全窗口浮卡 =====

        private void BeginIconDragGhost(
            DesktopIconPlacement item, Point windowPoint, Rect bounds)
        {
            // 画布图标属于预桌面，浮卡只在可见预桌面内移动；
            // 拖到新图标标签/面板时浮卡停在画布边缘，标签发光提示可投放。
            // 边界矩形由拖动入口缓存一次，拖动期间不再每帧 TransformToAncestor。
            ShowTrayDragGhost(item, windowPoint, bounds);
        }

        private void MoveIconDragGhost(Point windowPoint, Rect bounds)
        {
            PositionTrayDragGhost(windowPoint, bounds);
        }

        private void EndIconDragGhost()
        {
            HideTrayDragGhost();
        }

        /// <summary>
        /// 松手不在预桌面内时，把图标中心夹到预桌面内离鼠标最近的边缘。
        /// 之后仍由 SnapTile 吸附网格；目标格被占会找最近空格。
        /// </summary>
        private Point ClampIconCenterToCanvas(Point canvasPoint, double width, double height)
        {
            double halfWidth = Math.Max(0.0, width) / 2.0;
            double halfHeight = Math.Max(0.0, height) / 2.0;
            double minX = Math.Min(halfWidth, Math.Max(0.0, canvasWidth) / 2.0);
            double maxX = Math.Max(minX, canvasWidth - halfWidth);
            double minY = Math.Min(halfHeight, Math.Max(0.0, canvasHeight) / 2.0);
            double maxY = Math.Max(minY, canvasHeight - halfHeight);
            return new Point(
                Clamp(canvasPoint.X, minX, maxX),
                Clamp(canvasPoint.Y, minY, maxY));
        }
    }
}
