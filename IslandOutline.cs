using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Notchy;

/// <summary>
/// A rounded rectangle outline (a pill when the radius is half its height) described as its right half, from
/// the top centre down to the bottom centre; the left half is its mirror. Parts above y = 0 (the screen's top
/// edge) are hidden. The bubbles draw their light with it, using the same rules as the island's outline, so a
/// bubble's oval can stretch, step by step, into the island's edge.
/// </summary>
public sealed class RoundOutline
{
    private readonly List<Point> _pts = new();
    private readonly List<double> _at = new();
    private double _cx;

    /// <summary>Length of the half outline that's above the screen edge (not drawn).</summary>
    public double Hidden { get; private set; }

    /// <summary>Visible length of one half.</summary>
    public double Visible { get; private set; }

    public void Build(double left, double top, double right, double bottom, double radius)
    {
        _pts.Clear();
        _at.Clear();
        _cx = (left + right) / 2;
        double rad = Math.Max(0.5, Math.Min(radius, Math.Min((right - left) / 2, (bottom - top) / 2)));

        void Add(double x, double y)
        {
            if (_pts.Count > 0)
            {
                var last = _pts[^1];
                _at.Add(_at[^1] + Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)));
            }
            else _at.Add(0);
            _pts.Add(new Point(x, y));
        }
        void Line(double x0, double y0, double x1, double y1)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0))));
            for (int i = 1; i <= n; i++) Add(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n);
        }
        void Arc(double cx, double cy, double a0, double a1)
        {
            int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(a1 - a0) * rad));
            for (int i = 1; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                Add(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a));
            }
        }

        Add(_cx, top);
        if (right - rad > _cx) Line(_cx, top, right - rad, top);
        Arc(right - rad, top + rad, -Math.PI / 2, 0);
        if (bottom - rad > top + rad) Line(right, top + rad, right, bottom - rad);
        Arc(right - rad, bottom - rad, 0, Math.PI / 2);
        if (right - rad > _cx) Line(right - rad, bottom, _cx, bottom);

        Hidden = 0;
        for (int i = 0; i < _pts.Count; i++)
            if (_pts[i].Y >= 0)
            {
                Hidden = _at[i];
                break;
            }
        Visible = Math.Max(0, _at[^1] - Hidden);
    }

    /// <summary>Both sides from <paramref name="from"/> to <paramref name="to"/> (visible distance from the top).</summary>
    public Geometry Half(double from, double to)
    {
        var g = new StreamGeometry();
        if (to - from < 0.5 || _pts.Count == 0) return g;
        using (var ctx = g.Open())
        {
            Side(ctx, Hidden + from, Hidden + to, mirror: true, backwards: false);
            Side(ctx, Hidden + from, Hidden + to, mirror: false, backwards: false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>A light going around: down the left side, then up the right side (2 x Visible in total).</summary>
    public Geometry Loop(double from, double length)
    {
        var g = new StreamGeometry();
        double loop = 2 * Visible;
        if (_pts.Count == 0 || loop < 1 || length < 0.5) return g;
        from = ((from % loop) + loop) % loop;
        using (var ctx = g.Open())
        {
            double a = from, b = from + length;
            if (b <= loop) LoopPart(ctx, a, b);
            else
            {
                LoopPart(ctx, a, loop);
                LoopPart(ctx, 0, b - loop);
            }
        }
        g.Freeze();
        return g;
    }

    private void LoopPart(StreamGeometryContext ctx, double a, double b)
    {
        double V = Visible;
        if (a < V) Side(ctx, Hidden + a, Hidden + Math.Min(b, V), mirror: true, backwards: false);
        if (b > V) Side(ctx, Hidden + 2 * V - b, Hidden + 2 * V - Math.Max(a, V), mirror: false, backwards: true);
    }

    private void Side(StreamGeometryContext ctx, double from, double to, bool mirror, bool backwards)
    {
        if (to - from < 0.3) return;
        bool started = false;
        int n = _pts.Count;
        for (int k = 0; k < n; k++)
        {
            int i = backwards ? n - 1 - k : k;
            if (_at[i] < from || _at[i] > to) continue;
            var p = _pts[i];
            if (mirror) p = new Point(2 * _cx - p.X, p.Y);
            if (!started)
            {
                ctx.BeginFigure(p, false, false);
                started = true;
            }
            else ctx.LineTo(p, true, true);
        }
    }
}

/// <summary>
/// The right half of the island's visible outline, from the top to the bottom centre, sampled every pixel
/// (the left half is its mirror). Attached style: starts at the top corner (the top edge is the screen edge).
/// Used by the opening light and by the download / copy progress light.
/// </summary>
public sealed class IslandOutline
{
    private readonly List<Point> _pts = new();
    private readonly List<double> _at = new();
    private double _w = -1, _h = -1, _top = -1;

    public double Length { get; private set; }

    public void Invalidate() => _w = -1;

    /// <summary>Rebuilds for an island of W x H (visible part) when the size changed; true if it did.</summary>
    public bool Build(double W, double H, double topHidden)
    {
        if (Math.Abs(W - _w) < 0.25 && Math.Abs(H - _h) < 0.25 && topHidden == _top) return false;
        _w = W;
        _h = H;
        _top = topHidden;
        _pts.Clear();
        _at.Clear();
        const double inset = 1.1;
        bool floating = topHidden == 0;
        double r = W - inset, top = topHidden + (floating ? inset : 0), bottom = topHidden + H - inset;
        double rad = Math.Max(1, Math.Clamp(H / 2, 8, 26) - inset);

        void Add(double x, double y)
        {
            if (_pts.Count > 0)
            {
                var last = _pts[^1];
                _at.Add(_at[^1] + Math.Sqrt((x - last.X) * (x - last.X) + (y - last.Y) * (y - last.Y)));
            }
            else _at.Add(0);
            _pts.Add(new Point(x, y));
        }
        void Line(double x0, double y0, double x1, double y1)
        {
            int n = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0))));
            for (int i = _pts.Count == 0 ? 0 : 1; i <= n; i++) Add(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n);
        }
        void Arc(double cx, double cy, double a0, double a1)
        {
            int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(a1 - a0) * rad));
            for (int i = 1; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                Add(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a));
            }
        }

        if (floating)
        {
            Line(W / 2, top, r - rad, top);
            Arc(r - rad, top + rad, -Math.PI / 2, 0);
        }
        else Add(r, top);
        if (bottom - rad > _pts[^1].Y + 0.5) Line(r, _pts[^1].Y, r, bottom - rad);
        Arc(r - rad, bottom - rad, 0, Math.PI / 2);
        Line(r - rad, bottom, W / 2, bottom);
        Length = _at[^1];
        return true;
    }

    /// <summary>
    /// A stretch of light going all the way around: down the left side, along the bottom and up the right side
    /// (2 x Length in total), then back to the start, as if it passed behind the screen's top edge.
    /// <paramref name="from"/> may be anything: it wraps around.
    /// </summary>
    public Geometry LoopSegment(double from, double length)
    {
        var g = new StreamGeometry();
        double loop = 2 * Length;
        if (_pts.Count == 0 || loop < 1 || length < 0.5) return g;
        from = ((from % loop) + loop) % loop;
        using (var ctx = g.Open())
        {
            double a = from, b = from + length;
            if (b <= loop) Loop(ctx, a, b);
            else
            {
                Loop(ctx, a, loop);
                Loop(ctx, 0, b - loop);
            }
        }
        g.Freeze();
        return g;
    }

    /// <summary>Loop position s: 0..L = left half from the top down to the bottom centre, L..2L = right half back up.</summary>
    private void Loop(StreamGeometryContext ctx, double a, double b)
    {
        double L = Length;
        if (a < L) Side(ctx, a, Math.Min(b, L), mirror: true, backwards: false);
        if (b > L) Side(ctx, 2 * L - b, 2 * L - Math.Max(a, L), mirror: false, backwards: true);
    }

    /// <summary>Points of one half whose distance from the top is in [from, to]; "backwards" walks them bottom-up.</summary>
    private void Side(StreamGeometryContext ctx, double from, double to, bool mirror, bool backwards)
    {
        if (to - from < 0.3) return;
        bool started = false;
        int n = _pts.Count;
        for (int k = 0; k < n; k++)
        {
            int i = backwards ? n - 1 - k : k;
            if (_at[i] < from || _at[i] > to) continue;
            var p = _pts[i];
            if (mirror) p = new Point(_w - p.X, p.Y);
            if (!started)
            {
                ctx.BeginFigure(p, false, false);
                started = true;
            }
            else ctx.LineTo(p, true, true);
        }
    }

    /// <summary>How far down (0 = top, 1 = bottom) the outline is at that distance along the half outline.</summary>
    public double DownAt(double distance)
    {
        if (_pts.Count < 2) return 0;
        double top = _pts[0].Y, bottom = _pts[^1].Y;
        int i = _at.BinarySearch(distance);
        if (i < 0) i = Math.Min(~i, _pts.Count - 1);
        return bottom - top < 0.5 ? 0 : Math.Clamp((_pts[i].Y - top) / (bottom - top), 0, 1);
    }

    /// <summary>The stretch [from, to] (distance along the half outline) on both sides.</summary>
    public Geometry Segment(double from, double to)
    {
        var g = new StreamGeometry();
        if (to - from < 0.5 || _pts.Count == 0) return g;
        using (var ctx = g.Open())
        {
            for (int side = 0; side < 2; side++)
            {
                bool started = false;
                for (int i = 0; i < _pts.Count; i++)
                {
                    if (_at[i] < from) continue;
                    if (_at[i] > to) break;
                    var p = _pts[i];
                    if (side == 1) p = new Point(_w - p.X, p.Y);
                    if (!started)
                    {
                        ctx.BeginFigure(p, false, false);
                        started = true;
                    }
                    else ctx.LineTo(p, true, true);
                }
            }
        }
        g.Freeze();
        return g;
    }
}
