// BuildSizing — the decisions Cad2BimBuilder makes about types and geometry, kept free of
// Revit types so they can be unit-tested (the Tests project's RevitAPI is metadata-only).
//
//   which wall type a detected thickness gets, and what a new one is called;
//   door or window, and the width/height its family type must have;
//   which existing type matches a size, or which one to duplicate when none does;
//   where a door actually sits (the swing's hinge is a jamb, not the middle);
//   how far a wall end moves to meet the wall it runs into, so Revit can join them.
//
// Millimetres throughout, like the classifier.

// Nullable off: the add-in builds without it, the headless CLI (which links this file) with it.
#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Cad2Bim;

namespace RevitWebAppSync.Services.CadToBim
{
    public enum OpeningKind
    {
        Door,
        Window,
    }

    /// <summary>How far the builder turns a placed door toward its drawn swing. A door Revit
    /// deletes once turned ("can't cut instance out of its host") is tried again one step
    /// weaker: Full, then FacingOnly (no hand flip), then None (left as placed).</summary>
    public enum DoorTurn
    {
        Full,
        FacingOnly,
        None,
    }

    /// <summary>One type the model already has, as far as sizing cares. HeightMm is NaN for
    /// wall types. Resizable = its width (and height) can be written after a Duplicate.</summary>
    public readonly struct SizeCandidate
    {
        public SizeCandidate(string name, double widthMm, double heightMm, bool resizable)
        {
            Name = name ?? string.Empty;
            WidthMm = widthMm;
            HeightMm = heightMm;
            Resizable = resizable;
        }

        public string Name { get; }
        public double WidthMm { get; }
        public double HeightMm { get; }
        public bool Resizable { get; }
    }

    /// <summary>What to do for one wanted size. Index = -1: nothing to use at all.
    /// Exact: use candidates[Index] as it is. Otherwise duplicate candidates[Index] and resize
    /// it when Resizable, or use it unsized when nothing in the model can be resized.</summary>
    public readonly struct SizeChoice
    {
        public SizeChoice(int index, bool exact)
        {
            Index = index;
            Exact = exact;
        }

        public int Index { get; }
        public bool Exact { get; }
        public bool Found => Index >= 0;
    }

    /// <summary>Which way a drawn door opens, in plan (BuildSizing.SwingOf). Open = unit
    /// vector square to the wall, toward the side the leaf swings into. Hinge = unit vector
    /// along the wall, from the strike jamb toward the hinge jamb (the arc's centre). Known
    /// false: the drawing does not say clearly, and the door is left as Revit places it.</summary>
    public readonly struct DoorSwing
    {
        public DoorSwing(Point hinge, Point strike, double openX, double openY, double hingeX, double hingeY)
        {
            Known = true;
            Why = null;
            HingePoint = hinge;
            Strike = strike;
            OpenX = openX;
            OpenY = openY;
            HingeX = hingeX;
            HingeY = hingeY;
        }

        private DoorSwing(string why)
        {
            Known = false;
            Why = why;
            HingePoint = null;
            Strike = null;
            OpenX = OpenY = HingeX = HingeY = 0;
        }

        public static DoorSwing Unknown(string why) => new DoorSwing(why);

        public bool Known { get; }
        public string Why { get; }
        public Point HingePoint { get; }
        public Point Strike { get; }
        public double OpenX { get; }
        public double OpenY { get; }
        public double HingeX { get; }
        public double HingeY { get; }
    }

    public static class BuildSizing
    {
        /// <summary>Detected thicknesses are rounded to this before a type is chosen. Traced
        /// faces are never exactly parallel, so 198.7 and 201.2 are the same 200 mm wall.</summary>
        public const double WallStepMm = 5.0;

        /// <summary>Door and window widths and heights are rounded to this.</summary>
        public const double OpeningStepMm = 10.0;

