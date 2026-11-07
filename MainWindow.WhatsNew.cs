using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// After an update with notes (WhatsNew.cs), the small island says "✦ What's new in Notchy 2.0" with happy
/// golden eyes; opening it shows the Novidades tab: highlight cards (a little animation each, passing by
/// themselves or with the arrows) and "See all" with the full notes. Shown once; later from Settings → About.
/// New installs don't get it (they just met the app).
/// </summary>
public partial class MainWindow
{
    private static readonly Color NewsGold = Color.FromRgb(0xFF, 0xD3, 0x6B);
    private bool _whatsNewPending;
    private bool _whatsNewTeased; // the notice already showed itself once this time (over whatever the island had)

    private Border? _newsCard;
    private Grid? _newsArt;
    private TextBlock? _newsTag, _newsTitle, _newsText;
    private StackPanel? _newsDots;
    private ScrollViewer? _newsList;
    private TextBlock? _newsAllText;
    private Border? _newsPrev, _newsNext;
    private int _newsIndex;
    private double _newsShownAt;
    private bool _newsManual;
    private readonly List<Action<double>> _newsArtFrames = new();

    private void InitWhatsNew()
    {
        if (AppSettings.WasFirstRun) S.LastSeenNotes = WhatsNew.Version; // a new install: nothing is "new" yet
        else if (S.LastSeenNotes != WhatsNew.Version) _whatsNewPending = true;
        if (AppSettings.WasFirstRun) S.Save();
        BuildNewsTab();
    }

    /// <summary>Opens the island on the Novidades tab (Settings → About).</summary>
    public void ShowWhatsNew()
    {
        SelectTab(Tab.News, animate: false);
        _clickOpened = true;
        _hoverRequested = false;
        UpdateState();
        if (_state == IslandState.Expanded && _tab != Tab.News) SelectTab(Tab.News, animate: true);
    }

    private void MarkWhatsNewSeen()
    {
        if (!_whatsNewPending && S.LastSeenNotes == WhatsNew.Version) return;
        _whatsNewPending = false;
        S.LastSeenNotes = WhatsNew.Version;
        S.Save();
    }

    // ------------------------------------------------------------------ the small island

    /// <summary>Wide enough for "✦ What's new in Notchy 2.0" on a single line, whatever the language.</summary>
    private double WhatsNewWidth()
    {
        WhatsNewText.Text = Loc.F("news.compact", WhatsNew.Version);
        WhatsNewText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Math.Ceiling(WhatsNewText.DesiredSize.Width) + 20 + 13 + 8 + 9 + 24 + 20;
    }

