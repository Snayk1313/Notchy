using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace Notchy;

public enum TransferKind { Download, Copy }

/// <summary>
/// What is being transferred right now (one summary for everything running).
/// Progress, Total, Speed and Eta are null when they can't be known.
/// </summary>
public sealed record TransferInfo(TransferKind Kind, string Name, int Count, double? Progress, long Bytes, long? Total,
    double? BytesPerSecond, TimeSpan? Eta)
{
    /// <summary>Downloads: the in-progress file (".crdownload", ".part"...) of the newest download.</summary>
    public string PartialPath { get; init; } = "";
    /// <summary>Downloads: the browser's process name ("zen", "firefox", "msedge", "opera"...), "" if unknown.</summary>
    public string Browser { get; init; } = "";
}

/// <summary>One file being downloaded (the Downloads tab lists them when there are several).</summary>
public sealed record DownloadFile(string PartialPath, string Name, long Bytes, double BytesPerSecond, long? Total = null)
{
    public double? Progress => Total is long t && t > 0 ? Math.Clamp((double)Bytes / t, 0, 0.999) : null;
    public TimeSpan? Eta => Total is long t && BytesPerSecond > 1024 ? TimeSpan.FromSeconds(Math.Max(0, t - Bytes) / BytesPerSecond) : null;
}

/// <summary>
/// Notices file transfers without any browser extension or hook:
/// - downloads: the browsers' in-progress files (.crdownload for Chrome/Edge/Brave, .opdownload for Opera,
///   .part for Firefox) that are still growing, in whatever folder or disk they're saved to: the Downloads folder,
///   the in-progress downloads Zen/Firefox list in their own downloads.json (exact place, nothing to search), and
///   for the Chromium browsers Windows' own notice when such a file is created anywhere (only while one of them
///   is open; only file creations/renames wake us, not writes). The size so far and the speed come from
///   the file itself; the total comes from the browser's own download UI (Edge's toolbar button says "11%",
///   Opera's download popup "8%", the downloads panel "7,1 MB de 60,0 MB"). Once the total is known it stays,
///   so the percentage keeps going even after that popup closes.
/// - copies/moves: Explorer's progress dialog (percentage in its title, file name and speed inside it).
/// <see cref="Poll"/> does blocking work (UI Automation): call it off the UI thread.
/// </summary>
public sealed class TransferWatcher
{
    private static readonly string[] PartialExtensions = { ".crdownload", ".opdownload", ".part", ".partial", ".download" };
    private static readonly string[] Browsers = { "msedge", "chrome", "opera", "brave", "vivaldi", "arc", "yandex" };
    private static readonly Regex Percent = new(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex SizeOfSize = new(@"([\d.,]+)\s*(B|KB|MB|GB|TB)\s+\S{1,4}\s+([\d.,]+)\s*(B|KB|MB|GB|TB)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Size = new(@"([\d.,]+)\s*(bytes|B|KB|MB|GB|TB)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed class Growing
    {
        public long Size;
        public DateTime Changed;
        public double Speed;
        public string? Final; // where the browser says it will end up, once known
    }

    private readonly Dictionary<string, Growing> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _downloads = DownloadsFolder();
    private DateTime _lastPoll = DateTime.UtcNow;
    private bool _primed;
    private string _lastPath = "", _lastName = "";

    private const int WatcherSettleMs = 200, MaxRecentFiles = 400, MaxMoves = 200, ActiveSeconds = 20;
    private static readonly TimeSpan RecentFileWindow = TimeSpan.FromSeconds(15);
    private readonly List<(string path, Growing file)> _vanished = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _moved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<(string path, DateTime when)> _recent = new();

    public TransferInfo? Current { get; private set; }

    /// <summary>Every download in progress (usually one).</summary>
    public IReadOnlyList<DownloadFile> Files { get; private set; } = Array.Empty<DownloadFile>();

    /// <summary>Raised (on the polling thread) when a transfer is over: kind, name, final file (downloads).</summary>
    public event Action<TransferKind, string, string?>? Finished;

    /// <summary>Raised (on the polling thread) when a download stopped without a file (failed / canceled).</summary>
    public event Action<string>? Failed;

    public void Poll()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.05, (now - _lastPoll).TotalSeconds);
        _lastPoll = now;

        var copy = ExplorerCopy(now);
        var download = Downloads(now, dt);
        var next = copy ?? download; // a copy has a real percentage, it wins

        var prev = Current;
        Current = next;
        if (prev is { Kind: TransferKind.Copy } && next?.Kind != TransferKind.Copy) Finished?.Invoke(prev.Kind, prev.Name, null);
        ResolveVanished(now);
    }

