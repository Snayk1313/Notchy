using System;
using System.Diagnostics;
using System.Windows.Media;

namespace Notchy;

/// <summary>
/// The frame loop, paced by the screen instead of a timer. A 16 ms DispatcherTimer really fires every
/// ~31 ms on Windows (the system timer's resolution), so everything drawn by code (outline lights, eyes,
/// bubbles, bars) moved at ~30 fps next to WPF's own animations at the monitor's rate. Now OnFrame runs on
/// rendered frames, evenly spaced, at the first rate â‰¥ 60 fps that divides the refresh rate (165 Hz â†’ 82 fps).
/// The slower work (polls, texts) keeps its old ~32 Hz pace through <see cref="_slowTick"/>, and per-frame
/// easing goes through <see cref="Ease"/> so nothing speeds up or slows down with the frame rate.
/// </summary>
public partial class MainWindow
{
    private const double SlowStepMs = 1000.0 / 32;
    private static readonly Stopwatch AnimClock = Stopwatch.StartNew();

    /// <summary>Smooth time in seconds for code-driven animations (Environment.TickCount64 steps every ~16 ms).</summary>
    private static double AnimNow => AnimClock.Elapsed.TotalSeconds;

    private double _lastFrameMs = -1, _slowAcc, _vsyncPeriod = 1000.0 / 60;
    private TimeSpan _lastRenderingTime;
    private bool _slowTick = true;   // this frame also runs the ~32 Hz work (_tick advanced)
    private double _frameSteps = 1;  // time since the last frame, in old 32 Hz steps

    /// <summary>Per-frame easing factor: <paramref name="k"/> was tuned per 32 Hz step; scaled to this frame's length.</summary>
    private double Ease(double k) => k >= 1 ? 1 : 1 - Math.Pow(1 - k, _frameSteps);

    private void StartFrameLoop()
    {
        PaceLoop();
        // A DispatcherTimer on a window that isn't redrawing fires late (60 ms to seconds), so a plain thread
        // keeps the ~32 Hz pace and hands each tick to the UI thread (never more than one waiting).
        new System.Threading.Thread(() =>
        {
            while (true)
            {
                System.Threading.Thread.Sleep((int)SlowStepMs);
                if (System.Threading.Interlocked.Exchange(ref _tickQueued, 1) == 0)
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () => { _tickQueued = 0; FrameFallback(); });
            }
        }) { IsBackground = true, Name = "Notchy ticker" }.Start();
    }

    private int _tickQueued;

    // Following the screen keeps WPF redrawing every refresh (~8% of a core here) even when nothing moves.
    // While the island sits empty, the ~32 Hz ticker drives OnFrame instead (polls, alarms, hover still react).
    private bool _vsync;
    private double _calmSince = -1;
    private const double CalmDelayMs = 1500; // lets closing animations finish at full rate

    private void PaceLoop()
    {
        bool busy = _state != IslandState.Idle || _introActive || _hoverRequested
                    || ClaudePod.Visibility == System.Windows.Visibility.Visible || DownloadPod.Visibility == System.Windows.Visibility.Visible;
        double now = AnimClock.Elapsed.TotalMilliseconds;
        if (busy) _calmSince = -1;
        else if (_calmSince < 0) _calmSince = now;
        bool want = busy || now - _calmSince < CalmDelayMs;
        if (want == _vsync) return;
        _vsync = want;
        if (want) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can be raised more than once per frame: RenderingTime tells real frames apart
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _lastRenderingTime) return;
            double gap = (r.RenderingTime - _lastRenderingTime).TotalMilliseconds;
            _lastRenderingTime = r.RenderingTime;
            if (gap > 2 && gap < 40) _vsyncPeriod += (gap - _vsyncPeriod) * 0.05;
        }
        double now = AnimClock.Elapsed.TotalMilliseconds;
        int every = Math.Max(1, (int)Math.Floor(1000.0 / 60 / _vsyncPeriod + 0.05));
        if (_lastFrameMs >= 0 && now - _lastFrameMs < (every - 0.5) * _vsyncPeriod) return;
        OnFrame();
    }

    /// <summary>Drives the loop while the island is empty, and keeps it alive when nothing renders (screen off, locked).</summary>
    private void FrameFallback()
    {
        if (!_vsync || AnimClock.Elapsed.TotalMilliseconds - _lastFrameMs > 200) OnFrame();
    }

    private void BeginFrame()
    {
        double now = AnimClock.Elapsed.TotalMilliseconds;
        double dt = _lastFrameMs < 0 ? SlowStepMs : now - _lastFrameMs;
        _lastFrameMs = now;
        _frameSteps = Math.Clamp(dt / SlowStepMs, 0.05, 4);
        _slowAcc += dt;
        _slowTick = _slowAcc >= SlowStepMs;
        if (_slowTick)
        {
            _slowAcc = Math.Min(_slowAcc - SlowStepMs, SlowStepMs);
            _tick++;
        }
    }
}
