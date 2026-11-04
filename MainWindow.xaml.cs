using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

public partial class MainWindow : Window
{
    private enum IslandState { None, Idle, Compact, Volume, Expanded }
    private enum Tab { Home, Claude, Music, Chat, Focus, Shelf }
    private enum CompactMode { None, Music, MusicPaused, Claude, ClaudeDone, ClaudeAttention, Focus, FocusDone, Battery, BatteryLow, Update }

    private static readonly Color ClaudeColor = Color.FromRgb(0xE8, 0x82, 0x5F);
    private static readonly Color FocusColor = Color.FromRgb(0xB7, 0x9C, 0xFF);

    private readonly MediaService _media;
    private readonly ClaudeService _claude;
    private readonly FocusTimer _focus = new();
    private readonly LyricsService _lyrics = new();
    private readonly AudioService _audio = new();
    private readonly DispatcherTimer _frame;
    private readonly DispatcherTimer _hoverTimer;
    private readonly ScaleTransform _blink = new(1, 1);
    private readonly ContextMenu _menu;
    private readonly Random _rand = new();

    private static AppSettings S => AppSettings.Current;

    private IntPtr _hwnd;
    private IslandState _state = IslandState.None;
    private Tab _tab = Tab.Home;
    private Tab? _userTab;          // last tab YOU picked (nav, home cards)
    private bool _podOpenRequest;   // opened by clicking the Claude pod: show Claude this time only
    private CompactMode _compactMode = CompactMode.None;
    private bool _hoverRequested, _hoverActive, _clickOpened, _hiddenForFullscreen;
    private double _topHidden = 30;
    private DateTime _volumeOsdUntil = DateTime.MinValue;
    private float _lastVol = -1;
    private bool _lastMute;
    private int _tick;
    private Color _musicAccent = ColorUtil.Fallback;

    // transient compact messages
    private CompactMode _transient = CompactMode.None;
    private DateTime _transientUntil;
    private string _transientText = "", _transientRight = "";

    // lyrics
    private List<LyricLine>? _lines;
    private int _lineIdx = int.MinValue;
    private bool _lyricsLoading;
    private string _trackKey = "";
    private CancellationTokenSource? _lyricsCts;
    private string _compactShown = "\0";
    private string _lyricShown = "\0";
    private string _musicCompactText = "";

    private readonly Dictionary<Tab, (Button button, TextBlock label)> _nav = new();
    private readonly Border[] _stepFills = new Border[4];
    private readonly TextBlock[] _stepLabels = new TextBlock[4];

    public MainWindow()
    {
        InitializeComponent();

        _media = new MediaService(Dispatcher);
        _media.MediaChanged += OnMediaChanged;
        _media.PlaybackChanged += OnPlaybackChanged;

        _claude = new ClaudeService(Dispatcher);
        _claude.Changed += OnClaudeChanged;
        _claude.TaskDone += OnClaudeDone;
        _claude.AttentionNeeded += OnClaudeAttention;

        _focus.Finished += OnFocusFinished;

        EyeL.RenderTransform = EyeR.RenderTransform = EyeL2.RenderTransform = EyeR2.RenderTransform = _blink;
        StartBlinking();
        StartSpinners();

        ProgressSlider.ValueChanging += v => ElapsedText.Text = Format(TimeSpan.FromTicks((long)(_media.Duration.Ticks * v)));
        ProgressSlider.ValueCommitted += async v => await _media.SeekAsync(TimeSpan.FromTicks((long)(_media.Duration.Ticks * v)));
        VolumeSlider.ValueChanging += SetMusicVolume;
        VolumeSlider.ValueCommitted += SetMusicVolume;

        Island.SizeChanged += (_, _) => { UpdateIslandShape(); UpdatePodPosition(); };
        InitExtras();
        InitChat();
        InitSpotify();
        InitFace();
        InitAudioOut();
        InitUpdates();
        UpdateOutputIcon();
        _menu = BuildMenu();
        BuildNav();
        BuildSteps();

        _hoverTimer = new DispatcherTimer();
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            // The ••• menu grabs the mouse, which looks like "left the island". Ignore that while it's
            // open; when it closes, menu.Closed checks where the cursor really is.
            if (_menu.IsOpen || _devicesMenuOpen?.IsOpen == true || _outMenuOpen?.IsOpen == true) return;
            bool dragging = ProgressSlider.IsDragging || VolumeSlider.IsDragging;
            if (S.Open == OpenMode.Hover) _hoverActive = _hoverRequested || dragging;
            if (!_hoverRequested && !dragging) _clickOpened = false;
            UpdateState();
        };

        _frame = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frame.Tick += OnFrame;

        AppSettings.Changed += ApplySettings;

        ApplySettings();
        OnMediaChanged();
        OnPlaybackChanged();
        OnClaudeChanged();
        SelectTab(Tab.Home, animate: false);

