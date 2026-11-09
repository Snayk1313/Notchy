using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// A timer ran out or an alarm went off: the island grows and calls you. The eyes open wide and shake, the
/// light around the edge pulses, the name shows with "+1 min" (or "Soneca 5 min") and "Parar". The sound
/// repeats until you answer, for a minute at most. Clicking anywhere else on it stops it and opens the tab.
/// While a timer runs (and nothing else has the island) the small island shows its eyes, the time and a light
/// that empties as the time goes by (MainWindow.Focus.cs draws it, shared with the focus).
/// </summary>
public partial class MainWindow
{
    private sealed record ClockRing(ClockTimer? Timer, Alarm? Alarm, string Title, string Sub, DateTime Started);

    private const double RingWidth = 380, RingHeight = 64, RingMaxSeconds = 60, RingSoundEvery = 2.4;
    private ClockRing? _ring;
    private readonly Queue<ClockRing> _ringQueue = new();
    private double _ringSoundAt = -100, _ringShownAt;

    private Grid _ringPanel = null!;
    private ScaleTransform _ringEyeL = null!, _ringEyeR = null!;
    private TranslateTransform _ringEyesMove = null!;
    private TextBlock _ringTitle = null!, _ringSub = null!, _ringSecondaryText = null!, _ringStopText = null!;

    private bool PodIsTimer => !_focus.Running && _clock.AnyRunning;

