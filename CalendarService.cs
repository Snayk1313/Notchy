using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Notchy;

public enum CalRepeat { Once, Weekly, Monthly, Yearly }
public enum CalType { Personal, Birthday, Work, Bills, Holiday }

/// <summary>Something on a day of the calendar. <see cref="Date"/> is its first day; repeats count from there.</summary>
public sealed class CalEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTime Date { get; set; }
    public bool HasTime { get; set; }
    public int Hour { get; set; }
    public int Minute { get; set; }
    public CalRepeat Repeat { get; set; }
    public CalType Type { get; set; }
    /// <summary>How long before it the notice comes (0 = at the time; on an all-day event, on the day).</summary>
    public int NoticeMinutes { get; set; }
    /// <summary>A birthday's year of birth (0 = unknown: the age isn't shown).</summary>
    public int BornYear { get; set; }

    /// <summary>The occurrence the last notice was about.</summary>
    public DateTime? NotifiedFor { get; set; }
    /// <summary>The last day this birthday had its party.</summary>
    public DateTime? PartyOn { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    public DateTime? SnoozeFor { get; set; }

    /// <summary>From a connected calendar: its id there and the occurrence (keeps the notice state across refreshes).</summary>
    public string? Uid { get; set; }
    /// <summary>The connected calendar it comes from (read-only in Notchy); null for Notchy's own.</summary>
    [JsonIgnore] public ExternalCalendar? Source { get; set; }

    public bool OccursOn(DateTime day)
    {
        day = day.Date;
        if (day < Date.Date) return false;
        int sameDay = Math.Min(Date.Day, DateTime.DaysInMonth(day.Year, day.Month));
        return Repeat switch
        {
            CalRepeat.Weekly => day.DayOfWeek == Date.DayOfWeek,
            CalRepeat.Monthly => day.Day == sameDay,
            CalRepeat.Yearly => day.Month == Date.Month && day.Day == sameDay,
            _ => day == Date.Date
        };
    }

    public DateTime Start(DateTime day) => HasTime ? day.Date.AddHours(Hour).AddMinutes(Minute) : day.Date;

    /// <summary>When it stops counting as still to come: its time, or the end of the day if it has none.</summary>
    public DateTime End(DateTime day) => HasTime ? Start(day) : day.Date.AddDays(1);

    /// <summary>Birthdays arrive at midnight; other all-day events at 9:00, so the notice doesn't go by during the night.</summary>
    public DateTime NoticeAt(DateTime day)
    {
        var at = HasTime ? Start(day) : day.Date.AddHours(Type == CalType.Birthday ? 0 : 9);
        return at.AddMinutes(-NoticeMinutes);
    }

    public int? AgeOn(DateTime day) => Type == CalType.Birthday && BornYear > 0 && day.Year > BornYear ? day.Year - BornYear : null;

    [JsonIgnore]
    public int SortMinutes => HasTime ? Hour * 60 + Minute : -1;
}

public sealed record CalFired(CalEvent Event, DateTime Day, bool Party);

/// <summary>A calendar from elsewhere (Google, Outlook, iCloud…), read through its iCal link. Its events are kept so they show offline.</summary>
public sealed class ExternalCalendar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>google, outlook, icloud, samsung or other (for the label).</summary>
    public string Provider { get; set; } = "other";
    public string Color { get; set; } = "#4F8FF7";
    public bool Notify { get; set; } = true;
    public DateTime? LastSync { get; set; }
    /// <summary>Why the last refresh failed ("" when it worked).</summary>
    public string Error { get; set; } = "";
    public List<CalEvent> Events { get; set; } = new();

    [JsonIgnore]
    public Color Tint => (Color)ColorConverter.ConvertFromString(Color);
}

/// <summary>
/// The calendar's events, kept in %LOCALAPPDATA%\Notchy\calendar.json, and Brazil's national holidays (worked out here,
/// no internet needed). Call <see cref="Tick"/> regularly; it raises <see cref="Fired"/> when a notice is due.
/// </summary>
public sealed class CalendarService
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "calendar.json");

    public List<CalEvent> Events { get; set; } = new();
    public event Action<CalFired>? Fired;

#if DEBUG
    /// <summary>Tests work on made-up events and leave the real file alone.</summary>
    [JsonIgnore] public bool DontSave { get; set; }
