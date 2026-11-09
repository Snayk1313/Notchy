using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Notchy;

/// <summary>A paired Bluetooth audio device (earbuds, headphones, speaker).</summary>
/// <param name="Battery">0..100 when Windows knows it (the device reports it over Hands-Free), else null.</param>
public sealed record BudsInfo(string Id, string Name, bool Connected, int? Battery, bool Speaker);

/// <summary>
/// Watches paired Bluetooth audio devices: raises <see cref="Connected"/> / <see cref="Disconnected"/> when they come
/// and go, and <see cref="BatteryLow"/> once at 15% and once at 5% (per connection). The battery is what Windows
/// shows in Settings (one value for the pair; read from the device's Hands-Free node). Mice, keyboards and phones
/// are ignored. Events come on a background thread.
/// </summary>
public sealed class BluetoothAudioService : IDisposable
{
    private const string ConnectedKey = "System.Devices.Aep.IsConnected";
    private const string MajorKey = "System.Devices.Aep.Bluetooth.Cod.Major", MinorKey = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private static readonly string[] AepProps = { ConnectedKey, MajorKey, MinorKey };
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private DeviceWatcher? _watcher;
    private bool _enumerated;
    private readonly ConcurrentDictionary<string, BudsInfo> _devices = new();
    private readonly ConcurrentDictionary<string, int> _warned = new(); // lowest level already warned about, per device
    private readonly HashSet<string> _notAudio = new();
    private System.Threading.Timer? _poll;

    public event Action<BudsInfo>? Connected, Disconnected, BatteryLow;

    /// <summary>Every paired audio device seen so far.</summary>
    public IEnumerable<BudsInfo> Known => _devices.Values;

