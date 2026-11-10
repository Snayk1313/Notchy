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
    private const int IconSize = 96, AccentSampleSize = 32, AccentHues = 12;
    private const double AccentMinBrightness = 0.85;

    public static BitmapSource? Find(string appId, string appName) =>
        FromShell(@"shell:AppsFolder\" + appId) ?? FromRunningApp(appName) ?? FromRegistry(appId); // (sharpest first: the registered image is small)

    /// <summary>The icon's main colour, bright enough to glow on a dark island; null for a greyish icon.</summary>
    public static Color? Accent(BitmapSource? icon)
    {
        if (icon == null) return null;
        try
        {
            double shrink = (double)AccentSampleSize / Math.Max(icon.PixelWidth, icon.PixelHeight);
            var small = new FormatConvertedBitmap(new TransformedBitmap(icon, new ScaleTransform(shrink, shrink)), PixelFormats.Bgra32, null, 0);
            int width = small.PixelWidth, height = small.PixelHeight, stride = width * 4;
            var pixels = new byte[stride * height];
            small.CopyPixels(pixels, stride, 0);

            // the colour family with the most vivid pixels wins (an average of everything would come out muddy)
            var weight = new double[AccentHues];
            var red = new double[AccentHues];
            var green = new double[AccentHues];
            var blue = new double[AccentHues];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 128) continue;
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), range = max - min;
                if (range < 0.12) continue; // white, black and greys say nothing about the app
                double hue = max == r ? (g - b) / range % 6 : max == g ? (b - r) / range + 2 : (r - g) / range + 4;
                int bin = (int)((hue + 6) % 6 / 6 * AccentHues) % AccentHues;
                double w = range * max;
                weight[bin] += w;
                red[bin] += r * w;
                green[bin] += g * w;
                blue[bin] += b * w;
            }
            int best = Array.IndexOf(weight, weight.Max());
            if (weight[best] <= 0) return null;
            double R = red[best] / weight[best], G = green[best] / weight[best], B = blue[best] / weight[best];
            double top = Math.Max(R, Math.Max(G, B));
            if (top < AccentMinBrightness) (R, G, B) = (R * AccentMinBrightness / top, G * AccentMinBrightness / top, B * AccentMinBrightness / top);
            return Color.FromRgb((byte)(R * 255), (byte)(G * 255), (byte)(B * 255));
        }
        catch
        {
            return null;
        }
    }

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
        var chosen = Find(appId, appName);
        File.AppendAllText(Path.Combine(folder, "result.txt"), $"{appName} cor: {Accent(chosen)}\n");
        Save("registro", FromRegistry(appId));
        Save("iniciar", FromShell(@"shell:AppsFolder\" + appId));
        Save("programa", FromRunningApp(appName));
    }
#endif
}
