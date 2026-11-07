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
    /// Always exits 0 so it never disturbs Claude. A permission request waits for the answer given on the
    /// island and prints it; meanwhile Claude shows its own prompt too, and whichever answers first wins.
    /// No answer (island closed, answered elsewhere) prints nothing and changes nothing.
    /// </summary>
    private static int RunHook()
    {
        // Requests made by Notchy Chat itself run through Claude Code too: don't report them as tasks.
        if (Environment.GetEnvironmentVariable("NOTCHY_CHAT") == "1") return 0;
        try
        {
            // grab stdout before anything can touch the console (finding the host attaches to consoles)
            using var stdout = Console.OpenStandardOutput();
            using var stdin = Console.OpenStandardInput();
            using var ms = new MemoryStream();
            stdin.CopyTo(ms);
            if (ms.Length == 0) return 0;

            byte[] payload = ms.ToArray();
            string text = System.Text.Encoding.UTF8.GetString(payload);
            bool permission = text.Contains("\"PermissionRequest\"", StringComparison.Ordinal);
            // where the session runs, so the island can bring it back (only on the few events that need it)
            if (permission || text.Contains("\"UserPromptSubmit\"", StringComparison.Ordinal) ||
                text.Contains("\"SessionStart\"", StringComparison.Ordinal))
                payload = WithHost(text) ?? payload;

            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", HookPort);
            if (!connect.Wait(400) || !client.Connected) return 0;
            using var net = client.GetStream();
            net.WriteTimeout = 800;
            net.Write(payload, 0, payload.Length);
            if (!permission) return 0;

            // "that's all" (a 0 byte, not a half-close: the island reads a closed connection as "the hook is gone");
            // the island answers on the same connection
            net.WriteByte(0);
            net.ReadTimeout = 595_000; // the hook's own timeout is 600 s
            using var answer = new MemoryStream();
            net.CopyTo(answer);
            if (answer.Length > 0)
            {
                answer.Position = 0;
                answer.CopyTo(stdout);
                stdout.Flush();
            }
        }
        catch
        {
            // the island is not running, or went away — ignore
        }
        return 0;
    }

    private static byte[]? WithHost(string json)
    {
        try
        {
            var host = ClaudeHost.Find();
            if (host == null || System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject root) return null;
            root["notchy_host"] = new System.Text.Json.Nodes.JsonObject
            {
                ["pid"] = host.Pid,
                ["hwnd"] = host.Hwnd,
                ["exe"] = host.Exe
            };
            return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        }
        catch
        {
            return null;
        }
    }
}