        /// <summary>Two walls meet when they cross at more than this; below it they are one run
        /// with a gap, which is a doorway and must stay open.</summary>
        public const double CornerMinAngleDegrees = 20.0;

        /// <summary>How far past half the other wall's thickness an end may move to meet it.
        /// Covers drafting slop at corners without reaching across a doorway.</summary>
        public const double CornerReachMm = 100.0;

        /// <summary>Openings narrower than this are skipped by the builder (SkipReason.TooNarrow).</summary>
        public const double MinOpeningWidthMm = 300.0;

        /// <summary>A wall end never moves so far that the wall keeps less than this.</summary>
        public const double MinWallLengthMm = 50.0;

        // ─── walls ───────────────────────────────────────────────────────

        public static double Snap(double mm, double step) =>
            step > 0 ? Math.Round(mm / step, MidpointRounding.AwayFromZero) * step : mm;

        public static double WallWidthMm(double detectedMm) => Snap(detectedMm, WallStepMm);

        public static string WallTypeName(double widthMm) =>
            "CAD Wall " + Mm(widthMm) + " mm";

        // ─── doors and windows ───────────────────────────────────────────

        /// <summary>A swing is the drawing saying "door". Anything else the symbol reader found
        /// came from window linework, so it is a window.</summary>
        public static OpeningKind KindOf(Opening opening) =>
            opening != null && opening.IsDoor ? OpeningKind.Door : OpeningKind.Window;

        /// <summary>The family size an opening asks for: its drawn width, and the height the
        /// settings give its kind (a plan carries no heights).</summary>
        public static (double WidthMm, double HeightMm) OpeningSize(
            OpeningKind kind, double widthMm, double doorHeightMm, double windowHeightMm) =>
            (Snap(widthMm, OpeningStepMm),
             Snap(kind == OpeningKind.Door ? doorHeightMm : windowHeightMm, OpeningStepMm));

        public static string OpeningTypeName(double widthMm, double heightMm) =>
            "CAD " + Mm(widthMm) + " x " + Mm(heightMm) + " mm";

        // ─── picking a type ──────────────────────────────────────────────

        /// <summary>
        /// The type to use for a wanted size. A candidate within tolerance on every dimension
        /// is used as it is (closest first, then by name, so the pick is stable). Otherwise the
        /// closest resizable candidate is the one to duplicate; failing that, the closest of
        /// all, used unsized. heightMm = NaN compares widths only (wall types).
        /// </summary>
        public static SizeChoice Pick(
            IReadOnlyList<SizeCandidate> candidates, double widthMm, double heightMm, double toleranceMm)
        {
            if (candidates == null || candidates.Count == 0) return new SizeChoice(-1, false);

            bool withHeight = !double.IsNaN(heightMm);

            // A size the model could not read ranks last, never first (NaN compares below all).
            double Error(SizeCandidate c)
            {
                double error = double.IsNaN(c.WidthMm) ? 1e6 : Math.Abs(c.WidthMm - widthMm);
                if (withHeight) error += double.IsNaN(c.HeightMm) ? 1e6 : Math.Abs(c.HeightMm - heightMm);
                return error;
            }

            bool Fits(SizeCandidate c) =>
                Math.Abs(c.WidthMm - widthMm) <= toleranceMm &&
                (!withHeight || (!double.IsNaN(c.HeightMm) && Math.Abs(c.HeightMm - heightMm) <= toleranceMm));

            int Best(Func<SizeCandidate, bool> allowed)
            {
                int best = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!allowed(candidates[i])) continue;
                    if (best < 0) { best = i; continue; }

                    int byError = Error(candidates[i]).CompareTo(Error(candidates[best]));
                    if (byError < 0 ||
                        (byError == 0 && string.CompareOrdinal(candidates[i].Name, candidates[best].Name) < 0))
                    {
                        best = i;
                    }
                }
                return best;
            }

            int exact = Best(Fits);
            if (exact >= 0) return new SizeChoice(exact, true);

