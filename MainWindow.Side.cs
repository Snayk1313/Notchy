using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The island on a side of the screen (2.5). It's the same island, glued to that edge: minimized it shrinks into a
/// standing pill (eyes in its middle), open it grows out of it, so closing and opening are one shape changing.
/// Going from one place to the other, the island slides out of sight (up behind the top edge, or into the side
/// edge) and comes out at the other place.
/// </summary>
public partial class MainWindow
{
    public const double SidePillWidth = 40, SidePillHeight = 76; // (as thick as the bubbles: room for the eyes)
    /// <summary>The bubbles standing on a side: a bit thicker than the pill, so their eyes and light have room.</summary>
    private const double SidePodThickness = 40;
    /// <summary>Where the pill's middle sits, from the top of the work area (0..1).</summary>
    private const double SideCenterAt = 0.5; // (the middle of the screen's height, like the top island is in the middle of its width)

    /// <summary>Where the island is now (the setting, or later a rule that moved it).</summary>
    private IslandPlace _place = S.Place;
    private bool _placeMoving;

    /// <summary>Where the island should be.</summary>
    private IslandPlace WantedPlace => _rulePlace ?? S.Place;

    private bool OnSide => _place != IslandPlace.Top;
    /// <summary>For the outline lights: the island stands on the right (1) or left (-1) edge, or hangs from the top (0).</summary>
    private int OutlineTurn => OnSide ? (PlaceRight ? 1 : -1) : 0;
    private bool PlaceRight => _place == IslandPlace.Right;

    /// <summary>
    /// The states the island shows standing (the minimized island's own layout, turned to stand: what sat side by side
    /// sits one above the other). The rest are this island at the edge at their own size: the open island and the cards
    /// (notification, calendar, screenshot, ringing timer, news, earbuds).
    /// </summary>
    private bool PillState(IslandState s) => s is IslandState.Idle or IslandState.Volume ||
        s == IslandState.Compact && _compactMode is not (CompactMode.Notice or CompactMode.CalNotice or CompactMode.TimerRing
            or CompactMode.WhatsNew or CompactMode.Buds or CompactMode.BudsLow or CompactMode.BudsOff);

    /// <summary>The island's length along the edge it hangs from: its width at the top, its height on a side.</summary>
    private double IslandSpan => OnSide ? Island.ActualHeight : Island.ActualWidth;
    private double SpanOf(Size size) => OnSide ? size.Height : size.Width;

    /// <summary>The island's size along its edge, and into the screen (it hides by shrinking that one into the edge).</summary>
    private DependencyProperty SpanProperty => OnSide ? HeightProperty : WidthProperty;
    private DependencyProperty DepthProperty => OnSide ? WidthProperty : HeightProperty;
    private double DepthHidden => OnSide ? 0 : _topHidden;

    /// <summary>On a side, showing as the standing pill right now.</summary>
    private bool AsPill => OnSide && PillState(_state);

    /// <summary>Settings changed: the island goes to its place (with the move if it's somewhere else now).</summary>
    private void SyncPlace()
    {
        if (_placeMoving || WantedPlace == _place) return;
        if (!IsLoaded)
        {
            _place = WantedPlace;
            return;
        }
        if (!_introActive) MovePlace(WantedPlace);
    }

    /// <summary>Layout for the current place: where the window sits and how the island hangs in it.</summary>
    private void ApplyPlaceLayout()
    {
        if (OnSide)
        {
            // glued to the edge, growing out of the pill's middle (up and down, and into the screen)
            Island.HorizontalAlignment = PlaceRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Island.VerticalAlignment = VerticalAlignment.Center;
            Island.Margin = new Thickness(0);
            Island.RenderTransformOrigin = new Point(PlaceRight ? 1 : 0, 0.5);
        }
        else
        {
            Island.HorizontalAlignment = HorizontalAlignment.Center;
            Island.VerticalAlignment = VerticalAlignment.Top;
            Island.RenderTransformOrigin = new Point(0.5, 0);
        }
        TurnLevelBar();
        ArrangeCompact();
        ArrangeExpanded();
        ArrangePages();
        TurnPods();
        IdlePanel.VerticalAlignment = OnSide ? VerticalAlignment.Center : VerticalAlignment.Top;
        // the lights that run around the island's outline hang the way it does
        foreach (var light in new FrameworkElement?[] { ClaudeTrack, ClaudeLight, TransferTrack, TransferLight, FocusTrack, FocusLight, IntroLight, _clipLight })
            if (light != null) HangLikeIsland(light);
        PositionWindow();
        UpdateIslandShape();
    }

