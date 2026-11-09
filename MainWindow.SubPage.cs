using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// Menus inside the open island, in the same style as the volume pop-up's output list: the tab slides away and
/// a list comes in its place ("‹ Saída de áudio" in the header to go back), the island fitting its height.
/// Used by the Music tab (audio output, Spotify devices). The ••• button opens two pills right in the header
/// (Configurações, Sair) where the date was.
/// </summary>
public partial class MainWindow
{
    private Grid _subPage = null!;
    private StackPanel _subHeader = null!;
    private TextBlock _subTitle = null!;
    private bool _subOpen;
    private double _subHeight;
    private Action? _subRefresh;

    private StackPanel _moreChips = null!;
    private bool _moreOpen;
    private FrameworkElement _headerCenter = null!;

    private double SubPageHeight => 34 + 40 + 2 + _subHeight;

    /// <summary>A sub-page opened from another one ("Escolher cidade" from "Clima"): "‹" goes back there.</summary>
    private (string title, Func<FrameworkElement> build, Action? leaving)? _subParent;

    /// <summary>The tallest a sub-page can be and still fit in Notchy's window (420 px, with the header and the menu).</summary>
    private double MaxSubHeight => 420 / Math.Max(0.5, S.SizeScale) - _topHidden - 34 - 40 - 2 - 8;

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitSubPages()
    {
        var host = (Grid)HomeTab.Parent;
        _subPage = new Grid { VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform() };
        host.Children.Add(_subPage);

        // header: "‹ Title"
        var header = (Grid)((FrameworkElement)HeaderA.Parent).Parent;
        var back = new Border
        {
            Width = 24, Height = 20, CornerRadius = new CornerRadius(10), Background = White(0x1A), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        back.MouseEnter += (_, _) => back.Background = White(0x30);
        back.MouseLeave += (_, _) => back.Background = White(0x1A);
        back.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            SubPageBack();
        };
        _subTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        _subHeader = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed };
        _subHeader.Children.Add(back);
        _subHeader.Children.Add(_subTitle);
        header.Children.Add(_subHeader);

