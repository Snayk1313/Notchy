using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Notchy;

public enum ClipKind { Text, Link, Color, Image, Files }

/// <summary>Something that was copied.</summary>
public sealed class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipKind Kind { get; set; }
    /// <summary>The text, link or colour (for files: the paths, one per line).</summary>
    public string Text { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Images: the pixels' hash (the same picture copied again moves up instead of repeating).</summary>
    public string Hash { get; set; } = "";
    /// <summary>The app it was copied from, as shown.</summary>
    public string App { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
    /// <summary>Images: the text read in them (null: not read yet), so a search finds them.</summary>
    public string? Ocr { get; set; }

    [JsonIgnore] public string[] Files => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    [JsonIgnore] public string ImagePath => Path.Combine(ClipboardService.ImageFolder, Id + ".png");

    private BitmapSource? _thumb;
    /// <summary>A small copy of the image, for the list (read once).</summary>
    [JsonIgnore]
    public BitmapSource? Thumb => Kind == ClipKind.Image ? _thumb ??= ClipboardService.LoadImage(ImagePath, 160) : null;
}

/// <summary>
/// Keeps what is copied in Windows (texts, links, colours, images, files): listens to the clipboard, skips what asks not to
/// be kept (password managers, "não guardar no histórico") and copies an item back on request.
/// </summary>
public sealed class ClipboardService
{
    private static string Folder => Path.Combine(AppSettings.Folder, "clipboard");
    private static string FilePath => Path.Combine(Folder, "history.json");
    public static string ImageFolder => Path.Combine(Folder, "img");
    // shortcut: very long texts are kept cut at this size, raise it if someone needs whole documents back
    private const int MaxText = 100_000;

    public List<ClipItem> Items { get; } = new();

    /// <summary>The list changed (new item, removed, pinned…).</summary>
    public event Action? Changed;
    /// <summary>Something new was copied (not a copy made from the history itself).</summary>
    public event Action<ClipItem>? Copied;
    /// <summary>The shortcut was pressed, with the window that was in front then.</summary>
    public event Action<IntPtr>? HotkeyPressed;
    /// <summary>The chosen shortcut couldn't be taken (another app uses it).</summary>
    public bool HotkeyTaken { get; private set; }

    private HwndSource? _listener;
    private DateTime _ignoreUntil;
    private static AppSettings S => AppSettings.Current;

    public void Start()
    {
        Load();
        var p = new HwndSourceParameters("NotchyClipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */, Width = 0, Height = 0, WindowStyle = 0 };
        _listener = new HwndSource(p);
        _listener.AddHook(Hook);
        AddClipboardFormatListener(_listener.Handle);
        SetHotkey(S.ClipHotkey, force: true);
    }

    private const int HotkeyId = 0x4E43;

    private KeyboardHook? _winV;
    private bool _winVDown;
    private string? _hotkeyApplied;

    /// <summary>
    /// Takes the shortcut (and lets the previous one go). Win + V is Windows' own: it can't be registered, so a keyboard hook
    /// takes it before Windows does, and only while it's the chosen shortcut (choosing another one gives it back).
    /// </summary>
    public void SetHotkey(string name, bool force = false)
    {
        if (_listener == null) return;
        string wanted = S.ClipEnabled ? KeyboardHook.Normalize(name) : "";
        if (!force && wanted == _hotkeyApplied) return;
        _hotkeyApplied = wanted;
        UnregisterHotKey(_listener.Handle, HotkeyId);
        _winV?.Dispose();
        _winV = null;
        HotkeyTaken = false;
        if (KeyboardHook.Parse(wanted) is not { } k) return;
        if (k.mods == KeyboardHook.ModWin && k.vk == 0x56)
        {
            _winV = new KeyboardHook(WinV);
            HotkeyTaken = !_winV.Active;
            return;
        }
        HotkeyTaken = !RegisterHotKey(_listener.Handle, HotkeyId, k.mods | 0x4000 /* no repeat */, k.vk);
    }

    /// <summary>Stops listening for the shortcut while a new one is recorded.</summary>
    public void PauseHotkey()
    {
        _hotkeyApplied = "";
        if (_listener != null) UnregisterHotKey(_listener.Handle, HotkeyId);
        _winV?.Dispose();
        _winV = null;
    }

    private bool _winUsed;      // Win + V happened while this Win is held
    private int _passWinUp;     // Win releases Notchy sends itself: they go through

    /// <summary>
    /// Win + V for Notchy. Apps that open on Win alone (Raycast, Flow Launcher…) open when they see Win go up with no other key
    /// in between, and they ignore keys sent by programs: so the real "Win up" is kept from them and Notchy sends one instead,
    /// which Windows takes (Win doesn't stay stuck) and those apps don't count.
    /// </summary>
    private bool WinV(int vk, bool down)
    {
        if (vk is 0x5B or 0x5C)
        {
            if (down) return false;
            if (_passWinUp > 0)
            {
                _passWinUp--;
                return false;
            }
            if (!_winUsed) return false;
            _winUsed = false;
            _passWinUp++;
            byte key = (byte)vk;
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => keybd_event(key, 0, 2, UIntPtr.Zero));
            return true;
        }
        if (vk != 0x56) return false;
        if (!down)
        {
            if (!_winVDown) return false;
            _winVDown = false;
            return true;
        }
        bool win = GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
        bool other = GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 || GetAsyncKeyState(0x10) < 0;
        if (!win || other) return false;
        if (!_winVDown)
        {
            _winVDown = true;
            _winUsed = true;
            // a key nobody uses, so letting go of Win doesn't open the Start menu
            keybd_event(0xE8, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 2, UIntPtr.Zero);
            var front = GetForegroundWindow();
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(front));
        }
        return true;
    }

    /// <summary>The control with the keyboard focus in another app (a text box…), read before the island takes the focus.</summary>
    public static Task<System.Windows.Automation.AutomationElement?> FocusedAsync() => Task.Run(() =>
    {
        try
        {
            var el = System.Windows.Automation.AutomationElement.FocusedElement;
            return el == null || el.Current.ProcessId == Environment.ProcessId ? null : el;
        }
        catch
        {
            return null;
        }
    });

    /// <summary>Back to the app that was in front, and Ctrl+V there (into <paramref name="field"/>, the control that had the focus).</summary>
    public static async Task PasteIntoAsync(IntPtr target, System.Windows.Automation.AutomationElement? field = null)
    {
        if (target == IntPtr.Zero) return;
        if (!SetForegroundWindow(target) || GetForegroundWindow() != target)
        {
            // Windows only lets the app in front hand it over: a tap of Alt unlocks it
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(target);
        }
        await Task.Delay(90);
        // Explorer doesn't give the focus back to its search box when it comes back to the front: put it back where it was
        bool refocused = field != null && await Task.Run(() =>
        {
            try
            {
                var now = System.Windows.Automation.AutomationElement.FocusedElement;
                if (now != null && System.Windows.Automation.Automation.Compare(now, field)) return false;
                field.SetFocus();
                return true;
            }
            catch
            {
                return false; // (gone, or it can't take the focus: the paste goes where the app put it)
            }
        });
        if (refocused) await Task.Delay(80);
        // (the shortcut's own keys may still be down: let them go first)
        foreach (byte vk in new byte[] { 0x5B, 0x5C, 0x12, 0x10 }) if (GetAsyncKeyState(vk) < 0) keybd_event(vk, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(0x56, 0, 0, UIntPtr.Zero);
        keybd_event(0x56, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x031D /* WM_CLIPBOARDUPDATE */) _ = CaptureAsync();
        else if (msg == 0x0312 /* WM_HOTKEY */ && w.ToInt32() == HotkeyId) HotkeyPressed?.Invoke(GetForegroundWindow());
        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- reading

    private static readonly Regex HexColor = new(@"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
    private static readonly Regex RgbColor = new(@"^rgba?\(\s*\d{1,3}\s*,\s*\d{1,3}\s*,\s*\d{1,3}\s*(?:,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase);

    public static ClipKind KindOf(string text) =>
        HexColor.IsMatch(text) || RgbColor.IsMatch(text) ? ClipKind.Color
        : !text.Contains(' ') && !text.Contains('\n') && Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? ClipKind.Link
        : ClipKind.Text;

    private async Task CaptureAsync()
    {
        if (!S.ClipEnabled || S.ClipPaused || DateTime.UtcNow < _ignoreUntil) return;
        string app = OwnerApp(out string process, out IntPtr window);
        // a browser's private window (anônima, InPrivate…): nothing from it is kept
        if (window != IntPtr.Zero && Browsers.Contains(process) && await Task.Run(() => IsPrivateWindow(window))) return;
        await Task.Delay(60); // some apps put the data in a few steps
        try
        {
            IDataObject? data = null;
            for (int i = 0; i < 5 && data == null; i++)
            {
                try { data = Clipboard.GetDataObject(); }
                catch (COMException) { await Task.Delay(50); } // another app has it open for a moment
            }
            if (data == null || !Allowed(data)) return;
            if (S.ClipIgnoredApps.Any(a => a.Equals(process, StringComparison.OrdinalIgnoreCase) || a.Equals(app, StringComparison.OrdinalIgnoreCase))) return;

            ClipItem? item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = new ClipItem { Kind = ClipKind.Files, Text = string.Join("\n", files) };
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
            {
                // (a spreadsheet range also brings a picture of itself: the text is what was meant)
                text = text.Length > MaxText ? text[..MaxText] : text;
                item = new ClipItem { Kind = KindOf(text.Trim()), Text = KindOf(text.Trim()) == ClipKind.Text ? text : text.Trim() };
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is BitmapSource image)
                item = SaveImage(image);
            if (item == null) return;
            item.App = app;
            Add(item);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Copies that ask not to be kept: password managers' flags and Windows' "não guardar no histórico".</summary>
    private static bool Allowed(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore")) return false;
        const string history = "CanIncludeInClipboardHistory";
        return !(data.GetDataPresent(history) && data.GetData(history) is MemoryStream ms && ms.Length >= 4 && BitConverter.ToInt32(ms.ToArray(), 0) == 0);
    }

    private static ClipItem? SaveImage(BitmapSource image)
    {
        var frame = BitmapFrame.Create(image);
        var item = new ClipItem { Kind = ClipKind.Image, Width = image.PixelWidth, Height = image.PixelHeight };
        Directory.CreateDirectory(ImageFolder);
        using (var ms = new MemoryStream())
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(frame);
            png.Save(ms);
            var bytes = ms.ToArray();
            item.Hash = Convert.ToHexString(SHA1.HashData(bytes));
            File.WriteAllBytes(item.ImagePath, bytes);
        }
        return item;
    }

    public static BitmapSource? LoadImage(string path, int width = 0)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(path);
            if (width > 0) b.DecodePixelWidth = width;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch
        {
            return null;
        }
    }

    private static bool Same(ClipItem a, ClipItem b) =>
        a.Kind == b.Kind && (a.Kind == ClipKind.Image ? a.Hash == b.Hash : a.Text == b.Text);

    /// <summary>A new copy: on top (the same thing copied again moves up instead of repeating), then the limits.</summary>
    public void Add(ClipItem item)
    {
        var old = Items.FirstOrDefault(x => Same(x, item));
        if (old != null)
        {
            Items.Remove(old);
            if (item.Kind == ClipKind.Image) TryDelete(item.ImagePath); // (the picture already kept is used)
            old.Time = item.Time;
            old.App = item.App;
            item = old;
        }
        Items.Insert(0, item);
        Trim();
        Save();
        Changed?.Invoke();
        Copied?.Invoke(item);
        if (item.Kind == ClipKind.Image && item.Ocr == null) _ = ReadTextAsync(item);
    }

    /// <summary>Over the limits (count, days): the oldest go, except the pinned ones.</summary>
    private void Trim()
    {
        var cutoff = DateTime.Now.AddDays(-Math.Max(1, S.ClipMaxDays));
        int kept = 0;
        foreach (var x in Items.ToList())
        {
            if (x.Pinned) continue;
            if (++kept > Math.Max(10, S.ClipMaxItems) || x.Time < cutoff) Forget(x);
        }
    }

    private void Forget(ClipItem x)
    {
        Items.Remove(x);
        if (x.Kind == ClipKind.Image) TryDelete(x.ImagePath);
    }

    // ---------------------------------------------------------------- actions

    /// <summary>Puts the item back on the clipboard (and on top of the list).</summary>
    /// <summary>Why the last <see cref="CopyBack"/> failed: true = its picture or files are gone; false = the clipboard was busy.</summary>
    public bool LastFailGone { get; private set; }

    public async Task<bool> CopyBack(ClipItem item)
    {
        var took = System.Diagnostics.Stopwatch.StartNew();
        string holder = "", lastError = "";
        LastFailGone = false;
        // text, links and colours: straight to the clipboard, opened, written and closed in one go. (WPF checked that it
        // was free, let go, and opened it again itself: another app slipped in between and the copy failed for seconds)
        if (item.Kind is not (ClipKind.Image or ClipKind.Files))
        {
            while (took.ElapsedMilliseconds < 3000)
            {
                _ignoreUntil = DateTime.UtcNow.AddSeconds(1); // (not taken again as a new copy)
                if (SetText(item.Text))
                {
                    Moved(item);
                    return true;
                }
                holder = ProcessOf(GetOpenClipboardWindow());
                await Task.Delay(15);
            }
            App.Log(new TimeoutException($"Copiados: não deu para devolver em {took.ElapsedMilliseconds} ms (área de transferência com {holder})"));
            return false;
        }
        for (int i = 0; i < 5; i++)
        {
            // another app has the clipboard open: wait here, not inside WPF (its retries sleep the island's thread,
            // which is also the one answering the keyboard hook, so the whole keyboard froze and the paste was lost)
            while (took.ElapsedMilliseconds < 3000)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    CloseClipboard();
                    break;
                }
                holder = ProcessOf(GetOpenClipboardWindow());
                await Task.Delay(25);
            }
            _ignoreUntil = DateTime.UtcNow.AddSeconds(1); // (not taken again as a new copy)
            try
            {
                switch (item.Kind)
                {
                    case ClipKind.Image:
                        if (LoadImage(item.ImagePath) is not BitmapSource img)
                        {
                            LastFailGone = true;
                            return false;
                        }
                        Clipboard.SetImage(img);
                        break;
                    default:
                        var list = new StringCollection();
                        list.AddRange(item.Files.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray());
                        if (list.Count == 0)
                        {
                            LastFailGone = true;
                            return false;
                        }
                        Clipboard.SetFileDropList(list);
                        break;
                }
                Moved(item);
                if (took.ElapsedMilliseconds > 300) App.Log(new TimeoutException($"Copiados: devolver levou {took.ElapsedMilliseconds} ms (área de transferência com {holder})"));
                return true;
            }
            catch (COMException ex)
            {
                lastError = $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
                await Task.Delay(40);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return false;
            }
        }
        App.Log(new TimeoutException($"Copiados: não deu para devolver em {took.ElapsedMilliseconds} ms (área de transferência com {holder}; {lastError})"));
        return false;
    }

    /// <summary>Copied again: to the top of the list.</summary>
    private void Moved(ClipItem item)
    {
        Items.Remove(item);
        item.Time = DateTime.Now;
        Items.Insert(0, item);
        Save();
        Changed?.Invoke();
    }

    /// <summary>The text on the clipboard as plain Unicode text; false if another app has it open right now.</summary>
    private bool SetText(string text)
    {
        if (_listener == null || !OpenClipboard(_listener.Handle)) return false; // (an owner window: SetClipboardData needs one)
        try
        {
            if (!EmptyClipboard()) return false;
            int bytes = (text.Length + 1) * 2;
            IntPtr mem = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) return false;
            IntPtr at = GlobalLock(mem);
            Marshal.Copy(text.ToCharArray(), 0, at, text.Length);
            Marshal.WriteInt16(at, text.Length * 2, 0);
            GlobalUnlock(mem);
            if (SetClipboardData(13 /* CF_UNICODETEXT */, mem) != IntPtr.Zero) return true; // (the clipboard owns the memory now)
            GlobalFree(mem);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);

    private static string ProcessOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "um app sem janela";
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return "?";
        }
    }

    public void TogglePin(ClipItem item)
    {
        item.Pinned = !item.Pinned;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Takes items off the list; their pictures stay until <see cref="Discard"/> (so "Desfazer" can bring them back).</summary>
    public void Remove(IEnumerable<ClipItem> items)
    {
        foreach (var x in items.ToList()) Items.Remove(x);
        Save();
        Changed?.Invoke();
    }

    public void Restore(IEnumerable<ClipItem> items)
    {
        foreach (var x in items) if (!Items.Contains(x)) Items.Add(x);
        Items.Sort((a, b) => b.Time.CompareTo(a.Time));
        Save();
        Changed?.Invoke();
    }

    /// <summary>The undo time is over: the removed pictures go too.</summary>
    public static void Discard(IEnumerable<ClipItem> items)
    {
        foreach (var x in items) if (x.Kind == ClipKind.Image) TryDelete(x.ImagePath);
    }

    /// <summary>"Limpar": everything except the pinned ones.</summary>
    public List<ClipItem> Unpinned => Items.Where(x => !x.Pinned).ToList();

    /// <summary>The limits or the "guardar no disco" option changed.</summary>
    public void SettingsChanged()
    {
        if (!_loaded) return; // (before the list is read, a save would wipe it)
        Trim();
        if (!S.ClipSaveToDisk) TryDelete(FilePath);
        Save();
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- file

    private bool _loaded;

    private void Load()
    {
        _loaded = true;
        try
        {
            if (!S.ClipSaveToDisk)
            {
                // kept only while open: what the last run left goes
                TryDelete(FilePath);
                if (Directory.Exists(ImageFolder)) Directory.Delete(ImageFolder, true);
                return;
            }
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(FilePath)) is { } list)
                Items.AddRange(list.Where(x => x.Kind != ClipKind.Image || File.Exists(x.ImagePath)));
            _ = ReadMissingTextAsync();
            Trim();
            // pictures no item points to (a crash during an undo…)
            if (Directory.Exists(ImageFolder))
            {
                var keep = Items.Where(x => x.Kind == ClipKind.Image).Select(x => x.ImagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.GetFiles(ImageFolder, "*.png")) if (!keep.Contains(f)) TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    public bool DontSave { get; set; }

    private void Save()
    {
        if (!S.ClipSaveToDisk || DontSave || !_loaded) return;
        try
        {
            Directory.CreateDirectory(Folder);
            SafeFile.Write(FilePath, JsonSerializer.Serialize(Items));
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------------------------------------------------------- the app it came from

    /// <summary>The app, and its window in front (the clipboard's owner is often a hidden window of the app).</summary>
    private static string OwnerApp(out string process, out IntPtr window)
    {
        process = "";
        window = IntPtr.Zero;
        try
        {
            var owner = GetClipboardOwner();
            var front = GetForegroundWindow();
            if (owner == IntPtr.Zero) owner = front;
            GetWindowThreadProcessId(owner, out uint pid);
            if (pid == 0) return "";
            GetWindowThreadProcessId(front, out uint frontPid);
            window = frontPid == pid ? front : owner;
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
            string? name = null;
            try { name = p.MainModule?.FileVersionInfo.FileDescription; } catch { } // (an app running as admin doesn't tell)
            return string.IsNullOrWhiteSpace(name) ? p.ProcessName : name.Trim();
        }
        catch
        {
            return "";
        }
    }

    // ---------------------------------------------------------------- private windows

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "opera", "brave", "vivaldi", "firefox", "librewolf", "waterfox", "zen", "arc", "yandex", "chromium", "thorium", "floorp" };

    // the words browsers use for it, in the languages Notchy speaks and a few more
    private static readonly string[] PrivateWords =
        { "incognito", "incógnito", "anônim", "anonim", "privad", "privativ", "private", "inprivate", "privée", "privat", "privato" };

    /// <summary>
    /// A private window? Chromium browsers (Chrome, Edge, Opera, Brave…) say it at the end of the window's accessible name
    /// ("… - Opera (privada)", "… (Incognito)"), Edge also in its title ("[InPrivate]"), Firefox at the end of its title.
    /// </summary>
    public static bool IsPrivateWindow(IntPtr hwnd)
    {
        try
        {
            var root = GetAncestor(hwnd, 2 /* GA_ROOT */);
            if (root != IntPtr.Zero) hwnd = root;
            var sb = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            string name = "";
            try { name = System.Windows.Automation.AutomationElement.FromHandle(hwnd).Current.Name ?? ""; } catch { }
            return IsPrivateName(title, name);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Only the browser's own part is looked at (a page called "Private notes" isn't a private window).</summary>
    public static bool IsPrivateName(string title, string accessibleName)
    {
        bool Has(string part) => PrivateWords.Any(w => part.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (title.Contains("[InPrivate]", StringComparison.OrdinalIgnoreCase)) return true;
        // Chromium: "<title> (privada)" — the last brackets, added after the page's title
        var m = Regex.Match(accessibleName, @"\(([^()]*)\)\s*$");
        if (m.Success && accessibleName.Length > title.Length && Has(m.Groups[1].Value)) return true;
        // Firefox: "<page> — Mozilla Firefox Private Browsing" / "Navegação privativa do Mozilla Firefox"
        int dash = Math.Max(title.LastIndexOf(" — ", StringComparison.Ordinal), title.LastIndexOf(" - ", StringComparison.Ordinal));
        string tail = dash >= 0 ? title[(dash + 3)..] : "";
        return tail.Contains("Firefox", StringComparison.OrdinalIgnoreCase) && Has(tail);
    }

    // ---------------------------------------------------------------- text in images (Windows' own OCR)

    private bool _reading;

    private async Task ReadMissingTextAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            foreach (var x in Items.Where(x => x.Kind == ClipKind.Image && x.Ocr == null).ToList()) await ReadTextAsync(x);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>Reads the text in a copied picture (in the languages of the Windows profile), for the search.</summary>
    public async Task ReadTextAsync(ClipItem x)
    {
        try
        {
            x.Ocr = await ReadTextAsync(x.ImagePath) ?? "";
            Save();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            x.Ocr = "";
            App.Log(ex);
        }
    }

    public static async Task<string?> ReadTextAsync(string path)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null || !File.Exists(path)) return null;
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension, w = decoder.PixelWidth, h = decoder.PixelHeight;
        double k = Math.Min(1, (double)max / Math.Max(w, h));
        var transform = new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = (uint)(w * k), ScaledHeight = (uint)(h * k) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            transform, Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
