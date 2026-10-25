#nullable enable
// turning_circle_ge with door swings (rulings R30, R47), Revit-free; mm.
//
// A door that opens into the room sweeps a quarter disc of radius = leaf
// width, centred on its hinge at the wall face on the room side. Neither the
// swing direction (in or out) nor the hinge end is trusted from the model:
//   - R47, direction: the room is evaluated with every door opening in and
//     with none: pass only if both pass, fail only if both fail, otherwise
//     "result depends on door swing direction — check manually".
//   - R30, hinge end (inside the "opening in" evaluation): HandOrientation
//     gives the hinge axis, but which end is the hinge depends on how the
//     family was drawn, so both ends are candidates:
//       1. every candidate swing in place still fits → pass for any hinge;
//       2. no fit even with no door swings → fail for any hinge;
//       3. otherwise each hinge combination (up to MaxEnumeratedDoors doors):
//          all pass → pass, all fail → fail, else undecided, naming the doors.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Copilot.Check
{
    /// <summary>The two possible swings of one door that opens into the room.</summary>
    public sealed class DoorSwing
    {
        public string Label { get; }

        /// <summary>Hinge at the jamb on the +hand side; the closed leaf points along −hand.</summary>
        public ClearCircle.Sector HingeA { get; }

        /// <summary>Hinge at the jamb on the −hand side; the closed leaf points along +hand.</summary>
        public ClearCircle.Sector HingeB { get; }

        private DoorSwing(string label, ClearCircle.Sector a, ClearCircle.Sector b)
        {
            Label = label;
            HingeA = a;
            HingeB = b;
        }

        /// <param name="handX">HandOrientation in plan (along the wall).</param>
        /// <param name="swingX">The side the leaf swings to, in plan (across the wall, into the room).</param>
        /// <param name="leafMm">Leaf width: the swing radius.</param>
        /// <param name="wallHalfMm">Half the host wall's thickness: the hinge sits on the wall face.</param>
        /// <exception cref="ArgumentException">The door's geometry cannot give a swing.</exception>
        public static DoorSwing FromOrientation(string label, double locX, double locY, double handX, double handY,
            double swingX, double swingY, double leafMm, double wallHalfMm)
        {
            if (!Finite(locX, locY, handX, handY, swingX, swingY, leafMm, wallHalfMm))
                throw new ArgumentException($"{label}: its location, orientation or width is not a number");
            if (leafMm <= 0) throw new ArgumentException($"{label}: leaf width {leafMm} is not positive");
            var hand = Math.Sqrt(handX * handX + handY * handY);
            var swing = Math.Sqrt(swingX * swingX + swingY * swingY);
            if (hand < 1e-9 || swing < 1e-9) throw new ArgumentException($"{label}: it has no orientation in plan");
            handX /= hand;
            handY /= hand;
            swingX /= swing;
            swingY /= swing;
            if (Math.Abs(handX * swingX + handY * swingY) > 0.1)
                throw new ArgumentException($"{label}: its facing is not across the wall");

            var faceX = locX + swingX * Math.Max(0, wallHalfMm);
            var faceY = locY + swingY * Math.Max(0, wallHalfMm);
            var half = leafMm / 2;
            var a = new ClearCircle.Sector(faceX + handX * half, faceY + handY * half, -handX, -handY, swingX, swingY, leafMm);
            var b = new ClearCircle.Sector(faceX - handX * half, faceY - handY * half, handX, handY, swingX, swingY, leafMm);
            return new DoorSwing(label, a, b);
        }

        private static bool Finite(params double[] values) => values.All(v => !double.IsNaN(v) && !double.IsInfinity(v));
    }

    public static class TurningSpace
    {
        public const int MaxEnumeratedDoors = 3;

        public sealed class Result
        {
            public ClearCircle.Verdict Verdict { get; }

            /// <summary>The circle to show, mm: the smallest found for a pass, the largest for a fail.</summary>
            public double DiameterMm { get; }

            /// <summary>For pass / fail: what was subtracted. For undecided: why it is undecided.</summary>
            public string Detail { get; }

            public Result(ClearCircle.Verdict verdict, double diameterMm, string detail)
            {
                Verdict = verdict;
                DiameterMm = diameterMm;
                Detail = detail;
            }
        }

        public static Result Evaluate(
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops,
            IReadOnlyList<ClearCircle.Box> boxes,
            IReadOnlyList<DoorSwing> doors,
            double needMm,
            double precisionMm = 1.0)
        {
            var basis = $"clear of {Count(boxes.Count, "fixture/furniture box", "es")}"
                + (doors.Count > 0 ? $" and {Count(doors.Count, "door swing", "s")}" : "");
            var fixtures = boxes.Cast<ClearCircle.IObstacle>().ToList();

            var boxesOnly = $"clear of {Count(boxes.Count, "fixture/furniture box", "es")}";
            var bare = ClearCircle.Find(loops, fixtures, needMm, precisionMm);
            var withoutSwings = Single(bare, needMm, precisionMm, boxesOnly);
            if (doors.Count == 0) return withoutSwings;
            return Combine(WithSwings(loops, fixtures, doors, bare, needMm, precisionMm, basis), withoutSwings);
        }

        /// <summary>R47: the verdict with every door opening in, against the verdict with none.
        /// Agreement keeps the verdict; disagreement is undecided, never a guess.</summary>
        public static Result Combine(Result withSwings, Result withoutSwings)
        {
            if (withSwings.Verdict == withoutSwings.Verdict)
            {
                switch (withSwings.Verdict)
                {
                    case ClearCircle.Verdict.Pass:
                        return new Result(ClearCircle.Verdict.Pass, withSwings.DiameterMm, withSwings.Detail + "; doors opening in or out");
                    case ClearCircle.Verdict.Fail:
                        return new Result(ClearCircle.Verdict.Fail, withoutSwings.DiameterMm, withoutSwings.Detail + ", even with no door swings");
                    default:
                        return withSwings;
                }
            }
            var detail = $"{SwingDirectionDecides} (doors opening in: {Word(withSwings.Verdict)}; opening out: {Word(withoutSwings.Verdict)})";
            if (withSwings.Verdict == ClearCircle.Verdict.Undecided) detail += "; " + withSwings.Detail;
            else if (withoutSwings.Verdict == ClearCircle.Verdict.Undecided) detail += "; " + withoutSwings.Detail;
            return new Result(ClearCircle.Verdict.Undecided, Math.Min(withSwings.DiameterMm, withoutSwings.DiameterMm), detail);
        }

        public const string SwingDirectionDecides = "result depends on door swing direction — check manually";

        private static string Word(ClearCircle.Verdict v) => v switch
        {
            ClearCircle.Verdict.Pass => "fits",
            ClearCircle.Verdict.Fail => "does not fit",
            _ => "too close to call",
        };

        /// <summary>Every door opening into the room, with the R30 hinge handling.</summary>
        private static Result WithSwings(
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> loops,
            List<ClearCircle.IObstacle> fixtures,
            IReadOnlyList<DoorSwing> doors,
            ClearCircle.Result bare,
            double needMm,
            double precisionMm,
            string basis)
        {
            var union = ClearCircle.Find(loops, fixtures.Concat(doors.SelectMany(d => new ClearCircle.IObstacle[] { d.HingeA, d.HingeB })).ToList(), needMm, precisionMm);
            if (union.Against(needMm) == ClearCircle.Verdict.Pass)
                return new Result(ClearCircle.Verdict.Pass, union.DiameterMm, basis + ", hinged either side");

            if (bare.Against(needMm) == ClearCircle.Verdict.Fail)
                return new Result(ClearCircle.Verdict.Fail, bare.DiameterMm, "clear of fixture/furniture boxes, even before door swings");

            var labels = string.Join(", ", doors.Select(d => d.Label));
            if (doors.Count > MaxEnumeratedDoors)
                return new Result(ClearCircle.Verdict.Undecided, union.DiameterMm,
                    $"{doors.Count} doors open into the room ({labels}): too many hinge combinations to settle the turning space");

            var results = new List<ClearCircle.Result>();
            for (var mask = 0; mask < 1 << doors.Count; mask++)
            {
                var obstacles = new List<ClearCircle.IObstacle>(fixtures);
                for (var i = 0; i < doors.Count; i++)
                    obstacles.Add((mask & (1 << i)) == 0 ? doors[i].HingeA : doors[i].HingeB);
                results.Add(ClearCircle.Find(loops, obstacles, needMm, precisionMm));
            }
            var verdicts = results.Select(r => r.Against(needMm)).ToList();
            if (verdicts.All(v => v == ClearCircle.Verdict.Pass))
                return new Result(ClearCircle.Verdict.Pass, results.Min(r => r.DiameterMm), basis);
            if (verdicts.All(v => v == ClearCircle.Verdict.Fail))
                return new Result(ClearCircle.Verdict.Fail, results.Max(r => r.DiameterMm), basis);
            if (verdicts.Contains(ClearCircle.Verdict.Pass) && verdicts.Contains(ClearCircle.Verdict.Fail))
                return new Result(ClearCircle.Verdict.Undecided, results.Min(r => r.DiameterMm),
                    $"the circle fits or not depending on which side {labels} {(doors.Count == 1 ? "is" : "are")} hinged: check the door handing");
            return new Result(ClearCircle.Verdict.Undecided, results.Min(r => r.DiameterMm), TooClose(needMm, precisionMm, results.Any(r => !r.Complete)));
        }

        private static Result Single(ClearCircle.Result r, double needMm, double precisionMm, string basis)
        {
            var verdict = r.Against(needMm);
            return verdict == ClearCircle.Verdict.Undecided
                ? new Result(verdict, r.DiameterMm, TooClose(needMm, precisionMm, !r.Complete))
                : new Result(verdict, r.DiameterMm, basis);
        }

        private static string TooClose(double needMm, double precisionMm, bool capped) =>
            capped
                ? $"the search for a {CheckUnits.Format(needMm, CheckUnits.Length)} circle stopped before it was decided"
                : $"the largest clear circle is within {(2 * precisionMm).ToString("0.#", CultureInfo.InvariantCulture)} mm of {CheckUnits.Format(needMm, CheckUnits.Length)}: too close to call";

        private static string Count(int n, string noun, string plural) =>
            n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : plural);
    }
}
