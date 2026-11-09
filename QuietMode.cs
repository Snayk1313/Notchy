using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Notchy;

/// <summary>
/// "Só no Notchy": takes away Windows' own pop-up and its sound by turning on Windows' Do Not Disturb, in its "alarms only"
/// mode. Notifications still reach the notification center, which is where Notchy reads them.
/// Windows has no public way to set this, so it goes through the service its own quick panel talks to (no admin rights needed).
/// A small file remembers that Notchy turned it on and what it was before, so it can be undone even after a crash.
/// </summary>
internal static class QuietMode
{
    private const string AlarmsOnly = "Microsoft.QuietHoursProfile.AlarmsOnly", Off = "Microsoft.QuietHoursProfile.Unrestricted";

    private static readonly string Marker = Path.Combine(AppSettings.Folder, "quiet-mode.txt");
    private static readonly object Gate = new();
    private static bool? _wanted;

    [ComImport, Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IQuietHoursSettings
    {
        [PreserveSig] int GetSelectedProfile(out IntPtr profile);
        [PreserveSig] int SetSelectedProfile([MarshalAs(UnmanagedType.LPWStr)] string profile);
    }

    /// <summary>Turns it on or off when the setting changed. Leaves it alone otherwise, so a Win+N of yours isn't fought.</summary>
    public static void Sync(bool want)
    {
        if (_wanted == want) return;
        _wanted = want;
        Task.Run(() =>
        {
            if (want) TurnOn();
            else Undo();
        });
    }

    /// <summary>Notchy closing. (A second copy that is only closing again has nothing to undo: the first one owns this.)</summary>
    public static void OnAppExit()
    {
        if (_wanted != null) Task.Run(Undo).Wait(TimeSpan.FromSeconds(3));
    }

    private static void TurnOn()
    {
        lock (Gate)
        {
            try
            {
                var current = Selected();
                if (current == null || current == AlarmsOnly) return;
                bool ours = File.Exists(Marker);
                if (!ours)
                {
                    Directory.CreateDirectory(AppSettings.Folder);
                    File.WriteAllText(Marker, current);
                }
                if (!Select(AlarmsOnly) && !ours) File.Delete(Marker);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }

    /// <summary>Puts Do Not Disturb back as it was, if it was Notchy that turned it on. Also used when uninstalling.</summary>
    public static void Undo()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Marker)) return;
                var current = Selected();
                if (current == null) return; // Windows isn't answering (signing out?): the next start finishes this
                if (current == AlarmsOnly)
                {
                    var before = File.ReadAllText(Marker).Trim();
                    if (!Select(before.Length > 0 ? before : Off)) return;
                }
                File.Delete(Marker);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        }
    }

    private static string? Selected() => Use(q =>
    {
        if (q.GetSelectedProfile(out var text) != 0 || text == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    });

    private static bool Select(string profile) => Use(q => q.SetSelectedProfile(profile) == 0);

    private static T? Use<T>(Func<IQuietHoursSettings, T> call)
    {
        try
        {
            var service = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("F53321FA-34F8-4B7F-B9A3-361877CB94CF"))!)!;
            try
            {
                return call((IQuietHoursSettings)service);
            }
            finally
            {
                Marshal.ReleaseComObject(service);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return default;
        }
    }
}
