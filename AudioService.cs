using System;
using System.Runtime.InteropServices;

namespace Notchy;

/// <summary>System volume + output peak meter via Core Audio (default render device).</summary>
public sealed class AudioService
{
    private readonly IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private IAudioMeterInformation? _meter;
    private string? _deviceId;

    public bool HasMeter => _meter != null;

    // ---------------------------------------------------------------- per-app volume (Volume Mixer)

    private IAudioSessionManager2? _sessions;
    private string _appCacheName = "";
    private DateTime _appCacheAt = DateTime.MinValue;
    private readonly System.Collections.Generic.List<IChannelAudioVolume> _appCache = new();
    private readonly System.Collections.Generic.Dictionary<string, float> _appMuted = new(StringComparer.OrdinalIgnoreCase);

    // Sessions of other apps only expose per-channel volume here (ISimpleAudioVolume is refused),
    // so "volume" = the level of every channel, and "mute" = 0 with the old level remembered.
    private System.Collections.Generic.List<IChannelAudioVolume> AppSessions(string process)
    {
        if (process == _appCacheName && (DateTime.UtcNow - _appCacheAt).TotalSeconds < 2) return _appCache;
        _appCache.Clear();
        _appCacheName = process;
        _appCacheAt = DateTime.UtcNow;
        if (_sessions == null || string.IsNullOrEmpty(process)) return _appCache;
        try
        {
            if (_sessions.GetSessionEnumerator(out IAudioSessionEnumerator list) != 0 || list == null) return _appCache;
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (list.GetSession(i, out IAudioSessionControl ctl) != 0 || ctl is not IAudioSessionControl2 ctl2) continue;
                ctl2.GetProcessId(out uint pid);
                if (pid == 0) continue;
                string name;
                try
                {
                    using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = proc.ProcessName;
                }
                catch
                {
                    continue;
                }
                if (string.Equals(name, process, StringComparison.OrdinalIgnoreCase) && ctl is IChannelAudioVolume vol)
                    _appCache.Add(vol);
            }
        }
        catch
        {
            _appCache.Clear();
        }
        return _appCache;
    }

    private static float GetLevel(IChannelAudioVolume v)
    {
        v.GetChannelCount(out uint n);
        float max = 0;
        for (uint c = 0; c < n; c++)
        {
            v.GetChannelVolume(c, out float l);
            max = Math.Max(max, l);
        }
        return max;
    }

    private static void SetLevel(IChannelAudioVolume v, float level)
    {
        Guid ctx = Guid.Empty;
        v.GetChannelCount(out uint n);
        for (uint c = 0; c < n; c++) v.SetChannelVolume(c, Math.Clamp(level, 0f, 1f), ref ctx);
    }

    public bool TryGetApp(string process, out float volume, out bool muted)
    {
        volume = 0;
        muted = false;
        var list = AppSessions(process);
        if (list.Count == 0) return false;
        try
        {
            volume = GetLevel(list[0]);
            muted = _appMuted.ContainsKey(process) && volume <= 0.0001f;
            if (muted) volume = _appMuted[process];
            return true;
        }
        catch
        {
            _appCacheAt = DateTime.MinValue;
            return false;
        }
    }

    public void SetApp(string process, float value)
    {
        _appMuted.Remove(process);
        foreach (var v in AppSessions(process))
        {
            try { SetLevel(v, value); }
            catch { _appCacheAt = DateTime.MinValue; }
        }
    }

    public void ToggleAppMute(string process)
    {
        var list = AppSessions(process);
        if (list.Count == 0) return;
        try
        {
            if (_appMuted.TryGetValue(process, out float restore))
            {
                _appMuted.Remove(process);
                foreach (var v in list) SetLevel(v, restore <= 0 ? 0.5f : restore);
            }
            else
            {
                _appMuted[process] = GetLevel(list[0]);
                foreach (var v in list) SetLevel(v, 0);
            }
        }
        catch
        {
            _appCacheAt = DateTime.MinValue;
        }
    }

    public AudioService()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            EnsureDevice();
        }
        catch
        {
            _enumerator = null;
        }
    }

    /// <summary>Re-acquires the default output device if it changed (headphones plugged, etc.).</summary>
    public void EnsureDevice()
    {
        if (_enumerator == null) return;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out IMMDevice device) != 0 || device == null)
            {
                _volume = null;
                _meter = null;
                _deviceId = null;
                return;
            }

            device.GetId(out string id);
            if (id == _deviceId && _volume != null)
            {
                Marshal.ReleaseComObject(device);
                return;
            }

            _deviceId = id;
            Guid iidVol = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iidVol, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object vol);
            _volume = (IAudioEndpointVolume)vol;

            Guid iidMeter = typeof(IAudioMeterInformation).GUID;
            device.Activate(ref iidMeter, 23, IntPtr.Zero, out object meter);
            _meter = (IAudioMeterInformation)meter;

            Guid iidSessions = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iidSessions, 23, IntPtr.Zero, out object sessions);
            _sessions = (IAudioSessionManager2)sessions;
            _appCacheAt = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _volume = null;
            _meter = null;
            LastError = ex.Message;
        }
    }

    public string? LastError { get; private set; }

    public float GetPeak()
    {
        try
        {
            if (_meter != null && _meter.GetPeakValue(out float peak) == 0) return peak;
        }
        catch
        {
            // device gone
        }
        return 0f;
    }

    // ---------------------------------------------------------------- peak of the playing app only

    private string _meterApp = "\0";
    private DateTime _meterAt = DateTime.MinValue;
    private readonly System.Collections.Generic.List<IAudioMeterInformation> _appMeters = new();

    /// <summary>
    /// Peak of the app that is playing the media (its own audio sessions), so other apps (Premiere, games,
    /// calls...) don't move the waveform. Falls back to the whole output when its session can't be found.
    /// </summary>
    public float GetSourcePeak(string appId, out bool fromApp)
    {
        fromApp = false;
        if (string.IsNullOrEmpty(appId) || _sessions == null) return GetPeak();
        if (appId != _meterApp || (DateTime.UtcNow - _meterAt).TotalSeconds > 2) FindAppMeters(appId);
        if (_appMeters.Count == 0) return GetPeak();
        float max = 0;
        try
        {
            foreach (var m in _appMeters)
                if (m.GetPeakValue(out float p) == 0 && p > max) max = p;
        }
        catch
        {
            _meterAt = DateTime.MinValue; // session gone: look again next time
            return GetPeak();
        }
        fromApp = true;
        return max;
    }

    private void FindAppMeters(string appId)
    {
        _meterApp = appId;
        _meterAt = DateTime.UtcNow;
        _appMeters.Clear();
        try
        {
            if (_sessions!.GetSessionEnumerator(out IAudioSessionEnumerator list) != 0 || list == null) return;
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (list.GetSession(i, out IAudioSessionControl ctl) != 0 || ctl is not IAudioSessionControl2 ctl2) continue;
                ctl2.GetProcessId(out uint pid);
                if (pid == 0 || !SessionBelongsTo(pid, appId)) continue;
                if (ctl is IAudioMeterInformation meter) _appMeters.Add(meter);
            }
        }
        catch
        {
            _appMeters.Clear();
        }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

    private static string ProcessPath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// Does the audio session of process <paramref name="pid"/> belong to the media app <paramref name="appId"/>
    /// (its AppUserModelID: "Spotify.exe", "MSEdge", "Chrome", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"...)?
    /// </summary>
    private static bool SessionBelongsTo(uint pid, string appId)
    {
        string path = ProcessPath(pid);
        if (path.Length == 0) return false;
        string exe = System.IO.Path.GetFileNameWithoutExtension(path);
        string id = appId.Trim();

        // Store apps: the package folder ("Microsoft.ZuneMusic_11.x_x64__8wekyb3d8bbwe") holds the exe
        int bang = id.IndexOf('!');
        if (bang > 0)
        {
            string family = id[..bang];
            int us = family.LastIndexOf('_');
            if (us > 0 && path.Contains(family[..us], StringComparison.OrdinalIgnoreCase) &&
                path.Contains(family[(us + 1)..], StringComparison.OrdinalIgnoreCase)) return true;
            if (exe.Equals(id[(bang + 1)..], StringComparison.OrdinalIgnoreCase)) return true;
        }

        string name = System.IO.Path.GetFileNameWithoutExtension(id.Contains('\\') ? id : id.Replace('/', '\\'));
        if (exe.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return exe.Equals("Spotify", StringComparison.OrdinalIgnoreCase);
        // Firefox reports a hash of its install folder as its id
        if (id.Length == 16 && System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9A-Fa-f]{16}$"))
            return exe.Equals("firefox", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public bool TryGet(out float volume, out bool muted)
    {
        volume = 0;
        muted = false;
        if (_volume == null) return false;
        try
        {
            _volume.GetMasterVolumeLevelScalar(out volume);
            _volume.GetMute(out muted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetVolume(float value)
    {
        if (_volume == null) return;
        try
        {
            Guid ctx = Guid.Empty;
            _volume.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref ctx);
            _volume.GetMute(out bool muted);
            if (muted && value > 0) _volume.SetMute(false, ref ctx);
        }
        catch
        {
            // ignore
        }
    }

    public void ToggleMute()
    {
        if (_volume == null) return;
        try
        {
            Guid ctx = Guid.Empty;
            _volume.GetMute(out bool muted);
            _volume.SetMute(!muted, ref ctx);
        }
        catch
        {
            // ignore
        }
    }

    // ---- COM interop -------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid ctx);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolume(uint index, out float level);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
