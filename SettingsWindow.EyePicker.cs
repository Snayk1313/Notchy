using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The custom eye colour: a pop-up under the "+" with a colour wheel (hue and saturation), a brightness strip
/// and a little island whose eyes take the colour live, instead of the old Windows colour dialog.
/// </summary>
public partial class SettingsWindow
{
    private const double WheelSize = 170, KnobSize = 18;
    private static BitmapSource? _wheel;

    private Popup OpenEyePicker(FrameworkElement anchor, Action<string> pick)
    {
        double h = 330, s = 0.45, v = 1;
        try { (h, s, v) = ColorUtil.ToHsv((Color)ColorConverter.ConvertFromString(S.EyeColor)); } catch { /* keep the default */ }
        v = Math.Max(v, 0.25);
        bool changed = false;

        // the little island with its eyes
        Rectangle Eye() => new() { Width = 7, Height = 12, RadiusX = 3.5, RadiusY = 3.5, Margin = new Thickness(4.5, 0, 4.5, 0) };
        var eyeL = Eye();
        var eyeR = Eye();
        var glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 9, Opacity = 0.9 };
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = glow };
        eyes.Children.Add(eyeL);
        eyes.Children.Add(eyeR);
        var island = new Border { Width = 96, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.Black, Child = eyes, Margin = new Thickness(0, 0, 0, 12) };

        // the wheel
        Ellipse Knob() => new() { Width = KnobSize, Height = KnobSize, Stroke = Brushes.White, StrokeThickness = 3, IsHitTestVisible = false, Effect = new DropShadowEffect { ShadowDepth = 1, BlurRadius = 4, Opacity = 0.5 } };
        var wheelKnob = Knob();
        var wheel = new Canvas { Width = WheelSize, Height = WheelSize, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        wheel.Children.Add(new Image { Source = _wheel ??= DrawWheel(), Width = WheelSize, Height = WheelSize });
        wheel.Children.Add(wheelKnob);

        // the brightness strip
        var stripFill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var stripKnob = Knob();
        var strip = new Canvas { Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 14, 0, 0) };
        var stripBar = new Border { Height = 16, CornerRadius = new CornerRadius(8), Background = stripFill };
        strip.Children.Add(stripBar);
        strip.Children.Add(stripKnob);
        strip.SizeChanged += (_, _) => stripBar.Width = strip.ActualWidth;

        var hex = new TextBlock { Foreground = Dim(0x99), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Popup? popup = null;
        var done = ActionButton(Loc.T("sel.done"), () => popup!.IsOpen = false);
        var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(done, Dock.Right);
        bottom.Children.Add(done);
        bottom.Children.Add(hex);

        var stack = new StackPanel();
        stack.Children.Add(island);
        stack.Children.Add(wheel);
        stack.Children.Add(strip);
        stack.Children.Add(bottom);

        string Hex() { var c = ColorUtil.FromHsv(h, s, v); return $"#{c.R:X2}{c.G:X2}{c.B:X2}"; }
        void Paint()
        {
            var c = ColorUtil.FromHsv(h, s, v);
            var brush = new SolidColorBrush(c);
            eyeL.Fill = eyeR.Fill = brush;
            glow.Color = c;
            stripFill.GradientStops = new GradientStopCollection { new(Colors.Black, 0), new(ColorUtil.FromHsv(h, s, 1), 1) };
            double a = h * Math.PI / 180, r = s * WheelSize / 2;
            Canvas.SetLeft(wheelKnob, WheelSize / 2 + Math.Cos(a) * r - KnobSize / 2);
            Canvas.SetTop(wheelKnob, WheelSize / 2 - Math.Sin(a) * r - KnobSize / 2);
            wheelKnob.Fill = new SolidColorBrush(ColorUtil.FromHsv(h, s, 1));
            Canvas.SetLeft(stripKnob, v * Math.Max(0, strip.ActualWidth - KnobSize)); // the knob stays inside the strip
            Canvas.SetTop(stripKnob, -1);
            stripKnob.Fill = brush;
            hex.Text = Hex();
        }
        strip.SizeChanged += (_, _) => Paint();

        void Drag(FrameworkElement area, Action<Point> apply)
        {
            void At(MouseEventArgs e) { apply(e.GetPosition(area)); changed = true; Paint(); }
            area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); At(e); };
            area.MouseMove += (_, e) => { if (area.IsMouseCaptured) At(e); };
            area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
        }
        Drag(wheel, p =>
        {
            double dx = p.X - WheelSize / 2, dy = WheelSize / 2 - p.Y;
            h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            s = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (WheelSize / 2));
        });
        Drag(strip, p => v = Math.Clamp((p.X - KnobSize / 2) / Math.Max(1, strip.ActualWidth - KnobSize), 0.25, 1));

        var card = new Border
        {
            Width = 214, Padding = new Thickness(14), Margin = new Thickness(14, 6, 14, 18),
            CornerRadius = new CornerRadius(14), Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x34)),
            BorderBrush = Dim(0x18), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { ShadowDepth = 6, BlurRadius = 24, Opacity = 0.55 },
            Child = stack, RenderTransformOrigin = new Point(0.5, 0), RenderTransform = new ScaleTransform(0.9, 0.9), Opacity = 0
        };
        popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            HorizontalOffset = -(214 + 28) / 2.0 + anchor.ActualWidth / 2, Child = card
        };
        TextElement.SetFontFamily(card, FontFamily); // a pop-up would take the "+" button's icon font
        popup.Closed += (_, _) => { if (changed) pick(Hex()); };
        Paint();
        popup.IsOpen = true;

        var dur = TimeSpan.FromMilliseconds(220);
        var scale = (ScaleTransform)card.RenderTransform;
        var pop = new DoubleAnimation(1, dur) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        return popup;
    }

