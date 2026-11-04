using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

public enum Mood { Normal, Happy, Sad, Worried, Wide, Thinking, Focused, Working, Sleepy, Curious }

/// <summary>
/// Two little eyes with moods. Call <see cref="Update"/> every frame; everything is smoothed, so moods,
/// looks and blinks blend into each other instead of snapping.
/// </summary>
public sealed class EyesFace : Grid
{
    private readonly Eye _left, _right;
    private readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 7, Opacity = 0.7 };
    private readonly Random _rand = new();

    private Mood _base = Mood.Normal;
    private Mood _reaction = Mood.Normal;
    private DateTime _reactionUntil = DateTime.MinValue;

    // smoothed state
    private double _lookX, _lookY, _scaleY = 1, _scaleX = 1, _bounce, _shake;
    private double _targetLookX, _targetLookY;
    private bool _hasLookTarget;
    private DateTime _blinkUntil = DateTime.MinValue, _nextAutoBlink = DateTime.Now.AddSeconds(3);
    private double _t, _swayAmount;
    private Color _color = Colors.Transparent; // so the first Color = White really paints the eyes

    public EyesFace()
    {
        Width = 24;
        Height = 14;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _left = new Eye();
        _right = new Eye();
        row.Children.Add(_left);
        row.Children.Add(_right);
        row.Effect = _glow;
        Children.Add(row);
        IsHitTestVisible = false;
        Color = Colors.White;
    }

    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value) return;
            _color = value;
            var b = new SolidColorBrush(value);
            b.Freeze();
            _left.SetBrush(b);
            _right.SetBrush(b);
            _glow.Color = value;
            _glow.Opacity = value == Colors.White ? 0.35 : 0.75;
        }
    }

    /// <summary>The resting mood (depends on what the tab is doing).</summary>
    public Mood BaseMood
    {
        get => _base;
        set => _base = value;
    }

    /// <summary>A short-lived expression on top of the base mood.</summary>
    public void React(Mood mood, double seconds = 1.6)
    {
        _reaction = mood;
        _reactionUntil = DateTime.Now.AddSeconds(seconds);
        if (mood is Mood.Happy or Mood.Wide or Mood.Curious) _bounce = 1;
    }

    public void Blink() => _blinkUntil = DateTime.Now.AddMilliseconds(130);

    /// <summary>-1..1 in both axes (where to look), or null to look straight / follow the mood.</summary>
    public void LookAt(double? x, double? y)
    {
        _hasLookTarget = x != null;
        _targetLookX = Math.Clamp(x ?? 0, -1, 1);
        _targetLookY = Math.Clamp(y ?? 0, -1, 1);
    }

    /// <param name="sway">0..1: both eyes swing gently left and right (music playing).</param>
    public void Update(double dt, double sway = 0)
    {
        _t += dt;
        var now = DateTime.Now;
        var mood = now < _reactionUntil ? _reaction : _base;

        // automatic blinks every few seconds (not while happy / sleepy)
        if (now > _nextAutoBlink)
        {
            if (mood is not (Mood.Happy or Mood.Sleepy or Mood.Focused)) Blink();
            _nextAutoBlink = now.AddSeconds(2.5 + _rand.NextDouble() * 3.5);
        }

        // where to look
        double lx = 0, ly = 0;
        switch (mood)
        {
            case Mood.Working:
                lx = Math.Sign(Math.Sin(_t * 1.6)) * 0.9; // left ... right ... like reading
                break;
            case Mood.Thinking:
                // "hmm...": up and to the side, drifting slowly back and forth
                lx = 0.55 + Math.Sin(_t * 1.3) * 0.45;
                ly = -1;
                break;
            case Mood.Sad:
            case Mood.Sleepy:
                ly = 0.9;
                break;
            case Mood.Curious:
                lx = Math.Sin(_t * 9) > 0 ? 0.8 : -0.8;
                break;
        }
        if (_hasLookTarget && mood is not (Mood.Working or Mood.Thinking))
        {
            lx = _targetLookX;
            ly = _targetLookY;
        }
        double k = Math.Min(1, dt * 10);
        double rangeX = mood == Mood.Thinking ? 3.2 : 2.3, rangeY = mood == Mood.Thinking ? 2.6 : 1.6;
        _lookX += (lx * rangeX - _lookX) * k;
        _lookY += (ly * rangeY - _lookY) * k;

        // eye shape per mood
        double sy = mood switch
        {
            Mood.Focused => 0.5,
            Mood.Thinking => 0.72,
            Mood.Sleepy => 0.32,
            Mood.Wide or Mood.Worried => 1.25,
            _ => 1
        };
        double sx = mood is Mood.Wide or Mood.Worried ? 1.15 : 1;
        if (now < _blinkUntil) sy = 0.1;
        _scaleY += (sy - _scaleY) * Math.Min(1, dt * (now < _blinkUntil ? 40 : 14));
        _scaleX += (sx - _scaleX) * Math.Min(1, dt * 14);

        // little hop on reactions, nervous shake when worried, dancing on the beat
        _bounce = Math.Max(0, _bounce - dt * 3.2);
        double hop = -Math.Sin(_bounce * Math.PI) * 2.5 * (_bounce > 0 ? 1 : 0);
        _shake = mood == Mood.Worried ? Math.Sin(_t * 38) * 0.7 : 0;
        // music: both eyes swing together, softly, left and right
        _swayAmount += (sway - _swayAmount) * Math.Min(1, dt * 3);
        double swing = Math.Sin(_t * 2.4) * 1.8 * _swayAmount;

        bool happy = mood == Mood.Happy;
        bool sad = mood == Mood.Sad;
        _left.Apply(_lookX + _shake + swing, _lookY + hop, _scaleX, _scaleY, happy, sad, false);
        _right.Apply(_lookX + _shake + swing, _lookY + hop, _scaleX, _scaleY, happy, sad, true);
    }

    /// <summary>One eye: a rounded pill, a "^" for happy and a drooping arc for sad.</summary>
    private sealed class Eye : Grid
    {
        private readonly Rectangle _pill = new() { Width = 5, Height = 8, RadiusX = 2.5, RadiusY = 2.5, RenderTransformOrigin = new Point(0.5, 0.5) };
        private readonly Path _happy = new() { Data = Geometry.Parse("M0,5 Q3.5,-0.5 7,5"), StrokeThickness = 1.9, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly Path _sad = new() { StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private readonly TranslateTransform _move = new();
        private readonly ScaleTransform _scale = new();
        private bool? _wasRight;

        public Eye()
        {
            Width = 10;
            Height = 13;
            Margin = new Thickness(1, 0, 1, 0);
            _pill.RenderTransform = _scale;
            RenderTransform = _move;
            Children.Add(_pill);
            Children.Add(_happy);
            Children.Add(_sad);
        }

        public void SetBrush(Brush b)
        {
            _pill.Fill = b;
            _happy.Stroke = b;
            _sad.Stroke = b;
        }

        public void Apply(double x, double y, double sx, double sy, bool happy, bool sad, bool right)
        {
            if (_wasRight != right)
            {
                _wasRight = right;
                // sad eyes droop towards the outside
                _sad.Data = Geometry.Parse(right ? "M0,1.5 Q4,0.5 7,4" : "M0,4 Q3,0.5 7,1.5");
            }
            _move.X = x;
            _move.Y = y;
            _scale.ScaleX = sx;
            _scale.ScaleY = sy;
            _pill.Visibility = happy || sad ? Visibility.Collapsed : Visibility.Visible;
            _happy.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
            _sad.Visibility = sad ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
