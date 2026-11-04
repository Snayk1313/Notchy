using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Notchy;

/// <summary>
/// Wraps Windows' System Media Transport Controls (the same thing the volume flyout uses),
/// so it works with Spotify, browsers (YouTube, YT Music, Deezer web...), Media Player, etc.
/// All events are raised on the UI thread.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _mediaVersion;
    private TimeSpan _basePos;
    private DateTime _baseStamp = DateTime.UtcNow;

    public event Action? MediaChanged;
    public event Action? PlaybackChanged;

    public bool HasSession => _session != null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public string SourceAppId { get; private set; } = "";
    public string SourceName => FriendlyName(SourceAppId);
    public BitmapSource? Art { get; private set; }
    public Color? ArtColor { get; private set; }
    public bool IsPlaying { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var p = _basePos;
            if (IsPlaying) p += DateTime.UtcNow - _baseStamp;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public MediaService(Dispatcher ui) => _ui = ui;

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(PickSession);
        _manager.SessionsChanged += (_, _) => _ui.InvokeAsync(PickSession);
        PickSession();
    }

    private static bool IsSessionPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Chooses which session to follow: something playing wins, otherwise the current one.</summary>
    public void PickSession()
    {
        if (_manager == null) return;
        GlobalSystemMediaTransportControlsSession? best;
        try
        {
            var sessions = _manager.GetSessions().ToList();
            var current = _manager.GetCurrentSession();

            if (current != null && IsSessionPlaying(current)) best = current;
            else best = sessions.FirstOrDefault(IsSessionPlaying);

            if (best == null && _session != null)
                best = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _session.SourceAppUserModelId);

            best ??= current ?? sessions.FirstOrDefault();
        }
        catch
        {
            best = null;
        }

        if (best == null && _session == null) return;
        if (best != null && _session != null && best.SourceAppUserModelId == _session.SourceAppUserModelId)
        {
            // Same app. Only switch objects if the new one is playing and ours isn't (e.g. two browser tabs).
            if (ReferenceEquals(best, _session) || !IsSessionPlaying(best) || IsSessionPlaying(_session))
                return;
        }
        Attach(best);
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnMediaProps;
                _session.PlaybackInfoChanged -= OnPlaybackInfo;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            catch
            {
                // session already gone
            }
        }

        _session = s;
        _mediaVersion++;

        if (s == null)
        {
            Title = Artist = Album = SourceAppId = "";
            Art = null;
            ArtColor = null;
            IsPlaying = false;
            Duration = TimeSpan.Zero;
            _basePos = TimeSpan.Zero;
            MediaChanged?.Invoke();
            PlaybackChanged?.Invoke();
            return;
        }

        SourceAppId = s.SourceAppUserModelId ?? "";
        s.MediaPropertiesChanged += OnMediaProps;
        s.PlaybackInfoChanged += OnPlaybackInfo;
        s.TimelinePropertiesChanged += OnTimeline;

        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshMediaAsync();
    }

    private void OnMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() => _ = RefreshMediaAsync());

    private void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.InvokeAsync(() => { RefreshPlayback(); PickSession(); });

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(RefreshTimeline);

    private async Task RefreshMediaAsync()
    {
        var s = _session;
        if (s == null) return;
        int version = ++_mediaVersion;

        GlobalSystemMediaTransportControlsSessionMediaProperties? p;
        try
        {
            p = await s.TryGetMediaPropertiesAsync();
        }
        catch
        {
            return;
        }
        if (version != _mediaVersion || p == null) return;

        string title = p.Title ?? "";
        string artist = string.IsNullOrWhiteSpace(p.Artist) ? p.AlbumArtist ?? "" : p.Artist;
        bool trackChanged = title != Title || artist != Artist;
        Title = title;
        Artist = artist;
        Album = p.AlbumTitle ?? "";

        BitmapSource? art = null;
        if (p.Thumbnail != null)
        {
            try
            {
                art = await LoadThumbnailAsync(p.Thumbnail);
            }
            catch
            {
                art = null;
            }
        }
        if (version != _mediaVersion) return;

        if (art != null || trackChanged)
        {
            Art = art;
            ArtColor = art != null ? ColorUtil.Dominant(art) : null;
        }
        MediaChanged?.Invoke();
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        using var ras = await reference.OpenReadAsync();
        using var stream = ras.AsStreamForRead();
        var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        if (ms.Length == 0) return null;
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void RefreshPlayback()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            bool playing = IsSessionPlaying(s);
            if (playing != IsPlaying)
            {
                _basePos = Position; // freeze/resume from the extrapolated position
                _baseStamp = DateTime.UtcNow;
                IsPlaying = playing;
            }
        }
        catch
        {
            // ignore
        }
        PlaybackChanged?.Invoke();
    }

    private void RefreshTimeline()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var t = s.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            if (duration <= TimeSpan.Zero) duration = t.MaxSeekTime - t.MinSeekTime;
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;

            var now = DateTime.UtcNow;
            var stamp = t.LastUpdatedTime.UtcDateTime;
            if (t.LastUpdatedTime == default || stamp > now || stamp < now.AddHours(-6)) stamp = now;

            _basePos = t.Position - t.StartTime;
            _baseStamp = IsPlaying ? stamp : now;
        }
        catch
        {
            // ignore
        }
        PlaybackChanged?.Invoke();
    }

    public async Task TogglePlayPauseAsync()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var s = _session;
        if (s == null) return;
        _basePos = position;
        _baseStamp = DateTime.UtcNow;
        try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    /// <summary>Brings the source app (Spotify, browser...) to the front.</summary>
    public void ActivateSourceApp()
    {
        string id = SourceAppId;
        if (string.IsNullOrEmpty(id)) return;

        string name = id.Contains('!') ? id[(id.IndexOf('!') + 1)..] : id;
        name = Path.GetFileNameWithoutExtension(name);

        try
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    Native.FocusWindow(p.MainWindowHandle);
                    return;
                }
            }

            if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            else if (id.Contains('!'))
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Process name of the playing app ("Spotify", "chrome"...), for its own volume.</summary>
    public string SourceProcess
    {
        get
        {
            string id = SourceAppId;
            if (string.IsNullOrEmpty(id)) return "";
            if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
            string name = id.Contains('!') ? id[(id.IndexOf('!') + 1)..] : id;
            return Path.GetFileNameWithoutExtension(name);
        }
    }

    public static string FriendlyName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("opera")) return "Opera";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("zunemusic") || l.Contains("mediaplayer")) return "Media Player";
        if (l.Contains("applemusic")) return "Apple Music";
        if (l.Contains("deezer")) return "Deezer";
        if (l.Contains("tidal")) return "TIDAL";
        if (l.Contains("amazon")) return "Amazon Music";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("foobar")) return "foobar2000";
        if (l.Contains("musicbee")) return "MusicBee";

        string name = id.Contains('!') ? id[(id.IndexOf('!') + 1)..] : id;
        name = Path.GetFileNameWithoutExtension(name);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : id;
    }
}

public static class ColorUtil
{
    public static readonly Color Fallback = Color.FromRgb(0xFF, 0xA8, 0x4C);

    /// <summary>Saturation-weighted average color of an image.</summary>
    public static Color Dominant(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double val = max / 255.0;
                double weight = sat * sat * val + 0.01;
                r += R * weight;
                g += G * weight;
                b += B * weight;
                total += weight;
            }
            return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }
        catch
        {
            return Fallback;
        }
    }

    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min, h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
