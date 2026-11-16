using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Notchy;

/// <summary>
/// Sees every key before Windows does (a low-level keyboard hook): used to take Win + V from Windows, and to record a shortcut
/// (keys like Win + letter never reach a window). The handler runs on the thread that created the hook and must be quick;
/// returning true swallows the key.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private delegate IntPtr Proc(int code, IntPtr wParam, IntPtr lParam);
    private readonly Proc _proc; // (kept alive while the hook exists)
    private IntPtr _hook;
    private readonly Func<int, bool, bool> _handler;

    /// <param name="handler">(virtual key, down) → swallow it?</param>
    public KeyboardHook(Func<int, bool, bool> handler)
    {
        _handler = handler;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    public bool Active => _hook != IntPtr.Zero;

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            bool down = message is 0x100 or 0x104, up = message is 0x101 or 0x105;
            try
            {
                // (0xE8 is the key Notchy sends so letting go of Win doesn't open the Start menu: it passes through)
                if (vk != 0xE8 && (down || up) && _handler(vk, down)) return new IntPtr(1);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    // ---------------------------------------------------------------- shortcuts as text: "Ctrl+Alt+V", "Win+V"

    public const uint ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;
    private static readonly (string name, uint mod)[] Mods = { ("Win", ModWin), ("Ctrl", ModCtrl), ("Alt", ModAlt), ("Shift", ModShift) };

    /// <summary>A modifier key (either side), or 0.</summary>
    public static uint ModOf(int vk) => vk switch
    {
        0x5B or 0x5C => ModWin,
        0x11 or 0xA2 or 0xA3 => ModCtrl,
        0x12 or 0xA4 or 0xA5 => ModAlt,
        0x10 or 0xA0 or 0xA1 => ModShift,
        _ => 0
    };

    /// <summary>The saved text of a shortcut (older versions saved "WinAltV" and the like).</summary>
    public static string Normalize(string name) => name switch
    {
        "WinAltV" => "Win+Alt+V",
        "CtrlAltV" => "Ctrl+Alt+V",
        "CtrlShiftSpace" => "Ctrl+Shift+Space",
        _ => name ?? ""
    };

    public static string Format(uint mods, int vk) =>
        string.Join("+", Mods.Where(m => (mods & m.mod) != 0).Select(m => m.name).Append(KeyInterop.KeyFromVirtualKey(vk).ToString()));

    public static (uint mods, uint vk)? Parse(string name)
    {
        var parts = Normalize(name).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;
        uint mods = 0;
        foreach (var p in parts[..^1])
        {
            var m = Mods.FirstOrDefault(x => x.name.Equals(p, StringComparison.OrdinalIgnoreCase));
            if (m.mod == 0) return null;
            mods |= m.mod;
        }
        if (!Enum.TryParse<Key>(parts[^1], true, out var key)) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : (mods, (uint)vk);
    }

    /// <summary>The keys as they are shown (one chip each).</summary>
    public static string[] Labels(string name)
    {
        if (Parse(name) is not { } k) return Array.Empty<string>();
        var list = Mods.Where(m => (k.mods & m.mod) != 0).Select(m => m.name).ToList();
        list.Add(KeyLabel((int)k.vk));
        return list.ToArray();
    }

    public static string KeyLabel(int vk)
    {
        var key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            Key.Space => Loc.T("key.space"),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Escape => "Esc",
            Key.Oem3 => "'",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => key.ToString()
        };
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, Proc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
