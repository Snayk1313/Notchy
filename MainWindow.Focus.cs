using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The Focus tab: a ring with Notchy's eyes (concentrating while you focus, napping on a break), the time
/// whose minutes roll like a reel (scroll the mouse wheel over it, or over the little dial under it), quick
/// lengths, what you're focusing on, and today's numbers. On the small island: focused eyes, the time and a
/// light that fills the island's edge. "Não perturbe" keeps Notchy quiet while you focus.
/// </summary>
public partial class MainWindow
{
    private static readonly Color BreakGreen = Color.FromRgb(0x5E, 0xD6, 0x7D);
    private const double TickGap = 7, NeedleX = 66;
    private static readonly int[] FocusPresets = { 10, 15, 30, 60 };

    private readonly List<(Border chip, TextBlock text, int minutes)> _focusChips = new();
    private Border? _breakChip;
    private TextBlock? _breakChipText;
    private readonly Border[] _weekBars = new Border[7];
    private TextBlock? _reelShown;
    private int _reelValue = -1;
    private double _dialShown = -1;
    private readonly IslandOutline _focusOutline = new();
    private double _focusLightOpacity, _focusDrawn = -1;
    private Color _focusLightColor;

    // asking before a preset throws away a session that's under way
    private StackPanel? _focusConfirm;
    private TextBlock? _focusConfirmText;
    private int _pendingMinutes;
    private DispatcherTimer? _focusConfirmTimer;
    private bool FocusConfirming => _focusConfirm?.Visibility == Visibility.Visible;

    private Color FocusModeColor => _focus.Mode == FocusMode.Focus ? FocusColor : BreakGreen;

    // ------------------------------------------------------------------ build

