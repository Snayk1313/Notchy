using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;

namespace Notchy;

/// <summary>
/// The weather on the home screen: a 4th card (next to Claude, Music and Focus) with the sky's icon, the
/// temperature and today's high/low, tinted by the weather (gold sun, blue rain, lavender night…). Clicking it opens
/// the "‹ Clima" sub-page: now (icon, temperature, sky, city, feels like), the next hours and the next days.
/// </summary>
public partial class MainWindow
{
    private readonly WeatherService _weather = new();
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    private void InitWeather()
    {
        _weather.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateWeatherCard();
            // (not while you're typing a city: that would wipe the box and its matches)
            if (_subOpen && _subTitle.Text == Loc.T("weather.title") && !_cityEditing) RefreshSubPage(WeatherPage);
        });
        _weatherTimer.Tick += async (_, _) => await _weather.RefreshAsync();
        _weatherTimer.Start();
        Loaded += async (_, _) => await _weather.RefreshAsync(force: true);
        UpdateWeatherCard();
    }

    private async void HomeWeather_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SoundService.Play(Sfx.Click);
        // (no location: the sub-page itself offers to turn it on or to pick a city)
        OpenSubPage(Loc.T("weather.title"), WeatherPage);
        await _weather.RefreshAsync();
    }

    // =====================================================================================
    // the card
    // =====================================================================================

    private void UpdateWeatherCard()
    {
        HomeWeatherLabel.Text = Loc.T("weather.title");
        var w = _weather.Current;
        if (w == null)
        {
            HomeWeatherBig.Text = "–";
            HomeWeatherSub.Text = _weather.Problem switch
            {
                WeatherProblem.NoLocation => Loc.T("weather.nolocation"),
                WeatherProblem.Offline => Loc.T("weather.offline"),
                _ => Loc.T("weather.loading")
            };
            SetSkyIcon(HomeWeatherIcon, Sky.Cloudy, true);
            HomeWeatherCard.Background = CardGradient(Color.FromRgb(0x2A, 0x2A, 0x2E), Color.FromRgb(0x44, 0x44, 0x4C));
            return;
        }
        HomeWeatherBig.Text = $"{Math.Round(w.Temp)}°";
        HomeWeatherSub.Text = $"{Math.Round(w.TodayMax)}° / {Math.Round(w.TodayMin)}°";
        SetSkyIcon(HomeWeatherIcon, w.Sky, w.Day);
        var (top, bottom) = SkyCardColors(w.Sky, w.Day);
        HomeWeatherCard.Background = CardGradient(top, bottom);
        HomeWeatherCard.ToolTip = (w.City.Length > 0 ? w.City + " · " : "") + SkyName(w.Sky, w.Day);
        if (_nav.Count > 0) CardGlow(HomeWeatherCard, WeatherGlowColor(w)); // the halo follows the sky too
    }

    private static LinearGradientBrush CardGradient(Color top, Color bottom)
    {
        var b = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0.4, 1));
        b.Freeze();
        return b;
    }

    /// <summary>The card's tint follows the sky (dark at the top, the colour at the bottom, like the other cards).</summary>
    private static (Color, Color) SkyCardColors(Sky sky, bool day) => (sky, day) switch
    {
        (Sky.Clear or Sky.PartlyCloudy, true) => (Color.FromRgb(0x3A, 0x2E, 0x14), Color.FromRgb(0xB0, 0x82, 0x2C)),
        (Sky.Clear or Sky.PartlyCloudy, false) => (Color.FromRgb(0x1E, 0x1C, 0x3E), Color.FromRgb(0x4E, 0x46, 0xA0)),
        (Sky.Rain or Sky.Storm, _) => (Color.FromRgb(0x14, 0x24, 0x3A), Color.FromRgb(0x2C, 0x5E, 0x96)),
        (Sky.Snow, _) => (Color.FromRgb(0x24, 0x2C, 0x38), Color.FromRgb(0x70, 0x84, 0x9C)),
        _ => (Color.FromRgb(0x26, 0x28, 0x2E), Color.FromRgb(0x56, 0x5C, 0x68))
    };

    private static Color WeatherGlowColor(WeatherNow? w) => w == null ? Colors.White : SkyCardColors(w.Sky, w.Day).Item2;

    // =====================================================================================
    // icons (drawn on a 24-unit grid, thin strokes like the other icons)
    // =====================================================================================

    private const string SunPath = "M12 8a4 4 0 1 0 0 8a4 4 0 1 0 0-8zM12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4";
    private const string MoonPath = "M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z";
    private const string CloudPath = "M7 18a4.5 4.5 0 0 1-.7-8.95A6 6 0 0 1 17.6 9a4 4 0 0 1-.6 9z";
    private const string CloudHighPath = "M7 14a4.5 4.5 0 0 1-.7-8.95A6 6 0 0 1 17.6 5a4 4 0 0 1-.6 9z";

    private static (string data, Color color) SkyIcon(Sky sky, bool day) => sky switch
    {
        Sky.Clear => day ? (SunPath, SunColor) : (MoonPath, Color.FromRgb(0xC9, 0xC2, 0xFF)),
        Sky.PartlyCloudy => (day ? "M8 3v1.2M3.5 7.5h1.2M4.8 4.3l.9.9M11.2 4.3l-.9.9M5.5 9.5a3 3 0 0 1 5-3" : "M9.5 3a4.5 4.5 0 0 0 1.8 6.5",
            day ? SunColor : Color.FromRgb(0xC9, 0xC2, 0xFF)),
        Sky.Rain => (CloudHighPath + "M8 17l-1 3M12 17l-1 3M16 17l-1 3", Color.FromRgb(0x9F, 0xD7, 0xFF)),
        Sky.Storm => (CloudHighPath + "M12.5 15l-2 3.5h3l-2 3.5", SunColor),
        Sky.Snow => (CloudHighPath + "M8 18h.01M12 18h.01M16 18h.01M10 21h.01M14 21h.01", Colors.White),
        Sky.Fog => ("M4 9h16M3 13h18M6 17h12", Color.FromRgb(0xCF, 0xD8, 0xE3)),
        _ => (CloudPath, Color.FromRgb(0xCF, 0xD8, 0xE3))
    };

    private static void SetSkyIcon(Path path, Sky sky, bool day)
    {
        var (data, color) = SkyIcon(sky, day);
        // partly cloudy: the sun (or moon) peeking behind a cloud
        string full = sky == Sky.PartlyCloudy ? data + "M9 19a4 4 0 0 1-.6-7.95A5.2 5.2 0 0 1 18.2 11a3.4 3.4 0 0 1-.5 8z" : data;
        path.Data = Geometry.Parse(full);
        path.Stroke = new SolidColorBrush(color);
        path.Fill = null;
    }

    private Path SkyPath(Sky sky, bool day, double size)
    {
        var p = new Path { Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
        SetSkyIcon(p, sky, day);
        return p;
    }

    private static string SkyName(Sky sky, bool day) => Loc.T(sky switch
    {
        Sky.Clear => day ? "weather.clear" : "weather.clearnight",
        Sky.PartlyCloudy => "weather.partly",
        Sky.Cloudy => "weather.cloudy",
        Sky.Fog => "weather.fog",
        Sky.Rain => "weather.rain",
        Sky.Snow => "weather.snow",
        _ => "weather.storm"
    });

    // =====================================================================================
    // the sub-page
    // =====================================================================================

    private FrameworkElement WeatherPage()
    {
        var page = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        var w = _weather.Current;
        if (w == null)
        {
            string msg = _weather.Problem switch
            {
                WeatherProblem.NoLocation => Loc.T("weather.nolocation.d"),
                WeatherProblem.Offline => Loc.T("weather.offline"),
                _ => Loc.T("weather.loading")
            };
            page.Children.Add(new TextBlock { Text = msg, FontSize = 11.5, Foreground = White(0x80), Margin = new Thickness(4, 4, 0, 4), TextWrapping = TextWrapping.Wrap });
            page.Children.Add(WeatherPlaceRow());
            return page;
        }

        // now: big icon, temperature, sky · city | high/low, feels like
        var now = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.ColumnDefinitions.Add(new ColumnDefinition());
        now.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        now.Children.Add(SkyPath(w.Sky, w.Day, 40));
        var mid = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(new TextBlock { Text = $"{Math.Round(w.Temp)}°", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, -4, 0, -2) });
        mid.Children.Add(new TextBlock { Text = SkyName(w.Sky, w.Day) + (w.City.Length > 0 ? " · " + w.City : ""), FontSize = 11.5, Foreground = White(0x99),
            TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(mid, 1);
        now.Children.Add(mid);
        var right = new TextBlock { FontSize = 11, Foreground = White(0x99), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Inlines.Add(new System.Windows.Documents.Run(Loc.F("weather.highlow", Math.Round(w.TodayMax), Math.Round(w.TodayMin))));
        right.Inlines.Add(new System.Windows.Documents.LineBreak());
        right.Inlines.Add(new System.Windows.Documents.Run(Loc.F("weather.feels", Math.Round(w.Feels))));
        Grid.SetColumn(right, 2);
        now.Children.Add(right);
        page.Children.Add(now);

        // the next hours
        var hours = new UniformGrid6();
        foreach (var h in w.Hours.Where(h => h.Time > DateTime.Now.AddMinutes(-30)).Take(6))
        {
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(new TextBlock { Text = h.Time.ToString("HH", Loc.Culture) + "h", FontSize = 10.5, Foreground = White(0x88), HorizontalAlignment = HorizontalAlignment.Center });
            var icon = SkyPath(h.Sky, h.Day, 16);
            icon.Margin = new Thickness(0, 4, 0, 3);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            cell.Children.Add(icon);
            cell.Children.Add(new TextBlock { Text = $"{Math.Round(h.Temp)}°", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center });
            hours.Add(new Border { Child = cell, CornerRadius = new CornerRadius(10), Background = White(0x0A), Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(3, 0, 3, 0) });
        }
        hours.Grid.Margin = new Thickness(1, 8, 1, 0);
        page.Children.Add(hours.Grid);

        // the next days: name, icon, a bar from the low to the high on a shared scale
        double lo = w.Days.Min(d => d.Min), hi = w.Days.Max(d => d.Max), span = Math.Max(1, hi - lo);
        var days = new StackPanel { Margin = new Thickness(4, 6, 4, 0) };
        foreach (var d in w.Days.Skip(1).Take(3))
        {
            var row = new Grid { Height = 22 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.Children.Add(new TextBlock { Text = d.Date.ToString("ddd", Loc.Culture).TrimEnd('.'), FontSize = 11.5, Foreground = White(0xCC), VerticalAlignment = VerticalAlignment.Center });
            var ic = SkyPath(d.Sky, true, 14);
            Grid.SetColumn(ic, 1);
            row.Children.Add(ic);
            var min = new TextBlock { Text = $"{Math.Round(d.Min)}°", FontSize = 11, Foreground = White(0x88), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(min, 2);
            row.Children.Add(min);
            var bar = new Grid { Height = 4, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = White(0x14) });
            var fill = new Border
            {
                CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush(Color.FromRgb(0x7F, 0xD0, 0xFF), SunColor, 0)
            };
            bar.Children.Add(fill);
            var day = d;
            bar.SizeChanged += (_, _) =>
            {
                double a = (day.Min - lo) / span, b = (day.Max - lo) / span;
                fill.Margin = new Thickness(bar.ActualWidth * a, 0, 0, 0);
                fill.Width = Math.Max(4, bar.ActualWidth * (b - a));
            };
            Grid.SetColumn(bar, 3);
            row.Children.Add(bar);
            var max = new TextBlock { Text = $"{Math.Round(d.Max)}°", FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(max, 4);
            row.Children.Add(max);
            days.Children.Add(row);
        }
        page.Children.Add(days);
        page.Children.Add(WeatherPlaceRow());
        return page;
    }

    // =====================================================================================
    // where: automatic (the PC's location) or a city typed by hand
    // =====================================================================================

    private TextBox? _cityBox;
    private StackPanel? _cityResults;
    private DispatcherTimer? _citySearch;
    private bool _cityEditing;

    /// <summary>"Localização  [📍 Automática] [✎ Manual]", and in manual mode the city box with its matches.</summary>
    private FrameworkElement WeatherPlaceRow()
    {
        var box = new StackPanel { Margin = new Thickness(4, 8, 4, 0) };
        var line = new DockPanel();
        var pills = new StackPanel { Orientation = Orientation.Horizontal };
        bool manual = !S.WeatherAuto;
        pills.Children.Add(ListPill("", Loc.T("weather.auto"), !manual, () =>
        {
            S.WeatherAuto = true;
            _cityEditing = false;
            AppSettings.Commit();
            _ = _weather.RefreshAsync(force: true);
            RefreshSubPage(WeatherPage);
        }));
        // Manual: back to the city you saved (if any) at once; already manual (or none saved): pick one
        var manualPill = ListPill("", Loc.T("weather.manual"), manual, () =>
        {
            if (!manual && S.WeatherCity.Length > 0)
            {
                S.WeatherAuto = false;
                AppSettings.Commit();
                _ = _weather.RefreshAsync(force: true);
                RefreshSubPage(WeatherPage);
            }
            else OpenCityPage();
        });
        manualPill.Margin = new Thickness(5, 0, 0, 0);
        pills.Children.Add(manualPill);
        DockPanel.SetDock(pills, Dock.Right);
        line.Children.Add(pills);
        string where = manual && S.WeatherCity.Length > 0 ? S.WeatherCity : Loc.T("weather.loc.auto.d");
        line.Children.Add(new TextBlock { Text = Loc.T("weather.loc") + " · " + where, FontSize = 11, Foreground = White(0x80), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis });
        box.Children.Add(line);

        return box;
    }

    /// <summary>"‹ Escolher cidade": only the search box and its matches; picking one goes back to the weather.</summary>
    private void OpenCityPage()
    {
        _cityEditing = true;
        _subParent = (Loc.T("weather.title"), WeatherPage, () => _cityEditing = false);
        OpenSubPage(Loc.T("weather.city.title"), CityPage);
        Dispatcher.BeginInvoke(() =>
        {
            if (_cityBox == null) return;
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
            Activate();
            _cityBox.Focus();
            Keyboard.Focus(_cityBox);
            _cityBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private FrameworkElement CityPage()
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        box.Children.Add(ListTop(Loc.T("s.weather.city.d"), null));
        var field = new Grid();
        _cityBox = new TextBox
        {
            Background = White(0x0E), BorderBrush = White(0x1C), BorderThickness = new Thickness(1), Foreground = Brushes.White, CaretBrush = Brushes.White,
            FontSize = 12.5, Padding = new Thickness(10, 7, 10, 7), FocusVisualStyle = null, SelectionBrush = Br(Color.FromRgb(0x7F, 0xD0, 0xFF)),
            Text = S.WeatherCity
        };
        var hint = new TextBlock { Text = Loc.T("weather.city.hint"), FontSize = 12.5, Foreground = White(0x55), Margin = new Thickness(12, 8, 0, 0), IsHitTestVisible = false,
            Visibility = S.WeatherCity.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        field.Children.Add(_cityBox);
        field.Children.Add(hint);
        MakeTypable(_cityBox, null);
        _cityResults = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        _citySearch?.Stop();
        _citySearch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _citySearch.Tick += async (_, _) =>
        {
            _citySearch!.Stop();
            await SearchCitiesAsync(_cityBox.Text);
        };
        _cityBox.TextChanged += (_, _) =>
        {
            hint.Visibility = _cityBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _citySearch.Stop();
            _citySearch.Start();
        };
        box.Children.Add(field);
        // the matches scroll when they don't fit under the box
        box.Children.Add(Scroller(_cityResults, maxHeight: Math.Max(100, MaxSubHeight - 90)));
        if (_cityBox.Text.Length >= 2) _citySearch.Start();
        return box;
    }

    private async System.Threading.Tasks.Task SearchCitiesAsync(string text)
    {
        if (_cityResults == null) return;
        _cityResults.Children.Clear();
        if (text.Trim().Length >= 2)
        {
            _cityResults.Children.Add(new TextBlock { Text = Loc.T("weather.searching"), FontSize = 11, Foreground = White(0x70), Margin = new Thickness(4, 2, 0, 4) });
            ResizeSubPage();
            var found = await WeatherService.SearchAsync(text);
            if (_cityResults == null || _cityBox?.Text != text) return; // typed on meanwhile
            _cityResults.Children.Clear();
            if (found.Count == 0)
                _cityResults.Children.Add(new TextBlock { Text = Loc.T("weather.noresult"), FontSize = 11, Foreground = White(0x70), Margin = new Thickness(4, 2, 0, 4) });
            foreach (var c in found.Take(4))
            {
                var city = c;
                _cityResults.Children.Add(ListRow("", c.Name, c.Region, false, -1, () =>
                {
                    SoundService.Play(Sfx.Click);
                    S.WeatherAuto = false;
                    S.WeatherCity = city.Name;
                    S.WeatherLat = city.Lat;
                    S.WeatherLon = city.Lon;
                    AppSettings.Commit();
                    Keyboard.ClearFocus();
                    if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
                    _ = _weather.RefreshAsync(force: true);
                    SubPageBack();
                }));
            }
        }
        ResizeSubPage();
    }

    /// <summary>A row of equal cells (six hours).</summary>
    private sealed class UniformGrid6
    {
        public Grid Grid { get; } = new();
        private int _n;

        public void Add(UIElement e)
        {
            Grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(e, _n++);
            Grid.Children.Add(e);
        }
    }
}
