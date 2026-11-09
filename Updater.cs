using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Notchy;

public enum UpdatePhase { Idle, Checking, Retrying, Found, Downloading, Ready, Error }

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

    /// <summary>How many times a failed check is repeated before giving up (GitHub sometimes answers 504).</summary>
    public const int MaxRetries = 3;

    public static UpdatePhase Phase { get; private set; }
    /// <summary>Which retry is running, from 1 to <see cref="MaxRetries"/>, while <see cref="Phase"/> is Retrying.</summary>
    public static int Retry { get; private set; }
    public static int Progress { get; private set; }
    public static string? TargetVersion { get; private set; }
    public static long TotalBytes { get; private set; }
    /// <summary>True when the last failure happened while downloading, not while looking for a version.</summary>
    public static bool DownloadFailed { get; private set; }

    /// <summary>Raised (on a worker thread) when an update has been downloaded and is ready to apply.</summary>
    public static event Action? Ready;

    /// <summary>Raised (on any thread) whenever the phase or the progress changes.</summary>
    public static event Action? Changed;

    public static string Megabytes(long bytes) => $"{Math.Max(1, Math.Round(bytes / 1048576d)):0} MB";

    /// <summary>An old failure means nothing to someone only now opening the page.</summary>
    public static void ForgetError()
    {
        if (Phase == UpdatePhase.Error) Phase = UpdatePhase.Idle;
    }

    private static void Set(UpdatePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    private static UpdateManager? Manager
    {
        get
        {
            if (_mgr != null) return _mgr;
            if (RepoUrl == null) return null; // local build: no repository to update from
            try
            {
                _mgr = new UpdateManager(new GithubSource(RepoUrl, null, false, new FastUpdateDownloader()));
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
    public static async Task CheckAndDownloadAsync()
    {
        if (!IsInstalled || Pending != null || _busy) return;
        _busy = true;
        try
        {
            var mgr = Manager!;
            DownloadFailed = false;
            Set(UpdatePhase.Checking);
            var info = await CheckWithRetriesAsync(mgr).ConfigureAwait(false);
            if (info == null)
            {
                Set(UpdatePhase.Idle);
                return;
            }
            TargetVersion = info.TargetFullRelease.Version.ToString();
            TotalBytes = DownloadSize(info);
            Progress = 0;
            Set(UpdatePhase.Found);
            await Task.Delay(FoundNoticeMs).ConfigureAwait(false); // long enough to read that one was found
            DownloadFailed = true;
            Set(UpdatePhase.Downloading);
            await mgr.DownloadUpdatesAsync(info, SetProgress).ConfigureAwait(false);
            Pending = info;
            Progress = 100;
            Set(UpdatePhase.Ready);
            Ready?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Set(UpdatePhase.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private const int FoundNoticeMs = 1500;

    private static async Task<UpdateInfo?> CheckWithRetriesAsync(UpdateManager mgr)
    {
        for (int done = 0; ; done++)
        {
            try
            {
                return await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            }
            catch (Exception) when (done < MaxRetries)
            {
                Retry = done + 1;
                Set(UpdatePhase.Retrying);
                await Task.Delay(TimeSpan.FromSeconds(2 << done)).ConfigureAwait(false); // 2 s, 4 s, 8 s
            }
        }
    }

    /// <summary>What will really be downloaded: the small delta packages when they exist, otherwise the full package.</summary>
    private static long DownloadSize(UpdateInfo info)
    {
        long deltas = 0;
        foreach (var d in info.DeltasToTarget) deltas += d.Size;
        long full = info.TargetFullRelease.Size;
        return deltas > 0 && deltas < full ? deltas : full;
    }

    private static void SetProgress(int percent)
    {
        if (percent == Progress) return;
        Progress = percent;
        Changed?.Invoke();
    }

#if DEBUG
    /// <summary>Debug builds aren't installed, so the page's states can only be seen by acting them out.</summary>
    public static async Task SimulateAsync(string scenario)
    {
        if (_busy) return;
        _busy = true;
        Pending = null;
        DownloadFailed = false;
        Progress = 0;
        Set(UpdatePhase.Checking);
        await Task.Delay(1400);
        if (scenario == "found")
        {
            TargetVersion = "2.2.7";
            TotalBytes = 62L * 1048576;
            Set(UpdatePhase.Found);
            await Task.Delay(FoundNoticeMs);
            DownloadFailed = true;
            Set(UpdatePhase.Downloading);
            for (int i = 1; i <= 100; i++)
            {
                SetProgress(i);
                await Task.Delay(i % 13 == 0 ? 150 : 55);
            }
            Set(UpdatePhase.Ready);
        }
        else if (scenario == "flaky")
        {
            for (Retry = 1; Retry <= MaxRetries; Retry++)
            {
                Set(UpdatePhase.Retrying);
                await Task.Delay(1700);
            }
            Set(UpdatePhase.Error);
        }
        else
        {
            Set(UpdatePhase.Idle);
        }
        _busy = false;
    }
#endif

    /// <summary>Closes Notchy, installs the downloaded version and starts it again.</summary>
    public static void ApplyAndRestart()
    {
        if (Pending == null || Manager == null) return;
        try
        {
            var target = Pending.TargetFullRelease;
            bool card = InstallCard.Show(target.Version.ToString(), target.FileName); // no card: the updater shows its own window
            Manager.WaitExitThenApplyUpdates(target, silent: card, restart: true, new[] { InstallCard.AfterUpdateArg });
            Environment.Exit(0);
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
                    try { QuietMode.Undo(); } catch { }
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
