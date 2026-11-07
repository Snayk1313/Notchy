using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The bubble next to the island is shared: Claude's (orange, its light running around) or the focus timer's
/// (purple concentrating eyes, the light filling the outline as time goes by). The focus takes the bubble when
/// something else has the island (music, Claude, a download); Claude still comes first. When one hands the
/// bubble to the other, the bubble stays put: the eyes leave by the top edge ("behind the screen"), the light
/// changes colour, and the other pair comes down.
/// </summary>
public partial class MainWindow
{
    private enum PodKind { Claude, Focus }

    private PodKind _podKind = PodKind.Claude;
    private double _podSwapAt = -1;
    private PodKind _podSwapFrom, _podSwapTo;
    private bool _podSwapFlipped;
    private const double PodSwapTime = 0.8, PodSwapRise = 26;

    private static readonly Color PodClaudeTrack = Color.FromRgb(0xE8, 0x82, 0x5F), PodClaudeArc = Color.FromRgb(0xFF, 0x9A, 0x6E),
        PodClaudeEye = Color.FromRgb(0xFF, 0xD9, 0xC4);

    private bool PodSwapping => _podSwapAt >= 0;

    /// <summary>The focus wants the bubble: it's running and something else has the island.</summary>
    private bool FocusPodWanted() =>
        _focus.Running && !_hiddenForFullscreen && !_introActive && _state == IslandState.Compact &&
        (_compactMode == CompactMode.Music || IsClaudeMode(_compactMode) || IsTransferMode(_compactMode));

    private (Color track, Color arc, Color eye) PodColors(PodKind kind)
    {
        if (kind == PodKind.Claude) return (PodClaudeTrack, PodClaudeArc, PodClaudeEye);
        var c = FocusModeColor;
        return (c, Color.FromRgb((byte)((c.R + 255) / 2), (byte)((c.G + 255) / 2), (byte)((c.B + 255) / 2)), c);
    }

    private void PaintPod((Color track, Color arc, Color eye) c)
    {
        SetBrushColor(PodTrack, Shape.StrokeProperty, c.track);
        SetBrushColor(PodArc, Shape.StrokeProperty, c.arc);
        if (PodArc.Effect is DropShadowEffect ag && ag.Color != c.track) ag.Color = c.track;
        if (PodEyeL.Parent is FrameworkElement g && g.Parent is StackPanel eyes && eyes.Effect is DropShadowEffect eg && eg.Color != c.track) eg.Color = c.track;
        if (PodEyeL.Fill is not SolidColorBrush eb || eb.Color != c.eye)
        {
            var b = new SolidColorBrush(c.eye);
            PodEyeL.Fill = PodEyeR.Fill = b;
            PodHappyL.Stroke = PodHappyR.Stroke = b;
        }
    }

    /// <summary>Makes the bubble Claude's or the focus' right away (the eyes are out of sight, or it's just appearing).</summary>
    private void SetPodKind(PodKind kind)
    {
        _podKind = kind;
        PaintPod(PodColors(kind));
        if (kind == PodKind.Focus)
        {
            // concentrating eyes, no ^^, no attention pulse; the light fills the outline
            PodEyeL.Visibility = PodEyeR.Visibility = Visibility.Visible;
            PodHappyL.Visibility = PodHappyR.Visibility = Visibility.Collapsed;
            PodTrack.BeginAnimation(OpacityProperty, null);
            PodTrack.Opacity = 0.35;
            PodTrack.StrokeThickness = 1.6;
            PodArc.BeginAnimation(OpacityProperty, null);
            PodArc.Visibility = Visibility.Visible;
            _podWorking = true;
        }
        else
        {
            // let UpdatePod work out Claude's look again (happy, attention, light)
            _podHappy = !(DateTime.Now < _podDoneUntil);
            _podAttentionAnim = !(_claude.Active?.NeedsAttention == true);
            _podWorking = !(_claude.Active?.NeedsAttention != true && !(DateTime.Now < _podDoneUntil));
        }
        _podShapeW = -1;
        DrawClaudePod(AnimNow);
    }

    private void StartPodSwap(PodKind to)
    {
        _podSwapFrom = _podKind;
        _podSwapTo = to;
        _podSwapAt = AnimNow;
        _podSwapFlipped = false;
    }

