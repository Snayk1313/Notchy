using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Notchy;

// Marking several files on the Shelf and in Downloads, and dragging them out together. Three ways to mark, one result:
// Ctrl/Shift + click, the "Selecionar" mode, and the lasso (from an empty spot, or from anywhere with Ctrl).
public partial class MainWindow
{
    private FileSelector _shelfSel = null!, _dlSel = null!;
    private Border _selPill = null!;
    private TextBlock _selPillText = null!;
    private TextBlock? _dlSelectText, _dlAllText;
    private FrameworkElement? _dlAllLink;
    private bool _lassoing; // keeps the island open while you draw, even if the mouse wanders off it

    private void InitFileSelect()
    {
        DownloadsTab.Background = Brushes.Transparent; // the gaps between rows can start a lasso too
        _shelfSel = new FileSelector(this, ShelfDropZone, ShelfItems, ShelfScroll, ShelfTab, ShelfColor);
        _dlSel = new FileSelector(this, DownloadsTab, DownloadsList, DownloadsScroll, DownloadsTab, DoneGreen);

        _selPillText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        pillRow.Children.Add(_selPillText);
        pillRow.Children.Add(new TextBlock { Text = "✕", FontSize = 9, Opacity = 0.65, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _selPill = new Border
        {
            Child = pillRow, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2.5, 8, 2.5),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Visibility = Visibility.Collapsed
        };
        _selPill.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            ActiveSelector?.ClearMarks();
        };
        // in the middle of the header, in place of the date while files are marked (the right side is full)
        ((Panel)HeaderClaudeDot.Parent).Children.Add(_selPill);
    }

    private FileSelector? ActiveSelector => _tab switch { Tab.Shelf => _shelfSel, Tab.Downloads => _dlSel, _ => null };

    /// <summary>Marks and the "Selecionar" mode don't outlive the tab or the open island.</summary>
    private void ResetFileSelection()
    {
        if (_shelfSel == null || _draggingOut) return;
        _shelfSel.Reset();
        _dlSel.Reset();
    }

