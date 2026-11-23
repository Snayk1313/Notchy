using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

/// <summary>
/// Copiados: the clipboard history in its own tab (search, filter by kind, pinned on top, hover a picture to see it bigger,
/// click to copy again, swipe to delete with "Desfazer"), and the closed island's outline glowing a moment when something is copied.
/// </summary>
public partial class MainWindow
{
    private static readonly Color ClipColor = Color.FromRgb(0x8F, 0xB8, 0xFF);
    private const double ClipTabHeight = 240, ClipListHeight = 180;
    private FrameworkElement? _clipScroller;

    private readonly ClipboardService _clip = new();
    private string _clipQuery = "";
    private ClipKind? _clipFilter;          // null: everything (colours count as text)
    private StackPanel _clipList = null!;
    private StackPanel _clipChips = null!;
    private readonly List<(ClipKind? kind, string key, Border chip, TextBlock text)> _clipChipList = new();
    private Border _clipChipPill = null!;
    private Border? _clipClear;
    private ClipKind? _clipPillAt;
    private bool _clipPillPlaced;
    private Border _clipPause = null!;
    private DockPanel _clipTop = null!;
    private Canvas _clipOver = null!;        // the bigger picture on hover
    private List<ClipItem>? _clipCleared;
    private string _clipClearedText = "";
    private DateTime _clipUndoUntil;
    private DispatcherTimer? _clipUndoTimer;
    private System.Windows.Shapes.Path? _clipLight;
    private readonly IslandOutline _clipOutline = new();

    internal ClipboardService ClipHistory => _clip;

    // opened by the shortcut: the keyboard drives it (↑↓ choose, Enter pastes into the app that was in front, Esc closes)
    private TextBox _clipBox = null!;
    private TextBlock _clipKeysHint = null!;
    private readonly List<(ClipItem item, Border row, Border pin)> _clipRows = new();
    private int _clipSel = -1;
    private bool _clipKeys;
    private IntPtr _clipTarget;
    private bool ClipKeepsOpen => _clipKeys;

