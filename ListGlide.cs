using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Notchy;

/// <summary>Marks an element (by what it shows) so a list drawn again can find it.</summary>
public sealed record GlideKey(object Id);

/// <summary>
/// A list (or a settings page) drawn again from scratch: what was there glides from its old place to the new one, what
/// changed size grows or shrinks (pushing what's below along), and what's new fades in, instead of everything jumping.
/// Elements are matched by their <see cref="GlideKey"/> tag.
/// </summary>
public static class ListGlide
{
    public static T Key<T>(T el, object id) where T : FrameworkElement
    {
        el.Tag = new GlideKey(id);
        return el;
    }

    /// <summary>Where each marked element is now, and how tall (before the redraw).</summary>
    public static Dictionary<GlideKey, (double y, double h)> Snap(FrameworkElement root)
    {
        var at = new Dictionary<GlideKey, (double, double)>();
        foreach (var e in Marked(root)) at.TryAdd((GlideKey)e.Tag, (e.TranslatePoint(new Point(0, 0), root).Y, e.ActualHeight));
        return at;
    }

    private static IEnumerable<FrameworkElement> Marked(DependencyObject o)
    {
        if (o is FrameworkElement { Tag: GlideKey, IsVisible: true } fe) yield return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            foreach (var c in Marked(VisualTreeHelper.GetChild(o, i))) yield return c;
    }

    private static bool Inside(DependencyObject child, DependencyObject parent)
    {
        for (var p = VisualTreeHelper.GetParent(child); p != null; p = VisualTreeHelper.GetParent(p))
            if (p == parent) return true;
        return false;
    }

    /// <summary>After the redraw. <paramref name="ms"/> 0 = no animation.</summary>
    public static void Play(FrameworkElement root, Dictionary<GlideKey, (double y, double h)> before, double ms)
    {
        if (before.Count == 0 || ms <= 0) return;
        root.UpdateLayout();
        // every position and size first, before anything moves
        var now = Marked(root).Select(e => (el: e, y: e.TranslatePoint(new Point(0, 0), root).Y, h: e.ActualHeight,
            was: before.TryGetValue((GlideKey)e.Tag, out var w) ? w : ((double y, double h)?)null)).ToList();
        // what changed size grows/shrinks from the old size: the layout carries everything after it along
        var resized = now.Where(n => n.was is { } w && Math.Abs(n.h - w.h) > 1).ToList();

        var d = TimeSpan.FromMilliseconds(ms);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var moved = new Dictionary<FrameworkElement, double>();
        foreach (var n in now)
        {
            if (n.was is not { } was)
            {
                if (now.Any(o => o.was == null && Inside(n.el, o.el))) continue; // (inside something new: comes with it)
                Shift(n.el).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, d) { EasingFunction = ease });
                n.el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms * 0.8)));
                continue;
            }
            // where the layout puts it at the start: minus the growth of what's above it (still at the old sizes)
            double growthAbove = resized.Where(r => r.el != n.el && !Inside(n.el, r.el) && !Inside(r.el, n.el) && r.y + r.h <= n.y + 0.5).Sum(r => r.h - r.was!.Value.h);
            double byParents = moved.Where(m => Inside(n.el, m.Key)).Sum(m => m.Value);
            double dy = was.y - n.y + growthAbove - byParents;
            if (Math.Abs(dy) > 0.5)
            {
                moved[n.el] = dy;
                Shift(n.el).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, d) { EasingFunction = ease });
            }
        }
        foreach (var r in resized)
        {
            var el = r.el;
            bool clip = el.ClipToBounds;
            el.ClipToBounds = true;
            var grow = new DoubleAnimation(r.was!.Value.h, r.h, d) { EasingFunction = ease };
            grow.Completed += (_, _) =>
            {
                el.BeginAnimation(FrameworkElement.HeightProperty, null); // (back to its own size)
                el.ClipToBounds = clip;
            };
            el.BeginAnimation(FrameworkElement.HeightProperty, grow);
        }
    }

    // (an element moved by its own transform, like a stacked card, keeps it: the glide is added to it)
    private static TranslateTransform Shift(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform t) return t;
        if (el.RenderTransform is TransformGroup { Children.Count: > 0 } g && g.Children[^1] is TranslateTransform last) return last;
        var glide = new TranslateTransform();
        el.RenderTransform = el.RenderTransform is null || el.RenderTransform == Transform.Identity
            ? glide : new TransformGroup { Children = { el.RenderTransform, glide } };
        return glide;
    }
}
