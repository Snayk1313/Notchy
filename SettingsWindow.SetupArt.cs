using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The assistant's little pictures: under each step's title, a strip of "desktop" with a small island doing what the step is
/// about (it changes size and colour as you choose, a notification drops, the outline glows when something is copied…).
/// </summary>
public sealed partial class SettingsWindow
{
    private const double ArtHeight = 92;
    private static readonly Color ArtClip = Color.FromRgb(0x8F, 0xB8, 0xFF);

    private string _artKey = "";
    private FrameworkElement? _art;
    private Action<double, double>? _artFrame; // (seconds since the step's picture began, seconds since the last frame)
    private readonly System.Diagnostics.Stopwatch _artClock = System.Diagnostics.Stopwatch.StartNew();
    private double _artStepAt, _tipAt;

    private bool Joy => DateTime.Now < _joyUntil;

    private static SolidColorBrush ArtWhite(byte a) => new(Color.FromArgb(a, 255, 255, 255));
    private static double Ease(ref double now, double to, double dt, double seconds = 0.12) => now += (to - now) * (1 - Math.Exp(-dt / seconds));

    /// <summary>The small island of the pictures: it springs to the size, place and colour it is told.</summary>
    private sealed class MiniIsland
    {
        public readonly Grid Root = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        /// <summary>What it shows besides its eyes (cut to its shape).</summary>
        public readonly Grid Inside = new();

        // what it is told; eased every frame
        public double W = 98, H = 26, EyeX, EyeY, LookX, EyesOpacity = 1, Light;
        public bool Floating, Happy, Closed;
        public Color Eye = Colors.White, LightColor = Colors.White;

        public double NowW => _w;
        public double NowH => _h;

        private readonly Border _body, _light;
        private readonly StackPanel _eyeRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Rectangle[] _eyes = new Rectangle[2];
        private readonly Path[] _smiles = new Path[2];
        private readonly ScaleTransform _blink = new(1, 1), _squash = new(1, 1);
        private readonly EyeFlip _flip = new();
        private readonly TranslateTransform _look = new(), _place = new();
        private readonly DropShadowEffect _eyeGlow = new() { ShadowDepth = 0, BlurRadius = 7, Opacity = 0.9 };
        private readonly DropShadowEffect _lightGlow = new() { ShadowDepth = 0, BlurRadius = 12, Opacity = 1 };
        private readonly SolidColorBrush _eyeBrush = new(Colors.White), _lightBrush = new(Colors.White);
        private double _w = 98, _h = 26, _vw, _vh, _float, _eyeX, _eyeY, _eyesOpacity = 1, _lightNow, _blinkNow = 1;
        private Color _eye = Colors.White;

