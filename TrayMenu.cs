using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The tray icon's menu in Notchy's look (Windows' own menu can't be rounded or show a real switch): a small dark,
/// rounded window with Fluent icons, a soft hover, a blue switch for "Iniciar com o Windows" and "Sair" in red.
/// It opens above the tray icon and closes as soon as you click anywhere else.
/// </summary>
public sealed class TrayMenu : Window
{
    private static TrayMenu? _open;
    private static readonly Color Blue = Color.FromRgb(0x7F, 0xD0, 0xFF), Ink = Color.FromRgb(0x0B, 0x1A, 0x26), Red = Color.FromRgb(0xFF, 0x8A, 0x80);

    public static void ShowAtCursor()
    {
        _open?.Close();
        var m = new TrayMenu();
        _open = m;
        m.Closed += (_, _) => { if (_open == m) _open = null; };
        m.Show();
        m.Activate();
    }

    private TrayMenu()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        var list = new StackPanel { Margin = new Thickness(6) };
        list.Children.Add(Row("", Loc.T("menu.settings"), null, () =>
        {
            Close();
            App.OpenSettings();
        }));
        bool startup = StartupManager.IsEnabled;
        Border? sw = null;
        list.Children.Add(Row("", Loc.T("gen.startup"), sw = Switch(startup), () =>
        {
            startup = !startup;
            StartupManager.SetEnabled(startup);
            SetSwitch(sw!, startup);
        }));
        list.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), Margin = new Thickness(10, 4, 10, 4) });
        list.Children.Add(Row("", Loc.T("menu.quit"), null, () => Application.Current.Shutdown(), Red));

        var card = new Border
        {
            Child = list, CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x18, 0x18, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)), BorderThickness = new Thickness(1), MinWidth = 232,
            Margin = new Thickness(14), // room for the shadow
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.5 },
            RenderTransform = new TranslateTransform(0, 6)
        };
        Content = card;

        Loaded += (_, _) =>
        {
            // above the tray icon: its bottom-right corner at the mouse (kept on screen)
            var dpi = VisualTreeHelper.GetDpi(this);
            var p = System.Windows.Forms.Cursor.Position;
            var wa = SystemParameters.WorkArea;
            double x = p.X / dpi.DpiScaleX - ActualWidth + 14, y = p.Y / dpi.DpiScaleY - ActualHeight + 8;
            Left = Math.Clamp(x, wa.Left, wa.Right - ActualWidth);
            Top = Math.Clamp(y, wa.Top, wa.Bottom - ActualHeight + 14);
            var d = TimeSpan.FromMilliseconds(180);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
            ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        };
        Deactivated += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private Border Row(string glyph, string text, UIElement? right, Action click, Color? color = null)
    {
        var ink = new SolidColorBrush(color ?? Color.FromArgb(0xE6, 255, 255, 255));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = ink,
            VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        g.Children.Add(label);
        if (right != null)
        {
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
        }
        var row = new Border { Child = g, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return row;
    }

    private static Border Switch(bool on)
    {
        var sw = new Border { Width = 28, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new Ellipse { Width = 12, Height = 12, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center } };
        SetSwitch(sw, on);
        return sw;
    }

    private static void SetSwitch(Border sw, bool on)
    {
        sw.Background = new SolidColorBrush(on ? Blue : Color.FromArgb(0x30, 255, 255, 255));
        sw.Effect = on ? new DropShadowEffect { Color = Blue, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 } : null;
        var knob = (Ellipse)sw.Child;
        knob.Fill = new SolidColorBrush(on ? Ink : Color.FromRgb(0xC8, 0xC8, 0xC8));
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
}