    private void UpdateWhatsNewCompact()
    {
        if (_state != IslandState.Compact || _compactMode != CompactMode.WhatsNew) return;
        double t = AnimNow;
        double tw = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 1.4);
        WhatsNewSparkScale.ScaleX = WhatsNewSparkScale.ScaleY = 0.78 + 0.22 * tw;
        WhatsNewSparkRotate.Angle = 20 * (1 - tw);
        WhatsNewSpark.Opacity = 0.65 + 0.35 * tw;
        WhatsNewEyesBob.Y = -1.5 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2));
        WhatsNewLine.Opacity = 0.25 + 0.6 * (0.5 + 0.5 * Math.Sin(t * Math.PI * 2 / 2.2));
    }

    // ------------------------------------------------------------------ the tab

    private void BuildNewsTab()
    {
        NewsTab.Children.Clear();
        NewsTab.RowDefinitions.Clear();
        NewsTab.RowDefinitions.Add(new RowDefinition());
        NewsTab.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        // a highlight: its picture on the left, what it is on the right
        _newsArt = new Grid { ClipToBounds = true };
        var artBox = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), Child = _newsArt };
        _newsTag = new TextBlock { FontSize = 10, FontWeight = FontWeights.ExtraBold, Margin = new Thickness(0, 0, 0, 4) };
        _newsTitle = new TextBlock { FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        _newsText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xBB, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };
        texts.Children.Add(_newsTag);
        texts.Children.Add(_newsTitle);
        texts.Children.Add(_newsText);
        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(164) });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardGrid.Children.Add(artBox);
        Grid.SetColumn(texts, 1);
        cardGrid.Children.Add(texts);
        _newsCard = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(12), Child = cardGrid, RenderTransform = new TranslateTransform() };
        NewsTab.Children.Add(_newsCard);

        // see all: the full notes
        var list = new StackPanel { Margin = new Thickness(2, 0, 8, 6) };
        foreach (var s in WhatsNew.Sections)
        {
            list.Children.Add(new TextBlock
            {
                Text = s.Title.ToUpper(CultureInfo.CurrentCulture), FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(0x77, 255, 255, 255)), Margin = new Thickness(0, 10, 0, 4)
            });
            foreach (var item in s.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(NewsGold), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 7, 0, 0) });
                var tb = new TextBlock { Text = item, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)), TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);
                list.Children.Add(row);
            }
        }
        _newsList = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed, FocusVisualStyle = null };
        NewsTab.Children.Add(_newsList);

        // dots and buttons
        _newsDots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < WhatsNew.Highlights.Length; i++)
        {
            int k = i;
            var dot = new Border { Height = 6, Width = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) };
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; _newsManual = true; ShowNewsCard(k, animate: true); };
            _newsDots.Children.Add(dot);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _newsPrev = NewsButton("‹", () => { _newsManual = true; ShowNewsCard(_newsIndex - 1, animate: true); }, out _);
        _newsNext = NewsButton("›", () => { _newsManual = true; ShowNewsCard(_newsIndex + 1, animate: true); }, out _);
        buttons.Children.Add(_newsPrev);
        buttons.Children.Add(_newsNext);
        buttons.Children.Add(NewsButton(Loc.T("news.all"), ToggleNewsList, out _newsAllText, primary: true));
        buttons.Children.Add(NewsButton(Loc.T("news.close"), CloseWhatsNew, out _));
        var foot = new Grid();
        foot.Children.Add(_newsDots);
        foot.Children.Add(buttons);
        Grid.SetRow(foot, 1);
        NewsTab.Children.Add(foot);
    }

    private Border NewsButton(string text, Action click, out TextBlock label, bool primary = false)
    {
        label = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = primary ? new SolidColorBrush(Color.FromRgb(0x1C, 0x16, 0x06)) : Brushes.White };
        var b = new Border
        {
            Child = label, CornerRadius = new CornerRadius(13), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0), Cursor = Cursors.Hand,
            Background = primary ? new SolidColorBrush(NewsGold) : new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255)), Tag = primary
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.82;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            click();
        };
        return b;
    }

    /// <summary>The tab came up: highlights from the first one, and it counts as seen.</summary>
    private void OnNewsShown()
    {
        MarkWhatsNewSeen();
        if (_newsList != null) _newsList.Visibility = Visibility.Collapsed;
        if (_newsCard != null) _newsCard.Visibility = Visibility.Visible;
        if (_newsDots != null) _newsDots.Visibility = Visibility.Visible;
        if (_newsAllText != null) _newsAllText.Text = Loc.T("news.all");
        if (_newsPrev != null && _newsNext != null) _newsPrev.Visibility = _newsNext.Visibility = Visibility.Visible;
        _newsManual = false;
        ShowNewsCard(0, animate: false);
    }

    private void ToggleNewsList()
    {
        if (_newsList == null || _newsCard == null || _newsDots == null || _newsAllText == null) return;
        bool showList = _newsList.Visibility != Visibility.Visible;
        _newsList.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        _newsCard.Visibility = _newsDots.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
        _newsAllText.Text = Loc.T(showList ? "news.highlights" : "news.all");
        // the arrows are for the highlights: not in the full list
        if (_newsPrev != null && _newsNext != null) _newsPrev.Visibility = _newsNext.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
        if (showList)
        {
            _newsList.ScrollToTop();
            _newsList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(220))));
        }
        else ShowNewsCard(_newsIndex, animate: true);
    }

    private void CloseWhatsNew()
    {
        MarkWhatsNewSeen();
        _clickOpened = false;
        _hoverActive = false;
        _hoverRequested = false;
        SelectTab(SmartTab(), animate: false);
        UpdateState();
    }

    private void ShowNewsCard(int index, bool animate)
    {
        if (_newsCard == null || _newsArt == null || _newsTag == null || _newsTitle == null || _newsText == null || _newsDots == null) return;
        int n = WhatsNew.Highlights.Length;
        _newsIndex = (index % n + n) % n;
        var h = WhatsNew.Highlights[_newsIndex];
        _newsShownAt = AnimNow;

        _newsCard.Background = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0x38, h.Color.R, h.Color.G, h.Color.B), 0),
            new GradientStop(Color.FromArgb(0x10, h.Color.R, h.Color.G, h.Color.B), 0.6),
            new GradientStop(Color.FromArgb(0x06, 255, 255, 255), 1)
        }, new Point(0, 0), new Point(1, 1));
        _newsTag.Text = h.Tag.ToUpper(CultureInfo.CurrentCulture);
        _newsTag.Foreground = new SolidColorBrush(h.Color);
        _newsTitle.Text = h.Title;
        _newsText.Text = h.Text;
        BuildNewsArt(h);
        for (int i = 0; i < _newsDots.Children.Count; i++)
        {
            var dot = (Border)_newsDots.Children[i];
            bool on = i == _newsIndex;
            dot.Background = new SolidColorBrush(on ? h.Color : Color.FromArgb(0x30, 255, 255, 255));
            dot.BeginAnimation(WidthProperty, new DoubleAnimation(on ? 18 : 6, TimeSpan.FromMilliseconds(Dur(240))));
        }
        if (!animate) return;
        var d = TimeSpan.FromMilliseconds(Dur(260));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((TranslateTransform)_newsCard.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(14, 0, d) { EasingFunction = ease });
        _newsCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = ease });
    }

    /// <summary>Every frame while the tab is on screen: the highlight's little animation, and the next one after a while.</summary>
    private void UpdateWhatsNewFrame()
    {
        // right after an update (once the opening animation is over), the notice shows for a few seconds even
        // over music or Claude, so the update never goes unnoticed; then it waits for a free island
        if (_whatsNewPending && !_whatsNewTeased && !_introActive && _state is IslandState.Idle or IslandState.Compact)
        {
            _whatsNewTeased = true;
            ShowTransient(CompactMode.WhatsNew, "", "", 8);
        }
        UpdateWhatsNewCompact();
        if (_state != IslandState.Expanded || _tab != Tab.News) return;
        double t = AnimNow;
        foreach (var f in _newsArtFrames) f(t);
        bool listOpen = _newsList?.Visibility == Visibility.Visible;
        if (!_newsManual && !listOpen && t - _newsShownAt > 4.2 * Math.Max(0.3, S.SpeedFactor)) ShowNewsCard(_newsIndex + 1, animate: true);
    }

    // ------------------------------------------------------------------ the little pictures

    private void BuildNewsArt(WhatsNew.Highlight h)
    {
        _newsArt!.Children.Clear();
        _newsArtFrames.Clear();
        var c = new Canvas { Width = 140, Height = 84, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _newsArt.Children.Add(c);
        var color = h.Color;
        var glow = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.9, Color = color };

        Rectangle Pill(double x, double y, double w, double hh, Brush fill) { var r = new Rectangle { Width = w, Height = hh, RadiusX = hh / 2, RadiusY = hh / 2, Fill = fill }; Canvas.SetLeft(r, x); Canvas.SetTop(r, y); c.Children.Add(r); return r; }
        void Eyes(double cx, double cy, Color col, double w = 6, double hh = 10, bool round = false)
        {
            for (int i = 0; i < 2; i++)
            {
                Shape e = round ? new Ellipse { Width = w, Height = hh } : new Rectangle { Width = w, Height = hh, RadiusX = w / 2, RadiusY = w / 2 };
                e.Fill = new SolidColorBrush(col);
                Canvas.SetLeft(e, cx + (i == 0 ? -w - 4 : 4));
                Canvas.SetTop(e, cy - hh / 2);
                c.Children.Add(e);
            }
        }
        TextBlock Label(string text, double x, double y, Brush fg, double size = 10, bool bold = false)
        {
            var tb = new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); c.Children.Add(tb); return tb;
        }

        switch (h.Art)
        {
            case "dl":
            {
                // the island with the green light filling its edge, and the %
                Pill(10, 14, 120, 40, Brushes.Black);
                var edge = Pill(10, 14, 120, 40, Brushes.Transparent);
                edge.Stroke = new SolidColorBrush(color);
                edge.StrokeThickness = 2.5;
                edge.Effect = glow;
                Eyes(70, 34, color);
                var pct = Label("0%", 60, 60, new SolidColorBrush(Color.FromArgb(0xAA, 255, 255, 255)));
                double per = 2 * (120 - 40) + Math.PI * 40, th = 2.5;
                _newsArtFrames.Add(t =>
                {
                    double p = t % 4 / 4;
                    edge.StrokeDashArray = new DoubleCollection { Math.Max(0.01, p * per / th), per / th };
                    pct.Text = $"{Math.Floor(p * 100)}%";
                });
                break;
            }
            case "claude":
            {
                // the island with Claude's light running round, and Allow / Deny under it
                Pill(10, 4, 120, 36, Brushes.Black);
                var edge = Pill(10, 4, 120, 36, Brushes.Transparent);
                edge.Stroke = new SolidColorBrush(color);
                edge.StrokeThickness = 2.5;
                edge.Effect = glow;
                double per = 2 * (120 - 36) + Math.PI * 36, th = 2.5;
                edge.StrokeDashArray = new DoubleCollection { per * 0.18 / th, per * 0.82 / th };
                Eyes(70, 22, Color.FromRgb(0xFF, 0xD9, 0xC4), 7, 12, round: true);
                Pill(22, 52, 46, 20, new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)));
                Label(Loc.T("claude.ask.deny"), 31, 55, Brushes.White);
                Pill(74, 52, 50, 20, new SolidColorBrush(color));
                Label(Loc.T("claude.ask.allow"), 80, 55, new SolidColorBrush(Color.FromRgb(0x1B, 0x12, 0x30)), bold: true);
                _newsArtFrames.Add(t => edge.StrokeDashOffset = -(t % 2.6 / 2.6) * per / th);
                break;
            }
            case "focus":
            {
                // the ring filling up around concentrating eyes
                var track = new Ellipse { Width = 72, Height = 72, Stroke = new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)), StrokeThickness = 6 };
                Canvas.SetLeft(track, 34); Canvas.SetTop(track, 6); c.Children.Add(track);
                var arc = new Path { Stroke = new SolidColorBrush(color), StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = glow };
                Canvas.SetLeft(arc, 34); Canvas.SetTop(arc, 6); c.Children.Add(arc);
                Eyes(70, 42, color, 7, 7);
                _newsArtFrames.Add(t => arc.Data = ArcGeometry(36, 33, t % 6 / 6));
                break;
            }
            case "icons":
            {
                // the menu with its icons alive: ✻ turning, the download arrow breathing, the focus ring ticking
                Pill(2, 25, 136, 34, new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)));
                var star = new TextBlock { Text = "✻", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 16, Foreground = new SolidColorBrush(ClaudeColor), RenderTransformOrigin = new Point(0.5, 0.52), RenderTransform = new RotateTransform() };
                Canvas.SetLeft(star, 24); Canvas.SetTop(star, 30); c.Children.Add(star);
                var arrowMove = new TranslateTransform();
                var arrow = new Path { Data = Geometry.Parse("M0,-8 V3 M-5,-2 L0,3 L5,-2"), Stroke = new SolidColorBrush(DoneGreen), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, RenderTransform = arrowMove };
                Canvas.SetLeft(arrow, 70); Canvas.SetTop(arrow, 42); c.Children.Add(arrow);
                var bar = new Rectangle { Width = 14, Height = 2, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Color.FromArgb(0x66, DoneGreen.R, DoneGreen.G, DoneGreen.B)) };
                Canvas.SetLeft(bar, 63); Canvas.SetTop(bar, 51); c.Children.Add(bar);
                var ringTrack = new Ellipse { Width = 18, Height = 18, Stroke = new SolidColorBrush(Color.FromArgb(0x40, FocusColor.R, FocusColor.G, FocusColor.B)), StrokeThickness = 2 };
                Canvas.SetLeft(ringTrack, 101); Canvas.SetTop(ringTrack, 33); c.Children.Add(ringTrack);
                var ring = new Path { Stroke = new SolidColorBrush(FocusColor), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                Canvas.SetLeft(ring, 101); Canvas.SetTop(ring, 33); c.Children.Add(ring);
                _newsArtFrames.Add(t =>
                {
                    ((RotateTransform)star.RenderTransform).Angle = t * 150 % 360;
                    arrowMove.Y = -1.2 + 2.4 * (0.5 - 0.5 * Math.Cos(t * Math.PI * 2 / 1.6));
                    ring.Data = ArcGeometry(9, 8, t % 5 / 5);
                });
                break;
            }
            default:
            {
                // a little settings window: the coloured categories, one picked
                var frame = new Rectangle { Width = 124, Height = 76, RadiusX = 10, RadiusY = 10, Fill = new SolidColorBrush(Color.FromRgb(0x0B, 0x0C, 0x0F)) };
                Canvas.SetLeft(frame, 8); Canvas.SetTop(frame, 4); c.Children.Add(frame);
                Color[] cats = { Color.FromRgb(0xE6, 0xE6, 0xEE), Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x7C, 0xC8, 0xFF), Color.FromRgb(0xFF, 0x7E, 0xB6), ClaudeColor, FocusColor };
                var tiles = new List<Rectangle>();
                for (int i = 0; i < cats.Length; i++)
                {
                    var tile = new Rectangle { Width = 8, Height = 8, RadiusX = 2.5, RadiusY = 2.5, Fill = new SolidColorBrush(cats[i]) };
                    Canvas.SetLeft(tile, 16); Canvas.SetTop(tile, 10 + i * 10.5); c.Children.Add(tile);
                    var line = new Rectangle { Width = 30 + i % 3 * 8, Height = 4, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(Color.FromArgb(0x44, 255, 255, 255)) };
                    Canvas.SetLeft(line, 29); Canvas.SetTop(line, 12 + i * 10.5); c.Children.Add(line);
                    tiles.Add(tile);
                }
                var card = new Rectangle { Width = 44, Height = 22, RadiusX = 5, RadiusY = 5, Fill = new SolidColorBrush(Color.FromArgb(0x10, 255, 255, 255)) };
                Canvas.SetLeft(card, 78); Canvas.SetTop(card, 28); c.Children.Add(card);
                var toggle = new Rectangle { Width = 14, Height = 8, RadiusX = 4, RadiusY = 4 };
                Canvas.SetLeft(toggle, 104); Canvas.SetTop(toggle, 35); c.Children.Add(toggle);
                _newsArtFrames.Add(t =>
                {
                    int sel = (int)(t / 1.2) % cats.Length;
                    for (int i = 0; i < tiles.Count; i++) tiles[i].Opacity = i == sel ? 1 : 0.35;
                    if (toggle.Fill is not SolidColorBrush tb || tb.Color != cats[sel]) toggle.Fill = new SolidColorBrush(cats[sel]);
                });
                break;
            }
        }
        foreach (var f in _newsArtFrames) f(AnimNow);
    }
}
