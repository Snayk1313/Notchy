using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>
/// Small things that used to change from one frame to the next and now move: a text that slips out while the new one
/// comes in, a cover that fades into the next, an icon that hops when it turns into another, a colour that goes
/// smoothly to the next one, something that appears with a little rise instead of popping in.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ a text that changes in front of you

    private readonly Dictionary<TextBlock, string> _textComing = new();

    /// <summary>The old text slips up and out, the new one comes from below (set right away when it isn't on screen).</summary>
    private async void SwapText(TextBlock tb, string text)
    {
        if ((_textComing.TryGetValue(tb, out var coming) ? coming : tb.Text) == text) return;
        if (!tb.IsVisible || S.SpeedFactor <= 0 || tb.Text.Length == 0)
        {
            _textComing.Remove(tb);
            tb.Text = text;
            return;
        }
        bool leaving = _textComing.ContainsKey(tb);
        _textComing[tb] = text;
        if (leaving) return; // (already on its way out: it comes back with the newest text)
        if (tb.RenderTransform is not TranslateTransform move) tb.RenderTransform = move = new TranslateTransform();
        var away = TimeSpan.FromMilliseconds(Dur(110));
        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, away));
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-7, away) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        await Task.Delay(away);
        if (!_textComing.Remove(tb, out var now)) return;
        tb.Text = now;
        var back = TimeSpan.FromMilliseconds(Dur(280));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, back) { EasingFunction = ease });
        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, back) { EasingFunction = ease });
    }

    // ------------------------------------------------------------------ the album cover

    private ImageSource? _artShown;

    /// <summary>The new cover fades in over the old one (the big one gives a little pop too).</summary>
    private void SwapArt(Border art, Brush brush, bool animate, bool pop = false)
    {
        var old = art.Background;
        art.Background = brush;
        if (!animate || !art.IsVisible || S.SpeedFactor <= 0)
        {
            art.Child = null;
            return;
        }
        var cover = new Border { Background = old, CornerRadius = art.CornerRadius, IsHitTestVisible = false };
        art.Child = cover;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Dur(380))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) =>
        {
            if (ReferenceEquals(art.Child, cover)) art.Child = null;
        };
        cover.BeginAnimation(OpacityProperty, fade);
        if (!pop) return;
        var scale = new ScaleTransform(1, 1);
        art.RenderTransformOrigin = new Point(0.5, 0.5);
        art.RenderTransform = scale;
        var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(Dur(420))) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // ------------------------------------------------------------------ the music's colour

    private Color _musicFrom, _musicTo;
    private double _musicTweenAt = -1;
    private bool _musicColorSet;

    /// <summary>The song's colour (bars, glow, tab, home card) goes smoothly to the next song's.</summary>
    private void SetMusicAccent(Color target)
    {
        if (!_musicColorSet || S.SpeedFactor <= 0)
        {
            _musicColorSet = true;
            _musicAccent = _musicTo = target;
            _musicTweenAt = -1;
            return;
        }
        if (target == _musicTo) return;
        _musicFrom = _musicAccent;
        _musicTo = target;
        _musicTweenAt = AnimNow;
    }

    private void UpdateMusicAccentFrame()
    {
        if (_musicTweenAt < 0) return;
        double p = Math.Clamp((AnimNow - _musicTweenAt) / Math.Max(0.05, Dur(480) / 1000), 0, 1);
        _musicAccent = Mix(_musicFrom, _musicTo, p * p * (3 - 2 * p));
        if (p >= 1)
        {
            _musicAccent = _musicTo;
            _musicTweenAt = -1;
            if (_spTimer != null) UpdateSpotifyControls(); // (the heart and the other buttons take the final colour)
        }
        ApplyMusicAccent();
    }

    /// <summary>Everything painted with the song's colour, as it is now.</summary>
    private void ApplyMusicAccent()
    {
        var bright = new SolidColorBrush(Bright(_musicAccent));
        bright.Freeze();
        CompactViz.BarBrush = bright;
        ExpandedViz.BarBrush = bright;
        UpdateEyeColor();
        ApplyGlowEffects();
        var d = Dark(_musicAccent);
        HomeMusicCard.Background = new LinearGradientBrush(
            Color.FromRgb((byte)(d.R / 2), (byte)(d.G / 2), (byte)(d.B / 2)),
            Color.FromRgb((byte)Math.Min(255, d.R * 1.5), (byte)Math.Min(255, d.G * 1.5), (byte)Math.Min(255, d.B * 1.5)),
            new Point(0, 0), new Point(0.4, 1));
        if (_tab != Tab.Music) return;
        UpdateHeader();
        ApplyGlow();
        // (the menu's pill and the tab's icon keep their place: only the colour follows)
        var tab = TabColor(Tab.Music);
        NavPill.Background = new SolidColorBrush(Color.FromArgb(0x2E, tab.R, tab.G, tab.B));
        if (_nav.TryGetValue(Tab.Music, out var nav)) nav.button.Foreground = new SolidColorBrush(tab);
    }

    // ------------------------------------------------------------------ an icon that turns into another (play ↔ pause)

    /// <summary>The button's icon changes with a little hop. The icon becomes a text of its own, so it moves without the button.</summary>
    private void SwapGlyph(Button button, string glyph)
    {
        if (button.Content is not TextBlock icon)
        {
            icon = new TextBlock
            {
                Text = button.Content as string ?? "", RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            button.Content = icon;
        }
        if (icon.Text == glyph) return;
        icon.Text = glyph;
        if (!button.IsVisible || S.SpeedFactor <= 0) return;
        var scale = (ScaleTransform)icon.RenderTransform;
        var hop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(Dur(280))) { EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, hop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, hop);
        icon.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(Dur(150))));
    }

    // ------------------------------------------------------------------ something that shows up or goes away

    private readonly HashSet<FrameworkElement> _leaving = new();

    /// <summary>Shows or hides with a short fade and a little rise, instead of popping in and out.</summary>
    private void FadeVisible(FrameworkElement el, bool show, double rise = 6)
    {
        bool shown = el.Visibility == Visibility.Visible && !_leaving.Contains(el);
        if (show == shown) return;
        _leaving.Remove(el);
        if (!IsLoaded || S.SpeedFactor <= 0 || _state != IslandState.Expanded)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (show)
        {
            el.Visibility = Visibility.Visible;
            var time = TimeSpan.FromMilliseconds(Dur(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
            if (rise != 0 && (el.RenderTransform is null or TranslateTransform || el.RenderTransform == Transform.Identity))
            {
                var move = new TranslateTransform();
                el.RenderTransform = move;
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, time) { EasingFunction = ease });
            }
            return;
        }
        _leaving.Add(el);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(140)));
        fade.Completed += (_, _) =>
        {
            if (!_leaving.Remove(el)) return;
            el.Visibility = Visibility.Collapsed;
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 1;
        };
        el.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ eyes that turn into a smile

    private readonly Dictionary<FrameworkElement, int> _flipRun = new();

    /// <summary>
    /// Eyes turning into the smile (or back): they squash shut, change while shut, and open again with a little bounce.
    /// Each eye shares a small cell with its smile, and it is the cell that moves, so the eyes keep their own blinking and poses.
    /// </summary>
    private async void FlipEyes(bool happy, FrameworkElement eyeL, FrameworkElement eyeR, FrameworkElement smileL, FrameworkElement smileR)
    {
        int run = _flipRun[eyeL] = (_flipRun.TryGetValue(eyeL, out int before) ? before : 0) + 1;
        void Show()
        {
            eyeL.Visibility = eyeR.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
            smileL.Visibility = smileR.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;
        }
        var cells = new[] { eyeL.Parent as FrameworkElement, eyeR.Parent as FrameworkElement };
        var scales = new ScaleTransform[2];
        var hops = new TranslateTransform[2];
        for (int c = 0; c < 2; c++)
        {
            if (cells[c] is not { } cell) continue;
            if (cell.RenderTransform is not TransformGroup { Children.Count: 2 } group || group.Children[0] is not ScaleTransform || group.Children[1] is not TranslateTransform)
            {
                cell.RenderTransformOrigin = new Point(0.5, 0.5);
                cell.RenderTransform = group = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
            }
            scales[c] = (ScaleTransform)group.Children[0];
            hops[c] = (TranslateTransform)group.Children[1];
        }
        if (S.SpeedFactor <= 0 || scales[0] == null || scales[1] == null || (!eyeL.IsVisible && !smileL.IsVisible))
        {
            // not on screen: just as it should be, and open
            Show();
            foreach (var scale in scales)
            {
                scale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                if (scale != null) scale.ScaleY = 1;
            }
            return;
        }
        var shut = TimeSpan.FromMilliseconds(Dur(80));
        foreach (var scale in scales)
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.12, shut) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        await Task.Delay(shut);
        if (_flipRun[eyeL] != run) return; // (it changed again meanwhile: the newer one finishes)
        Show();
        var open = TimeSpan.FromMilliseconds(Dur(300));
        for (int c = 0; c < 2; c++)
        {
            scales[c].BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.12, 1, open) { EasingFunction = new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut } });
            if (happy)
                hops[c].BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, 0, TimeSpan.FromMilliseconds(Dur(480))) { EasingFunction = new BounceEase { Bounces = 2, Bounciness = 3 } });
        }
    }

    // ------------------------------------------------------------------ little answers to the mouse

    /// <summary>Grows into place with a small bounce (a badge showing up, or hopping when its number changes).</summary>
    private void PopIn(FrameworkElement el, double from)
    {
        if (S.SpeedFactor <= 0 || !el.IsVisible) return;
        if (el.RenderTransform is not ScaleTransform scale)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = scale = new ScaleTransform(1, 1);
        }
        var pop = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(Dur(320))) { EasingFunction = new BackEase { Amplitude = 0.8, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    /// <summary>The row lights up softly under the mouse and goes out the same way (instead of from one frame to the next).</summary>
    private void SoftHover(Border row, byte alpha = 0x0E, Func<bool>? when = null)
    {
        void To(byte a, double ms)
        {
            if (when?.Invoke() == false) return;
            var now = (row.Background as SolidColorBrush)?.Color ?? Color.FromArgb(0, 255, 255, 255);
            var brush = new SolidColorBrush(now);
            row.Background = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(a, 255, 255, 255), TimeSpan.FromMilliseconds(Dur(ms))));
        }
        row.MouseEnter += (_, _) => To(alpha, 110);
        row.MouseLeave += (_, _) => To(0, 200);
    }

    /// <summary>A pill gives a little under the click and springs back.</summary>
    private void Pressable(FrameworkElement pill)
    {
        var scale = new ScaleTransform(1, 1);
        pill.RenderTransformOrigin = new Point(0.5, 0.5);
        pill.RenderTransform = scale;
        void To(double size, double ms, IEasingFunction? ease)
        {
            var a = new DoubleAnimation(size, TimeSpan.FromMilliseconds(Dur(ms))) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        pill.PreviewMouseLeftButtonDown += (_, _) => To(0.93, 70, null);
        pill.PreviewMouseLeftButtonUp += (_, _) => To(1, 260, new BackEase { Amplitude = 0.9, EasingMode = EasingMode.EaseOut });
        pill.MouseLeave += (_, _) => To(1, 200, null);
    }
}