        public MiniIsland()
        {
            for (int i = 0; i < 2; i++)
            {
                var cell = new Grid { Width = 9, Height = 12, Margin = new Thickness(2, 0, 2, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _squash };
                _eyes[i] = new Rectangle { Width = 6, Height = 10, RadiusX = 3, RadiusY = 3, Fill = _eyeBrush, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _blink,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _smiles[i] = new Path { Data = Geometry.Parse("M0,6 Q4.5,0 9,6"), Stroke = _eyeBrush, StrokeThickness = 2.1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                cell.Children.Add(_eyes[i]);
                cell.Children.Add(_smiles[i]);
                _eyeRow.Children.Add(cell);
            }
            // (the glow sits on the part that stays still, so the eyes move smoothly under it)
            var eyes = new Grid { Effect = _eyeGlow, IsHitTestVisible = false };
            _eyeRow.RenderTransform = _look;
            eyes.Children.Add(_eyeRow);
            var content = new Grid();
            content.Children.Add(Inside);
            content.Children.Add(eyes);
            _body = new Border { Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)), Child = content, Width = _w, Height = _h };
            _light = new Border { BorderThickness = new Thickness(2), BorderBrush = _lightBrush, Effect = _lightGlow, Opacity = 0, IsHitTestVisible = false };
            Root.Children.Add(_body);
            Root.Children.Add(_light);
            Root.RenderTransform = _place;
        }

        public void Update(double dt)
        {
            dt = Math.Clamp(dt, 0.001, 0.05);
            // a little spring for the size (it overshoots a touch, like the real island opening)
            _vw += ((W - _w) * 300 - _vw * 24) * dt;
            _vh += ((H - _h) * 300 - _vh * 24) * dt;
            _w = Math.Max(20, _w + _vw * dt);
            _h = Math.Max(12, _h + _vh * dt);
            double k = 1 - Math.Exp(-dt / 0.1), fast = 1 - Math.Exp(-dt / 0.035);
            _float += ((Floating ? 1 : 0) - _float) * k;
            _eyeX += (EyeX + LookX - _eyeX) * k;
            _eyeY += (EyeY - _eyeY) * k;
            _eyesOpacity += (EyesOpacity - _eyesOpacity) * k;
            _lightNow += (Light - _lightNow) * (1 - Math.Exp(-dt / (Light > _lightNow ? 0.05 : 0.22)));
            _blinkNow += ((Closed ? 0.12 : 1) - _blinkNow) * fast;
            _eye = Color.FromRgb((byte)(_eye.R + (Eye.R - _eye.R) * k), (byte)(_eye.G + (Eye.G - _eye.G) * k), (byte)(_eye.B + (Eye.B - _eye.B) * k));

            _body.Width = _w;
            _body.Height = _h;
            double r = Math.Min(_h / 2, 15), top = r * _float;
            _body.CornerRadius = _light.CornerRadius = new CornerRadius(top, top, r, r);
            _light.BorderThickness = new Thickness(2, 2 * _float, 2, 2); // (no light along the screen's edge)
            _place.Y = 8 * _float;
            _look.X = _eyeX;
            _look.Y = _eyeY;
            _eyeRow.Opacity = Math.Clamp(_eyesOpacity, 0, 1);
            _blink.ScaleY = _blinkNow;
            _eyeBrush.Color = _eye;
            _eyeGlow.Color = _eye;
            _light.Opacity = Math.Clamp(_lightNow, 0, 1);
            _lightBrush.Color = LightColor;
            _lightGlow.Color = LightColor;
            var (shape, squash) = _flip.Step(Happy ? 1 : 0, dt);
            _squash.ScaleY = squash;
            foreach (var e in _eyes) e.Visibility = shape == 1 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var s in _smiles) s.Visibility = shape == 1 ? Visibility.Visible : Visibility.Collapsed;
            // (glued to the top: square corners up there)
            double up = r * (1 - _float);
            Inside.Clip = new RectangleGeometry(new Rect(0, -up, _w, _h + up), r, r);
        }
    }

