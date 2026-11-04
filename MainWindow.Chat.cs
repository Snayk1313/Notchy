using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// Notchy Chat tab: drag the eyes onto something (or circle it on screen) and ask Claude about it.
/// Answers come from the user's own Claude Code ("claude -p"), streamed live into the island.
/// </summary>
public partial class MainWindow
{
    private static readonly Color ChatColor = Color.FromRgb(0xFF, 0xAA, 0x4C);
    private static string G(int code) => ((char)code).ToString();

    private enum ChatView { Idle, Answer }

    private ClaudeRunner? _runner;
    private ChatView _chatView = ChatView.Idle;
    private CapturedText? _ctxText;
    private string? _ctxImage;
    private bool _ctxChangedSinceAnswer;
    private string _answer = "";
    private string _lastAction = "";
    private bool _hasConversation;
    private bool _draggingEyes;

    // UI
    private StackPanel _idleView = null!;
    private Grid _answerView = null!;
    private Border _ctxCard = null!, _ctxThumb = null!;
    private TextBlock _ctxLabel = null!, _ctxPreview = null!, _ctxIcon = null!;
    private TextBlock _chatTitle = null!, _chatSub = null!;
    private TextBox _chatInput = null!;
    private TextBlock _chatPlaceholder = null!;
    private TextBlock _answerLabel = null!, _answerText = null!, _thinking = null!;
    private ScrollViewer _answerScroll = null!;
    private Button _copyButton = null!, _replaceButton = null!;
    private FrameworkElement _bigEyes = null!;
    private readonly ScaleTransform _bigEyesScale = new(1, 1);
    private Window? _ghost;
    private readonly Dictionary<string, TextBlock> _chipLabels = new();

    private bool HasContext => _ctxText != null || _ctxImage != null;
    private double ChatContentHeight => _chatView == ChatView.Answer ? 292 : HasContext ? 262 : 214;
    private bool ChatKeepsOpen => _chatInput != null && (_chatInput.IsKeyboardFocused || _draggingEyes || _chatPinned);

    // =====================================================================================
    // build
    // =====================================================================================

    private void InitChat()
    {
        _runner = new ClaudeRunner(Dispatcher);
        _runner.TextChanged += t =>
        {
            _answer = t;
            RenderAnswer(t, streaming: true);
        };
        _runner.Finished += OnChatFinished;

        ChatTab.RowDefinitions.Add(new RowDefinition());
        ChatTab.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        BuildIdleView();
        BuildAnswerView();
        var input = BuildInput();
        Grid.SetRow(input, 1);
        ChatTab.Children.Add(_idleView);
        ChatTab.Children.Add(_answerView);
        ChatTab.Children.Add(input);

        Deactivated += (_, _) =>
        {
            // typing is over: go back to never stealing focus
            if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
            _clickOpened = CursorOverIsland();
            _hoverRequested = _clickOpened;
            _hoverTimer.Stop();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(300);
            _hoverTimer.Start();
        };

        ApplyChatTexts();
        UpdateChatView(animate: false);
    }

