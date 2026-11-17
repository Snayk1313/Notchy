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
    private enum Tab { Home, Claude, Music, Chat, Focus, Clock, Shelf, Clip, Downloads, News }
    private enum CompactMode { None, Music, MusicPaused, Claude, ClaudeDone, ClaudeAttention, Focus, FocusDone, Battery, BatteryLow, Update, Transfer, TransferDone, TransferFailed, WhatsNew, Timer, TimerRing, Buds, BudsLow, BudsOff, Notice, CalNotice, Shot }

    private static readonly Color ClaudeColor = Color.FromRgb(0xE8, 0x82, 0x5F);
    private static readonly Color FocusColor = Color.FromRgb(0xB7, 0x9C, 0xFF);

    private readonly MediaService _media;
    private readonly ClaudeService _claude;
    private readonly FocusTimer _focus = new();
    private readonly LyricsService _lyrics = new();
    private readonly AudioService _audio = new();
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
        ProgressSlider.HoverLabel = v => Format(TimeSpan.FromTicks((long)(_media.Duration.Ticks * v)));
        VolumeSlider.HoverLabel = v => $"{Math.Round(v * 100)}%";
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
        InitCapture();
        InitWhatsNew();
        InitFocusTab();
        InitClockTab();
        InitClockRing();
        InitVolumeIsland();
        InitSubPages();
        InitBuds();
        InitWeather();
        InitNotices();
        InitCalendar();
        InitClipboard();
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
            StartFrameLoop();
            _claude.Start();
            if (AppSettings.WasFirstRun) _ = OpenSetupAfterIntroAsync();
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
        // the scale grows the island from its hidden top: hide 30 px at that scale so the visible part still starts at the screen edge
        Island.Margin = new Thickness(0, floating ? 8 : -_topHidden * S.SizeScale, 0, 0);
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
        SyncQuietMode();
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
                Tab.Clip => Loc.T("tab.clip"),
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
        ClaudeRecentTitle.Text = Loc.T("claude.recent");
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
        bool ringing = _ring != null && (!_hiddenForFullscreen || S.RingFullscreen); // a timer/alarm calling you takes the island
        // a notification card stays a card under the mouse (so you can click it), instead of opening the island
        ringing |= _noticeShown != null && _transient == CompactMode.Notice && DateTime.UtcNow < _transientUntil;
        ringing |= CalNoticeHolds;
        ringing |= ShotNoticeHolds;
        if (_forcedState is { } forced) next = forced;
        else if (!ringing && WheelVolumeHolds) next = IslandState.Volume; // (Shift + wheel on the open island: the bar, for a moment)
        else if (!ringing && _hoverActive && !FullscreenHidesIsland || _clickOpened && !FullscreenHidesIsland || _holdOpenAfterPill || _menu.IsOpen || _devicesMenuOpen?.IsOpen == true || _dragOver || _lassoing || ChatKeepsOpen || ClipKeepsOpen) next = IslandState.Expanded;
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
        _ => new Size(CompactWidth(_compactMode), _compactMode == CompactMode.TimerRing ? RingHeight : _compactMode == CompactMode.Notice ? NoticeHeight : _compactMode == CompactMode.CalNotice ? CalNoticeHeight : _compactMode == CompactMode.Shot ? ShotHeight : IsTransferMode(_compactMode) ? TransferHeight : 34)
    };

    private double TabHeight(Tab t) => 34 + 40 + 2 + t switch
    {
        Tab.Home => 118,
        Tab.Claude => 150,
        Tab.Music => 186 + (_lyricTwoLines ? LyricExtraHeight : 0),
        Tab.Shelf => 118,
        Tab.Clip => ClipTabHeight,
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
        CompactMode.CalNotice => _calWidth,
        CompactMode.Shot => ShotWidth,
        CompactMode.WhatsNew => WhatsNewWidth(),
        CompactMode.Transfer or CompactMode.TransferDone or CompactMode.TransferFailed => TransferWidth,
        _ => 320
    };

    private void ApplyState(IslandState state, bool playSound = true)
    {
        // full screen: the bar goes back up still drawn as the bar; the island changes once it's out of sight
        if (_fsRising && state != IslandState.Volume)
        {
            _state = state;
            return;
        }
        if (_hiddenForFullscreen && _state == IslandState.Volume && state != IslandState.Volume && Island.Visibility == Visibility.Visible
            && !FullscreenCardUp)
        {
            RiseVolumeBar(state);
            return;
        }
        var prev = _state;
        bool fsDrop = _hiddenForFullscreen && state == IslandState.Volume && prev != IslandState.Volume && (Island.Visibility != Visibility.Visible || _fsRising);
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
            else if (_returnTab is Tab back) target = back; // (back from the volume bar: the tab it was on)
            else if (_userTab is Tab ut && (ut != Tab.Shelf || S.ShelfEnabled)) target = ut;
            else target = SmartTab();
            // back within a moment: the page it was on (notifications, calendar…) is still there; later, the tab as usual
            bool backToPage = _subOpen && (DateTime.UtcNow - _subLeftAt).TotalSeconds <= SubReturnSeconds && !_dlOpenRequest && !_podOpenRequest && !_dragOver;
            _podOpenRequest = false;
            _dlOpenRequest = false;
            _returnTab = null;
            if (!backToPage)
            {
                if (!_dragOver) SelectTab(target, animate: false); // (closes the page left open)
                else CloseSubPage(animate: false);
            }
            if (playSound && prev != IslandState.None) SoundService.Play(Sfx.Expand);
            UpdateClock();
            MaybePulseDate();
        }
        else if (prev == IslandState.Expanded && playSound)
        {
            SoundService.Play(Sfx.Collapse);
        }

        AnimateIslandSize(TargetSize(state), expanding || prev == IslandState.Expanded);

        Fade(IdlePanel, state == IslandState.Idle);
        Fade(CompactPanel, state == IslandState.Compact && !IsTransferMode(_compactMode) && !IsClaudeMode(_compactMode) && _compactMode is not (CompactMode.WhatsNew or CompactMode.TimerRing) && !IsBudsMode(_compactMode) && _compactMode is not (CompactMode.Notice or CompactMode.CalNotice or CompactMode.Shot));
        Fade(_budsPanel, state == IslandState.Compact && IsBudsMode(_compactMode));
        Fade(_noticePanel, state == IslandState.Compact && _compactMode == CompactMode.Notice);
        Fade(_calPanel, state == IslandState.Compact && _compactMode == CompactMode.CalNotice);
        Fade(_shotPanel, state == IslandState.Compact && _compactMode == CompactMode.Shot);
        Fade(_ringPanel, state == IslandState.Compact && _compactMode == CompactMode.TimerRing);
        Fade(WhatsNewPanel, state == IslandState.Compact && _compactMode == CompactMode.WhatsNew);
        Fade(ClaudePanel, state == IslandState.Compact && IsClaudeMode(_compactMode));
        Fade(TransferPanel, state == IslandState.Compact && IsTransferMode(_compactMode));
        Fade(VolumePanel, state == IslandState.Volume);
        if (state != IslandState.Volume) SetDeviceListOpen(false);
        bool stow = !expanding && prev == IslandState.Expanded && _subOpen;
        if (!expanding)
        {
            // a page inside the island stays as it is while the island closes (no jump back to the tab); the next opening
            // decides: back soon, the same page; later, the tab
            if (stow) _subLeftAt = DateTime.UtcNow;
            SetMoreOpen(false);
        }
        Fade(ExpandedPanel, expanding);
        if (stow) StowPage();
        AccentGlow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(expanding ? 1 : 0, TimeSpan.FromMilliseconds(Dur(expanding ? 420 : 160))));
        if (expanding) Dispatcher.InvokeAsync(() => MoveNavPill(false), DispatcherPriority.Loaded);
        if (prev == IslandState.Expanded && !expanding) TurnPillIntoCard();
        if (prev == IslandState.Idle && state == IslandState.Volume) ArriveFromIdle();
        else if (prev == IslandState.Volume && state == IslandState.Idle) DepartToIdle();
        else if (_departAt >= 0) FinishDepart(); // somewhere else meanwhile: no eyes left behind
        // in a full-screen game the island shows just for the volume/brightness bar, then hides again
        if (fsDrop) DropVolumeBar();
        else if (_hiddenForFullscreen && state == IslandState.Volume) ApplyFullscreenHide();
        else if (_hiddenForFullscreen && prev == IslandState.Volume) HideAfterCard();
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
        UpdateCalendarFrame();
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
        UpdateMusicAccentFrame();

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
        UpdateShotNotice();
        UpdateClipLight();
        UpdateNavActivity();
        UpdateFocusFrame();
        UpdateFocusPod();
        UpdateWhatsNewFrame();
        if (_state == IslandState.Expanded && _tab == Tab.Downloads) UpdateDownloadsTab(texts: _slowTick && _tick % 2 == 0);

        if (_slowTick && _tick % 6 == 0) PollVolume();
        if (_slowTick && _tick % 8 == 0) CheckFullscreen(); // ~4 times a second: a game going full screen hides the island right away
        if (_slowTick && _tick % 60 == 0) UpdateClock();
        if (_slowTick && _tick % 180 == 0)
        {
            _media.PickSession();
            _audio.EnsureDevice();
            UpdateOutputIcon();
            if (_hwnd != IntPtr.Zero && !_menu.IsOpen && _devicesMenuOpen?.IsOpen != true && _outMenuOpen?.IsOpen != true) Native.BringToTopmost(_hwnd);
        }

        UpdateState();
        PaceLoop();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = Loc.Culture;
        DateRun.Text = now.ToString("ddd d MMM", culture); // (the pill needs the room the year took; the calendar shows it)
        TimeRun.Text = FormatClock(now);
        UpdateDateDot();

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
        if (fullscreen != _hiddenForFullscreen) SetFullscreen(fullscreen);
    }

    private void SetFullscreen(bool on)
    {
        _hiddenForFullscreen = on;
        if (on && _noticeInPill) DismissNotice(); // a pill under the island goes to the history, not over the game
        ApplyFullscreenHide();
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
        AddNav(Tab.Clip, "\uE77F");
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
        Tab.Clip => ClipTab,
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
                el.IsHitTestVisible = true;
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
            else if (animate && t == old && _state == IslandState.Expanded && el.Visibility == Visibility.Visible && S.SpeedFactor > 0)
            {
                // the tab you leave goes out to the other side (like the pages inside the island), then it's put away
                double dir = (int)tab > (int)old ? 1 : -1;
                var leaving = t;
                var away = new TranslateTransform();
                el.RenderTransform = away;
                el.IsHitTestVisible = false;
                var time = TimeSpan.FromMilliseconds(Dur(140)); // (short: two tabs on top of each other is a blur)
                away.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -28 * dir, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                var fade = new DoubleAnimation(0, time);
                fade.Completed += (_, _) =>
                {
                    if (_tab == leaving) return;
                    el.Visibility = Visibility.Collapsed;
                };
                el.BeginAnimation(OpacityProperty, fade);
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
        if (tab == Tab.Clip) RenderClip();
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
        Tab.Clip => ClipColor,
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
            Tab.Music => (Loc.T("music.head.a"), Loc.T("music.head.b")),
            Tab.Focus => (Loc.T("focus.head.a"), Loc.T("focus.head.b")),
            Tab.Shelf => (Loc.T("shelf.head.a"), Loc.T("shelf.head.b")),
            Tab.Clip => (Loc.T("clip.head.a"), Loc.T("clip.head.b")),
            Tab.Downloads => (Loc.T("downloads.head.a"), Loc.T("downloads.head.b")),
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
            Tab.Clip => Color.FromRgb(0x1E, 0x32, 0x5C),
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
        if (IsClaudeMode(m) || IsTransferMode(m) || m is CompactMode.WhatsNew or CompactMode.TimerRing or CompactMode.Notice or CompactMode.CalNotice or CompactMode.Shot || IsBudsMode(m))
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
        SwapText(TitleText, has ? _media.Title : Loc.T("music.nothing"));
        SwapText(ArtistText, has ? _media.Artist : Loc.T("music.hint"));
        SourceText.Text = _media.SourceName;
        SourceButton.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;

        SwapText(HomeMusicTitle, has ? _media.Title : Loc.T("music.nothing.short"));
        HomeMusicSub.Text = has ? (_media.IsPlaying ? _media.Artist : Loc.T("music.paused")) : "—";
        HomeMusicPlay.Visibility = _media.HasSession ? Visibility.Visible : Visibility.Collapsed;

        Brush art = _media.Art != null
            ? new ImageBrush(_media.Art) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
        bool newArt = !ReferenceEquals(_media.Art, _artShown); // (the cover often arrives a moment after the title)
        _artShown = _media.Art;
        SwapArt(CompactArt, art, newArt);
        SwapArt(ExpandedArt, art, newArt, pop: true);
        SwapArt(HomeMusicArt, _media.Art != null ? art : new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), newArt);

        SetMusicAccent(_media.ArtColor ?? ColorUtil.Fallback);
        if (_spTimer != null)
        {
            UpdateSpotifyControls();
            _ = PollSpotifyAsync();
        }
        ApplyMusicAccent();

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
        SwapGlyph(PlayButton, glyph);
        SwapGlyph(HomeMusicPlay, glyph);
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

        FadeVisible(ClaudeActive, showActive, rise: 0);
        FadeVisible(ClaudeIdle, !showActive, rise: 0);

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
        FadeVisible(ClaudeAskCard, ask != null, rise: 8);
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
        FadeVisible(ClaudeFileCard, edit != null, rise: 8);
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
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(_hiddenForFullscreen ? 1.2 : 1.6);
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
        if (DateTime.UtcNow < _ignoreHoverUntil) return; // a notification was just dismissed right here
        if (_holdOpenAfterPill && ReferenceEquals(sender, Island))
        {
            // back on the island after dismissing a pill: from here on, leaving it closes it as usual
            _holdOpenAfterPill = false;
            if (S.Open == OpenMode.Hover) _hoverActive = true;
            else _clickOpened = true;
        }
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
        PaceLoop(); // back to the screen's pace now: a timer waiting on an idle window can lag
        _keepOpenArea = null;
        _hoverTimer.Stop();
        if (S.Open == OpenMode.Click) return;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(60); // just enough to ignore the mouse passing by the top of the screen
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
        _holdOpenAfterPill = false;
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
    /// <summary>Test: after a short wait, saves a picture of the whole window (on a purple backdrop) as &lt;folder&gt;/&lt;name&gt;.png.</summary>
    private async Task ShotAsync(string folder, string name, int wait)
    {
        await Task.Delay(wait);
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

    /// <summary>Snapshot tests: the first visible element under <paramref name="root"/> that passes <paramref name="ok"/>.</summary>
    private static T? TestFind<T>(DependencyObject root, Func<T, bool> ok) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t && ok(t) && (t is not UIElement u || u.IsVisible)) return t;
            if (TestFind(c, ok) is T found) return found;
        }
        return null;
    }

    /// <summary>Snapshot tests: a click (mouse up) on the nearest clickable Border from <paramref name="from"/> up.</summary>
    private static bool TestClick(DependencyObject? from)
    {
        for (var d = from; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Border { Cursor: not null } b && b.Cursor == Cursors.Hand)
            {
                b.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = MouseLeftButtonUpEvent });
                return true;
            }
        return false;
    }

    /// <summary>
    /// Only the paste from Copiados (NOTCHY_PASTETEST, quick): the real shortcut, a real Enter, into a test window and into
    /// Explorer's search box. The saved history is left alone.
    /// </summary>
    private async Task PasteTestAsync(string folder)
    {
        string log = System.IO.Path.Combine(folder, "paste.txt");
        int fails = 0;
        void Check(string what, bool ok, string got)
        {
            if (!ok) fails++;
            System.IO.File.AppendAllText(log, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        var saved = _clip.Items.ToList();
        _clip.DontSave = true;
        _clip.Items.Clear();
        bool wasPaused = S.ClipPaused;
        S.ClipPaused = false;
        System.Windows.Clipboard.SetText("https://github.com/notchy/notchy");
        await Task.Delay(600);
        Check("link no histórico", _clip.Items.FirstOrDefault()?.Kind == ClipKind.Link, $"{_clip.Items.Count} itens");

        var hk = KeyboardHook.Parse(S.ClipHotkey)!.Value;
        var hkKeys = new (uint mod, byte vk)[] { (KeyboardHook.ModWin, 0x5B), (KeyboardHook.ModCtrl, 0x11), (KeyboardHook.ModAlt, 0x12), (KeyboardHook.ModShift, 0x10) }
            .Where(m => (hk.mods & m.mod) != 0).Select(m => m.vk).Append((byte)hk.vk).ToList();
        string Value(System.Windows.Automation.AutomationElement? el)
        {
            try { return el != null && el.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var p) ? ((System.Windows.Automation.ValuePattern)p).Current.Value : "(sem valor)"; }
            catch (Exception e) { return e.GetType().Name; }
        }
        // the shortcut over the window in front, ↵ held a moment like a real press, then what landed in the box
        async Task PasteInto(string name, IntPtr window)
        {
            for (int i = 0; i < 30 && window != IntPtr.Zero && !Native.IsForeground(window); i++) await Task.Delay(100);
            var box = await Task.Run(() => System.Windows.Automation.AutomationElement.FocusedElement);
            string boxName = box == null ? "(nada)" : await Task.Run(() => $"{box.Current.ControlType.ProgrammaticName} “{box.Current.Name}”");
            Check($"{name}: na frente com a caixa focada", window != IntPtr.Zero && box != null && Native.IsForeground(window), boxName);
            foreach (var k in hkKeys) keybd_event(k, 0, 0, UIntPtr.Zero);
            foreach (var k in Enumerable.Reverse(hkKeys)) keybd_event(k, 0, 2, UIntPtr.Zero);
            await Task.Delay(900);
            Check($"{name}: atalho abre por cima", _clipKeys && _clipTarget == window, $"teclado={_clipKeys} alvo certo={_clipTarget == window}");
            if (_clipKeys && _clipTarget == window)
            {
                keybd_event(0x0D, 0, 0, UIntPtr.Zero);
                await Task.Delay(110);
                keybd_event(0x0D, 0, 2, UIntPtr.Zero);
            }
            await Task.Delay(900);
            string typed = await Task.Run(() => Value(box));
            Check($"{name}: Enter cola na caixa", typed.Contains("github.com/notchy"), $"“{typed}”");
        }

        // a plain test window
        string ps1 = System.IO.Path.Combine(folder, "target.ps1");
        System.IO.File.WriteAllText(ps1, "Add-Type -AssemblyName System.Windows.Forms\n" +
            "$f = New-Object System.Windows.Forms.Form; $f.Text = 'Notchy teste'; $f.TopMost = $true; $f.Width = 420; $f.Height = 140\n" +
            "$t = New-Object System.Windows.Forms.TextBox; $t.Dock = 'Fill'; $t.Multiline = $false; $f.Controls.Add($t)\n" +
            "$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 12000; $timer.Add_Tick({ $f.Close() }); $timer.Start()\n" +
            "$f.Add_Shown({ $f.Activate(); $t.Focus() }); [void]$f.ShowDialog()\n");
        using (var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            IntPtr win = IntPtr.Zero;
            for (int i = 0; i < 40 && win == IntPtr.Zero; i++)
            {
                await Task.Delay(150);
                other.Refresh();
                win = other.MainWindowHandle;
            }
            await Task.Delay(400);
            await PasteInto("app de teste", win);
            if (win != IntPtr.Zero) Native.CloseWindow(win);
        }

        // Explorer's search box (Ctrl+E): with Enter down it gives the focus to the file list and drops the Ctrl+V
        var cabinet = new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "CabinetWClass");
        int[] Cabinets() => System.Windows.Automation.AutomationElement.RootElement.FindAll(System.Windows.Automation.TreeScope.Children, cabinet)
            .Cast<System.Windows.Automation.AutomationElement>().Select(e => e.Current.NativeWindowHandle).ToArray();
        var openBefore = (await Task.Run(Cabinets)).ToHashSet();
        System.Diagnostics.Process.Start("explorer.exe", folder);
        IntPtr ex = IntPtr.Zero;
        for (int i = 0; i < 40 && ex == IntPtr.Zero; i++)
        {
            await Task.Delay(150);
            ex = new IntPtr((await Task.Run(Cabinets)).FirstOrDefault(h => !openBefore.Contains(h)));
        }
        for (int i = 0; i < 30 && ex != IntPtr.Zero && !Native.IsForeground(ex); i++) await Task.Delay(100);
        await Task.Delay(1200); // (Explorer draws the folder before the search box takes keys)
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x45, 0, 0, UIntPtr.Zero);
        keybd_event(0x45, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
        await Task.Delay(700);
        await PasteInto("Explorer", ex);
        if (ex != IntPtr.Zero) Native.CloseWindow(ex);

        _clip.Items.Clear();
        _clip.Items.AddRange(saved);
        _clip.DontSave = false;
        S.ClipPaused = wasPaused;
        System.IO.File.AppendAllText(log, $"falhas: {fails}" + Environment.NewLine);
    }

    public async Task SnapshotAsync(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        await Task.Delay(2500);
        // only the music bars' hover (quick): the lighter stretch and the label, on progress and volume
        if (Environment.GetEnvironmentVariable("NOTCHY_BARHOVERTEST") == "1")
        {
            await Task.Delay(4000); // (the hello wave first)
            _media.Fake("Waking up", "Teste", true);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Music, false);
            await Task.Delay(900);
            ProgressSlider.HoverAt(0.8);
            VolumeSlider.HoverAt(null);
            await ShotAsync(folder, "bar_progress_ahead", 300);
            ProgressSlider.HoverAt(0.1);
            await ShotAsync(folder, "bar_progress_behind", 300);
            ProgressSlider.HoverAt(null);
            VolumeSlider.HoverAt(0.9);
            await ShotAsync(folder, "bar_volume", 300);
            VolumeSlider.HoverAt(null);
            Application.Current.Shutdown();
            return;
        }
        // only Copiados' search box (quick): empty, then with text
        if (Environment.GetEnvironmentVariable("NOTCHY_CLIPSEARCHTEST") == "1")
        {
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Clip, false);
            await ShotAsync(folder, "search_empty", 900);
            _clipBox.Text = "boleto";
            await ShotAsync(folder, "search_typed", 400);
            _clipBox.Text = "";
            Application.Current.Shutdown();
            return;
        }
        if (Environment.GetEnvironmentVariable("NOTCHY_UIFIXTEST") == "1")
        {
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            // Downloads: "agora" whole, folder at the edge, the X on hover, the thin bar when it scrolls
            SelectTab(Tab.Downloads, false);
            var dl = Enumerable.Range(1, 9).Select(i => new DownloadEntry($@"C:\teste\arquivo gigante {i}.zip", i * 9_678_123, DateTime.UtcNow)).ToList();
            BuildDownloadsTab(null, new List<DownloadFile>(), dl);
            await ShotAsync(folder, "dl", 900);
            _dlHistoryRows[1].RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
            DownloadsScroll.ScrollToVerticalOffset(30);
            await ShotAsync(folder, "dl_hover_scroll", 300);
            _dlSel.SetMode(true);
            _dlSel.Marked.Add(dl[0].Path);
            _dlSel.Sync();
            DownloadsScroll.ScrollToTop();
            await ShotAsync(folder, "dl_mode", 500);
            _dlSel.Reset();
            // Chat: one pair of eyes on the start screen; the thin bar on a long answer
            SelectTab(Tab.Chat, false);
            await ShotAsync(folder, "chat_idle", 900);
            SetChatView(ChatView.Answer);
            _answerText.Text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Parágrafo {i}: a ilha dinâmica mostra música, timers e alertas no topo da tela."));
            await Task.Delay(500);
            _answerScroll.ScrollToVerticalOffset(60);
            await ShotAsync(folder, "chat_answer", 300);
            SetChatView(ChatView.Idle);
            // What's new: the full list with the thin bar
            SelectTab(Tab.News, false);
            _newsManual = true;
            ToggleNewsList();
            await Task.Delay(500);
            _newsScroll?.ScrollToVerticalOffset(40);
            await ShotAsync(folder, "news_list", 300);
            // Weather: the city box as a search pill
            SelectTab(Tab.Home, false);
            OpenCityPage();
            await ShotAsync(folder, "city_typed", 900);
            _cityBox!.Text = "";
            await ShotAsync(folder, "city_empty", 400);
            Application.Current.Shutdown();
            return;
        }
        if (Environment.GetEnvironmentVariable("NOTCHY_PATTERNTEST") == "1")
        {
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            // Limpar pills and plain section titles
            SelectTab(Tab.Downloads, false);
            BuildDownloadsTab(null, new List<DownloadFile>(), Enumerable.Range(1, 3).Select(i => new DownloadEntry($@"C:\teste\arquivo{i}.pdf", i * 1234567, DateTime.UtcNow)).ToList());
            await ShotAsync(folder, "p_dl", 900);
            SelectTab(Tab.Clip, false);
            await ShotAsync(folder, "p_clip", 900);
            SelectTab(Tab.Home, false);
            OnNotice(new Notice(999961, "WhatsApp", "", "Maria", "Já chegou?", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            await Task.Delay(300);
            EndNotice();
            OpenSubPage(Loc.T("notif.title"), NoticesPage);
            await ShotAsync(folder, "p_notices", 900);
            CloseSubPage(animate: false);
            // the event editor: name box, choices, buttons; and the list's "Próximos"
            OpenCalendar(DateTime.Today);
            await ShotAsync(folder, "p_cal", 900);
            OpenCalEditor(null);
            await ShotAsync(folder, "p_cal_editor", 900);
            CloseSubPage(animate: false);
            // clock: timer name box and shortcuts; alarm editor buttons
            SelectTab(Tab.Clock, false);
            ShowClockPage(alarms: false);
            await ShotAsync(folder, "p_timer", 900);
            ShowClockPage(alarms: true);
            OpenAlarmEditor(null);
            await ShotAsync(folder, "p_alarm_editor", 900);
            _edName.Focus();
            Keyboard.Focus(_edName);
            await ShotAsync(folder, "p_alarm_editor_focus", 300);
            CloseAlarmEditor();
            SelectTab(Tab.Focus, false);
            await ShotAsync(folder, "p_focus", 900);
            Application.Current.Shutdown();
            return;
        }
        // only Settings pages (e.g. NOTCHY_SETTINGSTEST=calendar): straight to them, the island prints skipped
        string? settingsOnly = Environment.GetEnvironmentVariable("NOTCHY_SETTINGSTEST");
        if (!string.IsNullOrEmpty(settingsOnly)) goto settings;
        // only the pinned capture (quick): the names under the buttons, the zoom inside the picture, a corner resize
        if (Environment.GetEnvironmentVariable("NOTCHY_PINTEST") == "1")
        {
            await Task.Delay(1500);
            var v = new DrawingVisual();
            using (var dc = v.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x1E, 0x3A, 0x5F), Color.FromRgb(0xC9, 0x73, 0x5A), 30), null, new Rect(0, 0, 600, 360));
                for (int i = 0; i < 5; i++) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(80 + i * 35), 200, (byte)(220 - i * 30))), null, new Rect(60 + i * 100, 300 - i * 45, 60, 40 + i * 45));
                dc.DrawEllipse(Brushes.White, null, new Point(300, 120), 30, 30);
            }
            var pic = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 360, 96, 96, PixelFormats.Pbgra32);
            pic.Render(v);
            pic.Freeze();
            var pin = new PinWindow(pic, new Int32Rect(400, 300, 600, 360), _ => { }, (_, _) => { });
            pin.Show();
            await Task.Delay(700);
            // the real screen around it: Windows' rounded corners and shadow only show there
            using (var screen = new System.Drawing.Bitmap(700, 460))
            {
                using (var g = System.Drawing.Graphics.FromImage(screen)) g.CopyFromScreen(350, 250, 0, 0, screen.Size);
                screen.Save(System.IO.Path.Combine(folder, "p0_screen.png"));
            }
            pin.TestShowTools();
            pin.TestTip(0);
            await Task.Delay(400);
            pin.TestTip(1);
            await Task.Delay(70);
            pin.TestSave(System.IO.Path.Combine(folder, "p1_tip_sliding.png"));
            await Task.Delay(300);
            pin.TestTip(2);
            await Task.Delay(300);
            pin.TestSave(System.IO.Path.Combine(folder, "p2_tip_close.png"));
            double w0 = pin.Width, h0 = pin.Height;
            pin.TestZoom(new Point(300, 120), 2.5);
            await Task.Delay(200);
            pin.TestSave(System.IO.Path.Combine(folder, "p3_zoom.png"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "pin.txt"), $"zoom muda a janela: {pin.Width != w0 || pin.Height != h0} · {pin.TestZoomState}");
            pin.TestPan(new Vector(-150, -80));
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $"{Environment.NewLine}depois de mover: {pin.TestZoomState}");
            await Task.Delay(100);
            pin.TestSave(System.IO.Path.Combine(folder, "p3b_panned.png"));
            pin.TestResize(2, new Vector(-200, 0));
            await Task.Delay(300);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $"{Environment.NewLine}depois de diminuir: {pin.TestZoomState}{Environment.NewLine}");
            await Task.Delay(300);
            pin.TestSave(System.IO.Path.Combine(folder, "p4_smaller.png"));
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $" · depois de diminuir: {pin.Width:0}x{pin.Height:0} (era {w0:0}x{h0:0})");
            double right = pin.Left + pin.Width, bottom = pin.Top + pin.Height;
            pin.TestResize(3, new Vector(80, 0)); // the bottom-left corner, inward
            await Task.Delay(300);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"),
                $"{Environment.NewLine}canto esquerdo: {pin.Width:0}x{pin.Height:0} · borda direita {right:0} → {pin.Left + pin.Width:0} · topo {pin.Top:0}");
            var sweep = pin.TestSweep();
            double jump = sweep.Zip(sweep.Skip(1), (a, b) => Math.Abs(b - a)).Max();
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $"{Environment.NewLine}arrasto: {string.Join(" ", sweep)} · maior passo {jump:0.0}");
            pin.TestResetZoom();
            await Task.Delay(120);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $"{Environment.NewLine}voltando: {pin.TestZoomState}");
            await Task.Delay(400);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "pin.txt"), $"{Environment.NewLine}clique no %: {pin.TestZoomState}");
            pin.TestSave(System.IO.Path.Combine(folder, "p5_reset.png"));
            pin.Close();
            Application.Current.Shutdown();
            return;
        }
        // only the news of this version (quick): each highlight card with its little picture, then the full list
        if (Environment.GetEnvironmentVariable("NOTCHY_NEWSTEST") == "1")
        {
            await Task.Delay(4000);
            ShowWhatsNew();
            await Task.Delay(1000);
            _newsManual = true;
            for (int i = 0; i < WhatsNew.Highlights.Length; i++)
            {
                ShowNewsCard(i, animate: false);
                await ShotAsync(folder, $"n{i}_{WhatsNew.Highlights[i].Art}_a", 900);
                await ShotAsync(folder, $"n{i}_{WhatsNew.Highlights[i].Art}_b", 700);
            }
            ToggleNewsList();
            await ShotAsync(folder, "n9_list", 700);
            Application.Current.Shutdown();
            return;
        }
        // only the glass (quick): the real island over a colourful window, the same spot without it, and the bar's
        // glass made from that for several blur strengths; the closest one wins (mean difference per channel)
        if (Environment.GetEnvironmentVariable("NOTCHY_GLASSTEST") == "1")
        {
            await Task.Delay(4000);
            var log = System.IO.Path.Combine(folder, "vidro.txt");
            var dpiNow = VisualTreeHelper.GetDpi(this);
            var a = Island.PointToScreen(new Point(0, _topHidden));
            var b = Island.PointToScreen(new Point(Island.ActualWidth, Island.ActualHeight));
            const int Pad = 320;
            var rect = new System.Drawing.Rectangle((int)a.X - Pad, (int)a.Y, (int)(b.X - a.X) + 2 * Pad, (int)(b.Y - a.Y) + 360);
            var colours = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Left = rect.Left / dpiNow.DpiScaleX - 60, Top = 0, Width = rect.Width / dpiNow.DpiScaleX + 120, Height = rect.Bottom / dpiNow.DpiScaleY + 60
            };
            bool dark = Environment.GetEnvironmentVariable("NOTCHY_GLASSDARK") == "1";
            var canvas = new Canvas { Background = dark
                ? new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0x10, 0x14, 0x30), 0), new(Color.FromRgb(0x3A, 0x10, 0x40), 0.5), new(Color.FromRgb(0x08, 0x30, 0x28), 1) }, 0)
                : new LinearGradientBrush(new GradientStopCollection { new(Color.FromRgb(0xFF, 0x5A, 0x5F), 0), new(Color.FromRgb(0xFF, 0xD1, 0x66), 0.25),
                new(Color.FromRgb(0x6E, 0xE7, 0xC4), 0.5), new(Color.FromRgb(0x7F, 0xD0, 0xFF), 0.75), new(Color.FromRgb(0xB9, 0xA6, 0xFF), 1) }, 0) };
            var rnd = new Random(4);
            for (int i = 0; i < 160; i++)
            {
                var e = new System.Windows.Shapes.Ellipse { Width = 10 + rnd.Next(40), Height = 10 + rnd.Next(40), Fill = new SolidColorBrush(Color.FromRgb((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256))) };
                Canvas.SetLeft(e, rnd.Next((int)colours.Width));
                Canvas.SetTop(e, rnd.Next((int)colours.Height));
                canvas.Children.Add(e);
            }
            colours.Content = canvas;
            colours.Show();
            colours.Activate();
            Activate();
            await Task.Delay(1200);
            System.Drawing.Bitmap Grab()
            {
                var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size);
                return bmp;
            }
            using var withIsland = Grab();
            withIsland.Save(System.IO.Path.Combine(folder, "g_real.png"));
            Opacity = 0; // (the glass goes with it)
            await Task.Delay(500);
            using var behind = Grab();
            behind.Save(System.IO.Path.Combine(folder, "g_behind.png"));
            Opacity = 1;
            colours.Close();
            var behindSrc = new System.Windows.Media.Imaging.BitmapImage();
            behindSrc.BeginInit();
            behindSrc.UriSource = new Uri(System.IO.Path.Combine(folder, "g_behind.png"));
            behindSrc.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            behindSrc.EndInit();
            // only the inside of the island counts (no text: a strip below the middle, away from the edges)
            int x0 = Pad + 60, x1 = rect.Width - Pad - 60, y0 = (int)((b.Y - a.Y) * 0.55), y1 = (int)((b.Y - a.Y) * 0.85);
            double Diff(System.Drawing.Bitmap mine)
            {
                double sum = 0;
                int n = 0;
                for (int y = y0; y < y1; y += 2)
                    for (int x = x0; x < x1; x += 2)
                    {
                        var p = withIsland.GetPixel(x, y);
                        var q = mine.GetPixel(x, y);
                        sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
                        n += 3;
                    }
                return sum / Math.Max(1, n);
            }
            System.IO.File.AppendAllText(log, $"desfoque {S.BlurIntensity}% · escurecimento {S.BlurTint}% · sem vidro (só a tela): {Diff(behind):0.0}{Environment.NewLine}");
            var best = (diff: double.MaxValue, scale: 0.0, radius: 0.0);
            foreach (var scale in new[] { 1.0 / 8, 1.0 / 16 })
                foreach (var radius in new[] { 3.0, 5.0, 7.0, 9.0, 12.0 })
                {
                    var glass = CaptureWindow.TestGlassOver(behindSrc, scale, radius);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(glass));
                    string file = System.IO.Path.Combine(folder, $"g_{1 / scale:0}_{radius:0}.png");
                    using (var f = System.IO.File.Create(file)) enc.Save(f);
                    using var mine = new System.Drawing.Bitmap(file);
                    double d = Diff(mine);
                    System.IO.File.AppendAllText(log, $"1/{1 / scale:0} raio {radius:0}: {d:0.0}{Environment.NewLine}");
                    if (d < best.diff) best = (d, scale, radius);
                }
            System.IO.File.AppendAllText(log, $"melhor: 1/{1 / best.scale:0} raio {best.radius:0} ({best.diff:0.0}){Environment.NewLine}");
            Application.Current.Shutdown();
            return;
        }
        // only the screen capture (quick): Print Screen opens it, an area with marks, the colours, the trip to the island, the notice
        if (Environment.GetEnvironmentVariable("NOTCHY_CAPTURETEST") == "1")
        {
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
            var log = System.IO.Path.Combine(folder, "captura.txt");
            void Note(string line) => System.IO.File.AppendAllText(log, line + Environment.NewLine);
            await Task.Delay(4000); // (the hello wave first)
            keybd_event(0x2C, 0, 0, UIntPtr.Zero);
            keybd_event(0x2C, 0, 2, UIntPtr.Zero);
            await Task.Delay(900);
            var cw = _capture;
            Note($"Print Screen abriu: {cw?.IsVisible == true} · ilha escondida: {Opacity:0}");
            if (cw == null)
            {
                Application.Current.Shutdown();
                return;
            }
            var isl = cw.TestIsland;
            Note($"ilha em {isl.X:0},{isl.Y:0}");
            cw.TestHover(new Point(isl.X - 300, 300));
            await Task.Delay(300);
            cw.TestSave(System.IO.Path.Combine(folder, "k1_hover.png"));
            var area = new Rect(isl.X - 380, 220, 620, 360);
            cw.TestDrag(area);
            await Task.Delay(200);
            cw.TestSave(System.IO.Path.Combine(folder, "k2_drag.png"));
            cw.TestHoldBar();
            cw.TestSelect(area);
            await Task.Delay(100);
            // the toolbar's entrance, moment by moment (the island down to the area)
            var film = new Rect(area.X - 80, 0, Math.Max(area.Width + 160, 760), area.Bottom + 110);
            int frame = 0;
            foreach (var t in new[] { 0.0, 0.04, 0.08, 0.12, 0.18, 0.24, 0.3, 0.36, 0.42, 0.46, 0.5, 0.54, 0.58, 0.62, 0.66, 0.72, 0.8, 0.9 })
            {
                cw.TestBarAt(t);
                await Task.Delay(30);
                cw.TestSaveRegion(System.IO.Path.Combine(folder, $"b{frame++:00}_{t:0.00}.png"), film);
            }
            await Task.Delay(300);
            cw.TestMark("Arrow", new Point(area.X + 60, area.Y + 60), new Point(area.X + 220, area.Y + 140));
            cw.TestMark("Rect", new Point(area.X + 260, area.Y + 40), new Point(area.X + 420, area.Y + 200));
            cw.TestColor(3);
            cw.TestMark("Pen", new Point(area.X + 60, area.Y + 260), new Point(area.X + 300, area.Y + 300));
            cw.TestColor(2);
            cw.TestMark("Marker", new Point(area.X + 330, area.Y + 290), new Point(area.X + 560, area.Y + 290));
            cw.TestMark("Blur", new Point(area.X + 450, area.Y + 60), new Point(area.X + 590, area.Y + 150));
            cw.TestColor(0);
            cw.TestText(new Point(area.X + 70, area.Y + 200), "olha aqui!");
            await Task.Delay(300);
            cw.TestSave(System.IO.Path.Combine(folder, "k3_edit.png"));
            cw.TestPop(true);
            await Task.Delay(350);
            cw.TestSave(System.IO.Path.Combine(folder, "k4_colors.png"));
            cw.TestPop(false);
            cw.TestHoldFly();
            var fly = cw.TestFinish(CaptureAction.Copy);
            await Task.Delay(50);
            // the trip to the island, moment by moment (the top of the screen, where it arrives)
            // the real screen around the island (the live island is over the capture now, and takes the picture in)
            var islandMid = Island.PointToScreen(new Point(Island.ActualWidth / 2, _topHidden));
            var grabRect = new System.Drawing.Rectangle((int)islandMid.X - 330, (int)islandMid.Y - 4, 660, 150);
            void GrabScreen(string name)
            {
                using var bmp = new System.Drawing.Bitmap(grabRect.Width, grabRect.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(grabRect.Left, grabRect.Top, 0, 0, bmp.Size);
                bmp.Save(System.IO.Path.Combine(folder, name + ".png"));
            }
            double oldW = Island.ActualWidth;
            int f2 = 0;
            foreach (var t in new[] { 0.3, 0.5, 0.62, 0.68 })
            {
                cw.TestFlyAt(t);
                await Task.Delay(60);
                GrabScreen($"h{f2++:00}_{t:0.00}");
            }
            // from the arrival on, in real time: the island opens while the capture fades over it
            var real = System.Diagnostics.Stopwatch.StartNew();
            while (real.Elapsed.TotalSeconds < 0.7)
            {
                double t = 0.72 + real.Elapsed.TotalSeconds;
                cw.TestFlyAt(t);
                await Task.Delay(45);
                GrabScreen($"h{f2++:00}_{t:0.00}");
                System.IO.File.AppendAllText(log, $"{t:0.00}: {_state} {_compactMode} ilha {Island.ActualWidth:0}x{Island.ActualHeight:0} vis {Island.Visibility} janela {Opacity:0.0} compacto {CompactPanel.Opacity:0.00} aviso {_shotPanel.Opacity:0.00} topo {_topHidden:0} captura {cw.IsVisible} · luz {FocusLight.Visibility} {FocusLight.Opacity:0.00} {(FocusLight.Stroke as SolidColorBrush)?.Color} {FocusLight.Width:0}x{FocusLight.Height:0} trilho {FocusTrack.Opacity:0.00}{Environment.NewLine}");
            }
            cw.TestFlyAt(2);
            await Task.Delay(100);
            // which window draws what: the island, then its glass, left out of the picture
            GrabScreen("x0_tudo");
            // who still has the old width (and shows)?
            void Walk(DependencyObject d, int depth)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is FrameworkElement fe && fe.IsVisible && fe.Opacity > 0.01 && Math.Abs(fe.ActualWidth - oldW) < 8)
                        System.IO.File.AppendAllText(log, $"largura antiga ({oldW:0}): {fe.GetType().Name} '{fe.Name}' {fe.ActualWidth:0}x{fe.ActualHeight:0} opac {fe.Opacity:0.00} pai '{(VisualTreeHelper.GetParent(fe) as FrameworkElement)?.Name}'{Environment.NewLine}");
                    Walk(c, depth + 1);
                }
            }
            Walk(this, 0);
            SetWindowDisplayAffinity(_hwnd, 0x11);
            await Task.Delay(80);
            GrabScreen("x1_sem_ilha");
            SetWindowDisplayAffinity(_hwnd, 0);
            if (_backdrop != null) SetWindowDisplayAffinity(_backdrop.Hwnd, 0x11);
            await Task.Delay(80);
            GrabScreen("x2_sem_vidro");
            if (_backdrop != null) SetWindowDisplayAffinity(_backdrop.Hwnd, 0);
            await fly;
            await Task.Delay(400);
            Note($"copiado: {Clipboard.ContainsImage()} · aviso: {_compactMode} · ilha visível: {Opacity:0}");
            await ShotAsync(folder, "k6_notice", 200);
            await ShotAsync(folder, "k7_notice_later", 1200);
            if (_shotPicture != null) Note($"imagem {_shotPicture.PixelWidth}x{_shotPicture.PixelHeight} · arquivo {_shotFile}");
            Application.Current.Shutdown();
            return;
        }
        // only the shelf's sideways scroll (quick): many files, the wheel walks along them and the thin bar shows
        if (Environment.GetEnvironmentVariable("NOTCHY_SHELFSCROLLTEST") == "1")
        {
            var saved = _shelf.ToList();
            _shelf.Clear();
            for (int i = 1; i <= 10; i++)
            {
                var p = System.IO.Path.Combine(folder, $"arquivo {i}.txt");
                System.IO.File.WriteAllText(p, "x");
                _shelf.Add(p);
            }
            RenderShelf();
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            await Task.Delay(900);
            SelectTab(Tab.Shelf, false);
            await ShotAsync(folder, "h1_start", 1200);
            _shelfSel.SetMode(true);
            _shelfSel.MarkAll();
            await ShotAsync(folder, "h1_marked", 500);
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -360) { RoutedEvent = UIElement.MouseWheelEvent };
            ShelfScroll.RaiseEvent(wheel);
            await ShotAsync(folder, "h2a_gliding", 60);
            await ShotAsync(folder, "h2_wheel", 600);
            ShelfScroll.ScrollToRightEnd();
            await ShotAsync(folder, "h3_end", 400);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "result.txt"), $"tratado {wheel.Handled} rolável {ShelfScroll.ScrollableWidth:0} posição {ShelfScroll.HorizontalOffset:0}");
            // a vertical list too: the news
            _shelfSel.SetMode(false);
            ShowWhatsNew();
            await Task.Delay(1000);
            ToggleNewsList();
            await ShotAsync(folder, "v1_top", 700);
            if (_newsScroll != null)
            {
                var trail = new System.Text.StringBuilder($" | antes {_newsScroll.VerticalOffset:0}:");
                _newsScroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -240) { RoutedEvent = UIElement.MouseWheelEvent });
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(30);
                    trail.Append($" {_newsScroll.VerticalOffset:0}");
                }
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "result.txt"), trail.ToString());
                await ShotAsync(folder, "v2_gliding", 60);
                await ShotAsync(folder, "v3_middle", 600);
                System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "result.txt"), $" | novidades {_newsScroll.VerticalOffset:0} de {_newsScroll.ScrollableHeight:0}");
            }
            _shelf.Clear();
            _shelf.AddRange(saved);
            SaveShelf();
            Application.Current.Shutdown();
            return;
        }
        // only Limpar on the shelf (quick): the files pop, the box swallows the sparks. Puts the saved shelf back after.
        if (Environment.GetEnvironmentVariable("NOTCHY_CLEARTEST") == "1")
        {
            var saved = _shelf.ToList();
            var files = new[] { "foto-praia.png", "relatório.pdf", "músicas.zip" }.Select(n =>
            {
                var p = System.IO.Path.Combine(folder, n);
                System.IO.File.WriteAllText(p, "x");
                return p;
            }).ToList();
            _shelf.Clear();
            _shelf.AddRange(files);
            RenderShelf();
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            await Task.Delay(900);
            SelectTab(Tab.Shelf, false);
            await Task.Delay(1200);
            ShelfClear_Click(this, new RoutedEventArgs());
            foreach (var (name, ms) in new[] { ("c1_pop", 120), ("c2_hang", 250), ("c3_box", 300), ("c4_pull", 350), ("c5_pull", 200), ("c6_closed", 300), ("c7_rest", 1200) })
                await ShotAsync(folder, name, ms);
            _shelf.Clear();
            _shelf.AddRange(saved);
            SaveShelf();
            Application.Current.Shutdown();
            return;
        }
        // only the first files caught by the shelf box (quick); the saved shelf file is left alone
        if (Environment.GetEnvironmentVariable("NOTCHY_SHELFTEST") == "1")
        {
            var files = new[] { "foto-praia.png", "relatório.pdf", "músicas.zip", "notas.txt" }.Select(n =>
            {
                var p = System.IO.Path.Combine(folder, n);
                System.IO.File.WriteAllText(p, "x");
                return p;
            }).ToList();
            _shelf.Clear();
            RenderShelf();
            await Task.Delay(4000); // (the hello wave first)
            _forcedState = IslandState.Expanded;
            UpdateState();
            await Task.Delay(900);
            SelectTab(Tab.Shelf, false);
            await Task.Delay(1200);
            _dragOver = true;
            _dragLast = DateTime.Now.AddSeconds(10);
            _shelfDragPos = new Point(34, -6);
            await ShotAsync(folder, "s1_drag", 600);
            _dragOver = false;
            _shelf.AddRange(files.Take(3));
            ShelfStored(true);
            await ShotAsync(folder, "s2_jump", 150);
            await ShotAsync(folder, "s3_sparks", 150);
            await ShotAsync(folder, "s4_vanish", 250);
            await ShotAsync(folder, "s5_spread", 250);
            await ShotAsync(folder, "s5b_spread", 200);
            await ShotAsync(folder, "s6_done", 900);
            _shelf.Add(files[3]);
            ShelfStored(false);
            await ShotAsync(folder, "s7_next", 120);
            await ShotAsync(folder, "s8_next_done", 600);
            Application.Current.Shutdown();
            return;
        }
        if (Environment.GetEnvironmentVariable("NOTCHY_PASTETEST") == "1")
        {
            await PasteTestAsync(folder);
            Application.Current.Shutdown();
            return;
        }

        // fake a Claude task so the Claude views have content
        _claude.Inject("""{"hook_event_name":"UserPromptSubmit","session_id":"demo","cwd":"C:\\proj\\Notchy","prompt":"Adicionar abas e sons"}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","cwd":"C:\\proj\\Notchy","tool_name":"Read","tool_input":{"file_path":"C:\\proj\\Notchy\\App.xaml"}}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","tool_name":"TodoWrite","tool_input":{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"},{"content":"c","status":"pending"},{"content":"d","status":"pending"}]}}""");
        _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","tool_name":"Edit","tool_input":{"file_path":"C:\\proj\\Notchy\\MainWindow.xaml","old_string":"x","new_string":"<Window x:Class=\"Notchy.MainWindow\"\n        xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"\n        Title=\"Notchy\">"}}""");
        await Task.Delay(300);

        Task Shot(string name) => ShotAsync(folder, name, (int)Dur(900));

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

        // alarm rules: writes alarm.txt with OK / FALHOU per case (made-up alarms, the real clock.json is left alone)
        if (Environment.GetEnvironmentVariable("NOTCHY_ALARMTEST") == "1")
        {
            var log = new System.Text.StringBuilder();
            var mon = new DateTime(2026, 11, 16); // a Monday
            void Case(string name, Alarm a, DateTime[] ticks, int expected)
            {
                var c = new ClockService { DontSave = true };
                c.Alarms.Add(a);
                int fired = 0;
                c.AlarmFired += _ => fired++;
                foreach (var t in ticks) c.Tick(t);
                log.AppendLine($"{(fired == expected ? "OK    " : "FALHOU")} {name}: tocou {fired}x (esperado {expected})");
            }
            Alarm Daily(int h, int m) => new() { Hour = h, Minute = m, Days = 0b1111111, ArmedAt = mon.AddDays(-7) };
            Case("na hora", Daily(7, 30), new[] { mon.AddHours(7.5).AddSeconds(30) }, 1);
            Case("3 min atrasado não toca", Daily(7, 30), new[] { mon.AddHours(7.5).AddMinutes(3) }, 0);
            Case("toca uma vez só", Daily(7, 30), new[] { mon.AddHours(7.5).AddSeconds(5), mon.AddHours(7.5).AddSeconds(40) }, 1);
            Case("23:59 visto à 00:00:30", Daily(23, 59), new[] { mon.AddDays(1).AddSeconds(30) }, 1);
            Case("só segunda, numa terça", new Alarm { Hour = 7, Minute = 30, Days = Alarm.DayBit(DayOfWeek.Monday), ArmedAt = mon.AddDays(-7) }, new[] { mon.AddDays(1).AddHours(7.5) }, 0);
            Case("uma vez, ligado depois da hora", new Alarm { Hour = 7, Minute = 30, Days = 0, ArmedAt = mon.AddHours(7.5).AddMinutes(1) }, new[] { mon.AddHours(7.5).AddMinutes(1.5) }, 0);
            Case("uma vez, desliga depois de tocar", new Alarm { Hour = 7, Minute = 30, Days = 0, ArmedAt = mon }, new[] { mon.AddHours(7.5), mon.AddDays(1).AddHours(7.5) }, 1);
            Case("soneca", new Alarm { Hour = 7, Minute = 30, Days = 0b1111111, ArmedAt = mon, SnoozeUntil = mon.AddHours(7.6) }, new[] { mon.AddHours(7.55), mon.AddHours(7.6).AddSeconds(1) }, 1);
            System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "alarm.txt"), log.ToString());
        }

        // 2.2.9 fixes: header with the update button + waveform, and a long Spotify device name
        if (Environment.GetEnvironmentVariable("NOTCHY_FIXTEST") == "1")
        {
            _updateButton.Visibility = Visibility.Visible;
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Claude, false);
            await Shot("20_header_update");
            _updateButton.Visibility = Visibility.Collapsed;
            await Shot("20_header_noupdate");
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "face.txt"),
                $"fix: music={_media.HasSession && !string.IsNullOrEmpty(_media.Title)} wave={_headerWave.Visibility}" + Environment.NewLine);
            SelectTab(Tab.Music, false);
            QueueButton.Visibility = Visibility.Visible;
            SourceText.Text = "Alto-falante da sala de estar do Pedro (Echo Dot)";
            await Shot("20_music_longdevice");
        }

        // melhorias: clock icon lit by an armed alarm; the volume bar showing over a full-screen game
        if (Environment.GetEnvironmentVariable("NOTCHY_IMPROVETEST") == "1")
        {
            string log = System.IO.Path.Combine(folder, "improve.txt");
            _clock.DontSave = true;
            _clock.Alarms.Add(new Alarm { Hour = 7, Minute = 30, Days = 0b1111111, Name = "Teste" });
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Shot("21_clock_lit");
            System.IO.File.AppendAllText(log, $"clock nav: {NavState(Tab.Clock).motion}" + Environment.NewLine);
            _clock.Alarms.Clear();
            await Shot("21_clock_off");
            System.IO.File.AppendAllText(log, $"clock nav sem alarme: {NavState(Tab.Clock).motion}" + Environment.NewLine);

            // the empty island's eyes pulling the volume bar out
            _forcedState = IslandState.Idle;
            UpdateState();
            await Shot("22_arrive_0_idle");
            _levelTarget = 0.38;
            _forcedState = IslandState.Volume;
            UpdateState();
            await ShotAsync(folder, "22_arrive_1", 90);
            await ShotAsync(folder, "22_arrive_2", 110);
            await ShotAsync(folder, "22_arrive_3", 140);
            await ShotAsync(folder, "22_arrive_4", 600);
            _forcedState = IslandState.Idle;
            UpdateState();
            await ShotAsync(folder, "23_depart_1", 90);
            await ShotAsync(folder, "23_depart_2", 110);
            await ShotAsync(folder, "23_depart_3", 120);
            await ShotAsync(folder, "23_depart_4", 500);
            System.IO.File.AppendAllText(log, $"volta: idle={IdlePanel.Opacity:0.##} barra={VolumePanel.Opacity:0.##}" + Environment.NewLine);

            _forcedState = null;
            Collapse();
            S.GameMode = false; // keep CheckFullscreen from undoing the fake full screen
            _hiddenForFullscreen = true;
            ApplyFullscreenHide();
            await System.Threading.Tasks.Task.Delay(300);
            System.IO.File.AppendAllText(log, $"tela cheia, parado: {Island.Visibility}" + Environment.NewLine);
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(4);
            UpdateState();
            await ShotAsync(folder, "24_fs_drop_1", 90);
            System.IO.File.AppendAllText(log, $"tela cheia, descendo: y={IslandDrop.Y:0} w={Island.ActualWidth:0}" + Environment.NewLine);
            await ShotAsync(folder, "24_fs_drop_2", 400);
            System.IO.File.AppendAllText(log, $"tela cheia, volume: {_state} {Island.Visibility} y={IslandDrop.Y:0}" + Environment.NewLine);
            _volumeOsdUntil = DateTime.MinValue;
            UpdateState();
            await ShotAsync(folder, "24_fs_rise_1", 130);
            System.IO.File.AppendAllText(log, $"tela cheia, subindo: y={IslandDrop.Y:0} barra={VolumePanel.Opacity:0.##}" + Environment.NewLine);
            // changed again on the way up: turns around and comes back down
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.2);
            UpdateState();
            await ShotAsync(folder, "24_fs_back_1", 400);
            System.IO.File.AppendAllText(log, $"tela cheia, voltou: {_state} y={IslandDrop.Y:0}" + Environment.NewLine);
            _volumeOsdUntil = DateTime.MinValue;
            UpdateState();
            await ShotAsync(folder, "24_fs_gone", 700);
            System.IO.File.AppendAllText(log, $"tela cheia, depois: {_state} {Island.Visibility} y={IslandDrop.Y:0}" + Environment.NewLine);
            _hiddenForFullscreen = false;
            ApplyFullscreenHide();
        }

        // Full screen: every kind of island (Claude, download, focus, timer, ringing, notification) going full screen,
        // the volume bar coming down over it and going back up, then leaving full screen
        if (Environment.GetEnvironmentVariable("NOTCHY_FSTEST") == "1")
        {
            string fsLog = System.IO.Path.Combine(folder, "fullscreen.txt");
            void Log(string step) => System.IO.File.AppendAllText(fsLog,
                $"{step,-22} ilha={Island.Visibility,-7} estado={_state}/{_compactMode} y={IslandDrop.Y:0} " +
                $"claude={ClaudeLight.Visibility}:{ClaudeLight.Opacity:0.##} download={TransferLight.Visibility}:{TransferLight.Opacity:0.##} " +
                $"foco={FocusLight.Visibility}:{FocusLight.Opacity:0.##} bolhaClaude={ClaudePod.Visibility} bolhaDl={DownloadPod.Visibility} pilula={_noticePill.Visibility}" + Environment.NewLine);
            bool claudeWas = S.ClaudeShowInCompact, gameWas = S.GameMode, notifWas = S.NotifyFullscreen;
            S.GameMode = false; // keep CheckFullscreen from undoing the fake full screen
            _forcedState = null;
            _hoverActive = _clickOpened = false;

            async Task Case(string name, Action setup, Action teardown, bool claude = false)
            {
                S.ClaudeShowInCompact = claude;
                setup();
                UpdateState();
                await ShotAsync(folder, $"fs_{name}_0_normal", 1200);
                Log($"{name} normal");
                SetFullscreen(true);
                await ShotAsync(folder, $"fs_{name}_1_tela_cheia", 600);
                Log($"{name} tela cheia");
                ShowVolumeLevel(0.4, false);
                _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.2);
                UpdateState();
                await ShotAsync(folder, $"fs_{name}_2_descendo", 110);
                Log($"{name} descendo");
                await ShotAsync(folder, $"fs_{name}_3_barra", 400);
                Log($"{name} barra");
                await ShotAsync(folder, $"fs_{name}_4_subindo", 820);
                Log($"{name} subindo");
                await ShotAsync(folder, $"fs_{name}_5_depois", 900);
                Log($"{name} depois");
                SetFullscreen(false);
                await ShotAsync(folder, $"fs_{name}_6_saiu", 900);
                Log($"{name} saiu");
                teardown();
                UpdateState();
                await Task.Delay(900);
            }

            await Case("claude", () => { }, () => { }, claude: true);
#if DEBUG
            await Case("download", () =>
            {
                _transfers.Fake = new TransferInfo(TransferKind.Download, "filme.mp4", 1, 0.42, 420_000_000, 1_000_000_000, 8_000_000, TimeSpan.FromSeconds(70));
                PollTransfers();
            }, () => { _transfers.Fake = null; PollTransfers(); });
#endif
            await Case("foco", () => _focus.Start(), () => _focus.Reset());
            ClockTimer? tm = null;
            await Case("timer", () => tm = _clock.Start(5 * 60, "☕ Café"), () => _clock.Remove(tm!));
            await Case("tocando", () => { tm = _clock.Start(5 * 60, "☕ Café"); OnClockTimerFinished(tm); }, () => { StopRing(); _clock.Remove(tm!); });
            S.NotifyFullscreen = true;
            await Case("notificacao", () => OnNotice(new Notice(999981, "WhatsApp", "", "Maria", "Já chegou?", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) }), () => { });

            // a timer going off while already in full screen, with the option on and off
            bool ringWas = S.RingFullscreen;
            S.NotifyFullscreen = false;
            foreach (bool on in new[] { true, false })
            {
                S.RingFullscreen = on;
                string n = on ? "alarme_ligado" : "alarme_desligado";
                SetFullscreen(true);
                await Task.Delay(400);
                tm = _clock.Start(5 * 60, "⏰ Acordar");
                OnClockTimerFinished(tm);
                await ShotAsync(folder, $"fs_{n}_1_tocando", 900);
                Log($"{n} tocando");
                _hoverRequested = _hoverActive = true; // the mouse on the card, clicking "Parar"
                StopRing();
                _clock.Remove(tm);
                await ShotAsync(folder, $"fs_{n}_2_parou", 60);
                Log($"{n} parou");
                await ShotAsync(folder, $"fs_{n}_3_depois", 1500);
                Log($"{n} depois");
                _hoverRequested = _hoverActive = false;
                SetFullscreen(false);
                await Task.Delay(600);
            }
            S.RingFullscreen = ringWas;

            // a notification card over the game, with the mouse on it: dismissed, then one that runs out
            S.NotifyFullscreen = true;
            foreach (bool dismiss in new[] { true, false })
            {
                string n = dismiss ? "cartao_dispensado" : "cartao_expirou";
                SetFullscreen(true);
                await Task.Delay(400);
                OnNotice(new Notice(dismiss ? 999971u : 999972u, "WhatsApp", "", "Maria", "Bora?", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
                await ShotAsync(folder, $"fs_{n}_1_cartao", 700);
                Log($"{n} cartao");
                _hoverRequested = _hoverActive = true;
                if (dismiss) DismissNotice();
                else _transientUntil = DateTime.UtcNow; // its time is up (the mouse isn't really on it)
                await ShotAsync(folder, $"fs_{n}_2_fim", 120);
                Log($"{n} fim");
                await ShotAsync(folder, $"fs_{n}_3_depois", 1500);
                Log($"{n} depois");
                _hoverRequested = _hoverActive = false;
                SetFullscreen(false);
                await Task.Delay(600);
            }

            S.ClaudeShowInCompact = claudeWas;
            S.GameMode = gameWas;
            S.NotifyFullscreen = notifWas;
        }

        // Notificações: grouped by app, a group folded and opened, cleared with its X, undone, one cleared, the undo running out
        if (Environment.GetEnvironmentVariable("NOTCHY_NOTIFCLEARTEST") == "1")
        {
            string nlog = System.IO.Path.Combine(folder, "notifclear.txt");
            void NLog(string line) => System.IO.File.AppendAllText(nlog, line + Environment.NewLine);
            _notices.History.Clear();
            var now = DateTime.Now;
            Notice Fake(uint id, string app, string who, string text, int minAgo, Color accent) => new(id, app, "", who, text, now.AddMinutes(-minAgo), null, false) { Accent = accent };
            var wa = Color.FromRgb(0x25, 0xD3, 0x66);
            var dc = Color.FromRgb(0x58, 0x65, 0xF2);
            var gm = Color.FromRgb(0xEA, 0x43, 0x35);
            foreach (var n in new[] { Fake(1, "Gmail", "Fatura de novembro", "Sua fatura está disponível", 120, gm), Fake(2, "Discord", "Ana", "te mandei o link", 43, dc),
                         Fake(3, "WhatsApp", "João", "Bora amanhã?", 34, wa), Fake(4, "Discord", "#geral", "Lucas: alguém online?", 19, dc),
                         Fake(5, "WhatsApp", "Grupo da família", "Pai: Foto", 12, wa), Fake(7, "WhatsApp", "Maria", "tá aí?", 6, wa),
                         Fake(8, "WhatsApp", "Maria", "e acabou o coisa não é", 3, wa), Fake(6, "WhatsApp", "Maria", "Já chegou em casa?", 1, wa) })
                _notices.AddLocal(n);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Task.Delay(500);
            OpenSubPage(Loc.T("notif.title"), NoticesPage);
            await ShotAsync(folder, "nc_1_page", 600);

            // the three from Maria: a stack, opened and gathered again
            var front = TestFind<TextBlock>(Island, t => t.Text == "Já chegou em casa?");
            front?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent }); // (the real mouse goes this way: each element turns it into its PreviewMouseLeftButtonUp)
            await ShotAsync(folder, "nc_s1_opening", 150);
            await ShotAsync(folder, "nc_s2_open", 400);
            NLog($"pilha aberta: {_noticeOpenStacks.Count == 1}");
            var sv = TestFind<ScrollViewer>(_subPage, _ => true);
            NLog($"rolagem: {(sv == null ? "nenhuma" : $"pode rolar {sv.ScrollableHeight:0} px, visível {sv.ViewportHeight:0} de {sv.ExtentHeight:0}")}");
            TestClick(TestFind<TextBlock>(Island, t => t.Text == Loc.T("notif.less")));
            await ShotAsync(folder, "nc_s3_closing", 150);
            await ShotAsync(folder, "nc_s4_closed", 400);
            NLog($"pilha fechada: {_noticeOpenStacks.Count == 0}");

            TestClick(TestFind<TextBlock>(Island, t => t.Text == "DISCORD"));
            await ShotAsync(folder, "nc_2_folding", 120);
            await ShotAsync(folder, "nc_3_folded", 400);
            NLog($"recolhido: {_noticeFolded.Contains("Discord")}");
            TestClick(TestFind<TextBlock>(Island, t => t.Text == "DISCORD"));
            await ShotAsync(folder, "nc_4_opened", 500);
            NLog($"aberto de novo: {!_noticeFolded.Contains("Discord")}");

            TestClick(TestFind<Border>(Island, b => b.ToolTip as string == Loc.F("notif.clear.app", "WhatsApp")));
            await ShotAsync(folder, "nc_5_clearing", 110);
            await ShotAsync(folder, "nc_6_cleared", 500);
            NLog($"WhatsApp limpo: restam {_notices.History.Count} ({string.Join(", ", _notices.History.Select(n => n.App))}), desfazer na tela: {_noticeCleared != null}");
            TestClick(TestFind<TextBlock>(Island, t => t.Text == Loc.T("dl.undo")));
            await ShotAsync(folder, "nc_7_undone", 500);
            NLog($"desfeito: {_notices.History.Count} ({string.Join(", ", _notices.History.Select(n => n.Title))})");

            ClearNotices(new List<Notice> { _notices.History.First(n => n.Title == "Ana") }, Loc.T("notif.cleared.one"), null);
            await ShotAsync(folder, "nc_8_one", 400);
            NLog($"uma limpa: {_notices.History.Count}");
            await ShotAsync(folder, "nc_9_undo_gone", (int)(NoticeUndoSeconds * 1000) + 300);
            NLog($"depois do tempo: desfazer na tela {_noticeCleared != null}, restam {_notices.History.Count}");

            // closing with the page open: it goes up into the island; back soon, the same page; later, the tab
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            UpdateState();
            await ShotAsync(folder, "nc_c1_closing", 45);
            await ShotAsync(folder, "nc_c2_closing", 80);
            await ShotAsync(folder, "nc_c3_closed", 500);
            NLog($"fechou: estado {_state}, página guardada {_subOpen}");
            _forcedState = IslandState.Expanded;
            UpdateState();
            await ShotAsync(folder, "nc_c4_back_soon", 600);
            NLog($"voltou logo: página {(_subOpen ? _subTitle.Text : "nenhuma")}, painel {ExpandedPanel.Opacity:0.##}");
            _forcedState = null;
            UpdateState();
            await Task.Delay((int)(SubReturnSeconds * 1000) + 400);
            _forcedState = IslandState.Expanded;
            UpdateState();
            await ShotAsync(folder, "nc_c5_back_later", 600);
            NLog($"voltou depois: página {(_subOpen ? _subTitle.Text : "nenhuma")}, aba {_tab}");
            OpenSubPage(Loc.T("notif.title"), NoticesPage);

            // an icon with a wide see-through margin: only the drawn part is shown
            var px = new byte[64 * 64 * 4];
            for (int y = 16; y < 48; y++) for (int x = 16; x < 48; x++) { int i = (y * 64 + x) * 4; px[i + 2] = 255; px[i + 3] = 255; }
            var padded = System.Windows.Media.Imaging.BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, px, 64 * 4);
            NLog($"area desenhada: {DesktopAppIcon.DrawnArea(padded)} (esperado 0,25;0,25;0,5;0,5)");

            CloseSubPage(animate: false);
            _notices.History.Clear();
            _forcedState = null;
            Collapse();
        }

        // Music paused and played again quickly while Claude works (bubble at the side): nothing may stay over the song's text.
        // A made-up song with made-up lyrics: sung lines and instrumental stretches (the island's own eyes) every 1.5 s.
        if (Environment.GetEnvironmentVariable("NOTCHY_MUSICPAUSETEST") == "1")
        {
            string plog = System.IO.Path.Combine(folder, "musicpause.txt");
            void PLog(string line) => System.IO.File.AppendAllText(plog, line + Environment.NewLine);
            PLog("início");
            bool claudeWas = S.ClaudeShowInCompact, podWas = S.ClaudePod;
            S.ClaudeShowInCompact = true;
            S.ClaudePod = true;
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            _claude.Inject("""{"hook_event_name":"PreToolUse","session_id":"demo","tool_name":"Edit","tool_input":{"file_path":"a.cs"}}""");
            PLog("claude ok");
            _media.Fake("Waking up", "Teste", true);
            PLog("música falsa ok");
            await Task.Delay(700);
            var fakeLines = new List<LyricLine>();
            for (int i = 0; i < 200; i++) fakeLines.Add(new LyricLine(TimeSpan.FromSeconds(i * 1.5), i % 2 == 0 ? "Waking up to you never felt so real" : ""));
            _lyricsCts?.Cancel();
            _lines = fakeLines;
            _lineIdx = int.MinValue;
            _titleUntil = DateTime.UtcNow;
            PLog("letra ok");
            await ShotAsync(folder, "pp_0_playing", 2500);
            PLog("print ok");

            string Look() => $"pos={_media.Position.TotalSeconds:0.0}s linha={_lineIdx} letra={(_lines == null ? "nula" : _lines.Count.ToString())} tocando={_media.IsPlaying} estado={_state}/{_compactMode} carinha={(_compactFaceOn ? "ligada" : "desligada")}:{_compactFace.Opacity:0.##} instrumental={_compactInstrumental} bloqueada={_islandFaceBlocked} " +
                             $"texto=\"{CompactText.Text}\" claudePanel={ClaudePanel.Opacity:0.##} bolha={ClaudePod.Visibility}:{ClaudePod.Opacity:0.##} transformando={_podMorphing}";
            PLog("tocando: " + Look());
            int bugs = 0;
            async Task Watch(string name, int ms)
            {
                // the bug: the island's eyes on screen while a sung line (or the title) is shown
                for (int t = 0; t < ms; t += 50)
                {
                    await Task.Delay(50);
                    bool shown = _compactFace.IsVisible && _compactFace.Opacity > 0.15 && CompactText.Text.Length > 0 && CompactText.IsVisible && CompactPanel.Opacity > 0.15 && _state == IslandState.Compact;
                    if (shown)
                    {
                        bugs++;
                        PLog($"  BUG {name} +{t}ms: " + Look());
                        await Shot($"pp_bug_{name}_{t}");
                    }
                }
            }
            foreach (int ms in new[] { 60, 120, 200, 350, 500, 800, 1200, 2000 })
            {
                _media.Fake("Waking up", "Teste", false);
                await Watch($"pausa{ms}", ms);
                PLog($"  fim da pausa {ms} ms: " + Look());
                _media.Fake("Waking up", "Teste", true);
                if (ms is 60 or 350 or 1200)
                {
                    int waited = 0;
                    foreach (int at in new[] { 80, 200, 350, 550, 800, 1200, 2000 })
                    {
                        await ShotAsync(folder, $"pp_f_{ms}_{at}", at - waited);
                        waited = at;
                        PLog($"    +{at} ms: " + Look());
                    }
                }
                await Watch($"volta{ms}", 3000);
                PLog($"pausa {ms} ms: " + Look());
            }
            for (int k = 0; k < 6; k++) // several quick ones in a row
            {
                _media.Fake("Waking up", "Teste", k % 2 == 1);
                await Watch($"seguidas{k}", 90);
            }
            _media.Fake("Waking up", "Teste", true);
            await Watch("seguidas_fim", 3000);
            PLog($"seguidas: " + Look());
            PLog($"total de quadros com o bug: {bugs}");

            // the same with the focus' bubble and the download's bubble (same move, same undo)
            async Task Other(string name, Action start, Action stop)
            {
                start();
                await Task.Delay(2500);
                PLog($"{name} tocando: estado={_state}/{_compactMode} bolhaFoco={ClaudePod.Visibility}:{ClaudePod.Opacity:0.##} bolhaDl={DownloadPod.Visibility}:{DownloadPod.Opacity:0.##}");
                foreach (int ms in new[] { 60, 350 })
                {
                    _media.Fake("Waking up", "Teste", false);
                    await Task.Delay(ms);
                    _media.Fake("Waking up", "Teste", true);
                    int waited = 0;
                    foreach (int at in new[] { 120, 350, 800 })
                    {
                        await ShotAsync(folder, $"pp_{name}_{ms}_{at}", at - waited);
                        waited = at;
                    }
                    await Task.Delay(1500);
                    PLog($"{name} pausa {ms} ms: estado={_state}/{_compactMode} bolhaFoco={ClaudePod.Visibility}:{ClaudePod.Opacity:0.##} x={PodX.X:0} bolhaDl={DownloadPod.Visibility}:{DownloadPod.Opacity:0.##} x={DlPodX.X:0} transformando={_podMorphing}/{_dlPodMorphing}");
                }
                stop();
                await Task.Delay(1500);
            }
            S.ClaudeShowInCompact = false;
            await Task.Delay(1500);
            await Other("foco", () => _focus.Start(), () => _focus.Reset());
#if DEBUG
            bool progWas = S.TransferProgress;
            S.TransferProgress = true;
            await Other("download", () =>
            {
                _transfers.Fake = new TransferInfo(TransferKind.Download, "filme.mp4", 1, 0.42, 420_000_000, 1_000_000_000, 8_000_000, TimeSpan.FromSeconds(70));
                PollTransfers();
            }, () => { _transfers.Fake = null; PollTransfers(); });
            S.TransferProgress = progWas;
#endif
            S.ClaudeShowInCompact = claudeWas;
            S.ClaudePod = podWas;
        }

        // Agendas conectadas: the iCal reader against a sample with every case, then that calendar on the island
        if (Environment.GetEnvironmentVariable("NOTCHY_ICSTEST") == "1")
        {
            string ilog = System.IO.Path.Combine(folder, "ics.txt");
            int fails = 0;
            void Check(string what, bool ok, string got)
            {
                if (!ok) fails++;
                System.IO.File.AppendAllText(ilog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            }
            var ics = string.Join("\r\n", new[]
            {
                "BEGIN:VCALENDAR", "VERSION:2.0", "X-WR-CALNAME:Trabalho",
                "BEGIN:VTIMEZONE", "TZID:America/Sao_Paulo", "END:VTIMEZONE",
                // timed, in São Paulo
                "BEGIN:VEVENT", "UID:a1", "SUMMARY:Reunião\\, sala 3", "DTSTART;TZID=America/Sao_Paulo:20261117T140000", "DTEND;TZID=America/Sao_Paulo:20261117T150000",
                "BEGIN:VALARM", "TRIGGER:-PT10M", "DESCRIPTION:alarme", "END:VALARM", "END:VEVENT",
                // UTC
                "BEGIN:VEVENT", "UID:a2", "SUMMARY:Ligação com Lisboa", "DTSTART:20261118T120000Z", "DTEND:20261118T123000Z", "END:VEVENT",
                // all-day over three days
                "BEGIN:VEVENT", "UID:a3", "SUMMARY:Férias", "DTSTART;VALUE=DATE:20261123", "DTEND;VALUE=DATE:20261126", "END:VEVENT",
                // weekly on Monday and Wednesday, 4 times
                "BEGIN:VEVENT", "UID:a4", "SUMMARY:Inglês", "DTSTART;TZID=America/Sao_Paulo:20261116T190000", "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=4", "END:VEVENT",
                // every 2 days until the 22nd, except the 20th; the 18th moved to 10:00
                "BEGIN:VEVENT", "UID:a5", "SUMMARY:Remédio", "DTSTART;TZID=America/Sao_Paulo:20261116T080000", "RRULE:FREQ=DAILY;INTERVAL=2;UNTIL=20261122T235959Z",
                "EXDATE;TZID=America/Sao_Paulo:20261120T080000", "END:VEVENT",
                "BEGIN:VEVENT", "UID:a5", "SUMMARY:Remédio (mais tarde)", "RECURRENCE-ID;TZID=America/Sao_Paulo:20261118T080000",
                "DTSTART;TZID=America/Sao_Paulo:20261118T100000", "END:VEVENT",
                // the 2nd Tuesday of each month
                "BEGIN:VEVENT", "UID:a6", "SUMMARY:Condomínio", "DTSTART;VALUE=DATE:20261110", "RRULE:FREQ=MONTHLY;BYDAY=2TU", "END:VEVENT",
                // every year, and a cancelled one
                "BEGIN:VEVENT", "UID:a7", "SUMMARY:Aniversário da Ana", "DTSTART;VALUE=DATE:20101125", "RRULE:FREQ=YEARLY", "END:VEVENT",
                "BEGIN:VEVENT", "UID:a8", "STATUS:CANCELLED", "SUMMARY:Cancelado", "DTSTART:20261119T150000Z", "END:VEVENT",
                // a folded long line
                "BEGIN:VEVENT", "UID:a9", "SUMMARY:Um nome muito compri", " do que foi dobrado", "DTSTART;VALUE=DATE:20261127", "END:VEVENT",
                "END:VCALENDAR"
            });
            var (name, evs) = IcsCalendar.Parse(ics, new DateTime(2026, 11, 1), new DateTime(2027, 2, 28));
            string Days(string n) => string.Join(" ", evs.Where(e => e.Name == n).Select(e => e.Date.ToString("dd/MM") + (e.HasTime ? $" {e.Hour:00}:{e.Minute:00}" : "")));
            Check("nome da agenda", name == "Trabalho", name ?? "nulo");
            Check("com fuso (14:00 em São Paulo)", Days("Reunião, sala 3") == $"17/11 {new DateTime(2026, 11, 17, 17, 0, 0, DateTimeKind.Utc).ToLocalTime():HH:mm}", Days("Reunião, sala 3"));
            Check("UTC", Days("Ligação com Lisboa") == $"18/11 {new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc).ToLocalTime():HH:mm}", Days("Ligação com Lisboa"));
            Check("vários dias", Days("Férias") == "23/11 24/11 25/11", Days("Férias"));
            Check("semanal seg/qua 4x", Days("Inglês").Split(' ').Where((_, i) => i % 2 == 0).SequenceEqual(new[] { "16/11", "18/11", "23/11", "25/11" }), Days("Inglês"));
            Check("a cada 2 dias até 22, sem o 20, o 18 movido", Days("Remédio").Replace(" 08:00", "") == "16/11 22/11" && Days("Remédio (mais tarde)") == "18/11 10:00", Days("Remédio") + " | " + Days("Remédio (mais tarde)"));
            Check("2ª terça do mês", Days("Condomínio").StartsWith("10/11 08/12 12/01 09/02"), Days("Condomínio"));
            Check("todo ano", Days("Aniversário da Ana") == "25/11", Days("Aniversário da Ana"));
            Check("cancelado fica de fora", Days("Cancelado") == "", Days("Cancelado"));
            Check("linha dobrada", Days("Um nome muito comprido que foi dobrado") == "27/11", string.Join(" / ", evs.Select(e => e.Name).Distinct()));
            Check("evento ignora o alarme", evs.Count(e => e.Name == "alarme") == 0, "");
            System.IO.File.AppendAllText(ilog, $"falhas: {fails}" + Environment.NewLine);

            // the real way, through the internet: a public Google calendar (Brazil's holidays), then a broken link
            _calendar.DontSave = true;
            string? err = await _calendar.AddExternalAsync("https://calendar.google.com/calendar/ical/pt-br.brazilian%23holiday%40group.v.calendar.google.com/public/basic.ics", "google");
            var real = _calendar.Externals.LastOrDefault();
            System.IO.File.AppendAllText(ilog, $"internet: erro={err ?? "nenhum"} nome={real?.Name} eventos={real?.Events.Count} primeiro={real?.Events.FirstOrDefault()?.Name} {real?.Events.FirstOrDefault()?.Date:dd/MM/yyyy}" + Environment.NewLine);
            if (real != null) _calendar.RemoveExternal(real);
            string? bad = await _calendar.AddExternalAsync("https://calendar.google.com/calendar/ical/nao-existe-123/public/basic.ics", "google");
            System.IO.File.AppendAllText(ilog, $"link quebrado: {bad}" + Environment.NewLine);
            string? notLink = await _calendar.AddExternalAsync("isso não é um link", "google");
            System.IO.File.AppendAllText(ilog, $"não é link: {notLink}" + Environment.NewLine);

            // that calendar, as if connected (not saved), on the island and in the settings
            _calendar.DontSave = true;
            var ext = new ExternalCalendar { Name = "Trabalho", Url = "https://example.com/basic.ics", Provider = "google", Color = CalendarService.ExternalColors[0], LastSync = DateTime.Now.AddMinutes(-4) };
            var shift = DateTime.Today - new DateTime(2026, 11, 17); // (the sample's days moved to around today)
            foreach (var e in evs) { e.Date = e.Date + shift; e.Source = ext; }
            ext.Events = evs;
            _calendar.Externals.Add(ext);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Task.Delay(500);
            OpenCalendar(DateTime.Today);
            await ShotAsync(folder, "ics_1_island", 900);
            CloseSubPage(animate: false);
            _forcedState = null;
            Collapse();
        }

        // Copiados: real copies through the clipboard (the saved history is left alone), the glow, the tab
        if (Environment.GetEnvironmentVariable("NOTCHY_CLIPTEST") == "1")
        {
            string clog = System.IO.Path.Combine(folder, "clip.txt");
            int cfails = 0;
            void CCheck(string what, bool ok, string got)
            {
                if (!ok) cfails++;
                System.IO.File.AppendAllText(clog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            }
            var saved = _clip.Items.ToList();
            _clip.DontSave = true;
            _clip.Items.Clear();
            bool wasPaused = S.ClipPaused;
            S.ClipPaused = false;
            _forcedState = IslandState.Idle;
            UpdateState();
            await Task.Delay(800);

            async Task Copy(Action set) { set(); await Task.Delay(500); }
            ClipItem? Top() => _clip.Items.FirstOrDefault();

            await Copy(() => System.Windows.Clipboard.SetText("Reunião remarcada para quinta às 14h, sala 3. Leva o relatório impresso."));
            CCheck("texto", Top()?.Kind == ClipKind.Text && Top()!.Text.StartsWith("Reunião"), $"{Top()?.Kind} {Top()?.App}");
            await Copy(() => System.Windows.Clipboard.SetText("https://github.com/notchy/notchy"));
            CCheck("link", Top()?.Kind == ClipKind.Link, $"{Top()?.Kind}");
            System.Windows.Clipboard.SetText("#E8825F");
            await ShotAsync(folder, "clip_0_glow", 140);
            await Task.Delay(400);
            CCheck("cor", Top()?.Kind == ClipKind.Color, $"{Top()?.Kind}");
            int before = _clip.Items.Count;
            await Copy(() =>
            {
                var d = new DataObject(DataFormats.UnicodeText, "senha-secreta-123");
                d.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[] { 1, 0, 0, 0 }));
                System.Windows.Clipboard.SetDataObject(d, true);
            });
            CCheck("senha marcada fica de fora", _clip.Items.Count == before && Top()?.Text != "senha-secreta-123", $"{_clip.Items.Count} itens");
            await Copy(() =>
            {
                var d = new DataObject(DataFormats.UnicodeText, "não guardar");
                d.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(BitConverter.GetBytes(0)));
                System.Windows.Clipboard.SetDataObject(d, true);
            });
            CCheck("“não guardar no histórico” fica de fora", Top()?.Text != "não guardar", $"{Top()?.Text}");
            var bmpSrc = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xFF, 0x9A, 0x62), Color.FromRgb(0x4A, 0x3A, 0x8A), 45), null, new Rect(0, 0, 320, 200));
            bmpSrc.Render(dv);
            await Copy(() => System.Windows.Clipboard.SetImage(bmpSrc));
            CCheck("imagem", Top()?.Kind == ClipKind.Image && System.IO.File.Exists(Top()!.ImagePath) && Top()!.Width == 320, $"{Top()?.Kind} {Top()?.Width}x{Top()?.Height}");
            await Copy(() => System.Windows.Clipboard.SetImage(bmpSrc));
            CCheck("mesma imagem não repete", _clip.Items.Count(x => x.Kind == ClipKind.Image) == 1, $"{_clip.Items.Count(x => x.Kind == ClipKind.Image)}");
            string tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Relatório final.pdf");
            System.IO.File.WriteAllText(tmpFile, "x");
            await Copy(() => System.Windows.Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { tmpFile }));
            CCheck("arquivo", Top()?.Kind == ClipKind.Files, $"{Top()?.Kind}");
            await Copy(() => System.Windows.Clipboard.SetText("https://github.com/notchy/notchy"));
            CCheck("copiar de novo sobe sem repetir", Top()?.Kind == ClipKind.Link && _clip.Items.Count(x => x.Kind == ClipKind.Link) == 1, $"{_clip.Items.Count} itens");
            var text = _clip.Items.First(x => x.Kind == ClipKind.Text);
            await _clip.CopyBack(text);
            await Task.Delay(400);
            CCheck("clicar copia de volta", System.Windows.Clipboard.GetText() == text.Text && Top() == text && _clip.Items.Count(x => x.Kind == ClipKind.Text) == 1, System.Windows.Clipboard.GetText());
            S.ClipPaused = true;
            await Copy(() => System.Windows.Clipboard.SetText("pausado"));
            CCheck("pausado não guarda", Top()?.Text != "pausado", $"{Top()?.Text}");
            S.ClipPaused = false;

            // the tab
            _clip.TogglePin(_clip.Items.First(x => x.Kind == ClipKind.Color));
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Clip, false);
            await ShotAsync(folder, "clip_1_tab", 900);
            var imgRow = TestFind<TextBlock>(ClipTab, t => t.Text.StartsWith(Loc.F("clip.image", 320, 200)));
            DependencyObject? up = imgRow;
            while (up != null && !(up is Border { Child: Grid })) up = VisualTreeHelper.GetParent(up);
            if (up is Border r) ShowClipPicture(_clip.Items.First(x => x.Kind == ClipKind.Image), r);
            await ShotAsync(folder, "clip_2_hover_image", 400);
            _clipOver.Children.Clear();
            _clipFilter = ClipKind.Image;
            RenderClip();
            await ShotAsync(folder, "clip_3_filter_images", 300);
            _clipFilter = null;
            _clipQuery = "github";
            RenderClip();
            await ShotAsync(folder, "clip_4_search", 300);
            _clipQuery = "";
            ClearClip(new List<ClipItem> { _clip.Items.First(x => x.Kind == ClipKind.Files) }, Loc.T("clip.deleted"), slide: false);
            await ShotAsync(folder, "clip_5_deleted_undo", 400);
            CCheck("apagar", !_clip.Items.Any(x => x.Kind == ClipKind.Files), "");
            TestClick(TestFind<TextBlock>(ClipTab, t => t.Text == Loc.T("dl.undo")));
            await Task.Delay(200);
            CCheck("desfazer", _clip.Items.Any(x => x.Kind == ClipKind.Files), "");
            // private windows: the names browsers give them, then a real one (Edge InPrivate) opened and closed again
            // (Opera ignores --private when it's already open: its "(privada)" name was checked by hand)
            foreach (var (title, name, want) in new[]
            {
                ("about:blank - Opera", "about:blank - Opera (privada)", true),
                ("about:blank - Opera", "about:blank - Opera", false),
                ("Nova guia - Google Chrome", "Nova guia - Google Chrome (Incognito)", true),
                ("Nova guia - Google Chrome", "Nova guia - Google Chrome (anônima)", true),
                ("Nova guia - Google Chrome", "Nova guia - Google Chrome (Pessoa 1)", false),
                ("Private notes (draft) - Google Chrome", "Private notes (draft) - Google Chrome", false),
                ("about:blank — [InPrivate] — Microsoft​ Edge", "", true),
                ("Página — Navegação privativa do Mozilla Firefox", "Página — Navegação privativa do Mozilla Firefox", true),
                ("Private browsing tips — Mozilla Firefox", "Private browsing tips — Mozilla Firefox", false),
            })
                CCheck($"janela {(want ? "privada" : "normal")}: {name.Length switch { 0 => title, _ => name }}", ClipboardService.IsPrivateName(title, name) == want, "");
            async Task LivePrivate(string exe, string arg, string label)
            {
                if (!System.IO.File.Exists(exe)) return;
                // every browser window (a browser has several): the new ones are the test's, and only those get closed
                static List<IntPtr> Windows() => System.Windows.Automation.AutomationElement.RootElement
                    .FindAll(System.Windows.Automation.TreeScope.Children, new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1"))
                    .Cast<System.Windows.Automation.AutomationElement>().Select(e => new IntPtr(e.Current.NativeWindowHandle)).ToList();
                var before = Windows();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arg + " about:blank") { UseShellExecute = false });
                await Task.Delay(4000);
                var fresh = Windows().Except(before).ToList();
                CCheck($"{label} de verdade é reconhecida", fresh.Count > 0 && fresh.Any(ClipboardService.IsPrivateWindow), $"{fresh.Count} janela(s) nova(s)");
                foreach (var h in fresh) Native.CloseWindow(h);
            }
            await LivePrivate(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "--inprivate", "janela InPrivate do Edge");
            CCheck("a própria ilha não é privada", !ClipboardService.IsPrivateWindow(_hwnd), "");

            // text in images: a picture with words, read by Windows, then found by the search
            var ocrBmp = new System.Windows.Media.Imaging.RenderTargetBitmap(640, 160, 96, 96, PixelFormats.Pbgra32);
            var ocrDv = new DrawingVisual();
            using (var dc = ocrDv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 160));
                dc.DrawText(new FormattedText("Boleto vence dia 20", Loc.Culture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 44, Brushes.Black, 1.0), new Point(30, 50));
            }
            ocrBmp.Render(ocrDv);
            await Copy(() => System.Windows.Clipboard.SetImage(ocrBmp));
            var withText = Top();
            for (int i = 0; i < 30 && withText?.Ocr == null; i++) await Task.Delay(200);
            CCheck("lê o texto da imagem", withText?.Ocr?.Contains("Boleto", StringComparison.OrdinalIgnoreCase) == true, $"“{withText?.Ocr}”");
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Clip, false);
            _clipQuery = "boleto";
            RenderClip();
            CCheck("busca acha a imagem pelo texto", _clipRows.Count == 1 && _clipRows[0].item == withText, $"{_clipRows.Count} resultado(s)");
            await ShotAsync(folder, "clip_11_ocr_search", 500);
            _clipQuery = "";
            RenderClip();

            // the keyboard: the real shortcut opens it, ↓ chooses, Enter pastes into the window that was in front
            _forcedState = null;
            Collapse();
            await Task.Delay(700);
            string got = System.IO.Path.Combine(folder, "pasted.txt");
            string ps1 = System.IO.Path.Combine(folder, "target.ps1");
            System.IO.File.WriteAllText(ps1, "Add-Type -AssemblyName System.Windows.Forms\n" +
                "$f = New-Object System.Windows.Forms.Form; $f.Text = 'Notchy teste'; $f.TopMost = $true; $f.Width = 420; $f.Height = 140\n" +
                "$t = New-Object System.Windows.Forms.TextBox; $t.Dock = 'Fill'; $t.Multiline = $true; $f.Controls.Add($t)\n" +
                "$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 14000; $timer.Add_Tick({ [IO.File]::WriteAllText('" + got + "', $t.Text); $f.Close() }); $timer.Start()\n" +
                "$f.Add_Shown({ $f.Activate(); $t.Focus() }); [void]$f.ShowDialog()\n");
            using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            IntPtr otherHwnd = IntPtr.Zero;
            for (int i = 0; i < 40 && otherHwnd == IntPtr.Zero; i++)
            {
                await Task.Delay(150);
                other.Refresh();
                otherHwnd = other.MainWindowHandle;
            }
            CCheck("app de teste aberto", otherHwnd != IntPtr.Zero, "");
            // (the test window takes the front itself once it's shown: only then the shortcut, or it takes it from the island)
            for (int i = 0; i < 30 && otherHwnd != IntPtr.Zero && !Native.IsForeground(otherHwnd); i++) await Task.Delay(100);
            await Task.Delay(400);
            CCheck("atalho registrado", !_clip.HotkeyTaken, S.ClipHotkey);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
            // the shortcut that's set (Win+V goes through the keyboard hook, the others through RegisterHotKey)
            var hk = KeyboardHook.Parse(S.ClipHotkey)!.Value;
            var hkKeys = new (uint mod, byte vk)[] { (KeyboardHook.ModWin, 0x5B), (KeyboardHook.ModCtrl, 0x11), (KeyboardHook.ModAlt, 0x12), (KeyboardHook.ModShift, 0x10) }
                .Where(m => (hk.mods & m.mod) != 0).Select(m => m.vk).Append((byte)hk.vk).ToList();
            foreach (var k in hkKeys) keybd_event(k, 0, 0, UIntPtr.Zero);
            foreach (var k in Enumerable.Reverse(hkKeys)) keybd_event(k, 0, 2, UIntPtr.Zero);
            await Task.Delay(900);
            CCheck("atalho abre a aba com o teclado", _clipKeys && _state == IslandState.Expanded && _tab == Tab.Clip && _clipBox.IsKeyboardFocused,
                $"teclado={_clipKeys} estado={_state} aba={_tab} foco={_clipBox.IsKeyboardFocused} ilha na frente={Native.IsForeground(_hwnd)}");
            await ShotAsync(folder, "clip_7_keys_open", 200);
            if (!_clipKeys) // (another window took the front first: the rest is still checked)
            {
                OpenClipFromKeys(otherHwnd);
                await Task.Delay(600);
            }
            void Press(Key k)
            {
                var src = PresentationSource.FromVisual(_clipBox);
                _clipBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            Press(Key.Down);
            Press(Key.Down);
            await ShotAsync(folder, "clip_8_keys_down", 250);
            var chosen = _clipSel >= 0 && _clipSel < _clipRows.Count ? _clipRows[_clipSel].item : null;
            CCheck("↓ escolhe", _clipSel == 2, $"{_clipSel} {chosen?.Kind}");
            _clipBox.Text = "github";
            await Task.Delay(150);
            CCheck("digitar busca e volta para o primeiro", _clipSel == 0 && _clipRows.Count == 1 && _clipRows[0].item.Kind == ClipKind.Link, $"{_clipSel} de {_clipRows.Count}");
            await ShotAsync(folder, "clip_9_keys_search", 250);
            // the window in front when the shortcut came may be anything on this desktop (Windows won't let the test window take
            // the front from a terminal): the paste goes only to the test window, never to whatever app was open
            // another app holds the clipboard open for a moment: Enter must not hold the island's thread (it's also the one
            // answering the keyboard hook) and the paste still goes through once the clipboard is free
            _clipTarget = otherHwnd;
            // (another process, with a window: Windows lets the same process, or one without a window, open it anyway)
            string heldMark = System.IO.Path.Combine(folder, "held.txt");
            string holdPs1 = System.IO.Path.Combine(folder, "hold.ps1");
            System.IO.File.WriteAllText(holdPs1, "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form\n" + "Add-Type -Name C -Namespace W -MemberDefinition '[DllImport(\"user32.dll\")] public static extern bool OpenClipboard(System.IntPtr h); [DllImport(\"user32.dll\")] public static extern bool CloseClipboard();'\n" +
                "if ([W.C]::OpenClipboard($f.Handle)) { Set-Content '" + heldMark + "' 1; Start-Sleep -Milliseconds 1500; [void][W.C]::CloseClipboard() }\n");
            using var busy = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{holdPs1}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            for (int t = 0; t < 80 && !System.IO.File.Exists(heldMark); t++) await Task.Delay(50);
            bool held0 = System.IO.File.Exists(heldMark);
            var enter = System.Diagnostics.Stopwatch.StartNew();
            if (otherHwnd != IntPtr.Zero && _clipTarget == otherHwnd) Press(Key.Enter);
            long held = enter.ElapsedMilliseconds;
            Press(Key.Enter); // (a second Enter while waiting doesn't paste twice)
            await Task.Delay(100);
            CCheck("Enter com a área de transferência ocupada não trava", held < 100 && _clipKeys, $"{held} ms, ainda esperando={_clipKeys}, ocupada={held0}");
            busy.WaitForExit(5000);
            await Task.Delay(400);
            await Task.Delay(900);
            bool front = Native.IsForeground(otherHwnd);
            other.WaitForExit(16000);
            string pasted = System.IO.File.Exists(got) ? System.IO.File.ReadAllText(got) : "(nada)";
            CCheck("Enter cola no app que estava na frente", pasted == "https://github.com/notchy/notchy", $"“{pasted}” frente={front}");
            CCheck("ilha fecha depois de colar", !_clipKeys && _state != IslandState.Expanded, $"{_state}");
            await ShotAsync(folder, "clip_10_pasted", 200);


            _clip.Items.Clear();
            RenderClip();
            await ShotAsync(folder, "clip_6_empty", 300);
            System.IO.File.AppendAllText(clog, $"falhas: {cfails}" + Environment.NewLine);

            // back as it was (the test's pictures go)
            foreach (var f in System.IO.Directory.GetFiles(ClipboardService.ImageFolder, "*.png"))
                if (!saved.Any(x => x.Kind == ClipKind.Image && string.Equals(x.ImagePath, f, StringComparison.OrdinalIgnoreCase))) System.IO.File.Delete(f);
            _clip.Items.AddRange(saved);
            _clip.DontSave = false;
            S.ClipPaused = wasPaused;
            System.IO.File.Delete(tmpFile);
            _forcedState = null;
            Collapse();
        }

        // Revisão das animações: each change caught in the middle (something must be moving), then at rest
        if (Environment.GetEnvironmentVariable("NOTCHY_ANIMTEST") == "1")
        {
            string alog = System.IO.Path.Combine(folder, "anim.txt");
            void ACheck(string what, bool ok, string got) => System.IO.File.AppendAllText(alog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            static bool Moving(DependencyObject root)
            {
                if (root is UIElement u && u.RenderTransform is TranslateTransform t && Math.Abs(t.Y) > 0.3) return true;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Moving(VisualTreeHelper.GetChild(root, i))) return true;
                return false;
            }
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Task.Delay(600);

            // 1 · a page opened from another: both are there for a moment, sliding; then only the new one
            OpenCalendar(DateTime.Today);
            await Task.Delay(700);
            OpenCalEditor(null);
            await ShotAsync(folder, "an_1_editor_mid", 90);
            int during = _subPage.Children.Count;
            await Task.Delay(500);
            ACheck("calendário → editor desliza", during == 2 && _subPage.Children.Count == 1, $"durante {during} páginas, depois {_subPage.Children.Count}");
            SubPageBack();
            await ShotAsync(folder, "an_2_back_mid", 90);
            during = _subPage.Children.Count;
            await Task.Delay(500);
            ACheck("‹ volta deslizando", during == 2 && _subPage.Children.Count == 1 && _subTitle.Text == Loc.T("cal.title"), $"durante {during}, depois {_subPage.Children.Count} ({_subTitle.Text})");
            CloseSubPage(animate: false);

            // 3 · the filters' highlight slides; 4 · a new copy glides in
            var savedClip = _clip.Items.ToList();
            _clip.DontSave = true;
            _clip.Items.Clear();
            foreach (var t in new[] { "https://github.com/notchy", "Endereço: Rua das Flores, 120", "#E8825F" })
                _clip.Items.Add(new ClipItem { Kind = ClipboardService.KindOf(t), Text = t, App = "Teste", Time = DateTime.Now.AddMinutes(-5) });
            _clipBox.Text = ""; // (an earlier test may have left a search)
            _clipFilter = null;
            SelectTab(Tab.Clip, false);
            await Task.Delay(600);
            var pillT = (TranslateTransform)_clipChipPill.RenderTransform;
            double x0 = pillT.X;
            _clipFilter = ClipKind.Link;
            RenderClip();
            await ShotAsync(folder, "an_3_filter_mid", 110);
            double xMid = pillT.X;
            await Task.Delay(450);
            ACheck("destaque do filtro desliza", xMid > x0 + 1 && xMid < pillT.X - 1, $"{x0:0} → {xMid:0} → {pillT.X:0}");
            _clipFilter = null;
            RenderClip();
            await Task.Delay(450);
            _clip.Add(new ClipItem { Kind = ClipKind.Text, Text = "Chegou agora", App = "Teste" });
            await ShotAsync(folder, "an_4_new_mid", 90);
            bool moving = Moving(_clipList);
            await Task.Delay(450);
            ACheck("item novo entra e os outros descem", moving && !Moving(_clipList), $"no meio: {moving}");
            _clip.Items.Clear();
            _clip.Items.AddRange(savedClip);
            _clip.DontSave = false;
            _forcedState = null;
            Collapse();
        }

        // Assistente: what the real island does for each tip of the tour
        if (Environment.GetEnvironmentVariable("NOTCHY_SETUPTEST") == "1")
        {
            string slog = System.IO.Path.Combine(folder, "setup.txt");
            _forcedState = null;
            Collapse();
            await Task.Delay(700);
            foreach (var tip in new[] { "open", "date", "bell", "clip", "volume", "shelf" })
            {
                SetupTour(tip);
                await ShotAsync(folder, "tour_" + tip, 950);
                System.IO.File.AppendAllText(slog, $"{tip}: estado {_state}, aba {_tab}" + Environment.NewLine);
            }
            SetupTour(null);
            await Task.Delay(600);
            System.IO.File.AppendAllText(slog, $"fim: estado {_state}, forçado {(_forcedState == null ? "não" : "sim")}" + Environment.NewLine);
            SetupClipDemo();
            await ShotAsync(folder, "setup_clip_demo", 140);
        }

        // Animações, segunda passada: each change caught in the middle, then at rest
        if (Environment.GetEnvironmentVariable("NOTCHY_ANIM2TEST") == "1")
        {
            string blog = System.IO.Path.Combine(folder, "anim2.txt");
            void BCheck(string what, bool ok, string got) => System.IO.File.AppendAllText(blog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            static double ScaleOf(FrameworkElement? cell) => cell?.RenderTransform is TransformGroup { Children.Count: 2 } g && g.Children[0] is ScaleTransform st ? st.ScaleY : 1;

            // 3 · eyes ↔ smile (the island at rest)
            _forcedState = IslandState.Idle;
            UpdateState();
            await Task.Delay(900);
            Happy(); // (keeps the smile for a moment, as when something finishes)
            await Task.Delay(50);
            bool eyesStill = EyeL.Visibility == Visibility.Visible;
            double squash = ScaleOf(EyeL.Parent as FrameworkElement);
            await ShotAsync(folder, "b3_eyes_mid", 10);
            await Task.Delay(600);
            BCheck("olhos se espremem antes do sorriso", eyesStill && squash < 0.98 && HappyL.Visibility == Visibility.Visible && ScaleOf(EyeL.Parent as FrameworkElement) > 0.97,
                $"no meio: olhos {eyesStill}, altura {squash:0.00}; depois: sorriso {HappyL.Visibility}, altura {ScaleOf(EyeL.Parent as FrameworkElement):0.00}");
            await ShotAsync(folder, "b3_smile", 10);
            _happyUntil = DateTime.Now;
            await Task.Delay(900);
            BCheck("e voltam", EyeL.Visibility == Visibility.Visible && HappyL.Visibility != Visibility.Visible, $"{EyeL.Visibility}");

            // 1 · a new song, 2 · play ↔ pause (Música tab)
            _media.Fake("Noite Azul", "Lia Monte", true);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Music, false);
            await Task.Delay(900);
            _media.Fake("Maré Alta", "Os Faróis", true);
            await Task.Delay(60);
            bool leaving = _textComing.ContainsKey(TitleText);
            string midTitle = TitleText.Text;
            await ShotAsync(folder, "b1_song_mid", 60);
            await Task.Delay(600);
            BCheck("título desliza para a música nova", leaving && midTitle == "Noite Azul" && TitleText.Text == "Maré Alta" && ArtistText.Text == "Os Faróis", $"no meio “{midTitle}” (saindo {leaving}), depois “{TitleText.Text}”");
            _media.Fake("Maré Alta", "Os Faróis", false);
            await Task.Delay(70);
            double hop = PlayButton.Content is TextBlock { RenderTransform: ScaleTransform ps } ? ps.ScaleX : -1;
            await Task.Delay(500);
            BCheck("play/pause com pulinho", hop > 0 && hop < 0.98 && PlayButton.Content is TextBlock { RenderTransform: ScaleTransform { ScaleX: > 0.99 } }, $"no meio {hop:0.00}");
            await ShotAsync(folder, "b1_song_after", 10);

            // 4 · tabs: the old one is still there, on its way out
            SelectTab(Tab.Home, false);
            await Task.Delay(400);
            SelectTab(Tab.Clock, true);
            await Task.Delay(70);
            bool both = HomeTab.Visibility == Visibility.Visible && ClockTab.Visibility == Visibility.Visible && HomeTab.Opacity < 1;
            await ShotAsync(folder, "b4_tabs_mid", 10);
            await Task.Delay(500);
            BCheck("aba antiga sai deslizando", both && HomeTab.Visibility == Visibility.Collapsed && ClockTab.Opacity > 0.99, $"no meio as duas: {both}; depois Início {HomeTab.Visibility}");
            SelectTab(Tab.Home, true);
            await Task.Delay(500);
            BCheck("e a aba volta inteira", HomeTab.Visibility == Visibility.Visible && HomeTab.Opacity > 0.99 && HomeTab.IsHitTestVisible && ClockTab.Visibility == Visibility.Collapsed, $"{HomeTab.Visibility} {HomeTab.Opacity:0.00}");

            // 5 · calendar: the ring glides to the day
            OpenCalendar(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 10));
            await Task.Delay(700);
            var dayBefore = _calDay;
            _calDay = new DateTime(dayBefore.Year, dayBefore.Month, 20);
            GlideCalDay(dayBefore);
            await Task.Delay(90);
            bool flying = _calRing != null;
            await ShotAsync(folder, "b5_day_mid", 10);
            await Task.Delay(500);
            BCheck("anel desliza até o dia", flying && _calRing == null && _subOpen, $"no meio {flying}, depois {( _calRing == null ? "pousou" : "no ar")}");
            await ShotAsync(folder, "b5_day_after", 10);
            CloseSubPage(animate: false);

            // 9 · the bell's count hops
            int unseen = _noticeUnseen;
            _noticeUnseen = 1;
            UpdateBell();
            await Task.Delay(400);
            _noticeUnseen = 2;
            UpdateBell();
            await Task.Delay(40);
            double badge = ((FrameworkElement)_bellBadge.Parent).RenderTransform is ScaleTransform bs ? bs.ScaleX : -1;
            await ShotAsync(folder, "b9_bell_mid", 10);
            await Task.Delay(450);
            BCheck("contador do sininho pula", _bell.IsVisible ? badge > 1.02 : badge == -1 || badge >= 1, $"{badge:0.00} (sininho visível: {_bell.IsVisible})");
            _noticeUnseen = unseen;
            UpdateBell();

            // 8 · Claude tab: the states fade
            SelectTab(Tab.Claude, false);
            await ShotAsync(folder, "b8_claude", 500);
            _forcedState = null;
            Collapse();
        }

        // Shift + wheel on the open island: the volume bar where nothing uses the wheel, and back to the same tab
        if (Environment.GetEnvironmentVariable("NOTCHY_WHEELTEST") == "1")
        {
            string wlog = System.IO.Path.Combine(folder, "wheel.txt");
            void WCheck(string what, bool ok, string got) => System.IO.File.AppendAllText(wlog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
            _audio.TryGet(out float volBefore, out _);
            bool Wheel(UIElement on, int delta)
            {
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent, Source = on };
                on.RaiseEvent(args);
                return args.Handled;
            }
            _forcedState = null; // (an earlier part of the test keeps it open by force)
            _hoverActive = true;
            _clickOpened = true;
            UpdateState();
            SelectTab(Tab.Shelf, false);
            await Task.Delay(700);

            keybd_event(0x10, 0, 0, UIntPtr.Zero); // Shift held
            await Task.Delay(100);
            bool took = Wheel(NavBar, 120);
            await ShotAsync(folder, "w_1_bar", 450);
            WCheck("Shift + roda no menu vira a barra de volume", took && _state == IslandState.Volume, $"{_state} tratado {took} lista {WheelTaken(NavBar)} shift {GetAsyncKeyStateShift(0x10)} página {_subOpen} até {WheelVolumeHolds}");
            WheelVolume(-120); // (back where it was: the open island is the bar now, so the turn goes to it)
            // the mouse leaves while still turning with Shift: the island closes, and the wheel must not start skipping songs
            _hoverActive = false;
            _clickOpened = false;
            UpdateState();
            await Task.Delay(200);
            var skipBefore = _lastTrackSkip;
            var bubble = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent };
            Island.RaiseEvent(bubble);
            WCheck("depois de sair, a roda continua no volume (não passa música)", _lastTrackSkip == skipBefore, $"{_state}");
            var back2 = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.MouseWheelEvent };
            Island.RaiseEvent(back2);
            _hoverActive = true;
            _clickOpened = true;
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            await Task.Delay((int)(WheelVolumeSeconds * 1000) + 900);
            await ShotAsync(folder, "w_2_back", 10);
            WCheck("volta para a mesma aba", _state == IslandState.Expanded && _tab == Tab.Shelf, $"{_state} {_tab}");

            // without Shift: nothing happens on the open island
            bool plain = Wheel(NavBar, 120);
            await Task.Delay(300);
            WCheck("sem Shift a ilha aberta fica como está", _state == IslandState.Expanded, $"{_state} (tratado {plain})");

            // where something uses the wheel (the calendar's month), Shift + wheel leaves it alone
            OpenCalendar(DateTime.Today);
            await Task.Delay(700);
            keybd_event(0x10, 0, 0, UIntPtr.Zero);
            await Task.Delay(100);
            var cells = TestFind<Border>(_calBody, b => b.Tag is DateTime);
            bool onCal = cells != null && Wheel(cells, 120);
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            await Task.Delay(400);
            WCheck("no calendário a roda continua com ele", _state == IslandState.Expanded, $"{_state} (tratado {onCal})");
            CloseSubPage(animate: false);

            if (_audio.TryGet(out float volAfter, out _)) WCheck("volume de volta ao que era", Math.Abs(volAfter - volBefore) < 0.011, $"{volBefore:0.00} → {volAfter:0.00}");
            _hoverActive = false;
            _clickOpened = false;
            Collapse();
        }

        // Calendário: the month page and the editor of an event (a made-up one, removed afterwards)
        if (Environment.GetEnvironmentVariable("NOTCHY_CALTEST") == "1")
        {
            var ev = new CalEvent { Name = "Dentista", Date = DateTime.Today.AddDays(2), HasTime = true, Hour = 14, Minute = 30, Type = CalType.Personal, NoticeMinutes = 15 };
            _calendar.Put(ev);
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Task.Delay(500);
            OpenSubPage(Loc.T("cal.title"), CalendarPage);
            await ShotAsync(folder, "cal_1_page", 700);
            string calLog = System.IO.Path.Combine(folder, "calendar.txt");
            void CalLog(string line) => System.IO.File.AppendAllText(calLog, line + Environment.NewLine);
            T? Find<T>(DependencyObject root, Func<T, bool> ok) where T : DependencyObject => TestFind(root, ok);
            void Click(DependencyObject? from) { if (!TestClick(from)) CalLog("clique: nada para clicar"); }

            // drop target: the cell under a point of the grid knows its day
            var cell17 = Find<Border>(_calBody, b => b.Tag is DateTime d && d == ev.Date.Date);
            var centre = cell17!.TranslatePoint(new Point(cell17.ActualWidth / 2, cell17.ActualHeight / 2), _calBody);
            CalLog($"celula sob o ponto: {(CalCellAt(centre)?.Tag as DateTime?)?.ToString("dd/MM") ?? "nenhuma"} (esperado {ev.Date:dd/MM})");

            // B: the editor's day opens the small month, a day is picked, saved
            OpenCalEditor(ev);
            await ShotAsync(folder, "cal_2_editor", 700);
            Click(Find<TextBlock>(Island, t => t.Text == DayLine(ev.Date)));
            await ShotAsync(folder, "cal_3a_abrindo_linhas", 70);
            await ShotAsync(folder, "cal_3b_abrindo_mes", 150);
            await ShotAsync(folder, "cal_3_mini_open", 400);
            var target = ev.Date.Date.AddDays(7);
            Click(Find<TextBlock>(Island, t => t.Text == target.Day.ToString() && t.FontSize == 11));
            await ShotAsync(folder, "cal_4a_fechando_mes", 110);
            await ShotAsync(folder, "cal_4b_fechando_linhas", 200);
            await ShotAsync(folder, "cal_4_mini_picked", 400);
            Click(Find<TextBlock>(Island, t => t.Text == Loc.T("cal.save")));
            await ShotAsync(folder, "cal_5_saved", 700);
            CalLog($"editor: dia salvo {ev.Date:dd/MM} (esperado {target:dd/MM})");

            // C: dropping moves it; a weekly one moves whole, keeping its rhythm
            MoveCalEvent(ev, ev.Date, ev.Date.AddDays(-3));
            CalLog($"arrastar: {ev.Date:dd/MM} (esperado {target.AddDays(-3):dd/MM})");
            var weekly = new CalEvent { Name = "Inglês", Date = new DateTime(2026, 11, 3), Repeat = CalRepeat.Weekly, NotifiedFor = new DateTime(2026, 11, 10) };
            MoveCalEvent(weekly, new DateTime(2026, 11, 17), new DateTime(2026, 11, 19));
            CalLog($"semanal: começa {weekly.Date:dd/MM ddd}, dia 19 {weekly.OccursOn(new DateTime(2026, 11, 19))}, dia 17 {weekly.OccursOn(new DateTime(2026, 11, 17))}, aviso zerado {weekly.NotifiedFor == null}");
            _calendar.Remove(weekly);
            RefreshCalendar();
            await ShotAsync(folder, "cal_6_page_after", 600);

            CloseSubPage(animate: false);
            _calendar.Remove(ev);
            _forcedState = null;
            Collapse();
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

        // a notification under the open island: the pill, how it goes up with the island, dismissing; and clearing the downloads
        if (Environment.GetEnvironmentVariable("NOTCHY_PILLTEST") == "1")
        {
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Home, false);
            await Task.Delay(700);
            OnNotice(new Notice(999991, "WhatsApp", "", "Maria", "Já chegou em casa?", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            OnNotice(new Notice(999992, "Discord", "", "João", "Bora jogar?", DateTime.Now, null, false) { Accent = Color.FromRgb(0x58, 0x65, 0xF2) });
            await Task.Delay(800);
            await Shot("14_pill");
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            UpdateState();
            await Task.Delay(120);
            await Shot("14_merging");
            await Task.Delay(900);
            await Shot("14_card");
            DismissNotice();
            await Task.Delay(900);
            await Shot("14_next");
            // the mouse was on the card when it was dismissed: the island must not open
            OnNotice(new Notice(999994, "WhatsApp", "", "Maria", "Oi", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            await Task.Delay(500);
            await Shot("14_line");
            _hoverActive = _hoverRequested = true;
            DismissNotice();
            await Task.Delay(700);
            string afterCard = $"{_state} hover={_hoverActive}";
            // a pill dismissed: the island stays open even with nothing asking for it, until the mouse has been back
            _forcedState = IslandState.Expanded;
            UpdateState();
            await Task.Delay(600);
            OnNotice(new Notice(999993, "WhatsApp", "", "Maria", "Oi", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            await Task.Delay(500);
            DismissNotice();
            _forcedState = null;
            _hoverActive = _clickOpened = false;
            UpdateState();
            await Task.Delay(800);
            string held = $"{_state} hold={_holdOpenAfterPill} pill={_noticePill.Visibility}";
            _holdOpenAfterPill = false;
            UpdateState();
            await Task.Delay(800);
            string musicBefore = $"{_state} {_compactMode} tocando={_media.IsPlaying}";
            OnNotice(new Notice(999995, "WhatsApp", "", "Maria", "Com música", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            await Task.Delay(700);
            await Shot("14_music");
            string musicDuring = $"{_state} {_compactMode} napilula={_noticeInPill}";
            DismissNotice();
            await Task.Delay(500);
            string musicAfter = $"{_state} {_compactMode} segura={_holdOpenAfterPill}";
            // the activity ends with the pill on screen: up it goes, as the card
            OnNotice(new Notice(999996, "WhatsApp", "", "Maria", "Acabou", DateTime.Now, null, false) { Accent = Color.FromRgb(0x25, 0xD3, 0x66) });
            await Task.Delay(400);
            string endBefore = $"{_state} {_compactMode} napilula={_noticeInPill}";
            _testIslandNotBusy = true;
            await Task.Delay(480);
            await Shot("14_morph_up");
            await Task.Delay(800);
            await Shot("14_morph_card");
            string endAfter = $"{_state} {_compactMode} napilula={_noticeInPill} transiente={_transient} ilha={Island.ActualWidth:0}x{Island.ActualHeight:0}";
            _testIslandNotBusy = false; // the activity is back: the card goes down as a pill
            await Task.Delay(420);
            await Shot("14_morph_down");
            await Task.Delay(800);
            await Shot("14_morph_pill");
            string backAfter = $"{_state} {_compactMode} napilula={_noticeInPill} transiente={_transient} forma={_noticePill.Height:0}/{_pillMove.Y:0}";
            DismissNotice();
            await Task.Delay(400);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "music.txt"), new[] { "antes: " + musicBefore, "durante: " + musicDuring, "depois: " + musicAfter, "pilula antes de acabar: " + endBefore, "depois de acabar: " + endAfter, "atividade voltou: " + backAfter });
            _holdOpenAfterPill = true;
            Island_MouseEnter(Island, null!);
            string back = $"depois de voltar: hold={_holdOpenAfterPill} hover={_hoverActive}";
            _hoverTimer.Stop();
            _hoverRequested = false;
            Collapse();
            await Task.Delay(500);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(folder, "pill.txt"), new[] { "cartao dispensado: " + afterCard, "pilula dispensada: " + held, back, "fechou: " + _state });
            _forcedState = IslandState.Expanded;
            UpdateState();
            SelectTab(Tab.Downloads, false);
            var entries = Enumerable.Range(1, 10).Select(i => new DownloadEntry($@"C:\teste\arquivo{i}.pdf", i * 1234567, DateTime.UtcNow)).ToList();
            BuildDownloadsTab(null, new List<DownloadFile>(), entries);
            await Task.Delay(700);
            await Shot("14_dl_list");
            _dlCleared = new List<DownloadEntry>();
            BuildDownloadsTab(null, new List<DownloadFile>(), new List<DownloadEntry>());
            await Task.Delay(500);
            await Shot("14_dl_undo");
            _dlCleared = null;
            _forcedState = null;
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
            RefreshWeatherNow();
            await Shot("12_refresh_busy");
            for (int i = 0; i < 60 && _weatherRefreshing; i++) await Task.Delay(250);
            await Shot("12_refresh_done");
            await Task.Delay(2500);
            await Shot("12_refresh_idle");
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
            if (S.ChatUseGemini)
            {
                RunChatAction("ask", "E o que mais?"); // a follow-up: the earlier messages go along
                await Task.Delay(300);
                for (int i = 0; i < 60 && _runner!.Busy; i++) await Task.Delay(250);
                await Shot("7_chat_followup");
                _ctxText = null;
                _ctxImage = System.IO.Path.Combine(folder, "7_chat_context.png");
                _ctxChangedSinceAnswer = true;
                RunChatAction("explain", "");
                await Task.Delay(300);
                for (int i = 0; i < 60 && _runner!.Busy; i++) await Task.Delay(250);
                await Shot("7_chat_image");
            }
        }

        settings:
        var sw = new SettingsWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = 100, Top = 100 };
        sw.Show();
        void RenderSettings(string name)
        {
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
            using var fs = System.IO.File.Create(System.IO.Path.Combine(folder, "6_settings_" + name + ".png"));
            enc.Save(fs);
        }
        foreach (var page in !string.IsNullOrEmpty(settingsOnly) ? settingsOnly.Split(',') : new[] { "general", "look", "notif", "sounds", "music", "calendar", "clock", "focus", "files", "clip", "claude", "chat", "about" })
        {
            sw.ShowPage(page);
            await Task.Delay(700);
            RenderSettings(page);
        }
        // scrolled down: the page must pass under the window's buttons' band, not through the buttons
        sw.ShowPage("look");
        await Task.Delay(500);
        sw.ScrollPageTo(140);
        await Task.Delay(300);
        RenderSettings("look_scrolled");
        System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "mask.txt"), sw.TestScrollMask());
        sw.ShowPage("calendar");
        await Task.Delay(500);
        sw.TestExtWizard("google");
        await Task.Delay(300);
        sw.ScrollPageTo(330);
        await Task.Delay(300);
        RenderSettings("calendar_wizard");
        sw.TestExtWizard("samsung");
        await Task.Delay(300);
        sw.ScrollPageTo(330);
        await Task.Delay(300);
        RenderSettings("calendar_wizard_samsung");
        if (Environment.GetEnvironmentVariable("NOTCHY_HOTKEYTEST") == "1")
        {
            // the shortcut: recorded with real keys through the field, then Win + V taken from Windows
            string hlog = System.IO.Path.Combine(folder, "hotkey.txt");
            void HCheck(string what, bool ok, string got) => System.IO.File.AppendAllText(hlog, $"{(ok ? "ok  " : "ERRO")} {what}: {got}" + Environment.NewLine);
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
            void Keys(params byte[] vks)
            {
                foreach (var k in vks) keybd_event(k, 0, 0, UIntPtr.Zero);
                for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2, UIntPtr.Zero);
            }
            [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
            static bool GetAsyncKeyStateWin() => GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
            string saved = S.ClipHotkey;
            sw.ShowPage("clip");
            sw.Activate();
            await Task.Delay(700);
            RenderSettings("hk_1_idle");
            var rec = TestFind<Button>((DependencyObject)sw.Content, b => b.Content is TextBlock { Text: var t } && t == Loc.T("s.cl.key.rec"));
            rec?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(300);
            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero);
            await Task.Delay(300);
            RenderSettings("hk_2_holding");
            keybd_event(0x4B, 0, 0, UIntPtr.Zero); keybd_event(0x4B, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
            await Task.Delay(500);
            RenderSettings("hk_3_recorded");
            HCheck("gravar Ctrl + Alt + K", S.ClipHotkey == "Ctrl+Alt+K" && !_clip.HotkeyTaken, S.ClipHotkey);
            Collapse();
            await Task.Delay(500);
            Keys(0x11, 0x12, 0x4B);
            await Task.Delay(900);
            HCheck("Ctrl + Alt + K abre a aba", _clipKeys && _tab == Tab.Clip, $"{_state} {_tab} teclado {_clipKeys}");
            if (_clipKeys) EndClipKeys();
            await Task.Delay(500);

            S.ClipHotkey = "Win+V";
            _clip.SetHotkey(S.ClipHotkey, force: true);
            await Task.Delay(300);
            Keys(0x5B, 0x56);
            await Task.Delay(900);
            HCheck("Win + V abre o Notchy no lugar do Windows", _clipKeys && _tab == Tab.Clip && !_clip.HotkeyTaken, $"{_state} {_tab} teclado {_clipKeys}");
            HCheck("Win não fica preso depois", GetAsyncKeyStateWin() == false, "");
            if (_clipKeys) EndClipKeys();
            await Task.Delay(500);

            S.ClipHotkey = saved;
            AppSettings.Commit();
            _clip.SetHotkey(saved, force: true);
            HCheck("de volta ao atalho de antes", !_clip.HotkeyTaken, saved);
        }
        if (Environment.GetEnvironmentVariable("NOTCHY_ANIM2TEST") == "1")
        {
            // 13 · the sidebar's highlight slides; 14 · the search results come in
            sw.ShowPage("general");
            await Task.Delay(600);
            double y0 = sw.TestSideMarkY();
            sw.ShowPage("files");
            await Task.Delay(90);
            double yMid = sw.TestSideMarkY();
            RenderSettings("b13_side_mid");
            await Task.Delay(500);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "anim2.txt"),
                $"{(yMid > y0 + 1 && yMid < sw.TestSideMarkY() - 1 ? "ok  " : "ERRO")} Configurações: destaque da barra lateral desliza: {y0:0} → {yMid:0} → {sw.TestSideMarkY():0}" + Environment.NewLine);
            RenderSettings("b13_side_after");
            sw.ShowPage("calendar"); // (back where the next test expects it)
            await Task.Delay(500);
        }
        if (Environment.GetEnvironmentVariable("NOTCHY_ANIMTEST") == "1")
        {
            // 5 · a step of the wizard: the rows glide instead of jumping
            sw.TestExtWizard("google");
            await Task.Delay(70);
            RenderSettings("glide_mid");
            bool glide = sw.TestAnyMoving();
            await Task.Delay(450);
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "anim.txt"),
                $"{(glide && !sw.TestAnyMoving() ? "ok  " : "ERRO")} Configurações: linhas deslizam ao mudar: no meio {glide}" + Environment.NewLine);
        }

        Application.Current.Shutdown();
    }
#endif
}
