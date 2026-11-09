using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Notchy;

/// <summary>
/// Screen brightness, only where Windows itself controls it (laptop panels and the few monitors exposed through
/// WMI): it raises <see cref="Changed"/> when the brightness changes (keys, quick settings, the slider…). On a PC
/// without it (a desktop monitor) it stays silent and the island never shows brightness.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    private ManagementEventWatcher? _watcher;

    public bool Supported { get; private set; }

    /// <summary>Brightness 0..100.</summary>
    public event Action<int>? Changed;

    public void StartAsync() => Task.Run(() =>
    {
        try
        {
            using (var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                foreach (var _ in s.Get())
                {
                    Supported = true;
                    break;
                }
            if (!Supported) return;
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                try
                {
                    Changed?.Invoke(Convert.ToInt32(e.NewEvent["Brightness"]));
                }
                catch
                {
                    // ignore
                }
            };
            _watcher.Start();
        }
        catch
        {
            Supported = false; // no WMI brightness here (desktop monitor): nothing to show
        }
    });

    public void Set(int percent)
    {
        if (!Supported) return;
        int p = Math.Clamp(percent, 0, 100);
        Task.Run(() =>
        {
            try
            {
                using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in s.Get())
                    using (o)
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)p });
            }
            catch
            {
                // ignore
            }
        });
    }

    public void Dispose()
    {
        try
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>
/// Hides Windows' own volume/brightness pop-up while Notchy shows its own.
/// Windows 11: the pop-up is a small XAML island window of Explorer ("XamlExplorerHostIslandWindow") sitting just
/// above the taskbar; it's made click-through and moved far off screen (the technique HideVolumeOSD and
/// FluentFlyout use). Older Windows: the classic "NativeHWNDHost" window is minimized. Restore() puts it back.
/// </summary>
public static class OsdHider
{
    private static IntPtr _island, _native;
    private static int _islandExStyle;
    private static bool _islandHidden, _nativeHidden;

    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    public static void HideWindowsOsd()
    {
        if (!AppSettings.Current.HideWindowsOsd) return;
        try
        {
            var island = FindIsland();
            if (island != IntPtr.Zero)
            {
                if (!_islandHidden) _islandExStyle = GetWindowLong(island, GWL_EXSTYLE);
                SetWindowLong(island, GWL_EXSTYLE, _islandExStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
                SetWindowPos(island, IntPtr.Zero, -99999, -99999, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                ShowWindow(island, 7 /* SW_SHOWMINNOACTIVE */);
                _islandHidden = true;
            }
            var native = FindNative();
            if (native != IntPtr.Zero)
            {
                ShowWindow(native, 6 /* SW_MINIMIZE */);
                _nativeHidden = true;
            }
        }
        catch
        {
            // nothing to hide on this Windows
        }
    }

    /// <summary>Gives Windows its pop-up back (setting turned off, or Notchy closing).</summary>
    public static void Restore()
    {
        try
        {
            if (_islandHidden && IsWindow(_island))
            {
                SetWindowLong(_island, GWL_EXSTYLE, _islandExStyle);
                SetWindowPos(_island, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                ShowWindow(_island, 4 /* SW_SHOWNOACTIVATE */);
            }
            if (_nativeHidden && IsWindow(_native)) ShowWindow(_native, 9 /* SW_RESTORE */);
        }
        catch
        {
            // ignore
        }
        _islandHidden = _nativeHidden = false;
    }

    /// <summary>
    /// Explorer has several XAML island windows (taskbar parts, the desktop…); the volume pop-up's is the small one
    /// near the bottom of the main screen (or the one already moved away by us).
    /// </summary>
    private static IntPtr FindIsland()
    {
        if (_island != IntPtr.Zero && IsWindow(_island)) return _island;
        _island = IntPtr.Zero;
        _islandHidden = false;
        int screenH = GetSystemMetrics(1 /* SM_CYSCREEN */), screenW = GetSystemMetrics(0);
        foreach (var cls in new[] { "XamlExplorerHostIslandWindow", "XamlExplorerHostIslandWindow_WASDK" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                if (!GetWindowRect(h, out var r)) continue;
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                bool small = w > 40 && w < Math.Min(900, screenW / 2) && ht > 20 && ht < 220;
                // (far off screen: we moved it there before, e.g. Notchy didn't close cleanly)
                if (small && (r.Top > screenH * 0.55 || r.Left < -50000))
                {
                    _island = h;
                    return h;
                }
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindNative()
    {
        if (_native != IntPtr.Zero && IsWindow(_native)) return _native;
        _native = IntPtr.Zero;
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "NativeHWNDHost", null)) != IntPtr.Zero)
            if (FindWindowEx(h, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
            {
                _native = h;
                break;
            }
        return _native;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr h, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr h, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr h, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
