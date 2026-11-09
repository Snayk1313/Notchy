using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchy;

/// <summary>A countdown of the Relógio tab. Running: <see cref="EndsAt"/> is set; paused: <see cref="Left"/> holds the time left.</summary>
public sealed class ClockTimer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public double TotalSeconds { get; set; }
    public DateTime? EndsAt { get; set; }
    public double Left { get; set; }

    [JsonIgnore] public bool Running => EndsAt != null;

    [JsonIgnore]
    public TimeSpan Remaining => EndsAt is DateTime end ? Max0(end - DateTime.Now) : TimeSpan.FromSeconds(Math.Max(0, Left));

    /// <summary>Share of the time still left (1 → 0).</summary>
    [JsonIgnore]
    public double LeftShare => TotalSeconds <= 0 ? 0 : Math.Clamp(Remaining.TotalSeconds / TotalSeconds, 0, 1);

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}

/// <summary>An alarm: a time of day, on some days of the week (none = just once).</summary>
public sealed class Alarm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Hour { get; set; } = 7;
    public int Minute { get; set; }
    /// <summary>Bit 0 = Monday … bit 6 = Sunday. 0 = once (the next time the clock gets there).</summary>
    public int Days { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime LastFired { get; set; }
    public DateTime? SnoozeUntil { get; set; }
    /// <summary>When a "once" alarm was switched on: it only rings after that.</summary>
    public DateTime ArmedAt { get; set; } = DateTime.Now;

    public static int DayBit(DayOfWeek d) => 1 << (((int)d + 6) % 7);

    public bool OnDay(DateTime day) => Days == 0 || (Days & DayBit(day.DayOfWeek)) != 0;

    /// <summary>The next time it rings (null when off).</summary>
    public DateTime? Next(DateTime now)
    {
        if (!Enabled) return null;
        if (SnoozeUntil is DateTime s && s > now) return s;
        for (int i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            var at = day.AddHours(Hour).AddMinutes(Minute);
            if (at <= now || at <= LastFired) continue;
            if (Days == 0 && at <= ArmedAt) continue;
            if (OnDay(day)) return at;
        }
        return null;
    }
}

/// <summary>
/// Timers and alarms of the Relógio tab, kept in %LOCALAPPDATA%\Notchy\clock.json (running timers carry on after a
/// restart). Call <see cref="Tick"/> regularly; it raises <see cref="TimerFinished"/> / <see cref="AlarmFired"/>.
/// </summary>
public sealed class ClockService
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "clock.json");
    public const int MaxAlarms = 12, MaxTimers = 4;

    public List<ClockTimer> Timers { get; set; } = new();
    public List<Alarm> Alarms { get; set; } = new();

    public event Action<ClockTimer>? TimerFinished;
    public event Action<Alarm>? AlarmFired;

    [JsonIgnore] public bool AnyRunning => Timers.Any(t => t.Running);

    /// <summary>The running timer that ends first.</summary>
    [JsonIgnore] public ClockTimer? Soonest => Timers.Where(t => t.Running).OrderBy(t => t.EndsAt).FirstOrDefault();

    public ClockTimer Start(double seconds, string name)
    {
        var t = new ClockTimer { Name = name.Trim(), TotalSeconds = seconds, EndsAt = DateTime.Now.AddSeconds(seconds) };
        Timers.Add(t);
        while (Timers.Count > MaxTimers) Timers.RemoveAt(0);
        Save();
        return t;
    }

    public void Pause(ClockTimer t)
    {
        if (!t.Running) return;
        t.Left = t.Remaining.TotalSeconds;
        t.EndsAt = null;
        Save();
    }

    public void Resume(ClockTimer t)
    {
        if (t.Running || t.Left <= 0) return;
        t.EndsAt = DateTime.Now.AddSeconds(t.Left);
        Save();
    }

    /// <summary>Adds (or takes away) time; a finished timer starts again with it.</summary>
    public void Add(ClockTimer t, double seconds)
    {
        double left = Math.Max(1, t.Remaining.TotalSeconds + seconds);
        t.TotalSeconds = Math.Max(t.TotalSeconds + seconds, left);
        if (t.Running || t.Left <= 0) t.EndsAt = DateTime.Now.AddSeconds(left);
        else t.Left = left;
        if (!Timers.Contains(t)) Timers.Add(t);
        Save();
    }

    public void Remove(ClockTimer t)
    {
        Timers.Remove(t);
        Save();
    }

    public void SaveAlarm(Alarm a)
    {
        a.SnoozeUntil = null;
        a.ArmedAt = DateTime.Now;
        a.LastFired = DateTime.MinValue;
        if (!Alarms.Contains(a)) Alarms.Add(a);
        Alarms.Sort((x, y) => (x.Hour * 60 + x.Minute).CompareTo(y.Hour * 60 + y.Minute));
        Save();
    }

    public void SetEnabled(Alarm a, bool on)
    {
        a.Enabled = on;
        a.SnoozeUntil = null;
        if (on) a.ArmedAt = DateTime.Now;
        Save();
    }

    public void Snooze(Alarm a, int minutes)
    {
        a.SnoozeUntil = DateTime.Now.AddMinutes(minutes);
        a.Enabled = true;
        Save();
    }

    public void RemoveAlarm(Alarm a)
    {
        Alarms.Remove(a);
        Save();
    }

    /// <summary>The next alarm to ring (and when).</summary>
    public (Alarm alarm, DateTime at)? NextAlarm()
    {
        var now = DateTime.Now;
        (Alarm, DateTime)? best = null;
        foreach (var a in Alarms)
            if (a.Next(now) is DateTime at && (best == null || at < best.Value.Item2)) best = (a, at);
        return best;
    }

    public void Tick()
    {
        var now = DateTime.Now;
        foreach (var t in Timers.ToList())
        {
            if (t.EndsAt is not DateTime end || now < end) continue;
            t.EndsAt = null;
            t.Left = 0;
            Timers.Remove(t);
            Save();
            TimerFinished?.Invoke(t);
        }

        foreach (var a in Alarms.ToList())
        {
            if (!a.Enabled) continue;
            if (a.SnoozeUntil is DateTime s)
            {
                if (now < s) continue;
                a.SnoozeUntil = null;
                Fire(a, now);
                continue;
            }
            // due a moment ago (up to 2 minutes late, e.g. the PC was busy): ring; older than that: let it go
            var due = now.Date.AddHours(a.Hour).AddMinutes(a.Minute);
            if (now < due || now - due > TimeSpan.FromMinutes(2) || a.LastFired >= due || !a.OnDay(due)) continue;
            if (a.Days == 0 && due <= a.ArmedAt) continue;
            Fire(a, now);
        }
    }

    private void Fire(Alarm a, DateTime now)
    {
        a.LastFired = now;
        if (a.Days == 0) a.Enabled = false; // "once" turns itself off
        Save();
        AlarmFired?.Invoke(a);
    }

    public static ClockService Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ClockService>(File.ReadAllText(FilePath)) ?? new ClockService();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return new ClockService();
    }

    public void Save()
    {
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
