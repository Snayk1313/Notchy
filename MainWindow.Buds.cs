using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Bluetooth earbuds/headphones on the island: when they connect, a pill with happy eyes, the earbuds icon, the name
/// and the battery, the island's edge lighting up to the battery level (green / yellow / red). When the battery gets
/// low (15%, then 5%) the same pill turns red, with worried eyes, and its light breathes with a red glow around it.
/// When they disconnect, a quick "Buds3 desconectado" with the eyes looking away.
/// </summary>
public partial class MainWindow
{
    private static readonly Color BattGreen = Color.FromRgb(0x5E, 0xD6, 0x7D), BattYellow = Color.FromRgb(0xFF, 0xD3, 0x6B), BattRed = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private const double BudsWidth = 290, BudsOffWidth = 250;

    private readonly BluetoothAudioService _bt = new();
    private BudsInfo? _buds;

    private Grid _budsPanel = null!;
    private Rectangle _budsEyeL = null!, _budsEyeR = null!;
    private TransformGroup _budsEyeLT = null!, _budsEyeRT = null!;
    private Path _budsIcon = null!;
    private TextBlock _budsName = null!, _budsPct = null!;

    private static bool IsBudsMode(CompactMode m) => m is CompactMode.Buds or CompactMode.BudsLow or CompactMode.BudsOff;

    private static Color BatteryColor(int b) => b > 40 ? BattGreen : b > 15 ? BattYellow : BattRed;

    /// <summary>Two earbuds, one facing each way: round head with the speaker hole cut out, and a stem (24-unit grid).</summary>
    private static Geometry EarbudsGeometry()
    {
        Geometry Bud(double hx, double hy, bool stemRight)
        {
            double sx = stemRight ? hx + 1.2 : hx - 3.8;
            var body = new CombinedGeometry(GeometryCombineMode.Union,
                new EllipseGeometry(new Point(hx, hy), 3.7, 3.7),
                new RectangleGeometry(new Rect(sx, hy, 2.6, 10.5), 1.3, 1.3));
            var hole = new EllipseGeometry(new Point(stemRight ? hx - 1.4 : hx + 1.4, hy - 0.2), 1, 1);
            return new CombinedGeometry(GeometryCombineMode.Exclude, body, hole);
        }
        var g = new GeometryGroup();
        g.Children.Add(Bud(7.2, 6.5, stemRight: true));
        g.Children.Add(Bud(16.8, 7.6, stemRight: false));
        g.Freeze();
        return g;
    }

