using System;
using System.Collections.Generic;
using System.Diagnostics;
using Path = System.Windows.Shapes.Path;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Notchy;

public enum CaptureAction { Copy, Save, Ocr, Pin }

/// <summary>
/// The screen capture: freezes the screen and dims it; you drag an area (or click a window, which lights up
/// under the mouse), with a magnifier and the size next to the cursor. Then the island comes down and opens as
/// the toolbar (arrow, rectangle, pen, marker, text, pixelate, colour, undo · read text, pin, save, Copiar). Copiar
/// and Salvar send the picture up to the island with a trail of light. Esc or right click cancels.
/// </summary>
public sealed partial class CaptureWindow : Window
{
    private enum Tool { Arrow, Rect, Pen, Marker, Text, Blur }
    private enum Phase { Choosing, Editing, Flying }

    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x5A, 0x5F), Color.FromRgb(0xFF, 0xB3, 0x47), Color.FromRgb(0xFF, 0xD1, 0x66), Color.FromRgb(0x6E, 0xE7, 0xC4),
        Color.FromRgb(0x7F, 0xD0, 0xFF), Color.FromRgb(0xB9, 0xA6, 0xFF), Colors.White, Color.FromRgb(0x1B, 0x1B, 0x1F)
    };
    private static readonly Color Ice = Color.FromRgb(0x9A, 0xD7, 0xFF);

    private static readonly Dictionary<string, string> Icons = new()
    {
        ["arrow"] = "M3,13 L13,3 M7,3 H13 V9",
        ["rect"] = "M4,3.5 H12 A1.5,1.5 0 0 1 13.5,5 V11 A1.5,1.5 0 0 1 12,12.5 H4 A1.5,1.5 0 0 1 2.5,11 V5 A1.5,1.5 0 0 1 4,3.5 Z",
        ["pen"] = "M2.5,13.5 L3.5,10 L10.5,3 L13,5.5 L6,12.5 Z",
        ["marker"] = "M3,13.5 H13 M5,10.5 L10.5,3 L13,4.8 L7.5,12.3 H5 Z",
        ["text"] = "M3.5,3.5 H12.5 M8,3.5 V13",
        ["blur"] = "M2.5,2.5 H6.5 V6.5 H2.5 Z M9.5,2.5 H13.5 V6.5 H9.5 Z M6,6 H10 V10 H6 Z M2.5,9.5 H6.5 V13.5 H2.5 Z M9.5,9.5 H13.5 V13.5 H9.5 Z",
        ["undo"] = "M5.5,3 L2.5,6 L5.5,9 M2.5,6 H9.5 A4,4 0 0 1 9.5,14 H7",
        ["ocr"] = "M2.5,5 V3.5 A1,1 0 0 1 3.5,2.5 H5 M11,2.5 H12.5 A1,1 0 0 1 13.5,3.5 V5 M13.5,11 V12.5 A1,1 0 0 1 12.5,13.5 H11 M5,13.5 H3.5 A1,1 0 0 1 2.5,12.5 V11 M5.5,6.5 H10.5 M5.5,9.5 H9",
        ["pin"] = "M10,2.5 L13.5,6 L11,7 L9,9 L9.5,12 L3.5,6 L6.5,6.5 L8.5,4.5 Z M5.5,10.5 L2.5,13.5",
        ["save"] = "M8,2.5 V10.5 M4.5,7 L8,10.5 L11.5,7 M2.5,13.5 H13.5",
        ["copy"] = "M7,5.5 H12 A1.5,1.5 0 0 1 13.5,7 V12 A1.5,1.5 0 0 1 12,13.5 H7 A1.5,1.5 0 0 1 5.5,12 V7 A1.5,1.5 0 0 1 7,5.5 Z M10.5,5.5 V3.5 A1,1 0 0 0 9.5,2.5 H3.5 A1,1 0 0 0 2.5,3.5 V9.5 A1,1 0 0 0 3.5,10.5 H5.5",
        ["close"] = "M4,4 L12,12 M12,4 L4,12"
    };

    private readonly System.Drawing.Bitmap _shot;
    private readonly System.Drawing.Rectangle _screenPx;
    private readonly BitmapSource _src;
    private readonly Point _islandAt;        // the island's middle, in this window's coordinates
    private readonly double _islandW;
    private readonly List<Rect> _windows;    // top-level windows, front to back (window coordinates)
    private readonly List<IntPtr> _windowHandles; // the same windows' handles (for the elements inside them)
    private readonly Canvas _stage = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Canvas _annot = new() { IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _hover = new() { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(9), BorderBrush = new SolidColorBrush(Ice), IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        Effect = new DropShadowEffect { Color = Ice, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 } };
    private readonly Border _size = new() { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _sizeText = new() { FontSize = 11, Foreground = Brushes.White };
    private readonly Border _lens = new() { Width = 86, Height = 86, CornerRadius = new CornerRadius(43), BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6 } };
    private readonly ImageBrush _lensBrush;
    private readonly TextBlock _lensText = new() { FontSize = 9.5, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 7),
        Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 1 } };
    private readonly List<Rectangle> _handles = new();
    private Border _bar = null!, _barInner = null!, _pop = null!, _dot = null!;
    private readonly Dictionary<Tool, Border> _toolButtons = new();
    private BarSlider _thick = null!;

    private Phase _phase = Phase.Choosing;
    private Tool _tool = Tool.Pen;
    private Color _color = Palette[0];
    private double _thickness = 4;
    private Rect _sel;
    private Point _down;
    private bool _dragging;
    private int _resizing = -1;              // which handle is being dragged
    private Rect _resizeFrom;
    private UIElement? _drawing;             // the mark being drawn
    private Rectangle? _blurBox;
    private TextBox? _typing;
    private readonly List<UIElement> _marks = new();

    /// <summary>Copiar / Salvar / Ler texto / Fixar: the picture, and where it was on screen (device pixels).</summary>
    public event Action<CaptureAction, BitmapSource, Int32Rect, string?>? Done;
    /// <summary>Esc, right click, or the X: nothing was taken.</summary>
    public event Action? Cancelled;
    /// <summary>The picture starts its trip: the real island can come back, over this window, where the frozen one is.</summary>
    public event Action? Returning;

    private Image? _islandPatch; // the screen under the island, without it: covers the frozen island once the real one is back

    /// <summary>What's under the island (device pixels, taken with the island left out), shown when the real island returns.</summary>
    public void SetIslandPatch(BitmapSource patch, Int32Rect px)
    {
        _islandPatch = new Image { Source = patch, Stretch = Stretch.Fill, Width = px.Width / Kx, Height = px.Height / Ky, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Canvas.SetLeft(_islandPatch, (px.X - _screenPx.Left) / Kx);
        Canvas.SetTop(_islandPatch, (px.Y - _screenPx.Top) / Ky);
        _stage.Children.Insert(1, _islandPatch); // (right over the frozen screen, under everything else)
    }

    /// <param name="islandPx">The island's middle on screen (device pixels), where the toolbar comes from.</param>
    public CaptureWindow(System.Windows.Point islandPx, double islandWidthPx)
    {
        _screenPx = System.Windows.Forms.SystemInformation.VirtualScreen;
        _shot = new System.Drawing.Bitmap(_screenPx.Width, _screenPx.Height);
        using (var g = System.Drawing.Graphics.FromImage(_shot))
            g.CopyFromScreen(_screenPx.Left, _screenPx.Top, 0, 0, _screenPx.Size);
        _src = ToBitmapSource(_shot);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        // shortcut: one window over every monitor, in the main monitor's scale; mixed scales can be a little off
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Title = "Notchy";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        double kx = _screenPx.Width / Width, ky = _screenPx.Height / Height;
        _islandAt = new Point((islandPx.X - _screenPx.Left) / kx, (islandPx.Y - _screenPx.Top) / ky);
        _islandW = islandWidthPx / kx;
        var tops = TopWindows();
        _windows = tops.Select(w => ToStage(w.r)).ToList();
        _windowHandles = tops.Select(w => w.h).ToList();

        _annot.Width = Width;
        _annot.Height = Height;
        var image = new Image { Source = _src, Width = Width, Height = Height, Stretch = Stretch.Fill };
        _stage.Children.Add(image);
        _stage.Children.Add(_annot);
        _stage.Children.Add(_dim);
        _stage.Children.Add(_hover);
        _stage.Children.Add(_frame);
        for (int i = 0; i < 8; i++)
        {
            var h = new Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), Visibility = Visibility.Collapsed,
                Cursor = (i % 4) switch { 0 => Cursors.SizeNWSE, 1 => Cursors.SizeNS, 2 => Cursors.SizeNESW, _ => Cursors.SizeWE } };
            int idx = i;
            h.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                CommitText();
                _resizing = idx;
                _resizeFrom = _sel;
                _down = e.GetPosition(_stage);
                CaptureMouse();
            };
            _handles.Add(h);
            _stage.Children.Add(h);
        }
        _size.Child = _sizeText;
        _stage.Children.Add(_size);
        _lensBrush = new ImageBrush(_src) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_lensBrush, BitmapScalingMode.NearestNeighbor);
        var lensGrid = new Grid { ClipToBounds = true };
        lensGrid.Children.Add(new Ellipse { Fill = _lensBrush });
        lensGrid.Children.Add(new Rectangle { Width = 7, Height = 7, Stroke = Brushes.White, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 2, ShadowDepth = 0, Opacity = 1 } });
        lensGrid.Children.Add(_lensText);
        lensGrid.Clip = new EllipseGeometry(new Point(41, 41), 41, 41);
        _lens.Child = lensGrid;
        _stage.Children.Add(_lens);
        BuildBar();
        Content = _stage;
        SetDim(Rect.Empty);

        Loaded += (_, _) =>
        {
            if (AppSettings.Current.BlurEnabled)
                Dispatcher.BeginInvoke(() => _blurred ??= MakeBlurred(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(200))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Native.GetCursorPos(out var c);
            Track(new Point((c.X - _screenPx.Left) / kx, (c.Y - _screenPx.Top) / ky));
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += (_, e) => OnMove(e.GetPosition(_stage));
        MouseWheel += OnElementWheel;
        MouseLeftButtonUp += (_, e) => OnUp(e.GetPosition(_stage));
        MouseRightButtonUp += (_, _) => { if (_phase != Phase.Flying) Cancel(); };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => _shot.Dispose();
    }

    private static double Ms(double ms) => ms * AppSettings.Current.SpeedFactor;
    private double Kx => _screenPx.Width / Width;
    private double Ky => _screenPx.Height / Height;

    // =====================================================================================
    // choosing the area
    // =====================================================================================

    private void SetDim(Rect hole)
    {
        Geometry full = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _dim.Data = hole.IsEmpty || hole.Width < 1 || hole.Height < 1 ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));
    }

    private Rect WindowAt(Point p) => _windows.FirstOrDefault(r => r.Contains(p), Rect.Empty);

    /// <summary>Before a drag: the window under the mouse lights up, the magnifier follows.</summary>
    private void Track(Point p)
    {
        if (_phase != Phase.Choosing) return;
        if (!_dragging)
        {
            TrackElements(p);
        }
        // the magnifier: 29 pixels around the cursor, three times as big
        double px = p.X * Kx, py = p.Y * Ky;
        _lensBrush.Viewbox = new Rect(px - 14.5, py - 14.5, 29, 29);
        _lensText.Text = $"{(int)px}, {(int)py}";
        double lx = p.X + 22 + 86 > Width ? p.X - 22 - 86 : p.X + 22, ly = p.Y + 22 + 86 > Height ? p.Y - 22 - 86 : p.Y + 22;
        Canvas.SetLeft(_lens, lx);
        Canvas.SetTop(_lens, ly);
    }

    private void ShowSize(Rect r, Point at)
    {
        _size.Visibility = Visibility.Visible;
        _sizeText.Text = $"{Math.Round(r.Width * Kx)} × {Math.Round(r.Height * Ky)}";
        Canvas.SetLeft(_size, at.X);
        Canvas.SetTop(_size, at.Y);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_stage);
        if (_phase == Phase.Choosing)
        {
            _down = p;
            _dragging = true;
            CaptureMouse();
            return;
        }
        if (_phase != Phase.Editing) return;
        ClosePop();
        if (e.OriginalSource is DependencyObject d && IsInside(d, _bar)) return;
        if (_typing != null && e.OriginalSource is DependencyObject t && IsInside(t, _typing)) return;
        CommitText();
        if (!_sel.Contains(p)) return;
        if (_tool == Tool.Text)
        {
            StartText(p);
            e.Handled = true;
            return;
        }
        _down = p;
        _drawing = StartMark(p);
        if (_drawing != null)
        {
            _annot.Children.Add(_drawing);
            CaptureMouse();
        }
    }

    private void OnMove(Point p)
    {
        if (_phase == Phase.Choosing)
        {
            Track(p);
            if (!_dragging) return;
            var r = new Rect(_down, p);
            _hover.Visibility = Visibility.Collapsed;
            SetDim(r);
            _frame.Visibility = Visibility.Visible;
            Place(_frame, r);
            ShowSize(r, new Point(Math.Min(p.X + 12, Width - 90), Math.Min(p.Y + 12, Height - 24)));
            return;
        }
        if (_phase != Phase.Editing) return;
        if (_resizing >= 0)
        {
            Resize(p);
            return;
        }
        if (_drawing != null) UpdateMark(p);
    }

    private void OnUp(Point p)
    {
        ReleaseMouseCapture();
        if (_phase == Phase.Choosing)
        {
            if (!_dragging) return;
            _dragging = false;
            var r = new Rect(_down, p);
            if (r.Width < 6 || r.Height < 6)
            {
                // a click: the element (or window) lit under it, or the whole monitor
                r = LitAt(p);
                if (r.IsEmpty)
                {
                    var m = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(p.X * Kx) + _screenPx.Left, (int)(p.Y * Ky) + _screenPx.Top)).Bounds;
                    r = new Rect((m.Left - _screenPx.Left) / Kx, (m.Top - _screenPx.Top) / Ky, m.Width / Kx, m.Height / Ky);
                }
                r.Intersect(new Rect(0, 0, Width, Height));
            }
            StartEditing(r);
            return;
        }
        if (_resizing >= 0)
        {
            _resizing = -1;
            return;
        }
        if (_drawing != null) FinishMark(p);
    }

    // =====================================================================================
    // editing: the frame, its handles and the marks
    // =====================================================================================

    private void StartEditing(Rect r)
    {
        _phase = Phase.Editing;
        _hover.Visibility = Visibility.Collapsed;
        _lens.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        SetSel(r);
        ShowBar();
    }

    private void SetSel(Rect r)
    {
        _sel = r;
        SetDim(r);
        _frame.Visibility = Visibility.Visible;
        Place(_frame, r);
        _annot.Clip = new RectangleGeometry(r);
        var spots = new[] { (0.0, 0.0), (0.5, 0.0), (1.0, 0.0), (1.0, 0.5), (1.0, 1.0), (0.5, 1.0), (0.0, 1.0), (0.0, 0.5) };
        for (int i = 0; i < 8; i++)
        {
            var h = _handles[i];
            h.Visibility = Visibility.Visible;
            Canvas.SetLeft(h, r.X + r.Width * spots[i].Item1 - 4);
            Canvas.SetTop(h, r.Y + r.Height * spots[i].Item2 - 4);
        }
        ShowSize(r, new Point(r.X, r.Y - 24 < 0 ? r.Y + 6 : r.Y - 24));
        UpdateCursor();
    }

    private void Resize(Point p)
    {
        var v = p - _down;
        double l = _resizeFrom.Left, t = _resizeFrom.Top, r = _resizeFrom.Right, b = _resizeFrom.Bottom;
        // handles go round from the top-left corner: 0 corner, 1 top, 2 corner, 3 right, 4 corner, 5 bottom, 6 corner, 7 left
        if (_resizing is 0 or 6 or 7) l += v.X;
        if (_resizing is 2 or 3 or 4) r += v.X;
        if (_resizing is 0 or 1 or 2) t += v.Y;
        if (_resizing is 4 or 5 or 6) b += v.Y;
        var n = new Rect(new Point(l, t), new Point(r, b));
        if (n.Width < 8 || n.Height < 8) return;
        SetSel(n);
        if (_bar.Visibility == Visibility.Visible)
        {
            PlaceBar(); // (the bar follows the area)
            _tip.Opacity = 0;
            ClosePop();
        }
    }

    private void UpdateCursor() => Cursor = _phase == Phase.Editing ? (_tool == Tool.Text ? Cursors.IBeam : Cursors.Cross) : Cursors.Cross;

    private SolidColorBrush Ink => new(_color);

    private UIElement? StartMark(Point p)
    {
        switch (_tool)
        {
            case Tool.Arrow:
                return new Path { Stroke = Ink, StrokeThickness = _thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            case Tool.Rect:
                return new Rectangle { Stroke = Ink, StrokeThickness = _thickness, RadiusX = 3, RadiusY = 3 };
            case Tool.Pen:
            case Tool.Marker:
                bool marker = _tool == Tool.Marker;
                return new Polyline
                {
                    Stroke = Ink, StrokeThickness = marker ? _thickness * 4 + 6 : _thickness, Opacity = marker ? 0.42 : 1,
                    StrokeStartLineCap = marker ? PenLineCap.Square : PenLineCap.Round, StrokeEndLineCap = marker ? PenLineCap.Square : PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round, Points = new PointCollection { p }
                };
            case Tool.Blur:
                _blurBox = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)) };
                return _blurBox;
        }
        return null;
    }

    private void UpdateMark(Point p)
    {
        switch (_drawing)
        {
            case Path arrow:
                arrow.Data = ArrowGeometry(_down, p, _thickness);
                break;
            case Polyline line:
                if ((p - line.Points[^1]).Length >= 2) line.Points.Add(p);
                break;
            case Rectangle box:
                Place(box, new Rect(_down, p));
                break;
        }
    }

    private void FinishMark(Point p)
    {
        var mark = _drawing!;
        _drawing = null;
        if (mark == _blurBox)
        {
            _annot.Children.Remove(mark);
            _blurBox = null;
            var r = Rect.Intersect(new Rect(_down, p), new Rect(0, 0, Width, Height));
            if (r.IsEmpty || r.Width < 4 || r.Height < 4) return;
            mark = Pixelated(r);
            _annot.Children.Add(mark);
        }
        else if ((p - _down).Length < 3 && mark is not Polyline)
        {
            _annot.Children.Remove(mark); // just a click
            return;
        }
        _marks.Add(mark);
    }

    private static Geometry ArrowGeometry(Point a, Point b, double thick)
    {
        var v = b - a;
        if (v.Length < 1) return Geometry.Empty;
        v.Normalize();
        double head = Math.Max(10, thick * 3.6);
        var left = new Vector(v.X * Math.Cos(2.6) - v.Y * Math.Sin(2.6), v.X * Math.Sin(2.6) + v.Y * Math.Cos(2.6));
        var right = new Vector(v.X * Math.Cos(-2.6) - v.Y * Math.Sin(-2.6), v.X * Math.Sin(-2.6) + v.Y * Math.Cos(-2.6));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(a, false, false);
            c.LineTo(b, true, true);
            c.BeginFigure(b + left * head, false, false);
            c.LineTo(b, true, true);
            c.LineTo(b + right * head, true, true);
        }
        g.Freeze();
        return g;
    }

    /// <summary>The area under it, in big blocks (to hide what's there).</summary>
    private Image Pixelated(Rect r)
    {
        var px = new Int32Rect((int)(r.X * Kx), (int)(r.Y * Ky), Math.Max(1, (int)(r.Width * Kx)), Math.Max(1, (int)(r.Height * Ky)));
        px.Width = Math.Min(px.Width, _src.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, _src.PixelHeight - px.Y);
        double block = Math.Max(6, _thickness * 2.5);
        var small = new TransformedBitmap(new CroppedBitmap(_src, px), new ScaleTransform(1 / block, 1 / block));
        var img = new Image { Source = small, Stretch = Stretch.Fill, Width = r.Width, Height = r.Height };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, r.X);
        Canvas.SetTop(img, r.Y);
        return img;
    }

    private void StartText(Point p)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ink, CaretBrush = Ink, FontSize = 14 + _thickness * 2,
            FontWeight = FontWeights.SemiBold, MinWidth = 20, AcceptsReturn = true, Padding = new Thickness(0), FocusVisualStyle = null,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.6, Color = _color == Colors.White ? Colors.Black : Colors.White }
        };
        Canvas.SetLeft(box, p.X);
        Canvas.SetTop(box, p.Y - box.FontSize * 0.7);
        _annot.Children.Add(box);
        _annot.IsHitTestVisible = true;
        _typing = box;
        box.Loaded += (_, _) => Keyboard.Focus(box);
    }

    /// <summary>The text being typed becomes a mark (or goes away if empty).</summary>
    private void CommitText()
    {
        if (_typing is not { } box) return;
        _typing = null;
        _annot.IsHitTestVisible = false;
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            _annot.Children.Remove(box);
            return;
        }
        box.IsReadOnly = true;
        box.Focusable = false;
        box.CaretBrush = Brushes.Transparent;
        _marks.Add(box);
        Keyboard.Focus(this);
    }

    private void Undo()
    {
        if (_typing != null)
        {
            _annot.Children.Remove(_typing);
            _typing = null;
            _annot.IsHitTestVisible = false;
            return;
        }
        if (_marks.Count == 0) return;
        _annot.Children.Remove(_marks[^1]);
        _marks.RemoveAt(_marks.Count - 1);
    }

    private static void Place(FrameworkElement e, Rect r)
    {
        Canvas.SetLeft(e, r.X);
        Canvas.SetTop(e, r.Y);
        e.Width = r.Width;
        e.Height = r.Height;
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_typing != null) CommitText();
            else if (_pop.Visibility == Visibility.Visible) ClosePop();
            else Cancel();
            return;
        }
        if (_phase != Phase.Editing) return;
        if (_typing != null)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                CommitText();
            }
            return;
        }
        if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.C) _ = Finish(CaptureAction.Copy);
        else if (ctrl && e.Key == Key.S) _ = Finish(CaptureAction.Save);
        else if (e.Key == Key.Enter) _ = Finish(CaptureAction.Copy);
        else return;
        e.Handled = true;
    }

    // =====================================================================================
    // the toolbar (B3 with B2's light): it drips out of the island, travels to the area and opens under it;
    // the island's eyes peek from a little tab on its top edge
    // =====================================================================================

    private static SolidColorBrush White(byte a) => new(Color.FromArgb(a, 255, 255, 255));
    private static readonly Color IceSoft = Color.FromRgb(0xBF, 0xE6, 0xFF);
    private const double TabW = 58, TabH = 22, BarRadius = 16, TabOverlap = 3; // (the tab covers the bar's top line and light under it: no seam)

    private Border _barShadow = null!, _tab = null!, _tip = null!;
    private TextBlock _tipText = null!;
    private readonly TranslateTransform _tipMove = new();
    private readonly TranslateTransform _tabMove = new();
    private readonly ScaleTransform _tabScale = new(1, 1);
    private StackPanel _row = null!;
    private Rect _barRect;                       // where the bar is (or will be)
    private readonly List<(Rectangle eye, ScaleTransform blink, Path happy)> _eyes = new();
    private TranslateTransform _eyesLook = null!;
    private double _happyUntil, _nextBlink = 1.5, _eyesClock;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _eyesRunning;

    private Border IconButton(string icon, string name, Action click, bool tool = false)
    {
        var path = new Path { Data = Geometry.Parse(Icons[icon]), Stroke = White(0xB8), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.None };
        var grid = new Grid();
        grid.Children.Add(path);
        if (tool)
            grid.Children.Add(new Ellipse
            {
                Width = 4, Height = 4, Fill = new SolidColorBrush(Ice), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3), Opacity = 0, Effect = new DropShadowEffect { Color = Ice, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 }
            });
        var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Colors.Transparent), Child = grid, Cursor = Cursors.Hand };
        Hover(b, name);
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return b;
    }

    /// <summary>The background lights up softly, and the name slides under the bar (as on the pinned capture).</summary>
    private void Hover(Border b, string name)
    {
        b.MouseEnter += (_, _) =>
        {
            if (b.Tag is not true) Paint(b, Color.FromArgb(0x1A, 255, 255, 255));
            ShowTip(b, name);
        };
        b.MouseLeave += (_, _) =>
        {
            if (b.Tag is not true) Paint(b, Colors.Transparent);
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Ms(160))) { BeginTime = TimeSpan.FromMilliseconds(Ms(90)) });
        };
    }

    private void Paint(Border b, Color to)
    {
        if (b.Background is not SolidColorBrush { IsFrozen: false } brush) b.Background = brush = new SolidColorBrush(((SolidColorBrush)b.Background).Color);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(Ms(160))));
    }

    private void ShowTip(FrameworkElement button, string name)
    {
        bool shown = _tip.Opacity > 0.4;
        _tipText.Text = name;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), _stage).X - _tip.DesiredSize.Width / 2;
        bool below = _barRect.Bottom + 30 < MonitorOf(_barRect).Bottom;
        Canvas.SetTop(_tip, below ? _barRect.Bottom + 7 : _barRect.Top - TabH - _tip.DesiredSize.Height - 4);
        var time = TimeSpan.FromMilliseconds(Ms(170));
        if (shown)
        {
            _tipMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _tipText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, time));
        }
        else
        {
            _tipMove.BeginAnimation(TranslateTransform.XProperty, null);
            _tipMove.X = x;
            _tipText.BeginAnimation(OpacityProperty, null);
            _tipText.Opacity = 1;
        }
        _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(shown ? 80 : 140))));
    }

    private static Border Sep() => new() { Width = 1, Height = 16, Background = White(0x1C), Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };

    private void SetTool(Tool t)
    {
        CommitText();
        bool changed = t != _tool;
        _tool = t;
        foreach (var (tool, b) in _toolButtons)
        {
            bool on = tool == t;
            b.Tag = on;
            Paint(b, on ? Color.FromArgb(0x26, Ice.R, Ice.G, Ice.B) : Colors.Transparent);
            var grid = (Grid)b.Child;
            ((Path)grid.Children[0]).Stroke = on ? new SolidColorBrush(IceSoft) : White(0xB8);
            grid.Children[1].BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(Ms(180))));
        }
        if (changed) _happyUntil = _clock.Elapsed.TotalSeconds + 0.7; // ^ ^
        UpdateCursor();
    }

    private void BuildBar()
    {
        _row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (tool, icon, tip) in new[] { (Tool.Pen, "pen", "cap.pen"), (Tool.Arrow, "arrow", "cap.arrow"), (Tool.Rect, "rect", "cap.rect"),
                     (Tool.Marker, "marker", "cap.marker"), (Tool.Text, "text", "cap.text"), (Tool.Blur, "blur", "cap.blur") })
        {
            var t = tool;
            var b = IconButton(icon, Loc.T(tip), () => SetTool(t), tool: true);
            _toolButtons[tool] = b;
            _row.Children.Add(b);
        }
        _row.Children.Add(Sep());
        _dot = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Colors.Transparent), Cursor = Cursors.Hand,
            Child = new Ellipse { Width = 14, Height = 14, Fill = Ink, Stroke = White(0x50), StrokeThickness = 1.5 } };
        Hover(_dot, Loc.T("cap.color"));
        _dot.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _dot.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_pop.Visibility == Visibility.Visible) ClosePop();
            else OpenPop();
        };
        _row.Children.Add(_dot);
        _row.Children.Add(IconButton("undo", Loc.T("cap.undo"), Undo));
        _row.Children.Add(Sep());
        _row.Children.Add(IconButton("ocr", Loc.T("cap.ocr"), () => _ = Finish(CaptureAction.Ocr)));
        _row.Children.Add(IconButton("pin", Loc.T("cap.pin"), () => _ = Finish(CaptureAction.Pin)));
        _row.Children.Add(IconButton("save", Loc.T("cap.save"), () => _ = Finish(CaptureAction.Save)));
        var copyIcon = new Path { Data = Geometry.Parse(Icons["copy"]), Stroke = Brushes.Black, StrokeThickness = 1.7, StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.None,
            RenderTransform = new ScaleTransform(0.9, 0.9, 8, 8) };
        var copyRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        copyRow.Children.Add(copyIcon);
        copyRow.Children.Add(new TextBlock { Text = Loc.T("cap.copy"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var copy = new Border { Height = 32, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0, 12, 0), Margin = new Thickness(6, 0, 2, 0), Background = Brushes.White, Child = copyRow, Cursor = Cursors.Hand };
        copy.MouseEnter += (_, _) =>
        {
            copy.BeginAnimation(OpacityProperty, new DoubleAnimation(0.86, TimeSpan.FromMilliseconds(Ms(140))));
            ShowTip(copy, Loc.T("cap.copy.tip"));
        };
        copy.MouseLeave += (_, _) =>
        {
            copy.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(160))));
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Ms(160))) { BeginTime = TimeSpan.FromMilliseconds(Ms(90)) });
        };
        copy.MouseLeftButtonDown += (_, e) => e.Handled = true;
        copy.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _ = Finish(CaptureAction.Copy);
        };
        _row.Children.Add(copy);
        _row.Children.Add(IconButton("close", Loc.T("cap.cancel"), Cancel));

        // the bar: a dark shadow under it, the ice line and glow on it, a thin light along its top
        _barShadow = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(BarRadius), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 10, Opacity = 0.7 } };
        _barInner = new Border { Padding = new Thickness(8, 6, 6, 6), Child = _row };
        var shine = new Border { CornerRadius = new CornerRadius(BarRadius), BorderBrush = White(0x14), BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false, Margin = new Thickness(1) };
        var inside = new Grid();
        _barGlass = Glass(inside);
        inside.Children.Add(shine);
        inside.Children.Add(_barInner);
        _bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x02, 0, 0, 0)), CornerRadius = new CornerRadius(BarRadius),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, Ice.R, Ice.G, Ice.B)), BorderThickness = new Thickness(1), Child = inside, Visibility = Visibility.Collapsed,
            Effect = new DropShadowEffect { Color = Ice, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.3 }
        };

        // the tab with the island's eyes, on the bar's top edge
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0) };
        _eyesLook = new TranslateTransform();
        eyes.RenderTransform = _eyesLook;
        for (int i = 0; i < 2; i++)
        {
            var blink = new ScaleTransform(1, 1);
            var eye = new Rectangle { Width = 5, Height = 10, RadiusX = 2.5, RadiusY = 2.5, Fill = Brushes.White, RenderTransform = blink, RenderTransformOrigin = new Point(0.5, 0.5) };
            var happy = new Path { Data = Geometry.Parse("M0,4.5 Q4,-0.5 8,4.5"), Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var cell = new Grid { Width = 9, Height = 12, Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) };
            cell.Children.Add(eye);
            cell.Children.Add(happy);
            eyes.Children.Add(cell);
            _eyes.Add((eye, blink, happy));
        }
        var tabInside = new Grid();
        _tabGlass = Glass(tabInside);
        tabInside.Children.Add(eyes);
        _tab = new Border
        {
            Width = TabW, Height = TabH + TabOverlap, CornerRadius = new CornerRadius(11, 11, 0, 0), Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, Ice.R, Ice.G, Ice.B)), BorderThickness = new Thickness(1, 1, 1, 0), Child = tabInside, Visibility = Visibility.Collapsed,
            RenderTransform = new TransformGroup { Children = { _tabScale, _tabMove } }, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false
        };
        _tipText = new TextBlock { FontSize = 10.5, Foreground = Brushes.White };
        _tip = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3), Child = _tipText,
            Opacity = 0, IsHitTestVisible = false, RenderTransform = _tipMove };
        _stage.Children.Add(_barShadow);
        _stage.Children.Add(_bar);
        _stage.Children.Add(_tab);
        _stage.Children.Add(_tip);
        SetTool(Tool.Pen);
        _happyUntil = 0;

        // the colours drop from the bar (C2)
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Rows = 2 };
        foreach (var c in Palette)
        {
            var color = c;
            var swatch = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(c), Margin = new Thickness(4.5), Cursor = Cursors.Hand,
                Stroke = c == Palette[^1] ? White(0x40) : null, StrokeThickness = 1 };
            swatch.MouseLeftButtonDown += (_, e) => e.Handled = true;
            swatch.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _color = color;
                ((Ellipse)_dot.Child).Fill = Ink;
                if (_typing != null) _typing.Foreground = _typing.CaretBrush = Ink;
                ClosePop();
            };
            grid.Children.Add(swatch);
        }
        _thick = new BarSlider { Width = 96, Height = 18, VerticalAlignment = VerticalAlignment.Center, Value = (_thickness - 1) / 9 };
        _thick.ValueChanging += v => _thickness = 1 + v * 9;
        _thick.ValueCommitted += v => _thickness = 1 + v * 9;
        var thickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 10, 4, 0) };
        thickRow.Children.Add(new TextBlock { Text = Loc.T("cap.thick"), FontSize = 11, Foreground = White(0x99), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        thickRow.Children.Add(_thick);
        var popStack = new StackPanel { Margin = new Thickness(10) };
        popStack.Children.Add(grid);
        popStack.Children.Add(thickRow);
        var popInside = new Grid();
        _popGlass = Glass(popInside);
        popInside.Children.Add(popStack);
        _pop = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x02, 0, 0, 0)), CornerRadius = new CornerRadius(16),
            Child = popInside, Visibility = Visibility.Collapsed,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, Ice.R, Ice.G, Ice.B)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.6 },
            RenderTransform = new ScaleTransform(1, 1), RenderTransformOrigin = new Point(0.5, 0)
        };
        _pop.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _stage.Children.Add(_pop);
    }

    /// <summary>The monitor an area is on, in this window's coordinates.</summary>
    private Rect MonitorOf(Rect r)
    {
        var px = new System.Drawing.Point((int)((r.X + r.Width / 2) * Kx) + _screenPx.Left, (int)((r.Y + r.Height / 2) * Ky) + _screenPx.Top);
        var m = System.Windows.Forms.Screen.FromPoint(px).Bounds;
        return new Rect((m.Left - _screenPx.Left) / Kx, (m.Top - _screenPx.Top) / Ky, m.Width / Kx, m.Height / Ky);
    }

    /// <summary>Where the open bar goes: centred under the area; above it when there's no room below; inside its bottom when neither.</summary>
    private Rect BarSpot()
    {
        _barInner.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = _barInner.DesiredSize.Width + 2, h = _barInner.DesiredSize.Height + 2;
        var mon = MonitorOf(_sel);
        double x = Math.Clamp(_sel.X + _sel.Width / 2 - w / 2, mon.Left + 8, mon.Right - w - 8);
        double y = _sel.Bottom + TabH + 6;
        if (y + h + 32 > mon.Bottom)
        {
            y = _sel.Top - h - 14;
            if (y - TabH < mon.Top + 4) y = _sel.Bottom - h - 14;
        }
        return new Rect(x, y, w, h);
    }

    /// <summary>The bar (and its shadow and tab) at a place and size; <paramref name="radius"/> for its corners.</summary>
    private void LayBar(Rect r, double radius)
    {
        foreach (var b in new[] { _bar, _barShadow })
        {
            Canvas.SetLeft(b, r.X);
            Canvas.SetTop(b, r.Y);
            b.Width = Math.Max(1, r.Width);
            b.Height = Math.Max(1, r.Height);
            b.CornerRadius = new CornerRadius(radius);
        }
        Canvas.SetLeft(_tab, r.X + r.Width / 2 - TabW / 2);
        Canvas.SetTop(_tab, r.Y - TabH);
        PlaceGlass(_barGlass, r, new CornerRadius(radius));
        PlaceGlass(_tabGlass, new Rect(r.X + r.Width / 2 - TabW / 2, r.Y - TabH, TabW, TabH + TabOverlap), new CornerRadius(11, 11, 0, 0));
    }

    // ---- the island's own background: black, or its glass (the frozen screen behind, blurred and darkened as set
    // in Aparência), so the bar looks like the island that came down. Follows whatever is set there.

    private sealed class GlassLayer
    {
        public required Border Clear, Blur, Tint;
        public required ImageBrush ClearBrush, BlurBrush;
    }

    private const double BlurScale = 1.0 / 16, BlurRadius = 7; // (measured against the island's real glass: NOTCHY_GLASSTEST)
    private BitmapSource? _blurred;
    private GlassLayer _barGlass = null!, _tabGlass = null!, _popGlass = null!;

    /// <summary>The background layers, at the bottom of <paramref name="host"/>: the screen as it is, the blurred screen over it, then the darkening.</summary>
    private GlassLayer Glass(Panel host)
    {
        var s = AppSettings.Current;
        var clearBrush = new ImageBrush(_src) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var blurBrush = new ImageBrush { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(blurBrush, BitmapScalingMode.HighQuality);
        // the same darkening the island's glass uses (BackdropWindow): #141418 at up to 85%; no glass: plain black
        var tint = new Border { IsHitTestVisible = false, Background = s.BlurEnabled
            ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(s.BlurTint, 0, 100) / 100.0 * 0.85 * 255), 0x14, 0x14, 0x18))
            : Brushes.Black };
        var g = new GlassLayer
        {
            Clear = new Border { Background = clearBrush, IsHitTestVisible = false },
            // the island's own curve (BackdropWindow): mixing sharp and blurred only looks blurred near the top, so it eases in
            Blur = new Border { Background = blurBrush, IsHitTestVisible = false, Opacity = 1 - Math.Pow(1 - Math.Clamp(s.BlurIntensity / 100.0, 0, 1), 3.2) },
            Tint = tint, ClearBrush = clearBrush, BlurBrush = blurBrush
        };
        if (!s.BlurEnabled) g.Clear.Visibility = g.Blur.Visibility = Visibility.Collapsed;
        host.Children.Insert(0, tint);
        host.Children.Insert(0, g.Blur);
        host.Children.Insert(0, g.Clear);
        return g;
    }

    /// <summary>The layers show what's behind <paramref name="r"/> (window coordinates), rounded like it.</summary>
    private void PlaceGlass(GlassLayer g, Rect r, CornerRadius corners)
    {
        g.Clear.CornerRadius = g.Blur.CornerRadius = g.Tint.CornerRadius = corners;
        if (!AppSettings.Current.BlurEnabled) return;
        _blurred ??= MakeBlurred();
        if (g.BlurBrush.ImageSource == null) g.BlurBrush.ImageSource = _blurred;
        var px = new Rect(r.X * Kx, r.Y * Ky, Math.Max(1, r.Width * Kx), Math.Max(1, r.Height * Ky));
        g.ClearBrush.Viewbox = px;
        g.BlurBrush.Viewbox = new Rect(px.X * BlurScale, px.Y * BlurScale, px.Width * BlurScale, px.Height * BlurScale);
    }

    /// <summary>The frozen screen, blurred once (small, then a soft blur: smooth when stretched back).</summary>
    private BitmapSource MakeBlurred() => Blurred(_src, BlurScale, BlurRadius);

    internal static BitmapSource Blurred(BitmapSource src, double scale, double radius)
    {
        int w = Math.Max(1, (int)(src.PixelWidth * scale)), h = Math.Max(1, (int)(src.PixelHeight * scale));
        var small = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        var visual = new DrawingVisual { Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian } };
        using (var dc = visual.RenderOpen())
        {
            // a stretched copy behind fills past the edges (so the blur doesn't darken them); the real one on top, exactly in place
            dc.DrawImage(small, new Rect(-radius * 3, -radius * 3, w + radius * 6, h + radius * 6));
            dc.DrawImage(small, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

#if DEBUG
    /// <summary>The island's glass as the bar draws it, over a picture of what's behind (for the comparison test).</summary>
    internal static BitmapSource TestGlassOver(BitmapSource behind, double scale, double radius)
    {
        var s = AppSettings.Current;
        var blurred = Blurred(behind, scale, radius);
        double w = behind.PixelWidth, h = behind.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(behind, new Rect(0, 0, w, h));
            dc.PushOpacity(1 - Math.Pow(1 - Math.Clamp(s.BlurIntensity / 100.0, 0, 1), 3.2));
            dc.DrawImage(blurred, new Rect(0, 0, w, h));
            dc.Pop();
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(s.BlurTint, 0, 100) / 100.0 * 0.85 * 255), 0x14, 0x14, 0x18)), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
#endif

    private void PlaceBar()
    {
        _barRect = BarSpot();
        LayBar(_barRect, BarRadius);
    }

    /// <summary>
    /// The island lets go of a drop that becomes the bar: it stretches down out of the island (0–0.12 s), travels
    /// along a curve to its place under the area while rounding into a capsule (to 0.46 s), then opens sideways
    /// with a soft spring (to 0.7 s); the tools appear from the middle outwards, and last the eyes peek up.
    /// </summary>
    private void ShowBar()
    {
        _barRect = BarSpot();
        var to = _barRect;
        foreach (var e in new UIElement[] { _bar, _barShadow, _tab }) e.Visibility = Visibility.Visible;
        _tab.Opacity = 0;
        _barInner.Opacity = 1;
        foreach (UIElement c in _row.Children) c.Opacity = 0;
        _barShadow.Opacity = 0;

        double h0 = 34, w0 = Math.Max(90, _islandW);
        var start = new Point(_islandAt.X, _islandAt.Y);
        double capW = Math.Min(150, to.Width), capH = to.Height;
        var end = new Point(to.X + to.Width / 2, to.Y + to.Height / 2);
        var stretched = new Point(start.X, start.Y + 10);
        var control = new Point(stretched.X + (end.X - stretched.X) * 0.15, end.Y - (end.Y - stretched.Y) * 0.15);
        double rowW = to.Width;
        var centers = _row.Children.Cast<FrameworkElement>().Select(c => { c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return c; }).ToList();
        // where each child sits along the row (0 = middle, 1 = an end), for the middle-out reveal
        var spread = new List<double>();
        double acc = 8;
        foreach (var c in centers)
        {
            double cw = c.DesiredSize.Width + c.Margin.Left + c.Margin.Right;
            spread.Add(Math.Abs(acc + cw / 2 - rowW / 2) / (rowW / 2));
            acc += cw;
        }

        const double Pull = 0.12, Travel = 0.46, Open = 0.70, Total = 0.88;
        var clock = Stopwatch.StartNew();
        double speed = Math.Max(AppSettings.Current.SpeedFactor, 0.01);
        static double Smooth(double u) => u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
        static double Out(double u) => 1 - Math.Pow(1 - u, 3);
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        void Frame(object? s, EventArgs e)
        {
            double t = clock.Elapsed.TotalSeconds / speed;
            if (!At(t)) return;
            CompositionTarget.Rendering -= Frame;
        }
        bool At(double t)
        {
            Rect r;
            double radius;
            if (t < Pull)
            {
                // the drop stretches down out of the island
                double u = Out(t / Pull);
                double w = Lerp(w0, w0 * 0.8, u), h = Lerp(h0, h0 + 10, u);
                r = new Rect(start.X - w / 2, start.Y - h0 / 2, w, h);
                radius = Math.Min(w, h) / 2;
            }
            else if (t < Travel)
            {
                // along the curve, rounding into a capsule; a little longer while it's fastest
                double raw = (t - Pull) / (Travel - Pull), u = Smooth(raw);
                double bx = (1 - u) * (1 - u) * stretched.X + 2 * (1 - u) * u * control.X + u * u * end.X;
                double by = (1 - u) * (1 - u) * stretched.Y + 2 * (1 - u) * u * control.Y + u * u * end.Y;
                double speedK = Math.Sin(Math.PI * raw);
                double w = Lerp(w0 * 0.8, capW, u) * (1 - 0.08 * speedK), h = Lerp(h0 + 10, capH, u) * (1 + 0.1 * speedK);
                r = new Rect(bx - w / 2, by - h / 2, w, h);
                radius = Math.Min(w, h) / 2;
            }
            else
            {
                // opens sideways with a soft spring
                double u = Math.Min((t - Travel) / (Open - Travel), 1);
                double spring = 1 - Math.Exp(-7 * u) * Math.Cos(7 * u); // (about 4% past, then settles)
                spring = u >= 1 ? 1 : spring;
                double w = Lerp(capW, to.Width, spring);
                r = new Rect(end.X - w / 2, to.Y, w, to.Height);
                radius = Lerp(capH / 2, BarRadius, Math.Min(u * 1.5, 1));
                _barShadow.Opacity = Math.Min(1, u * 2);
            }
            LayBar(r, radius);
            // the tools, middle first; then the eyes peek up out of the bar
            for (int i = 0; i < centers.Count; i++)
            {
                double begin = Travel + 0.06 + 0.14 * spread[i], v = Math.Clamp((t - begin) / 0.14, 0, 1);
                centers[i].Opacity = Out(v);
                centers[i].RenderTransform = new TranslateTransform(0, 4 * (1 - Out(v)));
            }
            double peek = Math.Clamp((t - (Open - 0.08)) / 0.18, 0, 1);
            double back = peek >= 1 ? 1 : 1 + 2.4 * Math.Pow(peek - 1, 3) + 1.4 * Math.Pow(peek - 1, 2); // (a little overshoot)
            _tab.Opacity = Math.Min(1, peek * 3);
            _tabMove.Y = (1 - back) * 14;
            _tabScale.ScaleX = _tabScale.ScaleY = 0.6 + 0.4 * back;
            if (t < Total) return false;
            LayBar(to, BarRadius);
            foreach (var c in centers)
            {
                c.Opacity = 1;
                c.RenderTransform = null;
            }
            _barShadow.Opacity = 1;
            _tab.Opacity = 1;
            _tabMove.Y = 0;
            _tabScale.ScaleX = _tabScale.ScaleY = 1;
            return true;
        }
#if DEBUG
        _barAt = At;
        if (_holdBar) return; // (a test steps through it by hand)
#endif
        CompositionTarget.Rendering += Frame;
        StartEyes();
    }

    /// <summary>The eyes in the tab: they blink, follow the mouse, and smile ^ ^ when you pick a tool.</summary>
    private void StartEyes()
    {
        if (_eyesRunning) return;
        _eyesRunning = true;
        _eyesClock = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += EyesFrame;
    }

    private void EyesFrame(object? sender, EventArgs e)
    {
        if (_phase == Phase.Flying || !IsVisible)
        {
            CompositionTarget.Rendering -= EyesFrame;
            _eyesRunning = false;
            return;
        }
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Clamp(now - _eyesClock, 0, 0.1);
        _eyesClock = now;
        bool happy = now < _happyUntil;
        double blink = 1;
        if (now > _nextBlink)
        {
            double b = now - _nextBlink;
            blink = b < 0.06 ? 1 - b / 0.06 * 0.85 : b < 0.13 ? 0.15 + (b - 0.06) / 0.07 * 0.85 : 1;
            if (b >= 0.13) _nextBlink = now + 2.2 + Random.Shared.NextDouble() * 2.6;
        }
        foreach (var (eye, scale, smile) in _eyes)
        {
            eye.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
            smile.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
            scale.ScaleY = blink;
        }
        // where the mouse is, seen from the tab
        var p = Mouse.GetPosition(_tab);
        double lx = Math.Clamp((p.X - TabW / 2) / 140, -1, 1) * 3.2, ly = Math.Clamp((p.Y - TabH / 2) / 90, -1, 1) * 2;
        double k = 1 - Math.Exp(-dt / 0.08);
        _eyesLook.X += (lx - _eyesLook.X) * k;
        _eyesLook.Y += (ly - _eyesLook.Y) * k;
    }

    private void OpenPop()
    {
        CommitText();
        _pop.Visibility = Visibility.Visible;
        _pop.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var at = _dot.TranslatePoint(new Point(16, 0), _stage);
        var mon = MonitorOf(_barRect);
        bool below = _barRect.Bottom + 10 + _pop.DesiredSize.Height < mon.Bottom;
        Canvas.SetLeft(_pop, Math.Clamp(at.X - _pop.DesiredSize.Width / 2, mon.Left + 8, mon.Right - _pop.DesiredSize.Width - 8));
        Canvas.SetTop(_pop, below ? _barRect.Bottom + 10 : _barRect.Top - TabH - 10 - _pop.DesiredSize.Height);
        _pop.RenderTransformOrigin = new Point(0.5, below ? 0 : 1);
        PlaceGlass(_popGlass, new Rect(Canvas.GetLeft(_pop), Canvas.GetTop(_pop), _pop.DesiredSize.Width, _pop.DesiredSize.Height), new CornerRadius(16));
        _tip.Opacity = 0;
        var time = TimeSpan.FromMilliseconds(Ms(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _pop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        var s = (ScaleTransform)_pop.RenderTransform;
        s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, time) { EasingFunction = ease });
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, time) { EasingFunction = ease });
    }

    private void ClosePop() => _pop.Visibility = Visibility.Collapsed;

    // =====================================================================================
    // the picture, and its trip to the island
    // =====================================================================================

    /// <summary>The area with the marks on it, in device pixels.</summary>
    private BitmapSource Compose()
    {
        int pw = Math.Max(1, (int)Math.Round(_sel.Width * Kx)), ph = Math.Max(1, (int)Math.Round(_sel.Height * Ky));
        var px = new Int32Rect((int)Math.Round(_sel.X * Kx), (int)Math.Round(_sel.Y * Ky), pw, ph);
        px.Width = Math.Min(px.Width, _src.PixelWidth - px.X);
        px.Height = Math.Min(px.Height, _src.PixelHeight - px.Y);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(new CroppedBitmap(_src, px), new Rect(0, 0, _sel.Width, _sel.Height));
            dc.PushTransform(new TranslateTransform(-_sel.X, -_sel.Y));
            var all = new Rect(0, 0, Width, Height);
            dc.DrawRectangle(new VisualBrush(_annot) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = all, ViewportUnits = BrushMappingMode.Absolute, Viewport = all,
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, all);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap(px.Width, px.Height, 96 * Kx, 96 * Ky, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private async Task Finish(CaptureAction action)
    {
        if (_phase != Phase.Editing) return;
        CommitText();
        ClosePop();
        var picture = Compose();
        var at = new Int32Rect((int)(_sel.X * Kx) + _screenPx.Left, (int)(_sel.Y * Ky) + _screenPx.Top, picture.PixelWidth, picture.PixelHeight);
        // the work happens now, before the trip (so nothing stalls the animation)
        string? file = null;
        if (action == CaptureAction.Save)
        {
            file = CaptureFiles.Save(picture, this);
            if (file == null) return; // (the folder window was closed: back to editing)
        }
        else if (action == CaptureAction.Copy) CaptureFiles.Copy(picture);
        _phase = Phase.Flying;
        if (action == CaptureAction.Pin)
        {
            Hide();
            Done?.Invoke(action, picture, at, null);
            return;
        }
        Returning?.Invoke();
        if (_islandPatch != null) _islandPatch.Visibility = Visibility.Visible;
        await Fly(picture, () => Done?.Invoke(action, picture, at, file));
    }

    private void Cancel()
    {
        if (_phase == Phase.Flying) return;
        _phase = Phase.Flying;
        Close();
        Cancelled?.Invoke();
    }

    /// <summary>
    /// The picture goes up to the island (D, "rastro de luz"), all on one timeline: a flash; then it glides along a
    /// curve, shrinking and melting into the island, with a trail of light that follows it in time; the screen
    /// around brightens. When it's in, <paramref name="arrived"/> runs (the real island takes over and opens into its
    /// notice) while this window fades away over it, so the hand-over has no jump.
    /// </summary>
    private async Task Fly(BitmapSource picture, Action arrived)
    {
        // from here on this window only shows the picture going up: no clicks for it
        IsHitTestVisible = false;
        System.Windows.Input.Mouse.Capture(null);
        foreach (var e in new UIElement[] { _bar, _barShadow, _tab, _tip, _frame, _size, _hover, _lens, _pop }) e.Visibility = Visibility.Collapsed;
        foreach (var h in _handles) h.Visibility = Visibility.Collapsed;
        _annot.Visibility = Visibility.Collapsed;
        // The picture flies in a window of its own, see-through and click-through (a layered window: this full-screen one
        // can't be, WPF won't let it), over the stretch from the picked area to the island. This one goes away as soon as
        // the dimming is gone, so clicks reach the windows underneath everywhere, even where the picture was taken.
        var c0 = new Point(_sel.X + _sel.Width / 2, _sel.Y + _sel.Height / 2);
        var to = new Point(_islandAt.X, _islandAt.Y);
        var cp = new Point(c0.X + (to.X - c0.X) * 0.2 + 50, to.Y + Math.Max(60, (c0.Y - to.Y) * 0.25));
        var box = _sel;
        box.Union(new Rect(to.X - 30, to.Y - 30, 60, 60));
        box.Union(new Rect(cp.X - 30, cp.Y - 30, 60, 60));
        box.Inflate(40, 40);
        var flyCanvas = new Canvas { IsHitTestVisible = false, RenderTransform = new TranslateTransform(-box.X, -box.Y) };
        var flyWindow = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Left = Left + box.X, Top = Top + box.Y, Width = box.Width, Height = box.Height,
            Content = flyCanvas, IsHitTestVisible = false, Title = "Notchy"
        };
        flyWindow.SourceInitialized += (_, _) => Native.MakeClickThrough(new System.Windows.Interop.WindowInteropHelper(flyWindow).Handle);

        var shot = new Image { Source = picture, Width = _sel.Width, Height = _sel.Height, Stretch = Stretch.Fill, RenderTransformOrigin = new Point(0.5, 0.5) };
        RenderOptions.SetBitmapScalingMode(shot, BitmapScalingMode.LowQuality); // (smooth enough while it shrinks, and cheap)
        Place(shot, _sel);
        flyCanvas.Children.Add(shot);
        var flash = new Rectangle { Fill = Brushes.White, IsHitTestVisible = false };
        Place(flash, _sel);
        flyCanvas.Children.Add(flash);
        flash.BeginAnimation(OpacityProperty, new DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(Ms(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // (quick: once it's gone this window goes, and the real screen is there, the same as this picture of it)
        _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Ms(150))));

        // the trail: soft dots of light (a gradient, no effects: cheap to move every frame)
        var glow = new RadialGradientBrush(Colors.White, Color.FromArgb(0, Ice.R, Ice.G, Ice.B)) { GradientStops = { new GradientStop(Color.FromArgb(0xCC, Ice.R, Ice.G, Ice.B), 0.45) } };
        glow.Freeze();
        var ghosts = Enumerable.Range(0, 12).Select(i => new Ellipse { Width = 20 - i, Height = 20 - i, Fill = glow, IsHitTestVisible = false, Opacity = 0 }).ToList();
        foreach (var g in ghosts) flyCanvas.Children.Add(g);
        flyWindow.Show();
        Returning?.Invoke(); // (the island back on top of the picture, which melts into it)
        Point At(double u)
        {
            double k = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
            return new Point((1 - k) * (1 - k) * c0.X + 2 * (1 - k) * k * cp.X + k * k * to.X, (1 - k) * (1 - k) * c0.Y + 2 * (1 - k) * k * cp.Y + k * k * to.Y);
        }
        static double Smooth(double a, double b, double x)
        {
            double t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }
        var scale = new ScaleTransform();
        var move = new TranslateTransform();
        shot.RenderTransform = new TransformGroup { Children = { scale, move } };

        const double Start = 0.10, Trip = 0.62, Fade = 0.22, GhostLag = 0.022;
        double arrive = Start + Trip, end = arrive + Math.Max(Fade, GhostLag * ghosts.Count) + 0.04;
        bool handedOver = false, gone = false;
        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        double speed = Math.Max(AppSettings.Current.SpeedFactor, 0.01);
        void Frame(object? s, EventArgs e) => Step(clock.Elapsed.TotalSeconds / speed);
        void Step(double t)
        {
            double u = Math.Clamp((t - Start) / Trip, 0, 1);
            var p = At(u);
            double k = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
            scale.ScaleX = scale.ScaleY = 1 - 0.95 * k;
            move.X = p.X - c0.X;
            move.Y = p.Y - c0.Y;
            shot.Opacity = 1 - Smooth(0.8, 1, u); // (melts into the island instead of popping out)
            for (int i = 0; i < ghosts.Count; i++)
            {
                double ui = Math.Clamp((t - Start - (i + 1) * GhostLag) / Trip, 0, 1);
                var q = At(ui);
                Canvas.SetLeft(ghosts[i], q.X - ghosts[i].Width / 2);
                Canvas.SetTop(ghosts[i], q.Y - ghosts[i].Height / 2);
                ghosts[i].Opacity = (1 - i / (double)ghosts.Count) * 0.85 * Smooth(0.04, 0.16, ui) * (1 - Smooth(0.86, 1, ui));
            }
            if (!gone && t >= 0.16)
            {
                gone = true;
                Hide(); // (the dimming is gone: the real screen looks the same as this picture of it)
            }
            if (!handedOver && t >= arrive)
            {
                handedOver = true;
                arrived();
            }
            if (t >= arrive) flyWindow.Opacity = 1 - Smooth(arrive, arrive + Fade, t);
            if (t < end) return;
            CompositionTarget.Rendering -= Frame;
            done.TrySetResult();
        }
#if DEBUG
        _flyAt = Step;
        if (!_holdFly)
#endif
        CompositionTarget.Rendering += Frame;
        await done.Task;
        Hide();
        flyWindow.Close();
    }

    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int index, int value);
    /// <summary>Editar on the island's notice: back to the same area and marks.</summary>
    public void Reopen()
    {
        if (_islandPatch != null) _islandPatch.Visibility = Visibility.Collapsed;
        _phase = Phase.Editing;
        _dim.BeginAnimation(OpacityProperty, null);
        _dim.Opacity = 1;
        _annot.Visibility = Visibility.Visible;
        Show();
        Activate();
        SetSel(_sel);
        ShowBar();
    }

