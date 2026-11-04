using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Notchy;

public sealed record SpotifyDevice(string Id, string Name, string Type, bool Active, bool SupportsVolume);
public sealed record SpotifyTrack(string Id, string Uri, string Name, string Artists, string? ImageUrl);

public sealed class SpotifyState
{
    public bool HasPlayer { get; init; }
    public string? TrackId { get; init; }
    public string TrackName { get; init; } = "";
    public bool Shuffle { get; init; }
    public string Repeat { get; init; } = "off"; // off / context / track
    public int? Volume { get; init; }
    public bool SupportsVolume { get; init; }
    public SpotifyDevice? Device { get; init; }
    public string? ContextUri { get; init; }
}

/// <summary>
/// Spotify Web API with the user's own app (Client ID) and the PKCE login for desktop apps:
/// no client secret, the refresh token is stored encrypted with Windows DPAPI.
/// Volume / shuffle / repeat / device changes need Spotify Premium.
/// </summary>
public sealed class SpotifyService
{
    public const int CallbackPort = 47932;
    public const string RedirectUri = "http://127.0.0.1:47932/callback";
    public const string DashboardUrl = "https://developer.spotify.com/dashboard";

    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing user-library-read user-library-modify";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static AppSettings S => AppSettings.Current;

    private string? _accessToken;
    private DateTime _accessExpires = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public bool IsConnected => !string.IsNullOrEmpty(S.SpotifyClientId) && !string.IsNullOrEmpty(S.SpotifyToken);

    public event Action? ConnectionChanged;

    // =====================================================================================
    // login (PKCE)
    // =====================================================================================

