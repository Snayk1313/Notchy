using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Notchy;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// Synced lyrics from LRCLIB (https://lrclib.net) — free, no API key. Results are cached on disk.
/// </summary>
public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly string _cacheDir = Path.Combine(AppSettings.Folder, "lyrics");
    private readonly Dictionary<string, List<LyricLine>?> _memory = new();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://lrclib.net/"), Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Notchy/1.0");
        return client;
    }

    public async Task<List<LyricLine>?> GetAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
    {
        string key = (artist + "|" + title).Trim().ToLowerInvariant();
        if (_memory.TryGetValue(key, out var cached)) return cached;

        string file = Path.Combine(_cacheDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))) + ".lrc");
        if (File.Exists(file))
        {
            var parsed = Parse(await File.ReadAllTextAsync(file, ct));
            if (parsed.Count > 0)
            {
                _memory[key] = parsed;
                return parsed;
            }
        }

        // Network errors propagate (so they are not cached as "no lyrics").
        string? lrc = await FetchAsync(title, artist, album, duration, ct);
        List<LyricLine>? result = lrc == null ? null : Parse(lrc);
        if (result is { Count: 0 }) result = null;

        if (result != null && lrc != null)
        {
            try
            {
                Directory.CreateDirectory(_cacheDir);
                await File.WriteAllTextAsync(file, lrc, ct);
            }
            catch
            {
                // cache is best effort
            }
        }
        _memory[key] = result;
        return result;
    }

    private async Task<string?> FetchAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
    {
        foreach (var (t, a) in Variants(title, artist))
        {
            var q = $"api/get?track_name={E(t)}&artist_name={E(a)}";
            if (!string.IsNullOrWhiteSpace(album)) q += $"&album_name={E(album)}";
            if (duration > TimeSpan.Zero) q += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

            var exact = await GetSingleAsync(q, ct);
            if (exact != null) return exact;

            var found = await SearchAsync($"api/search?track_name={E(t)}&artist_name={E(a)}", duration, ct);
            if (found != null) return found;
        }
        return await SearchAsync($"api/search?q={E(CleanTitle(title) + " " + MainArtist(artist))}", duration, ct);
    }

    private static async Task<string?> GetSingleAsync(string query, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(query, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return Extract(doc.RootElement);
    }

    private static async Task<string?> SearchAsync(string query, TimeSpan duration, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(query, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        double bestDiff = double.MaxValue;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var lrc = Extract(item);
            if (lrc == null) continue;
            double diff = 0;
            if (duration > TimeSpan.Zero && item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                diff = Math.Abs(d.GetDouble() - duration.TotalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = lrc;
            }
        }
        // Different durations usually mean a different version (live, remix...) => bad sync.
        return bestDiff <= 10 ? best : null;
    }

    private static string? Extract(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.True)
            return "[00:00.00]♪ Instrumental ♪";
        if (e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var text = s.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }
        return null;
    }

    private static IEnumerable<(string title, string artist)> Variants(string title, string artist)
    {
        var list = new List<(string, string)> { (title, artist), (CleanTitle(title), MainArtist(artist)) };

        // YouTube-style "Artist - Song (Official Video)"
        int dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            list.Add((CleanTitle(title[(dash + 3)..]), MainArtist(title[..dash])));

        return list.Where(v => !string.IsNullOrWhiteSpace(v.Item1) && !string.IsNullOrWhiteSpace(v.Item2)).Distinct();
    }

    private static string CleanTitle(string t)
    {
        t = Regex.Replace(t,
            @"\s*[\(\[][^\)\]]*\b(feat|ft|with|remaster(ed)?|version|live|edit|mono|stereo|deluxe|official|video|audio|lyrics?|visualizer|hd|4k|mv)\b[^\)\]]*[\)\]]",
            "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+-\s+[^-]*\b(remaster(ed)?|version|live|edit|mono|stereo|mix|demo)\b.*$", "", RegexOptions.IgnoreCase);
        return t.Trim();
    }

    private static string MainArtist(string a)
    {
        a = Regex.Replace(a, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        a = Regex.Replace(a, @"VEVO$", "", RegexOptions.IgnoreCase);
        return Regex.Split(a, @",|&|\bfeat\.?|\bft\.?", RegexOptions.IgnoreCase)[0].Trim();
    }

    private static string E(string s) => Uri.EscapeDataString(s);

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<\d+:\d+(?:[.:]\d+)?>", RegexOptions.Compiled);
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<LyricLine> Parse(string lrc)
    {
        var list = new List<LyricLine>();
        double offsetMs = 0;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var off = OffsetTag.Match(line);
            if (off.Success)
            {
                offsetMs = double.Parse(off.Groups[1].Value);
                continue;
            }

            var times = new List<TimeSpan>();
            int pos = 0;
            while (true)
            {
                var m = TimeTag.Match(line, pos);
                if (!m.Success || m.Index != pos) break;
                int min = int.Parse(m.Groups[1].Value), sec = int.Parse(m.Groups[2].Value);
                double ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    ms = int.Parse(frac) * (frac.Length == 1 ? 100 : frac.Length == 2 ? 10 : 1);
                }
                times.Add(TimeSpan.FromMilliseconds(min * 60000 + sec * 1000 + ms));
                pos = m.Index + m.Length;
            }
            if (times.Count == 0) continue;

            string text = WordTag.Replace(line[pos..], "").Trim();
            foreach (var t in times)
                list.Add(new LyricLine(t - TimeSpan.FromMilliseconds(offsetMs), text));
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }
}
