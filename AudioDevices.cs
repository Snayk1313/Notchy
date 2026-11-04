using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Notchy;

public enum OutputKind { Speakers, Headphones, Headset, Display, Bluetooth, Other }

public sealed record OutputDevice(string Id, string Name, OutputKind Kind, bool IsDefault);

/// <summary>Windows playback devices: list them and make one the default (like the taskbar's output picker).</summary>
public static class AudioDevices
{
    public static string? LastError { get; private set; }

    public static List<OutputDevice> List()
    {
        var result = new List<OutputDevice>();
        try
        {
            // the enumerator is shared per process (AudioService has one too): create it untyped, then QI
            var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            string? defId = null;
            if (en.GetDefaultAudioEndpoint(0, 1 /* eMultimedia */, out IMMDevice def) == 0 && def != null)
            {
                def.GetId(out defId);
                Marshal.ReleaseComObject(def);
            }
            if (en.EnumAudioEndpoints(0 /* eRender */, 1 /* ACTIVE */, out IMMDeviceCollection col) != 0 || col == null) return result;
            col.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (col.Item(i, out IMMDevice d) != 0 || d == null) continue;
                try
                {
                    d.GetId(out string id);
                    string name = id;
                    var kind = OutputKind.Other;
                    if (d.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore props) == 0 && props != null)
                    {
                        name = ReadString(props, PKEY_Device_FriendlyName) ?? id;
                        uint? ff = ReadUInt(props, PKEY_AudioEndpoint_FormFactor);
                        string desc = ReadString(props, PKEY_Device_DeviceDesc) ?? "";
                        string ep = (ReadString(props, PKEY_Device_EnumeratorName) ?? "") + "|" + (ReadString(props, PKEY_Device_InterfaceFriendlyName) ?? "");
                        bool bt = ep.Contains("BTH", StringComparison.OrdinalIgnoreCase) || ep.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                                  || name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
                        kind = ff switch
                        {
                            3 => OutputKind.Headphones,
                            5 => OutputKind.Headset,
                            9 => OutputKind.Display,
                            1 => OutputKind.Speakers,
                            _ => desc.Contains("Head", StringComparison.OrdinalIgnoreCase) || desc.Contains("Fone", StringComparison.OrdinalIgnoreCase)
                                ? OutputKind.Headphones : OutputKind.Other
                        };
                        if (bt && kind is OutputKind.Other or OutputKind.Speakers) kind = OutputKind.Bluetooth;
                        if (bt && kind == OutputKind.Headphones && ff != 3) kind = OutputKind.Bluetooth;
                        Marshal.ReleaseComObject(props);
                    }
                    result.Add(new OutputDevice(id, name, kind, id == defId));
                }
                finally
                {
                    Marshal.ReleaseComObject(d);
                }
            }
            Marshal.ReleaseComObject(col);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message; // no audio stack: empty list
        }
        return result;
    }

    /// <summary>Makes the device the default for every role (console, multimedia, communications).</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= pc.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static int Glyph(OutputKind k) => k switch
    {
        OutputKind.Headphones => 0xE7F6,
        OutputKind.Headset => 0xE95B,
        OutputKind.Display => 0xE7F4,
        OutputKind.Bluetooth => 0xE702,
        _ => 0xE7F5
    };

    // ---- property store ----------------------------------------------------

    private static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey PKEY_Device_DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey PKEY_Device_EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey PKEY_Device_InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey PKEY_AudioEndpoint_FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);

    private static string? ReadString(IPropertyStore props, PropertyKey key)
    {
        var k = key;
        if (props.GetValue(ref k, out PropVariant pv) != 0) return null;
        try
        {
            return pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    private static uint? ReadUInt(IPropertyStore props, PropertyKey key)
    {
        var k = key;
        if (props.GetValue(ref k, out PropVariant pv) != 0) return null;
        try
        {
            return pv.vt is 19 /* VT_UI4 */ or 3 /* VT_I4 */ ? pv.ui : null;
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
        public PropertyKey(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public uint ui;
    }

    // ---- COM interop -------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
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

    /// <summary>Undocumented but stable since Windows 7: what the Sound control panel uses to set the default device.</summary>
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