    /// <summary>Opens the browser on Spotify's consent page and waits for the redirect on 127.0.0.1.</summary>
    public async Task<(bool ok, string message)> ConnectAsync(CancellationToken ct = default)
    {
        string clientId = (S.SpotifyClientId ?? "").Trim();
        if (clientId.Length < 16) return (false, Loc.T("sp.err.clientid"));

        string verifier = RandomString(64);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = RandomString(16);

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, CallbackPort);
            listener.Start();
        }
        catch (Exception ex)
        {
            return (false, Loc.T("sp.err.port") + " " + ex.Message);
        }

        try
        {
            string url = "https://accounts.spotify.com/authorize" +
                         $"?client_id={Uri.EscapeDataString(clientId)}&response_type=code" +
                         $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                         $"&code_challenge_method=S256&code_challenge={challenge}" +
                         $"&scope={Uri.EscapeDataString(Scopes)}&state={state}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, timeout.Token);
                string request = Encoding.ASCII.GetString(buf, 0, n);
                string firstLine = request.Split('\n')[0];
                if (!firstLine.Contains("/callback"))
                {
                    await Respond(stream, 404, "");
                    continue;
                }
                var query = ParseQuery(firstLine.Split(' ')[1]);
                if (query.TryGetValue("error", out var err))
                {
                    await Respond(stream, 200, Page(Loc.T("sp.page.denied")));
                    return (false, Loc.T("sp.err.denied") + " (" + err + ")");
                }
                if (!query.TryGetValue("code", out var code) || !query.TryGetValue("state", out var st) || st != state)
                {
                    await Respond(stream, 400, Page(Loc.T("sp.page.error")));
                    return (false, Loc.T("sp.err.callback"));
                }

                var tokens = await PostToken(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier
                });
                if (tokens == null)
                {
                    await Respond(stream, 200, Page(Loc.T("sp.page.error")));
                    return (false, Loc.T("sp.err.token"));
                }
                await Respond(stream, 200, Page(Loc.T("sp.page.ok")));
                ConnectionChanged?.Invoke();
                return (true, Loc.T("sp.connected"));
            }
        }
        catch (OperationCanceledException)
        {
            return (false, Loc.T("sp.err.timeout"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Disconnect()
    {
        S.SpotifyToken = "";
        AppSettings.Commit();
        _accessToken = null;
        ConnectionChanged?.Invoke();
    }

    private static string Page(string text) =>
        "<!doctype html><meta charset='utf-8'><title>Notchy</title>" +
        "<body style='background:#121212;color:#fff;font:18px Segoe UI,sans-serif;display:grid;place-items:center;height:90vh'>" +
        $"<div style='text-align:center'><div style='font-size:44px'>• •</div><p>{WebUtility.HtmlEncode(text)}</p></div></body>";

    private static async Task Respond(NetworkStream stream, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(bytes);
    }

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>();
        int q = pathAndQuery.IndexOf('?');
        if (q < 0) return result;
        foreach (var part in pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return result;
    }

    private static string RandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(length);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // =====================================================================================
    // tokens
    // =====================================================================================

    private async Task<string?> PostToken(Dictionary<string, string> form)
    {
        try
        {
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                App.Log(new Exception("Spotify token: " + json));
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _accessToken = root.GetProperty("access_token").GetString();
            _accessExpires = DateTime.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
            if (root.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } refresh)
            {
                S.SpotifyToken = Dpapi.Protect(refresh);
                AppSettings.Commit();
            }
            return _accessToken;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    private async Task<string?> AccessTokenAsync()
    {
        if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
        await _tokenLock.WaitAsync();
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _accessExpires) return _accessToken;
            string? refresh = Dpapi.Unprotect(S.SpotifyToken);
            if (string.IsNullOrEmpty(refresh)) return null;
            return await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh!,
                ["client_id"] = S.SpotifyClientId.Trim()
            });
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // =====================================================================================
    // API
    // =====================================================================================

    private async Task<(int status, string body)> Call(HttpMethod method, string path, string? jsonBody = null)
    {
        if (!IsConnected) return (0, "");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string? token = await AccessTokenAsync();
            if (token == null) return (401, "");
            using var req = new HttpRequestMessage(method, "https://api.spotify.com/v1" + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            else if (method == HttpMethod.Put || method == HttpMethod.Post) req.Content = new StringContent("");
            try
            {
                using var resp = await Http.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    _accessToken = null; // expired: refresh and retry once
                    continue;
                }
                return ((int)resp.StatusCode, body);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return (0, "");
            }
        }
        return (401, "");
    }

    public async Task<string?> UserNameAsync()
    {
        var (status, body) = await Call(HttpMethod.Get, "/me");
        if (status != 200) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("display_name", out var n) ? n.GetString() : null;
    }

    public async Task<SpotifyState?> StateAsync()
    {
        var (status, body) = await Call(HttpMethod.Get, "/me/player");
        if (status == 204) return new SpotifyState { HasPlayer = false };
        if (status != 200 || string.IsNullOrWhiteSpace(body)) return null;
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        SpotifyDevice? device = null;
        int? volume = null;
        if (r.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            device = ParseDevice(d);
            if (device.SupportsVolume && d.TryGetProperty("volume_percent", out var v) && v.ValueKind == JsonValueKind.Number)
                volume = v.GetInt32();
        }
        string? trackId = null, trackName = "";
        string? contextUri = r.TryGetProperty("context", out var cx) && cx.ValueKind == JsonValueKind.Object &&
                             cx.TryGetProperty("uri", out var cu) ? cu.GetString() : null;
        if (r.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
        {
            trackId = item.TryGetProperty("id", out var id) ? id.GetString() : null;
            trackName = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
        }
        return new SpotifyState
        {
            HasPlayer = true,
            TrackId = trackId,
            TrackName = trackName,
            Shuffle = r.TryGetProperty("shuffle_state", out var sh) && sh.ValueKind == JsonValueKind.True,
            Repeat = r.TryGetProperty("repeat_state", out var rp) ? rp.GetString() ?? "off" : "off",
            Volume = volume,
            ContextUri = contextUri,
            SupportsVolume = device?.SupportsVolume ?? false,
            Device = device
        };
    }

    private static SpotifyDevice ParseDevice(JsonElement d) => new(
        d.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
        d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        d.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
        d.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True,
        d.TryGetProperty("supports_volume", out var sv) && sv.ValueKind == JsonValueKind.True);

    public async Task<List<SpotifyDevice>> DevicesAsync()
    {
        var (status, body) = await Call(HttpMethod.Get, "/me/player/devices");
        var list = new List<SpotifyDevice>();
        if (status != 200) return list;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("devices", out var arr))
            foreach (var d in arr.EnumerateArray()) list.Add(ParseDevice(d));
        return list;
    }

    public async Task<bool> SetVolumeAsync(int percent) =>
        Ok(await Call(HttpMethod.Put, $"/me/player/volume?volume_percent={Math.Clamp(percent, 0, 100)}"));

    public async Task<bool> SetShuffleAsync(bool on) =>
        Ok(await Call(HttpMethod.Put, $"/me/player/shuffle?state={(on ? "true" : "false")}"));

    public async Task<bool> SetRepeatAsync(string mode) =>
        Ok(await Call(HttpMethod.Put, $"/me/player/repeat?state={mode}"));

    public async Task<bool> TransferAsync(string deviceId) =>
        Ok(await Call(HttpMethod.Put, "/me/player", $"{{\"device_ids\":[\"{deviceId}\"],\"play\":true}}"));

    public async Task<bool> NextAsync() => Ok(await Call(HttpMethod.Post, "/me/player/next"));

    // Spotify moved "Liked Songs" to the /me/library endpoints; the old /me/tracks ones answer 403 for new apps.
    public async Task<bool?> IsLikedAsync(string trackId)
    {
        var (status, body) = await Call(HttpMethod.Get, $"/me/library/contains?uris=spotify:track:{trackId}");
        if (status != 200) (status, body) = await Call(HttpMethod.Get, $"/me/tracks/contains?ids={trackId}");
        if (status != 200) return null;
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 && doc.RootElement[0].GetBoolean();
    }

    public async Task<bool> SetLikedAsync(string trackId, bool liked)
    {
        var method = liked ? HttpMethod.Put : HttpMethod.Delete;
        var r = await Call(method, $"/me/library?uris=spotify:track:{trackId}");
        if (Ok(r)) return true;
        App.Log(new Exception($"Spotify library {method}: {r.status} {r.body}"));
        return Ok(await Call(method, $"/me/tracks?ids={trackId}"));
    }

    public async Task<List<SpotifyTrack>> QueueAsync()
    {
        var (status, body) = await Call(HttpMethod.Get, "/me/player/queue");
        var list = new List<SpotifyTrack>();
        if (status != 200) return list;
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("queue", out var arr)) return list;
        foreach (var t in arr.EnumerateArray().Take(20))
        {
            string artists = t.TryGetProperty("artists", out var ar) && ar.ValueKind == JsonValueKind.Array
                ? string.Join(", ", ar.EnumerateArray().Select(a => a.GetProperty("name").GetString()))
                : t.TryGetProperty("show", out var show) && show.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
            string? image = null;
            JsonElement images = default;
            if (t.TryGetProperty("album", out var al) && al.TryGetProperty("images", out var ai)) images = ai;
            else if (t.TryGetProperty("images", out var ti)) images = ti;
            if (images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
                image = images[images.GetArrayLength() - 1].GetProperty("url").GetString();
            list.Add(new SpotifyTrack(
                t.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                t.TryGetProperty("uri", out var uri) ? uri.GetString() ?? "" : "",
                t.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                artists, image));
        }
        return list;
    }

    /// <summary>
    /// Jumps to an upcoming track. If the music plays from a playlist/album, it plays that context starting at
    /// the track (instant, and the rest of the playlist follows). Otherwise it skips one by one.
    /// </summary>
    /// <remarks>
    /// With shuffle on, playing the context again would re-shuffle everything after the track, so it skips
    /// one by one instead: the shuffled order you had stays exactly the same (like clicking it in Spotify's queue).
    /// </remarks>
    public async Task SkipToAsync(int index, SpotifyTrack track, string? contextUri, bool shuffle)
    {
        if (!shuffle && !string.IsNullOrEmpty(contextUri) && track.Uri.StartsWith("spotify:track:") &&
            (contextUri!.Contains(":playlist:") || contextUri.Contains(":album:")))
        {
            string body = $"{{\"context_uri\":\"{contextUri}\",\"offset\":{{\"uri\":\"{track.Uri}\"}},\"position_ms\":0}}";
            var r = await Call(HttpMethod.Put, "/me/player/play", body);
            if (Ok(r)) return;
            App.Log(new Exception($"Spotify play in context: {r.status} {r.body}"));
        }
        await SkipByStepsAsync(index);
    }

    private async Task SkipByStepsAsync(int index)
    {
        for (int i = 0; i <= index; i++)
        {
            await NextAsync();
            if (i < index) await Task.Delay(260);
        }
    }

    private static bool Ok((int status, string body) r) => r.status is >= 200 and < 300;

    /// <summary>Debug: like the current song, check, then unlike it again (leaves the library as it was).</summary>
    public async Task<string> LikeRoundTripAsync()
    {
        var st = await StateAsync();
        if (st?.TrackId is not string id) return "no track";
        bool? before = await IsLikedAsync(id);
        if (before == true) return $"track {id} already liked: not touching it";
        bool saved = await SetLikedAsync(id, true);
        bool? after = await IsLikedAsync(id);
        bool removed = await SetLikedAsync(id, false);
        bool? final = await IsLikedAsync(id);
        return $"track={id} before={before} save={saved} afterSave={after} remove={removed} final={final}";
    }

    /// <summary>Debug: read-only calls showing which library endpoints this account/app accepts.</summary>
    public async Task<string> DiagnoseAsync()
    {
        var sb = new StringBuilder();
        var st = await Call(HttpMethod.Get, "/me/player");
        string? id = null, ctx = null;
        if (st.status == 200)
        {
            using var doc = JsonDocument.Parse(st.body);
            if (doc.RootElement.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object) id = it.GetProperty("id").GetString();
            if (doc.RootElement.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object) ctx = c.GetProperty("uri").GetString();
        }
        sb.AppendLine($"player: {st.status} track={id} context={ctx}");
        if (id != null)
        {
            var a = await Call(HttpMethod.Get, $"/me/tracks/contains?ids={id}");
            sb.AppendLine($"GET /me/tracks/contains: {a.status} {Trim(a.body)}");
            var b = await Call(HttpMethod.Get, $"/me/library/contains?uris=spotify:track:{id}");
            sb.AppendLine($"GET /me/library/contains: {b.status} {Trim(b.body)}");
        }
        var me = await Call(HttpMethod.Get, "/me");
        sb.AppendLine($"me: {me.status}");
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s.Replace(Environment.NewLine, " ");
}

/// <summary>Windows DPAPI (current user): the Spotify token can only be read by you, on this PC.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB data, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB data, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DATA_BLOB output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    public static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var result = Run(bytes, protect: true);
        return result == null ? "" : Convert.ToBase64String(result);
    }

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            var result = Run(Convert.FromBase64String(cipher), protect: false);
            return result == null ? null : Encoding.UTF8.GetString(result);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? Run(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Notchy Spotify", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* UI_FORBIDDEN */, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out outBlob);
            if (!ok) return null;
            var output = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
            LocalFree(outBlob.pbData);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }
}
