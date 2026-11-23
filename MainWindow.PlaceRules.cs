using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Where the island goes on its own and by hand. Rules (Configurações → Posição): while an app is in front, or filling
/// the screen, the island moves to the side that rule says, and back when it stops. Dragging: hold the island and
/// take it to the top or a side; the spots it can go show up, and near one it snaps into it.
/// </summary>
public partial class MainWindow
{
    // =====================================================================================
    // rules
    // =====================================================================================

    private const double RuleSteadyMs = 200; // (passing through an app for a blink doesn't move the island)

    /// <summary>Where a rule wants the island now (null: none applies, it's where the settings say).</summary>
    private IslandPlace? _rulePlace;
    private string? _ruleKey, _ruleSeenKey, _ruleHeldKey;
    private IslandPlace? _ruleSeen;
    private DateTime _ruleSeenAt;
    private uint _rulePid;
    private string _ruleProcess = "";

    /// <summary>Every ~30 ms: which rule applies to the window in front (cheap: the app is looked up once per window).</summary>
    private void CheckPlaceRules()
    {
        if (S.PlaceRules.Count == 0 && _rulePlace == null) return;
        var (fg, pid) = Native.Foreground();
        if (pid == Environment.ProcessId) return; // (Notchy's own windows, the settings: it stays as it was)
        PlaceRule? hit = null;
        if (pid != 0)
        {
            if (pid != _rulePid)
            {
                _rulePid = pid;
                _ruleProcess = ProcessName(pid);
            }
            foreach (var r in S.PlaceRules)
                if (r.On && string.Equals(r.Process, _ruleProcess, StringComparison.OrdinalIgnoreCase) && (r.When == PlaceWhen.Front || Native.FillsPrimaryScreen(fg)))
                {
                    hit = r;
                    break;
                }
        }
        string? key = hit == null ? null : $"{hit.Process}|{hit.When}|{hit.Place}";
        // moved by hand while this rule applied: it waits until it stops applying
        if (_ruleHeldKey != null)
        {
            if (key == _ruleHeldKey) hit = null;
            else _ruleHeldKey = null;
        }
        IslandPlace? want = hit?.Place;
        if (want != _ruleSeen || key != _ruleSeenKey)
        {
            (_ruleSeen, _ruleSeenKey, _ruleSeenAt) = (want, key, DateTime.UtcNow);
            return;
        }
        if (want == _rulePlace || (DateTime.UtcNow - _ruleSeenAt).TotalMilliseconds < RuleSteadyMs) return;
        if (_state == IslandState.Expanded || _placeDragging || _placeMoving || _introActive) return; // (not under the mouse)
        _rulePlace = want;
        _ruleKey = key;
        SyncPlace();
    }

    // (test build: what the rules and the move did, frame by frame around a move, to find what the eye sees)
    private string _placeLogLast = "";
    private double _placeLogUntil;

    private void PlaceLog()
    {
#if DEBUG
        try
        {
            var (fg, pid) = Native.Foreground();
            string front = pid == 0 ? "-" : pid == Environment.ProcessId ? "NOTCHY" : _rulePid == pid ? _ruleProcess : ProcessName(pid);
            string line = $"frente {front}{(fg != IntPtr.Zero && Native.FillsPrimaryScreen(fg) ? " (cheia)" : "")} · regra {(_rulePlace?.ToString() ?? "-")} · lugar {_place}{(_placeMoving ? " movendo" : "")}" +
                          $" · drop {IslandDrop.X:0},{IslandDrop.Y:0} · ilha {Island.ActualWidth:0}x{Island.ActualHeight:0} · janela {Left:0},{Top:0} {Width:0}x{Height:0} op {Opacity:0.00} · {_state}/{_compactMode}";
            if (_placeMoving) _placeLogUntil = AnimNow + 1.2;
            bool watching = AnimNow < _placeLogUntil;
            // (between moves: only when something changes, not the drop's every pixel)
            string key = watching ? line : line[..line.IndexOf(" · drop", StringComparison.Ordinal)];
            if (key == _placeLogLast) return;
            _placeLogLast = key;
            string file = System.IO.Path.Combine(AppSettings.Folder, "place-debug.log");
            var fi = new System.IO.FileInfo(file);
            if (fi.Exists && fi.Length > 400_000) fi.Delete();
            System.IO.File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
#endif
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    // =====================================================================================
    // dragging, with a magnet
    // =====================================================================================

    private const double DragStart = 10, Magnet = 150;

    private Point? _pressAt;
    private bool _placeDragging, _placeDropped;
    private Window? _dragLayer;
    private Canvas _dragCanvas = null!;
    private Border _dragGhost = null!;
    private readonly System.Collections.Generic.Dictionary<IslandPlace, Rectangle> _dragSlots = new();
    private IslandPlace? _dragSnap;

    private void InitPlaceDrag()
    {
        Island.MouseLeftButtonDown += (_, e) =>
        {
            if (S.PlaceDrag && CanGrabIsland(e)) _pressAt = e.GetPosition(this);
        };
        Island.MouseMove += (_, e) =>
        {
            if (_pressAt is not Point from) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _pressAt = null;
                return;
            }
            if (!_placeDragging)
            {
                if ((e.GetPosition(this) - from).Length < DragStart) return;
                StartPlaceDrag();
            }
            MovePlaceDrag();
        };
        // (before the island's own click: a drop isn't a click)
        Island.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _pressAt = null;
            if (!_placeDragging) return;
            e.Handled = true;
            EndPlaceDrag();
        };
        Island.LostMouseCapture += (_, _) => { if (_placeDragging) EndPlaceDrag(); };
    }

