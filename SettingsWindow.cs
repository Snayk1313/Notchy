using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Notchy;

/// <summary>Dark settings window with a sidebar (built in code so it can be rebuilt on language change).</summary>
public sealed class SettingsWindow : Window
{
    private static readonly Color Bg = Color.FromRgb(0x10, 0x10, 0x12);
    private static readonly Color Side = Color.FromRgb(0x16, 0x16, 0x19);
    private static readonly Color Card = Color.FromRgb(0x1B, 0x1B, 0x1F);
    private static readonly Color AccentC = Color.FromRgb(0xA7, 0x8B, 0xFA);

    private static AppSettings S => AppSettings.Current;

    private readonly StackPanel _sideItems = new();
    private readonly ContentControl _pageHost = new();
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, Border> _sideButtons = new();
    private string _page = "general";
    private TextBlock? _sideTitle, _sideSub;

    public SettingsWindow()
    {
        Title = "Notchy";
        double k = UiScale;
        Width = 900 * k;
        Height = 620 * k;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Bg);
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        Icon = TrayIconFactory.CreateImage(64); // same icon as the tray

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 44,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var root = new Grid { Background = new SolidColorBrush(Bg) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ---- sidebar
        var side = new Border { Background = new SolidColorBrush(Side), BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideStack = new StackPanel { Margin = new Thickness(14, 16, 14, 14) };
        sideStack.Children.Add(Logo());
        sideStack.Children.Add(_sideItems);
        side.Child = sideStack;
        root.Children.Add(side);

        // ---- content
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        content.RowDefinitions.Add(new RowDefinition());

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _pageHost,
            Padding = new Thickness(36, 0, 36, 30),
            FocusVisualStyle = null
        };
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);

        root.LayoutTransform = _scale;
        _scale.ScaleX = _scale.ScaleY = k;

        // minimize / maximize / close stay at the standard Windows size, outside the scaled area
        var outer = new Grid { Background = new SolidColorBrush(Bg) };
        outer.Children.Add(root);
        outer.Children.Add(CaptionButtons());
        Content = outer;

        BuildSidebar();
        ShowPage("general");

