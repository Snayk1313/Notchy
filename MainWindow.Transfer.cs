using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>
/// Downloads and copies.
/// - Alone on the island: a small island with a download/copy icon, green eyes watching the progress light and
///   the %, while a green light fills the island's outline. The eyes react: they follow the light, get excited
///   near 100%, get sleepy when a download stalls, smile ^ ^ when it's done and look sad if it fails.
/// - With music, Claude or Focus on the island: a little green bubble next to it shows the download (eyes and
///   progress around it); hovering or clicking it opens the Downloads tab. Copies just light the outline.
/// - Downloads tab (expanded island): the downloads in progress (name, speed, size, time left, open folder,
///   cancel) and the last 5 finished ones (click to open, button to show in the folder).
/// </summary>
public partial class MainWindow
{
    private readonly TransferWatcher _transfers = new();
    private readonly IslandOutline _transferOutline = new();
    private readonly DownloadHistory _dlHistory = new();
    private bool _transferPolling;
    private double _transferFill;          // smoothed 0..1
    private double _transferDrawn = -1;
    private double _transferOpacity;       // smoothed
    private DateTime _transferDoneUntil;   // the outline fills green and fades after a transfer ends
    private TransferKind _transferDoneKind;
    private DateTime _transferStallSince = DateTime.MaxValue;
    private bool _dlOpenRequest;           // opened from the green bubble: show the Downloads tab
    private static readonly Color DoneGreen = Color.FromRgb(0x5E, 0xD6, 0x7D);
    private static readonly Color DownloadEyeColor = Color.FromRgb(0x9C, 0xF5, 0xB6);
    private const double TransferHeight = 34, TransferWidth = 200;

    private EyeRig? _transferRig, _dlPodRig;
    private bool _transferHappyShown, _dlPodHappyShown;

    private static bool IsTransferMode(CompactMode m) => m is CompactMode.Transfer or CompactMode.TransferDone or CompactMode.TransferFailed;

    private void InitTransfers()
    {
        _transfers.Finished += (kind, name, path) => Dispatcher.BeginInvoke(() => OnTransferFinished(kind, name, path));
        _transfers.Failed += name => Dispatcher.BeginInvoke(OnTransferFailed);

        var brush = new SolidColorBrush(DoneGreen);
        brush.Freeze();
        TransferLight.Stroke = TransferTrack.Stroke = brush;
        TransferLightGlow.Color = DoneGreen;
        TransferIcon.Foreground = TransferPercent.Foreground = brush;
        var eye = new SolidColorBrush(DownloadEyeColor);
        eye.Freeze();
        TransferEyeL.Fill = TransferEyeR.Fill = DlEyeL.Fill = DlEyeR.Fill = eye;
        TransferHappyL.Stroke = TransferHappyR.Stroke = DlHappyL.Stroke = DlHappyR.Stroke = eye;

        _transferRig = new EyeRig { Look = TransferLook, Left = TransferEyeLScale, Right = TransferEyeRScale };
        _dlPodRig = new EyeRig { Look = DlPodLook, Left = DlEyeLScale, Right = DlEyeRScale };
        BuildDownloadPodOutline();
        _dlHistory.Load();
    }

    private TransferInfo? ActiveTransfer => S.TransferProgress ? _transfers.Current : null;

    private void OnTransferFinished(TransferKind kind, string name, string? path)
    {
        if (!S.TransferProgress) return;
        _transferDoneUntil = DateTime.UtcNow.AddSeconds(1.6);
        _transferDoneKind = kind;
        if (kind == TransferKind.Download) NavDone(Tab.Downloads);
        if (kind == TransferKind.Download && path != null)
        {
            _dlHistory.Add(path);
            if (_state == IslandState.Expanded && _tab == Tab.Downloads) UpdateDownloadsTab(force: true);
        }
        if (_dlPodShown) _dlPodMoodUntil = (DateTime.UtcNow.AddSeconds(2.4), true);
        // alone on the island: happy eyes ^ ^ (with music/Claude, the bubble smiles)
        if (DecideCompactWithoutTransfer() == CompactMode.None)
            ShowTransient(CompactMode.TransferDone, "", "", 2.6);
    }

