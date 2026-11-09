using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;

namespace Notchy;

/// <summary>
/// Home cards tilt in real 3D towards the mouse (the side under the cursor sinks, the opposite side comes
/// closer) with a soft light under the cursor. While the mouse is on a card it is drawn as a 3D plane
/// (a live VisualBrush of the card, so it keeps updating); once it settles back flat the normal, crisp
/// 2D card is shown again. Nothing moves when the mouse isn't on a card.
/// </summary>
public partial class MainWindow
{
    private sealed class Tilt
    {
        public required Border Card;
        public required FrameworkElement Picture; // what the 3D plane shows: the card and its glow
        public required Button Button;
        public required Border Shine;
        public required RadialGradientBrush Light;
        public Viewport3D? View;
        public AxisAngleRotation3D? TurnX, TurnY;
        public ScaleTransform3D? Lift;
        public double X, Y, Hover;          // current (smoothed)
        public bool Leaving;                // the 3D plane is fading away over the crisp card
        public required FrameworkElement Flat; // the card's own layer (the glow is beside it, not in it)
        public double TargetX, TargetY, TargetHover;
    }

    private readonly List<Tilt> _tilts = new();
    private bool _tiltRunning;
    private Canvas? _tiltLayer;

    private const double TiltDegrees = 7;      // max turn at the card's edge
    private const double TiltDistance = 520;   // camera distance in pixels: lower = stronger perspective
    private const double TiltGlowMargin = 26;  // room for the card's coloured glow around it
    private const double TiltPad = 14;         // room for the corners coming closer

