using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Notchy;

/// <summary>
/// Reads a calendar shared as an iCal link (Google's "secret address", Outlook's published calendar, iCloud's public
/// calendar…). Read-only. Repeating events are written out one by one for the days Notchy shows (a window around
/// today), so the rest of the calendar treats them like any single event.
/// Covers what those services send: all-day and timed events, time zones, RRULE (DAILY/WEEKLY/MONTHLY/YEARLY with
/// INTERVAL, COUNT, UNTIL, BYDAY incl. "2TU"/"-1FR", BYMONTHDAY), EXDATE, moved occurrences (RECURRENCE-ID),
/// cancelled ones, and all-day events over several days.
/// </summary>
public static class IcsCalendar
{
    private static readonly HttpClient Http = FastHttp.Client(TimeSpan.FromSeconds(20));
    private const int MaxSpanDays = 60, MaxOccurrences = 50_000; // (a daily event from years ago walks every day up to today)

    /// <summary>The calendar's name (X-WR-CALNAME) and its events between the two dates.</summary>
    public static async Task<(string? name, List<CalEvent> events)> FetchAsync(string url, DateTime from, DateTime to)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url["webcal://".Length..];
        string text = await Http.GetStringAsync(url);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) throw new FormatException("not an iCal calendar");
        return Parse(text, from, to);
    }

    // ---- reading the text

    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    private static IEnumerable<string> Unfold(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) { sb.Append(line, 1, line.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static Prop? ParseLine(string line)
    {
        // NAME;PARAM=a;PARAM2="b:c":VALUE — the first colon outside quotes ends the name and parameters
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in head.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) ps[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return new Prop(head[0].ToUpperInvariant(), ps, line[(colon + 1)..]);
    }

    private static string Unescape(string s) => s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    /// <summary>A date or date-time as written, with its zone: Utc (…Z), a named zone (TZID), or floating (local).</summary>
    private sealed record Stamp(DateTime Wall, bool DateOnly, TimeZoneInfo? Zone, bool Utc)
    {
        public DateTime Local => DateOnly ? Wall
            : Utc ? DateTime.SpecifyKind(Wall, DateTimeKind.Utc).ToLocalTime()
            : Zone != null ? InZone(Wall, Zone)
            : Wall;

        private static DateTime InZone(DateTime wall, TimeZoneInfo zone)
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local); }
            catch (ArgumentException) { return InZone(wall.AddHours(1), zone); } // an hour skipped by daylight saving
        }
        public Stamp At(DateTime wall) => this with { Wall = wall };
    }

    private static Stamp? ParseStamp(Prop p) => ParseStamp(p.Value, p.Params);

    private static Stamp? ParseStamp(string value, Dictionary<string, string> ps)
    {
        value = value.Trim();
        bool utc = value.EndsWith('Z');
        if (utc) value = value[..^1];
        bool dateOnly = value.Length == 8 || ps.TryGetValue("VALUE", out var v) && v.Equals("DATE", StringComparison.OrdinalIgnoreCase);
        string format = dateOnly ? "yyyyMMdd" : value.Length == 13 ? "yyyyMMdd'T'HHmm" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(dateOnly ? value[..Math.Min(8, value.Length)] : value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return null;
        TimeZoneInfo? zone = null;
        if (!utc && !dateOnly && ps.TryGetValue("TZID", out var tzid)) zone = FindZone(tzid);
        return new Stamp(wall, dateOnly, zone, utc);
    }

    private static readonly Dictionary<string, TimeZoneInfo?> Zones = new();

    /// <summary>IANA ("America/Sao_Paulo") or Windows ("E. South America Standard Time") names; unknown → local time.</summary>
    private static TimeZoneInfo? FindZone(string id)
    {
        lock (Zones)
        {
            if (Zones.TryGetValue(id, out var known)) return known;
            TimeZoneInfo? zone = null;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
            if (zone == null && TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) try { zone = TimeZoneInfo.FindSystemTimeZoneById(win); } catch { }
            return Zones[id] = zone;
        }
    }

    // ---- events

    private sealed class Vevent
    {
        public string Uid = "", Summary = "";
        public Stamp? Start, End;
        public TimeSpan? Duration;
        public string? Rule;
        public readonly List<Stamp> Except = new();
        public Stamp? RecurrenceId;
        public bool Cancelled;
    }

    public static (string? name, List<CalEvent> events) Parse(string text, DateTime from, DateTime to)
    {
        string? name = null;
        var all = new List<Vevent>();
        Vevent? cur = null;
        int depth = 0; // inside VALARM/VTIMEZONE parts of an event, their lines aren't the event's
        foreach (var line in Unfold(text))
        {
            var p = ParseLine(line);
            if (p == null) continue;
            if (p.Name == "BEGIN")
            {
                if (p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new Vevent(); depth = 0; }
                else if (cur != null) depth++;
                continue;
            }
            if (p.Name == "END")
            {
                if (p.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && cur != null) { all.Add(cur); cur = null; }
                else if (cur != null && depth > 0) depth--;
                continue;
            }
            if (cur == null)
            {
                if (p.Name == "X-WR-CALNAME") name = Unescape(p.Value);
                continue;
            }
            if (depth > 0) continue;
            switch (p.Name)
            {
                case "UID": cur.Uid = p.Value.Trim(); break;
                case "SUMMARY": cur.Summary = Unescape(p.Value); break;
                case "DTSTART": cur.Start = ParseStamp(p); break;
                case "DTEND": cur.End = ParseStamp(p); break;
                case "DURATION": cur.Duration = ParseDuration(p.Value); break;
                case "RRULE": cur.Rule = p.Value; break;
                case "EXDATE":
                    foreach (var part in p.Value.Split(','))
                        if (ParseStamp(part, p.Params) is { } ex) cur.Except.Add(ex);
                    break;
                case "RECURRENCE-ID": cur.RecurrenceId = ParseStamp(p); break;
                case "STATUS": cur.Cancelled = p.Value.Trim().Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
            }
        }

        // a moved or cancelled occurrence of a series replaces that occurrence
        var moved = all.Where(e => e.RecurrenceId != null).ToLookup(e => e.Uid);
        var events = new List<CalEvent>();
        foreach (var e in all.Where(e => e.RecurrenceId == null && e.Start != null && !e.Cancelled))
        {
            var skip = new HashSet<DateTime>(e.Except.Select(x => x.Local));
            foreach (var m in moved[e.Uid]) skip.Add(m.RecurrenceId!.Local);
            foreach (var start in Occurrences(e, from, to))
                if (!skip.Contains(start.Local)) AddEvent(events, e, start, from, to);
        }
        foreach (var m in all.Where(e => e.RecurrenceId != null && e.Start != null && !e.Cancelled))
            AddEvent(events, m, m.Start!, from, to);
        return (name, events.OrderBy(x => x.Date).ThenBy(x => x.SortMinutes).ToList());
    }

    private static TimeSpan? ParseDuration(string v)
    {
        // P1D, PT1H30M, P1W…
        var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        var d = new TimeSpan(N(2) * 7 + N(3), N(4), N(5), N(6));
        return m.Groups[1].Value == "-" ? -d : d;
    }

    private static void AddEvent(List<CalEvent> into, Vevent e, Stamp start, DateTime from, DateTime to)
    {
        var local = start.Local;
        // all-day over several days: one per day (DTEND is the day after the last one)
        int days = 1;
        if (start.DateOnly)
        {
            var end = e.End != null ? e.End.Wall.Date : e.Duration is TimeSpan d ? start.Wall.AddDays(Math.Max(1, d.Days)) : start.Wall.AddDays(1);
            if (e.End != null && e.Start != null) end = start.Wall.Date + (e.End.Wall.Date - e.Start.Wall.Date); // (a moved/repeated one keeps its length)
            days = Math.Clamp((end - start.Wall.Date).Days, 1, MaxSpanDays);
        }
        for (int i = 0; i < days; i++)
        {
            var day = local.Date.AddDays(i);
            if (day < from.Date || day > to.Date) continue;
            into.Add(new CalEvent
            {
                Name = e.Summary.Length > 0 ? e.Summary : "—",
                Date = day,
                HasTime = !start.DateOnly,
                Hour = start.DateOnly ? 0 : local.Hour,
                Minute = start.DateOnly ? 0 : local.Minute,
                Repeat = CalRepeat.Once,
                Type = CalType.Personal,
                NoticeMinutes = start.DateOnly ? 0 : 15,
                Uid = $"{e.Uid}|{local:yyyyMMddHHmm}|{i}"
            });
        }
    }

    // ---- repeats

    private static readonly Dictionary<string, DayOfWeek> WeekDays = new()
    {
        ["SU"] = DayOfWeek.Sunday, ["MO"] = DayOfWeek.Monday, ["TU"] = DayOfWeek.Tuesday, ["WE"] = DayOfWeek.Wednesday,
        ["TH"] = DayOfWeek.Thursday, ["FR"] = DayOfWeek.Friday, ["SA"] = DayOfWeek.Saturday
    };

    /// <summary>The starts of the event (one if it doesn't repeat), as written (wall clock in its zone).</summary>
    private static IEnumerable<Stamp> Occurrences(Vevent e, DateTime from, DateTime to)
    {
        var s = e.Start!;
        if (string.IsNullOrEmpty(e.Rule))
        {
            yield return s;
            yield break;
        }
        var r = e.Rule.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0].ToUpperInvariant(), x => x[1], StringComparer.OrdinalIgnoreCase);
        string freq = r.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        int interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTime? until = r.TryGetValue("UNTIL", out var u) && ParseStamp(u, new Dictionary<string, string>()) is { } us ? (us.DateOnly ? us.Wall.Date.AddDays(1).AddTicks(-1) : us.Local) : null;
        var byDay = r.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length >= 2).ToList() : new List<string>();
        var byMonthDay = r.TryGetValue("BYMONTHDAY", out var bmd) ? bmd.Split(',').Select(x => int.TryParse(x, out var n) ? n : 0).Where(n => n != 0).ToList() : new List<int>();
        var last = to.Date.AddDays(1);

        int made = 0;
        var time = s.Wall.TimeOfDay;
        // (every loop stops past the window: a rule that never hits a day must not spin forever)
        IEnumerable<DateTime> Candidates()
        {
            var d0 = s.Wall.Date;
            switch (freq)
            {
                case "DAILY":
                    for (var d = d0; d <= last; d = d.AddDays(interval)) yield return d;
                    yield break;
                case "WEEKLY":
                {
                    var days = byDay.Where(x => WeekDays.ContainsKey(x[^2..])).Select(x => WeekDays[x[^2..]]).ToList();
                    if (days.Count == 0) days.Add(d0.DayOfWeek);
                    var weekStart = d0.AddDays(-(((int)d0.DayOfWeek + 6) % 7)); // weeks start on Monday (WKST default)
                    for (var w = weekStart; w <= last; w = w.AddDays(7 * interval))
                        for (int i = 0; i < 7; i++)
                        {
                            var d = w.AddDays(i);
                            if (d >= d0 && days.Contains(d.DayOfWeek)) yield return d;
                        }
                    yield break;
                }
                case "MONTHLY":
                    for (var m = new DateTime(d0.Year, d0.Month, 1); m <= last; m = m.AddMonths(interval))
                    {
                        foreach (var d in DaysInMonth(m, byDay, byMonthDay.Count > 0 ? byMonthDay : byDay.Count > 0 ? new List<int>() : new List<int> { d0.Day }).OrderBy(x => x))
                            if (d >= d0) yield return d;
                    }
                    yield break;
                case "YEARLY":
                    for (int y = d0.Year; y <= last.Year; y += interval)
                        if (d0.Day <= DateTime.DaysInMonth(y, d0.Month)) yield return new DateTime(y, d0.Month, d0.Day);
                    yield break;
                default:
                    yield return d0;
                    yield break;
            }
        }

        foreach (var day in Candidates())
        {
            if (day > last || made >= MaxOccurrences) yield break;
            var start = s.At(day + time);
            if (until is DateTime un && (s.DateOnly ? start.Wall : start.Local) > un) yield break;
            if (count is int cnt && made >= cnt) yield break;
            made++;
            yield return start;
        }
    }

    /// <summary>The days of a month a MONTHLY rule picks: "2TU", "-1FR", "MO" (every Monday), or day numbers (-1 = last).</summary>
    private static IEnumerable<DateTime> DaysInMonth(DateTime month, List<string> byDay, List<int> byMonthDay)
    {
        int len = DateTime.DaysInMonth(month.Year, month.Month);
        foreach (int n in byMonthDay)
        {
            int d = n > 0 ? n : len + n + 1;
            if (d >= 1 && d <= len) yield return month.AddDays(d - 1);
        }
        foreach (var x in byDay)
        {
            if (!WeekDays.TryGetValue(x[^2..], out var wd)) continue;
            var all = Enumerable.Range(0, len).Select(i => month.AddDays(i)).Where(d => d.DayOfWeek == wd).ToList();
            if (x.Length == 2) { foreach (var d in all) yield return d; continue; }
            if (!int.TryParse(x[..^2], out int nth) || nth == 0) continue;
            int idx = nth > 0 ? nth - 1 : all.Count + nth;
            if (idx >= 0 && idx < all.Count) yield return all[idx];
        }
    }
}
