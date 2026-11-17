using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Thin rounded slider (progress / volume) that grows on hover, like the iOS one. Under the mouse, a lighter stretch runs
/// from the value to where a click would land, with a little label over it (the time, the volume) when <see cref="HoverLabel"/> is set.
/// </summary>
public class BarSlider : Grid
{
    private readonly Border _track = new();
    private readonly Border _ghost = new();
    private readonly Border _fill = new();
    private readonly Ellipse _thumb = new();
    private readonly Border _tag = new();
    private readonly TextBlock _tagText = new();
    private double _value;
    private double? _hoverAt; // where the mouse is (0..1), while it's over the bar

    /// <summary>The label over the mouse for a value (0..1); null: no label.</summary>
    public Func<double, string>? HoverLabel { get; set; }

    public event Action<double>? ValueChanging;
    public event Action<double>? ValueCommitted;

    public bool IsDragging { get; private set; }
    public bool ShowThumb { get; set; } = true;

    public Brush FillBrush
    {
        get => _fill.Background;
        set => _fill.Background = value;
    }

    public double Value
    {
        get => _value;
        set
        {
            _value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            UpdateVisual();
        }
    }

    public BarSlider()
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        _track.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        _track.VerticalAlignment = VerticalAlignment.Center;

        _fill.Background = Brushes.White;
        _fill.HorizontalAlignment = HorizontalAlignment.Left;
        _fill.VerticalAlignment = VerticalAlignment.Center;

        _thumb.Width = _thumb.Height = 11;
        _thumb.Fill = Brushes.White;
        _thumb.HorizontalAlignment = HorizontalAlignment.Left;
        _thumb.VerticalAlignment = VerticalAlignment.Center;
        _thumb.Opacity = 0;
        _thumb.IsHitTestVisible = false;

        _ghost.Background = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255));
        _ghost.HorizontalAlignment = HorizontalAlignment.Left;
        _ghost.VerticalAlignment = VerticalAlignment.Center;
        _ghost.IsHitTestVisible = false;
        _ghost.Opacity = 0;

        // the label sits over the bar (outside it), centred on the mouse
        _tagText.FontSize = 11;
        _tagText.Foreground = Brushes.White;
        _tag.Child = _tagText;
        _tag.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x30));
        _tag.BorderBrush = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255));
        _tag.BorderThickness = new Thickness(1);
        _tag.CornerRadius = new CornerRadius(6);
        _tag.Padding = new Thickness(7, 2, 7, 3);
        _tag.IsHitTestVisible = false;
        _tag.Opacity = 0;
        // (on a canvas: a grid would cut the label to the bar's height)
        var tagLayer = new Canvas { IsHitTestVisible = false };
        tagLayer.Children.Add(_tag);

        Children.Add(_track);
        Children.Add(_ghost);
        Children.Add(_fill);
        Children.Add(_thumb);
        Children.Add(tagLayer);

        SetHover(false);
        SizeChanged += (_, _) => UpdateVisual();
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) =>
        {
            if (IsDragging) return;
            SetHover(false);
            _hoverAt = null;
            UpdateHover();
        };
    }

    private void SetHover(bool on)
    {
        double h = on ? 6 : 4;
        _track.Height = _fill.Height = _ghost.Height = h;
        _track.CornerRadius = _fill.CornerRadius = _ghost.CornerRadius = new CornerRadius(h / 2);
        _thumb.Opacity = on && ShowThumb ? 1 : 0;
    }

    private void UpdateVisual()
    {
        double w = ActualWidth;
        _fill.Width = Math.Max(0, w * _value);
        _thumb.Margin = new Thickness(Math.Clamp(w * _value - _thumb.Width / 2, 0, Math.Max(0, w - _thumb.Width)), 0, 0, 0);
        UpdateHover();
    }

    /// <summary>For the snapshot tests: as if the mouse were over the bar at <paramref name="x"/> (0..1), or gone (null).</summary>
    internal void HoverAt(double? x)
    {
        _hoverAt = x;
        SetHover(x != null);
        UpdateHover();
    }

    private void UpdateHover()
    {
        double w = ActualWidth;
        // (dragging: the value is the mouse, so only the label shows)
        double? at = IsDragging ? _value : _hoverAt;
        if (at is not double x || w <= 0)
        {
            _ghost.Opacity = _tag.Opacity = 0;
            return;
        }
        double a = Math.Min(_value, x) * w, b = Math.Max(_value, x) * w;
        _ghost.Margin = new Thickness(a, 0, 0, 0);
        _ghost.Width = b - a;
        _ghost.Opacity = IsDragging ? 0 : 1;
        if (HoverLabel == null)
        {
            _tag.Opacity = 0;
            return;
        }
        _tagText.Text = HoverLabel(x);
        _tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_tag, x * w - _tag.DesiredSize.Width / 2);
        Canvas.SetTop(_tag, -_tag.DesiredSize.Height - 1);
        _tag.Opacity = 1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        IsDragging = true;
        SetFromMouse(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverAt = ActualWidth > 0 ? Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) : null;
        if (IsDragging) SetFromMouse(e);
        else UpdateHover();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        IsDragging = false;
        ReleaseMouseCapture();
        ValueCommitted?.Invoke(_value);
        if (!IsMouseOver)
        {
            SetHover(false);
            _hoverAt = null;
        }
        UpdateHover();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsDragging) return;
        IsDragging = false;
        ValueCommitted?.Invoke(_value);
        SetHover(false);
        _hoverAt = null;
        UpdateHover();
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        Value = ActualWidth > 0 ? e.GetPosition(this).X / ActualWidth : 0;
        ValueChanging?.Invoke(_value);
    }
}