    /// <summary>
    /// Each in-progress file that went away, one by one (several downloads don't end together): it became a real file,
    /// or it was canceled. One that had long stopped growing (paused, or left in a "save as" window) goes without an animation.
    /// </summary>
    private void ResolveVanished(DateTime now)
    {
        if (_vanished.Count == 0)
        {
            if (_files.Count == 0) _reported.Clear();
            return;
        }
        System.Threading.Thread.Sleep(WatcherSettleMs); // (Windows tells us about a rename a moment after it happens)
        foreach (var (path, file) in _vanished)
        {
            if (FinishedFile(path, file.Size, file.Final) is string finalPath) Report(finalPath);
            else if (!RenamedStillDownloading(path) && (now - file.Changed).TotalSeconds < ActiveSeconds) ReportFailed(CleanName(path));
        }
        _vanished.Clear();
    }

    /// <summary>Chrome renames "Unconfirmed 123.crdownload" to "name.crdownload" a moment in: the same download, going on.</summary>
    private bool RenamedStillDownloading(string partial) => _moved.TryGetValue(partial, out var next) && IsPartial(next);

    private void ReportFailed(string name)
    {
#if DEBUG
        try { File.AppendAllText(Path.Combine(AppSettings.Folder, "transfer-test.log"), $"{DateTime.Now:HH:mm:ss} cancelado {name}\n"); } catch { }
#endif
        Failed?.Invoke(name);
    }

    private void Report(string finalPath)
    {
        if (!_reported.Add(finalPath)) return; // (it can get here through more than one name)
#if DEBUG
        try { File.AppendAllText(Path.Combine(AppSettings.Folder, "transfer-test.log"), $"{DateTime.Now:HH:mm:ss} concluido {finalPath}\n"); } catch { }
#endif
        Finished?.Invoke(TransferKind.Download, Path.GetFileName(finalPath), finalPath);
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>
    /// The file a download became, or null if it didn't finish (paused / failed). Chrome renames
    /// "name.crdownload" → "name"; Edge keeps "Unconfirmed 123.crdownload" until the very end, so then it's
    /// the file that just appeared in the folder with (about) the size we saw.
    /// </summary>
    private string? FinishedFile(string partial, long bytes, string? knownFinal)
    {
        if (MovedTo(partial) is string moved) return moved;
        if (RenamedStillDownloading(partial)) return null;
        // Firefox/Zen create the final (empty) file at the start: it only counts once it has the data
        string? final = knownFinal ?? (_ffFor == partial ? _ffFinal : null);
        string direct = final ?? StripPartial(partial);
        try
        {
            var fi = new FileInfo(direct);
            if (fi.Exists && fi.Length >= bytes * 0.9) return fi.FullName;
            string? folder = Path.GetDirectoryName(partial);
            var fresh = folder == null || (CleanName(partial) != "" && final == null) ? null : new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => !PartialExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase) && !_reported.Contains(f.FullName) &&
                            (DateTime.Now - f.LastWriteTime).TotalSeconds < 15 && f.Length >= bytes * 0.9)
                .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            return fresh?.FullName ?? CreatedElsewhere(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The finished file a download's in-progress file was renamed to (it can go through a few names).</summary>
    private string? MovedTo(string partial)
    {
        string path = partial;
        for (int hop = 0; hop < 5 && _moved.TryGetValue(path, out var next); hop++)
        {
            path = next;
            if (!IsPartial(path)) return File.Exists(path) ? path : null;
        }
        return null;
    }

    /// <summary>Saved on another disk there's no rename, just a new file there: the one that appeared just now with exactly the download's size.</summary>
    private string? CreatedElsewhere(long bytes)
    {
        if (bytes <= 0) return null;
        var since = DateTime.UtcNow - RecentFileWindow;
        foreach (var (path, when) in _recent.Reverse())
        {
            if (when < since || _reported.Contains(path)) continue;
            try
            {
                var f = new FileInfo(path);
                if (f.Exists && f.Length == bytes) return f.FullName;
            }
            catch
            {
                // gone already / no access
            }
        }
        return null;
    }

    private TransferInfo? Downloads(DateTime now, double dt)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var path in PartialFiles(now))
            {
                string ext = Path.GetExtension(path);
                if (!PartialExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) continue;
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                seen.Add(path);
                if (!_files.TryGetValue(path, out var g))
                {
                    // a file that was already there when we started counts only once it grows
                    _files[path] = new Growing { Size = size, Changed = _primed ? now : DateTime.MinValue };
                    continue;
                }
                if (size != g.Size)
                {
                    double speed = Math.Max(0, size - g.Size) / dt;
                    g.Speed = g.Speed <= 0 ? speed : g.Speed * 0.75 + speed * 0.25;
                    g.Size = size;
                    g.Changed = now;
                }
                else if ((now - g.Changed).TotalSeconds > 1.5) g.Speed *= 0.8;
            }
        }
        catch
        {
            // folder missing / no access
        }
        _primed = true;
        foreach (var gone in _files.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            if (_files[gone].Changed != DateTime.MinValue) _vanished.Add((gone, _files[gone])); // (never grew while we watched: not one of ours)
            _files.Remove(gone);
        }

