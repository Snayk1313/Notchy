using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Notchy;

/// <summary>
/// Opens a notification the way Windows does when you click its pop-up, so the app goes to that chat/page:
/// the notification's "launch" arguments are handed to the app's own toast activator (a COM class the app registers,
/// <c>INotificationActivationCallback</c>; found in the registry, the app's Start menu shortcut, or a Store app's
/// manifest). A "protocol" notification opens its link instead. When neither works, the app just opens.
/// The technique is the one hesewake-notch uses. The arguments are read at click time and never stored or logged.
/// </summary>
public static class NotificationOpener
{
    private static Dictionary<string, Guid>? _shortcuts;
    private static readonly object Lock = new();

    /// <summary>True when the app was handed the notification (or its link was opened).</summary>
    public static async Task<bool> OpenAsync(Notice n)
    {
        return await Task.Run(() =>
        {
            try
            {
                // what it said when it arrived (exact, even after the app removed it from Windows), else Windows' copy
                string? payload = n.Payload ?? NotificationService.PayloadFor(n);
                if (payload == null) return false;
                var toast = Regex.Match(payload, "<toast[^>]*>").Value;
                string launch = System.Net.WebUtility.HtmlDecode(Regex.Match(toast, "\\slaunch=\"([^\"]*)\"").Groups[1].Value);
                string kind = Regex.Match(toast, "activationType=\"([^\"]*)\"").Groups[1].Value.ToLowerInvariant();

                if (kind == "protocol") return SafeLink(launch) && OpenLink(launch);
                if (kind == "background") return false;
                // no launch arguments (Electron apps: Discord…): Electron's activator finds the notification by its tag
                // ("type=click&tag=<id>") and fires its click, like a click on Windows' own pop-up
                if (launch.Length == 0 && (n.Tag ?? NotificationService.TagFor(n)) is { Length: > 0 } tag)
                    launch = $"type=click&tag={tag}";
                return Activate(n.AppId, launch);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return false;
            }
        });
    }

    // ---- links (activationType="protocol") ---------------------------------------------

    /// <summary>An app link (whatsapp:, discord:, https:…), never one that could run a file or a command.</summary>
    private static bool SafeLink(string link)
    {
        int colon = link.IndexOf(':');
        if (colon < 2 || link.Length > 2000) return false;
        string scheme = link[..colon].ToLowerInvariant();
        if (!Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]+$")) return false;
        string[] deny = { "file", "ms-msdt", "search", "search-ms", "ms-search", "shell", "javascript", "vbscript", "data", "mk", "res", "hcp", "ms-officecmd" };
        return Array.IndexOf(deny, scheme) < 0;
    }

    private static bool OpenLink(string link)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
        return true;
    }

    // ---- the app's toast activator -----------------------------------------------------

    private static bool Activate(string appId, string launch)
    {
        if (ActivatorFor(appId) is not Guid clsid) return false;
        var iid = typeof(INotificationActivationCallback).GUID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 4 /* CLSCTX_LOCAL_SERVER: the app's own process, never loaded into ours */, ref iid, out object obj);
        if (hr != 0 || obj is not INotificationActivationCallback cb) return false;
        try
        {
            CoAllowSetForegroundWindow(obj, IntPtr.Zero); // it may come to the front
            return cb.Activate(appId, launch, IntPtr.Zero, 0) == 0;
        }
        finally
        {
            Marshal.ReleaseComObject(obj);
        }
    }

    /// <summary>The app's toast activator: registry, then a Store app's manifest, then the Start menu shortcuts.</summary>
    internal static Guid? ActivatorFor(string appId)
    {
        using (var k = Registry.ClassesRoot.OpenSubKey($@"AppUserModelId\{appId}"))
            if (k?.GetValue("CustomActivator") is string s && Guid.TryParse(s, out var g)) return g;

        if (appId.Contains('!') && FromManifest(appId.Split('!')[0]) is Guid m) return m;

        lock (Lock)
        {
            _shortcuts ??= ScanShortcuts();
            if (_shortcuts.TryGetValue(appId.ToLowerInvariant(), out var c)) return c;
            _shortcuts = ScanShortcuts(); // maybe installed since: look again once
            return _shortcuts.TryGetValue(appId.ToLowerInvariant(), out c) ? c : null;
        }
    }

    /// <summary>A Store app declares it in its manifest (&lt;desktop:ToastNotificationActivation ToastActivatorCLSID=…/&gt;).</summary>
    private static Guid? FromManifest(string familyName)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var p in pm.FindPackagesForUser("", familyName))
            {
                string manifest = Path.Combine(p.InstalledLocation.Path, "AppxManifest.xml");
                if (!File.Exists(manifest)) continue;
                var m = Regex.Match(File.ReadAllText(manifest), "ToastActivatorCLSID=\"([0-9a-fA-F-]{36})\"");
                if (m.Success && Guid.TryParse(m.Groups[1].Value, out var g)) return g;
            }
        }
        catch
        {
            // not a Store app, or no access
        }
        return null;
    }

    /// <summary>(app id → activator) from the Start menu shortcuts' System.AppUserModel.ID / ToastActivatorCLSID.</summary>
    private static Dictionary<string, Guid> ScanShortcuts()
    {
        var map = new Dictionary<string, Guid>();
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
                 })
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }
            int n = 0;
            foreach (var f in files)
            {
                if (++n > 3000) break;
                if (ReadShortcut(f) is { } pair && !map.ContainsKey(pair.id)) map[pair.id] = pair.clsid;
            }
        }
        return map;
    }

    private static readonly Guid AppUserModelFmt = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    private static (string id, Guid clsid)? ReadShortcut(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var kClsid = new PropertyKey { fmtid = AppUserModelFmt, pid = 26 };
            var kId = new PropertyKey { fmtid = AppUserModelFmt, pid = 5 };
            if (store.GetValue(ref kClsid, out var pvC) != 0) return null;
            Guid? clsid = null;
            try
            {
                if (pvC.vt == 72 /* VT_CLSID */ && pvC.p != IntPtr.Zero) clsid = Marshal.PtrToStructure<Guid>(pvC.p);
            }
            finally
            {
                PropVariantClear(ref pvC);
            }
            if (clsid == null || clsid == Guid.Empty) return null;
            if (store.GetValue(ref kId, out var pvI) != 0) return null;
            string? id = null;
            try
            {
                if (pvI.vt == 31 /* VT_LPWSTR */ && pvI.p != IntPtr.Zero) id = Marshal.PtrToStringUni(pvI.p);
            }
            finally
            {
                PropVariantClear(ref pvI);
            }
            return string.IsNullOrEmpty(id) ? null : (id.ToLowerInvariant(), clsid.Value);
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    // ---- interop ------------------------------------------------------------------------

    [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INotificationActivationCallback
    {
        [PreserveSig]
        int Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, uint count);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);

    [DllImport("ole32.dll")]
    private static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object unk, IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx, int flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
}