/// <summary>Small bar visualizer driven by the system output peak meter. Shows dots when paused.</summary>
public class Visualizer : FrameworkElement
{
    private const int BarCount = 5;
    private const double BarWidth = 3;
    private readonly double[] _levels = new double[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly Random _rand = new();
    private double _runningMax;

    public Brush BarBrush { get; set; } = Brushes.Orange;

    public void Tick(double peak, bool playing, bool hasMeter)
    {
        // Auto-gain: normalise against a slowly decaying recent maximum so quiet output still moves the bars.
        _runningMax = Math.Max(peak, _runningMax * 0.997);
        double norm = peak / Math.Max(_runningMax, 0.005);
        double energy = !playing ? 0 : hasMeter ? (peak < 0.0005 ? 0.08 : 0.2 + 0.8 * norm * norm) : 0.6;
        for (int i = 0; i < BarCount; i++)
        {
            if (_rand.NextDouble() < 0.4)
                _targets[i] = energy * (0.3 + _rand.NextDouble() * 0.8);
        }
    }

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastStep = -1;

    /// <summary>Every frame: the bars glide toward the levels picked by <see cref="Tick"/> (35% per 62.5 ms, as before).</summary>
    public void Step()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = _lastStep < 0 ? 62.5 : Math.Min(now - _lastStep, 250);
        _lastStep = now;
        double k = 1 - Math.Pow(0.65, dt / 62.5);
        bool moved = false;
        for (int i = 0; i < BarCount; i++)
        {
            double d = (_targets[i] - _levels[i]) * k;
            if (Math.Abs(d) > 0.0005) moved = true;
            _levels[i] += d;
        }
        if (moved) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double gap = (w - BarCount * BarWidth) / (BarCount - 1);
        for (int i = 0; i < BarCount; i++)
        {
            double bh = BarWidth + Math.Clamp(_levels[i], 0, 1) * (h - BarWidth);
            double x = i * (BarWidth + gap);
            dc.DrawRoundedRectangle(BarBrush, null, new Rect(x, (h - bh) / 2, BarWidth, bh), BarWidth / 2, BarWidth / 2);
        }
    }
}
