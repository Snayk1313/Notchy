using System;
using System.Windows;
using System.Windows.Media;

namespace Notchy;

/// <summary>
/// With the music playing, only one bubble at a time has eyes: Claude's while it works, the download's
/// otherwise. When they change bubble, the eyes go up and leave through the screen's top edge, a faint glow
/// runs along that edge (orange → green, or back) and they come down into the other bubble.
/// </summary>
public partial class MainWindow
{
    private const double EyeTripMs = 800;
    private const double TripRise = 30; // far enough up to be out of sight
    private double _tripStart = -1;     // seconds; -1 = no trip
    private bool _tripForward;          // true: Claude → download, false: download → Claude
    private bool _tripPodToggled;       // the Claude bubble has been hidden (forward) / shown (back)

    private static readonly Color TripOrange = Color.FromRgb(0xFF, 0x9A, 0x6E);
    private static readonly Color TripGreen = Color.FromRgb(0x7C, 0xF0, 0xA0);

    private bool EyeTripActive => _tripStart >= 0;

    /// <summary>The download bubble shows eyes right now (no Claude around).</summary>
    private bool DownloadPodHasEyes => _dlPodShown && !_dlPodMorphing && _dlPodEyesU > 0.5;

    private static double TripNow => AnimNow;
    private static double TripSeg(double t, double a, double b) => Math.Clamp((t - a) / (b - a), 0, 1);
    private static double TripSmooth(double u) => u * u * (3 - 2 * u);
    private static double TripEaseIn(double u) => u * u * u;
    private static double TripEaseOutBack(double u)
    {
        const double c = 1.5;
        return 1 + (c + 1) * Math.Pow(u - 1, 3) + c * Math.Pow(u - 1, 2);
    }

    /// <summary>0 → 1 over the trip.</summary>
    private double TripProgress => EyeTripActive ? (TripNow - _tripStart) / (Dur(EyeTripMs) / 1000) : 1;

    private void StartEyeTrip(bool forward)
    {
        _tripStart = TripNow;
        _tripForward = forward;
        _tripPodToggled = false;
        // the receiving bubble's eyes wait up there, out of sight
        if (forward) DlPodTrip.Y = -TripRise;
        else PodTrip.Y = -TripRise;
    }

    private void EndEyeTrip()
    {
        if (!EyeTripActive) return;
        _tripStart = -1;
        PodTrip.Y = 0;
        DlPodTrip.Y = 0;
        EyeTripGlow.Opacity = 0;
        _dlPodEyesU = _tripForward ? 1 : 0;
    }

    /// <summary>
    /// The download bubble's eyes / % while the eyes travel (null when there's no trip).
    /// Forward: the % fades and the eyes arrive from above. Back: the eyes leave upward and the % comes back.
    /// </summary>
    private (double eyes, double info)? TripDownloadLook()
    {
        if (!EyeTripActive) return null;
        double s = TripProgress;
        return _tripForward
            ? (s > 0.6 ? 1 : 0, 1 - TripSmooth(TripSeg(s, 0.5, 0.65)))
            : (s < 0.36 ? 1 : 0, TripSmooth(TripSeg(s, 0.25, 0.5)));
    }

    /// <summary>Every frame (after the bubbles are updated).</summary>
    private void UpdateEyeTrip()
    {
        if (!EyeTripActive) return;
        // a bubble went away meanwhile (download done, the island took it...): no trip anymore
        if (!_dlPodShown || _dlPodMorphing || _podMorphing)
        {
            EndEyeTrip();
            return;
        }
        double s = TripProgress;
        if (s >= 1)
        {
            EndEyeTrip();
            return;
        }

        double rise = -TripRise * TripEaseIn(TripSeg(s, 0, 0.32));                 // leaving through the top edge
        double fall = -TripRise * (1 - TripEaseOutBack(TripSeg(s, 0.62, 1)));     // coming down from it
        if (_tripForward)
        {
            PodTrip.Y = rise;
            DlPodTrip.Y = s < 0.62 ? -TripRise : fall;
            if (!_tripPodToggled && s >= 0.3)
            {
                _tripPodToggled = true;
                HidePod(); // Claude's bubble leaves once its eyes are gone
            }
        }
        else
        {
            DlPodTrip.Y = s < 0.4 ? rise : -TripRise;
            PodTrip.Y = s < 0.62 ? -TripRise : fall;
            if (!_tripPodToggled && s >= 0.3)
            {
                _tripPodToggled = true;
                ShowPod(); // Claude's bubble pops in, still without eyes: they're on their way
                PodTrip.Y = -TripRise;
            }
        }

        // the faint glow along the screen's top edge, from one bubble to the other
        double scale = S.SizeScale;
        double side = Island.ActualWidth * scale / 2 + 8 * scale + 70 * scale / 2;
        double run = TripSmooth(TripSeg(s, 0.28, 0.68));
        double from = _tripForward ? -side : side, to = -from;
        EyeTripGlowX.X = from + (to - from) * run;
        EyeTripGlow.Margin = new Thickness(0, Island.Margin.Top + _topHidden - 1.3, 0, 0);
        double k = _tripForward ? run : 1 - run;
        var color = Color.FromRgb(
            (byte)(TripOrange.R + (TripGreen.R - TripOrange.R) * k),
            (byte)(TripOrange.G + (TripGreen.G - TripOrange.G) * k),
            (byte)(TripOrange.B + (TripGreen.B - TripOrange.B) * k));
        SetBrushColor(EyeTripGlow, System.Windows.Controls.Border.BackgroundProperty, color);
        EyeTripGlowShadow.Color = color;
        EyeTripGlow.Opacity = Math.Sin(Math.PI * Math.Clamp((s - 0.26) / 0.44, 0, 1)) * 0.95;
    }
}
