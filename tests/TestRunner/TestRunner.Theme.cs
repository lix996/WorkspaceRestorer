using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkspaceRestorer.UI;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunThemeTokenTests()
        {
            Run("主题：共享令牌原地改色，隐藏/延迟内容不会停在旧色", delegate
            {
                Application application = Application.Current;
                if (application == null)
                {
                    application = new Application();
                }

                ThemeService.Apply(ThemeMode.Light);
                SolidColorBrush surface = AppTheme.Surface;
                SolidColorBrush active = AppTheme.ActiveSurface;
                SolidColorBrush text = AppTheme.TextPrimary;

                foreach (PropertyInfo property in typeof(AppTheme).GetProperties(BindingFlags.Static | BindingFlags.Public))
                {
                    if (property.PropertyType != typeof(SolidColorBrush))
                    {
                        continue;
                    }
                    SolidColorBrush brush = property.GetValue(null, null) as SolidColorBrush;
                    Assert(brush != null, "主题颜色属性应返回 SolidColorBrush：" + property.Name);
                    Assert(!brush.IsFrozen, "主题令牌 Brush 不能被冻结：" + property.Name);
                }

                // 不在任何 Window 里的元素，模拟还没显示过的工作区卡片。
                Border hiddenCard = new Border { Background = surface };
                Border hiddenActiveCard = new Border { Background = active };
                TextBlock hiddenText = new TextBlock { Foreground = text };

                ThemeService.Apply(ThemeMode.Dark);

                AssertEqual(
                    AppTheme.Surface.Color.ToString(),
                    ((SolidColorBrush)hiddenCard.Background).Color.ToString());
                AssertEqual(
                    AppTheme.ActiveSurface.Color.ToString(),
                    ((SolidColorBrush)hiddenActiveCard.Background).Color.ToString());
                AssertEqual(
                    AppTheme.TextPrimary.Color.ToString(),
                    ((SolidColorBrush)hiddenText.Foreground).Color.ToString());
                Assert(AppTheme.Surface.Color != Colors.White, "深色主题表面色不应还是白色。");

                ThemeService.Apply(ThemeMode.Light);
                AssertEqual(
                    Colors.White.ToString(),
                    ((SolidColorBrush)hiddenCard.Background).Color.ToString());
            });
        }
    }
}
