using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The empty shelf: a little box with teal eyes. Every few seconds its lid opens, a file falls in, the lid
/// closes and the eyes smile ^ ^. In between the eyes blink and glance around, or follow the mouse when it's
/// over the shelf. While files are dragged over it the eyes go wide and follow them; when the first files are
/// dropped the box jumps to catch them, throws teal sparks, and the files spread out from the middle.
/// </summary>
public partial class MainWindow
{
    private const double ShelfCycle = 6.5;   // seconds between two falling files
    private const double CatchRender = 0.95; // (catch timeline, seconds) when the files take the box's place
    private EyeRig? _shelfRig;
    private double _shelfStart = -1;         // when the face showed up (-1 = not showing)
    private double _shelfCatchAt = -1;       // when the first files were caught (-1 = not catching)
    private bool _shelfHappyShown;
    private bool _shelfSpread;               // the next RenderShelf spreads the files out from the middle
    private Point _shelfDragPos;             // where files are being dragged, over the face
    private readonly Ellipse[] _shelfSparks = new Ellipse[8];

    private void InitShelfFace()
    {
        _shelfRig = new EyeRig { Look = ShelfLook, Left = ShelfEyeLScale, Right = ShelfEyeRScale };
        var host = new Canvas
        {
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = 0.9, Color = ShelfColor }
        };
        var fill = new SolidColorBrush(ShelfColor);
        fill.Freeze();
        for (int i = 0; i < _shelfSparks.Length; i++)
            host.Children.Add(_shelfSparks[i] = new Ellipse { Fill = fill, Opacity = 0 });
        ShelfFace.Children.Add(host);
    }

    private void UpdateShelfFace()
    {
        if (_shelfRig == null) return;
        if (_shelfClearAt >= 0 && UpdateShelfClear()) return;
        bool showing = _state == IslandState.Expanded && _tab == Tab.Shelf && ShelfEmpty.Visibility == Visibility.Visible;
        double t = AnimNow;
        if (!showing)
        {
            if (_shelfStart >= 0) ResetShelfCatch();
            _shelfStart = -1;
            return;
        }
        if (_shelfStart < 0) _shelfStart = t + 0.9; // the first file falls shortly after the tab opens

        if (_shelfCatchAt >= 0)
        {
            UpdateShelfCatch((t - _shelfCatchAt) / Math.Max(S.SpeedFactor, 0.01));
            return;
        }

        double s = t - _shelfStart;
        double c = s < 0 ? ShelfCycle : s % ShelfCycle;

        // lid and file
        ShelfLid.Angle = Keys(c, (0, 0), (0.35, -38), (1.2, -38), (1.5, 0));
        ShelfFileY.Y = Keys(c, (0.2, -11), (1.2, 8));
        ShelfFallingFile.Opacity = _dragOver ? 0 : Keys(c, (0.15, 0), (0.4, 1), (1.15, 1), (1.25, 0));

        SetShelfHappy(!_dragOver && c > 1.5 && c < 2.4);

        var pose = EyePose.Rest;
        if (_dragOver)
        {
            // files coming: eyes wide open, following them
            pose.X = Math.Clamp((_shelfDragPos.X - 14) / 30, -1, 1) * 2.2;
            pose.Y = Math.Clamp((_shelfDragPos.Y - 15) / 20, -1, 1) * 1.4;
            pose.L = pose.R = pose.W = 1.45;
        }
        else if (c < 1.5)
        {
            // watch the file come in: up, then follow it down
            pose.Y = Keys(c, (0, 0), (0.3, -1.6), (0.55, -1.6), (1.1, 1.1), (1.45, 0));
            pose.X = Keys(c, (0, 0), (0.4, 0.5), (1.1, 0.2), (1.45, 0));
        }
        else if (ShelfDropZone.IsMouseOver)
        {
            var p = Mouse.GetPosition(ShelfFace);
            pose.X = Math.Clamp((p.X - 14) / 30, -1, 1) * 2.2;
            pose.Y = Math.Clamp((p.Y - 15) / 20, -1, 1) * 1.4;
        }
        else
        {
            pose.X = Keys(t % 5.2, (0, 0), (1.6, 0), (1.9, 1.3), (2.8, 1.3), (3.1, -1.3), (4.0, -1.3), (4.4, 0));
        }

        double blink = (t + _blinkSeed) % 4.2;
        if (blink < 0.14 && !_dragOver) pose.L = pose.R = 0.15;
        ApplyPose(_shelfRig, pose, Ease(0.3));
    }

    private void SetShelfHappy(bool happy)
    {
        if (happy == _shelfHappyShown) return;
        _shelfHappyShown = happy;
        FlipEyes(happy, ShelfEyeL, ShelfEyeR, ShelfHappyL, ShelfHappyR);
    }

    /// <summary>The catch, <paramref name="k"/> seconds after the drop: jump, lid, sparks, then the box goes away.</summary>
    private void UpdateShelfCatch(double k)
    {
        double vanish = Keys(k, (0, 1), (0.62, 1), (0.72, 1.2), (0.92, 0));
        ShelfBoxY.Y = Keys(k, (0, 0), (0.11, 2.5), (0.28, -11), (0.45, 1.5), (0.56, 0));
        ShelfBoxScale.ScaleX = Keys(k, (0, 1), (0.11, 1.1), (0.28, 0.92), (0.45, 1.12), (0.56, 1)) * vanish;
        ShelfBoxScale.ScaleY = Keys(k, (0, 1), (0.11, 0.88), (0.28, 1.1), (0.45, 0.88), (0.56, 1)) * vanish;
        ShelfFace.Opacity = Keys(k, (0, 1), (0.72, 1), (0.92, 0));
        ShelfEmptyText.Opacity = Keys(k, (0, 1), (0.6, 1), (0.8, 0)); // (Keys must start at 0)
        ShelfLid.Angle = Keys(k, (0, 0), (0.2, -50), (0.36, 0));
        ShelfFallingFile.Opacity = 0;

        // up at the files while jumping, then ^ ^
        SetShelfHappy(k > 0.26);
        var pose = EyePose.Rest;
        if (k <= 0.26)
        {
            pose.Y = -1.6;
            pose.L = pose.R = pose.W = 1.45;
        }
        ApplyPose(_shelfRig!, pose, Ease(0.4));

        // eight sparks burst out around the box
        double u = Math.Clamp((k - 0.3) / 0.52, 0, 1), o = 1 - Math.Pow(1 - u, 3);
        for (int i = 0; i < _shelfSparks.Length; i++)
        {
            var e = _shelfSparks[i];
            double a = i * Math.PI * 2 / _shelfSparks.Length - Math.PI / 2, size = 3.4 - 2.4 * o;
            e.Width = e.Height = size;
            Canvas.SetLeft(e, 14 + Math.Cos(a) * 22 * o - size / 2);
            Canvas.SetTop(e, 15 + Math.Sin(a) * 15 * o - size / 2);
            e.Opacity = k < 0.3 ? 0 : 1 - u;
        }
    }

    private void ResetShelfCatch()
    {
        _shelfCatchAt = -1;
        ShelfBoxY.Y = 0;
        ShelfBoxScale.ScaleX = ShelfBoxScale.ScaleY = 1;
        ShelfFace.Opacity = ShelfEmptyText.Opacity = 1;
        foreach (var e in _shelfSparks) e.Opacity = 0;
        SetShelfHappy(false);
    }

    /// <summary>Files were put on the shelf: the box catches the first ones (when it was showing), and the edge glows.</summary>
    private void ShelfStored(bool wasEmpty)
    {
        NavDone(Tab.Shelf);
        var glow = new DoubleAnimationUsingKeyFrames();
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(140)))));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(900))),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        ShelfGlow.BeginAnimation(OpacityProperty, glow);

        if (!wasEmpty || ShelfEmpty.Visibility != Visibility.Visible || _state != IslandState.Expanded || S.SpeedFactor <= 0)
        {
            RenderShelf();
            return;
        }
        _shelfCatchAt = AnimNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Dur(CatchRender * 1000)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _shelfSpread = true;
            RenderShelf();
        };
        timer.Start();
    }

    /// <summary>Right after the box went away: each file springs from the middle of the shelf to its place, the middle ones first.</summary>
    private void SpreadShelf()
    {
        _shelfSpread = false;
        var mid = new Point(ShelfDropZone.ActualWidth / 2, ShelfDropZone.ActualHeight / 2);
        var cards = ShelfItems.Children.OfType<Border>()
            .Select(c => (c, d: (Vector)(mid - c.TranslatePoint(new Point(c.ActualWidth / 2, c.ActualHeight / 2), ShelfDropZone))))
            .OrderBy(x => Math.Abs(x.d.X)).ToList();
        for (int i = 0; i < cards.Count; i++)
        {
            var (card, d) = cards[i];
            var begin = TimeSpan.FromMilliseconds(Dur(60 * i));
            var time = TimeSpan.FromMilliseconds(Dur(520));
            var spring = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
            var move = new TranslateTransform();
            ((FrameworkElement)card.Child).RenderTransform = move;
            // the move itself doesn't overshoot (a file at the edge would pass its place and get cut); only the size springs
            var glide = new QuinticEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(d.X, 0, time) { BeginTime = begin, EasingFunction = glide });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(d.Y, 0, time) { BeginTime = begin, EasingFunction = glide });
            var grow = new DoubleAnimation(0, 1, time) { BeginTime = begin, EasingFunction = spring };
            var scale = (ScaleTransform)card.RenderTransform;
            scale.ScaleX = scale.ScaleY = 0; // (hidden until its turn)
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(150))) { BeginTime = begin });
        }
    }
}
