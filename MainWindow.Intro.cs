using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>
/// Opening animation (~4 s): a drop drips from the top of the screen and spreads into the island, a light
/// runs around its edge, then the eyes wake up (open, blink, wave "hi" with a little hand, look around).
/// Driven frame by frame so every step is in sync; the state machine waits until it ends.
/// </summary>
public partial class MainWindow
{
    private bool _introActive;
    private Stopwatch? _introClock;
    private bool _introSound;
    private readonly IslandOutline _introOutline = new();

    // timeline (seconds, before the animation speed setting)
    private const double DripEnd = 0.42, SpreadEnd = 0.95;
    private const double LightStart = 0.85, LightRun = 0.8, LightFade = 0.6;
    private const double EyesIn = 0.95, EyesOpen = 1.45, Blink1 = 1.95;
    // the little hand: pops up, waves "hi" (5 swings) while the eyes smile, then hides
    private const double HandIn = 2.15, WaveStart = 2.25, WavePeriod = 0.36, WaveEnd = 3.15, HandOut = 0.2;
    private const double LookStart = 3.35, IntroEnd = 3.85;

    private System.Windows.Shapes.Path? _introHand;
    private RotateTransform? _handTurn;
    private ScaleTransform? _handPop;
    private bool _waveSound, _introHappy;

    /// <summary>Plays the opening animation (at startup, or "Preview" in the settings).</summary>
    public void PlayIntro()
    {
        if (_introActive) return;
        if (Island.Visibility != Visibility.Visible)
        {
            UpdateState();
            return;
        }
        _introActive = true;
        _state = IslandState.None; // the real state is applied again when the intro ends

        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        Island.Width = 0;
        Island.Height = _topHidden;
        // every panel the island can be showing (it may already have picked one before the intro started)
        foreach (var panel in new UIElement[] { IdlePanel, CompactPanel, ClaudePanel, TransferPanel, WhatsNewPanel, VolumePanel, ExpandedPanel, AccentGlow })
        {
            panel.BeginAnimation(OpacityProperty, null);
            panel.Opacity = 0;
            panel.IsHitTestVisible = false;
        }
        SetHappy(false);
        PauseBlinking(true);
        EyesLook.BeginAnimation(TranslateTransform.XProperty, null);
        EyesLook.BeginAnimation(TranslateTransform.YProperty, null);
        EyesLook.X = EyesLook.Y = 0;
        EyesSleep.ScaleY = 0.12;

        IntroLight.Margin = Island.Margin;
        IntroLight.RenderTransform = Island.RenderTransform; // same size scale as the island
        IntroLight.Visibility = Visibility.Collapsed;
        IntroLight.Opacity = 1;
        _introOutline.Invalidate();
        _introSound = false;
        _waveSound = false;
        _introHappy = false;
        _introEyeColor = null;
        BuildIntroHand();
        _introHand!.Visibility = Visibility.Collapsed;

        _introClock = Stopwatch.StartNew();
        CompositionTarget.Rendering -= IntroFrame;
        CompositionTarget.Rendering += IntroFrame;
    }