    /// <summary>Every frame: the hand-over between Claude and the focus, and the focus bubble's eyes.</summary>
    private void UpdateFocusPod()
    {
        if (PodSwapping)
        {
            double s = (AnimNow - _podSwapAt) / (PodSwapTime * Math.Max(0.3, S.SpeedFactor));
            if (s >= 1)
            {
                _podSwapAt = -1;
                PodTrip.Y = 0;
                PodArc.Opacity = 1;
                if (!_podSwapFlipped) SetPodKind(_podSwapTo);
            }
            else
            {
                // out by the top (ease in), then the other pair comes down (with a little bounce)
                double y = s < 0.45 ? -PodSwapRise * EaseIn(s / 0.45)
                    : s < 0.55 ? -PodSwapRise
                    : -PodSwapRise * (1 - EaseOutBack((s - 0.55) / 0.45));
                PodTrip.Y = y;
                if (!_podSwapFlipped && s >= 0.5)
                {
                    _podSwapFlipped = true;
                    SetPodKind(_podSwapTo);
                    if (_podSwapTo == PodKind.Focus && _podRig != null) _podRig.Pose = EyePose.Rest;
                }
                // the light changes colour on the way, dimming while nobody's home
                double cu = Math.Clamp((s - 0.3) / 0.4, 0, 1);
                var a = PodColors(_podSwapFrom);
                var b = PodColors(_podSwapTo);
                PaintPod((Mix(a.track, b.track, cu), Mix(a.arc, b.arc, cu), s < 0.5 ? a.eye : b.eye));
                PodArc.Opacity = 1 - 0.75 * Math.Sin(Math.PI * Math.Clamp((s - 0.15) / 0.7, 0, 1));
            }
        }

        if (!(_podShown || _podMorphing) || _podKind != PodKind.Focus || _podRig == null) return;
        PaintPod(PodColors(PodKind.Focus)); // follows focus ↔ break
        double t = AnimNow;
        bool focusMode = _focus.Mode == FocusMode.Focus;
        bool blink = (t + 2.1) % 4.6 < 0.14;
        var pose = EyePose.Rest;
        pose.X = focusMode ? Math.Sin(t * 0.7) * 1.6 : 0;
        pose.Y = focusMode ? 0 : 1.5;
        pose.L = pose.R = blink ? 0.12 : focusMode ? 0.55 : 0.3;
        if (_podMorphing)
        {
            // growing into the island (or shrinking out of it): the eyes glide to where the island keeps them,
            // on its left (16 px in, 17 px wide), so nothing jumps when the island takes over
            double islandW = CompactWidth(CompactMode.Focus);
            double w = ClaudePod.ActualWidth > 1 ? ClaudePod.ActualWidth : 70;
            double u = Math.Clamp((w - 70) / (islandW - 70), 0, 1);
            pose.X = (16 + 8.5 - islandW / 2) * u;
            ApplyPose(_podRig, pose, 1);
            return;
        }
        ApplyPose(_podRig, pose, Ease(0.25));
    }

    /// <summary>Music stopped while the focus had the bubble: the bubble glides over and becomes the island.</summary>
    private void MorphFocusPod()
    {
        _podShown = false;
        _podMorphing = true;
        _focusLightOpacity = 0;
        MorphPodIntoIsland(ClaudePod, PodX, _podSlide, CompactWidth(CompactMode.Focus), CompactPanel, () =>
        {
            // the island's light is exactly where the bubble's is: on at once
            _focusLightOpacity = 1;
            _focusDrawn = -1;
        }, () =>
        {
            _podMorphing = false;
            UpdatePodPosition();
        });
    }

    /// <summary>Music came back while the focus had the island: the island becomes the focus bubble.</summary>
    private void MorphFocusIslandIntoPod()
    {
        SetPodKind(PodKind.Focus);
        _podShown = true;
        _podMorphing = true;
        PodArc.BeginAnimation(OpacityProperty, null);
        PodArc.Opacity = 1;
        PodArc.Visibility = Visibility.Visible;
        // the bubble draws the light from now on
        _focusLightOpacity = 0;
        FocusLight.Visibility = FocusTrack.Visibility = Visibility.Collapsed;
        _podShapeW = -1;
        DrawClaudePod(AnimNow);

        double s = S.SizeScale;
        double toX = -(TargetSize(_state).Width * s / 2 + 8 * s + 70 * s / 2);
        MorphIslandIntoPod(ClaudePod, PodScale, PodX, _podSlide, CompactWidth(CompactMode.Focus), toX, () =>
        {
            _podMorphing = false;
            UpdatePodPosition();
        });
    }

    /// <summary>Changes a brush's colour in place (a new brush only if it's shared/frozen or another kind).</summary>
    private static void SetBrushColor(DependencyObject el, DependencyProperty prop, Color color)
    {
        if (el.GetValue(prop) is SolidColorBrush b && !b.IsFrozen)
        {
            if (b.Color != color) b.Color = color;
        }
        else el.SetValue(prop, new SolidColorBrush(color));
    }

    private static double EaseIn(double u) => u * u * u;

    private static double EaseOutBack(double u)
    {
        const double c1 = 1.4, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(u - 1, 3) + c1 * Math.Pow(u - 1, 2);
    }

    private static Color Mix(Color a, Color b, double u) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * u), (byte)(a.G + (b.G - a.G) * u), (byte)(a.B + (b.B - a.B) * u));
}