    private void InitClockRing()
    {
        _ringPanel = new Grid { Height = RingHeight, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Stretch, Opacity = 0, IsHitTestVisible = false };
        _ringPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _ringPanel.ColumnDefinitions.Add(new ColumnDefinition());
        _ringPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var eyes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 14, 0) };
        _ringEyesMove = new TranslateTransform();
        eyes.RenderTransform = _ringEyesMove;
        Rectangle Eye(out ScaleTransform s, Thickness m)
        {
            s = new ScaleTransform();
            return new Rectangle { Width = 8, Height = 14, RadiusX = 4, RadiusY = 4, Fill = Br(TimerColor), Margin = m, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s };
        }
        eyes.Children.Add(Eye(out _ringEyeL, new Thickness(0, 0, 4, 0)));
        eyes.Children.Add(Eye(out _ringEyeR, new Thickness(4, 0, 0, 0)));
        eyes.Effect = new DropShadowEffect { Color = TimerColor, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.8 };
        _ringPanel.Children.Add(eyes);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texts, 1);
        _ringTitle = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        _ringSub = new TextBlock { FontSize = 11, Foreground = White(0x80), TextTrimming = TextTrimming.CharacterEllipsis };
        texts.Children.Add(_ringTitle);
        texts.Children.Add(_ringSub);
        _ringPanel.Children.Add(texts);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 16, 0) };
        Grid.SetColumn(buttons, 2);
        var secondary = Pill("", White(0x17), White(0xDD), new Thickness(11, 6, 11, 6));
        _ringSecondaryText = (TextBlock)secondary.Child;
        _ringSecondaryText.FontSize = 11;
        secondary.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RingSecondary();
        };
        var stop = Pill("", Br(TimerColor), Br(TimerInk), new Thickness(13, 6, 13, 6));
        stop.Margin = new Thickness(0);
        _ringStopText = (TextBlock)stop.Child;
        _ringStopText.FontSize = 11;
        stop.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            StopRing();
        };
        buttons.Children.Add(secondary);
        buttons.Children.Add(stop);
        _ringPanel.Children.Add(buttons);
        GlowUnder(stop, TimerColor, true, 12, 0.6);

        ((Panel)CompactPanel.Parent).Children.Add(_ringPanel);
    }

    // =====================================================================================
    // events
    // =====================================================================================

    private void OnClockTimerFinished(ClockTimer t)
    {
        NavDone(Tab.Clock);
        string name = string.IsNullOrEmpty(t.Name) ? Loc.T("clock.timer.default") : t.Name;
        int minutes = (int)Math.Round(t.TotalSeconds / 60);
        string length = minutes >= 1 ? $"{minutes} min" : $"{(int)t.TotalSeconds} s";
        StartRing(new ClockRing(t, null, name, Loc.F("clock.timeup", length), DateTime.Now));
        if (S.ClaudeWindowsNotifications) App.Notify(name, Loc.F("clock.done", name));
        UpdateClockUi();
    }

    private void OnAlarmFired(Alarm a)
    {
        NavDone(Tab.Clock);
        string when = FormatClock(DateTime.Today.AddHours(a.Hour).AddMinutes(a.Minute));
        string sub = string.IsNullOrEmpty(a.Name) ? Loc.T("clock.alarm.ring") : Loc.T("clock.alarm.ring") + " · " + a.Name;
        StartRing(new ClockRing(null, a, "⏰ " + when, sub, DateTime.Now));
        if (S.ClaudeWindowsNotifications) App.Notify(Loc.T("clock.alarm.ring"), string.IsNullOrEmpty(a.Name) ? when : $"{when} · {a.Name}");
        UpdateClockUi();
    }

    private void StartRing(ClockRing r)
    {
        if (_ring != null)
        {
            _ringQueue.Enqueue(r);
            return;
        }
        _ring = r;
        _ringSoundAt = -100;
        _ringShownAt = AnimNow;
        _ringTitle.Text = r.Title;
        _ringSub.Text = r.Sub;
        _ringSecondaryText.Text = r.Alarm != null ? Loc.T("clock.snooze") : Loc.T("clock.plus1");
        _ringStopText.Text = Loc.T("clock.stop");
        FaceReact(Mood.Wide, 2);
        UpdateState();
    }

    private void StopRing()
    {
        _ring = null;
        if (_ringQueue.Count > 0) StartRing(_ringQueue.Dequeue());
        UpdateClockUi();
        UpdateState();
    }

    /// <summary>"+1 min" starts the timer again with a minute; "Soneca" rings again in 5 minutes.</summary>
    private void RingSecondary()
    {
        if (_ring == null) return;
        SoundService.Play(Sfx.Click);
        if (_ring.Timer is { } t)
        {
            t.TotalSeconds = 60;
            _clock.Add(t, 60);
        }
        else if (_ring.Alarm is { } a) _clock.Snooze(a, 5);
        StopRing();
    }

    // =====================================================================================
    // every frame
    // =====================================================================================

    private void UpdateClockRingFrame()
    {
        if (_ring != null)
        {
            if ((DateTime.Now - _ring.Started).TotalSeconds > RingMaxSeconds)
            {
                StopRing(); // nobody answered: let it go quietly
                return;
            }
            double t = AnimNow;
            if (t - _ringSoundAt > RingSoundEvery)
            {
                _ringSoundAt = t;
                SoundService.Play(Sfx.Alarm);
            }
            if (_state == IslandState.Compact && _compactMode == CompactMode.TimerRing)
            {
                // wide open and shaking, with a little hop now and then
                double since = t - _ringShownAt;
                bool blink = (t % 3.1) < 0.12;
                double open = blink ? 0.15 : 1.15 + 0.1 * Math.Sin(t * 9);
                _ringEyeL.ScaleY += (open - _ringEyeL.ScaleY) * Ease(0.4);
                _ringEyeR.ScaleY = _ringEyeL.ScaleY;
                double shake = (since % 2.4) < 1.4 ? Math.Sin(t * 55) * 1.3 : 0;
                double hop = (since % 2.4) is double h && h > 1.5 && h < 1.9 ? -4 * Math.Sin(Math.PI * (h - 1.5) / 0.4) : 0;
                _ringEyesMove.X = shake;
                _ringEyesMove.Y = hop;
            }
        }

        // the small island while a timer runs: eyes watching the time
        if (_state == IslandState.Compact && _compactMode == CompactMode.Timer)
        {
            double t = AnimNow;
            bool blink = (t + 0.9) % 4.4 < 0.14;
            double target = blink ? 0.15 : 0.75;
            CompactFocusEyeL.ScaleY += (target - CompactFocusEyeL.ScaleY) * Ease(0.3);
            CompactFocusEyeR.ScaleY = CompactFocusEyeL.ScaleY;
        }
    }
}