    private void OnTransferFailed()
    {
        if (!S.TransferProgress) return;
        if (_dlPodShown) _dlPodMoodUntil = (DateTime.UtcNow.AddSeconds(2.2), false);
        if (DecideCompactWithoutTransfer() == CompactMode.None)
            ShowTransient(CompactMode.TransferFailed, "", "", 2.4);
        if (_state == IslandState.Expanded && _tab == Tab.Downloads) UpdateDownloadsTab(force: true);
    }

    /// <summary>Checks for transfers in the background (reading the browser / Explorer can take a moment).</summary>
    private void PollTransfers()
    {
        if (!S.TransferProgress || _transferPolling) return;
        _transferPolling = true;
        Task.Run(() =>
        {
            try
            {
                _transfers.Poll();
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
            finally
            {
                _transferPolling = false;
            }
        });
    }

    // ------------------------------------------------------------------ texts

    /// <summary>"3,2 MB/s · 12,3 de 60 MB · faltam 12 s"</summary>
    private static string TransferDetail(TransferInfo t)
    {
        var parts = new List<string>();
        if (t.BytesPerSecond is double s && s >= 1024) parts.Add(FormatBytes((long)s) + "/s");
        if (t.Kind == TransferKind.Download)
            parts.Add(t.Total is long total ? Loc.F("xfer.of", FormatBytes(t.Bytes, total), FormatBytes(total)) : FormatBytes(t.Bytes));
        string eta = TransferEta(t);
        if (eta != "") parts.Add(eta);
        return string.Join("  ·  ", parts);
    }

    private static string TransferEta(TransferInfo t)
    {
        if (t.Eta is not TimeSpan eta) return "";
        double s = Math.Max(1, eta.TotalSeconds);
        string left = s < 60 ? $"{Math.Ceiling(s)} s"
            : s < 3600 ? $"{Math.Ceiling(s / 60)} min"
            : $"{(int)(s / 3600)} h {(int)(s % 3600 / 60)} min";
        return Loc.F("xfer.left", left);
    }

    private static string FormatBytes(long b) => FormatBytes(b, b);

    /// <summary>Size in the unit that fits <paramref name="scale"/> (so "12,3 de 60 MB" share a unit).</summary>
    private static string FormatBytes(long b, long scale) => scale switch
    {
        < 1024 * 1024 => $"{Math.Max(1, b / 1024)} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB"
    };

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return Loc.T("dl.ago.now");
        if (d.TotalHours < 1) return Loc.F("dl.ago.min", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Loc.F("dl.ago.h", (int)d.TotalHours);
        return Loc.F("dl.ago.d", (int)d.TotalDays);
    }

    // ------------------------------------------------------------------ compact island (alone)

    /// <summary>Icon and % of the small transfer island (a few times per second).</summary>
    private void UpdateTransferPanel()
    {
        var m = _compactMode;
        if (m == CompactMode.TransferDone)
        {
            TransferIcon.Text = "";
            TransferPercent.Text = "";
            return;
        }
        if (m == CompactMode.TransferFailed)
        {
            TransferIcon.Text = "";
            TransferPercent.Text = "";
            return;
        }
        if (ActiveTransfer is not { } t) return;
        TransferIcon.Text = t.Kind == TransferKind.Copy ? "" : "";
        TransferPercent.Text = t.Progress is double p ? $"{Math.Floor(p * 100)}%" : "";
    }

    // ------------------------------------------------------------------ eyes

    /// <summary>
    /// Where the green eyes look: down along with the light (glancing at the left and the right head), excited
    /// near the end, sleepy when the download stalls, sad after a failure.
    /// </summary>
    private EyePose TransferPose(TransferInfo? t, double headDown, bool failed, double time)
    {
        var p = EyePose.Rest;
        double sf = Math.Max(0.3, S.SpeedFactor);
        double tt = time / sf;
        if (failed)
        {
            p.Y = 2.5;
            p.L = p.R = 0.5;
            return p;
        }
        bool stalled = t != null && DateTime.UtcNow - _transferStallSince > TimeSpan.FromSeconds(4);
        if (stalled)
        {
            p.Y = 1;
            p.L = p.R = 0.3;
            return p;
        }
        // watch the light: down as it goes down, glancing at one side and the other
        p.X = Keys(tt % 4.8, (0, -2.6), (1.9, -2.6), (2.4, 2.6), (4.3, 2.6), (4.8, -2.6));
        p.Y = -2.2 + 4.4 * Math.Clamp(headDown, 0, 1);
        if (t?.Progress is double prog && prog >= 0.9)
        {
            // almost there!
            p.L = p.R = 1.2;
            p.W = 1.08;
            p.Y += 0.9 * Math.Sin(tt * Math.PI * 2 * 2.2);
            p.X *= 0.5;
        }
        // natural blinks
        double bu = (tt + _blinkSeed * 1.7) % 4.3;
        if (bu < 0.16)
        {
            double f = 1 - 0.88 * Math.Sin(Math.PI * bu / 0.16);
            p.L *= f;
            p.R *= f;
        }
        return p;
    }

    private void UpdateTransferEyes()
    {
        if (_transferRig == null) return;
        var t = ActiveTransfer;
        // stall: no growth for a while
        if (t is { Kind: TransferKind.Download } && (t.BytesPerSecond ?? 0) < 2048)
        {
            if (_transferStallSince == DateTime.MaxValue) _transferStallSince = DateTime.UtcNow;
        }
        else _transferStallSince = DateTime.MaxValue;

        double time = AnimNow;
        if (_state == IslandState.Compact && IsTransferMode(_compactMode))
        {
            bool happy = _compactMode == CompactMode.TransferDone;
            if (happy != _transferHappyShown)
            {
                _transferHappyShown = happy;
                TransferEyeL.Visibility = TransferEyeR.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
                TransferHappyL.Visibility = TransferHappyR.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
            }
            double headDown = _transferOutline.Length > 0 ? _transferOutline.DownAt(_transferHead) : 0;
            var pose = happy ? EyePose.Rest : TransferPose(t, headDown, _compactMode == CompactMode.TransferFailed, time);
            ApplyPose(_transferRig, pose, Ease(0.2));
            if (_dlPodMorphing) CopyPose(_transferRig, _dlPodRig); // the bubble's eyes are the island's eyes now
            TransferPanel.Opacity = _compactMode == CompactMode.TransferFailed ? 0.75 : 1;
        }
        UpdateDownloadPod(t, time);
    }

    // ------------------------------------------------------------------ outline light

    private double _transferHead; // where the light's head is (distance along the half outline)

    /// <summary>The green light around the island's edge, every frame.</summary>
    private void UpdateTransferLight()
    {
        var t = ActiveTransfer;
        bool done = DateTime.UtcNow < _transferDoneUntil;
        bool solo = IsTransferMode(_compactMode) && _compactMode != CompactMode.TransferFailed;
        // with music/Claude/Focus a download lives in the bubble; a copy still lights their outline
        bool copyAround = t?.Kind == TransferKind.Copy || (t == null && done && _transferDoneKind == TransferKind.Copy);
        bool show = (solo || copyAround) && (t != null || done) && _state == IslandState.Compact && !_introActive && !_islandMorphing;
        double k = Ease(Math.Clamp(0.14 / Math.Max(0.3, S.SpeedFactor), 0.05, 0.5));
        _transferOpacity += ((show ? 1 : 0) - _transferOpacity) * k;
        if (_transferOpacity < 0.01 && !show)
        {
            if (TransferLight.Visibility == Visibility.Visible)
                TransferLight.Visibility = TransferTrack.Visibility = Visibility.Collapsed;
            _transferFill = _islandMorphing && t?.Progress is double now ? now : 0;
            _transferDrawn = -1;
            return;
        }
        TransferLight.Visibility = TransferTrack.Visibility = Visibility.Visible;
        // after the green flash, fade away (after a cancel, just fade with the look it had)
        double flash = done && t == null ? Math.Clamp((_transferDoneUntil - DateTime.UtcNow).TotalSeconds / 0.7, 0, 1) : 1;
        TransferLight.Opacity = _transferOpacity * flash;
        TransferTrack.Opacity = _transferOpacity * flash * 0.22;

        // follow the island exactly (it animates its size)
        double W = Island.ActualWidth, H = Island.ActualHeight - _topHidden;
        if (W < 2 || H < 2) return;
        if (_transferOutline.Build(W, H, _topHidden))
        {
            TransferLight.Width = TransferTrack.Width = W;
            TransferLight.Height = TransferTrack.Height = H + _topHidden;
            TransferTrack.Data = _transferOutline.Segment(0, _transferOutline.Length);
            _transferDrawn = -1;
        }
        TransferLight.Margin = TransferTrack.Margin = Island.Margin;
        TransferLight.RenderTransform = TransferTrack.RenderTransform = Island.RenderTransform;
        if (t == null && !done) return;

        double len = _transferOutline.Length;
        double? progress = t != null ? t.Progress : 1;
        if (progress is double p)
        {
            // real progress: both sides fill from the top and meet at the bottom centre
            _transferFill += (p - _transferFill) * k;
            if (Math.Abs(_transferFill - p) < 0.0005) _transferFill = p;
            if (_transferDrawn < 0 || Math.Abs(_transferFill - _transferDrawn) > 0.0004)
            {
                _transferDrawn = _transferFill;
                TransferLight.Data = _transferOutline.Segment(0, Math.Max(1.5, len * _transferFill));
            }
            _transferHead = len * _transferFill;
        }
        else
        {
            // unknown total: a short light glides down the sides and back
            double seg = len * 0.3;
            double cycle = 2.2 * Math.Max(0.3, S.SpeedFactor);
            double phase = AnimNow % cycle / cycle;
            double eased = 0.5 - 0.5 * Math.Cos(phase * Math.PI * 2);
            double from = eased * (len - seg);
            TransferLight.Data = _transferOutline.Segment(from, from + seg);
            _transferHead = from + seg;
            _transferFill = 0;
            _transferDrawn = -1;
        }
    }

    // ------------------------------------------------------------------ the green bubble (download + something else)

    private bool _dlPodShown, _dlPodMorphing;
    private (DateTime until, bool happy) _dlPodMoodUntil;

    private bool DownloadPodWanted(TransferInfo? t)
    {
        if (!S.TransferProgress || _hiddenForFullscreen || _introActive) return false;
        if (_state != IslandState.Compact || IsTransferMode(_compactMode) || _compactMode == CompactMode.None) return false;
        return t is { Kind: TransferKind.Download } || DateTime.UtcNow < _dlPodMoodUntil.until;
    }

    private void UpdateDownloadPod(TransferInfo? t, double time)
    {
        bool want = DownloadPodWanted(t);
        if (want && !_dlPodShown && !_dlPodMorphing)
        {
            // music came back while the download had the island: the island becomes the bubble
            if (IslandJustLeft(m => IsTransferMode(m))) MorphTransferIslandIntoPod(t);
            else if (CompactFaceVisible) { } // the music island's eyes go away first
            else ShowDownloadPod();
        }
        else if (!want && _dlPodShown && !_dlPodMorphing)
        {
            // music stopped and the download takes the island: the bubble becomes the island
            if (_state == IslandState.Compact && _compactMode == CompactMode.Transfer)
            {
                if (_transferRig != null && _dlPodRig != null) _transferRig.Pose = _dlPodRig.Pose;
                _dlPodShown = false;
                _dlPodMorphing = true;
                MorphPodIntoIsland(DownloadPod, DlPodX, DlPodSlide, TransferWidth, TransferPanel, () => _transferOpacity = 1,
                    () => _dlPodMorphing = false);
            }
            else HideDownloadPod();
        }
        if (_dlPodRig == null) return;
        if (_dlPodMorphing && !_dlPodShown)
        {
            // the eyes follow the island's (UpdateTransferEyes); the light stretches into the island's edge
            DrawDownloadPod(t?.Progress ?? (DateTime.UtcNow < _transferDoneUntil ? 1 : -1));
            return;
        }
        if (!_dlPodShown) return;

        // same size/place rules as the island and the Claude pod (which sits on the other side)
        DownloadPod.Height = 34 + _topHidden;
        DownloadPod.Margin = Island.Margin;
        DlPodContent.Margin = new Thickness(0, _topHidden, 0, 0);
        double scale = S.SizeScale;
        if (!_dlPodMorphing) DlPodX.X = Island.ActualWidth * scale / 2 + 8 * scale + 70 * scale / 2;

        bool mood = DateTime.UtcNow < _dlPodMoodUntil.until;
        bool happy = mood && _dlPodMoodUntil.happy;
        bool sad = mood && !_dlPodMoodUntil.happy;
        if (happy != _dlPodHappyShown)
        {
            _dlPodHappyShown = happy;
            DlEyeL.Visibility = DlEyeR.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
            DlHappyL.Visibility = DlHappyR.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
        }
        double prog = happy ? 1 : t?.Progress ?? -1;
        ApplyPose(_dlPodRig, happy ? EyePose.Rest : TransferPose(t, prog >= 0 ? prog : 0.5, sad, time), Ease(0.2));
        UpdateDownloadPodInfo(happy, sad, prog);
        DrawDownloadPod(prog);
        DlPodArc.Opacity = sad ? 0.25 : 1;
    }

    private static readonly Geometry PodCheck = Geometry.Parse("M0,4.5 L3.6,8 L10.5,0.5");
    private static readonly Geometry PodCross = Geometry.Parse("M0,0 L7,7 M7,0 L0,7");
    private int _dlPodInfoKind = -1; // 0 = %, 1 = no size yet (icon), 2 = done (check), 3 = failed (cross)

    /// <summary>What the eyeless bubble shows: the %, the download icon while the size is unknown, ✓ when done, × if it failed.</summary>
    private void UpdateDownloadPodInfo(bool happy, bool sad, double prog)
    {
        int kind = happy ? 2 : sad ? 3 : prog >= 0 ? 0 : 1;
        if (kind != _dlPodInfoKind)
        {
            _dlPodInfoKind = kind;
            DlPodPct.Visibility = kind == 0 ? Visibility.Visible : Visibility.Collapsed;
            DlPodMid.Visibility = kind == 1 ? Visibility.Visible : Visibility.Collapsed;
            DlPodMark.Visibility = kind >= 2 ? Visibility.Visible : Visibility.Collapsed;
            DlPodMark.Data = kind == 3 ? PodCross : PodCheck;
            DlPodMark.Opacity = kind == 3 ? 0.55 : 1;
            if (kind >= 2)
            {
                // a little pop
                var pop = new ScaleTransform(0.4, 0.4, 5, 4);
                DlPodMark.RenderTransform = pop;
                var ease = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
                var d = TimeSpan.FromMilliseconds(Dur(320));
                pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
                pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            }
        }
        if (kind == 0)
        {
            string text = Math.Floor(prog * 100).ToString("0") + "%";
            if (DlPodPct.Text != text) DlPodPct.Text = text;
        }
    }

    /// <summary>Music came back while the download had the island: the island becomes the download bubble.</summary>
    private void MorphTransferIslandIntoPod(TransferInfo? t)
    {
        _dlPodShown = true;
        _dlPodMorphing = true;
        _dlPodHappyShown = !_dlPodHappyShown; // force refresh
        DownloadPod.Height = 34 + _topHidden;
        DownloadPod.Margin = Island.Margin;
        DlPodContent.Margin = new Thickness(0, _topHidden, 0, 0);
        CopyPose(_transferRig, _dlPodRig); // the bubble's eyes start where the island's are
        _dlPodEyesU = ClaudeOnScreen ? 0 : 1;
        UpdateDownloadPodInfo(false, false, t?.Progress ?? -1);
        _transferOpacity = 0;              // the bubble draws the progress from now on
        TransferLight.Visibility = TransferTrack.Visibility = Visibility.Collapsed;
        BuildDownloadPodOutline();
        _dlShapeW = -1;
        DrawDownloadPod(t?.Progress ?? -1);

        double s = S.SizeScale;
        double toX = TargetSize(_state).Width * s / 2 + 8 * s + 70 * s / 2;
        MorphIslandIntoPod(DownloadPod, DlPodScale, DlPodX, DlPodSlide, TransferWidth, toX, () => _dlPodMorphing = false);
    }

    private void BuildDownloadPodOutline()
    {
        _dlShapeW = -1;
        DlPodArc.StrokeDashArray = null;
        DlPodArc.Stroke = new SolidColorBrush(DoneGreen); // the island's green, so the hand-over is seamless
        DrawDownloadPod(-1);
    }

    private void ShowDownloadPod()
    {
        _dlPodShown = true;
        _dlPodHappyShown = !_dlPodHappyShown; // force refresh
        _dlPodEyesU = ClaudeOnScreen ? 0 : 1; // pops in already showing eyes or the %
        DownloadPod.Visibility = Visibility.Visible;
        var ease = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(Dur(480));
        double s = S.SizeScale;
        DlPodScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s * 0.4, s, d) { EasingFunction = ease });
        DlPodScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s * 0.4, s, d) { EasingFunction = ease });
        DlPodSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-46, 0, d) { EasingFunction = ease });
        DownloadPod.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(220))));
    }

    private void HideDownloadPod()
    {
        _dlPodShown = false;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var d = TimeSpan.FromMilliseconds(Dur(300));
        double s = S.SizeScale;
        DlPodScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(s * 0.5, d) { EasingFunction = ease });
        DlPodScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(s * 0.5, d) { EasingFunction = ease });
        DlPodSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-46, d) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, d);
        fade.Completed += (_, _) => { if (!_dlPodShown) DownloadPod.Visibility = Visibility.Collapsed; };
        DownloadPod.BeginAnimation(OpacityProperty, fade);
    }

    private void DownloadPod_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SoundService.Play(Sfx.Click);
        _dlOpenRequest = true;
        SelectTab(Tab.Downloads, animate: false);
        _clickOpened = true;
        _hoverRequested = false;
        _hoverTimer.Stop();
        UpdateState();
    }

    private void DownloadPod_MouseEnter(object sender, MouseEventArgs e)
    {
        if (S.Open != OpenMode.Hover) return;
        _dlOpenRequest = true;
        Island_MouseEnter(sender, e);
    }

    // ------------------------------------------------------------------ Downloads tab

    private const double DownloadsMaxHeight = 300;
    private double _dlTabHeight = 96;
    private string _dlTabKey = "";
    private readonly Dictionary<string, (TextBlock name, TextBlock detail, TextBlock pct, Border fill, Grid bar)> _dlRows = new();
    private string? _dlCanceling;

    private void ApplyTransferSettings()
    {
        if (_nav.TryGetValue(Tab.Downloads, out var n))
            n.button.Visibility = S.TransferProgress ? Visibility.Visible : Visibility.Collapsed;
        if (!S.TransferProgress && _tab == Tab.Downloads) SelectTab(Tab.Home, animate: false);
    }

    /// <summary>Builds (when the list changes) or refreshes (texts, bars) the Downloads tab.</summary>
    private void UpdateDownloadsTab(bool force = false, bool texts = true)
    {
        var t = ActiveTransfer is { Kind: TransferKind.Download } a ? a : null;
        var files = t == null ? new List<DownloadFile>() : _transfers.Files.ToList();
        var history = _dlHistory.Entries;
        string key = string.Join("|", files.Select(f => f.PartialPath)) + "#" + string.Join("|", history.Select(h => h.Path)) +
                     "#" + (_dlCanceling ?? "");
        if (force || key != _dlTabKey)
        {
            _dlTabKey = key;
            BuildDownloadsTab(t, files, history);
        }

        // live values
        foreach (var f in files)
        {
            if (!_dlRows.TryGetValue(f.PartialPath, out var row)) continue;
            bool main = t != null && f.PartialPath == t.PartialPath && files.Count == 1;
            // each download has its own size / time left when the browser says it (Zen/Firefox always do)
            var info = main ? t! : new TransferInfo(TransferKind.Download, f.Name, 1, f.Progress, f.Bytes, f.Total,
                f.BytesPerSecond > 0 ? f.BytesPerSecond : null, f.Eta);
            double? p = info.Progress;
            if (texts || force)
            {
                row.name.Text = info.Name;
                row.detail.Text = _dlCanceling == f.PartialPath ? Loc.T("dl.canceling") : TransferDetail(info);
                row.pct.Text = p is double v ? $"{Math.Floor(v * 100)}%" : "";
            }
            double w = row.bar.ActualWidth;
            if (w > 0)
            {
                if (p is double pv)
                {
                    row.fill.Width = Math.Max(4, w * pv);
                    ((TranslateTransform)row.fill.RenderTransform).X = 0;
                }
                else
                {
                    double seg = w * 0.28, cycle = 1.8;
                    double ph = AnimNow % cycle / cycle;
                    row.fill.Width = seg;
                    ((TranslateTransform)row.fill.RenderTransform).X = (0.5 - 0.5 * Math.Cos(ph * Math.PI * 2)) * (w - seg);
                }
            }
        }
    }

    private void BuildDownloadsTab(TransferInfo? t, List<DownloadFile> files, IReadOnlyList<DownloadEntry> history)
    {
        DownloadsList.Children.Clear();
        _dlRows.Clear();
        double h = 0;

        _dlEmpty = files.Count == 0 && history.Count == 0;
        if (files.Count == 0 && history.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
            empty.Children.Add(SleepyCloud());
            empty.Children.Add(new TextBlock
            {
                Text = Loc.T("dl.empty"), FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center
            });
            DownloadsList.Children.Add(empty);
            h = 96;
        }
        if (files.Count > 0)
        {
            DownloadsList.Children.Add(SectionTitle(Loc.T("dl.active")));
            h += 18;
            foreach (var f in files)
            {
                DownloadsList.Children.Add(ActiveRow(f));
                h += 58;
            }
        }
        if (history.Count > 0)
        {
            DownloadsList.Children.Add(SectionTitle(Loc.T("dl.recent")));
            h += 18;
            foreach (var e in history)
            {
                DownloadsList.Children.Add(HistoryRow(e));
                h += 38;
            }
        }
        h = Math.Ceiling(h) + 4;
        // more than fits: the list scrolls (a little room so the rows don't touch the scrollbar)
        DownloadsList.Margin = new Thickness(0, 0, h > DownloadsMaxHeight ? 4 : 0, 0);
        h = Math.Clamp(h, 96, DownloadsMaxHeight);
        if (Math.Abs(h - _dlTabHeight) > 0.5)
        {
            _dlTabHeight = h;
            DownloadsTab.Height = h;
            if (_state == IslandState.Expanded && _tab == Tab.Downloads)
                AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
        }
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 9.5, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255)), Margin = new Thickness(4, 2, 0, 4), Height = 14
    };

    private Button SmallButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = glyph, FontSize = 12 },
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip
        };
        b.Click += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return b;
    }

    private FrameworkElement ActiveRow(DownloadFile f)
    {
        var green = new SolidColorBrush(DoneGreen);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromArgb(0x2E, DoneGreen.R, DoneGreen.G, DoneGreen.B)) });
        icon.Children.Add(new TextBlock
        {
            FontFamily = (FontFamily)FindResource("IconFont"), Text = "", FontSize = 12, Foreground = green,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        texts.Children.Add(name);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var pct = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = green, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        Grid.SetColumn(pct, 2);
        grid.Children.Add(pct);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string partial = f.PartialPath;
        buttons.Children.Add(SmallButton("", Loc.T("dl.folder"), () => ShowInFolder(partial)));
        var cancel = SmallButton("", Loc.T("dl.cancel"), () => CancelDownload(partial));
        cancel.IsEnabled = _dlCanceling == null;
        buttons.Children.Add(cancel);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        // progress bar under it
        var bar = new Grid { Height = 3, Margin = new Thickness(36, 7, 0, 0), ClipToBounds = true };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)) });
        var fill = new Border
        {
            CornerRadius = new CornerRadius(1.5), Background = green, HorizontalAlignment = HorizontalAlignment.Left, Width = 0,
            RenderTransform = new TranslateTransform(),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9, Color = DoneGreen }
        };
        bar.Children.Add(fill);
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 4);
        grid.Children.Add(bar);

        _dlRows[f.PartialPath] = (name, detail, pct, fill, bar);
        return new Border
        {
            Child = grid, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 8, 8), Margin = new Thickness(0, 0, 0, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255))
        };
    }

    private FrameworkElement HistoryRow(DownloadEntry e)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            FontFamily = (FontFamily)FindResource("IconFont"), Text = "", FontSize = 14, Width = 26,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), TextAlignment = TextAlignment.Center
        });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = Path.GetFileName(e.Path), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock
        {
            Text = FormatBytes(e.Size) + "  ·  " + Ago(e.Finished), FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255))
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        string path = e.Path;
        var folder = SmallButton("", Loc.T("dl.folder"), () => ShowInFolder(path));
        Grid.SetColumn(folder, 2);
        grid.Children.Add(folder);

        var row = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4, 8, 4), Margin = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Height = 36, ToolTip = Loc.T("dl.open")
        };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, ev) =>
        {
            if (ev.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return;
            ev.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenFile(path);
        };
        return row;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void CancelDownload(string partial)
    {
        if (_dlCanceling != null || ActiveTransfer is not { Kind: TransferKind.Download } t) return;
        _dlCanceling = partial;
        UpdateDownloadsTab(force: true);
        Task.Run(() => DownloadCanceller.Cancel(t)).ContinueWith(r => Dispatcher.BeginInvoke(() =>
        {
            _dlCanceling = null;
            UpdateDownloadsTab(force: true);
        }));
    }
}

/// <summary>A finished download kept in the Downloads tab.</summary>
public sealed record DownloadEntry(string Path, long Size, DateTime Finished);

/// <summary>The last 5 finished downloads (files you deleted drop out), saved next to the settings.</summary>
public sealed class DownloadHistory
{
    private const int Max = 5;
    private static readonly string FilePath = System.IO.Path.Combine(AppSettings.Folder, "downloads.json");
    private List<DownloadEntry> _list = new();

    public IReadOnlyList<DownloadEntry> Entries
    {
        get
        {
            int before = _list.Count;
            _list = _list.Where(e => File.Exists(e.Path)).ToList();
            if (_list.Count != before) Save();
            return _list;
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath)) _list = JsonSerializer.Deserialize<List<DownloadEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            _list = new();
        }
    }

    public void Add(string path)
    {
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        _list.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _list.Insert(0, new DownloadEntry(path, size, DateTime.UtcNow));
        if (_list.Count > Max) _list.RemoveRange(Max, _list.Count - Max);
        Save();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_list));
        }
        catch
        {
            // not important
        }
    }
}
