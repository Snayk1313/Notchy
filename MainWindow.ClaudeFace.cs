using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Claude working with nothing else on the island: a small island with orange eyes and no text. The eyes
/// act out what Claude is doing (reading = scanning lines, thinking = looking up, editing = focused down,
/// running a command = curious glances), an orange light runs around the island's edge, a permission
/// request makes the eyes go wide while the outline pulses, and a finished task gets happy ^ ^ eyes and
/// an outline that lights up all around. The pod next to the music island uses the same expressions.
/// </summary>
public partial class MainWindow
{
    private struct EyePose
    {
        public double X, Y, L, R, W; // look offset, vertical scale of each eye, width scale
        public static EyePose Rest => new() { L = 1, R = 1, W = 1 };
    }

    private sealed class EyeRig
    {
        public required TranslateTransform Look;
        public required ScaleTransform Left, Right;
        public EyePose Pose = EyePose.Rest;
    }

    private static readonly Color ClaudeEyeColor = Color.FromRgb(0xFF, 0xB8, 0x94);
    private const double ClaudeIslandWidth = 160;

    private EyeRig? _claudeRig, _podRig;
    private readonly IslandOutline _claudeOutline = new();
    private DateTime _claudeFlashUntil;   // done: the whole outline lights up, then fades
    private bool _claudeHappyShown;
    private double _claudeLightOpacity;
    private readonly double _blinkSeed = new Random().NextDouble() * 10;

    private static bool IsClaudeMode(CompactMode m) => m is CompactMode.Claude or CompactMode.ClaudeDone or CompactMode.ClaudeAttention;

