using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Notchy;

/// <summary>
/// Chrome / Edge / Opera / Brave / Vivaldi keep every download, the running ones too, in the profile's "History"
/// database (SQLite): the in-progress file (".crdownload"), the final file and the total size. Reading it gives each
/// download's own size, also when several run at once (the browser's UI only tells one total).
/// Read with Windows' own SQLite (winsqlite3.dll), opened read-only and "immutable" so it never locks or touches
/// the browser's file. The browser saves it every few seconds, so a new download shows up there a moment later.
/// </summary>
internal sealed class ChromiumHistory
{
    public sealed record Entry(long Total, string? Target);

    private static readonly string[] LocalRoots =
    {
        @"Microsoft\Edge\User Data", @"Google\Chrome\User Data", @"BraveSoftware\Brave-Browser\User Data",
        @"Vivaldi\User Data", @"Chromium\User Data", @"Yandex\YandexBrowser\User Data"
    };
    private static readonly string[] RoamingRoots =
    {
        @"Opera Software\Opera Stable", @"Opera Software\Opera GX Stable", @"Opera Software\Opera Air Stable"
    };

    private List<string>? _files;
    private DateTime _filesAt, _nextRead;
    private readonly Dictionary<string, DateTime> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Entry>> _byFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Running downloads by their in-progress path (".crdownload" / ".opdownload").</summary>
    public Dictionary<string, Entry> Running { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Re-reads the databases the browser saved since last time (at most every 2 s).</summary>
    public void Refresh(DateTime now)
    {
        if (now < _nextRead) return;
        _nextRead = now.AddSeconds(2);
        if (_files == null || now - _filesAt > TimeSpan.FromMinutes(1))
        {
            _filesAt = now;
            _files = HistoryFiles().ToList();
        }
        bool changed = false;
        foreach (var file in _files)
        {
            DateTime stamp;
            try
            {
                if (!File.Exists(file)) continue;
                stamp = File.GetLastWriteTimeUtc(file);
                // only profiles used lately (old ones would just cost time)
                if ((DateTime.UtcNow - stamp).TotalHours > 12) continue;
            }
            catch
            {
                continue;
            }
            if (_stamps.TryGetValue(file, out var known) && known == stamp) continue;
            if (Read(file) is not { } entries) continue; // being written right now: next time
            _stamps[file] = stamp;
            _byFile[file] = entries;
            changed = true;
        }
        if (!changed) return;
        var all = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entries in _byFile.Values)
            foreach (var (path, e) in entries)
                all[path] = e;
        Running = all;
    }

    private static IEnumerable<string> HistoryFiles()
    {
        var roots = LocalRoots.Select(r => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), r))
            .Concat(RoamingRoots.Select(r => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), r)));
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string direct = Path.Combine(root, "History"); // older Opera keeps the profile in the root
            if (File.Exists(direct)) yield return direct;
            IEnumerable<string> dirs;
            try
            {
                dirs = Directory.EnumerateDirectories(root).ToList();
            }
            catch
            {
                continue;
            }
            foreach (var dir in dirs)
            {
                string name = Path.GetFileName(dir);
                if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                string h = Path.Combine(dir, "History");
                if (File.Exists(h)) yield return h;
            }
        }
    }

    /// <summary>The in-progress downloads of one database, or null if it couldn't be read.</summary>
    private static Dictionary<string, Entry>? Read(string file)
    {
        IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            // file: URI so "immutable=1" works (no locks, no journal: the browser's file is never touched)
            string uri = "file:" + Uri.EscapeDataString(file.Replace('\\', '/')).Replace("%2F", "/").Replace("%3A", ":") + "?immutable=1";
            if (sqlite3_open_v2(Utf8(uri), out db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, IntPtr.Zero) != 0) return null;
            // state 0 = in progress
            const string sql = "SELECT current_path, target_path, total_bytes FROM downloads WHERE state = 0 AND total_bytes > 0";
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out st, IntPtr.Zero) != 0) return null;
            var list = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            int rc;
            while ((rc = sqlite3_step(st)) == SQLITE_ROW)
            {
                string? current = Marshal.PtrToStringUni(sqlite3_column_text16(st, 0));
                string? target = Marshal.PtrToStringUni(sqlite3_column_text16(st, 1));
                long total = sqlite3_column_int64(st, 2);
                if (!string.IsNullOrEmpty(current)) list[current] = new Entry(total, string.IsNullOrEmpty(target) ? null : target);
            }
            return rc == SQLITE_DONE ? list : null;
        }
        catch
        {
            return null; // no winsqlite3 (very old Windows) / unexpected file
        }
        finally
        {
            if (st != IntPtr.Zero) sqlite3_finalize(st);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    private const int SQLITE_OPEN_READONLY = 0x01, SQLITE_OPEN_URI = 0x40, SQLITE_ROW = 100, SQLITE_DONE = 101;

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern long sqlite3_column_int64(IntPtr stmt, int col);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close(IntPtr db);
}