        Loaded += async (_, _) =>
        {
            PositionWindow();
            UpdateState();
            _frame.Start();
            _claude.Start();
            try
            {
                await _media.InitAsync();
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeOverlayWindow(_hwnd);
    }

    private void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private double Dur(double ms) => ms * S.SpeedFactor;

    // =====================================================================================
    // settings / language
    // =====================================================================================

    private void ApplySettings()
    {
        Loc.Language = S.Language;
        IslandScale.ScaleX = IslandScale.ScaleY = S.SizeScale;

        bool floating = S.Style == IslandStyle.Floating;
        _topHidden = floating ? 0 : 30;
        Island.Margin = new Thickness(0, floating ? 8 : -30, 0, 0);
        PanelsHost.Margin = new Thickness(0, _topHidden, 0, 0);

        byte alpha = (byte)Math.Round(255 * (1 - Math.Clamp(S.SeeThrough, 0, 85) / 100.0));
        Island.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        ClaudePod.Background = Island.Background;
        ClaudePod.Margin = Island.Margin;
        PodContent.Margin = new Thickness(0, _topHidden, 0, 0);
        ApplyExtrasSettings();
        if (_nav.Count > 0) ApplyGlowEffects();

        if (!S.GameMode && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Island.Visibility = Visibility.Visible;
        }

        _focus.Refresh();
        ApplyTexts();
        ApplyGlow();
        _lineIdx = int.MinValue;
        if (_trackKey != "") { _trackKey = "\0"; OnMediaChanged(); }

        if (_state != IslandState.None)
        {
            var s = _state;
            _state = IslandState.None;
            ApplyState(s, playSound: false);
        }
    }

    private void ApplyTexts()
    {
        foreach (var (tab, (_, label)) in _nav)
            label.Text = tab switch
            {
                Tab.Home => Loc.T("tab.home"),
                Tab.Claude => "Claude",
                Tab.Music => Loc.T("tab.music"),
                Tab.Shelf => Loc.T("tab.shelf"),
                Tab.Chat => "Notchy",
                _ => "Focus"
            };
        ShelfEmptyText.Text = Loc.T("shelf.empty");
        ShelfHint.Text = Loc.T("shelf.hint");
        ShelfClearText.Text = Loc.T("shelf.clear");
        string[] steps = { Loc.T("claude.step.read"), Loc.T("claude.step.edit"), Loc.T("claude.step.test"), Loc.T("claude.step.done") };
        for (int i = 0; i < 4; i++) if (_stepLabels[i] != null) _stepLabels[i].Text = steps[i];

        HomeMusicLabel.Text = Loc.T("tab.music");
        ClaudeRecentTitle.Text = Loc.T("claude.recent").ToUpperInvariant();
        FocusModeFocusText.Text = Loc.T("focus.focus");
        FocusModeBreakText.Text = Loc.T("focus.break");
        SourceButton.ToolTip = Loc.T("music.openapp");
        MoreButton.ToolTip = Loc.T("menu.options");
        UpdateHeader();
        UpdateClock();
        OnClaudeChanged();
        UpdateFocusUi();
        ApplyChatTexts();
    }

    // =====================================================================================
    // state machine
    // =====================================================================================

    private void UpdateState()
    {
        IslandState next;
        if (_forcedState is { } forced) next = forced;
        else if (_hoverActive || _clickOpened || _menu.IsOpen || _devicesMenuOpen?.IsOpen == true || _dragOver || ChatKeepsOpen) next = IslandState.Expanded;
        else if (DateTime.UtcNow < _volumeOsdUntil || (_state == IslandState.Volume && (_volHover || _outMenuOpen?.IsOpen == true)))
            next = IslandState.Volume;
        else
        {
            var mode = DecideCompact();
            if (mode != _compactMode)
            {
                _compactMode = mode;
                ConfigureCompact();
                if (_state == IslandState.Compact && mode != CompactMode.None)
                {
                    _state = IslandState.None; // force a resize to the new compact width
                }
            }
            next = mode == CompactMode.None ? IslandState.Idle : IslandState.Compact;
        }

        if (next != _state) ApplyState(next);
    }

    private Size TargetSize(IslandState state) => state switch
    {
        IslandState.Idle => new Size(120, 30),
        IslandState.Volume => new Size(300, 34),
        IslandState.Expanded => new Size(480, TabHeight(_tab)),
        _ => new Size(CompactWidth(_compactMode), 34)
    };

    private double TabHeight(Tab t) => 34 + 40 + 2 + t switch
    {
        Tab.Home => 118,
        Tab.Claude => 134,
        Tab.Music => 186 + (_lyricTwoLines ? LyricExtraHeight : 0),
        Tab.Shelf => 118,
        Tab.Chat => ChatContentHeight,
        _ => 128
    };

    private double CompactWidth(CompactMode m) => m switch
    {
        CompactMode.Music => _musicCompactWidth,
        CompactMode.MusicPaused => 300,
        CompactMode.Claude => 360,
        CompactMode.Focus => 250,
        _ => 320
    };

    private void ApplyState(IslandState state, bool playSound = true)
    {
        var prev = _state;
        bool expanding = state == IslandState.Expanded;
        _state = state;
        if (Environment.GetEnvironmentVariable("NOTCHY_DEBUG") == "1")
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {prev} -> {state}" + Environment.NewLine);

        if (expanding && prev != IslandState.Expanded)
        {
            Tab target;
            if (_podOpenRequest || _claude.Active?.NeedsAttention == true) target = Tab.Claude;
            else if (_userTab is Tab ut && (ut != Tab.Shelf || S.ShelfEnabled)) target = ut;
            else target = SmartTab();
            _podOpenRequest = false;
            if (!_dragOver) SelectTab(target, animate: false);
            if (playSound && prev != IslandState.None) SoundService.Play(Sfx.Expand);
            UpdateClock();
        }
        else if (prev == IslandState.Expanded && playSound)
        {
            SoundService.Play(Sfx.Collapse);
        }

        AnimateIslandSize(TargetSize(state), expanding || prev == IslandState.Expanded);

        Fade(IdlePanel, state == IslandState.Idle);
        Fade(CompactPanel, state == IslandState.Compact);
        Fade(VolumePanel, state == IslandState.Volume);
        Fade(ExpandedPanel, expanding);
        AccentGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(expanding ? 1 : 0, TimeSpan.FromMilliseconds(Dur(expanding ? 420 : 160))));
        if (expanding) Dispatcher.InvokeAsync(() => MoveNavPill(false), DispatcherPriority.Loaded);
    }