    private void InitFocusTab()
    {
        // dial ticks: one per minute, longer every 5
        for (int i = 0; i <= 180; i++)
        {
            bool big = i % 5 == 0;
            var tick = new Rectangle
            {
                Width = 1.5, Height = big ? 10 : 5, RadiusX = 0.75, RadiusY = 0.75,
                Fill = new SolidColorBrush(Color.FromArgb(big ? (byte)0x80 : (byte)0x40, 255, 255, 255))
            };
            Canvas.SetLeft(tick, i * TickGap);
            Canvas.SetTop(tick, 10 - tick.Height);
            FocusTicks.Children.Add(tick);
        }

        foreach (int m in FocusPresets)
        {
            var (chip, text) = MakeFocusChip(m.ToString(CultureInfo.InvariantCulture));
            int minutes = m;
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                // a session under way would be thrown away: ask first
                if (_focus.Started) AskFocusRestart(minutes);
                else UseFocusPreset(minutes);
            };
            _focusChips.Add((chip, text, m));
            FocusChips.Children.Add(chip);
        }
        (_breakChip, _breakChipText) = MakeFocusChip("");
        _breakChip.Margin = new Thickness(4, 0, 0, 0);
        _breakChip.ToolTip = Loc.T("focus.break.tip");
        _breakChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            int b = S.BreakMinutes;
            _focus.SetBreakMinutes(b < 10 ? 10 : b < 15 ? 15 : 5);
            UpdateFocusUi();
        };
        FocusChips.Children.Add(_breakChip);
        BuildFocusConfirm();

        // this week, Monday first
        for (int i = 0; i < 7; i++)
        {
            FocusWeek.ColumnDefinitions.Add(new ColumnDefinition());
            FocusWeekLabels.ColumnDefinitions.Add(new ColumnDefinition());
            var bar = new Border { CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1.5, 0, 1.5, 0), Height = 3 };
            Grid.SetColumn(bar, i);
            FocusWeek.Children.Add(bar);
            _weekBars[i] = bar;
            var label = new TextBlock { FontSize = 8.5, Foreground = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(label, i);
            FocusWeekLabels.Children.Add(label);
        }

        // turn the wheel over the time or the dial
        FocusTimeRow.MouseWheel += FocusWheel;
        FocusDial.MouseWheel += FocusWheel;

        // what you're focusing on (typing needs the window to take focus for a moment, like the chat)
        FocusTaskBox.Text = S.FocusTask ?? "";
        FocusTaskBox.SelectionBrush = new SolidColorBrush(FocusColor);
        FocusTaskBox.TextChanged += (_, _) =>
        {
            FocusTaskHint.Visibility = FocusTaskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            S.FocusTask = FocusTaskBox.Text;
        };
        FocusTaskBox.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
            Activate();
            FocusTaskBox.Focus();
            Keyboard.Focus(FocusTaskBox);
        };
        FocusTaskBox.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Keyboard.ClearFocus();
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
        };
        FocusTaskBox.LostKeyboardFocus += (_, _) => S.Save();
        FocusTaskHint.Visibility = FocusTaskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        GlowUnder(FocusPlayButton, FocusColor, true, 14, 0.55);
    }

    private (Border, TextBlock) MakeFocusChip(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)) };
        var chip = new Border
        {
            Child = t, CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), Cursor = Cursors.Hand
        };
        chip.MouseEnter += (_, _) => chip.Opacity = 0.8;
        chip.MouseLeave += (_, _) => chip.Opacity = 1;
        return (chip, t);
    }

    private void ApplyFocusTexts()
    {
        FocusTaskHint.Text = Loc.T("focus.task.hint");
        FocusTodayLabel.Text = Loc.T("focus.today.label");
        FocusQuietText.Text = Loc.T("focus.quiet");
        FocusQuietPill.ToolTip = Loc.T("focus.quiet.tip");
        if (_breakChip != null) _breakChip.ToolTip = Loc.T("focus.break.tip");
        string[] days = Loc.T("focus.week").Split(',');
        for (int i = 0; i < FocusWeekLabels.Children.Count && i < days.Length; i++)
            ((TextBlock)FocusWeekLabels.Children[i]).Text = days[i];
    }

    // ------------------------------------------------------------------ input

    private void FocusWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_focus.Started) return; // the length can change before starting (or after a reset)
        int now = _focus.Mode == FocusMode.Focus ? S.FocusMinutes : S.BreakMinutes;
        int next = now + (e.Delta > 0 ? 1 : -1);
        if (_focus.Mode == FocusMode.Focus) SetFocusMinutes(next);
        else
        {
            _focus.SetMinutes(next);
            UpdateFocusUi();
        }
    }

    /// <summary>A preset is a focus length: on a break, go back to focus first.</summary>
    private void UseFocusPreset(int minutes)
    {
        if (_focus.Mode == FocusMode.Break) _focus.Skip();
        SetFocusMinutes(minutes);
    }

    /// <summary>"Recomeçar com 15 min?  Não  Sim" in place of the chips, for a few seconds.</summary>
    private void BuildFocusConfirm()
    {
        _focusConfirmText = new TextBlock
        {
            FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xDD, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 6, 0)
        };
        var no = ConfirmButton(Loc.T("focus.confirm.no"), Color.FromArgb(0x14, 255, 255, 255), Color.FromArgb(0xCC, 255, 255, 255));
        var yes = ConfirmButton(Loc.T("focus.confirm.yes"), FocusColor, Color.FromRgb(0x1B, 0x12, 0x30));
        no.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            CloseFocusConfirm();
        };
        yes.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            bool wasRunning = _focus.Running;
            CloseFocusConfirm();
            _focus.Reset();
            UseFocusPreset(_pendingMinutes);
            if (wasRunning) _focus.Start(); // start over at once with the new length
            UpdateFocusUi();
            UpdateState();
            FaceReact(Mood.Happy, 0.4);
        };
        _focusConfirm = new StackPanel
        {
            Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = FocusChips.Margin,
            VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform()
        };
        _focusConfirm.Children.Add(_focusConfirmText);
        _focusConfirm.Children.Add(no);
        _focusConfirm.Children.Add(yes);
        Grid.SetRow(_focusConfirm, Grid.GetRow(FocusChips));
        ((Grid)FocusChips.Parent).Children.Add(_focusConfirm);
    }

    private Border ConfirmButton(string text, Color background, Color foreground)
    {
        var b = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(foreground) },
            Background = new SolidColorBrush(background), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.8;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        return b;
    }

    private void AskFocusRestart(int minutes)
    {
        if (_focusConfirm == null || _focusConfirmText == null) return;
        _pendingMinutes = minutes;
        _focusConfirmText.Text = Loc.F("focus.confirm", minutes);
        SwapRow(FocusChips, _focusConfirm);
        // it goes away by itself if left alone
        _focusConfirmTimer?.Stop();
        _focusConfirmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _focusConfirmTimer.Tick += (_, _) => CloseFocusConfirm();
        _focusConfirmTimer.Start();
    }

    private void CloseFocusConfirm()
    {
        _focusConfirmTimer?.Stop();
        if (_focusConfirm == null || !FocusConfirming) return;
        SwapRow(_focusConfirm, FocusChips);
    }

    /// <summary>One row slides out to the left while the other fades in from the right.</summary>
    private void SwapRow(FrameworkElement from, FrameworkElement to)
    {
        var d = TimeSpan.FromMilliseconds(Dur(170));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        from.RenderTransform = new TranslateTransform();
        var leave = new DoubleAnimation(1, 0, d);
        leave.Completed += (_, _) =>
        {
            if (from.Opacity < 0.05) from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(OpacityProperty, leave);
        ((TranslateTransform)from.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -10, d) { EasingFunction = ease });
        to.Visibility = Visibility.Visible;
        to.RenderTransform = new TranslateTransform();
        var after = TimeSpan.FromMilliseconds(Dur(170));
        to.Opacity = 0;
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { BeginTime = after });
        ((TranslateTransform)to.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, d) { EasingFunction = ease, BeginTime = after });
    }

    private void SetFocusMinutes(int minutes)
    {
        if (!_focus.SetMinutes(minutes))
        {
            // it's running or paused: start over with the new length
            _focus.Reset();
            _focus.SetMinutes(minutes);
        }
        UpdateFocusUi();
        UpdateState();
    }

    private void FocusQuiet_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        S.FocusQuiet = !S.FocusQuiet;
        S.Save();
        SoundService.Play(Sfx.Click);
        UpdateFocusUi();
    }

    // ------------------------------------------------------------------ state → screen (a few times a second)

    private void UpdateFocusUi()
    {
        var color = FocusModeColor;
        bool focusMode = _focus.Mode == FocusMode.Focus;
        var remaining = _focus.Remaining;
        int secs = (int)Math.Ceiling(Math.Max(0, remaining.TotalSeconds) - 0.0001);

        // home card
        string time = FormatTimer(remaining);
        HomeFocusBig.Text = time;
        string sub = _focus.Running
            ? (focusMode ? Loc.T("focus.focusing") : Loc.T("focus.onbreak"))
            : _focus.Started ? Loc.T("focus.paused") : Loc.T("focus.ready");
        HomeFocusSub.Text = sub;
        string glyph = _focus.Running ? "" : "";
        FocusPlayButton.Content = glyph;
        HomeFocusPlay.Content = glyph;

        // the rest is the Focus tab itself: only while it's the tab on screen (SelectTab refreshes it when it
        // comes up); the minutes then show without rolling, as they're not "changing" in front of you
        if (_tab != Tab.Focus)
        {
            _reelValue = -1;
            return;
        }
        ShowFocusMinutes(secs / 60);
        FocusSeconds.Text = $":{secs % 60:00}";
        FocusUnit.Text = focusMode ? Loc.T("focus.unit.focus") : Loc.T("focus.unit.break");

        // the colour of the moment: purple to focus, green for the break
        if (FocusPlayButton.Background is not SolidColorBrush pb || pb.Color != color)
        {
            FocusPlayButton.Background = new SolidColorBrush(color);
            FocusPlayButton.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x12, 0x30));
            FocusRingArc.Stroke = new SolidColorBrush(color);
            FocusNeedle.Fill = new SolidColorBrush(color);
            FocusEyeL.Fill = FocusEyeR.Fill = new SolidColorBrush(color);
            Glow(FocusRingArc, color);
            Glow(FocusNeedle, color);
            GlowUnder(FocusPlayButton, color, true, 14, 0.55);
        }
        FocusRingArc.Data = ArcGeometry(48, 40.75, _focus.Progress);

        foreach (var (chip, text, minutes) in _focusChips)
        {
            bool sel = focusMode && S.FocusMinutes == minutes;
            chip.Background = new SolidColorBrush(sel ? Color.FromArgb(0x33, FocusColor.R, FocusColor.G, FocusColor.B) : Color.FromArgb(0x10, 255, 255, 255));
            text.Foreground = new SolidColorBrush(sel ? Colors.White : Color.FromArgb(0xAA, 255, 255, 255));
        }
        if (_breakChip != null && _breakChipText != null)
        {
            _breakChipText.Text = "☕ " + S.BreakMinutes;
            _breakChip.Background = new SolidColorBrush(!focusMode ? Color.FromArgb(0x30, BreakGreen.R, BreakGreen.G, BreakGreen.B) : Color.FromArgb(0x0A, 255, 255, 255));
            _breakChipText.Foreground = new SolidColorBrush(!focusMode ? Colors.White : Color.FromArgb(0x99, 255, 255, 255));
        }

        // today, the streak and this week
        double today = _focus.FocusedTodaySeconds;
        int mins = (int)(today / 60);
        FocusTodayText.Text = mins >= 60 ? $"{mins / 60}h {mins % 60:00}min" : $"{mins} min";
        int streak = _focus.Stats.Streak(today);
        FocusStreakText.Text = streak switch
        {
            0 => Loc.T("focus.streak0"),
            1 => "🔥 " + Loc.T("focus.streak1"),
            _ => "🔥 " + Loc.F("focus.streak", streak)
        };
        FocusStreakText.Foreground = new SolidColorBrush(streak > 0 ? Color.FromRgb(0xFF, 0xD4, 0x9A) : Color.FromArgb(0x77, 255, 255, 255));
        var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        double max = 3600;
        for (int i = 0; i < 7; i++) max = Math.Max(max, monday.AddDays(i) == DateTime.Today ? today : _focus.Stats.On(monday.AddDays(i)));
        for (int i = 0; i < 7; i++)
        {
            var day = monday.AddDays(i);
            double v = day == DateTime.Today ? today : _focus.Stats.On(day);
            var bar = _weekBars[i];
            bar.Height = Math.Max(3, v / max * 26);
            bool isToday = day == DateTime.Today;
            bar.Background = new SolidColorBrush(isToday ? FocusColor
                : day > DateTime.Today ? Color.FromArgb(0x0C, 255, 255, 255) : Color.FromArgb(0x26, 255, 255, 255));
            if (isToday) Glow(bar, FocusColor); else NoGlow(bar);
        }

        // "não perturbe"
        // on: filled with the play button's colour (and its glow); off: a quiet grey outline
        bool quiet = S.FocusQuiet;
        FocusQuietPill.Background = new SolidColorBrush(quiet ? color : Color.FromArgb(0x0C, 255, 255, 255));
        FocusQuietPill.BorderBrush = new SolidColorBrush(quiet ? color : Color.FromArgb(0x26, 255, 255, 255));
        FocusQuietPill.BorderThickness = new Thickness(1);
        var ink = new SolidColorBrush(quiet ? Color.FromRgb(0x1B, 0x12, 0x30) : Color.FromArgb(0x88, 255, 255, 255));
        FocusQuietText.Foreground = ink;
        FocusQuietMoon.Foreground = ink;
        FocusQuietMoon.Opacity = quiet ? 1 : 0.6;
        GlowUnder(FocusQuietPill, color, quiet, 12, 0.55); // the glow sits under it, so the text stays crisp
    }

    /// <summary>The minutes roll like a reel: the old number slides away, the new one comes in from the other side.</summary>
    private void ShowFocusMinutes(int minutes)
    {
        if (minutes == _reelValue) return;
        int dir = _reelValue < 0 ? 0 : minutes > _reelValue ? 1 : -1;
        _reelValue = minutes;
        var text = new TextBlock
        {
            Text = minutes.ToString("00", CultureInfo.InvariantCulture), FontSize = 38, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, RenderTransform = new TranslateTransform()
        };
        // measured once it's in the island, with the island's font (measuring it alone used the default font)
        FocusReel.Children.Add(text);
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        FocusReel.Width = Math.Ceiling(text.DesiredSize.Width);
        Canvas.SetTop(text, (44 - text.DesiredSize.Height) / 2);
        Canvas.SetLeft(text, 0);

        var old = _reelShown;
        _reelShown = text;
        if (dir == 0 || S.SpeedFactor <= 0)
        {
            if (old != null) FocusReel.Children.Remove(old);
            return;
        }
        var d = TimeSpan.FromMilliseconds(Dur(300));
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        // up = more time: the new number comes from below, the old one leaves upwards (and the other way)
        ((TranslateTransform)text.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(dir * 40, 0, d) { EasingFunction = ease });
        text.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d));
        if (old != null)
        {
            old.RenderTransform = new TranslateTransform();
            var leave = new DoubleAnimation(0, -dir * 40, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            leave.Completed += (_, _) => FocusReel.Children.Remove(old);
            ((TranslateTransform)old.RenderTransform).BeginAnimation(TranslateTransform.YProperty, leave);
            old.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d));
        }
    }

    // ------------------------------------------------------------------ every frame

    private void UpdateFocusFrame()
    {
        // keep Notchy quiet while you focus
        SoundService.Hushed = S.FocusQuiet && _focus.Running && _focus.Mode == FocusMode.Focus;

        double t = AnimNow;
        bool focusMode = _focus.Mode == FocusMode.Focus;
        bool blink = (t + 1.3) % 4.4 < 0.14;

        if (_state == IslandState.Expanded && _tab == Tab.Focus)
        {
            // eyes: concentrating (half closed) while focusing, wide open when idle, napping on a break
            double target = blink ? 0.12 : !focusMode ? 0.16 : _focus.Running ? 0.55 : 1;
            double k = Ease(0.3);
            double lookX = focusMode && _focus.Running ? Math.Sin(t * 0.7) * 1.6 : 0;
            double lookY = focusMode ? 0 : 2.5;
            if (FocusConfirming)
            {
                // a little surprised, looking down at the question
                target = blink ? 0.12 : 1.12;
                lookX = 3;
                lookY = 2.5;
            }
            else if (!_focus.Started && FocusEyeLookAtMouse() is { } look)
            {
                target = blink ? 0.12 : 1;
                (lookX, lookY) = look;
            }
            FocusEyeLScale.ScaleY += (target - FocusEyeLScale.ScaleY) * k;
            FocusEyeRScale.ScaleY = FocusEyeLScale.ScaleY;
            FocusFaceLook.X += (lookX - FocusFaceLook.X) * Ease(0.15);
            FocusFaceLook.Y += (lookY - FocusFaceLook.Y) * Ease(0.15);
            double z = !focusMode ? 0.55 + 0.45 * Math.Sin(t * 2) : 0;
            FocusZz.Opacity += (z - FocusZz.Opacity) * Ease(0.2);
            double rise = t * 0.8 % 1;
            Canvas.SetTop(FocusZ1, 20 - 4 * rise);
            Canvas.SetTop(FocusZ2, 11 - 4 * rise);

            // the dial follows the time (smoothly while it runs, with a little glide when you turn it)
            double minutesNow = _focus.Started ? _focus.Remaining.TotalMinutes : (focusMode ? S.FocusMinutes : S.BreakMinutes);
            if (_dialShown < 0) _dialShown = minutesNow;
            _dialShown += (minutesNow - _dialShown) * Ease(0.35);
            FocusTicksX.X = NeedleX - _dialShown * TickGap;
            if (_focus.Running && _tick % 2 == 0) FocusRingArc.Data = ArcGeometry(48, 40.75, _focus.Progress);
        }

        // small island: focused eyes
        if (_state == IslandState.Compact && _compactMode == CompactMode.Focus)
        {
            double target = blink ? 0.15 : focusMode ? 0.6 : 0.35;
            CompactFocusEyeL.ScaleY += (target - CompactFocusEyeL.ScaleY) * Ease(0.3);
            CompactFocusEyeR.ScaleY = CompactFocusEyeL.ScaleY;
        }
        UpdateFocusLight();
    }

    /// <summary>Where the ring's eyes look to follow the mouse (null when it can't tell).</summary>
    private (double x, double y)? FocusEyeLookAtMouse() => EyesLookAtMouse((FrameworkElement)FocusEyeL.Parent);

    /// <summary>Where a pair of eyes centred on <paramref name="face"/> look to follow the mouse.</summary>
    private static (double x, double y)? EyesLookAtMouse(FrameworkElement face)
    {
        Point center;
        try
        {
            center = face.PointToScreen(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
        }
        catch
        {
            return null;
        }
        Native.GetCursorPos(out var p);
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return (Math.Clamp(dx / 260, -1, 1) * 5, Math.Clamp(dy / 260, -1, 1) * 4);
    }

    /// <summary>The light on the small island's edge: it fills from the top as the time goes by.</summary>
    private void UpdateFocusLight()
    {
        // shared with the Relógio tab: a running timer's light empties, a ringing one pulses all the way round
        bool timer = _compactMode is CompactMode.Timer or CompactMode.TimerRing;
        var buds = _state == IslandState.Compact ? BudsLight() : null; // Bluetooth earbuds: the battery level
        bool cal = _compactMode == CompactMode.CalNotice; // a calendar notice: all the way round, in the event's colour
        bool show = _state == IslandState.Compact && (_compactMode == CompactMode.Focus || timer || cal || buds != null) && !_introActive && !_islandMorphing;
        double k = Ease(Math.Clamp(0.14 / Math.Max(0.3, S.SpeedFactor), 0.05, 0.5));
        _focusLightOpacity += ((show ? 1 : 0) - _focusLightOpacity) * k;
        if (_focusLightOpacity < 0.01 && !show)
        {
            if (FocusLight.Visibility == Visibility.Visible) FocusLight.Visibility = FocusTrack.Visibility = Visibility.Collapsed;
            _focusDrawn = -1;
            return;
        }
        FocusLight.Visibility = FocusTrack.Visibility = Visibility.Visible;
        double W = Island.ActualWidth, H = Island.ActualHeight - _topHidden;
        if (W < 2 || H < 2) return;
        if (_focusOutline.Build(W, H, _topHidden))
        {
            FocusLight.Width = FocusTrack.Width = W;
            FocusLight.Height = FocusTrack.Height = H + _topHidden;
            FocusTrack.Data = _focusOutline.Segment(0, _focusOutline.Length);
            _focusDrawn = -1;
        }
        FocusLight.Margin = FocusTrack.Margin = Island.Margin;
        FocusLight.RenderTransform = FocusTrack.RenderTransform = Island.RenderTransform;

        var color = cal ? _calColor : buds?.color ?? (timer ? TimerColor : FocusModeColor);
        if (color != _focusLightColor)
        {
            _focusLightColor = color;
            FocusLight.Stroke = new SolidColorBrush(color);
            FocusTrack.Stroke = new SolidColorBrush(color);
            FocusLightGlow.Color = color;
            CompactFocusEyesFill(color);
        }
        bool ringing = _compactMode == CompactMode.TimerRing;
        bool budsLow = buds?.pulse == true; // low battery: the light breathes in red, with a red glow around
        double pulse = ringing ? 0.45 + 0.55 * Math.Abs(Math.Sin(AnimNow * 4))
            : budsLow ? 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(AnimNow * 4.2)) : 1;
        FocusLightGlow.BlurRadius = budsLow ? 10 + 12 * (pulse - 0.55) / 0.45 : 10;
        FocusLight.Opacity = _focusLightOpacity * pulse;
        FocusTrack.Opacity = _focusLightOpacity * (ringing || budsLow ? 0.15 + 0.45 * pulse : 0.22);
        double p = Math.Clamp(cal ? 1 : buds?.share ?? (ringing ? 1 : timer ? _clock.Soonest?.LeftShare ?? 0 : _focus.Progress), 0, 1);
        if (_focusDrawn < 0 || Math.Abs(p - _focusDrawn) > 0.0004)
        {
            _focusDrawn = p;
            FocusLight.Data = _focusOutline.Segment(0, Math.Max(1.5, _focusOutline.Length * p));
        }
    }

    private void CompactFocusEyesFill(Color color)
    {
        var b = new SolidColorBrush(color);
        foreach (var child in CompactFocusEyes.Children)
            if (child is Rectangle r) r.Fill = b;
    }

    /// <summary>An arc around (c, c) of radius r, from the top, clockwise, for a 0..1 share of the circle.</summary>
    private static Geometry ArcGeometry(double c, double r, double p)
    {
        p = Math.Clamp(p, 0, 1);
        if (p <= 0.001) return Geometry.Empty;
        var start = new Point(c, c - r);
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        if (p >= 0.999)
        {
            fig.Segments.Add(new ArcSegment(new Point(c, c + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new ArcSegment(start, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        }
        else
        {
            double a = p * 2 * Math.PI;
            fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
        }
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }
}
