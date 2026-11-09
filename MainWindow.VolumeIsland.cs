using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Volume (and, where Windows controls it, brightness) on the island: the island itself fills with light from the
/// left as the level goes up, Notchy's eyes on the left (wider when it's loud, closed when muted, squinting at a
/// bright screen), the number on the right. Drag or click anywhere on it to set the level. With the mouse on it the
/// number turns into the output device's icon; a click there opens the list of outputs right in the island (each
/// with its kind and its own volume; "Mudo" at the top).
/// </summary>
public partial class MainWindow
{
    private enum LevelKind { Volume, Brightness }

    private static readonly Color SunColor = Color.FromRgb(0xFF, 0xD3, 0x6B);
    private const double LevelWidth = 240, DevListWidth = 300, DevRowHeight = 50, DevHeaderHeight = 44;

    private LevelKind _levelKind = LevelKind.Volume;
    private double _levelTarget, _levelShown = -1;
    private bool _levelMuted, _levelDragging, _levelHover, _devListOpen;
    private double _devButtonShown;

    private Grid _levelPill = null!;
    private Border _levelFill = null!;
    private GradientStop _levelFillA = null!, _levelFillB = null!;
    private Rectangle _levelEyeL = null!, _levelEyeR = null!;
    private ScaleTransform _levelEyeLScale = null!, _levelEyeRScale = null!;
    private TextBlock _levelText = null!, _devIcon = null!;
    private Border _devButton = null!;
    private StackPanel _devList = null!;
    private int _devCount;
    private readonly BrightnessService _brightness = new();

    private Color LevelColor => _levelKind == LevelKind.Brightness ? SunColor : Colors.White;