    /// <summary>The step's picture; drawn again for the same step (an option showed others), the same one goes on from where it was.</summary>
    private FrameworkElement SetupArt(string key)
    {
        if (_art != null && _artKey == key)
        {
            (_art.Parent as Panel)?.Children.Remove(_art);
            _art.BeginAnimation(OpacityProperty, null);
            _art.Opacity = 1;
            return _art;
        }
        _artKey = key;
        _artStepAt = _tipAt = _artClock.Elapsed.TotalSeconds;
        var stage = new Grid { ClipToBounds = true };
        var island = new MiniIsland();
        stage.Children.Add(island.Root);
        _artFrame = key switch
        {
            "hello" => HelloArt(island),
            "look" => LookArt(island),
            "notif" => NoticeArt(island),
            "clip" => ClipArt(stage, island),
            "extras" => DayArt(island),
            "claude" => ClaudeArt(stage, island),
            _ => TourArt(island)
        };
        _artFrame(0, 0.016);
        _art = new Border
        {
            Height = ArtHeight, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 9), Child = stage,
            BorderBrush = ArtWhite(0x12), BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromRgb(0x5B, 0x6F, 0x9C), 0), new(Color.FromRgb(0x39, 0x40, 0x6A), 0.55), new(Color.FromRgb(0x2A, 0x2C, 0x4A), 1)
            }, new Point(0, 0), new Point(1, 1))
        };
        return _art;
    }

    private void SetupArtFrame(double dt)
    {
        if (_artFrame == null || !IsVisible) return;
        _artFrame(_artClock.Elapsed.TotalSeconds - _artStepAt, dt);
    }

    private static Color EyeColorNow()
    {
        try { return (Color)ColorConverter.ConvertFromString(S.EyeColor); }
        catch { return Colors.White; }
    }

    private static Border KeyCap(string text) => new()
    {
        CornerRadius = new CornerRadius(5), Background = ArtWhite(0x26), BorderBrush = ArtWhite(0x22), BorderThickness = new Thickness(0, 0, 0, 2), Padding = new Thickness(6, 1, 6, 1),
        Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
    };

    // ------------------------------------------------------------------ each step's picture

    /// <summary>Boas-vindas: it wakes up, looks around and smiles.</summary>
    private Action<double, double> HelloArt(MiniIsland isl) => (t, dt) =>
    {
        double p = t % 6.4;
        isl.Closed = p < 0.9 || (p > 4.9 && p < 5.03);
        isl.LookX = p is > 1.5 and < 2.4 ? -5 : p is > 2.4 and < 3.3 ? 5 : 0;
        isl.Happy = Joy || (p > 3.5 && p < 4.5);
        isl.EyeY = isl.Happy ? -1 : 0;
        isl.Update(dt);
    };

    /// <summary>Aparência: the size, the style and the eyes' colour, as they are chosen.</summary>
    private Action<double, double> LookArt(MiniIsland isl) => (t, dt) =>
    {
        (isl.W, isl.H) = S.Size switch { IslandSize.Compact => (78.0, 22.0), IslandSize.Large => (124.0, 31.0), _ => (98.0, 26.0) };
        isl.Floating = S.Style == IslandStyle.Floating;
        isl.Eye = EyeColorNow();
        isl.Happy = Joy;
        isl.Closed = t % 4.3 < 0.13;
        isl.Update(dt);
    };

    /// <summary>Notificações: the island drops into a card with who sent it (it stays still while they're off).</summary>
    private Action<double, double> NoticeArt(MiniIsland isl)
    {
        var avatar = new Ellipse
        {
            Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            Fill = new LinearGradientBrush(Color.FromRgb(0x25, 0xD3, 0x66), Color.FromRgb(0x12, 0x8C, 0x7E), 45)
        };
        var name = new TextBlock { Text = "Maria", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(47, 10, 0, 0) };
        var line = new Border { Width = 104, Height = 5, CornerRadius = new CornerRadius(2.5), Background = ArtWhite(0x80), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(47, 30, 0, 0) };
        var time = new TextBlock { Text = Loc.T("notif.now"), FontSize = 8.5, Foreground = ArtWhite(0x77), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11, 13, 0) };
        var card = new Grid { Opacity = 0 };
        card.Children.Add(avatar);
        card.Children.Add(name);
        card.Children.Add(line);
        card.Children.Add(time);
        isl.Inside.Children.Add(card);
        double shown = 0;
        return (t, dt) =>
        {
            double p = t % 5.0;
            bool card1 = S.NotifyEnabled && p is > 0.7 and < 3.9;
            isl.W = card1 ? 216 : 98;
            isl.H = card1 ? 50 : 26;
            isl.EyesOpacity = card1 ? 0 : 1;
            card.Opacity = Ease(ref shown, card1 && isl.NowW > 170 ? 1 : 0, dt, 0.09);
            isl.Happy = Joy && !card1;
            isl.Closed = !card1 && (p > 4.5 && p < 4.63);
            isl.Update(dt);
        };
    }

    /// <summary>Copiados: Ctrl+C, the outline glows, and the copy slides out under the island (nothing happens while it's off).</summary>
    private Action<double, double> ClipArt(Grid stage, MiniIsland isl)
    {
        var press = new ScaleTransform(1, 1);
        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 12),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = press
        };
        keys.Children.Add(KeyCap("Ctrl"));
        keys.Children.Add(KeyCap("C"));
        stage.Children.Insert(0, keys);

        var slide = new TranslateTransform();
        var row = new Grid();
        row.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ArtClip), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) });
        row.Children.Add(new Border { Width = 74, Height = 5, CornerRadius = new CornerRadius(2.5), Background = ArtWhite(0x99), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(29, 0, 0, 0) });
        var chip = new Border
        {
            Width = 124, Height = 26, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)), Child = row, Opacity = 0, RenderTransform = slide,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0)
        };
        stage.Children.Insert(0, chip);
        double down = 1, seen = 0;
        return (t, dt) =>
        {
            double p = t % 4.4;
            bool on = S.ClipEnabled;
            press.ScaleX = press.ScaleY = Ease(ref down, p is > 0.6 and < 0.85 ? 0.88 : 1, dt, 0.04);
            keys.Opacity = p is > 0.6 and < 1.1 ? 1 : 0.6;
            isl.LightColor = ArtClip;
            isl.Light = on && p is > 0.85 and < 1.25 ? 1 : 0;
            isl.Closed = on && p is > 0.9 and < 1.03;
            Ease(ref seen, on && p is > 1.25 and < 3.6 ? 1 : 0, dt, 0.12);
            chip.Opacity = seen;
            slide.Y = -9 * (1 - seen);
            isl.Happy = Joy;
            isl.Update(dt);
        };
    }

    /// <summary>Calendário e clima: the open island's top line, with the weather on one side and today's date on the other.</summary>
    private Action<double, double> DayArt(MiniIsland isl)
    {
        var sunGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 9, Color = Color.FromRgb(0xFF, 0xD3, 0x6B), Opacity = 0.9 };
        var sun = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x6B)), Effect = sunGlow, VerticalAlignment = VerticalAlignment.Center };
        var temp = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var weather = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        weather.Children.Add(sun);
        weather.Children.Add(temp);

        var pulse = new ScaleTransform(1, 1);
        var holiday = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(0x5E, 0xD6, 0x7D)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = pulse };
        var agenda = new Ellipse { Width = 5, Height = 5, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        var dateText = new TextBlock { Text = DateTime.Now.ToString("ddd d MMM", Loc.Culture), FontSize = 9.5, FontWeight = FontWeights.SemiBold, Foreground = ArtWhite(0xDD), VerticalAlignment = VerticalAlignment.Center };
        var dateRow = new StackPanel { Orientation = Orientation.Horizontal };
        dateRow.Children.Add(holiday);
        dateRow.Children.Add(agenda);
        dateRow.Children.Add(dateText);
        var date = new Border { CornerRadius = new CornerRadius(8), Background = ArtWhite(0x1C), Padding = new Thickness(7, 2, 8, 3), Child = dateRow, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 11, 0) };
        var line = new Grid { Opacity = 0 };
        line.Children.Add(weather);
        line.Children.Add(date);
        isl.Inside.Children.Add(line);
        double shown = 0;
        return (t, dt) =>
        {
            isl.W = 250;
            isl.H = 30;
            line.Opacity = Ease(ref shown, isl.NowW > 210 ? 1 : 0, dt, 0.1);
            var now = Island?.WeatherForSetup;
            temp.Text = now == null ? "" : $"{Math.Round(now.Temp)}°";
            sunGlow.Opacity = 0.55 + 0.4 * Math.Sin(t * 2.2);
            holiday.Visibility = S.CalendarHolidays ? Visibility.Visible : Visibility.Collapsed;
            pulse.ScaleX = pulse.ScaleY = 1 + 0.35 * Math.Max(0, Math.Sin(t * 3.0));
            var connected = Island?.Calendar.Externals;
            agenda.Visibility = connected is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            if (connected is { Count: > 0 } && (agenda.Fill as SolidColorBrush)?.Color != connected[0].Tint) agenda.Fill = new SolidColorBrush(connected[0].Tint);
            double p = t % 6.0;
            isl.LookX = p is > 1.2 and < 2.6 ? 5 : p is > 3.4 and < 4.6 ? -5 : 0; // (at the date, then at the weather)
            isl.Happy = Joy;
            isl.Closed = p > 5.2 && p < 5.33;
            isl.Update(dt);
        };
    }

    /// <summary>Claude: its orange light runs around the island while it's connected.</summary>
    private Action<double, double> ClaudeArt(Grid stage, MiniIsland isl)
    {
        const double W = 112, H = 26, R = 13, Thick = 2;
        double length = 2 * (H - R) + Math.PI * R + (W - 2 * R);
        var orange = Color.FromRgb(0xFF, 0x9A, 0x6E);
        var outline = Geometry.Parse(FormattableString.Invariant($"M0,0 L0,{H - R} A{R},{R} 0 0 0 {R},{H} L{W - R},{H} A{R},{R} 0 0 0 {W},{H - R} L{W},0"));
        var track = new Path { Data = outline, Stroke = new SolidColorBrush(Color.FromArgb(0x50, orange.R, orange.G, orange.B)), StrokeThickness = Thick, Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        var light = new Path
        {
            Data = outline, Stroke = new SolidColorBrush(orange), StrokeThickness = Thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeDashCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { 34 / Thick, (length - 34) / Thick }, Width = W, Height = H, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 9, Color = orange, Opacity = 1 }
        };
        stage.Children.Add(track);
        stage.Children.Add(light);
        bool connected = false;
        double checkedAt = -10, on = 0;
        return (t, dt) =>
        {
            if (t - checkedAt > 1) // (it reads a file: not every frame)
            {
                checkedAt = t;
                connected = ClaudeHooks.IsInstalled;
            }
            isl.W = W;
            isl.H = H;
            isl.Eye = connected ? Color.FromRgb(0xFF, 0xB8, 0x94) : Colors.White;
            Ease(ref on, connected ? 1 : 0, dt, 0.25);
            light.Opacity = on;
            track.Opacity = 0.35 + 0.65 * on;
            light.StrokeDashOffset = -(t % 2.6 / 2.6) * (length / Thick);
            isl.LookX = connected ? 4 * Math.Sin(t * 2.4) : 0; // (following the light)
            isl.Happy = Joy;
            isl.Closed = t % 4.3 < 0.13;
            isl.Update(dt);
        };
    }

    /// <summary>Tour: the small island does what the lit tip says (opens, lights the date or the bell, shows the copies, the volume, a file landing).</summary>
    private Action<double, double> TourArt(MiniIsland isl)
    {
        var dateGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Color = Colors.White, Opacity = 0 };
        var bellGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Color = Colors.White, Opacity = 0 };
        var dateFill = ArtWhite(0x26);
        var bellFill = ArtWhite(0x26);
        var date = new Border { Width = 42, Height = 10, CornerRadius = new CornerRadius(5), Background = dateFill, Effect = dateGlow, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(13, 9, 0, 0) };
        var bell = new Ellipse { Width = 10, Height = 10, Fill = bellFill, Effect = bellGlow, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 13, 0) };

        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 34, 14, 0) };
        lines.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = ArtWhite(0x40), Margin = new Thickness(0, 0, 0, 8) });
        lines.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = ArtWhite(0x30), Width = 130, HorizontalAlignment = HorizontalAlignment.Left });

        var list = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 31, 14, 0), Opacity = 0 };
        for (int i = 0; i < 2; i++)
        {
            var item = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            item.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(4), Background = i == 0 ? new SolidColorBrush(ArtClip) : ArtWhite(0x33), HorizontalAlignment = HorizontalAlignment.Left });
            item.Children.Add(new Border { Height = 5, Width = i == 0 ? 150 : 110, CornerRadius = new CornerRadius(2.5), Background = ArtWhite(0x66), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(21, 0, 0, 0) });
            list.Children.Add(item);
        }

        var fall = new TranslateTransform();
        var file = new Border { Width = 17, Height = 21, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xC4)), RenderTransform = fall, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var zone = new Border { CornerRadius = new CornerRadius(10), BorderBrush = ArtWhite(0x44), BorderThickness = new Thickness(1.2), Background = ArtWhite(0x0C), Margin = new Thickness(14, 29, 14, 10), Child = file, Opacity = 0, ClipToBounds = false };

        var open = new Grid { Opacity = 0 };
        open.Children.Add(date);
        open.Children.Add(bell);
        open.Children.Add(lines);
        open.Children.Add(list);
        open.Children.Add(zone);
        isl.Inside.Children.Add(open);

        var fill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = Brushes.White, BorderThickness = new Thickness(0, 0, 2, 0),
            Background = new LinearGradientBrush(Color.FromArgb(0x0D, 255, 255, 255), Color.FromArgb(0x4C, 255, 255, 255), 0)
        };
        var number = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = ArtWhite(0xE0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var bar = new Grid { Opacity = 0 };
        bar.Children.Add(fill);
        bar.Children.Add(number);
        isl.Inside.Children.Add(bar);

        double openNow = 0, barNow = 0, linesNow = 1, listNow = 0, zoneNow = 0, dateNow = 0, bellNow = 0;
        return (t, dt) =>
        {
            string tip = _tip >= 0 && _tip < _tourKeys.Count ? _tourKeys[_tip] : "open";
            double since = _artClock.Elapsed.TotalSeconds - _tipAt;
            bool volume = tip == "volume";
            isl.W = volume ? 180 : 240;
            isl.H = volume ? 28 : 84;
            isl.EyeX = volume ? -64 : 0;
            isl.EyeY = volume ? 0 : -28;

            open.Opacity = Ease(ref openNow, !volume && isl.NowH > 60 ? 1 : 0, dt, 0.09);
            bar.Opacity = Ease(ref barNow, volume && isl.NowH < 40 ? 1 : 0, dt, 0.09);
            lines.Opacity = Ease(ref linesNow, tip is "open" or "date" or "bell" ? 1 : 0, dt);
            list.Opacity = Ease(ref listNow, tip == "clip" ? 1 : 0, dt);
            zone.Opacity = Ease(ref zoneNow, tip == "shelf" ? 1 : 0, dt);

            Ease(ref dateNow, tip == "date" ? 1 : 0, dt);
            Ease(ref bellNow, tip == "bell" ? 1 : 0, dt);
            dateFill.Color = Color.FromArgb((byte)(0x26 + 0xD9 * dateNow), 255, 255, 255);
            bellFill.Color = Color.FromArgb((byte)(0x26 + 0xD9 * bellNow), 255, 255, 255);
            dateGlow.Opacity = 0.9 * dateNow;
            bellGlow.Opacity = 0.9 * bellNow;

            isl.LightColor = ArtClip;
            isl.Light = tip == "clip" && since is > 0.25 and < 0.8 ? 1 : 0;

            double level = 0.38 + 0.3 * (0.5 - 0.5 * Math.Cos(since * Math.PI * 2 / 2.2));
            fill.Width = Math.Max(2, isl.NowW * level);
            number.Text = $"{Math.Round(level * 100)}";

            // the file comes down from above the island and lands with a little bounce
            double a = Math.Clamp((since - 0.35) / 0.55, 0, 1);
            double back = 1 + 2.2 * Math.Pow(a - 1, 3) + 1.2 * Math.Pow(a - 1, 2);
            fall.Y = tip == "shelf" ? -70 * (1 - back) : 0;

            isl.Happy = Joy;
            isl.Closed = t % 4.3 < 0.13;
            isl.Update(dt);
        };
    }
}
