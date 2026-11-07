using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The settings window, in Notchy's look: a dark sidebar with the little eyes (they follow the mouse, blink
/// and smile when you turn something on), a search box and one category per feature, each in the colour of
/// its tab on the island. Built in code so it can be rebuilt on language change.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly Color Bg = Color.FromRgb(0x12, 0x13, 0x17);
    private static readonly Color Side = Color.FromRgb(0x0B, 0x0C, 0x0F);
    private static readonly Color Ink = Color.FromRgb(0x14, 0x0E, 0x1E); // text on a coloured fill

    private static AppSettings S => AppSettings.Current;

    /// <summary>A category: key, icon, whether the icon is a symbol (✻), colour.</summary>
    private sealed record Category(string Key, string Glyph, bool Symbol, Color Color);

    private static readonly Category[] Categories =
    {
        new("general", "", false, Color.FromRgb(0xE6, 0xE6, 0xEE)),
        new("look", "", false, Color.FromRgb(0xA7, 0x8B, 0xFA)),
        new("behave", "", false, Color.FromRgb(0x7C, 0xC8, 0xFF)),
        new("music", "", false, Color.FromRgb(0xFF, 0x7E, 0xB6)),
        new("claude", "✻", true, Color.FromRgb(0xE8, 0x82, 0x5F)),
        new("focus", "", false, Color.FromRgb(0xB7, 0x9C, 0xFF)),
        new("files", "", false, Color.FromRgb(0x5E, 0xD6, 0x7D)),
        new("sounds", "", false, Color.FromRgb(0xFF, 0xD3, 0x6B)),
        new("about", "", false, Color.FromRgb(0x9A, 0xA3, 0xB5)),
    };

    /// <summary>Something was switched on: the sidebar's eyes smile for a moment.</summary>
    public static event Action? Cheer;

    private readonly StackPanel _sideItems = new();
    private readonly ContentControl _pageHost = new();
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<string, (Border item, Border tile, Border glow, TextBlock icon, TextBlock text)> _side = new();
    private string _page = "general";
    private Color _accent = Categories[0].Color;
    private TextBlock? _sideSub, _searchHint;
    private TextBox? _search;
    private string _lang = AppSettings.Current.Language;

    // the sidebar's eyes
    private readonly TranslateTransform _eyesLook = new();
    private readonly ScaleTransform _eyesBlink = new(1, 1);
    private readonly List<Rectangle> _eyes = new();
    private readonly List<Path> _smiles = new();
    private readonly DispatcherTimer _eyesTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private FrameworkElement? _eyesPill;
    private DateTime _joyUntil;

    // search: while a page is built for the results, every row is collected here
    private List<(Grid row, string text)>? _collect;

    /// <summary>Everything is drawn this much bigger, for comfortable reading.</summary>
    private const double Scale = 1.2;

    public SettingsWindow()
    {
        Title = "Notchy";
        var area = SystemParameters.WorkArea;
        Width = Math.Min(920 * Scale, area.Width - 40);
        Height = Math.Min(640 * Scale, area.Height - 40);
        MinWidth = 680;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Bg);
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;
        UseLayoutRounding = true;
        Icon = TrayIconFactory.CreateImage(64); // same icon as the tray

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 40 * Scale,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var root = new Grid { Background = new SolidColorBrush(Bg) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // ---- sidebar
        var side = new Border { Background = new SolidColorBrush(Side), BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)), BorderThickness = new Thickness(0, 0, 1, 0) };
        var sideGrid = new Grid { Margin = new Thickness(12, 18, 12, 14) };
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideGrid.RowDefinitions.Add(new RowDefinition());
        sideGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sideGrid.Children.Add(Brand());
        var search = SearchBox();
        Grid.SetRow(search, 1);
        sideGrid.Children.Add(search);
        var itemsScroll = new ScrollViewer { Content = _sideItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FocusVisualStyle = null };
        Grid.SetRow(itemsScroll, 2);
        sideGrid.Children.Add(itemsScroll);
        var version = new TextBlock { Text = "Notchy " + Updater.CurrentVersion, FontSize = 10.5, Foreground = Dim(0x44), Margin = new Thickness(10, 8, 0, 0) };
        Grid.SetRow(version, 3);
        sideGrid.Children.Add(version);
        side.Child = sideGrid;
        root.Children.Add(side);

        // ---- content
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        content.RowDefinitions.Add(new RowDefinition());
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _pageHost,
            Padding = new Thickness(34, 0, 34, 30),
            FocusVisualStyle = null
        };
        Grid.SetRow(_scroll, 1);
        content.Children.Add(_scroll);
        Grid.SetColumn(content, 1);
        root.Children.Add(content);

        root.LayoutTransform = new ScaleTransform(Scale, Scale);

        // (minimize / maximize / close stay at the standard Windows size, outside the scaled area)
        var outer = new Grid { Background = new SolidColorBrush(Bg) };
        outer.Children.Add(root);
        outer.Children.Add(CaptionButtons());
        Content = outer;

        BuildSidebar();
        ShowPage("general", animate: false);

        AppSettings.Changed += OnSettingsChanged;
        Cheer += OnCheer;
        _eyesTimer.Tick += (_, _) => AnimateEyes();
        _eyesTimer.Start();
        Closed += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            Cheer -= OnCheer;
            _eyesTimer.Stop();
        };
    }

    private void OnSettingsChanged()
    {
        if (_lang == S.Language) return;
        _lang = S.Language;
        BuildSidebar();
        ShowPage(_page, animate: false);
    }

    // =====================================================================================
    // sidebar
    // =====================================================================================

    /// <summary>The little island with its eyes, and the name.</summary>
    private UIElement Brand()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 16) };
        var eyes = new Grid();
        var look = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _eyesLook };
        for (int i = 0; i < 2; i++)
        {
            var cell = new Grid { Width = 9, Height = 12, Margin = new Thickness(2, 0, 2, 0) };
            var eye = new Rectangle { Width = 6, Height = 10, RadiusX = 3, RadiusY = 3, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = _eyesBlink, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var smile = new Path
            {
                Data = Geometry.Parse("M0,6 Q4.5,0 9,6"), StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            cell.Children.Add(eye);
            cell.Children.Add(smile);
            look.Children.Add(cell);
            _eyes.Add(eye);
            _smiles.Add(smile);
        }
        look.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.9 };
        eyes.Children.Add(look);
        var pill = new Border
        {
            Width = 56, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), BorderThickness = new Thickness(1), Child = eyes
        };
        _eyesPill = pill;
        row.Children.Add(pill);
        var texts = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = "Notchy", FontSize = 15, FontWeight = FontWeights.Bold });
        _sideSub = new TextBlock { FontSize = 11, Foreground = Dim(0x77) };
        texts.Children.Add(_sideSub);
        row.Children.Add(texts);
        WindowChrome.SetIsHitTestVisibleInChrome(row, false);
        PaintEyes();
        return row;
    }

    private void PaintEyes()
    {
        var b = new SolidColorBrush(_accent);
        foreach (var e in _eyes) e.Fill = b;
        foreach (var s in _smiles) s.Stroke = b;
        if (_eyes.Count > 0 && _eyes[0].Parent is FrameworkElement cell && cell.Parent is StackPanel look && look.Effect is DropShadowEffect glow)
            glow.Color = _accent;
    }

    private void OnCheer() => _joyUntil = DateTime.Now.AddMilliseconds(750);

    /// <summary>Eyes follow the mouse, blink now and then, and smile (^ ^) for a moment after something's turned on.</summary>
    private void AnimateEyes()
    {
        if (_eyesPill == null || !IsVisible) return;
        bool joy = DateTime.Now < _joyUntil;
        foreach (var e in _eyes) e.Visibility = joy ? Visibility.Collapsed : Visibility.Visible;
        foreach (var s in _smiles) s.Visibility = joy ? Visibility.Visible : Visibility.Collapsed;
        double tx = 0, ty = 0;
        try
        {
            var c = _eyesPill.PointToScreen(new Point(_eyesPill.ActualWidth / 2, _eyesPill.ActualHeight / 2));
            Native.GetCursorPos(out var p);
            tx = Math.Clamp((p.X - c.X) / 320, -1, 1) * 4;
            ty = Math.Clamp((p.Y - c.Y) / 320, -1, 1) * 2.5;
        }
        catch
        {
            // not on screen yet
        }
        _eyesLook.X += (tx - _eyesLook.X) * 0.25;
        _eyesLook.Y += (ty - _eyesLook.Y) * 0.25;
        double t = Environment.TickCount64 / 1000.0;
        _eyesBlink.ScaleY = t % 4.3 < 0.13 ? 0.15 : 1;
    }

    private UIElement SearchBox()
    {
        var grid = new Grid();
        _search = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White,
            FontSize = 12.5, Padding = new Thickness(26, 7, 8, 7), FocusVisualStyle = null, VerticalContentAlignment = VerticalAlignment.Center
        };
        _searchHint = new TextBlock { FontSize = 12.5, Foreground = Dim(0x55), Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var glass = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 11.5, Foreground = Dim(0x77),
            Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        grid.Children.Add(_search);
        grid.Children.Add(_searchHint);
        grid.Children.Add(glass);
        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            string q = _search.Text.Trim();
            if (q.Length == 0) ShowPage(_page, animate: false);
            else ShowResults(q);
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _search.Text = "";
            e.Handled = true;
        };
        var box = new Border
        {
            Child = grid, CornerRadius = new CornerRadius(10), Margin = new Thickness(2, 0, 2, 12),
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)), BorderThickness = new Thickness(1)
        };
        WindowChrome.SetIsHitTestVisibleInChrome(box, true);
        return box;
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
        var b = new Border { Width = 46, Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.Child = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10, Foreground = Dim(0xCC),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
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
        if (_searchHint != null) _searchHint.Text = Loc.T("s.search");
        _sideItems.Children.Clear();
        _side.Clear();
        foreach (var c in Categories) AddSide(c);
        HighlightSide();
    }

    private void AddSide(Category c)
    {
        var icon = new TextBlock
        {
            Text = c.Glyph, FontFamily = c.Symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = c.Symbol ? 13.5 : 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var tile = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Child = icon };
        // the selected tile glows: a plain shape behind it carries the glow (the icon stays crisp)
        var glow = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(c.Color), Opacity = 0,
            Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.7, Color = c.Color }
        };
        var tileHost = new Grid();
        tileHost.Children.Add(glow);
        tileHost.Children.Add(tile);
        var text = new TextBlock { Text = Loc.T("s.page." + c.Key), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(tileHost);
        row.Children.Add(text);

        var item = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 1, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = row };
        item.MouseEnter += (_, _) => { if (_page != c.Key || Searching) item.Background = new SolidColorBrush(Color.FromArgb(0x0C, 255, 255, 255)); };
        item.MouseLeave += (_, _) => HighlightSide();
        item.MouseLeftButtonUp += (_, _) =>
        {
            SoundService.Play(Sfx.Click);
            if (_search != null && _search.Text.Length > 0) _search.Text = "";
            if (_page != c.Key) ShowPage(c.Key);
        };
        _sideItems.Children.Add(item);
        _side[c.Key] = (item, tile, glow, icon, text);
    }

    private bool Searching => _search != null && _search.Text.Trim().Length > 0;

    private void HighlightSide()
    {
        foreach (var c in Categories)
        {
            if (!_side.TryGetValue(c.Key, out var s)) continue;
            bool sel = c.Key == _page && !Searching;
            s.item.Background = sel ? new SolidColorBrush(Color.FromArgb(0x26, c.Color.R, c.Color.G, c.Color.B)) : Brushes.Transparent;
            s.tile.Background = new SolidColorBrush(sel ? c.Color : Color.FromArgb(0x2E, c.Color.R, c.Color.G, c.Color.B));
            s.icon.Foreground = new SolidColorBrush(sel ? Ink : c.Color);
            s.text.Foreground = sel ? Brushes.White : Dim(0xB0);
            s.glow.Opacity = sel ? 1 : 0;
        }
    }

    // =====================================================================================
    // pages
    // =====================================================================================

    public void ShowPage(string key) => ShowPage(key, animate: true);

    private void ShowPage(string key, bool animate)
    {
        key = key switch { "island" => "look", "spotify" => "music", _ => key }; // old names
        if (Categories.All(c => c.Key != key)) key = "general";
        _page = key;
        _accent = Categories.First(c => c.Key == key).Color;
        PaintEyes();
        HighlightSide();
        var page = BuildPage(key);
        _pageHost.Content = page;
        _scroll.ScrollToTop();
        if (animate) Appear(page);
    }

    private StackPanel BuildPage(string key) => key switch
    {
        "look" => LookPage(),
        "behave" => BehavePage(),
        "music" => MusicPage(),
        "claude" => ClaudePage(),
        "focus" => FocusPage(),
        "files" => FilesPage(),
        "sounds" => SoundsPage(),
        "about" => AboutPage(),
        _ => GeneralPage()
    };

    private static void Appear(FrameworkElement page)
    {
        var tt = new TranslateTransform();
        page.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    /// <summary>Every page's rows that mention the text, grouped by page.</summary>
    private void ShowResults(string q)
    {
        HighlightSide();
        var keep = _accent;
        var p = new StackPanel { MaxWidth = 760 };
        p.Children.Add(Header("", false, Color.FromArgb(0x30, 255, 255, 255), Loc.T("s.results"), Loc.F("s.results.for", q), whiteIcon: true));
        int found = 0;
        foreach (var c in Categories)
        {
            _accent = c.Color;
            _collect = new List<(Grid, string)>();
            BuildPage(c.Key);
            var hits = _collect.Where(r => r.text.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
            _collect = null;
            if (hits.Count == 0) continue;
            found += hits.Count;
            var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 18, 0, 7) };
            label.Children.Add(new TextBlock
            {
                Text = c.Glyph, FontFamily = c.Symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 11, Foreground = new SolidColorBrush(c.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            });
            label.Children.Add(new TextBlock { Text = Loc.T("s.page." + c.Key).ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c.Color) });
            p.Children.Add(label);
            var rows = new StackPanel();
            p.Children.Add(CardOf(rows));
            foreach (var (row, _) in hits)
            {
                if (row.Parent is Panel parent) parent.Children.Remove(row);
                AddToCard(rows, row);
            }
        }
        _accent = keep;
        if (found == 0)
            p.Children.Add(new TextBlock { Text = Loc.T("s.nothing") + " 👀", FontSize = 13, Foreground = Dim(0x66), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 50, 0, 0) });
        _pageHost.Content = p;
        _scroll.ScrollToTop();
    }

    private StackPanel Page(string key)
    {
        var c = Categories.First(x => x.Key == key);
        var p = new StackPanel { MaxWidth = 760 };
        p.Children.Add(Header(c.Glyph, c.Symbol, c.Color, Loc.T("s.page." + key), Loc.T("s.page." + key + ".sub")));
        return p;
    }

    private UIElement Header(string glyph, bool symbol, Color color, string title, string subtitle, bool whiteIcon = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        var tile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(color),
            Child = new TextBlock
            {
                Text = glyph, FontFamily = symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = symbol ? 19 : 17, Foreground = whiteIcon ? Brushes.White : new SolidColorBrush(Ink),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var glow = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(color), Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 20, Opacity = whiteIcon ? 0 : 0.55, Color = color } };
        var host = new Grid();
        host.Children.Add(glow);
        host.Children.Add(tile);
        row.Children.Add(host);
        var texts = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.Bold });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = Dim(0x80), Margin = new Thickness(0, 1, 0, 0) });
        row.Children.Add(texts);
        return row;
    }

    private StackPanel Section(StackPanel page, string? title = null)
    {
        if (title != null)
            page.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Dim(0x66), Margin = new Thickness(4, 20, 0, 8) });
        else page.Children.Add(new Border { Height = 12 });
        var rows = new StackPanel();
        page.Children.Add(CardOf(rows));
        return rows;
    }

    private static Border CardOf(StackPanel rows) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x08, 255, 255, 255)), CornerRadius = new CornerRadius(14),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x0E, 255, 255, 255)), BorderThickness = new Thickness(1), Child = rows
    };

    private static void AddToCard(StackPanel card, Grid row)
    {
        if (card.Children.Count > 0)
            card.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x0B, 255, 255, 255)), Margin = new Thickness(16, 0, 16, 0) });
        card.Children.Add(row);
    }

    /// <summary>A row: title, description (returned, so it can change) and the control on the right.</summary>
    private TextBlock Row(StackPanel section, string title, string desc, UIElement control, bool sub = false)
    {
        var g = new Grid { Margin = new Thickness(sub ? 34 : 16, 11, 16, 11), MinHeight = 32 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var d = new TextBlock { Text = desc, FontSize = 11.5, Foreground = Dim(0x80), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), LineHeight = 16 };
        if (string.IsNullOrEmpty(desc)) d.Visibility = Visibility.Collapsed;
        texts.Children.Add(d);
        g.Children.Add(texts);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        AddToCard(section, g);
        _collect?.Add((g, title + " " + desc));
        return d;
    }

    private static MainWindow? Island => Application.Current.MainWindow as MainWindow;

    // ------------------------------------------------------------------ Geral

    private StackPanel GeneralPage()
    {
        var p = Page("general");
        var main = Section(p);
        Row(main, Loc.T("gen.language"), Loc.T("s.language.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English") }, S.Language, v => { S.Language = v; Loc.Language = v; }));
        Row(main, Loc.T("s.startup"), Loc.T("s.startup.d"), new Switch(StartupManager.IsEnabled, StartupManager.SetEnabled, _accent));
        Row(main, Loc.T("s.game"), Loc.T("s.game.d"), Toggle(S.GameMode, v => S.GameMode = v));

        var bat = Section(p, Loc.T("s.sec.battery"));
        Row(bat, Loc.T("s.bat"), Loc.T("s.bat.d"), Toggle(S.BatteryAlerts, v => S.BatteryAlerts = v));
        Row(bat, Loc.T("s.batlow"), Loc.T("s.batlow.d"),
            Segmented(new[] { (10, "10%"), (15, "15%"), (20, "20%"), (30, "30%") }, S.BatteryLowPercent, v => S.BatteryLowPercent = v));
        return p;
    }

    // ------------------------------------------------------------------ Aparência

    private StackPanel LookPage()
    {
        var p = Page("look");
        var island = Section(p, Loc.T("s.sec.island"));
        Row(island, Loc.T("s.size"), Loc.T("s.size.d"),
            Segmented(new[] { (IslandSize.Compact, Loc.T("isl.size.compact")), (IslandSize.Normal, Loc.T("isl.size.normal")), (IslandSize.Large, Loc.T("isl.size.large")) },
                S.Size, v => S.Size = v));
        Row(island, Loc.T("s.style"), Loc.T("s.style.d"),
            Segmented(new[] { (IslandStyle.Attached, Loc.T("isl.style.attached")), (IslandStyle.Floating, Loc.T("isl.style.floating")) }, S.Style, v => S.Style = v));
        Row(island, Loc.T("s.glass"), Loc.T("s.glass.d"), new Switch(S.BlurEnabled, v =>
        {
            S.BlurEnabled = v;
            AppSettings.Commit();
            Dispatcher.InvokeAsync(() => Rebuild());
        }, _accent));
        if (S.BlurEnabled)
        {
            Row(island, Loc.T("s.blur"), Loc.T("s.blur.d"), Slider(S.BlurIntensity, 0, 100, v => S.BlurIntensity = v, "%"), sub: true);
            Row(island, Loc.T("s.tint"), Loc.T("s.tint.d"), Slider(S.BlurTint, 0, 100, v => S.BlurTint = v, "%"), sub: true);
        }
        else Row(island, Loc.T("s.see"), Loc.T("s.see.d"), Slider(S.SeeThrough, 0, 85, v => S.SeeThrough = v, "%"));
        Row(island, Loc.T("s.shadow"), Loc.T("s.shadow.d"), Toggle(S.IslandShadow, v => S.IslandShadow = v));

        var colors = Section(p, Loc.T("s.sec.colors"));
        Row(colors, Loc.T("s.glow"), Loc.T("s.glow.d"),
            Segmented(new[] { (0, Loc.T("off")), (1, Loc.T("isl.glow.subtle")), (2, Loc.T("isl.glow.medium")), (3, Loc.T("isl.glow.strong")) }, S.GlowLevel, v => S.GlowLevel = v));
        Row(colors, Loc.T("s.gradient"), Loc.T("s.gradient.d"), Slider(S.GradientStrength, 0, 100, v => S.GradientStrength = v, "%"));

        var eyes = Section(p, Loc.T("s.sec.eyes"));
        Row(eyes, Loc.T("s.eyecolor"), Loc.T("s.eyecolor.d"), EyeSwatches());
        Row(eyes, Loc.T("s.eyealbum"), Loc.T("s.eyealbum.d"), Toggle(S.EyesUseAlbumColor, v => S.EyesUseAlbumColor = v));
        Row(eyes, Loc.T("s.lively"), Loc.T("s.lively.d"), Toggle(S.LivelyEyes, v => S.LivelyEyes = v));
        var intro = new StackPanel { Orientation = Orientation.Horizontal };
        var watch = ActionButton(Loc.T("isl.intro.b"), () => Island?.PlayIntro());
        watch.Margin = new Thickness(0, 0, 10, 0);
        intro.Children.Add(watch);
        intro.Children.Add(Toggle(S.IntroAnimation, v => S.IntroAnimation = v));
        Row(eyes, Loc.T("s.intro"), Loc.T("s.intro.d"), intro);
        return p;
    }

    /// <summary>Redraws the page as it is, at the same scroll position (after an option shows or hides others).</summary>
    private void Rebuild()
    {
        double offset = _scroll.VerticalOffset;
        if (Searching) ShowResults(_search!.Text.Trim());
        else ShowPage(_page, animate: false);
        _scroll.ScrollToVerticalOffset(offset);
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
            Dispatcher.InvokeAsync(Rebuild);
        }

        foreach (var hex in EyePresets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool selected = manual && string.Equals(S.EyeColor, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Ellipse
            {
                Width = 20, Height = 20, Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(selected ? Colors.White : Color.FromArgb(0x30, 255, 255, 255)), StrokeThickness = selected ? 2.5 : 1,
                Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand, ToolTip = hex
            };
            if (selected) dot.Effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9 };
            dot.MouseLeftButtonUp += (_, _) => { SoundService.Play(Sfx.Toggle); Cheer?.Invoke(); Pick(hex); };
            panel.Children.Add(dot);
        }

        bool custom = manual && Array.FindIndex(EyePresets, h => string.Equals(h, S.EyeColor, StringComparison.OrdinalIgnoreCase)) < 0;
        var customButton = ActionButton("+", () =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(S.EyeColor);
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch
            {
                // keep the default
            }
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                Pick($"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}");
        });
        customButton.ToolTip = Loc.T("eye.custom");
        customButton.Padding = new Thickness(9, 3, 9, 3);
        if (custom)
        {
            var c = (Color)ColorConverter.ConvertFromString(S.EyeColor);
            customButton.Background = new SolidColorBrush(Color.FromArgb(0x50, c.R, c.G, c.B));
        }
        panel.Children.Add(customButton);
        return panel;
    }

    // ------------------------------------------------------------------ Comportamento

    private StackPanel BehavePage()
    {
        var p = Page("behave");
        var main = Section(p);
        Row(main, Loc.T("s.open"), Loc.T("s.open.d"),
            Segmented(new[] { (OpenMode.Hover, Loc.T("isl.open.hover")), (OpenMode.Click, Loc.T("isl.open.click")) }, S.Open, v => S.Open = v));
        Row(main, Loc.T("s.speed"), Loc.T("s.speed.d"),
            Segmented(new[] { (AnimSpeed.Fast, Loc.T("gen.speed.fast")), (AnimSpeed.Normal, Loc.T("gen.speed.normal")), (AnimSpeed.Smooth, Loc.T("gen.speed.smooth")) }, S.Speed, v => S.Speed = v));
        Row(main, Loc.T("s.scroll"), Loc.T("s.scroll.d"), Toggle(S.ScrollVolume, v => S.ScrollVolume = v));
        Row(main, Loc.T("s.volume"), Loc.T("s.volume.d"), Toggle(S.VolumeOsd, v => S.VolumeOsd = v));
        Row(main, Loc.T("s.clock"), Loc.T("s.clock.d"),
            Segmented(new[] { (ClockMode.Auto, Loc.T("isl.clock.auto")), (ClockMode.H12, "12h"), (ClockMode.H24, "24h") }, S.Clock, v => S.Clock = v));
        return p;
    }

    // ------------------------------------------------------------------ Música (+ Spotify)

    private StackPanel MusicPage()
    {
        var p = Page("music");
        var main = Section(p);
        Row(main, Loc.T("s.lyrics"), Loc.T("s.lyrics.d"), Toggle(S.LyricsEnabled, v => S.LyricsEnabled = v));
        Row(main, Loc.T("s.musiccolor"), Loc.T("s.musiccolor.d"), Toggle(S.MusicColor, v => S.MusicColor = v));

        var sp = Section(p, Loc.T("s.sec.spotify"));
        var spotify = Island?.Spotify;
        bool connected = spotify?.IsConnected == true;
        var account = new StackPanel { Orientation = Orientation.Horizontal };
        account.Children.Add(StatusDot(connected ? Color.FromRgb(0x1D, 0xB9, 0x54) : Color.FromRgb(0x66, 0x66, 0x6E), Loc.T(connected ? "sp.status.on" : "sp.status.off")));
        TextBlock? accountDesc = null;
        if (!connected)
        {
            account.Children.Add(ActionButton(Loc.T("sp.connect"), async () =>
            {
                if (spotify == null) return;
                if (accountDesc != null) accountDesc.Text = Loc.T("sp.waiting");
                var (ok, msg) = await spotify.ConnectAsync();
                if (accountDesc != null) accountDesc.Text = msg;
                if (ok) Rebuild();
            }, primary: true));
        }
        else account.Children.Add(ActionButton(Loc.T("sp.disconnect"), () => { spotify?.Disconnect(); Rebuild(); }));
        accountDesc = Row(sp, Loc.T("s.sp.account"), connected ? "…" : Loc.T("sp.status.off.d"), account);
        if (connected && spotify != null && _collect == null) // (not while only searching)
        {
            var d = accountDesc;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var name = await spotify.UserNameAsync();
                d.Text = name != null ? Loc.F("sp.status.as", name) : Loc.T("sp.status.check");
                d.Visibility = Visibility.Visible;
            });
        }

        var idBox = new TextBox
        {
            Width = 250, Text = S.SpotifyClientId, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12
        };
        idBox.TextChanged += (_, _) =>
        {
            S.SpotifyClientId = idBox.Text.Trim();
            S.Save();
        };
        Row(sp, Loc.T("sp.clientid"), Loc.T("s.sp.clientid.d"), idBox);
        Row(sp, Loc.T("s.sp.dev"), Loc.T("s.sp.dev.d"), ActionButton(Loc.T("sp.dashboard.b"), () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SpotifyService.DashboardUrl) { UseShellExecute = true }); } catch { }
        }));
        var uri = new StackPanel { Orientation = Orientation.Horizontal };
        uri.Children.Add(new TextBlock { Text = SpotifyService.RedirectUri, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5, Foreground = Dim(0xCC), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        uri.Children.Add(ActionButton(Loc.T("chat.copy"), () => { try { Clipboard.SetText(SpotifyService.RedirectUri); } catch { } }));
        Row(sp, Loc.T("sp.redirect"), Loc.T("sp.redirect.d"), uri);

        // the steps open right under their row
        var steps = new TextBlock { Text = Loc.T("sp.steps"), FontSize = 12, Foreground = Dim(0xAA), TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(16, 0, 16, 14), Visibility = Visibility.Collapsed };
        Button? toggle = null;
        toggle = ActionButton(Loc.T("s.sp.show"), () =>
        {
            bool open = steps.Visibility != Visibility.Visible;
            steps.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (toggle?.Content is TextBlock t) t.Text = Loc.T(open ? "s.sp.hide" : "s.sp.show");
        });
        Row(sp, Loc.T("s.sp.howto"), Loc.T("s.sp.howto.d"), toggle);
        sp.Children.Add(steps);
        return p;
    }

    private static UIElement StatusDot(Color color, string text)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        s.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Effect = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 8, Opacity = 0.8 } });
        s.Children.Add(new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return s;
    }

    // ------------------------------------------------------------------ Claude

    private StackPanel ClaudePage()
    {
        var p = Page("claude");

        var conn = Section(p, Loc.T("s.sec.connection"));
        bool installed = ClaudeHooks.IsInstalled;
        bool moved = installed && ClaudeHooks.PointsElsewhere;
        var control = new StackPanel { Orientation = Orientation.Horizontal };
        control.Children.Add(StatusDot(installed && !moved ? Color.FromRgb(0x34, 0xC7, 0x59) : moved ? Color.FromRgb(0xF5, 0xA5, 0x24) : Color.FromRgb(0x66, 0x66, 0x6E),
            Loc.T(moved ? "cl.status.moved" : installed ? "cl.status.on" : "cl.status.off")));
        TextBlock? connDesc = null;
        void Do(Func<string> op)
        {
            try
            {
                op();
                Rebuild();
            }
            catch (Exception ex)
            {
                if (connDesc != null) connDesc.Text = $"{Loc.T("cl.error")}: {ex.Message}";
                App.Log(ex);
            }
        }
        if (!installed || moved)
            control.Children.Add(ActionButton(Loc.T(moved ? "cl.update" : "cl.connect"), () => Do(ClaudeHooks.Install), primary: true));
        if (installed)
            control.Children.Add(ActionButton(Loc.T("cl.disconnect"), () => Do(ClaudeHooks.Uninstall)));
        connDesc = Row(conn, "Claude Code", Loc.T(moved ? "s.cl.moved.d" : installed ? "s.cl.on.d" : "s.cl.off.d"), control);

        var on = Section(p, Loc.T("s.sec.onisland"));
        Row(on, Loc.T("s.cl.enabled"), Loc.T("s.cl.enabled.d"), Toggle(S.ClaudeEnabled, v => S.ClaudeEnabled = v));
        Row(on, Loc.T("s.cl.compact"), Loc.T("s.cl.compact.d"), Toggle(S.ClaudeShowInCompact, v => S.ClaudeShowInCompact = v));
        Row(on, Loc.T("s.cl.pod"), Loc.T("s.cl.pod.d"), Toggle(S.ClaudePod, v => S.ClaudePod = v));
        Row(on, Loc.T("s.cl.answer"), Loc.T("s.cl.answer.d"), Toggle(S.ClaudeAnswerOnIsland, v => S.ClaudeAnswerOnIsland = v));

        var alerts = Section(p, Loc.T("s.sec.alerts"));
        Row(alerts, Loc.T("s.cl.done"), Loc.T("s.cl.done.d"), WithListen(Sfx.Done, Toggle(S.ClaudeSoundDone, v => S.ClaudeSoundDone = v)));
        Row(alerts, Loc.T("s.cl.att"), Loc.T("s.cl.att.d"), WithListen(Sfx.Attention, Toggle(S.ClaudeSoundAttention, v => S.ClaudeSoundAttention = v)));
        Row(alerts, Loc.T("s.cl.notify"), Loc.T("s.cl.notify.d"), Toggle(S.ClaudeWindowsNotifications, v => S.ClaudeWindowsNotifications = v));
        Row(alerts, Loc.T("s.cl.test"), Loc.T("s.cl.test.d"), ActionButton(Loc.T("cl.test.b"), () => Island?.SimulateClaudeTask()));

        var chat = Section(p, Loc.T("s.sec.chat"));
        var chatStatus = new TextBlock { FontSize = 11.5, Foreground = Dim(0x99), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var check = new StackPanel { Orientation = Orientation.Horizontal };
        check.Children.Add(chatStatus);
        async void Check()
        {
            chatStatus.Text = "…";
            var (found, loggedIn, _) = await ClaudeRunner.CheckAsync();
            chatStatus.Text = Loc.T(!found ? "cl.check.missing" : loggedIn ? "cl.check.ok" : "cl.check.login");
            chatStatus.Foreground = new SolidColorBrush(found && loggedIn ? Color.FromRgb(0x5E, 0xD6, 0x7D) : Color.FromRgb(0xF5, 0xA5, 0x24));
        }
        check.Children.Add(ActionButton(Loc.T("cl.check"), Check));
        Row(chat, Loc.T("s.cl.chat"), Loc.T("s.cl.chat.d"), check);
        Row(chat, Loc.T("s.cl.translate"), Loc.T("s.cl.translate.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English"), ("es", "Español") }, S.ChatTranslateTo, v => S.ChatTranslateTo = v));
        // (not while only searching; and after the page is on screen, so opening it never stutters)
        if (_collect == null) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Check));
        return p;
    }

    // ------------------------------------------------------------------ Foco, Arquivos, Sons

    private StackPanel FocusPage()
    {
        var p = Page("focus");
        var main = Section(p);
        Row(main, Loc.T("s.fo.len"), Loc.T("s.fo.len.d"),
            Segmented(new[] { (10, "10"), (15, "15"), (30, "30"), (60, "60") }, S.FocusMinutes, v => S.FocusMinutes = v));
        Row(main, Loc.T("s.fo.break"), Loc.T("s.fo.break.d"),
            Segmented(new[] { (5, "5"), (10, "10"), (15, "15") }, S.BreakMinutes, v => S.BreakMinutes = v));
        Row(main, Loc.T("s.fo.quiet"), Loc.T("s.fo.quiet.d"), Toggle(S.FocusQuiet, v => S.FocusQuiet = v));
        Row(main, Loc.T("s.fo.sound"), Loc.T("s.fo.sound.d"), WithListen(Sfx.FocusEnd, Toggle(S.SoundFocus, v => S.SoundFocus = v)));
        return p;
    }

    private StackPanel FilesPage()
    {
        var p = Page("files");
        var main = Section(p);
        Row(main, Loc.T("s.fi.xfer"), Loc.T("s.fi.xfer.d"), Toggle(S.TransferProgress, v => S.TransferProgress = v));
        Row(main, Loc.T("s.fi.shelf"), Loc.T("s.fi.shelf.d"), Toggle(S.ShelfEnabled, v => S.ShelfEnabled = v));
        return p;
    }

    private StackPanel SoundsPage()
    {
        var p = Page("sounds");
        var main = Section(p);
        Row(main, Loc.T("s.so.enabled"), Loc.T("s.so.enabled.d"), Toggle(S.SoundsEnabled, v => S.SoundsEnabled = v));
        Row(main, Loc.T("s.so.volume"), Loc.T("s.so.volume.d"), Slider(S.SoundVolume, 0, 100, v => S.SoundVolume = v, "%", () => SoundService.Preview(Sfx.Toggle)));
        var when = Section(p, Loc.T("s.sec.when"));
        Row(when, Loc.T("s.so.openclose"), Loc.T("s.so.openclose.d"), WithListen(Sfx.Expand, Toggle(S.SoundOpenClose, v => S.SoundOpenClose = v)));
        Row(when, Loc.T("s.so.clicks"), Loc.T("s.so.clicks.d"), WithListen(Sfx.Click, Toggle(S.SoundClicks, v => S.SoundClicks = v)));
        return p;
    }

    // ------------------------------------------------------------------ Sobre

    private StackPanel AboutPage()
    {
        var p = Page("about");
        var upd = Section(p, Loc.T("s.sec.updates"));
        TextBlock? status = null;
        Button? check = null;
        void Paint(UpdateStatus st)
        {
            if (status != null)
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
            if (status != null) status.Text = Loc.T("upd.checking");
            Paint(await Updater.CheckAndDownloadAsync());
        }, primary: true);
        status = Row(upd, "Notchy " + Updater.CurrentVersion, Loc.T("upd.latest"), check);
        if (!Updater.IsInstalled) Paint(UpdateStatus.NotInstalled);
        else if (Updater.Pending != null) Paint(UpdateStatus.Ready);
        Row(upd, Loc.T("s.ab.auto"), Loc.T("s.ab.auto.d"), Toggle(S.AutoUpdate, v => S.AutoUpdate = v));
        Row(upd, Loc.T("s.ab.news"), Loc.F("s.ab.news.d", WhatsNew.Version), ActionButton(Loc.T("s.ab.news.b"), () => Island?.ShowWhatsNew()));

        var app = Section(p);
        Row(app, Loc.T("s.ab.reset"), Loc.T("s.ab.reset.d"), ActionButton(Loc.T("gen.reset.b"), () =>
        {
            AppSettings.ResetAll();
            Loc.Language = S.Language;
            _lang = S.Language;
            BuildSidebar();
            ShowPage(_page, animate: false);
        }));
        Row(app, Loc.T("s.ab.quit"), Loc.T("s.ab.quit.d"), ActionButton(Loc.T("gen.quit.b"), () => Application.Current.Shutdown(), danger: true));
        return p;
    }

    // =====================================================================================
    // controls (in the page's colour)
    // =====================================================================================

    private static SolidColorBrush Dim(byte alpha) => new(Color.FromArgb(alpha, 255, 255, 255));

    private Switch Toggle(bool value, Action<bool> set) => new(value, v =>
    {
        set(v);
        AppSettings.Commit();
    }, _accent);

    /// <summary>A switch with a small ▶ before it, to hear the sound it's about.</summary>
    private UIElement WithListen(Sfx sfx, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand,
            Background = Dim(0x10), ToolTip = Loc.T("snd.play"),
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 9, Foreground = new SolidColorBrush(_accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 0, 0) }
        };
        play.MouseEnter += (_, _) => play.Background = Dim(0x22);
        play.MouseLeave += (_, _) => play.Background = Dim(0x10);
        play.MouseLeftButtonUp += (_, _) => SoundService.Preview(sfx);
        panel.Children.Add(play);
        panel.Children.Add(control);
        return panel;
    }

    private UIElement Segmented<T>(IEnumerable<(T value, string label)> options, T current, Action<T> set)
    {
        var accent = _accent;
        var host = new Border { Background = Dim(0x0E), CornerRadius = new CornerRadius(9), Padding = new Thickness(2) };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        host.Child = panel;
        var items = new List<(T value, Border b, TextBlock t)>();

        void Paint(T selected)
        {
            foreach (var (v, b, t) in items)
            {
                bool sel = EqualityComparer<T>.Default.Equals(v, selected);
                b.Background = sel ? new SolidColorBrush(accent) : Brushes.Transparent;
                t.Foreground = sel ? new SolidColorBrush(Ink) : Dim(0xA8);
                t.FontWeight = sel ? FontWeights.Bold : FontWeights.SemiBold;
            }
        }

        foreach (var (value, label) in options)
        {
            var t = new TextBlock { Text = label, FontSize = 11.5 };
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand, Child = t, Background = Brushes.Transparent };
            var v = value;
            b.MouseLeftButtonUp += (_, _) =>
            {
                SoundService.Play(Sfx.Toggle);
                set(v);
                Paint(v);
                AppSettings.Commit();
                Cheer?.Invoke();
            };
            items.Add((value, b, t));
            panel.Children.Add(b);
        }
        Paint(current);
        return host;
    }

    private UIElement Slider(int value, int min, int max, Action<int> set, string suffix, Action? onCommit = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var bar = new BarSlider { Width = 150, Height = 20, Value = (value - min) / (double)(max - min), FillBrush = new SolidColorBrush(_accent) };
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

    private Button ActionButton(string text, Action action, bool primary = false, bool danger = false)
    {
        var bg = primary ? _accent : danger ? Color.FromArgb(0x26, 0xFF, 0x5A, 0x5A) : Color.FromArgb(0x16, 255, 255, 255);
        var fg = primary ? Ink : danger ? Color.FromRgb(0xFF, 0x8A, 0x8A) : Colors.White;
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("PillButton"),
            Background = new SolidColorBrush(bg),
            Foreground = new SolidColorBrush(fg),
            Padding = new Thickness(13, 6, 13, 6),
            Margin = new Thickness(6, 0, 0, 0),
            Content = new TextBlock { Text = text, FontFamily = (FontFamily)Application.Current.FindResource("UiFont"), FontSize = 12, FontWeight = FontWeights.SemiBold }
        };
        b.Click += (_, _) => action();
        return b;
    }

    internal static void RaiseCheer() => Cheer?.Invoke();
}