    private void IntroFrame(object? sender, EventArgs e)
    {
        if (_introClock == null) return;
        double t = _introClock.Elapsed.TotalSeconds / Math.Max(0.2, S.SpeedFactor);
        var final = TargetSize(IslandState.Idle);
        double W = final.Width, H = final.Height;

        // 1) the drop: drips down narrow, then spreads sideways with a little squash and bounce
        double w, h;
        const double dripW = 22, dripH = 36;
        if (t < DripEnd)
        {
            double p = t / DripEnd;
            w = 4 + (dripW - 4) * EaseOutCubic(p);
            h = dripH * EaseOutCubic(p);
        }
        else if (t < SpreadEnd)
        {
            double p = (t - DripEnd) / (SpreadEnd - DripEnd);
            w = dripW + (W - dripW) * EaseOutBack(p, 1.5);
            h = H + (dripH - H) * (1 - EaseOutCubic(p)) - 5 * Math.Sin(Math.PI * Math.Min(1, p * 1.5)) * (1 - p);
            if (!_introSound)
            {
                _introSound = true;
                SoundService.Play(Sfx.Hello);
            }
        }
        else
        {
            w = W;
            h = H;
        }
        Island.Width = Math.Max(0, w);
        Island.Height = Math.Max(0, h) + _topHidden;

        // 2) the light: two heads leave the top and meet at the bottom, then the outline glows and fades
        if (t >= LightStart)
        {
            if (_introOutline.Build(W, H, _topHidden))
            {
                IntroLight.Width = W;
                IntroLight.Height = H + _topHidden;
            }
            double len = _introOutline.Length;
            double p = Math.Min(1, (t - LightStart) / LightRun);
            double head = len * EaseInOutCubic(p);
            double tail = Math.Max(0, head - len * (0.25 + 0.75 * p));
            IntroLight.Data = _introOutline.Segment(tail, head);
            var color = IntroColor();
            if (IntroLight.Stroke is not SolidColorBrush sb || sb.Color != color) IntroLight.Stroke = new SolidColorBrush(color);
            IntroLightGlow.Color = color;
            double fadeT = (t - (LightStart + LightRun)) / LightFade;
            if (fadeT > 0)
            {
                IntroLight.Opacity = 1 - EaseInCubic(Math.Min(1, fadeT));
                IntroLightGlow.BlurRadius = 10 + 14 * Math.Min(1, fadeT);
            }
            else
            {
                IntroLight.Opacity = 1;
                IntroLightGlow.BlurRadius = 10;
            }
            IntroLight.Visibility = IntroLight.Opacity > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        }

        // eyes and hand share the light's colour (album colour while music plays) during the intro
        SetIntroEyeColor(IntroColor());

        // 3) the eyes wake up: appear closed, open slowly, blink, wave hi, look left and right
        IdlePanel.Opacity = Math.Clamp((t - EyesIn) / 0.25, 0, 1);
        double open = t < EyesOpen ? 0.12 : 0.12 + 0.88 * EaseInOutCubic(Math.Min(1, (t - EyesOpen) / 0.4));
        if (t >= Blink1 && t < Blink1 + 0.14) open = Math.Min(open, 0.1 + 0.9 * Math.Abs(Math.Cos(Math.PI * (t - Blink1) / 0.14)));
        EyesSleep.ScaleY = open;

        // the hand: pops up beside the eyes (they slide a little to the left to make room) and waves
        double pop = t < HandIn ? 0
            : t < WaveEnd ? EaseOutBack(Math.Min(1, (t - HandIn) / 0.22), 2.2)
            : 1 - EaseInCubic(Math.Min(1, (t - WaveEnd) / HandOut));
        pop = Math.Max(0, pop);
        _introHand!.Visibility = pop > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        _handPop!.ScaleX = _handPop.ScaleY = pop;
        _handTurn!.Angle = t >= WaveStart && t < WaveEnd ? 22 * Math.Sin(2 * Math.PI * (t - WaveStart) / WavePeriod) : 0;
        if (_introHand.Fill != EyeL.Fill) _introHand.Fill = EyeL.Fill;
        if (_introHand.Effect is System.Windows.Media.Effects.DropShadowEffect hg)
        {
            hg.Color = EyesGlow.Color;
            hg.Opacity = Math.Max(0.35, EyesGlow.Opacity);
        }
        if (t >= WaveStart && !_waveSound)
        {
            _waveSound = true;
            SoundService.Play(Sfx.Wave);
        }
        bool happy = t >= WaveStart + 0.05 && t < WaveEnd;
        if (happy != _introHappy)
        {
            _introHappy = happy;
            if (happy) _happyUntil = DateTime.Now.AddSeconds(5); // the frame loop turns ^^ off after _happyUntil
            SetHappy(happy);
        }

        double room = t < HandIn ? 0 : t < WaveEnd ? EaseOutCubic((t - HandIn) / 0.25) : 1 - EaseInOutCubic((t - WaveEnd) / 0.3);
        double look = t >= LookStart && t < IntroEnd ? -3.2 * Math.Sin(2 * Math.PI * (t - LookStart) / (IntroEnd - LookStart)) : 0;
        EyesLook.X = -6 * Math.Clamp(room, 0, 1) + look;

        if (t >= IntroEnd) FinishIntro();
    }

