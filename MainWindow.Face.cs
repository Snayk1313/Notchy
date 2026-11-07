using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Notchy;

/// <summary>The little face in the header of every tab, plus the waveform shortcut back to the Music tab.</summary>
public partial class MainWindow
{
    private readonly EyesFace _face = new() { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Stopwatch _faceClock = Stopwatch.StartNew();
    private double _faceLast;
    private Border _headerWave = null!;
    private Visualizer _headerWaveViz = null!;

    // eyes in the minimized island while the song has no lyric line (intro, solo, instrumental)
    private readonly EyesFace _compactFace = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Opacity = 0 };
    private bool _compactInstrumental, _compactFaceOn;
    private double _compactFaceLast;

    private void InitFace()
    {
        // eyes right before the date, like a face in the middle of the header
        var center = (Panel)HeaderClaudeDot.Parent;
        center.Children.Insert(center.Children.IndexOf(HeaderClaudeDot) + 1, _face);

        // waveform shortcut (only on the other tabs, only when there is music)
        _headerWaveViz = new Visualizer { Width = 20, Height = 12, IsHitTestVisible = false };
        _headerWave = new Border
        {
            Child = _headerWaveViz,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 4, 5, 4),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _headerWave.MouseEnter += (_, _) => _headerWave.Background = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255));
        _headerWave.MouseLeave += (_, _) => _headerWave.Background = Brushes.Transparent;
        _headerWave.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _userTab = Tab.Music;
            SelectTab(Tab.Music, animate: true);
        };
        var right = (Panel)BatteryText.Parent;
        right.Children.Insert(right.Children.IndexOf(BatteryText), _headerWave);

        // same spot as the lyric text in the minimized island
        ((Panel)CompactTextHost.Parent).Children.Add(_compactFace);

    }

    // ---- one pair of eyes at a time: while a bubble next to the music island has eyes (Claude's, or the
    //      download's), the island shows the song's name where it would show its eyes. They come back only once
    //      the bubble is completely gone, and a bubble with eyes waits for them to be gone before it appears.

    private bool _islandFaceBlocked;

    private bool BubbleEyesAround =>
        EyeTripActive
        || (ClaudePod.Visibility == Visibility.Visible && ClaudePod.Opacity > 0.01)
        || (DownloadPod.Visibility == Visibility.Visible && DownloadPod.Opacity > 0.01 && DlPodEyes.Opacity > 0.01)
        || (PodWanted() && !_podShown)                                            // about to appear
        || (DownloadPodWanted(ActiveTransfer) && !_dlPodShown && !ClaudeOnScreen); // about to appear, with eyes

    /// <summary>The music island's eyes are (still) on screen, even fading out.</summary>
    private bool CompactFaceVisible => _compactFaceOn || _compactFace.Opacity > 0.01;

    /// <summary>What the music island says now: the lyric line, or the song's name while a bubble has the eyes.</summary>
    private string MusicCompactShown => _compactInstrumental && _islandFaceBlocked ? _musicTitleText : _musicCompactText;

    /// <summary>Every frame, before the bubbles are updated.</summary>
    private void UpdateIslandFaceGate()
    {
        bool blocked = BubbleEyesAround;
        if (blocked == _islandFaceBlocked) return;
        _islandFaceBlocked = blocked;
        if (_compactMode != CompactMode.Music) return;
        SetCompactText(MusicCompactShown);
        ShowCompactFace(_compactInstrumental && !blocked);
    }

    private void ShowCompactFace(bool on)
    {
        if (on == _compactFaceOn) return;
        _compactFaceOn = on;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(Dur(300));
        if (on)
        {
            _compactFaceLast = _faceClock.Elapsed.TotalSeconds;
            _compactFace.Visibility = Visibility.Visible;
            var tt = new TranslateTransform();
            _compactFace.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(8, 0, d) { EasingFunction = ease });
            _compactFace.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, d) { EasingFunction = ease });
            UpdateCompactFace();
        }
        else
        {
            var a = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(160)));
            a.Completed += (_, _) => { if (!_compactFaceOn) _compactFace.Visibility = Visibility.Collapsed; };
            _compactFace.BeginAnimation(OpacityProperty, a);
        }
    }

    /// <summary>Every frame while the minimized island shows the eyes: album color, swaying while it plays.</summary>
    private void UpdateCompactFace()
    {
        double now = _faceClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _compactFaceLast, 0, 0.1);
        _compactFaceLast = now;
        _compactFace.Color = S.EyesUseAlbumColor ? Bright(_musicAccent) : ParseColor(S.EyeColor);
        _compactFace.BaseMood = _media.IsPlaying ? Mood.Normal : Mood.Sleepy;
        _compactFace.Update(dt, S.LivelyEyes && _media.IsPlaying ? 1 : 0);
    }

    /// <summary>A short expression on the header face (if lively eyes are on).</summary>
    private void FaceReact(Mood mood, double seconds = 1.6)
    {
        if (S.LivelyEyes) _face.React(mood, seconds);
    }

    /// <summary>Every frame while the island is open: mood of the tab, where to look, music beat, waveform.</summary>
    private void UpdateFace(double peak)
    {
        double now = _faceClock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _faceLast, 0, 0.1);
        _faceLast = now;

        _face.Color = TabColor(_tab);

        bool hasMusic = _media.HasSession && !string.IsNullOrEmpty(_media.Title);
        bool playing = hasMusic && _media.IsPlaying;
        var c = _claude.Active;

        Mood mood = Mood.Normal;
        double sway = 0;
        if (S.LivelyEyes)
        {
            mood = _tab switch
            {
                Tab.Claude when c?.NeedsAttention == true => Mood.Worried,
                Tab.Claude when c is { IsWorking: true } => Mood.Working,
                Tab.Chat when _runner?.Busy == true => Mood.Thinking,
                Tab.Focus when _focus.Running && _focus.Mode == FocusMode.Focus => Mood.Focused,
                Tab.Music when hasMusic && !playing => Mood.Sleepy,
                Tab.Shelf when _dragOver => Mood.Wide,
                _ => Mood.Normal
            };
            if (_tab == Tab.Music && playing) sway = 1;

            // look at the mouse while it moves over the island (or while you drag a file over it)
            if (Island.IsMouseOver || _dragOver)
            {
                try
                {
                    Native.GetCursorPos(out var p);
                    var local = _face.PointFromScreen(new Point(p.X, p.Y));
                    _face.LookAt((local.X - _face.ActualWidth / 2) / 140, (local.Y - _face.ActualHeight / 2) / 70);
                }
                catch
                {
                    _face.LookAt(null, null);
                }
            }
            else _face.LookAt(null, null);
        }
        else _face.LookAt(null, null);

        _face.BaseMood = mood;
        _face.Update(dt, sway);

        // a tab that already has eyes (the shelf's box, the downloads' sleepy cloud, the focus ring) doesn't need a second pair up here
        bool tabHasEyes = (_tab == Tab.Shelf && ShelfEmpty.Visibility == Visibility.Visible)
                          || (_tab == Tab.Downloads && _dlEmpty) || _tab == Tab.Focus || _tab == Tab.News;
        double faceTarget = tabHasEyes ? 0 : 1;
        if (_face.LayoutTransform is not ScaleTransform fs)
        {
            fs = new ScaleTransform(1, 1);
            _face.LayoutTransform = fs;
        }
        double k = Math.Clamp(dt * 14, 0, 1);
        fs.ScaleX += (faceTarget - fs.ScaleX) * k;
        if (Math.Abs(fs.ScaleX - faceTarget) < 0.01) fs.ScaleX = faceTarget;
        _face.Opacity = fs.ScaleX;
        _face.Margin = new Thickness(0, 0, 8 * fs.ScaleX, 0);
        _face.Visibility = fs.ScaleX < 0.01 ? Visibility.Collapsed : Visibility.Visible;

        // waveform shortcut
        bool showWave = hasMusic && _tab != Tab.Music;
        _headerWave.Visibility = showWave ? Visibility.Visible : Visibility.Collapsed;
        if (showWave)
        {
            var accent = Bright(_musicAccent);
            if (_headerWaveViz.BarBrush is not SolidColorBrush sb || sb.Color != accent)
            {
                _headerWaveViz.BarBrush = new SolidColorBrush(accent);
                _headerWaveViz.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.7, Color = accent };
            }
            _headerWave.ToolTip = Loc.T("tab.music") + (hasMusic ? " · " + _media.Title : "");
            if (_slowTick && _tick % 2 == 0) _headerWaveViz.Tick(peak, playing, _audio.HasMeter);
            _headerWaveViz.Step();
        }
    }
}
