using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>
/// What every scrolling list shares with the alarms' row: the items turn transparent at an edge that has more behind
/// it, and the wheel glides there instead of jumping.
/// </summary>
internal static class ScrollPolish
{
    public const double FadeWidth = 34; // (the alarms' AlarmFade)

    /// <summary>Fades the content at the start/end edge while there's more to scroll that way.</summary>
    public static void Fade(ScrollViewer sv, bool horizontal)
    {
        // (in pixels over what you see: a relative mask would stretch over the whole content, scrolled-away part too)
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1), MappingMode = BrushMappingMode.Absolute };
        foreach (var o in new[] { 0.0, 0.0, 1.0, 1.0 }) mask.GradientStops.Add(new GradientStop(Colors.Black, o));
        sv.OpacityMask = mask;
        FrameworkElement on = sv;
        bool? fadeA = null, fadeB = null;
        double size = 0;
        // only the scrolled content fades, not a scroll bar beside it (the Settings window shows one)
        sv.Loaded += (_, _) =>
        {
            if (sv.Template?.FindName("PART_ScrollContentPresenter", sv) is not FrameworkElement p || p == on) return;
            sv.OpacityMask = null;
            p.OpacityMask = mask;
            on = p;
            size = 0;
            Update();
        };
        void Update()
        {
            double length = horizontal ? on.ActualWidth : on.ActualHeight;
            if (length < 1) return;
            double at = horizontal ? sv.HorizontalOffset : sv.VerticalOffset, max = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
            bool a = at > 2, b = at < max - 2;
            if (a == fadeA && b == fadeB && length == size) return;
            fadeA = a;
            fadeB = b;
            size = length;
            mask.EndPoint = horizontal ? new Point(length, 0) : new Point(0, length);
            double f = Math.Min(FadeWidth / length, 0.3);
            var time = TimeSpan.FromMilliseconds(200 * AppSettings.Current.SpeedFactor);
            var st = mask.GradientStops;
            st[0].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(a ? Colors.Transparent : Colors.Black, time));
            st[1].BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(a ? f : 0, time));
            st[2].BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(b ? 1 - f : 1, time));
            st[3].BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(b ? Colors.Transparent : Colors.Black, time));
        }
        sv.ScrollChanged += (_, _) => Update();
        sv.SizeChanged += (_, _) => Update();
    }
}

/// <summary>
/// A ScrollViewer whose wheel glides to where it's going, like the alarms' row (each notch adds to the trip). Only
/// a wheel nobody inside used reaches it (the calendar's months, say, keep theirs). <see cref="Step"/> gives one
/// notch's distance; with <see cref="Snap"/> a row of items stops on an item's edge, so the first one is whole.
/// </summary>
internal sealed class GlideScrollViewer : ScrollViewer
{
    public bool Horizontal { get; init; }
    public Func<double>? Step { get; init; }
    public bool Snap { get; init; }

    private double _target;
    private bool _moving;
    private TimeSpan _last;

    public GlideScrollViewer() => Unloaded += (_, _) => Stop();

    private double Now => Horizontal ? HorizontalOffset : VerticalOffset;
    private double Max => Horizontal ? ScrollableWidth : ScrollableHeight;

    private void Go(double to)
    {
        if (Horizontal) ScrollToHorizontalOffset(to);
        else ScrollToVerticalOffset(to);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled || Max <= 1)
        {
            if (!Horizontal) base.OnMouseWheel(e);
            return;
        }
        double one = Step?.Invoke() ?? 48;
        double notches = Math.Max(1, Math.Round(Math.Abs(e.Delta) / 120.0)) * Math.Sign(e.Delta);
        double from = _moving ? _target : Now;
        if (Snap) from = Math.Round(from / one) * one;
        _target = Math.Clamp(from - notches * one, 0, Max);
        e.Handled = true;
        if (_moving) return;
        _moving = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += Frame;
    }

    private void Frame(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((t - _last).TotalSeconds, 0, 0.1);
        _last = t;
        double x = Now;
        if (Math.Abs(_target - x) < 0.5)
        {
            Go(_target);
            Stop();
            return;
        }
        // the alarms' row: 22% of the way each 60 Hz frame
        double k = 1 - Math.Exp(-dt / (0.067 * AppSettings.Current.SpeedFactor));
        Go(x + (_target - x) * k);
    }

    private void Stop()
    {
        if (!_moving) return;
        _moving = false;
        CompositionTarget.Rendering -= Frame;
    }
}
