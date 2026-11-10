using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Notchy;

/// <summary>
/// With the island open, a notification comes down as a separate pill under it, so whatever is on the island stays as
/// it was. If the island closes, the pill goes up with it and carries on as the usual card. Both can be dismissed
/// with the ✕ or by dragging them sideways: the notification stays in the bell's history.
/// </summary>
public partial class MainWindow
{
    private const double PillWidth = 380, PillHeight = 62, PillRadius = 22, PillGap = 10, SwipeStart = 6, SwipeDismiss = 90, SwipeFlyTo = 560;
    private const double NoticeContentDrop = 2; // the card's content sits this far below its middle
    private const double PillMinSecondsOnCard = 2, PillKeepSeconds = 1.5, HoverIgnoreMsAfterDismiss = 700, PillIdleBeforeCardMs = 300, CardBusyBeforePillMs = 200, PillMorphMs = 380;
    // the coloured line: the same on the pill and around the minimized island
    private const double NoticeLineThickness = 1.5, NoticeLineOpacity = 0.45, NoticeGlowBlur = 8, NoticeGlowOpacity = 0.6;
    private static readonly Color NoticeNoAccent = Color.FromRgb(0xE6, 0xE6, 0xEE);
    private static readonly Brush PillNoGlass = new SolidColorBrush(Color.FromArgb(0xE6, 0, 0, 0));

    private Grid _noticePill = null!;
    private readonly Grid _pillRow = new();
    private Border _pillBody = null!, _pillGlow = null!, _pillAvatar = null!;
    private TextBlock _pillApp = null!, _pillTitle = null!, _pillText = null!, _pillMore = null!;
    private DropShadowEffect _pillGlowEffect = null!;
    private readonly ScaleTransform _pillScale = new(1, 1);
    private readonly TranslateTransform _pillMove = new(), _noticeCardMove = new();
    private bool _noticeInPill, _noticeSwiping;
    /// <summary>A pill was dismissed: the open island stays as it is until the mouse has been back on it.</summary>
    private bool _holdOpenAfterPill;
    private DateTime _pillUntil, _ignoreHoverUntil;
    private DateTime? _pillIdleSince, _cardBusySince;
    private bool _pillMorphing, _pillFrozen;
    private Color _noticeAccent = NoticeNoAccent;

    private void BuildNoticePill()
    {
        _pillGlowEffect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = NoticeGlowBlur, Opacity = NoticeGlowOpacity };
        _pillGlow = new Border { CornerRadius = new CornerRadius(PillRadius), BorderThickness = new Thickness(NoticeLineThickness), Opacity = NoticeLineOpacity, Effect = _pillGlowEffect, IsHitTestVisible = false };
        _pillBody = new Border { CornerRadius = new CornerRadius(PillRadius), Padding = new Thickness(20, 0, 16, 0) };

