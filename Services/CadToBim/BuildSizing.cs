// BuildSizing — the decisions Cad2BimBuilder makes about types and geometry, kept free of
// Revit types so they can be unit-tested (the Tests project's RevitAPI is metadata-only).
//
//   which wall type a detected thickness gets, and what a new one is called;
//   door or window, and the width/height its family type must have (rounded to 10 mm,
//   but never so far up that it is wider than the wall piece it is cut out of);
//   which existing type matches a size, or which one to duplicate when none does
//   (for a door or window, never one wider than the size);
//   where a door actually sits (the swing's hinge is a jamb, not the middle);
//   how much wall is left past each jamb, and which flip recuts a door or window Revit left uncut;
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

    /// <summary>How the builder recuts a door or window Revit kept hosted but did not cut:
    /// flip it twice (there and back) by Hand or by Facing, else leave it (None). Doors try
    /// Hand first, windows Facing first (RecutOrder). A recut that costs an opening is rolled
    /// back and tried one step weaker.</summary>
    public enum DoorRecut
    {
        Hand,
        Facing,
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

        /// <summary>How much wall a door or window must leave past each jamb on its host piece
        /// when its width is fitted (FitWidthMm). 0: an opening may exactly fill its piece.
        /// Raise to 1.0 if Revit still removes windows that exactly fill their host.</summary>
        public const double OpeningEndClearMm = 0.0;

        // Float slack for "fits": 900.00001 in a 900 mm piece still fits.
        private const double FitSlackMm = 0.05;

        /// <summary>Wall left past a jamb that is too thin for Revit to cut an opening against
        /// (FillSliverMm). 2D_FloorPlan: a 760 door in a 762 x 50 piece is deleted.</summary>
        public const double HostSliverMm = 2.0;

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
        /// settings give its kind (a plan carries no heights), both rounded to the nearest
        /// OpeningStepMm. Does not look at the host; the overload taking one does.</summary>
        public static (double WidthMm, double HeightMm) OpeningSize(
            OpeningKind kind, double widthMm, double doorHeightMm, double windowHeightMm) =>
            (Snap(widthMm, OpeningStepMm),
             Snap(kind == OpeningKind.Door ? doorHeightMm : windowHeightMm, OpeningStepMm));

        /// <summary>
        /// The family size for one opening on its host wall piece. As the overload above, but
        /// the width is then fitted to the host (FitWidthMm): rounding 2946 to 2950 in a piece
        /// 2946 long would make an insert Revit cannot cut, and Revit deletes it. host is the
        /// drawn host line as built (HostLines); null = no fitting.
        /// </summary>
        public static (double WidthMm, double HeightMm) OpeningSize(
            Opening opening, Segment host, double doorHeightMm, double windowHeightMm)
        {
            var size = OpeningSize(KindOf(opening), OpeningWidthMm(opening), doorHeightMm, windowHeightMm);
            if (host == null) return size;
            Point centre = OpeningCentre(opening);
            double fitted = FitWidthMm(size.WidthMm, host, centre);
            return (FillSliverMm(fitted, host, centre), size.HeightMm);
        }

        /// <summary>
        /// An opening that fills its host but for a sliver takes the host's whole length: a
        /// 760 door centred in a 762 piece leaves 1 mm of wall past each jamb, which Revit
        /// cannot cut, and it deletes the door. Only when the leftover is more than float slack
        /// and under 2 x HostSliverMm in all, and the centre is within HostSliverMm of the
        /// host's middle; otherwise the width is returned as it is. Never wider than the host.
        /// </summary>
        public static double FillSliverMm(double widthMm, Segment host, Point centre)
        {
            if (host == null || centre == null) return widthMm;
            double length = host.Length;
            if (!(length > 0)) return widthMm;

            double leftover = length - widthMm;
            if (leftover <= FitSlackMm || leftover >= 2.0 * HostSliverMm) return widthMm;
            if (Math.Abs(Along(host, centre, out _) - (length / 2.0)) > HostSliverMm) return widthMm;
            return length;
        }

        /// <summary>
        /// How much wall an opening of widthMm (the fitted width the builder places) leaves past
        /// each jamb on its host line: (from P1 to the near jamb, from the far jamb to P2).
        /// Negative = the opening runs off that end. NaN for both when there is no opening or the
        /// host is null or of zero length. Diagnostic only: logged, never used to decide.
        /// </summary>
        public static (double Start, double End) WallPastJambsMm(Opening opening, Segment host, double widthMm)
        {
            Point centre = OpeningCentre(opening);
            if (centre == null || host == null || !(host.Length > 0)) return (double.NaN, double.NaN);
            double along = Along(host, centre, out _);
            double half = widthMm / 2.0;
            return (along - half, host.Length - along - half);
        }

        /// <summary>
        /// A rounded opening width, made to fit its host piece. The room is twice the distance
        /// from the opening's centre to the nearer end of the host (the insert is placed at
        /// the centre), less OpeningEndClearMm past each jamb. A width that overshoots the room
        /// by at most one OpeningStepMm becomes the largest multiple of the step that fits
        /// (2946 -> 2940, not 2950). One that overshoots by more is a different problem and is
        /// left as it is, as is anything that would go below MinOpeningWidthMm.
        /// </summary>
        public static double FitWidthMm(double roundedMm, Segment host, Point centre)
        {
            if (host == null || centre == null) return roundedMm;
            double length = host.Length;
            if (!(length > 0)) return roundedMm;

            double along = Along(host, centre, out _);
            double room = (2.0 * Math.Min(along, length - along)) - (2.0 * OpeningEndClearMm);
            if (roundedMm <= room + FitSlackMm || roundedMm - room > OpeningStepMm) return roundedMm;

            double fitted = Math.Floor((room + FitSlackMm) / OpeningStepMm) * OpeningStepMm;
            return fitted >= MinOpeningWidthMm ? fitted : roundedMm;
        }

        public static string OpeningTypeName(double widthMm, double heightMm) =>
            "CAD " + Mm(widthMm) + " x " + Mm(heightMm) + " mm";

        // ─── picking a type ──────────────────────────────────────────────

        /// <summary>
        /// The type to use for a wanted size. A candidate within tolerance on every dimension
        /// is used as it is (closest first, then by name, so the pick is stable). Otherwise the
        /// closest resizable candidate is the one to duplicate; failing that, the closest of
        /// all, used unsized. heightMm = NaN compares widths only (wall types).
        /// neverWider (doors and windows): a candidate may be up to toleranceMm narrower than
        /// widthMm but not wider, since that width was already fitted to its host.
        /// </summary>
        public static SizeChoice Pick(
            IReadOnlyList<SizeCandidate> candidates, double widthMm, double heightMm, double toleranceMm,
            bool neverWider = false)
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
                widthMm - c.WidthMm <= toleranceMm &&
                c.WidthMm - widthMm <= (neverWider ? FitSlackMm : toleranceMm) &&
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
        /// Doors inside a mirrored (reflected) block insert arrive here too: the reader keeps an
        /// arc as an arc under a reflection as well as a rotation (CadRenderSource
        /// Xform.TryConformal / MapAngles), with its ends swapped so it still runs
        /// counter-clockwise. The end points are what is read here, so the swap changes nothing.
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

        /// <summary>The recuts tried for an uncut opening, strongest first, ending in None: a
        /// door Hand, Facing, None; a window Facing, Hand, None (Revit 2027: flipping a window's
        /// facing twice makes Revit cut it).</summary>
        public static IReadOnlyList<DoorRecut> RecutOrder(OpeningKind kind) =>
            kind == OpeningKind.Window ? WindowRecuts : DoorRecuts;

        private static readonly DoorRecut[] DoorRecuts = { DoorRecut.Hand, DoorRecut.Facing, DoorRecut.None };
        private static readonly DoorRecut[] WindowRecuts = { DoorRecut.Facing, DoorRecut.Hand, DoorRecut.None };

        /// <summary>The first recut at or after <paramref name="from"/> in the kind's
        /// RecutOrder that the opening allows: Hand needs canFlipHand, Facing needs
        /// canFlipFacing; None when neither is left.</summary>
        public static DoorRecut RecutBy(OpeningKind kind, bool canFlipHand, bool canFlipFacing, DoorRecut from)
        {
            IReadOnlyList<DoorRecut> order = RecutOrder(kind);
            int i = 0;
            while (order[i] != from) i++;
            for (; i < order.Count; i++)
            {
                if (order[i] == DoorRecut.Hand && canFlipHand) return DoorRecut.Hand;
                if (order[i] == DoorRecut.Facing && canFlipFacing) return DoorRecut.Facing;
            }
            return DoorRecut.None;
        }

        /// <summary>The door recut (Hand first): RecutBy(OpeningKind.Door, ...).</summary>
        public static DoorRecut RecutBy(bool canFlipHand, bool canFlipFacing, DoorRecut from) =>
            RecutBy(OpeningKind.Door, canFlipHand, canFlipFacing, from);

        /// <summary>The next weaker recut in the kind's RecutOrder; None stays None.</summary>
        public static DoorRecut Weaker(OpeningKind kind, DoorRecut recut)
        {
            IReadOnlyList<DoorRecut> order = RecutOrder(kind);
            for (int i = 0; i < order.Count - 1; i++)
                if (order[i] == recut) return order[i + 1];
            return DoorRecut.None;
        }

        /// <summary>The next weaker door recut: Hand -> Facing -> None.</summary>
        public static DoorRecut Weaker(DoorRecut recut) => Weaker(OpeningKind.Door, recut);

        /// <summary>
        /// The height above the level, in mm, at which the builder looks for wall left in an
        /// opening: a door at min(1000, half its height), a window at the middle of its pane
        /// (sill + half its height, as requested).
        /// </summary>
        public static double CutProbeHeightMm(OpeningKind kind, double doorHeightMm, double windowSillMm, double windowHeightMm) =>
            kind == OpeningKind.Door
                ? Math.Min(1000.0, doorHeightMm / 2.0)
                : windowSillMm + (windowHeightMm / 2.0);

        /// <summary>
        /// One line per door or window the recut step looked at, for cad-to-bim.log: where it is (at), its
        /// type, how much wall is past each jamb (NaN prints as ?), the recut used and the
        /// outcome.
        /// </summary>
        public static string DoorRecutLine(string at, string type, double startMm, double endMm, DoorRecut recut, string outcome)
        {
            string M(double v) => double.IsNaN(v) ? "?" : Mm(v);
            string how = recut == DoorRecut.Hand ? "flipped hand twice"
                       : recut == DoorRecut.Facing ? "flipped facing twice"
                       : "not flipped";
            return (at ?? "door ?") + " | " + (type ?? "?") +
                   " | wall past each jamb " + M(startMm) + " / " + M(endMm) + " mm" +
                   " | " + how + " | " + outcome;
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

        /// <summary>The host lines the builder places openings on: walls met at corners
        /// (MeetAtCorners), then lengthened to cover their openings (CoverOpenings). The
        /// builder and the TYPES pane both call this, so they fit widths to the same lines.</summary>
        public static List<Segment> HostLines(IReadOnlyList<Wall> walls, IEnumerable<Opening> openings) =>
            CoverOpenings(walls, MeetAtCorners(walls, CornerReachMm), openings);

        /// <summary>
        /// The walls to shorten back once some openings are gone. drawn is HostLines over every
        /// opening, as the walls were built; kept are the openings still in the model. A host
        /// lengthened across a doorway for an opening that was never placed or that Revit
        /// removed would otherwise wall that doorway shut. Returns each wall whose line under
        /// HostLines(walls, kept) differs from drawn[i] by more than 0.01 mm at either end, with
        /// that line. Ends are compared, not references: MeetAtCorners makes new segments on
        /// every call, so corner moves come out equal and are left alone.
        /// </summary>
        public static List<(int Index, Segment Line)> ReopenLines(
            IReadOnlyList<Wall> walls, IReadOnlyList<Segment> drawn, IEnumerable<Opening> kept)
        {
            var result = new List<(int Index, Segment Line)>();
            if (walls == null || drawn == null || walls.Count == 0) return result;

            List<Segment> lines = HostLines(walls, kept ?? Enumerable.Empty<Opening>());
            int count = Math.Min(lines.Count, drawn.Count);
            for (int i = 0; i < count; i++)
            {
                if (drawn[i] == null) continue;
                if (Near(lines[i].P1, drawn[i].P1) && Near(lines[i].P2, drawn[i].P2)) continue;
                result.Add((i, lines[i]));
            }
            return result;
        }

        private static bool Near(Point a, Point b) =>
            Segment.PointDistance(a, b) <= 0.01;

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