    /// <summary>An open little hand (palm, four fingers, thumb) in the eye colour, beside the eyes.</summary>
    private void BuildIntroHand()
    {
        if (_introHand != null) return;
        // one soft blob (traced from the user's drawing): thumb, three round fingers, a pinky and a round palm;
        // drawn in the drawing's own coordinates and scaled down by Stretch
        var g = Geometry.Parse(
            "M 415,668 C 395,620 315,580 315,515 C 315,470 350,455 380,457 C 425,460 465,495 500,525 " +
            "C 495,470 485,410 490,370 C 495,320 525,295 565,295 C 610,295 635,340 648,435 " +
            "C 665,390 690,310 745,310 C 790,310 815,350 805,410 L 792,480 " +
            "C 830,440 870,400 910,405 C 945,410 960,445 945,480 C 930,515 905,545 885,578 " +
            "C 935,570 975,590 970,630 C 965,675 920,700 875,730 " +
            "C 855,800 800,898 645,898 C 500,898 418,800 415,668 Z");
        g.Freeze();

        _handTurn = new RotateTransform();
        _handPop = new ScaleTransform(0, 0);
        _introHand = new System.Windows.Shapes.Path
        {
            Data = g,
            Width = 16,
            Height = 15,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(36, 0, 0, 0), // to the right of the eyes
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.95), // waves from the wrist
            RenderTransform = new TransformGroup { Children = { _handPop, _handTurn } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 9, Opacity = 0.9 },
            Visibility = Visibility.Collapsed
        };
        IdlePanel.Children.Add(_introHand);
    }

    private void FinishIntro()
    {
        CompositionTarget.Rendering -= IntroFrame;
        _introClock = null;
        IntroLight.Visibility = Visibility.Collapsed;
        IntroLight.Data = null;
        EyesSleep.ScaleY = 1;
        EyesLook.X = EyesLook.Y = 0;
        if (_introHand != null) _introHand.Visibility = Visibility.Collapsed;
        if (_introHappy)
        {
            _introHappy = false;
            SetHappy(false);
        }
        _introActive = false;
        _introEyeColor = null;
        PauseBlinking(false);
        // hand over to the state machine (it may grow into the music / Claude island from here)
        _state = IslandState.None;
        IdlePanel.Opacity = 1;
        UpdateState();
        if (_state == IslandState.Idle)
        {
            UpdateEyeColor(); // back to the normal eye colour rules
            return;
        }
        // the eyes are fading out into the music / Claude island: keep the intro colour until they're gone
        _eyeColorHoldUntil = DateTime.Now.AddMilliseconds(600);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await System.Threading.Tasks.Task.Delay(650);
            UpdateEyeColor();
        });
    }

    private DateTime _eyeColorHoldUntil;

    private Color? _introEyeColor;

    private void SetIntroEyeColor(Color color)
    {
        if (_introEyeColor == color) return;
        _introEyeColor = color;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        EyeL.Fill = EyeR.Fill = brush;
        HappyL.Stroke = HappyR.Stroke = brush;
        EyesGlow.Color = color;
        EyesGlow.Opacity = color == Colors.White ? 0.25 : 0.95;
    }

    /// <summary>Album colour while music plays, otherwise the eye colour.</summary>
    private Color IntroColor()
    {
        bool music = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        return music ? Bright(_musicAccent) : ParseColor(S.EyeColor);
    }

    private static double EaseOutCubic(double p) => 1 - Math.Pow(1 - Math.Clamp(p, 0, 1), 3);
    private static double EaseInCubic(double p) => Math.Pow(Math.Clamp(p, 0, 1), 3);
    private static double EaseInOutCubic(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;
    }
    private static double EaseOutBack(double p, double s)
    {
        p = Math.Clamp(p, 0, 1) - 1;
        return 1 + (s + 1) * p * p * p + s * p * p;
    }
}