    private void AnimateIslandSize(Size size, bool bigMove)
    {
        // the little overshoot only when opening; closing settles without bouncing (a bounce makes
        // a wide compact island - long lyric line - wobble sideways before it rests)
        bool growing = size.Height + _topHidden > Island.ActualHeight + 0.5;
        IEasingFunction ease = bigMove && growing
            ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut }
            : bigMove ? new QuarticEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(Dur(bigMove ? 460 : 340));
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, duration) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height + _topHidden, duration) { EasingFunction = ease });
        // The compact content takes its final width right away (so nothing is cut at the edges) and the
        // island animates around it. Animating the content's width too made long lyric lines re-layout on
        // every frame and shake.
        if (_state == IslandState.Compact)
        {
            CompactPanel.BeginAnimation(WidthProperty, null);
            CompactPanel.Width = size.Width;
        }
    }

    private void Fade(UIElement panel, bool visible)
    {
        panel.IsHitTestVisible = visible;
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? 1 : 0,
            TimeSpan.FromMilliseconds(Dur(visible ? 240 : 90)))
        {
            BeginTime = TimeSpan.FromMilliseconds(Dur(visible ? 110 : 0))
        });
    }

    private void UpdateIslandShape()
    {
        double w = Island.ActualWidth, h = Island.ActualHeight;
        double radius = Math.Clamp((h - _topHidden) / 2, 8, 26);
        Island.CornerRadius = new CornerRadius(radius);
        IslandContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
    }

    private bool _blinkPaused;

    /// <summary>Stops the periodic blink while the eyes are closed (sleepy) and resumes it when they open.</summary>
    private void PauseBlinking(bool paused)
    {
        if (paused == _blinkPaused) return;
        _blinkPaused = paused;
        if (paused)
        {
            _blink.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _blink.ScaleY = 1;
        }
        else StartBlinking();
    }

    private void StartBlinking()
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(15), RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(14.6))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(14.68))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(14.8))));
        _blink.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void StartSpinners()
    {
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever };
        ClaudeSpin.BeginAnimation(RotateTransform.AngleProperty, spin);

        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        CompactAlertDot.BeginAnimation(OpacityProperty, pulse);
        HeaderClaudeDot.BeginAnimation(OpacityProperty, pulse);
    }

    /// <summary>Eyes glance around now and then.</summary>
    private void MaybeGlance()
    {
        if (_rand.NextDouble() > 0.004) return;
        double x = (_rand.NextDouble() - 0.5) * 6;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.6) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25)), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.3))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6)), ease));
        EyesLook.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // =====================================================================================
    // frame loop
    // =====================================================================================

    private void OnFrame(object? sender, EventArgs e)
    {
        _tick++;
        _focus.Tick();
        var pos = _media.Position;

        if (_state == IslandState.Expanded)
        {
            switch (_tab)
            {
                case Tab.Music when !ProgressSlider.IsDragging:
                    var dur = _media.Duration;
                    ProgressSlider.Value = dur > TimeSpan.Zero ? pos.TotalMilliseconds / dur.TotalMilliseconds : 0;
                    ElapsedText.Text = Format(pos);
                    RemainingText.Text = dur > TimeSpan.Zero ? "-" + Format(dur - pos) : "";
                    break;
                case Tab.Claude when _tick % 6 == 0:
                    UpdateClaudeTimer();
                    break;
            }
            if (_tick % 6 == 0)
            {
                UpdateFocusUi();
                UpdateHomeLive();
            }
        }

        UpdateLyrics(pos);

        if (_state == IslandState.Expanded) UpdateFace(_audio.GetPeak());
        else if (_compactFaceOn) UpdateCompactFace();

        if (_tick % 2 == 0)
        {
            float peak = _audio.GetPeak();
            if (_state == IslandState.Compact && CompactViz.Visibility == Visibility.Visible)
                CompactViz.Tick(peak, _media.IsPlaying, _audio.HasMeter);
            if (_state == IslandState.Expanded && _tab == Tab.Music)
                ExpandedViz.Tick(peak, _media.IsPlaying, _audio.HasMeter);
        }

        if (_state == IslandState.Compact && _tick % 10 == 0) UpdateCompactRight();
        UpdateExtrasFrame();

        if (_tick % 6 == 0) PollVolume();
        if (_tick % 60 == 0)
        {
            UpdateClock();
            CheckFullscreen();
        }
        if (_tick % 180 == 0)
        {
            _media.PickSession();
            _audio.EnsureDevice();
            UpdateOutputIcon();
            if (_hwnd != IntPtr.Zero && !_menu.IsOpen && _devicesMenuOpen?.IsOpen != true && _outMenuOpen?.IsOpen != true) Native.BringToTopmost(_hwnd);
        }

        UpdateState();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = Loc.Culture;
        DateRun.Text = now.ToString("ddd d MMM yyyy", culture);
        TimeRun.Text = FormatClock(now);

        var power = System.Windows.Forms.SystemInformation.PowerStatus;
        if (power.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ||
            power.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.Unknown)
        {
            BatteryText.Text = "";
        }
        else
        {
            string charging = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online ? "⚡" : "";
            BatteryText.Text = $"{charging}{Math.Round(power.BatteryLifePercent * 100)}%";
        }
    }

    private static string FormatClock(DateTime t) => S.Clock switch
    {
        ClockMode.H12 => t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
        ClockMode.H24 => t.ToString("HH:mm"),
        _ => t.ToString(Loc.Culture.DateTimeFormat.ShortTimePattern, Loc.Culture)
    };

    private void CheckFullscreen()
    {
        if (_hwnd == IntPtr.Zero || !S.GameMode) return;
        bool fullscreen = Native.IsForegroundFullscreen(_hwnd);
        if (fullscreen == _hiddenForFullscreen) return;
        _hiddenForFullscreen = fullscreen;
        Island.Visibility = fullscreen ? Visibility.Hidden : Visibility.Visible;
    }

    // =====================================================================================
    // tabs
    // =====================================================================================

    private void BuildNav()
    {
        AddNav(Tab.Home, "\uE80F");
        AddNav(Tab.Claude, "✻", symbol: true);
        AddNav(Tab.Music, "\uE8D6");
        AddNav(Tab.Chat, ((char)0xE8BD).ToString());
        AddNav(Tab.Focus, "\uE916");
        AddNav(Tab.Shelf, "\uE7B8");
    }

    private void AddNav(Tab tab, string glyph, bool symbol = false)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)FindResource("IconFont"),
            FontSize = symbol ? 14 : 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(label);

        var button = new Button { Style = (Style)FindResource("NavButton"), Content = content, Margin = new Thickness(2, 0, 2, 0) };
        button.Click += (_, _) =>
        {
            _userTab = tab;
            SelectTab(tab, animate: true);
        };
        NavBar.Children.Add(button);
        _nav[tab] = (button, label);
    }

    private Tab SmartTab()
    {
        var c = _claude.Active;
        if (c != null && (c.NeedsAttention || c.IsWorking)) return Tab.Claude;
        if (_focus.Running) return Tab.Focus;
        if (_media.HasSession && _media.IsPlaying) return Tab.Music;
        return Tab.Home;
    }

    private UIElement TabElement(Tab t) => t switch
    {
        Tab.Home => HomeTab,
        Tab.Claude => ClaudeTab,
        Tab.Music => MusicTab,
        Tab.Chat => ChatTab,
        Tab.Shelf => ShelfTab,
        _ => FocusTab
    };

    private void SelectTab(Tab tab, bool animate)
    {
        var old = _tab;
        _tab = tab;
        if (animate && old != tab && _state == IslandState.Expanded)
        {
            FaceReact(Mood.Curious, 0.45);
        }

        foreach (var (t, (button, label)) in _nav)
        {
            bool sel = t == tab;
            label.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = sel ? new SolidColorBrush(TabColor(t)) : (Brush)new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255));
        }

        foreach (Tab t in Enum.GetValues(typeof(Tab)))
        {
            var el = TabElement(t);
            if (t == tab)
            {
                el.Visibility = Visibility.Visible;
                if (animate && old != tab)
                {
                    double dir = (int)tab > (int)old ? 1 : -1;
                    var tt = new TranslateTransform();
                    el.RenderTransform = tt;
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                    tt.BeginAnimation(TranslateTransform.XProperty,
                        new DoubleAnimation(28 * dir, 0, TimeSpan.FromMilliseconds(Dur(320))) { EasingFunction = ease });
                    el.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(260))) { EasingFunction = ease });
                }
                else
                {
                    el.BeginAnimation(OpacityProperty, null);
                    el.Opacity = 1;
                    el.RenderTransform = null;
                }
            }
            else
            {
                el.BeginAnimation(OpacityProperty, null);
                el.Visibility = Visibility.Collapsed;
            }
        }

        UpdateHeader();
        ApplyGlow();
        if (tab == Tab.Claude) RefreshClaudeTab();
        if (tab == Tab.Focus) UpdateFocusUi();
        if (tab == Tab.Home) UpdateHomeLive();
        if (tab == Tab.Music) _ = PollSpotifyAsync();

        if (_state == IslandState.Expanded)
        {
            AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
            Dispatcher.InvokeAsync(() => MoveNavPill(animate), DispatcherPriority.Loaded);
        }
        ApplyGlowEffects();
    }

    private void MoveNavPill(bool animate)
    {
        if (!_nav.TryGetValue(_tab, out var nav)) return;
        nav.button.UpdateLayout();
        var p = nav.button.TranslatePoint(new Point(0, 0), NavBar);
        double x = p.X, w = nav.button.ActualWidth;
        if (w <= 0) return;
        NavPill.Background = new SolidColorBrush(Color.FromArgb(0x2E, TabColor(_tab).R, TabColor(_tab).G, TabColor(_tab).B));
        if (!animate)
        {
            NavPillX.BeginAnimation(TranslateTransform.XProperty, null);
            NavPill.BeginAnimation(WidthProperty, null);
            NavPillX.X = x;
            NavPill.Width = w;
            return;
        }
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(Dur(380));
        NavPillX.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, d) { EasingFunction = ease });
        NavPill.BeginAnimation(WidthProperty, new DoubleAnimation(w, d) { EasingFunction = ease });
    }

    private Color TabColor(Tab t) => t switch
    {
        Tab.Claude => ClaudeColor,
        Tab.Music => Bright(_musicAccent),
        Tab.Focus => FocusColor,
        Tab.Shelf => ShelfColor,
        Tab.Chat => ChatColor,
        _ => Colors.White
    };

    private void UpdateHeader()
    {
        (string a, string b) = _tab switch
        {
            Tab.Claude => ("Claude", " Code"),
            Tab.Music => ("Music", " Player"),
            Tab.Focus => ("Focus", " " + Loc.T("focus.mode")),
            Tab.Shelf => (Loc.T("shelf.title.a"), " " + Loc.T("shelf.title.b")),
            Tab.Chat => ("Notchy", " Chat"),
            _ => ("Notchy", "")
        };
        HeaderA.Text = a;
        HeaderB.Text = b;
        HeaderB.Foreground = new SolidColorBrush(TabColor(_tab));
    }

    private void ApplyGlow()
    {
        Color dark = _tab switch
        {
            Tab.Claude => Color.FromRgb(0x6B, 0x33, 0x22),
            Tab.Focus => Color.FromRgb(0x3E, 0x2A, 0x86),
            Tab.Shelf => Color.FromRgb(0x1C, 0x4A, 0x40),
            Tab.Chat => Color.FromRgb(0x4A, 0x2E, 0x12),
            Tab.Music when S.MusicColor => Dark(_musicAccent),
            _ => Color.FromRgb(0x26, 0x26, 0x2B)
        };
        var glow = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, dark.R, dark.G, dark.B), 0.3),
                new GradientStop(Color.FromArgb((byte)Math.Round(235 * Math.Clamp(S.GradientStrength, 0, 100) / 100.0), dark.R, dark.G, dark.B), 1.0),
            }
        };
        glow.Freeze();
        AccentGlow.Background = glow;
    }

    private static Color Bright(Color c)
    {
        var (h, s, _) = ColorUtil.ToHsv(c);
        return s < 0.12 ? Color.FromRgb(0xE6, 0xE6, 0xE6) : ColorUtil.FromHsv(h, Math.Max(s, 0.5), 1.0);
    }

    private static Color Dark(Color c)
    {
        var (h, s, _) = ColorUtil.ToHsv(c);
        return s < 0.12 ? Color.FromRgb(0x40, 0x40, 0x40) : ColorUtil.FromHsv(h, Math.Max(s, 0.55), 0.45);
    }

    // =====================================================================================
    // compact
    // =====================================================================================

    private CompactMode DecideCompact()
    {
        if (_transient != CompactMode.None)
        {
            if (DateTime.UtcNow < _transientUntil) return _transient;
            _transient = CompactMode.None;
        }

        var c = S.ClaudeShowInCompact ? _claude.Active : null;
        bool hasMusic = _media.HasSession && !string.IsNullOrEmpty(_media.Title);

        if (hasMusic && _media.IsPlaying) return CompactMode.Music;
        if (c is { IsWorking: true }) return CompactMode.Claude;
        if (_focus.Running) return CompactMode.Focus;
        return CompactMode.None; // paused music => small pill with glowing eyes
    }

    private void ShowTransient(CompactMode mode, string text, string right, double seconds)
    {
        _transient = mode;
        _transientText = text;
        _transientRight = right;
        _transientUntil = DateTime.UtcNow.AddSeconds(seconds);
        _compactMode = CompactMode.None; // force reconfigure
        UpdateState();
    }

    private void ConfigureCompact()
    {
        var m = _compactMode;
        var c = _claude.Active;
        bool art = m is CompactMode.Music or CompactMode.MusicPaused;

        CompactArt.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactClaudeGlyph.Visibility = m == CompactMode.Claude || (art && c is { IsWorking: true } && S.ClaudeShowInCompact && !S.ClaudePod)
            ? Visibility.Visible : Visibility.Collapsed;
        CompactAlertDot.Visibility = m == CompactMode.ClaudeAttention || (c?.NeedsAttention == true && S.ClaudeShowInCompact && !(art && S.ClaudePod))
            ? Visibility.Visible : Visibility.Collapsed;

        CompactIcon.Visibility = m is CompactMode.ClaudeDone or CompactMode.Focus or CompactMode.FocusDone or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update
            ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Text = m switch
        {
            CompactMode.Battery => _batteryCharging ? "\uE945" : "\uE83F",
            CompactMode.BatteryLow => "\uE850",
            CompactMode.ClaudeDone => "\uE73E",
            CompactMode.Focus => "\uE916",
            CompactMode.FocusDone => "\uEA8F",
            CompactMode.Update => ((char)0xE896).ToString(),
            _ => ""
        };
        CompactIcon.Foreground = new SolidColorBrush(m switch
        {
            CompactMode.ClaudeDone => Color.FromRgb(0x5E, 0xD6, 0x7D),
            CompactMode.Focus or CompactMode.FocusDone => FocusColor,
            CompactMode.Battery => _batteryCharging ? Color.FromRgb(0x5E, 0xD6, 0x7D) : Colors.White,
            CompactMode.BatteryLow => Color.FromRgb(0xFF, 0x5F, 0x57),
            CompactMode.Update => UpdateColor,
            _ => Colors.White
        });
        CompactRight.Foreground = new SolidColorBrush(m switch
        {
            CompactMode.Battery when _batteryCharging => Color.FromRgb(0x5E, 0xD6, 0x7D),
            CompactMode.BatteryLow => Color.FromRgb(0xFF, 0x5F, 0x57),
            _ => Color.FromArgb(0xCC, 255, 255, 255)
        });

        bool eyes = m == CompactMode.MusicPaused;
        CompactEyes.Visibility = eyes ? Visibility.Visible : Visibility.Collapsed;
        CompactText.Visibility = eyes ? Visibility.Collapsed : Visibility.Visible;

        ShowCompactFace(m == CompactMode.Music && _compactInstrumental);

        CompactViz.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactRight.Visibility = art ? Visibility.Collapsed : Visibility.Visible;

        UpdateCompactText();
        UpdateCompactRight();
        FitCompactText();
        ApplyGlowEffects();
    }

    private void UpdateCompactText()
    {
        var c = _claude.Active;
        string text = _compactMode switch
        {
            CompactMode.Music => _musicCompactText,
            CompactMode.Claude when c != null => ClaudeShortStatus(c),
            CompactMode.ClaudeDone or CompactMode.ClaudeAttention or CompactMode.FocusDone or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update => _transientText,
            CompactMode.Focus => _focus.Mode == FocusMode.Focus ? Loc.T("focus.focusing") : Loc.T("focus.onbreak"),
            _ => ""
        };
        SetCompactText(text);
    }

    private void UpdateCompactRight()
    {
        var c = _claude.Active;
        CompactRight.Text = _compactMode switch
        {
            CompactMode.Claude when c != null => FormatTimer(c.Elapsed),
            CompactMode.Focus => FormatTimer(_focus.Remaining),
            CompactMode.ClaudeDone or CompactMode.FocusDone or CompactMode.ClaudeAttention or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update => _transientRight,
            _ => ""
        };
        if (_compactMode == CompactMode.Claude && c != null) SetCompactText(ClaudeShortStatus(c));
    }

    private void SetCompactText(string text)
    {
        if (text == _compactShown) return;
        _compactShown = text;
        CompactText.Text = text;
        SlideIn(CompactText);
        FitCompactText();
    }

    // ---- long lyric lines: grow the island, then slide the text if it still doesn't fit
    private const double MinMusicWidth = 340, MaxMusicWidth = 700, LyricExtraHeight = 22;
    private double _musicCompactWidth = MinMusicWidth;
    private bool _lyricTwoLines;

    private double MeasureText(TextBlock tb, string text) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private void FitCompactText()
    {
        double textW = MeasureText(CompactText, CompactText.Text);
        // everything around the text in the music layout: margins, cover art, visualizer
        const double musicChrome = 16 + 22 + 20 + 28 + 14 + 10;

        if (_compactMode == CompactMode.Music)
        {
            double want = Math.Clamp(Math.Ceiling(textW + musicChrome), MinMusicWidth, MaxMusicWidth);
            if (Math.Abs(want - _musicCompactWidth) > 2)
            {
                _musicCompactWidth = want;
                if (_state == IslandState.Compact) AnimateIslandSize(TargetSize(IslandState.Compact), bigMove: false);
            }
        }

        double available = CompactWidth(_compactMode) - (_compactMode == CompactMode.Music ? musicChrome - 10 : 100);
        double overflow = textW - available;
        var tt = CompactText.RenderTransform as TranslateTransform ?? new TranslateTransform();
        CompactText.RenderTransform = tt;
        if (overflow > 2)
        {
            CompactTextHost.HorizontalAlignment = HorizontalAlignment.Left;
            double slide = overflow / 45.0; // seconds, ~45 px/s
            var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4 + slide)), ease));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-overflow, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.8 + slide))));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.5 + slide)), ease));
            tt.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            CompactTextHost.HorizontalAlignment = HorizontalAlignment.Center;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
        }
    }

    /// <summary>On the Music tab a long line wraps to two lines and the island grows a little.</summary>
    private void FitExpandedLyric()
    {
        bool two = MeasureText(LyricCurrent, LyricCurrent.Text) > 440 - 2;
        if (two == _lyricTwoLines) return;
        _lyricTwoLines = two;
        MusicTab.Height = 186 + (two ? LyricExtraHeight : 0);
        if (_state == IslandState.Expanded && _tab == Tab.Music)
            AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
    }

    private void SlideIn(FrameworkElement el)
    {
        var tt = new TranslateTransform();
        el.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(Dur(300));
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, d) { EasingFunction = ease });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
    }

    // =====================================================================================
    // media
    // =====================================================================================

    private void OnMediaChanged()
    {
        bool has = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        TitleText.Text = has ? _media.Title : Loc.T("music.nothing");
        ArtistText.Text = has ? _media.Artist : Loc.T("music.hint");
        SourceText.Text = _media.SourceName;
        SourceButton.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;

        HomeMusicTitle.Text = has ? _media.Title : Loc.T("music.nothing.short");
        HomeMusicSub.Text = has ? (_media.IsPlaying ? _media.Artist : Loc.T("music.paused")) : "—";
        HomeMusicPlay.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;

        Brush art = _media.Art != null
            ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        CompactArt.Background = art;
        ExpandedArt.Background = art;
        HomeMusicArt.Background = _media.Art != null ? art : new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255));

        _musicAccent = _media.ArtColor ?? ColorUtil.Fallback;
        var bright = new SolidColorBrush(Bright(_musicAccent));
        bright.Freeze();
        CompactViz.BarBrush = bright;
        UpdateEyeColor();
        if (_spTimer != null)
        {
            UpdateSpotifyControls();
            _ = PollSpotifyAsync();
        }
        ApplyGlowEffects();
        ExpandedViz.BarBrush = bright;
        var d = Dark(_musicAccent);
        HomeMusicCard.Background = new LinearGradientBrush(
            Color.FromRgb((byte)(d.R / 2), (byte)(d.G / 2), (byte)(d.B / 2)),
            Color.FromRgb((byte)Math.Min(255, d.R * 1.5), (byte)Math.Min(255, d.G * 1.5), (byte)Math.Min(255, d.B * 1.5)),
            new Point(0, 0), new Point(0.4, 1));
        if (_tab == Tab.Music)
        {
            UpdateHeader();
            ApplyGlow();
            MoveNavPill(false);
        }

        string key = has ? _media.Artist + "|" + _media.Title : "";
        if (key != _trackKey)
        {
            _trackKey = key;
            LoadLyrics();
        }
        UpdateState();
    }

    private void OnPlaybackChanged()
    {
        string glyph = _media.IsPlaying ? "\uE769" : "\uE768";
        PlayButton.Content = glyph;
        HomeMusicPlay.Content = glyph;
        UpdateEyeColor();
        bool has = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        HomeMusicSub.Text = has ? (_media.IsPlaying ? _media.Artist : Loc.T("music.paused")) : "—";
        UpdateState();
    }

    // =====================================================================================
    // lyrics
    // =====================================================================================

    private async void LoadLyrics()
    {
        _lyricsCts?.Cancel();
        _lines = null;
        _lineIdx = int.MinValue;
        _lyricsLoading = false;

        if (!S.LyricsEnabled || string.IsNullOrWhiteSpace(_media.Title)) return;

        var cts = new CancellationTokenSource();
        _lyricsCts = cts;
        _lyricsLoading = true;
        string title = _media.Title, artist = _media.Artist, album = _media.Album;
        try
        {
            await Task.Delay(400, cts.Token);
            var lines = await _lyrics.GetAsync(title, artist, album, _media.Duration, cts.Token);
            if (cts.IsCancellationRequested) return;
            _lines = lines;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        if (_lyricsCts == cts)
        {
            _lyricsLoading = false;
            _lineIdx = int.MinValue;
        }
    }

    private void UpdateLyrics(TimeSpan pos)
    {
        int idx = -1;
        if (_lines != null)
        {
            var t = pos + TimeSpan.FromMilliseconds(250);
            for (int i = 0; i < _lines.Count && _lines[i].Time <= t; i++) idx = i;
        }
        if (idx == _lineIdx) return;
        _lineIdx = idx;

        bool has = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        string fallback = has ? (string.IsNullOrEmpty(_media.Artist) ? _media.Title : $"{_media.Title} — {_media.Artist}") : "";
        string current, next, compact;

        if (!has)
        {
            current = next = compact = "";
        }
        else if (!S.LyricsEnabled)
        {
            current = "♪";
            next = Loc.T("lyrics.off");
            compact = fallback;
        }
        else if (_lines == null)
        {
            current = "♪";
            next = _lyricsLoading ? Loc.T("lyrics.loading") : Loc.T("lyrics.none");
            compact = fallback;
        }
        else
        {
            current = idx >= 0 ? LineText(idx) : "♪";
            next = idx + 1 < _lines.Count ? LineText(idx + 1) : "";
            compact = idx >= 0 && current != "♪" ? current : fallback;
        }

        // no line being sung right now (intro, solo, instrumental): little eyes swaying instead of the title
        bool instrumental = has && S.LyricsEnabled && _lines != null && !(idx >= 0 && current != "♪");
        if (instrumental) compact = "";
        if (instrumental != _compactInstrumental)
        {
            _compactInstrumental = instrumental;
            if (_compactMode == CompactMode.Music) ShowCompactFace(instrumental);
        }

        LyricNext.Text = next;
        if (current != _lyricShown)
        {
            _lyricShown = current;
            LyricCurrent.Text = current;
            SlideIn(LyricCurrent);
            FitExpandedLyric();
        }
        _musicCompactText = compact;
        if (_compactMode == CompactMode.Music) SetCompactText(compact);
    }

    private string LineText(int i) => string.IsNullOrWhiteSpace(_lines![i].Text) ? "♪" : _lines[i].Text;

    // =====================================================================================
    // Claude
    // =====================================================================================

    private void BuildSteps()
    {
        for (int i = 0; i < 4; i++)
        {
            ClaudeSteps.ColumnDefinitions.Add(new ColumnDefinition());
            var track = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
                Margin = new Thickness(i == 0 ? 0 : 4, 0, i == 3 ? 0 : 4, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            var fill = new Border
            {
                CornerRadius = new CornerRadius(1.5),
                Background = Brushes.White,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0, 1)
            };
            track.Child = fill;
            var label = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x77, 255, 255, 255)),
                Margin = new Thickness(i == 0 ? 0 : 4, 8, 0, 0)
            };
            var cell = new Grid();
            cell.Children.Add(track);
            cell.Children.Add(label);
            Grid.SetColumn(cell, i);
            ClaudeSteps.Children.Add(cell);
            _stepFills[i] = fill;
            _stepLabels[i] = label;
        }
    }

    private void OnClaudeChanged()
    {
        var c = _claude.Active;
        bool working = c is { IsWorking: true };
        HeaderClaudeDot.Visibility = working || c?.NeedsAttention == true ? Visibility.Visible : Visibility.Collapsed;
        HeaderClaudeDot.Fill = c?.NeedsAttention == true ? (Brush)FindResource("ClaudeOrange") : (Brush)FindResource("ClaudeOrange");

        // Idle eyes turn orange while Claude waits for you (the discreet indicator).
        UpdateEyeColor();

        if (_compactMode != CompactMode.None) ConfigureCompact();
        if (_tab == Tab.Claude) RefreshClaudeTab();
        UpdateHomeLive();
        UpdateState();
    }

    private void OnClaudeDone(ClaudeSession s)
    {
        string dur = FormatTimer(s.Elapsed);
        Happy();
        if (_podShown) PodDone();
        else ShowTransient(CompactMode.ClaudeDone, Loc.T("claude.done.compact"), dur, 5);
        SoundService.Play(Sfx.Done);
        if (S.ClaudeWindowsNotifications)
            App.Notify(Loc.T("claude.done.title"), $"{s.Project} · {dur}" + (s.Prompt.Length > 0 ? $"\n{s.Prompt}" : ""));
    }

    private void OnClaudeAttention(ClaudeSession s)
    {
        FaceReact(Mood.Worried, 3);
        if (!PodWanted(includeDone: false)) ShowTransient(CompactMode.ClaudeAttention, Loc.T("claude.attention.compact"), "", 6);
        SoundService.Play(Sfx.Attention);
        if (S.ClaudeWindowsNotifications)
            App.Notify(Loc.T("claude.attention.title"), s.AttentionMessage);
    }

    private string ClaudeShortStatus(ClaudeSession c)
    {
        if (c.NeedsAttention) return Loc.T("claude.attention.compact");
        string detail = c.Detail;
        return c.Phase switch
        {
            ClaudePhase.Thinking => Loc.T("claude.thinking"),
            ClaudePhase.Reading => Loc.F("claude.reading", detail),
            ClaudePhase.Editing => Loc.F("claude.editing", detail),
            ClaudePhase.Testing => Loc.T("claude.testing"),
            ClaudePhase.Running => Loc.F("claude.running", detail),
            ClaudePhase.Done => Loc.T("claude.finished"),
            _ => Loc.T("claude.idle")
        };
    }

    private string ClaudeLongStatus(ClaudeSession c)
    {
        if (c.NeedsAttention)
            return Loc.T("claude.attention.compact") + (c.AttentionMessage.Length > 0 ? " · " + c.AttentionMessage : "");
        string phase = c.Phase switch
        {
            ClaudePhase.Thinking => Loc.T("claude.thinking"),
            ClaudePhase.Reading => Loc.T("claude.phase.reading"),
            ClaudePhase.Editing => Loc.T("claude.phase.editing"),
            ClaudePhase.Testing => Loc.T("claude.testing"),
            ClaudePhase.Running => Loc.T("claude.phase.running"),
            ClaudePhase.Done => Loc.T("claude.finished"),
            _ => Loc.T("claude.idle")
        };
        if (c.StepCount > 0 && c.Phase != ClaudePhase.Done)
            phase += " · " + Loc.F("claude.stepof", c.StepIndex, c.StepCount);
        return phase;
    }

    private void RefreshClaudeTab()
    {
        var c = _claude.Active;
        bool showActive = c != null && (c.IsWorking || c.NeedsAttention ||
                                         (c.TaskEnd != null && (DateTime.Now - c.TaskEnd.Value).TotalMinutes < 3));

        ClaudeActive.Visibility = showActive ? Visibility.Visible : Visibility.Collapsed;
        ClaudeIdle.Visibility = showActive ? Visibility.Collapsed : Visibility.Visible;

        if (!showActive || c == null)
        {
            int jobs = _claude.JobsToday;
            ClaudeJobsBig.Text = jobs.ToString();
            ClaudeJobsSub.Text = jobs == 1 ? Loc.T("claude.jobtoday") : Loc.T("claude.jobstoday");
            bool connected = ClaudeHooks.IsInstalled;
            ClaudeConnectButton.Visibility = connected && !ClaudeHooks.PointsElsewhere ? Visibility.Collapsed : Visibility.Visible;
            ClaudeConnectText.Text = connected ? Loc.T("claude.reconnect") : Loc.T("claude.connect");

            ClaudeRecentList.Children.Clear();
            if (_claude.Recent.Count == 0)
            {
                ClaudeRecentList.Children.Add(new TextBlock
                {
                    Text = connected ? Loc.T("claude.norecent") : Loc.T("claude.notconnected"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            foreach (var job in _claude.Recent.GetRange(0, Math.Min(3, _claude.Recent.Count)))
                ClaudeRecentList.Children.Add(RecentRow(job));
            return;
        }

        ClaudeProject.Text = c.Project;
        ClaudeStatus.Text = ClaudeLongStatus(c);
        ClaudeStatus.Foreground = c.NeedsAttention ? (Brush)FindResource("ClaudeOrange") : new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));
        UpdateClaudeTimer();

        var edit = c.LastEdit;
        ClaudeFileCard.Visibility = edit == null ? Visibility.Collapsed : Visibility.Visible;
        if (edit != null)
        {
            ClaudeLang.Text = edit.Lang;
            ClaudeFile.Text = edit.FileName;
            ClaudeFileState.Text = c.IsWorking && c.Phase == ClaudePhase.Editing ? Loc.T("claude.beingedited") : Loc.T("claude.edited");
            ClaudeDiff.Children.Clear();
            for (int i = 0; i < edit.Lines.Length; i++) ClaudeDiff.Children.Add(DiffRow(i + 1, edit.Lines[i]));
        }

        // Read / Edit / Test / Done
        ClaudePhase[] phases = { ClaudePhase.Reading, ClaudePhase.Editing, ClaudePhase.Testing, ClaudePhase.Done };
        for (int i = 0; i < 4; i++)
        {
            bool seen = c.PhasesSeen.Contains(phases[i]);
            bool current = c.IsWorking && (c.Phase == phases[i] || (phases[i] == ClaudePhase.Reading && c.Phase == ClaudePhase.Running));
            double target = current ? 0.55 : seen ? 1 : 0;
            var scale = (ScaleTransform)_stepFills[i].RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(Dur(450))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _stepFills[i].Background = phases[i] == ClaudePhase.Done && seen
                ? new SolidColorBrush(Color.FromRgb(0x5E, 0xD6, 0x7D))
                : Brushes.White;
            if (phases[i] == ClaudePhase.Done && seen) Glow(_stepFills[i], Color.FromRgb(0x5E, 0xD6, 0x7D)); else NoGlow(_stepFills[i]);
            if (current)
                _stepFills[i].BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else
            {
                _stepFills[i].BeginAnimation(OpacityProperty, null);
                _stepFills[i].Opacity = 1;
            }
            _stepLabels[i].Foreground = new SolidColorBrush(current || seen ? Colors.White : Color.FromArgb(0x66, 255, 255, 255));
        }
    }

    private void UpdateClaudeTimer()
    {
        var c = _claude.Active;
        if (c != null) ClaudeTimer.Text = FormatTimer(c.Elapsed);
    }

    private static UIElement DiffRow(int n, string code)
    {
        var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var num = new TextBlock { Text = n.ToString(), FontFamily = mono, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), TextAlignment = TextAlignment.Right };
        var plus = new TextBlock { Text = "+", FontFamily = mono, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0xD6, 0x7D)), Margin = new Thickness(4, 0, 0, 0) };
        var text = new TextBlock { FontFamily = mono, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis };
        foreach (var run in SyntaxRuns(code)) text.Inlines.Add(run);
        Grid.SetColumn(plus, 1);
        Grid.SetColumn(text, 2);
        g.Children.Add(num);
        g.Children.Add(plus);
        g.Children.Add(text);
        return g;
    }

    /// <summary>Tiny highlighter: strings orange, tags/keywords blue, the rest light.</summary>
    private static IEnumerable<System.Windows.Documents.Run> SyntaxRuns(string code)
    {
        var plain = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7));
        var str = new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x6E));
        var kw = new SolidColorBrush(Color.FromRgb(0x6C, 0xC6, 0xFF));
        var matches = System.Text.RegularExpressions.Regex.Matches(code,
            "(\"[^\"]*\"?|'[^']*'?)|(</?[A-Za-z][\\w:.]*|\\b(?:public|private|protected|internal|class|void|var|const|let|function|return|if|else|for|foreach|while|new|using|import|from|export|static|async|await|def|string|int|bool|true|false|null)\\b)");
        int last = 0;
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (m.Index > last) yield return new System.Windows.Documents.Run(code[last..m.Index]) { Foreground = plain };
            yield return new System.Windows.Documents.Run(m.Value) { Foreground = m.Groups[1].Success ? str : kw };
            last = m.Index + m.Length;
        }
        if (last < code.Length) yield return new System.Windows.Documents.Run(code[last..]) { Foreground = plain };
    }

    private UIElement RecentRow(ClaudeJob job)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var check = new Grid { Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0) };
        check.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)) });
        check.Children.Add(new TextBlock
        {
            Text = "\uE73E",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 8,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        var text = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(job.Prompt) ? job.Project : job.Prompt,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var time = new TextBlock
        {
            Text = FormatClock(job.Finished),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(time, 2);
        g.Children.Add(check);
        g.Children.Add(text);
        g.Children.Add(time);
        return g;
    }

    private void ClaudeConnect_Click(object sender, RoutedEventArgs e) => App.OpenSettings("claude");

    /// <summary>Plays a fake 8-second Claude task (from the settings "Simulate" button).</summary>
    public async void SimulateClaudeTask()
    {
        const string sid = "\"session_id\":\"notchy-demo\",\"cwd\":\"C:\\\\Projetos\\\\Notchy\"";
        void Send(string body) => _claude.Inject("{" + sid + "," + body + "}");

        Send("\"hook_event_name\":\"UserPromptSubmit\",\"prompt\":\"Demonstração do Notchy\"");
        await Task.Delay(1200);
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"in_progress\"},{\"status\":\"pending\"},{\"status\":\"pending\"}]}");
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:\\\\Projetos\\\\Notchy\\\\App.xaml\"}");
        await Task.Delay(1600);
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"completed\"},{\"status\":\"in_progress\"},{\"status\":\"pending\"}]}");
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:\\\\Projetos\\\\Notchy\\\\Island.cs\",\"new_string\":\"public class Island\\n{\\n    public string Name = \\\"Notchy\\\";\"}}");
        await Task.Delay(2200);
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"completed\"},{\"status\":\"completed\"},{\"status\":\"in_progress\"}]}");
        Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet build\"}");
        await Task.Delay(2200);
        Send("\"hook_event_name\":\"Stop\"");
    }

    // =====================================================================================
    // focus
    // =====================================================================================

    private void OnFocusFinished(FocusMode finished)
    {
        SoundService.Play(Sfx.FocusEnd);
        Happy();
        ShowTransient(CompactMode.FocusDone,
            finished == FocusMode.Focus ? Loc.T("focus.done") : Loc.T("focus.breakdone"), "", 6);
        if (S.ClaudeWindowsNotifications)
            App.Notify("Focus", finished == FocusMode.Focus ? Loc.T("focus.done") : Loc.T("focus.breakdone"));
        UpdateFocusUi();
    }

    private void UpdateFocusUi()
    {
        string time = FormatTimer(_focus.Remaining);
        FocusTime.Text = time;
        HomeFocusBig.Text = time;
        string sub = _focus.Running
            ? (_focus.Mode == FocusMode.Focus ? Loc.T("focus.focusing") : Loc.T("focus.onbreak"))
            : _focus.Started ? Loc.T("focus.paused") : Loc.T("focus.ready");
        if (_focus.CompletedToday > 0 && !_focus.Running) sub += " · " + Loc.F("focus.today", _focus.CompletedToday);
        FocusSub.Text = sub;
        HomeFocusSub.Text = sub;
        string glyph = _focus.Running ? "\uE769" : "\uE768";
        FocusPlayButton.Content = glyph;
        HomeFocusPlay.Content = glyph;

        bool focusMode = _focus.Mode == FocusMode.Focus;
        FocusModeFocus.Background = new SolidColorBrush(focusMode ? Color.FromArgb(0x40, FocusColor.R, FocusColor.G, FocusColor.B) : Colors.Transparent);
        FocusModeBreak.Background = new SolidColorBrush(!focusMode ? Color.FromArgb(0x40, 0x5E, 0xD6, 0x7D) : Colors.Transparent);
        FocusModeFocusText.Foreground = new SolidColorBrush(focusMode ? FocusColor : Color.FromArgb(0x88, 255, 255, 255));
        FocusModeBreakText.Foreground = new SolidColorBrush(!focusMode ? Color.FromRgb(0x5E, 0xD6, 0x7D) : Color.FromArgb(0x88, 255, 255, 255));
        FocusProgress.Background = new SolidColorBrush(focusMode ? FocusColor : Color.FromRgb(0x5E, 0xD6, 0x7D));
        FocusProgressScale.ScaleX = _focus.Progress;
        if (FocusProgress.Background is SolidColorBrush fb) Glow(FocusProgress, fb.Color);
    }

    private void FocusPlay_Click(object sender, RoutedEventArgs e)
    {
        _focus.Toggle();
        UpdateFocusUi();
        e.Handled = true;
        UpdateState();
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focus.Reset();
        UpdateFocusUi();
    }

    private void FocusSkip_Click(object sender, RoutedEventArgs e)
    {
        _focus.Skip();
        UpdateFocusUi();
    }

    // =====================================================================================
    // home
    // =====================================================================================

    private void UpdateHomeLive()
    {
        var c = _claude.Active;
        if (c is { IsWorking: true })
        {
            HomeClaudeBig.Text = FormatTimer(c.Elapsed);
            HomeClaudeSub.Text = ClaudeShortStatus(c);
            if (!HomeClaudeSpin.HasAnimatedProperties)
                HomeClaudeSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.4)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            int jobs = _claude.JobsToday;
            HomeClaudeBig.Text = jobs.ToString();
            HomeClaudeSub.Text = c?.NeedsAttention == true ? Loc.T("claude.attention.compact")
                : jobs == 1 ? Loc.T("claude.jobtoday") : Loc.T("claude.jobstoday");
            HomeClaudeSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            HomeClaudeSpin.Angle = 0;
        }
    }

    private void HomeClaude_Click(object sender, RoutedEventArgs e) { _userTab = Tab.Claude; SelectTab(Tab.Claude, true); }
    private void HomeMusic_Click(object sender, RoutedEventArgs e) { _userTab = Tab.Music; SelectTab(Tab.Music, true); }
    private void HomeFocus_Click(object sender, RoutedEventArgs e) { _userTab = Tab.Focus; SelectTab(Tab.Focus, true); }

    // =====================================================================================
    // volume
    // =====================================================================================

    private void PollVolume()
    {
        if (!_audio.TryGet(out float vol, out bool mute)) return;

        bool changed = _lastVol >= 0 && (Math.Abs(vol - _lastVol) > 0.004 || mute != _lastMute);
        if (changed && S.VolumeOsd && !VolumeSlider.IsDragging && _state != IslandState.Expanded)
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.6);

        _lastVol = vol;
        _lastMute = mute;

        double shown = mute ? 0 : vol;
        if (!VolOsdBar.IsDragging)
        {
            VolOsdBar.Value = shown;
            VolOsdText.Text = mute ? "0" : Math.Round(vol * 100).ToString();
            VolOsdIcon.Text = OutputGlyph(vol, mute);
        }

        // Music tab: the slider controls only the playing app (Spotify...), like the Volume Mixer.
        // With the Spotify integration, the slider shows Spotify's own volume instead (PollSpotifyAsync).
        if (SpotifyActive && _spState is { SupportsVolume: true }) return;
        string app = _media.SourceProcess;
        bool appVol = _audio.TryGetApp(app, out float av, out bool am);
        if (!appVol)
        {
            av = vol;
            am = mute;
        }
        if (!VolumeSlider.IsDragging) VolumeSlider.Value = am ? 0 : av;
        MuteButton.Content = VolumeGlyph(av, am);
        MuteButton.ToolTip = appVol ? Loc.F("music.appvolume", _media.SourceName) : null;
    }

    private static string VolumeGlyph(float vol, bool mute) =>
        ((char)(mute || vol <= 0.001 ? 0xE74F : vol < 0.34 ? 0xE993 : vol < 0.67 ? 0xE994 : 0xE995)).ToString();

    /// <summary>Music tab slider: the playing app's own volume when it has an audio session, else the system's.</summary>
    private void SetMusicVolume(double value)
    {
        if (TrySetSpotifyVolume(value)) return; // Spotify's own volume bar (Web API)
        string app = _media.SourceProcess;
        if (_audio.TryGetApp(app, out _, out _)) _audio.SetApp(app, (float)value);
        else SetVolume(value);
    }

    private void SetVolume(double value)
    {
        _audio.SetVolume((float)value);
        _lastVol = (float)value;
        _lastMute = false;
    }

    // =====================================================================================
    // input
    // =====================================================================================

    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        RememberForeground();
        if (_chatPinned && !_draggingEyes)
        {
            // hand over from "pinned after a capture" to "mouse is on it" with no gap, so it doesn't blink shut
            if (S.Open == OpenMode.Hover) _hoverActive = true;
            else _clickOpened = true;
            UnpinChat();
        }
        // the volume pop-up stays a pop-up under the mouse (output menu, drag the bar) instead of opening the island
        if (_state == IslandState.Volume && _forcedState == null)
        {
            _volHover = true;
            return;
        }
        _hoverRequested = true;
        _hoverTimer.Stop();
        if (S.Open == OpenMode.Click) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, S.HoverDelayMs));
        _hoverTimer.Start();
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_volHover)
        {
            _volHover = false;
            if (_outMenuOpen?.IsOpen != true) _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.5);
        }
        _hoverRequested = false;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(450);
        _hoverTimer.Start();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state == IslandState.Expanded) return;
        if (_state == IslandState.Volume)
        {
            // the speaker and the bar are controls; anywhere else on the pop-up opens the island
            if (e.OriginalSource is DependencyObject src && (IsInside(src, VolOsdIconBtn) || IsInside(src, VolOsdBar) || VolOsdBar.IsDragging)) return;
            _volHover = false;
            _volumeOsdUntil = DateTime.MinValue;
            _hoverRequested = true;
        }
        if (S.Open == OpenMode.Click)
        {
            _clickOpened = true;
            UpdateState();
        }
        else
        {
            _hoverTimer.Stop();
            _hoverActive = true;
            UpdateState();
        }
    }

    private void Collapse()
    {
        _hoverActive = false;
        _clickOpened = false;
        _forcedState = null;
        UpdateState();
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        await _media.TogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
    private void Source_Click(object sender, RoutedEventArgs e)
    {
        if (SpotifyActive) _ = ShowDeviceMenuAsync();
        else _media.ActivateSourceApp();
    }
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (TryToggleSpotifyMute()) return;
        string app = _media.SourceProcess;
        if (_audio.TryGetApp(app, out _, out _)) _audio.ToggleAppMute(app);
        else _audio.ToggleMute();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _menu.Items)
            if (item is MenuItem mi && mi.Tag is string key) mi.Header = Loc.T(key);
        _menu.PlacementTarget = MoreButton;
        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        _menu.HorizontalOffset = -130;
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string key, string glyph, Action action, Brush? color = null)
        {
            var mi = new MenuItem { Tag = key, Header = Loc.T(key), Icon = new TextBlock { Text = glyph, FontSize = 12 } };
            if (color != null) mi.Foreground = color;
            mi.Click += (_, _) => action();
            return mi;
        }

        menu.Items.Add(Item("menu.settings", "\uE713", () => App.OpenSettings()));
        menu.Items.Add(Item("menu.foldin", "\uE70E", Collapse));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("menu.quit", "\uE7E8", () => Application.Current.Shutdown(), new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B))));

        menu.Opened += (_, _) => RaisePopup(menu);
        menu.Closed += (_, _) =>
        {
            // While the menu was open it held the mouse, so WPF thinks the pointer left the island.
            // Ask where the cursor really is instead, so the island doesn't close and re-open.
            bool over = CursorOverIsland();
            _hoverTimer.Stop();
            _hoverRequested = over;
            _hoverActive = over && S.Open == OpenMode.Hover;
            if (over && S.Open == OpenMode.Click) _clickOpened = true;
            if (!over) _clickOpened = false;
            UpdateState();
        };
        return menu;
    }

    /// <summary>True if the mouse cursor is really over the visible island right now.</summary>
    private bool CursorOverIsland()
    {
        try
        {
            Native.GetCursorPos(out var p);
            var local = Island.PointFromScreen(new Point(p.X, p.Y));
            return local.X >= 0 && local.X <= Island.ActualWidth &&
                   local.Y >= _topHidden - 1 && local.Y <= Island.ActualHeight + 2;
        }
        catch
        {
            return Island.IsMouseOver;
        }
    }

    // =====================================================================================
    // helpers
    // =====================================================================================

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private static string FormatTimer(TimeSpan t) => Format(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, t.TotalSeconds) - 0.0001)));

    // =====================================================================================
    // debug snapshot  (Notchy.exe --snapshot <folder>)
    // =====================================================================================

    private IslandState? _forcedState;

    public async Task SnapshotAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        await Task.Delay(2500);

        // fake a Claude task so the Claude views have content
        _claude.Inject("""{"hook_event_name":"UserPromptSubmit","session_id":"demo","cwd":"C:\\proj\\Notchy","prompt":"Adicionar abas e sons"}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","cwd":"C:\\proj\\Notchy","tool_name":"Read","tool_input":{"file_path":"C:\\proj\\Notchy\\App.xaml"}}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","tool_name":"TodoWrite","tool_input":{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"},{"content":"c","status":"pending"},{"content":"d","status":"pending"}]}}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","tool_name":"Edit","tool_input":{"file_path":"C:\\proj\\Notchy\\MainWindow.xaml","old_string":"x","new_string":"<Window x:Class=\"Notchy.MainWindow\"\n        xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n        Title=\"Notchy\">"}}""");
        await Task.Delay(300);

        async Task Shot(string name)
        {
            await Task.Delay((int)Dur(900));
            var root = (FrameworkElement)Content;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x2A, 0x86)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            rtb.Render(dv);
            rtb.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
            enc.Save(fs);
        }

        _forcedState = IslandState.Compact;
        UpdateState();
        await Shot("1_compact");

        _forcedState = IslandState.Expanded;
        foreach (Tab t in Enum.GetValues(typeof(Tab)))
        {
            UpdateState();
            SelectTab(t, true);
            await Shot("2_tab_" + t);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "face.txt"),
                $"{t}: tab={_tab} wave={_headerWave.Visibility} faceColor={_face.Color} state={_state}" + Environment.NewLine);
        }

        _claude.Inject("""{"hook_event_name":"Notification","session_id":"demo","message":"Claude needs your permission to use Bash"}""");
        _forcedState = null;
        UpdateState();
        await Shot("3_attention");

        _claude.Inject("""{"hook_event_name":"Stop","session_id":"demo"}""");
        await Shot("4_done");

        _forcedState = IslandState.Expanded;
        UpdateState();
        SelectTab(Tab.Claude, true);
        await Shot("5_claude_after");

        if (Environment.GetEnvironmentVariable("NOTCHY_CHATTEST") == "1")
        {
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Chat, false);
            SetContext(new CapturedText("A ilha dinâmica é a área em forma de pílula no topo da tela que mostra música, timers e alertas.", "Teste", IntPtr.Zero, true));
            await Shot("7_chat_context");
            RunChatAction("explain", "");
            await Task.Delay(2500);
            await Shot("7_chat_streaming");
            for (int i = 0; i < 60 && _runner!.Busy; i++) await Task.Delay(500);
            await Shot("7_chat_answer");
        }

        var sw = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100 };
        sw.Show();
        foreach (var page in new[] { "general", "island", "sounds", "claude", "spotify" })
        {
            sw.ShowPage(page);
            await Task.Delay(700);
            var root = (FrameworkElement)sw.Content;
            var bounds = VisualTreeHelper.GetDescendantBounds(root);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)sw.ActualWidth, (int)sw.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var host = new DrawingVisual();
            using (var dc = host.RenderOpen())
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, sw.ActualWidth, sw.ActualHeight));
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var fs = System.IO.File.Create(System.IO.Path.Combine(folder, "6_settings_" + page + ".png"));
            enc.Save(fs);
        }

        Application.Current.Shutdown();
    }
}
