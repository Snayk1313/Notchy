using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// The assistant of the first opening: this same window, smaller, with a trail of steps on the left instead of the categories.
/// It has no eyes of its own; the real island above it looks around and smiles while the choices are made.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>A step: key, colour and its icon (lines on a 24x24 grid).</summary>
    private static readonly (string Key, Color Color, string Icon)[] SetupSteps =
    {
        ("hello", Color.FromRgb(0xE6, 0xE6, 0xEE), "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M8,14 C9,15.4 10.4,16 12,16 S15,15.4 16,14 M9,9 L9,10 M15,9 L15,10"),
        ("look", Color.FromRgb(0xA7, 0x8B, 0xFA), "M12,3 A9,9 0 1 0 12,21 C13.6,21 14,19.8 13.4,18.8 C12.7,17.6 13.5,16 15,16 L18,16 A3,3 0 0 0 21,13 C21,7.5 17,3 12,3 Z M8,11 L8.01,11 M12,7.5 L12.01,7.5 M16.3,10 L16.31,10"),
        ("notif", Color.FromRgb(0x7C, 0xC8, 0xFF), "M6,16 L6,11 A6,6 0 1 1 18,11 L18,16 L20,18 L4,18 Z M10,21 L14,21"),
        ("extras", Color.FromRgb(0x5E, 0xD6, 0x7D), "M7,5 L17,5 A3,3 0 0 1 20,8 L20,18 A3,3 0 0 1 17,21 L7,21 A3,3 0 0 1 4,18 L4,8 A3,3 0 0 1 7,5 Z M4,10 L20,10 M9,3 L9,7 M15,3 L15,7"),
        ("claude", Color.FromRgb(0xE8, 0x82, 0x5F), "M12,3 L12,21 M4.2,7.5 L19.8,16.5 M4.2,16.5 L19.8,7.5"),
        ("tour", Color.FromRgb(0xFF, 0xD3, 0x6B), "M12,3 L14.6,8.8 L21,9.5 L16.2,13.8 L17.5,20 L12,16.8 L6.5,20 L7.8,13.8 L3,9.5 L9.4,8.8 Z"),
    };

    private const int TourStep = 5;
    private const double TrailRowHeight = 46, TrailTop = 23;
    private const double EnterOffset = 26, LeaveOffset = 34, StaggerMs = 55;
    private static readonly Color DoneStep = Color.FromRgb(0x5E, 0xD6, 0x7D);
    private static readonly Color IdleNode = Color.FromRgb(0x1C, 0x1D, 0x22);
    private static readonly IEasingFunction Spring = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction Smooth = new CubicEase { EasingMode = EasingMode.EaseOut };

    private sealed record TrailNode(SolidColorBrush Fill, SolidColorBrush Edge, ScaleTransform Scale, Path Check, SolidColorBrush Text);
    private sealed record TourTip(SolidColorBrush Background, SolidColorBrush Text, SolidColorBrush Dot, TranslateTransform Move);

    private readonly bool _setup;
    private readonly Border _setupFoot = new() { Padding = new Thickness(34, 10, 34, 18) };
    private readonly Grid _setupStage = new();
    private readonly DispatcherTimer _tourTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    private readonly List<TourTip> _tourTips = new();
    private readonly List<TrailNode> _trail = new();
    private readonly StackPanel _setupButtons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _setupCount = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Dim(0x60) };
    private readonly ScaleTransform _setupProgress = new(0, 1);
    private readonly SolidColorBrush _setupProgressBrush = new(SetupSteps[0].Color);
    private readonly GradientStop _auraCenter = new(SetupSteps[0].Color, 0);
    private readonly GradientStop _auraEdge = new(Color.FromArgb(0, SetupSteps[0].Color.R, SetupSteps[0].Color.G, SetupSteps[0].Color.B), 1);
    private readonly GradientStop _trailEnd = new(SetupSteps[0].Color, 1);
    private Border? _trailFill, _fold;
    private int _step, _tip, _setupDirection = 1, _shownStep = -1;
    private bool _celebrate;

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private void InitSetup()
    {
        Island?.SetupStarted();
        _tourTimer.Tick += (_, _) => ShowTip((_tip + 1) % _tourTips.Count);
        Closed += (_, _) =>
        {
            _tourTimer.Stop();
            Island?.SetupEnded(_celebrate);
        };
    }

