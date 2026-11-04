using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Notchy;

internal static class Native
{
    // ---------------------------------------------------------------- blur behind (DWM accent)

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    /// <summary>state 0 = off, 3 = blur behind, 4 = acrylic. tint is AABBGGRR.</summary>
    public static void SetAccent(IntPtr hwnd, int state, uint tint)
    {
        var accent = new AccentPolicy { AccentState = state, AccentFlags = state == 4 ? 2 : 0, GradientColor = tint };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttribData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    /// <summary>Lets DWM honour the alpha channel of a normal window's client area.</summary>
    public static void ExtendFrame(IntPtr hwnd)
    {
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11 rounded corners for a borderless window.</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    /// <summary>Windows 11 system backdrop: 2 = Mica, 3 = Acrylic (Start menu / flyouts), 4 = Mica Alt.</summary>
    public static void SystemBackdrop(IntPtr hwnd, int type)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref type, sizeof(int));
    }

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);

    /// <summary>Layered (alpha 255) so WS_EX_TRANSPARENT really lets clicks through.</summary>
    public static void MakeLayeredOpaque(IntPtr hwnd)
    {
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | 0x80000 /* WS_EX_LAYERED */));
        SetLayeredWindowAttributes(hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
    }

    public static IntPtr WindowAt(int x, int y) => WindowFromPoint(new POINT { X = x, Y = y });

    public static bool IsLayeredTransparent(IntPtr hwnd)
    {
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        return (ex & 0x80000) != 0 && (ex & 0x20) != 0;
    }

    /// <summary>Allows composition's HostBackdropBrush in this window.</summary>
    public static void UseHostBackdrop(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, sizeof(int));
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    /// <summary>Window region = union of rounded rects (device px). An empty list => empty region (nothing clickable).</summary>
    public static void SetRegion(IntPtr hwnd, (int x, int y, int w, int h, int r)[] shapes)
    {
        IntPtr total = CreateRectRgn(0, 0, 0, 0);
        foreach (var (x, y, w, h, r) in shapes)
        {
            if (w <= 0 || h <= 0) continue;
            IntPtr one = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2);
            CombineRgn(total, total, one, 2 /* RGN_OR */);
            DeleteObject(one);
        }
        SetWindowRgn(hwnd, total, true); // system owns the region now
    }

    public static void SetRoundRegion(IntPtr hwnd, int w, int h, int r) =>
        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2), true);

    public static void ShowNoActivate(IntPtr hwnd, bool show) => ShowWindow(hwnd, show ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);

    /// <summary>Moves/resizes a window (device pixels) and keeps it right below <paramref name="above"/>.</summary>
    public static void MoveBelow(IntPtr hwnd, IntPtr above, int x, int y, int w, int h) =>
        SetWindowPos(hwnd, above == IntPtr.Zero ? HWND_TOPMOST : above, x, y, w, h, SWP_NOACTIVATE);

    /// <summary>Lets the island take keyboard focus (to type in Notchy Chat) or go back to never stealing focus.</summary>
    public static void SetActivatable(IntPtr hwnd, bool on)
    {
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex = on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static bool IsNoActivate(IntPtr hwnd) => ((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_NOACTIVATE) != 0;

    public static void MakeClickThrough(IntPtr hwnd)
    {
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= 0x20 /* WS_EX_TRANSPARENT */ | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Puts <paramref name="hwnd"/> right below <paramref name="above"/> in z-order.</summary>
    public static void PlaceBelow(IntPtr hwnd, IntPtr above) =>
        SetWindowPos(hwnd, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    // ---------------------------------------------------------------- cursor

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);

    // ---------------------------------------------------------------- shell icons

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Large shell icon for a file or folder.</summary>
    public static System.Windows.Media.ImageSource? ShellIcon(string path)
    {
        var info = new SHFILEINFO();
        IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x100 /* SHGFI_ICON | LARGE(0) */);
        if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const long WS_EX_APPWINDOW = 0x00040000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void MakeOverlayWindow(IntPtr hwnd)
    {
        long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public static void BringToTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>True when the foreground window covers the whole primary monitor (games, videos in fullscreen).</summary>
    public static bool IsForegroundFullscreen(IntPtr own)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == own || fg == GetShellWindow() || fg == GetDesktopWindow())
            return false;

        var sb = new StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "WorkerW" or "Progman" or "Shell_TrayWnd")
            return false;

        if (!GetWindowRect(fg, out RECT r)) return false;
        IntPtr mon = MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        bool primary = (mi.dwFlags & 1) != 0;
        if (!primary) return false;

        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    public static void FocusWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }
}
