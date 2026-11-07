using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Notchy;

public enum FocusMode { Focus, Break }

/// <summary>Simple Pomodoro timer. Call <see cref="Tick"/> regularly (UI frame loop).</summary>
public sealed class FocusTimer
{
    private DateTime _endsAt;
    private TimeSpan _remaining;
    private DateTime _segmentStart; // when the current stretch of focusing began (counted when it stops)

    public FocusMode Mode { get; private set; } = FocusMode.Focus;
    public bool Running { get; private set; }
    public bool Started { get; private set; }
    public int CompletedToday { get; private set; }
    private DateTime _completedDay = DateTime.Today;

    public FocusStats Stats { get; } = FocusStats.Load();

    public event Action<FocusMode>? Finished;

    public FocusTimer() => _remaining = Total;

    public TimeSpan Total => TimeSpan.FromMinutes(Mode == FocusMode.Focus
        ? Math.Clamp(AppSettings.Current.FocusMinutes, 1, 180)
        : Math.Clamp(AppSettings.Current.BreakMinutes, 1, 60));

    public TimeSpan Remaining => Running ? Max0(_endsAt - DateTime.Now) : _remaining;

    public double Progress => Total.TotalSeconds <= 0 ? 0 : 1 - Remaining.TotalSeconds / Total.TotalSeconds;

    /// <summary>Seconds focused today, including the stretch running right now.</summary>
    public double FocusedTodaySeconds => Stats.Today + (Running && Mode == FocusMode.Focus ? (DateTime.Now - _segmentStart).TotalSeconds : 0);

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    public void Toggle()
    {
        if (Running) Pause();
        else Start();
    }

    public void Start()
    {
        if (Running) return;
        if (_remaining <= TimeSpan.Zero) _remaining = Total;
        _endsAt = DateTime.Now + _remaining;
        _segmentStart = DateTime.Now;
        Running = true;
        Started = true;
    }

    public void Pause()
    {
        if (!Running) return;
        CountSegment();
        _remaining = Max0(_endsAt - DateTime.Now);
        Running = false;
    }

    public void Reset()
    {
        CountSegment();
        Running = false;
        Started = false;
        _remaining = Total;
    }

    /// <summary>Switches between focus and break.</summary>
    public void Skip()
    {
        CountSegment();
        Running = false;
        Mode = Mode == FocusMode.Focus ? FocusMode.Break : FocusMode.Focus;
        Reset();
    }

    /// <summary>Sets how long the current kind of session lasts (only before it starts).</summary>
    public bool SetMinutes(int minutes)
    {
        if (Started) return false;
        var st = AppSettings.Current;
        if (Mode == FocusMode.Focus) st.FocusMinutes = Math.Clamp(minutes, 1, 180);
        else st.BreakMinutes = Math.Clamp(minutes, 1, 60);
        st.Save();
        _remaining = Total;
        return true;
    }

    /// <summary>Sets the break length (any time; it applies to the next break, or this one if it hasn't started).</summary>
    public void SetBreakMinutes(int minutes)
    {
        AppSettings.Current.BreakMinutes = Math.Clamp(minutes, 1, 60);
        AppSettings.Current.Save();
        if (Mode == FocusMode.Break && !Started) _remaining = Total;
    }

    /// <summary>Re-reads the configured length when idle.</summary>
    public void Refresh()
    {
        if (!Started) _remaining = Total;
    }

    public void Tick()
    {
        if (!Running || DateTime.Now < _endsAt) return;
        CountSegment(_endsAt);
        Running = false;
        Started = false;
        var finished = Mode;
        if (finished == FocusMode.Focus)
        {
            if (_completedDay != DateTime.Today)
            {
                _completedDay = DateTime.Today;
                CompletedToday = 0;
            }
            CompletedToday++;
        }
        Mode = finished == FocusMode.Focus ? FocusMode.Break : FocusMode.Focus;
        _remaining = Total;
        Finished?.Invoke(finished);
    }

    private void CountSegment(DateTime? until = null)
    {
        if (!Running || Mode != FocusMode.Focus) return;
        var end = until ?? DateTime.Now;
        if (end > _segmentStart) Stats.Add(_segmentStart, end);
        _segmentStart = end;
    }
}

/// <summary>Seconds focused per day (kept for a few weeks): today's total, the streak and this week's bars.</summary>
public sealed class FocusStats
{
    private static readonly string FilePath = Path.Combine(AppSettings.Folder, "focus-stats.json");
    public Dictionary<string, double> Days { get; set; } = new();

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    [System.Text.Json.Serialization.JsonIgnore]
    public double Today => Days.TryGetValue(Key(DateTime.Today), out var s) ? s : 0;

    public double On(DateTime day) => Days.TryGetValue(Key(day), out var s) ? s : 0;

    /// <summary>Adds a stretch of focusing (split at midnight if it crosses it).</summary>
    public void Add(DateTime from, DateTime to)
    {
        while (from.Date < to.Date)
        {
            var midnight = from.Date.AddDays(1);
            Bump(from.Date, (midnight - from).TotalSeconds);
            from = midnight;
        }
        Bump(from.Date, (to - from).TotalSeconds);
        foreach (var old in Days.Keys.Where(k => string.CompareOrdinal(k, Key(DateTime.Today.AddDays(-60))) < 0).ToList()) Days.Remove(old);
        Save();
    }

    private void Bump(DateTime day, double seconds)
    {
        if (seconds <= 0) return;
        Days[Key(day)] = On(day) + seconds;
    }

    /// <summary>Days in a row with at least a minute of focus (today counts once it has some; until then, up to yesterday).</summary>
    public int Streak(double todaySeconds)
    {
        var day = DateTime.Today;
        if (todaySeconds < 60) day = day.AddDays(-1);
        int n = 0;
        while ((day == DateTime.Today ? todaySeconds : On(day)) >= 60)
        {
            n++;
            day = day.AddDays(-1);
        }
        return n;
    }

    public static FocusStats Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<FocusStats>(File.ReadAllText(FilePath)) ?? new FocusStats();
        }
        catch
        {
            // ignore
        }
        return new FocusStats();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // ignore
        }
    }
}
