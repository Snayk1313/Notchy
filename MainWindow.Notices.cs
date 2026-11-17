using System;
using System.Collections.Generic;
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
/// Notifications from other apps on the island. When one arrives, the island drops down into a card (like an
/// iPhone banner): the sender's photo (or the app's icon), "WhatsApp · agora", who sent it and
/// the message. Several in a row come one after another ("+2" while more wait). A bell next to the clock counts
/// the new ones and opens "‹ Notificações", grouped by app; clicking one opens its app.
/// </summary>
public partial class MainWindow
{
    private const double NoticeWidth = 380, NoticeHeight = 76, NoticeSeconds = 5;

    private readonly NotificationService _notices = new();
    private readonly Queue<Notice> _noticeQueue = new();
    private Notice? _noticeShown;
    private int _noticeUnseen;

    private Grid _noticePanel = null!;
    private Border _noticeAvatar = null!;
    private TextBlock _noticeApp = null!, _noticeTitle = null!, _noticeBody = null!, _noticeMore = null!;
    private Border _bell = null!;
    private TextBlock _bellBadge = null!;

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitNotices()
    {
        _noticePanel = new Grid { Height = NoticeHeight, VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false, Cursor = Cursors.Hand };
        _noticePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _noticePanel.ColumnDefinitions.Add(new ColumnDefinition());
        _noticeAvatar = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(20, 4, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = White(0x14) };
        _noticePanel.Children.Add(_noticeAvatar);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 44, 0) };
        Grid.SetColumn(texts, 1);
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        _noticeApp = new TextBlock { FontSize = 10.5, Foreground = White(0x88), VerticalAlignment = VerticalAlignment.Center };
        top.Children.Add(_noticeApp);
        texts.Children.Add(top);
        _noticeTitle = new TextBlock { FontSize = 13, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
        texts.Children.Add(_noticeTitle);
        _noticeBody = new TextBlock { FontSize = 12, Foreground = White(0xCC), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxHeight = 18 };
        texts.Children.Add(_noticeBody);
        _noticePanel.Children.Add(texts);

        _noticeMore = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 18, 0), Visibility = Visibility.Collapsed };
        Grid.SetColumn(_noticeMore, 1);
        _noticePanel.Children.Add(_noticeMore);

        var close = NoticeCloseButton();
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 4, 16, 0);
        Grid.SetColumn(close, 1);
        _noticePanel.Children.Add(close);

        // a click on the card opens the app; dragging it sideways dismisses it
        _noticePanel.RenderTransform = _noticeCardMove;
        MakeSwipeable(_noticePanel, _noticeCardMove, close, ClickNotice);
        ((Panel)CompactPanel.Parent).Children.Add(_noticePanel);

        BuildNoticePill();
        BuildBell();
        _notices.AccessChecked += SyncQuietMode;
        _notices.Arrived += OnNotice;
        _notices.HistoryChanged += () => { if (_subOpen && _subTitle.Text == Loc.T("notif.title")) RefreshSubPage(NoticesPage); };
        _notices.Start();
    }

#if DEBUG
    internal void SendNoticeTest() => _notices.SendTest();
