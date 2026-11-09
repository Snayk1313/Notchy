using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Notchy;

public partial class App : Application
{
    private Mutex? _mutex;
    private static System.Windows.Forms.NotifyIcon? _tray;
    private static SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        int spd = Array.IndexOf(e.Args, "--spdiag");
        if (spd >= 0 && spd + 1 < e.Args.Length)
        {
            base.OnStartup(e);
            string outFile = e.Args[spd + 1];
            Dispatcher.InvokeAsync(async () =>
            {
                var sp = new SpotifyService();
                string r = await sp.DiagnoseAsync() + Environment.NewLine +
                           (Array.IndexOf(e.Args, "--like") >= 0 ? await sp.LikeRoundTripAsync() : "");
                System.IO.File.WriteAllText(outFile, r);
                Shutdown();
            });
            return;
        }

        int selTest = Array.IndexOf(e.Args, "--seltest");
        if (selTest >= 0 && selTest + 1 < e.Args.Length)
        {
            // debug: read the selection of the current foreground window both ways
            base.OnStartup(e);
            string outFile = e.Args[selTest + 1];
            Dispatcher.InvokeAsync(async () =>
            {
                var fg = ScreenContext.ForegroundWindow();
                var a = await ScreenContext.FocusedSelectionAsync(fg);
                var b = await ScreenContext.SelectionViaCopyAsync(fg);
                System.IO.File.WriteAllText(outFile, $"app={ScreenContext.AppName(fg)}" + Environment.NewLine +
                    $"uia={a?.Text} sel={a?.FromSelection}" + Environment.NewLine + $"copy={b?.Text} sel={b?.FromSelection}");
                Shutdown();
            });
            return;
        }

        int diag = Array.IndexOf(e.Args, "--diag");
        if (diag >= 0 && diag + 3 < e.Args.Length)
        {
            // debug: read the text at a screen point and the playing app's volume, write them to a file
            base.OnStartup(e);
            int dx = int.Parse(e.Args[diag + 1]), dy = int.Parse(e.Args[diag + 2]);
            string outFile = e.Args[diag + 3];
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var t = await ScreenContext.TextAtAsync(dx, dy);
                AudioService audio = null!;
                bool ok = false; float v = 0; bool m = false;
                Dispatcher.Invoke(() =>
                {
                    audio = new AudioService(); // same thread as the real app (UI / STA)
                    ok = audio.TryGetApp("Spotify", out v, out m);
                });
                System.IO.File.WriteAllText(outFile,
                    $"text[{t?.App}|sel={t?.FromSelection}]: {t?.Text}" + Environment.NewLine +
                    $"spotifySession={ok} volume={v:0.00} muted={m}" + Environment.NewLine);
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

        int lens = Array.IndexOf(e.Args, "--lens");
        if (lens >= 0 && lens + 1 < e.Args.Length)
        {
            // debug: open only the "draw to search" overlay and write the result path to <dir>\lens-result.txt
            base.OnStartup(e);
            string dir = e.Args[lens + 1];
            _ = LensWindow.SelectAsync(dir).ContinueWith(t =>
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lens-result.txt"), t.Result ?? "(cancelled)");
                Dispatcher.Invoke(Shutdown);
            });
            return;
        }

#endif
#if DEBUG
        int snap = Array.IndexOf(e.Args, "--snapshot");
        bool snapshot = snap >= 0 && snap + 1 < e.Args.Length;

        if (!snapshot)
#endif
        {
            _mutex = new Mutex(true, "Notchy.SingleInstance", out bool created);
            if (!created)
            {
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);
        Loc.Language = AppSettings.Current.Language;

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

        // Soft click on every button / toggle in the app.
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is ToggleButton) SoundService.Play(Sfx.Toggle);
                else if (s is Button b && b.Tag as string != "nosound") SoundService.Play(Sfx.Click);
            }));

        SoundService.Warmup();

#if DEBUG
        if (snapshot)
        {
            // preview overrides (not saved): NOTCHY_GLOW=0..3, NOTCHY_GRADIENT=0..100
            if (int.TryParse(Environment.GetEnvironmentVariable("NOTCHY_GLOW"), out int glow)) AppSettings.Current.GlowLevel = glow;
            if (int.TryParse(Environment.GetEnvironmentVariable("NOTCHY_GRADIENT"), out int grad)) AppSettings.Current.GradientStrength = grad;
        }

#endif
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

#if DEBUG
        if (snapshot)
        {
            _ = window.SnapshotAsync(e.Args[snap + 1]);
            return;
        }
        int rt = Array.IndexOf(e.Args, "--replacetest");
        if (rt >= 0 && rt + 1 < e.Args.Length)
        {
            _ = window.ReplaceSelfTestAsync(e.Args[rt + 1]);
            return;
        }

#endif
        _tray = CreateTray();
        AppSettings.Changed += () => Loc.Language = AppSettings.Current.Language;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        OsdHider.Restore(); // Windows gets its volume pop-up back
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }

    public static void OpenSettings(string? page = null)
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (page != null) _settingsWindow.ShowPage(page);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    public static void Notify(string title, string text)
    {
        if (SoundService.Hushed) return; // "Não perturbe" while focusing
        try
        {
            _tray?.ShowBalloonTip(4000, title, string.IsNullOrWhiteSpace(text) ? " " : text, System.Windows.Forms.ToolTipIcon.None);
        }
        catch
        {
            // ignore
        }
    }

    private System.Windows.Forms.NotifyIcon CreateTray()
    {
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIconFactory.Create(),
            Text = "Notchy",
            Visible = true
        };
        // right click: Notchy's own menu (dark, rounded, with a real switch) instead of Windows' menu
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right) TrayMenu.ShowAtCursor();
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        return tray;
    }

    internal static void Log(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // ignore logging failures
        }
    }
}

internal static class TrayIconFactory
{
    public static System.Drawing.Icon Create(int size = 32)
    {
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.ScaleTransform(size / 32f, size / 32f);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            var r = new System.Drawing.RectangleF(1, 8, 30, 16);
            float d = 16;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var black = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            g.FillPath(white, path);
            g.FillEllipse(black, 11, 12, 4, 8);
            g.FillEllipse(black, 17, 12, 4, 8);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>The same icon as a WPF image (for window / taskbar icons).</summary>
    public static System.Windows.Media.ImageSource CreateImage(int size = 64)
    {
        using var icon = Create(size);
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
