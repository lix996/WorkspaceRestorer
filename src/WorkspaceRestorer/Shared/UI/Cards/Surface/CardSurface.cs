using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WorkspaceRestorer.UI.Cards.Surface
{
    /// <summary>
    /// 【公共件】工作方案 / 自启动方案 / 图标布局三类卡片的统一外框。
    /// 以「启动与恢复」的 headerBorder 为准：
    ///   当前项：ActiveSurface 底 + Accent 边 + 1px + 圆角 10 + 内边距 10；
    ///   其它项：Surface 底 + Border 边；
    ///   悬停：当前项 #DCEAEF，其它项 HoverSurface/HoverBorder。
    /// 各工作区只提供内容和动作，不再各写一套边框颜色/粗细。
    /// </summary>
    internal static class CardSurface
    {
        private static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached(
                "IsActive", typeof(bool), typeof(CardSurface),
                new PropertyMetadata(false));

        // 拖动卡片期间临时关掉 hover 底色：否则源卡片在整段拖动里一直保持 MouseEnter 的灰色，
        // 看起来像“两张卡都串了色”。只由需要的地方打开/关闭，默认行为不变。
        private static readonly DependencyProperty HoverEnabledProperty =
            DependencyProperty.RegisterAttached(
                "HoverEnabled", typeof(bool), typeof(CardSurface),
                new PropertyMetadata(true));

        internal static Border Create(bool active, bool interactive, double minHeight = 0.0)
        {
            Border border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10)
            };
            if (interactive)
            {
                border.Cursor = Cursors.Hand;
            }
            if (minHeight > 0.0)
            {
                border.MinHeight = minHeight;
            }
            SetActive(border, active);
            AttachHover(border, active);
            return border;
        }

        /// <summary>
        /// 切换卡片“当前项”外观（不重建卡片）。
        /// 悬停颜色也读同一个属性，所以调用后下一次 MouseEnter/Leave 不会又按旧状态画。
        /// </summary>
        internal static void SetActive(Border border, bool active)
        {
            if (border == null)
            {
                return;
            }
            border.SetValue(IsActiveProperty, active);
            ApplyBaseAppearance(border, active);
        }

        private static bool IsActive(Border border)
        {
            return border != null && (bool)border.GetValue(IsActiveProperty);
        }

        /// <summary>拖动期间关掉 hover 变色；传 true 恢复默认。</summary>
        internal static void SetHoverEnabled(Border border, bool enabled)
        {
            if (border == null)
            {
                return;
            }
            border.SetValue(HoverEnabledProperty, enabled);
        }

        private static bool IsHoverEnabled(Border border)
        {
            return border == null || (bool)border.GetValue(HoverEnabledProperty);
        }

        private static void ApplyBaseAppearance(Border border, bool active)
        {
            border.Background = active ? AppTheme.ActiveSurface : AppTheme.Surface;
            border.BorderBrush = active ? AppTheme.Accent : AppTheme.Border;
        }

        internal static void AttachHover(Border border, bool active)
        {
            if (border == null)
            {
                return;
            }
            border.MouseEnter += delegate
            {
                if (!IsHoverEnabled(border))
                {
                    return;
                }
                bool nowActive = IsActive(border);
                border.Background = nowActive ? AppTheme.NavigationActiveSurface : AppTheme.HoverSurface;
                if (!nowActive)
                {
                    border.BorderBrush = AppTheme.HoverBorder;
                }
            };
            border.MouseLeave += delegate
            {
                ApplyBaseAppearance(border, IsActive(border));
            };
        }
    }
}