    private void BuildIdleView()
    {
        _idleView = new StackPanel { VerticalAlignment = VerticalAlignment.Top };

        // big draggable eyes
        var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < 2; i++)
            eyes.Children.Add(new Ellipse
            {
                Width = 22,
                Height = 14,
                Margin = new Thickness(7, 0, 7, 0),
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x3D)),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _blink
            });
        eyes.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9, Color = Color.FromRgb(0xFF, 0x96, 0x2E) };
        var eyesHost = new Border
        {
            Width = 110,
            Height = 42,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = eyes,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _bigEyesScale,
            Margin = new Thickness(0, 4, 0, 0)
        };
        eyesHost.MouseEnter += (_, _) => AnimateScale(_bigEyesScale, 1.12);
        eyesHost.MouseLeave += (_, _) => { if (!_draggingEyes) AnimateScale(_bigEyesScale, 1); };
        eyesHost.MouseLeftButtonDown += EyesDragStart;
        eyesHost.MouseMove += EyesDragMove;
        eyesHost.MouseLeftButtonUp += EyesDragEnd;
        eyesHost.LostMouseCapture += (_, _) => { if (_draggingEyes) EndDragCancelled(); };
        _bigEyes = eyesHost;
        _idleView.Children.Add(eyesHost);

        _chatTitle = new TextBlock { FontSize = 15.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        _chatSub = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _idleView.Children.Add(_chatTitle);
        _idleView.Children.Add(_chatSub);

        // captured context card
        _ctxThumb = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(7), Visibility = Visibility.Collapsed };
        _ctxIcon = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = new SolidColorBrush(ChatColor), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        _ctxLabel = new TextBlock { FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis };
        _ctxPreview = new TextBlock { FontSize = 12, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        texts.Children.Add(_ctxLabel);
        texts.Children.Add(_ctxPreview);
        var clear = new Button { Style = (Style)FindResource("IconButton"), Content = G(0xE711), FontSize = 10, Padding = new Thickness(7), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (_, _) => ClearContext();
        var cardGrid = new Grid();
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var lead = new Grid();
        lead.Children.Add(_ctxThumb);
        lead.Children.Add(_ctxIcon);
        cardGrid.Children.Add(lead);
        Grid.SetColumn(texts, 1);
        cardGrid.Children.Add(texts);
        Grid.SetColumn(clear, 2);
        cardGrid.Children.Add(clear);
        _ctxCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, ChatColor.R, ChatColor.G, ChatColor.B)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 5, 4, 5),
            Margin = new Thickness(0, 9, 0, 0),
            Child = cardGrid,
            Visibility = Visibility.Collapsed
        };
        _idleView.Children.Add(_ctxCard);

        // quick actions
        var chips = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        chips.Children.Add(Chip("explain", 0xE82F, Color.FromRgb(0x6C, 0xC6, 0xFF)));
        chips.Children.Add(Chip("spelling", 0xE8FB, Color.FromRgb(0x5E, 0xD6, 0x7D)));
        chips.Children.Add(Chip("translate", 0xE774, Color.FromRgb(0x4F, 0xD8, 0xE8)));
        chips.Children.Add(Chip("summarize", 0xE8FD, Color.FromRgb(0xC4, 0x9B, 0xFF)));
        chips.Children.Add(Chip("lens", 0xE8B3, ChatColor));
        _idleView.Children.Add(chips);
    }

    private Button Chip(string action, int glyph, Color color)
    {
        var icon = new TextBlock { Text = G(glyph), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };
        icon.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 8, Opacity = 0.6, Color = color };
        var label = new TextBlock { FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _chipLabels[action] = label;
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(icon);
        content.Children.Add(label);
        var b = new Button { Style = (Style)FindResource("PillButton"), Content = content, Padding = new Thickness(11, 6, 12, 6), Margin = new Thickness(3) };
        // Grab the selection the moment the button goes down, before anything can move the focus.
        if (action != "lens") b.PreviewMouseLeftButtonDown += (_, _) => BeginSelectionCapture(action);
        b.Click += (_, _) =>
        {
            if (action == "lens") _ = LensCaptureAsync();
            else RunChatAction(action, "");
        };
        return b;
    }

    private void BuildAnswerView()
    {
        _answerView = new Grid { Visibility = Visibility.Collapsed };
        _answerView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _answerView.RowDefinitions.Add(new RowDefinition());
        _answerView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        _answerLabel = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 70, 0) };
        var newChat = new Button { Style = (Style)FindResource("IconButton"), Content = G(0xE711), FontSize = 10, Padding = new Thickness(7), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        newChat.Click += (_, _) => NewChat();
        header.Children.Add(_answerLabel);
        header.Children.Add(newChat);
        _answerView.Children.Add(header);

        _thinking = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(ChatColor), FontWeight = FontWeights.SemiBold };
        _thinking.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        _answerText = new TextBlock { FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
        var answerStack = new StackPanel();
        answerStack.Children.Add(_thinking);
        answerStack.Children.Add(_answerText);
        _answerScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = answerStack, Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(_answerScroll, 1);
        _answerView.Children.Add(_answerScroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _copyButton = SmallPill(0xE8C8, "chat.copy", async () =>
        {
            try { Clipboard.SetText(_answer); } catch { }
            FlashLabel(Loc.T("chat.copied"));
            Happy();
            await BackToChatHomeAsync();
        });
        _replaceButton = SmallPill(0xE8AB, "chat.replace", async () =>
        {
            if (_ctxText == null) return;
            await ScreenContext.ReplaceSelectionAsync(_ctxText, _answer);
            FlashLabel(Loc.T("chat.replaced"));
            Happy();
            await BackToChatHomeAsync();
        });
        _copyButton.Visibility = Visibility.Collapsed;
        _replaceButton.Visibility = Visibility.Collapsed;
        buttons.Children.Add(_copyButton);
        buttons.Children.Add(_replaceButton);
        Grid.SetRow(buttons, 2);
        _answerView.Children.Add(buttons);
    }

    private Button SmallPill(int glyph, string key, Action action)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = G(glyph), FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { FontFamily = (FontFamily)FindResource("UiFont"), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Tag = key };
        content.Children.Add(label);
        var b = new Button { Style = (Style)FindResource("PillButton"), Content = content, Padding = new Thickness(10, 4, 11, 4), Margin = new Thickness(0, 0, 6, 0), Tag = label };
        b.Click += (_, _) => action();
        return b;
    }

    private UIElement BuildInput()
    {
        _chatInput = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            SelectionBrush = new SolidColorBrush(ChatColor),
            FontSize = 13,
            FontFamily = (FontFamily)FindResource("UiFont"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(12, 0, 6, 0),
            FocusVisualStyle = null
        };
        _chatPlaceholder = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0x77, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0), IsHitTestVisible = false };
        _chatInput.TextChanged += (_, _) => _chatPlaceholder.Visibility = _chatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _chatInput.PreviewMouseLeftButtonDown += (_, _) => FocusChatInput();
        _chatInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendTyped();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Keyboard.ClearFocus();
                if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, false);
            }
        };

        var send = new Button { Style = (Style)FindResource("CircleButton"), Width = 30, Height = 30, FontSize = 12, Content = G(0xE74A), FontFamily = (FontFamily)FindResource("IconFont"), Margin = new Thickness(0, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        send.Click += (_, _) => SendTyped();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_chatInput);
        grid.Children.Add(_chatPlaceholder);
        Grid.SetColumn(send, 1);
        grid.Children.Add(send);

        return new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Child = grid
        };
    }

    private void ApplyChatTexts()
    {
        if (_chatTitle == null) return;
        _chatTitle.Text = Loc.T("chat.title");
        if (!_chatSubFlashing) _chatSub.Text = Loc.T("chat.sub");
        _chatPlaceholder.Text = Loc.T("chat.placeholder");
        foreach (var (key, label) in _chipLabels) label.Text = Loc.T("chat.a." + key);
        foreach (var b in new[] { _copyButton, _replaceButton })
            if (b.Tag is TextBlock tb && tb.Tag is string k) tb.Text = Loc.T(k);
        UpdateContextCard();
    }

    // =====================================================================================
    // view state
    // =====================================================================================

    private void UpdateChatView(bool animate)
    {
        var show = _chatView == ChatView.Answer ? (UIElement)_answerView : _idleView;
        var hide = _chatView == ChatView.Answer ? (UIElement)_idleView : _answerView;
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        ChatTab.Height = ChatContentHeight - 0;
        if (animate) SlideIn((FrameworkElement)show);
        if (_state == IslandState.Expanded && _tab == Tab.Chat)
            AnimateIslandSize(TargetSize(IslandState.Expanded), bigMove: false);
    }

    private void SetChatView(ChatView view)
    {
        if (_chatView == view)
        {
            UpdateChatView(animate: false);
            return;
        }
        _chatView = view;
        UpdateChatView(animate: true);
    }

    private bool _chatSubFlashing;

    private async void FlashSub(string text)
    {
        _chatSubFlashing = true;
        _chatSub.Text = text;
        _chatSub.Foreground = new SolidColorBrush(ChatColor);
        SlideIn(_chatSub);
        await Task.Delay(2600);
        _chatSubFlashing = false;
        _chatSub.Text = Loc.T("chat.sub");
        _chatSub.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
    }

    private async void FlashLabel(string text)
    {
        string old = _answerLabel.Text;
        _answerLabel.Text = text;
        _answerLabel.Foreground = new SolidColorBrush(ChatColor);
        await Task.Delay(1600);
        _answerLabel.Text = old;
        _answerLabel.Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
    }

    private static void AnimateScale(ScaleTransform s, double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // =====================================================================================
    // context
    // =====================================================================================

    private void SetContext(CapturedText text)
    {
        _ctxText = text;
        _ctxImage = null;
        _ctxChangedSinceAnswer = true;
        UpdateContextCard();
        SetChatView(ChatView.Idle);
    }

    private void SetContextImage(string file)
    {
        _ctxImage = file;
        _ctxText = null;
        _ctxChangedSinceAnswer = true;
        UpdateContextCard();
        SetChatView(ChatView.Idle);
    }

    private void ClearContext()
    {
        _ctxText = null;
        _ctxImage = null;
        UpdateContextCard();
        UpdateChatView(animate: false);
    }

    private void UpdateContextCard()
    {
        if (_ctxCard == null) return;
        bool has = HasContext;
        _ctxCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (!has) return;

        if (_ctxImage != null)
        {
            _ctxIcon.Visibility = Visibility.Collapsed;
            _ctxThumb.Visibility = Visibility.Visible;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 80;
                bmp.UriSource = new Uri(_ctxImage);
                bmp.EndInit();
                _ctxThumb.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch
            {
                _ctxThumb.Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255));
            }
            _ctxLabel.Text = Loc.T("chat.ctx.image");
            _ctxPreview.Text = Loc.T("chat.ctx.imagehint");
        }
        else if (_ctxText != null)
        {
            _ctxThumb.Visibility = Visibility.Collapsed;
            _ctxIcon.Visibility = Visibility.Visible;
            _ctxIcon.Text = G(_ctxText.FromSelection ? 0xE8E9 : 0xE8A5);
            string from = string.IsNullOrWhiteSpace(_ctxText.App) ? "" : " · " + _ctxText.App;
            _ctxLabel.Text = (_ctxText.FromSelection ? Loc.T("chat.ctx.selection") : Loc.T("chat.ctx.text")) + from;
            _ctxPreview.Text = Regex.Replace(_ctxText.Text, @"\s+", " ");
        }
        SlideIn(_ctxCard);
    }

    /// <summary>The last window you were using before touching the island (the one with your selection).</summary>
    private IntPtr _lastForeign;

    private void RememberForeground()
    {
        var fg = ScreenContext.ForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd) _lastForeign = ScreenContext.RootWindowAtWindow(fg);
    }

    /// <summary>
    /// No context yet? Use the text you have selected in the app you were using (UI Automation, then a
    /// careful Ctrl+C), and only then what's on the clipboard.
    /// </summary>
    private Task<CapturedText?>? _pendingSelection;

    /// <summary>Starts reading the selection of the app you were using (UI Automation, then a careful Ctrl+C).</summary>
    private void BeginSelectionCapture(string action)
    {
        var fg = ScreenContext.ForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd) _lastForeign = ScreenContext.RootWindowAtWindow(fg);
        bool typing = _chatInput.IsKeyboardFocused;
        if (!typing && _hwnd != IntPtr.Zero && !Native.IsNoActivate(_hwnd)) Native.SetActivatable(_hwnd, false);
        ChatLog($"[{action}] mouse-down: foreground={(fg == _hwnd ? "NOTCHY" : ScreenContext.AppName(fg))} source={ScreenContext.AppName(_lastForeign)} " +
                $"noActivate={(_hwnd != IntPtr.Zero && Native.IsNoActivate(_hwnd))} typing={typing} ctxImage={_ctxImage != null}");
        if (_ctxImage != null || _lastForeign == IntPtr.Zero)
        {
            _pendingSelection = null;
            return;
        }
        _pendingSelection = CaptureSelectionAsync(_lastForeign, allowCopy: !(_ctxText is { Editable: true } c && c.Window == _lastForeign));
    }

    private async Task<CapturedText?> CaptureSelectionAsync(IntPtr window, bool allowCopy = true)
    {
        var viaUia = await ScreenContext.FocusedSelectionAsync(window);
        ChatLog($"   uia selection: {(viaUia == null ? "none" : $"\"{Short(viaUia.Text)}\"")}");
        if (viaUia != null) return viaUia;
        if (!allowCopy)
        {
            ChatLog("   ctrl+c skipped (editable box already captured)");
            return null;
        }
        var viaCopy = await ScreenContext.SelectionViaCopyAsync(window);
        ChatLog($"   ctrl+c selection: {(viaCopy == null ? (ScreenContext.IsTerminal(window) ? "skipped (terminal)" : "none") : $"\"{Short(viaCopy.Text)}\"")}");
        return viaCopy;
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s.Replace("\n", " ");

    private static readonly string ChatLogFile = System.IO.Path.Combine(AppSettings.Folder, "chat-debug.log");

    /// <summary>Small rolling log of how Notchy Chat found its text (to diagnose app-specific problems).</summary>
    private static void ChatLog(string line)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Folder);
            var fi = new System.IO.FileInfo(ChatLogFile);
            if (fi.Exists && fi.Length > 200_000) fi.Delete();
            System.IO.File.AppendAllText(ChatLogFile, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private async Task<bool> EnsureContextForActionAsync()
    {
        // What you have selected right now wins over an older capture (except a screen snip).
        // A selection we already captured from the app is kept if nothing newer is found.
        var pending = _pendingSelection;
        _pendingSelection = null;
        if (pending == null && _ctxImage == null && _lastForeign != IntPtr.Zero)
            pending = CaptureSelectionAsync(_lastForeign, allowCopy: !(_ctxText is { Editable: true } c && c.Window == _lastForeign));
        if (pending != null)
        {
            var fresh = await pending;
            if (fresh != null)
            {
                if (_ctxText == null || !_ctxText.FromSelection || _ctxText.Text != fresh.Text || _ctxText.Window != fresh.Window)
                    SetContext(fresh);
                ChatLog($"   using fresh selection from {fresh.App}");
                return true;
            }
            if (_ctxText is { FromSelection: true } || _ctxText is { Editable: true })
            {
                ChatLog($"   keeping earlier {(_ctxText.FromSelection ? "selection" : "text box")} from {_ctxText.App}");
                return true;
            }
        }
        ChatLog($"   no selection; existing context={(HasContext ? (_ctxImage != null ? "image" : $"text from {_ctxText?.App}, fromSelection={_ctxText?.FromSelection}") : "none")}");
        if (HasContext) return true;
        if (_lastForeign != IntPtr.Zero)
        {
            var sel = await ScreenContext.FocusedSelectionAsync(_lastForeign)
                      ?? await ScreenContext.SelectionViaCopyAsync(_lastForeign);
            if (sel != null)
            {
                SetContext(sel);
                return true;
            }
        }
        try
        {
            if (Clipboard.ContainsText())
            {
                string t = Clipboard.GetText().Trim();
                if (t.Length > 1)
                {
                    SetContext(new CapturedText(t.Length > 8000 ? t[..8000] : t, Loc.T("chat.ctx.clipboard"), IntPtr.Zero, false));
                    return true;
                }
            }
        }
        catch
        {
            // clipboard busy
        }
        FlashSub(Loc.T("chat.needctx"));
        return false;
    }

    // =====================================================================================
    // drag the eyes onto something
    // =====================================================================================

    private void EyesDragStart(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _draggingEyes = true;
        _bigEyes.CaptureMouse();
        FaceReact(Mood.Wide, 1.2);
        AnimateScale(_bigEyesScale, 0.8);
        ShowGhost();
        SoundService.Play(Sfx.Toggle);
    }

    private void EyesDragMove(object sender, MouseEventArgs e)
    {
        if (_draggingEyes) MoveGhost();
    }

    private async void EyesDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingEyes) return;
        _draggingEyes = false;
        _bigEyes.ReleaseMouseCapture();
        HideGhost();
        AnimateScale(_bigEyesScale, 1);

        Native.GetCursorPos(out var p);
        if (CursorOverIsland()) return; // dropped back on the island
        await CaptureAtAsync(p.X, p.Y);
    }

    private void EndDragCancelled()
    {
        _draggingEyes = false;
        HideGhost();
        AnimateScale(_bigEyesScale, 1);
    }

    private void ShowGhost()
    {
        if (_ghost == null)
        {
            var eyes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 2; i++)
                eyes.Children.Add(new Ellipse { Width = 13, Height = 9, Margin = new Thickness(3, 0, 3, 0), Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x3D)) });
            eyes.Effect = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 10, Opacity = 0.9, Color = Color.FromRgb(0xFF, 0x96, 0x2E) };
            _ghost = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Width = 64,
                Height = 34,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(17),
                    Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x0C, 0x0C, 0x0E)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, ChatColor.R, ChatColor.G, ChatColor.B)),
                    BorderThickness = new Thickness(1),
                    Child = eyes
                }
            };
            _ghost.SourceInitialized += (_, _) => Native.MakeClickThrough(new System.Windows.Interop.WindowInteropHelper(_ghost).Handle);
        }
        MoveGhost();
        _ghost.Show();
    }

    private void MoveGhost()
    {
        if (_ghost == null) return;
        Native.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        _ghost.Left = p.X / dpi.DpiScaleX - _ghost.Width / 2;
        _ghost.Top = p.Y / dpi.DpiScaleY - _ghost.Height - 6;
    }

    private void HideGhost() => _ghost?.Hide();

    /// <summary>Text under the point if there is any; otherwise let the user circle what they want.</summary>
    private async Task CaptureAtAsync(int x, int y)
    {
        _chatSubFlashing = true;
        _chatSub.Text = Loc.T("chat.reading");
        IntPtr dropWindow = ScreenContext.RootWindowAt(x, y);
        var text = await ScreenContext.TextAtAsync(x, y);

        // Apps like Discord / WhatsApp don't report their selection to UI Automation. If you dropped the eyes
        // on the app you were using, copy its selection (carefully) and prefer it when it belongs to that spot.
        if ((text == null || !(text.FromSelection || text.Editable)) && dropWindow != IntPtr.Zero &&
            (dropWindow == _lastForeign || dropWindow == ScreenContext.RootWindowAtWindow(ScreenContext.ForegroundWindow())))
        {
            // You selected something in the app you dropped the eyes on: that selection is what you meant.
            var copied = await ScreenContext.SelectionViaCopyAsync(dropWindow);
            if (copied != null) text = copied;
        }
        ChatLog($"[drop] on {ScreenContext.AppName(dropWindow)} (source={ScreenContext.AppName(_lastForeign)}): " +
                $"{(text == null ? "no text" : $"\"{Short(text.Text)}\" fromSelection={text.FromSelection} editable={text.Editable} ocr={text.Ocr}")}");
        _chatSubFlashing = false;
        _chatSub.Text = Loc.T("chat.sub");
        if (text != null)
        {
            SoundService.Play(Sfx.Expand);
            SetContext(text);
            // don't take the keyboard focus: the app keeps its selection, so Replace can paste over it
            OpenChatForTyping(focusInput: false);
            return;
        }
        await LensCaptureAsync();
    }

    private async Task LensCaptureAsync()
    {
        _clickOpened = false;
        _hoverActive = false;
        UpdateState();
        await Task.Delay(Math.Max(120, (int)Dur(260))); // let the island get out of the way
        string? file = await LensWindow.SelectAsync(ClaudeRunner.WorkDir);
        if (file == null) return;
        SoundService.Play(Sfx.Expand);
        SetContextImage(file);
        OpenChatForTyping();
    }

    /// <summary>Opens the island on the chat tab, ready to type or pick an action.</summary>
    private void OpenChatForTyping(bool focusInput = true)
    {
        _userTab = Tab.Chat;
        if (_tab != Tab.Chat) SelectTab(Tab.Chat, animate: _state == IslandState.Expanded);
        // The mouse is somewhere else on the screen right now: stay open until you come back to the island.
        _chatPinned = true;
        _clickOpened = true;
        _hoverRequested = false;
        UpdateState();
        if (focusInput) FocusChatInput();
    }

    /// <summary>Open after a capture (eyes / screen snip) until the mouse comes back to the island.</summary>
    private bool _chatPinned;

    /// <summary>Called when the mouse enters the island: from now on it opens/closes with the mouse again.</summary>
    private void UnpinChat() => _chatPinned = false;

    /// <summary>After Copy / Replace: a moment to see it worked, then the chat's start screen for the next one.</summary>
    private async Task BackToChatHomeAsync()
    {
        await Task.Delay(1100);
        _runner?.Cancel();
        _hasConversation = false;
        _answer = "";
        _ctxText = null;
        _ctxImage = null;
        UpdateContextCard();
        SetChatView(ChatView.Idle);
    }

    private void FocusChatInput()
    {
        if (_hwnd != IntPtr.Zero) Native.SetActivatable(_hwnd, true);
        Activate();
        _chatInput.Focus();
        Keyboard.Focus(_chatInput);
    }

    // =====================================================================================
    // asking Claude
    // =====================================================================================

    private void SendTyped()
    {
        string q = _chatInput.Text.Trim();
        if (q.Length == 0) return;
        _chatInput.Clear();
        RunChatAction("ask", q);
    }

    private string TranslateTarget => S.ChatTranslateTo switch
    {
        "en" => "English",
        "es" => "español",
        _ => "português do Brasil"
    };

    private string SystemPrompt => Loc.F("chat.system", S.Language == "en" ? "English" : "português do Brasil");

    private async void RunChatAction(string action, string question)
    {
        if (_runner == null) return;
        if (action != "ask" && !await EnsureContextForActionAsync()) return;

        string instruction = action switch
        {
            "explain" => Loc.T("chat.p.explain"),
            "spelling" => Loc.T("chat.p.spelling"),
            "translate" => Loc.F("chat.p.translate", TranslateTarget),
            "summarize" => Loc.T("chat.p.summarize"),
            _ => question
        };

        var prompt = new System.Text.StringBuilder();
        bool includeContext = HasContext && (action != "ask" || _ctxChangedSinceAnswer || !_hasConversation);
        if (includeContext && _ctxImage != null)
        {
            // Image: open it first, then apply the action to what is IN the image
            // (otherwise "translate the content below" translates the instruction itself).
            string task = action switch
            {
                "explain" => Loc.T("chat.pi.explain"),
                "spelling" => Loc.T("chat.pi.spelling"),
                "translate" => Loc.F("chat.pi.translate", TranslateTarget),
                "summarize" => Loc.T("chat.pi.summarize"),
                _ => Loc.F("chat.pi.ask", question)
            };
            prompt.Append(Loc.F("chat.pi.open", System.IO.Path.GetFileName(_ctxImage))).Append("\n\n").Append(task);
        }
        else
        {
            prompt.Append(instruction);
        }
        if (includeContext && _ctxImage == null)
        {
            if (_ctxText != null)
            {
                string from = string.IsNullOrWhiteSpace(_ctxText.App) ? "" : $" ({_ctxText.App})";
                prompt.Append("\n\n").Append(Loc.T("chat.p.content")).Append(from).Append(":\n<<<\n").Append(_ctxText.Text).Append("\n>>>");
            }
        }

        bool followUp = action == "ask" && _hasConversation && !includeContext;
        _ctxChangedSinceAnswer = false;
        _lastAction = action;

        string actionLabel = action == "ask" ? question : Loc.T("chat.a." + action);
        string source = _ctxImage != null ? Loc.T("chat.ctx.image") : _ctxText?.App ?? "";
        _answerLabel.Text = string.IsNullOrWhiteSpace(source) || action == "ask" ? actionLabel : $"{actionLabel} · {source}";
        _answer = "";
        _answerText.Inlines.Clear();
        _thinking.Text = Loc.T("chat.thinking");
        _thinking.Visibility = Visibility.Visible;
        _copyButton.Visibility = Visibility.Collapsed;
        _replaceButton.Visibility = Visibility.Collapsed;
        SetChatView(ChatView.Answer);

        _runner.Run(prompt.ToString(), SystemPrompt, followUp);
    }

    private void OnChatFinished(string text, bool isError)
    {
        _answer = isError ? "" : text;
        _thinking.Visibility = Visibility.Collapsed;
        FaceReact(isError ? Mood.Sad : Mood.Happy, isError ? 2.5 : 1.4);
        if (!isError && _lastAction == "spelling" && _ctxText != null)
            RenderCorrection(_ctxText.Text, text);
        else
            RenderAnswer(text, streaming: false, error: isError);
        _hasConversation = !isError;
        _copyButton.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
        _replaceButton.Visibility = !isError && _lastAction == "spelling" && _ctxText != null &&
                                    (_ctxText.FromSelection || _ctxText.Editable) && _ctxText.Window != IntPtr.Zero
            ? Visibility.Visible : Visibility.Collapsed;
        if (_lastAction == "spelling")
            ChatLog($"   finished: error={isError} replace={_replaceButton.Visibility} (fromSelection={_ctxText?.FromSelection}, editable={_ctxText?.Editable}, sourceWindow={(_ctxText?.Window ?? IntPtr.Zero) != IntPtr.Zero})");

        if (_state != IslandState.Expanded || _tab != Tab.Chat)
        {
            SoundService.Play(Sfx.Done);
            ShowTransient(CompactMode.ClaudeDone, Loc.T("chat.ready"), "", 4);
        }
    }

    private void NewChat()
    {
        _runner?.Cancel();
        _hasConversation = false;
        _answer = "";
        ClearContext();
        SetChatView(ChatView.Idle);
    }

    /// <summary>Debug (--replacetest dir): spelling on the foreground app's selection, then Replace.</summary>
    public async Task ReplaceSelfTestAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var log = new System.Text.StringBuilder();
        await Task.Delay(1500);
        _lastForeign = ScreenContext.RootWindowAtWindow(ScreenContext.ForegroundWindow());
        log.AppendLine($"source={ScreenContext.AppName(_lastForeign)}");
        _forcedState = IslandState.Expanded;
        UpdateState();
        SelectTab(Tab.Chat, false);
        if (Environment.GetEnvironmentVariable("NOTCHY_DRAGXY") is string xy && xy.Contains(','))
        {
            // same as dropping the eyes at that point
            var parts = xy.Split(',');
            await CaptureAtAsync(int.Parse(parts[0]), int.Parse(parts[1]));
            log.AppendLine($"afterDrop ctx={_ctxText?.Text} fromSel={_ctxText?.FromSelection} fgIsSource={ScreenContext.RootWindowAtWindow(ScreenContext.ForegroundWindow()) == _lastForeign}");
        }
        RunChatAction("spelling", "");
        await Task.Delay(1200);
        for (int i = 0; i < 80 && _runner!.Busy; i++) await Task.Delay(500);
        log.AppendLine($"ctx={_ctxText?.Text} fromSel={_ctxText?.FromSelection} app={_ctxText?.App}");
        log.AppendLine($"answer={_answer}");
        log.AppendLine($"label={_answerLabel.Text}");
        log.AppendLine($"replaceVisible={_replaceButton.Visibility}");
        await Task.Delay(600);
        var root = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x30, 0x24, 0x48)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        rtb.Render(dv);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, "replace_answer.png"))) enc.Save(fs);
        if (_replaceButton.Visibility == Visibility.Visible && _ctxText != null)
        {
            await ScreenContext.ReplaceSelectionAsync(_ctxText, _answer);
            log.AppendLine("replaced");
        }
        await Task.Delay(800);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "replace_log.txt"), log.ToString());
        Application.Current.Shutdown();
    }

    // =====================================================================================
    // spelling: corrected text with the changed words underlined (like the Samsung keyboard)
    // =====================================================================================

    private static readonly Color ChangeColor = Color.FromRgb(0x4D, 0x9F, 0xFF);

    private void RenderCorrection(string original, string corrected)
    {
        _answerText.Inlines.Clear();
        _answerText.Foreground = Brushes.White;

        var tokens = Regex.Matches(corrected, @"\s+|[^\s]+").Select(m => m.Value).ToList();
        var newWords = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var oldWords = Regex.Matches(original, @"[^\s]+").Select(m => m.Value).ToList();
        var same = MatchedWords(oldWords, newWords);

        var underline = new TextDecorationCollection
        {
            new TextDecoration(TextDecorationLocation.Underline, new Pen(new SolidColorBrush(ChangeColor), 2), 1.5,
                TextDecorationUnit.FontRecommended, TextDecorationUnit.Pixel)
        };

        int word = 0, changes = 0;
        foreach (var t in tokens)
        {
            if (string.IsNullOrWhiteSpace(t))
            {
                if (t.Contains('\n'))
                    foreach (var _ in t.Where(c => c == '\n')) _answerText.Inlines.Add(new LineBreak());
                else
                    _answerText.Inlines.Add(new Run(t));
                continue;
            }
            bool changed = !same[word++];
            if (changed) changes++;
            var run = new Run(t);
            if (changed) run.TextDecorations = underline;
            _answerText.Inlines.Add(run);
        }

        _answerLabel.Text = changes == 0
            ? Loc.T("chat.nochanges")
            : Loc.F(changes == 1 ? "chat.changes1" : "chat.changes", changes) + (_ctxText?.App is { Length: > 0 } app ? " · " + app : "");
    }

    /// <summary>For each new word: true if it is part of the longest common word sequence with the original.</summary>
    private static bool[] MatchedWords(List<string> a, List<string> b)
    {
        var result = new bool[b.Count];
        if (a.Count == 0 || b.Count == 0) return result;
        if ((long)a.Count * b.Count > 4_000_000)
        {
            var set = new HashSet<string>(a);
            for (int j = 0; j < b.Count; j++) result[j] = set.Contains(b[j]);
            return result;
        }
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (a[x] == b[y])
            {
                result[y] = true;
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) x++;
            else y++;
        }
        return result;
    }

    // =====================================================================================
    // tiny markdown renderer (bold, `code`, bullets, headings)
    // =====================================================================================

    private void RenderAnswer(string md, bool streaming, bool error = false)
    {
        if (md.Length > 0) _thinking.Visibility = Visibility.Collapsed;
        _answerText.Inlines.Clear();
        _answerText.Foreground = error ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9A, 0x8A)) : Brushes.White;
        var mono = new FontFamily("Cascadia Mono, Consolas");
        var lines = md.Replace("\r", "").Split('\n');
        bool inCode = false;
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            if (line.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (li > 0) _answerText.Inlines.Add(new LineBreak());
            if (inCode)
            {
                _answerText.Inlines.Add(new Run(line) { FontFamily = mono, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC6, 0x9A)) });
                continue;
            }

            string trimmed = line.TrimStart();
            bool heading = trimmed.StartsWith("#");
            if (heading) trimmed = trimmed.TrimStart('#').TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                _answerText.Inlines.Add(new Run("  •  ") { Foreground = new SolidColorBrush(ChatColor) });
                trimmed = trimmed[2..];
            }

            foreach (Match m in Regex.Matches(trimmed, @"(\*\*[^*]+\*\*|`[^`]+`|[^*`]+|[*`])"))
            {
                string v = m.Value;
                if (v.StartsWith("**") && v.EndsWith("**") && v.Length > 4)
                    _answerText.Inlines.Add(new Run(v[2..^2]) { FontWeight = FontWeights.Bold });
                else if (v.StartsWith("`") && v.EndsWith("`") && v.Length > 2)
                    _answerText.Inlines.Add(new Run(v[1..^1]) { FontFamily = mono, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC6, 0x9A)) });
                else
                    _answerText.Inlines.Add(new Run(v) { FontWeight = heading ? FontWeights.Bold : FontWeights.Normal });
            }
        }
        if (streaming) _answerScroll.ScrollToEnd();
    }
}
