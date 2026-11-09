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
public sealed partial class SettingsWindow : Window
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
        new("notif", "\uEA8F", false, Color.FromRgb(0xFF, 0x7A, 0x7A)),
        new("sounds", "", false, Color.FromRgb(0xFF, 0xD3, 0x6B)),
        new("music", "", false, Color.FromRgb(0xFF, 0x7E, 0xB6)),
        new("calendar", "\uE787", false, Color.FromRgb(0x7C, 0xC8, 0xFF)),
        new("focus", "", false, Color.FromRgb(0xB7, 0x9C, 0xFF)),
        new("files", "", false, Color.FromRgb(0x5E, 0xD6, 0x7D)),
        new("claude", "✻", true, Color.FromRgb(0xE8, 0x82, 0x5F)),
        new("chat", "", false, Color.FromRgb(0xFF, 0xAA, 0x4C)),
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
    private const double SideGlowRoom = 8, EyesFollowSeconds = 0.06, EyesMaxFrameSeconds = 0.05;
    private readonly DropShadowEffect _eyesGlow = new() { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.9 };
    private TimeSpan _eyesLastFrame;
    private FrameworkElement? _eyesPill;
    private DateTime _joyUntil;

    // search: while a page is built for the results, every row is collected here
    private List<(Grid row, string text)>? _collect;

    // the "Sobre" page's update row: repainted whenever Updater changes
    private Action? _paintUpdate;
    private UpdatePhase _lastUpdatePhase = Updater.Phase;

    /// <summary>Everything is drawn this much bigger, for comfortable reading.</summary>
    private const double Scale = 1.2;

    public SettingsWindow(bool setup = false)
    {
        _setup = setup;
        Title = "Notchy";
        var area = SystemParameters.WorkArea;
        Width = Math.Min((setup ? 700 : 920) * Scale, area.Width - 40);
        Height = Math.Min((setup ? 590 : 640) * Scale, area.Height - 40);
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
        sideGrid.Children.Add(setup ? SetupBrand() : Brand());
        if (!setup)
        {
            var search = SearchBox();
            Grid.SetRow(search, 1);
            sideGrid.Children.Add(search);
        }
        // (the scroll area clips at its own edge: this extra room keeps the selected icon's glow whole, and nothing moves on screen)
        _sideItems.Margin = new Thickness(SideGlowRoom, SideGlowRoom, SideGlowRoom, 0);
        var itemsScroll = new ScrollViewer { Content = _sideItems, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FocusVisualStyle = null,
            Margin = new Thickness(-SideGlowRoom, -SideGlowRoom, -SideGlowRoom, 0) };
        Grid.SetRow(itemsScroll, 2);
        sideGrid.Children.Add(itemsScroll);
        var version = new TextBlock { Text = "Notchy " + Updater.CurrentVersion, FontSize = 10.5, Foreground = Dim(0x44), Margin = new Thickness(10, 8, 0, 0) };
        Grid.SetRow(version, 3);
        if (!setup) sideGrid.Children.Add(version);
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
            FocusVisualStyle = null
        };
        // The scroll area clips at its own edge, so the margins go inside it: the glows have room and nothing moves on screen
        Grid.SetRowSpan(_scroll, 2);
        _scroll.Margin = new Thickness(0, 16, 0, 0);
        _pageHost.Margin = new Thickness(34, 24, 34, 30);
        _scroll.Loaded += (_, _) =>
        {
            // ...but the scroll bar stays clear of the window's buttons
            if (_scroll.Template?.FindName("PART_VerticalScrollBar", _scroll) is FrameworkElement bar) bar.Margin = new Thickness(0, 24, 0, 0);
        };
        content.Children.Add(_scroll);
        if (setup)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_setupFoot, 2);
            content.Children.Add(_setupFoot);
            content.Children.Insert(0, BuildSetupAura());
            content.ClipToBounds = true;
            BuildSetupFoot();
        }
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
        Updater.Changed += OnUpdaterChanged;
        CompositionTarget.Rendering += OnEyesFrame;
        if (setup) InitSetup();
        Closed += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            Cheer -= OnCheer;
            Updater.Changed -= OnUpdaterChanged;
            CompositionTarget.Rendering -= OnEyesFrame;
        };
    }

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (Updater.Phase == UpdatePhase.Ready && _lastUpdatePhase == UpdatePhase.Downloading) RaiseCheer();
        _lastUpdatePhase = Updater.Phase;
        _paintUpdate?.Invoke();
    });

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
        // on the part that stays still: an element with an effect moves in whole pixels, so the eyes would go in little jumps
        eyes.Effect = _eyesGlow;
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
        _eyesGlow.Color = _accent;
    }

    private void OnCheer()
    {
        _joyUntil = DateTime.Now.AddMilliseconds(750);
        if (_setup) Island?.SetupCheer();
    }

    /// <summary>Eyes follow the mouse, blink now and then, and smile (^ ^) for a moment after something's turned on.</summary>
    private void OnEyesFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || frame.RenderingTime == _eyesLastFrame) return; // (raised more than once per frame sometimes)
        double seconds = Math.Min((frame.RenderingTime - _eyesLastFrame).TotalSeconds, EyesMaxFrameSeconds);
        _eyesLastFrame = frame.RenderingTime;
        AnimateEyes(seconds);
    }

    private void AnimateEyes(double seconds)
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
        double follow = 1 - Math.Exp(-seconds / EyesFollowSeconds); // the same speed at any frame rate
        _eyesLook.X += (tx - _eyesLook.X) * follow;
        _eyesLook.Y += (ty - _eyesLook.Y) * follow;
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
        if (_setup) panel.Children.RemoveAt(1); // the assistant has a fixed size: no maximize
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
        if (_setup)
        {
            BuildSetupSidebar();
            return;
        }
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
        var tile = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Child = TargetIcon.Show(icon) };
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
        if (_setup)
        {
            ShowSetupStep(animate);
            return;
        }
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
        "notif" => NoticesPage(),
        "calendar" => CalendarPage(),
        "music" => MusicPage(),
        "claude" => ClaudePage(),
        "chat" => ChatPage(),
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
            label.Children.Add(TargetIcon.Show(new TextBlock
            {
                Text = c.Glyph, FontFamily = c.Symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 11, Foreground = new SolidColorBrush(c.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            }));
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
            Child = TargetIcon.Show(new TextBlock
            {
                Text = glyph, FontFamily = symbol ? new FontFamily("Segoe UI Symbol") : (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = symbol ? 19 : 17, Foreground = whiteIcon ? Brushes.White : new SolidColorBrush(Ink),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            })
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
        Row(main, Loc.T("s.setup.redo"), Loc.T("s.setup.redo.d"), ActionButton(Loc.T("s.setup.redo.b"), App.OpenSetup));
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

        var behave = Section(p, Loc.T("s.sec.behave"));
        Row(behave, Loc.T("s.open"), Loc.T("s.open.d"),
            Segmented(new[] { (OpenMode.Hover, Loc.T("isl.open.hover")), (OpenMode.Click, Loc.T("isl.open.click")) }, S.Open, v => S.Open = v));
        Row(behave, Loc.T("s.speed"), Loc.T("s.speed.d"),
            Segmented(new[] { (AnimSpeed.Fast, Loc.T("gen.speed.fast")), (AnimSpeed.Normal, Loc.T("gen.speed.normal")), (AnimSpeed.Smooth, Loc.T("gen.speed.smooth")) }, S.Speed, v => S.Speed = v));
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
            if (_setup) PaintSwatches();
            else Dispatcher.InvokeAsync(Rebuild);
        }

        _swatches.Clear();
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
            _swatches.Add((dot, hex));
        }
        if (_setup) PrepareSwatches();

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

    // ------------------------------------------------------------------ Notificações, Calendário e clima

    private StackPanel NoticesPage()
    {
        var p = Page("notif");
        NotifyRows(p);
        return p;
    }

    private StackPanel CalendarPage()
    {
        var p = Page("calendar");
        var calendar = Section(p, Loc.T("s.sec.calendar"));
        Row(calendar, Loc.T("s.calholidays"), Loc.T("s.calholidays.d"), Toggle(S.CalendarHolidays, v => S.CalendarHolidays = v));
        Row(calendar, Loc.T("s.clock"), Loc.T("s.clock.d"),
            Segmented(new[] { (ClockMode.Auto, Loc.T("isl.clock.auto")), (ClockMode.H12, "12h"), (ClockMode.H24, "24h") }, S.Clock, v => S.Clock = v));
        WeatherRows(Section(p, Loc.T("s.sec.weather")));
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

    /// <summary>
    /// Weather location: Automática (the PC's location) or Manual, with a box to type the city and its matches
    /// right under it (there are several "Alagoinha": the state tells them apart).
    /// </summary>
    private void WeatherRows(StackPanel section)
    {
        Row(section, Loc.T("s.weather"), Loc.T("s.weather.d"),
            Segmented(new[] { (true, Loc.T("weather.auto")), (false, Loc.T("weather.manual")) }, S.WeatherAuto, v =>
            {
                S.WeatherAuto = v;
                _focusCityBox = !v;
                Dispatcher.InvokeAsync(() => Rebuild());
            }));
        if (S.WeatherAuto) return; // the box is only for picking a city by hand

        var cityBox = new TextBox
        {
            Width = 250, Text = S.WeatherCity, Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)), BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), FontSize = 12
        };
        var results = new StackPanel { Margin = new Thickness(16, 0, 16, 10) };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            string text = cityBox.Text;
            results.Children.Clear();
            if (text.Trim().Length < 2 || text == S.WeatherCity) return;
            results.Children.Add(new TextBlock { Text = Loc.T("weather.searching"), FontSize = 12, Foreground = Dim(0x88), Margin = new Thickness(4, 2, 0, 4) });
            RevealWhenLaidOut(results);
            var found = await WeatherService.SearchAsync(text);
            if (cityBox.Text != text) return;
            results.Children.Clear();
            if (found.Count == 0) results.Children.Add(new TextBlock { Text = Loc.T("weather.noresult"), FontSize = 12, Foreground = Dim(0x88), Margin = new Thickness(4, 2, 0, 4) });
            foreach (var c in found)
            {
                var city = c;
                var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                var t = new TextBlock { FontSize = 12.5, Foreground = Brushes.White };
                t.Inlines.Add(new System.Windows.Documents.Run(c.Name) { FontWeight = FontWeights.SemiBold });
                t.Inlines.Add(new System.Windows.Documents.Run("   " + c.Region) { Foreground = Dim(0x88) });
                row.Child = t;
                row.MouseEnter += (_, _) => row.Background = Dim(0x10);
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    SoundService.Play(Sfx.Toggle);
                    S.WeatherAuto = false;
                    S.WeatherCity = city.Name;
                    S.WeatherLat = city.Lat;
                    S.WeatherLon = city.Lon;
                    AppSettings.Commit();
                    Rebuild(); // the switch shows Manual, the box the city
                };
                results.Children.Add(row);
            }
            RevealWhenLaidOut(results); // the list opens below the box, often past the bottom of the window
        };
        cityBox.TextChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        Row(section, Loc.T("s.weather.city"), Loc.T("s.weather.city.d"), cityBox);
        section.Children.Add(results);
        if (_focusCityBox)
        {
            _focusCityBox = false;
            if (_setup && cityBox.Parent is FrameworkElement cityRow) Appear(cityRow);
            Dispatcher.BeginInvoke(() =>
            {
                cityBox.Focus();
                cityBox.BringIntoView();
            }, DispatcherPriority.Loaded);
        }
    }

    private bool _focusCityBox;

    private void RevealWhenLaidOut(FrameworkElement element) =>
        Dispatcher.BeginInvoke(() => element.BringIntoView(), DispatcherPriority.Loaded);

    /// <summary>Notifications on the island, and an on/off per app that has sent one.</summary>
    private void NotifyRows(StackPanel page)
    {
        var section = Section(page);
        Row(section, Loc.T("s.notif"), Loc.T("s.notif.d"), new Switch(S.NotifyEnabled, v =>
        {
            S.NotifyEnabled = v;
            AppSettings.Commit();
            Dispatcher.InvokeAsync(() => Rebuild());
        }, _accent));
        if (S.NotifyEnabled)
        {
            Row(section, Loc.T("s.notif.only"), Loc.T("s.notif.only.d"), Toggle(S.NotifyOnlyNotchy, v => S.NotifyOnlyNotchy = v), sub: true);
            Row(section, Loc.T("s.notif.full"), Loc.T("s.notif.full.d"), Toggle(S.NotifyFullscreen, v => S.NotifyFullscreen = v), sub: true);
            Row(section, Loc.T("s.so.notice"), Loc.T("s.so.notice.d"), WithListen(Sfx.Notice, Toggle(S.SoundNotice, v => S.SoundNotice = v)), sub: true);
        }
#if DEBUG
        Row(section, "Enviar notificação de teste", "Só na versão de testes", ActionButton("Enviar", () => Island?.SendNoticeTest(), primary: true));
#endif
        if (S.NotifyApps.Count == 0) return;
        var apps = Section(page, Loc.T("s.sec.apps"));
        foreach (var app in S.NotifyApps.OrderBy(a => a))
        {
            var name = app;
            Row(apps, name, Loc.T("s.notif.app.d"), Toggle(!S.NotifyMuted.Contains(name), on =>
            {
                S.NotifyMuted.Remove(name);
                if (!on) S.NotifyMuted.Add(name);
            }));
        }
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

        return p;
    }

    private StackPanel ChatPage()
    {
        var p = Page("chat");
        var chat = Section(p);
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
        ChatEngineRows(chat);
        if (!S.ChatUseGemini) Row(chat, Loc.T("s.cl.chat"), Loc.T("s.cl.chat.d"), check);
        Row(chat, Loc.T("s.cl.translate"), Loc.T("s.cl.translate.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English"), ("es", "Español") }, S.ChatTranslateTo, v => S.ChatTranslateTo = v));
        // (not while only searching; and after the page is on screen, so opening it never stutters)
        if (_collect == null && !S.ChatUseGemini) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Check));
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
        Row(main, Loc.T("s.so.alarm"), Loc.T("s.so.alarm.d"), Slider(S.AlarmVolume, 10, 100, v => S.AlarmVolume = v, "%", () => SoundService.Preview(Sfx.Alarm)));
        var when = Section(p, Loc.T("s.sec.when"));
        Row(when, Loc.T("s.so.openclose"), Loc.T("s.so.openclose.d"), WithListen(Sfx.Expand, Toggle(S.SoundOpenClose, v => S.SoundOpenClose = v)));
        Row(when, Loc.T("s.so.clicks"), Loc.T("s.so.clicks.d"), WithListen(Sfx.Click, Toggle(S.SoundClicks, v => S.SoundClicks = v)));

        var volume = Section(p, Loc.T("s.sec.volume"));
        Row(volume, Loc.T("s.scroll"), Loc.T("s.scroll.d"), Toggle(S.ScrollVolume, v => S.ScrollVolume = v));
        Row(volume, Loc.T("s.volume"), Loc.T("s.volume.d"), Toggle(S.VolumeOsd, v => S.VolumeOsd = v));
        Row(volume, Loc.T("s.hideosd"), Loc.T("s.hideosd.d"), Toggle(S.HideWindowsOsd, v =>
        {
            S.HideWindowsOsd = v;
            if (!v) OsdHider.Restore();
        }));

        var devices = Section(p, Loc.T("s.sec.devices"));
        Row(devices, Loc.T("s.buds"), Loc.T("s.buds.d"), Toggle(S.BudsAlerts, v => S.BudsAlerts = v));
        Row(devices, Loc.T("s.bat"), Loc.T("s.bat.d"), Toggle(S.BatteryAlerts, v => S.BatteryAlerts = v));
        Row(devices, Loc.T("s.batlow"), Loc.T("s.batlow.d"),
            Segmented(new[] { (10, "10%"), (15, "15%"), (20, "20%"), (30, "30%") }, S.BatteryLowPercent, v => S.BatteryLowPercent = v));
        return p;
    }

    // ------------------------------------------------------------------ Sobre

    private StackPanel AboutPage()
    {
        var p = Page("about");
        var upd = Section(p, Loc.T("s.sec.updates"));
        UpdateRow(upd);
        Row(upd, Loc.T("s.ab.auto"), Loc.T("s.ab.auto.d"), Toggle(S.AutoUpdate, v => S.AutoUpdate = v));
        Row(upd, Loc.T("s.ab.news"), Loc.F("s.ab.news.d", WhatsNew.Version), ActionButton(Loc.T("s.ab.news.b"), () => Island?.ShowWhatsNew()));
#if DEBUG
        var simulate = new StackPanel { Orientation = Orientation.Horizontal };
        simulate.Children.Add(ActionButton("Tem atualização", () => _ = Updater.SimulateAsync("found")));
        simulate.Children.Add(ActionButton("Já atualizado", () => _ = Updater.SimulateAsync("latest")));
        simulate.Children.Add(ActionButton("GitHub instável", () => _ = Updater.SimulateAsync("flaky")));
        simulate.Children.Add(ActionButton("Cartão de instalação", () => _ = InstallCard.DemoAsync()));
        Row(upd, "Simular atualização", "Só na versão de testes", simulate);
#endif

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

#if DEBUG
    /// <summary>Acts out every update state on the "Sobre" page, saves a picture of each and quits.</summary>
    public async System.Threading.Tasks.Task UpdateTestAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        void Save(string name)
        {
            var root = (FrameworkElement)Content;
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            picture.Render(root);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using var file = System.IO.File.Create(System.IO.Path.Combine(dir, name + ".png"));
            png.Save(file);
        }
        await System.Threading.Tasks.Task.Delay(900);
        var run = Updater.SimulateAsync("found");
        await System.Threading.Tasks.Task.Delay(700);
        Save("1-procurando");
        await System.Threading.Tasks.Task.Delay(1500);
        Save("2-encontrou");
        await System.Threading.Tasks.Task.Delay(2500);
        Save("3-baixando");
        ShowPage("general", animate: false);
        ShowPage("about", animate: false);
        await System.Threading.Tasks.Task.Delay(200);
        Save("4-voltou-a-pagina");
        await run;
        await System.Threading.Tasks.Task.Delay(500);
        Save("5-pronto");
        run = Updater.SimulateAsync("flaky");
        await System.Threading.Tasks.Task.Delay(2400);
        Save("6-tentando");
        await run;
        await System.Threading.Tasks.Task.Delay(500);
        Save("7-erro");
        run = Updater.SimulateAsync("found");
        await System.Threading.Tasks.Task.Delay(2400);
        Save("8-reabriu");
        Application.Current.Shutdown();
    }
