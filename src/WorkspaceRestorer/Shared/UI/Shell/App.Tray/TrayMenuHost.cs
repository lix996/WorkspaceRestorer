using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using WorkspaceRestorer.Infrastructure;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 托盘右键菜单。用独立的无边框 WPF 窗口承载，避免 ContextMenu 从托盘弹出时
    /// 没有合适的 PlacementTarget 而出现 0 尺寸；外观仍用 AppTheme 的颜色/圆角。
    /// </summary>
    internal sealed class TrayMenuHost : IDisposable
    {
        private readonly TrayMenuActions actions;
        private Window menuWindow;

        internal TrayMenuHost(TrayMenuActions actions)
        {
            if (actions == null)
            {
                throw new ArgumentNullException("actions");
            }
            this.actions = actions;
        }

        internal void ShowMenu()
        {
            CloseMenu();
            try
            {
                Window window = new Window();
                window.WindowStyle = WindowStyle.None;
                window.AllowsTransparency = true;
                window.Background = Brushes.Transparent;
                window.ShowInTaskbar = false;
                window.Topmost = true;
                window.ResizeMode = ResizeMode.NoResize;
                window.SizeToContent = SizeToContent.WidthAndHeight;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowActivated = true;
                window.Opacity = 0.0;
                window.Deactivated += delegate { CloseMenu(); };
                window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape)
                    {
                        CloseMenu();
                    }
                };

                Border card = new Border();
                card.Background = AppTheme.Surface;
                card.BorderBrush = AppTheme.Border;
                card.BorderThickness = new Thickness(1);
                card.CornerRadius = new CornerRadius(10);
                card.Padding = new Thickness(6);
                card.Margin = new Thickness(10);
                card.MinWidth = 210.0;
                card.Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 2,
                    Opacity = 0.16,
                    Color = Colors.Black
                };

                StackPanel panel = new StackPanel();
                card.Child = panel;
                window.Content = card;

                AddRow(panel, AppText.T(ShellTextKeys.TrayOpenMain), true, actions.ShowMain);
                AddSeparator(panel);
                AddRow(panel, AppText.T(ShellTextKeys.TraySaveProfile), Can(actions.CanSaveProfile), actions.SaveProfile);
                AddRow(panel, AppText.T(ShellTextKeys.TraySaveLayout), Can(actions.CanSaveLayout), actions.SaveLayout);
                AddSeparator(panel);
                AddRow(panel, AppText.T(ShellTextKeys.TrayExit), true, actions.Exit);

                menuWindow = window;
                window.Show();
                window.UpdateLayout();
                PositionWindow(window);
                window.Opacity = 1.0;
                window.Activate();
            }
            catch (Exception exception)
            {
                CloseMenu();
                AppLog.Write("[托盘菜单] 显示失败。", exception);
            }
        }

        public void Dispose()
        {
            CloseMenu();
        }

        private void CloseMenu()
        {
            Window window = menuWindow;
            menuWindow = null;
            if (window == null)
            {
                return;
            }
            try
            {
                window.Close();
            }
            catch
            {
            }
        }

        private void AddRow(StackPanel panel, string text, bool enabled, Action action)
        {
            Button button = new Button();
            button.Content = text;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(14, 9, 14, 9);
            button.Margin = new Thickness(0, 1, 0, 1);
            button.MinHeight = 0;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.FontFamily = AppTheme.UiFont;
            button.FontSize = 13.0;
            button.Foreground = AppTheme.TextPrimary;
            button.Cursor = Cursors.Hand;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.IsEnabled = enabled && action != null;
            button.Template = BuildRowTemplate();
            button.Click += delegate
            {
                CloseMenu();
                if (action != null)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        AppLog.Write("[托盘菜单] 菜单动作失败。", exception);
                    }
                }
            };
            panel.Children.Add(button);
        }

        private static void AddSeparator(StackPanel panel)
        {
            Border line = new Border();
            line.Height = 1.0;
            line.Background = AppTheme.Border;
            line.Margin = new Thickness(10, 4, 10, 4);
            line.SnapsToDevicePixels = true;
            panel.Children.Add(line);
        }

        private static ControlTemplate BuildRowTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory frame = new FrameworkElementFactory(typeof(Border));
            frame.Name = "row";
            frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            frame.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            frame.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            frame.AppendChild(content);
            template.VisualTree = frame;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new DynamicResourceExtension(ThemeResourceKeys.HoverSurface),
                "row"));
            template.Triggers.Add(hover);

            Trigger disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
            template.Triggers.Add(disabled);
            return template;
        }

        private static bool Can(Func<bool> check)
        {
            if (check == null)
            {
                return false;
            }
            try
            {
                return check();
            }
            catch
            {
                return false;
            }
        }

        private static void PositionWindow(Window window)
        {
            Drawing.Point cursor = Forms.Cursor.Position;
            double scaleX = 1.0;
            double scaleY = 1.0;
            using (Drawing.Graphics graphics = Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                scaleX = graphics.DpiX / 96.0;
                scaleY = graphics.DpiY / 96.0;
            }
            if (scaleX <= 0.0)
            {
                scaleX = 1.0;
            }
            if (scaleY <= 0.0)
            {
                scaleY = 1.0;
            }

            double left = cursor.X / scaleX;
            double top = cursor.Y / scaleY;
            window.Left = left;
            window.Top = top;
            window.UpdateLayout();

            double width = window.ActualWidth * scaleX;
            double height = window.ActualHeight * scaleY;
            Forms.Screen screen = Forms.Screen.FromPoint(cursor);
            Drawing.Rectangle workingArea = screen.WorkingArea;
            if (cursor.X + width > workingArea.Right)
            {
                left = (workingArea.Right - width) / scaleX;
            }
            if (cursor.Y + height > workingArea.Bottom)
            {
                top = (workingArea.Bottom - height) / scaleY;
            }
            double minLeft = workingArea.Left / scaleX;
            double minTop = workingArea.Top / scaleY;
            if (left < minLeft)
            {
                left = minLeft;
            }
            if (top < minTop)
            {
                top = minTop;
            }
            window.Left = left;
            window.Top = top;
        }
    }
}
