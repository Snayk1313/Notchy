using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The menu icons come alive while something happens in their tab (only tabs you're not looking at):
/// the download arrow keeps falling, Claude's ✻ turns (and pulses when it needs you), a ring fills around the
/// focus timer, the chat icon bobs while it answers, the shelf glows softly while it holds files. A soft halo
/// in the tab's colour sits behind (a plain shape, so the icon itself stays crisp). When something finishes,
/// the icon hops and shows a check for a moment.
/// </summary>
public partial class MainWindow
{
    private sealed class NavIcon
    {
        public required TextBlock Icon;
        public required Ellipse Halo;
        public required Path Ring;
        public required Ellipse Track;
        public readonly SolidColorBrush Tint = new(Colors.White);
        // Downloads only: the icon drawn in two parts (arrow + bar) so the arrow can move while the bar fills
        public FrameworkElement? Art;
        // Focus only: the target, drawn (the font has none); the font icon stays for its size and for the check mark
        public FrameworkElement? Drawn;
        public TranslateTransform? ArtArrow;
        public Rectangle? ArtFill;
        public Rectangle? ArtTrack;
        public required ScaleTransform Scale;
        public required RotateTransform Rotate;
        public required TranslateTransform Move;
        public required string Glyph;
        public required FontFamily Font;
        public required double FontSize;
        public double Active;            // 0..1, eases in and out
        public double HopAt = -100;      // when the "done" hop started (AnimNow)
        public double DoneUntil = -100;  // a hop waiting to be seen (the island was closed), until then
        public bool CheckShown;
        public double RingDrawn = -1;
        public Color HaloColor;
    }

    private readonly Dictionary<Tab, NavIcon> _navIcons = new();
    private const double HopTime = 0.65, CheckTime = 1.5;

    /// <summary>Wraps a menu icon with its halo and ring (they take no room: negative margins).</summary>
    private FrameworkElement MakeNavIcon(Tab tab, TextBlock icon)
    {
        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var move = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(move);
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        icon.RenderTransform = group;

        var halo = new Ellipse { Width = 26, Height = 26, Margin = new Thickness(-7), IsHitTestVisible = false, Opacity = 0 };
        var ring = new Path
        {
            Width = 21, Height = 21, Margin = new Thickness(-4.5), IsHitTestVisible = false, StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        // the ring's faint full circle, so it reads as a timer from the first second
        var track = new Ellipse
        {
            Width = 18.4, Height = 18.4, Margin = new Thickness(-3.2), IsHitTestVisible = false, StrokeThickness = 1.6, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(halo);
        host.Children.Add(track);
        host.Children.Add(ring);
        host.Children.Add(icon);

        var n = new NavIcon
        {
            Icon = icon, Halo = halo, Ring = ring, Track = track, Scale = scale, Rotate = rotate, Move = move,
            Glyph = icon.Text, Font = icon.FontFamily, FontSize = icon.FontSize
        };
        if (tab == Tab.Downloads) host.Children.Add(MakeDownloadArt(n));
        if (tab == Tab.Focus) host.Children.Add(MakeFocusArt(n));
        _navIcons[tab] = n;
        return host;
    }

    /// <summary>The Focus tab's target, on a 24-unit grid like the font's. It takes the font icon's colour and movements.</summary>
    private static FrameworkElement MakeFocusArt(NavIcon n)
    {
        var art = TargetIcon.Art(n.Icon, 14.5);
        art.Margin = new Thickness(-1);
        art.RenderTransformOrigin = new Point(0.5, 0.5);
        art.RenderTransform = n.Icon.RenderTransform; // hops with the icon
        n.Drawn = art;
        n.Icon.Opacity = 0;
        return art;
    }

    /// <summary>
    /// The download icon in two parts, on a 24-unit grid like the font's: an arrow that breathes up and down, and
    /// under it the line, which becomes a little bar filling with the real progress.
    /// </summary>
    private FrameworkElement MakeDownloadArt(NavIcon n)
    {
        var green = new SolidColorBrush(DoneGreen);
        var arrowMove = new TranslateTransform();
        var arrow = new Path
        {
            Data = Geometry.Parse("M12,3.4 V15.2 M7,10.4 L12,15.4 L17,10.4"),
            Stroke = green, StrokeThickness = 2.1, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, RenderTransform = arrowMove
        };
        var trackBar = new Rectangle { Width = 15, Height = 2.1, RadiusX = 1.05, RadiusY = 1.05, Fill = new SolidColorBrush(WithAlpha(DoneGreen, 0x4C)) };
        var fill = new Rectangle { Width = 0, Height = 2.1, RadiusX = 1.05, RadiusY = 1.05, Fill = green };
        Canvas.SetLeft(trackBar, 4.5); Canvas.SetTop(trackBar, 19.4);
        Canvas.SetLeft(fill, 4.5); Canvas.SetTop(fill, 19.4);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(arrow);
        canvas.Children.Add(trackBar);
        canvas.Children.Add(fill);
        var art = new Viewbox
        {
            Child = canvas, Width = 16, Height = 16, Margin = new Thickness(-2), IsHitTestVisible = false, Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = n.Icon.RenderTransform // hops with the icon
        };
        n.Art = art;
        n.ArtArrow = arrowMove;
        n.ArtFill = fill;
        n.ArtTrack = trackBar;
        return art;
    }

    /// <summary>Something finished in a tab: the icon hops and shows a check (now, or when the menu is next seen).</summary>
    private void NavDone(Tab tab)
    {
        if (!_navIcons.TryGetValue(tab, out var n)) return;
        n.DoneUntil = AnimNow + 60;
    }

    /// <summary>What's going on in each tab: (busy 0..1 strength, colour, kind).</summary>
    private enum NavMotion { None, Breathe, Spin, Pulse, Ring, Bob, Glow }

    private (NavMotion motion, Color color) NavState(Tab t) => t switch
    {
        Tab.Downloads when ActiveTransfer is { Kind: TransferKind.Download } => (NavMotion.Breathe, DoneGreen),
        Tab.Claude when _claude.Active?.NeedsAttention == true => (NavMotion.Pulse, ClaudeColor),
        Tab.Claude when _claude.AnyWorking => (NavMotion.Spin, ClaudeColor),
        Tab.Focus when _focus.Running => (NavMotion.Ring, FocusColor),
        Tab.Clock when _ring != null => (NavMotion.Pulse, TimerColor),
        Tab.Clock when _clock.AnyRunning => (NavMotion.Ring, TimerColor),
        Tab.Chat when _runner?.Busy == true => (NavMotion.Bob, ChatColor),
        Tab.Shelf when _shelf.Count > 0 && S.ShelfEnabled => (NavMotion.Glow, ShelfColor),
        _ => (NavMotion.None, Colors.White)
    };

    /// <summary>Every frame while the island is open.</summary>
    private void UpdateNavActivity()
    {
        if (_state != IslandState.Expanded || _navIcons.Count == 0) return;
        double now = AnimNow;
        double sf = Math.Max(0.3, S.SpeedFactor);
        foreach (var (tab, n) in _navIcons)
        {
            bool selected = tab == _tab;
            var (motion, color) = selected ? (NavMotion.None, Colors.White) : NavState(tab);

            // a finished thing waiting to be seen: hop now (the tab you're on just shows it)
            if (n.DoneUntil > now && ExpandedPanel.Opacity > 0.9)
            {
                n.DoneUntil = -100;
                bool busyAgain = NavState(tab).motion is not (NavMotion.None or NavMotion.Glow);
                if (!selected && !busyAgain) n.HopAt = now;
            }
            double hop = (now - n.HopAt) / (HopTime * sf);
            bool hopping = hop >= 0 && hop < 1;
            bool check = (now - n.HopAt) < CheckTime * sf && !selected && tab != Tab.Shelf;

            n.Active += ((motion != NavMotion.None ? 1 : 0) - n.Active) * Ease(0.12);
            if (motion != NavMotion.None) n.HaloColor = color;
            else if (check || hopping) n.HaloColor = TabColor(tab);

            // the check mark in place of the icon for a moment
            if (check != n.CheckShown)
            {
                n.CheckShown = check;
                n.Icon.Text = check ? "" : n.Glyph;
                n.Icon.FontFamily = check ? (FontFamily)FindResource("IconFont") : n.Font;
                n.Icon.FontSize = check ? 13 : n.FontSize;
                if (check) n.Icon.Foreground = new SolidColorBrush(TabColor(tab));
                else n.Icon.ClearValue(TextBlock.ForegroundProperty);
            }
            if (!check)
            {
                // while busy the icon takes the tab's colour (it fades back to the menu's grey)
                if (n.Active > 0.01 && !selected)
                {
                    double k = Math.Min(1, n.Active) * 0.9;
                    var from = Color.FromArgb(0x8C, 255, 255, 255);
                    var c2 = n.HaloColor;
                    n.Tint.Color = Color.FromArgb((byte)(from.A + (255 - from.A) * k), (byte)(from.R + (c2.R - from.R) * k),
                        (byte)(from.G + (c2.G - from.G) * k), (byte)(from.B + (c2.B - from.B) * k));
                    if (!ReferenceEquals(n.Icon.Foreground, n.Tint)) n.Icon.Foreground = n.Tint;
                }
                else if (ReferenceEquals(n.Icon.ReadLocalValue(TextBlock.ForegroundProperty), n.Tint))
                    n.Icon.ClearValue(TextBlock.ForegroundProperty);
            }

            double a = n.Active, t = now / sf;
            double x = 0, y = 0, angle = 0, s = 1, halo = 0, iconOpacity = 1;
            var visual = check || hopping ? NavMotion.None : motion;
            switch (visual)
            {
                case NavMotion.Breathe:
                    halo = a * (0.45 + 0.12 * Math.Sin(t * Math.PI * 2 / 1.6));
                    break;
                case NavMotion.Spin:
                    angle = t * 150 % 360;
                    halo = a * (0.5 + 0.15 * Math.Sin(t * Math.PI * 2 / 2.4));
                    break;
                case NavMotion.Pulse:
                {
                    double p = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 0.9);
                    s = 1 + 0.16 * p * a;
                    halo = a * (0.45 + 0.5 * p);
                    break;
                }
                case NavMotion.Ring:
                    halo = a * (0.3 + 0.1 * Math.Sin(t * Math.PI * 2 / 3));
                    // a little "tick" each second
                    double tick = t % 1;
                    angle = tick < 0.12 ? 7 * Math.Sin(Math.PI * tick / 0.12) * a : 0;
                    break;
                case NavMotion.Bob:
                    y = -1.8 * Math.Abs(Math.Sin(t * Math.PI * 2.2)) * a;
                    halo = a * (0.45 + 0.15 * Math.Sin(t * Math.PI * 4.4));
                    break;
                case NavMotion.Glow:
                    halo = a * (0.32 + 0.12 * Math.Sin(t * Math.PI * 2 / 3.5));
                    break;
                default:
                    halo = 0.5 * a; // fading out
                    break;
            }
            // keep spinning while it fades out, then settle back upright (a check never turns)
            if (visual != NavMotion.Spin && n.Rotate.Angle != 0 && visual != NavMotion.Ring)
                angle = a > 0.02 && !check && !hopping ? n.Rotate.Angle * 0.8 : 0;

            if (hopping)
            {
                double up = Math.Sin(Math.PI * Math.Min(1, hop / 0.7));
                y += -4.5 * up;
                s *= 1 + 0.28 * up;
                if (hop > 0.7) s *= 1 + 0.06 * Math.Sin(Math.PI * (hop - 0.7) / 0.3); // tiny landing squash
            }
            double flash = (now - n.HopAt) / (CheckTime * sf);
            if (flash >= 0 && flash < 1 && !selected) halo = Math.Max(halo, 0.9 * (1 - flash));

            n.Move.X = x;
            n.Move.Y = y;
            n.Rotate.Angle = angle;
            n.Scale.ScaleX = n.Scale.ScaleY = s;
            if (n.Art != null)
            {
                // the drawn arrow + bar take over from the font icon while downloading (not during the check)
                double art = check ? 0 : a;
                n.Art.Opacity = art;
                iconOpacity *= 1 - art;
                if (art > 0.01) UpdateDownloadArt(n, t);
            }
            if (n.Drawn != null)
            {
                n.Drawn.Opacity = check ? 0 : iconOpacity;
                iconOpacity = check ? iconOpacity : 0;
            }
            n.Icon.Opacity = iconOpacity;
            SetNavHalo(n, halo);
            UpdateNavRing(n, tab, motion == NavMotion.Ring ? a : 0);
        }
    }

    /// <summary>The arrow breathes (eases down and back up, never vanishing); the bar shows the real progress.</summary>
    private void UpdateDownloadArt(NavIcon n, double t)
    {
        n.ArtArrow!.Y = -1.1 + 2.2 * (0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 1.6));
        var p = ActiveTransfer?.Progress;
        if (p is double v)
        {
            Canvas.SetLeft(n.ArtFill!, 4.5);
            n.ArtFill!.Width = Math.Max(2.1, 15 * Math.Clamp(v, 0, 1));
        }
        else
        {
            // unknown size: a short piece glides along the bar
            double u = 0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 1.8);
            n.ArtFill!.Width = 5;
            Canvas.SetLeft(n.ArtFill, 4.5 + u * 10);
        }
    }

    private void SetNavHalo(NavIcon n, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity < 0.005)
        {
            if (n.Halo.Opacity != 0) n.Halo.Opacity = 0;
            return;
        }
        if (n.Halo.Fill is not RadialGradientBrush rb || rb.GradientStops[0].Color != WithAlpha(n.HaloColor, 0xB4))
        {
            var brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(WithAlpha(n.HaloColor, 0xB4), 0));
            brush.GradientStops.Add(new GradientStop(WithAlpha(n.HaloColor, 0x48), 0.5));
            brush.GradientStops.Add(new GradientStop(WithAlpha(n.HaloColor, 0), 1));
            brush.Freeze();
            n.Halo.Fill = brush;
        }
        n.Halo.Opacity = opacity;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>The focus timer's ring: how much of the session has gone by (on Relógio, of the timer ending first).</summary>
    private void UpdateNavRing(NavIcon n, Tab tab, double a)
    {
        n.Ring.Opacity = a;
        n.Track.Opacity = a;
        if (a < 0.01) return;
        bool clock = tab == Tab.Clock;
        var color = clock ? TimerColor : _focus.Mode == FocusMode.Break ? DoneGreen : FocusColor;
        if (n.Ring.Stroke is not SolidColorBrush sb || sb.Color != color)
        {
            n.Ring.Stroke = new SolidColorBrush(color);
            n.Track.Stroke = new SolidColorBrush(WithAlpha(color, 0x38));
        }
        double p = Math.Clamp(clock ? 1 - (_clock.Soonest?.LeftShare ?? 1) : _focus.Progress, 0, 1);
        if (Math.Abs(p - n.RingDrawn) < 0.002) return;
        n.RingDrawn = p;
        const double c = 10.5, r = 9.2;
        double end = Math.Max(0.004, p) * 2 * Math.PI;
        var start = new Point(c, c - r);
        var to = new Point(c + r * Math.Sin(end), c - r * Math.Cos(end));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        if (p >= 0.999)
        {
            fig.Segments.Add(new ArcSegment(new Point(c, c + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new ArcSegment(start, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        }
        else fig.Segments.Add(new ArcSegment(to, new Size(r, r), 0, end > Math.PI, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        n.Ring.Data = geo;
    }
}