/// <summary>Animated on/off switch, in the colour of its page.</summary>
public sealed class Switch : Border
{
    private readonly Ellipse _knob = new() { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _move = new();
    private readonly Action<bool> _changed;
    private readonly Color _onColor;
    private bool _on;

    private static readonly Color OffColor = Color.FromArgb(0x24, 255, 255, 255);
    private static readonly Color KnobOn = Color.FromRgb(0x14, 0x0E, 0x1E);

    public Switch(bool on, Action<bool> changed, Color? color = null)
    {
        _on = on;
        _changed = changed;
        _onColor = color ?? Color.FromRgb(0xA7, 0x8B, 0xFA);
        Width = 40;
        Height = 22;
        CornerRadius = new CornerRadius(11);
        Padding = new Thickness(3);
        Cursor = Cursors.Hand;
        _knob.RenderTransform = _move;
        Child = _knob;
        Background = new SolidColorBrush(on ? _onColor : OffColor);
        _knob.Fill = new SolidColorBrush(on ? KnobOn : Colors.White);
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
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? _onColor : OffColor, TimeSpan.FromMilliseconds(200)));
        var knob = new SolidColorBrush(((SolidColorBrush)_knob.Fill).Color);
        _knob.Fill = knob;
        knob.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? KnobOn : Colors.White, TimeSpan.FromMilliseconds(200)));
        if (on) SettingsWindow.RaiseCheer();
        _changed(on);
    }
}