#if DEBUG
    /// <summary>Test: walks through every step by itself and ends with the celebration.</summary>
    public async Task SetupWalkAsync()
    {
        for (int i = 1; i < SetupSteps.Length; i++)
        {
            await Task.Delay(1600);
            GoSetup(i);
        }
        await Task.Delay(5500);
        _celebrate = true;
        Close();
    }

    /// <summary>Test: a picture of every step, taken from the window itself (whatever is in front of it on the screen).</summary>
    public async Task SetupShotsAsync(string folder)
    {
        for (int i = 0; i < SetupSteps.Length; i++)
        {
            GoSetup(i);
            await Task.Delay(1500);
            var content = (FrameworkElement)Content;
            var picture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            picture.Render(content);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
            using var file = System.IO.File.Create(System.IO.Path.Combine(folder, $"step{i}.png"));
            png.Save(file);
        }
        Close();
    }

    private static IEnumerable<string> DumpTexts(DependencyObject d)
    {
        if (d is TextBlock t && t.Inlines.Count > 0) yield return new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            foreach (var s in DumpTexts(VisualTreeHelper.GetChild(d, i))) yield return s;
    }

    /// <summary>Test: types a city in the assistant's box and writes down what the list shows.</summary>
    public async Task CitySearchTestAsync(string file)
    {
        bool wasAuto = S.WeatherAuto;
        GoSetup(3);
        await Task.Delay(800);
        S.WeatherAuto = false; // (in memory only, put back below)
        Rebuild();
        await Task.Delay(600);
        var boxes = new List<TextBox>();
        void Find(DependencyObject d)
        {
            if (d is TextBox t) boxes.Add(t);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Find(VisualTreeHelper.GetChild(d, i));
        }
        Find((DependencyObject)_pageHost.Content);
        var log = new List<string> { $"caixas: {boxes.Count}" };
        if (boxes.Count > 0)
        {
            boxes[0].Text = "Recife";
            await Task.Delay(9000);
            log.AddRange(DumpTexts((DependencyObject)_pageHost.Content));
            log.Add($"rolagem: viewport={_scroll.ViewportHeight:0} extent={_scroll.ExtentHeight:0} offset={_scroll.VerticalOffset:0}");
        }
        S.WeatherAuto = wasAuto;
        System.IO.File.WriteAllLines(file, log);
    }
