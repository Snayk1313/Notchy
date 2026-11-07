using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

namespace Notchy;

/// <summary>
/// Cancels a browser download from outside the browser.
/// - Firefox family (Zen, Firefox, LibreWolf, Floorp...): opens the browser's own "about:downloads" page in a
///   new tab, presses "Cancel" on that download (found by its file name, the one still in progress) and closes
///   the tab again. A browser can't be stopped any other way: its file stays locked while it downloads.
/// - Chromium browsers (Edge, Chrome, Brave, Opera): their cancel buttons only exist while the mouse is over
///   the download, so we open the browser's downloads list instead and leave the last click to you.
/// Blocking (UI Automation): call it off the UI thread.
/// </summary>
public static class DownloadCanceller
{
    private static readonly string[] FirefoxFamily = { "zen", "firefox", "librewolf", "floorp", "waterfox", "mercury" };

    public enum Result { Canceled, ListOpened, Failed }

    public static Result Cancel(TransferInfo t)
    {
        try
        {
            string browser = t.Browser;
            if (FirefoxFamily.Contains(browser)) return CancelFirefox(browser, t.Name) ? Result.Canceled : Result.ListOpened;
            return OpenChromiumList(browser) ? Result.ListOpened : Result.Failed;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return Result.Failed;
        }
    }

    // ------------------------------------------------------------------ Firefox family

    private static bool CancelFirefox(string processName, string fileName)
    {
        var proc = Process.GetProcessesByName(processName).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                   ?? Process.GetProcessesByName(processName).FirstOrDefault();
        if (proc == null) return false;
        string exe = proc.MainModule?.FileName ?? "";
        if (!File.Exists(exe)) return false;
        Process.Start(new ProcessStartInfo(exe, "--new-tab about:downloads") { UseShellExecute = false });

        var pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToHashSet();
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(300);
            foreach (var window in TopWindows(pids))
            {
                var page = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "contentAreaDownloadsView"));
                if (page == null) continue;
                var list = page.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "downloadsListBox"));
                if (list == null) continue;
                foreach (AutomationElement item in list.FindAll(TreeScope.Children, Condition.TrueCondition))
                {
                    // the one still downloading: its row has our file name and a percentage ("video.zip 18% 52s left ...")
                    string name = item.Current.Name ?? "";
                    if (!name.StartsWith(fileName, StringComparison.OrdinalIgnoreCase) || !name.Contains('%')) continue;
                    var button = item.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    if (button?.GetCurrentPattern(InvokePattern.Pattern) is not InvokePattern invoke) continue;
                    invoke.Invoke();
                    Thread.Sleep(400);
                    CloseCurrentTab(window);
                    return true;
                }
            }
        }
        return false; // the page is open: the user can cancel there
    }

    /// <summary>Closes the "about:downloads" tab we opened (Ctrl+W on that window, which is in front).</summary>
    private static void CloseCurrentTab(AutomationElement window)
    {
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        if (hwnd == IntPtr.Zero || GetForegroundWindow() != hwnd) return;
        keybd_event(0x11, 0, 0, UIntPtr.Zero);          // Ctrl down
        keybd_event(0x57, 0, 0, UIntPtr.Zero);          // W
        keybd_event(0x57, 0, 2, UIntPtr.Zero);
        keybd_event(0x11, 0, 2, UIntPtr.Zero);          // Ctrl up
    }

    // ------------------------------------------------------------------ Chromium family

    private static bool OpenChromiumList(string processName)
    {
        var names = processName != "" ? new[] { processName } : new[] { "msedge", "chrome", "brave", "opera", "vivaldi" };
        var pids = names.SelectMany(Process.GetProcessesByName).Select(p => p.Id).ToHashSet();
        foreach (var window in TopWindows(pids))
        {
            var button = FindDownloadsButton(window);
            if (button == null) continue;
            var hwnd = new IntPtr(window.Current.NativeWindowHandle);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
                SetForegroundWindow(hwnd);
            }
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) ((InvokePattern)inv).Invoke();
            else if (button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec))
            {
                try { ((ExpandCollapsePattern)ec).Expand(); } catch (COMException) { /* Edge opens it anyway */ }
            }
            else if (button.TryGetCurrentPattern(TogglePattern.Pattern, out var tg)) ((TogglePattern)tg).Toggle();
            return true;
        }
        return false;
    }

    /// <summary>The toolbar's downloads button (skipping web pages).</summary>
    private static AutomationElement? FindDownloadsButton(AutomationElement window)
    {
        var walker = TreeWalker.ControlViewWalker;
        AutomationElement? Walk(AutomationElement e, int depth)
        {
            if (depth > 14) return null;
            for (var c = walker.GetFirstChild(e); c != null; c = walker.GetNextSibling(c))
            {
                var info = c.Current;
                if (info.ControlType == ControlType.Document) continue;
                if (info.ControlType == ControlType.Button && info.ClassName.Contains("Download", StringComparison.OrdinalIgnoreCase)) return c;
                if (Walk(c, depth + 1) is { } found) return found;
            }
            return null;
        }
        return Walk(window, 0);
    }

    private static System.Collections.Generic.IEnumerable<AutomationElement> TopWindows(System.Collections.Generic.HashSet<int> pids)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var w = walker.GetFirstChild(AutomationElement.RootElement); w != null; w = walker.GetNextSibling(w))
        {
            bool mine;
            try { mine = pids.Contains(w.Current.ProcessId) && !string.IsNullOrEmpty(w.Current.Name); } catch { continue; }
            if (mine) yield return w;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