    /// <summary>A light drawn over the island's outline hangs exactly like the island (alignment, scale origin, margin).</summary>
    private void HangLikeIsland(FrameworkElement el)
    {
        el.HorizontalAlignment = Island.HorizontalAlignment;
        el.VerticalAlignment = Island.VerticalAlignment;
        el.RenderTransformOrigin = Island.RenderTransformOrigin;
        el.Margin = Island.Margin;
    }

    /// <summary>On a side, the window's middle is the pill's middle.</summary>
    private double SideWindowTop()
    {
        var wa = SystemParameters.WorkArea;
        return Math.Clamp(wa.Top + wa.Height * SideCenterAt - Height / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
    }

    /// <summary>How far the island slides to be out of sight behind its side edge.</summary>
    private double SideHiddenX => (Island.ActualWidth * S.SizeScale + 16) * (PlaceRight ? 1 : -1);

    /// <summary>
    /// The island goes somewhere else: out of sight where it is (up behind the top edge, or into the side edge),
    /// then out at the new place (down from the top, or out of the side edge).
    /// </summary>
    private const double PlaceOutMs = 200, PlaceInMs = 360; // (out of sight at the old place, out at the new one)

    private void MovePlace(IslandPlace to)
    {
        if (to == _place || _placeMoving) return;
        _placeMoving = true;
        bool dropped = _placeDropped; // (dropped there by hand: the carried island already sits in the spot)
        void Arrive()
        {
            _place = to;
            ApplySettings(); // (the island's geometry depends on the place: floating on a side)
            _placeMoving = false;
            IslandDrop.BeginAnimation(TranslateTransform.XProperty, null);
            IslandDrop.BeginAnimation(TranslateTransform.YProperty, null);
            IslandDrop.X = IslandDrop.Y = 0;
            if (dropped) return;
            // its shape here at once, while out of sight (not changing shape as it slides in)
            var size = TargetSize(_state);
            SetIslandSizeNow(size.Width, size.Height + _topHidden);
            if (OnSide)
            {
                // out of the side edge. Hidden behind the edge already now: the slide starts after the next layout,
                // and until then it'd show in place.
                IslandDrop.X = (size.Width * S.SizeScale + 16) * (PlaceRight ? 1 : -1);
                Dispatcher.InvokeAsync(() => IslandDrop.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(SideHiddenX, 0, TimeSpan.FromMilliseconds(Dur(PlaceInMs))) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } }),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                // down from behind the top edge
                IslandDrop.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-(size.Height + _topHidden) - 10, 0, TimeSpan.FromMilliseconds(Dur(PlaceInMs)))
                {
                    EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
                });
            }
        }
        if (_placeDropped)
        {
            // dropped there by hand: it's out of sight already, and shows right where it was dropped
            _placeDropped = false;
            Arrive();
            return;
        }
        var leave = OnSide
            ? new DoubleAnimation(SideHiddenX, TimeSpan.FromMilliseconds(Dur(PlaceOutMs - 20))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } }
            : new DoubleAnimation(-Island.ActualHeight - 10, TimeSpan.FromMilliseconds(Dur(PlaceOutMs))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        leave.Completed += (_, _) => Arrive();
        IslandDrop.BeginAnimation(OnSide ? TranslateTransform.XProperty : TranslateTransform.YProperty, leave);
    }

    /// <summary>Every frame: the standing volume bar, the bubbles' places.</summary>
    private void SideFrame()
    {
        PlacePods();
        FitSidePage();
    }

    // ---- the minimized island standing: the same pieces, one above the other

    /// <summary>Its size: as wide as its widest piece (a pill, wider for a lyric line), as tall as they stack.</summary>
    private Size PillSize(IslandState state)
    {
        if (state == IslandState.Volume)
            return _devListOpen ? new Size(SidePillWidth + DevListWidth, Math.Max(LevelPillHeight, SideDevListHeight)) : new Size(SidePillWidth, LevelPillHeight);
        if (state == IslandState.Idle) return new Size(IdleWidth, IdlePillHeight); // (bigger while the alarm's time shows)
        if (state != IslandState.Compact) return new Size(SidePillWidth, SidePillHeight);
        FrameworkElement panel = IsTransferMode(_compactMode) ? TransferPanel : IsClaudeMode(_compactMode) ? ClaudePanel : CompactPanel;
        double w = SidePillWidth;
        if (panel == CompactPanel)
        {
            if (CompactText.Text != "" && CompactTextHost.Visibility == Visibility.Visible)
                w = Math.Max(w, Math.Min(LyricPillWidth, MeasureText(CompactText, CompactText.Text) + 2 * PillPad));
            if (CompactRight.Visibility == Visibility.Visible && CompactRight.Text != "")
                w = Math.Max(w, MeasureText(CompactRight, CompactRight.Text) + 2 * PillPad);
        }
        else if (panel == TransferPanel && TransferPercent.Text != "")
            w = Math.Max(w, MeasureText(TransferPercent, TransferPercent.Text) + 6); // (snug: as thick as the bubble it turns into)
        double h = panel.Height;
        panel.Height = double.NaN;
        Remeasure(panel); // (a text that just changed: WPF would hand back the last measure)
        panel.Measure(new Size(w, double.PositiveInfinity));
        double need = panel.DesiredSize.Height;
        panel.Height = h; // (the size animation sets it)
        return new Size(Math.Ceiling(w), Math.Max(SidePillHeight, Math.Ceiling(need)));
    }

    private static void Remeasure(DependencyObject d)
    {
        if (d is UIElement u) u.InvalidateMeasure();
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Remeasure(VisualTreeHelper.GetChild(d, i));
    }

    private const double LyricPillWidth = 96, PillPad = 7;
    private Size? _pillAsked;
    private readonly System.Collections.Generic.Dictionary<UIElement, (int col, Thickness margin, HorizontalAlignment ha, VerticalAlignment va)> _topLayout = new();
    private bool? _arranged;

    /// <summary>
    /// The minimized island's panels, laid out for where it is: at the top their pieces sit side by side; on a side the
    /// same pieces stand one above the other (cover / icon, then the text, which wraps, then the wave / time / %).
    /// </summary>
    private void ArrangeCompact()
    {
        if (_arranged == OnSide) return;
        _arranged = OnSide;
        bool side = OnSide;
        void Keep(UIElement e, FrameworkElement f)
        {
            if (!_topLayout.ContainsKey(e)) _topLayout[e] = (Grid.GetColumn(e), f.Margin, f.HorizontalAlignment, f.VerticalAlignment);
        }
        void Put(FrameworkElement f, Thickness sideMargin, HorizontalAlignment ha, VerticalAlignment va)
        {
            Keep(f, f);
            var top = _topLayout[f];
            f.Margin = side ? sideMargin : top.margin;
            f.HorizontalAlignment = side ? ha : top.ha;
            f.VerticalAlignment = side ? va : top.va;
        }

        // music, focus, battery, update...: cover / icon on top, the text (wrapping), the wave or the time below
        CompactPanel.RowDefinitions.Clear();
        if (side)
        {
            CompactPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            CompactPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            CompactPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        foreach (UIElement child in CompactPanel.Children)
        {
            if (child is not FrameworkElement f) continue;
            Keep(child, f);
            int col = _topLayout[child].col;
            Grid.SetRow(child, side ? col : 0);
            Grid.SetColumn(child, side ? 0 : col);
            Grid.SetColumnSpan(child, side ? 3 : 1);
            if (child is StackPanel sp) sp.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
            Put(f, col == 0 ? new Thickness(0, 9, 0, 0) : col == 1 ? new Thickness(PillPad, 6, PillPad, 6) : new Thickness(0, 0, 0, 9),
                HorizontalAlignment.Center, col == 0 ? VerticalAlignment.Top : col == 2 ? VerticalAlignment.Bottom : VerticalAlignment.Center);
        }
        foreach (UIElement child in ((StackPanel)CompactPanel.Children[0]).Children)
            if (child is FrameworkElement f) Put(f, new Thickness(0, 0, 0, 4), HorizontalAlignment.Center, VerticalAlignment.Center);
        CompactTextHost.Orientation = side ? Orientation.Vertical : Orientation.Horizontal; // (vertical: the width is given, so it wraps)
        CompactText.TextWrapping = side ? TextWrapping.Wrap : TextWrapping.NoWrap;
        CompactText.TextAlignment = side ? TextAlignment.Center : TextAlignment.Left;
        CompactText.MaxWidth = side ? LyricPillWidth - 2 * PillPad : double.PositiveInfinity;

        // the resting island: what sits left of the eyes at the top (the calendar's dot) sits above them, what sits
        // right of them (the alarm's bell; for its 15 seconds, the alarm's time in its place) below
        IdlePanel.Width = IdleWidth;
        IdlePanel.Height = side ? IdlePillHeight : 30;
        Put(_idleCalDot, new Thickness(0, 12, 0, 0), HorizontalAlignment.Center, VerticalAlignment.Top);
        // standing, they're lined up with the eyes: they go where the eyes look (on a side, mostly into the screen)
        _idleCalDot.RenderTransform = side ? EyesLook : Transform.Identity;
        if (_idleBell?.Parent is StackPanel hint && _idleSoon != null)
        {
            hint.RenderTransform = side ? EyesLook : Transform.Identity;
            Put(hint, new Thickness(0, 0, 0, 10), HorizontalAlignment.Center, VerticalAlignment.Bottom);
            hint.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
            _idleBell.HorizontalAlignment = HorizontalAlignment.Center;
            _idleSoon.HorizontalAlignment = HorizontalAlignment.Center;
            _idleSoon.Margin = side ? new Thickness(0) : new Thickness(AlarmTimeGap, 0, 0, 0);
            _idleSoon.FontSize = side ? 10.5 : 11.5; // (room on both sides of it in the pill)
            UpdateAlarmHint(force: true);
        }

        // download: icon on top, eyes, % below
        Put(TransferIcon, new Thickness(0, 10, 0, 0), HorizontalAlignment.Center, VerticalAlignment.Top);
        Put(TransferPercent, new Thickness(0, 0, 0, 9), HorizontalAlignment.Center, VerticalAlignment.Bottom);

        foreach (var panel in new FrameworkElement[] { CompactPanel, TransferPanel, ClaudePanel })
        {
            panel.VerticalAlignment = side ? VerticalAlignment.Center : VerticalAlignment.Top;
            if (!side) panel.Height = 34;
        }
    }

    // ---- bubbles: the same bubbles as at the top (Claude / focus, download, their eyes, their light, how they turn into
    //      the island and back), turned to stand: what sits beside the island at the top sits above / below it here

    private readonly TranslateTransform _podTurn = new(), _dlPodTurn = new();
    private bool? _podsTurned;
    /// <summary>On a side: the length of the island a bubble is turning into (or out of), for its light to stretch to its edge.</summary>
    private double _podMorphSpan = SidePillHeight;
    private double _podsAngle;

    private void TurnPods()
    {
        foreach (var (pod, turn) in new[] { (ClaudePod, _podTurn), (DownloadPod, _dlPodTurn) })
            if (pod.RenderTransform is TransformGroup g)
            {
                if (!g.Children.Contains(turn)) g.Children.Add(turn);
                // last: the island's own slide when it changes place (out of sight, out at the new place), so the
                // bubbles leave and come back with it, the same way
                g.Children.Remove(IslandDrop);
                g.Children.Add(IslandDrop);
            }
        double angle = OnSide ? (PlaceRight ? 90 : -90) : 0;
        if (_podsTurned == OnSide && _podsAngle == angle) return;
        _podsTurned = OnSide;
        _podsAngle = angle;
        _podShapeW = _dlShapeW = -1; // (the bubbles' light is redrawn for their new thickness)
        // lying at the top, standing on a side (its glued top on that edge, like the island); the eyes and the % stay upright
        Transform Turn(double a) => a == 0 ? Transform.Identity : new RotateTransform(a);
        PodContent.Height = DlPodContent.Height = OnSide ? SidePodThickness : 34;
        ClaudePod.LayoutTransform = Turn(angle);
        DownloadPod.LayoutTransform = Turn(angle);
        foreach (var upright in new FrameworkElement[] { (FrameworkElement)((FrameworkElement)PodEyeL.Parent).Parent, DlPodEyes, DlPodInfo, DlPodIcon })
            upright.LayoutTransform = Turn(-angle);
        // where the eyes look: kept in the screen's directions (a bubble's eyes hand over to the island's exactly)
        var podEyes = (StackPanel)((FrameworkElement)PodEyeL.Parent).Parent;
        foreach (var (eyes, look) in new[] { (podEyes, PodLook), (DlPodEyes, DlPodLook) })
            eyes.RenderTransform = !OnSide ? look : new TransformGroup { Children = { new RotateTransform(angle), look, new RotateTransform(-angle) } };
        // the download icon at the bubble's top end, like the island's (on the left edge the bubble's "left" is at the bottom)
        DlPodIcon.HorizontalAlignment = OnSide && !PlaceRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        DlPodIcon.Margin = !OnSide ? new Thickness(22, 0, 0, 0) : PlaceRight ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0);
        double r = SidePodThickness / 2;
        ClaudePod.CornerRadius = DownloadPod.CornerRadius = OnSide ? new CornerRadius(0, 0, r, r) : new CornerRadius(17);
        foreach (var pod in new[] { ClaudePod, DownloadPod })
        {
            pod.HorizontalAlignment = Island.HorizontalAlignment;
            pod.VerticalAlignment = OnSide ? VerticalAlignment.Center : VerticalAlignment.Top;
            if (!OnSide) pod.RenderTransformOrigin = new Point(0.5, 0);
        }
        if (!OnSide) _podTurn.X = _podTurn.Y = _dlPodTurn.X = _dlPodTurn.Y = 0;
    }

    /// <summary>On a side, how far the focus island's eyes sit from its middle (up, at its top end): where the bubble's eyes glide to.</summary>
    private double FocusEyesFromMiddle()
    {
        if (!CompactFocusEyes.IsVisible || CompactPanel.ActualHeight < 1) return 0;
        var c = CompactFocusEyes.TranslatePoint(new Point(CompactFocusEyes.ActualWidth / 2, CompactFocusEyes.ActualHeight / 2), CompactPanel);
        return c.Y - _podMorphSpan / 2; // (from the island's top: the panel stands as tall as the island it becomes)
    }

    /// <summary>
    /// Every frame on a side: the bubbles' code moves them sideways (beside the island, and into it when they turn into
    /// it); on a side that same move goes along the edge instead: up for Claude / focus, down for the download.
    /// </summary>
    private void PlacePods()
    {
        if (!OnSide) return;
        Place(ClaudePod, _podTurn);
        Place(DownloadPod, _dlPodTurn);

        void Place(Border pod, TranslateTransform turn)
        {
            if (pod.Visibility != Visibility.Visible) return;
            if (pod.Margin != default) pod.Margin = default;
            // it scales (the size setting, popping in) from the middle of its edge side. WPF reads the origin against the
            // bubble's size before turning it: length L along the edge, thickness T, so the edge's middle is (T/L, L/2/T)
            double L = Math.Max(1, pod.ActualWidth), T = Math.Max(1, pod.ActualHeight);
            var origin = new Point(PlaceRight ? T / L : 0, L / 2 / T);
            if (pod.RenderTransformOrigin != origin) pod.RenderTransformOrigin = origin;
            double x = 0;
            foreach (var t in ((TransformGroup)pod.RenderTransform).Children)
                if (t is TranslateTransform tt && tt != turn && tt != IslandDrop) x += tt.X; // (the island's slide is on screen already)
            turn.X = -x;
            turn.Y = x;
        }
    }

    // ---- volume / brightness on a side: the top's bar itself, turned to stand (eyes at the bottom, the light rising,
    //      the number on top); the eyes, the number and the outputs button stay upright. The outputs list (under the
    //      bar at the top) opens beside it, into the screen: the island widens for it, the bar stays on the edge.

    private const double LevelPillHeight = 170;
    private IslandPlace? _levelTurned;
    private Grid? _sideDevHost;

    private void TurnLevelBar()
    {
        if (_levelTurned == _place) return;
        _levelTurned = _place;
        SetDeviceListOpen(false);
        Transform Turn(double a) => a == 0 ? Transform.Identity : new RotateTransform(a);
        VolumePanel.LayoutTransform = Turn(OnSide ? -90 : 0);
        VolumePanel.VerticalAlignment = OnSide ? VerticalAlignment.Center : VerticalAlignment.Top;
        VolumePanel.HorizontalAlignment = !OnSide ? HorizontalAlignment.Center : PlaceRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VolumePanel.RowDefinitions[0].Height = new GridLength(OnSide ? SidePillWidth : 34);
        foreach (var upright in new FrameworkElement[] { (FrameworkElement)_levelEyeL.Parent, _levelText, _devButton })
            upright.LayoutTransform = Turn(OnSide ? 90 : 0);
        // the list: under the bar at the top, beside it on a side
        _sideDevHost ??= new Grid { Width = DevListWidth, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, IsHitTestVisible = false };
        if (_sideDevHost.Parent == null) PanelsHost.Children.Add(_sideDevHost);
        _sideDevHost.HorizontalAlignment = PlaceRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var want = OnSide ? (Panel)_sideDevHost : VolumePanel;
        if (_devList.Parent is Panel had && had != want)
        {
            had.Children.Remove(_devList);
            want.Children.Add(_devList);
        }
        _devList.Margin = OnSide ? new Thickness(10, 10, 10, 10) : new Thickness(8, 0, 8, 10);
    }

    /// <summary>The outputs list beside the standing bar: its height (header + a row a device).</summary>
    private double SideDevListHeight => DevHeaderHeight + _devCount * DevRowHeight + 20;

    // ---- open, on a side: the tabs stand in a column against the edge (icons only), the page beside it keeps its width;
    //      the home cards go 2 x 2 (the column of tabs is that tall anyway)

    private const double RailWidth = 46, RailGap = 5, RailMinHeight = 16 + 9 * (23 + 2 * RailGap);
    private bool? _expandedArranged;

    private void ArrangeExpanded()
    {
        bool side = OnSide;
        int railCol = PlaceRight ? 1 : 0, pageCol = PlaceRight ? 0 : 1;
        var nav = (FrameworkElement)((FrameworkElement)NavBar.Parent).Parent; // the bottom row's grid
        if (_expandedArranged == side && (!side || Grid.GetColumn(nav) == railCol)) return;
        _expandedArranged = side;
        var header = (FrameworkElement)ExpandedPanel.Children[0];
        var pages = (FrameworkElement)ExpandedPanel.Children[1];
        ExpandedPanel.ColumnDefinitions.Clear();
        if (side)
        {
            ExpandedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = PlaceRight ? new GridLength(1, GridUnitType.Star) : new GridLength(RailWidth) });
            ExpandedPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = PlaceRight ? new GridLength(RailWidth) : new GridLength(1, GridUnitType.Star) });
        }
        ExpandedPanel.RowDefinitions[2].Height = new GridLength(side ? 0 : 40);
        ExpandedPanel.Width = 480 + (side ? RailWidth : 0);
        Grid.SetColumn(header, side ? pageCol : 0);
        Grid.SetColumn(pages, side ? pageCol : 0);
        Grid.SetColumn(nav, side ? railCol : 0);
        Grid.SetRow(nav, side ? 0 : 2);
        Grid.SetRowSpan(nav, side ? 3 : 1);
        nav.Margin = side ? new Thickness(0, 8, 0, 8) : new Thickness(12, 0, 12, 6);
        NavBar.Orientation = side ? Orientation.Vertical : Orientation.Horizontal;
        foreach (var (button, _) in _nav.Values) button.Margin = side ? new Thickness(0, RailGap, 0, RailGap) : new Thickness(2, 0, 2, 0);
        NavPill.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        NavPill.VerticalAlignment = side ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (!side) NavPill.Height = 26;
        NavPillX.Y = 0;

        // the home cards: a row of 4 at the top, 2 x 2 on a side
        HomeTab.RowDefinitions.Clear();
        if (side)
        {
            HomeTab.RowDefinitions.Add(new RowDefinition());
            HomeTab.RowDefinitions.Add(new RowDefinition());
        }
        for (int c = 0; c < HomeTab.ColumnDefinitions.Count; c++)
            HomeTab.ColumnDefinitions[c].Width = side && c >= 2 ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        int i = 0;
        foreach (var card in HomeTab.Children)
        {
            if (card is not Button b) continue;
            _homeCardMargins.TryAdd(b, b.Margin);
            Grid.SetColumn(b, side ? i % 2 : i);
            Grid.SetRow(b, side ? i / 2 : 0);
            b.Margin = side ? new Thickness(i % 2 == 0 ? 0 : 5, i < 2 ? 0 : 5, i % 2 == 0 ? 5 : 0, i < 2 ? 5 : 0) : _homeCardMargins[b];
            i++;
        }
        foreach (var (_, label) in _nav.Values) label.Margin = new Thickness(6, 0, 0, 0);
        Dispatcher.InvokeAsync(() => MoveNavPill(false), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private readonly System.Collections.Generic.Dictionary<Button, Thickness> _homeCardMargins = new();

    /// <summary>On a side the page loses the bottom row of tabs, but the island stays as tall as its column of tabs.</summary>
    private double ExpandedHeight(double topHeight) => !OnSide ? topHeight : Math.Max(RailMinHeight, topHeight - 40);

    // ---- the pages, standing: each fills the height the column of tabs gives the island, laid out to use it

    /// <summary>The page's height on a side (under the header, beside the column of tabs).</summary>
    private double SidePageHeight => RailMinHeight - 36;

    /// <summary>Every frame while open on a side: the page shown is as tall as the island (or its own height, if more).</summary>
    private void FitSidePage()
    {
        if (!OnSide || _state != IslandState.Expanded || _subOpen) return;
        if (TabElement(_tab) is not FrameworkElement page) return;
        // every page keeps some room between its last row and the island's bottom edge
        double pad = SidePageBottom;
        double want = Math.Max(TabHeight(_tab) - 76, SidePageHeight) - pad; // (a page taller than the tabs too: its gap is inside the island)
        if (Math.Abs(page.Height - want) > 0.5) page.Height = want;
        if (Math.Abs(page.Margin.Bottom - pad) > 0.5) page.Margin = new Thickness(page.Margin.Left, page.Margin.Top, page.Margin.Right, pad);
    }

    private const double SidePageBottom = 12;

    private readonly System.Collections.Generic.Dictionary<FrameworkElement, (Thickness margin, HorizontalAlignment ha, VerticalAlignment va, int col, Transform layout)> _pageTop = new();
    private Grid? _focusMid, _focusToday;
    private Border? _alarmFace;
    private StackPanel? _focusButtonRow;
    private FrameworkElement? _focusRing;

    private void ArrangePages()
    {
        bool side = OnSide;
        void Keep(FrameworkElement f) { if (!_pageTop.ContainsKey(f)) _pageTop[f] = (f.Margin, f.HorizontalAlignment, f.VerticalAlignment, Grid.GetColumn(f), f.LayoutTransform); }
        void Back(FrameworkElement f) { if (_pageTop.TryGetValue(f, out var t)) { f.Margin = t.margin; f.HorizontalAlignment = t.ha; f.VerticalAlignment = t.va; Grid.SetColumn(f, t.col); f.LayoutTransform = t.layout; } }

        // every page back to its own height at the top (FitSidePage stretches it on a side)
        if (!side)
            foreach (Tab t in Enum.GetValues(typeof(Tab)))
                if (TabElement(t) is FrameworkElement page && t != Tab.News)
                {
                    page.Height = TabHeight(t) - 76;
                    page.Margin = new Thickness(page.Margin.Left, page.Margin.Top, page.Margin.Right, 0);
                }

        // home: the 2 x 2 cards share the height
        double card = side ? (SidePageHeight - SidePageBottom - 10) / 2 : 112;
        foreach (var c in new[] { HomeClaudeCard, HomeMusicCard, HomeFocusCard, HomeWeatherCard }) c.Height = card;

        // music: a big cover (the lyrics under it, with room for more lines)
        ExpandedArt.Width = ExpandedArt.Height = side ? 96 : 54;
        ExpandedArt.CornerRadius = new CornerRadius(side ? 14 : 10);
        MusicTab.RowDefinitions[0].Height = new GridLength(side ? 106 : 62);
        LyricCurrent.MaxHeight = side ? 66 : 44;

        // focus: the ring big on the left with its buttons under it; the time, then the day's numbers, on the right
        _focusRing ??= (FrameworkElement)FocusTab.Children[0];
        _focusMid ??= (Grid)FocusTab.Children[1];
        _focusToday ??= (Grid)FocusTab.Children[2];
        // (the row of buttons under the time: found once, in the middle column's last row)
        _focusButtonRow ??= System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OfType<StackPanel>(_focusMid.Children), p => Grid.GetRow(p) == 4);
        var buttons = _focusButtonRow;
        if (buttons == null) return;
        foreach (var f in new FrameworkElement[] { _focusRing, _focusMid, _focusToday, buttons! }) Keep(f);
        FocusTab.ColumnDefinitions[0].Width = new GridLength(side ? 150 : 100);
        FocusTab.ColumnDefinitions[2].Width = new GridLength(side ? 0 : 124);
        if (side)
        {
            _focusRing.LayoutTransform = new ScaleTransform(1.35, 1.35);
            _focusRing.HorizontalAlignment = HorizontalAlignment.Center;
            _focusRing.VerticalAlignment = VerticalAlignment.Top;
            _focusRing.Margin = new Thickness(0, 16, 0, 0);
            if (buttons!.Parent == _focusMid)
            {
                _focusMid.Children.Remove(buttons);
                FocusTab.Children.Add(buttons);
            }
            Grid.SetColumn(buttons, 0);
            Grid.SetRow(buttons, 0);
            buttons.HorizontalAlignment = HorizontalAlignment.Center;
            buttons.VerticalAlignment = VerticalAlignment.Bottom;
            buttons.Margin = new Thickness(0, 0, 0, 14);
            _focusMid.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(_focusToday, 1);
            _focusToday.VerticalAlignment = VerticalAlignment.Bottom;
            _focusToday.Margin = new Thickness(2, 0, 10, 8);
            _focusToday.Height = 118;
            FocusWeek.Height = 34;
        }
        else
        {
            if (buttons!.Parent == FocusTab)
            {
                FocusTab.Children.Remove(buttons);
                _focusMid.Children.Add(buttons);
            }
            Grid.SetRow(buttons, 4);
            foreach (var f in new FrameworkElement[] { _focusRing, _focusMid, _focusToday, buttons }) Back(f);
            _focusToday.Height = double.NaN;
            FocusWeek.Height = 26;
        }

        // clock: the Timer and the Alarmes pages both showing, one above the other
        PlaceClockPages(animate: false);
        // (the little face before the next alarm goes: the Timer's ring just above already has eyes)
        if (_alarmFace != null)
        {
            _alarmFace.Visibility = side ? Visibility.Collapsed : Visibility.Visible;
            _alarmNext.Margin = new Thickness(side ? 0 : 9, 0, 0, 0);
        }
        UpdateHeader(); // (the Relógio's title: plain on a side)
        ArrangeAlarmEditor();

        ArrangeShelf();

        // copied: the list takes the height
        if (_clipScroller != null) _clipScroller.Height = side ? SidePageHeight - SidePageBottom - (ClipTabHeight - ClipListHeight) : ClipListHeight;

        OnClaudeChanged(); // (more recent jobs standing, and the week)
    }

    /// <summary>Claude, standing: the jobs of the last 7 days as little bars under today's number.</summary>
    private Grid? _claudeWeek;

    private void UpdateClaudeWeek()
    {
        if (_claudeWeek == null)
        {
            _claudeWeek = new Grid { Height = 46, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            var host = (Panel)ClaudeJobsSub.Parent;
            host.Children.Insert(host.Children.IndexOf(ClaudeJobsSub) + 1, _claudeWeek);
        }
        _claudeWeek.Visibility = OnSide ? Visibility.Visible : Visibility.Collapsed;
        if (!OnSide) return;
        _claudeWeek.Children.Clear();
        _claudeWeek.ColumnDefinitions.Clear();
        int[] counts = new int[7];
        for (int i = 0; i < 7; i++) counts[i] = _claude.JobsOn(DateTime.Today.AddDays(i - 6));
        int max = Math.Max(1, System.Linq.Enumerable.Max(counts));
        for (int i = 0; i < 7; i++)
        {
            _claudeWeek.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            var day = DateTime.Today.AddDays(i - 6);
            var col = new Grid();
            col.RowDefinitions.Add(new RowDefinition());
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var c = ClaudeColor;
            var bar = new Border
            {
                Width = 9, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom,
                Height = Math.Max(3, 30.0 * counts[i] / max),
                Background = new SolidColorBrush(i == 6 ? c : Color.FromArgb((byte)(counts[i] > 0 ? 0x77 : 0x33), c.R, c.G, c.B)),
                ToolTip = $"{counts[i]}"
            };
            col.Children.Add(bar);
            var label = new TextBlock
            {
                Text = Loc.Culture.DateTimeFormat.GetShortestDayName(day.DayOfWeek)[..1].ToUpper(), FontSize = 8.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0x77, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
            };
            Grid.SetRow(label, 1);
            col.Children.Add(label);
            Grid.SetColumn(col, i);
            _claudeWeek.Children.Add(col);
        }
    }

    // ---- the shelf, standing: the files in a grid (5 a row) filling the tall drop zone, scrolling down; a row at the top

    private Grid? _shelfRowHost, _shelfGridHost;

    private void ArrangeShelf()
    {
        if (_shelfRowHost == null || _shelfGridHost == null) return;
        var row = (ScrollViewer)_shelfRowHost.Children[0];
        var grid = (ScrollViewer)_shelfGridHost.Children[0];
        var (from, to) = OnSide ? (row, grid) : (grid, row);
        if (to.Content == ShelfItems) return;
        bool shown = ShelfScroll.Visibility == Visibility.Visible;
        from.Content = OnSide ? new Border() : null;
        to.Content = ShelfItems;
        ShelfItems.VerticalAlignment = OnSide ? VerticalAlignment.Top : VerticalAlignment.Center;
        ShelfItems.HorizontalAlignment = OnSide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        _shelfRowHost.Visibility = OnSide ? Visibility.Collapsed : Visibility.Visible;
        _shelfGridHost.Visibility = OnSide ? Visibility.Visible : Visibility.Collapsed;
        ShelfScroll = to;
        ShelfScroll.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (_shelfSel != null) _shelfSel.Viewer = to;
    }

    // ---- the alarm editor, standing: the time big on top, "rings in…" under it, then the name, the days and the
    //      buttons, all in a centred column (at the top: the time on the left, the rest beside it, as it was)

    private TextBlock? _edWhen;
    private bool? _editorArranged;

    private void ArrangeAlarmEditor()
    {
        if (_alarmEditor?.Child is not Grid row || _editorArranged == OnSide) return;
        _editorArranged = OnSide;
        bool side = OnSide;
        var time = (StackPanel)row.Children[0];
        var right = (StackPanel)row.Children[1];
        if (_edWhen == null)
        {
            _edWhen = new TextBlock { FontSize = 11.5, Foreground = White(0x80), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) };
            Grid.SetRow(_edWhen, 1);
            row.Children.Add(_edWhen);
        }
        row.ColumnDefinitions.Clear();
        row.RowDefinitions.Clear();
        if (side)
            for (int i = 0; i < 3; i++) row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
        }
        _edWhen.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(time, 0);
        Grid.SetRow(right, side ? 2 : 0);
        Grid.SetColumn(right, side ? 0 : 1);
        row.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        row.VerticalAlignment = side ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        time.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        right.Margin = side ? new Thickness(0, 10, 0, 0) : new Thickness(16, 0, 0, 0);
        foreach (FrameworkElement part in right.Children) part.HorizontalAlignment = side ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        // bigger: the time, the name field, the days (easy to click)
        foreach (var reel in new[] { _edHourText, _edMinuteText })
        {
            reel.FontSize = side ? 56 : 44;
            reel.Width = side ? 72 : 58;
        }
        ((TextBlock)time.Children[1]).FontSize = side ? 50 : 40;
        if (_edName.Parent is FrameworkElement nameGrid) nameGrid.Width = side ? 240 : 132;
        foreach (var chip in _edDayChips)
        {
            chip.Width = chip.Height = side ? 26 : 18;
            chip.CornerRadius = new CornerRadius(side ? 13 : 9);
            chip.Margin = new Thickness(side ? 3 : 0, 0, 3, 0);
            ((TextBlock)chip.Child).FontSize = side ? 11 : 9.5;
        }
        UpdateEditorTexts();
    }
}
