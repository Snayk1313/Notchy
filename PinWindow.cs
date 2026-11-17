using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// A capture pinned over every window (F1): only the picture, with Windows' own rounded corners and shadow. With
/// the mouse on it, Copiar, Salvar and Fechar show in a corner (their name sliding under them) and the zoom in
/// the other (a click on it goes back to 100%). Its edges and corners resize it the way any window resizes: Windows
/// does the dragging, Notchy only keeps the picture's shape. The wheel zooms into the picture around the mouse
/// (the window stays its size), Space + drag moves the zoomed picture, a drag moves the window, a double click closes it.
/// </summary>
public sealed class PinWindow : Window
{
    private const double MinWidth0 = 90, MaxZoom = 8;
    private readonly BitmapSource _picture;
    private readonly double _aspect;
    private readonly Border _tools, _zoomTag, _tip, _view;
    private readonly TextBlock _zoomText = new() { FontSize = 10, Foreground = Brushes.White };
    private readonly TextBlock _tipText = new() { FontSize = 10.5, Foreground = Brushes.White };
    private readonly MatrixTransform _zoomMatrix = new();
    private readonly Grid _body = new();
    private readonly TranslateTransform _tipMove = new();
    private bool _panning;
    private Point _panFrom;
    private Matrix _panMatrix;
    private RECT _sizeStart;     // the window when a resize began (device pixels)

