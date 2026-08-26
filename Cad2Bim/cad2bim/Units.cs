using ACadSharp;
using ACadSharp.Types.Units;

namespace Cad2Bim {
    /// <summary>
    /// Drawing units to millimetres.
    ///
    /// Why this exists: Wall.SMin / Wall.SMax are plain numbers, so they only mean anything
    /// once the geometry they are compared against is in a known unit. The defaults (0.05 /
    /// 0.40) are metres; run them against a drawing authored in millimetres and the pairing
    /// finds only near-coincident linework — 26 "walls" out of 4076 segments on test.dwg,
    /// every one of them noise. Normalising to millimetres first makes one threshold pair
    /// correct for every drawing: the same file then yields 1481 walls at a 132 mm median.
    ///
    /// Millimetres, not metres, because every model-visible length in the copilot is already
    /// millimetres.
    /// </summary>
    public static class Units {
        public const double DefaultMinWallThicknessMm = 50.0;
        public const double DefaultMaxWallThicknessMm = 400.0;

        /// <summary>Typical extent of a floor-plan drawing, used only to guess unitless files.</summary>
        private const double TypicalPlanDiagonalMm = 30_000.0;

        private static readonly double[] PlausibleScales = [
            1.0,        // millimetres
            10.0,       // centimetres
            25.4,       // inches
            304.8,      // feet
            1_000.0,    // metres
        ];

        /// <summary>
        /// Millimetres per drawing unit, from the file header. Returns null when the header
        /// says Unitless — callers should fall back to <see cref="InferScale"/>, because a
        /// wrong guess is better made against real geometry than against a missing field.
        /// </summary>
        public static double? FromHeader(CadDocument document) => document.Header.InsUnits switch {
            UnitsType.Millimeters => 1.0,
            UnitsType.Centimeters => 10.0,
            UnitsType.Decimeters => 100.0,
            UnitsType.Meters => 1_000.0,
            UnitsType.Decameters => 10_000.0,
            UnitsType.Hectometers => 100_000.0,
            UnitsType.Kilometers => 1_000_000.0,
            UnitsType.Microns => 0.001,
            UnitsType.Nanometers => 0.000_001,
            UnitsType.Angstroms => 0.000_000_1,
            UnitsType.Microinches => 0.000_025_4,
            UnitsType.Mils => 0.025_4,
            UnitsType.Inches => 25.4,
            UnitsType.Feet or UnitsType.USSurveyFeet => 304.8,
            UnitsType.Yards => 914.4,
            UnitsType.Miles => 1_609_344.0,
            _ => null,
        };

        /// <summary>
        /// Best-guess scale for a drawing whose header carries no unit: pick the plausible
        /// scale that puts the drawing's diagonal closest to the size of a real floor plan.
        /// Deliberately coarse — it separates millimetres from metres from feet, which is all
        /// that is needed to keep a wall-thickness threshold meaningful.
        /// </summary>
        public static double InferScale(IReadOnlyList<Segment> segments) {
            if (segments.Count == 0) return 1.0;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (Segment segment in segments) {
                foreach (Point point in segment.Points) {
                    if (point.x < minX) minX = point.x;
                    if (point.y < minY) minY = point.y;
                    if (point.x > maxX) maxX = point.x;
                    if (point.y > maxY) maxY = point.y;
                }
            }

            double width = maxX - minX;
            double height = maxY - minY;
            double diagonal = Math.Sqrt((width * width) + (height * height));
            if (diagonal <= 0) return 1.0;

            double best = 1.0;
            double bestError = double.MaxValue;

            foreach (double scale in PlausibleScales) {
                // Compare in log space so 10x too small and 10x too big score equally.
                double error = Math.Abs(Math.Log(diagonal * scale / TypicalPlanDiagonalMm));
                if (error < bestError) {
                    bestError = error;
                    best = scale;
                }
            }

            return best;
        }

        /// <summary>Header scale when the file states one, inferred scale otherwise.</summary>
        public static double Resolve(CadDocument document, IReadOnlyList<Segment> segments)
            => FromHeader(document) ?? InferScale(segments);

        /// <summary>
        /// Restates the loaded geometry in millimetres. Scaling the drawing rather than the
        /// thresholds keeps every downstream number — wall thickness, opening width, junction
        /// tolerance, room area — in one unit, so a constant means the same thing whichever
        /// file it is applied to.
        /// </summary>
        public static (List<GeometryElement> Geometry, List<TextElement> Text) Normalize(
                IReadOnlyList<GeometryElement> geometry, IReadOnlyList<TextElement> text, double scale) {

            var scaledGeometry = new List<GeometryElement>(geometry.Count);
            var scaledText = new List<TextElement>(text.Count);

            if (scale == 1.0) {
                scaledGeometry.AddRange(geometry);
                scaledText.AddRange(text);
                return (scaledGeometry, scaledText);
            }

            Point At(Point p) => new(p.x * scale, p.y * scale);

            foreach (GeometryElement element in geometry) {
                switch (element) {
                    case Segment segment:
                        scaledGeometry.Add(new Segment(At(segment.P1), At(segment.P2)));
                        break;

                    case Arc arc:
                        scaledGeometry.Add(new Arc {
                            Center = At(arc.Center),
                            Radius = arc.Radius * scale,
                            StartAngle = arc.StartAngle,
                            EndAngle = arc.EndAngle,
                        });
                        break;
                }
            }

            foreach (TextElement item in text) {
                scaledText.Add(new TextElement {
                    P1 = At(item.P1),
                    P2 = At(item.P2),
                    Text = item.Text,
                });
            }

            return (scaledGeometry, scaledText);
        }
    }
}
