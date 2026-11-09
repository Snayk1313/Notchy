using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace Notchy;

public enum Sfx { Expand, Collapse, Click, Toggle, Done, Attention, FocusEnd, Like, Hello, Wave, Alarm }

/// <summary>
/// Soft, short UI sounds synthesized at startup (no third-party audio files).
/// Each sound is rendered once to a small WAV in %LOCALAPPDATA%\Notchy\sfx and played with WPF's MediaPlayer
/// (so sounds can overlap and have their own volume).
/// </summary>
public static class SoundService
{
    private const int Rate = 44100;
    private static readonly Dictionary<Sfx, MediaPlayer> Players = new();
    private static readonly Dictionary<Sfx, DateTime> LastPlayed = new();
    private static bool _ready;

    private static void EnsureReady()
    {
        if (_ready) return;
        _ready = true;
        string dir = Path.Combine(AppSettings.Folder, "sfx");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (Sfx s in Enum.GetValues(typeof(Sfx)))
            {
                string file = Path.Combine(dir, s == Sfx.Alarm ? $"{s}_v3.wav" : $"{s}_v2.wav"); // (v3: the louder alarm)
                if (!File.Exists(file)) File.WriteAllBytes(file, ToWav(Render(s)));
                var p = new MediaPlayer();
                p.Open(new Uri(file));
                p.Volume = 0; // warm-up
                Players[s] = p;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Call once at startup so the first sound has no delay.</summary>
    public static void Warmup() => EnsureReady();

    /// <summary>True while you focus with "Não perturbe" on: only the end-of-focus sound plays.</summary>
    public static bool Hushed { get; set; }

    public static void Play(Sfx sfx)
    {
        var st = AppSettings.Current;
        // a timer or alarm you set yourself always rings (it isn't a UI sound)
        if (sfx == Sfx.Alarm)
        {
            PlayNow(sfx);
            return;
        }
        if (!st.SoundsEnabled) return;
        if (Hushed && sfx != Sfx.FocusEnd) return;
        bool allowed = sfx switch
        {
            Sfx.Expand or Sfx.Collapse or Sfx.Hello or Sfx.Wave => st.SoundOpenClose,
            Sfx.Click or Sfx.Toggle or Sfx.Like => st.SoundClicks,
            Sfx.FocusEnd => st.SoundFocus,
            Sfx.Done => st.ClaudeSoundDone,
            Sfx.Attention => st.ClaudeSoundAttention,
            _ => true
        };
        if (allowed) PlayNow(sfx);
    }

    /// <summary>Plays regardless of per-event toggles (used by the "listen" buttons in settings).</summary>
    public static void Preview(Sfx sfx) => PlayNow(sfx);

    private static void PlayNow(Sfx sfx)
    {
        EnsureReady();
        if (!Players.TryGetValue(sfx, out var p)) return;

        // Don't machine-gun the same sound.
        var now = DateTime.UtcNow;
        if (LastPlayed.TryGetValue(sfx, out var last) && (now - last).TotalMilliseconds < 60) return;
        LastPlayed[sfx] = now;

        double vol = (sfx == Sfx.Alarm ? AppSettings.Current.AlarmVolume : AppSettings.Current.SoundVolume) / 100.0;
        p.Volume = Math.Clamp(vol * vol * 1.0, 0, 1); // perceptual curve
        p.Position = TimeSpan.Zero;
        p.Play();
    }

    // ------------------------------------------------------------------ synthesis

    private static float[] Render(Sfx s)
    {
        switch (s)
        {
            case Sfx.Expand: // soft rising two-note "bloop"
            {
                var b = Buffer(0.32);
                Tone(b, 0.000, 659.25, 0.20, 0.30, 0.10);
                Tone(b, 0.055, 987.77, 0.24, 0.26, 0.12);
                return b;
            }
            case Sfx.Collapse: // softer falling pair
            {
                var b = Buffer(0.30);
                Tone(b, 0.000, 880.00, 0.18, 0.22, 0.09);
                Tone(b, 0.050, 587.33, 0.22, 0.24, 0.11);
                return b;
            }
            case Sfx.Click: // tiny tick
            {
                var b = Buffer(0.05);
                Tone(b, 0, 2093.0, 0.035, 0.16, 0.012, harmonics: false);
                return b;
            }
            case Sfx.Toggle:
            {
                var b = Buffer(0.09);
                Tone(b, 0, 1318.5, 0.07, 0.16, 0.025, harmonics: false);
                return b;
            }
            case Sfx.Done: // gentle "ding-ding"
            {
                var b = Buffer(1.1);
                Tone(b, 0.00, 1046.5, 0.55, 0.26, 0.22, bell: true);
                Tone(b, 0.11, 1567.98, 0.90, 0.28, 0.32, bell: true);
                return b;
            }
            case Sfx.Attention: // two quick "pling"s
            {
                var b = Buffer(0.55);
                Tone(b, 0.00, 1396.9, 0.25, 0.24, 0.08, bell: true);
                Tone(b, 0.12, 1396.9, 0.35, 0.22, 0.10, bell: true);
                return b;
            }
            case Sfx.Like: // soft rising "pling-pling" for a liked song
            {
                var b = Buffer(0.6);
                Tone(b, 0.00, 1318.5, 0.30, 0.20, 0.10, bell: true);
                Tone(b, 0.07, 1975.5, 0.45, 0.20, 0.16, bell: true);
                return b;
            }
            case Sfx.Hello: // opening: a soft "bloop" as the drop becomes the island, then a quiet rising shimmer
            {
                var b = Buffer(1.0);
                Tone(b, 0.00, 392.00, 0.22, 0.22, 0.09);
                Tone(b, 0.05, 587.33, 0.26, 0.20, 0.11);
                Tone(b, 0.16, 1174.66, 0.55, 0.10, 0.22, bell: true);
                Tone(b, 0.24, 1567.98, 0.70, 0.09, 0.28, bell: true);
                return b;
            }
            case Sfx.Wave: // the opening "hi": a soft air swish on each swing of the little hand (0.18 s apart)
            {
                var b = Buffer(1.0);
                for (int k = 0; k < 5; k++)
                {
                    Swish(b, k * 0.18, 0.15, k % 2 == 0 ? 0.055 : 0.045);
                    Tone(b, k * 0.18 + 0.05, k % 2 == 0 ? 1760 : 1568, 0.08, 0.018, 0.03, harmonics: false);
                }
                return b;
            }
            case Sfx.Alarm: // a bright "ding-ding · ding-ding", repeated while the timer/alarm rings
            {
                var b = Buffer(1.3);
                for (int k = 0; k < 2; k++)
                {
                    double at = k * 0.42;
                    Tone(b, at, 1567.98, 0.30, 0.36, 0.10, bell: true);
                    Tone(b, at + 0.13, 2093.0, 0.45, 0.33, 0.14, bell: true);
                }
                return b;
            }
            case Sfx.FocusEnd: // three-note chime
            {
                var b = Buffer(1.6);
                Tone(b, 0.00, 1046.5, 0.9, 0.22, 0.30, bell: true);
                Tone(b, 0.18, 1318.5, 0.9, 0.22, 0.30, bell: true);
                Tone(b, 0.36, 1567.98, 1.2, 0.24, 0.40, bell: true);
                return b;
            }
        }
        return Buffer(0.01);
    }

    private static float[] Buffer(double seconds) => new float[(int)(seconds * Rate)];

    /// <summary>Adds a short band-passed noise "whoosh" that swells and fades (air moving).</summary>
    private static void Swish(float[] buf, double start, double length, double amp)
    {
        var rnd = new Random(7 + (int)(start * 1000));
        int s0 = (int)(start * Rate), n = (int)(length * Rate);
        double lo = 0, hi = 0;
        // one-pole filters: keep roughly 700 Hz - 4 kHz, the "fff" of a quick movement
        double aHi = Math.Exp(-2 * Math.PI * 4000 / Rate), aLo = Math.Exp(-2 * Math.PI * 700 / Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double x = rnd.NextDouble() * 2 - 1;
            hi = (1 - aHi) * x + aHi * hi;
            lo = (1 - aLo) * hi + aLo * lo;
            double band = hi - lo;
            double p = (double)i / n;
            double env = Math.Pow(Math.Sin(Math.PI * Math.Pow(p, 0.7)), 2);
            buf[s0 + i] += (float)(band * env * amp * 3);
        }
    }

    /// <summary>Adds a sine tone with soft attack and exponential decay.</summary>
    private static void Tone(float[] buf, double start, double freq, double length, double amp, double decay,
        bool harmonics = true, bool bell = false)
    {
        int s0 = (int)(start * Rate);
        int n = (int)(length * Rate);
        double attack = 0.006 * Rate;
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Min(1, i / attack) * Math.Exp(-t / decay);
            // fade the tail to zero to avoid clicks
            double tail = Math.Min(1, (n - i) / (0.01 * Rate));
            double w = 2 * Math.PI * freq * t;
            double v = Math.Sin(w);
            if (harmonics) v += 0.18 * Math.Sin(2 * w) * Math.Exp(-t / (decay * 0.6));
            if (bell) v += 0.22 * Math.Sin(2.76 * w) * Math.Exp(-t / (decay * 0.35)) + 0.08 * Math.Sin(5.4 * w) * Math.Exp(-t / (decay * 0.2));
            buf[s0 + i] += (float)(v * env * tail * amp);
        }
    }

    private static byte[] ToWav(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Length * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataLen);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);       // PCM
        w.Write((short)1);       // mono
        w.Write(Rate);
        w.Write(Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataLen);
        foreach (var f in samples)
            w.Write((short)(Math.Clamp(f, -1f, 1f) * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
