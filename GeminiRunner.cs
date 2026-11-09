using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Notchy;

/// <summary>What answers in Notchy Chat: Claude Code on this PC, or Google's Gemini with a free key.</summary>
public interface IChatRunner
{
    /// <summary>Full answer text so far (UI thread).</summary>
    event Action<string>? TextChanged;

    /// <summary>Final text and whether it is an error (UI thread).</summary>
    event Action<string, bool>? Finished;

    bool Busy { get; }
    void Cancel();
    void Run(string prompt, string systemPrompt, bool followUp, string? imagePath);
}

/// <summary>The Gemini key: only on this PC, encrypted by Windows for this user.</summary>
public static class GeminiKey
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "gemini-key.bin");

    public static bool Exists => File.Exists(FilePath)
#if DEBUG
        || Environment.GetEnvironmentVariable("NOTCHY_GEMINI_TESTKEY") != null
#endif
        ;

    public static void Save(string key)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    public static string? Load()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("NOTCHY_GEMINI_TESTKEY") is { } test) return test;
#endif
        try
        {
            return File.Exists(FilePath) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser)) : null;
        }
        catch (Exception ex)
        {
            App.Log(ex); // made by another Windows user: as good as no key
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}

public enum GeminiKeyCheck { Ok, Invalid, NoConnection }

/// <summary>Notchy Chat through the Gemini API (free tier, with a key each person creates at Google).</summary>
public sealed class GeminiRunner : IChatRunner
{
    public const string KeyPageUrl = "https://aistudio.google.com/apikey";

    // stable models first (the newest one, behind the "latest" name, often hangs); that name is for when these retire
    private static readonly string[] Models = { "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-flash-latest" };
    private const int MaxHistoryMessages = 12, PushEveryMs = 50, AnswerTimeoutSeconds = 120, CheckTimeoutSeconds = 12, FirstWordsTimeoutSeconds = 15;

    private static string BaseUrl =>
#if DEBUG
        Environment.GetEnvironmentVariable("NOTCHY_GEMINI_URL") ??
#endif
        "https://generativelanguage.googleapis.com";

    private static readonly HttpClient Http = FastHttp.Client(Timeout.InfiniteTimeSpan);

    private readonly Dispatcher _ui;
    private readonly List<JsonObject> _history = new();
    private CancellationTokenSource? _cts;
    private int _runId;

    public event Action<string>? TextChanged;
    public event Action<string, bool>? Finished;
    public bool Busy { get; private set; }

    public GeminiRunner(Dispatcher ui) => _ui = ui;

    private static HttpRequestMessage Request(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("x-goog-api-key", key); // in a header, so it never ends up in a logged address
        return request;
    }

    /// <summary>Asks Google whether the key works (lists the models, which costs nothing).</summary>
    public static async Task<GeminiKeyCheck> CheckKeyAsync(string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CheckTimeoutSeconds));
            using var request = Request(HttpMethod.Get, "/v1beta/models?pageSize=1", key);
            using var response = await Http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode) return GeminiKeyCheck.Ok;
            return response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? GeminiKeyCheck.Invalid : GeminiKeyCheck.NoConnection;
        }
        catch
        {
            return GeminiKeyCheck.NoConnection;
        }
    }

#if DEBUG
    /// <summary>Test: what Google answers to a tiny question, model by model (never writes the key).</summary>
    public static async Task DiagnoseAsync(string file)
    {
        var log = new StringBuilder();
        string? key = GeminiKey.Load();
        log.AppendLine($"chave salva: {!string.IsNullOrEmpty(key)}");
        const string body = "{\"systemInstruction\":{\"parts\":[{\"text\":\"Responda curto.\"}]},\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"Diga oi.\"}]}]}";
        for (int round = 1; round <= 3; round++)
            foreach (string model in new[] { "gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-flash-latest" })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var request = Request(HttpMethod.Post, $"/v1beta/models/{model}:streamGenerateContent?alt=sse", key ?? "");
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    long headers = clock.ElapsedMilliseconds;
                    string text = await response.Content.ReadAsStringAsync(cts.Token);
                    log.AppendLine($"rodada {round} {model}: {(int)response.StatusCode} cabecalho={headers}ms total={clock.ElapsedMilliseconds}ms");
                    if (!response.IsSuccessStatusCode) log.AppendLine(text.Length > 300 ? text[..300] : text);
                }
                catch (Exception ex)
                {
                    log.AppendLine($"rodada {round} {model}: excecao {ex.GetType().Name} apos {clock.ElapsedMilliseconds}ms {ex.InnerException?.Message}");
                }
            }
        File.WriteAllText(file, log.ToString());
    }
