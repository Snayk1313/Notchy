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
    public ClaudeHostInfo? Host { get; set; }

    /// <summary>Permission requests waiting for an answer on the island (oldest first).</summary>
    public List<PermissionAsk> Asks { get; } = new();
    public PermissionAsk? Ask => Asks.Count > 0 ? Asks[0] : null;

    public bool IsWorking => TaskStart != null && TaskEnd == null;
    public TimeSpan Elapsed => TaskStart == null ? TimeSpan.Zero : (TaskEnd ?? DateTime.Now) - TaskStart.Value;
}

/// <summary>A tool waiting for permission: answered on the island through the hook's open connection.</summary>
public sealed class PermissionAsk
{
    public string SessionId { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Summary { get; init; } = "";
    public string InputJson { get; init; } = "";
    internal TcpClient? Client { get; init; }
    internal bool Closed { get; set; }
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
        bool keep = false;
        try
        {
            client.ReceiveTimeout = 2000;
            var net = client.GetStream();
            using var ms = new MemoryStream();
            var buf = new byte[16384];
            int read;
            while ((read = net.Read(buf, 0, buf.Length)) > 0)
            {
                // a permission request ends with a 0 byte and keeps the connection open for the answer
                bool end = buf[read - 1] == 0;
                ms.Write(buf, 0, end ? read - 1 : read);
                if (end || ms.Length > 8_000_000) break;
            }
            string json = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF', ' ', '\r', '\n', '\t'); // tolerate a BOM / leading whitespace
            var doc = JsonDocument.Parse(json);
            PermissionAsk? ask = null;
            var root = doc.RootElement;
            if (Str(root, "hook_event_name") == "PermissionRequest" && AppSettings.Current.ClaudeEnabled && AppSettings.Current.ClaudeAnswerOnIsland
                && !IsQuestion(Str(root, "tool_name")))
            {
                // the hook waits on this connection for the answer
                var input = root.TryGetProperty("tool_input", out var ti) ? ti : default;
                string tool = Str(root, "tool_name");
                ask = new PermissionAsk
                {
                    SessionId = Str(root, "session_id") is { Length: > 0 } sid ? sid : "default",
                    Tool = tool,
                    Summary = AskSummary(tool, input),
                    InputJson = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText(),
                    Client = client
                };
                keep = true;
                _ = WatchAsk(ask);
            }
            _ui.InvokeAsync(() =>
            {
                using (doc)
                {
                    try { Handle(doc.RootElement, ask); } catch (Exception ex) { App.Log(ex); }
                }
            });
        }
        catch
        {
            // malformed or timed-out payload
        }
        finally
        {
            if (!keep) client.Dispose();
        }
    }

    /// <summary>Notices when the hook goes away (answered in Claude itself, or timed out) and drops the question.</summary>
    private async Task WatchAsk(PermissionAsk ask)
    {
        var started = DateTime.UtcNow;
        while (!ask.Closed)
        {
            await Task.Delay(400).ConfigureAwait(false);
            bool gone;
            try
            {
                var sock = ask.Client!.Client;
                gone = sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0;
            }
            catch
            {
                gone = true;
            }
            if (gone || DateTime.UtcNow - started > TimeSpan.FromSeconds(600))
            {
                _ = _ui.InvokeAsync(() => DropAsk(ask));
                return;
            }
        }
    }

    /// <summary>Answers a permission request from the island.</summary>
    public void Answer(PermissionAsk ask, bool allow)
    {
        if (ask.Closed) return;
        string decision = allow
            ? "{\"behavior\":\"allow\"}"
            : "{\"behavior\":\"deny\",\"message\":" + JsonSerializer.Serialize(Loc.T("claude.ask.denied")) + "}";
        string json = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":" + decision + "}}";
        try
        {
            var net = ask.Client!.GetStream();
            net.WriteTimeout = 1000;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            net.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        DropAsk(ask);
    }

    /// <summary>Forgets a question without answering it (Claude's own prompt stays in charge).</summary>
    private void DropAsk(PermissionAsk ask)
    {
        if (!ask.Closed)
        {
            ask.Closed = true;
            try { ask.Client?.Dispose(); } catch { }
        }
        if (_sessions.TryGetValue(ask.SessionId, out var s) && s.Asks.Remove(ask))
        {
            if (s.Asks.Count == 0) s.NeedsAttention = false;
            Changed?.Invoke();
        }
    }

    private static void DropAsks(ClaudeSession s, Func<PermissionAsk, bool> which)
    {
        foreach (var a in s.Asks.Where(which).ToList())
        {
            a.Closed = true;
            try { a.Client?.Dispose(); } catch { }
            s.Asks.Remove(a);
        }
    }

    /// <summary>Tools that ask you something (a question with options, a plan to approve): answered in Claude, not Allow/Deny.</summary>
    private static bool IsQuestion(string tool) => tool is "AskUserQuestion" or "ExitPlanMode";

    /// <summary>What the tool wants to do, in one line: the command, the file, the address...</summary>
    private static string AskSummary(string tool, JsonElement input)
    {
        string v = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "Write" or "MultiEdit" or "Read" => FileName(Str(input, "file_path")),
            "NotebookEdit" => FileName(Str(input, "notebook_path")),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            _ => Str(input, "description")
        };
        if (v.Length == 0) v = Str(input, "description");
        v = Regex.Replace(v, @"\s+", " ").Trim();
        return v.Length > 300 ? v[..300] + "…" : v;
    }