        // only files that grew in the last few seconds (paused or failed downloads leave their file behind)
        // (a download that stops growing stays a while: the eyes get sleepy before it's dropped)
        var active = _files.Where(f => (now - f.Value.Changed).TotalSeconds < ActiveSeconds).ToList();
        // Chrome/Edge/Opera...: each download's size is in the profile's History database
        if (active.Any(f => IsChromiumPartial(f.Key) && !_chromium.Running.ContainsKey(f.Key)))
        {
            try { _chromium.Refresh(now); } catch { }
        }
        Files = active.OrderByDescending(f => f.Value.Changed).Select(f =>
        {
            string n = CleanName(f.Key) is { Length: > 0 } c ? c : _lastName;
            long? size = null;
            if (_ffByPart.TryGetValue(f.Key, out var ff))
            {
                if (ff.Final is { Length: > 0 } final)
                {
                    n = Path.GetFileName(final);
                    f.Value.Final = final;
                }
                if (ff.Total >= f.Value.Size) size = ff.Total;
            }
            else if (_chromium.Running.TryGetValue(f.Key, out var ch))
            {
                if (ch.Target is { Length: > 0 } target)
                {
                    n = Path.GetFileName(target);
                    f.Value.Final = target;
                }
                if (ch.Total >= f.Value.Size) size = ch.Total;
            }
            return new DownloadFile(f.Key, n, f.Value.Size, f.Value.Speed, size);
        }).ToList();
        if (active.Count == 0)
        {
            _browserTotal = null;
            _probe = null;
            _pctSeen = -1;
            _estSum = _estWeight = 0;
            _exactTotal = false;
            return null;
        }

        var newest = active.OrderByDescending(f => f.Value.Changed).First();
        string name = CleanName(newest.Key);
        if (name != "") _lastName = name;
        // Chrome renames "Unconfirmed 123.crdownload" to "name.crdownload" a moment in: same download
        if (_lastPath != newest.Key && !(_lastPath != "" && CleanName(_lastPath) == "" && active.Count == 1))
        {
            _browserTotal = null;
            _pctSeen = -1;
            _estSum = _estWeight = 0;
            _exactTotal = false;
        }
        _lastPath = newest.Key;