#endif

    public void Cancel()
    {
        _runId++;
        try { _cts?.Cancel(); } catch { }
        _cts = null;
        Busy = false;
    }

    public void Run(string prompt, string systemPrompt, bool followUp, string? imagePath)
    {
        Cancel();
        int id = ++_runId;
        Busy = true;
        if (!followUp) _history.Clear();

        string? key = GeminiKey.Load();
        if (string.IsNullOrEmpty(key))
        {
            Busy = false;
            Finished?.Invoke(Loc.T("chat.err.gnokey"), true);
            return;
        }

        var question = new JsonObject { ["role"] = "user", ["parts"] = PartsFor(prompt, imagePath) };
        _history.Add(question);
        while (_history.Count > MaxHistoryMessages) _history.RemoveAt(0);

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
            ["contents"] = new JsonArray(_history.ConvertAll(m => (JsonNode?)m.DeepClone()).ToArray())
        };
        string json = body.ToJsonString();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AnswerTimeoutSeconds));
        _cts = cts;
        _ = Task.Run(() => StreamAsync(id, key, json, question, cts));
    }

    private static JsonArray PartsFor(string prompt, string? imagePath)
    {
        var parts = new JsonArray();
        if (imagePath != null && File.Exists(imagePath))
        {
            string mime = Path.GetExtension(imagePath).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(File.ReadAllBytes(imagePath)) } });
        }
        parts.Add(new JsonObject { ["text"] = prompt });
        return parts;
    }

    private async Task StreamAsync(int id, string key, string json, JsonObject question, CancellationTokenSource cts)
    {
        var text = new StringBuilder();
        string? error = null;
        try
        {
            foreach (string model in Models)
            {
                bool last = model == Models[^1];
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(FirstWordsTimeoutSeconds)); // no first words in time: the other model gets a chance
                try
                {
                    using var request = Request(HttpMethod.Post, $"/v1beta/models/{model}:streamGenerateContent?alt=sse", key);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    if (!last && (response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode >= 500)) continue;
                    if (!response.IsSuccessStatusCode)
                    {
                        error = ErrorFor(response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
                        break;
                    }

                    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(attempt.Token), Encoding.UTF8);
                    var lastPush = DateTime.MinValue;
                    string? line;
                    while ((line = await reader.ReadLineAsync(attempt.Token)) != null)
                    {
                        if (id != _runId) return;
                        if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                        AppendText(line[5..], text);
                        if (text.Length == 0) continue;
                        attempt.CancelAfter(Timeout.InfiniteTimeSpan); // it's answering: only the whole answer's time limit counts now
                        if ((DateTime.Now - lastPush).TotalMilliseconds < PushEveryMs) continue;
                        lastPush = DateTime.Now;
                        string snapshot = text.ToString();
                        _ = _ui.InvokeAsync(() => { if (id == _runId) TextChanged?.Invoke(snapshot); });
                    }
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
                {
                    if (id != _runId) return;
                    if (text.Length > 0) break; // keep what already arrived
                    App.Log(new Exception($"Gemini {model}: {ex.GetType().Name}"));
                    if (last || cts.IsCancellationRequested) error = Loc.T("chat.err.gnet");
                    if (cts.IsCancellationRequested) break;
                }
            }
        }
        catch (JsonException)
        {
            if (id != _runId) return;
            if (text.Length == 0) error = Loc.T("chat.err.gnet");
        }
        finally
        {
            cts.Dispose();
        }

        string answer = text.ToString().Trim();
        if (error == null && answer.Length == 0) error = Loc.T("chat.err.gempty");
        _ = _ui.InvokeAsync(() =>
        {
            if (id != _runId) return;
            Busy = false;
            _cts = null;
            if (error != null) _history.Remove(question);
            else _history.Add(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(new JsonObject { ["text"] = answer }) });
            Finished?.Invoke(error ?? answer, error != null);
        });
    }

    private static void AppendText(string json, StringBuilder text)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0) return;
        if (!candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts)) return;
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t)) text.Append(t.GetString());
    }

    private static string ErrorFor(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.TooManyRequests) return Loc.T("chat.err.glimit");
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || body.Contains("API_KEY", StringComparison.Ordinal)) return Loc.T("chat.err.gkey");
        return Loc.T("chat.err.gnet");
    }
}
