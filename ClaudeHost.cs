using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Notchy;

/// <summary>Where a Claude Code session lives: the window of the app or terminal that runs it.</summary>
public sealed record ClaudeHostInfo(int Pid, long Hwnd, string Exe);

/// <summary>
/// Finds the window that hosts a Claude Code session (run from the hook process: its ancestors are the
/// shell, the claude process and, further up, the terminal / VS Code / Claude app), and brings it back.
/// </summary>
public static class ClaudeHost
{
    // ------------------------------------------------------------------ finding it (hook side)

    /// <summary>Walks up from this process to the first ancestor that owns a visible window.</summary>
    public static ClaudeHostInfo? Find()
    {
        try
        {
            var parents = ProcessParents();
            var windows = TopWindowsByPid();
            int pid = Environment.ProcessId;
            for (int depth = 0; depth < 16; depth++)
            {
                if (!parents.TryGetValue(pid, out var info) || info.parent <= 4 || info.parent == pid) break;
                pid = info.parent;
                if (!parents.TryGetValue(pid, out var me)) break;
                if (me.exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) break; // went past the host
                if (windows.TryGetValue(pid, out var hwnd)) return new ClaudeHostInfo(pid, hwnd.ToInt64(), ExePath(pid) ?? me.exe);
                // a classic console window belongs to conhost, not to the shell: ask the console instead
                var console = ConsoleWindowOf(pid);
                if (console != IntPtr.Zero) return new ClaudeHostInfo(pid, console.ToInt64(), ExePath(pid) ?? me.exe);
            }
        }
        catch
        {
            // best effort
        }
        return null;
    }

    private static Dictionary<int, (int parent, string exe)> ProcessParents()
    {
        var map = new Dictionary<int, (int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref e)) return map;
            do map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile);
            while (Process32Next(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>The main (visible, unowned, titled) top-level window of each process.</summary>
    private static Dictionary<int, IntPtr> TopWindowsByPid()
    {
        var map = new Dictionary<int, IntPtr>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            map.TryAdd((int)pid, h);
            return true;
        }, IntPtr.Zero);
        return map;
    }

    private static IntPtr ConsoleWindowOf(int pid)
    {
        try
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return IntPtr.Zero;
            IntPtr w = GetConsoleWindow();
            FreeConsole();
            // Windows Terminal gives a hidden stand-in window: keep walking up to the terminal itself
            return w != IntPtr.Zero && IsWindowVisible(w) ? w : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static string? ExePath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ going back to it (island side)

    public enum OpenResult { Focused, Launched, NotFound }

    /// <summary>
    /// Brings the session's window to the front; if it's gone, opens the project again: the Claude app or
    /// VS Code as they were, or a terminal resuming the conversation (only when Claude isn't still working).
    /// </summary>
    public static OpenResult Open(ClaudeSession s)
    {
        var host = s.Host;
        if (host != null && host.Hwnd != 0)
        {
            var hwnd = new IntPtr(host.Hwnd);
            if (IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == host.Pid)
            {
                Focus(hwnd);
                return OpenResult.Focused;
            }
        }
        if (s.IsWorking) return OpenResult.NotFound; // a second copy of a running session would collide

        string name = host == null ? "" : Path.GetFileNameWithoutExtension(host.Exe).ToLowerInvariant();
        try
        {
            if (host != null && File.Exists(host.Exe) && name is "code" or "code - insiders" or "cursor" or "windsurf")
            {
                Process.Start(new ProcessStartInfo(host.Exe) { UseShellExecute = true, ArgumentList = { s.Cwd } });
                return OpenResult.Launched;
            }
            if (host != null && File.Exists(host.Exe) && name == "claude" && !host.Exe.Contains(@"\.local\", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(host.Exe) { UseShellExecute = true }); // the Claude app
                return OpenResult.Launched;
            }
            return ResumeInTerminal(s) ? OpenResult.Launched : OpenResult.NotFound;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return OpenResult.NotFound;
        }
    }

    private static bool ResumeInTerminal(ClaudeSession s)
    {
        string? claude = ClaudeRunner.FindClaude();
        if (claude == null || string.IsNullOrEmpty(s.Cwd) || !Directory.Exists(s.Cwd)) return false;
        bool resume = s.Id is { Length: > 8 } && s.Id != "default" && s.Id != "demo";
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var psi = File.Exists(wt) ? new ProcessStartInfo(wt) : new ProcessStartInfo("cmd.exe");
        psi.UseShellExecute = false;
        psi.WorkingDirectory = s.Cwd;
        if (File.Exists(wt))
        {
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(s.Cwd);
        }
        else psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add(claude);
        if (resume)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(s.Id);
        }
        Process.Start(psi);
        return true;
    }

    /// <summary>
    /// SetForegroundWindow is refused to a window that isn't in front (the island never takes focus):
    /// borrowing the foreground thread's input queue for a moment makes it allowed.
    /// </summary>
    private static void Focus(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (SetForegroundWindow(hwnd)) return;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    // ------------------------------------------------------------------ native

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
}