#if DEBUG
    private Button? _eyePlus;

    /// <summary>Opens the picker on the Aparência page and saves a picture of the screen around the window.</summary>
    public async System.Threading.Tasks.Task EyeTestAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await System.Threading.Tasks.Task.Delay(900);
        _eyePlus!.BringIntoView();
        await System.Threading.Tasks.Task.Delay(400);
        string before = S.EyeColor;
        S.EyeColor = "#FF8FC8"; // in memory only, to see coloured eyes
        var popup = OpenEyePicker(_eyePlus, _ => { });
        S.EyeColor = before;
        await System.Threading.Tasks.Task.Delay(600);
        void Save(FrameworkElement root, string name)
        {
            var picture = new RenderTargetBitmap((int)(root.ActualWidth + root.Margin.Left + root.Margin.Right) * 2, (int)(root.ActualHeight + root.Margin.Top + root.Margin.Bottom) * 2, 192, 192, PixelFormats.Pbgra32);
            picture.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(picture));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            png.Save(file);
        }
        Save((FrameworkElement)Content, "eye-window");
        Save((FrameworkElement)popup.Child, "eye-picker");
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "eye.txt"), $"aberto={popup.IsOpen} ancora={_eyePlus.IsVisible}");
        popup.IsOpen = false;
        Application.Current.Shutdown();
    }
#endif

    /// <summary>Hue around, saturation outwards, full brightness; drawn once at 2x for sharp edges.</summary>
    private static BitmapSource DrawWheel()
    {
        const int n = (int)(WheelSize * 2);
        var px = new byte[n * n * 4];
        double c = n / 2.0;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                double dx = x + 0.5 - c, dy = c - (y + 0.5), r = Math.Sqrt(dx * dx + dy * dy) / c;
                double alpha = Math.Clamp((1 - r) * c, 0, 1); // soft 1 px edge
                if (alpha <= 0) continue;
                var col = ColorUtil.FromHsv((Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360, r, 1);
                int i = (y * n + x) * 4;
                px[i] = (byte)(col.B * alpha); px[i + 1] = (byte)(col.G * alpha); px[i + 2] = (byte)(col.R * alpha); px[i + 3] = (byte)(255 * alpha);
            }
        var bmp = BitmapSource.Create(n, n, 192, 192, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        return bmp;
    }
}