    public PinWindow(BitmapSource picture, Int32Rect at, Action<BitmapSource> copy, Action<BitmapSource, Window> save)
    {
        _picture = picture;
        _aspect = (double)at.Width / Math.Max(1, at.Height);
        // a normal window (not a transparent one): Windows resizes it smoothly and draws its corners and shadow
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Black;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), GlassFrameThickness = new Thickness(0, 0, 0, 1), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false
        });
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Notchy";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        // device pixels → this window's units (the main monitor's scale)
        var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
        Left = at.X / dpi.DpiScaleX;
        Top = at.Y / dpi.DpiScaleY;
        Width = at.Width / dpi.DpiScaleX;
        Height = at.Height / dpi.DpiScaleY;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };

        // the picture, clipped to the window's rounded shape; the zoom moves it inside
        var image = new Image { Source = picture, Stretch = Stretch.Fill, RenderTransform = _zoomMatrix };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        _view = new Border { Child = image, ClipToBounds = true, Background = Brushes.Black };
        _view.SizeChanged += (_, e) =>
        {
            // resized: the same zoom on the same spot (the offset grows with the window)
            if (e.PreviousSize.Width > 1)
            {
                var m = _zoomMatrix.Matrix;
                m.OffsetX *= e.NewSize.Width / e.PreviousSize.Width;
                m.OffsetY *= e.NewSize.Height / e.PreviousSize.Height;
                _zoomMatrix.Matrix = m;
            }
            KeepInside();
        };
        // the buttons; their name shows right under them at once (Windows' tooltips lagged here)
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button("M7,5.5 H12 A1.5,1.5 0 0 1 13.5,7 V12 A1.5,1.5 0 0 1 12,13.5 H7 A1.5,1.5 0 0 1 5.5,12 V7 A1.5,1.5 0 0 1 7,5.5 Z M10.5,5.5 V3.5 A1,1 0 0 0 9.5,2.5 H3.5 A1,1 0 0 0 2.5,3.5 V9.5 A1,1 0 0 0 3.5,10.5 H5.5",
            Loc.T("cap.copy"), () => copy(_picture)));
        row.Children.Add(Button("M8,2.5 V10.5 M4.5,7 L8,10.5 L11.5,7 M2.5,13.5 H13.5", Loc.T("cap.save.short"), () => save(_picture, this)));
        row.Children.Add(Button("M4,4 L12,12 M12,4 L4,12", Loc.T("cap.close"), Close));
        _tools = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0, 0, 0)), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = row,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), Opacity = 0
        };
        _tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 3), Child = _tipText,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), Opacity = 0, IsHitTestVisible = false,
            RenderTransform = _tipMove
        };
        _zoomTag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2, 7, 2), Child = _zoomText,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 6), Opacity = 0,
            Cursor = Cursors.Hand, ToolTip = null
        };
        // a click on the % goes back to 100%
        _zoomTag.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ResetZoom();
        };
        var body = _body;
        body.SizeChanged += (_, _) =>
        {
            // (resizing: the name would be left where the button was)
            _tip.BeginAnimation(OpacityProperty, null);
            _tip.Opacity = 0;
        };
        body.Children.Add(_view);
        body.Children.Add(_tools);
        body.Children.Add(_tip);
        body.Children.Add(_zoomTag);
        Content = body;
        UpdateZoomTag();

        body.MouseEnter += (_, _) => ShowTools(true);
        body.MouseLeave += (_, _) => ShowTools(false);
        // with zoom, Space + drag moves the picture inside (a drag alone still moves the window)
        _view.MouseLeftButtonDown += (_, e) =>
        {
            if (Zoomed && SpaceDown())
            {
                _panFrom = e.GetPosition(_view);
                _panMatrix = _zoomMatrix.Matrix;
                _panning = true;
                _view.CaptureMouse();
                _view.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
            DragMove();
        };
        _view.MouseMove += (_, e) =>
        {
            if (!_panning)
            {
                _view.Cursor = Zoomed && SpaceDown() ? Cursors.Hand : null;
                return;
            }
            var m = _panMatrix;
            var v = e.GetPosition(_view) - _panFrom;
            m.OffsetX += v.X;
            m.OffsetY += v.Y;
            _zoomMatrix.Matrix = m;
            KeepInside();
        };
        _view.MouseLeftButtonUp += (_, _) =>
        {
            if (!_panning) return;
            _panning = false;
            _view.ReleaseMouseCapture();
            _view.Cursor = Zoomed && SpaceDown() ? Cursors.Hand : null;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Space && Zoomed && _view.IsMouseOver) _view.Cursor = Cursors.Hand; };
        KeyUp += (_, e) => { if (e.Key == Key.Space && !_panning) _view.Cursor = null; };
        MouseWheel += (_, e) => ZoomAt(e.GetPosition(_view), e.Delta > 0 ? 1.15 : 1 / 1.15);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Opacity = 0;
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180 * AppSettings.Current.SpeedFactor)));
    }

    private Border Button(string icon, string name, Action click)
    {
        var b = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Child = new Path { Data = Geometry.Parse(icon), Stroke = Brushes.White, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, RenderTransform = new ScaleTransform(0.8, 0.8, 8, 8) }
        };
        b.MouseEnter += (_, _) =>
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
            ShowTip(b, name);
        };
        b.MouseLeave += (_, _) =>
        {
            b.Background = Brushes.Transparent;
            _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Ms(160))) { BeginTime = TimeSpan.FromMilliseconds(Ms(90)) });
        };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return b;
    }

    /// <summary>
    /// The name under the button: from one button to the next it slides over and the words cross-fade; from
    /// nothing it fades in where it belongs.
    /// </summary>
    private void ShowTip(FrameworkElement button, string name)
    {
        bool shown = _tip.Opacity > 0.4;
        _tipText.Text = name;
        _tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = button.TranslatePoint(new Point(button.ActualWidth / 2, 0), _body).X - _tip.DesiredSize.Width / 2;
        x = Math.Min(x, _body.ActualWidth - 4 - _tip.DesiredSize.Width);
        var time = TimeSpan.FromMilliseconds(Ms(170));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (shown)
        {
            _tipMove.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, time) { EasingFunction = ease });
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

    private static double Ms(double ms) => ms * AppSettings.Current.SpeedFactor;
    private bool Zoomed => _zoomMatrix.Matrix.M11 > 1.0001;
    private static bool SpaceDown() => GetAsyncKeyState(0x20) < 0;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    // ---- resizing: Windows drags the edge, Notchy keeps the picture's shape (WM_SIZING)

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0231) // WM_ENTERSIZEMOVE
            GetWindowRect(hwnd, out _sizeStart);
        else if (msg == 0x0214) // WM_SIZING
        {
            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(lParam);
            Fit(wParam.ToInt32(), ref r);
            System.Runtime.InteropServices.Marshal.StructureToPtr(r, lParam, false);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The rect Windows proposes, made the picture's shape. An edge sets the size by itself; a corner's move is
    /// taken along the picture's diagonal, so the size follows the mouse smoothly. The side(s) not dragged stay put.
    /// </summary>
    private void Fit(int edge, ref RECT r)
    {
        double w0 = _sizeStart.Right - _sizeStart.Left, h0 = _sizeStart.Bottom - _sizeStart.Top;
        double w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (edge is 4 or 5 or 7 or 8)
        {
            double t = ((w - w0) * w0 + (h - h0) * h0) / (w0 * w0 + h0 * h0);
            w = w0 * (1 + t);
        }
        else if (edge is 3 or 6) w = h * _aspect; // top / bottom
        double min = MinWidth0 * VisualTreeHelper.GetDpi(this).DpiScaleX;
        w = Math.Max(min, w);
        int W = (int)Math.Round(w), H = (int)Math.Round(w / _aspect);
        if (edge is 1 or 4 or 7) r.Left = r.Right - W; // left side dragged: the right one stays
        else r.Right = r.Left + W;
        if (edge is 3 or 4 or 5) r.Top = r.Bottom - H; // top dragged: the bottom stays
        else r.Bottom = r.Top + H;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>The picture zooms around the mouse; the window keeps its size.</summary>
    private void ZoomAt(Point p, double factor)
    {
        var m = _zoomMatrix.Matrix;
        double now = m.M11, next = Math.Clamp(now * factor, 1, MaxZoom);
        factor = next / now;
        if (Math.Abs(factor - 1) < 1e-6) return;
        m.ScaleAt(factor, factor, p.X, p.Y);
        _zoomMatrix.Matrix = m;
        KeepInside();
        UpdateZoomTag();
    }

    /// <summary>A zoomed picture never shows an empty edge.</summary>
    private void KeepInside()
    {
        var m = _zoomMatrix.Matrix;
        double w = _view.ActualWidth, h = _view.ActualHeight, k = m.M11;
        if (k <= 1.0001)
        {
            _zoomMatrix.Matrix = Matrix.Identity;
            return;
        }
        m.OffsetX = Math.Clamp(m.OffsetX, w - w * k, 0);
        m.OffsetY = Math.Clamp(m.OffsetY, h - h * k, 0);
        _zoomMatrix.Matrix = m;
    }

    /// <summary>Back to the whole picture, gliding there.</summary>
    private void ResetZoom()
    {
        var from = _zoomMatrix.Matrix;
        if (from.IsIdentity) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double total = Ms(240) / 1000;
        void Frame(object? s, EventArgs e)
        {
            double u = Math.Min(clock.Elapsed.TotalSeconds / Math.Max(total, 0.001), 1), k = 1 - Math.Pow(1 - u, 3);
            double scale = from.M11 + (1 - from.M11) * k;
            _zoomMatrix.Matrix = new Matrix(scale, 0, 0, scale, from.OffsetX * (1 - k), from.OffsetY * (1 - k));
            UpdateZoomTag();
            if (u < 1) return;
            CompositionTarget.Rendering -= Frame;
            _zoomMatrix.Matrix = Matrix.Identity;
            UpdateZoomTag();
        }
        CompositionTarget.Rendering += Frame;
    }

    private void UpdateZoomTag() => _zoomText.Text = $"{Math.Round(_zoomMatrix.Matrix.M11 * 100)}%";

    private void ShowTools(bool on)
    {
        var time = TimeSpan.FromMilliseconds((on ? 160 : 240) * AppSettings.Current.SpeedFactor);
        _tools.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, time));
        _zoomTag.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, time));
        if (!on) _tip.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
    }