        // ••• → pills in the header
        _headerCenter = header.Children.OfType<StackPanel>().First(p => p.HorizontalAlignment == HorizontalAlignment.Center);
        var right = (StackPanel)MoreButton.Parent;
        _moreChips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            RenderTransform = new TranslateTransform(), Margin = new Thickness(0, 0, 4, 0) };
        _moreChips.Children.Add(HeaderChip("", "menu.settings", White(0x17), White(0xDD), () =>
        {
            SetMoreOpen(false);
            App.OpenSettings();
        }));
        var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        _moreChips.Children.Add(HeaderChip("", "menu.quit", new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0x6B, 0x6B)), red, () => Application.Current.Shutdown()));
        right.Children.Insert(right.Children.IndexOf(MoreButton), _moreChips);
    }

    private Border HeaderChip(string glyph, string key, Brush background, Brush foreground, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10.5, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = foreground, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = key });
        var chip = new Border { Child = row, CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(0, 0, 5, 0), Background = background, Cursor = Cursors.Hand };
        chip.MouseEnter += (_, _) => chip.Opacity = 0.8;
        chip.MouseLeave += (_, _) => chip.Opacity = 1;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return chip;
    }

    private void ApplySubPageTexts()
    {
        foreach (var chip in _moreChips.Children.OfType<Border>())
            if (chip.Child is StackPanel sp && sp.Children[1] is TextBlock tb && tb.Tag is string key) tb.Text = Loc.T(key);
    }

    // =====================================================================================
    // ••• pills
    // =====================================================================================

    private void SetMoreOpen(bool open)
    {
        if (open == _moreOpen) return;
        _moreOpen = open;
        // one after the other, never both at once: opening, the date leaves and then the pills come in;
        // closing, the pills leave and then the date comes back
        var outD = TimeSpan.FromMilliseconds(Dur(130));
        var inD = TimeSpan.FromMilliseconds(Dur(220));
        var after = TimeSpan.FromMilliseconds(Dur(140));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = (TranslateTransform)_moreChips.RenderTransform;
        if (open)
        {
            _headerCenter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, outD));
            BatteryText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, outD));
            _moreChips.Visibility = Visibility.Visible;
            _moreChips.Opacity = 0;
            _moreChips.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, inD) { BeginTime = after });
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, inD) { EasingFunction = ease, BeginTime = after });
        }
        else
        {
            var fade = new DoubleAnimation(0, outD);
            fade.Completed += (_, _) => { if (!_moreOpen) _moreChips.Visibility = Visibility.Collapsed; };
            _moreChips.BeginAnimation(OpacityProperty, fade);
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(8, outD));
            _headerCenter.BeginAnimation(OpacityProperty, new DoubleAnimation(1, inD) { BeginTime = after });
            BatteryText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, inD) { BeginTime = after });
        }
        // ••• becomes ✕
        MoreButton.Content = open ? "" : "";
        MoreButton.FontSize = open ? 11 : 13;
    }

    // =====================================================================================
    // sub-pages
    // =====================================================================================

    /// <summary>The tab slides away and <paramref name="build"/>'s list comes in its place.</summary>
    private void OpenSubPage(string title, Func<FrameworkElement> build, Action? refresh = null)
    {
        var content = build();
        _subPage.Children.Clear();
        _subPage.Children.Add(content);
        content.Measure(new Size(440, double.PositiveInfinity));
        _subHeight = Math.Min(Math.Ceiling(content.DesiredSize.Height), MaxSubHeight);
        _subTitle.Text = title;
        _subRefresh = refresh;
        bool wasOpen = _subOpen;
        _subOpen = true;
        SetMoreOpen(false);

        ((FrameworkElement)HeaderA.Parent).Visibility = Visibility.Collapsed;
        _clockHeader.Visibility = Visibility.Collapsed;
        _subHeader.Visibility = Visibility.Visible;

        if (!wasOpen) SlidePages(TabElement(_tab), _subPage, forward: true);
        if (_state == IslandState.Expanded)
        {
            RememberKeepOpenArea();
            AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
        }
    }

    /// <summary>The open sub-page's content changed size (a list grew): the island follows.</summary>
    private void ResizeSubPage()
    {
        if (!_subOpen || _subPage.Children.Count == 0 || _subPage.Children[0] is not FrameworkElement content) return;
        // measure afresh, without the size the layout already settled on
        void Invalidate(DependencyObject d)
        {
            if (d is UIElement u) u.InvalidateMeasure();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Invalidate(VisualTreeHelper.GetChild(d, i));
        }
        Invalidate(content);
        content.Measure(new Size(440, double.PositiveInfinity));
        double h = Math.Min(Math.Ceiling(content.DesiredSize.Height), MaxSubHeight);
        if (Math.Abs(h - _subHeight) < 0.5) return;
        _subHeight = h;
        if (_state == IslandState.Expanded) AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
    }

    /// <summary>"‹": back to the sub-page this one was opened from, or to the tab.</summary>
    private void SubPageBack()
    {
        if (_subParent is { } p)
        {
            _subParent = null;
            p.leaving?.Invoke();
            Keyboard.ClearFocus();
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
            OpenSubPage(p.title, p.build);
            return;
        }
        CloseSubPage(animate: true);
    }

    /// <summary>Rebuilds the open sub-page in place (after picking a device…).</summary>
    private void RefreshSubPage(Func<FrameworkElement> build)
    {
        if (!_subOpen) return;
        OpenSubPage(_subTitle.Text, build, _subRefresh);
    }

    private void CloseSubPage(bool animate)
    {
        if (!_subOpen) return;
        _subOpen = false;
        _queueOpen = false;
        _subParent?.leaving?.Invoke();
        _subParent = null;
        _subRefresh = null;
        _subHeader.Visibility = Visibility.Collapsed;
        UpdateHeader();
        var tab = TabElement(_tab);
        if (animate && _state == IslandState.Expanded) SlidePages(_subPage, tab, forward: false);
        else
        {
            _subPage.Visibility = Visibility.Collapsed;
            tab.Visibility = Visibility.Visible;
            tab.BeginAnimation(OpacityProperty, null);
            tab.Opacity = 1;
            if (tab.RenderTransform is TranslateTransform tt) tt.BeginAnimation(TranslateTransform.XProperty, null);
            tab.RenderTransform = null;
        }
        if (_state == IslandState.Expanded)
        {
            RememberKeepOpenArea();
            AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
        }
    }

    private void SlidePages(UIElement from, UIElement to, bool forward)
    {
        var d = TimeSpan.FromMilliseconds(Dur(300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double dir = forward ? 1 : -1;
        var ft = new TranslateTransform();
        from.RenderTransform = ft;
        ft.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -40 * dir, d) { EasingFunction = ease });
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(180)));
        fade.Completed += (_, _) =>
        {
            if (from.Opacity < 0.05 && !(from == _subPage && _subOpen)) from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(OpacityProperty, fade);

        to.Visibility = Visibility.Visible;
        var tt = new TranslateTransform();
        to.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * dir, 0, d) { EasingFunction = ease });
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
    }

    // =====================================================================================
    // the lists
    // =====================================================================================

    /// <summary>One row of a list: round icon (white when it's the current one), name, a small line, maybe a level bar.</summary>
    private Border ListRow(string glyph, string title, string sub, bool on, float level, Action click, Brush? accent = null)
    {
        var circle = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = on ? Brushes.White : White(0x12), VerticalAlignment = VerticalAlignment.Center,
            Effect = on ? new DropShadowEffect { Color = Colors.White, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.5 } : null,
            Child = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14,
                Foreground = on ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : accent ?? White(0xCC),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 12, Foreground = accent ?? (on ? Brushes.White : White(0xCC)), TextTrimming = TextTrimming.CharacterEllipsis });
        if (sub.Length > 0) texts.Children.Add(new TextBlock { Text = sub, FontSize = 10, Foreground = White(0x70), TextTrimming = TextTrimming.CharacterEllipsis });
        if (level >= 0)
        {
            var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = White(0x1F) });
            var fill = new Border { CornerRadius = new CornerRadius(2), Background = on ? Brushes.White : White(0x99), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            bar.Children.Add(fill);
            bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * Math.Clamp(level, 0, 1);
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
            click();
        };
        return row;
    }

    /// <summary>A pill button for the top line of a list (e.g. "Mudo").</summary>
    private Border ListPill(string glyph, string text, bool on, Action click)
    {
        var ink = on ? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)) : White(0xDD);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border { Child = row, CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 4, 10, 4), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Background = on ? Brushes.White : White(0x14) };
        pill.MouseEnter += (_, _) => pill.Opacity = 0.8;
        pill.MouseLeave += (_, _) => pill.Opacity = 1;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return pill;
    }

    private FrameworkElement ListTop(string hint, FrameworkElement? right)
    {
        var top = new DockPanel { Height = 30, Margin = new Thickness(4, 0, 0, 2) };
        if (right != null)
        {
            DockPanel.SetDock(right, Dock.Right);
            top.Children.Add(right);
        }
        top.Children.Add(new TextBlock { Text = hint, FontSize = 11, Foreground = White(0x80), VerticalAlignment = VerticalAlignment.Center });
        return top;
    }

    /// <summary>A list that scrolls when it's taller than the island can sensibly be.</summary>
    private FrameworkElement Capped(StackPanel list, double max = 230)
    {
        list.Measure(new Size(440, double.PositiveInfinity));
        if (list.DesiredSize.Height <= max) return list;
        return Scroller(list, height: max);
    }

    /// <summary>
    /// A vertical scroller with a thin bar on the right (like the alarms' one, standing): it shows where you are,
    /// and appears while the mouse is on the list or while it scrolls; it hides when there's nothing to scroll.
    /// </summary>
    private Grid Scroller(FrameworkElement content, double? height = null, double? maxHeight = null)
    {
        var sv = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, Margin = new Thickness(0, 0, 8, 0) };
        var thumb = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = White(0xCC), VerticalAlignment = VerticalAlignment.Top,
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 } };
        var track = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = White(0x14), HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 1, 2), Opacity = 0, IsHitTestVisible = false, Child = thumb };
        var host = new Grid { Background = Brushes.Transparent };
        if (height is double h) host.Height = h;
        if (maxHeight is double mh) host.MaxHeight = mh;
        host.Children.Add(sv);
        host.Children.Add(track);

        bool hover = false;
        double shownUntil = 0;
        bool Scrollable() => sv.ScrollableHeight > 1;
        void Place()
        {
            if (!Scrollable() || track.ActualHeight < 1) return;
            double trackH = track.ActualHeight;
            double thumbH = Math.Max(18, trackH * sv.ViewportHeight / Math.Max(1, sv.ExtentHeight));
            thumb.Height = thumbH;
            thumb.Margin = new Thickness(0, (trackH - thumbH) * (sv.VerticalOffset / sv.ScrollableHeight), 0, 0);
        }
        void Show(bool on)
        {
            on &= Scrollable();
            track.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(Dur(on ? 160 : 260))));
        }
        sv.ScrollChanged += (_, _) =>
        {
            Place();
            // while it scrolls, the bar shows for a moment even without the mouse on it
            shownUntil = AnimNow + 1.0;
            Show(true);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (!hover && AnimNow >= shownUntil) Show(false);
            };
            t.Start();
        };
        track.SizeChanged += (_, _) => Place();
        host.MouseEnter += (_, _) =>
        {
            hover = true;
            Place();
            Show(true);
        };
        host.MouseLeave += (_, _) =>
        {
            hover = false;
            Show(false);
        };
        return host;
    }

    // ---- Music tab: audio output ---------------------------------------------------------

    private void OpenOutputSubPage() => OpenSubPage(Loc.T("out.title"), BuildOutputPage);

    private FrameworkElement BuildOutputPage()
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        _audio.TryGet(out _, out bool muted);
        list.Children.Add(ListTop(Loc.T("out.hint"), ListPill("", Loc.T("out.mute"), muted, () =>
        {
            _audio.ToggleMute();
            PollVolume();
            RefreshSubPage(BuildOutputPage);
        })));
        var devices = AudioDevices.List();
        foreach (var d in devices)
        {
            var dev = d;
            string sub = OutputSub(d);
            list.Children.Add(ListRow(((char)AudioDevices.Glyph(d.Kind)).ToString(), d.Name, sub, d.IsDefault, d.Muted ? 0 : d.Volume, () =>
            {
                if (dev.IsDefault) return;
                if (AudioDevices.SetDefault(dev.Id)) SoundService.Play(Sfx.Click);
                _audio.EnsureDevice();
                _lastVol = -1;
                PollVolume();
                UpdateOutputIcon();
                RefreshSubPage(BuildOutputPage);
            }));
        }
        if (devices.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("out.none"), FontSize = 11.5, Foreground = White(0x80), Margin = new Thickness(4, 4, 0, 8) });
        return Capped(list);
    }

    // ---- Music tab: Spotify devices ------------------------------------------------------

    private async Task OpenSpotifyDevicesAsync()
    {
        var devices = await Spotify.DevicesAsync();
        OpenSubPage(Loc.T("sp.devices"), () => BuildSpotifyPage(devices));
    }

    private FrameworkElement BuildSpotifyPage(System.Collections.Generic.IReadOnlyList<SpotifyDevice> devices)
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        list.Children.Add(ListTop(Loc.T("sp.devices.hint"), null));
        foreach (var d in devices)
        {
            var dev = d;
            list.Children.Add(ListRow(Glyph(DeviceGlyph(d.Type)), d.Name, d.Type, d.Active, -1, async () =>
            {
                if (dev.Active) return;
                SoundService.Play(Sfx.Click);
                if (await Spotify.TransferAsync(dev.Id)) await Task.Delay(700);
                await PollSpotifyAsync();
                if (_subOpen) await OpenSpotifyDevicesAsync();
            }));
        }
        if (devices.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("sp.nodevices"), FontSize = 11.5, Foreground = White(0x80), Margin = new Thickness(4, 4, 0, 8) });
        list.Children.Add(ListRow("", Loc.T("music.openapp"), "", false, -1, () =>
        {
            SoundService.Play(Sfx.Click);
            _media.ActivateSourceApp();
        }));
        return Capped(list);
    }
}
