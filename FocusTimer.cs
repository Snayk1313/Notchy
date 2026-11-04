using System;

namespace Notchy;

public enum FocusMode { Focus, Break }

/// <summary>Simple Pomodoro timer. Call <see cref="Tick"/> regularly (UI frame loop).</summary>
public sealed class FocusTimer
{
    private DateTime _endsAt;
    private TimeSpan _remaining;

    public FocusMode Mode { get; private set; } = FocusMode.Focus;
    public bool Running { get; private set; }
    public bool Started { get; private set; }
    public int CompletedToday { get; private set; }
    private DateTime _completedDay = DateTime.Today;

    public event Action<FocusMode>? Finished;

    public FocusTimer() => _remaining = Total;

    public TimeSpan Total => TimeSpan.FromMinutes(Mode == FocusMode.Focus
        ? Math.Clamp(AppSettings.Current.FocusMinutes, 1, 180)
        : Math.Clamp(AppSettings.Current.BreakMinutes, 1, 60));

    public TimeSpan Remaining => Running ? Max0(_endsAt - DateTime.Now) : _remaining;

    public double Progress => Total.TotalSeconds <= 0 ? 0 : 1 - Remaining.TotalSeconds / Total.TotalSeconds;

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
        Running = true;
        Started = true;
    }

    public void Pause()
    {
        if (!Running) return;
        _remaining = Max0(_endsAt - DateTime.Now);
        Running = false;
    }

    public void Reset()
    {
        Running = false;
        Started = false;
        _remaining = Total;
    }

    /// <summary>Switches between focus and break.</summary>
    public void Skip()
    {
        Mode = Mode == FocusMode.Focus ? FocusMode.Break : FocusMode.Focus;
        Reset();
    }

    /// <summary>Re-reads the configured length when idle.</summary>
    public void Refresh()
    {
        if (!Started) _remaining = Total;
    }

    public void Tick()
    {
        if (!Running || DateTime.Now < _endsAt) return;
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
}
