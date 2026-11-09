using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Notchy;

/// <summary>
/// The icon of a desktop app that sends notifications. Windows' notification API only has logos for Store apps;
/// the others are found by what Windows registered for them (tray apps such as Lightshot get a generated image),
/// by their entry in the Start menu, or by the program that's running.
/// </summary>
internal static class DesktopAppIcon
{
    private const int IconSize = 96;

    public static BitmapSource? Find(string appId, string appName) =>
        FromShell(@"shell:AppsFolder\" + appId) ?? FromRunningApp(appName) ?? FromRegistry(appId); // (sharpest first: the registered image is small)

    /// <summary>The image Windows keeps for this app id (usually a generated file in the temporary folder, which may be gone).</summary>
    private static BitmapSource? FromRegistry(string appId)
    {
        if (appId.Length == 0) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Classes\AppUserModelId\" + appId);
                if (key?.GetValue("IconUri") is string path && File.Exists(path) && DecodeFile(path) is { } icon) return icon;
            }
            catch
            {
                // try the next way
            }
        }
        return null;
    }

    private static BitmapSource? FromRunningApp(string appName)
    {
        foreach (var process in Process.GetProcessesByName(appName))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } exe && FromShell(exe) is { } icon) return icon;
                }
                catch
                {
                    // a program Notchy isn't allowed to look at
                }
            }
        }
        return null;
    }

    private static BitmapSource? DecodeFile(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? FromShell(string parsingName)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
            ((IShellItemImageFactory)item).GetImage(new NativeSize { Width = IconSize, Height = IconSize }, IconOnly | BiggerSizeOk, out bitmap);
            return ToBitmapSource(bitmap);
        }
        catch
        {
            return null; // Windows doesn't know this name
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    /// <summary>Keeps the transparency, which WPF's own conversion of a bitmap handle loses.</summary>
    private static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        int width = info.Width, height = info.Height;
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top line first
            Planes = 1,
            BitCount = 32
        };
        var pixels = new byte[width * height * 4];
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            DeleteDC(dc);
        }
        bool hasAlpha = pixels.Where((_, i) => i % 4 == 3).Any(a => a != 0);
        var source = BitmapSource.Create(width, height, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    private const int BiggerSizeOk = 0x1, IconOnly = 0x4;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out NativeBitmap info);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] pixels, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

#if DEBUG
    /// <summary>Test: saves what each way finds for the given apps.</summary>
    public static void SaveTest(string folder, string appId, string appName)
    {
        void Save(string name, BitmapSource? icon)
        {
            if (icon == null) { File.AppendAllText(Path.Combine(folder, "result.txt"), $"{appName} {name}: nada\n"); return; }
            File.AppendAllText(Path.Combine(folder, "result.txt"), $"{appName} {name}: {icon.PixelWidth}x{icon.PixelHeight}\n");
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(icon));
            using var file = File.Create(Path.Combine(folder, $"{appName}-{name}.png"));
            png.Save(file);
        }
        Save("registro", FromRegistry(appId));
        Save("iniciar", FromShell(@"shell:AppsFolder\" + appId));
        Save("programa", FromRunningApp(appName));
    }
#endif
}
