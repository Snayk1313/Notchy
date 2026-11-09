// The card shown while Notchy is being replaced by a new version. It's a separate little program because Notchy has to be
// closed during the update; it runs from the temp folder and uses the .NET Framework that comes with Windows.
// Built by the csc.exe of that framework (see Notchy.csproj), so this file stays in C# 5 and plain ASCII.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NotchyUpdateCard
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var options = new Dictionary<string, string>();
            for (int i = 0; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
            Process setup = Installer.HasSetup ? Installer.Start(options) : null;
            new Application().Run(new CardWindow(options, setup));
        }
    }

    /// <summary>
    /// When the release adds the regular setup inside this program (see wrap-setup.ps1), this is the installer people download:
    /// it runs that setup silently and shows the card instead of the setup's own window.
    /// </summary>
    internal static class Installer
    {
        private const string SetupResource = "Setup.exe", InfoResource = "install-info.txt";

        public static DateTime StartedUtc;

        public static bool HasSetup
        {
            get { return typeof(Installer).Assembly.GetManifestResourceInfo(SetupResource) != null; }
        }

        public static Process Start(Dictionary<string, string> options)
        {
            string[] info;
            using (var reader = new StreamReader(typeof(Installer).Assembly.GetManifestResourceStream(InfoResource)))
                info = reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string packId = info[0], version = info[1], total = info[2];

            string root;
            if (!options.TryGetValue("--root", out root))
                root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), packId);
            bool portuguese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
            options["--current"] = System.IO.Path.Combine(root, "current");
            options["--app"] = System.IO.Path.Combine(root, "Notchy.exe");
            options["--total"] = total;
            options["--title"] = portuguese ? "Instalando o Notchy" : "Installing Notchy";
            options["--subtitle"] = (portuguese ? "Versão " : "Version ") + version;
            options["--eye"] = "#FFFFFF";
            if (!options.ContainsKey("--mutex")) options["--mutex"] = "Notchy.SingleInstance";

            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "notchy-card-setup-" + Guid.NewGuid().ToString("N") + ".exe");
            using (var embedded = typeof(Installer).Assembly.GetManifestResourceStream(SetupResource))
            using (var output = File.Create(file))
                embedded.CopyTo(output);

            StartedUtc = DateTime.UtcNow;
            return Process.Start(new ProcessStartInfo(file, "--silent") { UseShellExecute = false, CreateNoWindow = true });
        }
    }

    internal sealed class CardWindow : Window
    {
        private const double BarWidth = 190;
        private const int LastExtractingPercent = 97, FinishingPercent = 98;
        private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);
        private static readonly Color Accent = Color.FromRgb(0x7C, 0xC8, 0xFF), Done = Color.FromRgb(0x7C, 0xF2, 0xA8);

        private readonly Process _setup;
        private readonly string _current, _app;
        private bool _launched;
        private readonly string _temp, _mutex, _subtitle;
        private readonly long _total;
        private readonly int _pid;
        private readonly HashSet<string> _alreadyThere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };

        private readonly Border _card, _fill;
        private readonly TextBlock _sub;
        private readonly FrameworkElement _eyes, _happy;
        private readonly DropShadowEffect _fillGlow;

        private string _extracting;
        private int _percent = -1;
        private bool _oldGone, _finished;

        public CardWindow(Dictionary<string, string> options, Process setup)
        {
            _setup = setup;
            _current = Get(options, "--current");
            _app = Get(options, "--app");
            _temp = Get(options, "--temp");
            _mutex = Get(options, "--mutex");
            _subtitle = Get(options, "--subtitle");
            long.TryParse(Get(options, "--total"), out _total);
            int.TryParse(Get(options, "--pid"), out _pid);
            if (Directory.Exists(_temp)) _alreadyThere.UnionWith(Directory.GetDirectories(_temp, "tmp_*"));

            Width = 320;
            Height = 260;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Title = "Notchy";
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

            Color eye = ParseColor(Get(options, "--eye"));
            _eyes = BuildEyes(eye);
            _happy = BuildHappyEyes(eye);
            _happy.Visibility = Visibility.Collapsed;
            var face = new Grid { Height = 20, HorizontalAlignment = HorizontalAlignment.Center };
            face.Children.Add(_eyes);
            face.Children.Add(_happy);

            var title = new TextBlock { Text = Get(options, "--title"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 2) };
            _sub = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 13) };

            _fillGlow = new DropShadowEffect { Color = Accent, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
            _fill = new Border { Width = 0, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Accent),
                HorizontalAlignment = HorizontalAlignment.Left, Effect = _fillGlow };
            var track = new Grid { Width = BarWidth, Height = 4 };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)) });
            track.Children.Add(_fill);

            var content = new StackPanel();
            content.Children.Add(face);
            content.Children.Add(title);
            content.Children.Add(_sub);
            content.Children.Add(track);

            _card = new Border
            {
                Width = 232,
                Padding = new Thickness(20, 22, 20, 18),
                Margin = new Thickness(36),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x13, 0x17)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.55 },
                Child = content,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(0.94, 0.94)
            };
            Content = _card;

            SetPercent(0);
            Loaded += delegate { Appear(); };
            _timer.Tick += Tick;
            _timer.Start();
        }

        private static string Get(Dictionary<string, string> options, string key)
        {
            string value;
            return options.TryGetValue(key, out value) ? value : "";
        }

        private static Color ParseColor(string text)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(text);
            }
            catch (Exception)
            {
                return Colors.White;
            }
        }

        // ---- what the update is doing

        private void Tick(object sender, EventArgs e)
        {
            if (_finished) return;
            if ((_setup == null || _launched) && _clock.Elapsed > GiveUpAfter)
            {
                Leave();
                return;
            }
            if (_setup != null) WatchSetup();
            else
            {
                if (!_oldGone) _oldGone = OldNotchyGone();
                MeasureExtraction();
            }
            if (!_finished && _oldGone && NewNotchyRunning()) Finish();
        }

        private void Leave()
        {
            _finished = true;
            _timer.Stop();
            FadeOutAndClose();
        }

        /// <summary>A new install: the setup fills the "current" folder, ends, and then it's up to this program to open Notchy.</summary>
        private void WatchSetup()
        {
            MeasureInstall();
            if (!_setup.HasExited || _launched) return;
            if (_setup.ExitCode != 0)
            {
                // something went wrong silently: let the regular setup show what it is
                Process.Start(new ProcessStartInfo(_setup.StartInfo.FileName) { UseShellExecute = false });
                Leave();
                return;
            }
            _launched = true;
            _oldGone = true;
            _clock.Restart();
            SetPercent(FinishingPercent);
            Process.Start(new ProcessStartInfo(_app, "--after-update") { UseShellExecute = false, CreateNoWindow = true });
        }

        private void MeasureInstall()
        {
            try
            {
                // (a folder from an earlier install doesn't count: reinstalling replaces it)
                if (_total <= 0 || !Directory.Exists(_current) || Directory.GetCreationTimeUtc(_current) < Installer.StartedUtc) return;
                long written = new DirectoryInfo(_current).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                SetPercent((int)Math.Min(LastExtractingPercent, written * LastExtractingPercent / _total));
            }
            catch (IOException)
            {
                // a file moved while it was being measured: the next look gets it
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private bool OldNotchyGone()
        {
            if (_pid == 0) return true;
            try
            {
                using (var process = Process.GetProcessById(_pid)) return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private bool NewNotchyRunning()
        {
            Mutex mutex;
            if (!Mutex.TryOpenExisting(_mutex, out mutex)) return false;
            mutex.Dispose();
            return true;
        }

        /// <summary>The updater unpacks the new version into a new "tmp_" folder and then moves it into place: how much is there is how far it got.</summary>
        private void MeasureExtraction()
        {
            try
            {
                if (_extracting == null)
                {
                    if (!Directory.Exists(_temp)) return;
                    _extracting = Directory.GetDirectories(_temp, "tmp_*").FirstOrDefault(d => !_alreadyThere.Contains(d));
                    if (_extracting == null) return;
                }
                if (!Directory.Exists(_extracting))
                {
                    SetPercent(FinishingPercent); // moved into place: only the last steps are left
                    return;
                }
                if (_total <= 0) return;
                long written = new DirectoryInfo(_extracting).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                SetPercent((int)Math.Min(LastExtractingPercent, written * LastExtractingPercent / _total));
            }
            catch (IOException)
            {
                // a file moved while it was being measured: the next look gets it
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void SetPercent(int percent)
        {
            if (percent <= _percent) return;
            _percent = percent;
            _sub.Text = _subtitle + " · " + percent + "%";
            _fill.BeginAnimation(WidthProperty, new DoubleAnimation(BarWidth * percent / 100, TimeSpan.FromMilliseconds(140)));
        }

        private void Finish()
        {
            _finished = true;
            _timer.Stop();
            SetPercent(100);
            _eyes.Visibility = Visibility.Collapsed;
            _happy.Visibility = Visibility.Visible;
            _fill.Background = new SolidColorBrush(Done);
            _fillGlow.Color = Done;
            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            hold.Tick += delegate
            {
                hold.Stop();
                FadeOutAndClose();
            };
            hold.Start();
        }

        // ---- how it looks

        private void Appear()
        {
            _card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            var pop = new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
            var scale = (ScaleTransform)_card.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }

        private void FadeOutAndClose()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += delegate { Close(); };
            _card.BeginAnimation(OpacityProperty, fade);
        }

        private static FrameworkElement BuildEyes(Color color)
        {
            var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = color, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.7 } };
            var blink = new ScaleTransform(1, 1);
            for (int i = 0; i < 2; i++)
            {
                eyes.Children.Add(new Rectangle { Width = 8, Height = 18, RadiusX = 4.5, RadiusY = 4.5, Fill = new SolidColorBrush(color),
                    Margin = new Thickness(i == 0 ? 0 : 10, 0, 0, 0), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = blink });
            }
            var look = new TranslateTransform();
            eyes.RenderTransform = look;

            var around = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.2), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var key in new[] { new[] { 0, 0.0 }, new[] { 0.18, 0.0 }, new[] { 0.32, -5.0 }, new[] { 0.48, -5.0 }, new[] { 0.62, 5.0 }, new[] { 0.82, 5.0 }, new[] { 1, 0.0 } })
                around.KeyFrames.Add(new EasingDoubleKeyFrame(key[1], KeyTime.FromPercent(key[0]), new SineEase { EasingMode = EasingMode.EaseInOut }));
            look.BeginAnimation(TranslateTransform.XProperty, around);

            var closing = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.2), RepeatBehavior = RepeatBehavior.Forever };
            closing.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.88)));
            closing.KeyFrames.Add(new LinearDoubleKeyFrame(0.12, KeyTime.FromPercent(0.93)));
            closing.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(1)));
            blink.BeginAnimation(ScaleTransform.ScaleYProperty, closing);
            return eyes;
        }

        private static FrameworkElement BuildHappyEyes(Color color)
        {
            var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new DropShadowEffect { Color = color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7 } };
            for (int i = 0; i < 2; i++)
            {
                eyes.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,9 Q7.5,-1 14,9"), Stroke = new SolidColorBrush(color), StrokeThickness = 2.8,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) });
            }
            return eyes;
        }
    }
}
