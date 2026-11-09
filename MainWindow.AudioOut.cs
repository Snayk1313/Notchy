using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Notchy;

/// <summary>Windows audio output picker: from the volume pop-up's speaker and from the Music tab.</summary>
public partial class MainWindow
{
    private bool _volHover;          // mouse is on the volume pop-up (keeps it a pop-up)
    private ContextMenu? _outMenuOpen; // output menu opened from the volume pop-up
    private Button _musicOutButton = null!;

    private void InitAudioOut()
    {
        // (the volume pop-up has its own output list: MainWindow.VolumeIsland.cs)

        // Music tab: a little chevron right after the speaker opens the same menu
        _musicOutButton = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = Glyph(0xE70D),
            FontSize = 8,
            Padding = new Thickness(3, 6, 3, 5),
            Margin = new Thickness(-3, 0, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)),
            ToolTip = Loc.T("out.title"),
            VerticalAlignment = VerticalAlignment.Center
        };
        _musicOutButton.Click += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenOutputSubPage();
        };
        var row = (Panel)MuteButton.Parent;
        row.Children.Insert(row.Children.IndexOf(MuteButton) + 1, _musicOutButton);
    }

    private void ShowOutputMenu(FrameworkElement target, bool fromPopup)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };

        _audio.TryGet(out _, out bool muted);
        var mute = new MenuItem
        {
            Header = Loc.T("out.mute") + (muted ? "  ✓" : ""),
            Icon = new TextBlock { Text = Glyph(0xE74F), FontSize = 12 },
            FontWeight = muted ? FontWeights.SemiBold : FontWeights.Normal
        };
        mute.Click += (_, _) =>
        {
            _audio.ToggleMute();
            PollVolume();
        };
        menu.Items.Add(mute);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = Loc.T("out.title"), IsEnabled = false, FontSize = 11 });

        var devices = AudioDevices.List();
        foreach (var d in devices)
        {
            var item = new MenuItem
            {
                Header = d.Name + (d.IsDefault ? "  ✓" : ""),
                Icon = new TextBlock { Text = Glyph(AudioDevices.Glyph(d.Kind)), FontSize = 12 },
                FontWeight = d.IsDefault ? FontWeights.SemiBold : FontWeights.Normal
            };
            var dev = d;
            item.Click += (_, _) =>
            {
                if (dev.IsDefault) return;
                if (AudioDevices.SetDefault(dev.Id)) SoundService.Play(Sfx.Click);
                _audio.EnsureDevice();
                _lastVol = -1; // the new device has its own volume: don't count it as a change
                PollVolume();
                UpdateOutputIcon();
            };
            menu.Items.Add(item);
        }
        if (devices.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("out.none"), IsEnabled = false });

        menu.Opened += (_, _) => RaisePopup(menu);
        menu.Closed += (_, _) =>
        {
            bool over = CursorOverIsland();
            if (fromPopup)
            {
                _outMenuOpen = null;
                _volHover = over;
                _volumeOsdUntil = DateTime.UtcNow.AddSeconds(over ? 0 : 1.5);
            }
            else
            {
                _hoverRequested = over;
                _hoverActive = over && S.Open == OpenMode.Hover;
            }
            UpdateState();
        };

        if (fromPopup) _outMenuOpen = menu;
        else _devicesMenuOpen = menu;
        menu.IsOpen = true;
    }

    /// <summary>The volume pop-up's speaker shows what kind of output is playing (headphones, TV...).</summary>
    private void UpdateOutputIcon()
    {
        foreach (var d in AudioDevices.List())
            if (d.IsDefault)
            {
                _devButton.ToolTip = d.Name;
                _devIcon.Text = Glyph(AudioDevices.Glyph(d.Kind));
                _musicOutButton.ToolTip = Loc.T("out.title") + " · " + d.Name;
                _outputKind = d.Kind;
                return;
            }
    }

    private OutputKind _outputKind = OutputKind.Speakers;

    /// <summary>Speaker glyph for the pop-up: headphones / TV / Bluetooth when that's the output, else the volume level.</summary>
    private string OutputGlyph(float vol, bool mute) =>
        mute || vol <= 0.001 || _outputKind is OutputKind.Speakers or OutputKind.Other
            ? VolumeGlyph(vol, mute)
            : Glyph(AudioDevices.Glyph(_outputKind));

    private static bool IsInside(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (child == parent) return true;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }
}
