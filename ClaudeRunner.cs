using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// Runs Claude Code in headless mode ("claude -p") for Notchy Chat and streams the answer back.
/// Uses the user's own Claude Code login; nothing else is needed.
/// </summary>
public sealed class ClaudeRunner
{
    public static readonly string WorkDir = Path.Combine(AppSettings.Folder, "chat");

    private readonly Dispatcher _ui;
    private Process? _proc;
    private int _runId;

    /// <summary>Full answer text so far (UI thread).</summary>
    public event Action<string>? TextChanged;

    /// <summary>Final text and whether it is an error (UI thread).</summary>
    public event Action<string, bool>? Finished;

    public bool Busy { get; private set; }

    public ClaudeRunner(Dispatcher ui) => _ui = ui;

    /// <summary>Official install first, then PATH, then the copy bundled with the Claude desktop app.</summary>
    public static string? FindClaude()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string> { Path.Combine(home, ".local", "bin", "claude.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
            candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
            if (Directory.Exists(data))
            {
                var bundled = Directory.GetDirectories(data)
                    .Select(d => Path.Combine(d, "claude-code"))
                    .Where(Directory.Exists)
                    .SelectMany(d => Directory.GetFiles(d, "claude.exe", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (bundled != null) return bundled;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    /// <summary>Checks "claude auth status" (off the UI thread). Returns (found, loggedIn, path).</summary>
    public static async Task<(bool found, bool loggedIn, string? path)> CheckAsync()
    {
        var exe = FindClaude();
        if (exe == null) return (false, false, null);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("status");
            psi.Environment["NOTCHY_CHAT"] = "1";
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (true, output.Contains("\"loggedIn\": true") || output.Contains("\"loggedIn\":true"), exe);
        }
        catch
        {
            return (true, false, exe);
        }
    }

    public void Cancel()
    {
        _runId++;
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch
        {
            // ignore
        }
        _proc = null;
        Busy = false;
    }

    /// <param name="followUp">Continue the previous conversation (claude --continue).</param>
    public void Run(string prompt, string systemPrompt, bool followUp)
    {
        Cancel();
        int id = ++_runId;
        Busy = true;

        var exe = FindClaude();
        if (exe == null)
        {
            Busy = false;
            Finished?.Invoke(Loc.T("chat.err.noclaude"), true);
            return;
        }

        Directory.CreateDirectory(WorkDir);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = WorkDir
        };
        foreach (var a in new[]
                 {
                     "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                     "--max-turns", "4", "--allowedTools", "Read", "--append-system-prompt", systemPrompt
                 })
            psi.ArgumentList.Add(a);
        if (followUp) psi.ArgumentList.Add("--continue");
        psi.Environment["NOTCHY_CHAT"] = "1"; // our own hook ignores these requests

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Busy = false;
            Finished?.Invoke(Loc.T("chat.err.start") + " " + ex.Message, true);
            return;
        }
        _proc = proc;

        _ = Task.Run(async () =>
        {
            var text = new StringBuilder();
            string? final = null;
            bool isError = false;
            var lastPush = DateTime.MinValue;
            var stderr = proc.StandardError.ReadToEndAsync();
            try
            {
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();

                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    if (id != _runId) return;
                    if (line.Length == 0 || line[0] != '{') continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

                    if (type == "stream_event" && root.TryGetProperty("event", out var ev))
                    {
                        string evType = ev.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
                        if (evType == "message_start" && text.Length > 0) text.Append("\n\n");
                        if (evType == "content_block_delta" && ev.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var dt))
                        {
                            text.Append(dt.GetString());
                            if ((DateTime.Now - lastPush).TotalMilliseconds > 50)
                            {
                                lastPush = DateTime.Now;
                                string snapshot = text.ToString();
                                _ = _ui.InvokeAsync(() => { if (id == _runId) TextChanged?.Invoke(snapshot); });
                            }
                        }
                    }
                    else if (type == "assistant" && text.Length == 0 && root.TryGetProperty("message", out var msg) &&
                             msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                            if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" && part.TryGetProperty("text", out var tx))
                                text.Append(tx.GetString());
                    }
                    else if (type == "result")
                    {
                        final = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                        isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    }
                }
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                if (final == null)
                {
                    final = ex.Message;
                    isError = true;
                }
            }

            string err = "";
            try { err = await stderr; } catch { }
            if (string.IsNullOrWhiteSpace(final))
            {
                final = text.Length > 0 ? text.ToString() : err.Trim();
                if (text.Length == 0) isError = true;
            }
            if (final!.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) ||
                final.Contains("/login", StringComparison.OrdinalIgnoreCase))
            {
                final = Loc.T("chat.err.login");
                isError = true;
            }

            string done = final.Trim();
            _ = _ui.InvokeAsync(() =>
            {
                if (id != _runId) return;
                Busy = false;
                _proc = null;
                Finished?.Invoke(done, isError);
            });
        });
    }
}
