using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>
/// Choosing what to take, the spotlight: the element under the mouse (an image, a button, a panel… found with UI
/// Automation in the window under it) stays lit and the rest of the screen darkens; the wheel goes up to the groups
/// around it and to the whole window, and back down. Where an app has no elements (games), the whole window lights up.
/// </summary>
public sealed partial class CaptureWindow
{
    private const double MinElement = 10; // (device pixels: smaller ones are skipped, so the light doesn't jitter)
    private const int ElementLookMs = 600;

    /// <summary>What may light up: things you'd want to take. Panes, plain groups, menu and title bars are only gone through
    ///     (apps are full of invisible ones: a browser's hidden menu bar, layout frames), their children still count.</summary>
    private static readonly HashSet<ControlType> Takeable = new()
    {
        ControlType.Image, ControlType.Button, ControlType.SplitButton, ControlType.Hyperlink, ControlType.Edit, ControlType.ComboBox,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.List, ControlType.ListItem, ControlType.Tree, ControlType.TreeItem,
        ControlType.Tab, ControlType.TabItem, ControlType.MenuItem, ControlType.Text, ControlType.Document, ControlType.DataGrid,
        ControlType.DataItem, ControlType.Table, ControlType.Slider, ControlType.ProgressBar, ControlType.Calendar, ControlType.Spinner
    };

    private readonly RectangleGeometry _spotHole = new(Rect.Empty);
    private Geometry? _spotDim;
    private List<Rect> _chain = new(); // the lit window, then each element inside it down to the one under the mouse
    private int _chainWindow = -1, _levelsUp;
    private Rect _lit = Rect.Empty;
    private Point _lookAt;
    private bool _looking;

    private Rect ToStage(System.Drawing.Rectangle r) => new((r.Left - _screenPx.Left) / Kx, (r.Top - _screenPx.Top) / Ky, r.Width / Kx, r.Height / Ky);

    /// <summary>What a click takes: the lit element (or window) if the mouse is on it, else the window under it.</summary>
    private Rect LitAt(Point p) => !_lit.IsEmpty && _lit.Contains(p) ? _lit : WindowAt(p);

    /// <summary>The mouse moved (no drag): the window lights up at once, then the element under the mouse when it's found.</summary>
    private void TrackElements(Point p)
    {
        int w = _windows.FindIndex(r => r.Contains(p));
        if (w < 0)
        {
            _chain.Clear();
            _chainWindow = -1;
            Light(Rect.Empty);
            return;
        }
        if (w != _chainWindow)
        {
            _chainWindow = w;
            _levelsUp = 0;
            _chain = new List<Rect> { _windows[w] };
            Light(_windows[w]);
        }
        _lookAt = p;
        if (!_looking) LookForElement();
    }

    /// <summary>One look at a time, always for the latest mouse position (UI Automation asks the app, which takes a moment).</summary>
    private async void LookForElement()
    {
        _looking = true;
        try
        {
            while (true)
            {
                var p = _lookAt;
                int w = _windows.FindIndex(r => r.Contains(p));
                if (w < 0) return;
                var hwnd = _windowHandles[w];
                var px = new Point(p.X * Kx + _screenPx.Left, p.Y * Ky + _screenPx.Top);
                var winPx = new Rect(_windows[w].X * Kx + _screenPx.Left, _windows[w].Y * Ky + _screenPx.Top, _windows[w].Width * Kx, _windows[w].Height * Ky);
                var found = await Task.Run(() => ElementChain(hwnd, px, winPx));
                if (_phase != Phase.Choosing || _dragging) return;
                if (_lookAt != p) continue; // (the mouse moved meanwhile: look again where it is now)
                if (w != _chainWindow) return;
                var win = _windows[w];
                var chain = new List<Rect> { win };
                foreach (var r in found)
                {
                    var s = Rect.Intersect(win, ToStage(new System.Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)));
                    if (s.IsEmpty || Near(s, chain[^1]) || LooksEmpty(r)) continue; // (the same box again: one level; or nothing drawn in it)
                    chain.Add(s);
                }
                _chain = chain;
                _levelsUp = Math.Min(_levelsUp, chain.Count - 1);
                Light(chain[chain.Count - 1 - _levelsUp]);
                return;
            }
        }
        finally
        {
            _looking = false;
        }
    }

    /// <summary>Nothing drawn in it: the frozen picture is (almost) one colour inside the box (screen pixels).</summary>
    private bool LooksEmpty(Rect r)
    {
        int lo = 255, hi = 0;
        for (int i = 1; i <= 12; i++)
            for (int j = 1; j <= 12; j++)
            {
                int x = (int)(r.X + r.Width * i / 13) - _screenPx.Left, y = (int)(r.Y + r.Height * j / 13) - _screenPx.Top;
                if (x < 0 || y < 0 || x >= _shot.Width || y >= _shot.Height) continue;
                var c = _shot.GetPixel(x, y);
                int l = (c.R * 3 + c.G * 6 + c.B) / 10;
                lo = Math.Min(lo, l);
                hi = Math.Max(hi, l);
            }
        return hi - lo < 10;
    }

#if DEBUG
    public string TestChain(Point p)
    {
        int w = _windows.FindIndex(r => r.Contains(p));
        if (w < 0) return "sem janela";
        var sb = new System.Text.StringBuilder();
        var winPx = new Rect(_windows[w].X * Kx + _screenPx.Left, _windows[w].Y * Ky + _screenPx.Top, _windows[w].Width * Kx, _windows[w].Height * Ky);
        var found = ElementChain(_windowHandles[w], new Point(p.X * Kx + _screenPx.Left, p.Y * Ky + _screenPx.Top), winPx, m => sb.AppendLine(m));
        foreach (var r in found) sb.AppendLine($"  aceito {r} vazio={LooksEmpty(r)}");
        return sb.ToString();
    }
