using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Notchy;

/// <summary>Spotify extras on the Music tab (Web API, Premium): real volume, like, shuffle, repeat, devices, queue.</summary>
public partial class MainWindow
{
    public SpotifyService Spotify { get; } = new();

    private DispatcherTimer? _spTimer;
    private SpotifyState? _spState;
    private bool _spPolling;
    private string? _spLikedTrack;
    private bool _spLiked;
    private DateTime _spUserVolumeAt = DateTime.MinValue;
    private int? _spVolumeTarget;
    private bool _spVolumeSending;
    private int _spMutedFrom = -1;
    private bool _queueOpen;

    private static string Glyph(int c) => ((char)c).ToString();

    /// <summary>Spotify is connected and is what's playing.</summary>
    private bool SpotifyActive => Spotify.IsConnected && string.Equals(_media.SourceProcess, "Spotify", StringComparison.OrdinalIgnoreCase);

    private void InitSpotify()
    {
        _spTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _spTimer.Tick += async (_, _) =>
        {
            if (_state == IslandState.Expanded && (_tab == Tab.Music || _tab == Tab.Home)) await PollSpotifyAsync();
        };
        _spTimer.Start();
        Spotify.ConnectionChanged += () => Dispatcher.InvokeAsync(async () =>
        {
            UpdateSpotifyControls();
            await PollSpotifyAsync();
        });
        QueueTitle.Text = Loc.T("sp.queue");
        UpdateSpotifyControls();
    }

    private void UpdateSpotifyControls()
    {
        bool on = SpotifyActive;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        LikeButton.Visibility = ShuffleButton.Visibility = RepeatButton.Visibility = QueueButton.Visibility = vis;
        if (!on && _queueOpen) CloseQueue();
        QueueTitle.Text = Loc.T("sp.queue");

        if (on && _spState?.Device is { } dev)
        {
            SourceText.Text = dev.Name;
            SourceIcon.Text = Glyph(DeviceGlyph(dev.Type));
            SourceButton.ToolTip = Loc.T("sp.devices");
        }
        else
        {
            SourceText.Text = _media.SourceName;
            SourceIcon.Text = Glyph(0xE7F4);
            SourceButton.ToolTip = Loc.T("music.openapp");
        }
        PaintShuffleRepeat();
        PaintLike(animate: false);
    }

    private static int DeviceGlyph(string type) => type.ToLowerInvariant() switch
    {
        "smartphone" => 0xE8EA,
        "speaker" => 0xE7F5,
        "tv" or "castvideo" => 0xE7F4,
        "tablet" => 0xE70A,
        _ => 0xE7F4
    };

    /// <summary>Reads the player state (volume, shuffle, repeat, device) and the liked status of the song.</summary>
    private async Task PollSpotifyAsync()
    {
        if (!SpotifyActive || _spPolling) return;
        _spPolling = true;
        try
        {
            var st = await Spotify.StateAsync();
            if (st == null) return;
            _spState = st;
            UpdateSpotifyControls();

            if (st.Volume is int v && !VolumeSlider.IsDragging && (DateTime.UtcNow - _spUserVolumeAt).TotalSeconds > 2.5)
            {
                VolumeSlider.Value = v / 100.0;
                MuteButton.Content = VolumeGlyph(v / 100f, v == 0);
            }

            if (st.TrackId != null && st.TrackId != _spLikedTrack)
            {
                var liked = await Spotify.IsLikedAsync(st.TrackId);
                if (liked != null)
                {
                    _spLikedTrack = st.TrackId;
                    _spLiked = liked.Value;
                    PaintLike(animate: false);
                }
            }
        }
        finally
        {
            _spPolling = false;
        }
    }

    // =====================================================================================
    // volume
    // =====================================================================================

    /// <summary>Spotify's own volume bar (debounced so dragging doesn't flood the API).</summary>
    private bool TrySetSpotifyVolume(double value)
    {
        if (!SpotifyActive || _spState is not { SupportsVolume: true }) return false;
        _spUserVolumeAt = DateTime.UtcNow;
        _spVolumeTarget = (int)Math.Round(value * 100);
        MuteButton.Content = VolumeGlyph((float)value, value <= 0.001);
        if (!_spVolumeSending) _ = SendSpotifyVolumeAsync();
        return true;
    }

