using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Notchy;

public enum ClaudePhase { Idle, Thinking, Reading, Editing, Testing, Running, Done }

public sealed record FileEdit(string FileName, string Lang, string[] Lines);

public sealed record ClaudeJob(string Project, string Prompt, DateTime Finished, double Seconds);

public sealed class ClaudeSession
{
    public string Id { get; init; } = "";
    public string Cwd { get; set; } = "";
    public string Project => string.IsNullOrEmpty(Cwd) ? "Claude" : Path.GetFileName(Cwd.TrimEnd('\\', '/'));
    public DateTime? TaskStart { get; set; }
    public DateTime? TaskEnd { get; set; }
    public ClaudePhase Phase { get; set; }
    public string Detail { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int StepIndex { get; set; }
    public int StepCount { get; set; }
    public FileEdit? LastEdit { get; set; }
    public DateTime LastEvent { get; set; } = DateTime.Now;
    public bool NeedsAttention { get; set; }
    public string AttentionMessage { get; set; } = "";
    public HashSet<ClaudePhase> PhasesSeen { get; } = new();

    public bool IsWorking => TaskStart != null && TaskEnd == null;
    public TimeSpan Elapsed => TaskStart == null ? TimeSpan.Zero : (TaskEnd ?? DateTime.Now) - TaskStart.Value;
}

/// <summary>
/// Receives Claude Code hook events (forwarded by "Notchy.exe --hook") on a loopback TCP port
/// and keeps a small model of what Claude is doing.
/// </summary>
public sealed class ClaudeService
{
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, ClaudeSession> _sessions = new();
    private TcpListener? _listener;
    private static readonly string StatsFile = Path.Combine(AppSettings.Folder, "claude-stats.json");

    public event Action? Changed;
    public event Action<ClaudeSession>? TaskDone;
    public event Action<ClaudeSession>? AttentionNeeded;

    public List<ClaudeJob> Recent { get; private set; } = new();
    public int JobsToday => Recent.Count(j => j.Finished.Date == DateTime.Today) + _extraToday;
    private int _extraToday; // jobs beyond the stored recent list
    private DateTime _extraDay = DateTime.Today;
    public bool Listening => _listener != null;
    public DateTime LastEventAt { get; private set; } = DateTime.MinValue;

    public ClaudeService(Dispatcher ui)
    {
        _ui = ui;
        LoadStats();
    }

    /// <summary>The session to display: the most recently active one (within the last hour).</summary>
    public ClaudeSession? Active =>
        _sessions.Values.Where(s => (DateTime.Now - s.LastEvent).TotalMinutes < 60)
            .OrderByDescending(s => s.IsWorking || s.NeedsAttention)
            .ThenByDescending(s => s.LastEvent)
            .FirstOrDefault();

