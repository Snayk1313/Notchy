using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Notchy;

/// <summary>
/// A burst of paper out of the island: each piece leaves with its own speed, loses it to the air, falls at its own pace
/// swinging sideways and turns over, showing a darker back. Only runs (and only asks for frames) while there's paper.
/// </summary>
public sealed class ConfettiLayer : FrameworkElement
{
    private enum Shape { Strip, Square, Dot }

    private sealed class Piece
    {
        public double X, Y, Vx, Vy, W, H, Rot, Vr, Flip, Vf, Sway, SwayAmp, SwayRate, Age, Life, Fall;
        public int Color;
        public Shape Shape;
    }

    private const double Gravity = 330, Drag = 2.4;

    private static readonly Brush[] Front, Back;
    private readonly List<Piece> _pieces = new();
    private readonly Random _rand = new();
    private readonly Stopwatch _clock = new();
    private bool _running;

    static ConfettiLayer()
    {
        Color[] palette =
        {
            Color.FromRgb(0xFF, 0x7E, 0xB6), Color.FromRgb(0xFF, 0xD3, 0x6B), Color.FromRgb(0x7C, 0xC8, 0xFF),
            Color.FromRgb(0x5E, 0xD6, 0x7D), Color.FromRgb(0xA7, 0x8B, 0xFA), Colors.White, Color.FromRgb(0xFF, 0x9F, 0x6B)
        };
        Front = new Brush[palette.Length];
        Back = new Brush[palette.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            var c = palette[i];
            Front[i] = new SolidColorBrush(c);
            Back[i] = new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.62), (byte)(c.G * 0.62), (byte)(c.B * 0.62)));
            Front[i].Freeze();
            Back[i].Freeze();
        }
    }

    public ConfettiLayer() => IsHitTestVisible = false;

    private double R(double a, double b) => a + _rand.NextDouble() * (b - a);

    /// <param name="x">Middle of the island.</param>
    /// <param name="y">Its bottom edge.</param>
    public void Burst(double x, double y, double scale)
    {
        _pieces.Clear();
        for (int i = 0; i < 110; i++)
        {
            double angle = R(0.12, 3.02), speed = R(160, 1050) * scale;
            var shape = _rand.Next(5) switch { 0 => Shape.Dot, 1 => Shape.Square, _ => Shape.Strip };
            _pieces.Add(new Piece
            {
                X = x + R(-40, 40) * scale, Y = y, Vx = Math.Cos(angle) * speed, Vy = Math.Sin(angle) * speed,
                Shape = shape, Color = _rand.Next(Front.Length),
                W = (shape == Shape.Dot ? R(2.4, 3.6) : shape == Shape.Square ? R(5, 7) : R(4, 6.5)) * scale, H = R(8, 12) * scale,
                Rot = R(0, 6.28), Vr = R(-7, 7), Flip = R(0, 6.28), Vf = R(6, 14), Sway = R(0, 6.28), SwayAmp = R(10, 34), SwayRate = R(2, 4.2),
                Life = R(3, 4.2), Fall = R(70, 165) * scale
            });
        }
        _clock.Restart();
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double dt = Math.Min(0.05, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        foreach (var p in _pieces)
        {
            p.Age += dt;
            double air = Math.Exp(-Drag * dt);
            p.Vx *= air;
            p.Vy = p.Vy * air + Gravity * dt;
            if (p.Vy > p.Fall) p.Vy = p.Fall + (p.Vy - p.Fall) * Math.Exp(-7 * dt);
            p.Sway += p.SwayRate * dt;
            p.X += (p.Vx + (p.Vy > 30 ? Math.Cos(p.Sway) * p.SwayAmp : 0)) * dt;
            p.Y += p.Vy * dt;
            p.Rot += p.Vr * dt;
            p.Flip += p.Vf * dt;
        }
        _pieces.RemoveAll(p => p.Age > p.Life || p.Y > ActualHeight);
        InvalidateVisual();
        if (_pieces.Count > 0) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var p in _pieces)
        {
            // fades with age, and before reaching the bottom of the window (so it doesn't end in a straight line)
            double alpha = Math.Min(Math.Min(1, (p.Life - p.Age) / 0.6), (ActualHeight - p.Y) / 70);
            if (alpha <= 0) continue;
            double turn = Math.Cos(p.Flip);
            var m = Matrix.Identity;
            m.Scale(1, Math.Max(0.12, Math.Abs(turn)));
            m.Rotate(p.Rot * 180 / Math.PI);
            m.Translate(p.X, p.Y);
            dc.PushTransform(new MatrixTransform(m));
            dc.PushOpacity(alpha);
            var brush = turn >= 0 ? Front[p.Color] : Back[p.Color];
            switch (p.Shape)
            {
                case Shape.Dot: dc.DrawEllipse(brush, null, new Point(0, 0), p.W, p.W); break;
                case Shape.Square: dc.DrawRectangle(brush, null, new Rect(-p.W / 2, -p.W / 2, p.W, p.W)); break;
                default: dc.DrawRectangle(brush, null, new Rect(-p.W / 2, -p.H / 2, p.W, p.H)); break;
            }
            dc.Pop();
            dc.Pop();
        }
    }
}
