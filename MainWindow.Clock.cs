using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The Relógio tab: two pages, Timer and Alarmes, one above the other. The mouse wheel (away from the time)
/// or a click on the header's words switches between them; the header shows both words, the page you're on in
/// light blue and the other one dimmed with a hint ("Alarmes 07:30", "Timer 7:28").
/// Timer: a ring with Notchy's eyes, the time (scroll it to adjust), and on the right either shortcuts or the
/// other timers running. Alarmes: sleepy eyes + "next alarm in…", a card per alarm, "+" opens an editor.
/// </summary>
public partial class MainWindow
{
    private static readonly Color TimerColor = Color.FromRgb(0x7F, 0xD0, 0xFF);
    private static readonly Color TimerInk = Color.FromRgb(0x0B, 0x1A, 0x26);
    private static readonly int[] TimerPresets = { 1, 3, 5, 10, 15, 30 };

    private readonly ClockService _clock = ClockService.Load();
    private ClockTimer? _clockSel;          // the timer in the ring (null = a new one, not started yet)
    private bool _clockOnAlarms;
    private double _clockWheelAt;

    // header
    private StackPanel _clockHeader = null!;
    private TextBlock _hdrTimer = null!, _hdrTimerGlow = null!, _hdrAlarms = null!, _hdrAlarmsGlow = null!, _hdrTimerHint = null!, _hdrAlarmsHint = null!;

    // timer page
    private Grid _timerPage = null!, _alarmPage = null!;
    private Path _clockArc = null!;
    private Grid _clockRing = null!;
    private ScaleTransform _clockEyeL = null!, _clockEyeR = null!;
    private TranslateTransform _clockLook = null!;
    private TextBox _clockName = null!;
    private TextBlock _clockNameHint = null!, _clockTime = null!, _clockSub = null!;
    private Button _clockPlay = null!;
    private StackPanel _clockSide = null!;
    private string _clockSideKey = "";
    private readonly List<(ClockTimer t, TextBlock time, Path ring)> _clockOthers = new();

    // alarm page
    private ScaleTransform _alarmEyeL = null!, _alarmEyeR = null!;
    private TextBlock _alarmNext = null!;
    private Grid _alarmCards = null!;
    private string _alarmCardsKey = "";
    private Border _alarmEditor = null!;
    private Alarm? _editing;
    private bool _editingNew;
    private int _edHour, _edMinute, _edDays;
    private TextBlock _edHourText = null!, _edMinuteText = null!;
    private TextBox _edName = null!;
    private TextBlock _edNameHint = null!;
    private readonly Border[] _edDayChips = new Border[7];
    private Border _edDelete = null!;
    private static readonly Color TrashRed = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private Border _undoPill = null!;
    private TextBlock _undoText = null!, _undoLink = null!;
    private Alarm? _undoAlarm;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    private static SolidColorBrush Br(Color c) => new(c);
    private static SolidColorBrush White(byte a) => new(Color.FromArgb(a, 255, 255, 255));

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitClockTab()
    {
        _clockOnAlarms = S.ClockOnAlarms;
        _clock.TimerFinished += OnClockTimerFinished;
        _clock.AlarmFired += OnAlarmFired;

        BuildClockHeader();
        _timerPage = BuildTimerPage();
        _alarmPage = BuildAlarmPage();
        ClockTab.Children.Add(_timerPage);
        ClockTab.Children.Add(_alarmPage);
        ClockTab.Background = Brushes.Transparent; // the wheel works anywhere on the tab
        ClockTab.MouseWheel += ClockTab_MouseWheel;
        PlaceClockPages(animate: false);
    }