    /// <summary>The pill in the header, the buttons and the hint follow what's marked.</summary>
    private void UpdateSelChrome()
    {
        if (_shelfSel == null) return;
        var s = ActiveSelector;
        int n = s?.Marked.Count ?? 0;
        if (n > 0 && s != null)
        {
            _selPillText.Text = n == 1 ? Loc.T("sel.one") : Loc.F("sel.count", n);
            var c = s.Color;
            _selPillText.Foreground = new SolidColorBrush(c);
            ((TextBlock)((Panel)_selPill.Child).Children[1]).Foreground = new SolidColorBrush(c);
            _selPill.Background = new SolidColorBrush(Color.FromArgb(0x24, c.R, c.G, c.B));
        }
        _selPill.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        DatePill.Visibility = HeaderTime.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;

        bool shelfMode = _shelfSel.Mode;
        ShelfSelect.Visibility = _shelf.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
        ShelfSelectText.Text = Loc.T(shelfMode ? "sel.done" : "sel.select");
        ShelfSelectText.Foreground = shelfMode ? new SolidColorBrush(ShelfColor) : new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255));
        ShelfClearText.Text = Loc.T(shelfMode ? "sel.all" : "shelf.clear");
        ShelfHint.Text = Loc.T(_shelfSel.Marked.Count > 0 ? "shelf.hint.sel" : "shelf.hint");

        if (_dlSelectText != null)
        {
            _dlSelectText.Text = Loc.T(_dlSel.Mode ? "sel.done" : "sel.select");
            _dlSelectText.Foreground = _dlSel.Mode ? new SolidColorBrush(DoneGreen) : new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
        }
        if (_dlAllText != null) _dlAllText.Text = Loc.T("sel.all");
        if (_dlAllLink != null) _dlAllLink.Visibility = _dlSel.Mode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShelfSelect_Click(object sender, RoutedEventArgs e)
    {
        SoundService.Play(Sfx.Click);
        _shelfSel.SetMode(!_shelfSel.Mode);
    }

    // ---- pieces shared by the two lists ----------------------------------------------------

    /// <summary>The ✓ of a marked file, or an empty ring in the "Selecionar" mode.</summary>
    private (FrameworkElement host, Action<bool, bool> paint) SelBadge(Color color, Color ink)
    {
        var ring = new Ellipse { Stroke = new SolidColorBrush(Color.FromArgb(0x66, 255, 255, 255)), StrokeThickness = 1.5, Opacity = 0 };
        var scale = new ScaleTransform(0, 0);
        var check = new Grid { RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale };
        check.Children.Add(new Ellipse { Fill = new SolidColorBrush(color) });
        check.Children.Add(new TextBlock
        {
            Text = "✓", FontSize = 10, FontWeight = FontWeights.ExtraBold, Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        var host = new Grid { Width = 16, Height = 16, IsHitTestVisible = false };
        host.Children.Add(ring);
        host.Children.Add(check);

        bool? lastMarked = null, lastRing = null;
        void Paint(bool marked, bool mode)
        {
            bool showRing = mode && !marked;
            if (lastRing != showRing)
            {
                lastRing = showRing;
                ring.BeginAnimation(OpacityProperty, new DoubleAnimation(showRing ? 1 : 0, TimeSpan.FromMilliseconds(Dur(160))));
            }
            if (lastMarked != marked)
            {
                lastMarked = marked;
                var pop = new DoubleAnimation(marked ? 1 : 0, TimeSpan.FromMilliseconds(Dur(marked ? 240 : 120)));
                if (marked) pop.EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }
        return (host, Paint);
    }

    /// <summary>Hands the files to whatever they're dropped on; two or more travel as a little pile with a count.</summary>
    private void DragFilesOut(FrameworkElement source, string[] paths, Action after)
    {
        var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (files.Length == 0) return;

        _draggingOut = true;
        DragGhost? ghost = null;
        GiveFeedbackEventHandler follow = (_, _) => ghost?.FollowCursor();
        try
        {
            if (files.Length > 1)
            {
                ghost = new DragGhost(files.Take(3).Select(Thumb).ToList(), files.Length);
                ghost.Show();
                ghost.FollowCursor();
                source.GiveFeedback += follow;
            }
            DragDrop.DoDragDrop(source, new DataObject(DataFormats.FileDrop, files), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        catch
        {
            // ignore
        }
        finally
        {
            source.GiveFeedback -= follow;
            ghost?.Close();
            _draggingOut = false;
        }
        after();
    }

    // ---- the selection of one list -----------------------------------------------------------

    private sealed class SelItem
    {
        public required string Path { get; init; }
        public required FrameworkElement Element { get; init; }
        public required Action<bool, bool> Paint { get; init; }

        /// <summary>When false, only the row's texts and icon are "the file": the rest of it can start a lasso.</summary>
        public bool WholeIsFile { get; init; } = true;
        public Action? Click { get; init; }
        public Action? DoubleClick { get; init; }
    }

    private sealed class Press
    {
        public SelItem? Item;
        public bool OnFile, Ctrl, Shift;
        public Point Start, StartInContent;
    }

    private sealed class FileSelector
    {
        private readonly MainWindow _w;
        private readonly FrameworkElement _region, _content;
        private readonly ScrollViewer _viewer;
        private readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
        private readonly Border _lasso;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private Press? _press;
        private string? _anchor;
        private HashSet<string> _lassoBase = new(StringComparer.OrdinalIgnoreCase);
        private Point _lassoStart;
        private bool _lassoOn;

        public readonly List<SelItem> Items = new();
        public readonly HashSet<string> Marked = new(StringComparer.OrdinalIgnoreCase);
        public readonly Color Color;
        public bool Mode { get; private set; }

        /// <summary>After files were dragged out (some may have been moved away).</summary>
        public Action? AfterDrag { get; set; }

        public FileSelector(MainWindow w, FrameworkElement region, FrameworkElement content, ScrollViewer viewer, Panel overlayHost, Color color)
        {
            _w = w;
            _region = region;
            _content = content;
            _viewer = viewer;
            Color = color;
            _lasso = new Border
            {
                BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed,
                BorderBrush = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromArgb(0x1A, color.R, color.G, color.B))
            };
            _overlay.Children.Add(_lasso);
            overlayHost.Children.Add(_overlay);
            _timer.Tick += (_, _) => LassoTick();
            region.PreviewMouseLeftButtonDown += OnDown;
            region.PreviewMouseMove += OnMove;
            region.PreviewMouseLeftButtonUp += OnUp;
            region.LostMouseCapture += (_, _) => EndLasso();
        }

        // ---- marks ---------------------------------------------------------------------------

        private void Repaint()
        {
            foreach (var i in Items) i.Paint(Marked.Contains(i.Path), Mode);
            _w.UpdateSelChrome();
        }

        public void ClearMarks()
        {
            Marked.Clear();
            _anchor = null;
            Repaint();
        }

        public void MarkAll()
        {
            foreach (var i in Items) Marked.Add(i.Path);
            Repaint();
        }

        public void SetMode(bool on)
        {
            Mode = on;
            if (!on) Marked.Clear();
            Repaint();
        }

        public void Reset()
        {
            if (!Mode && Marked.Count == 0) return;
            Mode = false;
            Marked.Clear();
            _anchor = null;
            Repaint();
        }

        /// <summary>After the list was rebuilt: forget what's gone and paint the new rows.</summary>
        public void Sync()
        {
            Marked.IntersectWith(Items.Select(i => i.Path));
            if (Items.Count < 2) Mode = false;
            Repaint();
        }

        // ---- the mouse ---------------------------------------------------------------------------

        private static DependencyObject? Parent(DependencyObject d) => d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

        private bool Blocked(DependencyObject? d)
        {
            for (var p = d; p != null && !ReferenceEquals(p, _region); p = Parent(p))
                if (p is ButtonBase or ScrollBar || p is FrameworkElement { Tag: "noselect" }) return true;
            return false;
        }

        private SelItem? ItemAt(DependencyObject? d)
        {
            for (var p = d; p != null && !ReferenceEquals(p, _region); p = Parent(p))
                foreach (var i in Items)
                    if (ReferenceEquals(i.Element, p)) return i;
            return null;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _press = null;
            var src = e.OriginalSource as DependencyObject;
            if (Items.Count == 0 || Blocked(src)) return;
            var item = ItemAt(src);
            // (Notchy never takes the keyboard, so Ctrl and Shift are read straight from the hardware state)
            bool ctrl = Native.IsKeyDown(0x11), shift = Native.IsKeyDown(0x10);
            if (item?.DoubleClick != null && e.ClickCount == 2 && !ctrl && !shift && !Mode)
            {
                item.DoubleClick();
                e.Handled = true;
                return;
            }
            _press = new Press
            {
                Item = item, OnFile = item != null && (item.WholeIsFile || src is TextBlock), Ctrl = ctrl, Shift = shift,
                Start = e.GetPosition(_region), StartInContent = e.GetPosition(_content)
            };
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (_press is not { } p || _lassoOn || _w._draggingOut) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _press = null;
                return;
            }
            var pos = e.GetPosition(_region);
            if (Math.Abs(pos.X - p.Start.X) < 5 && Math.Abs(pos.Y - p.Start.Y) < 5) return;

            _press = null;
            e.Handled = true;
            // only how the gesture began counts: Ctrl held afterwards is Windows' own "copy"
            if (p.Item != null && p.OnFile && !p.Ctrl) StartFileDrag(p.Item);
            else StartLasso(p);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_lassoOn)
            {
                e.Handled = true;
                _region.ReleaseMouseCapture();
                EndLasso();
                return;
            }
            if (_press is { } p) Clicked(p);
            _press = null;
        }

        private void Clicked(Press p)
        {
            if (p.Item is not { } item)
            {
                if (!p.Ctrl && !p.Shift && Marked.Count > 0) ClearMarks(); // a click on nothing lets go
                return;
            }
            if (p.Shift)
            {
                int to = Items.IndexOf(item), from = Items.FindIndex(i => string.Equals(i.Path, _anchor, StringComparison.OrdinalIgnoreCase));
                if (from < 0) from = to;
                if (!p.Ctrl) Marked.Clear();
                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) Marked.Add(Items[i].Path);
                _anchor ??= item.Path;
                SoundService.Play(Sfx.Click);
                Repaint();
            }
            else if (p.Ctrl || Mode)
            {
                if (!Marked.Remove(item.Path)) Marked.Add(item.Path);
                _anchor = item.Path;
                SoundService.Play(Sfx.Click);
                Repaint();
            }
            else if (Marked.Count > 0) ClearMarks();
            else item.Click?.Invoke();
        }

        private void StartFileDrag(SelItem item)
        {
            string[] paths;
            if (Marked.Contains(item.Path)) paths = Items.Where(i => Marked.Contains(i.Path)).Select(i => i.Path).ToArray();
            else
            {
                if (Marked.Count > 0) ClearMarks(); // dragging another file: that one only
                paths = new[] { item.Path };
            }
            _w.DragFilesOut(_region, paths, () =>
            {
                Marked.Clear();
                _anchor = null;
                if (_w._state != IslandState.Expanded) Mode = false;
                Repaint();
                AfterDrag?.Invoke();
            });
        }

        // ---- the lasso --------------------------------------------------------------------------

        private void StartLasso(Press p)
        {
            _lassoBase = p.Ctrl ? new HashSet<string>(Marked, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _lassoStart = p.StartInContent;
            _lassoOn = true;
            _w._lassoing = true;
            _region.CaptureMouse();
            _lasso.Visibility = Visibility.Visible;
            _timer.Start();
            LassoTick();
        }

        private void LassoTick()
        {
            if (!_lassoOn) return;
            // near the edges the list scrolls by itself
            var v = Mouse.GetPosition(_viewer);
            if (_viewer.ScrollableWidth > 0)
            {
                if (v.X < 10) _viewer.ScrollToHorizontalOffset(_viewer.HorizontalOffset - 8);
                else if (v.X > _viewer.ActualWidth - 10) _viewer.ScrollToHorizontalOffset(_viewer.HorizontalOffset + 8);
            }
            if (_viewer.ScrollableHeight > 0)
            {
                if (v.Y < 8) _viewer.ScrollToVerticalOffset(_viewer.VerticalOffset - 6);
                else if (v.Y > _viewer.ActualHeight - 8) _viewer.ScrollToVerticalOffset(_viewer.VerticalOffset + 6);
            }

            var rect = new Rect(_lassoStart, Mouse.GetPosition(_content));
            var topLeft = _content.TranslatePoint(rect.TopLeft, _overlay);
            Canvas.SetLeft(_lasso, topLeft.X);
            Canvas.SetTop(_lasso, topLeft.Y);
            _lasso.Width = rect.Width;
            _lasso.Height = rect.Height;

            var next = new HashSet<string>(_lassoBase, StringComparer.OrdinalIgnoreCase);
            foreach (var i in Items)
            {
                if (!i.Element.IsDescendantOf(_content)) continue;
                var box = i.Element.TransformToAncestor(_content).TransformBounds(new Rect(i.Element.RenderSize));
                if (box.IntersectsWith(rect)) next.Add(i.Path);
            }
            if (next.SetEquals(Marked)) return;
            Marked.Clear();
            Marked.UnionWith(next);
            Repaint();
        }

        private void EndLasso()
        {
            if (!_lassoOn) return;
            _lassoOn = false;
            _timer.Stop();
            _lasso.Visibility = Visibility.Collapsed;
            _lasso.Width = _lasso.Height = 0;
            _w._lassoing = false;
            _w.UpdateState();
        }
    }
}

/// <summary>A small pile of the dragged files with their count, following the mouse (clicks and drops pass through it).</summary>
internal sealed class DragGhost : Window
{
    public DragGhost(IReadOnlyList<ImageSource?> thumbs, int count)
    {
        Width = 110;
        Height = 90;
        Left = -32000;
        Top = -32000;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;

        var canvas = new Canvas();
        for (int i = 0; i < thumbs.Count; i++)
        {
            var card = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x30)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform((i - (thumbs.Count - 1) / 2.0) * 6),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { ShadowDepth = 3, BlurRadius = 12, Opacity = 0.6 },
                Child = thumbs[i] is { } src
                    ? new Border { CornerRadius = new CornerRadius(8), Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill } }
                    : null
            };
            Canvas.SetLeft(card, 12 + i * 4);
            Canvas.SetTop(card, 26 - i * 4);
            canvas.Children.Add(card);
        }
        var badge = new Border
        {
            MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Padding = new Thickness(5, 0, 5, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F)),
            Child = new TextBlock
            {
                Text = count.ToString(), FontSize = 10.5, FontWeight = FontWeights.ExtraBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(badge, 46);
        Canvas.SetTop(badge, 10);
        canvas.Children.Add(badge);
        Content = canvas;

        SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle);
    }

    public void FollowCursor()
    {
        Native.GetCursorPos(out var p);
        Native.MoveTopmost(new WindowInteropHelper(this).Handle, p.X + 8, p.Y + 2);
    }
}
