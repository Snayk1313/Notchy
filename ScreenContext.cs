using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Notchy;

/// <summary>Text grabbed from the screen for Notchy Chat.</summary>
/// <param name="Editable">Text of a whole editable field (message box, input...): Replace rewrites the field.</param>
/// <param name="Field">That field, to put the focus back in it.</param>
/// <param name="Ocr">Read from the screen image (the app doesn't expose its text); Drop* = where the eyes were dropped.</param>
public sealed record CapturedText(string Text, string App, IntPtr Window, bool FromSelection, bool Editable = false,
    AutomationElement? Field = null, bool Ocr = false, int DropX = 0, int DropY = 0);

/// <summary>
/// Reads text from whatever is under a screen point using Windows UI Automation (the accessibility
/// API): the current selection first, then the paragraph/field under the point. Nothing is typed or
/// copied in the target app, so it is safe in terminals too.
/// </summary>
public static class ScreenContext
{
    private const int MaxChars = 8000;

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    public static IntPtr RootWindowAt(int x, int y)
    {
        IntPtr h = WindowFromPoint(new Native.POINT { X = x, Y = y });
        return h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, 2 /* GA_ROOT */);
    }

    public static IntPtr RootWindowAtWindow(IntPtr hwnd) => hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, 2 /* GA_ROOT */);

    public static string AppName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            using var p = Process.GetProcessById(pid);
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) return desc!;
            }
            catch
            {
                // access denied for some processes
            }
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// UI Automation first (exact text, selections); if the app doesn't expose its text (e.g. WhatsApp),
    /// read it with the Windows built-in OCR around the point. Runs off the UI thread.
    /// </summary>
    public static async Task<CapturedText?> TextAtAsync(int x, int y)
    {
        var viaUia = await Task.Run(() => TextAt(x, y));
        if (viaUia != null) return viaUia;
        try
        {
            string? ocr = await OcrAroundAsync(x, y);
            if (!string.IsNullOrWhiteSpace(ocr))
            {
                IntPtr root = RootWindowAt(x, y);
                // Apps that hide their text (WhatsApp...) keep the message box at the bottom of the window:
                // text dropped there is treated as that box, so Replace can click into it and paste.
                bool likelyMessageBox = false;
                if (GetWindowRect(root, out RECT wr) && wr.Bottom > wr.Top)
                    likelyMessageBox = y >= wr.Bottom - Math.Max(110, (wr.Bottom - wr.Top) * 0.22);
                return new CapturedText(Clip(ocr!), AppName(root), root, false, likelyMessageBox, null, true, x, y);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        return null;
    }

    private static CapturedText? TextAt(int x, int y)
    {
        IntPtr root = RootWindowAt(x, y);
        string app = AppName(root);
        try
        {
            var pt = new System.Windows.Point(x, y);
            var el = AutomationElement.FromPoint(pt);
            if (el == null) return null;

            var walker = TreeWalker.ControlViewWalker;

            // 0) dropped on a text box you can type in (Discord / WhatsApp message box...)?
            //    The point usually hits a text piece inside it, so look a few levels up.
            AutomationElement? editBox = el;
            for (int i = 0; editBox != null && i < 6; i++)
            {
                if (IsEditable(editBox))
                {
                    string? sel = null, all = null;
                    if (editBox.TryGetCurrentPattern(TextPattern.Pattern, out object btp))
                    {
                        try { sel = string.Join("\n", ((TextPattern)btp).GetSelection().Select(r => r.GetText(MaxChars))).Trim(); } catch { }
                        try { all = ((TextPattern)btp).DocumentRange.GetText(MaxChars).Trim(); } catch { }
                    }
                    if (string.IsNullOrEmpty(all) && editBox.TryGetCurrentPattern(ValuePattern.Pattern, out object bvp))
                        all = (((ValuePattern)bvp).Current.Value ?? "").Trim();
                    if (!string.IsNullOrEmpty(sel) && sel!.Length > 1 && sel != all)
                        return new CapturedText(Clip(sel), app, root, true, true, editBox);
                    if (!string.IsNullOrEmpty(all) && all!.Length > 1)
                        return new CapturedText(Clip(all!), app, root, false, true, editBox);
                    break;
                }
                editBox = walker.GetParent(editBox);
            }

            // 1) selection / paragraph from the nearest element that exposes text
            AutomationElement? cur = el;
            for (int i = 0; cur != null && i < 10; i++)
            {
                if (cur.TryGetCurrentPattern(TextPattern.Pattern, out object p))
                {
                    var tp = (TextPattern)p;
                    try
                    {
                        string sel = string.Join("\n", tp.GetSelection().Select(r => r.GetText(MaxChars))).Trim();
                        if (sel.Length > 1) return new CapturedText(Clip(sel), app, root, true);
                    }
                    catch
                    {
                        // some providers don't support selection
                    }
                    // an editable box (Discord / WhatsApp message box, inputs...): take its whole text
                    if (IsEditable(cur))
                    {
                        try
                        {
                            string all = tp.DocumentRange.GetText(MaxChars).Trim();
                            if (all.Length > 1) return new CapturedText(Clip(all), app, root, false, true, cur);
                        }
                        catch
                        {
                            // fall back to the paragraph
                        }
                    }
                    try
                    {
                        var range = tp.RangeFromPoint(pt);
                        range.ExpandToEnclosingUnit(TextUnit.Paragraph);
                        string para = range.GetText(MaxChars).Trim();
                        if (para.Length > 1) return new CapturedText(Clip(para), app, root, false);
                    }
                    catch
                    {
                        // ignore
                    }
                    break;
                }
                cur = walker.GetParent(cur);
            }

            // 2) value of an input field
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
            {
                var value = ((ValuePattern)vp).Current;
                string v = value.Value ?? "";
                if (v.Trim().Length > 1) return new CapturedText(Clip(v.Trim()), app, root, false, !value.IsReadOnly, el);
            }

            // 3) text elements: the element itself, or the text pieces inside it (chat bubbles, cards...)
            string title = "";
            try { title = AutomationElement.FromHandle(root).Current.Name ?? ""; } catch { }
            string name = (el.Current.Name ?? "").Trim();
            var type = el.Current.ControlType;
            bool texty = type == ControlType.Text || type == ControlType.ListItem || type == ControlType.Hyperlink ||
                         type == ControlType.DataItem || type == ControlType.TreeItem;
            if (texty && name.Length > 2 && name != title) return new CapturedText(Clip(name), app, root, false);

            var box = el.Current.BoundingRectangle;
            if (box.IsEmpty || box.Width * box.Height > 900 * 500) return null; // whole window: too broad, let OCR decide
            var inner = el.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var pieces = inner.Cast<AutomationElement>().Take(40)
                .Select(e => (e.Current.Name ?? "").Trim())
                .Where(t => t.Length > 0 && t != title)
                .Distinct()
                .ToList();
            string joined = string.Join("\n", pieces).Trim();
            if (joined.Length > 2 && joined.Length < MaxChars) return new CapturedText(joined, app, root, false);
        }
        catch
        {
            // the target app doesn't support UI Automation
        }
        return null;
    }

    private static readonly string ObjectChar = ((char)0xFFFC).ToString();

    // U+FFFC = "object replacement" (emoji / image placeholders that Chromium apps put in their text)
    /// <summary>A text box you can type in (not a read-only document or label).</summary>
    private static bool IsEditable(AutomationElement el)
    {
        try
        {
            // only real text boxes (a web page "Document" also has a value, but you can't type in it)
            if (el.Current.ControlType != ControlType.Edit) return false;
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vp)) return !((ValuePattern)vp).Current.IsReadOnly;
            return el.Current.IsKeyboardFocusable;
        }
        catch
        {
            return false;
        }
    }

    private static string Clip(string s)
    {
        s = s.Replace(ObjectChar, "").Trim();
        return s.Length > MaxChars ? s[..MaxChars] : s;
    }

    /// <summary>Whitespace-insensitive "a contains b".</summary>
    public static bool LooselyContains(string a, string b)
    {
        static string N(string x) => System.Text.RegularExpressions.Regex.Replace(x.Replace(ObjectChar, ""), @"\s+", " ").Trim().ToLowerInvariant();
        string na = N(a), nb = N(b);
        return nb.Length > 0 && na.Contains(nb);
    }

    // ------------------------------------------------------------------ OCR fallback

    /// <summary>Reads the paragraph under (x, y) with Windows.Media.Ocr (offline, built into Windows).</summary>
    private static async Task<string?> OcrAroundAsync(int x, int y)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine == null) return null;

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        int w = 900, h = 360;
        int left = Math.Clamp(x - w / 2, screen.Left, screen.Right - w);
        int top = Math.Clamp(y - h / 2, screen.Top, screen.Bottom - h);

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(left, top, 0, 0, bmp.Size);

        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        using var ras = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ras);
        using var soft = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(soft);

        double px = x - left, py = y - top;
        var lines = result.Lines
            .Select(l =>
            {
                double lx = l.Words.Min(wd => wd.BoundingRect.X), ly = l.Words.Min(wd => wd.BoundingRect.Y);
                double rx = l.Words.Max(wd => wd.BoundingRect.X + wd.BoundingRect.Width);
                double by = l.Words.Max(wd => wd.BoundingRect.Y + wd.BoundingRect.Height);
                return (text: l.Text, l: lx, t: ly, r: rx, b: by);
            })
            .OrderBy(l => l.t)
            .ToList();
        if (lines.Count == 0) return null;

        // the line under the cursor (or the closest one)
        int hit = 0;
        double best = double.MaxValue;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            double dx = px < l.l ? l.l - px : px > l.r ? px - l.r : 0;
            double dy = py < l.t ? l.t - py : py > l.b ? py - l.b : 0;
            double d = dx * dx + dy * dy * 4;
            if (d < best) { best = d; hit = i; }
        }
        if (best > 80 * 80) return null;

        // grow into a paragraph: neighbouring lines that are close and overlap horizontally
        bool Near((string text, double l, double t, double r, double b) a, (string text, double l, double t, double r, double b) b2)
        {
            double lineH = Math.Max(a.b - a.t, b2.b - b2.t);
            double gap = b2.t > a.b ? b2.t - a.b : a.t - b2.b;
            bool overlap = Math.Min(a.r, b2.r) - Math.Max(a.l, b2.l) > -20;
            return gap < lineH * 0.9 && overlap;
        }
        int from = hit, to = hit;
        while (from > 0 && Near(lines[from], lines[from - 1])) from--;
        while (to < lines.Count - 1 && Near(lines[to], lines[to + 1])) to++;
        return string.Join("\n", lines.Skip(from).Take(to - from + 1).Select(l => l.text)).Trim();
    }

    // ------------------------------------------------------------------ selection of the app you were using

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    private static readonly string[] Terminals =
    {
        "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "OpenConsole", "wezterm-gui", "alacritty",
        "mintty", "Hyper", "Tabby", "kitty", "putty", "wsl", "ubuntu", "bash", "WindowsTerminalPreview"
    };

    public static bool IsTerminal(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            using var p = Process.GetProcessById(pid);
            return Terminals.Any(t => string.Equals(t, p.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true; // unknown: play safe, no Ctrl+C
        }
    }

    /// <summary>The selected text of the focused control, via UI Automation (no keys sent).</summary>
    public static Task<CapturedText?> FocusedSelectionAsync(IntPtr window) => Task.Run(() =>
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el == null || el.Current.ProcessId == Environment.ProcessId) return null; // that's Notchy's own text box
            var walker = TreeWalker.ControlViewWalker;
            for (int i = 0; el != null && i < 10; i++)
            {
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out object p))
                {
                    string sel = string.Join("\n", ((TextPattern)p).GetSelection().Select(r => r.GetText(MaxChars))).Trim();
                    if (sel.Length > 1) return new CapturedText(Clip(sel), AppName(window), window, true);
                    return null;
                }
                el = walker.GetParent(el);
            }
        }
        catch
        {
            // ignore
        }
        return (CapturedText?)null;
    });

    /// <summary>
    /// Copies the selection of <paramref name="window"/> with Ctrl+C and gives the clipboard back afterwards.
    /// Never used on terminals (Ctrl+C would interrupt them). Must run on the UI thread.
    /// </summary>
    public static async Task<CapturedText?> SelectionViaCopyAsync(IntPtr window)
    {
        if (window == IntPtr.Zero || IsTerminal(window)) return null;
        string? previous = null;
        try { if (System.Windows.Clipboard.ContainsText()) previous = System.Windows.Clipboard.GetText(); } catch { }

        uint before = GetClipboardSequenceNumber();
        Native.FocusWindow(window);
        await Task.Delay(120);
        SendCtrl(0x43 /* C */);
        string? copied = null;
        bool changed = false;
        for (int i = 0; i < 16; i++)
        {
            await Task.Delay(40);
            if (GetClipboardSequenceNumber() == before) continue;
            changed = true;
            await Task.Delay(30); // let the app finish writing all formats
            try { if (System.Windows.Clipboard.ContainsText()) copied = System.Windows.Clipboard.GetText(); } catch { }
            break;
        }
        if (changed && previous != null)
        {
            // always give the clipboard back, whatever was copied
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    System.Windows.Clipboard.SetText(previous);
                    break;
                }
                catch
                {
                    await Task.Delay(50);
                }
            }
        }
        copied = copied?.Trim();
        return string.IsNullOrEmpty(copied) || copied!.Length < 2 ? null : new CapturedText(Clip(copied), AppName(window), window, true);
    }

    private static void SendCtrl(byte key)
    {
        const byte VK_CONTROL = 0x11;
        const uint KEYUP = 0x2;
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYUP, UIntPtr.Zero);
    }

    // ------------------------------------------------------------------ replace (spelling)

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Puts text on the clipboard, brings the source app back and pastes over its selection.</summary>
    public static async Task ReplaceSelectionAsync(CapturedText source, string text)
    {
        string? previous = null;
        try { if (System.Windows.Clipboard.ContainsText()) previous = System.Windows.Clipboard.GetText(); } catch { }
        System.Windows.Clipboard.SetText(text);
        Native.FocusWindow(source.Window);
        await Task.Delay(180);
        if (!source.FromSelection && source.Editable)
        {
            // whole box: put the caret back in it, select everything in it, then paste over it
            if (source.Field != null)
            {
                try { await Task.Run(() => source.Field.SetFocus()); } catch { }
                await Task.Delay(120);
            }
            else if (source.Ocr)
            {
                // the app hides its text from UI Automation: click where the eyes were dropped (the box),
                // then put the mouse back where it was
                Native.GetCursorPos(out var back);
                SetCursorPos(source.DropX, source.DropY);
                mouse_event(0x2 /* LEFTDOWN */, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x4 /* LEFTUP */, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(60);
                SetCursorPos(back.X, back.Y);
                await Task.Delay(120);
            }
            SendCtrl(0x41 /* A */);
            await Task.Delay(80);
        }
        SendCtrl(0x56 /* V */);
        // give the clipboard back once the app has pasted
        await Task.Delay(450);
        if (previous != null)
        {
            try { System.Windows.Clipboard.SetText(previous); } catch { }
        }
    }
}
