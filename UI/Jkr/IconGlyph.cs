using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RevitWebAppSync.UI.Jkr
{
    /// <summary>
    /// Icon set mirrored from design v2 icons.jsx.
    /// Each glyph is a 16x16 stroked path (currentColor-equivalent via Foreground binding).
    /// Usage:  <jkr:IconGlyph Name="bolt" Size="14" Foreground="{StaticResource Brand}"/>
    /// </summary>
    public class IconGlyph : ContentControl
    {
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(IconGlyph),
                new PropertyMetadata(null, OnAnyChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconGlyph),
                new PropertyMetadata(14.0, OnAnyChanged));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(IconGlyph),
                new PropertyMetadata(1.5, OnAnyChanged));

        public static readonly DependencyProperty FilledProperty =
            DependencyProperty.Register(nameof(Filled), typeof(bool), typeof(IconGlyph),
                new PropertyMetadata(false, OnAnyChanged));

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }
        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }
        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }
        public bool Filled
        {
            get => (bool)GetValue(FilledProperty);
            set => SetValue(FilledProperty, value);
        }

        private static void OnAnyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is IconGlyph g) g.Rebuild();
        }

        public IconGlyph()
        {
            Focusable = false;
            IsHitTestVisible = false;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Center;
            SnapsToDevicePixels = true;
        }

        private void Rebuild()
        {
            Width = Size;
            Height = Size;
            if (string.IsNullOrEmpty(Glyph) || !Glyphs.TryGetValue(Glyph, out var data))
            {
                Content = null;
                return;
            }

            var vb = new Viewbox { Width = Size, Height = Size, Stretch = Stretch.Uniform };
            var canvas = new Grid { Width = 16, Height = 16 };

            // Binding Foreground → Stroke
            var fgBinding = new System.Windows.Data.Binding("Foreground")
            {
                Source = this,
                Mode = System.Windows.Data.BindingMode.OneWay
            };

            foreach (var geoSpec in data)
            {
                var path = new Path
                {
                    Data = Geometry.Parse(geoSpec.Data),
                    StrokeThickness = StrokeThickness,
                    StrokeLineJoin = geoSpec.LineJoin,
                    StrokeStartLineCap = geoSpec.LineCap,
                    StrokeEndLineCap = geoSpec.LineCap,
                };
                if (geoSpec.Filled || Filled)
                {
                    path.SetBinding(Shape.FillProperty, fgBinding);
                }
                else
                {
                    path.SetBinding(Shape.StrokeProperty, fgBinding);
                    path.Fill = Brushes.Transparent;
                }
                canvas.Children.Add(path);
            }

            vb.Child = canvas;
            Content = vb;
        }

        private class GeoSpec
        {
            public string Data;
            public bool Filled;
            public PenLineJoin LineJoin = PenLineJoin.Miter;
            public PenLineCap LineCap = PenLineCap.Flat;
        }

        private static GeoSpec Stroke(string d, PenLineCap cap = PenLineCap.Flat, PenLineJoin join = PenLineJoin.Miter)
            => new GeoSpec { Data = d, LineCap = cap, LineJoin = join };
        private static GeoSpec Fill(string d) => new GeoSpec { Data = d, Filled = true };

        // ─────────── GLYPH TABLE (ported from v2/icons.jsx) ───────────
        private static readonly Dictionary<string, GeoSpec[]> Glyphs = new Dictionary<string, GeoSpec[]>
        {
            ["bolt"]   = new[] { Stroke("M9 1 L3 9 H7 L6 15 L13 7 H9 L10 1 Z", join: PenLineJoin.Round) },
            ["check"]  = new[] { Stroke("M3 8.5 L6.5 12 L13 4.5", PenLineCap.Round, PenLineJoin.Round) },
            ["x"]      = new[] {
                Stroke("M4 4 L12 12", PenLineCap.Round),
                Stroke("M12 4 L4 12", PenLineCap.Round),
            },
            ["approve"] = new[] {
                Stroke("M8 2.5 A5.5 5.5 0 1 1 7.99 2.5 Z"),
                Fill("M8 6 A2 2 0 1 1 7.99 6 Z"),
            },
            ["target"]  = new[] {
                Stroke("M8 2 A6 6 0 1 1 7.99 2 Z"),
                Stroke("M8 5 A3 3 0 1 1 7.99 5 Z"),
                Fill("M8 7.2 A0.8 0.8 0 1 1 7.99 7.2 Z"),
            },
            ["crosshair"] = new[] {
                Stroke("M8 2.5 A5.5 5.5 0 1 1 7.99 2.5 Z", PenLineCap.Round),
                Stroke("M8 1 L8 3.5", PenLineCap.Round),
                Stroke("M8 12.5 L8 15", PenLineCap.Round),
                Stroke("M1 8 L3.5 8", PenLineCap.Round),
                Stroke("M12.5 8 L15 8", PenLineCap.Round),
            },
            ["rotate"]  = new[] {
                Stroke("M13 8 A5 5 0 1 1 11.5 4.5", PenLineCap.Round),
                Stroke("M13.5 2 L13 5 L10 4.5", PenLineCap.Round, PenLineJoin.Round),
            },
            ["undo"]    = new[] {
                Stroke("M3.5 5.5 Q7 2 10.5 5.5 A4 4 0 1 1 6.5 11", PenLineCap.Round),
                Stroke("M3.5 2.5 L3.5 5.5 L6.5 5.5", PenLineCap.Round, PenLineJoin.Round),
            },
            ["search"]  = new[] {
                Stroke("M7 2.8 A4.2 4.2 0 1 1 6.99 2.8 Z", PenLineCap.Round),
                Stroke("M10.2 10.2 L13.5 13.5", PenLineCap.Round),
            },
            ["close"]   = new[] {
                Stroke("M4 4 L12 12", PenLineCap.Round),
                Stroke("M12 4 L4 12", PenLineCap.Round),
            },
            ["download"] = new[] {
                Stroke("M8 2 L8 10.5", PenLineCap.Round),
                Stroke("M4.5 7 L8 10.5 L11.5 7", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M3 13.5 L13 13.5", PenLineCap.Round),
            },
            ["arrowUp"] = new[] {
                Stroke("M8 13 L8 3", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M4 7 L8 3 L12 7", PenLineCap.Round, PenLineJoin.Round),
            },
            ["arrowDn"] = new[] {
                Stroke("M8 3 L8 13", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M4 9 L8 13 L12 9", PenLineCap.Round, PenLineJoin.Round),
            },
            ["arrowL"]  = new[] {
                Stroke("M13 8 L3 8", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M7 4 L3 8 L7 12", PenLineCap.Round, PenLineJoin.Round),
            },
            ["arrowR"]  = new[] {
                Stroke("M3 8 L13 8", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M9 4 L13 8 L9 12", PenLineCap.Round, PenLineJoin.Round),
            },
            ["caretDn"] = new[] { Stroke("M4 6 L8 10 L12 6", PenLineCap.Round, PenLineJoin.Round) },
            ["caretR"]  = new[] { Stroke("M6 4 L10 8 L6 12", PenLineCap.Round, PenLineJoin.Round) },
            ["enter"]   = new[] {
                Stroke("M3 8 L6 5 L6 11 Z", PenLineCap.Round, PenLineJoin.Round),
                Stroke("M3 8 H12 V4", PenLineCap.Round, PenLineJoin.Round),
            },
            ["dot"]     = new[] { Fill("M8 5 A3 3 0 1 1 7.99 5 Z") },
            ["book"]    = new[] {
                Stroke("M2.5 3 H7 A1.5 1.5 0 0 1 8 4.5 V13.5 A1.5 1.5 0 0 0 7 12 H2.5 Z", join: PenLineJoin.Round),
                Stroke("M13.5 3 H9 A1.5 1.5 0 0 0 8 4.5 V13.5 A1.5 1.5 0 0 1 9 12 H13.5 Z", join: PenLineJoin.Round),
            },
            ["file"]    = new[] {
                Stroke("M3.5 1.5 H10 L12.5 4 V14.5 H3.5 Z", join: PenLineJoin.Round),
                Stroke("M10 1.5 L10 4 L12.5 4", join: PenLineJoin.Round),
            },
            ["clipboard"] = new[] {
                Stroke("M3 2.5 H13 V14.5 H3 Z", join: PenLineJoin.Round),
                Stroke("M5.5 1 H10.5 V3.5 H5.5 Z", join: PenLineJoin.Round),
                Stroke("M5.5 7 L10.5 7", PenLineCap.Round),
                Stroke("M5.5 9.5 L10.5 9.5", PenLineCap.Round),
                Stroke("M5.5 12 L8.5 12", PenLineCap.Round),
            },
            ["pin"]     = new[] {
                Stroke("M8 14.5 C8 14.5 3 9.5 3 6.5 A5 5 0 0 1 13 6.5 C13 9.5 8 14.5 8 14.5 Z", join: PenLineJoin.Round),
                Stroke("M8 4.7 A1.8 1.8 0 1 1 7.99 4.7 Z"),
            },
            ["grid"]    = new[] {
                Stroke("M2.5 2.5 H13.5 V13.5 H2.5 Z"),
                Stroke("M6 2.5 L6 13.5"),
                Stroke("M10 2.5 L10 13.5"),
                Stroke("M2.5 6 L13.5 6"),
                Stroke("M2.5 10 L13.5 10"),
            },
            ["levels"]  = new[] {
                Stroke("M2 4 L14 4", PenLineCap.Round),
                Stroke("M2 8 L14 8", PenLineCap.Round),
                Stroke("M2 12 L14 12", PenLineCap.Round),
            },
            ["tag"]     = new[] {
                Stroke("M8.5 2 L14 2 V7.5 L7.5 14 L2 8.5 Z", join: PenLineJoin.Round),
                Fill("M11.5 3.6 A0.9 0.9 0 1 1 11.49 3.6 Z"),
            },
            ["gear"]    = new[] {
                Stroke("M8 6 A2 2 0 1 1 7.99 6 Z", join: PenLineJoin.Round),
                Stroke("M8 1.5 V3.5 M8 12.5 V14.5 M1.5 8 H3.5 M12.5 8 H14.5 M3.3 3.3 L4.7 4.7 M11.3 11.3 L12.7 12.7 M3.3 12.7 L4.7 11.3 M11.3 4.7 L12.7 3.3"),
            },
            ["box"]     = new[] {
                Stroke("M8 2 L13.5 5 L13.5 11 L8 14 L2.5 11 L2.5 5 Z", join: PenLineJoin.Round),
                Stroke("M2.5 5 L8 8 L13.5 5", join: PenLineJoin.Round),
                Stroke("M8 8 L8 14"),
            },
            ["diamond"] = new[] { Stroke("M8 2 L14 8 L8 14 L2 8 Z", join: PenLineJoin.Round) },
            ["sparkle"] = new[] { Stroke("M8 2 L9 7 L14 8 L9 9 L8 14 L7 9 L2 8 L7 7 Z", join: PenLineJoin.Round) },
        };
    }
}