    /// <summary>The audio devices connected right now.</summary>
    public IEnumerable<BudsInfo> ConnectedDevices => _devices.Values.Where(d => d.Connected);

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Trace(string msg)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("NOTCHY_DEBUG") != "1") return;
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.Folder, "bt-debug.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
#endif
    }

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(BluetoothDevice.GetDeviceSelectorFromPairingState(true), AepProps,
                DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (w, d) => _ = OnAddedAsync(d.Id, d.Name, d.Properties);
            _watcher.Updated += (w, u) =>
            {
                if (u.Properties.TryGetValue(ConnectedKey, out var v) && v is bool c) _ = OnConnectionAsync(u.Id, c);
            };
            _watcher.Removed += (w, u) => _devices.TryRemove(u.Id, out var gone);
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
            // the battery changes slowly: check the connected ones now and then
            _poll = new System.Threading.Timer(_ => _ = PollBatteryAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        catch (Exception ex)
        {
            App.Log(ex); // no Bluetooth on this PC
        }
    }

    private async Task OnAddedAsync(string id, string name, IReadOnlyDictionary<string, object> props)
    {
        bool connected = props.TryGetValue(ConnectedKey, out var v) && v is bool c && c;
        var kind = AudioKind(id, props);
        Trace($"added '{name}' connected={connected} audio={kind?.ToString() ?? "no"} id={id}");
#if DEBUG
        if (kind != null && Environment.GetEnvironmentVariable("NOTCHY_DEBUG") == "1") Trace("battery read: " + ((await ReadBatteryAsync(name))?.ToString() ?? "none"));
#endif
        if (kind == null) return;
        int? battery = connected ? await ReadBatteryAsync(name) : null;
        var info = new BudsInfo(id, name, connected, battery, kind.Value);
        _devices[id] = info;
        // already connected when Notchy starts: no "connected" pop-up, but a low battery still warns
        if (connected) CheckLow(info);
        if (connected && _enumerated) Connected?.Invoke(info);
    }

    private async Task OnConnectionAsync(string id, bool connected)
    {
        lock (_notAudio)
            if (_notAudio.Contains(id)) return;
        if (!_devices.TryGetValue(id, out var info))
        {
            // paired while Notchy runs
            try
            {
                var di = await DeviceInformation.CreateFromIdAsync(id, AepProps);
                var props = new Dictionary<string, object>(di.Properties) { [ConnectedKey] = connected };
                await OnAddedAsync(id, di.Name, props);
            }
            catch
            {
                // ignore
            }
            return;
        }
        if (info.Connected == connected) return;
        if (!connected)
        {
            info = info with { Connected = false };
            _devices[id] = info;
            _warned.TryRemove(id, out _);
            Disconnected?.Invoke(info);
            return;
        }
        // Windows fills in the battery a moment after the audio profile connects
        int? battery = await ReadBatteryAsync(info.Name);
        if (battery == null)
        {
            await Task.Delay(2500);
            battery = await ReadBatteryAsync(info.Name);
        }
        info = info with { Connected = true, Battery = battery };
        _devices[id] = info;
        Connected?.Invoke(info);
        CheckLow(info);
    }

    private async Task PollBatteryAsync()
    {
        foreach (var info in _devices.Values.Where(d => d.Connected).ToList())
        {
            int? b = await ReadBatteryAsync(info.Name);
            if (b == null || b == info.Battery) continue;
            var now = info with { Battery = b };
            _devices[info.Id] = now;
            CheckLow(now);
        }
    }

    /// <summary>Warns once at 15% and once more at 5%.</summary>
    private void CheckLow(BudsInfo info)
    {
        if (info.Battery is not int b) return;
        int level = b <= 5 ? 5 : b <= 15 ? 15 : 100;
        if (level == 100) return;
        int warned = _warned.TryGetValue(info.Id, out var w) ? w : 100;
        if (level >= warned) return;
        _warned[info.Id] = level;
        BatteryLow?.Invoke(info);
    }

    /// <summary>
    /// Audio device? (true = a speaker, false = earbuds/headphones, null = not audio: mouse, phone…), from the
    /// Bluetooth class of device Windows reports (major 4 = audio/video; minor 5 = loudspeaker).
    /// </summary>
    private bool? AudioKind(string id, IReadOnlyDictionary<string, object> props)
    {
        int major = props.TryGetValue(MajorKey, out var ma) && ma != null ? Convert.ToInt32(ma) : -1;
        int minor = props.TryGetValue(MinorKey, out var mi) && mi != null ? Convert.ToInt32(mi) : -1;
        if (major == 4) return minor == 5;
        lock (_notAudio)
            _notAudio.Add(id);
        return null;
    }

    /// <summary>
    /// The battery Windows shows for the device. It lives on the device's Hands-Free node ("Buds3 de Pedro
    /// Hands-Free AG"), which only exists while it's connected: look for nodes whose name starts with the device's.
    /// </summary>
    private static async Task<int?> ReadBatteryAsync(string name)
    {
        try
        {
            // (an AQS name filter doesn't find the Hands-Free node on every Windows: look through all the devices)
            var all = await DeviceInformation.FindAllAsync("", new[] { BatteryKey }, DeviceInformationKind.Device);
            var best = Best(all, name);
            Trace($"battery by scan: {best?.ToString() ?? "none"} ({all.Count} nodes)");
            return best;
        }
        catch (Exception ex)
        {
            Trace("battery read failed: " + ex.Message);
            return null;
        }
    }

    private static int? Best(IReadOnlyList<DeviceInformation> nodes, string name)
    {
        int? best = null;
        foreach (var n in nodes)
            if (n.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && n.Properties.TryGetValue(BatteryKey, out var v) && v != null)
            {
                int b = Convert.ToInt32(v);
                if (b is >= 0 and <= 100) best = Math.Max(best ?? 0, b);
            }
        return best;
    }

    /// <summary>Battery of a connected device whose name is part of <paramref name="outputName"/> (Windows' audio output name).</summary>
    public int? BatteryFor(string outputName) =>
        _devices.Values.FirstOrDefault(d => d.Connected && d.Battery != null && outputName.Contains(d.Name, StringComparison.OrdinalIgnoreCase))?.Battery;

    public void Dispose()
    {
        _poll?.Dispose();
        try
        {
            _watcher?.Stop();
        }
        catch
        {
            // ignore
        }
    }
}
