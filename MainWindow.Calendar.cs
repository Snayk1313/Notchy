using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The calendar. The date in the header is a pill that opens "‹ Calendário": the month on the left and, on the right, the
/// chosen day's events with what comes next below them. A notice lights the island's outline and shows the event next to
/// the eyes; the mouse over it offers "remind me again". On a birthday the eyes smile and paper bursts out of the island.
/// </summary>
public partial class MainWindow
{
    private const string CalendarAppId = "notchy.calendar";
    private const double CalDotSize = 7, CalDotGap = 6, CalNoticeOpenHeight = 72;
    private const double CalNoticeSeconds = 12, CalPartySeconds = 9;

    private readonly CalendarService _calendar = CalendarService.Load();
    private readonly List<CalFired> _calQueue = new();
    private readonly ConfettiLayer _confetti = new();
    private readonly EyesFace _calFace = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };

    private CalFired? _calShown;
    private Grid _calPanel = null!;
    private TextBlock _calTitle = null!, _calSub = null!;
    private FrameworkElement _calOptions = null!;
    private bool _calOpen;
    private double _calWidth = 300, _calFaceLast;
    private Color _calColor;
    private DateTime _calNextTick;

    private readonly SolidColorBrush _dateEdge = new(Colors.Transparent) { Opacity = 0 };
    private readonly DropShadowEffect _dateGlow = new() { ShadowDepth = 0, BlurRadius = 9, Opacity = 0.55 };
    private Ellipse _idleCalDot = null!, _musicCalDot = null!;
    private Color? _calSign;

    private DateTime _calDay = DateTime.Today, _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalEvent? _calEditing;

    private enum CalLevel { Day, Month, Year }
    private enum CalMove { Up, Down }
    private const int CalMinYear = 1900, CalMaxYear = 2199;
    private CalLevel _calLevel;
    private int _calYear = DateTime.Today.Year, _calBlock;
    private Decorator _calTop = null!, _calRight = null!;
    private Grid _calBody = null!, _calScroll = null!;
    private bool _calEscDown;

    private bool CalNoticeHolds => _calShown != null && _transient == CompactMode.CalNotice && DateTime.UtcNow < _transientUntil;
    private double CalNoticeHeight => _calOpen ? CalNoticeOpenHeight : 34;

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitCalendar()
    {
        BuildCalPanel();
        BuildDatePill();

        // the paper comes out from behind the island
        var root = (Panel)Island.Parent;
        root.Children.Insert(root.Children.IndexOf(Island), _confetti);

        _idleCalDot = MakeCalDot();
        _idleCalDot.HorizontalAlignment = HorizontalAlignment.Left;
        _idleCalDot.Margin = new Thickness(IdleHintEdge, 0, 0, 0);
        IdlePanel.Children.Add(_idleCalDot);
        _musicCalDot = MakeCalDot();
        _musicCalDot.Margin = new Thickness(0, 0, CalDotGap, 0);
        if (_musicBell?.Parent is Panel host) host.Children.Insert(0, _musicCalDot);

        _calendar.Fired += OnCalFired;
        UpdateCalSign();
    }

    private static Ellipse MakeCalDot()
    {
        var dot = new Ellipse { Width = CalDotSize, Height = CalDotSize, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.5, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        return dot;
    }

    private void BuildCalPanel()
    {
        _calPanel = new Grid { Height = CalNoticeOpenHeight, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0, IsHitTestVisible = false, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _calPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        _calPanel.RowDefinitions.Add(new RowDefinition());

        var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_calFace);
        _calTitle = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        top.Children.Add(_calTitle);
        _calSub = new TextBlock { FontSize = 11, Foreground = White(0x90), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        top.Children.Add(_calSub);
        _calPanel.Children.Add(top);

        var options = new UniformGrid { Rows = 1, Columns = 3, Margin = new Thickness(14, 0, 14, 10), Opacity = 0 };
        options.Children.Add(CalButton(Loc.T("cal.later.hour"), false, () => SnoozeCal(DateTime.Now.AddHours(1))));
        options.Children.Add(CalButton(Loc.T("cal.later.day"), false, () => SnoozeCal(DateTime.Now.AddDays(1))));
        options.Children.Add(CalButton(Loc.T("cal.ok"), true, EndCalNotice));
        Grid.SetRow(options, 1);
        _calOptions = options;
        _calPanel.Children.Add(options);

        // a click on the notice opens the calendar on that day
        _calPanel.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_calShown is not { } f) return;
            SoundService.Play(Sfx.Click);
            EndCalNotice();
            if (S.Open == OpenMode.Hover) _hoverActive = true;
            else _clickOpened = true;
            UpdateState();
            OpenCalendar(f.Day);
        };
        ((Panel)CompactPanel.Parent).Children.Add(_calPanel);
    }

    private Border CalButton(string text, bool main, Action click)
    {
        var button = new Border
        {
            CornerRadius = new CornerRadius(9), Height = 26, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, Background = main ? Brushes.White : White(0x18),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = main ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        button.MouseEnter += (_, _) => button.Opacity = 0.8;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return button;
    }

    private void BuildDatePill()
    {
        DatePill.BorderBrush = _dateEdge;
        DatePill.ToolTip = Loc.T("cal.open");
        DatePill.MouseEnter += (_, _) => LightDate(true);
        DatePill.MouseLeave += (_, _) => LightDate(false);
        DatePill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenCalendar(DateTime.Today);
        };
    }

    private void LightDate(bool on)
    {
        _dateEdge.Color = _dateGlow.Color = TabColor(_tab);
        DatePill.Background = White((byte)(on ? 0x20 : 0x0E));
        DatePill.Effect = on ? _dateGlow : null;
        _dateEdge.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(Dur(220))));
    }

    /// <summary>Only once: the first time the island opens after the update, the date's outline blinks three times.</summary>
    private void MaybePulseDate()
    {
        if (S.CalendarHintSeen) return;
        S.CalendarHintSeen = true;
        S.Save();
        _dateEdge.Color = TabColor(_tab);
        var one = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1), BeginTime = TimeSpan.FromMilliseconds(700), RepeatBehavior = new RepeatBehavior(3), FillBehavior = FillBehavior.Stop };
        one.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.5)));
        one.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        _dateEdge.BeginAnimation(Brush.OpacityProperty, one);
    }

    private void UpdateDateDot()
    {
        var first = _calendar.On(DateTime.Today).FirstOrDefault();
        DateDot.Visibility = first == null ? Visibility.Collapsed : Visibility.Visible;
        if (first != null) DateDot.Fill = Br(CalendarService.ColorOf(first.Type));
    }

    // =====================================================================================
    // notices
    // =====================================================================================

    private void OnCalFired(CalFired f)
    {
        var (title, sub) = CalNoticeTexts(f);
        _notices.AddLocal(new Notice(0, Loc.T("cal.title"), CalendarAppId, title, sub, DateTime.Now, null, false) { CalendarType = f.Event.Type });
        _noticeUnseen++;
        UpdateBell();
        _calQueue.Add(f);
    }

    /// <summary>The event's type in the history of notices: a lit square in its colour with a dark drawing on it.</summary>
    private static Border CalNoticeIcon(CalType type)
    {
        string shape = type switch
        {
            CalType.Birthday => "M4 20h16v-7a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2z M4 15.5c2.6 2 3.6 2 5.4 0s3.4-2 5.3 0 2.7 2 5.3 0 M12 11V7.5 M12 3.4c1 1 1 2.2 0 2.9-1-.7-1-1.9 0-2.9z",
            CalType.Work => "M5 8h14a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-8a2 2 0 0 1 2-2z M9 8V6a2 2 0 0 1 2-2h2a2 2 0 0 1 2 2v2 M3 13.5h18",
            CalType.Bills => "M6 3h12v18l-3-2-3 2-3-2-3 2z M9.5 8h5 M9.5 12.5h5",
            _ => "M16 8a4 4 0 1 1-8 0 4 4 0 0 1 8 0z M5 20.5c0-3.7 3.1-6 7-6s7 2.3 7 6"
        };
        var color = CalendarService.ColorOf(type);
        var drawing = new Viewbox
        {
            Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Canvas { Width = 24, Height = 24, Children = { new Path
            {
                Data = Geometry.Parse(shape), Stroke = new SolidColorBrush(Color.FromRgb(0x14, 0x0E, 0x1E)), StrokeThickness = 1.9,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            } } }
        };
        var square = new Border { CornerRadius = new CornerRadius(8), Background = Br(color), Effect = new DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45 } };
        var host = new Grid();
        host.Children.Add(square);
        host.Children.Add(drawing); // on top of the glow, so the drawing stays sharp
        return new Border { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center, Child = host };
    }

    private (string title, string sub) CalNoticeTexts(CalFired f)
    {
        if (f.Party)
        {
            var m = Regex.Match(f.Event.Name, @"^(?:anivers[aá]rio\s+d[aeo]s?\s+)(.+)$", RegexOptions.IgnoreCase);
            string who = m.Success ? m.Groups[1].Value : f.Event.Name;
            return f.Event.AgeOn(f.Day) is int age ? (Loc.F("cal.party", who, age), "") : (f.Event.Name, "");
        }
        return (f.Event.Name, CalWhen(f.Event, f.Day));
    }

    /// <summary>"em 15 min", "hoje às 14:30", "amanhã"…</summary>
    private static string CalWhen(CalEvent e, DateTime day)
    {
        var now = DateTime.Now;
        int days = (day.Date - now.Date).Days;
        if (e.HasTime)
        {
            var left = e.Start(day) - now;
            if (left.TotalMinutes < 1) return Loc.T("cal.when.now");
            if (left.TotalMinutes < 60) return Loc.F("cal.when.min", (int)Math.Ceiling(left.TotalMinutes));
            string clock = FormatClock(e.Start(day));
            return days switch { 0 => Loc.F("cal.when.today.at", clock), 1 => Loc.F("cal.when.tomorrow.at", clock), _ => Loc.F("cal.when.days", days) };
        }
        return days switch { <= 0 => Loc.T("cal.when.today"), 1 => Loc.T("cal.when.tomorrow"), _ => Loc.F("cal.when.days", days) };
    }

    private void ShowNextCalNotice()
    {
        var f = _calQueue[0];
        _calQueue.RemoveAt(0);
        _calShown = f;
        var (title, sub) = CalNoticeTexts(f);
        _calColor = CalendarService.ColorOf(f.Event.Type);
        _calFace.Color = _calColor;
        _calFace.BaseMood = f.Party ? Mood.Happy : Mood.Normal;
        if (f.Party) _calFace.React(Mood.Happy, CalPartySeconds);
        _calTitle.Text = title;
        _calTitle.MaxWidth = 250;
        _calSub.Text = sub;
        _calSub.Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        double text = Math.Min(250, MeasureText(_calTitle, title)) + (sub.Length > 0 ? 6 + MeasureText(_calSub, sub) : 0);
        _calWidth = Math.Clamp(Math.Ceiling(text + 33 + 44), 260, 400);
        _calPanel.Width = _calWidth;
        SetCalOpen(false);
        _calFaceLast = _faceClock.Elapsed.TotalSeconds;
        SoundService.Play(f.Party ? Sfx.Done : Sfx.Toggle);
        ShowTransient(CompactMode.CalNotice, "", "", f.Party ? CalPartySeconds : CalNoticeSeconds);
        if (f.Party) Dispatcher.BeginInvoke(BurstConfetti, DispatcherPriority.Loaded);
    }

    private void BurstConfetti()
    {
        var bottom = Island.TransformToAncestor(this).Transform(new Point(0, _topHidden + 34));
        _confetti.Burst(ActualWidth / 2, bottom.Y, Math.Max(0.5, S.SizeScale));
    }

    private void SetCalOpen(bool open)
    {
        if (open == _calOpen) return;
        _calOpen = open;
        _calOptions.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(Dur(open ? 200 : 90))));
        if (_state == IslandState.Compact && _compactMode == CompactMode.CalNotice) AnimateIslandSize(TargetSize(IslandState.Compact), bigMove: false);
    }

    private void SnoozeCal(DateTime until)
    {
        if (_calShown is { } f) _calendar.Snooze(f.Event, f.Day, until);
        EndCalNotice();
    }

    private void EndCalNotice()
    {
        _calShown = null;
        _calOpen = false;
        _calOptions.BeginAnimation(OpacityProperty, null);
        _calOptions.Opacity = 0;
        if (_transient == CompactMode.CalNotice) _transient = CompactMode.None;
        UpdateState();
    }

    /// <summary>Every frame: looks at the clock now and then, shows what's due, and keeps a notice while the mouse is on it.</summary>
    private void UpdateCalendarFrame()
    {
        if (_subOpen) UpdateCalendarKeys();
        if (DateTime.UtcNow >= _calNextTick)
        {
            _calNextTick = DateTime.UtcNow.AddSeconds(2);
            _calendar.Tick(DateTime.Now);
            UpdateCalSign();
        }

        if (_calShown is { } f)
        {
            if (_transient != CompactMode.CalNotice)
            {
                // something else took the island before its time was up: this one comes back afterwards
                if (DateTime.UtcNow < _transientUntil) _calQueue.Insert(0, f);
                _calShown = null;
                SetCalOpen(false);
                return;
            }
            if (_compactMode == CompactMode.CalNotice && _state == IslandState.Compact)
            {
                bool hover = !f.Party && Island.IsMouseOver;
#if DEBUG
                hover |= _calForceOpen;
#endif
                SetCalOpen(hover);
                if (hover) _transientUntil = DateTime.UtcNow.AddSeconds(1.5);
                double now = _faceClock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _calFaceLast, 0, 0.1);
                _calFaceLast = now;
                _calFace.Update(dt, 0);
                _calFace.Visibility = BubbleEyesAround ? Visibility.Collapsed : Visibility.Visible;
            }
            if (DateTime.UtcNow >= _transientUntil) EndCalNotice();
            return;
        }

        // one at a time, and never over something you're using, a ringing alarm, a game or "Não perturbe"
        if (_calQueue.Count > 0 && _state != IslandState.Expanded && _transient == CompactMode.None && _ring == null
            && !_introActive && !_hiddenForFullscreen && !SoundService.Hushed)
            ShowNextCalNotice();
    }

    private void UpdateCalSign()
    {
        var pending = _calendar.PendingToday(DateTime.Now);
        Color? color = pending == null ? null : CalendarService.ColorOf(pending.Type);
        if (color == _calSign) return;
        _calSign = color;
        foreach (var dot in new[] { _idleCalDot, _musicCalDot })
        {
            dot.Visibility = color == null ? Visibility.Collapsed : Visibility.Visible;
            if (color is Color c)
            {
                dot.Fill = Br(c);
                dot.Effect = new DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.8 };
            }
        }
        UpdateAlarmHint(force: true);
        if (_compactMode == CompactMode.Music) FitCompactText();
    }

    // =====================================================================================
    // the page
    // =====================================================================================

    private void OpenCalendar(DateTime day)
    {
        _calDay = day.Date;
        _calMonth = new DateTime(day.Year, day.Month, 1);
        _calLevel = CalLevel.Day;
        _subParent = null;
        OpenSubPage(Loc.T("cal.title"), CalendarPage);
    }

    private void RefreshCalendar()
    {
        RefreshSubPage(CalendarPage);
        UpdateDateDot();
        UpdateCalSign();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Loc.Culture) + s[1..];

    private static string WeekdayName(DateTime d) => d.ToString("dddd", Loc.Culture).Replace("-feira", "");

    private static string DayLine(DateTime d) => Loc.F("cal.dayline", Cap(WeekdayName(d)), d.Day, d.ToString("MMMM", Loc.Culture));

    private static string RepeatLine(CalEvent e) => e.Repeat switch
    {
        CalRepeat.Weekly => Loc.Language == "en"
            ? "Every " + WeekdayName(e.Date)
            : (e.Date.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Saturday ? "Todo " : "Toda ") + WeekdayName(e.Date),
        CalRepeat.Monthly => Loc.F("cal.line.month", e.Date.Day),
        CalRepeat.Yearly => Loc.T("cal.line.year"),
        _ => ""
    };

    private static string CalLine(CalEvent e, DateTime day)
    {
        if (e.Type == CalType.Holiday) return Loc.T("cal.holiday");
        var parts = new List<string>();
        if (e.HasTime) parts.Add(FormatClock(e.Start(day)));
        if (e.AgeOn(day) is int age) parts.Add(Loc.F("cal.age", age));
        string repeat = RepeatLine(e);
        if (repeat.Length > 0) parts.Add(repeat);
        return parts.Count > 0 ? string.Join(" · ", parts) : Loc.T("cal.allday");
    }

    private FrameworkElement CalendarPage()
    {
        var left = new StackPanel { Background = Brushes.Transparent };
        _calTop = new Decorator { Height = 26, Child = CalBar() };
        _calBody = new Grid { Height = CalBodyHeight, ClipToBounds = true };
        _calBody.Children.Add(CalPane());
        left.Children.Add(_calTop);
        left.Children.Add(_calBody);
        left.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            CalStep(e.Delta < 0 ? 1 : -1);
        };

        _calRight = new Decorator { Child = CalRightColumn() };
        var right = _calRight;

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
#if DEBUG
        var page = new StackPanel();
        page.Children.Add(grid);
        var tests = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 6) };
        tests.Children.Add(new TextBlock { Text = "Só na versão de testes:", FontSize = 10.5, Foreground = White(0x66), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var bday = new CalEvent { Name = "Aniversário da Ana", Date = DateTime.Today, Type = CalType.Birthday, BornYear = DateTime.Today.Year - 30 };
        var meet = new CalEvent { Name = "Reunião do projeto", Date = DateTime.Today, HasTime = true, Hour = DateTime.Now.AddMinutes(15).Hour, Minute = DateTime.Now.AddMinutes(15).Minute, Type = CalType.Work };
        var testAviso = ListPill("", "Testar aviso", false, () => OnCalFired(new CalFired(meet, DateTime.Today, false)));
        var testFesta = ListPill("", "Testar festinha", false, () => OnCalFired(new CalFired(bday, DateTime.Today, true)));
        testFesta.Margin = new Thickness(6, 0, 0, 0);
        tests.Children.Add(testAviso);
        tests.Children.Add(testFesta);
        page.Children.Add(tests);
        return page;
#else
        return grid;
#endif
    }

    private Border CalArrow(string glyph, int direction)
    {
        var arrow = new Border { Width = 24, Height = 22, CornerRadius = new CornerRadius(8), Background = White(0x10), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } };
        arrow.MouseEnter += (_, _) => arrow.Background = White(0x26);
        arrow.MouseLeave += (_, _) => arrow.Background = White(0x10);
        arrow.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            CalStep(direction);
        };
        return arrow;
    }

    // ---- quick navigation: day → month → year, in the same space, zooming out and back in

    private int CalRows => (int)Math.Ceiling(((int)_calMonth.DayOfWeek + DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month)) / 7.0);
    private double CalBodyHeight => 18 + CalRows * 30;
    private double CalListHeight => 26 + CalBodyHeight - 27 - 32;

    private bool CalAtHome
    {
        get
        {
            var today = DateTime.Today;
            return _calLevel switch
            {
                CalLevel.Day => _calMonth.Year == today.Year && _calMonth.Month == today.Month,
                CalLevel.Month => _calYear == today.Year,
                _ => today.Year >= _calBlock && today.Year < _calBlock + 12
            };
        }
    }

    private static int CalBlockOf(int year) => year / 12 * 12;

    private FrameworkElement CalRightColumn()
    {
        var list = new StackPanel();
        var onDay = _calendar.On(_calDay);
        if (onDay.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("cal.none"), FontSize = 11.5, Foreground = White(0x66), Margin = new Thickness(6, 6, 0, 6) });
        foreach (var e in onDay) list.Children.Add(CalRow(e, _calDay, false));
        var after = _calendar.After(_calDay, 12);
        if (after.Count > 0)
        {
            list.Children.Add(new TextBlock { Text = Loc.T("cal.next"), FontSize = 10, FontWeight = FontWeights.Bold, Foreground = White(0x66), Margin = new Thickness(6, 8, 0, 3) });
            foreach (var (e, day) in after) list.Children.Add(CalRow(e, day, true));
        }

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = DayLine(_calDay), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Height = 22, Margin = new Thickness(6, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        _calScroll = Scroller(list, height: CalListHeight);
        right.Children.Add(_calScroll);
        var add = ListPill(Glyph(0xE710), Loc.T("cal.new"), true, () => OpenCalEditor(null));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(6, 6, 0, 0);
        right.Children.Add(add);
        return right;
    }

    private FrameworkElement CalBar()
    {
        var bar = new DockPanel();
        var next = CalArrow("›", 1);
        DockPanel.SetDock(next, Dock.Right);
        bar.Children.Add(next);
        var prev = CalArrow("‹", -1);
        DockPanel.SetDock(prev, Dock.Right);
        prev.Margin = new Thickness(0, 0, 4, 0);
        bar.Children.Add(prev);
        if (!CalAtHome)
        {
            var home = CalTodayPill();
            DockPanel.SetDock(home, Dock.Right);
            bar.Children.Add(home);
        }
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        switch (_calLevel)
        {
            case CalLevel.Day:
                titles.Children.Add(CalTitleButton(Cap(_calMonth.ToString("MMMM", Loc.Culture)), () => CalGoUp(CalLevel.Month)));
                titles.Children.Add(CalTitleButton(_calMonth.Year.ToString(), () => CalGoUp(CalLevel.Year)));
                break;
            case CalLevel.Month:
                titles.Children.Add(CalTitleButton(_calYear.ToString(), () => CalGoUp(CalLevel.Year)));
                break;
            default:
                titles.Children.Add(new TextBlock { Text = $"{_calBlock} – {_calBlock + 11}", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(5, 0, 0, 0) });
                break;
        }
        bar.Children.Add(titles);
        return bar;
    }

    /// <summary>The month's or year's name: a small arrow next to it says it can be clicked.</summary>
    private Border CalTitleButton(string text, Action click)
    {
        var arrow = new TextBlock { Text = "▾", FontSize = 9, Foreground = Brushes.White, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 1, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(arrow);
        var button = new Border { Child = row, Padding = new Thickness(5, 2, 5, 2), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        button.MouseEnter += (_, _) => { button.Background = White(0x1C); arrow.Opacity = 1; };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; arrow.Opacity = 0.55; };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return button;
    }

    private Border CalTodayPill()
    {
        var pill = new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            Child = new TextBlock { Text = Loc.T("cal.today"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) }
        };
        pill.MouseEnter += (_, _) => pill.Opacity = 0.8;
        pill.MouseLeave += (_, _) => pill.Opacity = 1;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            var today = DateTime.Today;
            _calDay = today;
            _calMonth = new DateTime(today.Year, today.Month, 1);
            _calYear = today.Year;
            _calLevel = CalLevel.Day;
            CalSwap(CalMove.Down, null);
            _calRight.Child = CalRightColumn();
        };
        return pill;
    }

    private FrameworkElement CalPane() => _calLevel switch
    {
        CalLevel.Month => CalMonthsPane(),
        CalLevel.Year => CalYearsPane(),
        _ => CalDaysPane()
    };

    private FrameworkElement CalDaysPane()
    {
        var today = DateTime.Today;
        var pane = new StackPanel { Background = Brushes.Transparent };
        var week = new UniformGrid { Rows = 1, Columns = 7, Height = 18 };
        foreach (string name in Loc.Culture.DateTimeFormat.AbbreviatedDayNames)
            week.Children.Add(new TextBlock { Text = Cap(name.TrimEnd('.')), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = White(0x66), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        pane.Children.Add(week);
        var days = new UniformGrid { Rows = CalRows, Columns = 7 };
        var cursor = _calMonth.AddDays(-(int)_calMonth.DayOfWeek);
        for (int i = 0; i < CalRows * 7; i++, cursor = cursor.AddDays(1)) days.Children.Add(CalCell(cursor, _calMonth, today));
        pane.Children.Add(days);
        return pane;
    }

    /// <summary>The kinds of event between two dates (holidays don't count: every year has them), for the dots on a card.</summary>
    private List<CalType> CalTypesBetween(DateTime from, DateTime to)
    {
        var found = new List<CalType>();
        foreach (var e in _calendar.Events)
        {
            if (found.Contains(e.Type)) continue;
            for (var d = from; d < to; d = d.AddDays(1))
                if (e.OccursOn(d))
                {
                    found.Add(e.Type);
                    break;
                }
        }
        return found;
    }

    private FrameworkElement CalMonthsPane()
    {
        var today = DateTime.Today;
        var grid = new UniformGrid { Rows = 3, Columns = 4, Margin = new Thickness(0, 3, 0, 0) };
        for (int m = 1; m <= 12; m++)
        {
            int month = m;
            var start = new DateTime(_calYear, month, 1);
            string name = Cap(Loc.Culture.DateTimeFormat.AbbreviatedMonthNames[m - 1].TrimEnd('.'));
            Border? cell = null;
            cell = CalCard(name, _calYear == today.Year && month == today.Month, _calYear == _calDay.Year && month == _calDay.Month, CalTypesBetween(start, start.AddMonths(1)), () =>
            {
                var origin = cell!.TranslatePoint(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2), _calBody);
                _calMonth = start;
                _calLevel = CalLevel.Day;
                CalSwap(CalMove.Down, origin);
            });
            grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement CalYearsPane()
    {
        var today = DateTime.Today;
        var grid = new UniformGrid { Rows = 3, Columns = 4, Margin = new Thickness(0, 3, 0, 0) };
        for (int i = 0; i < 12; i++)
        {
            int year = _calBlock + i;
            var start = new DateTime(year, 1, 1);
            Border? cell = null;
            cell = CalCard(year.ToString(), year == today.Year, year == _calDay.Year, CalTypesBetween(start, start.AddYears(1)), () =>
            {
                var origin = cell!.TranslatePoint(new Point(cell.ActualWidth / 2, cell.ActualHeight / 2), _calBody);
                _calYear = year;
                _calLevel = CalLevel.Month;
                CalSwap(CalMove.Down, origin);
            });
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Border CalCard(string text, bool current, bool selected, List<CalType> types, Action click)
    {
        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), Background = current ? Brushes.White : Brushes.Transparent,
            Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = current ? FontWeights.ExtraBold : FontWeights.SemiBold, Foreground = current ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : Brushes.White }
        };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        foreach (var t in types.Take(3)) dots.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Br(CalendarService.ColorOf(t)), Margin = new Thickness(1, 0, 1, 0) });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label);
        stack.Children.Add(dots);
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(11), Margin = new Thickness(1.5), Background = White(0x0A), Cursor = Cursors.Hand,
            BorderThickness = new Thickness(1.3), BorderBrush = selected ? White(0x90) : Brushes.Transparent };
        card.MouseEnter += (_, _) => card.Background = White(0x20);
        card.MouseLeave += (_, _) => card.Background = White(0x0A);
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return card;
    }

    private void CalGoUp(CalLevel to)
    {
        if (to == CalLevel.Month) _calYear = _calMonth.Year;
        else _calBlock = CalBlockOf(_calLevel == CalLevel.Month ? _calYear : _calMonth.Year);
        _calLevel = to;
        CalSwap(CalMove.Up, null);
    }

    /// <summary>Esc: one level back towards the days.</summary>
    private void CalStepBack()
    {
        if (_calLevel == CalLevel.Day) return;
        _calLevel = _calLevel == CalLevel.Year ? CalLevel.Month : CalLevel.Day;
        CalSwap(CalMove.Down, null);
    }

    /// <summary>The arrows and the mouse wheel: the next or previous month, year or block of years.</summary>
    private void CalStep(int direction)
    {
        switch (_calLevel)
        {
            case CalLevel.Day:
                var month = _calMonth.AddMonths(direction);
                if (month.Year is < CalMinYear or > CalMaxYear) return;
                _calMonth = month;
                break;
            case CalLevel.Month:
                if (_calYear + direction is < CalMinYear or > CalMaxYear) return;
                _calYear += direction;
                break;
            default:
                if (_calBlock + 12 * direction is < CalMinYear or > CalMaxYear) return;
                _calBlock += 12 * direction;
                break;
        }
        CalSlide(direction);
    }

    /// <summary>Next or previous page: the grids move sideways together, the new one coming in from the side you're going to.</summary>
    private void CalSlide(int direction)
    {
        const double Width = 238;
        _calTop.Child = CalBar();
        var panes = _calBody.Children.OfType<FrameworkElement>().ToList();
        var old = panes.LastOrDefault();
        foreach (var leftover in panes.Where(p => p != old)) _calBody.Children.Remove(leftover);
        var next = CalPane();
        var time = TimeSpan.FromMilliseconds(Dur(280));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (old != null)
        {
            old.IsHitTestVisible = false;
            // (scrolling fast: it may still be on its way in, so it leaves from where it is)
            double fromX = old.RenderTransform is TranslateTransform moving ? moving.X : 0;
            var oldMove = new TranslateTransform(fromX, 0);
            old.RenderTransform = oldMove;
            var leave = new DoubleAnimation(fromX, -direction * Width, time) { EasingFunction = ease };
            leave.Completed += (_, _) => _calBody.Children.Remove(old);
            oldMove.BeginAnimation(TranslateTransform.XProperty, leave);
        }

        var move = new TranslateTransform(direction * Width, 0);
        next.RenderTransform = move;
        _calBody.Children.Add(next);
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * Width, 0, time) { EasingFunction = ease });
        CalFitHeight();
    }

    /// <summary>Puts the new grid in place of the old one, zooming from where you clicked.</summary>
    private void CalSwap(CalMove move, Point? origin)
    {
        _calTop.Child = CalBar();
        var old = _calBody.Children.OfType<FrameworkElement>().LastOrDefault();
        var next = CalPane();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var inTime = TimeSpan.FromMilliseconds(Dur(300));
        var outTime = TimeSpan.FromMilliseconds(Dur(200));
        var (from, to) = move == CalMove.Down ? (0.72, 1.3) : (1.3, 0.72);
        var center = origin ?? new Point(238 / 2.0, CalBodyHeight / 2);

        if (old != null)
        {
            old.IsHitTestVisible = false;
            var oldScale = new ScaleTransform(1, 1, center.X, center.Y);
            old.RenderTransform = oldScale;
            oldScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, outTime));
            oldScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, outTime));
            var fade = new DoubleAnimation(0, outTime);
            fade.Completed += (_, _) => _calBody.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
        }

        var scale = new ScaleTransform(from, from, center.X, center.Y);
        next.RenderTransform = scale;
        _calBody.Children.Add(next);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(from, 1, inTime) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(from, 1, inTime) { EasingFunction = ease });
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, inTime) { EasingFunction = ease });
        CalFitHeight();
    }

    private void CalFitHeight()
    {
        // a month with six weeks is taller than one with five
        if (Math.Abs(_calBody.Height - CalBodyHeight) > 0.5)
        {
            _calBody.Height = CalBodyHeight;
            _calScroll.Height = CalListHeight;
            ResizeSubPage();
        }
    }

    /// <summary>Notchy never takes the keyboard from the app in use, so Esc is read directly while the mouse is on the island.</summary>
    private void UpdateCalendarKeys()
    {
        bool down = (GetAsyncKeyState(0x1B) & 0x8000) != 0;
        if (down && !_calEscDown && _calLevel != CalLevel.Day && _subOpen && _subTitle.Text == Loc.T("cal.title") && Island.IsMouseOver) CalStepBack();
        _calEscDown = down;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private Border CalCell(DateTime day, DateTime month, DateTime today)
    {
        var events = _calendar.On(day);
        bool outside = day.Month != month.Month, isToday = day == today, selected = day == _calDay, holiday = events.Any(e => e.Type == CalType.Holiday);
        var numberColor = isToday ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14))
            : outside ? White(0x3A) : holiday ? Br(CalendarService.ColorOf(CalType.Holiday)) : Brushes.White;
        var number = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = isToday ? Brushes.White : Brushes.Transparent,
            Child = new TextBlock { Text = day.Day.ToString(), FontSize = 12, FontWeight = isToday ? FontWeights.ExtraBold : FontWeights.SemiBold, Foreground = numberColor, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0), Opacity = outside ? 0.35 : 1 };
        foreach (var e in events.Take(3))
        {
            var c = CalendarService.ColorOf(e.Type);
            dots.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = Br(c), Margin = new Thickness(1, 0, 1, 0) });
        }
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        number.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(number);
        stack.Children.Add(dots);
        var cell = new Border { Height = 30, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = stack,
            BorderThickness = new Thickness(1.2), BorderBrush = selected ? White(0x90) : Brushes.Transparent };
        cell.MouseEnter += (_, _) => cell.Background = White(0x10);
        cell.MouseLeave += (_, _) => cell.Background = Brushes.Transparent;
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _calDay = day;
            if (outside) _calMonth = new DateTime(day.Year, day.Month, 1);
            RefreshCalendar();
        };
        return cell;
    }

    private Border CalRow(CalEvent e, DateTime day, bool showDate)
    {
        var color = CalendarService.ColorOf(e.Type);
        var bar = new Border { Width = 3, Height = 26, CornerRadius = new CornerRadius(1.5), Background = Br(color), Effect = new DropShadowEffect { Color = color, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.7 }, VerticalAlignment = VerticalAlignment.Center };
        var texts = new StackPanel { Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = e.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = CalLine(e, day), FontSize = 10.5, Foreground = White(0x80), TextTrimming = TextTrimming.CharacterEllipsis });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(bar);
        grid.Children.Add(texts);
        if (showDate)
        {
            var when = new TextBlock { Text = day.ToString("ddd d MMM", Loc.Culture), FontSize = 10, Foreground = White(0x80), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(when, 2);
            grid.Children.Add(when);
        }
        var row = new Border { Child = grid, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent };
        if (e.Type != CalType.Holiday)
        {
            row.Cursor = Cursors.Hand;
            row.MouseEnter += (_, _) => row.Background = White(0x10);
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                SoundService.Play(Sfx.Click);
                OpenCalEditor(e);
            };
        }
        return row;
    }

    // =====================================================================================
    // the editor
    // =====================================================================================

    private void OpenCalEditor(CalEvent? e)
    {
        _calEditing = e;
        _subParent = (Loc.T("cal.title"), CalendarPage, null);
        OpenSubPage(Loc.T(e == null ? "cal.new.title" : "cal.edit.title"), CalEditor);
    }

    private static bool TryCalTime(string text, out int hour, out int minute)
    {
        hour = minute = 0;
        text = text.Trim().ToLowerInvariant().Replace('h', ':');
        if (text.EndsWith(':')) text = text[..^1];
        string h, m = "0";
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length != 2) return false;
            (h, m) = (parts[0], parts[1]);
        }
        else if (text.Length >= 3)
        {
            (h, m) = (text[..^2], text[^2..]);
        }
        else h = text;
        return int.TryParse(h, out hour) && int.TryParse(m, out minute) && hour is >= 0 and < 24 && minute is >= 0 and < 60;
    }

    private TextBox CalBox(string text, double? width = null)
    {
        var box = new TextBox
        {
            Text = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White, FontSize = 11.5,
            Padding = new Thickness(4, 5, 4, 5), FocusVisualStyle = null, SelectionBrush = Br(Color.FromRgb(0x7F, 0xD0, 0xFF)), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        if (width is double w) box.Width = w;
        MakeTypable(box, null);
        return box;
    }

    /// <summary>The box with its placeholder, on a rounded background when <paramref name="rounded"/>.</summary>
    private Border WithHint(TextBox box, string hint, TextAlignment align = TextAlignment.Center, bool rounded = true)
    {
        var label = new TextBlock { Text = hint, FontSize = box.FontSize, Foreground = White(0x66), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, TextAlignment = align,
            Margin = new Thickness(rounded ? 0 : 4, 0, 0, 0), Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        box.TextChanged += (_, _) => label.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(label);
        return new Border { Child = grid, CornerRadius = new CornerRadius(9), Background = rounded ? White(0x0E) : Brushes.Transparent };
    }

    private static FrameworkElement CalLineOf(string label, UIElement control, double top = 8)
    {
        var line = new DockPanel { Margin = new Thickness(0, top, 0, 0) };
        var text = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), Width = 62, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        line.Children.Add(text);
        line.Children.Add(control);
        return line;
    }

    private FrameworkElement CalEditor()
    {
        var editing = _calEditing;
        bool noticeTouched = editing != null;
        var repeat = editing?.Repeat ?? CalRepeat.Once;
        var type = editing?.Type ?? CalType.Personal;
        int notice = editing?.NoticeMinutes ?? 15;
        var day = editing?.Date.Date ?? _calDay;

        var name = new TextBox
        {
            Text = editing?.Name ?? "", Background = Brushes.Transparent, BorderBrush = White(0x40), BorderThickness = new Thickness(0, 0, 0, 1.3), Foreground = Brushes.White, CaretBrush = Brushes.White,
            FontSize = 14, Padding = new Thickness(2, 3, 2, 5), FocusVisualStyle = null, SelectionBrush = Br(Color.FromRgb(0x7F, 0xD0, 0xFF))
        };
        MakeTypable(name, null);
        var time = CalBox(editing is { HasTime: true } ? $"{editing.Hour:00}:{editing.Minute:00}" : "", 66);
        var born = CalBox(editing is { BornYear: > 0 } ? editing.BornYear.ToString() : "", 66);

        var page = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        page.Children.Add(WithHint(name, Loc.T("cal.name.hint"), TextAlignment.Left, rounded: false));

        var dayLine = new DockPanel();
        var timeHost = WithHint(time, Loc.T("cal.allday.short"));
        DockPanel.SetDock(timeHost, Dock.Right);
        dayLine.Children.Add(timeHost);
        var timeLabel = new TextBlock { Text = Loc.T("cal.time"), FontSize = 11, Foreground = White(0x80), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(timeLabel, Dock.Right);
        dayLine.Children.Add(timeLabel);
        dayLine.Children.Add(new TextBlock { Text = DayLine(day), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        page.Children.Add(CalLineOf(Loc.T("cal.day"), dayLine));

        var repeatCells = new List<(Border cell, CalRepeat value)>();
        var noticeCells = new List<(Border cell, int value)>();
        var typeCells = new List<(Border cell, CalType value)>();
        FrameworkElement bornLine = null!;

        bool HasTimeNow() => time.Text.Trim().Length > 0;
        void Paint()
        {
            bool timed = HasTimeNow();
            if (!noticeTouched) notice = type == CalType.Birthday ? 0 : timed ? 15 : 1440;
            if (notice == 15 && !timed) notice = 1440;
            foreach (var (c, v) in repeatCells) MarkCell(c, v == repeat, true);
            foreach (var (c, v) in noticeCells) MarkCell(c, v == notice, v != 15 || timed);
            foreach (var (c, v) in typeCells) MarkCell(c, v == type, true);
            bornLine.Visibility = type == CalType.Birthday ? Visibility.Visible : Visibility.Collapsed;
            ResizeSubPage();
        }
        time.TextChanged += (_, _) => Paint();

        var repeatRow = new UniformGrid { Rows = 1, Columns = 4 };
        foreach (var (key, value) in new[] { ("cal.rep.once", CalRepeat.Once), ("cal.rep.week", CalRepeat.Weekly), ("cal.rep.month", CalRepeat.Monthly), ("cal.rep.year", CalRepeat.Yearly) })
        {
            var cell = CalSegCell(Loc.T(key), null, () => { repeat = value; Paint(); });
            repeatCells.Add((cell, value));
            repeatRow.Children.Add(cell);
        }
        page.Children.Add(CalLineOf(Loc.T("cal.repeat"), repeatRow));

        var noticeRow = new UniformGrid { Rows = 1, Columns = 4 };
        foreach (var (key, value) in new[] { ("cal.notice.now", 0), ("cal.notice.15", 15), ("cal.notice.day", 1440), ("cal.notice.week", 10080) })
        {
            var cell = CalSegCell(Loc.T(key), null, () =>
            {
                if (value == 15 && !HasTimeNow()) return;
                notice = value;
                noticeTouched = true;
                Paint();
            });
            noticeCells.Add((cell, value));
            noticeRow.Children.Add(cell);
        }
        page.Children.Add(CalLineOf(Loc.T("cal.notice"), noticeRow));

        var typeRow = new UniformGrid { Rows = 1, Columns = 4 };
        foreach (var (key, value) in new[] { ("cal.type.personal", CalType.Personal), ("cal.type.birthday", CalType.Birthday), ("cal.type.work", CalType.Work), ("cal.type.bills", CalType.Bills) })
        {
            var cell = CalSegCell(Loc.T(key), CalendarService.ColorOf(value), () => { type = value; Paint(); });
            typeCells.Add((cell, value));
            typeRow.Children.Add(cell);
        }
        page.Children.Add(CalLineOf(Loc.T("cal.type"), typeRow));

        var bornRow = new StackPanel { Orientation = Orientation.Horizontal };
        bornRow.Children.Add(WithHint(born, Loc.T("cal.year")));
        bornRow.Children.Add(new TextBlock { Text = Loc.T("cal.born.hint"), FontSize = 10.5, Foreground = White(0x80), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        bornLine = CalLineOf(Loc.T("cal.born"), bornRow);
        page.Children.Add(bornLine);

        // ---- bottom line
        void Back()
        {
            Keyboard.ClearFocus();
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
            SubPageBack();
            UpdateDateDot();
            UpdateCalSign();
        }
        void Save()
        {
            string title = name.Text.Trim();
            if (title.Length == 0)
            {
                name.BorderBrush = Br(Color.FromRgb(0xF5, 0xA5, 0x24));
                return;
            }
            int hour = 0, minute = 0;
            bool timed = HasTimeNow();
            if (timed && !TryCalTime(time.Text, out hour, out minute))
            {
                timeHost.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xF5, 0xA5, 0x24));
                return;
            }
            int.TryParse(born.Text.Trim(), out int year);
            var e = editing ?? new CalEvent { Date = day };
            bool moved = editing == null || e.HasTime != timed || e.Hour != hour || e.Minute != minute || e.NoticeMinutes != notice || e.Repeat != repeat || e.Type != type;
            e.Name = title;
            e.HasTime = timed;
            e.Hour = hour;
            e.Minute = minute;
            e.Repeat = repeat;
            e.Type = type;
            e.NoticeMinutes = notice;
            e.BornYear = type == CalType.Birthday && year is > 1800 and < 3000 ? year : 0;
            if (moved)
            {
                e.NotifiedFor = null;
                e.SnoozeUntil = null;
                e.SnoozeFor = null;
            }
            _calendar.Put(e);
            Back();
        }

        var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        if (editing != null)
        {
            var remove = ListPill(Glyph(0xE74D), Loc.T("cal.delete"), false, () =>
            {
                _calendar.Remove(editing);
                Back();
            });
            DockPanel.SetDock(remove, Dock.Left);
            foot.Children.Add(remove);
        }
        var save = ListPill(Glyph(0xE73E), Loc.T("cal.save"), true, Save);
        DockPanel.SetDock(save, Dock.Right);
        foot.Children.Add(save);
        var cancel = ListPill(Glyph(0xE711), Loc.T("cal.cancel"), false, Back);
        DockPanel.SetDock(cancel, Dock.Right);
        cancel.Margin = new Thickness(0, 0, 6, 0);
        foot.Children.Add(cancel);
        page.Children.Add(foot);

        Paint();
        if (editing == null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
                Activate();
                name.Focus();
                Keyboard.Focus(name);
            }, DispatcherPriority.Loaded);
        }
        return page;
    }

#if DEBUG
    private bool _calForceOpen;

    /// <summary>Goes through the calendar's screens with made-up events, saves a picture of each and quits.</summary>
    public async System.Threading.Tasks.Task CalendarTestAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        while (_introActive) await System.Threading.Tasks.Task.Delay(200);
        await System.Threading.Tasks.Task.Delay(800);
        async System.Threading.Tasks.Task Shot(string name, int wait = 900)
        {
            await System.Threading.Tasks.Task.Delay(wait);
            var root = (FrameworkElement)Content;
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            var back = new DrawingVisual();
            using (var dc = back.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x2A, 0x86)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            picture.Render(back);
            picture.Render(root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
            png.Save(file);
        }

        var today = DateTime.Today;
        var results = new List<string>();
        void Check(string what, bool ok) => results.Add((ok ? "ok    " : "FALHOU ") + what);
        List<CalFired> Run(CalEvent e, params DateTime[] moments)
        {
            var s = new CalendarService { DontSave = true };
            var fired = new List<CalFired>();
            s.Fired += fired.Add;
            s.Events.Add(e);
            foreach (var m in moments) s.Tick(m);
            return fired;
        }
        var probe = new CalendarService { DontSave = true };
        Check("Sexta-feira Santa de 2026 em 3/4", probe.On(new DateTime(2026, 4, 3)).Any(e => e.Type == CalType.Holiday));
        Check("sem feriado em 4/4/2026", !probe.On(new DateTime(2026, 4, 4)).Any(e => e.Type == CalType.Holiday));
        var monthly = new CalEvent { Date = new DateTime(2026, 1, 31), Repeat = CalRepeat.Monthly };
        Check("dia 31 cai em 28/2", monthly.OccursOn(new DateTime(2026, 2, 28)) && !monthly.OccursOn(new DateTime(2026, 2, 27)));
        var weekly = new CalEvent { Name = "w", Date = new DateTime(2026, 11, 9), Repeat = CalRepeat.Weekly };
        Check("semanal não aparece antes do início", !weekly.OccursOn(new DateTime(2026, 11, 2)) && weekly.OccursOn(new DateTime(2026, 11, 16)));
        probe.Events.Add(weekly);
        Check("repetido aparece uma vez em Próximos", probe.After(new DateTime(2026, 11, 9), 12).Count(x => x.e == weekly) == 1);
        var noon = today.AddHours(12);
        var soon = new CalEvent { Date = today, HasTime = true, Hour = 12, Minute = 10, NoticeMinutes = 15 };
        Check("avisa 15 min antes, uma vez só", Run(soon, noon, noon.AddMinutes(1)).Count == 1);
        var gone = new CalEvent { Date = today, HasTime = true, Hour = 11, Minute = 55, NoticeMinutes = 15 };
        Check("evento que já passou não avisa", Run(gone, noon).Count == 0);
        var bills = new CalEvent { Date = today.AddDays(1), NoticeMinutes = 1440, Type = CalType.Bills };
        Check("dia inteiro, 1 dia antes: não avisa às 8h", Run(bills, today.AddHours(8)).Count == 0);
        Check("dia inteiro, 1 dia antes: avisa às 10h", Run(new CalEvent { Date = today.AddDays(1), NoticeMinutes = 1440, Type = CalType.Bills }, today.AddHours(10)).Count == 1);
        var b1 = Run(new CalEvent { Date = today, Type = CalType.Birthday, Repeat = CalRepeat.Yearly }, today.AddSeconds(30), today.AddSeconds(40));
        Check("aniversário: festinha à meia-noite, uma vez só", b1.Count == 1 && b1[0].Party);
        var b2 = Run(new CalEvent { Date = today, Type = CalType.Birthday, Repeat = CalRepeat.Yearly }, today.AddHours(23));
        Check("aniversário: PC ligado tarde ainda festeja", b2.Count == 1 && b2[0].Party);
        var snoozed = new CalEvent { Date = today, HasTime = true, Hour = 12, Minute = 10, NoticeMinutes = 15, NotifiedFor = today, SnoozeUntil = noon.AddHours(1), SnoozeFor = today };
        Check("lembrar de novo: espera a hora", Run(snoozed, noon).Count == 0);
        Check("lembrar de novo: avisa depois", Run(snoozed, noon.AddHours(2)).Count == 1);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "logica.txt"), results);

        _calNextTick = DateTime.MaxValue;
        _calendar.DontSave = true;
        var meet = new CalEvent { Name = "Reunião do projeto", Date = today, HasTime = true, Hour = 23, Minute = 30, Repeat = CalRepeat.Weekly, Type = CalType.Work };
        var bday = new CalEvent { Name = "Aniversário da Ana", Date = today.AddDays(3), Repeat = CalRepeat.Yearly, Type = CalType.Birthday, BornYear = today.Year - 30 };
        _calendar.Events = new List<CalEvent>
        {
            meet, bday,
            new() { Name = "Pagar internet", Date = today.AddDays(1), Repeat = CalRepeat.Monthly, Type = CalType.Bills },
            new() { Name = "Dentista", Date = today.AddDays(14), HasTime = true, Hour = 16, Type = CalType.Personal }
        };
        _calSign = null;
        UpdateCalSign();
        UpdateDateDot();
        _forcedState = IslandState.Idle;
        UpdateState();
        await Shot("1_sinal");

        _claude.Inject("""{"hook_event_name":"UserPromptSubmit","session_id":"demo","cwd":"C:\\proj\\Notchy","prompt":"x"}""");
        _forcedState = IslandState.Expanded;
        UpdateState();
        SelectTab(Tab.Downloads, true);
        await Shot("2_cabecalho");
        OpenCalendar(today);
        await Shot("3_calendario");
        _calDay = today.AddDays(3);
        RefreshCalendar();
        await Shot("4_outro_dia");
        CalGoUp(CalLevel.Month);
        await Shot("4a_meses");
        CalStep(1);
        await Shot("4b1_deslizando", 90);
        await Shot("4b_meses_outro_ano");
        CalGoUp(CalLevel.Year);
        await Shot("4c_anos");
        CalStepBack();
        await Shot("4d_voltou_meses");
        _calMonth = new DateTime(2026, 8, 1); // six weeks
        _calLevel = CalLevel.Day;
        CalSwap(CalMove.Down, null);
        await Shot("4e_seis_semanas");
        _calMonth = new DateTime(today.Year, today.Month, 1);
        RefreshCalendar();
        OpenCalEditor(bday);
        await Shot("5_editor");
        Keyboard.ClearFocus();
        CloseSubPage(animate: false);

        _forcedState = null;
        UpdateState();
        OnCalFired(new CalFired(meet, today, false));
        await Shot("6_aviso", 1500);
        _calForceOpen = true;
        await Shot("7_aviso_mouse");
        _calForceOpen = false;
        EndCalNotice();
        await System.Threading.Tasks.Task.Delay(600);
        OnCalFired(new CalFired(bday, today, true));
        await Shot("8_festa", 1300);
        await Shot("9_festa_depois", 1000);

        // only made-up rows: the real list has other apps' messages
        EndCalNotice();
        _forcedState = IslandState.Expanded;
        UpdateState();
        await System.Threading.Tasks.Task.Delay(700);
        OpenSubPage(Loc.T("notif.title"), () =>
        {
            var list = new StackPanel();
            foreach (var (type, text, sub) in new[] { (CalType.Birthday, "Ana faz 30 anos", ""), (CalType.Work, "Reunião do projeto", "em 15 min"), (CalType.Bills, "Pagar internet", "amanhã"), (CalType.Personal, "Dentista", "hoje às 16:00") })
                list.Children.Add(NoticeRow(new Notice(0, Loc.T("cal.title"), CalendarAppId, text, sub, DateTime.Now, null, false) { CalendarType = type }));
            return list;
        });
        await Shot("10_historico", 1000);

        S.CalendarHintSeen = false;
        S.Save();
        Application.Current.Shutdown();
    }
#endif

    private Border CalSegCell(string text, Color? dot, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (dot is Color c) row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Br(c), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var cell = new Border { Child = row, Height = 26, CornerRadius = new CornerRadius(9), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return cell;
    }

    private static void MarkCell(Border cell, bool on, bool enabled)
    {
        cell.Background = White((byte)(on ? 0x2C : 0x0D));
        cell.BorderBrush = on ? White(0x55) : Brushes.Transparent;
        cell.Opacity = enabled ? 1 : 0.35;
        cell.Cursor = enabled ? Cursors.Hand : Cursors.Arrow;
        if (cell.Child is Panel p && p.Children[^1] is TextBlock t) t.Foreground = on ? Brushes.White : White(0xB0);
    }
}