        AppSettings.Changed += OnSettingsChanged;
        Closed += (_, _) => AppSettings.Changed -= OnSettingsChanged;
    }

    private string _lang = AppSettings.Current.Language;
    private readonly ScaleTransform _scale = new(1, 1);

    private static double UiScale => S.SettingsUiSize switch { 0 => 1.0, 2 => 1.36, _ => 1.18 };

    /// <summary>Applies Small / Medium / Large, growing or shrinking the window with it.</summary>
    private void ApplyUiScale()
    {
        double old = _scale.ScaleX, k = UiScale;
        if (Math.Abs(old - k) < 0.001) return;
        _scale.ScaleX = _scale.ScaleY = k;
        if (WindowState == WindowState.Normal)
        {
            var area = SystemParameters.WorkArea;
            double w = Math.Min(ActualWidth / old * k, area.Width), h = Math.Min(ActualHeight / old * k, area.Height);
            Left = Math.Max(area.Left, Left - (w - ActualWidth) / 2);
            Top = Math.Max(area.Top, Top - (h - ActualHeight) / 2);
            Width = w;
            Height = h;
        }
    }

    private void OnSettingsChanged()
    {
        ApplyUiScale();
        if (_lang == S.Language) return;
        _lang = S.Language;
        BuildSidebar();
        ShowPage(_page, animate: false);
    }

    // =====================================================================================
    // chrome
    // =====================================================================================

    private UIElement Logo()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 22) };
        var pill = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.Black, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1) };
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        eyes.Children.Add(new Ellipse { Width = 4, Height = 7, Fill = Brushes.White, Margin = new Thickness(2, 0, 2, 0) });
        eyes.Children.Add(new Ellipse { Width = 4, Height = 7, Fill = Brushes.White, Margin = new Thickness(2, 0, 2, 0) });
        pill.Child = eyes;
        row.Children.Add(pill);
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _sideTitle = new TextBlock { Text = "Notchy", FontSize = 14, FontWeight = FontWeights.Bold };
        _sideSub = new TextBlock { FontSize = 11, Foreground = Dim(0x88) };
        texts.Children.Add(_sideTitle);
        texts.Children.Add(_sideSub);
        row.Children.Add(texts);
        WindowChrome.SetIsHitTestVisibleInChrome(row, false);
        return row;
    }

    private UIElement CaptionButtons()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(Caption("", () => WindowState = WindowState.Minimized));
        panel.Children.Add(Caption("", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized));
        panel.Children.Add(Caption("", Close, close: true));
        return panel;
    }

    private UIElement Caption(string glyph, Action action, bool close = false)
    {
        var b = new Border { Width = 46, Height = 36, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.Child = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = 10,
            Foreground = Dim(0xCC),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(close ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0x1A, 255, 255, 255));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => action();
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }

    private void BuildSidebar()
    {
        if (_sideSub != null) _sideSub.Text = Loc.T("set.title");
        _sideItems.Children.Clear();
        _sideButtons.Clear();
        AddSide("general", "", Loc.T("page.general"));
        AddSide("island", "", Loc.T("page.island"));
        AddSide("sounds", "", Loc.T("page.sounds"));
        AddSide("claude", "✻", "Claude Code", symbol: true);
        AddSide("spotify", ((char)0xE8D6).ToString(), "Spotify");
        HighlightSide();
    }

    private void AddSide(string key, string glyph, string text, bool symbol = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = symbol ? 14 : 13,
            Width = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = symbol ? new SolidColorBrush(Color.FromRgb(0xE8, 0x82, 0x5F)) : Dim(0xCC)
        });
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = row };
        b.MouseEnter += (_, _) => { if (_page != key) b.Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)); };
        b.MouseLeave += (_, _) => { if (_page != key) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (_page == key) return;
            SoundService.Play(Sfx.Click);
            ShowPage(key);
        };
        _sideItems.Children.Add(b);
        _sideButtons[key] = b;
    }

    private void HighlightSide()
    {
        foreach (var (key, b) in _sideButtons)
            b.Background = key == _page ? new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) : Brushes.Transparent;
    }

    // =====================================================================================
    // pages
    // =====================================================================================

    public void ShowPage(string key) => ShowPage(key, animate: true);

    private void ShowPage(string key, bool animate)
    {
        _page = key;
        HighlightSide();
        var page = key switch
        {
            "island" => IslandPage(),
            "sounds" => SoundsPage(),
            "claude" => ClaudePage(),
            "spotify" => SpotifyPage(),
            _ => GeneralPage()
        };
        _pageHost.Content = page;
        _scroll.ScrollToTop();
        if (!animate) return;
        var tt = new TranslateTransform();
        page.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private StackPanel Page(string title, string subtitle)
    {
        var p = new StackPanel { MaxWidth = 760 };
        p.Children.Add(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 2) });
        p.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Dim(0x88), Margin = new Thickness(0, 0, 0, 10) });
        return p;
    }

    private StackPanel Section(StackPanel page, string title)
    {
        page.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Dim(0xAA), Margin = new Thickness(2, 18, 0, 8) });
        var rows = new StackPanel();
        page.Children.Add(new Border { Background = new SolidColorBrush(Card), CornerRadius = new CornerRadius(12), BorderBrush = new SolidColorBrush(Color.FromArgb(0x0F, 255, 255, 255)), BorderThickness = new Thickness(1), Child = rows });
        return rows;
    }

    private void Row(StackPanel section, string title, string desc, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(16, 12, 16, 12), MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(desc))
            texts.Children.Add(new TextBlock { Text = desc, FontSize = 11.5, Foreground = Dim(0x88), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(texts);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);

        if (section.Children.Count > 0)
            section.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x0F, 255, 255, 255)), Margin = new Thickness(16, 0, 16, 0) });
        section.Children.Add(g);
    }

    private StackPanel GeneralPage()
    {
        var p = Page(Loc.T("page.general"), Loc.T("page.general.sub"));

        var lang = Section(p, Loc.T("sec.language"));
        Row(lang, Loc.T("gen.uisize"), Loc.T("gen.uisize.d"),
            Segmented(new[] { (0, Loc.T("gen.uisize.s")), (1, Loc.T("gen.uisize.m")), (2, Loc.T("gen.uisize.l")) },
                S.SettingsUiSize, v => S.SettingsUiSize = v));
        Row(lang, Loc.T("gen.language"), Loc.T("gen.language.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English") }, S.Language, v => { S.Language = v; Loc.Language = v; }));

        var beh = Section(p, Loc.T("sec.behaviour"));
        Row(beh, Loc.T("gen.startup"), Loc.T("gen.startup.d"), new Switch(StartupManager.IsEnabled, StartupManager.SetEnabled));
        Row(beh, Loc.T("gen.speed"), Loc.T("gen.speed.d"),
            Segmented(new[] { (AnimSpeed.Fast, Loc.T("gen.speed.fast")), (AnimSpeed.Normal, Loc.T("gen.speed.normal")), (AnimSpeed.Smooth, Loc.T("gen.speed.smooth")) },
                S.Speed, v => S.Speed = v));
        Row(beh, Loc.T("gen.game"), Loc.T("gen.game.d"), Toggle(S.GameMode, v => S.GameMode = v));

        var focus = Section(p, Loc.T("sec.focus"));
        Row(focus, Loc.T("gen.focuslen"), Loc.T("gen.focuslen.d"),
            Segmented(new[] { (15, "15"), (25, "25"), (45, "45"), (60, "60") }, S.FocusMinutes, v => S.FocusMinutes = v));
        Row(focus, Loc.T("gen.breaklen"), Loc.T("gen.breaklen.d"),
            Segmented(new[] { (5, "5"), (10, "10"), (15, "15") }, S.BreakMinutes, v => S.BreakMinutes = v));

        var battery = Section(p, Loc.T("sec.battery"));
        Row(battery, Loc.T("bat.alerts"), Loc.T("bat.alerts.d"), Toggle(S.BatteryAlerts, v => S.BatteryAlerts = v));
        Row(battery, Loc.T("bat.threshold"), Loc.T("bat.threshold.d"),
            Segmented(new[] { (10, "10%"), (15, "15%"), (20, "20%"), (30, "30%") }, S.BatteryLowPercent, v => S.BatteryLowPercent = v));

        var upd = Section(p, Loc.T("sec.updates"));
        Row(upd, Loc.T("upd.auto"), Loc.T("upd.auto.d"), Toggle(S.AutoUpdate, v => S.AutoUpdate = v));
        var status = new TextBlock { FontSize = 11.5, Foreground = Dim(0x88), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Margin = new Thickness(0, 0, 6, 0) };
        Button? check = null;
        void Paint(UpdateStatus st)
        {
            status.Text = st switch
            {
                UpdateStatus.NotInstalled => Loc.T("upd.dev"),
                UpdateStatus.Ready => Loc.F("upd.found", Updater.PendingVersion ?? ""),
                UpdateStatus.Error => Loc.T("upd.error"),
                _ => Loc.T("upd.latest")
            };
            if (check?.Content is TextBlock t) t.Text = Loc.T(st == UpdateStatus.Ready ? "upd.install.b" : "upd.check.b");
            if (check != null) check.IsEnabled = st != UpdateStatus.NotInstalled;
        }
        check = ActionButton(Loc.T("upd.check.b"), async () =>
        {
            if (Updater.Pending != null)
            {
                Updater.ApplyAndRestart();
                return;
            }
            status.Text = Loc.T("upd.checking");
            Paint(await Updater.CheckAndDownloadAsync());
        });
        var updCtl = new StackPanel { Orientation = Orientation.Horizontal };
        updCtl.Children.Add(status);
        updCtl.Children.Add(check);
        if (!Updater.IsInstalled) Paint(UpdateStatus.NotInstalled);
        else if (Updater.Pending != null) Paint(UpdateStatus.Ready);
        Row(upd, Loc.F("upd.version", Updater.CurrentVersion), Loc.T("upd.version.d"), updCtl);

        var app = Section(p, Loc.T("sec.notchy"));
        Row(app, Loc.T("gen.reset"), Loc.T("gen.reset.d"), ActionButton(Loc.T("gen.reset.b"), () =>
        {
            AppSettings.ResetAll();
            Loc.Language = S.Language;
            _lang = S.Language;
            BuildSidebar();
            ShowPage(_page, animate: false);
        }));
        Row(app, Loc.T("gen.quit"), Loc.T("gen.quit.d"), ActionButton(Loc.T("gen.quit.b"), () => Application.Current.Shutdown(), danger: true));
        return p;
    }

    private StackPanel IslandPage()
    {
        var p = Page(Loc.T("page.island"), Loc.T("page.island.sub"));

        var look = Section(p, Loc.T("sec.look"));
        Row(look, Loc.T("isl.size"), Loc.T("isl.size.d"),
            Segmented(new[] { (IslandSize.Compact, Loc.T("isl.size.compact")), (IslandSize.Normal, Loc.T("isl.size.normal")), (IslandSize.Large, Loc.T("isl.size.large")) },
                S.Size, v => S.Size = v));
        Row(look, Loc.T("isl.style"), Loc.T("isl.style.d"),
            Segmented(new[] { (IslandStyle.Attached, Loc.T("isl.style.attached")), (IslandStyle.Floating, Loc.T("isl.style.floating")) },
                S.Style, v => S.Style = v));
        Row(look, Loc.T("isl.blur"), Loc.T("isl.blur.d"), new Switch(S.BlurEnabled, v =>
        {
            S.BlurEnabled = v;
            AppSettings.Commit();
            Dispatcher.InvokeAsync(() => ShowPage("island", animate: false));
        }));
        if (S.BlurEnabled)
        {
            Row(look, Loc.T("isl.blurint"), Loc.T("isl.blurint.d"), Slider(S.BlurIntensity, 0, 100, v => S.BlurIntensity = v, "%"));
            Row(look, Loc.T("isl.blurtint"), Loc.T("isl.blurtint.d"), Slider(S.BlurTint, 0, 100, v => S.BlurTint = v, "%"));
        }
        else
            Row(look, Loc.T("isl.see"), Loc.T("isl.see.d"), Slider(S.SeeThrough, 0, 85, v => S.SeeThrough = v, "%"));
        Row(look, Loc.T("isl.shadow"), Loc.T("isl.shadow.d"), Toggle(S.IslandShadow, v => S.IslandShadow = v));
        Row(look, Loc.T("isl.glow"), Loc.T("isl.glow.d"),
            Segmented(new[] { (0, Loc.T("off")), (1, Loc.T("isl.glow.subtle")), (2, Loc.T("isl.glow.medium")), (3, Loc.T("isl.glow.strong")) },
                S.GlowLevel, v => S.GlowLevel = v));
        Row(look, Loc.T("isl.gradient"), Loc.T("isl.gradient.d"), Slider(S.GradientStrength, 0, 100, v => S.GradientStrength = v, "%"));
        Row(look, Loc.T("isl.musiccolor"), Loc.T("isl.musiccolor.d"), Toggle(S.MusicColor, v => S.MusicColor = v));
        Row(look, Loc.T("isl.open"), Loc.T("isl.open.d"),
            Segmented(new[] { (OpenMode.Hover, Loc.T("isl.open.hover")), (OpenMode.Click, Loc.T("isl.open.click")) }, S.Open, v => S.Open = v));
        Row(look, Loc.T("isl.clock"), Loc.T("isl.clock.d"),
            Segmented(new[] { (ClockMode.Auto, Loc.T("isl.clock.auto")), (ClockMode.H12, "12h · 6:30 PM"), (ClockMode.H24, "24h · 18:30") }, S.Clock, v => S.Clock = v));

        var on = Section(p, Loc.T("sec.onisland"));
        Row(on, Loc.T("isl.lyrics"), Loc.T("isl.lyrics.d"), Toggle(S.LyricsEnabled, v => S.LyricsEnabled = v));
        Row(on, Loc.T("isl.volume"), Loc.T("isl.volume.d"), Toggle(S.VolumeOsd, v => S.VolumeOsd = v));
        Row(on, Loc.T("isl.claude"), Loc.T("isl.claude.d"), Toggle(S.ClaudeShowInCompact, v => S.ClaudeShowInCompact = v));
        Row(on, Loc.T("isl.scroll"), Loc.T("isl.scroll.d"), Toggle(S.ScrollVolume, v => S.ScrollVolume = v));
        Row(on, Loc.T("isl.shelf"), Loc.T("isl.shelf.d"), Toggle(S.ShelfEnabled, v => S.ShelfEnabled = v));

        var eyes = Section(p, Loc.T("sec.eyes"));
        Row(eyes, Loc.T("eye.album"), Loc.T("eye.album.d"), Toggle(S.EyesUseAlbumColor, v => S.EyesUseAlbumColor = v));
        Row(eyes, Loc.T("eye.color"), Loc.T("eye.color.d"), EyeSwatches());
        Row(eyes, Loc.T("eye.lively"), Loc.T("eye.lively.d"), Toggle(S.LivelyEyes, v => S.LivelyEyes = v));
        return p;
    }

    private static readonly string[] EyePresets = { "#FFFFFF", "#B8F5D0", "#FFB070", "#FF8FC8", "#7FE3FF", "#C4B0FF", "#FFE27A" };

    private UIElement EyeSwatches()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        bool manual = !S.EyesUseAlbumColor;

        void Pick(string hex)
        {
            S.EyeColor = hex;
            S.EyesUseAlbumColor = false;
            AppSettings.Commit();
            Dispatcher.InvokeAsync(() =>
            {
                double offset = _scroll.VerticalOffset;
                ShowPage("island", animate: false);
                _scroll.ScrollToVerticalOffset(offset);
            });
        }

        foreach (var hex in EyePresets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool selected = manual && string.Equals(S.EyeColor, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(selected ? Colors.White : Color.FromArgb(0x30, 255, 255, 255)),
                StrokeThickness = selected ? 2.5 : 1,
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = hex
            };
            if (selected) dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9 };
            dot.MouseLeftButtonUp += (_, _) => { SoundService.Play(Sfx.Toggle); Pick(hex); };
            panel.Children.Add(dot);
        }

        bool custom = manual && Array.FindIndex(EyePresets, h => string.Equals(h, S.EyeColor, StringComparison.OrdinalIgnoreCase)) < 0;
        var customButton = ActionButton(Loc.T("eye.custom"), () =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(S.EyeColor);
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch
            {
                // keep default
            }
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                Pick($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        });
        if (custom)
        {
            var c = (Color)ColorConverter.ConvertFromString(S.EyeColor);
            customButton.Background = new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B));
            customButton.BorderBrush = new SolidColorBrush(c);
        }
        panel.Children.Add(customButton);
        return panel;
    }

    private StackPanel SoundsPage()
    {
        var p = Page(Loc.T("page.sounds"), Loc.T("page.sounds.sub"));

        var fx = Section(p, Loc.T("sec.effects"));
        Row(fx, Loc.T("snd.enabled"), Loc.T("snd.enabled.d"), Toggle(S.SoundsEnabled, v => S.SoundsEnabled = v));
        Row(fx, Loc.T("snd.volume"), Loc.T("snd.volume.d"), Slider(S.SoundVolume, 0, 100, v => S.SoundVolume = v, "%", () => SoundService.Preview(Sfx.Toggle)));
        Row(fx, Loc.T("snd.openclose"), Loc.T("snd.openclose.d"), Toggle(S.SoundOpenClose, v => S.SoundOpenClose = v));
        Row(fx, Loc.T("snd.clicks"), Loc.T("snd.clicks.d"), Toggle(S.SoundClicks, v => S.SoundClicks = v));
        Row(fx, Loc.T("snd.focus"), Loc.T("snd.focus.d"), Toggle(S.SoundFocus, v => S.SoundFocus = v));

        var listen = Section(p, Loc.T("sec.listen"));
        foreach (var (sfx, key) in new[] { (Sfx.Expand, "snd.expand"), (Sfx.Collapse, "snd.collapse"), (Sfx.Click, "snd.click"), (Sfx.Done, "snd.done"), (Sfx.Attention, "snd.attention"), (Sfx.FocusEnd, "snd.focusend") })
            Row(listen, Loc.T(key), "", ActionButton("▶  " + Loc.T("snd.play"), () => SoundService.Preview(sfx), silent: true));
        return p;
    }

    private StackPanel SpotifyPage()
    {
        var p = Page("Spotify", Loc.T("sp.page.sub"));
        var spotify = (Application.Current.MainWindow as MainWindow)?.Spotify;

        // --- connection
        var conn = Section(p, Loc.T("sp.sec.connection"));
        bool connected = spotify?.IsConnected == true;
        var status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Text = Loc.T(connected ? "sp.status.on" : "sp.status.off") };
        var feedback = new TextBlock { FontSize = 11.5, Foreground = Dim(0x99), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(connected ? Color.FromRgb(0x1D, 0xB9, 0x54) : Color.FromRgb(0x66, 0x66, 0x6E)) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(dot);
        head.Children.Add(status);
        left.Children.Add(head);
        left.Children.Add(feedback);
        if (connected && spotify != null)
        {
            feedback.Text = "…";
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var name = await spotify.UserNameAsync();
                feedback.Text = name != null ? Loc.F("sp.status.as", name) : Loc.T("sp.status.check");
            });
        }
        else feedback.Text = Loc.T("sp.status.off.d");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (!connected)
        {
            buttons.Children.Add(ActionButton(Loc.T("sp.connect"), async () =>
            {
                if (spotify == null) return;
                feedback.Text = Loc.T("sp.waiting");
                var (ok, msg) = await spotify.ConnectAsync();
                feedback.Text = msg;
                if (ok) ShowPage("spotify", animate: false);
            }, primary: true));
        }
        else
        {
            buttons.Children.Add(ActionButton(Loc.T("sp.disconnect"), () =>
            {
                spotify?.Disconnect();
                ShowPage("spotify", animate: false);
            }));
        }
        var g = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        g.Children.Add(buttons);
        conn.Children.Add(g);

        // --- your Spotify app
        var app = Section(p, Loc.T("sp.sec.app"));
        var idBox = new TextBox
        {
            Width = 280,
            Text = S.SpotifyClientId,
            Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12
        };
        idBox.TextChanged += (_, _) =>
        {
            S.SpotifyClientId = idBox.Text.Trim();
            AppSettings.Current.Save();
        };
        Row(app, Loc.T("sp.clientid"), Loc.T("sp.clientid.d"), idBox);
        Row(app, Loc.T("sp.dashboard"), Loc.T("sp.dashboard.d"), ActionButton(Loc.T("sp.dashboard.b"), () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SpotifyService.DashboardUrl) { UseShellExecute = true }); } catch { }
        }));
        var uriRow = new StackPanel { Orientation = Orientation.Horizontal };
        uriRow.Children.Add(new TextBlock { Text = SpotifyService.RedirectUri, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, Foreground = Dim(0xCC), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        uriRow.Children.Add(ActionButton(Loc.T("chat.copy"), () => { try { Clipboard.SetText(SpotifyService.RedirectUri); } catch { } }));
        Row(app, Loc.T("sp.redirect"), Loc.T("sp.redirect.d"), uriRow);

        // --- steps
        var steps = Section(p, Loc.T("sp.sec.steps"));
        steps.Children.Add(new TextBlock { Text = Loc.T("sp.steps"), FontSize = 12, Foreground = Dim(0xAA), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 12, 16, 14), LineHeight = 20 });
        return p;
    }

    private StackPanel ClaudePage()
    {
        var p = Page(Loc.T("page.claude"), Loc.T("page.claude.sub"));

        var conn = Section(p, Loc.T("sec.connection"));
        bool installed = ClaudeHooks.IsInstalled;
        bool moved = installed && ClaudeHooks.PointsElsewhere;

        var status = new StackPanel { Orientation = Orientation.Horizontal };
        status.Children.Add(new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(installed && !moved ? Color.FromRgb(0x34, 0xC7, 0x59) : moved ? Color.FromRgb(0xF5, 0xA5, 0x24) : Color.FromRgb(0x66, 0x66, 0x6E)) });
        status.Children.Add(new TextBlock { Text = Loc.T(moved ? "cl.status.moved" : installed ? "cl.status.on" : "cl.status.off"), FontSize = 13, FontWeight = FontWeights.SemiBold });

        var feedback = new TextBlock { FontSize = 11, Foreground = Dim(0x88), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12), Visibility = Visibility.Collapsed };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        void Do(Func<string> op)
        {
            try
            {
                string backup = op();
                ShowPage("claude", animate: false);
                if (!string.IsNullOrEmpty(backup) && _pageHost.Content is StackPanel page)
                    page.Children.Add(new TextBlock { Text = $"{Loc.T("cl.backup")} {backup}", FontSize = 11, Foreground = Dim(0x77), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 10, 0, 0) });
            }
            catch (Exception ex)
            {
                feedback.Text = $"{Loc.T("cl.error")}: {ex.Message}";
                feedback.Visibility = Visibility.Visible;
                App.Log(ex);
            }
        }
        if (!installed || moved)
            buttons.Children.Add(ActionButton(Loc.T(moved ? "cl.update" : "cl.connect"), () => Do(ClaudeHooks.Install), primary: true));
        if (installed)
            buttons.Children.Add(ActionButton(Loc.T("cl.disconnect"), () => Do(ClaudeHooks.Uninstall)));

        var g = new Grid { Margin = new Thickness(16, 14, 16, 14) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(status);
        left.Children.Add(new TextBlock { Text = Loc.T(moved ? "cl.status.moved.d" : installed ? "cl.status.on.d" : "cl.status.off.d"), FontSize = 11.5, Foreground = Dim(0x88), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        left.Children.Add(new TextBlock { Text = $"{Loc.T("cl.path")}: {ClaudeHooks.SettingsPath}", FontSize = 10.5, Foreground = Dim(0x66), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        g.Children.Add(left);
        Grid.SetColumn(buttons, 1);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(buttons);
        conn.Children.Add(g);
        conn.Children.Add(feedback);

        var follow = Section(p, Loc.T("sec.alerts"));
        Row(follow, Loc.T("cl.enabled"), Loc.T("cl.enabled.d"), Toggle(S.ClaudeEnabled, v => S.ClaudeEnabled = v));
        Row(follow, Loc.T("isl.claude"), Loc.T("isl.claude.d"), Toggle(S.ClaudeShowInCompact, v => S.ClaudeShowInCompact = v));
        Row(follow, Loc.T("cl.pod"), Loc.T("cl.pod.d"), Toggle(S.ClaudePod, v => S.ClaudePod = v));

        var chat = Section(p, Loc.T("sec.chat"));
        var chatStatus = new TextBlock { FontSize = 11.5, Foreground = Dim(0x99), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal };
        checkRow.Children.Add(chatStatus);
        async void Check()
        {
            chatStatus.Text = "…";
            var (found, loggedIn, _) = await ClaudeRunner.CheckAsync();
            chatStatus.Text = Loc.T(!found ? "cl.check.missing" : loggedIn ? "cl.check.ok" : "cl.check.login");
            chatStatus.Foreground = new SolidColorBrush(found && loggedIn ? Color.FromRgb(0x5E, 0xD6, 0x7D) : Color.FromRgb(0xF5, 0xA5, 0x24));
        }
        checkRow.Children.Add(ActionButton(Loc.T("cl.check"), Check));
        Row(chat, Loc.T("cl.chatstatus"), Loc.T("cl.chatstatus.d"), checkRow);
        Row(chat, Loc.T("cl.translate"), Loc.T("cl.translate.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English"), ("es", "Español") }, S.ChatTranslateTo, v => S.ChatTranslateTo = v));
        Check();
        Row(follow, Loc.T("cl.sounddone"), Loc.T("cl.sounddone.d"), Toggle(S.ClaudeSoundDone, v => S.ClaudeSoundDone = v));
        Row(follow, Loc.T("cl.soundattention"), Loc.T("cl.soundattention.d"), Toggle(S.ClaudeSoundAttention, v => S.ClaudeSoundAttention = v));
        Row(follow, Loc.T("cl.notify"), Loc.T("cl.notify.d"), Toggle(S.ClaudeWindowsNotifications, v => S.ClaudeWindowsNotifications = v));
        Row(follow, Loc.T("cl.test"), Loc.T("cl.test.d"), ActionButton(Loc.T("cl.test.b"), () => (Application.Current.MainWindow as MainWindow)?.SimulateClaudeTask()));
        return p;
    }

    // =====================================================================================
    // controls
    // =====================================================================================

    private static SolidColorBrush Dim(byte alpha) => new(Color.FromArgb(alpha, 255, 255, 255));

    private static Switch Toggle(bool value, Action<bool> set) => new(value, v =>
    {
        set(v);
        AppSettings.Commit();
    });

    private static UIElement Segmented<T>(IEnumerable<(T value, string label)> options, T current, Action<T> set)
    {
        var host = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(3)
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        host.Child = panel;
        var items = new List<(T value, Border b, TextBlock t)>();

        void Paint(T selected)
        {
            foreach (var (v, b, t) in items)
            {
                bool sel = EqualityComparer<T>.Default.Equals(v, selected);
                b.Background = new SolidColorBrush(sel ? Color.FromArgb(0x38, AccentC.R, AccentC.G, AccentC.B) : Colors.Transparent);
                t.Foreground = sel ? new SolidColorBrush(Color.FromRgb(0xD4, 0xC6, 0xFF)) : Dim(0xAA);
                t.FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        foreach (var (value, label) in options)
        {
            var t = new TextBlock { Text = label, FontSize = 11.5 };
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(11, 5, 11, 5), Cursor = Cursors.Hand, Child = t, Background = Brushes.Transparent };
            var v = value;
            b.MouseLeftButtonUp += (_, _) =>
            {
                SoundService.Play(Sfx.Toggle);
                set(v);
                Paint(v);
                AppSettings.Commit();
            };
            items.Add((value, b, t));
            panel.Children.Add(b);
        }
        Paint(current);
        return host;
    }

    private static UIElement Slider(int value, int min, int max, Action<int> set, string suffix, Action? onCommit = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var bar = new BarSlider { Width = 150, Height = 20, Value = (value - min) / (double)(max - min), FillBrush = new SolidColorBrush(AccentC) };
        var text = new TextBlock { Text = value + suffix, FontSize = 11.5, Foreground = Dim(0xAA), Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        bar.ValueChanging += v =>
        {
            int iv = (int)Math.Round(min + v * (max - min));
            text.Text = iv + suffix;
            set(iv);
            AppSettings.Commit();
        };
        bar.ValueCommitted += _ => onCommit?.Invoke();
        panel.Children.Add(bar);
        panel.Children.Add(text);
        return panel;
    }

    private static Button ActionButton(string text, Action action, bool primary = false, bool danger = false, bool silent = false)
    {
        var bg = primary ? Color.FromRgb(0xA7, 0x8B, 0xFA) : danger ? Color.FromArgb(0x30, 0xFF, 0x5A, 0x5A) : Color.FromArgb(0x1C, 255, 255, 255);
        var fg = primary ? Color.FromRgb(0x14, 0x10, 0x24) : danger ? Color.FromRgb(0xFF, 0x8A, 0x8A) : Colors.White;
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("PillButton"),
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(6, 0, 0, 0),
            Tag = silent ? "nosound" : null,
            Content = new TextBlock { Text = text, FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 12, FontWeight = FontWeights.SemiBold }
        };
        b.Click += (_, _) => action();
        return b;
    }
}

/// <summary>Animated on/off switch.</summary>
public sealed class Switch : Border
{
    private readonly Ellipse _knob = new() { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _move = new();
    private readonly Action<bool> _changed;
    private bool _on;

    private static readonly Color OnColor = Color.FromRgb(0xA7, 0x8B, 0xFA);
    private static readonly Color OffColor = Color.FromArgb(0x30, 255, 255, 255);

    public Switch(bool on, Action<bool> changed)
    {
        _on = on;
        _changed = changed;
        Width = 40;
        Height = 22;
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(3);
        Cursor = Cursors.Hand;
        _knob.RenderTransform = _move;
        Child = _knob;
        Background = new SolidColorBrush(on ? OnColor : OffColor);
        _move.X = on ? 18 : 0;
        MouseLeftButtonUp += (_, _) => Set(!_on);
    }

    private void Set(bool on)
    {
        _on = on;
        SoundService.Play(Sfx.Toggle);
        var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        var brush = new SolidColorBrush(((SolidColorBrush)Background).Color);
        Background = brush;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? OnColor : OffColor, TimeSpan.FromMilliseconds(200)));
        _changed(on);
    }
}
