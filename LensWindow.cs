using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// "Circle to search": freezes the screen, dims it, and lets you draw around anything with the mouse.
/// The area you drew around is saved as a PNG for Notchy Chat. Esc or right click cancels.
/// </summary>
public sealed class LensWindow : Window
{
    private readonly System.Drawing.Bitmap _shot;
    private readonly System.Drawing.Rectangle _screenPx;
    private readonly string _saveDir;
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly Grid _root = new();
    private readonly System.Windows.Shapes.Path _dim = new();
    private readonly Polyline _line = new();
    private readonly Border _hint;
    private readonly List<Point> _points = new();
    private bool _drawing;

    public static Task<string?> SelectAsync(string saveDir)
    {
        var w = new LensWindow(saveDir);
        w.Show();
        w.Activate();
        w.Focus();
        return w._result.Task;
    }

    private LensWindow(string saveDir)
    {
        _saveDir = saveDir;

        // freeze the whole desktop first (device pixels)
        _screenPx = System.Windows.Forms.SystemInformation.VirtualScreen;
        _shot = new System.Drawing.Bitmap(_screenPx.Width, _screenPx.Height);
        using (var g = System.Drawing.Graphics.FromImage(_shot))
            g.CopyFromScreen(_screenPx.Left, _screenPx.Top, 0, 0, _screenPx.Size);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Title = "Notchy";

        var image = new Image { Source = ToBitmapSource(_shot), Stretch = Stretch.Fill };
        _root.Children.Add(image);

        _dim.Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
        _root.Children.Add(_dim);

        _line.Stroke = new LinearGradientBrush(Color.FromRgb(0xFF, 0xC0, 0x5C), Color.FromRgb(0xB7, 0x9C, 0xFF), 0);
        _line.StrokeThickness = 4;
        _line.StrokeLineJoin = PenLineJoin.Round;
        _line.StrokeStartLineCap = _line.StrokeEndLineCap = PenLineCap.Round;
        _line.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Color = Color.FromRgb(0xFF, 0xB0, 0x50), Opacity = 0.95 };
        _root.Children.Add(_line);

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x16, 0x1A)),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 9, 18, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 70, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = Loc.T("lens.hint"),
                Foreground = Brushes.White,
                FontSize = 14,
                FontFamily = (FontFamily)Application.Current.FindResource("UiFont")
            }
        };
        _root.Children.Add(_hint);
        Content = _root;

        ResetDim();
        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            _hint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
            var tt = new TranslateTransform();
            _hint.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        };

        MouseLeftButtonDown += (_, e) =>
        {
            _drawing = true;
            _points.Clear();
            _points.Add(e.GetPosition(_root));
            CaptureMouse();
            _hint.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        };
        MouseMove += (_, e) =>
        {
            if (!_drawing) return;
            var p = e.GetPosition(_root);
            if ((p - _points[^1]).Length < 3) return;
            _points.Add(p);
            _line.Points = new PointCollection(_points);
            UpdateDim();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!_drawing) return;
            _drawing = false;
            ReleaseMouseCapture();
            Finish();
        };
        MouseRightButtonUp += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
        Deactivated += (_, _) => { if (!_result.Task.IsCompleted) Cancel(); };
        Closed += (_, _) =>
        {
            _result.TrySetResult(null);
            _shot.Dispose();
        };
    }

    private void ResetDim() =>
        _dim.Data = new RectangleGeometry(new Rect(0, 0, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));

    /// <summary>Everything is dimmed except the area being circled.</summary>
    private void UpdateDim()
    {
        if (_points.Count < 3) return;
        // Nonzero: overlapping / crossing strokes still count as "inside" whichever way you draw
        var poly = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = poly.Open())
        {
            ctx.BeginFigure(_points[0], true, true);
            ctx.PolyLineTo(_points.Skip(1).ToList(), true, true);
        }
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, poly);
    }

    private void Finish()
    {
        if (_points.Count < 2)
        {
            ResetDim();
            return;
        }
        double minX = _points.Min(p => p.X), maxX = _points.Max(p => p.X);
        double minY = _points.Min(p => p.Y), maxY = _points.Max(p => p.Y);
        if (maxX - minX < 12 || maxY - minY < 12)
        {
            // just a click: let them try again
            _line.Points = new PointCollection();
            ResetDim();
            return;
        }

        double kx = _shot.Width / ActualWidth, ky = _shot.Height / ActualHeight;
        const double pad = 6;
        int x = (int)Math.Max(0, (minX - pad) * kx), y = (int)Math.Max(0, (minY - pad) * ky);
        int w = (int)Math.Min(_shot.Width - x, (maxX - minX + 2 * pad) * kx);
        int h = (int)Math.Min(_shot.Height - y, (maxY - minY + 2 * pad) * ky);

        try
        {
            Directory.CreateDirectory(_saveDir);
            foreach (var old in Directory.GetFiles(_saveDir, "lens-*.png").OrderByDescending(f => f).Skip(10))
                File.Delete(old);
            string file = System.IO.Path.Combine(_saveDir, $"lens-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var crop = _shot.Clone(new System.Drawing.Rectangle(x, y, w, h), _shot.PixelFormat))
                crop.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            _result.TrySetResult(file);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _result.TrySetResult(null);
        }
        Close();
    }

    private void Cancel()
    {
        _result.TrySetResult(null);
        Close();
    }

    private static ImageSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            DeleteObject(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}