#endif

    /// <summary>An event's colour: its connected calendar's, or its type's.</summary>
    public static Color ColorOf(CalEvent e) => e.Source?.Tint ?? ColorOf(e.Type);

    public static Color ColorOf(CalType t) => t switch
    {
        CalType.Birthday => Color.FromRgb(0xFF, 0x7E, 0xB6),
        CalType.Work => Color.FromRgb(0x7C, 0xC8, 0xFF),
        CalType.Bills => Color.FromRgb(0xFF, 0xD3, 0x6B),
        CalType.Holiday => Color.FromRgb(0x5E, 0xD6, 0x7D),
        _ => Color.FromRgb(0xA7, 0x8B, 0xFA)
    };

    // ---- what's on a day

    public List<CalEvent> On(DateTime day)
    {
        var list = Events.Where(e => e.OccursOn(day)).ToList();
        list.AddRange(Externals.SelectMany(x => x.Events).Where(e => e.OccursOn(day)));
        if (AppSettings.Current.CalendarHolidays) list.AddRange(HolidaysOn(day));
        return list.OrderBy(e => e.SortMinutes).ToList();
    }

    /// <summary>What comes after <paramref name="from"/>: a repeating event only the next time it happens.</summary>
    public List<(CalEvent e, DateTime day)> After(DateTime from, int count)
    {
        var seen = new HashSet<Guid>();
        var result = new List<(CalEvent, DateTime)>();
        var day = from.Date.AddDays(1);
        for (int i = 0; i < 400 && result.Count < count; i++, day = day.AddDays(1))
            foreach (var e in On(day))
                if (seen.Add(e.Id) && result.Count < count) result.Add((e, day));
        return result;
    }

    /// <summary>The event of today still to come that the minimized island hints at (holidays don't count).</summary>
    public CalEvent? PendingToday(DateTime now) =>
        Events.Concat(Externals.SelectMany(x => x.Events)).Where(e => e.OccursOn(now) && now < e.End(now)).OrderBy(e => e.SortMinutes).FirstOrDefault();

    // ---- holidays

    private static List<CalEvent> HolidaysOn(DateTime day)
    {
        var list = new List<CalEvent>();
        void Add(string key) => list.Add(new CalEvent { Name = Loc.T("cal.h." + key), Date = day.Date, Type = CalType.Holiday, Repeat = CalRepeat.Once });
        switch ((day.Month, day.Day))
        {
            case (1, 1): Add("newyear"); break;
            case (4, 21): Add("tiradentes"); break;
            case (5, 1): Add("labor"); break;
            case (9, 7): Add("independence"); break;
            case (10, 12): Add("aparecida"); break;
            case (11, 2): Add("souls"); break;
            case (11, 15): Add("republic"); break;
            case (11, 20): Add("consciousness"); break;
            case (12, 25): Add("christmas"); break;
        }
        if (day.Date == Easter(day.Year).AddDays(-2)) Add("goodfriday");
        return list;
    }

    /// <summary>Easter Sunday (Meeus/Jones/Butcher).</summary>
    private static DateTime Easter(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, dayOf = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, dayOf);
    }

    // ---- changes

    public void Put(CalEvent e)
    {
        if (!Events.Contains(e)) Events.Add(e);
        Save();
    }

    public void Remove(CalEvent e)
    {
        Events.Remove(e);
        Save();
    }

    public void Snooze(CalEvent e, DateTime day, DateTime until)
    {
        e.SnoozeUntil = until;
        e.SnoozeFor = day.Date;
        Save();
    }

    // ---- notices

    public void Tick(DateTime now)
    {
        var today = now.Date;
        foreach (var e in Events.Concat(Externals.Where(x => x.Notify).SelectMany(x => x.Events)).ToList())
        {
            if (e.SnoozeUntil is DateTime s)
            {
                if (now < s) continue;
                var day = e.SnoozeFor ?? today;
                e.SnoozeUntil = null;
                e.SnoozeFor = null;
                Save();
                Fired?.Invoke(new CalFired(e, day, false));
                continue;
            }
            // a birthday has its party on the day (at midnight, or when Notchy opens if it was closed then)
            if (e.Type == CalType.Birthday && e.OccursOn(today) && e.PartyOn?.Date != today)
            {
                e.PartyOn = today;
                e.NotifiedFor = today;
                Save();
                Fired?.Invoke(new CalFired(e, today, true));
                continue;
            }
            // a notice that was missed (PC off, Notchy closed) still comes, as long as the event hasn't gone by
            int ahead = e.NoticeMinutes / 1440 + 1;
            for (int i = 0; i <= ahead; i++)
            {
                var day = today.AddDays(i);
                if (!e.OccursOn(day) || e.NotifiedFor?.Date == day || now < e.NoticeAt(day) || now >= e.End(day)) continue;
                e.NotifiedFor = day;
                Save();
                Fired?.Invoke(new CalFired(e, day, false));
                break;
            }
        }
    }

    // ---- connected calendars (read through their iCal link)

    public List<ExternalCalendar> Externals { get; set; } = new();

    /// <summary>A connected calendar was refreshed, added or removed.</summary>
    public event Action? ExternalsChanged;

    public static readonly string[] ExternalColors = { "#4F8FF7", "#F2A541", "#34C7A0", "#F06292", "#9CCC65", "#B39DDB" };
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);
    private bool _refreshing;

    /// <summary>Connects a calendar by its link; null when it worked, else what went wrong (to show).</summary>
    public async Task<string?> AddExternalAsync(string url, string provider)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url.Replace("webcal://", "https://", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return Loc.T("ext.err.link");
        if (Externals.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))) return Loc.T("ext.err.dup");
        var x = new ExternalCalendar
        {
            Url = url, Provider = provider,
            Color = ExternalColors.FirstOrDefault(c => Externals.All(e => e.Color != c)) ?? ExternalColors[Externals.Count % ExternalColors.Length]
        };
        string? error = await RefreshOneAsync(x, DateTime.Now);
        if (error != null) return error;
        Externals.Add(x);
        Save();
        ExternalsChanged?.Invoke();
        return null;
    }

    public void RemoveExternal(ExternalCalendar x)
    {
        Externals.Remove(x);
        Save();
        ExternalsChanged?.Invoke();
    }

    public void ChangedExternal(ExternalCalendar x)
    {
        Save();
        ExternalsChanged?.Invoke();
    }

    /// <summary>Reads again the connected calendars not read for a while (all of them with <paramref name="force"/>).</summary>
    public async Task RefreshExternalsAsync(bool force = false)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var now = DateTime.Now;
            bool any = false;
            foreach (var x in Externals.ToList())
            {
                if (!force && x.LastSync is DateTime last && now - last < RefreshEvery) continue;
                await RefreshOneAsync(x, now);
                any = true;
            }
            if (any)
            {
                Save();
                ExternalsChanged?.Invoke();
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Reads one calendar; keeps what's known about each occurrence (its notice) across refreshes.</summary>
    private static async Task<string?> RefreshOneAsync(ExternalCalendar x, DateTime now)
    {
        try
        {
            var (name, events) = await IcsCalendar.FetchAsync(x.Url, now.Date.AddDays(-31), now.Date.AddDays(400));
            var old = x.Events.Where(e => e.Uid != null).GroupBy(e => e.Uid!).ToDictionary(g => g.Key, g => g.First());
            foreach (var e in events)
            {
                e.Source = x;
                if (e.Uid != null && old.TryGetValue(e.Uid, out var was))
                {
                    e.NotifiedFor = was.NotifiedFor;
                    e.SnoozeUntil = was.SnoozeUntil;
                    e.SnoozeFor = was.SnoozeFor;
                }
                // seen for the first time after its notice time: no late notice for something already under way
                else if (now >= e.NoticeAt(e.Date)) e.NotifiedFor = e.Date;
            }
            x.Events = events;
            if (x.Name.Length == 0) x.Name = string.IsNullOrWhiteSpace(name) ? Loc.T("ext.p." + x.Provider) : name.Trim();
            x.LastSync = now;
            x.Error = "";
            return null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            x.Error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? Loc.T("ext.err.gone") : ex is FormatException ? Loc.T("ext.err.notcal") : Loc.T("ext.err.net");
            return x.Error;
        }
    }

    // ---- file

    public static CalendarService Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var c = JsonSerializer.Deserialize<CalendarService>(File.ReadAllText(FilePath)) ?? new CalendarService();
                foreach (var x in c.Externals) foreach (var e in x.Events) e.Source = x;
                return c;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new CalendarService();
    }

    public void Save()
    {
#if DEBUG
        if (DontSave) return;
#endif
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            SafeFile.Write(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
