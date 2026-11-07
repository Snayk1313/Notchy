using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Notchy;

/// <summary>
/// The empty Downloads tab: a cloud taking a nap. Its green eyes are closed, it breathes slowly and little
/// "z"s float up from it. (The header hides its own eyes meanwhile, so there's a single pair.)
/// </summary>
public partial class MainWindow
{
    private bool _dlEmpty; // the Downloads tab shows the sleepy cloud

    private UIElement SleepyCloud()
    {
        var green = new SolidColorBrush(DoneGreen);
        green.Freeze();
        var line = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
        line.Freeze();
        Effect Glow() => new DropShadowEffect { ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9, Color = DoneGreen };

        var canvas = new Canvas
        {
            Width = 24,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            LayoutTransform = new ScaleTransform(1.3, 1.3)
        };

        // the cloud and its closed eyes, breathing
        var body = new Canvas();
        body.Children.Add(new Path
        {
            Data = Geometry.Parse("M7,19.5 A4.5,4.5 0 0 1 6.4,10.54 A6,6 0 0 1 17.7,9.6 A4,4 0 0 1 17.5,19.5 Z"),
            Stroke = line,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round
        });
        body.Children.Add(new Path
        {
            Data = Geometry.Parse("M8.6,14.8 Q9.8,16.2 11,14.8 M13,14.8 Q14.2,16.2 15.4,14.8"),
            Stroke = green,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = Glow()
        });
        var breathe = new TranslateTransform();
        body.RenderTransform = breathe;
        canvas.Children.Add(body);
        breathe.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 0.8, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        // two little z's floating up, one after the other
        void Z(double size, double delay)
        {
            var z = new Path
            {
                Data = Geometry.Parse(FormattableString.Invariant($"M0,0 H{size} L0,{size} H{size}")),
                Stroke = green,
                StrokeThickness = 1.1,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Effect = Glow(),
                Opacity = 0
            };
            Canvas.SetLeft(z, 17.5);
            Canvas.SetTop(z, 4.5);
            var move = new TranslateTransform();
            z.RenderTransform = move;
            canvas.Children.Add(z);

            var period = TimeSpan.FromSeconds(2.8);
            var begin = TimeSpan.FromSeconds(delay);
            var ease = new SineEase { EasingMode = EasingMode.EaseOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 4, period) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -7, period) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            var fade = new DoubleAnimationUsingKeyFrames { Duration = period, BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.2)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            z.BeginAnimation(OpacityProperty, fade);
        }
        Z(3, 0);
        Z(2.3, 1.4);
        return canvas;
    }
}