#endif

    /// <summary>The version row and, below it, the panel that opens while a new version is found and downloaded.</summary>
    private void UpdateRow(StackPanel section)
    {
        Color accent = _accent, blue = Color.FromRgb(0x5A, 0xC8, 0xFA), green = Color.FromRgb(0x5E, 0xD6, 0x7D), amber = Color.FromRgb(0xF5, 0xA5, 0x24);
        Updater.ForgetError();

        var check = ActionButton(Loc.T("upd.check.b"), () =>
        {
            if (Updater.Phase == UpdatePhase.Ready) Updater.ApplyAndRestart();
            else _ = Updater.CheckAndDownloadAsync();
        }, primary: true);
        var status = Row(section, "Notchy " + Updater.CurrentVersion, Loc.T("upd.latest"), check);

        // ---- the panel
        var tileBrush = new SolidColorBrush(blue);
        var tileGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0.55, Color = blue };
        var icon = new TextBlock { FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 14, Foreground = new SolidColorBrush(Ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var tile = new Grid { Width = 30, Height = 30 };
        tile.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = tileBrush, Effect = tileGlow });
        tile.Children.Add(icon);

        var title = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold };
        var sub = new TextBlock { FontSize = 11.5, Foreground = Dim(0x99), Margin = new Thickness(0, 1, 0, 0) };
        var texts = new StackPanel { Margin = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(sub);
        Grid.SetColumn(texts, 1);

        var percent = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Documents.Typography.SetNumeralAlignment(percent, FontNumeralAlignment.Tabular);
        Grid.SetColumn(percent, 2);

        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(tile);
        line.Children.Add(texts);
        line.Children.Add(percent);

        var doneColumn = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var restColumn = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
        var fillBrush = new SolidColorBrush(blue);
        var fillGlow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9, Color = blue };
        var bar = new Grid { Height = 6, Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(doneColumn);
        bar.ColumnDefinitions.Add(restColumn);
        var track = new Border { CornerRadius = new CornerRadius(3), Background = Dim(0x14) };
        Grid.SetColumnSpan(track, 2);
        bar.Children.Add(track);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = fillBrush, Effect = fillGlow });

        var content = new StackPanel();
        content.Children.Add(line);
        content.Children.Add(bar);
        var inner = new Border { Background = new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), CornerRadius = new CornerRadius(11), Padding = new Thickness(14, 12, 14, 12), Child = content };
        var panel = new Border { Child = inner, Padding = new Thickness(0, 0, 0, 12), Margin = new Thickness(12, 0, 12, 0), ClipToBounds = true, MaxHeight = 0, Opacity = 0 };
        section.Children.Add(panel);

        bool open = false;
        void Slide(bool show, bool animate)
        {
            open = show;
            if (!animate)
            {
                panel.MaxHeight = show ? double.PositiveInfinity : 0;
                panel.Opacity = show ? 1 : 0;
                return;
            }
            inner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double target = show ? inner.DesiredSize.Height + panel.Padding.Bottom : 0;
            var time = TimeSpan.FromMilliseconds(350);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            panel.BeginAnimation(MaxHeightProperty, new DoubleAnimation(panel.ActualHeight, target, time) { EasingFunction = ease });
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, time) { EasingFunction = ease });
        }

        void PaintButton(string text, Color background, Color foreground, bool enabled, Effect? effect = null)
        {
            if (check.Content is TextBlock t) t.Text = text;
            check.Background = new SolidColorBrush(background);
            check.Foreground = new SolidColorBrush(foreground);
            check.IsEnabled = enabled;
            check.Effect = effect;
        }

        void Paint(bool animate)
        {
            var phase = Updater.Phase;
            string version = Updater.TargetVersion ?? "", size = Updater.Megabytes(Updater.TotalBytes);
            bool ready = phase == UpdatePhase.Ready, warn = phase is UpdatePhase.Retrying or UpdatePhase.Error;
            Color busyBack = Color.FromArgb(0x0D, 255, 255, 255), busyInk = Color.FromArgb(0x88, 255, 255, 255);

            status.Foreground = warn ? new SolidColorBrush(amber) : Dim(0x80);
            status.Text = phase switch
            {
                UpdatePhase.Checking => Loc.T("upd.checking"),
                UpdatePhase.Retrying => Loc.F("upd.retry", Updater.Retry, Updater.MaxRetries),
                UpdatePhase.Found => Loc.F("upd.found.new", version, size),
                UpdatePhase.Downloading => Loc.T("upd.downloading"),
                UpdatePhase.Ready => Loc.F("upd.found", version, size),
                UpdatePhase.Error => Loc.T(Updater.DownloadFailed ? "upd.error.down" : "upd.error"),
                _ => Loc.T(Updater.IsInstalled ? "upd.latest" : "upd.dev")
            };

            switch (phase)
            {
                case UpdatePhase.Checking or UpdatePhase.Retrying:
                    PaintButton(Loc.T("upd.checking"), busyBack, busyInk, false);
                    break;
                case UpdatePhase.Found or UpdatePhase.Downloading:
                    PaintButton(Loc.T("upd.downloading.b"), busyBack, busyInk, false);
                    break;
                case UpdatePhase.Ready:
                    PaintButton(Loc.T("upd.install.b"), green, Color.FromRgb(0x07, 0x21, 0x0F), true,
                        new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.5, Color = green });
                    break;
                default:
                    PaintButton(Loc.T("upd.check.b"), accent, Ink, Updater.IsInstalled);
                    break;
            }

            bool show = phase is UpdatePhase.Found or UpdatePhase.Downloading or UpdatePhase.Ready;
            if (show)
            {
                int done = ready ? 100 : Updater.Progress;
                var color = ready ? green : blue;
                icon.Text = ((char)(ready ? 0xE73E : 0xE896)).ToString();
                tileBrush.Color = color;
                tileGlow.Color = color;
                fillBrush.Color = color;
                fillGlow.Color = color;
                title.Text = Loc.F("upd.panel.version", version);
                sub.Text = ready ? Loc.F("upd.panel.ready", size)
                    : phase == UpdatePhase.Found ? Loc.F("upd.panel.found", size)
                    : Loc.F("upd.panel.progress", Updater.Megabytes(Updater.TotalBytes * done / 100), size);
                percent.Text = done + "%";
                doneColumn.Width = new GridLength(done, GridUnitType.Star);
                restColumn.Width = new GridLength(100 - done, GridUnitType.Star);
            }
            if (show != open) Slide(show, animate);
        }

        Paint(false);
        _paintUpdate = () => Paint(true);
    }

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
        if (_setup) return SlidingSegmented(options, current, set);
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
