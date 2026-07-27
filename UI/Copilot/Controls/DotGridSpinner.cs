using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// A 3×3 grid of dots that shimmer in a diagonal wave — the loading
    /// indicator for the live turn.
    ///
    /// Replaces the spinning arc, which read as a generic "busy" throbber: it
    /// span at a fixed rate whatever was happening, and with one arc per trail
    /// row a turn could show two or three of them turning at once (UAT
    /// 2026-07-27). A dot matrix carries the same "working" signal while
    /// staying still enough to sit beside text without pulling the eye off it.
    ///
    /// Every dot animates its own Opacity with a staggered BeginTime, so the
    /// wave travels top-left to bottom-right. Animations are started with
    /// direct BeginAnimation calls and never a XAML Storyboard — a Storyboard
    /// inside a Revit dockable pane crashes (same constraint the arc spinner
    /// documented).
    /// </summary>
    public class DotGridSpinner : Grid
    {
        private const int N = 3;                  // 3×3
        private const double Dot = 2.6;           // dot diameter, px
        private const double Gap = 2.2;           // space between dots, px
        // One full shimmer per dot. Long enough to read as breathing rather
        // than blinking; the stagger below is what makes it a wave.
        private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(1100);
        // Delay between diagonals. 5 diagonals × 110ms trails just under one
        // period, so the wave loops continuously with no visible gap.
        private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(110);

        public DotGridSpinner()
        {
            Width = N * Dot + (N - 1) * Gap;
            Height = Width;
            HorizontalAlignment = HorizontalAlignment.Center;
            VerticalAlignment = VerticalAlignment.Center;
            SnapsToDevicePixels = true;

            for (int i = 0; i < N; i++)
            {
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            for (int r = 0; r < N; r++)
            {
                for (int c = 0; c < N; c++)
                {
                    var dot = new Ellipse
                    {
                        Width = Dot,
                        Height = Dot,
                        Margin = new Thickness(c == 0 ? 0 : Gap, r == 0 ? 0 : Gap, 0, 0),
                        // Start dim; the animation lifts each dot in turn.
                        Opacity = 0.25,
                    };
                    dot.SetResourceReference(Shape.FillProperty, "Cp.Accent");
                    Grid.SetRow(dot, r);
                    Grid.SetColumn(dot, c);
                    Children.Add(dot);

                    // Diagonal wave: cells on the same anti-diagonal (r+c) lift
                    // together, giving a sweep rather than nine independent
                    // blinks. AutoReverse fades each dot back down.
                    var pulse = new DoubleAnimation(0.25, 1.0, new Duration(Period))
                    {
                        BeginTime = TimeSpan.FromMilliseconds((r + c) * Stagger.TotalMilliseconds),
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    };
                    dot.BeginAnimation(UIElement.OpacityProperty, pulse);
                }
            }
        }
    }
}
