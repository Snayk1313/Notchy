using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>Thin rounded slider (progress / volume) that grows on hover, like the iOS one.</summary>
public class BarSlider : Grid
{
    private readonly Border _track = new();
    private readonly Border _fill = new();
    private readonly Ellipse _thumb = new();
    private double _value;

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

        Children.Add(_track);
        Children.Add(_fill);
        Children.Add(_thumb);

        SetHover(false);
        SizeChanged += (_, _) => UpdateVisual();
        MouseEnter += (_, _) => SetHover(true);
        MouseLeave += (_, _) => { if (!IsDragging) SetHover(false); };
    }

    private void SetHover(bool on)
    {
        double h = on ? 6 : 4;
        _track.Height = _fill.Height = h;
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(h / 2);
        _thumb.Opacity = on && ShowThumb ? 1 : 0;
    }

    private void UpdateVisual()
    {
        double w = ActualWidth;
        _fill.Width = Math.Max(0, w * _value);
        _thumb.Margin = new Thickness(Math.Clamp(w * _value - _thumb.Width / 2, 0, Math.Max(0, w - _thumb.Width)), 0, 0, 0);
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
        if (IsDragging) SetFromMouse(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        IsDragging = false;
        ReleaseMouseCapture();
        ValueCommitted?.Invoke(_value);
        if (!IsMouseOver) SetHover(false);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!IsDragging) return;
        IsDragging = false;
        ValueCommitted?.Invoke(_value);
        SetHover(false);
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
            _levels[i] += (_targets[i] - _levels[i]) * 0.35;
        }
        InvalidateVisual();
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