    /// <summary>Feeds a hook payload directly (used for previews/tests).</summary>
    public void Inject(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Handle(doc.RootElement, null);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void Handle(JsonElement root, PermissionAsk? ask)
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
        if (root.TryGetProperty("notchy_host", out var host) && host.ValueKind == JsonValueKind.Object &&
            host.TryGetProperty("hwnd", out var hw) && hw.TryGetInt64(out long hwnd) &&
            host.TryGetProperty("pid", out var hp) && hp.TryGetInt32(out int hpid))
            s.Host = new ClaudeHostInfo(hpid, hwnd, Str(host, "exe"));

        switch (evt)
        {
            case "SessionStart":
                if (!s.IsWorking) s.Phase = ClaudePhase.Idle;
                break;

            case "UserPromptSubmit":
                DropAsks(s, _ => true);
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
            {
                // that tool got its answer (in Claude itself, if it's still on the island)
                string tool = Str(root, "tool_name");
                string done = root.TryGetProperty("tool_input", out var ti) ? ti.GetRawText() : "";
                if (s.Asks.Any(a => a.Tool == tool && a.InputJson == done)) DropAsks(s, a => a.Tool == tool && a.InputJson == done);
                else if (s.Asks.FirstOrDefault(a => a.Tool == tool) is { } same) DropAsks(s, a => ReferenceEquals(a, same));
                s.NeedsAttention = s.Asks.Count > 0;
                break;
            }

            case "Notification":
            case "PermissionRequest":
            {
                string msg = Str(root, "message");
                string type = Str(root, "notification_type");
                bool permission = evt == "PermissionRequest" || type.Contains("permission") ||
                                  msg.Contains("permission", StringComparison.OrdinalIgnoreCase);
                // "waiting for your input" after a finished task is not news.
                if (!permission && !s.IsWorking) break;
                if (ask != null) s.Asks.Add(ask);
                if (evt == "PermissionRequest" && IsQuestion(Str(root, "tool_name")))
                {
                    s.NeedsAttention = true;
                    s.AttentionMessage = Loc.T(Str(root, "tool_name") == "ExitPlanMode" ? "claude.plan" : "claude.question");
                    AttentionNeeded?.Invoke(s);
                    break;
                }
                // the "waiting for permission" reminder of a question already on the island isn't news
                if (evt == "Notification" && s.Asks.Count > 0) break;
                s.NeedsAttention = true;
                s.AttentionMessage = permission && evt == "PermissionRequest"
                    ? Str(root, "tool_name")
                    : OneLine(msg);
                AttentionNeeded?.Invoke(s);
                break;
            }

            case "Stop":
                DropAsks(s, _ => true);
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
                DropAsks(s, _ => true);
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
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "SessionEnd"
    };

    private static readonly string[] MatcherEvents = { "PreToolUse", "PostToolUse", "PermissionRequest" };

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
                if (ours == null) return false;
                if (ours["command"]?.GetValue<string>() != Command) return true;
                // installed by an older Notchy, without the permission hook: needs a refresh too
                var perm = root?["hooks"]?["PermissionRequest"] as JsonArray;
                return perm == null || !perm.Any(g => (g?["hooks"] as JsonArray)?.Any(IsOurs) == true);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Hooks put in by an older version of this same Notchy lack the permission hook: add it on start
    /// (only when they already point here, so a different copy of the app is never taken over).
    /// </summary>
    public static void AddMissingPermissionHook()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath));
            var stop = root?["hooks"]?["Stop"] as JsonArray;
            var ours = stop?.SelectMany(g => (g?["hooks"] as JsonArray) ?? new JsonArray()).FirstOrDefault(IsOurs);
            if (ours == null || ours["command"]?.GetValue<string>() != Command) return;
            var perm = root?["hooks"]?["PermissionRequest"] as JsonArray;
            if (perm != null && perm.Any(g => (g?["hooks"] as JsonArray)?.Any(IsOurs) == true)) return;
            Install();
        }
        catch (Exception ex)
        {
            App.Log(ex);
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
                ["timeout"] = evt == "PermissionRequest" ? 600 : 5 // waits for the answer on the island
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
