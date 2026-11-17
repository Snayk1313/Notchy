using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// "Limpar" on the shelf: the files pop one by one (right to left) into teal sparks that hang in the air. When the
/// last one is gone the box shows up with its lid open and wide eyes, the sparks fly into it, the lid closes and it
/// bounces with a ^ ^. Times below are seconds since the click (before the speed setting).
/// </summary>
public partial class MainWindow
{
    private sealed class ShelfSpark
    {
        public required Ellipse E;
        public Point From;
        public Vector Out;
        public double Born, Size, Delay;
    }

    private const double PopStep = 0.09, PopBurst = 0.12, SparkOut = 0.35, SparkPull = 0.45;
    private readonly List<ShelfSpark> _clearSparks = new();
    private double _shelfClearAt = -1;   // when Limpar was clicked (-1 = not clearing)
    private double _clrBox, _clrPull, _clrEnd;
    private bool _clrRendered;
    private Point? _clrMouth;

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfSel.Mode) // while selecting, this button is "Marcar todos"
        {
            SoundService.Play(Sfx.Click);
            _shelfSel.MarkAll();
            return;
        }
        if (_shelfClearAt >= 0) return;

        // the cards you can see, right to left
        double view = ShelfScroll.ViewportWidth;
        var cards = ShelfItems.Children.OfType<Border>()
            .Select(c => (c, x: c.TranslatePoint(new Point(), ShelfScroll).X))
            .Where(v => v.x + v.c.ActualWidth > 0 && v.x < view)
            .OrderByDescending(v => v.x).Select(v => v.c).ToList();

        _shelf.Clear();
        SaveShelf();
        if (S.SpeedFactor <= 0 || _state != IslandState.Expanded || cards.Count == 0)
        {
            RenderShelf();
            return;
        }
        StartShelfClear(cards);
    }

    private void StartShelfClear(List<Border> cards)
    {
        var rnd = new Random();
        var fill = new SolidColorBrush(ShelfColor);
        fill.Freeze();
        for (int j = 0; j < cards.Count; j++)
        {
            var card = cards[j];
            // swells a little, then pops
            var begin = TimeSpan.FromMilliseconds(Dur(PopStep * 1000 * j));
            var scale = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            scale.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(80)))));
            scale.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(220))),
                new CubicEase { EasingMode = EasingMode.EaseIn }));
            var st = (ScaleTransform)card.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            var fade = new DoubleAnimationUsingKeyFrames { BeginTime = begin };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(80)))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(220)))));
            card.BeginAnimation(OpacityProperty, fade);
            card.IsHitTestVisible = false;

            var from = card.TranslatePoint(new Point(card.ActualWidth / 2, 29), ShelfFx); // (the icon's middle)
            for (int i = 0; i < 10; i++)
            {
                double a = i * Math.PI * 2 / 10 + (rnd.NextDouble() - 0.5) * 0.6, r = 14 + rnd.NextDouble() * 16;
                var s = new ShelfSpark
                {
                    E = new Ellipse { Fill = fill, Opacity = 0 },
                    From = from,
                    Out = new Vector(Math.Cos(a) * r, Math.Sin(a) * r * 0.75),
                    Born = PopStep * j + PopBurst,
                    Size = 2.6 + rnd.NextDouble() * 2.2,
                    Delay = rnd.NextDouble() * 0.12
                };
                _clearSparks.Add(s);
                ShelfFx.Children.Add(s.E);
            }
        }
        foreach (var f in new UIElement[] { ShelfHint, ShelfClear })
            f.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(200))));

        _clrBox = PopStep * (cards.Count - 1) + PopBurst + 0.38; // every pop is over
        _clrPull = _clrBox + 0.3;                                // the lid is open
        _clrEnd = _clrPull + 0.12 + SparkPull;                   // the last spark is in
        _clrRendered = false;
        _clrMouth = null;
        _shelfClearAt = AnimNow;
    }

    /// <summary>One frame of the clear; true while it's driving the box (the usual box loop waits).</summary>
    private bool UpdateShelfClear()
    {
        if (_shelf.Count > 0) // files were dropped meanwhile: they win
        {
            EndShelfClear();
            return false;
        }
        double k = (AnimNow - _shelfClearAt) / Math.Max(S.SpeedFactor, 0.01);

        if (k >= _clrPull && _clrMouth == null && ShelfFace.IsVisible)
            _clrMouth = ShelfFace.TranslatePoint(new Point(14, 9), ShelfFx);
        foreach (var s in _clearSparks)
        {
            double u = k - s.Born;
            if (u < 0) continue;
            double o = 1 - Math.Pow(1 - Math.Min(u / SparkOut, 1), 3);
            // out, then hanging in the air with a slow drift up
            var p = s.From + s.Out * o + new Vector(0, -Math.Min(u, 1.5) * 3 + Math.Sin(k * 6 + s.Delay * 50) * 0.6);
            double size = s.Size, op = 1;
            if (_clrMouth is Point m && k >= _clrPull + s.Delay)
            {
                double w = Math.Min((k - _clrPull - s.Delay) / SparkPull, 1), e = w * w;
                p = p + (m - p) * e - new Vector(0, Math.Sin(Math.PI * w) * 12);
                size *= 1 - 0.6 * w;
                op = w < 0.85 ? 1 : (1 - w) / 0.15;
            }
            s.E.Width = s.E.Height = size;
            Canvas.SetLeft(s.E, p.X - size / 2);
            Canvas.SetTop(s.E, p.Y - size / 2);
            s.E.Opacity = op;
        }

        if (k < _clrBox) return false;
        if (!_clrRendered)
        {
            _clrRendered = true;
            RenderShelf();
        }

        // the box: pops in with the lid opening, swallows the sparks, closes and bounces
        double kb = k - _clrBox, e2 = k - _clrEnd;
        double pop = Keys(kb, (0, 0.3), (0.2, 1.1), (0.28, 1));
        ShelfBoxScale.ScaleX = pop * (e2 > 0 ? Keys(e2, (0, 1), (0.15, 0.94), (0.28, 1.08), (0.38, 1)) : 1);
        ShelfBoxScale.ScaleY = pop * (e2 > 0 ? Keys(e2, (0, 1), (0.15, 1.08), (0.28, 0.9), (0.38, 1)) : 1);
        ShelfBoxY.Y = e2 > 0 ? Keys(e2, (0, 0), (0.15, -6), (0.28, 0)) : 0;
        ShelfFace.Opacity = Keys(kb, (0, 0), (0.08, 1));
        ShelfLid.Angle = e2 < 0 ? Keys(kb, (0, 0), (0.12, 0), (0.3, -55)) : Keys(e2, (0, -55), (0.15, 0));
        ShelfEmptyText.Opacity = e2 < 0 ? 0 : Keys(e2, (0, 0), (0.3, 1));
        ShelfFallingFile.Opacity = 0;

        SetShelfHappy(e2 >= 0);
        var pose = EyePose.Rest;
        if (e2 < 0)
        {
            pose.Y = -0.8; // wide open, looking up at the sparks
            pose.L = pose.R = pose.W = 1.45;
        }
        ApplyPose(_shelfRig!, pose, Ease(0.4));

        if (e2 < 1.0) return true;
        EndShelfClear();
        return false;
    }

    private void EndShelfClear()
    {
        _shelfClearAt = -1;
        foreach (var s in _clearSparks) ShelfFx.Children.Remove(s.E);
        _clearSparks.Clear();
        foreach (var f in new UIElement[] { ShelfHint, ShelfClear })
        {
            f.BeginAnimation(OpacityProperty, null);
            f.Opacity = 1;
        }
        ResetShelfCatch();
        if (!_clrRendered) RenderShelf();
    }
}