        var row = _pillRow;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pillAvatar = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_pillAvatar);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texts, 1);
        _pillApp = new TextBlock { FontSize = 10.5, Foreground = White(0x88), TextTrimming = TextTrimming.CharacterEllipsis };
        _pillTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        _pillText = new TextBlock { FontSize = 12, Foreground = White(0xCC), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxHeight = 18 };
        texts.Children.Add(_pillApp);
        texts.Children.Add(_pillTitle);
        texts.Children.Add(_pillText);
        row.Children.Add(texts);
        _pillMore = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(_pillMore, 2);
        row.Children.Add(_pillMore);
        var close = NoticeCloseButton();
        close.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(close, 3);
        row.Children.Add(close);
        _pillBody.Child = row;

        _noticePill = new Grid
        {
            Width = PillWidth, Height = PillHeight, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0),
            RenderTransform = new TransformGroup { Children = { _pillScale, _pillMove } }
        };
        _noticePill.Children.Add(_pillBody);
        _noticePill.Children.Add(_pillGlow);
        // moving between the island and the pill doesn't count as leaving
        // (under the minimized island it's just a card: hovering it must not open the island)
        _noticePill.MouseEnter += (s, e) => { if (_state == IslandState.Expanded) Island_MouseEnter(s, e); };
        _noticePill.MouseLeave += (s, e) => { if (_state == IslandState.Expanded) Island_MouseLeave(s, e); };
        MakeSwipeable(_noticePill, _pillMove, close, ClickNotice, fadeTowardsEdge: true);

        var parent = (Panel)Island.Parent;
        parent.Children.Insert(parent.Children.IndexOf(Island), _noticePill); // behind the island: it goes up under it
    }

    /// <summary>The ✕ of a notification: it shows while the mouse is on the card.</summary>
    private Border NoticeCloseButton()
    {
        var close = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = White(0x26), Opacity = 0, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            DismissNotice();
        };
        return close;
    }

    /// <summary>Hover shows the ✕; a click is a click; dragging sideways far enough throws the card away.</summary>
    private void MakeSwipeable(FrameworkElement card, TranslateTransform move, UIElement close, Action click, bool fadeTowardsEdge = false)
    {
        Point? start = null;
        card.MouseEnter += (_, _) => close.Opacity = 1;
        card.MouseLeave += (_, _) => { if (!_noticeSwiping) close.Opacity = 0; };
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            start = e.GetPosition(this);
            card.CaptureMouse();
        };
        card.MouseMove += (_, e) =>
        {
            if (start is not Point from || !card.IsMouseCaptured) return;
            double dx = e.GetPosition(this).X - from.X;
            if (!_noticeSwiping && Math.Abs(dx) < SwipeStart) return;
            _noticeSwiping = true;
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.X = dx;
            if (fadeTowardsEdge)
            {
                // fully gone by the time it reaches the edge of the area it can be drawn in
                double toEdge = Math.Max(1, (ActualWidth - PillWidth * S.SizeScale) / 2);
                card.BeginAnimation(OpacityProperty, null);
                card.Opacity = Math.Clamp(1 - Math.Abs(dx) / toEdge, 0, 1);
            }
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (start == null) return;
            start = null;
            card.ReleaseMouseCapture();
            if (!_noticeSwiping)
            {
                click();
                return;
            }
            double x = move.X;
            bool throwAway = Math.Abs(x) > SwipeDismiss;
            var time = TimeSpan.FromMilliseconds(Dur(throwAway ? 200 : 260));
            var slide = new DoubleAnimation(throwAway ? Math.Sign(x) * SwipeFlyTo : 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            slide.Completed += (_, _) =>
            {
                _noticeSwiping = false;
                if (throwAway) DismissNotice(); // (first: back in its place it's already invisible, so it can't flash there)
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.X = 0;
            };
            move.BeginAnimation(TranslateTransform.XProperty, slide);
            if (throwAway) card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time));
            else if (fadeTowardsEdge) card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, time));
        };
    }

    /// <summary>
    /// The minimized island is showing something (music, Claude, a timer, a download, a quick notice...). The paused
    /// music and the alarm's little bell don't count: those are just the resting island.
    /// </summary>
    private bool IslandBusy =>
#if DEBUG
        !_testIslandNotBusy &&
#endif
        (_state == IslandState.Volume || (_state == IslandState.Compact && _compactMode is not (CompactMode.None or CompactMode.MusicPaused or CompactMode.Notice)));

#if DEBUG
    private bool _testIslandNotBusy;
