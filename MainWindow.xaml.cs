using System;
using System.Linq;
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
    private enum Tab { Home, Claude, Music, Chat, Focus, Clock, Shelf, Downloads, News }
    private enum CompactMode { None, Music, MusicPaused, Claude, ClaudeDone, ClaudeAttention, Focus, FocusDone, Battery, BatteryLow, Update, Transfer, TransferDone, TransferFailed, WhatsNew, Timer, TimerRing, Buds, BudsLow, BudsOff, Notice }

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
    /// <summary>A new song shows its name on the compact island until then (unless a lyric line starts first).</summary>
    private DateTime _titleUntil;
    private bool _titleWindowShown;
    private const double TitleSeconds = 5;
    private CancellationTokenSource? _lyricsCts;
    private string _compactShown = "\0";
    private string _lyricShown = "\0";
    private string _musicCompactText = "";
    private string _musicTitleText = ""; // "title — artist": shown instead of the eyes while a bubble has eyes

    private readonly Dictionary<Tab, (Button button, TextBlock label)> _nav = new();
    private readonly Dictionary<Tab, FrameworkElement> _navContent = new(); // icon + name (glows when selected)
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
        InitFileSelect();
        InitAlarmHint();
        InitExtras();
        InitChat();
        InitSpotify();
        InitFace();
        InitAudioOut();
        InitUpdates();
        InitCardGlow();
        InitCardTilt();
        InitTransfers();
        InitClaudeFace();
        InitShelfFace();
        InitWhatsNew();
        InitFocusTab();
        InitClockTab();
        InitClockRing();
        InitVolumeIsland();
        InitSubPages();
        InitBuds();
        InitWeather();
        InitNotices();
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
            if (!_hoverRequested && CursorInKeepOpenArea())
            {
                // a tab switch made the island smaller under a still mouse: wait until it really moves away
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(120);
                _hoverTimer.Start();
                return;
            }
            bool dragging = ProgressSlider.IsDragging || VolumeSlider.IsDragging;
            if (S.Open == OpenMode.Hover) _hoverActive = _hoverRequested || dragging;
            if (!_hoverRequested && !dragging) _clickOpened = false;
            UpdateState();
        };

        // the real frame loop follows the screen (StartFrameLoop); this only covers when nothing renders
        _frame = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _frame.Tick += (_, _) => FrameFallback();

        AppSettings.Changed += ApplySettings;

        ApplySettings();
        OnMediaChanged();
        OnPlaybackChanged();
        OnClaudeChanged();
        SelectTab(Tab.Home, animate: false);

        Loaded += async (_, _) =>
        {
            PositionWindow();
            if (S.IntroAnimation) PlayIntro();
            else UpdateState();
            _frame.Start();
            StartFrameLoop();
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

    /// <summary>
    /// Audio level for the waveforms and the dancing eyes: only the app playing the media (Spotify, the
    /// browser, a video player...), not everything else that makes sound. Read once per frame.
    /// </summary>
    private float MusicPeak()
    {
        if (_peakTick == _tick) return _peakValue;
        _peakTick = _tick;
        _peakValue = _media.HasSession ? _audio.GetSourcePeak(_media.SourceAppId, out _) : _audio.GetPeak();
        return _peakValue;
    }
    private long _peakTick = -1;
    private float _peakValue;

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
        DownloadPod.Background = Island.Background;
        PodContent.Margin = new Thickness(0, _topHidden, 0, 0);
        ApplyExtrasSettings();
        if (_nav.Count > 0) ApplyGlowEffects();

        if (!S.GameMode && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Island.Visibility = Visibility.Visible;
        }

        _focus.Refresh();
        if (_weather.PlaceChanged) _ = _weather.RefreshAsync(); // the weather's place was changed in the settings
        if (_bell != null) UpdateBell();
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
                Tab.Downloads => Loc.T("tab.downloads"),
                Tab.Chat => "Notchy",
                Tab.Clock => Loc.T("tab.clock"),
                _ => Loc.T("tab.focus")
            };
        ShelfEmptyText.Text = Loc.T("shelf.empty");
        ShelfHint.Text = Loc.T("shelf.hint");
        ShelfClearText.Text = Loc.T("shelf.clear");
        string[] steps = { Loc.T("claude.step.read"), Loc.T("claude.step.edit"), Loc.T("claude.step.test"), Loc.T("claude.step.done") };
        for (int i = 0; i < 4; i++) if (_stepLabels[i] != null) _stepLabels[i].Text = steps[i];

        HomeMusicLabel.Text = Loc.T("tab.music");
        HomeFocusLabel.Text = Loc.T("tab.focus");
        ClaudeRecentTitle.Text = Loc.T("claude.recent").ToUpperInvariant();
        ApplyFocusTexts();
        ApplyClockTexts();
        ApplySubPageTexts();
        UpdateSelChrome();
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

    private CompactMode _leftMode = CompactMode.None; // what the minimized island showed before its last change
    private DateTime _leftAt;

    /// <summary>The minimized island was showing that kind of mode a moment ago (and now shows the music).</summary>
    private bool IslandJustLeft(Func<CompactMode, bool> kind) =>
        _state == IslandState.Compact && _compactMode == CompactMode.Music && kind(_leftMode)
        && (DateTime.UtcNow - _leftAt).TotalMilliseconds < 400 && !_introActive;

    private void UpdateState()
    {
        if (_introActive) return; // the opening animation hands over when it ends
        IslandState next;
        bool ringing = _ring != null && !_hiddenForFullscreen; // a timer/alarm calling you takes the island
        // a notification card stays a card under the mouse (so you can click it), instead of opening the island
        ringing |= _noticeShown != null && _transient == CompactMode.Notice && DateTime.UtcNow < _transientUntil;
        if (_forcedState is { } forced) next = forced;
        else if (!ringing && _hoverActive || _clickOpened || _menu.IsOpen || _devicesMenuOpen?.IsOpen == true || _dragOver || _lassoing || ChatKeepsOpen) next = IslandState.Expanded;
        else if (!ringing && DateTime.UtcNow < _volumeOsdUntil || !ringing && (_state == IslandState.Volume && (_volHover || _outMenuOpen?.IsOpen == true)))
            next = IslandState.Volume;
        else
        {
            var mode = DecideCompact();
            if (mode != _compactMode)
            {
                if (_state == IslandState.Compact) (_leftMode, _leftAt) = (_compactMode, DateTime.UtcNow);
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
        IslandState.Idle => new Size(IdleWidth, 30),
        IslandState.Volume => LevelSize(),
        IslandState.Expanded => new Size(480, _subOpen ? SubPageHeight : TabHeight(_tab)),
        _ => new Size(CompactWidth(_compactMode), _compactMode == CompactMode.TimerRing ? RingHeight : _compactMode == CompactMode.Notice ? NoticeHeight : IsTransferMode(_compactMode) ? TransferHeight : 34)
    };

    private double TabHeight(Tab t) => 34 + 40 + 2 + t switch
    {
        Tab.Home => 118,
        Tab.Claude => 150,
        Tab.Music => 186 + (_lyricTwoLines ? LyricExtraHeight : 0),
        Tab.Shelf => 118,
        Tab.Downloads => _dlTabHeight,
        Tab.Focus => 150,
        Tab.Clock => 150,
        Tab.News => 186,
        Tab.Chat => ChatContentHeight,
        _ => 128
    };

    private double CompactWidth(CompactMode m) => m switch
    {
        CompactMode.Music => _musicCompactWidth,
        CompactMode.MusicPaused => 300,
        CompactMode.Claude or CompactMode.ClaudeDone or CompactMode.ClaudeAttention => ClaudeIslandWidth,
        CompactMode.Focus or CompactMode.Timer => 190,
        CompactMode.TimerRing => RingWidth,
        CompactMode.Buds or CompactMode.BudsLow => BudsWidth,
        CompactMode.BudsOff => BudsOffWidth,
        CompactMode.Notice => NoticeWidth,
        CompactMode.WhatsNew => WhatsNewWidth(),
        CompactMode.Transfer or CompactMode.TransferDone or CompactMode.TransferFailed => TransferWidth,
        _ => 320
    };

    private void ApplyState(IslandState state, bool playSound = true)
    {
        var prev = _state;
        bool expanding = state == IslandState.Expanded;
        _state = state;
#if DEBUG
        if (Environment.GetEnvironmentVariable("NOTCHY_DEBUG") == "1")
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {prev} -> {state}" + Environment.NewLine);
#endif

        if (prev == IslandState.Expanded && !expanding) ResetFileSelection();
        if (expanding && prev != IslandState.Expanded)
        {
            Tab target;
            if (_whatsNewPending && !_dlOpenRequest && !_podOpenRequest) target = Tab.News; // the update's news first
            // a click on the download or Claude bubble opens that tab
            else if (S.TransferProgress && _dlOpenRequest) target = Tab.Downloads;
            else if (_podOpenRequest) target = Tab.Claude;
            // something playing: open on the player (downloads and Claude have their own bubbles meanwhile)
            else if (_media.HasSession && _media.IsPlaying) target = Tab.Music;
            else if (S.TransferProgress && _compactMode == CompactMode.Transfer && ActiveTransfer?.Kind == TransferKind.Download)
                target = Tab.Downloads;
            else if (_claude.Active?.NeedsAttention == true) target = Tab.Claude;
            else if (_userTab is Tab ut && (ut != Tab.Shelf || S.ShelfEnabled)) target = ut;
            else target = SmartTab();
            _podOpenRequest = false;
            _dlOpenRequest = false;
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
        Fade(CompactPanel, state == IslandState.Compact && !IsTransferMode(_compactMode) && !IsClaudeMode(_compactMode) && _compactMode is not (CompactMode.WhatsNew or CompactMode.TimerRing) && !IsBudsMode(_compactMode) && _compactMode != CompactMode.Notice);
        Fade(_budsPanel, state == IslandState.Compact && IsBudsMode(_compactMode));
        Fade(_noticePanel, state == IslandState.Compact && _compactMode == CompactMode.Notice);
        Fade(_ringPanel, state == IslandState.Compact && _compactMode == CompactMode.TimerRing);
        Fade(WhatsNewPanel, state == IslandState.Compact && _compactMode == CompactMode.WhatsNew);
        Fade(ClaudePanel, state == IslandState.Compact && IsClaudeMode(_compactMode));
        Fade(TransferPanel, state == IslandState.Compact && IsTransferMode(_compactMode));
        Fade(VolumePanel, state == IslandState.Volume);
        if (state != IslandState.Volume) SetDeviceListOpen(false);
        if (!expanding)
        {
            CloseSubPage(animate: false);
            SetMoreOpen(false);
        }
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
        if (_podEntering)
        {
            // the island is hiding while a bubble moves out of its place: it comes down when that's done
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, TimeSpan.Zero));
        }
        else
        {
            Island.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, duration) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height + _topHidden, duration) { EasingFunction = ease });
        }
        // The compact content takes its final width right away (so nothing is cut at the edges) and the
        // island animates around it. Animating the content's width too made long lyric lines re-layout on
        // every frame and shake.
        if (_state == IslandState.Compact)
        {
            CompactPanel.BeginAnimation(WidthProperty, null);
            CompactPanel.Width = size.Width;
            TransferPanel.Width = size.Width;
            ClaudePanel.Width = size.Width;
            _ringPanel.Width = size.Width;
            _budsPanel.Width = size.Width;
            _noticePanel.Width = size.Width;
        }
        if (_state == IslandState.Volume)
        {
            VolumePanel.Width = size.Width;
            VolumePanel.Height = size.Height;
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

    private void OnFrame()
    {
        BeginFrame();
        _focus.Tick();
        UpdateClockFrame();
        UpdateClockRingFrame();
        UpdateVolumeIsland();
        UpdateBudsFrame();
        UpdateNoticesFrame();
        var pos = _media.Position;

        if (_state == IslandState.Expanded)
        {
            switch (_tab)
            {
                case Tab.Music when !ProgressSlider.IsDragging:
                    var dur = _media.Duration;
                    ProgressSlider.Value = dur > TimeSpan.Zero ? pos.TotalMilliseconds / dur.TotalMilliseconds : 0;
                    ElapsedText.Text = Format(pos);
                    RemainingText.Text = dur > TimeSpan.Zero ? Format(dur) : ""; // the song's length
                    break;
                case Tab.Claude when _slowTick && _tick % 6 == 0:
                    UpdateClaudeTimer();
                    break;
            }
            if (_slowTick && _tick % 6 == 0)
            {
                UpdateFocusUi();
                if (_tab == Tab.Clock) UpdateClockUi();
                if (_tab == Tab.Home) UpdateHomeLive();
            }
        }

        UpdateLyrics(pos);

        if (_state == IslandState.Expanded) UpdateFace(MusicPeak());
        else if (_compactFaceOn) UpdateCompactFace();

        bool compactViz = _state == IslandState.Compact && CompactViz.Visibility == Visibility.Visible;
        bool expandedViz = _state == IslandState.Expanded && _tab == Tab.Music;
        if (_slowTick && _tick % 2 == 0)
        {
            float peak = MusicPeak();
            if (compactViz) CompactViz.Tick(peak, _media.IsPlaying, _audio.HasMeter);
            if (expandedViz) ExpandedViz.Tick(peak, _media.IsPlaying, _audio.HasMeter);
        }
        if (compactViz) CompactViz.Step();
        if (expandedViz) ExpandedViz.Step();

        if (_state == IslandState.Compact && _slowTick && _tick % 10 == 0) UpdateCompactRight();
        UpdateExtrasFrame();
        if (_slowTick && _tick % 30 == 0) PollTransfers();
        UpdateTransferLight();
        UpdateClaudeFace();
        if (_slowTick && _tick % 15 == 0 && IsTransferMode(_compactMode)) UpdateTransferPanel();
        UpdateTransferEyes();
        UpdateEyeTrip();
        UpdateShelfFace();
        UpdateNavActivity();
        UpdateFocusFrame();
        UpdateFocusPod();
        UpdateWhatsNewFrame();
        if (_state == IslandState.Expanded && _tab == Tab.Downloads) UpdateDownloadsTab(texts: _slowTick && _tick % 2 == 0);

        if (_slowTick && _tick % 6 == 0) PollVolume();
        if (_slowTick && _tick % 60 == 0)
        {
            UpdateClock();
            CheckFullscreen();
        }
        if (_slowTick && _tick % 180 == 0)
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
        AddNav(Tab.Clock, "\uE121");
        AddNav(Tab.Shelf, "\uE7B8");
        AddNav(Tab.Downloads, "\uE896");
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
        content.Children.Add(MakeNavIcon(tab, icon));
        content.Children.Add(label);

        var button = new Button { Style = (Style)FindResource("NavButton"), Content = content, Margin = new Thickness(2, 0, 2, 0) };
        button.Click += (_, _) =>
        {
            _userTab = tab;
            SelectTab(tab, animate: true);
        };
        NavBar.Children.Add(button);
        _nav[tab] = (button, label);
        _navContent[tab] = content;
    }

    private Tab SmartTab()
    {
        var c = _claude.Active;
        if (c != null && (c.NeedsAttention || c.IsWorking)) return Tab.Claude;
        if (_focus.Running) return Tab.Focus;
        if (_clock.AnyRunning) return Tab.Clock;
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
        Tab.Downloads => DownloadsTab,
        Tab.News => NewsTab,
        Tab.Clock => ClockTab,
        _ => FocusTab
    };

    private void SelectTab(Tab tab, bool animate)
    {
        CloseSubPage(animate: false);
        SetMoreOpen(false);
        var old = _tab;
        if (old != tab) ResetFileSelection();
        _tab = tab;
        UpdateSelChrome();
        if (old != tab && _state == IslandState.Expanded) RememberKeepOpenArea();
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
        if (tab == Tab.Clock)
        {
            // a timer under way: open on it; otherwise on the page you used last
            if (old != Tab.Clock && _clock.AnyRunning && _clockOnAlarms)
            {
                _clockOnAlarms = false;
                PlaceClockPages(animate: false);
            }
            if (_clockSel == null && old != Tab.Clock) _clockSel = _clock.Soonest;
            _clockSideKey = _alarmCardsKey = "";
            UpdateClockUi();
            if (_clockOnAlarms && old != Tab.Clock) ScrollToNextAlarm(instant: true);
        }
        else if (old == Tab.Clock) CloseAlarmEditor();
        if (tab == Tab.Home) UpdateHomeLive();
        if (tab == Tab.Music) _ = PollSpotifyAsync();
        if (tab == Tab.Downloads) UpdateDownloadsTab(force: true);
        if (tab == Tab.News) OnNewsShown();
        NavPill.Opacity = _nav.ContainsKey(tab) ? 1 : 0; // the news tab has no button in the menu

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
        Tab.Clock => TimerColor,
        Tab.Shelf => ShelfColor,
        Tab.Downloads => DoneGreen,
        Tab.Chat => ChatColor,
        Tab.News => NewsGold,
        _ => Colors.White
    };

    private void UpdateHeader()
    {
        (string a, string b) = _tab switch
        {
            Tab.Claude => ("Claude", " Code"),
            Tab.Music => ("Music", " Player"),
            Tab.Focus => (Loc.T("focus.head.a"), Loc.T("focus.head.b")),
            Tab.Shelf => ("File", " Shelf"),
            Tab.Downloads => ("Download", " Center"),
            Tab.Chat => ("Notchy", " Chat"),
            Tab.News => (Loc.T("news.title"), " " + WhatsNew.Version),
            _ => ("Notchy", "")
        };
        // Relógio: "Timer · Alarmes" in place of the title (MainWindow.Clock.cs)
        bool clock = _tab == Tab.Clock;
        ((FrameworkElement)HeaderA.Parent).Visibility = clock ? Visibility.Collapsed : Visibility.Visible;
        _clockHeader.Visibility = clock ? Visibility.Visible : Visibility.Collapsed;
        if (clock) UpdateClockHeader();
        HeaderA.Text = a;
        HeaderB.Text = HeaderBGlow.Text = b;
        HeaderB.Foreground = HeaderBGlow.Foreground = new SolidColorBrush(TabColor(_tab));
        if (_subOpen)
        {
            // a sub-page has its own "‹ title"
            ((FrameworkElement)HeaderA.Parent).Visibility = Visibility.Collapsed;
            _clockHeader.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyGlow()
    {
        Color dark = _tab switch
        {
            Tab.Claude => Color.FromRgb(0x6B, 0x33, 0x22),
            Tab.Focus => Color.FromRgb(0x3E, 0x2A, 0x86),
            Tab.Clock => Color.FromRgb(0x1C, 0x44, 0x66),
            Tab.Shelf => Color.FromRgb(0x1C, 0x4A, 0x40),
            Tab.Downloads => Color.FromRgb(0x1C, 0x4A, 0x2A),
            Tab.Chat => Color.FromRgb(0x4A, 0x2E, 0x12),
            Tab.News => Color.FromRgb(0x4A, 0x3A, 0x12),
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
        if (_ring != null) return CompactMode.TimerRing;
        if (_transient != CompactMode.None)
        {
            if (DateTime.UtcNow < _transientUntil) return _transient;
            _transient = CompactMode.None;
        }
        var mode = DecideCompactWithoutTransfer();
        // nothing else going on: the download / copy gets the whole compact island
        if (mode == CompactMode.None && ActiveTransfer != null) return CompactMode.Transfer;
        // nothing at all: an update's news, until they're seen
        if (mode == CompactMode.None && _whatsNewPending) return CompactMode.WhatsNew;
        return mode;
    }

    /// <summary>What the compact island would show if no download/copy were running.</summary>
    private CompactMode DecideCompactWithoutTransfer()
    {

        var c = S.ClaudeShowInCompact ? _claude.Active : null;
        bool hasMusic = _media.HasSession && !string.IsNullOrEmpty(_media.Title);

        if (hasMusic && _media.IsPlaying) return CompactMode.Music;
        if (c is { IsWorking: true }) return CompactMode.Claude;
        if (_focus.Running) return CompactMode.Focus;
        if (_clock.AnyRunning) return CompactMode.Timer;
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
        // Claude / download alone have their own panels: leave the generic one as it was while it fades out
        if (IsClaudeMode(m) || IsTransferMode(m) || m is CompactMode.WhatsNew or CompactMode.TimerRing or CompactMode.Notice || IsBudsMode(m))
        {
            if (IsTransferMode(m)) UpdateTransferPanel();
            ShowCompactFace(false);
            return;
        }
        var c = _claude.Active;
        bool art = m is CompactMode.Music or CompactMode.MusicPaused;

        CompactArt.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactClaudeGlyph.Visibility = m == CompactMode.Claude || (art && c is { IsWorking: true } && S.ClaudeShowInCompact && !S.ClaudePod)
            ? Visibility.Visible : Visibility.Collapsed;
        CompactAlertDot.Visibility = m == CompactMode.ClaudeAttention || (c?.NeedsAttention == true && S.ClaudeShowInCompact && !(art && S.ClaudePod))
            ? Visibility.Visible : Visibility.Collapsed;

        CompactFocusEyes.Visibility = m is CompactMode.Focus or CompactMode.Timer ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Visibility = m is CompactMode.ClaudeDone or CompactMode.FocusDone or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update
            or CompactMode.Transfer or CompactMode.TransferDone
            ? Visibility.Visible : Visibility.Collapsed;
        CompactIcon.Text = m switch
        {
            CompactMode.Battery => _batteryCharging ? "\uE945" : "\uE83F",
            CompactMode.BatteryLow => "\uE850",
            CompactMode.ClaudeDone => "\uE73E",
            CompactMode.Focus => "\uE916",
            CompactMode.FocusDone => "\uEA8F",
            CompactMode.Update => ((char)0xE896).ToString(),
            CompactMode.Transfer => ActiveTransfer?.Kind == TransferKind.Copy ? "\uE8C8" : "\uE896",
            CompactMode.TransferDone => "\uE73E",
            _ => ""
        };
        CompactIcon.Foreground = new SolidColorBrush(m switch
        {
            CompactMode.ClaudeDone => Color.FromRgb(0x5E, 0xD6, 0x7D),
            CompactMode.Focus or CompactMode.FocusDone => FocusColor,
            CompactMode.Battery => _batteryCharging ? Color.FromRgb(0x5E, 0xD6, 0x7D) : Colors.White,
            CompactMode.BatteryLow => Color.FromRgb(0xFF, 0x5F, 0x57),
            CompactMode.Update => UpdateColor,
            CompactMode.Transfer => DoneGreen,
            CompactMode.TransferDone => DoneGreen,
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

        ShowCompactFace(m == CompactMode.Music && _compactInstrumental && !_islandFaceBlocked);

        CompactViz.Visibility = art ? Visibility.Visible : Visibility.Collapsed;
        CompactRight.Visibility = art ? Visibility.Collapsed : Visibility.Visible;

        UpdateCompactText();
        UpdateCompactRight();
        UpdateAlarmHint(force: true);
        FitCompactText();
        if (IsTransferMode(m)) UpdateTransferPanel();
        ApplyGlowEffects();
    }

    private void UpdateCompactText()
    {
        var c = _claude.Active;
        string text = _compactMode switch
        {
            CompactMode.Music => MusicCompactShown,
            CompactMode.Claude when c != null => ClaudeShortStatus(c),
            CompactMode.ClaudeDone or CompactMode.ClaudeAttention or CompactMode.FocusDone or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update
                or CompactMode.TransferDone => _transientText,
            CompactMode.Focus => "", // the eyes say it
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
            CompactMode.Timer => FormatTimer(_clock.Soonest?.Remaining ?? TimeSpan.Zero),
            CompactMode.ClaudeDone or CompactMode.FocusDone or CompactMode.ClaudeAttention or CompactMode.Battery or CompactMode.BatteryLow or CompactMode.Update
                or CompactMode.TransferDone => _transientRight,
            CompactMode.Transfer when ActiveTransfer is { } t => (t.Progress is double tp ? $"{Math.Floor(tp * 100)}%" : ""),
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
        double musicChrome = 16 + 22 + 20 + 28 + 14 + 10 + CompactAlarmExtra;

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
            _titleUntil = has ? DateTime.UtcNow.AddSeconds(TitleSeconds) : default;
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
        bool has = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        bool titleWindow = has && DateTime.UtcNow < _titleUntil;
        if (idx == _lineIdx && titleWindow == _titleWindowShown) return;
        _lineIdx = idx;
        _titleWindowShown = titleWindow;

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
        // a new song first shows its name for a few seconds; a lyric line that starts earlier replaces it
        if (instrumental && titleWindow) instrumental = false;
        if (instrumental) compact = "";
        if (instrumental != _compactInstrumental)
        {
            _compactInstrumental = instrumental;
            if (_compactMode == CompactMode.Music) ShowCompactFace(instrumental && !_islandFaceBlocked);
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
        _musicTitleText = fallback;
        if (_compactMode == CompactMode.Music) SetCompactText(MusicCompactShown);
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
        if (!_claude.AnyWorking) NavDone(Tab.Claude); // the check only when Claude is really done (no other session busy)
        string dur = FormatTimer(s.Elapsed);
        Happy();
        ClaudeFaceDone();
        if (_podShown && _podKind == PodKind.Claude) PodDone();
        else ShowTransient(CompactMode.ClaudeDone, Loc.T("claude.done.compact"), dur, 3.2);
        SoundService.Play(Sfx.Done);
        if (S.ClaudeWindowsNotifications)
            App.Notify(Loc.T("claude.done.title"), $"{s.Project} · {dur}" + (s.Prompt.Length > 0 ? $"\n{s.Prompt}" : ""));
    }

    private void OnClaudeAttention(ClaudeSession s)
    {
        FaceReact(Mood.Worried, 3);
        if (!ClaudePodWanted(includeDone: false)) ShowTransient(CompactMode.ClaudeAttention, Loc.T("claude.attention.compact"), "", 6);
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

        // open where the session lives; a question waiting for permission takes the right side
        ClaudeOpenText.Text = Loc.T("claude.open");
        var ask = c.Ask;
        ClaudeAskCard.Visibility = ask == null ? Visibility.Collapsed : Visibility.Visible;
        if (ask != null)
        {
            ClaudeAskTool.Text = ask.Tool.Contains("__") ? ask.Tool[(ask.Tool.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : ask.Tool;
            ClaudeAskWants.Text = Loc.T("claude.ask.wants");
            ClaudeAskMore.Text = c.Asks.Count > 1 ? Loc.F("claude.ask.more", c.Asks.Count - 1) : "";
            ClaudeAskWhat.Text = ask.Summary.Length > 0 ? ask.Summary : ask.Tool;
            ClaudeAskWhat.ToolTip = ask.Summary.Length > 60 ? ask.Summary : null;
            ClaudeAllowText.Text = Loc.T("claude.ask.allow");
            ClaudeDenyText.Text = Loc.T("claude.ask.deny");
        }

        var edit = ask == null ? c.LastEdit : null;
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

    private bool _simulatingClaude;

    /// <summary>
    /// Plays a fake Claude task through every stage (from the settings "Simulate" button):
    /// thinking, reading, editing, running a command, testing, asking for permission, then done.
    /// </summary>
    public async void SimulateClaudeTask()
    {
        if (_simulatingClaude) return;
        _simulatingClaude = true;
        try
        {
            const string sid = "\"session_id\":\"notchy-demo\",\"cwd\":\"C:/Projetos/Notchy\"";
            void Send(string body) => _claude.Inject("{" + sid + "," + body + "}");
            const int stage = 3500;

            // thinking
            Send("\"hook_event_name\":\"UserPromptSubmit\",\"prompt\":\"Demonstração do Notchy\"");
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"in_progress\"},{\"status\":\"pending\"},{\"status\":\"pending\"}]}");
            await Task.Delay(stage);
            // reading
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"C:/Projetos/Notchy/App.xaml\"}");
            await Task.Delay(stage);
            // editing
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"completed\"},{\"status\":\"in_progress\"},{\"status\":\"pending\"}]}");
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"C:/Projetos/Notchy/Island.cs\",\"new_string\":\"public class Island\\n{\\n    public string Name = \\\"Notchy\\\";\"}");
            await Task.Delay(stage);
            // running a command
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git status\"}");
            await Task.Delay(stage);
            // testing
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"TodoWrite\",\"tool_input\":{\"todos\":[{\"status\":\"completed\"},{\"status\":\"completed\"},{\"status\":\"in_progress\"}]}");
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet test\"}");
            await Task.Delay(stage);
            // asking for permission
            Send("\"hook_event_name\":\"Notification\",\"message\":\"Claude needs your permission to use Bash\"");
            await Task.Delay(stage + 1000);
            // allowed: one more step, then done
            Send("\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Bash\"");
            Send("\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"git commit\"}");
            await Task.Delay(1500);
            Send("\"hook_event_name\":\"Stop\"");
        }
        finally
        {
            _simulatingClaude = false;
        }
    }

    // =====================================================================================
    // focus
    // =====================================================================================

    private void OnFocusFinished(FocusMode finished)
    {
        NavDone(Tab.Focus);
        SoundService.Play(Sfx.FocusEnd);
        Happy();
        ShowTransient(CompactMode.FocusDone,
            finished == FocusMode.Focus ? Loc.T("focus.done") : Loc.T("focus.breakdone"), "", 6);
        if (S.ClaudeWindowsNotifications)
            App.Notify(Loc.T("tab.focus"), finished == FocusMode.Focus ? Loc.T("focus.done") : Loc.T("focus.breakdone"));
        UpdateFocusUi();
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
        {
            if (_levelKind != LevelKind.Volume) SetDeviceListOpen(false);
            ShowVolumeLevel(vol, mute);
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.6);
            OsdHider.HideWindowsOsd();
        }

        _lastVol = vol;
        _lastMute = mute;
        if (_levelKind == LevelKind.Volume && !_levelDragging) ShowVolumeLevel(vol, mute);

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
        _keepOpenArea = null;
        _hoverTimer.Stop();
        if (S.Open == OpenMode.Click) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, S.HoverDelayMs));
        _hoverTimer.Start();
    }

    private Rect? _keepOpenArea;

    /// <summary>
    /// Switching tabs resizes the island; if the new tab is smaller the mouse can end up outside it without
    /// moving. Until the mouse leaves the area the island had before the switch, that doesn't count as leaving.
    /// </summary>
    private void RememberKeepOpenArea()
    {
        if (Island.ActualWidth < 1) return;
        var r = Island.TransformToAncestor(this).TransformBounds(new Rect(0, _topHidden, Island.ActualWidth, Island.ActualHeight - _topHidden));
        // a moment ago the island may have been bigger still (quick switches): keep the union
        _keepOpenArea = _keepOpenArea is Rect prev ? Rect.Union(prev, r) : r;
    }

    private bool CursorInKeepOpenArea()
    {
        if (_keepOpenArea is not Rect r || _state != IslandState.Expanded) return false;
        Native.GetCursorPos(out var p);
        var pt = PointFromScreen(new Point(p.X, p.Y));
        r.Inflate(6, 6);
        if (r.Contains(pt)) return true;
        _keepOpenArea = null; // it moved away: back to normal
        return false;
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
        if (_ring != null)
        {
            // a click on the ringing island (not on its buttons): stop it and open the Relógio tab
            SoundService.Play(Sfx.Click);
            StopRing();
            _userTab = Tab.Clock;
        }
        if (_state == IslandState.Expanded) return;
        // the volume pop-up is all controls (the level, the device button, the output list)
        if (_state == IslandState.Volume) return;
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
        if (SpotifyActive) _ = OpenSpotifyDevicesAsync();
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
        SoundService.Play(Sfx.Click);
        SetMoreOpen(!_moreOpen); // Configurações / Sair as pills in the header
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

#if DEBUG
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

        // Relógio: a timer in the ring with others running, the alarms, the editor, the small island and the ring
        if (Environment.GetEnvironmentVariable("NOTCHY_CLOCKTEST") == "1")
        {
            _clockSel = _clock.Start(8 * 60 - 18, "🍝 Macarrão");
            _clock.Start(4 * 60, "☕ Café");
            _clock.Start(45 * 60, "🧺 Roupa");
            _clock.SaveAlarm(new Alarm { Hour = 7, Minute = 30, Days = 0b0011111, Name = "Acordar" });
            _clock.SaveAlarm(new Alarm { Hour = 14, Minute = 0, Days = 0, Name = "Remédio", Enabled = false });
            _clock.SaveAlarm(new Alarm { Hour = 22, Minute = 15, Days = 0b1111111, Name = "Dormir" });
            _clock.SaveAlarm(new Alarm { Hour = 3, Minute = 0, Days = 0b1111111 });
            _clock.SaveAlarm(new Alarm { Hour = 18, Minute = 30, Days = 0b0011111, Name = "Treino" });
            _clockOnAlarms = false;
            PlaceClockPages(animate: false);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Clock, false);
            await Shot("8_clock_timer");
            ShowClockPage(alarms: true);
            await Shot("8_clock_alarms");
            SetAlarmHover(true);
            _alarmTarget = AlarmMaxOffset;
            await Shot("8_clock_alarms_end");
            _alarmTarget = 0;
            SetAlarmHover(false);
            DeleteAlarmWithUndo(_clock.Alarms[1], null);
            await Shot("8_clock_undo");
            HideUndo();
            OpenAlarmEditor(null);
            await Shot("8_clock_editor");
            CloseAlarmEditor();
            ShowClockPage(alarms: false);
            foreach (var t in System.Linq.Enumerable.ToList(_clock.Timers)) if (t != _clockSel) _clock.Remove(t);
            await Shot("8_clock_single");
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            bool claudeShown = S.ClaudeShowInCompact;
            S.ClaudeShowInCompact = false; // the demo Claude task would take the bubble (not saved)
            UpdateState();
            await Shot("8_clock_compact");
            await Task.Delay(1500);
            await Shot("8_clock_compact2");
            S.ClaudeShowInCompact = claudeShown;
            OnClockTimerFinished(_clockSel!);
            await Task.Delay(600);
            await Shot("8_clock_ring");
            StopRing();
            foreach (var t in System.Linq.Enumerable.ToList(_clock.Timers)) _clock.Remove(t);
            foreach (var a in System.Linq.Enumerable.ToList(_clock.Alarms)) _clock.RemoveAlarm(a);
        }

        // Bluetooth earbuds: connected, low battery, disconnected (simulated), and what the real watcher found
        if (Environment.GetEnvironmentVariable("NOTCHY_BUDSTEST") == "1")
        {
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            var fake = new BudsInfo("test", "Buds3 de Pedro", true, 77, false);
            ShowBuds(CompactMode.Buds, fake, 6);
            await Task.Delay(400);
            await Shot("11_buds");
            ShowBuds(CompactMode.BudsLow, fake with { Battery = 12 }, 6);
            await Task.Delay(400);
            await Shot("11_buds_low");
            ShowBuds(CompactMode.BudsOff, fake with { Connected = false }, 6);
            await Task.Delay(400);
            await Shot("11_buds_off");
            _transient = CompactMode.None;
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "buds.txt"),
                System.Linq.Enumerable.Select(_bt.Known, d => $"{d.Name} | conectado={d.Connected} | bateria={d.Battery} | caixa={d.Speaker}"));
        }

        if (Environment.GetEnvironmentVariable("NOTCHY_ACTTEST") == "1")
        {
            var ids = new[] { "5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "com.squirrel.Discord.Discord", "Telegram.TelegramDesktop" };
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "activators.txt"),
                System.Linq.Enumerable.Select(ids, id => $"{id} -> {NotificationOpener.ActivatorFor(id)?.ToString() ?? "nenhum"}"));
        }

        // notifications: the card coming down (with the sender's photo from a real WhatsApp notification) and the history
        if (Environment.GetEnvironmentVariable("NOTCHY_NOTIFTEST") == "1")
        {
            for (int i = 0; i < 20 && _notices.History.Count == 0; i++) await Task.Delay(300);
            var wa = System.Linq.Enumerable.FirstOrDefault(_notices.History, n => n.App.Contains("WhatsApp"));
            var photo = wa == null ? null : await NotificationService.SenderPhotoAsync(wa.Id, wa.AppId, wa.Title);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "notif.txt"),
                $"history={_notices.History.Count} apps={string.Join(",", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(_notices.History, n => n.App)))} whatsapp={(wa != null)} photo={(photo != null)}");
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            var demo = new Notice(999999, "WhatsApp", wa?.AppId ?? "", "Mãe", "Já chegou em casa? Me avisa quando chegar ❤️", DateTime.Now, photo ?? wa?.Avatar, photo != null);
            OnNotice(demo);
            await Task.Delay(500);
            await Shot("13_notice");
            EndNotice();
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Shot("13_bell");
            OpenSubPage(Loc.T("notif.title"), NoticesPage);
            await Shot("13_history");
            CloseSubPage(animate: false);
        }

        // weather: the home card and the "‹ Clima" sub-page (real data: Windows location + Open-Meteo)
        if (Environment.GetEnvironmentVariable("NOTCHY_WEATHERTEST") == "1")
        {
            await _weather.RefreshAsync(force: true);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Shot("12_home");
            OpenSubPage(Loc.T("weather.title"), WeatherPage);
            await Shot("12_weather");
            await Shot("12_weather_place");
            OpenCityPage();
            _cityBox!.Text = "Alagoinha";
            for (int i = 0; i < 40 && (_cityResults?.Children.Count ?? 0) < 2; i++) await Task.Delay(500);
            await Task.Delay(400);
            await Shot("12_weather_city");
            (_cityResults?.Parent as ScrollViewer)?.ScrollToEnd();
            await Task.Delay(100);
            await Shot("12_weather_city_scrolled");
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "city.txt"),
                $"results={_cityResults?.Children.Count} box='{_cityBox?.Text}' sub={_subHeight} editing={_cityEditing} timer={_citySearch?.IsEnabled}");
            SubPageBack();
            await Shot("12_weather_back");
            CloseSubPage(animate: false);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "weather.txt"),
                $"problem={_weather.Problem} city={_weather.Current?.City} temp={_weather.Current?.Temp} sky={_weather.Current?.Sky}");
        }

        // marking several files (in memory only: nothing is saved to the shelf or the history)
        if (Environment.GetEnvironmentVariable("NOTCHY_SELTEST") == "1")
        {
            _forcedState = IslandState.Expanded;
            UpdateState();
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "notchy-seltest");
            System.IO.Directory.CreateDirectory(dir);
            var names = new[] { "aula.mp4", "musica.mp3", "foto.png", "contrato.pdf", "projeto.zip", "setup.exe", "dados.bin" };
            var fake = names.Select(n => System.IO.Path.Combine(dir, n)).ToList();
            foreach (var f in fake) System.IO.File.WriteAllText(f, "x");
            _shelf.Clear();
            _shelf.AddRange(fake);
            SelectTab(Tab.Shelf, false);
            RenderShelf();
            await Shot("11_shelf");
            _shelfSel.Marked.Add(fake[0]);
            _shelfSel.Marked.Add(fake[2]);
            _shelfSel.Sync();
            await Shot("11_shelf_marked");
            _shelfSel.SetMode(true);
            _shelfSel.Marked.Add(fake[1]);
            _shelfSel.Sync();
            await Shot("11_shelf_mode");
            _shelfSel.Reset();

            SelectTab(Tab.Downloads, false);
            BuildDownloadsTab(null, new List<DownloadFile>(), fake.Select(f => new DownloadEntry(f, 1234567, DateTime.UtcNow)).ToList());
            UpdateSelChrome();
            await Shot("11_dl");
            _dlSel.Marked.Add(fake[0]);
            _dlSel.Marked.Add(fake[2]);
            _dlSel.Sync();
            await Shot("11_dl_marked");
            _dlSel.SetMode(true);
            _dlSel.Marked.Add(fake[1]);
            _dlSel.Sync();
            await Shot("11_dl_mode");
            _dlSel.Reset();
            _shelf.Clear();
            SelectTab(Tab.Focus, false);
            await Shot("11_focus");
            SelectTab(Tab.Home, false);
            await Shot("11_home");

            // the pictures of the news
            SelectTab(Tab.News, false);
            _newsManual = true;
            for (int i = 0; i < WhatsNew.Highlights.Length; i++)
            {
                ShowNewsCard(i, animate: false);
                await Task.Delay(1000);
                await Shot("13_news" + i);
            }
            SelectTab(Tab.Home, false);

            // the bell on the closed island
            _forcedState = IslandState.Idle;
            UpdateState();
            _alarmHintForced = (true, null);
            UpdateAlarmHint();
            await Shot("12_idle_bell");
            _alarmHintForced = (true, FormatClock(DateTime.Now.AddMinutes(8)));
            UpdateAlarmHint();
            await Shot("12_idle_soon");
            _forcedState = IslandState.Compact;
            _compactMode = CompactMode.Music;
            ConfigureCompact();
            UpdateState();
            _alarmHintForced = (true, null);
            UpdateAlarmHint();
            await Shot("12_music_bell");
            _alarmHintForced = (true, FormatClock(DateTime.Now.AddMinutes(8)));
            UpdateAlarmHint();
            await Shot("12_music_soon");
            _alarmHintForced = null;
            _forcedState = IslandState.Expanded;
            UpdateState();
        }

        // menus inside the open island: the output sub-page and the ••• pills
        if (Environment.GetEnvironmentVariable("NOTCHY_MENUTEST") == "1")
        {
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Music, false);
            await Shot("10_music");
            OpenOutputSubPage();
            await Shot("10_output_sub");
            CloseSubPage(animate: true);
            await Shot("10_back");
            SetMoreOpen(true);
            await Shot("10_more");
            SetMoreOpen(false);
            await Task.Delay(500);
            await Shot("10_more_closed");
            await OpenQueueAsync();
            await Shot("10_queue");
            CloseQueue();

            // the tray menu, drawn on its own
            TrayMenu.ShowAtCursor();
            await Task.Delay(700);
            foreach (Window w in Application.Current.Windows)
            {
                if (w is not TrayMenu tm || tm.Content is not FrameworkElement tc) continue;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)tc.ActualWidth * 2, (int)tc.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)), null, new Rect(0, 0, tc.ActualWidth, tc.ActualHeight));
                rtb.Render(dv);
                rtb.Render(tc);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = System.IO.File.Create(System.IO.Path.Combine(folder, "10_tray.png"))) enc.Save(fs);
                tm.Close();
            }
        }

        // volume / brightness: the filling island, the device button, the output list, mute and brightness
        if (Environment.GetEnvironmentVariable("NOTCHY_VOLTEST") == "1")
        {
            _forcedState = IslandState.Volume;
            ShowVolumeLevel(0.62, false);
            UpdateState();
            await Shot("9_vol");
            _levelHover = true;
            await Shot("9_vol_hover");
            SetDeviceListOpen(true);
            await Shot("9_vol_list");
            SetDeviceListOpen(false);
            _levelHover = false;
            ShowVolumeLevel(0.62, true);
            await Shot("9_vol_mute");
            _levelKind = LevelKind.Brightness;
            _levelTarget = 0.85;
            _levelMuted = false;
            await Shot("9_bright");
            _levelKind = LevelKind.Volume;
            _forcedState = null;
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
        foreach (var page in new[] { "general", "island", "sounds", "claude", "spotify", "focus" })
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
#endif
}
