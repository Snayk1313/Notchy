using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The empty shelf: a little box with teal eyes. Every few seconds its lid opens, a file falls in, the lid
/// closes and the eyes smile ^ ^. In between the eyes blink and glance around, or follow the mouse when it's
/// over the shelf. Putting files on the shelf makes the box smile and the dashed edge glow.
/// </summary>
public partial class MainWindow
{
    private const double ShelfCycle = 6.5;   // seconds between two falling files
    private EyeRig? _shelfRig;
    private double _shelfStart = -1;         // when the face showed up (-1 = not showing)
    private double _shelfHappyUntil;
    private bool _shelfHappyShown;

    private void InitShelfFace()
    {
        _shelfRig = new EyeRig { Look = ShelfLook, Left = ShelfEyeLScale, Right = ShelfEyeRScale };
    }

    private void UpdateShelfFace()
    {
        if (_shelfRig == null) return;
        bool showing = _state == IslandState.Expanded && _tab == Tab.Shelf && ShelfEmpty.Visibility == Visibility.Visible;
        double t = AnimNow;
        if (!showing)
        {
            _shelfStart = -1;
            return;
        }
        if (_shelfStart < 0) _shelfStart = t + 0.9; // the first file falls shortly after the tab opens

        double s = t - _shelfStart;
        double c = s < 0 ? ShelfCycle : s % ShelfCycle;
        bool stored = t < _shelfHappyUntil;

        // lid and file
        double lid = stored ? 0 : Keys(c, (0, 0), (0.35, -38), (1.2, -38), (1.5, 0));
        ShelfLid.Angle = lid;
        ShelfFileY.Y = Keys(c, (0.2, -11), (1.2, 8));
        ShelfFallingFile.Opacity = stored ? 0 : Keys(c, (0.15, 0), (0.4, 1), (1.15, 1), (1.25, 0));

        bool happy = stored || (c > 1.5 && c < 2.4);
        if (happy != _shelfHappyShown)
        {
            _shelfHappyShown = happy;
            var eyes = happy ? Visibility.Collapsed : Visibility.Visible;
            var smile = happy ? Visibility.Visible : Visibility.Collapsed;
            ShelfEyeL.Visibility = ShelfEyeR.Visibility = eyes;
            ShelfHappyL.Visibility = ShelfHappyR.Visibility = smile;
        }

        var pose = EyePose.Rest;
        if (!stored && c < 1.5)
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
        if (blink < 0.14) pose.L = pose.R = 0.15;
        ApplyPose(_shelfRig, pose, Ease(0.3));
    }

    /// <summary>Files were put on the shelf: a smile (when the box was showing) and a glow along the edge.</summary>
    private void ShelfStored(bool wasEmpty)
    {
        NavDone(Tab.Shelf);
        var glow = new DoubleAnimationUsingKeyFrames();
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(140)))));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Dur(900))),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        ShelfGlow.BeginAnimation(OpacityProperty, glow);

        if (!wasEmpty || ShelfEmpty.Visibility != Visibility.Visible || _state != IslandState.Expanded)
        {
            RenderShelf();
            return;
        }
        // let the box smile for a moment before the files take its place
        _shelfHappyUntil = AnimNow + 0.7;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RenderShelf();
        };
        timer.Start();
    }
}