        long bytes = active.Sum(f => f.Value.Size);
        double rate = active.Sum(f => f.Value.Speed);

        long? total = null;
        if (active.Count == 1)
        {
            if (newest.Key.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                // Firefox / Zen / LibreWolf / Floorp: the profile's downloads.json has the exact size
                ReadFirefox(now, newest.Key);
                total = _ffTotal ?? Files[0].Total;
                if (_ffFinal != null) name = Path.GetFileName(_ffFinal);
            }
            else if (Files[0].Total is long exact)
            {
                total = exact; // the browser's History database: exact
                if (Files[0].Name != "") name = Files[0].Name;
            }
            else
            {
                ReadBrowser(now, bytes);
                total = _browserTotal;
            }
            if (total < bytes) total = null;
        }
        else if (Files.All(f => f.Total != null))
        {
            // several downloads, each with a known size (Zen/Firefox): progress of all of them together
            total = Files.Sum(f => f.Total!.Value);
        }
        double? progress = total is long t && t > 0 ? Math.Clamp((double)bytes / t, 0, 0.999) : null;
        TimeSpan? eta = total is long tt && rate > 1024 ? TimeSpan.FromSeconds((tt - bytes) / rate) : null;
        return new TransferInfo(TransferKind.Download, name == "" ? _lastName : name, active.Count, progress, bytes, total,
            rate > 0 ? rate : null, eta)
        {
            PartialPath = newest.Key,
            Browser = BrowserOf(newest.Key)
        };
    }

    // ------------------------------------------------------------------ where downloads are

    private static readonly string[] ChromiumPartials = { ".crdownload", ".opdownload" };
    private readonly ChromiumHistory _chromium = new();

    private static bool IsChromiumPartial(string path) =>
        ChromiumPartials.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsPartial(string path)
    {
        string ext = Path.GetExtension(path);
        return PartialExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The in-progress download files: Downloads folder + Zen/Firefox's list + what Windows told us about.</summary>
    private List<string> PartialFiles(DateTime now)
    {
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_downloads != null)
        {
            try
            {
                foreach (var p in Directory.EnumerateFiles(_downloads))
                    if (IsPartial(p)) list.Add(p);
            }
            catch
            {
                // folder missing / no access
            }
        }

        SpotFirefox(now);
        foreach (var p in _ffActive)
            if (File.Exists(p)) list.Add(p);

        UpdateDiskWatch(now);
        foreach (var p in _spotted.Keys)
        {
            if (File.Exists(p)) list.Add(p);
            else _spotted.TryRemove(p, out _);
        }
        return list.ToList();
    }

    // ---- Zen / Firefox: their downloads.json says where each running download is being written

    private sealed record FfDownload(string Part, long? Total, string? Final);

    private readonly Dictionary<string, (DateTime stamp, List<FfDownload> parts)> _ffLists = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _ffActive = new();
    /// <summary>Each running Zen/Firefox download's size and final file, by its .part path.</summary>
    private Dictionary<string, FfDownload> _ffByPart = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-reads a profile's downloads.json only when the browser rewrote it (a quick date check otherwise).</summary>
    private void SpotFirefox(DateTime now)
    {
        if (_ffFiles == null || now - _ffFilesAt > TimeSpan.FromMinutes(1))
        {
            _ffFilesAt = now;
            _ffFiles = FirefoxProfiles().Select(p => Path.Combine(p, "downloads.json")).ToList();
        }
        bool changed = false;
        foreach (var file in _ffFiles)
        {
            DateTime stamp;
            try
            {
                if (!File.Exists(file)) continue;
                stamp = File.GetLastWriteTimeUtc(file);
            }
            catch
            {
                continue;
            }
            if (_ffLists.TryGetValue(file, out var known) && known.stamp == stamp) continue;
            var parts = new List<FfDownload>();
            try
            {
                string json;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    json = sr.ReadToEnd();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("list", out var items))
                    foreach (var d in items.EnumerateArray())
                        if (d.TryGetProperty("target", out var target) && target.ValueKind == System.Text.Json.JsonValueKind.Object &&
                            target.TryGetProperty("partFilePath", out var part) && part.ValueKind == System.Text.Json.JsonValueKind.String &&
                            part.GetString() is { Length: > 0 } path)
                        {
                            long? total = d.TryGetProperty("totalBytes", out var tb) && tb.TryGetInt64(out long n) && n > 0 ? n : null;
                            string? final = target.TryGetProperty("path", out var fp) && fp.ValueKind == System.Text.Json.JsonValueKind.String ? fp.GetString() : null;
                            parts.Add(new FfDownload(path, total, final));
                        }
            }
            catch
            {
                continue; // being written right now: next time (the stamp isn't stored, so it's read again)
            }
            _ffLists[file] = (stamp, parts);
            changed = true;
        }
        if (changed)
        {
            var all = _ffLists.Values.SelectMany(v => v.parts).ToList();
            _ffActive = all.Select(p => p.Part).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var byPart = new Dictionary<string, FfDownload>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all)
                if (!byPart.TryGetValue(p.Part, out var had) || had.Total == null) byPart[p.Part] = p;
            _ffByPart = byPart;
        }
    }

    // ---- Chromium browsers: Windows tells us when a .crdownload / .opdownload file is created on any disk

    private readonly ConcurrentDictionary<string, byte> _spotted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _diskWatchers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _diskCheckAt;

    /// <summary>Every few seconds: is a Chromium browser open? Then watch every disk (new ones too); else watch nothing.</summary>
    private void UpdateDiskWatch(DateTime now)
    {
        if (now < _diskCheckAt) return;
        _diskCheckAt = now.AddSeconds(3);

        bool browserOpen = false;
        foreach (var proc in Process.GetProcesses())
        {
            if (!browserOpen && (Browsers.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                                 FirefoxProcesses.Contains(proc.ProcessName, StringComparer.OrdinalIgnoreCase))) browserOpen = true;
            proc.Dispose();
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (browserOpen)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType is DriveType.Fixed or DriveType.Removable && drive.IsReady) roots.Add(drive.RootDirectory.FullName);
                }
                catch
                {
                    // drive just went away
                }
            }
        }

        foreach (var gone in _diskWatchers.Keys.Where(r => !roots.Contains(r)).ToList())
        {
            _diskWatchers[gone].Dispose();
            _diskWatchers.Remove(gone);
        }
        foreach (var root in roots)
        {
            if (_diskWatchers.ContainsKey(root)) continue;
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName, // creations, renames, deletions: not every write
                    InternalBufferSize = 64 * 1024
                };
                w.Created += (_, e) =>
                {
                    Spot(e.FullPath);
                    RememberNew(e.FullPath);
                };
                w.Renamed += (_, e) =>
                {
                    Spot(e.FullPath);
                    RememberNew(e.FullPath);
                    if (IsPartial(e.OldFullPath))
                    {
                        if (_moved.Count >= MaxMoves) _moved.Clear();
                        _moved[e.OldFullPath] = e.FullPath;
                    }
                };
                w.EnableRaisingEvents = true;
                _diskWatchers[root] = w;
            }
            catch
            {
                // no access to that disk
            }
        }
    }

    private void Spot(string path)
    {
        string ext = Path.GetExtension(path);
        if (ChromiumPartials.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) _spotted[path] = 0;
    }

    /// <summary>Files that just appeared where someone would save a download (programs' own data folders are left out).</summary>
    private void RememberNew(string path)
    {
        if (IsPartial(path) || path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return;
        // (the temporary copies cloud apps make of whatever lands in a synced folder: a dot folder, no extension)
        if (path.Contains(@"\.", StringComparison.Ordinal) || Path.GetExtension(path).Length == 0) return;
        _recent.Enqueue((path, DateTime.UtcNow));
        while (_recent.Count > MaxRecentFiles) _recent.TryDequeue(out _);
    }

    // ---- Firefox family: <profile>\downloads.json lists the session's downloads with their size, written
    //      as soon as a download starts ("partFilePath" is the .part file we see growing, "totalBytes" its size)

    private static readonly string[] FirefoxRoots = { "zen", @"Mozilla\Firefox", "librewolf", "Floorp", "Waterfox", "Mercury" };
    private static readonly string[] FirefoxProcesses = { "zen", "firefox", "librewolf", "floorp", "waterfox", "mercury" };
    private string _ffBrowser = "";
    private string _probeBrowser = "";

    /// <summary>Which browser is downloading this file (for "cancel").</summary>
    private string BrowserOf(string partial)
    {
        string ext = Path.GetExtension(partial).ToLowerInvariant();
        static string FirstRunning(IEnumerable<string> names) =>
            names.FirstOrDefault(n => Process.GetProcessesByName(n).Length > 0) ?? "";
        return ext switch
        {
            ".part" => _ffBrowser != "" ? _ffBrowser : FirstRunning(FirefoxProcesses),
            ".opdownload" => "opera",
            _ => _probeBrowser != "" ? _probeBrowser : FirstRunning(new[] { "msedge", "chrome", "brave", "vivaldi" })
        };
    }
    private List<string>? _ffFiles;
    private DateTime _ffFilesAt, _ffNextRead;
    private string _ffFor = "";
    private long? _ffTotal;
    private string? _ffFinal;

    private void ReadFirefox(DateTime now, string partPath)
    {
        if (_ffFor != partPath)
        {
            _ffFor = partPath;
            _ffTotal = null;
            _ffFinal = null;
            _ffNextRead = default;
        }
        if (_ffTotal != null || now < _ffNextRead) return;
        _ffNextRead = now.AddSeconds(1.5);

        if (_ffFiles == null || now - _ffFilesAt > TimeSpan.FromMinutes(1))
        {
            _ffFilesAt = now;
            _ffFiles = FirefoxProfiles().Select(p => Path.Combine(p, "downloads.json")).ToList();
        }
        foreach (var file in _ffFiles)
        {
            try
            {
                if (!File.Exists(file) || (DateTime.Now - File.GetLastWriteTime(file)).TotalHours > 6) continue;
                string json;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs))
                    json = sr.ReadToEnd();
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("list", out var list)) continue;
                foreach (var d in list.EnumerateArray())
                {
                    if (!d.TryGetProperty("target", out var target) || target.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (!target.TryGetProperty("partFilePath", out var part) || part.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    if (!string.Equals(part.GetString(), partPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (target.TryGetProperty("path", out var final) && final.ValueKind == System.Text.Json.JsonValueKind.String)
                        _ffFinal = final.GetString();
                    if (d.TryGetProperty("totalBytes", out var tb) && tb.TryGetInt64(out long total) && total > 0)
                        _ffTotal = total;
                    // which browser this profile belongs to: ...\AppData\Roaming\<root>\Profiles\...
                    for (int r = 0; r < FirefoxRoots.Length; r++)
                        if (file.Contains(Path.DirectorySeparatorChar + FirefoxRoots[r] + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            _ffBrowser = FirefoxProcesses[r];
                    return;
                }
            }
            catch
            {
                // being written right now: next time
            }
        }
    }

    private static IEnumerable<string> FirefoxProfiles()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in FirefoxRoots)
        {
            string root = Path.Combine(appData, r);
            try
            {
                string profiles = Path.Combine(root, "Profiles");
                if (Directory.Exists(profiles))
                    foreach (var d in Directory.EnumerateDirectories(profiles)) dirs.Add(d);
                // profiles kept somewhere else
                string ini = Path.Combine(root, "profiles.ini");
                if (!File.Exists(ini)) continue;
                bool relative = true;
                foreach (var line in File.ReadAllLines(ini))
                {
                    if (line.StartsWith("[")) relative = true;
                    else if (line.StartsWith("IsRelative=")) relative = line.EndsWith("1");
                    else if (line.StartsWith("Path=") && !relative)
                    {
                        string p = line[5..].Trim();
                        if (Directory.Exists(p)) dirs.Add(p);
                    }
                }
            }
            catch
            {
                // no access
            }
        }
        return dirs;
    }

    // ---- the browser's own download UI (UI Automation, only the browser's toolbar/popups: never page content)

    private long? _browserTotal;
    private double _pctSeen = -1, _estSum, _estWeight;
    private bool _exactTotal;
    private List<AutomationElement>? _probe;   // elements that showed download progress, re-read each poll
    private DateTime _nextSearch;

    private void ReadBrowser(DateTime now, long bytes)
    {
        if (_probe is { Count: > 0 })
        {
            bool alive = false;
            foreach (var el in _probe)
            {
                try
                {
                    alive |= Learn(el.Current.Name, bytes);
                }
                catch
                {
                    // element went away (popup closed)
                }
            }
            if (alive) return;
            _probe = null;
        }
        if (now < _nextSearch) return;
        _nextSearch = now.AddSeconds(_browserTotal == null ? 1.5 : 4);
        var found = new List<AutomationElement>();
        try
        {
            var pids = new HashSet<int>(Browsers.SelectMany(n => Process.GetProcessesByName(n)).Select(p => p.Id));
            if (pids.Count == 0) return;
            var walker = TreeWalker.ControlViewWalker;
            for (var w = walker.GetFirstChild(AutomationElement.RootElement); w != null; w = walker.GetNextSibling(w))
            {
                if (!pids.Contains(w.Current.ProcessId)) continue;
                int before = found.Count;
                Search(walker, w, 0, false, found);
                if (found.Count > before)
                    try { _probeBrowser = Process.GetProcessById(w.Current.ProcessId).ProcessName.ToLowerInvariant(); } catch { }
            }
        }
        catch
        {
            // browser busy / closing
        }
        foreach (var el in found)
        {
            try { Learn(el.Current.Name, bytes); } catch { }
        }
        _probe = found.Count > 0 ? found : null;
    }

    /// <summary>Walks the browser window (skipping web pages) for download elements that mention progress.</summary>
    private static void Search(TreeWalker walker, AutomationElement e, int depth, bool inDownload, List<AutomationElement> found)
    {
        if (depth > 14 || found.Count > 6) return;
        for (var c = walker.GetFirstChild(e); c != null; c = walker.GetNextSibling(c))
        {
            var info = c.Current;
            if (info.ControlType == ControlType.Document) continue; // page content: never read
            bool dl = inDownload || info.ClassName.Contains("Download", StringComparison.OrdinalIgnoreCase);
            if (dl && (Percent.IsMatch(info.Name) || SizeOfSize.IsMatch(info.Name))) found.Add(c);
            Search(walker, c, depth + 1, dl, found);
        }
    }

    /// <summary>Learns the total from a text like "11%" or "7,1 MB de 60,0 MB". True if it said anything.</summary>
    private bool Learn(string text, long bytes)
    {
        var m = SizeOfSize.Match(text);
        if (m.Success && ParseSize(m.Groups[3].Value, m.Groups[4].Value) is long total && total > 0)
        {
            _browserTotal = total;
            _exactTotal = true;
            return true;
        }
        var p = Percent.Match(text);
        if (_exactTotal) return p.Success;
        if (p.Success && ParseNumber(p.Groups[1].Value) is double pct && pct >= 1 && pct < 100 && bytes > 0)
        {
            // The browser shows a whole percentage that lags the file a little. The moment it ticks up to n%,
            // the download is right at n%: those moments give the best estimates. Average them, trusting
            // higher percentages more, so the total stays steady instead of jumping with every reading.
            if (pct != _pctSeen)
            {
                bool tick = _pctSeen >= 0 && pct > _pctSeen;
                double at = tick ? pct : pct + 0.5;
                double estimate = bytes / (at / 100);
                double weight = tick ? pct : pct * 0.3;
                _estSum += estimate * weight;
                _estWeight += weight;
                _pctSeen = pct;
                _browserTotal = (long)(_estSum / _estWeight);
            }
            return true;
        }
        return false;
    }

    /// <summary>"relatorio.pdf.crdownload" → "relatorio.pdf"; Chrome's temporary "Unconfirmed 123.crdownload" → "".</summary>
    private static string CleanName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            name = Regex.Replace(name, @"\.[A-Za-z0-9]{8}(?=\.[^.]+$)", "");
        if (Regex.IsMatch(name, @"^(Unconfirmed|Não confirmado|No confirmado|Non confermato|Nicht bestätigt|Non confirmé)\s+\d+$", RegexOptions.IgnoreCase))
            return "";
        return name;
    }

    private static string StripPartial(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));

    private static string? DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                string s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (Directory.Exists(s)) return s;
            }
        }
        catch
        {
            // fall back below
        }
        string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    // ------------------------------------------------------------------ Explorer copy / move

    private IntPtr _copyWindow;
    private readonly Queue<(DateTime t, double p)> _copyHistory = new();
    private string _copyName = "";
    private double? _copySpeed;
    private DateTime _copyDetailsAt;

    private TransferInfo? ExplorerCopy(DateTime now)
    {
        double best = -1;
        int count = 0;
        IntPtr bestWindow = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "OperationStatusWindow") return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            var m = Percent.Match(title.ToString());
            if (!m.Success || ParseNumber(m.Groups[1].Value) is not double pct) return true;
            double p = Math.Clamp(pct / 100.0, 0, 1);
            count++;
            if (best < 0 || p < best) // several dialogs: show the one furthest from done
            {
                best = p;
                bestWindow = h;
            }
            return true;
        }, IntPtr.Zero);

        if (count == 0)
        {
            _copyWindow = IntPtr.Zero;
            _copyHistory.Clear();
            _copyName = "";
            _copySpeed = null;
            return null;
        }
        if (bestWindow != _copyWindow)
        {
            _copyWindow = bestWindow;
            _copyHistory.Clear();
            _copyName = "";
            _copySpeed = null;
        }

        // time left from how fast the percentage moves (last ~6 s)
        _copyHistory.Enqueue((now, best));
        while (_copyHistory.Count > 2 && (now - _copyHistory.Peek().t).TotalSeconds > 6) _copyHistory.Dequeue();
        TimeSpan? eta = null;
        var first = _copyHistory.Peek();
        double span = (now - first.t).TotalSeconds, moved = best - first.p;
        if (span > 1.2 && moved > 0.002) eta = TimeSpan.FromSeconds((1 - best) * span / moved);

        if (now >= _copyDetailsAt)
        {
            _copyDetailsAt = now.AddSeconds(1);
            ReadCopyDetails(bestWindow);
        }
        return new TransferInfo(TransferKind.Copy, _copyName, count, best, 0, null, _copySpeed, eta);
    }

    /// <summary>The dialog's "more details" view: current file name and speed ("Velocidade: 3,23 GB por segundo").</summary>
    private void ReadCopyDetails(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            var name = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "eltItemName"));
            if (name != null) _copyName = name.Current.Name ?? "";
            var chart = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "eltRateChart_New"));
            string text = chart?.Current.Name ?? "";
            int colon = text.LastIndexOf(':');
            var m = Size.Match(colon >= 0 ? text[(colon + 1)..] : "");
            _copySpeed = m.Success ? ParseSize(m.Groups[1].Value, m.Groups[2].Value) : null;
        }
        catch
        {
            // dialog closing
        }
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>"3,23" / "3.23" / "1.024,5" → number (the browser/Explorer use the system's language).</summary>
    private static double? ParseNumber(string s)
    {
        s = s.Trim();
        int lastComma = s.LastIndexOf(','), lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            s = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (lastComma >= 0) s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static long? ParseSize(string number, string unit)
    {
        if (ParseNumber(number) is not double v) return null;
        double mult = unit.ToUpperInvariant() switch
        {
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1
        };
        return (long)(v * mult);
    }

    // ------------------------------------------------------------------ native

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
