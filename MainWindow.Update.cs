using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Notchy;

/// <summary>Auto-update: background checks, a notice on the island and an "Update" pill in the header.</summary>
public partial class MainWindow
{
    private static readonly Color UpdateColor = Color.FromRgb(0x5A, 0xC8, 0xFA);
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(3) };
    private Button _updateButton = null!;

    private void InitUpdates()
    {
        // header pill (expanded island), shown only while an update is ready
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Glyph(0xE896), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        // (the button's style sets the icon font for everything in it: the label needs the normal font back,
        // or "Atualizar" shows as empty boxes, wide enough to run into the clock)
        row.Children.Add(new TextBlock { Text = Loc.T("upd.button"), FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        _updateButton = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Content = row,
            Foreground = new SolidColorBrush(UpdateColor),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        _updateButton.Click += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            Updater.ApplyAndRestart();
        };
        var right = (Panel)BatteryText.Parent;
        right.Children.Insert(right.Children.IndexOf(BatteryText), _updateButton);
        GlowUnder(_updateButton, UpdateColor, true, 8, 0.6);

        Updater.Ready += () => Dispatcher.BeginInvoke(OnUpdateReady);

        if (!Updater.IsInstalled) return; // dev build: nothing to update in place
        _updateTimer.Tick += (_, _) => CheckUpdates();
        _updateTimer.Start();
        // first look a little after startup, so it never competes with the app opening
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        first.Tick += (_, _) =>
        {
            first.Stop();
            CheckUpdates();
        };
        first.Start();
    }

    private void CheckUpdates()
    {
        if (S.AutoUpdate) _ = Updater.CheckAndDownloadAsync();
    }

    private void OnUpdateReady()
    {
        string v = Updater.PendingVersion ?? "";
        _updateButton.ToolTip = Loc.F("upd.tip", v);
        if (_updateButton.Content is StackPanel sp && sp.Children[1] is TextBlock t) t.Text = Loc.T("upd.button");
        _updateButton.Visibility = Visibility.Visible;
        ShowTransient(CompactMode.Update, Loc.F("upd.ready", v), "", 8);
    }
}