    private void InitClaudeFace()
    {
        System.Threading.Tasks.Task.Run(ClaudeHooks.AddMissingPermissionHook); // answering on the island needs it
        _claudeRig = new EyeRig { Look = ClaudeLook, Left = ClaudeEyeLScale, Right = ClaudeEyeRScale };
        var podL = new ScaleTransform();
        var podR = new ScaleTransform();
        PodEyeL.RenderTransform = podL;
        PodEyeR.RenderTransform = podR;
        _podRig = new EyeRig { Look = PodLook, Left = podL, Right = podR };

        var eye = new SolidColorBrush(ClaudeEyeColor);
        eye.Freeze();
        ClaudeEyeL.Fill = ClaudeEyeR.Fill = eye;
        ClaudeHappyL.Stroke = ClaudeHappyR.Stroke = eye;
        var light = new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x6E));
        light.Freeze();
        ClaudeLight.Stroke = light;
        var track = new SolidColorBrush(ClaudeColor);
        track.Freeze();
        ClaudeTrack.Stroke = track;
    }

    /// <summary>Called when Claude finishes (the outline lights up all around before fading).</summary>
    private void ClaudeFaceDone() => _claudeFlashUntil = DateTime.UtcNow.AddSeconds(1.8);

    // ------------------------------------------------------------------ bubble → island

    private bool _podMorphing;
    private bool _islandMorphing; // the island is out of the way while a bubble takes its place

    /// <summary>
    /// When the music stops while a bubble (Claude / download) is showing, the bubble becomes the island:
    /// first the music island goes up into the top edge and disappears; only then the bubble glides to the
    /// centre and grows to the new island's size, its light stretching into the island's edge on the way.
    /// The island comes back right under it, looking the same (eyes, outline, light), and the bubble melts away.
    /// </summary>
    private void MorphPodIntoIsland(Border pod, TranslateTransform x, TranslateTransform slide, double width,
        UIElement islandPanel, Action handOver, Action finished)
    {
        var up = TimeSpan.FromMilliseconds(Dur(280));
        var d = TimeSpan.FromMilliseconds(Dur(460));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _islandMorphing = true;

        // 1) the island goes up and away (keeps its width, its content fades)
        islandPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(Island.ActualWidth, TimeSpan.Zero));
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(_topHidden, up)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });

        // 2) once it's gone, the bubble takes its place
        Panel.SetZIndex(pod, 10);
        pod.IsHitTestVisible = false;
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = ease, BeginTime = up });
        x.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, d) { EasingFunction = ease, BeginTime = up });
        var grow = new DoubleAnimation(width, d) { EasingFunction = ease, BeginTime = up };
        grow.Completed += (_, _) =>
        {
            // 3) the island comes back right underneath, the same size, and the bubble melts into it
            var size = TargetSize(_state);
            Island.BeginAnimation(WidthProperty, null);
            Island.BeginAnimation(HeightProperty, null);
            Island.Width = size.Width;
            Island.Height = size.Height + _topHidden;
            AnimateIslandSize(size, bigMove: false); // (nothing to move: sets the panels' width)
            islandPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(180))));
            _islandMorphing = false;
            handOver(); // the island's light is already exactly where the bubble's is: show it at once

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(200)));
            fade.Completed += (_, _) =>
            {
                pod.Visibility = Visibility.Collapsed;
                pod.BeginAnimation(WidthProperty, null);
                pod.Width = 70;
                pod.IsHitTestVisible = true;
                Panel.SetZIndex(pod, 0);
                x.BeginAnimation(TranslateTransform.XProperty, null);
                slide.BeginAnimation(TranslateTransform.XProperty, null);
                finished();
            };
            pod.BeginAnimation(OpacityProperty, fade);
        };
        pod.BeginAnimation(WidthProperty, grow);
    }

    private bool _podEntering; // the island just became a bubble: it stays up until the bubble has moved aside

    /// <summary>
    /// The other way round: music starts while Claude / a download has the island. The bubble appears right
    /// over the island, looking exactly the same (same eyes, same edge light), and the island hides behind it.
    /// The bubble shrinks back into its oval while it moves aside; only then the music island comes down.
    /// </summary>
    private void MorphIslandIntoPod(Border pod, ScaleTransform scale, TranslateTransform x, TranslateTransform slide,
        double fromWidth, double toX, Action finished)
    {
        var d = TimeSpan.FromMilliseconds(Dur(460));
        var down = TimeSpan.FromMilliseconds(Dur(280));
        _islandMorphing = true;
        _podEntering = true;

        // 1) the bubble, at the island's size and place
        double s = S.SizeScale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = scale.ScaleY = s;
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.X = 0;
        x.BeginAnimation(TranslateTransform.XProperty, null);
        x.X = 0;
        pod.BeginAnimation(OpacityProperty, null);
        pod.Opacity = 1;
        pod.BeginAnimation(WidthProperty, null);
        pod.Width = fromWidth;
        pod.Visibility = Visibility.Visible;
        pod.IsHitTestVisible = false;
        Panel.SetZIndex(pod, 10);

        // ...and the island hides behind it right away (taking the music island's width while it's out of sight)
        var size = TargetSize(_state);
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, TimeSpan.Zero));
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(_topHidden, TimeSpan.Zero));

        // 2) the bubble shrinks into its oval while it moves aside
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        x.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(toX, d) { EasingFunction = ease });
        var shrink = new DoubleAnimation(70, d) { EasingFunction = ease };
        shrink.Completed += (_, _) =>
        {
            pod.BeginAnimation(WidthProperty, null);
            pod.Width = 70;
            x.BeginAnimation(TranslateTransform.XProperty, null);
            x.X = toX;
            pod.IsHitTestVisible = true;
            Panel.SetZIndex(pod, 0);
            _podEntering = false;
            _islandMorphing = false;
            finished();

            // 3) only now the island comes down from the top
            var target = TargetSize(_state);
            Island.BeginAnimation(WidthProperty, null);
            Island.Width = target.Width;
            Island.Height = target.Height + _topHidden;
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(_topHidden, target.Height + _topHidden, down)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        };
        pod.BeginAnimation(WidthProperty, shrink);
    }

    /// <summary>Music came back while Claude had the island: the island becomes the Claude bubble.</summary>
    private void MorphClaudeIslandIntoPod()
    {
        var c = _claude.Active;
        bool attention = c?.NeedsAttention == true;
        bool done = DateTime.Now < _podDoneUntil;
        _podShown = true;
        _podMorphing = true;
        _podHappy = !done;                 // force refresh (UpdatePod)
        _podAttentionAnim = !attention;    // force refresh
        _podWorking = !attention && !done; // the light is on from the first frame (no fade-in)
        PodArc.BeginAnimation(OpacityProperty, null);
        PodArc.Opacity = 1;
        PodArc.Visibility = _podWorking ? Visibility.Visible : Visibility.Collapsed;
        CopyPose(_claudeRig, _podRig); // the bubble's eyes start where the island's are
        _claudeLightOpacity = 0;       // the bubble draws the light from now on
        ClaudeLight.Visibility = ClaudeTrack.Visibility = Visibility.Collapsed;
        _podShapeW = -1;
        DrawClaudePod(AnimNow);

        double s = S.SizeScale;
        double toX = -(TargetSize(_state).Width * s / 2 + 8 * s + 70 * s / 2);
        MorphIslandIntoPod(ClaudePod, PodScale, PodX, _podSlide, ClaudeIslandWidth, toX, () =>
        {
            _podMorphing = false;
            UpdatePodPosition();
        });
    }

    // ---- the bubbles' outline: an oval inside the bubble that stretches into the island's edge (u: 0 → 1)

    private double _dlPodEyesU = 1; // 1 = the download bubble shows eyes, 0 = the % (Claude has the eyes)
    // (also true while the focus has the bubble or the island: it has eyes too, so the download shows its %)
    private bool ClaudeOnScreen => _podShown || _podMorphing || (_state == IslandState.Compact && (IsClaudeMode(_compactMode) || _compactMode is CompactMode.Focus or CompactMode.Timer or CompactMode.TimerRing));
    private readonly RoundOutline _podShape = new(), _dlPodShape = new();
    private double _podShapeW = -1, _podShapeU = -1, _dlShapeW = -1, _dlShapeU = -1;

    private static double Lerp(double a, double b, double u) => a + (b - a) * u;

    private bool ShapePod(RoundOutline shape, double w, double u, ref double lastW, ref double lastU)
    {
        if (Math.Abs(w - lastW) < 0.2 && Math.Abs(u - lastU) < 0.002) return false;
        lastW = w;
        lastU = u;
        const double h = 34;
        // the island's edge: 1.1 px inside, round bottom corners, the top hidden behind the screen edge
        double endRadius = Math.Clamp(h / 2, 8, 26) - 1.1;
        double endTop = _topHidden == 0 ? 1.1 : -(endRadius + 1.5);
        double pad = Lerp(6, 1.1, u);
        shape.Build(pad, Lerp(5, endTop, u), w - pad, h - Lerp(5, 1.1, u), Lerp((h - 10) / 2, endRadius, u));
        return true;
    }

    /// <summary>The Claude bubble's track and running light (the same light as the island's, on its oval).</summary>
    private void DrawClaudePod(double t)
    {
        double w = ClaudePod.ActualWidth > 1 ? ClaudePod.ActualWidth : ClaudePod.Width;
        double islandW = _podKind == PodKind.Focus ? CompactWidth(CompactMode.Focus) : ClaudeIslandWidth;
        double u = _podMorphing ? Math.Clamp((w - 70) / (islandW - 70), 0, 1) : 0;
        if (ShapePod(_podShape, w, u, ref _podShapeW, ref _podShapeU))
        {
            PodTrack.Data = _podShape.Half(0, _podShape.Visible);
            PodTrack.StrokeThickness = Lerp(1.6, 2.2, u);
            PodTrack.Opacity = Lerp(0.35, 0.22, u);
            if (PodArc.Effect is System.Windows.Media.Effects.DropShadowEffect glow) glow.BlurRadius = Lerp(6, 10, u);
        }
        if (PodArc.Visibility != Visibility.Visible) return;
        if (_podKind == PodKind.Focus)
        {
            // the focus fills the outline from the top, like its light on the island (a timer's light empties)
            double share = PodIsTimer ? _clock.Soonest?.LeftShare ?? 0 : _focus.Progress;
            PodArc.Data = _podShape.Half(0, Math.Max(1.5, _podShape.Visible * Math.Clamp(share, 0, 1)));
            return;
        }
        double loop = 2 * _podShape.Visible;
        double lap = 2.6 * Math.Max(0.3, S.SpeedFactor);
        PodArc.Data = _podShape.Loop(t % lap / lap * loop, loop * 0.2);
    }

    /// <summary>The download bubble's track and progress (filling from the top down both sides, like the island).</summary>
    private void DrawDownloadPod(double prog)
    {
        double w = DownloadPod.ActualWidth > 1 ? DownloadPod.ActualWidth : DownloadPod.Width;
        double u = _dlPodMorphing ? Math.Clamp((w - 70) / (TransferWidth - 70), 0, 1) : 0;
        if (ShapePod(_dlPodShape, w, u, ref _dlShapeW, ref _dlShapeU))
        {
            DlPodTrack.Data = _dlPodShape.Half(0, _dlPodShape.Visible);
            DlPodTrack.StrokeThickness = Lerp(1.6, 2.2, u);
            DlPodTrack.Opacity = Lerp(0.3, 0.22, u);
            if (DlPodArc.Effect is System.Windows.Media.Effects.DropShadowEffect glow) glow.BlurRadius = Lerp(6, 10, u);
        }
        // the island's download icon fades in as the bubble grows into the island (and out on the way back)
        double iconOpacity = Math.Clamp((u - 0.55) / 0.45, 0, 1);
        if (Math.Abs(DlPodIcon.Opacity - iconOpacity) > 0.005) DlPodIcon.Opacity = iconOpacity;
        if (iconOpacity > 0 && DlPodIcon.Text != TransferIcon.Text) DlPodIcon.Text = TransferIcon.Text;
        // the bubble has the eyes, unless Claude is on screen (its island or its bubble): then one pair is
        // enough and the bubble shows the % instead. When Claude is gone the eyes come back, smoothly.
        bool claudeOnScreen = ClaudeOnScreen;
        double k = Ease(Math.Clamp(0.12 / Math.Max(0.3, S.SpeedFactor), 0.04, 0.5));
        _dlPodEyesU += ((claudeOnScreen ? 0 : 1) - _dlPodEyesU) * k;
        if (Math.Abs(_dlPodEyesU - (claudeOnScreen ? 0 : 1)) < 0.01) _dlPodEyesU = claudeOnScreen ? 0 : 1;
        // while it turns into the island (or back) the island's eyes take over
        double eyes = _dlPodMorphing ? Math.Max(_dlPodEyesU, Math.Clamp((u - 0.3) / 0.5, 0, 1)) : _dlPodEyesU;
        double info = 1 - eyes;
        // the eyes travelling between the bubbles (behind the screen) decide for themselves
        if (!_dlPodMorphing && TripDownloadLook() is { } trip) (eyes, info) = trip;
        if (Math.Abs(DlPodEyes.Opacity - eyes) > 0.005) DlPodEyes.Opacity = eyes;
        if (Math.Abs(DlPodInfo.Opacity - info) > 0.005) DlPodInfo.Opacity = info;

        double v = _dlPodShape.Visible;
        if (prog >= 0) DlPodArc.Data = _dlPodShape.Half(0, Math.Max(1.5, v * prog));
        else
        {
            // unknown size: the island's gliding light
            double seg = v * 0.3;
            double cycle = 2.2 * Math.Max(0.3, S.SpeedFactor);
            double phase = AnimNow % cycle / cycle;
            double from = (0.5 - 0.5 * Math.Cos(phase * Math.PI * 2)) * (v - seg);
            DlPodArc.Data = _dlPodShape.Half(from, from + seg);
        }
    }

    private void MorphClaudePod()
    {
        if (_claudeRig != null && _podRig != null) _claudeRig.Pose = _podRig.Pose; // the island's eyes start where the bubble's are
        _podShown = false;
        _podMorphing = true;
        MorphPodIntoIsland(ClaudePod, PodX, _podSlide, ClaudeIslandWidth, ClaudePanel, () => _claudeLightOpacity = 1, () =>
        {
            _podMorphing = false;
            UpdatePodPosition();
        });
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>Where the eyes look and how open they are, for what Claude is doing, at time t (s).</summary>
    private EyePose ClaudePose(ClaudePhase phase, bool attention, double t)
    {
        var p = EyePose.Rest;
        double sf = Math.Max(0.3, S.SpeedFactor);
        t /= sf;
        if (attention)
        {
            // wide open, looking right at you
            p.L = p.R = 1.3;
            p.W = 1.15;
            p.Y = 0.5;
            return p;
        }
        switch (phase)
        {
            case ClaudePhase.Reading:
            {
                // scanning a line left → right, quick return, next line a little lower
                const double cycle = 1.25;
                double u = t % cycle;
                p.X = u < 0.98 ? -3.5 + 7 * (u / 0.98) : 3.5 - 7 * ((u - 0.98) / (cycle - 0.98));
                p.Y = -1.2 + Math.Floor(t / cycle) % 3 * 1.2;
                p.L = p.R = 0.8;
                break;
            }
            case ClaudePhase.Editing:
            {
                // focused, looking down at what it's writing, small typing jitters
                p.Y = 2;
                p.L = p.R = 0.55;
                p.W = 1.12;
                p.X = 1.3 * Math.Sin(t * Math.PI * 2 * 1.5) * (Math.Sin(t * 0.9) > -0.3 ? 1 : 0.2);
                break;
            }
            case ClaudePhase.Testing:
            case ClaudePhase.Running:
            {
                // curious: glances to one side, then the other; the eye on that side opens a bit more
                p.X = Keys(t % 3.6, (0, 0), (0.4, 4), (1.6, 4), (2.0, -4), (3.2, -4), (3.6, 0));
                p.Y = -0.5;
                p.R = 1 + 0.18 * Math.Clamp(p.X / 4, 0, 1);
                p.L = 1 + 0.18 * Math.Clamp(-p.X / 4, 0, 1);
                break;
            }
            case ClaudePhase.Thinking:
            {
                // looking up to one side, then the other
                p.X = Keys(t % 4.4, (0, 2.5), (1.8, 2.5), (2.2, -2.5), (4.0, -2.5), (4.4, 2.5));
                p.Y = -3;
                // pondering: the eye away from where it looks squints a little
                p.L = 0.95 - 0.3 * Math.Clamp(p.X / 2.5, 0, 1);
                p.R = 0.95 - 0.3 * Math.Clamp(-p.X / 2.5, 0, 1);
                break;
            }
        }

        // natural blinks (not while reading a line mid-way)
        double bc = 3.9;
        double bu = (t + _blinkSeed) % bc;
        if (bu < 0.16)
        {
            double f = 1 - 0.88 * Math.Sin(Math.PI * bu / 0.16);
            p.L *= f;
            p.R *= f;
        }
        return p;
    }

    /// <summary>Smooth piecewise interpolation through (time, value) keys.</summary>
    private static double Keys(double t, params (double t, double v)[] keys)
    {
        for (int i = 1; i < keys.Length; i++)
        {
            if (t > keys[i].t) continue;
            var (t0, v0) = keys[i - 1];
            var (t1, v1) = keys[i];
            double u = t1 - t0 < 1e-6 ? 1 : (t - t0) / (t1 - t0);
            u = u * u * (3 - 2 * u);
            return v0 + (v1 - v0) * u;
        }
        return keys[^1].v;
    }

    /// <summary>Makes <paramref name="to"/> look exactly like <paramref name="from"/> (during a bubble → island hand-over).</summary>
    private static void CopyPose(EyeRig? from, EyeRig? to)
    {
        if (from == null || to == null) return;
        to.Pose = from.Pose;
        ApplyPose(to, from.Pose, 1);
    }

    private static void ApplyPose(EyeRig rig, EyePose target, double k)
    {
        ref var p = ref rig.Pose;
        p.X += (target.X - p.X) * k;
        p.Y += (target.Y - p.Y) * k;
        p.L += (target.L - p.L) * k;
        p.R += (target.R - p.R) * k;
        p.W += (target.W - p.W) * k;
        rig.Look.X = p.X;
        rig.Look.Y = p.Y;
        rig.Left.ScaleY = p.L;
        rig.Right.ScaleY = p.R;
        rig.Left.ScaleX = rig.Right.ScaleX = p.W;
    }

    // ------------------------------------------------------------------ every frame

    private void UpdateClaudeFace()
    {
        if (_claudeRig == null || _podRig == null) return;
        var c = _claude.Active;
        double t = AnimNow;
        bool attention = c?.NeedsAttention == true || _compactMode == CompactMode.ClaudeAttention;
        var phase = c?.Phase ?? ClaudePhase.Idle;
        var pose = ClaudePose(phase, attention, t);

        bool onIsland = _state == IslandState.Compact && IsClaudeMode(_compactMode);
        if (onIsland)
        {
            bool happy = _compactMode == CompactMode.ClaudeDone;
            if (happy != _claudeHappyShown)
            {
                _claudeHappyShown = happy;
                ClaudeEyeL.Visibility = ClaudeEyeR.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
                ClaudeHappyL.Visibility = ClaudeHappyR.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
            }
            var eyeColor = attention ? ClaudeColor : ClaudeEyeColor;
            if (ClaudeEyesGlow.Color != eyeColor) ClaudeEyesGlow.Color = eyeColor;
            if (((SolidColorBrush)ClaudeEyeL.Fill).Color != eyeColor)
            {
                var b = new SolidColorBrush(eyeColor);
                b.Freeze();
                ClaudeEyeL.Fill = ClaudeEyeR.Fill = b;
            }
            ApplyPose(_claudeRig, happy ? EyePose.Rest : pose, Ease(0.2));
        }
        if (_podShown && _podKind == PodKind.Claude) ApplyPose(_podRig, DateTime.Now < _podDoneUntil ? EyePose.Rest : pose, Ease(0.2));
        else if (_podMorphing) CopyPose(_claudeRig, _podRig); // the bubble's eyes are the island's eyes now
        if (_podShown || _podMorphing) DrawClaudePod(t);

        UpdateClaudeLight(onIsland, attention, t);
    }

    /// <summary>The orange light running around the island's edge (or pulsing, or lit all around when done).</summary>
    private void UpdateClaudeLight(bool onIsland, bool attention, double t)
    {
        bool flash = DateTime.UtcNow < _claudeFlashUntil;
        // a running download / copy draws its own (green) outline: don't stack two lights
        bool copyLight = ActiveTransfer?.Kind == TransferKind.Copy || (DateTime.UtcNow < _transferDoneUntil && _transferDoneKind == TransferKind.Copy);
        bool show = onIsland && !_introActive && !copyLight && !_islandMorphing;
        double k = Ease(Math.Clamp(0.14 / Math.Max(0.3, S.SpeedFactor), 0.05, 0.5));
        _claudeLightOpacity += ((show ? 1 : 0) - _claudeLightOpacity) * k;
        if (_claudeLightOpacity < 0.01 && !show)
        {
            if (ClaudeLight.Visibility == Visibility.Visible)
                ClaudeLight.Visibility = ClaudeTrack.Visibility = Visibility.Collapsed;
            return;
        }
        ClaudeLight.Visibility = ClaudeTrack.Visibility = Visibility.Visible;

        double W = Island.ActualWidth, H = Island.ActualHeight - _topHidden;
        if (W < 2 || H < 2) return;
        if (_claudeOutline.Build(W, H, _topHidden))
        {
            ClaudeLight.Width = ClaudeTrack.Width = W;
            ClaudeLight.Height = ClaudeTrack.Height = H + _topHidden;
            ClaudeTrack.Data = _claudeOutline.Segment(0, _claudeOutline.Length);
        }
        ClaudeLight.Margin = ClaudeTrack.Margin = Island.Margin;
        ClaudeLight.RenderTransform = ClaudeTrack.RenderTransform = Island.RenderTransform;

        double o = _claudeLightOpacity;
        double loop = 2 * _claudeOutline.Length;
        if (flash && _compactMode == CompactMode.ClaudeDone)
        {
            // done: lit all around, then fades away
            double left = (_claudeFlashUntil - DateTime.UtcNow).TotalSeconds;
            ClaudeLight.Data = _claudeOutline.Segment(0, _claudeOutline.Length);
            ClaudeLight.Opacity = o * Math.Clamp(left / 0.8, 0, 1);
            ClaudeTrack.Opacity = 0;
        }
        else if (_compactMode == CompactMode.ClaudeDone)
        {
            ClaudeLight.Opacity = 0;
            ClaudeTrack.Opacity = 0;
        }
        else if (attention)
        {
            // needs you: the whole outline pulses
            double pulse = 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 1.4));
            ClaudeLight.Data = _claudeOutline.Segment(0, _claudeOutline.Length);
            ClaudeLight.Opacity = o * pulse;
            ClaudeTrack.Opacity = 0;
        }
        else
        {
            // working: a light running around the island
            double lap = 2.6 * Math.Max(0.3, S.SpeedFactor);
            double pos = t % lap / lap * loop;
            ClaudeLight.Data = _claudeOutline.LoopSegment(pos, loop * 0.2);
            ClaudeLight.Opacity = o;
            ClaudeTrack.Opacity = o * 0.22;
        }
    }
}
