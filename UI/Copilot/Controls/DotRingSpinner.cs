using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// A ring of dots that breathe in a travelling wave — the loading indicator
    /// for the live turn.
    ///
    /// Ported from the "Dotted spinner load" Lottie/SVG the designer supplied
    /// (2026-07-27). The source draws 54 dots on three concentric rings
    /// (r = 79 / 119 / 158 on a 400×400 canvas, 12 / 18 / 24 dots) and animates
    /// each dot's SCALE — not its opacity — over a 3s loop, with the phase baked
    /// into per-dot keyframes rather than a delay. The measured curve carries
    /// TWO pulses per loop (peaks at 34.7% and 93.1% of the timeline, troughs at
    /// 16.7% and 63.9%), ranges 0.035..0.996, and neighbouring dots on a ring lag
    /// each other by 85ms. <see cref="Curve"/> is that curve, sampled every 4th
    /// of its 72 keyframes; <see cref="LagMs"/> is that lag. Both are the source
    /// values, not an approximation by eye.
    ///
    /// What is deliberately NOT ported: three rings and 54 dots. Inline beside a
    /// line of chat text this control is ~16px, where 54 dots across three radii
    /// collapse into grey mush — the design is for a large canvas. One ring of
    /// the source's inner count (12) keeps the motion legible at text size.
    /// Construct with a bigger <see cref="Diameter"/> and Rings=3 for a
    /// full-pane loading state, where the original density does read.
    ///
    /// Animations are started with direct BeginAnimation calls and never a XAML
    /// Storyboard — a Storyboard inside a Revit dockable pane crashes (the
    /// constraint the old arc spinner already documented).
    /// </summary>
    public class DotRingSpinner : Canvas
    {
        // The source curve: every 4th of 72 keyframes across the 3s loop.
        private static readonly double[] Curve =
        {
            0.94, 0.72, 0.49, 0.43, 0.55, 0.78, 0.90, 0.85, 0.54,
            0.18, 0.06, 0.04, 0.05, 0.12, 0.41, 0.84, 0.99, 0.99,
        };

        private static readonly TimeSpan Loop = TimeSpan.FromSeconds(3);
        private const double LagMs = 85;          // measured dot-to-dot lag

        /// <param name="diameter">Overall control size in px.</param>
        /// <param name="dots">Dots on the ring (source inner ring uses 12).</param>
        public DotRingSpinner(double diameter = 16, int dots = 12)
        {
            if (dots < 3) dots = 3;
            Width = diameter;
            Height = diameter;
            SnapsToDevicePixels = true;

            // Dot size tracks the control so the ring stays airy at any size:
            // the source's inner ring reads as roughly 1/8 of its diameter.
            double dot = Math.Max(1.6, diameter / 8.0);
            double radius = (diameter - dot) / 2.0;
            double cx = diameter / 2.0, cy = diameter / 2.0;

            for (int i = 0; i < dots; i++)
            {
                // Start at 12 o'clock and go clockwise, matching the source's
                // sweep direction.
                double angle = -Math.PI / 2 + (2 * Math.PI * i / dots);
                var e = new Ellipse
                {
                    Width = dot,
                    Height = dot,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                };
                e.SetResourceReference(Shape.FillProperty, "Cp.Accent");

                var scale = new ScaleTransform(Curve[0], Curve[0]);
                e.RenderTransform = scale;

                Canvas.SetLeft(e, cx + radius * Math.Cos(angle) - dot / 2.0);
                Canvas.SetTop(e, cy + radius * Math.Sin(angle) - dot / 2.0);
                Children.Add(e);

                // Phase as a BeginTime delay. The source bakes the offset into
                // each dot's keyframe values instead; a shared curve plus a
                // per-dot delay is the same motion with 1/54th of the data.
                var begin = TimeSpan.FromMilliseconds(i * LagMs);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(begin));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(begin));
            }
        }

        private static DoubleAnimationUsingKeyFrames Pulse(TimeSpan begin)
        {
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = new Duration(Loop),
                BeginTime = begin,
                RepeatBehavior = RepeatBehavior.Forever,
            };
            for (int k = 0; k < Curve.Length; k++)
            {
                // Evenly spaced because the source's keyTimes are evenly spaced
                // — its shaping lives in the values, which is what Curve holds.
                var at = TimeSpan.FromMilliseconds(Loop.TotalMilliseconds * k / Curve.Length);
                anim.KeyFrames.Add(new SplineDoubleKeyFrame(
                    Curve[k], KeyTime.FromTimeSpan(at),
                    // Gentle ease between samples so 18 points read as smooth
                    // motion rather than 18 discrete steps.
                    new KeySpline(0.4, 0.0, 0.6, 1.0)));
            }
            return anim;
        }
    }
}