    private Size LevelSize() => _devListOpen
        ? new Size(DevListWidth, 34 + DevHeaderHeight + _devCount * DevRowHeight + 12)
        : new Size(LevelWidth, 34);

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitVolumeIsland()
    {
        var root = VolumePanel;
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        root.RowDefinitions.Add(new RowDefinition());

        // the pill: light filling from the left, eyes, the number (or the device button)
        _levelPill = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, ClipToBounds = true };
        _levelFillA = new GradientStop(Color.FromArgb(0x0D, 255, 255, 255), 0);
        _levelFillB = new GradientStop(Color.FromArgb(0x4C, 255, 255, 255), 1);
        _levelFill = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, Width = 0, BorderThickness = new Thickness(0, 0, 2, 0),
            BorderBrush = Brushes.White, Background = new LinearGradientBrush(new GradientStopCollection { _levelFillA, _levelFillB }, 0),
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.45, Direction = 0 }
        };
        _levelPill.Children.Add(_levelFill);

        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0), Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 } };
        Rectangle Eye(out ScaleTransform s, Thickness m)
        {
            s = new ScaleTransform();
            return new Rectangle { Width = 6, Height = 10, RadiusX = 3, RadiusY = 3, Fill = Brushes.White, Margin = m, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = s };
        }
        _levelEyeL = Eye(out _levelEyeLScale, new Thickness(0, 0, 5, 0));
        _levelEyeR = Eye(out _levelEyeRScale, new Thickness(0));
        eyes.Children.Add(_levelEyeL);
        eyes.Children.Add(_levelEyeR);
        _levelPill.Children.Add(eyes);

        _levelText = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = White(0xE0), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 15, 0), IsHitTestVisible = false };
        _levelPill.Children.Add(_levelText);

        _devIcon = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 7, Foreground = White(0x99), Margin = new Thickness(4, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center };
        var devRow = new StackPanel { Orientation = Orientation.Horizontal };
        devRow.Children.Add(_devIcon);
        devRow.Children.Add(chevron);
        _devButton = new Border
        {
            Child = devRow, Height = 22, CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 0, 7, 0), Background = White(0x1A),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Opacity = 0, Cursor = Cursors.Hand
        };
        _devButton.MouseEnter += (_, _) => _devButton.Background = White(0x2E);
        _devButton.MouseLeave += (_, _) => _devButton.Background = White(0x1A);
        _devButton.MouseLeftButtonDown += (_, e) => e.Handled = true; // not the start of a drag
        _devButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            SetDeviceListOpen(!_devListOpen);
        };
        _levelPill.Children.Add(_devButton);

        // drag / click anywhere on the pill: the level follows the mouse
        _levelPill.MouseEnter += (_, _) => _levelHover = true;
        _levelPill.MouseLeave += (_, _) => _levelHover = false;
        _levelPill.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _levelDragging = true;
            _levelPill.CaptureMouse();
            SetLevelFromMouse(e.GetPosition(_levelPill).X);
        };
        _levelPill.MouseMove += (_, e) =>
        {
            if (_levelDragging) SetLevelFromMouse(e.GetPosition(_levelPill).X);
        };
        _levelPill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!_levelDragging) return;
            _levelDragging = false;
            _levelPill.ReleaseMouseCapture();
            SoundService.Play(Sfx.Click);
        };
        root.Children.Add(_levelPill);

        // the outputs, opened from the device button
        _devList = new StackPanel { Margin = new Thickness(8, 0, 8, 10), Opacity = 0 };
        Grid.SetRow(_devList, 1);
        root.Children.Add(_devList);

        _brightness.Changed += pct => Dispatcher.BeginInvoke(() => ShowBrightness(pct / 100.0));
        _brightness.StartAsync();
        // Windows' own pop-up out of the way from the start, so the first key press doesn't flash it
        if (S.VolumeOsd) OsdHider.HideWindowsOsd();
    }

    // =====================================================================================
    // showing a level
    // =====================================================================================

    /// <summary>The system volume changed (keys, mixer, wheel on the island…): the island shows it.</summary>
    private void ShowVolumeLevel(double value, bool muted)
    {
        _levelKind = LevelKind.Volume;
        _levelTarget = value;
        _levelMuted = muted;
    }

    /// <summary>Windows changed the screen brightness (laptop keys, quick settings…): the island shows it in yellow.</summary>
    private void ShowBrightness(double value)
    {
        if (!S.VolumeOsd || _state == IslandState.Expanded) return;
        if (_levelKind != LevelKind.Brightness) SetDeviceListOpen(false);
        _levelKind = LevelKind.Brightness;
        _levelTarget = value;
        _levelMuted = false;
        if (!_levelDragging) _volumeOsdUntil = DateTime.UtcNow.AddSeconds(1.6);
        OsdHider.HideWindowsOsd();
        UpdateState();
    }

    private void SetLevelFromMouse(double x)
    {
        double w = Math.Max(1, _levelPill.ActualWidth);
        double v = Math.Clamp(x / w, 0, 1);
        _levelTarget = _levelShown = v;
        _levelMuted = false;
        if (_levelKind == LevelKind.Brightness) _brightness.Set((int)Math.Round(v * 100));
        else
        {
            _audio.TryGet(out _, out bool muted);
            if (muted) _audio.ToggleMute();
            SetVolume(v);
        }
    }

    // =====================================================================================
    // the output list
    // =====================================================================================

    private void SetDeviceListOpen(bool open)
    {
        if (open && _levelKind != LevelKind.Volume) return;
        if (open) BuildDeviceList();
        if (open == _devListOpen) return;
        _devListOpen = open;
        _devList.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(Dur(open ? 260 : 120))) { BeginTime = TimeSpan.FromMilliseconds(open ? Dur(120) : 0) });
        if (_state == IslandState.Volume)
        {
            var size = LevelSize();
            VolumePanel.Width = size.Width;
            VolumePanel.Height = size.Height;
            AnimateIslandSize(size, bigMove: true);
        }
    }

    private void BuildDeviceList()
    {
        _devList.Children.Clear();
        var devices = AudioDevices.List();
        _audio.TryGet(out _, out bool muted);

        // header: "Saída de áudio" + a Mudo button
        var head = new DockPanel { Height = DevHeaderHeight, Margin = new Thickness(12, 0, 4, 0) };
        var mute = new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 4, 10, 4), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Background = muted ? Brushes.White : White(0x14)
        };
        var muteRow = new StackPanel { Orientation = Orientation.Horizontal };
        var ink = muted ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : White(0xDD);
        muteRow.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        muteRow.Children.Add(new TextBlock { Text = Loc.T("out.mute"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        mute.Child = muteRow;
        mute.MouseEnter += (_, _) => mute.Opacity = 0.8;
        mute.MouseLeave += (_, _) => mute.Opacity = 1;
        mute.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _audio.ToggleMute();
            PollVolume();
            BuildDeviceList();
        };
        DockPanel.SetDock(mute, Dock.Right);
        head.Children.Add(mute);
        head.Children.Add(new TextBlock { Text = Loc.T("out.title"), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        _devList.Children.Add(head);

        foreach (var d in devices)
        {
            var dev = d;
            bool on = d.IsDefault;
            var circle = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = on ? Brushes.White : White(0x12), VerticalAlignment = VerticalAlignment.Center,
                Effect = on ? new DropShadowEffect { Color = Colors.White, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.5 } : null,
                Child = new TextBlock
                {
                    Text = ((char)AudioDevices.Glyph(d.Kind)).ToString(), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
                    Foreground = on ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : White(0xCC),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = d.Name, FontSize = 12, Foreground = on ? Brushes.White : White(0xCC), TextTrimming = TextTrimming.CharacterEllipsis });
            string sub = OutputSub(d);
            texts.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = White(0x70), TextTrimming = TextTrimming.CharacterEllipsis });
            if (d.Volume >= 0)
            {
                var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = White(0x1F) });
                bar.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(2), Background = d.Muted ? White(0x55) : on ? Brushes.White : White(0x99), HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0, Tag = d.Muted ? 0f : d.Volume
                });
                bar.SizeChanged += (_, _) =>
                {
                    if (bar.Children[1] is Border fill && fill.Tag is float v) fill.Width = bar.ActualWidth * v;
                };
                texts.Children.Add(bar);
            }
            var check = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, Opacity = on ? 1 : 0, Margin = new Thickness(0, 0, 4, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(check, 2);
            grid.Children.Add(circle);
            grid.Children.Add(texts);
            grid.Children.Add(check);
            var row = new Border
            {
                Child = grid, Height = DevRowHeight - 3, Margin = new Thickness(0, 0, 0, 3), Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(12),
                Background = on ? White(0x17) : Brushes.Transparent, Cursor = Cursors.Hand
            };
            row.MouseEnter += (_, _) => { if (!on) row.Background = White(0x0E); };
            row.MouseLeave += (_, _) => { if (!on) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (dev.IsDefault) return;
                if (AudioDevices.SetDefault(dev.Id)) SoundService.Play(Sfx.Click);
                _audio.EnsureDevice();
                _lastVol = -1; // the new device has its own volume: don't count it as a change
                PollVolume();
                UpdateOutputIcon();
                BuildDeviceList();
            };
            _devList.Children.Add(row);
        }
        if (devices.Count == 0)
            _devList.Children.Add(new TextBlock { Text = Loc.T("out.none"), FontSize = 11.5, Foreground = White(0x80), Margin = new Thickness(12, 4, 0, 8) });
        _devCount = Math.Max(1, devices.Count);
    }

    /// <summary>"Fone de ouvido · USB", "Bluetooth · 77%" (the earbuds' battery when Windows knows it).</summary>
    private string OutputSub(OutputDevice d)
    {
        string sub = KindLabel(d.Kind) + (d.Bus.Length > 0 && d.Kind != OutputKind.Bluetooth ? " · " + d.Bus : "");
        if (_bt.BatteryFor(d.Name) is int b) sub += $" · {b}%";
        return sub;
    }

    private static string KindLabel(OutputKind k) => Loc.T(k switch
    {
        OutputKind.Headphones => "out.kind.headphones",
        OutputKind.Headset => "out.kind.headset",
        OutputKind.Display => "out.kind.display",
        OutputKind.Bluetooth => "out.kind.bluetooth",
        OutputKind.Speakers => "out.kind.speakers",
        _ => "out.kind.other"
    });

    // =====================================================================================
    // every frame
    // =====================================================================================

    private void UpdateVolumeIsland()
    {
        if (_state != IslandState.Volume)
        {
            _levelShown = -1; // next time it appears it starts where the level is
            return;
        }
        double target = _levelMuted ? 0 : Math.Clamp(_levelTarget, 0, 1);
        if (_levelShown < 0) _levelShown = target;
        _levelShown += (target - _levelShown) * Ease(0.3);

        var c = LevelColor;
        if (_levelFill.BorderBrush is not SolidColorBrush bb || bb.Color != c)
        {
            _levelFill.BorderBrush = new SolidColorBrush(c);
            _levelFillA.Color = Color.FromArgb(0x0D, c.R, c.G, c.B);
            _levelFillB.Color = Color.FromArgb(0x4C, c.R, c.G, c.B);
            ((DropShadowEffect)_levelFill.Effect).Color = c;
            var eye = new SolidColorBrush(c);
            _levelEyeL.Fill = _levelEyeR.Fill = eye;
            if (_levelEyeL.Parent is FrameworkElement ep && ep.Effect is DropShadowEffect eg) eg.Color = c;
        }
        double w = _levelPill.ActualWidth > 1 ? _levelPill.ActualWidth : LevelWidth;
        _levelFill.Width = Math.Max(0, _levelShown * w);
        _levelFill.BorderThickness = new Thickness(0, 0, _levelShown > 0.005 ? 2 : 0, 0);
        _levelText.Text = _levelMuted ? "" : Math.Round(_levelTarget * 100).ToString();

        // eyes: wider when it's loud, shut when muted; a bright screen makes them squint
        double t = AnimNow;
        bool blink = (t + 0.4) % 4.1 < 0.12;
        double open = _levelMuted ? 0.12
            : _levelKind == LevelKind.Brightness ? 1.1 - 0.85 * Math.Max(0, (_levelShown - 0.4) / 0.6)
            : 0.55 + 0.6 * _levelShown;
        if (blink && !_levelMuted) open = 0.12;
        _levelEyeLScale.ScaleY += (open - _levelEyeLScale.ScaleY) * Ease(0.35);
        _levelEyeRScale.ScaleY = _levelEyeLScale.ScaleY;

        // the device button takes the number's place while the mouse is on it (or its list is open)
        bool showDev = _levelKind == LevelKind.Volume && (_levelHover || _devListOpen) && !_levelDragging;
        _devButtonShown += ((showDev ? 1 : 0) - _devButtonShown) * Ease(0.25);
        _devButton.Opacity = _devButtonShown;
        _devButton.IsHitTestVisible = _devButtonShown > 0.5;
        _levelText.Opacity = 1 - _devButtonShown;
    }
}