    private void BuildClockHeader()
    {
        var host = (Panel)HeaderA.Parent;
        var grid = (Grid)host.Parent;
        (TextBlock word, TextBlock glow, TextBlock hint, FrameworkElement el) Word(Action click)
        {
            var glow = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Br(TimerColor), IsHitTestVisible = false, Opacity = 0,
                Effect = new BlurEffect { Radius = 8 } };
            var word = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold };
            var g = new Grid();
            g.Children.Add(glow);
            g.Children.Add(word);
            var hint = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = White(0x66), Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            sp.Children.Add(g);
            sp.Children.Add(hint);
            sp.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            return (word, glow, hint, sp);
        }
        var a = Word(() => ShowClockPage(alarms: false));
        var b = Word(() => ShowClockPage(alarms: true));
        (_hdrTimer, _hdrTimerGlow, _hdrTimerHint) = (a.word, a.glow, a.hint);
        (_hdrAlarms, _hdrAlarmsGlow, _hdrAlarmsHint) = (b.word, b.glow, b.hint);
        _clockHeader = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed };
        _clockHeader.Children.Add(a.el);
        _clockHeader.Children.Add(new TextBlock { Text = "·", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = White(0x4C), Margin = new Thickness(7, 0, 7, 0) });
        _clockHeader.Children.Add(b.el);
        grid.Children.Add(_clockHeader);
    }

    /// <summary>The ring with eyes | name, time, buttons | shortcuts or the other timers.</summary>
    private Grid BuildTimerPage()
    {
        var page = new Grid { RenderTransform = new TranslateTransform() };
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        page.ColumnDefinitions.Add(new ColumnDefinition());
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });

        // ring + eyes (scroll here to adjust)
        var ring = new Grid { Width = 104, Height = 104, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.SizeNS };
        ring.Children.Add(new Ellipse { Width = 96, Height = 96, Stroke = White(0x18), StrokeThickness = 7 });
        _clockArc = new Path { Width = 104, Height = 104, StrokeThickness = 7, Stroke = Br(TimerColor), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        ring.Children.Add(_clockArc);
        ring.Children.Add(new Ellipse { Width = 68, Height = 68, Fill = White(0x0A) });
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _clockLook = new TranslateTransform();
        eyes.RenderTransform = _clockLook;
        Rectangle Eye(out ScaleTransform s, Thickness m)
        {
            s = new ScaleTransform();
            return new Rectangle { Width = 9, Height = 15, RadiusX = 4.5, RadiusY = 4.5, Fill = Br(TimerColor), Margin = m, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s };
        }
        eyes.Children.Add(Eye(out _clockEyeL, new Thickness(0, 0, 6, 0)));
        eyes.Children.Add(Eye(out _clockEyeR, new Thickness(6, 0, 0, 0)));
        eyes.Effect = new DropShadowEffect { Color = TimerColor, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 };
        ring.Children.Add(eyes);
        Glow(_clockArc, TimerColor);
        ring.MouseWheel += TimerWheel;
        page.Children.Add(ring);
        _clockRing = ring;

        // name, time, buttons
        var mid = new StackPanel { Margin = new Thickness(4, 2, 8, 0) };
        Grid.SetColumn(mid, 1);
        var nameGrid = new Grid { Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        _clockName = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White,
            FontSize = 12, Padding = new Thickness(0, 3, 0, 3), FocusVisualStyle = null, MaxLength = 40, SelectionBrush = Br(TimerColor) };
        _clockNameHint = new TextBlock { FontSize = 12, Foreground = White(0x66), Margin = new Thickness(2, 3, 0, 0), IsHitTestVisible = false };
        nameGrid.Children.Add(_clockName);
        nameGrid.Children.Add(_clockNameHint);
        nameGrid.Children.Add(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, StrokeDashArray = new DoubleCollection { 3, 3 }, Stroke = White(0x33), StrokeThickness = 1 });
        MakeTypable(_clockName, () =>
        {
            if (_clockSel != null)
            {
                _clockSel.Name = _clockName.Text.Trim();
                _clock.Save();
            }
        });
        _clockName.TextChanged += (_, _) => _clockNameHint.Visibility = _clockName.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        mid.Children.Add(nameGrid);

        _clockTime = new TextBlock { FontSize = 38, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, -3),
            Background = Brushes.Transparent, Cursor = Cursors.SizeNS, HorizontalAlignment = HorizontalAlignment.Left };
        _clockTime.MouseWheel += TimerWheel;
        mid.Children.Add(_clockTime);
        _clockSub = new TextBlock { FontSize = 11, Foreground = White(0x77), Margin = new Thickness(1, 1, 0, 0) };
        mid.Children.Add(_clockSub);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var reset = new Button { Style = (Style)FindResource("IconButton"), Content = "", FontSize = 12, Width = 30, Height = 30, Padding = new Thickness(0), Background = White(0x14) };
        reset.Click += (_, e) =>
        {
            e.Handled = true;
            ClockReset();
        };
        _clockPlay = new Button { Style = (Style)FindResource("CircleButton"), Width = 40, Height = 40, FontSize = 14, Margin = new Thickness(8, 0, 8, 0),
            Background = Br(TimerColor), Foreground = Br(TimerInk) };
        _clockPlay.Click += (_, e) =>
        {
            e.Handled = true;
            ClockPlay();
        };
        var plus = Pill("+1", White(0x14), Brushes.White, new Thickness(9, 0, 9, 0));
        plus.Height = 30;
        plus.CornerRadius = new CornerRadius(15);
        plus.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            if (_clockSel != null) _clock.Add(_clockSel, 60);
            else SetTimerMinutes(S.TimerMinutes + 1);
            UpdateClockUi();
            UpdateState();
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(_clockPlay);
        buttons.Children.Add(plus);
        mid.Children.Add(buttons);
        page.Children.Add(mid);
        GlowUnder(_clockPlay, TimerColor, true, 14, 0.55);

        // shortcuts / other timers
        _clockSide = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetColumn(_clockSide, 2);
        page.Children.Add(_clockSide);
        return page;
    }

    /// <summary>Sleepy eyes + "next alarm in…", then a card per alarm (and "+"), or the editor.</summary>
    private Grid BuildAlarmPage()
    {
        var page = new Grid { RenderTransform = new TranslateTransform() };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition());

        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var face = new Border { Width = 30, Height = 18, CornerRadius = new CornerRadius(9), Background = Br(Color.FromArgb(0x1C, TimerColor.R, TimerColor.G, TimerColor.B)), VerticalAlignment = VerticalAlignment.Center };
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Rectangle Eye(out ScaleTransform s, Thickness m)
        {
            s = new ScaleTransform(1, 0.3);
            return new Rectangle { Width = 4.5, Height = 7.5, RadiusX = 2.25, RadiusY = 2.25, Fill = Br(TimerColor), Margin = m, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s };
        }
        eyes.Children.Add(Eye(out _alarmEyeL, new Thickness(0, 0, 2, 0)));
        eyes.Children.Add(Eye(out _alarmEyeR, new Thickness(2, 0, 0, 0)));
        face.Child = eyes;
        top.Children.Add(face);
        _alarmNext = new TextBlock { FontSize = 11.5, Foreground = Brushes.White, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_alarmNext);
        page.Children.Add(top);

        _alarmCards = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        Grid.SetRow(_alarmCards, 1);
        page.Children.Add(_alarmCards);
        BuildAlarmScroller();

        _alarmEditor = BuildAlarmEditor();
        Grid.SetRow(_alarmEditor, 1);
        page.Children.Add(_alarmEditor);

        _undoPill = BuildUndoPill();
        Grid.SetRow(_undoPill, 1);
        page.Children.Add(_undoPill);
        return page;
    }

    private Border BuildAlarmEditor()
    {
        var ed = new Border
        {
            Margin = new Thickness(0, 10, 0, 6), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 8, 14, 8),
            Background = White(0x0E), BorderBrush = White(0x12), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(1, 0.5), RenderTransform = new ScaleTransform(1, 1)
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        // hh:mm, each part turns with the wheel
        var time = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TextBlock Reel(Action<int> turn)
        {
            var t = new TextBlock { FontSize = 44, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Width = 58, TextAlignment = TextAlignment.Center,
                Background = Brushes.Transparent, Cursor = Cursors.SizeNS, RenderTransform = new TranslateTransform() };
            t.MouseWheel += (_, e) =>
            {
                e.Handled = true;
                turn(e.Delta > 0 ? 1 : -1);
                SoundService.Play(Sfx.Click);
                RollIn(t, e.Delta > 0 ? 1 : -1);
                UpdateEditorTexts();
            };
            return t;
        }
        _edHourText = Reel(d => _edHour = (_edHour + d + 24) % 24);
        _edMinuteText = Reel(d => _edMinute = (_edMinute + d + 60) % 60);
        time.Children.Add(_edHourText);
        time.Children.Add(new TextBlock { Text = ":", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = White(0x80), Margin = new Thickness(0, 0, 0, 6) });
        time.Children.Add(_edMinuteText);
        row.Children.Add(time);

        var right = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(right, 1);
        var nameGrid = new Grid { Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
        _edName = new TextBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White,
            FontSize = 12, Padding = new Thickness(0, 2, 0, 2), FocusVisualStyle = null, MaxLength = 30, SelectionBrush = Br(TimerColor) };
        _edNameHint = new TextBlock { FontSize = 12, Foreground = White(0x66), Margin = new Thickness(2, 2, 0, 0), IsHitTestVisible = false };
        nameGrid.Children.Add(_edName);
        nameGrid.Children.Add(_edNameHint);
        nameGrid.Children.Add(new Rectangle { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, StrokeDashArray = new DoubleCollection { 3, 3 }, Stroke = White(0x33), StrokeThickness = 1 });
        MakeTypable(_edName, null);
        _edName.TextChanged += (_, _) => _edNameHint.Visibility = _edName.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        right.Children.Add(nameGrid);

        var days = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        for (int i = 0; i < 7; i++)
        {
            int bit = 1 << i;
            var chip = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 3, 0), Cursor = Cursors.Hand,
                Child = new TextBlock { FontSize = 9.5, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                _edDays ^= bit;
                UpdateEditorTexts();
            };
            _edDayChips[i] = chip;
            days.Children.Add(chip);
        }
        right.Children.Add(days);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        var cancel = Pill(Loc.T("clock.cancel"), White(0x14), White(0xCC), new Thickness(10, 3, 10, 3));
        cancel.Tag = "clock.cancel";
        cancel.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            CloseAlarmEditor();
        };
        var save = Pill(Loc.T("clock.save"), Br(TimerColor), Br(TimerInk), new Thickness(12, 3, 12, 3));
        save.Tag = "clock.save";
        save.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            SaveAlarmEditor();
        };
        _edDelete = Pill(Loc.T("clock.delete"), Brushes.Transparent, Br(Color.FromRgb(0xFF, 0x8A, 0x80)), new Thickness(8, 3, 8, 3));
        _edDelete.Tag = "clock.delete";
        _edDelete.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            if (_editing != null && !_editingNew) _clock.RemoveAlarm(_editing);
            CloseAlarmEditor();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        buttons.Children.Add(_edDelete);
        right.Children.Add(buttons);
        GlowUnder(save, TimerColor, true, 10, 0.5);
        row.Children.Add(right);
        ed.Child = row;
        return ed;
    }

    private static Border Pill(string text, Brush background, Brush foreground, Thickness padding)
    {
        var b = new Border
        {
            Child = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center },
            Background = background, CornerRadius = new CornerRadius(10), Padding = padding, Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.8;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        return b;
    }

    /// <summary>Typing needs the window to take focus for a moment (like the chat); Enter/Esc gives it back.</summary>
    private void MakeTypable(TextBox box, Action? committed)
    {
        box.PreviewMouseLeftButtonDown += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
            Activate();
            box.Focus();
            Keyboard.Focus(box);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            Keyboard.ClearFocus();
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
        };
        box.LostKeyboardFocus += (_, _) => committed?.Invoke();
    }

    /// <summary>A number rolls in like the Focus minutes: from below when it goes up, from above when it goes down.</summary>
    private void RollIn(FrameworkElement el, int dir)
    {
        if (S.SpeedFactor <= 0 || el.RenderTransform is not TranslateTransform tt) return;
        var d = TimeSpan.FromMilliseconds(Dur(260));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dir * 14, 0, d) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, d));
    }

    private void ApplyClockTexts()
    {
        _hdrTimer.Text = _hdrTimerGlow.Text = Loc.T("clock.timer");
        _hdrAlarms.Text = _hdrAlarmsGlow.Text = Loc.T("clock.alarms");
        _clockNameHint.Text = Loc.T("clock.name.hint");
        _edNameHint.Text = Loc.T("clock.alarm.name.hint");
        _undoText.Text = Loc.T("clock.deleted");
        _undoLink.Text = Loc.T("clock.undo");
        foreach (var pill in new[] { _edDelete }.Concat(((StackPanel)_edDelete.Parent).Children.OfType<Border>()))
            if (pill.Tag is string key && pill.Child is TextBlock tb) tb.Text = Loc.T(key);
        _clockSideKey = _alarmCardsKey = "";
        UpdateClockUi();
    }

    // =====================================================================================
    // pages
    // =====================================================================================

    private void ClockTab_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_editing != null) return; // the editor's numbers take the wheel; elsewhere it does nothing meanwhile
        if (AnimNow - _clockWheelAt < 0.45) return; // one notch = one page, even on free-spinning wheels
        bool down = e.Delta < 0;
        if (down == _clockOnAlarms) return;
        _clockWheelAt = AnimNow;
        ShowClockPage(alarms: down);
    }

    private void ShowClockPage(bool alarms)
    {
        if (alarms == _clockOnAlarms) return;
        _clockOnAlarms = alarms;
        S.ClockOnAlarms = alarms;
        S.Save();
        SoundService.Play(Sfx.Click);
        if (!alarms) CloseAlarmEditor();
        PlaceClockPages(animate: true);
        UpdateClockUi();
        if (alarms) ScrollToNextAlarm(instant: true);
        FaceReact(Mood.Curious, 0.4);
    }

    /// <summary>Timer above, Alarmes below: the wheel slides between them.</summary>
    private void PlaceClockPages(bool animate)
    {
        double h = 150;
        void Place(FrameworkElement page, double y, bool visible)
        {
            var tt = (TranslateTransform)page.RenderTransform;
            if (visible) page.Visibility = Visibility.Visible;
            if (!animate || S.SpeedFactor <= 0)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                page.BeginAnimation(OpacityProperty, null);
                tt.Y = y;
                page.Opacity = visible ? 1 : 0;
                page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                page.IsHitTestVisible = visible;
                return;
            }
            var d = TimeSpan.FromMilliseconds(Dur(420));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, d) { EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut } });
            var fade = new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromMilliseconds(Dur(visible ? 320 : 220)));
            if (!visible) fade.Completed += (_, _) => { if (page.Opacity < 0.05) page.Visibility = Visibility.Collapsed; };
            page.BeginAnimation(OpacityProperty, fade);
            page.IsHitTestVisible = visible;
        }
        Place(_timerPage, _clockOnAlarms ? -h : 0, !_clockOnAlarms);
        Place(_alarmPage, _clockOnAlarms ? 0 : h, _clockOnAlarms);
        UpdateClockHeader();
    }

    /// <summary>"Timer · Alarmes": the page you're on in light blue (with a soft glow), the other dimmed with a hint.</summary>
    private void UpdateClockHeader()
    {
        var d = TimeSpan.FromMilliseconds(Dur(300));
        void Word(TextBlock word, TextBlock glow, bool on)
        {
            var target = on ? TimerColor : Color.FromArgb(0x66, 255, 255, 255);
            if (word.Foreground is not SolidColorBrush b || b.IsFrozen) word.Foreground = b = new SolidColorBrush(((SolidColorBrush)word.Foreground ?? Brushes.White).Color);
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, d));
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 0.75 : 0, d));
        }
        Word(_hdrTimer, _hdrTimerGlow, !_clockOnAlarms);
        Word(_hdrAlarms, _hdrAlarmsGlow, _clockOnAlarms);
        UpdateClockHints();
    }

    private void UpdateClockHints()
    {
        var next = _clock.NextAlarm();
        var shown = _clockSel != null && _clock.Timers.Contains(_clockSel) ? _clockSel : _clock.Soonest;
        _hdrTimerHint.Text = _clockOnAlarms && shown != null ? FormatTimer(shown.Remaining) : "";
        _hdrAlarmsHint.Text = !_clockOnAlarms && next != null ? FormatClock(next.Value.at) : "";
    }

    // =====================================================================================
    // timer actions
    // =====================================================================================

    private void TimerWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        int dir = e.Delta > 0 ? 1 : -1;
        if (_clockSel != null)
        {
            // a timer under way: a minute more or less
            if (dir < 0 && _clockSel.Remaining.TotalSeconds <= 61) return;
            _clock.Add(_clockSel, dir * 60);
        }
        else SetTimerMinutes(S.TimerMinutes + dir);
        RollIn(_clockTime, dir);
        UpdateClockUi();
    }

    private void SetTimerMinutes(int minutes)
    {
        S.TimerMinutes = Math.Clamp(minutes, 1, 600);
        S.Save();
    }

    private void ClockPlay()
    {
        SoundService.Play(Sfx.Click);
        if (_clockSel == null)
        {
            _clockSel = _clock.Start(S.TimerMinutes * 60, _clockName.Text);
            FaceReact(Mood.Happy, 0.5);
        }
        else if (_clockSel.Running) _clock.Pause(_clockSel);
        else _clock.Resume(_clockSel);
        UpdateClockUi();
        UpdateState();
    }

    /// <summary>↺ cancels the timer in the ring (back to a new one with the same length).</summary>
    private void ClockReset()
    {
        SoundService.Play(Sfx.Click);
        if (_clockSel != null)
        {
            S.TimerMinutes = Math.Max(1, (int)Math.Round(_clockSel.TotalSeconds / 60));
            _clock.Remove(_clockSel);
            _clockSel = _clock.Soonest;
        }
        UpdateClockUi();
        UpdateState();
    }

    /// <summary>A shortcut: the length of a new timer, or (with one already in the ring) a second timer at once.</summary>
    private void UseTimerPreset(int minutes)
    {
        SoundService.Play(Sfx.Click);
        if (_clockSel == null) SetTimerMinutes(minutes);
        else
        {
            _clock.Start(minutes * 60, "");
            FaceReact(Mood.Happy, 0.4);
        }
        RollIn(_clockTime, 1);
        UpdateClockUi();
        UpdateState();
    }

    // =====================================================================================
    // state → screen
    // =====================================================================================

    /// <summary>A few times a second while the tab is open (and when something changes).</summary>
    private void UpdateClockUi()
    {
        if (_clockSel != null && !_clock.Timers.Contains(_clockSel)) _clockSel = _clock.Soonest;
        if (_tab != Tab.Clock) return;
        UpdateClockHints();

        // ---- timer page
        var sel = _clockSel;
        var remaining = sel?.Remaining ?? TimeSpan.FromMinutes(S.TimerMinutes);
        _clockTime.Text = FormatTimer(remaining);
        _clockSub.Text = sel == null ? Loc.T("clock.set") : sel.Running ? Loc.T("clock.left") : Loc.T("clock.paused");
        _clockPlay.Content = sel?.Running == true ? "" : "";
        _clockArc.Data = ArcGeometry(52, 44.5, sel?.LeftShare ?? 1);
        if (!_clockName.IsKeyboardFocused)
        {
            string name = sel?.Name ?? "";
            if (_clockName.Text != name && (sel != null || _clockName.Text.Length == 0)) _clockName.Text = name;
        }

        var others = _clock.Timers.Where(t => t != sel).ToList();
        string key = others.Count == 0 ? "presets:" + S.TimerMinutes + (sel == null) : string.Join(",", others.Select(o => o.Id));
        if (key != _clockSideKey)
        {
            _clockSideKey = key;
            BuildClockSide(others);
        }
        foreach (var (t, time, ring) in _clockOthers)
        {
            time.Text = FormatTimer(t.Remaining);
            ring.Data = ArcGeometry(8, 6.5, t.LeftShare);
            ring.Opacity = t.Running ? 1 : 0.45;
        }

        // ---- alarm page
        var next = _clock.NextAlarm();
        _alarmNext.Inlines.Clear();
        if (next is { } n)
        {
            var left = n.at - DateTime.Now;
            string inText = left.TotalHours >= 1 ? $"{(int)left.TotalHours} h {left.Minutes:00} min" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";
            string when = n.at.Date == DateTime.Today ? Loc.F("clock.next.at.today", FormatClock(n.at))
                : n.at.Date == DateTime.Today.AddDays(1) ? Loc.F("clock.next.at.tomorrow", FormatClock(n.at))
                : n.at.ToString("ddd ", Loc.Culture) + FormatClock(n.at);
            _alarmNext.Inlines.Add(new System.Windows.Documents.Run(Loc.T("clock.next") + " ") { Foreground = White(0x77) });
            _alarmNext.Inlines.Add(new System.Windows.Documents.Run(inText) { FontWeight = FontWeights.SemiBold });
            _alarmNext.Inlines.Add(new System.Windows.Documents.Run(" · " + when) { Foreground = White(0x77) });
        }
        else _alarmNext.Inlines.Add(new System.Windows.Documents.Run(Loc.T("clock.noalarm")) { Foreground = White(0x77) });

        string cardsKey = string.Join("|", _clock.Alarms.Select(a => $"{a.Id}{a.Hour}{a.Minute}{a.Days}{a.Enabled}{a.Name}")) + Loc.Language + S.Clock;
        if (cardsKey != _alarmCardsKey)
        {
            _alarmCardsKey = cardsKey;
            BuildAlarmCards();
        }
    }

    private void BuildClockSide(List<ClockTimer> others)
    {
        _clockSide.Children.Clear();
        _clockOthers.Clear();
        TextBlock Label(string text) => new() { Text = text, FontSize = 10, Foreground = White(0x77), Margin = new Thickness(0, 0, 0, 5) };
        TextBlock NewTimer()
        {
            var t = new TextBlock { Text = Loc.T("clock.newtimer"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Br(TimerColor), Margin = new Thickness(1, 6, 0, 0),
                Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            t.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                _clockSel = null;
                _clockName.Text = "";
                UpdateClockUi();
            };
            return t;
        }

        if (others.Count == 0)
        {
            _clockSide.Children.Add(Label(Loc.T("clock.shortcuts")));
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            foreach (int m in TimerPresets)
            {
                bool on = _clockSel == null && S.TimerMinutes == m;
                var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(m.ToString(CultureInfo.InvariantCulture)) { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
                text.Inlines.Add(new System.Windows.Documents.Run(" " + Loc.T("clock.min")) { FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = White(0x77) });
                var cell = new Border
                {
                    Child = text, CornerRadius = new CornerRadius(10), Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 0, 5, 5), Cursor = Cursors.Hand,
                    Background = on ? Br(Color.FromArgb(0x33, TimerColor.R, TimerColor.G, TimerColor.B)) : White(0x0E),
                    BorderBrush = on ? Br(Color.FromArgb(0x66, TimerColor.R, TimerColor.G, TimerColor.B)) : White(0x12), BorderThickness = new Thickness(1)
                };
                int minutes = m;
                cell.MouseEnter += (_, _) => cell.Opacity = 0.8;
                cell.MouseLeave += (_, _) => cell.Opacity = 1;
                cell.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    UseTimerPreset(minutes);
                };
                grid.Children.Add(cell);
            }
            _clockSide.Children.Add(grid);
            if (_clockSel != null) _clockSide.Children.Add(NewTimer());
            return;
        }

        _clockSide.Children.Add(Label(Loc.T("clock.alsorunning")));
        foreach (var t in others.Take(3))
        {
            var ring = new Path { Width = 16, Height = 16, Stroke = Br(TimerColor), StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var ringHost = new Grid { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            ringHost.Children.Add(new Ellipse { Width = 13, Height = 13, Stroke = White(0x26), StrokeThickness = 2.2 });
            ringHost.Children.Add(ring);
            var time = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            var name = new TextBlock { Text = string.IsNullOrEmpty(t.Name) ? Loc.T("clock.timer.default") : t.Name, FontSize = 9.5, Foreground = White(0x77), TextTrimming = TextTrimming.CharacterEllipsis };
            var texts = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
            texts.Children.Add(time);
            texts.Children.Add(name);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ringHost);
            row.Children.Add(texts);
            var card = new Border { Child = row, CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 5),
                Background = White(0x0E), BorderBrush = White(0x12), BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
            var timer = t;
            card.MouseEnter += (_, _) => card.Opacity = 0.8;
            card.MouseLeave += (_, _) => card.Opacity = 1;
            card.MouseLeftButtonUp += (_, e) =>
            {
                // bring it into the ring
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                _clockSel = timer;
                RollIn(_clockTime, 1);
                UpdateClockUi();
            };
            _clockOthers.Add((t, time, ring));
            _clockSide.Children.Add(card);
        }
        if (_clock.Timers.Count < ClockService.MaxTimers) _clockSide.Children.Add(NewTimer());
    }

    private void BuildAlarmCards()
    {
        // the cards scroll sideways (from the 4th on, each keeps the size of three side by side); "+" stays put on the right
        _alarmCards.Children.Clear();
        _alarmCards.ColumnDefinitions.Clear();
        _alarmRow.Children.Clear();
        int n = _clock.Alarms.Count;
        _alarmCards.ColumnDefinitions.Add(new ColumnDefinition { Width = n == 0 ? new GridLength(0) : new GridLength(1, GridUnitType.Star) });
        _alarmCards.ColumnDefinitions.Add(new ColumnDefinition { Width = n == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        _alarmCards.Children.Add(_alarmScrollHost);
        var nextAlarm = _clock.NextAlarm()?.alarm;
        string[] days = Loc.T("clock.days").Split(',');
        foreach (var alarm in _clock.Alarms)
        {
            var a = alarm;
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            var head = new DockPanel();
            var toggle = MiniSwitch(a.Enabled, on =>
            {
                _clock.SetEnabled(a, on);
                UpdateClockUi();
            });
            DockPanel.SetDock(toggle, Dock.Right);
            head.Children.Add(toggle);
            head.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(a.Name) ? Loc.T("clock.alarm.default") : a.Name, FontSize = 11, Foreground = White(0x88),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            g.Children.Add(head);
            var time = new TextBlock { Text = FormatClock(DateTime.Today.AddHours(a.Hour).AddMinutes(a.Minute)), FontSize = 25, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, Opacity = a.Enabled ? 1 : 0.4, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(time, 1);
            g.Children.Add(time);
            var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            if (a.Days == 0)
                dots.Children.Add(new TextBlock { Text = Loc.T("clock.once"), FontSize = 9.5, Foreground = a.Enabled ? Br(TimerColor) : White(0x47) });
            else
                for (int i = 0; i < 7 && i < days.Length; i++)
                    dots.Children.Add(new TextBlock
                    {
                        Text = days[i], FontSize = 8.5, FontWeight = FontWeights.Bold, Width = 11, TextAlignment = TextAlignment.Center,
                        Foreground = (a.Days & (1 << i)) != 0 && a.Enabled ? Br(TimerColor) : White(0x47)
                    });
            Grid.SetRow(dots, 2);
            g.Children.Add(dots);

            // a red bin in the corner, only while the mouse is on the card: one click deletes (with "Desfazer")
            var binScale = new ScaleTransform(0.6, 0.6);
            var bin = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Cursor = Cursors.Hand, Opacity = 0, IsHitTestVisible = false,
                Background = Br(Color.FromArgb(0x24, TrashRed.R, TrashRed.G, TrashRed.B)), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -2), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = binScale,
                ToolTip = Loc.T("clock.delete"),
                Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11.5, Foreground = Br(TrashRed),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            bin.MouseEnter += (_, _) => bin.Background = Br(Color.FromArgb(0x40, TrashRed.R, TrashRed.G, TrashRed.B));
            bin.MouseLeave += (_, _) => bin.Background = Br(Color.FromArgb(0x24, TrashRed.R, TrashRed.G, TrashRed.B));
            Border? cardRef = null;
            bin.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                DeleteAlarmWithUndo(a, cardRef);
            };
            Grid.SetRow(bin, 2);
            g.Children.Add(bin);
            var card = new Border
            {
                Child = g, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Background = White(0x0E), BorderThickness = new Thickness(1),
                // the next one to ring wears a thin blue edge
                BorderBrush = a == nextAlarm ? Br(Color.FromArgb(0x73, TimerColor.R, TimerColor.G, TimerColor.B)) : White(0x12)
            };
            cardRef = card;
            void ShowBin(bool on)
            {
                var d = TimeSpan.FromMilliseconds(Dur(on ? 220 : 160));
                var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
                bin.IsHitTestVisible = on;
                bin.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, d));
                binScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(on ? 1 : 0.6, d) { EasingFunction = on ? ease : null });
                binScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(on ? 1 : 0.6, d) { EasingFunction = on ? ease : null });
            }
            card.MouseEnter += (_, _) =>
            {
                card.Background = White(0x16);
                ShowBin(true);
            };
            card.MouseLeave += (_, _) =>
            {
                card.Background = White(0x0E);
                ShowBin(false);
            };
            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                OpenAlarmEditor(a);
            };
            _alarmRow.Children.Add(card);
        }
        if (_clock.Alarms.Count < ClockService.MaxAlarms)
        {
            var plus = new Border
            {
                MinWidth = 46, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand,
                BorderBrush = Br(Color.FromArgb(0x5A, TimerColor.R, TimerColor.G, TimerColor.B)), Background = Brushes.Transparent,
                Child = new TextBlock { Text = "+", FontSize = 22, Foreground = Br(TimerColor), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            plus.MouseEnter += (_, _) => plus.Background = Br(Color.FromArgb(0x14, TimerColor.R, TimerColor.G, TimerColor.B));
            plus.MouseLeave += (_, _) => plus.Background = Brushes.Transparent;
            plus.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SoundService.Play(Sfx.Click);
                OpenAlarmEditor(null);
            };
            Grid.SetColumn(plus, 1);
            _alarmCards.Children.Add(plus);
        }
        LayoutAlarmCards();
    }

    // =====================================================================================
    // alarm row: sideways scrolling
    // =====================================================================================

    private const double AlarmGap = 8, AlarmFade = 34;
    private Grid _alarmScrollHost = null!;
    private ScrollViewer _alarmScroll = null!;
    private StackPanel _alarmRow = null!;
    private Border _alarmBar = null!, _alarmThumb = null!;
    private LinearGradientBrush _alarmMask = null!;
    private double _alarmTarget, _alarmFadeL, _alarmFadeR, _alarmCardW = 118;
    private bool _alarmHover, _alarmDragPending, _alarmDragging;
    private double _alarmDragX, _alarmDragFrom;

    private double AlarmPitch => _alarmCardW + AlarmGap;
    private double AlarmMaxOffset => Math.Max(0, _alarmRow.Children.Count * AlarmPitch - AlarmViewport);
    private double AlarmViewport => _alarmScroll.ActualWidth > 1 ? _alarmScroll.ActualWidth : 382;
    private bool AlarmsScroll => AlarmMaxOffset > 1;

    /// <summary>
    /// The cards in a row that slides sideways: the wheel or a drag moves it, a thin bar shows (with the mouse on
    /// it) where you are, and the cards turn transparent at an edge that has more behind it.
    /// </summary>
    private void BuildAlarmScroller()
    {
        _alarmRow = new StackPanel { Orientation = Orientation.Horizontal };
        _alarmScroll = new ScrollViewer
        {
            Content = _alarmRow, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false, FocusVisualStyle = null, Focusable = false
        };
        _alarmMask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < 4; i++) _alarmMask.GradientStops.Add(new GradientStop(Colors.Black, i < 2 ? 0 : 1));
        _alarmScroll.OpacityMask = _alarmMask;
        _alarmScroll.SizeChanged += (_, _) => LayoutAlarmCards();

        _alarmThumb = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Br(TimerColor), HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new DropShadowEffect { Color = TimerColor, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.8 } };
        _alarmBar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = White(0x14), VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, AlarmGap, -5), Opacity = 0, IsHitTestVisible = false, Child = _alarmThumb };

        _alarmScrollHost = new Grid { Background = Brushes.Transparent };
        _alarmScrollHost.Children.Add(_alarmScroll);
        _alarmScrollHost.Children.Add(_alarmBar);
        _alarmScrollHost.MouseEnter += (_, _) => SetAlarmHover(true);
        _alarmScrollHost.MouseLeave += (_, _) => SetAlarmHover(false);

        // the wheel over the cards scrolls them (elsewhere on the tab it switches pages)
        _alarmScrollHost.PreviewMouseWheel += (_, e) =>
        {
            if (!AlarmsScroll) return;
            e.Handled = true;
            _alarmTarget = Math.Clamp(_alarmTarget + (e.Delta > 0 ? -AlarmPitch : AlarmPitch), 0, AlarmMaxOffset);
        };

        // drag the row like on a phone (a real drag doesn't count as a click on a card)
        _alarmScrollHost.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!AlarmsScroll) return;
            _alarmDragPending = true;
            _alarmDragX = e.GetPosition(_alarmScrollHost).X;
            _alarmDragFrom = _alarmScroll.HorizontalOffset;
        };
        _alarmScrollHost.PreviewMouseMove += (_, e) =>
        {
            if (!_alarmDragPending) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _alarmDragPending = _alarmDragging = false;
                return;
            }
            double dx = e.GetPosition(_alarmScrollHost).X - _alarmDragX;
            if (!_alarmDragging && Math.Abs(dx) > 5)
            {
                _alarmDragging = true;
                _alarmScrollHost.CaptureMouse();
            }
            if (!_alarmDragging) return;
            _alarmTarget = Math.Clamp(_alarmDragFrom - dx, 0, AlarmMaxOffset);
            _alarmScroll.ScrollToHorizontalOffset(_alarmTarget);
        };
        _alarmScrollHost.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _alarmDragPending = false;
            if (!_alarmDragging) return;
            _alarmDragging = false;
            _alarmScrollHost.ReleaseMouseCapture();
            e.Handled = true;
        };
    }

    private void SetAlarmHover(bool on)
    {
        _alarmHover = on;
        _alarmBar.BeginAnimation(OpacityProperty, new DoubleAnimation(on && AlarmsScroll ? 1 : 0, TimeSpan.FromMilliseconds(Dur(on ? 200 : 260))));
    }

    /// <summary>Up to three cards share the width; from the 4th on they keep that width and the row scrolls.</summary>
    private void LayoutAlarmCards()
    {
        int n = _alarmRow.Children.Count;
        if (n == 0) return;
        _alarmCardW = Math.Max(60, AlarmViewport / Math.Min(n, 3) - AlarmGap);
        foreach (FrameworkElement card in _alarmRow.Children) card.Width = _alarmCardW;
        _alarmTarget = Math.Clamp(_alarmTarget, 0, AlarmMaxOffset);
        if (!AlarmsScroll) _alarmBar.Opacity = 0;
    }

    /// <summary>Brings the next alarm to ring into view (it sits second from the left when it can).</summary>
    private void ScrollToNextAlarm(bool instant)
    {
        _alarmRow.UpdateLayout();
        LayoutAlarmCards();
        int i = _clock.NextAlarm() is { } n ? _clock.Alarms.IndexOf(n.alarm) : 0;
        _alarmTarget = Math.Clamp((i - 1) * AlarmPitch, 0, AlarmMaxOffset);
        if (instant) _alarmScroll.ScrollToHorizontalOffset(_alarmTarget);
    }

    /// <summary>Every frame on the alarms page: glide to the target, fade the edges that have more, move the bar.</summary>
    private void UpdateAlarmScroll()
    {
        double x = _alarmScroll.HorizontalOffset, max = AlarmMaxOffset;
        if (!_alarmDragging && Math.Abs(_alarmTarget - x) > 0.4)
        {
            x += (_alarmTarget - x) * Ease(0.22);
            _alarmScroll.ScrollToHorizontalOffset(x);
        }
        double k = Ease(0.2);
        _alarmFadeL += ((x > 2 ? 1 : 0) - _alarmFadeL) * k;
        _alarmFadeR += ((x < max - 2 ? 1 : 0) - _alarmFadeR) * k;
        double vw = AlarmViewport, f = AlarmFade / vw;
        var st = _alarmMask.GradientStops;
        st[0].Color = Color.FromArgb((byte)(255 * (1 - _alarmFadeL)), 0, 0, 0);
        st[1].Offset = f * _alarmFadeL;
        st[2].Offset = 1 - f * _alarmFadeR;
        st[3].Color = Color.FromArgb((byte)(255 * (1 - _alarmFadeR)), 0, 0, 0);

        if (max > 1)
        {
            double track = Math.Max(1, vw - AlarmGap), total = _alarmRow.Children.Count * AlarmPitch;
            double w = Math.Max(16, track * vw / total);
            _alarmThumb.Width = w;
            _alarmThumb.Margin = new Thickness(Math.Clamp(x / max, 0, 1) * (track - w), 0, 0, 0);
        }
    }

    /// <summary>The card shrinks away, the alarm is gone, and "Alarme excluído · Desfazer" shows for a few seconds.</summary>
    private void DeleteAlarmWithUndo(Alarm a, Border? card)
    {
        SoundService.Play(Sfx.Click);
        void Remove()
        {
            _clock.RemoveAlarm(a);
            _undoAlarm = a;
            _undoPill.Visibility = Visibility.Visible;
            _undoPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(200))));
            _undoTimer?.Stop();
            _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _undoTimer.Tick += (_, _) => HideUndo();
            _undoTimer.Start();
            UpdateClockUi();
        }
        if (card == null || S.SpeedFactor <= 0)
        {
            Remove();
            return;
        }
        card.IsHitTestVisible = false;
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = new ScaleTransform(1, 1);
        card.RenderTransform = st;
        var d = TimeSpan.FromMilliseconds(Dur(240));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, d));
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, d));
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => Remove();
        card.BeginAnimation(OpacityProperty, fade);
    }

    private void HideUndo()
    {
        _undoTimer?.Stop();
        _undoAlarm = null;
        if (_undoPill.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(200)));
        fade.Completed += (_, _) => { if (_undoAlarm == null) _undoPill.Visibility = Visibility.Collapsed; };
        _undoPill.BeginAnimation(OpacityProperty, fade);
    }

    private Border BuildUndoPill()
    {
        var text = new TextBlock { FontSize = 11, Foreground = White(0xDD), VerticalAlignment = VerticalAlignment.Center };
        var undo = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Br(TimerColor), Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(text);
        row.Children.Add(undo);
        var pill = new Border
        {
            Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 4), Background = Br(Color.FromRgb(0x26, 0x26, 0x2C)),
            BorderBrush = White(0x1F), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -2), Visibility = Visibility.Collapsed
        };
        undo.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            if (_undoAlarm is { } a)
            {
                _clock.SaveAlarm(a);
                FaceReact(Mood.Happy, 0.5);
            }
            HideUndo();
            UpdateClockUi();
        };
        _undoText = text;
        _undoLink = undo;
        return pill;
    }

    /// <summary>A small on/off switch like the preview's (blue when on).</summary>
    private static Border MiniSwitch(bool on, Action<bool> changed)
    {
        var knob = new Ellipse { Width = 10, Height = 10, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0),
            Fill = on ? Br(TimerInk) : White(0xBB), VerticalAlignment = VerticalAlignment.Center };
        var sw = new Border { Width = 24, Height = 14, CornerRadius = new CornerRadius(7), Background = on ? Br(TimerColor) : White(0x26), Child = knob, Cursor = Cursors.Hand };
        sw.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Toggle);
            changed(!on);
        };
        return sw;
    }

    // =====================================================================================
    // alarm editor
    // =====================================================================================

    private void OpenAlarmEditor(Alarm? alarm)
    {
        _editingNew = alarm == null;
        _editing = alarm ?? new Alarm { Hour = 7, Minute = 0, Days = 0b0011111 };
        if (_editingNew && _clock.NextAlarm() == null && DateTime.Now.Hour >= 7)
        {
            // a sensible start: the next round half hour
            var t = DateTime.Now.AddMinutes(30);
            _editing.Hour = t.Hour;
            _editing.Minute = t.Minute / 5 * 5;
            _editing.Days = 0;
        }
        (_edHour, _edMinute, _edDays) = (_editing.Hour, _editing.Minute, _editing.Days);
        _edName.Text = _editing.Name;
        _edDelete.Visibility = _editingNew ? Visibility.Collapsed : Visibility.Visible;
        UpdateEditorTexts();

        _alarmCards.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(160))));
        _alarmCards.IsHitTestVisible = false;
        _alarmEditor.Visibility = Visibility.Visible;
        var st = (ScaleTransform)_alarmEditor.RenderTransform;
        var d = TimeSpan.FromMilliseconds(Dur(380));
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, d) { EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, d) { EasingFunction = ease });
        _alarmEditor.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(220))));
    }

    private void CloseAlarmEditor()
    {
        if (_editing == null) return;
        _editing = null;
        Keyboard.ClearFocus();
        if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(160)));
        fade.Completed += (_, _) => { if (_editing == null) _alarmEditor.Visibility = Visibility.Collapsed; };
        _alarmEditor.BeginAnimation(OpacityProperty, fade);
        _alarmCards.IsHitTestVisible = true;
        _alarmCards.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(240))) { BeginTime = TimeSpan.FromMilliseconds(Dur(100)) });
        UpdateClockUi();
    }

    private void SaveAlarmEditor()
    {
        if (_editing == null) return;
        _editing.Hour = _edHour;
        _editing.Minute = _edMinute;
        _editing.Days = _edDays;
        _editing.Name = _edName.Text.Trim();
        _editing.Enabled = true;
        _clock.SaveAlarm(_editing);
        FaceReact(Mood.Happy, 0.5);
        CloseAlarmEditor();
    }

    private void UpdateEditorTexts()
    {
        _edHourText.Text = _edHour.ToString("00", CultureInfo.InvariantCulture);
        _edMinuteText.Text = _edMinute.ToString("00", CultureInfo.InvariantCulture);
        string[] days = Loc.T("clock.days").Split(',');
        for (int i = 0; i < 7; i++)
        {
            bool on = (_edDays & (1 << i)) != 0;
            var chip = _edDayChips[i];
            chip.Background = on ? Br(Color.FromArgb(0x30, TimerColor.R, TimerColor.G, TimerColor.B)) : Brushes.Transparent;
            var tb = (TextBlock)chip.Child;
            tb.Text = i < days.Length ? days[i] : "";
            tb.Foreground = on ? Br(TimerColor) : White(0x55);
        }
    }

    // =====================================================================================
    // every frame
    // =====================================================================================

    private void UpdateClockFrame()
    {
        if (_slowTick && _tick % 8 == 0)
        {
            _clock.Tick();
            UpdateAlarmHint();
        }
        if (_state != IslandState.Expanded || _tab != Tab.Clock) return;
        double t = AnimNow;
        bool blink = (t + 0.7) % 4.6 < 0.14;
        var sel = _clockSel;
        // eyes: wide open while you set it, watching the time (glancing side to side) while it runs, drowsy paused
        double open = blink ? 0.12 : sel == null ? 1 : sel.Running ? 0.8 : 0.45;
        double lookX = sel?.Running == true ? Math.Sin(t * 0.6) * 2 : 0, lookY = sel != null && !sel.Running ? 2 : 0;
        if (sel == null && EyesLookAtMouse(_clockRing) is { } look) (lookX, lookY) = look;
        _clockEyeL.ScaleY += (open - _clockEyeL.ScaleY) * Ease(0.3);
        _clockEyeR.ScaleY = _clockEyeL.ScaleY;
        _clockLook.X += (lookX - _clockLook.X) * Ease(0.15);
        _clockLook.Y += (lookY - _clockLook.Y) * Ease(0.15);
        if (sel?.Running == true && _tick % 2 == 0) _clockArc.Data = ArcGeometry(52, 44.5, sel.LeftShare);

        if (_clockOnAlarms) UpdateAlarmScroll();

        // the alarm page's sleepy little face: nearly closed, a slow breath
        double sleepy = (t % 6.5) < 0.18 ? 0.08 : 0.28 + 0.06 * Math.Sin(t * 1.3);
        _alarmEyeL.ScaleY = _alarmEyeR.ScaleY = sleepy;
    }
}
