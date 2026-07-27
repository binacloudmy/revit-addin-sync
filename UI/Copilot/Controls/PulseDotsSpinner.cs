using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// Three dots that pulse in sequence — the loading indicator beside live
    /// status text.
    ///
    /// Third attempt, and the reasoning matters. The spinning arc read as a
    /// generic busy throbber. A 3×3 shimmer grid and then a faithful port of the
    /// designer's 12-dot ring both followed, and the ring was rejected on sight
    /// (2026-07-27) — correctly: that artwork is 54 dots across three radii on a
    /// 400×400 canvas, and its dots are ~12% of the ring's diameter. Scaled to
    /// the ~16px that fits beside a line of chat text, each dot lands under 2px
    /// and twelve of them dissolve into grey speckle. The design is not wrong;
    /// it is drawn for a canvas two orders of magnitude larger.
    ///
    /// Three dots at 4px, spaced 4px, is legible at text size — which is why
    /// every chat product converges on it. Each dot carries BOTH scale and
    /// opacity so the wave stays visible even at this size, staggered by a third
    /// of the period so exactly one dot is at its peak at any moment.
    ///
    /// Animations use direct BeginAnimation calls, never a XAML Storyboard — a
    /// Storyboard inside a Revit dockable pane crashes.
    /// </summary>
    public class PulseDotsSpinner : StackPanel
    {
        private const int Count = 3;
        private const double Dot = 4.0;
        private const double Gap = 4.0;
        // Slow enough to read as breathing; fast enough to say "working".
        private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1200);

        // Dim/small at rest, full at peak. The floor is deliberately well above
        // zero: dots that vanish entirely make the row look broken rather than
        // busy.
        private const double MinScale = 0.55;
        private const double MinOpacity = 0.30;

        public PulseDotsSpinner()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;
            Height = Dot;

            for (int i = 0; i < Count; i++)
            {
                var e = new Ellipse
                {
                    Width = Dot,
                    Height = Dot,
                    Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Opacity = MinOpacity,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                e.SetResourceReference(Shape.FillProperty, "Cp.Accent");

                var scale = new ScaleTransform(MinScale, MinScale);
                e.RenderTransform = scale;
                Children.Add(e);

                // A third of the period apart: the crest walks left to right and
                // wraps seamlessly.
                var begin = TimeSpan.FromMilliseconds(Period.TotalMilliseconds * i / Count);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Wave(MinScale, 1.0, begin));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Wave(MinScale, 1.0, begin));
                e.BeginAnimation(UIElement.OpacityProperty, Wave(MinOpacity, 1.0, begin));
            }
        }

        // One pulse per period: up over the first third, back down over the
        // rest, so a dot spends most of its cycle at rest and the crest reads as
        // a distinct travelling highlight rather than three dots throbbing
        // together.
        private static DoubleAnimationUsingKeyFrames Wave(double lo, double hi, TimeSpan begin)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Period),
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            var ms = Period.TotalMilliseconds;
            anim.KeyFrames.Add(new SplineDoubleKeyFrame(lo, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new SplineDoubleKeyFrame(
                hi, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms * 0.33)),
                new KeySpline(0.25, 0.1, 0.25, 1.0)));
            anim.KeyFrames.Add(new SplineDoubleKeyFrame(
                lo, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)),
                new KeySpline(0.4, 0.0, 0.6, 1.0)));
            return anim;
        }
    }
}
