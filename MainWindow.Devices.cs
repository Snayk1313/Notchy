using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>A wireless device with a battery: headset, earbuds, mouse, keyboard.</summary>
public sealed record DeviceBattery(string Name, DeviceKind Kind, int Percent, bool Charging);

public enum DeviceKind { Computer, Headset, Earbuds, Mouse, Keyboard, Other }

/// <summary>
/// "Dispositivos": a small battery next to the bell (the weakest device's level, only while one is connected) opens
/// "‹ Dispositivos", one row per device with its icon, name, a bar and the percentage.
/// </summary>
public partial class MainWindow
{
    private const int DeviceLow = 30, DeviceCritical = 15;
    private static readonly Color DeviceGreen = Color.FromRgb(0x6C, 0xE6, 0xA0), DeviceOrange = Color.FromRgb(0xFF, 0xB0, 0x54), DeviceRed = Color.FromRgb(0xFF, 0x6B, 0x6B);

    private List<DeviceBattery> _devices = new(), _extDevices = new();
    /// <summary>A laptop's own battery: the first row, and the header's only battery (no separate percentage next to it).</summary>
    private DeviceBattery? _pcBattery;
#if DEBUG
    private bool _testPcBattery;
#endif
    private Border _devChip = null!;
    private Rectangle _devChipFill = null!;
    private TextBlock _devChipText = null!;
    /// <summary>The weakest device is low: the header battery shows its percentage too (if there's room, see FitHeader).</summary>
    private bool _devChipLow;

    private readonly System.Windows.Threading.DispatcherTimer _devTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private HidBattery.Reading? _hyperx, _rapoo;
    private int _hyperxWarned = 100; // the last low level warned about (15, then 5), back to 100 once charged
    private bool _devPolling;