    /// <summary>The minimized island anywhere; the open one by its title row (the rest of it is buttons, lists, cards).</summary>
    private bool CanGrabIsland(MouseButtonEventArgs e) => !_placeMoving && !_capturing && !_introActive &&
        (_state is IslandState.Idle or IslandState.Compact || _state == IslandState.Expanded && !_subOpen && e.GetPosition(ExpandedPanel).Y is >= 0 and < 40);

    /// <summary>The spot the island takes at each place, on the screen (as it is now, resting).</summary>
    private Rect SlotOf(IslandPlace place, Rect wa)
    {
        double sc = S.SizeScale;
        if (place == IslandPlace.Top)
        {
            double w = IdleWidth * sc, h = 30 * sc, top = S.Style == IslandStyle.Floating ? 8 : 0;
            return new Rect(wa.Left + wa.Width / 2 - w / 2, wa.Top + top, w, h);
        }
        double pw = SidePillWidth * sc, ph = SidePillHeight * sc, cy = wa.Top + wa.Height * SideCenterAt;
        return new Rect(place == IslandPlace.Left ? wa.Left : wa.Right - pw, cy - ph / 2, pw, ph);
    }

    private void StartPlaceDrag()
    {
        _placeDragging = true;
        _hoverTimer.Stop();
        // carried, it doesn't open (in "open on hover" it may have started to): it's at rest when it shows again
        _hoverActive = _hoverRequested = _clickOpened = false;
        UpdateState();
        Island.CaptureMouse();
        var wa = SystemParameters.WorkArea;
        _dragCanvas = new Canvas { IsHitTestVisible = false };
        _dragSlots.Clear();
        foreach (var place in new[] { IslandPlace.Top, IslandPlace.Left, IslandPlace.Right })
        {
            var r = SlotOf(place, wa);
            var slot = new Rectangle
            {
                Width = r.Width, Height = r.Height, RadiusX = Math.Min(r.Width, r.Height) / 2, RadiusY = Math.Min(r.Width, r.Height) / 2,
                Stroke = new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2.5 },
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 255, 255, 255)), Opacity = 0,
                Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 12, Opacity = 0, Color = Colors.White }
            };
            Canvas.SetLeft(slot, r.Left - wa.Left);
            Canvas.SetTop(slot, r.Top - wa.Top);
            slot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(180))));
            _dragCanvas.Children.Add(slot);
            _dragSlots[place] = slot;
        }
        // the island being carried: its eyes (the real one steps aside, with its glass: one pair of eyes on screen)
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 2; i++)
            eyes.Children.Add(new Ellipse { Width = 6 * S.SizeScale, Height = 10 * S.SizeScale, Fill = EyeL.Fill, Margin = new Thickness(3 * S.SizeScale, 0, 3 * S.SizeScale, 0) }); // (as far apart as the island's: 12)
        // it looks like the island it becomes (its colour, its eyes' colour and glow): no change when they swap
        eyes.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = EyesGlow.BlurRadius, Opacity = EyesGlow.Opacity, Color = EyesGlow.Color };
        var tint = Island.Background is SolidColorBrush ib ? ib.Color : Colors.Black;
        tint.A = Math.Max(tint.A, (byte)0xE6); // (the real one has the glass under it, this one doesn't)
        _dragGhost = new Border
        {
            Background = new SolidColorBrush(tint), Child = eyes, BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { ShadowDepth = 3, BlurRadius = 16, Opacity = 0.5 }
        };
        _dragCanvas.Children.Add(_dragGhost);
        _dragSnap = null;
        _dragTestAt = null;
        _dragLayer = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            ResizeMode = ResizeMode.NoResize, Left = wa.Left, Top = wa.Top, Width = wa.Width, Height = wa.Height, Content = _dragCanvas, Title = "Notchy"
        };
        _dragLayer.SourceInitialized += (_, _) => Native.MakeClickThrough(new System.Windows.Interop.WindowInteropHelper(_dragLayer).Handle);
        _dragLayer.Show();
        StartGhost();
        DragFrame(null, EventArgs.Empty);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(120))));
    }

    // the carried island: springs pull its place and size (tight under the mouse, softer into a spot); it lies flat
    // like at the top, and stands up bit by bit as it nears a side (StandFrom: starts, StandFull: standing)
    private const double StandFrom = 300, StandFull = 100, FreeStiff = 900, FreeDamp = 55, SnapStiff = 320, SnapDamp = 26, SettleMs = 260, SwapMs = 150;
    private readonly double[] _carry = new double[4], _carryVel = new double[4]; // x, y, width, height (on the layer)
    private Point? _dragTestAt;
    private bool _dragReturning;
    private double _carryLast;

    /// <param name="testAt">(tests: where the mouse would be, on the layer, without moving the real one)</param>
    private void MovePlaceDrag(Point? testAt = null)
    {
        if (testAt != null) _dragTestAt = testAt;
    }

    private void StartGhost()
    {
        var wa = SystemParameters.WorkArea;
        var r = SlotOf(_place, wa);
        (_carry[0], _carry[1], _carry[2], _carry[3]) = (r.Left - wa.Left, r.Top - wa.Top, r.Width, r.Height);
        Array.Clear(_carryVel);
        _carryLast = AnimNow;
        _dragReturning = false;
        CompositionTarget.Rendering += DragFrame;
    }

    private void DragFrame(object? sender, EventArgs e)
    {
        if (_dragLayer == null)
        {
            CompositionTarget.Rendering -= DragFrame;
            return;
        }
        double now = AnimNow, dt = Math.Clamp(now - _carryLast, 0, 0.033) / Math.Max(0.3, S.SpeedFactor);
        _carryLast = now;
        var wa = SystemParameters.WorkArea;
        Point at;
        if (_dragTestAt is Point t) at = t;
        else
        {
            Native.GetCursorPos(out var c);
            at = _dragLayer.PointFromScreen(new Point(c.X, c.Y));
        }
        // the nearest spot pulls it in when it's close enough (let go away from them: back to where it was)
        IslandPlace? snap = _dragReturning ? _place : null;
        if (!_dragReturning)
        {
            double best = Magnet;
            foreach (var (place, slot) in _dragSlots)
            {
                var r = new Rect(Canvas.GetLeft(slot), Canvas.GetTop(slot), slot.Width, slot.Height);
                double dx = Math.Max(Math.Max(r.Left - at.X, at.X - r.Right), 0), dy = Math.Max(Math.Max(r.Top - at.Y, at.Y - r.Bottom), 0);
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) (best, snap) = (d, place);
            }
        }
        Rect want;
        var flat = SlotOf(IslandPlace.Top, wa);
        if (snap is { } s)
        {
            var r = SlotOf(s, wa);
            want = new Rect(r.Left - wa.Left, r.Top - wa.Top, r.Width, r.Height);
        }
        else
        {
            // flat, standing up bit by bit near a side
            var stand = SlotOf(IslandPlace.Right, wa);
            double side = Math.Min(at.X, wa.Width - at.X), k = Math.Clamp((StandFrom - side) / (StandFrom - StandFull), 0, 1);
            k = k * k * (3 - 2 * k);
            double w = flat.Width + (stand.Width - flat.Width) * k, h = flat.Height + (stand.Height - flat.Height) * k;
            want = new Rect(at.X - w / 2, at.Y - h / 2, w, h);
        }
        if (snap != _dragSnap)
        {
            _dragSnap = snap;
            foreach (var (place, slot) in _dragSlots)
            {
                bool on = place == snap && !_dragReturning;
                slot.Stroke = new SolidColorBrush(on ? Color.FromRgb(0x9F, 0xE0, 0xFF) : Color.FromArgb(0x70, 255, 255, 255));
                slot.StrokeDashArray = on ? null : new DoubleCollection { 3, 2.5 };
                ((DropShadowEffect)slot.Effect).Color = Color.FromRgb(0x9F, 0xE0, 0xFF);
                ((DropShadowEffect)slot.Effect).Opacity = on ? 0.9 : 0;
            }
            if (snap != null && !_dragReturning) SoundService.Play(Sfx.Click);
        }
        double stiff = snap == null ? FreeStiff : SnapStiff, damp = snap == null ? FreeDamp : SnapDamp;
        double[] target = { want.Left, want.Top, want.Width, want.Height };
        for (int i = 0; i < 4; i++)
        {
            _carryVel[i] += (stiff * (target[i] - _carry[i]) - damp * _carryVel[i]) * dt;
            _carry[i] += _carryVel[i] * dt;
        }
        double gw = Math.Max(8, _carry[2]), gh = Math.Max(8, _carry[3]), round = Math.Min(gw, gh) / 2;
        Canvas.SetLeft(_dragGhost, _carry[0]);
        Canvas.SetTop(_dragGhost, _carry[1]);
        _dragGhost.Width = gw;
        _dragGhost.Height = gh;
        // in a spot: glued to its edge once it gets there, like the island there (no rounded corners against the edge)
        bool there = Math.Abs(_carry[0] - target[0]) + Math.Abs(_carry[1] - target[1]) < 6;
        _dragGhost.CornerRadius = (there ? snap : null) switch
        {
            IslandPlace.Top => new CornerRadius(0, 0, round, round),
            IslandPlace.Left => new CornerRadius(0, round, round, 0),
            IslandPlace.Right => new CornerRadius(round, 0, 0, round),
            _ => new CornerRadius(round)
        };
        ((DropShadowEffect)_dragGhost.Effect).ShadowDepth = snap == null ? 5 : 1;
        // glued in its spot: no outline nor shadow, like the island there (they swap without a change)
        bool glued = there && snap != null;
        ((DropShadowEffect)_dragGhost.Effect).Opacity += ((glued ? 0 : 0.5) - ((DropShadowEffect)_dragGhost.Effect).Opacity) * 0.25;
        _dragGhost.BorderBrush.Opacity += ((glued ? 0 : 1) - _dragGhost.BorderBrush.Opacity) * 0.25;
    }

    private async void EndPlaceDrag()
    {
        if (!_placeDragging) return;
        _placeDragging = false;
        Island.ReleaseMouseCapture();
        var to = _dragSnap;
        _hoverActive = _hoverRequested = _clickOpened = false;
        _ignoreHoverUntil = DateTime.UtcNow.AddMilliseconds(SettleMs + 600);
        foreach (var slot in _dragSlots.Values) slot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(SettleMs))));
        bool moves = to is { } p && p != _place;
        if (!moves)
        {
            _dragReturning = true; // (let go away from the spots, or where it was: it goes back)
            _dragSnap = null;
        }
        // the carried island settles into its spot, then the real one takes its place right there
        await System.Threading.Tasks.Task.Delay((int)Dur(SettleMs + (moves ? 0 : 120)));
        if (moves)
        {
            // the place chosen by hand: that's the usual place now (a rule that applied waits until it stops applying)
            if (_rulePlace != null)
            {
                _ruleHeldKey = _ruleKey;
                _rulePlace = null;
            }
            _placeDropped = true;
            S.Place = to!.Value;
            AppSettings.Commit();
        }
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); // (laid out at its place)
        // already at its final size where the carried one is: no shrinking from the size it had at the old place
        if (_state != IslandState.Expanded)
        {
            var size = TargetSize(_state);
            SetIslandSizeNow(size.Width, size.Height + _topHidden);
            Island.UpdateLayout();
        }
        // a short crossfade: the carried island melts into the real one (the real one has its glass, the carried one can't)
        var swap = TimeSpan.FromMilliseconds(Dur(SwapMs));
        var show = new DoubleAnimation(0, 1, swap);
        show.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, show);
        // (above the island's glass, which shows at once with it: the carried one fades out over the real one)
        if (_dragLayer != null) Native.BringTopmost(new System.Windows.Interop.WindowInteropHelper(_dragLayer).Handle);
        _dragCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, swap)); // (its content: the see-through window itself doesn't fade)
        await System.Threading.Tasks.Task.Delay((int)Dur(SwapMs + 20));
        CompositionTarget.Rendering -= DragFrame;
        _dragLayer?.Close();
        _dragLayer = null;
        UpdateState();
    }
}
