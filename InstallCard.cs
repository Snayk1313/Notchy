using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Notchy;

/// <summary>
/// Starts the card that shows the installation's real progress. Notchy has to be closed while it's replaced, so the card is a
/// small program of its own, carried inside Notchy and run from the temp folder (see UpdateCard\UpdateCard.cs).
/// It gets the size the new version will have and watches the updater's folder fill up to that size.
/// </summary>
internal static class InstallCard
{
    /// <summary>Tells the new version to wait a moment before appearing, so the card finishes first: one pair of eyes at a time.</summary>
    public const string AfterUpdateArg = "--after-update";
    public const int AfterUpdateWaitMs = 1400;

    private const string CardFiles = "notchy-card-*.exe";

    /// <summary>False if the card couldn't be started: the updater then shows its own window instead.</summary>
    public static bool Show(string version, string packageFile)
    {
        try
        {
            var installFolder = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            var packages = Path.Combine(installFolder, "packages");
            long total = UnpackedSize(Path.Combine(packages, packageFile));
            if (total <= 0) return false;
            Launch(Path.Combine(packages, "VelopackTemp"), total, version, Environment.ProcessId, App.SingleInstanceName);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    /// <summary>Cards from earlier updates (each one is still running when the new Notchy starts, so it's cleaned up the time after).</summary>
    public static void CleanLeftovers()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), CardFiles))
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>The size of what the updater will unpack (two of the package's files, the updater's own, go elsewhere).</summary>
    private static long UnpackedSize(string package)
    {
        using var zip = ZipFile.OpenRead(package);
        return zip.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0)
            .Where(e => !e.Name.Equals("Squirrel.exe", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith("_ExecutionStub.exe", StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.Length);
    }

    private static void Launch(string updaterFolder, long total, string version, int notchyProcess, string newNotchyMutex)
    {
        var exe = Path.Combine(Path.GetTempPath(), $"notchy-card-{Guid.NewGuid():N}.exe");
        using (var card = typeof(InstallCard).Assembly.GetManifestResourceStream("UpdateCard.exe")!)
        using (var file = File.Create(exe))
            card.CopyTo(file);

        // never Notchy's own folder as the working directory: that would keep it in use, and the updater has to move it
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        void Option(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
        Option("--temp", updaterFolder);
        Option("--total", total.ToString());
        Option("--pid", notchyProcess.ToString());
        Option("--mutex", newNotchyMutex);
        Option("--title", Loc.T("upd.card.title"));
        Option("--subtitle", Loc.F("upd.panel.version", version));
        Option("--eye", AppSettings.Current.EyeColor);
        using var process = Process.Start(start);
    }

#if DEBUG
    /// <summary>Test: runs the real card against a stand-in for the updater's folder that fills up, without closing Notchy.</summary>
    public static async Task DemoAsync()
    {
        const int parts = 48, partBytes = 1024 * 1024;
        var folder = Path.Combine(Path.GetTempPath(), "notchy-card-demo");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        const string mutexName = "Notchy.CardDemo";
        Launch(folder, parts * (long)partBytes, "2.2.7", 0, mutexName);
        await Task.Delay(1200);
        var unpacking = Path.Combine(folder, "tmp_demo");
        Directory.CreateDirectory(unpacking);
        var data = new byte[partBytes];
        for (int i = 0; i < parts; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(unpacking, i + ".bin"), data);
            await Task.Delay(75);
        }
        await Task.Delay(300);
        Directory.Delete(unpacking, true);
        await Task.Delay(700);
        using (new Mutex(true, mutexName)) await Task.Delay(1800);
        Directory.Delete(folder, true);
    }
#endif
}
