using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Notchy;

/// <summary>
/// Battery of wireless headsets whose USB receiver doesn't tell Windows: Notchy asks the receiver itself over HID.
/// HyperX Cloud Alpha Wireless (03F0:098D): write <c>21 BB cmd</c>, the answer's byte 3 is the value
/// (03 = headset off? 1 = yes · 0C = charging? 1 = yes · 0B = battery %). Protocol facts from public notes
/// (HeadsetControl); the code is Notchy's own.
/// Rapoo 2.4 GHz mice: the receiver's vendor collection FF00:0002 pushes status report 7 every ~3 s; byte 8 (counting the
/// report id) is the battery.
/// </summary>
public static class HidBattery
{
    public sealed record Reading(string Name, int Percent, bool Charging);

    private const ushort HyperXVid = 0x03F0, CloudAlphaWirelessPid = 0x098D;
    private static string? _workingPath; // the receiver's interface that answered last time

    /// <summary>The HyperX headset's battery, or null when there's no receiver or the headset is off. Blocking (≈ up to a few seconds): call off the UI thread.</summary>
    public static Reading? ReadHyperX(Action<string>? log = null)
    {
        var paths = DevicePaths(HyperXVid, CloudAlphaWirelessPid);
        if (_workingPath != null && paths.Remove(_workingPath)) paths.Insert(0, _workingPath);
        foreach (var path in paths)
        {
            try
            {
                using var dev = Open(path, out int outLen, out int inLen, out _);
                if (dev == null) { log?.Invoke($"{path}: não abriu"); continue; }
                log?.Invoke($"{path}: saída={outLen} entrada={inLen}");
                if (outLen < 3 || inLen < 4) continue;
                var off = Ask(dev, outLen, inLen, 0x03, log);
                log?.Invoke($"  03 → {Hex(off)}");
                if (off == null) continue;
                _workingPath = path;
                if (off[3] == 1) return null; // (the receiver is in, the headset is off)
                var charging = Ask(dev, outLen, inLen, 0x0C, log);
                var level = Ask(dev, outLen, inLen, 0x0B, log);
                log?.Invoke($"  0C → {Hex(charging)}  0B → {Hex(level)}");
                if (level == null) return null;
                return new Reading("HyperX Cloud Alpha", Math.Clamp((int)level[3], 0, 100), charging?[3] == 1);
            }
            catch (Exception e)
            {
                log?.Invoke($"  erro: {e.GetType().Name}: {e.Message}");
            }
        }
        return null;
    }

    /// <summary>Sends <c>21 BB cmd</c> (padded to the output report) and waits up to 1 s for the matching answer.</summary>
    private static byte[]? Ask(FileStream dev, int outLen, int inLen, byte cmd, Action<string>? log = null)
    {
        var req = new byte[outLen];
        req[0] = 0x21;
        req[1] = 0xBB;
        req[2] = cmd;
        using var cts = new CancellationTokenSource(1000);
        try
        {
            dev.WriteAsync(req, 0, req.Length, cts.Token).GetAwaiter().GetResult();
            var buf = new byte[inLen];
            // other reports (volume keys, status pushes) may come first: wait for the one echoing the command
            for (int i = 0; i < 8; i++)
            {
                int n = dev.ReadAsync(buf, 0, buf.Length, cts.Token).GetAwaiter().GetResult();
                log?.Invoke($"    lido {cmd:X2}: {Hex(buf)}");
                if (n >= 4 && buf[0] == 0x21 && buf[1] == 0xBB && buf[2] == cmd) return buf;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        return null;
    }

    private static Reading? _rapooLast;
    /// <summary>The last status report as it came (tests compare it with and without the charging cable).</summary>
    public static string? RapooRaw;
    private static DateTime _rapooAt;
    private static int _rapooWatching;

    /// <summary>
    /// A Rapoo mouse's battery: the receiver pushes it by itself (every few seconds while the mouse is on), so a
    /// background reader keeps the last one. Two layouts: report 07 (VT series, battery at byte 8) and report BB
    /// (7 bytes, battery at byte 6, e.g. 24AE:1227 "Rapoo Gaming Device"). Null when nothing came in the last 2 minutes.
    /// </summary>
    public static Reading? ReadRapoo()
    {
        if (Interlocked.Exchange(ref _rapooWatching, 1) == 0) Task.Run(WatchRapoo);
        return DateTime.UtcNow - _rapooAt < TimeSpan.FromMinutes(2) ? _rapooLast : null;
    }

    private static async Task WatchRapoo()
    {
        while (true)
        {
            var readers = new List<Task>();
            foreach (var path in DevicePaths(0x24AE, null))
            {
                var dev = Open(path, out _, out int inLen, out uint usage);
                if (dev == null || usage != 0xFF000002 || inLen < 7) { dev?.Dispose(); continue; }
                readers.Add(Task.Run(async () =>
                {
                    using (dev)
                    {
                        var buf = new byte[inLen];
                        try
                        {
                            while (true)
                            {
                                int n = await dev.ReadAsync(buf, 0, buf.Length);
                                RapooRaw = BitConverter.ToString(buf, 0, n);
                                int pct = n >= 9 && buf[0] == 0x07 ? buf[8] : n >= 7 && buf[0] == 0xBB ? buf[6] : -1;
                                // BB: byte 5 is the power state, 1 = on battery, 2 = charging, 0 = switching (its level is junk)
                                bool bb = buf[0] == 0xBB, charging = bb && buf[5] == 2;
                                if (pct is > 0 and <= 100 && !(bb && buf[5] == 0))
                                {
                                    _rapooLast = new Reading("Mouse Rapoo", pct, charging);
                                    _rapooAt = DateTime.UtcNow;
                                }
                            }
                        }
                        catch (Exception) { } // (unplugged: looked for again below)
                    }
                }));
            }
            if (readers.Count > 0) await Task.WhenAll(readers);
            await Task.Delay(TimeSpan.FromSeconds(30)); // (no receiver, or it was unplugged: look again in a while)
        }
    }

    private static string Hex(byte[]? b) => b == null ? "sem resposta" : BitConverter.ToString(b, 0, Math.Min(8, b.Length));

    // ------------------------------------------------------------------ Win32 HID

    private static FileStream? Open(string path, out int outLen, out int inLen, out uint usage)
    {
        outLen = inLen = 0;
        usage = 0;
        var h = CreateFile(path, 0xC0000000 /* read | write */, 3 /* share read | write */, IntPtr.Zero, 3 /* open existing */, 0x40000000 /* overlapped */, IntPtr.Zero);
        if (h.IsInvalid) return null;
        if (HidD_GetPreparsedData(h, out var pp))
        {
            if (HidP_GetCaps(pp, out var caps) == 0x00110000) // HIDP_STATUS_SUCCESS
            {
                outLen = caps.OutputReportByteLength;
                usage = (uint)caps.UsagePage << 16 | caps.Usage;
                inLen = caps.InputReportByteLength;
            }
            HidD_FreePreparsedData(pp);
        }
        return new FileStream(h, FileAccess.ReadWrite, 0, true);
    }

    private static List<string> DevicePaths(ushort vid, ushort? pid)
    {
        var list = new List<string>();
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12 /* present | device interface */);
        if (set == new IntPtr(-1)) return list;
        try
        {
            string want = pid is ushort p ? $"vid_{vid:x4}&pid_{p:x4}" : $"vid_{vid:x4}&";
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4);
                    if (path != null && path.Contains(want, StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr p, int flags);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int need, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint mode, uint flags, IntPtr template);
}
