using System;
using System.IO;
using System.Net.Sockets;

namespace Notchy;

public static class Program
{
    // 47823 is used by another island app (TwoCode); keep clear of it.
    public const int HookPort = 47931;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--hook")
            return RunHook();
        if (args.Length > 0 && args[0] is "--install-hooks" or "--uninstall-hooks")
        {
            try
            {
                if (args[0] == "--install-hooks") ClaudeHooks.Install();
                else ClaudeHooks.Uninstall();
                return 0;
            }
            catch (Exception ex)
            {
                App.Log(ex);
                return 1;
            }
        }

        if (args.Length > 1 && args[0] == "--export-icon")
        {
            ExportIcon(args[1]);
            return 0;
        }

        // install / update / uninstall events from the installer, then keep hooks + autostart on this exe
        Updater.Bootstrap();
        Updater.FixPaths();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>Writes the app icon (the tray drawing) as a multi-size .ico, for the exe and the installer.</summary>
    private static void ExportIcon(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var pngs = new System.Collections.Generic.List<byte[]>();
        foreach (int s in sizes)
        {
            using var icon = TrayIconFactory.Create(s);
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    /// <summary>
    /// Called by Claude Code hooks: forwards the hook JSON (stdin) to the running island.
    /// Always exits 0 quickly so it never blocks or disturbs Claude.
    /// </summary>
    private static int RunHook()
    {
        // Requests made by Notchy Chat itself run through Claude Code too: don't report them as tasks.
        if (Environment.GetEnvironmentVariable("NOTCHY_CHAT") == "1") return 0;
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            if (ms.Length == 0) return 0;

            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", HookPort);
            if (!connect.Wait(400) || !client.Connected) return 0;
            using var net = client.GetStream();
            net.WriteTimeout = 800;
            ms.Position = 0;
            ms.CopyTo(net);
        }
        catch
        {
            // the island is not running — ignore
        }
        return 0;
    }
}