            int source = Best(c => c.Resizable);
            if (source >= 0) return new SizeChoice(source, false);

            return new SizeChoice(Best(_ => true), false);
        }

        /// <summary>The name as given, or with " (2)", " (3)"… when the model already uses it
        /// for a type of a different size.</summary>
        public static string UniqueName(string wanted, ICollection<string> taken)
        {
            if (taken == null || !taken.Contains(wanted)) return wanted;
            for (int suffix = 2; ; suffix++)
            {
                string name = wanted + " (" + suffix + ")";
                if (!taken.Contains(name)) return name;
            }
        }

        // ─── where a door sits ───────────────────────────────────────────

        /// <summary>The width a family type should have. For a door that is the swing's radius
        /// (the leaf): the reader clips its jambs to the host wall's ends, and a door hinged at
        /// the end of a wall piece - the usual case, the wall stops at the doorway - comes back
        /// a fraction of its width, sometimes zero.</summary>
        public static double OpeningWidthMm(Opening opening)
        {
            Arc swing = opening?.Geometry.OfType<Arc>().FirstOrDefault();
            if (opening != null && opening.IsDoor && swing != null && swing.Radius > 0) return swing.Radius;
            return opening?.Width ?? 0;
        }

        /// <summary>Where the family instance goes: a door's leaf centre, a window's middle.</summary>
        public static Point OpeningCentre(Opening opening) =>
            opening != null && opening.IsDoor ? DoorCentre(opening) : opening?.Position;

        /// <summary>
        /// The middle of a door, on its wall's centreline. The symbol reader centres a door on
        /// the swing's hinge, but the hinge is one jamb: the leaf closes along the wall to the
        /// other one. Of the arc's two ends, the one lying on the wall is the closed leaf, so
        /// the door spans hinge to that end. That end is SwingOf's strike when the swing reads
        /// clearly, so the door's position and the way it is turned always use the same leaf.
        /// Otherwise the older test below picks it, and when neither end clearly lies on the
        /// wall the reader's own position is kept.
        /// </summary>
        public static Point DoorCentre(Opening door)
        {
            if (door == null) return null;
            Arc swing = door.Geometry.OfType<Arc>().FirstOrDefault();
            Segment line = door.Wall?.Centerline;
            if (swing == null || line == null || line.Length <= 0) return door.Position;

            double hinge = Along(line, swing.Center, out _);

            DoorSwing read = SwingOf(door);
            if (read.Known) return At(line, (hinge + Along(line, read.Strike, out _)) / 2.0);

            double atStart = Along(line, swing.StartPoint, out double offStart);
            double atEnd = Along(line, swing.EndPoint, out double offEnd);

            // The closed leaf lies along the wall; the open one stands off it by about a leaf.
            double strike;
            if (offStart < offEnd * 0.5) strike = atStart;
            else if (offEnd < offStart * 0.5) strike = atEnd;
            else return door.Position;

            return At(line, (hinge + strike) / 2.0);
        }

        // ─── which way a door swings ─────────────────────────────────────

        /// <summary>
        /// Revit's door convention, in one place. +1: the leaf of a placed door opens toward
        /// its FacingOrientation, and its hinge is at the HandOrientation end of the opening.
        /// -1 on either reverses that half of the rule. Families differ, so this is what a
        /// single Revit test confirms or flips (the build counts doors it could not turn in
        /// BuildReport.DoorsNotTurned, but cannot see a family that swings the "other" way).
        ///
        /// Why +1 / +1 (Autodesk's own door content, M_Door-* and Door-*):
        /// - FacingOrientation is the family's +Y in plan, HandOrientation its +X; flipFacing
        ///   reverses only the first, flipHand only the second (Micrographics, "Revit Door Hand
        ///   &amp; Facing Orientations"; Revit API guide, FamilyInstances: "The flip operation is
        ///   like a mirror transformation").
        /// - The door template puts the exterior (To Room) above the wall, on +Y, and the
        ///   international library draws its swings on that side (RevitCity thread 21748:
        ///   "international Revit library has door swings on the exterior"; Revit sets the
        ///   room on the swing side as To Room). So the leaf opens toward FacingOrientation.
        /// - Unflipped, the family is a "left hand reverse" door (Ideate: HandFlipped False =
        ///   left hand, FacingFlipped False = reverse). Reverse = it swings toward the person on
        ///   the exterior side; left hand = hinged on their left. Standing on +Y looking at the
        ///   door (toward -Y), the left is +X: the hinge is at the HandOrientation end.
        /// - The Revit 2027 test on demo-plan.dxf (M_Door-Exterior-Single-Two_Lite, nothing
        ///   flipped) is consistent with it: the wall is drawn bottom to top, so its exterior
        ///   (Wall.Orientation, left of the drawn direction) is -X, and the door swung -X. That
        ///   test alone cannot tell "faces the exterior, swings toward facing" from "faces the
        ///   interior, swings away from it"; the sources above decide it. The hinge side was
        ///   not checked there.
        /// To check in Revit: after a build, the demo-plan door must open into the right-hand
        /// room (+X) with its hinge at the lower jamb. Opens left = set SwingAlongFacing to -1;
        /// hinge at the upper jamb = set HingeAlongHand to -1. Each build appends one line per
        /// door to %LOCALAPPDATA%\Bina\RevitSync\logs\cad-to-bim.log (DoorTurnLine) with the
        /// facing/hand as placed and the flips applied, which is enough to decide both.
        /// A type the drafter picks in the TYPES section may come from a non-Autodesk family
        /// that follows the other convention; its doors would be turned exactly wrong, with
        /// no warning (the log shows the family type per door).
        /// </summary>
        public const int SwingAlongFacing = +1;

        /// <inheritdoc cref="SwingAlongFacing"/>
        public const int HingeAlongHand = +1;

        /// <summary>How far off the wall a swing's open leaf must stand for SwingOf to read
        /// it. Below this the arc is too flat to say which side it opens to.</summary>
        public const double SwingMinOpenDegrees = 20.0;

        /// <summary>
        /// Which way a door's swing opens, read from the drawing. Two ends of the swing arc:
        /// one lies on the wall (the closed leaf, at the strike jamb), the other stands about
        /// a leaf off it (the open leaf). The open end's side of the wall is the side the door
        /// opens into; the arc's centre is the hinge jamb. Actual drawn end points are used,
        /// not the arc's mid angle, so an arc whose angles are stored clockwise, negative or
        /// past a full turn gives the same answer.
        ///
        /// Known gap: doors inside a mirrored (reflected) block insert never get here. The
        /// reader keeps an arc as an arc only under a rotation and uniform scale
        /// (CadRenderSource Xform.TryConformal) and turns a reflected one into chords, so such
        /// a door is not detected at all.
        ///
        /// Each leaf of a double door is its own swing and its own door instance, centred on
        /// its own leaf (DoorCentre), so each is read and turned on its own.
        ///
        /// Unknown (left as Revit places it) when there is no arc, no host line, or the arc
        /// does not show a closed and an open leaf clearly: the open leaf must stand at least
        /// SwingMinOpenDegrees off the wall (a door drawn 30° open is read; 15° is not).
        /// </summary>
        public static DoorSwing SwingOf(Opening door)
        {
            Arc swing = door?.Geometry.OfType<Arc>().FirstOrDefault();
            Segment line = door?.Wall?.Centerline;
            if (swing == null || swing.Radius <= 0) return DoorSwing.Unknown("no swing arc");
            if (line == null || line.Length <= 0) return DoorSwing.Unknown("no host wall line");

            double length = line.Length;
            double ux = (line.P2.x - line.P1.x) / length;
            double uy = (line.P2.y - line.P1.y) / length;
            Point hinge = swing.Center;

            // Each end relative to the hinge: along the wall, and across it (signed, + = left of P1→P2).
            (double Along, double Across) Rel(Point p) =>
                (((p.x - hinge.x) * ux) + ((p.y - hinge.y) * uy),
                 ((p.y - hinge.y) * ux) - ((p.x - hinge.x) * uy));

            var a = Rel(swing.StartPoint);
            var b = Rel(swing.EndPoint);

            // The closed leaf lies along the wall, the open one across it, both by most of a leaf.
            double r = swing.Radius;
            bool aClosed = Math.Abs(a.Across) < Math.Abs(b.Across) * 0.5;
            bool bClosed = Math.Abs(b.Across) < Math.Abs(a.Across) * 0.5;
            if (!aClosed && !bClosed) return DoorSwing.Unknown("arc does not show a closed leaf");

            var closed = aClosed ? a : b;
            var open = aClosed ? b : a;
            if (Math.Abs(closed.Along) < r * 0.5) return DoorSwing.Unknown("closed leaf does not run along the wall");
            if (Math.Abs(open.Across) < r * Math.Sin(SwingMinOpenDegrees * Math.PI / 180.0))
                return DoorSwing.Unknown("open leaf does not stand off the wall");

            double side = Math.Sign(open.Across);      // + = left of the wall's P1→P2
            double toHinge = -Math.Sign(closed.Along); // + = hinge is further along P1→P2 than the strike

            Point strike = aClosed ? swing.StartPoint : swing.EndPoint;
            return new DoorSwing(hinge, strike, -uy * side, ux * side, ux * toHinge, uy * toHinge);
        }

        /// <summary>
        /// What to flip on a placed door so it opens the drawn way. facing/hand are the
        /// instance's FacingOrientation and HandOrientation in plan (after a regenerate); the
        /// convention (SwingAlongFacing, HingeAlongHand) says where its leaf and hinge are
        /// for those. A vector square to the wall (dot ~ 0) is read as "don't know": no flip.
        /// </summary>
        public static (bool FlipFacing, bool FlipHand) DoorFlips(
            DoorSwing want, double facingX, double facingY, double handX, double handY)
        {
            if (!want.Known) return (false, false);

            double opens = SwingAlongFacing * ((facingX * want.OpenX) + (facingY * want.OpenY));
            double hinge = HingeAlongHand * ((handX * want.HingeX) + (handY * want.HingeY));

            const double square = 0.1;
            return (opens < -square, hinge < -square);
        }

        /// <summary>The next weaker turn: Full -> FacingOnly -> None, and None stays None.</summary>
        public static DoorTurn Weaker(DoorTurn turn) =>
            turn == DoorTurn.Full ? DoorTurn.FacingOnly : DoorTurn.None;

        /// <summary>The flips DoorFlips asked for, cut down to what a turn allows: Full keeps
        /// both, FacingOnly drops the hand flip, None drops both.</summary>
        public static (bool FlipFacing, bool FlipHand) Limit((bool FlipFacing, bool FlipHand) flips, DoorTurn turn)
        {
            switch (turn)
            {
                case DoorTurn.Full: return flips;
                case DoorTurn.FacingOnly: return (flips.FlipFacing, false);
                default: return (false, false);
            }
        }

        /// <summary>
        /// One line per turned door for cad-to-bim.log, so one build settles the convention
        /// without RevitLookup: the family type, the instance's facing and hand as placed
        /// (before any flip), what the drawing wants, which flips were applied, and whether
        /// the final check passed. If the demo-plan door then opens the wrong way, the line
        /// shows which constant to change. A type picked in the TYPES section from a
        /// non-Autodesk family may follow a different convention; its lines show that too.
        /// </summary>
        public static string DoorTurnLine(
            string type, double facingX, double facingY, double handX, double handY,
            DoorSwing want, bool flipFacing, bool flipHand, string result)
        {
            // Rounded first, so -0.0 and 1e-17 print as 0.
            string N(double v)
            {
                double r = Math.Round(v, 3);
                return (r == 0 ? 0.0 : r).ToString("0.###", CultureInfo.InvariantCulture);
            }
            string V(double x, double y) => "(" + N(x) + "," + N(y) + ")";
            string P(Point p) => p == null ? "?" :
                "(" + Mm(p.x) + "," + Mm(p.y) + ")";

            return "door " + (type ?? "?") +
                   " hinge " + P(want.HingePoint) +
                   " | placed facing " + V(facingX, facingY) + " hand " + V(handX, handY) +
                   " | drawing opens " + V(want.OpenX, want.OpenY) + " hinge toward " + V(want.HingeX, want.HingeY) +
                   " | flipped " + (flipFacing && flipHand ? "facing+hand" : flipFacing ? "facing" : flipHand ? "hand" : "none") +
                   " | " + result +
                   " | convention SwingAlongFacing=" + SwingAlongFacing + " HingeAlongHand=" + HingeAlongHand;
        }

        // ─── corners ─────────────────────────────────────────────────────

        /// <summary>
        /// Moves wall ends onto the centreline of the wall they run into, so corners and tees
        /// meet at one point and Revit can join them cleanly.
        ///
        /// A traced centreline stops where its two faces stop overlapping, which at a corner is
        /// the inside face of the other wall: half that wall's thickness short of its centreline.
        /// Joined from there Revit butts the walls and leaves the outside corner notched. Each end
        /// is moved along its own line (extended or trimmed, never turned) to where it crosses
        /// a wall at an angle, if that point is within half the other wall's thickness plus
        /// <paramref name="reachMm"/>. Nearly parallel walls are left alone: the gap between
        /// two pieces of one run is a doorway. Points along a wall stay on it, so openings
        /// placed by position are not disturbed.
        /// </summary>
        public static List<Segment> MeetAtCorners(IReadOnlyList<Wall> walls, double reachMm)
        {
            var result = new List<Segment>(walls.Count);
            if (walls.Count == 0) return result;

            List<Segment> lines = walls.Select(w => w.Centerline).ToList();
            double maxThickness = walls.Max(w => w.Thickness);
            var index = new SegmentIndex(lines, Math.Max(1000, (maxThickness + reachMm) * 4));
            double minSine = Math.Sin(CornerMinAngleDegrees * Math.PI / 180.0);

            for (int i = 0; i < walls.Count; i++)
            {
                Segment line = lines[i];
                double length = line.Length;
                if (length <= 0) { result.Add(line); continue; }

                double ux = (line.P2.x - line.P1.x) / length;
                double uy = (line.P2.y - line.P1.y) / length;

                // Distance along this wall from P1 each end should sit at, and how far it moved.
                double start = 0, end = length;
                double startMove = double.MaxValue, endMove = double.MaxValue;

                foreach (int j in index.Near(line, maxThickness + reachMm))
                {
                    if (j == i) continue;
                    Segment other = lines[j];
                    double otherLength = other.Length;
                    if (otherLength <= 0) continue;

                    double vx = (other.P2.x - other.P1.x) / otherLength;
                    double vy = (other.P2.y - other.P1.y) / otherLength;
                    double cross = (ux * vy) - (uy * vx);
                    if (Math.Abs(cross) < minSine) continue;

                    // Where the two infinite lines cross, as a distance along each.
                    double wx = other.P1.x - line.P1.x;
                    double wy = other.P1.y - line.P1.y;
                    double t = ((wx * vy) - (wy * vx)) / cross;   // along this wall
                    double s = ((wx * uy) - (wy * ux)) / cross;   // along the other

                    // The crossing must be on the other wall (or just past its end, a corner).
                    double slack = (walls[i].Thickness / 2.0) + reachMm;
                    if (s < -slack || s > otherLength + slack) continue;

                    // Closest crossing within reach wins, for each end separately.
                    double limit = (walls[j].Thickness / 2.0) + reachMm;
                    double toStart = Math.Abs(t);
                    double toEnd = Math.Abs(t - length);

                    if (toStart <= toEnd)
                    {
                        if (toStart <= limit && toStart < startMove) { start = t; startMove = toStart; }
                    }
                    else if (toEnd <= limit && toEnd < endMove)
                    {
                        end = t;
                        endMove = toEnd;
                    }
                }

                bool moved = startMove < double.MaxValue || endMove < double.MaxValue;
                result.Add(!moved || end - start < MinWallLengthMm
                    ? line
                    : new Segment(At(line, start), At(line, end)) { Layer = line.Layer });
            }

            return result;
        }

        /// <summary>
        /// Lengthens host walls so every opening lies within its host. A drafter stops the wall
        /// at the doorway, so the piece a door is hung on ends at the hinge and the leaf hangs
        /// over the gap; Revit can only cut an opening out of wall that is there. The end is
        /// extended along its own line to cover the opening, never past the next piece of the
        /// same run, and never shortened. lines[i] is walls[i]'s centreline (after
        /// MeetAtCorners); the result is a new list in the same order.
        /// </summary>
        public static List<Segment> CoverOpenings(
            IReadOnlyList<Wall> walls, IReadOnlyList<Segment> lines, IEnumerable<Opening> openings)
        {
            var result = lines.ToList();
            if (walls.Count == 0 || openings == null) return result;

            var indexOf = new Dictionary<Wall, int>();
            for (int i = 0; i < walls.Count; i++) indexOf[walls[i]] = i;

            foreach (Opening opening in openings)
            {
                if (opening?.Wall == null || !indexOf.TryGetValue(opening.Wall, out int i)) continue;

                Segment line = result[i];
                double length = line.Length;
                if (length <= 0) continue;

                double centre = Along(line, OpeningCentre(opening), out _);
                double half = OpeningWidthMm(opening) / 2.0;
                double start = Math.Min(0, centre - half);
                double end = Math.Max(length, centre + half);
                if (start == 0 && end == length) continue;

                // Stop at the next piece of the same run, so the two meet end to end.
                double thickness = walls[i].Thickness;
                for (int j = 0; j < result.Count; j++)
                {
                    if (j == i || !result[j].isParallelTo(line)) continue;

                    double a = Along(line, result[j].P1, out double offA);
                    double b = Along(line, result[j].P2, out double offB);
                    if (Math.Max(offA, offB) > Math.Max(thickness, walls[j].Thickness) / 2.0) continue;

                    double near = Math.Min(a, b), far = Math.Max(a, b);
                    if (near >= length - 1.0 && near < end) end = near;
                    if (far <= 1.0 && far > start) start = far;
                }

                start = Math.Min(start, 0);
                end = Math.Max(end, length);
                if (start < 0 || end > length) result[i] = new Segment(At(line, start), At(line, end)) { Layer = line.Layer };
            }

            return result;
        }

        // ─── helpers ─────────────────────────────────────────────────────

        private static string Mm(double mm) => Math.Round(mm).ToString("0", CultureInfo.InvariantCulture);

        private static double Along(Segment line, Point point, out double offset)
        {
            double length = line.Length;
            double dx = (line.P2.x - line.P1.x) / length;
            double dy = (line.P2.y - line.P1.y) / length;
            double px = point.x - line.P1.x;
            double py = point.y - line.P1.y;
            offset = Math.Abs((px * -dy) + (py * dx));
            return (px * dx) + (py * dy);
        }

        private static Point At(Segment line, double along)
        {
            double length = line.Length;
            return new Point(
                line.P1.x + ((line.P2.x - line.P1.x) * along / length),
                line.P1.y + ((line.P2.y - line.P1.y) * along / length));
        }
    }
}
