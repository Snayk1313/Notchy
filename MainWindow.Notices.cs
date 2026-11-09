using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Notifications from other apps on the island. When one arrives, the island drops down into a card (like an
/// iPhone banner): the sender's photo (or the app's icon), Notchy's eyes with "WhatsApp · agora", who sent it and
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

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 22, 0) };
        Grid.SetColumn(texts, 1);
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        // Notchy's eyes, small, before the app's name
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 } };
        eyes.Children.Add(new Rectangle { Width = 4, Height = 7, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Margin = new Thickness(0, 0, 3, 0) });
        eyes.Children.Add(new Rectangle { Width = 4, Height = 7, RadiusX = 2, RadiusY = 2, Fill = Brushes.White });
        top.Children.Add(eyes);
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

        // a click on the card opens the app
        _noticePanel.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_noticeShown is { } n) OpenNoticeApp(n);
            EndNotice();
        };
        ((Panel)CompactPanel.Parent).Children.Add(_noticePanel);

        BuildBell();
        _notices.Arrived += OnNotice;
        _notices.HistoryChanged += () => { if (_subOpen && _subTitle.Text == Loc.T("notif.title")) RefreshSubPage(NoticesPage); };
        _notices.Start();
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
        _bellBadge.Text = _noticeUnseen > 9 ? "9+" : _noticeUnseen.ToString();
        ((FrameworkElement)_bellBadge.Parent).Visibility = _noticeUnseen > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        // focusing with "Não perturbe", or a game/video full screen: kept in the history, not shown
        if (SoundService.Hushed || _hiddenForFullscreen) return;
        _noticeQueue.Enqueue(n);
        if (_noticeShown == null) ShowNextNotice();
        else _noticeMore.Text = "+" + _noticeQueue.Count;
        _noticeMore.Visibility = _noticeQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _noticeMore.Text = "+" + _noticeQueue.Count;
        _noticeMore.Visibility = _noticeQueue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SoundService.Play(Sfx.Toggle);
        ShowTransient(CompactMode.Notice, "", "", NoticeSeconds);
    }

    private void EndNotice()
    {
        if (_transient == CompactMode.Notice) _transient = CompactMode.None;
        _noticeShown = null;
        UpdateState();
    }

    /// <summary>Every frame: when a card's time is up, the next one (if any) comes down.</summary>
    private void UpdateNoticesFrame()
    {
        if (_noticeShown == null) return;
        // reading it: it waits while the mouse is on it
        if (_transient == CompactMode.Notice && _noticePanel.IsMouseOver) _transientUntil = DateTime.UtcNow.AddSeconds(1.5);
        if (_transient == CompactMode.Notice && DateTime.UtcNow < _transientUntil) return;
        _noticeShown = null;
        if (_noticeQueue.Count > 0) ShowNextNotice();
    }

    // =====================================================================================
    // the history
    // =====================================================================================

    private FrameworkElement NoticesPage()
    {
        var list = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var all = _notices.History.Where(n => !S.NotifyMuted.Contains(n.App)).ToList();
        list.Children.Add(ListTop(all.Count == 0 ? Loc.T("notif.empty") : (all.Count == 1 ? Loc.T("notif.count1") : Loc.F("notif.count", all.Count)),
            all.Count == 0 ? null : ListPill("", Loc.T("notif.clear"), false, () =>
            {
                _notices.Clear();
                RefreshSubPage(NoticesPage);
            })));
        // grouped by app, the app with the newest one first
        foreach (var group in all.GroupBy(n => n.App))
        {
            list.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = White(0x70),
                Margin = new Thickness(6, 6, 0, 4) });
            foreach (var n in group.Take(6)) list.Children.Add(NoticeRow(n));
        }
        return Capped(list, MaxSubHeight - 10);
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
        if (_hwnd != IntPtr.Zero)
        {
            Native.SetActivatable(_hwnd, true);
            Native.FocusWindow(_hwnd);
        }
        Native.AllowSetForegroundWindow(-1); // ASFW_ANY: whoever opens next may come to the front
        // like a click on Windows' own pop-up: the app's activator goes to that chat; otherwise just the app
        Collapse();
        if (!await NotificationOpener.OpenAsync(n)) NotificationService.OpenApp(appId);
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

    private Border NoticeRow(Notice n)
    {
        var avatar = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(n.IsPhoto ? 15 : 8), VerticalAlignment = VerticalAlignment.Center,
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
        Grid.SetColumn(when, 2);
        grid.Children.Add(avatar);
        grid.Children.Add(texts);
        grid.Children.Add(when);
        var row = new Border { Child = grid, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Loc.F("notif.open", n.App) };
        row.MouseEnter += (_, _) => row.Background = White(0x0E);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SoundService.Play(Sfx.Click);
            OpenNoticeApp(n);
        };
        return row;
    }
}