    private void InitBuds()
    {
        _budsPanel = new Grid { Height = 34, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };

        var eyes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 } };
        Rectangle Eye(out TransformGroup t, Thickness m)
        {
            t = new TransformGroup();
            t.Children.Add(new ScaleTransform());
            t.Children.Add(new RotateTransform());
            t.Children.Add(new TranslateTransform());
            return new Rectangle { Width = 6, Height = 10, RadiusX = 3, RadiusY = 3, Fill = Brushes.White, Margin = m, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = t };
        }
        _budsEyeL = Eye(out _budsEyeLT, new Thickness(0, 0, 5, 0));
        _budsEyeR = Eye(out _budsEyeRT, new Thickness(0));
        eyes.Children.Add(_budsEyeL);
        eyes.Children.Add(_budsEyeR);
        row.Children.Add(eyes);

        _budsIcon = new Path
        {
            Data = EarbudsGeometry(), Fill = Brushes.White, Width = 16, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(10, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_budsIcon);
        _budsName = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 };
        row.Children.Add(_budsName);
        _budsPanel.Children.Add(row);

        _budsPct = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0) };
        _budsPanel.Children.Add(_budsPct);
        ((Panel)CompactPanel.Parent).Children.Add(_budsPanel);

        _bt.Connected += i => Dispatcher.BeginInvoke(() => ShowBuds(CompactMode.Buds, i, 4));
        _bt.BatteryLow += i => Dispatcher.BeginInvoke(() => ShowBuds(CompactMode.BudsLow, i, 5));
        _bt.Disconnected += i => Dispatcher.BeginInvoke(() => ShowBuds(CompactMode.BudsOff, i, 2.5));
        _bt.Start();
    }

    private void ShowBuds(CompactMode mode, BudsInfo info, double seconds)
    {
        if (!S.BudsAlerts) return;
        _buds = info;
        string shortName = info.Name;
        _budsName.Text = mode switch
        {
            CompactMode.BudsLow => Loc.F("buds.low", FirstWord(info.Name)),
            CompactMode.BudsOff => Loc.F("buds.off", FirstWord(info.Name)),
            _ => shortName
        };
        _budsPct.Text = mode != CompactMode.BudsOff && info.Battery is int b ? b + "%" : "";
        _budsPct.Foreground = new SolidColorBrush(info.Battery is int b2 ? BatteryColor(b2) : Colors.White);
        _budsIcon.Data = info.Speaker ? Geometry.Parse("M7 3h10a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm5 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6zm0-6a1.3 1.3 0 1 0 0 2.6A1.3 1.3 0 0 0 12 6z") : EarbudsGeometry();
        _budsIcon.Fill = new SolidColorBrush(mode == CompactMode.BudsLow ? Color.FromRgb(0xFF, 0x8A, 0x80) : Colors.White);
        if (mode == CompactMode.Buds) SoundService.Play(Sfx.Hello);
        else if (mode == CompactMode.BudsLow) SoundService.Play(Sfx.Attention);
        ShowTransient(mode, "", "", seconds);
    }

    /// <summary>"Buds3 de Pedro" → "Buds3" for the short messages.</summary>
    private static string FirstWord(string name)
    {
        int i = name.IndexOf(' ');
        return i > 0 ? name[..i] : name;
    }

    /// <summary>Every frame while the pill shows: the eyes act out the moment.</summary>
    private void UpdateBudsFrame()
    {
        if (!(_state == IslandState.Compact && IsBudsMode(_compactMode))) return;
        double t = AnimNow;
        bool low = _compactMode == CompactMode.BudsLow, off = _compactMode == CompactMode.BudsOff;
        bool blink = (t + 0.3) % 4.0 < 0.12;
        var eye = low ? Color.FromRgb(0xFF, 0x8A, 0x80) : Colors.White;
        if (_budsEyeL.Fill is not SolidColorBrush eb || eb.Color != eye)
        {
            _budsEyeL.Fill = _budsEyeR.Fill = new SolidColorBrush(eye);
            if (_budsEyeL.Parent is FrameworkElement ep && ep.Effect is DropShadowEffect g) g.Color = eye;
        }
        // happy ^^ (short and lifted) when they connect, worried (tilted) when low, looking away when they leave
        double sy = blink ? 0.12 : _compactMode == CompactMode.Buds ? 0.5 : low ? 0.7 : 1;
        double ty = _compactMode == CompactMode.Buds ? -1.5 : low ? 1 : 0;
        double tx = off ? 3 : 0;
        for (int i = 0; i < 2; i++)
        {
            var tg = i == 0 ? _budsEyeLT : _budsEyeRT;
            var sc = (ScaleTransform)tg.Children[0];
            sc.ScaleY += (sy - sc.ScaleY) * Ease(0.35);
            ((RotateTransform)tg.Children[1]).Angle = low ? (i == 0 ? 12 : -12) : 0;
            var tr = (TranslateTransform)tg.Children[2];
            tr.X += (tx - tr.X) * Ease(0.2);
            tr.Y += (ty - tr.Y) * Ease(0.3);
        }
    }

    /// <summary>The island's edge light for the earbuds: the battery level in its colour (null = no light).</summary>
    private (Color color, double share, bool pulse)? BudsLight()
    {
        if (!IsBudsMode(_compactMode) || _compactMode == CompactMode.BudsOff || _buds?.Battery is not int b) return null;
        return (BatteryColor(b), b / 100.0, _compactMode == CompactMode.BudsLow);
    }
}