    private void InitCardTilt()
    {
        // a free-drawing layer over the three cards (a Canvas doesn't clip what sticks out)
        _tiltLayer = new Canvas { IsHitTestVisible = false };
        Grid.SetColumnSpan(_tiltLayer, 3);
        Panel.SetZIndex(_tiltLayer, 10);
        HomeTab.Children.Add(_tiltLayer);

        foreach (var card in new[] { HomeClaudeCard, HomeMusicCard, HomeFocusCard, HomeWeatherCard })
        {
            // the card sits in the button directly, or in a grid with its glow (MainWindow.Glow.cs)
            var picture = card.Parent is Grid g ? (FrameworkElement)g : card;
            if (picture.Parent is not Button button || card.Child is not Grid content) continue;

            var light = new RadialGradientBrush
            {
                RadiusX = 0.8,
                RadiusY = 1.2,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x22, 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb(0x0A, 255, 255, 255), 0.5),
                    new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 1)
                }
            };
            var shine = new Border
            {
                Background = light,
                CornerRadius = card.CornerRadius,
                Margin = new Thickness(-card.Padding.Left, -card.Padding.Top, -card.Padding.Right, -card.Padding.Bottom),
                IsHitTestVisible = false,
                Opacity = 0
            };
            card.Child = null;
            var layers = new Grid();
            layers.Children.Add(content);
            layers.Children.Add(shine);
            card.Child = layers;

            // the card gets a layer of its own next to its glow, so it can hide while the glow stays
            FrameworkElement flat = card;
            if (card.Parent is Grid layersGrid)
            {
                int i = layersGrid.Children.IndexOf(card);
                layersGrid.Children.RemoveAt(i);
                var own = new Grid();
                own.Children.Add(card);
                layersGrid.Children.Insert(i, own);
                flat = own;
            }
            var t = new Tilt { Card = card, Picture = picture, Button = button, Shine = shine, Light = light, Flat = flat };
            _tilts.Add(t);

            button.MouseEnter += (_, e) => { t.TargetHover = 1; Aim(t, e); };
            button.MouseMove += (_, e) => Aim(t, e);
            button.MouseLeave += (_, _) =>
            {
                t.TargetHover = 0;
                t.TargetX = t.TargetY = 0;
                StartTilt();
            };
        }
    }

    /// <summary>Mouse position on the card, from -1 (left/top) to 1 (right/bottom).</summary>
    private void Aim(Tilt t, MouseEventArgs e)
    {
        double w = t.Button.ActualWidth, h = t.Button.ActualHeight;
        if (w < 1 || h < 1) return;
        var p = e.GetPosition(t.Button);
        t.TargetX = Math.Clamp(p.X / w * 2 - 1, -1, 1);
        t.TargetY = Math.Clamp(p.Y / h * 2 - 1, -1, 1);
        if (t.View == null) Enter3D(t);
        else if (t.Leaving) StayIn3D(t);
        StartTilt();
    }

    private void StartTilt()
    {
        if (_tiltRunning) return;
        _tiltRunning = true;
        CompositionTarget.Rendering += TiltFrame;
    }

    private void TiltFrame(object? sender, EventArgs e)
    {
        bool moving = false;
        double k = Math.Clamp(0.12 / Math.Max(0.3, S.SpeedFactor), 0.04, 0.4); // calm follow (animation speed setting)
        foreach (var t in _tilts)
        {
            t.X += (t.TargetX - t.X) * k;
            t.Y += (t.TargetY - t.Y) * k;
            t.Hover += (t.TargetHover - t.Hover) * k;
            bool settling = Math.Abs(t.TargetX - t.X) > 0.002 || Math.Abs(t.TargetY - t.Y) > 0.002 ||
                            Math.Abs(t.TargetHover - t.Hover) > 0.002;
            if (settling) moving = true;
            else
            {
                t.X = t.TargetX;
                t.Y = t.TargetY;
                t.Hover = t.TargetHover;
            }

            if (t.View != null)
            {
                // the side under the cursor sinks away, the opposite side comes closer
                t.TurnY!.Angle = t.X * TiltDegrees * t.Hover;
                t.TurnX!.Angle = t.Y * TiltDegrees * 0.8 * t.Hover;
                t.Lift!.ScaleX = t.Lift.ScaleY = 1 + 0.03 * t.Hover; // same little grow as before
            }
            var c = new Point(0.5 + t.X * 0.5, 0.5 + t.Y * 0.5);
            t.Light.Center = c;
            t.Light.GradientOrigin = c;
            t.Shine.Opacity = t.Hover;

            if (!settling && t.Hover == 0 && t.View != null && !t.Leaving) Exit3D(t); // flat again: back to the crisp 2D card
        }
        if (!moving)
        {
            CompositionTarget.Rendering -= TiltFrame;
            _tiltRunning = false;
        }
    }

    /// <summary>Shows the card as a 3D plane (in the overlay) and hides the 2D one, which still gets the clicks.</summary>
    private void Enter3D(Tilt t)
    {
        if (_tiltLayer == null) return;
        double w = t.Card.ActualWidth, h = t.Card.ActualHeight;
        if (w < 1 || h < 1) return;
        double m = TiltGlowMargin;
        double qw = w + 2 * m, qh = h + 2 * m;
        double vw = qw + 2 * TiltPad, vh = qh + 2 * TiltPad;

        // Only the card turns 3D: its coloured glow (the layer behind it) stays the real 2D one, lit the whole
        // time, so the glow never changes when the card switches between 2D and 3D.
        // (a VisualBrush's coordinates include the card's own offset in its parent: centred, 112 in 118)
        var own = VisualTreeHelper.GetOffset(t.Card);
        var brush = new VisualBrush(t.Card)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(own.X - m, own.Y - m, qw, qh),
            Stretch = Stretch.Fill
        };
        var mesh = new MeshGeometry3D
        {
            Positions = { new Point3D(-qw / 2, qh / 2, 0), new Point3D(qw / 2, qh / 2, 0), new Point3D(qw / 2, -qh / 2, 0), new Point3D(-qw / 2, -qh / 2, 0) },
            TextureCoordinates = { new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1) },
            TriangleIndices = { 0, 3, 2, 0, 2, 1 }
        };
        t.TurnX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        t.TurnY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        t.Lift = new ScaleTransform3D(1, 1, 1);
        var transform = new Transform3DGroup();
        transform.Children.Add(t.Lift);
        transform.Children.Add(new RotateTransform3D(t.TurnX));
        transform.Children.Add(new RotateTransform3D(t.TurnY));
        var material = new DiffuseMaterial(brush);
        var model = new GeometryModel3D(mesh, material) { BackMaterial = material, Transform = transform };

        // camera at TiltDistance px: at rest the plane covers exactly the card's pixels
        double fov = 2 * Math.Atan(vw / 2 / TiltDistance) * 180 / Math.PI;
        var view = new Viewport3D
        {
            Width = vw,
            Height = vh,
            IsHitTestVisible = false,
            Camera = new PerspectiveCamera(new Point3D(0, 0, TiltDistance), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), fov)
        };
        RenderOptions.SetBitmapScalingMode(view, BitmapScalingMode.HighQuality); // the sharpest the texture can be
        view.Children.Add(new ModelVisual3D { Content = new AmbientLight(Colors.White) });
        view.Children.Add(new ModelVisual3D { Content = model });

        // where the card sits in the layout, without the button's hover-grow transform (which is animating
        // right now and would shift the plane): the button's layout offset in HomeTab + the card inside it
        var offset = VisualTreeHelper.GetOffset(t.Button);
        var inButton = t.Card.TranslatePoint(new Point(w / 2, h / 2), t.Button);
        var center = new Point(offset.X + inButton.X, offset.Y + inButton.Y);
        Canvas.SetLeft(view, center.X - vw / 2);
        Canvas.SetTop(view, center.Y - vh / 2);
        _tiltLayer.Children.Add(view);
        t.View = view;
        t.Leaving = false;
        // the plane fades in over the card (which stays fully drawn under it, so nothing gets see-through
        // half-way); then the 2D card hides (only the card: its glow stays, and the button keeps the mouse)
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Dur(90)));
        fadeIn.Completed += (_, _) =>
        {
            if (t.View == view && !t.Leaving) HideFlat(t, true);
        };
        view.BeginAnimation(OpacityProperty, fadeIn);
    }

    /// <summary>
    /// Flat again: the crisp card comes back underneath at once and the 3D plane fades away over it, so the
    /// switch from the drawn copy to the real card can't be seen.
    /// </summary>
    private void Exit3D(Tilt t)
    {
        var view = t.View;
        if (view == null) return;
        t.Leaving = true;
        HideFlat(t, false);
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Dur(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        fadeOut.Completed += (_, _) =>
        {
            if (!t.Leaving || t.View != view) return;
            _tiltLayer?.Children.Remove(view);
            t.View = null;
            t.Leaving = false;
        };
        view.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Hides the flat card while the 3D plane shows it: its own layer goes invisible (the brush still draws the
    /// card, which is inside it), while the glow next to it stays lit.
    /// </summary>
    private static void HideFlat(Tilt t, bool hide) => t.Flat.Opacity = hide ? 0 : 1;

    /// <summary>The mouse came back while the plane was fading away: it stays.</summary>
    private void StayIn3D(Tilt t)
    {
        if (t.View == null) return;
        t.Leaving = false;
        var back = new DoubleAnimation(1, TimeSpan.FromMilliseconds(Dur(90)));
        var view = t.View;
        back.Completed += (_, _) =>
        {
            if (t.View == view && !t.Leaving) HideFlat(t, true);
        };
        view.BeginAnimation(OpacityProperty, back);
    }
}
