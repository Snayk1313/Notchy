using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Notchy;

/// <summary>
/// Extra island features: the separate Claude "pod", background blur, lively eyes,
/// scroll-to-change-volume, the file shelf and battery alerts.
/// </summary>
public partial class MainWindow
{
    private static readonly Color ShelfColor = Color.FromRgb(0x6E, 0xE7, 0xC4);

    // blur
    private BackdropWindow? _backdrop;
    private int _blurSyncCount;

    // pod
    private readonly TranslateTransform _podSlide = new();
    private bool _podShown, _podAttentionAnim, _podHappy;
    private DateTime _podDoneUntil = DateTime.MinValue;

    // eyes
    private DateTime _happyUntil = DateTime.MinValue;
    private bool _happyShown;
    private Native.POINT _lastCursor;
    private DateTime _lastCursorMove = DateTime.Now;

    // scroll
    private DateTime _lastTrackSkip = DateTime.MinValue;

    // battery
    private bool _batteryCharging;
    private bool? _lastOnline;
    private bool _lowWarned;

    // shelf
    private readonly List<string> _shelf = new();
    private static readonly string ShelfFile = Path.Combine(AppSettings.Folder, "shelf.json");
    private bool _dragOver, _draggingOut;
    private DateTime _dragLast;

    private void InitExtras()
    {
        var group = (TransformGroup)ClaudePod.RenderTransform;
        group.Children.Add(_podSlide);
        _shelfSel.AfterDrag = () =>
        {
            if (_shelf.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) == 0) return; // moved away: off the shelf
            SaveShelf();
            RenderShelf();
        };
        LoadShelf();
        RenderShelf();

    }

    private void ApplyExtrasSettings()
    {
        if (_nav.TryGetValue(Tab.Shelf, out var n))
            n.button.Visibility = S.ShelfEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!S.ShelfEnabled && _tab == Tab.Shelf) SelectTab(Tab.Home, animate: false);

        ApplyTransferSettings();
        ClaudePod.Height = 34 + _topHidden;
        BuildPodOutline();
        if (!S.ClaudePod && _podShown && _podKind == PodKind.Claude) HidePod();
        PodScale.ScaleX = PodScale.ScaleY = _podShown ? S.SizeScale : PodScale.ScaleX;
        UpdatePodPosition();

        UpdateBlur();
        UpdateEyeColor();
        if (!S.LivelyEyes)
        {
            EyesSleep.ScaleY = 1;
            SetHappy(false);
        }
    }

    /// <summary>Called every frame from OnFrame.</summary>
    private void UpdateExtrasFrame()
    {
        if (!_introActive) UpdateLivelyEyes();
        UpdateIslandFaceGate();
        UpdatePod();

        if (_happyShown && DateTime.Now > _happyUntil) SetHappy(false);

        if (_dragOver && (DateTime.Now - _dragLast).TotalMilliseconds > 350)
        {
            _dragOver = false;
            ShelfDropZone.Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255));
            UpdateState();
        }

        if (_slowTick && _tick % 60 == 30) CheckBattery();
    }

    // =====================================================================================
    // blur
    // =====================================================================================

    private bool ShadowActive => S.IslandShadow;

    private void UpdateBlur()
    {
        bool want = S.BlurEnabled || ShadowActive;
        if (want)
        {
            if (_backdrop == null)
            {
                _backdrop = new BackdropWindow();
                // keep the glass in lock-step with what WPF draws (smooth while the island animates)
                CompositionTarget.Rendering += OnRenderingSyncBlur;
            }
            _backdrop.Configure(S.BlurEnabled, S.BlurIntensity, S.BlurTint, ShadowActive);
        }
        else if (_backdrop != null)
        {
            CompositionTarget.Rendering -= OnRenderingSyncBlur;
            _backdrop.Close();
            _backdrop = null;
        }

        if (S.BlurEnabled)
        {
            // the glass itself is the background: no black bar, just enough alpha to stay hoverable
            var clear = new SolidColorBrush(Color.FromArgb(0x02, 0, 0, 0));
            Island.Background = clear;
            ClaudePod.Background = clear;
            DownloadPod.Background = clear;
        }
    }

    private void OnRenderingSyncBlur(object? sender, EventArgs e) => UpdateBlurRegion();

    /// <summary>Keeps the composition blur/shadow shapes exactly under the island and the Claude pod.</summary>
    private void UpdateBlurRegion()
    {
        if (_backdrop == null || _backdrop.Hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        _backdrop.SetBounds(new Int32Rect((int)Math.Round(Left * dpi.DpiScaleX), (int)Math.Round(Top * dpi.DpiScaleY),
            (int)Math.Round(Width * dpi.DpiScaleX), (int)Math.Round(Height * dpi.DpiScaleY)), _hwnd);
        if (++_blurSyncCount % 240 == 0) _backdrop.KeepBelow(_hwnd);
        bool islandShown = Island.Visibility == Visibility.Visible; // (also true for a notification card over a full-screen game)
        bool show = !_hiddenForFullscreen && islandShown;

        (Rect r, double radius) Px(FrameworkElement el, double cornerRadius)
        {
            if (el.ActualWidth <= 0) return (Rect.Empty, 0);
            var b = el.TransformToAncestor(this).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            double k = b.Width / el.ActualWidth;
            return (new Rect(b.X * dpi.DpiScaleX, b.Y * dpi.DpiScaleY, b.Width * dpi.DpiScaleX, b.Height * dpi.DpiScaleY),
                cornerRadius * k * dpi.DpiScaleX);
        }

        // an island pulled all the way up (while a bubble takes its place) has nothing on screen: no blur, no shadow
        bool islandOnScreen = Island.ActualHeight - _topHidden > 0.5;
        var island = islandShown && islandOnScreen ? Px(Island, Island.CornerRadius.TopLeft) : (new Rect(), 0);
        bool podVisible = show && ClaudePod.Visibility == Visibility.Visible;
        var pod = podVisible ? Px(ClaudePod, ClaudePod.CornerRadius.TopLeft) : (new Rect(), 0);
        bool dlVisible = show && DownloadPod.Visibility == Visibility.Visible;
        var dl = dlVisible ? Px(DownloadPod, DownloadPod.CornerRadius.TopLeft) : (new Rect(), 0);
        _backdrop.Update(island.Item1, island.Item2, pod.Item1, pod.Item2, podVisible ? ClaudePod.Opacity : 0,
            dl.Item1, dl.Item2, dlVisible ? DownloadPod.Opacity : 0);
    }

    // =====================================================================================
    // eyes
    // =====================================================================================

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch
        {
            return Colors.White;
        }
    }

    private static Color Soften(Color c) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * 0.3), (byte)(c.G + (255 - c.G) * 0.3), (byte)(c.B + (255 - c.B) * 0.3));

    private void UpdateEyeColor()
    {
        // the opening animation colours the eyes itself, and keeps its colour while the eyes fade out
        if (_introActive || DateTime.Now < _eyeColorHoldUntil) return;
        var c = _claude.Active;
        bool music = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        Color color;
        if (c?.NeedsAttention == true && S.ClaudeShowInCompact) color = ClaudeColor;
        else if (!S.EyesUseAlbumColor) color = ParseColor(S.EyeColor);
        else if (music && !_media.IsPlaying) color = Soften(Bright(_musicAccent));
        else if (music) color = Bright(_musicAccent);
        else color = Colors.White;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        EyeL.Fill = EyeR.Fill = EyeL2.Fill = EyeR2.Fill = brush;
        HappyL.Stroke = HappyR.Stroke = brush;
        EyesGlow.Color = color;
        EyesGlow.Opacity = color == Colors.White ? 0.25 : 0.95;
    }

    /// <summary>^^ eyes for a moment (Claude finished, focus finished...).</summary>
    private void Happy()
    {
        FaceReact(Mood.Happy, 1.8);
        if (!S.LivelyEyes) return;
        _happyUntil = DateTime.Now.AddSeconds(2.8);
        SetHappy(true);
    }

    private void SetHappy(bool on)
    {
        if (_happyShown == on) return;
        _happyShown = on;
        EyeL.Visibility = EyeR.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        HappyL.Visibility = HappyR.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            var bounce = new DoubleAnimation(-3, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 3 } };
            HappyL.RenderTransform = HappyR.RenderTransform = new TranslateTransform();
            ((TranslateTransform)HappyL.RenderTransform).BeginAnimation(TranslateTransform.YProperty, bounce);
            ((TranslateTransform)HappyR.RenderTransform).BeginAnimation(TranslateTransform.YProperty, bounce);
        }
    }

    private void UpdateLivelyEyes()
    {
        Native.GetCursorPos(out var p);
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _lastCursorMove = DateTime.Now;
        }

        if (!S.LivelyEyes)
        {
            PauseBlinking(false);
            if (_state == IslandState.Idle && _slowTick) MaybeGlance();
            return;
        }

        // sleepy after 90 s without mouse movement (and nothing going on)
        bool sleepy = (DateTime.Now - _lastCursorMove).TotalSeconds > 90 && !_media.IsPlaying &&
                      _claude.Active?.IsWorking != true && !_focus.Running && !_happyShown;
        double targetScale = sleepy ? 0.28 : 1;
        PauseBlinking(sleepy); // closed eyes don't blink
        if (Math.Abs(EyesSleep.ScaleY - targetScale) > 0.005)
            EyesSleep.ScaleY += (targetScale - EyesSleep.ScaleY) * Ease(sleepy ? 0.03 : 0.25);

        if (_state != IslandState.Idle || !IdleEyes.IsVisible) return;

        // follow the mouse
        Point center;
        try
        {
            center = IdleEyes.PointToScreen(new Point(IdleEyes.ActualWidth / 2, IdleEyes.ActualHeight / 2));
        }
        catch
        {
            return;
        }
        double dx = p.X - center.X, dy = p.Y - center.Y;
        double tx = sleepy ? 0 : Math.Clamp(dx / 320, -1, 1) * 3.4;
        double ty = sleepy ? 1 : Math.Clamp(dy / 320, -1, 1) * 1.8;
        if (EyesLook.HasAnimatedProperties)
        {
            EyesLook.BeginAnimation(TranslateTransform.XProperty, null);
            EyesLook.BeginAnimation(TranslateTransform.YProperty, null);
        }
        double ek = Ease(0.16);
        EyesLook.X += (tx - EyesLook.X) * ek;
        EyesLook.Y += (ty - EyesLook.Y) * ek;
    }

    // =====================================================================================
    // separate Claude island ("pod")
    // =====================================================================================

    /// <summary>Someone wants the bubble: Claude first, else the focus timer (MainWindow.FocusPod.cs).</summary>
    private bool PodWanted(bool includeDone = true) => ClaudePodWanted(includeDone) || FocusPodWanted();

    private bool ClaudePodWanted(bool includeDone = true)
    {
        if (!S.ClaudePod || !S.ClaudeShowInCompact || _hiddenForFullscreen) return false;
        if (_state != IslandState.Compact || _compactMode != CompactMode.Music) return false;
        var c = _claude.Active;
        return (c != null && (c.IsWorking || c.NeedsAttention)) || (includeDone && DateTime.Now < _podDoneUntil);
    }

    private void UpdatePodPosition()
    {
        if (_podMorphing) return;
        double scale = S.SizeScale;
        double islandW = Island.ActualWidth * scale;
        const double podW = 70, gap = 8;
        PodX.X = -(islandW / 2 + gap * scale + podW * scale / 2);
    }

    private void UpdatePod()
    {
        bool claudeWants = ClaudePodWanted();
        bool want = claudeWants || FocusPodWanted();
        var kind = claudeWants ? PodKind.Claude : PodKind.Focus;
        if (want && !_podShown && !_podMorphing && kind == PodKind.Focus)
        {
            // music came back while the focus had the island: the island becomes the bubble
            if (IslandJustLeft(m => m is CompactMode.Focus or CompactMode.Timer)) MorphFocusIslandIntoPod();
            // otherwise it just pops in (after the music island's eyes have gone)
            else if (!CompactFaceVisible && !EyeTripActive)
            {
                SetPodKind(PodKind.Focus);
                ShowPod();
            }
        }
        else if (want && !_podShown && !_podMorphing)
        {
            if (_podKind != PodKind.Claude) SetPodKind(PodKind.Claude);
            // music came back while Claude had the island: the island becomes the bubble
            if (IslandJustLeft(IsClaudeMode)) MorphClaudeIslandIntoPod();
            else if (EyeTripActive) { } // the eyes are on their way: the trip brings the bubble in
            else if (CompactFaceVisible) { } // the music island's eyes go away first
            else if (DownloadPodHasEyes) StartEyeTrip(forward: false); // the download's eyes go to Claude
            else ShowPod();
        }
        else if (!want && _podShown && !_podMorphing && !EyeTripActive && !PodSwapping && _podKind == PodKind.Focus)
        {
            // music stopped and the focus takes the island: the bubble becomes the island
            if (_state == IslandState.Compact && _compactMode is CompactMode.Focus or CompactMode.Timer) MorphFocusPod();
            else HidePod();
        }
        else if (want && _podShown && !_podMorphing && !EyeTripActive && !PodSwapping && kind != _podKind)
        {
            // Claude starts (or is done) while the bubble is the focus' (or the other way): the eyes swap behind the screen
            StartPodSwap(kind);
        }
        else if (!want && _podShown && !_podMorphing && !EyeTripActive && !PodSwapping)
        {
            // music stopped and Claude takes the island: the bubble becomes the island
            if (_state == IslandState.Compact && IsClaudeMode(_compactMode)) MorphClaudePod();
            // Claude is done and a download is still going: its eyes go to the download's bubble
            else if (_dlPodShown && !_dlPodMorphing && _dlPodEyesU < 0.5) StartEyeTrip(forward: true);
            else HidePod();
        }
        if (!_podShown || _podKind != PodKind.Claude || PodSwapping) return;

        var c = _claude.Active;
        bool attention = c?.NeedsAttention == true;
        bool done = DateTime.Now < _podDoneUntil;

        // happy ^^ when done
        if (done != _podHappy)
        {
            _podHappy = done;
            PodEyeL.Visibility = PodEyeR.Visibility = done ? Visibility.Collapsed : Visibility.Visible;
            PodHappyL.Visibility = PodHappyR.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        }

        // attention: outline + eyes pulse orange
        if (attention != _podAttentionAnim)
        {
            _podAttentionAnim = attention;
            if (attention)
            {
                PodTrack.StrokeThickness = 2;
                PodTrack.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                PodTrack.BeginAnimation(OpacityProperty, null);
                PodTrack.Opacity = 0.35;
                PodTrack.StrokeThickness = 1.6;
            }
            var eye = new SolidColorBrush(attention ? ClaudeColor : Color.FromRgb(0xFF, 0xB8, 0x94));
            PodEyeL.Fill = PodEyeR.Fill = eye;
        }

        // working: a little bar running around the outline
        bool working = !attention && !done;
        if (working != _podWorking)
        {
            _podWorking = working;
            SetPodWorking(working);
        }
    }

    private bool _podWorking;

    private void SetPodWorking(bool on)
    {
        var d = TimeSpan.FromMilliseconds(Dur(240));
        // (the eyes themselves act out what Claude is doing: MainWindow.ClaudeFace.cs)

        if (on)
        {
            BuildPodOutline();
            PodArc.Visibility = Visibility.Visible;
            PodArc.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d)); // (the light itself: DrawClaudePod)
        }
        else
        {
            var fade = new DoubleAnimation(0, d);
            fade.Completed += (_, _) =>
            {
                if (_podWorking) return;
                PodArc.Visibility = Visibility.Collapsed;
            };
            PodArc.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>The pod's outline is drawn every frame (DrawClaudePod): just make it redraw now.</summary>
    private void BuildPodOutline()
    {
        _podShapeW = -1;
        PodArc.StrokeDashArray = null;
        DrawClaudePod(AnimNow);
    }

    private void ShowPod()
    {
        _podShown = true;
        if (_podKind == PodKind.Claude)
        {
            _podWorking = !(_claude.Active?.NeedsAttention != true && !(DateTime.Now < _podDoneUntil)); // force refresh
            BuildPodOutline();
            _podAttentionAnim = !(_claude.Active?.NeedsAttention == true); // force refresh
            _podHappy = !(DateTime.Now < _podDoneUntil);
        }
        else BuildPodOutline();
        ClaudePod.Visibility = Visibility.Visible;
        UpdatePodPosition();

        var ease = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(Dur(480));
        double s = S.SizeScale;
        PodScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s * 0.4, s, d) { EasingFunction = ease });
        PodScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s * 0.4, s, d) { EasingFunction = ease });
        _podSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(46, 0, d) { EasingFunction = ease });
        ClaudePod.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(220))));
    }

    private void HidePod()
    {
        _podShown = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(Dur(300));
        double s = S.SizeScale;
        PodScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s * 0.5, d) { EasingFunction = ease });
        PodScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s * 0.5, d) { EasingFunction = ease });
        _podSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(46, d) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => { if (!_podShown) ClaudePod.Visibility = Visibility.Collapsed; };
        ClaudePod.BeginAnimation(OpacityProperty, fade);
    }

    private void PodDone() => _podDoneUntil = DateTime.Now.AddSeconds(3.2);

    private void ClaudePod_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SoundService.Play(Sfx.Click);
        // the focus bubble opens the Focus tab (a timer's, the Relógio tab)
        var tab = _podKind == PodKind.Focus ? (PodIsTimer ? Tab.Clock : Tab.Focus) : Tab.Claude;
        if (_podKind == PodKind.Focus) _userTab = tab;
        else _podOpenRequest = true;
        SelectTab(tab, animate: false);
        _clickOpened = true;
        _hoverRequested = false;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(2200);
        _hoverTimer.Start();
        UpdateState();
    }

    // =====================================================================================
    // scroll on the island
    // =====================================================================================

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!S.ScrollVolume) return;
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if ((DateTime.Now - _lastTrackSkip).TotalMilliseconds < 450) return;
            _lastTrackSkip = DateTime.Now;
            SoundService.Play(Sfx.Click);
            _ = e.Delta < 0 ? _media.NextAsync() : _media.PreviousAsync();
            return;
        }

        if (!_audio.TryGet(out float vol, out bool mute)) return;
        double next = Math.Clamp((mute ? 0 : vol) + e.Delta / 120.0 * 0.02, 0, 1);
        SetVolume(next);
        if (_levelKind != LevelKind.Volume) SetDeviceListOpen(false);
        ShowVolumeLevel(next, false);
        VolumeSlider.Value = next;
        if (_state != IslandState.Expanded && S.VolumeOsd) _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.3);
        UpdateState();
    }

    // =====================================================================================
    // battery
    // =====================================================================================

    private void CheckBattery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) ||
            ps.BatteryChargeStatus == System.Windows.Forms.BatteryChargeStatus.Unknown)
            return;

        bool online = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        int pct = (int)Math.Round(ps.BatteryLifePercent * 100);

        if (_lastOnline is bool was && was != online && S.BatteryAlerts)
        {
            _batteryCharging = online;
            SoundService.Play(Sfx.Toggle);
            if (online) Happy();
            ShowTransient(CompactMode.Battery, Loc.T(online ? "bat.charging" : "bat.onbattery"), pct + "%", 3.5);
        }
        _lastOnline = online;

        if (online || pct > S.BatteryLowPercent + 3) _lowWarned = false;
        else if (!_lowWarned && pct <= S.BatteryLowPercent && S.BatteryAlerts)
        {
            _lowWarned = true;
            SoundService.Play(Sfx.Attention);
            ShowTransient(CompactMode.BatteryLow, Loc.T("bat.low"), pct + "%", 6);
        }
    }

    // =====================================================================================
    // shelf
    // =====================================================================================

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!S.ShelfEnabled || _draggingOut || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        _dragLast = DateTime.Now;
        if (_dragOver) return;

        _dragOver = true;
        ShelfDropZone.Background = new SolidColorBrush(Color.FromArgb(0x30, ShelfColor.R, ShelfColor.G, ShelfColor.B));
        SelectTab(Tab.Shelf, animate: _state == IslandState.Expanded);
        UpdateState();
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragOver = false;
        ShelfDropZone.Background = new SolidColorBrush(Color.FromArgb(0x0D, 255, 255, 255));
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        bool wasEmpty = _shelf.Count == 0;
        foreach (var f in files)
        {
            if (_shelf.Contains(f, StringComparer.OrdinalIgnoreCase)) continue;
            if (!File.Exists(f) && !Directory.Exists(f)) continue;
            _shelf.Add(f);
            added++;
        }
        if (added > 0)
        {
            SoundService.Play(Sfx.Expand);
            Happy();
            SaveShelf();
            ShelfStored(wasEmpty);
        }

        // stay open a moment so you can see the new item
        _clickOpened = true;
        _hoverRequested = false;
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _hoverTimer.Start();
        UpdateState();
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        if (_shelfSel.Mode) // while selecting, this button is "Marcar todos"
        {
            SoundService.Play(Sfx.Click);
            _shelfSel.MarkAll();
            return;
        }
        _shelf.Clear();
        SaveShelf();
        RenderShelf();
    }

    private void LoadShelf()
    {
        try
        {
            if (!File.Exists(ShelfFile)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfFile)) ?? new();
            _shelf.AddRange(list.Where(p => File.Exists(p) || Directory.Exists(p)));
        }
        catch
        {
            // ignore
        }
    }

    private void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf));
        }
        catch
        {
            // ignore
        }
    }

    private void RenderShelf()
    {
        ShelfItems.Children.Clear();
        _shelfSel.Items.Clear();
        bool empty = _shelf.Count == 0;
        ShelfEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ShelfScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShelfClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShelfHint.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        foreach (var path in _shelf) ShelfItems.Children.Add(ShelfItem(path));
        _shelfSel.Sync();
    }

    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    private static ImageSource? Thumb(string path)
    {
        if (ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant()) && File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 80;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // fall back to the icon
            }
        }
        return Native.ShellIcon(path);
    }

    private UIElement ShelfItem(string path)
    {
        string name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(name)) name = path;

        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Background = Thumb(path) is { } src
                ? new ImageBrush(src) { Stretch = ImageExt.Contains(Path.GetExtension(path).ToLowerInvariant()) ? Stretch.UniformToFill : Stretch.Uniform }
                : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = name,
            FontSize = 10,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 66,
            Margin = new Thickness(0, 5, 0, 0)
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        var remove = new Border
        {
            Width = 17,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x3A, 0x3A, 0x3E)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Visibility = Visibility.Hidden,
            Cursor = Cursors.Hand,
            Tag = "noselect",
            Child = new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 7,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // marked: a tinted frame and a check where the X goes
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Opacity = 0, IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xAA, ShelfColor.R, ShelfColor.G, ShelfColor.B)),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, ShelfColor.R, ShelfColor.G, ShelfColor.B))
        };
        var (badge, paintBadge) = SelBadge(ShelfColor, Color.FromRgb(0x06, 0x26, 0x1C));
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, 3, 3, 0);

        var grid = new Grid { Width = 74, Height = 76 };
        grid.Children.Add(frame);
        grid.Children.Add(stack);
        grid.Children.Add(remove);
        grid.Children.Add(badge);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 2, 0),
            Child = grid,
            Cursor = Cursors.Hand,
            ToolTip = path,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.6, 0.6),
            Opacity = 0
        };
        // pop in
        var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(320))) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(200))));

        card.MouseEnter += (_, _) =>
        {
            card.Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
            remove.Visibility = _shelfSel.Mode || _shelfSel.Marked.Contains(path) ? Visibility.Hidden : Visibility.Visible;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Brushes.Transparent;
            remove.Visibility = Visibility.Hidden;
        };
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _shelf.Remove(path);
            SaveShelf();
            RenderShelf();
        };
        bool wasMarked = false;
        _shelfSel.Items.Add(new SelItem
        {
            Path = path,
            Element = card,
            DoubleClick = () =>
            {
                try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
            },
            Paint = (marked, mode) =>
            {
                paintBadge(marked, mode);
                if (marked != wasMarked)
                {
                    wasMarked = marked;
                    frame.BeginAnimation(OpacityProperty, new DoubleAnimation(marked ? 1 : 0, TimeSpan.FromMilliseconds(Dur(160))));
                }
                remove.Visibility = !marked && !mode && card.IsMouseOver ? Visibility.Visible : Visibility.Hidden;
            }
        });
        return card;
    }
}