#endif

    /// <summary>When the island is in use (open, or minimized and busy), a notification goes under it instead of replacing what's there.</summary>
    private bool NoticeGoesUnderIsland => !_hiddenForFullscreen && (_state == IslandState.Expanded || IslandBusy);

    /// <summary>The activity over the pill ended (after a moment, so the gap between two songs doesn't count): it goes up and carries on as the card.</summary>
    private void CheckPillStillNeeded()
    {
        if (_state == IslandState.Expanded || IslandBusy || _noticePill.IsMouseOver || _noticeSwiping)
        {
            _pillIdleSince = null;
            return;
        }
        _pillIdleSince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _pillIdleSince.Value).TotalMilliseconds < PillIdleBeforeCardMs) return;
        _pillIdleSince = null;
        MorphPillIntoCard();
    }

    /// <summary>An activity started under a notification card: it goes back to being a pill, and the activity takes the island.</summary>
    private void CheckCardStillFits()
    {
        bool activity =
#if DEBUG
            !_testIslandNotBusy &&
#endif
            (_ring != null || DecideCompactWithoutTransfer() != CompactMode.None || ActiveTransfer != null);
        if (!activity || _pillMorphing || _noticeSwiping || _noticePanel.IsMouseOver || _hiddenForFullscreen || _state != IslandState.Compact || _compactMode != CompactMode.Notice)
        {
            _cardBusySince = null;
            return;
        }
        _cardBusySince ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - _cardBusySince.Value).TotalMilliseconds < CardBusyBeforePillMs) return;
        _cardBusySince = null;
        TurnCardIntoPill();
    }

    /// <summary>Where the pill must be, and how tall, to sit exactly where the card would be (attached: its top corners behind the screen's edge).</summary>
    private (double y, double height, double contentTop) PillShapeOfCard()
    {
        double scale = S.SizeScale, extra = _topHidden > 0 ? PillRadius : 0, screenTop = Math.Max(Island.Margin.Top, 0);
        double cardBottom = Island.Margin.Top + (NoticeHeight + _topHidden) * scale;
        double pillTop = screenTop - extra * scale, height = (cardBottom - pillTop) / scale;
        // the content stays centred in what's left under this margin: it has to land exactly on the card's own centre
        double cardCentre = Island.Margin.Top + (_topHidden + NoticeHeight / 2 + NoticeContentDrop) * scale;
        return (-(PillGap + extra) * scale, height, 2 * (cardCentre - pillTop) / scale - height);
    }

    private void SetIslandSizeNow(double width, double height)
    {
        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        Island.Width = width;
        Island.Height = height;
    }

    private void ResetPillShape()
    {
        _pillFrozen = false;
        _pillMove.BeginAnimation(TranslateTransform.YProperty, null);
        _pillMove.Y = 0;
        _noticePill.BeginAnimation(HeightProperty, null);
        _noticePill.Height = PillHeight;
        _pillRow.BeginAnimation(MarginProperty, null);
        _pillRow.Margin = new Thickness(0);
    }

    /// <summary>The activity ended: its island goes up off the screen while the pill rises to take its place and becomes the island.</summary>
    private void MorphPillIntoCard()
    {
        if (!_noticeInPill || _noticeShown == null || _pillMorphing) return;
        _pillMorphing = true;
        _noticePill.IsHitTestVisible = false;
        double left = Math.Max(PillMinSecondsOnCard, (_pillUntil - DateTime.UtcNow).TotalSeconds);
        var time = TimeSpan.FromMilliseconds(Dur(PillMorphMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(_topHidden / S.SizeScale, time) { EasingFunction = ease });
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(NoticeWidth, time) { EasingFunction = ease });
        var (y, height, contentTop) = PillShapeOfCard();
        _pillRow.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0, contentTop, 0, 0), time) { EasingFunction = ease });
        _pillMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, time) { EasingFunction = ease });
        var grow = new DoubleAnimation(height, time) { EasingFunction = ease };
        grow.Completed += (_, _) => FinishPillMorph(left);
        _noticePill.BeginAnimation(HeightProperty, grow);
    }

    private void FinishPillMorph(double secondsLeft)
    {
        _pillMorphing = false;
        var settle = TimeSpan.FromMilliseconds(Dur(PillMorphMs * 0.7));
        if (!_noticeInPill || _noticeShown == null || _state == IslandState.Expanded || IslandBusy)
        {
            // something else took over in the meantime: everything back in its place
            AnimateIslandSize(TargetSize(_state), bigMove: false);
            _pillMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, settle));
            _noticePill.BeginAnimation(HeightProperty, new DoubleAnimation(PillHeight, settle));
            _pillRow.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0), settle));
            _noticePill.IsHitTestVisible = _noticeInPill;
            return;
        }

        // the island appears exactly where the pill is, in front of it
        _noticeInPill = false;
        _pillFrozen = true;
        ShowTransient(CompactMode.Notice, "", "", secondsLeft);
        var size = TargetSize(IslandState.Compact);
        SetIslandSizeNow(size.Width, size.Height + _topHidden);
        _noticePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.Zero));
        // (what the resting island showed would still be fading out, in the middle of the card that just arrived)
        foreach (var resting in new UIElement[] { IdlePanel, CompactPanel }) resting.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.Zero));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(120)));
        fade.Completed += (_, _) =>
        {
            if (_noticeInPill) return;
            _noticePill.Visibility = Visibility.Collapsed;
            ResetPillShape();
        };
        _noticePill.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The reverse: the card becomes a pill where it is, and goes down as the activity's island comes in above it.</summary>
    private void TurnCardIntoPill()
    {
        if (_noticeShown is not { } n) return;
        double left = Math.Max(PillMinSecondsOnCard, (_transientUntil - DateTime.UtcNow).TotalSeconds);
        ResetPillShape();
        FillNoticePill(n);
        _noticePill.BeginAnimation(OpacityProperty, null);
        _noticePill.Opacity = 1;
        _noticePill.Visibility = Visibility.Visible;
        var (y, height, contentTop) = PillShapeOfCard();
        _pillRow.Margin = new Thickness(0, contentTop, 0, 0);
        _pillMove.Y = y;
        _noticePill.Height = height;
        SetIslandSizeNow(Island.ActualWidth, _topHidden / S.SizeScale);
        Island.UpdateLayout();
        PlaceNoticePill();

        _noticeInPill = true;
        _pillUntil = DateTime.UtcNow.AddSeconds(left);
        _transient = CompactMode.None;
        UpdateState(); // the activity's island comes down, and the pill keeps right under it

        var time = TimeSpan.FromMilliseconds(Dur(PillMorphMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _pillMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, 0, time) { EasingFunction = ease });
        _pillRow.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0, contentTop, 0, 0), new Thickness(0), time) { EasingFunction = ease });
        var shrink = new DoubleAnimation(height, PillHeight, time) { EasingFunction = ease };
        shrink.Completed += (_, _) => { if (_noticeInPill) _noticePill.IsHitTestVisible = true; };
        _noticePill.BeginAnimation(HeightProperty, shrink);
    }

    private void FillNoticePill(Notice n)
    {
        _pillApp.Text = $"{n.App} · {Loc.T("notif.now")}";
        _pillTitle.Text = n.Title;
        _pillText.Text = n.Body;
        _pillText.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _pillAvatar.CornerRadius = new CornerRadius(n.IsPhoto ? 20 : 11);
        _pillAvatar.Background = n.Avatar != null ? new ImageBrush(n.Avatar) { Stretch = Stretch.UniformToFill } : White(0x14);
        _pillBody.Background = Island.Background;
        _pillGlow.BorderBrush = new SolidColorBrush(_noticeAccent);
        _pillGlowEffect.Color = _noticeAccent;
        _pillScale.ScaleX = _pillScale.ScaleY = S.SizeScale;
    }

    private void ShowNoticePill(Notice n)
    {
        ResetPillShape();
        _noticeInPill = true;
        _pillUntil = DateTime.UtcNow.AddSeconds(NoticeSeconds);
        FillNoticePill(n);
        _noticePill.Visibility = Visibility.Visible;
        _noticePill.IsHitTestVisible = true;
        PlaceNoticePill();
        var time = TimeSpan.FromMilliseconds(Dur(360));
        _pillMove.BeginAnimation(TranslateTransform.XProperty, null);
        _pillMove.X = 0;
        _pillMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-22, 0, time) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
        _noticePill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(220))));
    }

    private void HideNoticePill()
    {
        _noticeInPill = false;
        _noticePill.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(240)));
        fade.Completed += (_, _) => { if (!_noticeInPill) _noticePill.Visibility = Visibility.Collapsed; };
        _noticePill.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Every frame: right under the island, whatever size it is at the moment (that's also how it goes up with it).</summary>
    private void PlaceNoticePill()
    {
        if (_noticePill.Visibility != Visibility.Visible || _pillFrozen) return;
        var island = Island.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Island.ActualWidth, Island.ActualHeight));
        _noticePill.Margin = new Thickness(0, island.Bottom + PillGap * S.SizeScale, 0, 0);
    }

    /// <summary>The island closed under a pill: the same notification carries on as the card, for the time it had left.</summary>
    private void TurnPillIntoCard()
    {
        if (!_noticeInPill || _noticeShown == null) return;
        if (IslandBusy) return; // what's on the island stays there, the pill under it
        double left = Math.Max(PillMinSecondsOnCard, (_pillUntil - DateTime.UtcNow).TotalSeconds);
        HideNoticePill();
        ShowTransient(CompactMode.Notice, "", "", left);
    }

    private void ClickNotice()
    {
        if (_noticeShown is not { } n) return;
        DismissNotice();
        OpenNoticeApp(n);
    }

    /// <summary>Done with this one (it stays in the bell's history): the next one in line, if any.</summary>
    private void DismissNotice()
    {
        if (_noticeShown == null) return;
        if (_noticeInPill)
        {
            _holdOpenAfterPill = _state == IslandState.Expanded;
            HideNoticePill();
        }
        else
        {
            // dismissing it isn't asking for the island: the mouse that was on the card doesn't open it
            _hoverTimer.Stop();
            _hoverActive = _hoverRequested = false;
            _ignoreHoverUntil = DateTime.UtcNow.AddMilliseconds(HoverIgnoreMsAfterDismiss);
        }
        _noticeShown = null;
        if (_transient == CompactMode.Notice) _transient = CompactMode.None;
        if (_noticeQueue.Count > 0)
        {
            ShowNextNotice();
            return;
        }
        UpdateState();
        HideAfterCard();
    }
}
