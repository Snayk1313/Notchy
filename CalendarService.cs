using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Events.Where(e => e.OccursOn(now) && now < e.End(now)).OrderBy(e => e.SortMinutes).FirstOrDefault();

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
        foreach (var e in Events.ToList())
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

    // ---- file

    public static CalendarService Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<CalendarService>(File.ReadAllText(FilePath)) ?? new CalendarService();
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }
}