#endif

    private static UIElement SetupBrand() =>
        new TextBlock { Text = "Notchy", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 16) };

    // =====================================================================================
    // around the pages
    // =====================================================================================

    /// <summary>A soft glow of the step's colour behind the page, breathing slowly.</summary>
    private UIElement BuildSetupAura()
    {
        var brush = new RadialGradientBrush { GradientStops = { _auraCenter, _auraEdge } };
        var breath = new ScaleTransform(1, 1);
        var aura = new Ellipse
        {
            Width = 420, Height = 340, Fill = brush, Opacity = 0.2, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -130, -110, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = breath
        };
        var grow = new DoubleAnimation(1, 1.14, TimeSpan.FromSeconds(2.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        breath.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        breath.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Grid.SetRowSpan(aura, 3);
        return aura;
    }

    private void BuildSetupFoot()
    {
        var bar = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = Dim(0x0D), Margin = new Thickness(0, 0, 0, 10), ClipToBounds = true,
            Child = new Border { Background = _setupProgressBrush, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _setupProgress } };
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(_setupCount);
        Grid.SetColumn(_setupButtons, 1);
        line.Children.Add(_setupButtons);
        var all = new StackPanel();
        all.Children.Add(bar);
        all.Children.Add(line);
        _setupFoot.Child = all;
    }

    private static void Tint(SolidColorBrush brush, Color color, bool animate, double ms = 300)
    {
        if (animate)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, Ms(ms)));
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = color;
    }

    private static void TintStop(GradientStop stop, Color color, bool animate)
    {
        if (animate)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(color, Ms(450)));
            return;
        }
        stop.BeginAnimation(GradientStop.ColorProperty, null);
        stop.Color = color;
    }

    private static void ToScale(ScaleTransform scale, double to, double ms = 260)
    {
        var move = new DoubleAnimation(to, Ms(ms)) { EasingFunction = Spring };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, move);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, move);
    }

    // =====================================================================================
    // the trail on the left
    // =====================================================================================

    private void BuildSetupSidebar()
    {
        _sideItems.Children.Clear();
        _trail.Clear();

        var trail = new Grid();
        trail.Children.Add(new Border { Width = 2, CornerRadius = new CornerRadius(1), Background = Dim(0x14), HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(17, TrailTop, 0, TrailTop) });
        _trailFill = new Border
        {
            Width = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(17, TrailTop, 0, 0),
            Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop(DoneStep, 0), _trailEnd } },
            Height = 0
        };
        trail.Children.Add(_trailFill);

        var items = new StackPanel();
        for (int i = 0; i < SetupSteps.Length; i++) items.Children.Add(TrailItem(i));
        trail.Children.Add(items);
        _sideItems.Children.Add(trail);
        PaintTrail(animate: false, from: _step);
    }

    private UIElement TrailItem(int step)
    {
        var fill = new SolidColorBrush(IdleNode);
        var edge = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
        var text = new SolidColorBrush(Color.FromArgb(0x78, 255, 255, 255));
        var check = new Path
        {
            Data = Geometry.Parse("M2,6.5 L5,9.5 L10,3"), Stroke = new SolidColorBrush(Side), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, StrokeDashArray = new DoubleCollection { 7, 7 }, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var scale = new ScaleTransform(1, 1);
        var dot = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = fill, BorderBrush = edge, BorderThickness = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale, Child = check };
        var label = new TextBlock { Text = Loc.T("setup.step." + SetupSteps[step].Key), FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(11, 0, 0, 0), Foreground = text, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        row.Children.Add(dot);
        row.Children.Add(label);
        var move = new TranslateTransform();
        var item = new Border { Height = TrailRowHeight, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Child = row, RenderTransform = move };
        item.MouseEnter += (_, _) => move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(2, Ms(160)));
        item.MouseLeave += (_, _) => move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Ms(160)));
        item.MouseLeftButtonUp += (_, _) =>
        {
            SoundService.Play(Sfx.Click);
            GoSetup(step);
        };
        _trail.Add(new TrailNode(fill, edge, scale, check, text));
        return item;
    }

    private void PaintTrail(bool animate, int from)
    {
        var accent = SetupSteps[_step].Color;
        for (int i = 0; i < _trail.Count; i++)
        {
            var node = _trail[i];
            bool current = i == _step, past = i < _step;
            Tint(node.Fill, past ? DoneStep : current ? Bg : IdleNode, animate);
            Tint(node.Edge, past ? DoneStep : current ? accent : Color.FromArgb(0x30, 255, 255, 255), animate);
            Tint(node.Text, current ? Colors.White : Color.FromArgb((byte)(past ? 0xB0 : 0x78), 255, 255, 255), animate);
            if (animate) ToScale(node.Scale, current ? 1.14 : 1);
            else
            {
                node.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                node.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                node.Scale.ScaleX = node.Scale.ScaleY = current ? 1.14 : 1;
            }

            node.Check.Visibility = past ? Visibility.Visible : Visibility.Collapsed;
            node.Check.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            bool justDone = past && i >= from;
            node.Check.StrokeDashOffset = animate && justDone ? 7 : 0;
            if (animate && justDone)
                node.Check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(7, 0, Ms(350)) { BeginTime = Ms(120) });
        }

        double height = _step * TrailRowHeight;
        TintStop(_trailEnd, accent, animate);
        if (_trailFill == null) return;
        if (animate) _trailFill.BeginAnimation(HeightProperty, new DoubleAnimation(height, Ms(420)) { EasingFunction = Smooth });
        else
        {
            _trailFill.BeginAnimation(HeightProperty, null);
            _trailFill.Height = height;
        }
    }

    // =====================================================================================
    // the pages
    // =====================================================================================

    private void GoSetup(int step)
    {
        if (step == _step) return;
        _setupDirection = step > _step ? 1 : -1;
        int from = _step;
        _step = step;
        PaintTrail(animate: true, from: from);
        ShowSetupStep(animate: true);
    }

    private void ShowSetupStep(bool animate)
    {
        var (key, color, icon) = SetupSteps[_step];
        _accent = color;
        _pageHost.Content = _setupStage;
        bool changedStep = animate && _shownStep != _step;
        _shownStep = _step;

        var page = new StackPanel { MaxWidth = 760, RenderTransform = new TranslateTransform() };
        var header = SetupHeader(key, color, icon, changedStep);

        var built = new StackPanel();
        switch (key)
        {
            case "hello": HelloRows(built); break;
            case "look": LookRows(built); break;
            case "notif": NoticeRows(built); break;
            case "extras": ExtrasRows(built); break;
            case "claude": ClaudeRows(built); break;
            default: TourRows(built); break;
        }

        var parts = new List<FrameworkElement> { header };
        var cards = WrapRows(built);
        if (key == "notif")
        {
            parts.Add(cards[0]);
            parts.Add(Fold(cards.Skip(1), S.NotifyEnabled));
        }
        else parts.AddRange(cards);
        foreach (var part in parts)
        {
            page.Children.Add(part);
            if (changedStep) EnterStaggered(part, page.Children.Count - 1);
        }

        SwapPage(page, changedStep);
        _scroll.ScrollToTop();
        PaintSetupFoot(changedStep);

        TintStop(_auraCenter, color, animate);
        TintStop(_auraEdge, Color.FromArgb(0, color.R, color.G, color.B), animate);

        if (_step == TourStep)
        {
            ShowTip(0);
            _tourTimer.Start();
        }
        else
        {
            _tourTimer.Stop();
            Island?.SetupTour(-1);
        }
    }

    private void SwapPage(FrameworkElement page, bool animate)
    {
        if (!animate) _setupStage.Children.Clear();
        foreach (var old in _setupStage.Children.OfType<FrameworkElement>().ToList())
        {
            old.IsHitTestVisible = false;
            var fade = new DoubleAnimation(0, Ms(240));
            fade.Completed += (_, _) => _setupStage.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, fade);
            ((TranslateTransform)old.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-LeaveOffset * _setupDirection, Ms(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }
        _setupStage.Children.Add(page);
    }

    /// <summary>One after the other, sliding in from the side the user is moving towards.</summary>
    private void EnterStaggered(FrameworkElement element, int index)
    {
        if (element.RenderTransform is not TranslateTransform move)
        {
            move = new TranslateTransform();
            element.RenderTransform = move;
        }
        var delay = Ms(index * StaggerMs);
        double start = EnterOffset * _setupDirection;
        element.Opacity = 0;
        move.X = start;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(360)) { BeginTime = delay });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(start, 0, Ms(420)) { BeginTime = delay, EasingFunction = Spring });
    }

    private FrameworkElement SetupHeader(string key, Color color, string icon, bool animate)
    {
        var glyph = new Path
        {
            Data = Geometry.Parse(icon), Stroke = new SolidColorBrush(Ink), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new ScaleTransform(0.88, 0.88)
        };
        var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(color), Child = glyph };
        var glow = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(color), Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 22, Opacity = 0.55, Color = color } };
        var scale = new ScaleTransform(1, 1);
        var turn = new RotateTransform(0);
        var host = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new TransformGroup { Children = { scale, turn } } };
        host.Children.Add(glow);
        host.Children.Add(tile);
        if (animate)
        {
            var pop = new DoubleAnimation(0.5, 1, Ms(500)) { EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-14, 0, Ms(500)) { EasingFunction = Smooth });
        }

        var texts = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = Loc.T("setup.t." + key), FontSize = 21, FontWeight = FontWeights.Bold });
        texts.Children.Add(new TextBlock { Text = Loc.T("setup.s." + key), FontSize = 12.5, Foreground = Dim(0x80), Margin = new Thickness(0, 1, 0, 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 12) };
        row.Children.Add(host);
        row.Children.Add(texts);
        return row;
    }

    /// <summary>The rows were built the same way as the settings' ones; here each becomes its own floating card.</summary>
    private List<FrameworkElement> WrapRows(StackPanel built)
    {
        var items = built.Children.Cast<UIElement>().ToList();
        built.Children.Clear();
        var result = new List<FrameworkElement>();
        foreach (var item in items)
        {
            if (item is Border { Height: 1 }) continue; // the separators between rows
            result.Add(item is Grid row ? FloatingCard(row) : (FrameworkElement)item);
        }
        return result;
    }

    private FrameworkElement FloatingCard(Grid row)
    {
        var background = new SolidColorBrush(Color.FromArgb(0x09, 255, 255, 255));
        var edge = new SolidColorBrush(Color.FromArgb(0x0F, 255, 255, 255));
        var move = new TranslateTransform();
        var card = new Border { CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 0, 7), Background = background, BorderBrush = edge, BorderThickness = new Thickness(1), Child = row, RenderTransform = move };
        var accent = _accent;
        card.MouseEnter += (_, _) =>
        {
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2, Ms(220)) { EasingFunction = Spring });
            Tint(background, Color.FromArgb(0x0F, 255, 255, 255), true, 180);
            Tint(edge, Color.FromArgb(0x66, accent.R, accent.G, accent.B), true, 180);
        };
        card.MouseLeave += (_, _) =>
        {
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(220)) { EasingFunction = Smooth });
            Tint(background, Color.FromArgb(0x09, 255, 255, 255), true, 180);
            Tint(edge, Color.FromArgb(0x0F, 255, 255, 255), true, 180);
        };
        return card;
    }

    private FrameworkElement Fold(IEnumerable<FrameworkElement> cards, bool open)
    {
        var panel = new StackPanel();
        foreach (var card in cards) panel.Children.Add(card);
        _fold = new Border { Child = panel, ClipToBounds = true, Opacity = open ? 1 : 0, Height = open ? double.NaN : 0 };
        return _fold;
    }

    private void AnimateFold(bool open)
    {
        if (_fold is not { } fold) return;
        double from = fold.ActualHeight;
        fold.Child.Measure(new Size(fold.ActualWidth, double.PositiveInfinity));
        double to = open ? fold.Child.DesiredSize.Height : 0;
        var grow = new DoubleAnimation(from, to, Ms(360)) { EasingFunction = Smooth };
        grow.Completed += (_, _) =>
        {
            fold.BeginAnimation(HeightProperty, null);
            fold.Height = open ? double.NaN : 0;
        };
        fold.BeginAnimation(HeightProperty, grow);
        fold.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, Ms(260)));
    }

    // =====================================================================================
    // each step
    // =====================================================================================

    private void HelloRows(StackPanel card)
    {
        Row(card, Loc.T("gen.language"), Loc.T("s.language.d"),
            Segmented(new[] { ("pt", "Português"), ("en", "English") }, S.Language, v => { S.Language = v; Loc.Language = v; }));
        Row(card, Loc.T("s.startup"), Loc.T("s.startup.d"), new Switch(StartupManager.IsEnabled, StartupManager.SetEnabled, _accent));
    }

    private void LookRows(StackPanel card)
    {
        Row(card, Loc.T("s.size"), Loc.T("s.size.d"),
            Segmented(new[] { (IslandSize.Compact, Loc.T("isl.size.compact")), (IslandSize.Normal, Loc.T("isl.size.normal")), (IslandSize.Large, Loc.T("isl.size.large")) },
                S.Size, v => S.Size = v));
        Row(card, Loc.T("s.style"), Loc.T("s.style.d"),
            Segmented(new[] { (IslandStyle.Attached, Loc.T("isl.style.attached")), (IslandStyle.Floating, Loc.T("isl.style.floating")) }, S.Style, v => S.Style = v));
        Row(card, Loc.T("setup.eyes"), "", EyeSwatches());
    }

    private void NoticeRows(StackPanel card)
    {
        Row(card, Loc.T("s.notif"), Loc.T("s.notif.d"), new Switch(S.NotifyEnabled, v =>
        {
            S.NotifyEnabled = v;
            AppSettings.Commit();
            AnimateFold(v);
        }, _accent));

        var slot = new ContentControl();
        var description = Row(card, Loc.T("setup.perm"), Loc.T(Island?.NoticesAllowed == true ? "setup.perm.ok.d" : "setup.perm.d"), slot, sub: true);
        void ShowAllowButton() => slot.Content = SetupButton(Loc.T("setup.perm.b"), async () =>
        {
            slot.Content = Spinner();
            Island?.AskNotices();
            await Task.Delay(800);
            if (Island?.NoticesAllowed == true)
            {
                slot.Content = DoneChip(Loc.T("setup.perm.ok"), animate: true);
                description.Text = Loc.T("setup.perm.ok.d");
                RaiseCheer();
                return;
            }
            OpenWindowsNotificationPrivacy();
            ShowAllowButton();
        }, primary: true);
        if (Island?.NoticesAllowed == true) slot.Content = DoneChip(Loc.T("setup.perm.ok"), animate: false);
        else ShowAllowButton();

        Row(card, Loc.T("s.notif.only"), Loc.T("s.notif.only.d"), Toggle(S.NotifyOnlyNotchy, v => S.NotifyOnlyNotchy = v), sub: true);
        Row(card, Loc.T("s.notif.full"), Loc.T("s.notif.full.d"), Toggle(S.NotifyFullscreen, v => S.NotifyFullscreen = v), sub: true);
    }

    private static void OpenWindowsNotificationPrivacy()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void ExtrasRows(StackPanel card)
    {
        Row(card, Loc.T("s.calholidays"), Loc.T("s.calholidays.d"), Toggle(S.CalendarHolidays, v => S.CalendarHolidays = v));
        Row(card, Loc.T("s.so.enabled"), Loc.T("s.so.enabled.d"), Toggle(S.SoundsEnabled, v => S.SoundsEnabled = v));
        WeatherRows(card);
    }

    private void ClaudeRows(StackPanel card)
    {
        var slot = new ContentControl();
        var description = Row(card, "Claude Code", Loc.T(ClaudeHooks.IsInstalled ? "s.cl.on.d" : "s.cl.off.d"), slot);
        void ShowConnectButton() => slot.Content = SetupButton(Loc.T("cl.connect"), async () =>
        {
            slot.Content = Spinner();
            await Task.Delay(650); // long enough to see that something is happening
            try
            {
                ClaudeHooks.Install();
                slot.Content = DoneChip(Loc.T("cl.status.on"), animate: true);
                description.Text = Loc.T("s.cl.on.d");
                RaiseCheer();
            }
            catch (Exception ex)
            {
                App.Log(ex);
                description.Text = $"{Loc.T("cl.error")}: {ex.Message}";
                ShowConnectButton();
            }
        }, primary: true);
        if (ClaudeHooks.IsInstalled) slot.Content = DoneChip(Loc.T("cl.status.on"), animate: false);
        else ShowConnectButton();
        ChatEngineRows(card);
    }

    private void TourRows(StackPanel card)
    {
        _tourTips.Clear();
        for (int i = 0; i < 3; i++)
        {
            var background = new SolidColorBrush(Colors.Transparent);
            var text = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255));
            var dot = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            line.Children.Add(new TextBlock { Text = Loc.T("setup.tip." + i), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = text });
            var move = new TranslateTransform();
            card.Children.Add(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 8, 5), Background = background, Child = line, RenderTransform = move });
            _tourTips.Add(new TourTip(background, text, dot, move));
        }
    }

    private void ShowTip(int tip)
    {
        _tip = tip;
        var accent = SetupSteps[TourStep].Color;
        for (int i = 0; i < _tourTips.Count; i++)
        {
            bool on = i == tip;
            var t = _tourTips[i];
            Tint(t.Background, on ? Color.FromArgb(0x14, 255, 255, 255) : Colors.Transparent, true);
            Tint(t.Text, on ? Colors.White : Color.FromArgb(0x88, 255, 255, 255), true);
            Tint(t.Dot, on ? accent : Color.FromArgb(0x26, 255, 255, 255), true);
            t.Move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 5 : 0, Ms(300)) { EasingFunction = Spring });
        }
        Island?.SetupTour(tip);
    }

    // =====================================================================================
    // controls
    // =====================================================================================

    /// <summary>The choice with a highlight that slides from one option to the other.</summary>
    private UIElement SlidingSegmented<T>(IEnumerable<(T value, string label)> options, T current, Action<T> set)
    {
        var move = new TranslateTransform();
        var thumb = new Border { Width = 0, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = move, Background = new SolidColorBrush(_accent),
            Effect = new DropShadowEffect { Color = _accent, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.45 } };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var grid = new Grid();
        grid.Children.Add(thumb);
        grid.Children.Add(panel);
        var host = new Border { Background = Dim(0x0E), CornerRadius = new CornerRadius(10), Padding = new Thickness(2), Child = grid };
        var items = new List<(T value, Border item, SolidColorBrush text)>();
        var selected = current;

        void Place(bool animate)
        {
            foreach (var (value, _, text) in items)
                Tint(text, EqualityComparer<T>.Default.Equals(value, selected) ? Ink : Color.FromArgb(0xA8, 255, 255, 255), animate, 200);
            var on = items.First(i => EqualityComparer<T>.Default.Equals(i.value, selected)).item;
            double x = on.TranslatePoint(new Point(0, 0), panel).X;
            if (animate)
            {
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Ms(300)) { EasingFunction = Spring });
                thumb.BeginAnimation(WidthProperty, new DoubleAnimation(on.ActualWidth, Ms(300)) { EasingFunction = Smooth });
                return;
            }
            move.BeginAnimation(TranslateTransform.XProperty, null);
            thumb.BeginAnimation(WidthProperty, null);
            move.X = x;
            thumb.Width = on.ActualWidth;
        }

        foreach (var (value, label) in options)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0xA8, 255, 255, 255));
            var item = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 4, 11, 4), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = brush } };
            var option = value;
            item.MouseLeftButtonUp += (_, _) =>
            {
                SoundService.Play(Sfx.Toggle);
                selected = option;
                set(option);
                Place(animate: true);
                AppSettings.Commit();
                Cheer?.Invoke();
            };
            items.Add((value, item, brush));
            panel.Children.Add(item);
        }
        host.Loaded += (_, _) => Place(animate: false);
        return host;
    }

    private readonly List<(Ellipse Dot, string Hex)> _swatches = new();

    private void PrepareSwatches()
    {
        foreach (var (dot, _) in _swatches)
        {
            var scale = new ScaleTransform(1, 1);
            dot.RenderTransformOrigin = new Point(0.5, 0.5);
            dot.RenderTransform = scale;
            dot.MouseEnter += (_, _) => ToScale(scale, 1.2, 220);
            dot.MouseLeave += (_, _) => PaintSwatches();
        }
        PaintSwatches();
    }

    /// <summary>In the assistant the colour changes in place, so the page doesn't blink.</summary>
    private void PaintSwatches()
    {
        foreach (var (dot, hex) in _swatches)
        {
            bool selected = !S.EyesUseAlbumColor && string.Equals(S.EyeColor, hex, StringComparison.OrdinalIgnoreCase);
            var color = (Color)ColorConverter.ConvertFromString(hex);
            dot.Stroke = new SolidColorBrush(selected ? Colors.White : Color.FromArgb(0x30, 255, 255, 255));
            dot.StrokeThickness = selected ? 2.5 : 1;
            dot.Effect = selected ? new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9 } : null;
            ToScale((ScaleTransform)dot.RenderTransform, selected ? 1.14 : 1, 240);
        }
    }

    /// <summary>A button with a soft press and, on the main one, a shine that passes now and then.</summary>
    private Border SetupButton(string text, Action action, bool primary = false)
    {
        var accent = _accent;
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = primary ? new SolidColorBrush(Ink) : Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid();
        var background = new SolidColorBrush(primary ? accent : Color.FromArgb(0x14, 255, 255, 255));
        var scale = new ScaleTransform(1, 1);
        var button = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 7, 15, 7), Margin = new Thickness(8, 0, 0, 0), Background = background, Child = content,
            Cursor = System.Windows.Input.Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
        if (primary) content.Children.Add(Shine(button));
        content.Children.Add(label);

        var hover = primary ? Lighter(accent) : Color.FromArgb(0x22, 255, 255, 255);
        var rest = background.Color;
        button.MouseEnter += (_, _) =>
        {
            Tint(background, hover, true, 150);
            ToScale(scale, 1.04, 180);
        };
        button.MouseLeave += (_, _) =>
        {
            Tint(background, rest, true, 150);
            ToScale(scale, 1, 180);
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ToScale(scale, 0.95, 90);
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToScale(scale, 1, 180);
            if (button.IsMouseOver)
            {
                SoundService.Play(Sfx.Click);
                action();
            }
        };
        return button;
    }

    private static Color Lighter(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) * 0.18), (byte)(c.G + (255 - c.G) * 0.18), (byte)(c.B + (255 - c.B) * 0.18));

    private static UIElement Shine(Border button)
    {
        var move = new TranslateTransform(-70, 0);
        var streak = new Rectangle
        {
            Width = 36, Margin = new Thickness(-15, -7, -15, -7), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = move, IsHitTestVisible = false,
            Fill = new LinearGradientBrush(Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0x70, 255, 255, 255), 0) { GradientStops = { new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) } }
        };
        ((LinearGradientBrush)streak.Fill).GradientStops[1].Offset = 0.5;
        button.SizeChanged += (_, e) =>
        {
            button.Clip = new RectangleGeometry(new Rect(e.NewSize), 10, 10);
            var pass = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(3.6), RepeatBehavior = RepeatBehavior.Forever };
            pass.KeyFrames.Add(new LinearDoubleKeyFrame(-70, KeyTime.FromPercent(0)));
            pass.KeyFrames.Add(new EasingDoubleKeyFrame(e.NewSize.Width + 40, KeyTime.FromPercent(0.26), new SineEase { EasingMode = EasingMode.EaseInOut }));
            pass.KeyFrames.Add(new LinearDoubleKeyFrame(e.NewSize.Width + 40, KeyTime.FromPercent(1)));
            move.BeginAnimation(TranslateTransform.XProperty, pass);
        };
        return streak;
    }

    private UIElement Spinner()
    {
        var turn = new RotateTransform();
        var ring = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(_accent), StrokeThickness = 2.2, StrokeDashArray = new DoubleCollection { 4, 2.6 }, Margin = new Thickness(18, 4, 18, 4),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn };
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.7)) { RepeatBehavior = RepeatBehavior.Forever });
        return ring;
    }

    private static UIElement DoneChip(string text, bool animate)
    {
        var check = new Path { Data = Geometry.Parse("M1.5,7 L5.5,11 L13,2"), Stroke = new SolidColorBrush(DoneStep), StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, StrokeDashArray = new DoubleCollection { 9, 9 }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), StrokeDashOffset = animate ? 9 : 0 };
        var chip = new StackPanel { Orientation = Orientation.Horizontal, RenderTransformOrigin = new Point(0.5, 0.5) };
        chip.Children.Add(check);
        chip.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(DoneStep), VerticalAlignment = VerticalAlignment.Center });
        if (animate)
        {
            var scale = new ScaleTransform(0.6, 0.6);
            chip.RenderTransform = scale;
            ToScale(scale, 1, 400);
            check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(9, 0, Ms(400)) { BeginTime = Ms(100) });
        }
        return chip;
    }

    private void PaintSetupFoot(bool animate)
    {
        bool last = _step == TourStep;
        _setupCount.Text = Loc.F("setup.count", _step + 1, SetupSteps.Length);
        double progress = (_step + 1.0) / SetupSteps.Length;
        if (animate) _setupProgress.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(progress, Ms(420)) { EasingFunction = Smooth });
        else
        {
            _setupProgress.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _setupProgress.ScaleX = progress;
        }
        Tint(_setupProgressBrush, _accent, animate, 450);

        _setupButtons.Children.Clear();
        if (!last) _setupButtons.Children.Add(SetupButton(Loc.T("setup.skip"), () => GoSetup(TourStep)));
        if (_step > 0) _setupButtons.Children.Add(SetupButton(Loc.T("setup.back"), () => GoSetup(_step - 1)));
        _setupButtons.Children.Add(SetupButton(Loc.T(last ? "setup.start" : "setup.next"), () =>
        {
            if (!last)
            {
                GoSetup(_step + 1);
                return;
            }
            _celebrate = true;
            Close();
        }, primary: true));
    }
}