    /// <summary>The wireless devices: Bluetooth (what Windows knows, via the earbuds' service) and the USB receivers read over HID
    ///     (HyperX asked every minute, Rapoo listened to in the background).</summary>
    private void InitDevices()
    {
        _devTimer.Tick += (_, _) => PollDevices();
        HidBattery.ReadRapoo(); // (starts listening to the Rapoo receiver now, so the first look already has it)
        _devTimer.Start();
        _bt.Connected += _ => Dispatcher.BeginInvoke(RefreshBluetoothDevices);
        _bt.Disconnected += _ => Dispatcher.BeginInvoke(RefreshBluetoothDevices);
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            PollDevices();
        };
        first.Start();
    }

    private async void PollDevices()
    {
        if (_devPolling) return;
        _devPolling = true;
        try
        {
            _hyperx = await System.Threading.Tasks.Task.Run(() => HidBattery.ReadHyperX());
            _rapoo = HidBattery.ReadRapoo();
        }
        catch
        {
            _hyperx = null;
        }
        finally
        {
            _devPolling = false;
        }
        MaybeWarnHyperX();
        RefreshBluetoothDevices();
    }

    private void RefreshBluetoothDevices()
    {
        var list = new List<DeviceBattery>();
        if (_hyperx is { } h) list.Add(new DeviceBattery(h.Name, DeviceKind.Headset, h.Percent, h.Charging));
        if (_rapoo is { } m) list.Add(new DeviceBattery(m.Name, DeviceKind.Mouse, m.Percent, m.Charging));
        foreach (var b in _bt.ConnectedDevices)
            if (b.Battery is int p) list.Add(new DeviceBattery(b.Name, b.Speaker ? DeviceKind.Other : DeviceKind.Earbuds, p, false));
#if DEBUG
        if (_testPcBattery) return; // (a test has its own made-up devices)
#endif
        SetDevices(list);
    }

    /// <summary>The headset getting low: the earbuds' warning on the island, once at 15% and once at 5%.</summary>
    private void MaybeWarnHyperX()
    {
        if (_hyperx is not { } h || h.Charging || h.Percent > 15)
        {
            if (_hyperx is { } c && (c.Charging || c.Percent > 20)) _hyperxWarned = 100;
            return;
        }
        int level = h.Percent <= 5 ? 5 : 15;
        if (level >= _hyperxWarned) return;
        _hyperxWarned = level;
        ShowBuds(CompactMode.BudsLow, new BudsInfo(HyperXId, h.Name, true, h.Percent, false), 5);
    }

    private const string HyperXId = "hid:hyperx";

    private static Color DeviceColor(int p) => p <= DeviceCritical ? DeviceRed : p <= DeviceLow ? DeviceOrange : DeviceGreen;

    private void BuildDevicesChip()
    {
        var right = (StackPanel)MoreButton.Parent;
        // a little battery drawn to scale: the fill is the weakest device's level
        var battery = new Grid { Width = 19, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        battery.Children.Add(new Border { Width = 17, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2.5), BorderThickness = new Thickness(1.2), BorderBrush = White(0xBB) });
        battery.Children.Add(new Border { Width = 1.6, Height = 4, HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(0.8), Background = White(0xBB) });
        _devChipFill = new Rectangle { Height = 6, Width = 0, RadiusX = 1.2, RadiusY = 1.2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2.2, 0, 0, 0) };
        battery.Children.Add(_devChipFill);
        _devChipText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), Visibility = Visibility.Collapsed };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(battery);
        row.Children.Add(_devChipText);
        _devChip = new Border { Child = row, Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(10), Background = White(0x0E), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Visibility = Visibility.Collapsed, ToolTip = Loc.T("dev.title") };
        _devChip.MouseEnter += (_, _) => _devChip.Background = White(0x20);
        _devChip.MouseLeave += (_, _) => _devChip.Background = White(0x0E);
        _devChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenDevicesPage();
        };
        right.Children.Insert(right.Children.IndexOf(_bell), _devChip);
    }

    private void OpenDevicesPage() => OpenSubPage(Loc.T("dev.title"), DevicesPage);

    private void SetDevices(List<DeviceBattery> devices)
    {
        _extDevices = devices;
        RefreshDevices();
    }

    private void SetPcBattery(DeviceBattery? pc)
    {
        if (pc == _pcBattery) return;
        _pcBattery = pc;
        RefreshDevices();
    }

    /// <summary>The devices changed: the header battery shows the weakest one (hidden with none connected).</summary>
    private void RefreshDevices()
    {
        _devices = (_pcBattery is { } pc ? new List<DeviceBattery> { pc } : new()).Concat(_extDevices).ToList();
        if (_devChip == null) return; // (the clock's first tick can come before the header is built)
        if (_devices.Count == 0)
        {
            _devChip.Visibility = Visibility.Collapsed;
            return;
        }
        var weakest = _devices.OrderBy(d => d.Charging ? 101 : d.Percent).First();
        _devChipLow = weakest.Percent <= DeviceLow && !weakest.Charging;
        var color = _devChipLow ? DeviceColor(weakest.Percent) : Color.FromRgb(0xEE, 0xEE, 0xF2);
        _devChipFill.Fill = new SolidColorBrush(color);
        _devChipFill.Width = Math.Max(1.5, 12.6 * Math.Clamp(weakest.Percent, 0, 100) / 100.0);
        _devChipText.Foreground = new SolidColorBrush(color);
        _devChipText.Text = weakest.Percent + "%";
        _devChip.ToolTip = $"{weakest.Name} · {(weakest.Charging ? "⚡" : "")}{weakest.Percent}%";
        _devChip.Visibility = Visibility.Visible;
        if (DevicesPageOpen) RefreshSubPage(DevicesPage);
    }

    private bool DevicesPageOpen => _subOpen && _subTitle.Text == Loc.T("dev.title");

    private FrameworkElement DevicesPage()
    {
        var list = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        foreach (var d in _devices)
        {
            var color = d.Charging ? DeviceGreen : DeviceColor(d.Percent);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            var icon = new TextBlock { Text = DeviceGlyph(d.Kind), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = d.Name, FontSize = 12.5, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            texts.Children.Add(new TextBlock { Text = Loc.T(d.Charging ? "dev.charging" : "dev.connected"), FontSize = 10.5, Foreground = White(0x88) });
            Grid.SetColumn(texts, 1);
            var bar = new Grid { Width = 70, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = White(0x1F) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), HorizontalAlignment = HorizontalAlignment.Left, Width = 70 * Math.Clamp(d.Percent, 0, 100) / 100.0 });
            Grid.SetColumn(bar, 2);
            var pct = new TextBlock { Text = (d.Charging ? "⚡" : "") + d.Percent + "%", FontSize = 12.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(pct, 3);
            g.Children.Add(icon);
            g.Children.Add(texts);
            g.Children.Add(bar);
            g.Children.Add(pct);
            list.Children.Add(new Border { Child = g, Background = White(0x0F), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 0, 6) });
        }
        if (_devices.Count == 0)
            list.Children.Add(new TextBlock { Text = Loc.T("dev.none"), FontSize = 12, Foreground = White(0x88), Margin = new Thickness(4, 8, 0, 0) });
        return list;
    }

    private static string DeviceGlyph(DeviceKind k) => k switch
    {
        DeviceKind.Computer => "",
        DeviceKind.Headset => "",
        DeviceKind.Earbuds => "",
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        _ => ""
    };
}