#endif

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);

    private static bool Near(Rect a, Rect b) => Math.Abs(a.Left - b.Left) < 3 && Math.Abs(a.Top - b.Top) < 3 && Math.Abs(a.Right - b.Right) < 3 && Math.Abs(a.Bottom - b.Bottom) < 3;

    /// <summary>
    /// From the window down: at each level the smallest child under the point (screen pixels). Empty if the app has no
    /// elements. Left out (their children still count): what the app calls off screen, boxes that run past the window or
    /// the part of their parent that shows (a toolbar cut by the edge, a scrolling page bigger than its view), and
    /// elements that aren't <see cref="Takeable"/>.
    /// </summary>
    private static List<Rect> ElementChain(IntPtr hwnd, Point px, Rect winPx, Action<string>? log = null)
    {
        var list = new List<Rect>();
        var shown = winPx; // the part of the current element that is on screen
        try
        {
            var clock = Stopwatch.StartNew();
            // Firefox-based browsers (Zen, Firefox…): their tree stacks hidden tabs and workspaces over the visible one and
            // calls the visible page "off screen", so nothing in it can be trusted: those light up as a whole window
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() == "MozillaWindowClass") return list;
            var walker = TreeWalker.ControlViewWalker;
            var cur = AutomationElement.FromHandle(hwnd);
            for (int depth = 0; depth < 30 && clock.ElapsedMilliseconds < ElementLookMs; depth++)
            {
                AutomationElement? best = null;
                Rect bestR = Rect.Empty;
                int n = 0;
                for (var c = walker.GetFirstChild(cur); c != null && n < 300; c = walker.GetNextSibling(c), n++)
                {
                    Rect r;
                    try
                    {
                        r = c.Current.BoundingRectangle;
                    }
                    catch (ElementNotAvailableException)
                    {
                        continue;
                    }
                    if (r.IsEmpty || r.Width < MinElement || r.Height < MinElement || !r.Contains(px)) continue;
                    try
                    {
                        if (c.Current.IsOffscreen)
                        {
                            log?.Invoke($"  {depth}: {c.Current.ControlType.ProgrammaticName} {r} fora da tela (descartado)");
                            continue;
                        }
                    }
                    catch (ElementNotAvailableException)
                    {
                        continue;
                    }
                    if (best == null || r.Width * r.Height < bestR.Width * bestR.Height) (best, bestR) = (c, r);
                }
                if (best == null) break;
                var inside = shown;
                inside.Inflate(2, 2);
                ControlType? type = null;
                try
                {
                    type = best.Current.ControlType;
                }
                catch (ElementNotAvailableException) { }
                bool fits = inside.Contains(bestR), takeable = type != null && Takeable.Contains(type);
                if (fits && takeable) list.Add(bestR);
                log?.Invoke($"  {depth}: {type?.ProgrammaticName} {bestR} {(!fits ? $"cortado (visível {shown})" : takeable ? "ok" : "só atravessado")}");
                shown = Rect.Intersect(shown, bestR);
                cur = best;
            }
        }
        catch (Exception)
        {
            // (an app that doesn't answer: whatever was found so far, or just the window)
        }
        return list;
    }

    /// <summary>The wheel: up to the group around the lit element (and the window), down back to it.</summary>
    private void OnElementWheel(object sender, MouseWheelEventArgs e)
    {
        if (_phase != Phase.Choosing || _dragging || _chain.Count < 2) return;
        e.Handled = true;
        _levelsUp = Math.Clamp(_levelsUp + (e.Delta > 0 ? 1 : -1), 0, _chain.Count - 1);
        Light(_chain[_chain.Count - 1 - _levelsUp]);
    }

    /// <summary>The spotlight: <paramref name="r"/> stays lit with the ice outline, the rest of the screen darkens; it glides from one to the next.</summary>
    private void Light(Rect r)
    {
        if (r == _lit && _hover.Visibility == (r.IsEmpty ? Visibility.Collapsed : Visibility.Visible)) return;
        bool glide = !_lit.IsEmpty && !r.IsEmpty && _hover.Visibility == Visibility.Visible;
        _lit = r;
        _spotDim ??= new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, Width, Height)), _spotHole);
        _dim.Data = _spotDim;
        if (r.IsEmpty)
        {
            _hover.Visibility = Visibility.Collapsed;
            _spotHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            _spotHole.Rect = Rect.Empty;
            return;
        }
        bool window = _chainWindow >= 0 && Near(r, _windows[_chainWindow]);
        _hover.CornerRadius = new CornerRadius(window ? 9 : 4);
        _hover.Visibility = Visibility.Visible;
        var to = new Rect(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
        var time = TimeSpan.FromMilliseconds(Ms(glide ? 170 : 0));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Go(DependencyObject o, DependencyProperty dp, double v)
        {
            if (time.TotalMilliseconds <= 0)
            {
                ((IAnimatable)o).BeginAnimation(dp, null);
                o.SetValue(dp, v);
            }
            else ((IAnimatable)o).BeginAnimation(dp, new DoubleAnimation(v, time) { EasingFunction = ease });
        }
        Go(_hover, Canvas.LeftProperty, to.X);
        Go(_hover, Canvas.TopProperty, to.Y);
        Go(_hover, WidthProperty, to.Width);
        Go(_hover, HeightProperty, to.Height);
        if (time.TotalMilliseconds <= 0)
        {
            _spotHole.BeginAnimation(RectangleGeometry.RectProperty, null);
            _spotHole.Rect = r;
        }
        else _spotHole.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(r, time) { EasingFunction = ease });
    }
}
