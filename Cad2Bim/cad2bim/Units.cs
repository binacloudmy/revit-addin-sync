using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

            double diagonal = Diagonal(segments);
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

        /// <summary>A building is somewhere between a shed and an airport.</summary>
        private const double SmallestPlanMm = 3_000.0;
        private const double LargestPlanMm = 1_000_000.0;

        /// <summary>The narrower size band a header has to miss before door swings may
        /// overrule it: a plan this size is believable whatever the swings say.</summary>
        private const double CommonPlanMinMm = 5_000.0;
        private const double CommonPlanMaxMm = 200_000.0;

        /// <summary>A door leaf is 600..1200 mm wide whatever the drawing's unit.</summary>
        private const double DoorLeafMinMm = 600.0;
        private const double DoorLeafMaxMm = 1_200.0;

        /// <summary>Door swings a scale needs before they count as evidence for it, and how
        /// many times the runner-up's count it needs to be a clear winner.</summary>
        private const int DoorVotesNeeded = 3;
        private const double DoorVotesLead = 2.0;

        /// <summary>From this many segments up, the extents are taken between the 1st and 99th
        /// percentile of the segment midpoints, so a stray line far off the sheet does not
        /// set the size of the drawing.</summary>
        private const int RobustExtentsMinSegments = 200;

        /// <summary>
        /// The header's scale when it survives a sanity check, an inferred one otherwise.
        ///
        /// Drawings lie about their units. A house drawn in millimetres was found declaring
        /// itself in inches, which multiplies every length by 25.4 and turns a 90 metre sheet
        /// into a 2.3 kilometre one - and then nothing is wall-thickness any more, so four
        /// walls come back out of a house. The header is a claim, not a measurement.
        ///
        /// So the claim is checked against the drawing: its size, and its door swings - a
        /// quarter arc whose radius is a door leaf (600..1200 mm) at one scale and nothing
        /// plausible at the others is a measurement of the unit. Furniture fillets are quarter
        /// arcs too, so swings only overrule the size when one scale wins them clearly.
        /// </summary>
        public static double Resolve(CadDocument document, IReadOnlyList<Segment> segments,
                                     IReadOnlyList<Arc>? arcs = null) =>
            Resolve(FromHeader(document), segments, arcs);

        /// <summary>
        /// <see cref="Resolve(CadDocument, IReadOnlyList{Segment}, IReadOnlyList{Arc})"/> on the
        /// header's scale alone (null when the header has none).
        ///
        /// 1. No header: the clear door-swing winner if it makes the drawing building-sized,
        ///    otherwise the size guess (<see cref="InferScale"/>).
        /// 2. Header makes the drawing building-sized: keep it, unless no swing agrees with it,
        ///    another scale wins the swings clearly, and only that scale puts the drawing in the
        ///    common 5..200 m band.
        /// 3. Header does not: keep it if it is the clear door-swing winner, otherwise the
        ///    clear swing winner if that makes the drawing building-sized, otherwise the size
        ///    guess.
        /// </summary>
        internal static double Resolve(double? stated, IReadOnlyList<Segment> segments,
                                       IReadOnlyList<Arc>? arcs) {
            double diagonal = Diagonal(segments);
            double? winner = DoorSwingWinner(arcs);

            if (stated is null) {
                if (winner is double guess && InBand(diagonal * guess, SmallestPlanMm, LargestPlanMm)) return guess;
                return InferScale(segments);
            }

            double header = stated.Value;
            int headerVotes = DoorSwingVotes(arcs, header);

            if (InBand(diagonal * header, SmallestPlanMm, LargestPlanMm)) {
                if (headerVotes == 0 && winner is double swings &&
                    !InBand(diagonal * header, CommonPlanMinMm, CommonPlanMaxMm) &&
                    InBand(diagonal * swings, CommonPlanMinMm, CommonPlanMaxMm)) {
                    return swings;
                }
                return header;
            }

            // Only as the clear swing winner: three fillets at the header's scale must not beat more swings at another.
            // shortcut: a header outside PlausibleScales (yards, dm) can no longer be kept by swings here, add it to the vote if one turns up.
            if (winner == header) return header;
            if (winner is double clear && InBand(diagonal * clear, SmallestPlanMm, LargestPlanMm)) return clear;
            return InferScale(segments);
        }

        private static bool InBand(double value, double min, double max) => value >= min && value <= max;

        /// <summary>Arcs that would be door swings - a 60..120 degree sweep and a leaf-width
        /// radius - if the drawing were in <paramref name="scale"/> millimetres per unit.</summary>
        private static int DoorSwingVotes(IReadOnlyList<Arc>? arcs, double scale) {
            if (arcs is null) return 0;

            int votes = 0;
            foreach (Arc arc in arcs) {
                double sweep = arc.SweepDegrees;
                if (sweep < CadClassifier.SwingMinSweepDegrees || sweep > CadClassifier.SwingMaxSweepDegrees) continue;

                double radius = arc.Radius * scale;
                if (radius >= DoorLeafMinMm && radius <= DoorLeafMaxMm) votes++;
            }
            return votes;
        }

        /// <summary>The plausible scale the door swings clearly pick, or null: at least
        /// <see cref="DoorVotesNeeded"/> swings and twice the runner-up's count. Not a plain
        /// majority - a sheet of furniture fillets votes for the wrong unit.</summary>
        private static double? DoorSwingWinner(IReadOnlyList<Arc>? arcs) {
            if (arcs is null || arcs.Count == 0) return null;

            double best = 0;
            int bestVotes = 0, runnerUp = 0;
            foreach (double scale in PlausibleScales) {
                int votes = DoorSwingVotes(arcs, scale);
                if (votes > bestVotes) {
                    runnerUp = bestVotes;
                    bestVotes = votes;
                    best = scale;
                } else if (votes > runnerUp) {
                    runnerUp = votes;
                }
            }

            if (bestVotes < DoorVotesNeeded || bestVotes < DoorVotesLead * runnerUp) return null;
            return best;
        }

        /// <summary>The drawing's diagonal in drawing units: the plain bounding box for a small
        /// drawing, the 1st..99th percentile box of segment midpoints for a large one.</summary>
        private static double Diagonal(IReadOnlyList<Segment> segments) {
            if (segments.Count == 0) return 0;

            double width, height;
            if (segments.Count >= RobustExtentsMinSegments) {
                var xs = new double[segments.Count];
                var ys = new double[segments.Count];
                for (int i = 0; i < segments.Count; i++) {
                    xs[i] = (segments[i].P1.x + segments[i].P2.x) / 2;
                    ys[i] = (segments[i].P1.y + segments[i].P2.y) / 2;
                }
                Array.Sort(xs);
                Array.Sort(ys);

                int low = (int)Math.Floor(0.01 * (segments.Count - 1));
                int high = (int)Math.Ceiling(0.99 * (segments.Count - 1));
                width = xs[high] - xs[low];
                height = ys[high] - ys[low];
            } else {
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

                width = maxX - minX;
                height = maxY - minY;
            }

            return Math.Sqrt((width * width) + (height * height));
        }

        /// <summary>True when the file states a unit and <paramref name="scale"/> is not it -
        /// <see cref="Resolve(CadDocument, IReadOnlyList{Segment}, IReadOnlyList{Arc})"/>
        /// did not believe the header.</summary>
        public static bool HeaderDisbelieved(CadDocument document, double scale) =>
            FromHeader(document) is double header && Math.Abs(header - scale) > 1e-9;

        /// <summary>
        /// The units line the CAD to BIM pane shows: "1 unit = 1 mm · header" when the header
        /// was believed, "· inferred" when there was none, and the header's claim beside the
        /// scale actually used when it was overruled - a drawing that came out 1000x off is
        /// otherwise silent about why.
        /// </summary>
        public static string Describe(CadDocument document, double scale) {
            string mm = "1 unit = " + scale.ToString("0.###") + " mm";

            if (FromHeader(document) is null) return mm + " · inferred";
            if (!HeaderDisbelieved(document, scale)) return mm + " · header";

            string? name = ScaleName(scale);
            return mm + (name is null ? "" : " (" + name + ")") +
                   " · header said " + document.Header.InsUnits + ", geometry says otherwise";
        }

        private static string? ScaleName(double scale) => scale switch {
            1.0 => "millimetres",
            10.0 => "centimetres",
            25.4 => "inches",
            304.8 => "feet",
            1_000.0 => "metres",
            _ => null,
        };

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
