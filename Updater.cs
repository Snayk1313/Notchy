using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Notchy;

public enum UpdateStatus { NotInstalled, UpToDate, Ready, Error }

/// <summary>
/// Auto-update through GitHub Releases (Velopack). Every push to the repository builds a new release
/// in GitHub Actions; installed copies find it, download it in the background and apply it with one click.
/// </summary>
public static class Updater
{
    /// <summary>
    /// The public repository whose Releases hold the installers / update packages. Baked in at build
    /// time by the release workflow (-p:NotchyRepo=...), so forks update from their own repository.
    /// </summary>
    public static string? RepoUrl { get; } = ReadRepo();

    private static string? ReadRepo()
    {
        foreach (var a in typeof(Updater).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            if (a is System.Reflection.AssemblyMetadataAttribute m && m.Key == "NotchyRepo" && !string.IsNullOrWhiteSpace(m.Value))
                return m.Value;
        return null;
    }

    private static UpdateManager? _mgr;
    private static bool _busy;

    public static UpdateInfo? Pending { get; private set; }
    public static string? PendingVersion => Pending?.TargetFullRelease.Version.ToString();

    /// <summary>Raised (on a worker thread) when an update has been downloaded and is ready to apply.</summary>
    public static event Action? Ready;

    private static UpdateManager? Manager
    {
        get
        {
            if (_mgr != null) return _mgr;
            if (RepoUrl == null) return null; // local build: no repository to update from
            try
            {
                _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false));
            }
            catch
            {
                _mgr = null;
            }
            return _mgr;
        }
    }

    /// <summary>False when running from a build folder (dev): there is nothing to update in place.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return Manager?.IsInstalled == true; }
            catch { return false; }
        }
    }

    public static string CurrentVersion
    {
        get
        {
            try
            {
                if (IsInstalled && Manager!.CurrentVersion is { } v) return v.ToString();
            }
            catch
            {
                // fall through
            }
            var a = typeof(Updater).Assembly.GetName().Version;
            return a == null ? "?" : $"{a.Major}.{a.Minor}.{a.Build}";
        }
    }

    /// <summary>Looks for a newer release and downloads it. Safe to call often; does nothing while busy.</summary>
    public static async Task<UpdateStatus> CheckAndDownloadAsync()
    {
        if (!IsInstalled) return UpdateStatus.NotInstalled;
        if (Pending != null) return UpdateStatus.Ready;
        if (_busy) return UpdateStatus.UpToDate;
        _busy = true;
        try
        {
            var mgr = Manager!;
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null) return UpdateStatus.UpToDate;
            await mgr.DownloadUpdatesAsync(info).ConfigureAwait(false);
            Pending = info;
            Ready?.Invoke();
            return UpdateStatus.Ready;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return UpdateStatus.Error;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Closes Notchy, installs the downloaded version and starts it again.</summary>
    public static void ApplyAndRestart()
    {
        if (Pending == null || Manager == null) return;
        try
        {
            Manager.ApplyUpdatesAndRestart(Pending.TargetFullRelease);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>
    /// Called first thing in Main: lets Velopack handle install / update / uninstall events.
    /// After install the exe lives in a new folder, so hooks and "start with Windows" are pointed there.
    /// </summary>
    public static void Bootstrap()
    {
        try
        {
            VelopackApp.Build()
                .OnAfterInstallFastCallback(_ =>
                {
                    try { StartupManager.SetEnabled(true); } catch { }
                })
                .OnBeforeUninstallFastCallback(_ =>
                {
                    try { StartupManager.SetEnabled(false); } catch { }
                    try { if (ClaudeHooks.IsInstalled) ClaudeHooks.Uninstall(); } catch { }
                })
                .Run();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Once per start of the installed app: keep hooks / autostart pointing at this exe.</summary>
    public static void FixPaths()
    {
        if (!IsInstalled) return;
        try
        {
            if (ClaudeHooks.IsInstalled && ClaudeHooks.PointsElsewhere) ClaudeHooks.Install();
        }
        catch
        {
            // ignore
        }
        try
        {
            if (StartupManager.IsEnabled) StartupManager.SetEnabled(true);
        }
        catch
        {
            // ignore
        }
    }
}
