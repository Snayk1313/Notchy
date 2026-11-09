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

    /// <summary>The tour: the island opens and lights what each tip is about (-1 puts it back).</summary>
    internal void SetupTour(int tip)
    {
        _forcedState = tip >= 0 ? IslandState.Expanded : null;
        UpdateState();
        LightDate(tip == 1);
        LightBell(tip == 2);
    }

    internal async void SetupEnded(bool celebrate)
    {
        _setupOpen = false;
        SetupTour(-1);
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