    private void InitClipboard()
    {
        var grid = ClipTab;
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        grid.RowDefinitions.Add(new RowDefinition());

        // ---- search · pause · clear
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White, FontSize = 11.5,
            Padding = new Thickness(24, 5, 6, 5), FocusVisualStyle = null, SelectionBrush = Br(ClipColor), VerticalAlignment = VerticalAlignment.Center
        };
        MakeTypable(box, null);
        _clipBox = box;
        box.TextChanged += (_, _) =>
        {
            _clipQuery = box.Text.Trim();
            if (_clipKeys) _clipSel = 0;
            RenderClip();
        };
        box.PreviewKeyDown += ClipKey;
        _clipTop = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        _clipPause = new Border();
        var search = SearchField(box, Loc.T("clip.search"));
        search.VerticalAlignment = VerticalAlignment.Center;
        _clipKeysHint = new TextBlock { Text = Loc.T("clip.keys"), FontSize = 10, Foreground = White(0x66), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        ((Grid)search.Child).Children.Add(_clipKeysHint);
        _clipTop.Children.Add(search);
        grid.Children.Add(_clipTop);

        // ---- Tudo · Texto · Imagens · Links · Arquivos
        _clipChips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 4) };
        _clipChipPill = new Border { CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x38, ClipColor.R, ClipColor.G, ClipColor.B)),
            BorderBrush = new SolidColorBrush(ClipColor), BorderThickness = new Thickness(1.3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = new TranslateTransform(), Opacity = 0, IsHitTestVisible = false };
        Grid.SetRow(_clipChipPill, 1);
        Grid.SetRow(_clipChips, 1);
        grid.Children.Add(_clipChipPill);
        grid.Children.Add(_clipChips);

        _clipList = new StackPanel();
        var scroller = _clipScroller = Scroller(_clipList, height: ClipListHeight);
        Grid.SetRow(scroller, 2);
        grid.Children.Add(scroller);

        _clipOver = new Canvas { IsHitTestVisible = false, ClipToBounds = false };
        Grid.SetRowSpan(_clipOver, 3);
        grid.Children.Add(_clipOver);

        _clip.Changed += () => { if (_tab == Tab.Clip && _state == IslandState.Expanded) RenderClip(flip: true); };
        _clip.Copied += OnClipCopied;
        _clip.HotkeyPressed += OpenClipFromKeys;
        Deactivated += (_, _) =>
        {
            // another app in front (a click outside, Alt+Tab, or the paste going back): the keyboard session is over
            if (_clipTakingFront) return; // (taking the front from Explorer flickers through a deactivation)
            _clipKeys = false;
            _clipKeysHint.Visibility = Visibility.Collapsed;
        };
        try { _clip.Start(); }
        catch (Exception ex) { App.Log(ex); }
        RenderClip();
    }

    private void ApplyClipSettings()
    {
        if (_nav.TryGetValue(Tab.Clip, out var n))
            n.button.Visibility = S.ClipEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!S.ClipEnabled && _tab == Tab.Clip) SelectTab(Tab.Home, animate: false);
        _clip.SettingsChanged();
        _clip.SetHotkey(S.ClipHotkey);
    }

    // ---------------------------------------------------------------- the tab

    private void RenderClip(bool flip = false)
    {
        if (_clipList == null) return;
        var before = flip && _clipList.IsVisible ? ListGlide.Snap(_clipList) : null;
        DrawClip();
        if (before != null) ListGlide.Play(_clipList, before, Dur(300));
    }

    private void DrawClip()
    {
        _clipOver.Children.Clear();

        // the top line's pills (their text follows pause and language)
        _clipTop.Children.Remove(_clipPause);
        _clipPause = ListPill(S.ClipPaused ? "" : "", Loc.T(S.ClipPaused ? "clip.resume" : "clip.pause"), S.ClipPaused, () =>
        {
            S.ClipPaused = !S.ClipPaused;
            AppSettings.Commit();
            RenderClip();
        });
        _clipPause.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(_clipPause, Dock.Right);
        _clipTop.Children.Insert(0, _clipPause);

        // the filters: built once; the white highlight slides under the chosen one
        if (_clipChipList.Count == 0)
        {
            foreach (var (kind, key) in new (ClipKind?, string)[] { (null, "clip.f.all"), (ClipKind.Text, "clip.f.text"), (ClipKind.Image, "clip.f.image"), (ClipKind.Link, "clip.f.link"), (ClipKind.Files, "clip.f.files") })
            {
                var text = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
                var chip = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 5, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Child = text };
                var k = kind;
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    SoundService.Play(Sfx.Click);
                    _clipFilter = k;
                    RenderClip();
                };
                _clipChips.Children.Add(chip);
                _clipChipList.Add((kind, key, chip, text));
            }
        }
        foreach (var (kind, key, chip, text) in _clipChipList)
        {
            bool on = _clipFilter == kind;
            chip.Background = on ? Brushes.Transparent : White(0x12);
            text.Text = Loc.T(key);
            text.Foreground = on ? Brushes.White : White(0xB0);
        }
        bool filterChanged = _clipPillAt != _clipFilter && _clipPillPlaced;
        Dispatcher.BeginInvoke(() => MoveClipPill(filterChanged), System.Windows.Threading.DispatcherPriority.Loaded);
        if (filterChanged) _clipList.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(180))));

        if (_clipClear != null) _clipChips.Children.Remove(_clipClear);
        _clipClear = null;
        if (_clip.Unpinned.Count > 0)
        {
            _clipClear = ListPill("\uE74D", Loc.T("clip.clear"), false, () =>
            {
                var gone = _clip.Unpinned;
                ClearClip(gone, Loc.F("clip.cleared", gone.Count));
            });
            _clipClear.Padding = new Thickness(8, 2, 9, 3);
            _clipClear.Margin = new Thickness(8, 0, 0, 0);
            _clipChips.Children.Add(_clipClear); // (after the chips, a little apart)
        }

        _clipList.Children.Clear();
        _clipRows.Clear();
        if (_clipCleared != null) _clipList.Children.Add(ListGlide.Key(ClipUndoRow(), "undo"));
        var shown = _clip.Items.Where(Matches).ToList();
        if (shown.Count == 0)
        {
            string key = S.ClipPaused ? "clip.paused" : _clip.Items.Count == 0 ? "clip.empty" : "clip.none";
            _clipList.Children.Add(new TextBlock { Text = Loc.T(key), FontSize = 11.5, Foreground = White(0x80), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 30, 20, 0) });
            return;
        }
        var pinned = shown.Where(x => x.Pinned).ToList();
        if (pinned.Count > 0)
        {
            _clipList.Children.Add(ListGlide.Key(ClipSection(Loc.T("clip.pinned")), "pinned"));
            foreach (var x in pinned) _clipList.Children.Add(ClipRow(x));
            _clipList.Children.Add(ListGlide.Key(ClipSection(Loc.T("clip.recent")), "recent"));
        }
        // shortcut: draws at most 80 rows; search reaches the rest
        foreach (var x in shown.Where(x => !x.Pinned).Take(80)) _clipList.Children.Add(ClipRow(x));
        MarkClipSel(scroll: false);
    }

    private bool Matches(ClipItem x)
    {
        if (_clipFilter is ClipKind f && !(x.Kind == f || f == ClipKind.Text && x.Kind == ClipKind.Color)) return false;
        return _clipQuery.Length == 0 || x.Text.Contains(_clipQuery, StringComparison.CurrentCultureIgnoreCase) || x.App.Contains(_clipQuery, StringComparison.CurrentCultureIgnoreCase)
               || ClipTitle(x).Contains(_clipQuery, StringComparison.CurrentCultureIgnoreCase)
               || (x.Ocr ?? "").Contains(_clipQuery, StringComparison.CurrentCultureIgnoreCase);
    }

    private static TextBlock ClipSection(string text) =>
        new() { Text = text, FontSize = 10, Foreground = White(0x77), Margin = new Thickness(6, 6, 0, 3) };

    private static string ClipTitle(ClipItem x) => x.Kind switch
    {
        ClipKind.Image => Loc.F("clip.image", x.Width, x.Height),
        ClipKind.Files => x.Files.Length == 1 ? System.IO.Path.GetFileName(x.Files[0].TrimEnd('\\')) : Loc.F("clip.files", System.IO.Path.GetFileName(x.Files[0].TrimEnd('\\')), x.Files.Length - 1),
        ClipKind.Link when Uri.TryCreate(x.Text, UriKind.Absolute, out var u) => u.Host.Replace("www.", "") + u.PathAndQuery.TrimEnd('/'),
        _ => string.Join(" ", x.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
    };

    private static string ClipAgo(DateTime t)
    {
        var span = DateTime.Now - t;
        return span.TotalMinutes < 1 ? Loc.T("notif.now")
            : span.TotalHours < 1 ? Loc.F("ext.ago.min", (int)span.TotalMinutes)
            : t.Date == DateTime.Today ? FormatClock(t)
            : t.Date == DateTime.Today.AddDays(-1) ? Loc.T("clip.yesterday")
            : t.ToString("d MMM", Loc.Culture);
    }

    /// <summary>The little square before each item: the picture, the colour, or the kind's sign.</summary>
    private FrameworkElement ClipThumb(ClipItem x)
    {
        var box = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, Background = White(0x12), ClipToBounds = true };
        string glyph = "";
        switch (x.Kind)
        {
            case ClipKind.Image when x.Thumb is { } thumb:
                box.Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill };
                break;
            case ClipKind.Color:
                var c = ClipColorOf(x.Text);
                box.Background = White(0x12);
                box.Child = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(c), Stroke = White(0x50), StrokeThickness = 1.5 };
                break;
            case ClipKind.Link: glyph = ""; box.Background = new SolidColorBrush(Color.FromArgb(0x40, ClipColor.R, ClipColor.G, ClipColor.B)); break;
            case ClipKind.Files: glyph = x.Files.Length == 1 && Directory.Exists(x.Files[0]) ? "" : ""; break;
            default: glyph = ""; break; // (text)
        }
        if (glyph.Length > 0)
            box.Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = White(0xDD), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return box;
    }

    private static Color ClipColorOf(string text)
    {
        try
        {
            if (text.StartsWith('#')) return (Color)ColorConverter.ConvertFromString(text.Length == 9 ? "#" + text[7..9] + text[1..7] : text); // (CSS #RRGGBBAA → #AARRGGBB)
            var n = text[(text.IndexOf('(') + 1)..text.IndexOf(')')].Split(',').Select(s => s.Trim()).ToArray();
            return Color.FromRgb(byte.Parse(n[0]), byte.Parse(n[1]), byte.Parse(n[2]));
        }
        catch
        {
            return Colors.Gray;
        }
    }

    private Border ClipRow(ClipItem x)
    {
        var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = ClipTitle(x), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        string kind = Loc.T(x.Kind switch { ClipKind.Image => "clip.k.image", ClipKind.Link => "clip.k.link", ClipKind.Color => "clip.k.color", ClipKind.Files => "clip.k.files", _ => "clip.k.text" });
        string withText = x.Kind == ClipKind.Image && !string.IsNullOrWhiteSpace(x.Ocr) ? Loc.T("clip.hastext") : "";
        var sub = new TextBlock { Text = string.Join(" · ", new[] { kind, withText, x.App, ClipAgo(x.Time) }.Where(s => s.Length > 0)), FontSize = 10, Foreground = White(0x80), TextTrimming = TextTrimming.CharacterEllipsis };
        texts.Children.Add(sub);

        // pin: always shown when pinned, otherwise on hover
        var pin = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            Opacity = x.Pinned ? 1 : 0, ToolTip = Loc.T(x.Pinned ? "clip.unpin" : "clip.pin"),
            Child = new TextBlock { Text = x.Pinned ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11,
                Foreground = x.Pinned ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)) : White(0xCC), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        pin.MouseEnter += (_, _) => pin.Background = White(0x18);
        pin.MouseLeave += (_, _) => pin.Background = Brushes.Transparent;
        pin.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _clip.TogglePin(x);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(pin, 2);
        grid.Children.Add(ClipThumb(x));
        grid.Children.Add(texts);
        grid.Children.Add(pin);
        var row = new Border { Child = grid, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = x.Kind is ClipKind.Text && x.Text.Length > 60 ? x.Text[..Math.Min(400, x.Text.Length)]
                : x.Kind is ClipKind.Image && !string.IsNullOrWhiteSpace(x.Ocr) ? x.Ocr[..Math.Min(400, x.Ocr.Length)] : null };
        _clipRows.Add((x, row, pin));
        row.Tag = new GlideKey(x);
        bool Selected() => _clipSel >= 0 && _clipSel < _clipRows.Count && _clipRows[_clipSel].row == row;
        row.MouseEnter += (_, _) =>
        {
            if (!Selected()) row.Background = White(0x0E);
            if (!x.Pinned) pin.Opacity = 1;
            if (x.Kind == ClipKind.Image) ShowClipPicture(x, row);
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Selected() ? White(0x1C) : Brushes.Transparent;
            if (!x.Pinned && !Selected()) pin.Opacity = 0;
            _clipOver.Children.Clear();
        };
        row.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _clipOver.Children.Clear();
            if (_clipKeys && _clipTarget != IntPtr.Zero)
            {
                PasteClip(x); // (opened by the shortcut: a click pastes too)
                return;
            }
            if (await _clip.CopyBack(x))
            {
                FaceReact(Mood.Happy, 0.6);
                FlashClipCopied(); // (the list is drawn again with it on top)
            }
            else sub.Text = Loc.T(_clip.LastFailGone ? "clip.gone" : "clip.busy");
        };
        SwipeToClear(row, () => new[] { row }, () => ClearClip(new List<ClipItem> { x }, Loc.T("clip.deleted"), slide: false));
        return row;
    }

    /// <summary>"Copiado" for a moment in the header's place of the first row.</summary>
    private void FlashClipCopied()
    {
        if (_clipList.Children.OfType<Border>().FirstOrDefault(b => b.Child is Grid) is not { Child: Grid g } || g.Children[1] is not StackPanel { Children: [_, TextBlock sub] }) return;
        sub.Text = "✓ " + Loc.T("clip.copied");
        sub.Foreground = new SolidColorBrush(ClipColor);
    }

    /// <summary>The picture bigger, above (or below) the row, inside the island.</summary>
    private void ShowClipPicture(ClipItem x, FrameworkElement row)
    {
        var img = ClipboardService.LoadImage(x.ImagePath, 480);
        if (img == null) return;
        double maxW = 220, maxH = 120;
        double k = Math.Min(maxW / img.PixelWidth, maxH / img.PixelHeight);
        double w = Math.Max(40, img.PixelWidth * k), h = Math.Max(30, img.PixelHeight * k);
        var big = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(10), Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill },
            BorderBrush = White(0x30), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.7 }, RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(0.85, 0.85), Opacity = 0
        };
        var at = row.TranslatePoint(new Point(0, 0), _clipOver);
        double top = at.Y + row.ActualHeight / 2 - h / 2;
        top = Math.Clamp(top, 0, Math.Max(0, ClipTabHeight - h));
        Canvas.SetLeft(big, Math.Min(ActualWidth, 480) - w - 70);
        Canvas.SetTop(big, top);
        _clipOver.Children.Add(big);
        var d = TimeSpan.FromMilliseconds(Dur(180));
        big.BeginAnimation(OpacityProperty, new DoubleAnimation(1, d));
        var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
        ((ScaleTransform)big.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
        ((ScaleTransform)big.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
    }

    // ---------------------------------------------------------------- delete, undo

    private async void ClearClip(List<ClipItem> gone, string text, bool slide = true)
    {
        if (gone.Count == 0) return;
        if (slide)
        {
            var rows = _clipList.Children.OfType<Border>().Where(b => b.Child is Grid).ToList();
            double stagger = Dur(30), fade = Dur(180);
            for (int i = 0; i < Math.Min(rows.Count, 8); i++) SlideRowAway(rows[i], TimeSpan.FromMilliseconds(i * stagger), TimeSpan.FromMilliseconds(fade));
            await System.Threading.Tasks.Task.Delay((int)(Math.Min(rows.Count, 8) * stagger + fade));
        }
        EndClipUndo(); // (a new delete makes the one before final)
        _clipCleared = gone;
        _clipClearedText = text;
        _clipUndoUntil = DateTime.UtcNow.AddSeconds(NoticeUndoSeconds);
        _clipUndoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(NoticeUndoSeconds) };
        _clipUndoTimer.Tick += (_, _) => EndClipUndo();
        _clipUndoTimer.Start();
        _clip.Remove(gone); // (draws the list again, with the undo line on top)
    }

    private void EndClipUndo()
    {
        _clipUndoTimer?.Stop();
        if (_clipCleared == null) return;
        ClipboardService.Discard(_clipCleared.Where(x => !_clip.Items.Contains(x)));
        _clipCleared = null;
        RenderClip(flip: true);
    }

    private FrameworkElement ClipUndoRow()
    {
        var grid = new Grid { Height = 38, Margin = new Thickness(4, 0, 2, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = _clipClearedText, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        var scale = new ScaleTransform(1, 1);
        left.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 6, 14, 0), Background = White(0xCC), RenderTransform = scale });
        double secondsLeft = Math.Clamp((_clipUndoUntil - DateTime.UtcNow).TotalSeconds, 0, NoticeUndoSeconds);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(secondsLeft / NoticeUndoSeconds, 0, TimeSpan.FromSeconds(secondsLeft)));
        grid.Children.Add(left);
        var undo = ListPill("", Loc.T("dl.undo"), false, () =>
        {
            var back = _clipCleared;
            _clipCleared = null;
            _clipUndoTimer?.Stop();
            if (back != null) _clip.Restore(back);
            else RenderClip(flip: true);
        });
        Grid.SetColumn(undo, 1);
        grid.Children.Add(undo);
        return grid;
    }

    // ---------------------------------------------------------------- copied: the outline glows, the eyes blink

    private void OnClipCopied(ClipItem item)
    {
        if (_state is not (IslandState.Idle or IslandState.Compact) || IslandLightsOff || _hiddenForFullscreen || _introActive) return;
        // a capture copied it: its own light comes with the picture into the island
        if (_capturing || DateTime.UtcNow < _quietClipUntil || ShotShown != null) return;
        var color = item.Kind == ClipKind.Color ? ClipColorOf(item.Text) : ClipColor;
        GlowOutline(color);
        BlinkOnce();
    }

    private void GlowOutline(Color color)
    {
        double W = Island.ActualWidth, H = Island.ActualHeight - _topHidden;
        if (W < 2 || H < 2) return;
        if (_clipLight == null)
        {
            _clipLight = new System.Windows.Shapes.Path
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, StrokeThickness = 2.2,
                StrokeLineJoin = PenLineJoin.Round, RenderTransformOrigin = new Point(0.5, 0), Opacity = 0,
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 1 }
            };
            var parent = (Panel)TransferLight.Parent;
            parent.Children.Insert(parent.Children.IndexOf(TransferLight) + 1, _clipLight);
        }
        _clipOutline.Build(W, H, _topHidden, OutlineTurn);
        _clipLight.Width = W;
        _clipLight.Height = H + _topHidden;
        _clipLight.Data = _clipOutline.Segment(0, _clipOutline.Length);
        HangLikeIsland(_clipLight); // (wherever the island is: the top, or a side)
        _clipLight.RenderTransform = Island.RenderTransform;
        _clipLight.Stroke = new SolidColorBrush(color);
        ((DropShadowEffect)_clipLight.Effect).Color = color;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(Dur(900)) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.15), new CubicEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.35)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));
        _clipLight.BeginAnimation(OpacityProperty, a);
    }

    private DateTime _quietClipUntil;

    /// <summary>Every frame while the copy light is on: it keeps the island's shape if the island changes size.</summary>
    private void UpdateClipLight()
    {
        if (_clipLight == null || _clipLight.Opacity < 0.01) return;
        double W = Island.ActualWidth, H = Island.ActualHeight - _topHidden;
        if (W < 2 || H < 2 || !_clipOutline.Build(W, H, _topHidden, OutlineTurn)) return;
        _clipLight.Width = W;
        _clipLight.Height = H + _topHidden;
        _clipLight.Data = _clipOutline.Segment(0, _clipOutline.Length);
        HangLikeIsland(_clipLight); // (wherever the island is: the top, or a side)
    }

    private void BlinkOnce()
    {
        if (_blinkPaused) return; // (sleepy: the eyes are closed already)
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
        a.Completed += (_, _) => { if (!_blinkPaused) StartBlinking(); };
        _blink.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ---------------------------------------------------------------- the keyboard

    /// <summary>The shortcut: open on Copiados with the search box taking the keys (pressed again: closes).</summary>
    private async void OpenClipFromKeys(IntPtr front)
    {
        if (!S.ClipEnabled || FullscreenHidesIsland) return;
        if (_clipKeys && _state == IslandState.Expanded)
        {
            EndClipKeys();
            return;
        }
        _clipTarget = front == _hwnd ? IntPtr.Zero : front;
        _clipField = null;
        var field = _clipTarget != IntPtr.Zero ? ClipboardService.FocusedAsync() : null;
        _clipKeys = true;
        _clipSel = 0;
        _clipFilter = null;
        _clipBox.Text = ""; // (draws the list)
        _clickOpened = true;
        _hoverRequested = false;
        UpdateState();
        if (_tab != Tab.Clip) SelectTab(Tab.Clip, animate: false);
        RenderClip();
        _clipKeysHint.Visibility = Visibility.Visible;
        // which control had the focus is read before the island takes it (a short wait at most)
        if (field != null && await System.Threading.Tasks.Task.WhenAny(field, System.Threading.Tasks.Task.Delay(250)) == field) _clipField = field.Result;
        if (!_clipKeys) return; // (closed meanwhile)
        if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
        // (over Explorer, Activate alone is refused and the keys stay there)
        _clipTakingFront = true;
        try { if (_hwnd != IntPtr.Zero) Native.FocusWindow(_hwnd); }
        finally { _clipTakingFront = false; }
        Activate();
        _clipBox.Focus();
        Keyboard.Focus(_clipBox);
    }

    private void EndClipKeys()
    {
        _clipKeys = false;
        _clipTarget = IntPtr.Zero;
        _clipField = null;
        _clipSel = -1;
        _clipKeysHint.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
        if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
        Collapse();
    }

    private void ClipKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        ClipItem? sel = _clipSel >= 0 && _clipSel < _clipRows.Count ? _clipRows[_clipSel].item : null;
        switch (e.Key)
        {
            case Key.Down or Key.Up:
                if (_clipRows.Count == 0) break;
                _clipSel = Math.Clamp(_clipSel + (e.Key == Key.Down ? 1 : -1), 0, _clipRows.Count - 1);
                MarkClipSel(scroll: true);
                break;
            case Key.Enter:
                if (sel == null) break;
                if (_clipTarget != IntPtr.Zero) PasteClip(sel);
                else CopyClip(sel);
                break;
            case Key.Escape:
                if (_clipBox.Text.Length > 0) _clipBox.Text = "";
                else if (_clipKeys) EndClipKeys();
                else Keyboard.ClearFocus();
                break;
            case Key.Tab:
                // the next (Shift: previous) filter
                ClipKind?[] order = { null, ClipKind.Text, ClipKind.Image, ClipKind.Link, ClipKind.Files };
                int i = Array.IndexOf(order, _clipFilter);
                _clipFilter = order[(i + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? order.Length - 1 : 1)) % order.Length];
                _clipSel = 0;
                RenderClip();
                break;
            case Key.P when ctrl:
                if (sel != null) _clip.TogglePin(sel);
                break;
            case Key.Delete when ctrl:
                if (sel != null) ClearClip(new List<ClipItem> { sel }, Loc.T("clip.deleted"), slide: false);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Lights the chosen row (and keeps it in sight).</summary>
    private void MarkClipSel(bool scroll)
    {
        if (_clipKeys && _clipRows.Count > 0) _clipSel = Math.Clamp(_clipSel, 0, _clipRows.Count - 1);
        for (int i = 0; i < _clipRows.Count; i++)
        {
            var (item, row, pin) = _clipRows[i];
            bool on = i == _clipSel;
            row.Background = on ? White(0x1C) : Brushes.Transparent;
            pin.Opacity = on || item.Pinned ? 1 : 0;
            if (on && scroll) row.BringIntoView(new Rect(0, -6, row.ActualWidth, row.ActualHeight + 12));
        }
    }

    /// <summary>Enter: copied, the island closes, and Ctrl+V goes to the app that was in front.</summary>
    private bool _clipTakingFront;
    private bool _clipPasting; // (a second Enter while the clipboard is busy doesn't paste twice)
    private System.Windows.Automation.AutomationElement? _clipField; // the control that had the focus when the shortcut came

    private async void PasteClip(ClipItem item)
    {
        if (_clipPasting) return;
        var target = _clipTarget;
        var field = _clipField;
        _clipPasting = true;
        bool copied;
        try { copied = await _clip.CopyBack(item); }
        finally { _clipPasting = false; }
        if (!copied)
        {
            if (_clipRows.FirstOrDefault(r => r.item == item).row?.Child is Grid { Children: [_, StackPanel { Children: [_, TextBlock sub] }, ..] }) sub.Text = Loc.T(_clip.LastFailGone ? "clip.gone" : "clip.busy");
            return;
        }
        if (!_clipKeys || _clipTarget != target) return; // (closed meanwhile: Esc or the shortcut again)
        // the app goes back to the front while the island still has it (Windows allows it then), then the island closes
        var paste = ClipboardService.PasteIntoAsync(target, field);
        EndClipKeys();
        await paste;
    }

    private async void CopyClip(ClipItem item)
    {
        if (await _clip.CopyBack(item)) FlashClipCopied();
    }

    /// <summary>The white highlight goes under the chosen filter (sliding there when it changes).</summary>
    private void MoveClipPill(bool animate)
    {
        var chip = _clipChipList.FirstOrDefault(c => c.kind == _clipFilter).chip;
        if (chip == null || chip.ActualWidth < 1) return;
        // (the highlight shares the filters' row and margin: placed from the row's corner)
        var at = chip.TranslatePoint(new Point(0, 0), _clipChips);
        _clipChipPill.Margin = new Thickness(_clipChips.Margin.Left, _clipChips.Margin.Top, 0, 0);
        var tt = (TranslateTransform)_clipChipPill.RenderTransform;
        _clipChipPill.Height = chip.ActualHeight;
        _clipChipPill.Opacity = 1;
        _clipPillAt = _clipFilter;
        if (!animate || !_clipPillPlaced)
        {
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            _clipChipPill.BeginAnimation(WidthProperty, null);
            tt.X = at.X;
            tt.Y = at.Y;
            _clipChipPill.Width = chip.ActualWidth;
            _clipPillPlaced = true;
            return;
        }
        var d = TimeSpan.FromMilliseconds(Dur(300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        tt.Y = at.Y;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(at.X, d) { EasingFunction = ease });
        _clipChipPill.BeginAnimation(WidthProperty, new DoubleAnimation(chip.ActualWidth, d) { EasingFunction = ease });
    }
}