#if DEBUG
    // ---- snapshot tests
    public void TestHover(Point p) => OnMove(p);
    public void TestSelect(Rect r)
    {
        _down = r.TopLeft;
        _dragging = true;
        OnMove(r.BottomRight);
        OnUp(r.BottomRight);
    }
    public void TestDrag(Rect r)
    {
        _dragging = true;
        _down = r.TopLeft;
        OnMove(new Point(r.X + r.Width * 0.6, r.Y + r.Height * 0.6));
    }
    public void TestMark(string tool, Point a, Point b)
    {
        SetTool(Enum.Parse<Tool>(tool));
        _down = a;
        _drawing = StartMark(a);
        if (_drawing == null) return;
        _annot.Children.Add(_drawing);
        for (int i = 1; i <= 8; i++) UpdateMark(new Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8 + Math.Sin(i) * (tool == "Pen" ? 8 : 0)));
        FinishMark(b);
    }
    public void TestText(Point p, string text)
    {
        SetTool(Tool.Text);
        StartText(p);
        _typing!.Text = text;
        CommitText();
    }
    public void TestColor(int i)
    {
        _color = Palette[i];
        ((Ellipse)_dot.Child).Fill = Ink;
    }
    public void TestPop(bool open) { if (open) OpenPop(); else ClosePop(); }
    public Task TestFinish(CaptureAction a) => Finish(a);
    public void TestCancel() => Cancel();
    private Action<double>? _flyAt;
    private bool _holdFly;
    /// <summary>The next trip to the island waits; <see cref="TestFlyAt"/> draws any moment of it.</summary>
    public void TestHoldFly() => _holdFly = true;
    public void TestFlyAt(double t) => _flyAt?.Invoke(t);
    /// <summary>The bar's glass alone, over <paramref name="r"/> (window coordinates), saved as a picture.</summary>
    public void TestGlass(string file, Rect r)
    {
        var host = new Grid { Width = r.Width, Height = r.Height };
        var g = Glass(host);
        PlaceGlass(g, r, new CornerRadius(r.Height / 2));
        host.Measure(new Size(r.Width, r.Height));
        host.Arrange(new Rect(0, 0, r.Width, r.Height));
        var bmp = new RenderTargetBitmap((int)(r.Width * Kx), (int)(r.Height * Ky), 96 * Kx, 96 * Ky, PixelFormats.Pbgra32);
        bmp.Render(host);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(file);
        png.Save(f);
    }
    private Func<double, bool>? _barAt;
    private bool _holdBar;
    /// <summary>The next toolbar entrance waits; <see cref="TestBarAt"/> draws any moment of it.</summary>
    public void TestHoldBar() => _holdBar = true;
    public void TestBarAt(double t) => _barAt?.Invoke(t);
    public void TestSaveRegion(string file, Rect region)
    {
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
            dc.DrawRectangle(new VisualBrush(_stage) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = region, Stretch = Stretch.Fill }, null, new Rect(0, 0, region.Width, region.Height));
        var bmp = new RenderTargetBitmap((int)region.Width, (int)region.Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(file);
        png.Save(f);
    }
    public Rect TestSel => _sel;
    public void TestWheel(int delta) => OnElementWheel(this, new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = MouseWheelEvent });
    public string TestLit => $"aceso={_lit.X:0},{_lit.Y:0} {_lit.Width:0}x{_lit.Height:0} níveis={_chain.Count} subiu={_levelsUp} buraco={_spotHole.Rect.Width:0}x{_spotHole.Rect.Height:0}";
    public Point TestIsland => _islandAt;

    /// <summary>The window's picture, half size.</summary>
    public void TestSave(string file)
    {
        double k = 0.5;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
            dc.DrawRectangle(new VisualBrush(_stage) { Stretch = Stretch.Fill }, null, new Rect(0, 0, ActualWidth * k, ActualHeight * k));
        var bmp = new RenderTargetBitmap((int)(ActualWidth * k), (int)(ActualHeight * k), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(file);
        png.Save(f);
    }
#endif

    // =====================================================================================
    // helpers
    // =====================================================================================

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    /// <summary>Visible top-level windows of other apps, front to back, as they look (device pixels).</summary>
    private static List<(IntPtr h, System.Drawing.Rectangle r)> TopWindows()
    {
        var list = new List<(IntPtr, System.Drawing.Rectangle)>();
        int me = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out int pid);
            if (pid == me) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if ((GetWindowLong(h, -20) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return true;
            if (DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40) return true;
            list.Add((h, new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
