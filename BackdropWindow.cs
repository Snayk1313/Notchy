using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using Windows.UI.Composition.Desktop;

namespace Notchy;

/// <summary>
/// Native (non-WPF) click-through window right below the island. It hosts Windows.UI.Composition
/// visuals: the system host backdrop brush — the same blurred, saturated background the Start menu's
/// acrylic uses — clipped to the island's exact rounded shape, plus a soft shadow around it.
/// It is created directly with WS_EX_LAYERED | WS_EX_TRANSPARENT so every click passes through
/// (WPF would strip WS_EX_LAYERED from a normal window, which made it swallow clicks).
/// </summary>
public sealed class BackdropWindow
{
    public IntPtr Hwnd { get; private set; }
    private DesktopWindowTarget? _target;
    private BackdropShape? _island, _pod;
    private bool _blur = true, _shadow = true;
    private int _amount = 100, _tint = 30;
    private Vector2 _hostSize;
    private Int32Rect _bounds = new(-1, -1, -1, -1);

    // ---- Win32 window plumbing
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    private static readonly WndProc Proc = WindowProc; // keep the delegate alive
    private static bool _classRegistered;
    private const string ClassName = "NotchyBackdrop";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x0084 /* WM_NCHITTEST */) return new IntPtr(-1); // HTTRANSPARENT
        if (msg == 0x0021 /* WM_MOUSEACTIVATE */) return new IntPtr(3); // MA_NOACTIVATE
        return DefWindowProc(hwnd, msg, w, l);
    }

    public BackdropWindow()
    {
        IntPtr inst = GetModuleHandle(null);
        if (!_classRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = inst,
                lpszClassName = ClassName
            };
            RegisterClassEx(ref wc);
            _classRegistered = true;
        }

        const int exStyle = 0x80000 /* LAYERED */ | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */ |
                            0x08000000 /* NOACTIVATE */ | 0x8 /* TOPMOST */ | 0x00200000 /* NOREDIRECTIONBITMAP */;
        Hwnd = CreateWindowEx(exStyle, ClassName, "Notchy backdrop", 0x80000000 /* WS_POPUP */,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) return;

        Native.UseHostBackdrop(Hwnd);
        try
        {
            _target = CompositionHost.CreateTarget(Hwnd, false);
            var root = CompositionHost.Compositor.CreateContainerVisual();
            _target.Root = root;
            _hostSize = new Vector2(1, 1);
            _island = new BackdropShape(root, _hostSize);
            _pod = new BackdropShape(root, _hostSize);
            Apply();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        ShowWindow(Hwnd, 4 /* SW_SHOWNOACTIVATE */);
    }

    public void Close()
    {
        _target?.Dispose();
        _target = null;
        if (Hwnd != IntPtr.Zero) DestroyWindow(Hwnd);
        Hwnd = IntPtr.Zero;
    }

    /// <summary>Same position/size as the island window (device pixels), kept right below it.</summary>
    public void SetBounds(Int32Rect px, IntPtr islandHwnd)
    {
        if (Hwnd == IntPtr.Zero || px.Equals(_bounds)) return;
        _bounds = px;
        Native.MoveBelow(Hwnd, islandHwnd, px.X, px.Y, px.Width, px.Height);
        _hostSize = new Vector2(px.Width, px.Height);
        _island?.SetHostSize(_hostSize);
        _pod?.SetHostSize(_hostSize);
    }

    public void KeepBelow(IntPtr islandHwnd)
    {
        if (Hwnd != IntPtr.Zero && islandHwnd != IntPtr.Zero) Native.PlaceBelow(Hwnd, islandHwnd);
    }

    /// <param name="amount">0..100 blur amount (0 = clear glass).</param>
    /// <param name="tint">0..100 darkening laid over the glass.</param>
    public void Configure(bool blur, int amount, int tint, bool shadow)
    {
        _blur = blur;
        _shadow = shadow;
        _amount = Math.Clamp(amount, 0, 100);
        _tint = Math.Clamp(tint, 0, 100);
        Apply();
    }

    private void Apply()
    {
        if (_island == null || _pod == null) return;
        byte alpha = (byte)Math.Round(_tint / 100.0 * 0.85 * 255);
        var tint = Windows.UI.Color.FromArgb(_blur ? alpha : (byte)0, 0x14, 0x14, 0x18);
        // Mixing sharp + blurred only *looks* blurred near full opacity, so ease the slider
        // in: the effect grows evenly across 0..100 instead of all at the top end.
        float a = _amount / 100f;
        foreach (var s in new[] { _island, _pod })
        {
            s.SetBlurAmount(_blur ? 1f - MathF.Pow(1f - a, 3.2f) : 0f);
            s.SetTint(tint);
            s.SetShadow(_shadow, 0.55f);
        }
    }

    /// <summary>Shapes in window pixels. A shape with width 0 is hidden.</summary>
    public void Update(Rect island, double islandRadius, Rect pod, double podRadius, double podOpacity)
    {
        if (_island == null || _pod == null) return;
        Set(_island, island, islandRadius, 1);
        Set(_pod, pod, podRadius, podOpacity);
    }

    private static void Set(BackdropShape s, Rect r, double radius, double opacity)
    {
        bool visible = !r.IsEmpty && r.Width > 1 && r.Height > 1 && opacity > 0.02;
        s.Visible = visible;
        if (!visible) return;
        s.Opacity = (float)opacity;
        s.Set((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, (float)radius);
    }
}