    private async Task SendSpotifyVolumeAsync()
    {
        _spVolumeSending = true;
        try
        {
            while (_spVolumeTarget is int target)
            {
                _spVolumeTarget = null;
                await Spotify.SetVolumeAsync(target);
                await Task.Delay(120);
            }
        }
        finally
        {
            _spVolumeSending = false;
        }
    }

    private bool TryToggleSpotifyMute()
    {
        if (!SpotifyActive || _spState is not { SupportsVolume: true }) return false;
        int now = (int)Math.Round(VolumeSlider.Value * 100);
        int target;
        if (now > 0)
        {
            _spMutedFrom = now;
            target = 0;
        }
        else
        {
            target = _spMutedFrom > 0 ? _spMutedFrom : 50;
            _spMutedFrom = -1;
        }
        VolumeSlider.Value = target / 100.0;
        TrySetSpotifyVolume(target / 100.0);
        return true;
    }

    // =====================================================================================
    // like
    // =====================================================================================

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        string? id = _spState?.TrackId;
        if (id == null) return;
        _spLiked = !_spLiked;
        _spLikedTrack = id;
        PaintLike(animate: true);
        if (_spLiked)
        {
            SoundService.Play(Sfx.Like);
            Happy();
        }
        if (!await Spotify.SetLikedAsync(id, _spLiked))
        {
            _spLiked = !_spLiked; // didn't work: put it back
            PaintLike(animate: false);
            FaceReact(Mood.Sad, 2);
        }
    }

    private void PaintLike(bool animate)
    {
        var accent = Bright(_musicAccent);
        LikeButton.Content = Glyph(_spLiked ? 0xEB52 : 0xE006);
        LikeButton.Foreground = _spLiked ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));
        GlowUnder(LikeButton, accent, _spLiked, 12, 0.9);
        LikeButton.ToolTip = Loc.T(_spLiked ? "sp.unlike" : "sp.like");
        if (!animate) return;
        if (LikeButton.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(1, 1);
            LikeButton.RenderTransform = st;
        }
        var pop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(_spLiked ? 1.45 : 0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    // =====================================================================================
    // shuffle / repeat
    // =====================================================================================

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_spState == null) return;
        bool on = !_spState.Shuffle;
        _spState = Copy(_spState, shuffle: on);
        PaintShuffleRepeat();
        if (!await Spotify.SetShuffleAsync(on)) await PollSpotifyAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (_spState == null) return;
        string next = _spState.Repeat switch { "off" => "context", "context" => "track", _ => "off" };
        _spState = Copy(_spState, repeat: next);
        PaintShuffleRepeat();
        if (!await Spotify.SetRepeatAsync(next)) await PollSpotifyAsync();
    }

    private static SpotifyState Copy(SpotifyState s, bool? shuffle = null, string? repeat = null) => new()
    {
        HasPlayer = s.HasPlayer,
        TrackId = s.TrackId,
        TrackName = s.TrackName,
        Shuffle = shuffle ?? s.Shuffle,
        Repeat = repeat ?? s.Repeat,
        Volume = s.Volume,
        SupportsVolume = s.SupportsVolume,
        Device = s.Device
    };

    private void PaintShuffleRepeat()
    {
        var accent = Bright(_musicAccent);
        var off = new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255));
        bool sh = _spState?.Shuffle == true;
        string rp = _spState?.Repeat ?? "off";

        ShuffleButton.Foreground = sh ? new SolidColorBrush(accent) : off;
        GlowUnder(ShuffleButton, accent, sh, 10, 0.8);
        ShuffleButton.ToolTip = Loc.T(sh ? "sp.shuffle.on" : "sp.shuffle.off");

        RepeatButton.Content = Glyph(rp == "track" ? 0xE8ED : 0xE8EE);
        RepeatButton.Foreground = rp != "off" ? new SolidColorBrush(accent) : off;
        GlowUnder(RepeatButton, accent, rp != "off", 10, 0.8);
        RepeatButton.ToolTip = Loc.T("sp.repeat." + rp);
    }

    // =====================================================================================
    // devices
    // =====================================================================================

    private async Task ShowDeviceMenuAsync()
    {
        var devices = await Spotify.DevicesAsync();
        var menu = new ContextMenu { PlacementTarget = SourceButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var d in devices)
        {
            var item = new MenuItem
            {
                Header = d.Name + (d.Active ? "  ✓" : ""),
                Icon = new TextBlock { Text = Glyph(DeviceGlyph(d.Type)), FontSize = 12 },
                FontWeight = d.Active ? FontWeights.SemiBold : FontWeights.Normal
            };
            var dev = d;
            item.Click += async (_, _) =>
            {
                if (await Spotify.TransferAsync(dev.Id)) await Task.Delay(700);
                await PollSpotifyAsync();
            };
            menu.Items.Add(item);
        }
        if (devices.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("sp.nodevices"), IsEnabled = false });
        menu.Items.Add(new Separator());
        var open = new MenuItem { Header = Loc.T("music.openapp"), Icon = new TextBlock { Text = Glyph(0xE8A7), FontSize = 12 } };
        open.Click += (_, _) => _media.ActivateSourceApp();
        menu.Items.Add(open);
        menu.Closed += (_, _) =>
        {
            bool over = CursorOverIsland();
            _hoverRequested = over;
            _hoverActive = over && S.Open == OpenMode.Hover;
            UpdateState();
        };
        menu.Opened += (_, _) => RaisePopup(menu);
        _devicesMenuOpen = menu;
        menu.IsOpen = true;
    }

    private ContextMenu? _devicesMenuOpen;

    /// <summary>Menus open from the island must sit above it (the island and its glass are topmost too).</summary>
    private static void RaisePopup(ContextMenu menu)
    {
        if (System.Windows.PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src)
            Native.BringToTopmost(src.Handle);
    }

    // =====================================================================================
    // queue
    // =====================================================================================

    private async void Queue_Click(object sender, RoutedEventArgs e)
    {
        if (_queueOpen)
        {
            CloseQueue();
            return;
        }
        _queueOpen = true;
        QueueList.Children.Clear();
        QueueList.Children.Add(new TextBlock { Text = Loc.T("chat.thinking"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), Margin = new Thickness(2, 6, 0, 0) });
        QueuePanel.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        QueueSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(Dur(280))) { EasingFunction = ease });
        QueuePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(220))));

        var queue = await Spotify.QueueAsync();
        if (!_queueOpen) return;
        QueueList.Children.Clear();
        if (queue.Count == 0)
            QueueList.Children.Add(new TextBlock { Text = Loc.T("sp.queue.empty"), FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), Margin = new Thickness(2, 6, 0, 0) });
        for (int i = 0; i < queue.Count; i++) QueueList.Children.Add(QueueRow(queue[i], i));
    }

    private void CloseQueue()
    {
        _queueOpen = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(160)));
        fade.Completed += (_, _) => { if (!_queueOpen) QueuePanel.Visibility = Visibility.Collapsed; };
        QueuePanel.BeginAnimation(OpacityProperty, fade);
        QueueSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, TimeSpan.FromMilliseconds(Dur(160))));
    }

    private UIElement QueueRow(SpotifyTrack t, int index)
    {
        var art = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) };
        if (t.ImageUrl != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(t.ImageUrl);
                bmp.DecodePixelWidth = 60;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch
            {
                // keep the placeholder
            }
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        texts.Children.Add(new TextBlock { Text = t.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = t.Artists, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(art);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 1, 4, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = grid, ToolTip = Loc.T("sp.queue.jump") };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += async (_, _) =>
        {
            SoundService.Play(Sfx.Click);
            CloseQueue();
            await Spotify.SkipToAsync(index, t, _spState?.ContextUri, _spState?.Shuffle == true);
            await Task.Delay(500);
            await PollSpotifyAsync();
        };
        return row;
    }
}
