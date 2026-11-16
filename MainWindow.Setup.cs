using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Notchy;

/// <summary>What the island does while the assistant of the first opening is on screen.</summary>
public partial class MainWindow
{
    private const double SetupSmileSeconds = 0.8;
    private const int ConfettiDelayMs = 350;

    private bool _setupOpen;

    internal bool NoticesAllowed => _notices.Available;

    /// <summary>Asks Windows for the notifications again (the first answer is asked when Notchy starts).</summary>
    internal void AskNotices()
    {
        if (!_notices.Available) _notices.Start();
    }

    /// <summary>A new install (a reinstall keeps its settings and skips this): the questions come after the opening animation.</summary>
    private async Task OpenSetupAfterIntroAsync()
    {
        while (_introActive) await Task.Delay(200);
        App.OpenSetup();
    }

    internal void SetupStarted()
    {
        _setupOpen = true;
        SyncSetupFace();
    }

    internal void SetupCheer()
    {
        _compactFace.React(Mood.Happy, SetupSmileSeconds);
        _face.React(Mood.Happy, SetupSmileSeconds);
    }

    /// <summary>The weather now, for the assistant's picture.</summary>
    internal WeatherNow? WeatherForSetup => _weather.Current;

    /// <summary>"Guardar o que eu copiar" was switched on in the assistant: the island shows what a copy looks like.</summary>
    internal void SetupClipDemo()
    {
        if (_state is not (IslandState.Idle or IslandState.Compact)) return;
        GlowOutline(ClipColor);
        BlinkOnce();
    }

    /// <summary>
    /// The tour: the real island shows what each tip is about (null puts it back). It opens for most of them: on the home tab,
    /// lighting the date or the bell, or on Copiados or the Prateleira; for the volume it closes and shows the bar.
    /// </summary>
    internal void SetupTour(string? tip)
    {
        bool open = tip is "open" or "date" or "bell" or "clip" or "shelf";
        _forcedState = open ? IslandState.Expanded : null;
        UpdateState(); // (opening picks a tab by itself: the tip's one comes right after)
        if (open)
        {
            var tab = tip == "clip" ? Tab.Clip : tip == "shelf" ? Tab.Shelf : Tab.Home;
            if (_tab != tab || _subOpen) SelectTab(tab, animate: true);
        }
        else if (tip == "volume" && _audio.TryGet(out float volume, out bool muted))
        {
            ShowVolumeLevel(volume, muted);
            _volumeOsdUntil = DateTime.UtcNow.AddSeconds(2.3);
            UpdateState();
        }
        LightDate(tip == "date");
        LightBell(tip == "bell");
    }

    internal async void SetupEnded(bool celebrate)
    {
        _setupOpen = false;
        SetupTour(null);
        if (!celebrate) return;
        SoundService.Play(Sfx.Done);
        await Task.Delay(ConfettiDelayMs); // the island has closed by then
        SetupCheer();
        BurstConfetti();
    }

    private void LightBell(bool on)
    {
        if (_bell == null) return;
        _bell.Background = White((byte)(on ? 0x30 : 0x0E));
        _bell.Effect = on ? new DropShadowEffect { Color = TabColor(_tab), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 } : null;
    }

    /// <summary>While the assistant is open the eyes are awake and follow the mouse, wherever it is on the screen.</summary>
    private void SyncSetupFace()
    {
        if (!_setupOpen) return;
        _compactFace.BaseMood = Mood.Normal;
        try
        {
            Native.GetCursorPos(out var p);
            var local = _compactFace.PointFromScreen(new Point(p.X, p.Y));
            _compactFace.LookAt((local.X - _compactFace.ActualWidth / 2) / 400, (local.Y - _compactFace.ActualHeight / 2) / 300);
        }
        catch (InvalidOperationException)
        {
            _compactFace.LookAt(null, null); // not on screen right now
        }
    }
}
