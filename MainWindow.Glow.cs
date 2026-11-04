using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

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

    /// <summary>Re-applies glow to everything coloured. Cheap: effects are updated in place.</summary>
    private void ApplyGlowEffects()
    {
        var music = Bright(_musicAccent);
        Glow(CompactViz, music);
        Glow(ExpandedViz, music);

        // header word ("Player", "Code"...) and the selected tab
        var tabColor = TabColor(_tab);
        if (_tab == Tab.Home) NoGlow(HeaderB); else Glow(HeaderB, tabColor);
        foreach (var (t, (button, _)) in _nav)
        {
            if (t == _tab && t != Tab.Home) Glow((UIElement)button.Content, TabColor(t));
            else NoGlow((UIElement)button.Content);
        }

        // Claude indicators
        Glow(HeaderClaudeDot, ClaudeColor);
        Glow(CompactAlertDot, ClaudeColor);
        Glow(CompactClaudeGlyph, ClaudeColor);
        if (CompactIcon.Foreground is SolidColorBrush ib && ib.Color != Colors.White) Glow(CompactIcon, ib.Color); else NoGlow(CompactIcon);
        for (int i = 0; i < _stepFills.Length; i++)
            if (_stepFills[i] != null)
            {
                var c = _stepFills[i].Background is SolidColorBrush sb ? sb.Color : Colors.White;
                if (c == Colors.White) NoGlow(_stepFills[i]); else Glow(_stepFills[i], c);
            }

        // focus
        if (FocusProgress.Background is SolidColorBrush fb) Glow(FocusProgress, fb.Color);

        // home cards: a soft halo in each card's colour
        Glow(HomeClaudeCard, ClaudeColor, big: true);
        Glow(HomeMusicCard, music, big: true);
        Glow(HomeFocusCard, FocusColor, big: true);
    }
}