    public bool AnyWorking => _sessions.Values.Any(s => s.IsWorking);

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Program.HookPort);
            _listener.Start();
            _ = AcceptLoop(_listener);
        }
        catch (Exception ex)
        {
            _listener = null;
            App.Log(ex);
        }
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 2000;
                using var net = client.GetStream();
                using var ms = new MemoryStream();
                var buf = new byte[16384];
                int read;
                while ((read = net.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                    if (ms.Length > 8_000_000) break;
                }
                string json = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF', ' ', '\r', '\n', '\t'); // tolerate a BOM / leading whitespace
                var doc = JsonDocument.Parse(json);
                _ui.InvokeAsync(() =>
                {
                    using (doc)
                    {
                        try { Handle(doc.RootElement); } catch (Exception ex) { App.Log(ex); }
                    }
                });
            }
        }
        catch
        {
            // malformed or timed-out payload
        }
    }

    /// <summary>Feeds a hook payload directly (used for previews/tests).</summary>
    public void Inject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Handle(doc.RootElement);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void Handle(JsonElement root)
    {
        if (!AppSettings.Current.ClaudeEnabled) return;

        string evt = Str(root, "hook_event_name");
        string id = Str(root, "session_id");
        if (string.IsNullOrEmpty(evt)) return;
        if (string.IsNullOrEmpty(id)) id = "default";
        LastEventAt = DateTime.Now;

        if (!_sessions.TryGetValue(id, out var s))
        {
            s = new ClaudeSession { Id = id };
            _sessions[id] = s;
        }
        string cwd = Str(root, "cwd");
        if (!string.IsNullOrEmpty(cwd)) s.Cwd = cwd;
        s.LastEvent = DateTime.Now;

        switch (evt)
        {
            case "SessionStart":
                if (!s.IsWorking) s.Phase = ClaudePhase.Idle;
                break;

            case "UserPromptSubmit":
                BeginTask(s);
                s.Prompt = OneLine(Str(root, "prompt"));
                s.Phase = ClaudePhase.Thinking;
                s.Detail = "";
                break;

            case "PreToolUse":
                if (!s.IsWorking) BeginTask(s);
                s.NeedsAttention = false;
                OnTool(s, Str(root, "tool_name"), root.TryGetProperty("tool_input", out var input) ? input : default);
                break;

            case "PostToolUse":
            case "PostToolUseFailure":
                s.NeedsAttention = false;
                break;

            case "Notification":
            case "PermissionRequest":
            {
                string msg = Str(root, "message");
                string type = Str(root, "notification_type");
                bool permission = evt == "PermissionRequest" || type.Contains("permission") ||
                                  msg.Contains("permission", StringComparison.OrdinalIgnoreCase);
                // "waiting for your input" after a finished task is not news.
                if (!permission && !s.IsWorking) break;
                s.NeedsAttention = true;
                s.AttentionMessage = permission && evt == "PermissionRequest"
                    ? Str(root, "tool_name")
                    : OneLine(msg);
                AttentionNeeded?.Invoke(s);
                break;
            }

            case "Stop":
                if (s.IsWorking)
                {
                    s.TaskEnd = DateTime.Now;
                    s.Phase = ClaudePhase.Done;
                    s.PhasesSeen.Add(ClaudePhase.Done);
                    s.NeedsAttention = false;
                    AddJob(s);
                    TaskDone?.Invoke(s);
                }
                break;

            case "SessionEnd":
                _sessions.Remove(id);
                break;
        }
        Changed?.Invoke();
    }

    private static void BeginTask(ClaudeSession s)
    {
        s.TaskStart = DateTime.Now;
        s.TaskEnd = null;
        s.PhasesSeen.Clear();
        s.StepIndex = s.StepCount = 0;
        s.LastEdit = null;
        s.NeedsAttention = false;
        s.Phase = ClaudePhase.Thinking;
    }

    private static readonly Regex TestCommand = new(
        @"\b(test|tests|pytest|jest|vitest|mocha|build|dotnet|msbuild|npm|pnpm|yarn|bun|cargo|go\s+test|gradle|mvn|make|tsc|lint|eslint)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void OnTool(ClaudeSession s, string tool, JsonElement input)
    {
        string t = tool;
        string shortName = t.Contains("__") ? t[(t.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : t;

        // Plan / todo tools: update step counter only.
        if (t == "TodoWrite" || shortName == "update_plan")
        {
            UpdateSteps(s, input);
            return;
        }

        switch (t)
        {
            case "Read":
            case "NotebookRead":
                SetPhase(s, ClaudePhase.Reading, FileName(Str(input, "file_path")));
                break;
            case "Grep":
            case "Glob":
            case "LS":
                SetPhase(s, ClaudePhase.Reading, Str(input, "pattern"));
                break;
            case "WebFetch":
            case "WebSearch":
                SetPhase(s, ClaudePhase.Reading, Str(input, "url") is { Length: > 0 } u ? new Uri(u).Host : Str(input, "query"));
                break;
            case "Edit":
            case "Write":
            case "MultiEdit":
            case "NotebookEdit":
            {
                string path = Str(input, "file_path");
                if (path.Length == 0) path = Str(input, "notebook_path");
                SetPhase(s, ClaudePhase.Editing, FileName(path));
                string code = Str(input, "new_string");
                if (code.Length == 0) code = Str(input, "content");
                if (code.Length == 0) code = Str(input, "new_source");
                if (code.Length == 0 && input.ValueKind == JsonValueKind.Object &&
                    input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > 0)
                    code = Str(edits[0], "new_string");
                s.LastEdit = new FileEdit(FileName(path), LangOf(path), FirstLines(code, 3));
                break;
            }
            case "Bash":
            case "PowerShell":
            {
                string cmd = OneLine(Str(input, "command"));
                SetPhase(s, TestCommand.IsMatch(cmd) ? ClaudePhase.Testing : ClaudePhase.Running, cmd);
                break;
            }
            case "Task":
            case "Agent":
                SetPhase(s, ClaudePhase.Running, OneLine(Str(input, "description")));
                break;
            default:
                SetPhase(s, s.Phase is ClaudePhase.Editing or ClaudePhase.Testing ? s.Phase : ClaudePhase.Running, shortName.Replace('_', ' '));
                break;
        }
    }

    private static void SetPhase(ClaudeSession s, ClaudePhase phase, string detail)
    {
        s.Phase = phase;
        s.Detail = detail;
        s.PhasesSeen.Add(phase == ClaudePhase.Running ? ClaudePhase.Reading : phase);
    }

    private static void UpdateSteps(ClaudeSession s, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        JsonElement list = default;
        if (input.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array) list = todos;
        else if (input.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) list = items;

        if (list.ValueKind == JsonValueKind.Array)
        {
            var statuses = list.EnumerateArray().Select(i => Str(i, "status")).ToList();
            s.StepCount = statuses.Count;
            int current = statuses.FindIndex(x => x == "in_progress");
            s.StepIndex = current >= 0 ? current + 1 : Math.Min(statuses.Count, statuses.Count(x => x == "completed") + 1);
            return;
        }
        if (input.TryGetProperty("advance", out var adv) && adv.ValueKind == JsonValueKind.True && s.StepCount > 0)
            s.StepIndex = Math.Min(s.StepCount, s.StepIndex + 1);
        if (input.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Number && s.StepCount > 0)
            s.StepIndex = Math.Clamp(start.GetInt32(), 1, s.StepCount);
    }

    private static string FileName(string path) => string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path);

    private static string LangOf(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return ext switch
        {
            "" => "TXT",
            "CSPROJ" => "XML",
            "MD" or "MDX" => "MD",
            _ => ext.Length > 4 ? ext[..4] : ext
        };
    }

    private static string[] FirstLines(string code, int count) =>
        code.Replace("\r", "").Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(count)
            .Select(l => l.Replace("\t", "    ").TrimEnd())
            .Select(l => l.Length > 90 ? l[..90] : l)
            .ToArray();

    private static string OneLine(string text)
    {
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    // ------------------------------------------------------------------ stats

    private void AddJob(ClaudeSession s)
    {
        if (s.Id is "demo" or "notchy-demo") return; // simulations don't count

        if (_extraDay != DateTime.Today)
        {
            _extraDay = DateTime.Today;
            _extraToday = 0;
        }
        Recent.Insert(0, new ClaudeJob(s.Project, s.Prompt, DateTime.Now, s.Elapsed.TotalSeconds));
        if (Recent.Count > 12)
        {
            var dropped = Recent[^1];
            if (dropped.Finished.Date == DateTime.Today) _extraToday++;
            Recent.RemoveAt(Recent.Count - 1);
        }
        SaveStats();
    }

    private sealed class Stats
    {
        public List<ClaudeJob> Recent { get; set; } = new();
        public int ExtraToday { get; set; }
        public DateTime ExtraDay { get; set; }
    }

    private void LoadStats()
    {
        try
        {
            if (!File.Exists(StatsFile)) return;
            var st = JsonSerializer.Deserialize<Stats>(File.ReadAllText(StatsFile));
            if (st == null) return;
            Recent = st.Recent;
            if (st.ExtraDay == DateTime.Today)
            {
                _extraToday = st.ExtraToday;
                _extraDay = st.ExtraDay;
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SaveStats()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(StatsFile, JsonSerializer.Serialize(new Stats { Recent = Recent, ExtraToday = _extraToday, ExtraDay = _extraDay }));
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>Installs / removes Notchy's hooks in ~/.claude/settings.json (with a backup).</summary>
public static class ClaudeHooks
{
    private static readonly string[] Events =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd"
    };

    private static readonly string[] MatcherEvents = { "PreToolUse", "PostToolUse" };

    public static string SettingsPath => Environment.GetEnvironmentVariable("NOTCHY_CLAUDE_SETTINGS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    // Forward slashes work in both Git Bash and cmd, which Claude Code may use to run hooks on Windows.
    private static string Command => $"\"{Environment.ProcessPath?.Replace('\\', '/')}\" --hook";

    private static bool IsOurs(JsonNode? hook) =>
        hook?["command"]?.GetValue<string>() is string c &&
        c.Contains("--hook", StringComparison.Ordinal) &&
        c.Contains("Notchy", StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                var root = JsonNode.Parse(File.ReadAllText(SettingsPath));
                var stop = root?["hooks"]?["Stop"] as JsonArray;
                return stop != null && stop.Any(g => (g?["hooks"] as JsonArray)?.Any(IsOurs) == true);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when the installed hooks point to a different Notchy.exe (e.g. the app was moved).</summary>
    public static bool PointsElsewhere
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return false;
                var root = JsonNode.Parse(File.ReadAllText(SettingsPath));
                var stop = root?["hooks"]?["Stop"] as JsonArray;
                var ours = stop?.SelectMany(g => (g?["hooks"] as JsonArray) ?? new JsonArray()).FirstOrDefault(IsOurs);
                return ours != null && ours["command"]?.GetValue<string>() != Command;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Install()
    {
        var root = LoadOrCreate(out string? backup);
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        RemoveOurs(hooks);

        foreach (var evt in Events)
        {
            if (hooks[evt] is not JsonArray arr)
            {
                arr = new JsonArray();
                hooks[evt] = arr;
            }
            var group = new JsonObject();
            if (MatcherEvents.Contains(evt)) group["matcher"] = "*";
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = Command,
                ["timeout"] = 5
            });
            arr.Add(group);
        }
        Save(root);
        return backup ?? "";
    }

    public static string Uninstall()
    {
        var root = LoadOrCreate(out string? backup);
        if (root["hooks"] is JsonObject hooks)
        {
            RemoveOurs(hooks);
            if (hooks.Count == 0) root.Remove("hooks");
        }
        Save(root);
        return backup ?? "";
    }

    private static void RemoveOurs(JsonObject hooks)
    {
        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray groups) continue;
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i]?["hooks"] is not JsonArray list) continue;
                for (int j = list.Count - 1; j >= 0; j--)
                    if (IsOurs(list[j])) list.RemoveAt(j);
                if (list.Count == 0) groups.RemoveAt(i);
            }
            if (groups.Count == 0) hooks.Remove(evt);
        }
    }

    private static JsonObject LoadOrCreate(out string? backup)
    {
        backup = null;
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return new JsonObject();

        string text = File.ReadAllText(path);
        backup = path + $".notchy-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backup, true);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidDataException("settings.json is not a JSON object");
    }

    private static void Save(JsonObject root)
    {
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
    }
}
