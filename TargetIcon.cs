using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>Focus's target icon. The icon font has none, so it's drawn, on a 24-unit grid like the font's.</summary>
internal static class TargetIcon
{
    /// <summary>The glyph that stands for the target in a list of font icons (it keeps the text's size and colour).</summary>
    public const string Placeholder = "\uECCB";

    /// <summary>The drawing, in the colour <paramref name="colorOf"/> has at any moment.</summary>
    public static Viewbox Art(TextBlock colorOf, double size)
    {
        var color = new Binding(nameof(TextBlock.Foreground)) { Source = colorOf };
        var rings = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M12,2.8 A9.2,9.2 0 1 1 12,21.2 A9.2,9.2 0 1 1 12,2.8 Z M12,7.2 A4.8,4.8 0 1 1 12,16.8 A4.8,4.8 0 1 1 12,7.2 Z"),
            StrokeThickness = 1.8
        };
        rings.SetBinding(Shape.StrokeProperty, color);
        var dot = new Ellipse { Width = 3, Height = 3 };
        dot.SetBinding(Shape.FillProperty, color);
        Canvas.SetLeft(dot, 10.5);
        Canvas.SetTop(dot, 10.5);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(rings);
        canvas.Children.Add(dot);
        return new Viewbox
        {
            Child = canvas, Width = size, Height = size, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>A font icon as it is, or, when it's the placeholder, the target drawn in its place.</summary>
    public static FrameworkElement Show(TextBlock icon)
    {
        if (icon.Text != Placeholder) return icon;
        var host = new Grid { HorizontalAlignment = icon.HorizontalAlignment, VerticalAlignment = icon.VerticalAlignment, Margin = icon.Margin };
        icon.Margin = new Thickness(0);
        icon.Opacity = 0;
        double size = icon.FontSize * 1.12;
        var art = Art(icon, size);
        art.Margin = new Thickness(-(size - icon.FontSize) / 2);
        host.Children.Add(icon);
        host.Children.Add(art);
        return host;
    }
}