#if DEBUG
    public void TestZoom(Point p, double factor) => ZoomAt(p, factor);
    public void TestTip(int i) => ((Border)((StackPanel)_tools.Child).Children[i]).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = MouseEnterEvent });
    public void TestShowTools() => ShowTools(true);
    public void TestResetZoom() => ResetZoom();
    /// <summary>The bottom-left corner dragged along a wobbly path (as Windows would propose it): the widths.</summary>
    public System.Collections.Generic.List<double> TestSweep()
    {
        var widths = new System.Collections.Generic.List<double>();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out _sizeStart);
        for (int i = 0; i <= 40; i++)
        {
            int dx = -i * 4, dy = (int)(i * 1.5 + Math.Sin(i) * 6); // outwards, with the hand wobbling up and down
            var r = new RECT { Left = _sizeStart.Left + dx, Top = _sizeStart.Top, Right = _sizeStart.Right, Bottom = _sizeStart.Bottom + dy };
            Fit(7, ref r);
            widths.Add(r.Right - r.Left);
            if (i == 40) SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0014);
        }
        return widths;
    }
    public void TestPan(Vector by)
    {
        var m = _zoomMatrix.Matrix;
        m.OffsetX += by.X;
        m.OffsetY += by.Y;
        _zoomMatrix.Matrix = m;
        KeepInside();
    }
    public string TestZoomState => $"{_zoomMatrix.Matrix.M11:0.00}x · deslocamento {_zoomMatrix.Matrix.OffsetX:0},{_zoomMatrix.Matrix.OffsetY:0} · área {_view.ActualWidth:0}x{_view.ActualHeight:0}";
    public void TestResize(int corner, Vector by)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out _sizeStart);
        var dpi = VisualTreeHelper.GetDpi(this);
        int dx = (int)(by.X * dpi.DpiScaleX), dy = (int)(by.Y * dpi.DpiScaleY);
        var r = _sizeStart;
        int edge = corner switch { 0 => 4, 1 => 5, 2 => 8, _ => 7 };
        if (edge is 4 or 7) r.Left += dx; else r.Right += dx;
        if (edge is 4 or 5) r.Top += dy; else r.Bottom += dy;
        Fit(edge, ref r);
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, 0x0014);
    }
    public void TestSave(string file)
    {
        var bmp = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5B, 0x2A, 0x86)), null, new Rect(0, 0, ActualWidth, ActualHeight));
            var all = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(new VisualBrush((Visual)Content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = all, Stretch = Stretch.None }, null, all);
        }
        bmp.Render(v);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = System.IO.File.Create(file);
        png.Save(f);
    }
#endif
}
