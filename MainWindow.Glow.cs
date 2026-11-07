using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>Soft neon glow on the coloured parts of the island.</summary>
public partial class MainWindow
{
    private (double blur, double opacity) GlowParams(bool big) => (S.GlowLevel, big) switch
    {
        (<= 0, _) => (0, 0),
        (1, false) => (8, 0.6),
        (1, true) => (18, 0.35),
        (2, false) => (14, 0.9),
        (2, true) => (26, 0.55),
        (_, false) => (20, 1.0),
        (_, true) => (34, 0.75),
    };

    /// <summary>Sets (or updates in place) a coloured glow on an element.</summary>
    private void Glow(UIElement el, Color color, bool big = false)
    {
        var (blur, opacity) = GlowParams(big);
        if (blur <= 0)
        {
            el.Effect = null;
            return;
        }
        if (el.Effect is DropShadowEffect fx && !fx.IsFrozen)
        {
            if (fx.Color != color) fx.Color = color;
            if (fx.BlurRadius != blur) fx.BlurRadius = blur;
            if (fx.Opacity != opacity) fx.Opacity = opacity;
            return;
        }
        el.Effect = new DropShadowEffect { ShadowDepth = 0, Color = color, BlurRadius = blur, Opacity = opacity, RenderingBias = RenderingBias.Performance };
    }

    private static void NoGlow(UIElement el) => el.Effect = null;

    // ---- glow underneath: anything drawn through an effect (text, icons, pictures) loses sharpness, and is
    //      redrawn a little differently each time something nearby repaints. So the element itself stays plain
    //      and crisp, and a live picture of it right underneath carries the glow (only the halo shows).

    private readonly Dictionary<FrameworkElement, Rectangle> _glowUnder = new();

    /// <summary>A glow around <paramref name="el"/> that doesn't touch the element itself.</summary>
    private void GlowUnder(FrameworkElement el, Color color, bool on = true, double? blur = null, double? opacity = null)
    {
        if (!_glowUnder.TryGetValue(el, out var picture))
        {
            if (!on) return;
            picture = MakeGlowUnder(el);
            if (picture == null)
            {
                Glow(el, color); // nowhere to put it: the old way
                return;
            }
            _glowUnder[el] = picture;
        }
        if (!on)
        {
            NoGlow(picture);
            picture.Visibility = Visibility.Collapsed;
            return;
        }
        picture.Visibility = Visibility.Visible;
        Glow(picture, color);
        if ((blur != null || opacity != null) && picture.Effect is DropShadowEffect fx)
        {
            if (blur is double b && S.GlowLevel > 0) fx.BlurRadius = b;
            if (opacity is double o && S.GlowLevel > 0) fx.Opacity = o;
        }
    }

    /// <summary>Puts <paramref name="el"/> in a little grid with its live picture underneath.</summary>
    private static Rectangle? MakeGlowUnder(FrameworkElement el)
    {
        var host = new Grid
        {
            Margin = el.Margin,
            HorizontalAlignment = el.HorizontalAlignment,
            VerticalAlignment = el.VerticalAlignment
        };
        switch (el.Parent)
        {
            case Panel panel:
            {
                int i = panel.Children.IndexOf(el);
                panel.Children.RemoveAt(i);
                Grid.SetRow(host, Grid.GetRow(el));
                Grid.SetColumn(host, Grid.GetColumn(el));
                Grid.SetRowSpan(host, Grid.GetRowSpan(el));
                Grid.SetColumnSpan(host, Grid.GetColumnSpan(el));
                DockPanel.SetDock(host, DockPanel.GetDock(el));
                Panel.SetZIndex(host, Panel.GetZIndex(el));
                panel.Children.Insert(i, host);
                break;
            }
            case ContentControl cc when ReferenceEquals(cc.Content, el):
                cc.Content = host;
                break;
            default:
                return null;
        }
        // a hidden element hides its glow too (and its margin doesn't leave a gap)
        BindingOperations.SetBinding(host, UIElement.VisibilityProperty, new Binding(nameof(Visibility)) { Source = el });
        el.Margin = new Thickness(0);
        el.HorizontalAlignment = HorizontalAlignment.Stretch;
        el.VerticalAlignment = VerticalAlignment.Stretch;

        var brush = new VisualBrush(el) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        var picture = new Rectangle { Fill = brush, IsHitTestVisible = false };
        void Fit()
        {
            if (el.ActualWidth > 0 && el.ActualHeight > 0) brush.Viewbox = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        }
        el.SizeChanged += (_, _) => Fit();
        Fit();
        host.Children.Add(picture);
        host.Children.Add(el);
        return picture;
    }

    // ---- the home cards: a plain shape the card's colour right behind each card carries its glow

    private readonly Dictionary<Border, Border> _cardGlow = new();

    private void InitCardGlow()
    {
        foreach (var card in new[] { HomeClaudeCard, HomeMusicCard, HomeFocusCard })
        {
            if (card.Parent is not Button button) continue;
            var halo = new Border { CornerRadius = card.CornerRadius, IsHitTestVisible = false, Height = card.Height };
            button.Content = null;
            var layers = new Grid();
            layers.Children.Add(halo);
            layers.Children.Add(card);
            button.Content = layers;
            _cardGlow[card] = halo;
        }
    }

    private void CardGlow(Border card, Color color)
    {
        if (!_cardGlow.TryGetValue(card, out var halo))
        {
            Glow(card, color, big: true);
            return;
        }
        if (halo.Background is not SolidColorBrush b || b.Color != color) halo.Background = new SolidColorBrush(color);
        Glow(halo, color, big: true);
    }

    /// <summary>Re-applies glow to everything coloured. Cheap: effects are updated in place.</summary>
    private void ApplyGlowEffects()
    {
        var music = Bright(_musicAccent);
        Glow(CompactViz, music);
        Glow(ExpandedViz, music);

        // header word ("Player", "Code"...) and the selected tab
        var tabColor = TabColor(_tab);
        // the glow goes on a copy of the word underneath: text drawn through an effect is anti-aliased a little
        // differently each time something nearby repaints (it seemed to shake), so the word itself stays plain and crisp
        if (_tab == Tab.Home) NoGlow(HeaderBGlow); else Glow(HeaderBGlow, tabColor);
        foreach (var (t, _) in _nav)
            if (_navContent.TryGetValue(t, out var content)) GlowUnder(content, TabColor(t), t == _tab && t != Tab.Home);

        // Claude indicators
        Glow(HeaderClaudeDot, ClaudeColor);
        Glow(CompactAlertDot, ClaudeColor);
        GlowUnder(CompactClaudeGlyph, ClaudeColor);
        if (CompactIcon.Foreground is SolidColorBrush ib && ib.Color != Colors.White) GlowUnder(CompactIcon, ib.Color);
        else GlowUnder(CompactIcon, Colors.White, on: false);
        for (int i = 0; i < _stepFills.Length; i++)
            if (_stepFills[i] != null)
            {
                var c = _stepFills[i].Background is SolidColorBrush sb ? sb.Color : Colors.White;
                if (c == Colors.White) NoGlow(_stepFills[i]); else Glow(_stepFills[i], c);
            }


        // home cards: a soft halo in each card's colour
        CardGlow(HomeClaudeCard, ClaudeColor);
        CardGlow(HomeMusicCard, music);
        CardGlow(HomeFocusCard, FocusColor);
    }
}