#endif

    /// <summary>"Só no Notchy" only applies once Notchy is known to be able to read notifications: otherwise they'd show nowhere.</summary>
    private void SyncQuietMode()
    {
        if (!_notices.AccessKnown) return;
        bool quiet = S.NotifyEnabled && S.NotifyOnlyNotchy && _notices.Available;
        QuietMode.Sync(quiet);
        _notices.WatchTray = quiet;
    }

    /// <summary>A notification card or a ringing timer/alarm that may show over a full-screen game (each has its own option).</summary>
    private bool FullscreenCardUp => S.NotifyFullscreen && _noticeShown != null && !_noticeInPill || S.RingFullscreen && _ring != null;

    /// <summary>Full screen with nothing allowed to show: the mouse can't open the island.</summary>
    private bool FullscreenHidesIsland => _hiddenForFullscreen && Island.Visibility != Visibility.Visible;

    /// <summary>A full-screen game or video hides the island, except while the volume/brightness bar or a notification card / ringing timer (if that's turned on) is up.</summary>
    private void ApplyFullscreenHide()
    {
        Island.Visibility = _hiddenForFullscreen && !(FullscreenCardUp || _state == IslandState.Volume) ? Visibility.Hidden : Visibility.Visible;
        // the game may have covered the glass under the island since it was last put in place
        if (_hiddenForFullscreen && Island.Visibility == Visibility.Visible && _backdrop != null && _backdrop.Hwnd != IntPtr.Zero) _backdrop.KeepBelow(_hwnd);
    }

    /// <summary>
    /// Full screen: once the card (notification, ringing timer) or bar is done the island goes at once, with no closing
    /// animation over the game, and the mouse that was on it doesn't open it. Call it before the state changes.
    /// </summary>
    private void HideAfterCard()
    {
        if (!_hiddenForFullscreen) return;
        ApplyFullscreenHide();
        if (Island.Visibility == Visibility.Visible) return;
        _hoverTimer.Stop();
        _hoverRequested = _hoverActive = _clickOpened = false;
        _ignoreHoverUntil = DateTime.UtcNow.AddSeconds(1);
    }

    /// <summary>The bell next to the clock, with how many arrived since you last looked.</summary>
    private void BuildBell()
    {
        var right = (StackPanel)MoreButton.Parent;
        _bellBadge = new TextBlock { FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        var badge = new Border { Child = _bellBadge, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)), CornerRadius = new CornerRadius(6.5), Padding = new Thickness(4, 0, 4, 0),
            MinWidth = 13, Height = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -5, -6, 0) };
        _bellBadge.HorizontalAlignment = HorizontalAlignment.Center;
        var g = new Grid();
        g.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = White(0xDD), VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(badge);
        _bell = new Border { Child = g, Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(10), Background = White(0x0E), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = Loc.T("notif.title") };
        _bell.MouseEnter += (_, _) => _bell.Background = White(0x20);
        _bell.MouseLeave += (_, _) => _bell.Background = White(0x0E);
        _bell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            _noticeUnseen = 0;
            UpdateBell();
            OpenSubPage(Loc.T("notif.title"), NoticesPage);
        };
        right.Children.Insert(right.Children.IndexOf(MoreButton), _bell);
        UpdateBell();
    }

    private void UpdateBell()
    {
        var badge = (FrameworkElement)_bellBadge.Parent;
        string count = _noticeUnseen > 9 ? "9+" : _noticeUnseen.ToString();
        bool was = badge.Visibility == Visibility.Visible, changed = _bellBadge.Text != count;
        _bellBadge.Text = count;
        badge.Visibility = _noticeUnseen > 0 ? Visibility.Visible : Visibility.Collapsed;
        // it grows in when it shows up, and hops when another one arrives
        if (_noticeUnseen > 0 && (changed || !was)) PopIn(badge, was ? 1.5 : 0.2);
        _bell.Visibility = _notices.Available && S.NotifyEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    // =====================================================================================
    // arriving
    // =====================================================================================

    private void OnNotice(Notice n)
    {
        // remember the apps, for the list in the settings
        if (!S.NotifyApps.Contains(n.App))
        {
            S.NotifyApps.Add(n.App);
            S.Save();
        }
        if (!S.NotifyEnabled || S.NotifyMuted.Contains(n.App)) return;
        _noticeUnseen++;
        UpdateBell();
        // focusing with "Não perturbe", or a game/video full screen (unless the card may show there): kept in the history, not shown
        if (SoundService.Hushed || _hiddenForFullscreen && !S.NotifyFullscreen) return;
        _noticeQueue.Enqueue(n);
        if (_noticeShown == null) ShowNextNotice();
        else UpdateNoticeCount();
    }

    private void UpdateNoticeCount()
    {
        _noticeMore.Text = _pillMore.Text = "+" + _noticeQueue.Count;
        _noticeMore.Visibility = _pillMore.Visibility = _noticeQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowNextNotice()
    {
        if (_noticeQueue.Count == 0)
        {
            _noticeShown = null;
            return;
        }
        var n = _noticeQueue.Dequeue();
        _noticeShown = n;
        _noticeApp.Text = $"{n.App} · {Loc.T("notif.now")}";
        _noticeTitle.Text = n.Title;
        _noticeBody.Text = n.Body;
        _noticeBody.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _noticeAvatar.CornerRadius = new CornerRadius(n.IsPhoto ? 20 : 11);
        _noticeAvatar.Background = n.Avatar != null ? new ImageBrush(n.Avatar) { Stretch = Stretch.UniformToFill } : White(0x14);
        _noticeAccent = n.Accent ?? NoticeNoAccent;
        UpdateNoticeCount();
        ApplyFullscreenHide();
        SoundService.Play(Sfx.Notice);
        // the island in use isn't disturbed: under it, as a pill
        if (NoticeGoesUnderIsland)
        {
            ShowNoticePill(n);
            return;
        }
        ShowTransient(CompactMode.Notice, "", "", NoticeSeconds);
        if (_state == IslandState.Compact && _compactMode == CompactMode.Notice) Fade(_noticePanel, true); // (the one before may have been swiped away)
    }

    private void EndNotice()
    {
        if (_transient == CompactMode.Notice) _transient = CompactMode.None;
        _noticeShown = null;
        HideAfterCard();
        UpdateState();
    }

    /// <summary>Every frame: when a card's time is up, the next one (if any) comes down.</summary>
    private void UpdateNoticesFrame()
    {
        PlaceNoticePill();
        if (_noticeShown == null) return;
        if (_pillMorphing) return;
        CheckCardStillFits();
        if (_noticeInPill)
        {
            if (_noticePill.IsMouseOver || _noticeSwiping) _pillUntil = DateTime.UtcNow.AddSeconds(PillKeepSeconds);
            if (DateTime.UtcNow < _pillUntil)
            {
                CheckPillStillNeeded();
                return;
            }
            HideNoticePill();
            _noticeShown = null;
            if (_noticeQueue.Count > 0) ShowNextNotice();
            return;
        }
        // reading it: it waits while the mouse is on it
        if (_transient == CompactMode.Notice && (_noticePanel.IsMouseOver || _noticeSwiping)) _transientUntil = DateTime.UtcNow.AddSeconds(1.5);
        if (_transient == CompactMode.Notice && DateTime.UtcNow < _transientUntil) return;
        _noticeShown = null;
        if (_noticeQueue.Count > 0) ShowNextNotice();
        else HideAfterCard();
    }

    // =====================================================================================
    // the history
    // =====================================================================================

    private FrameworkElement NoticesPage()
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var all = _notices.History.Where(n => !S.NotifyMuted.Contains(n.App)).ToList();
        if (_noticeCleared != null) list.Children.Add(ListGlide.Key(NoticeUndoRow(), "undo"));
        var groups = all.GroupBy(n => n.App).ToList();
        var everyRow = new List<FrameworkElement>();
        list.Children.Add(ListGlide.Key(ListTop(all.Count == 0 ? Loc.T("notif.empty") : (all.Count == 1 ? Loc.T("notif.count1") : Loc.F("notif.count", all.Count)),
            all.Count == 0 ? null : ListPill(Glyph(0xE74D), Loc.T("notif.clear"), false, () => ClearNotices(all, Loc.T("notif.cleared.all"), everyRow))), "top"));
        // grouped by app, the app with the newest one first
        foreach (var group in groups)
        {
            var items = group.ToList();
            var rows = new StackPanel();
            // the same person / conversation more than once: one stack (iOS-like), opened with a click
            foreach (var chat in items.GroupBy(n => n.Title).Take(6))
            {
                var msgs = chat.ToList();
                if (msgs.Count > 1)
                {
                    rows.Children.Add(ListGlide.Key(NoticeStack(msgs.Take(5).ToList(), msgs), "stack:" + group.Key + "|" + chat.Key));
                    continue;
                }
                var n = msgs[0];
                var row = NoticeRow(n);
                row.Tag = new GlideKey(n);
                SwipeToClear(row, () => new[] { row }, () => ClearNotices(new List<Notice> { n }, Loc.T("notif.cleared.one"), null));
                rows.Children.Add(row);
            }
            var body = new Border { Child = rows, ClipToBounds = true };
            if (_noticeFolded.Contains(group.Key)) body.Height = 0;
            Border head = null!;
            head = NoticeGroupHead(items, body,
                () => ClearNotices(items, Loc.F("notif.cleared.app", group.Key), new List<FrameworkElement> { head }.Concat(rows.Children.OfType<FrameworkElement>()).ToList()));
            SwipeToClear(head, () => new FrameworkElement[] { head, body }, () => ClearNotices(items, Loc.F("notif.cleared.app", group.Key), null));
            head.Tag = new GlideKey("head:" + group.Key);
            body.Tag = new GlideKey("body:" + group.Key);
            list.Children.Add(head);
            list.Children.Add(body);
            everyRow.Add(head);
            everyRow.AddRange(rows.Children.OfType<FrameworkElement>());
        }
        // always in a scroller: an opened stack or group can make it taller than the island after it's built
        return Scroller(list, maxHeight: MaxSubHeight - 10);
    }

    // ---- the same conversation: a stack

    private const double StackPeek = 7, StackGap = 4;
    private readonly HashSet<string> _noticeOpenStacks = new();

    /// <summary>
    /// Several notifications from one person or conversation. Closed: the newest in front, the next two peeking out under
    /// it, smaller and dimmer; a click fans them out one under the other, with "Mostrar menos" above to gather them again.
    /// Swiping it clears the whole conversation.
    /// </summary>
    private FrameworkElement NoticeStack(List<Notice> shown, List<Notice> all)
    {
        string key = shown[0].App + "\n" + shown[0].Title;
        var cards = new List<(Border card, FrameworkElement row, ScaleTransform scale, TranslateTransform move, object? tip)>();
        var deck = new Grid { Background = Brushes.Transparent };
        bool IsOpen() => _noticeOpenStacks.Contains(key);
        // "+3": how many more are under the front one (only while it's closed)
        var more = new Border { CornerRadius = new CornerRadius(8), Background = White(0x22), Padding = new Thickness(6, 0, 6, 1),
            Child = new TextBlock { Text = "+" + (all.Count - 1), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White } };
        foreach (var n in shown)
        {
            var row = NoticeRow(n, n == shown[0] ? more : null);
            row.Margin = new Thickness(0);
            var card = new Border { Child = row, CornerRadius = new CornerRadius(13), Background = White(0x10) };
            // the holder keeps the card's place in the stack; the card inside is what a swipe moves
            var scale = new ScaleTransform(1, 1);
            var move = new TranslateTransform();
            var holder = new Border { Child = card, VerticalAlignment = VerticalAlignment.Top, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new TransformGroup { Children = { scale, move } } };
            var one = n;
            SwipeToClear(card, () => new[] { card }, () => ClearNotices(new List<Notice> { one }, Loc.T("notif.cleared.one"), null), active: IsOpen);
            cards.Add((holder, row, scale, move, row.ToolTip));
        }
        for (int i = cards.Count - 1; i >= 0; i--) deck.Children.Add(cards[i].card); // the newest drawn last: in front
        cards[0].card.Measure(new Size(440, double.PositiveInfinity));
        double h = cards[0].card.DesiredSize.Height;

        var less = new DockPanel { Height = 0, Opacity = 0, ClipToBounds = true, Margin = new Thickness(6, 0, 0, 0) };
        var gather = ListPill("", Loc.T("notif.less"), false, () => Lay(false));
        gather.Padding = new Thickness(9, 1, 10, 2);
        DockPanel.SetDock(gather, Dock.Right);
        less.Children.Add(gather);
        less.Children.Add(new TextBlock { Text = $"{shown[0].Title} · {all.Count}", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = White(0x99),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var wrap = new StackPanel { Margin = new Thickness(0, 1, 0, 3) };
        wrap.Children.Add(less);
        wrap.Children.Add(deck);

        void Lay(bool open, bool animate = true)
        {
            if (open) _noticeOpenStacks.Add(key);
            else _noticeOpenStacks.Remove(key);
            var time = TimeSpan.FromMilliseconds(Dur(animate ? 380 : 0));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            void To(Animatable target, DependencyProperty prop, double value) =>
                target.BeginAnimation(prop, new DoubleAnimation(value, time) { EasingFunction = ease });
            for (int i = 0; i < cards.Count; i++)
            {
                var (card, row, scale, move, tip) = cards[i];
                int back = Math.Min(i, 2);
                To(move, TranslateTransform.YProperty, open ? i * (h + StackGap) : back * StackPeek);
                To(scale, ScaleTransform.ScaleXProperty, open ? 1 : 1 - back * 0.06);
                To(scale, ScaleTransform.ScaleYProperty, open ? 1 : 1 - back * 0.06);
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(open || i == 0 ? 1 : i <= 2 ? (i == 1 ? 0.55 : 0.3) : 0, time) { EasingFunction = ease });
                // closing, the text behind goes first: the cards are see-through, it would show through the front one
                var textTime = open || !animate ? time : TimeSpan.FromMilliseconds(Dur(130));
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(open || i == 0 ? 1 : 0, textTime) { EasingFunction = ease });
                card.IsHitTestVisible = open || i == 0;
                row.ToolTip = open ? tip : null; // closed, a click opens the stack, not the app
            }
            // the island measures the final sizes first, then everything moves from where it is
            double fromDeck = deck.ActualHeight, fromLess = less.ActualHeight;
            double toDeck = open ? cards.Count * (h + StackGap) - StackGap : h + Math.Min(cards.Count - 1, 2) * StackPeek;
            double toLess = open ? 24 : 0;
            deck.BeginAnimation(HeightProperty, null);
            less.BeginAnimation(HeightProperty, null);
            deck.Height = toDeck;
            less.Height = toLess;
            if (animate) ResizeSubPage();
            if (animate && fromDeck > 0)
            {
                deck.BeginAnimation(HeightProperty, new DoubleAnimation(fromDeck, toDeck, time) { EasingFunction = ease });
                less.BeginAnimation(HeightProperty, new DoubleAnimation(fromLess, toLess, time) { EasingFunction = ease });
            }
            less.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, time) { EasingFunction = ease });
            // (gone from the layout once faded, so the open card's line is like the others)
            if (!open) more.Visibility = Visibility.Visible;
            var fadeMore = new DoubleAnimation(open ? 0 : 1, time) { EasingFunction = ease };
            if (open) fadeMore.Completed += (_, _) => { if (IsOpen()) more.Visibility = Visibility.Collapsed; };
            more.BeginAnimation(OpacityProperty, fadeMore);
        }
        Lay(_noticeOpenStacks.Contains(key), animate: false);

        SwipeToClear(deck, () => new FrameworkElement[] { wrap }, () => ClearNotices(all, Loc.F("notif.cleared.chat", shown[0].Title), null), active: () => !IsOpen());
        // closed: a click opens it (before the row's own click, which would open the app)
        deck.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || _noticeOpenStacks.Contains(key)) return;
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            Lay(true);
        };
        return wrap;
    }

    // ---- by app: fold a group, clear it with its X or by swiping, and the way back

    private const double NoticeUndoSeconds = 5, NoticeSwipeAway = 90;
    private readonly HashSet<string> _noticeFolded = new();
    private List<Notice>? _noticeCleared;
    private string _noticeClearedText = "";
    private DateTime _noticeUndoUntil;
    private System.Windows.Threading.DispatcherTimer? _noticeUndoTimer;

    private bool NoticesPageOpen => _subOpen && _subTitle.Text == Loc.T("notif.title");

    /// <summary>The app's line over its notifications: icon, name, how many, the fold arrow and the X that clears them.</summary>
    private Border NoticeGroupHead(List<Notice> items, Border body, Action clear)
    {
        string app = items[0].App;
        bool folded = _noticeFolded.Contains(app);
        // the app's icon (a photo on a notification is the sender's, not the app's); its colour only while none is known
        var logo = _notices.IconOf(items[0].AppId) ?? items.FirstOrDefault(n => !n.IsPhoto && n.Avatar != null)?.Avatar;
        var icon = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(logo != null ? 3 : 5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            Background = logo != null ? new ImageBrush(logo) { Stretch = Stretch.Uniform, Viewbox = DesktopAppIcon.DrawnArea(logo) } : items[0].Accent is Color c ? Br(c) : White(0x30) };
        var name = new TextBlock { Text = app, FontSize = 10.5, Foreground = White(0x88), VerticalAlignment = VerticalAlignment.Center };
        var count = new Border { CornerRadius = new CornerRadius(7), Background = White(0x14), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = items.Count.ToString(), FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = White(0xAA) } };
        var turn = new RotateTransform(folded ? -90 : 0);
        var chevron = new TextBlock { Text = "▾", FontSize = 11, Foreground = White(0xAA), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = turn };
        var x = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = White(0x0E), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Loc.F("notif.clear.app", app),
            Child = new TextBlock { Text = Glyph(0xE711), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 8, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        x.MouseEnter += (_, _) => x.Background = White(0x2A);
        x.MouseLeave += (_, _) => x.Background = White(0x0E);
        x.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            clear();
        };
        var line = new DockPanel();
        DockPanel.SetDock(x, Dock.Right);
        line.Children.Add(x);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(icon);
        left.Children.Add(name);
        left.Children.Add(count);
        left.Children.Add(chevron);
        line.Children.Add(left);
        var head = new Border { Child = line, Padding = new Thickness(6, 3, 4, 3), Margin = new Thickness(0, 6, 0, 2), CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        head.MouseEnter += (_, _) => head.Background = White(0x0A);
        head.MouseLeave += (_, _) => head.Background = Brushes.Transparent;
        head.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            FoldNoticeGroup(app, body, turn);
        };
        return head;
    }

    /// <summary>Folds an app's notifications away under its line, or opens them again; the island follows.</summary>
    private void FoldNoticeGroup(string app, Border body, RotateTransform turn)
    {
        bool fold = !_noticeFolded.Remove(app);
        if (fold) _noticeFolded.Add(app);
        var time = TimeSpan.FromMilliseconds(Dur(280));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(fold ? -90 : 0, time) { EasingFunction = ease });
        double from = body.ActualHeight;
        body.Child.Measure(new Size(440, double.PositiveInfinity));
        double full = body.Child.DesiredSize.Height;
        // the island measures the final height first (the animation then starts from where it is), so both move together
        body.BeginAnimation(HeightProperty, null);
        body.Height = fold ? 0 : full;
        ResizeSubPage();
        var grow = new DoubleAnimation(from, fold ? 0 : full, time) { EasingFunction = ease };
        if (!fold) grow.Completed += (_, _) => { if (!_noticeFolded.Contains(app)) { body.BeginAnimation(HeightProperty, null); body.Height = double.NaN; } };
        body.BeginAnimation(HeightProperty, grow);
    }

    /// <summary>
    /// Clears these notifications: their lines slide away one after the other (unless a swipe already took them), then
    /// "Desfazer" stays for a few seconds at the top. A new clear makes the one before final.
    /// </summary>
    private async void ClearNotices(List<Notice> gone, string text, List<FrameworkElement>? slide)
    {
        if (slide is { Count: > 0 })
        {
            double stagger = Dur(40), fade = Dur(200);
            for (int i = 0; i < slide.Count; i++) SlideRowAway(slide[i], TimeSpan.FromMilliseconds(i * stagger), TimeSpan.FromMilliseconds(fade));
            await Task.Delay((int)((slide.Count - 1) * stagger + fade));
        }
        _noticeCleared = gone;
        _noticeClearedText = text;
        _noticeUndoUntil = DateTime.UtcNow.AddSeconds(NoticeUndoSeconds);
        _noticeUndoTimer?.Stop();
        _noticeUndoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(NoticeUndoSeconds) };
        _noticeUndoTimer.Tick += (_, _) => EndNoticeUndo();
        _noticeUndoTimer.Start();
        _notices.Remove(gone); // (the page is drawn again, with the undo line on top)
    }

    private void EndNoticeUndo()
    {
        _noticeUndoTimer?.Stop();
        if (_noticeCleared == null) return;
        _noticeCleared = null;
        if (NoticesPageOpen) RefreshSubPage(NoticesPage);
    }

    /// <summary>"Notificações do WhatsApp limpas", a bar running out, and the way back (like the downloads').</summary>
    private FrameworkElement NoticeUndoRow()
    {
        var grid = new Grid { Height = 40, Margin = new Thickness(4, 0, 2, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = _noticeClearedText, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        var scale = new ScaleTransform(1, 1);
        left.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 6, 14, 0), Background = White(0xCC), RenderTransform = scale });
        // from where it really is: the page is drawn again whenever something arrives
        double secondsLeft = Math.Clamp((_noticeUndoUntil - DateTime.UtcNow).TotalSeconds, 0, NoticeUndoSeconds);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(secondsLeft / NoticeUndoSeconds, 0, TimeSpan.FromSeconds(secondsLeft)));
        grid.Children.Add(left);
        var undo = ListPill("", Loc.T("dl.undo"), false, () =>
        {
            var back = _noticeCleared;
            _noticeCleared = null;
            _noticeUndoTimer?.Stop();
            if (back != null) _notices.Restore(back); // (draws the page again)
        });
        Grid.SetColumn(undo, 1);
        grid.Children.Add(undo);
        return grid;
    }

    /// <summary>
    /// Drag <paramref name="handle"/> sideways: <paramref name="parts"/> follow it, fading; let go far enough and they fly
    /// out and <paramref name="clear"/> runs, otherwise they spring back. A press that didn't move stays a click.
    /// </summary>
    private void SwipeToClear(FrameworkElement handle, Func<IEnumerable<FrameworkElement>> parts, Action clear, Func<bool>? active = null)
    {
        Point? start = null;
        bool swiping = false;
        double dx = 0;

        TranslateTransform Shift(FrameworkElement p)
        {
            if (p.RenderTransform is not TranslateTransform t) p.RenderTransform = t = new TranslateTransform();
            return t;
        }
        void Show(double x)
        {
            foreach (var p in parts())
            {
                var t = Shift(p);
                t.BeginAnimation(TranslateTransform.XProperty, null);
                t.X = x;
                p.BeginAnimation(OpacityProperty, null);
                p.Opacity = 1 - Math.Min(0.75, Math.Abs(x) / 180);
            }
        }
        void Settle(bool away)
        {
            var time = TimeSpan.FromMilliseconds(Dur(away ? 170 : 240));
            double to = away ? Math.Sign(dx) * 460 : 0;
            bool first = true;
            foreach (var p in parts())
            {
                var move = new DoubleAnimation(to, time) { EasingFunction = away ? new CubicEase { EasingMode = EasingMode.EaseIn } : new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } };
                if (away && first) move.Completed += (_, _) => clear();
                first = false;
                Shift(p).BeginAnimation(TranslateTransform.XProperty, move);
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(away ? 0 : 1, time));
            }
        }

        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (active?.Invoke() == false)
            {
                start = null; // (a stack: open, each card swipes alone; closed, the whole stack)
                return;
            }
            start = e.GetPosition(this);
            swiping = false;
            dx = 0;
        };
        handle.MouseMove += (_, e) =>
        {
            if (start is not Point s || e.LeftButton != MouseButtonState.Pressed) return;
            dx = e.GetPosition(this).X - s.X;
            if (!swiping)
            {
                if (Math.Abs(dx) < 8) return; // a click, not a swipe
                swiping = true;
                handle.CaptureMouse();
            }
            Show(dx);
        };
        handle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            start = null;
            if (!swiping) return;
            e.Handled = true; // not a click: nothing opens
            swiping = false;
            handle.ReleaseMouseCapture();
            bool away = Math.Abs(dx) > NoticeSwipeAway;
            if (away) SoundService.Play(Sfx.Click);
            Settle(away);
        };
        handle.LostMouseCapture += (_, _) =>
        {
            if (!swiping) return;
            swiping = false;
            start = null;
            Settle(false);
        };
    }

    /// <summary>
    /// Opens a notification's app. Notchy's window never takes the focus, and Windows won't let a background program
    /// bring another app to the front (Discord would get the request but stay hidden in the tray); so Notchy takes the
    /// focus for a moment and lets the app come forward.
    /// </summary>
    private async void OpenNoticeApp(Notice n)
    {
        string appId = n.AppId;
        if (appId.Length == 0) return;
        if (appId == CalendarAppId)
        {
            OpenCalendar(DateTime.Today);
            return;
        }
        if (_hwnd != IntPtr.Zero)
        {
            Native.SetActivatable(_hwnd, true);
            Native.FocusWindow(_hwnd);
        }
        Native.AllowSetForegroundWindow(-1); // ASFW_ANY: whoever opens next may come to the front
        // like a click on Windows' own pop-up: the app's activator goes to that chat; otherwise just the app
        Collapse();
        // (tray programs have nothing Windows can open: the search below brings their window, if they have one)
        if (!appId.StartsWith(NotificationService.TrayAppPrefix, StringComparison.Ordinal) && !await NotificationOpener.OpenAsync(n)) NotificationService.OpenApp(appId);
        // apps living in the tray (Discord…) show their window but may stay behind: find it and bring it forward
        string app = _notices.History.FirstOrDefault(n => n.AppId == appId)?.App ?? "";
        int tries = 0;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        t.Tick += (_, _) =>
        {
            tries++;
            IntPtr w = app.Length > 0 ? Native.FindAppWindow(app) : IntPtr.Zero;
            if (w != IntPtr.Zero) Native.FocusWindow(w);
            if (w != IntPtr.Zero && Native.IsForeground(w) || tries > 16)
            {
                t.Stop();
                if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
            }
        };
        t.Start();
    }

    private Border NoticeRow(Notice n, FrameworkElement? badge = null)
    {
        var avatar = n.CalendarType is CalType calType ? CalNoticeIcon(calType)
            : new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(n.IsPhoto ? 15 : 8), VerticalAlignment = VerticalAlignment.Center,
                Background = n.Avatar != null ? new ImageBrush(n.Avatar) { Stretch = Stretch.UniformToFill } : White(0x14) };
        var texts = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = n.Title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        if (n.Body.Length > 0) texts.Children.Add(new TextBlock { Text = n.Body, FontSize = 11, Foreground = White(0x99), TextTrimming = TextTrimming.CharacterEllipsis });
        var when = new TextBlock { Text = (DateTime.Now - n.Time).TotalMinutes < 1 ? Loc.T("notif.now") : FormatClock(n.Time), FontSize = 10, Foreground = White(0x66),
            VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        FrameworkElement right = when;
        if (badge != null)
        {
            // on the time's line, before it: the time stays where the other rows have theirs
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(0, 0, 6, 0);
            line.Children.Add(badge);
            line.Children.Add(when);
            right = line;
        }
        Grid.SetColumn(right, 2);
        grid.Children.Add(avatar);
        grid.Children.Add(texts);
        grid.Children.Add(right);
        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Loc.F("notif.open", n.App) };
        SoftHover(row);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenNoticeApp(n);
        };
        return row;
    }

}
