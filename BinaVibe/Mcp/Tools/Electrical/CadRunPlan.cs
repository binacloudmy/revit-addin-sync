#nullable enable
// CadRunPlan - phase 2 of the CAD electrical to BIM work (conduit runs).
//
// Chained runs (CadRunChain, drawing mm) become planned runs in model mm: carried onto the
// model by the drawing's fit, lifted to the run height, and, for conduit, given a vertical
// drop at each end that reaches a device (a planned point, or a device already in the model).
// A cable tray run gets no drops.
//
// Pure: System and Linq only, so it is linked into Tests. It must also compile on net48.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>Something a run's end can drop to, model mm.</summary>
    internal sealed class DropTarget
    {
        /// <summary>The plan point, for a planned device.</summary>
        public int? PointIndex;
        /// <summary>The model element, for a device already in the model.</summary>
        public long? ElementId;
        /// <summary>Where the device is: a planned point's snapped position, or an existing
        /// device's location with its contact height.</summary>
        public double XMm, YMm, ZMm;
        /// <summary>The symbol's drawn centre on the model and half its size; a drawn run ends at
        /// the symbol's edge, not at the wall face the point was snapped to. Half 0: no symbol.</summary>
        public double DrawnXMm, DrawnYMm, DrawnHalfMm;

        public string Key => PointIndex.HasValue ? "p:" + PointIndex.Value : "e:" + (ElementId ?? 0);
    }

    internal sealed class RunPlanOptions
    {
        /// <summary>An end this close to a device (or its drawn symbol) drops to it.</summary>
        public double DropReachMm = 300;
        /// <summary>A second device this much further away than the nearest makes it ambiguous.</summary>
        public double AmbiguousMm = 50;
        /// <summary>An end closer than this to the device moves onto it; further, it gets a jog.</summary>
        public double MinLegMm = 150;
        /// <summary>A vertical drop shorter than this is left out (drop_too_short).</summary>
        public double MinDropMm = 300;
        /// <summary>A jog this close to the last leg's direction lengthens the leg instead.</summary>
        public double CollinearDeg = 1;
    }

    /// <summary>What a mapping row says about a run layer.</summary>
    internal sealed class RunLayerSpec
    {
        public string Layer = "";
        /// <summary>"conduit" | "cable_tray"</summary>
        public string Kind = "conduit";
        public string? TypeName;
        public double? DiameterMm, WidthMm, HeightMm;
        public double? RunHeightMm;
    }

    /// <summary>The fittings a cable tray type has assigned (its routing fittings).</summary>
    internal sealed class TrayFittings
    {
        public string Name = "";
        public bool Bend, Tee, Cross, Union;
        public bool Any => Bend || Tee || Cross || Union;
        public int Count => (Bend ? 1 : 0) + (Tee ? 1 : 0) + (Cross ? 1 : 0) + (Union ? 1 : 0);
    }

    /// <summary>One routed run's end at a junction: which run, which end, where, and the plan
    /// direction from the junction into the run. Group keeps different types and levels apart.</summary>
    internal sealed class JunctionEnd
    {
        public int Item;
        public bool AtStart;
        public string Group = "";
        public double X, Y, Z, Dx, Dy;
    }

    internal static class CadRunPlan
    {
        /// <summary>
        /// The tray type to route with: the named one when it has any fitting; else the project's
        /// type with the most fittings (a bend first, then by name), and <paramref name="switched"/>
        /// is true; else the named one, which then builds straight runs only.
        /// </summary>
        public static TrayFittings ChooseTrayType(TrayFittings named, IEnumerable<TrayFittings> all, out bool switched)
        {
            switched = false;
            if (named.Any) return named;
            TrayFittings? other = all.Where(t => t.Any)
                .OrderByDescending(t => t.Bend).ThenByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (other == null) return named;
            switched = true;
            return other;
        }

        /// <summary>A run with a corner needs a bend; a straight one always builds.</summary>
        public static bool CanBuild(TrayFittings t, int points) => points <= 2 || t.Bend;

        /// <summary>Why a junction of <paramref name="ends"/> cannot be joined with this type, or
        /// null when it can. Two ends straight on are only connected and need no fitting.</summary>
        public static string? JoinSkip(TrayFittings t, int ends, bool straight)
        {
            if (ends == 2 && straight) return null;
            if (!t.Any) return "tray type '" + t.Name + "' has no fittings; joins skipped";
            if (ends == 2) return t.Bend ? null : "tray type '" + t.Name + "' has no bend";
            if (ends == 3) return t.Tee ? null : "tray type '" + t.Name + "' has no tee";
            return t.Cross ? null : "tray type '" + t.Name + "' has no cross";
        }

        /// <summary>Run ends meeting at one junction: ends of one group within joinMm of each
        /// other in plan and height, two or more of them, in input order.</summary>
        public static List<List<JunctionEnd>> JunctionGroups(IReadOnlyList<JunctionEnd> ends, double joinMm = 20)
        {
            var groups = new List<List<JunctionEnd>>();
            foreach (JunctionEnd e in ends)
            {
                List<JunctionEnd>? at = groups.FirstOrDefault(g => g[0].Group == e.Group &&
                    Math.Abs(g[0].X - e.X) <= joinMm && Math.Abs(g[0].Y - e.Y) <= joinMm && Math.Abs(g[0].Z - e.Z) <= joinMm);
                if (at == null) groups.Add(at = new List<JunctionEnd>());
                at.Add(e);
            }
            return groups.Where(g => g.Count >= 2).ToList();
        }

        /// <summary>
        /// The ends in the order Revit's fittings take them: for a tee the two most opposite
        /// first (the main) and the branch last; for a cross the most opposite pair, then the
        /// other two. Two ends are returned as given.
        /// </summary>
        public static List<JunctionEnd> FittingOrder(List<JunctionEnd> ends)
        {
            if (ends.Count < 3) return ends.ToList();
            int bi = 0, bj = 1;
            double best = double.MaxValue;
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                {
                    double dot = (ends[i].Dx * ends[j].Dx) + (ends[i].Dy * ends[j].Dy);
                    if (dot < best) { best = dot; bi = i; bj = j; }
                }
            var order = new List<JunctionEnd> { ends[bi], ends[bj] };
            order.AddRange(ends.Where((_, k) => k != bi && k != bj));
            return order;
        }

        /// <summary>Two ends pointing opposite ways (within 10 degrees) carry straight on.</summary>
        public static bool Straight(JunctionEnd a, JunctionEnd b) =>
            (a.Dx * b.Dx) + (a.Dy * b.Dy) <= -Math.Cos(10 * Math.PI / 180);

        /// <summary>Each run flag, and whether it leaves the run out of a default route_conduit.</summary>
        public static readonly IReadOnlyDictionary<string, bool> FlagExcludes = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["open_end"] = false,
            ["junction"] = false,
            ["shares_drop"] = false,
            ["short_leg"] = false,
            ["drop_too_short"] = false,
            ["device_ambiguous"] = false,
            ["provisional_alignment"] = false,
            ["closed_loop"] = true,
        };

        /// <summary>
        /// The targets runs may drop to: every plan point not excluded, with its symbol's drawn
        /// centre and half size where <paramref name="drawn"/> knows them, and every device of
        /// the model (contact height, CadElectricalResolver.ExistingDevices).
        /// </summary>
        public static List<DropTarget> Targets(IEnumerable<PlannedPoint> points,
                                               IReadOnlyDictionary<int, (double X, double Y, double Half)> drawn,
                                               IEnumerable<PresentDevice> existing)
        {
            var targets = new List<DropTarget>();
            foreach (PlannedPoint p in points)
            {
                if (p.Excluded) continue;
                var t = new DropTarget { PointIndex = p.Index, XMm = p.XMm, YMm = p.YMm, ZMm = p.ZMm, DrawnXMm = p.XMm, DrawnYMm = p.YMm };
                if (drawn.TryGetValue(p.Index, out var d))
                {
                    t.DrawnXMm = d.X;
                    t.DrawnYMm = d.Y;
                    t.DrawnHalfMm = d.Half;
                }
                targets.Add(t);
            }
            foreach (PresentDevice device in existing)
            {
                targets.Add(new DropTarget
                {
                    ElementId = device.Id, XMm = device.X, YMm = device.Y, ZMm = device.Z, DrawnXMm = device.X, DrawnYMm = device.Y,
                });
            }
            return targets;
        }

        /// <summary>
        /// One layer's runs, planned. Indices start at <paramref name="firstIndex"/>; ends are
        /// matched in run order, start before end, and <paramref name="usedTargets"/> (shared
        /// across layers) remembers which devices already have a drop: a later end at one of them
        /// stops at the top of that drop (shares_drop). The spec must carry a run height.
        /// </summary>
        public static List<PlannedRun> Plan(IReadOnlyList<ChainedRun> runs, RunLayerSpec spec, FitTransform t,
                                            double levelZMm, string levelName, IReadOnlyList<DropTarget> targets,
                                            bool provisional, RunPlanOptions? options = null, int firstIndex = 0,
                                            ISet<string>? usedTargets = null)
        {
            if (!spec.RunHeightMm.HasValue) throw new ArgumentException("the run layer has no run height", nameof(spec));
            RunPlanOptions o = options ?? new RunPlanOptions();
            ISet<string> used = usedTargets ?? new HashSet<string>(StringComparer.Ordinal);
            double runZ = levelZMm + spec.RunHeightMm.Value;
            bool conduit = spec.Kind == "conduit";

            var planned = new List<PlannedRun>(runs.Count);
            foreach (ChainedRun run in runs)
            {
                var p = new PlannedRun
                {
                    Index = firstIndex + planned.Count,
                    Layer = spec.Layer.Length > 0 ? spec.Layer : run.Layer,
                    Kind = spec.Kind,
                    TypeName = spec.TypeName,
                    DiameterMm = spec.DiameterMm,
                    WidthMm = spec.WidthMm,
                    HeightMm = spec.HeightMm,
                    LevelName = levelName,
                    RunHeightMm = spec.RunHeightMm.Value,
                };
                foreach ((double x, double y) in run.Points)
                {
                    t.Apply(x, y, out double mx, out double my);
                    p.PointsMm.Add(new[] { mx, my, runZ });
                }
                p.Flags.AddRange(run.Flags);
                p.Start.Kind = run.StartKind;
                p.End.Kind = run.EndKind;

                if (conduit && !run.Closed)
                {
                    EndToDevice(p, atStart: true, targets, used, runZ, o);
                    EndToDevice(p, atStart: false, targets, used, runZ, o);
                    if (p.Start.Kind == "open" || p.End.Kind == "open") AddFlag(p, "open_end");
                }
                if (p.Start.Kind == "junction" || p.End.Kind == "junction") AddFlag(p, "junction");
                if (provisional) AddFlag(p, "provisional_alignment");

                p.LengthMm = Length3(p.PointsMm);
                p.Excluded = p.Flags.Any(f => FlagExcludes.TryGetValue(f, out bool excludes) && excludes);
                planned.Add(p);
            }
            return planned;
        }

        /// <summary>Gives one open end its drop, when a device is in reach.</summary>
        private static void EndToDevice(PlannedRun p, bool atStart, IReadOnlyList<DropTarget> targets, ISet<string> used,
                                        double runZ, RunPlanOptions o)
        {
            RunEnd end = atStart ? p.Start : p.End;
            if (end.Kind != "open" || p.PointsMm.Count < 2) return;

            // Work on the end as the last point; the start is the reversed run's end.
            List<double[]> pts = p.PointsMm;
            if (atStart) pts.Reverse();
            try
            {
                double[] e = pts[pts.Count - 1];
                DropTarget? target = Nearest(targets, e[0], e[1], o, out bool ambiguous);
                if (target == null) return;
                if (ambiguous) AddFlag(p, "device_ambiguous");

                end.PointIndex = target.PointIndex;
                end.ElementId = target.ElementId;
                bool shares = !used.Add(target.Key);

                // Along to the device: a near end moves onto it, a far one gets a jog, and a jog
                // straight on lengthens the last leg rather than add a corner that is not one.
                double[] before = pts[pts.Count - 2];
                double h = Math.Sqrt(Sq(target.XMm - e[0]) + Sq(target.YMm - e[1]));
                if (h > 0)
                {
                    double lx = e[0] - before[0], ly = e[1] - before[1];
                    double ll = Math.Sqrt((lx * lx) + (ly * ly));
                    double jx = target.XMm - e[0], jy = target.YMm - e[1];
                    bool straightOn = ll > 0 && Math.Abs(((lx * jy) - (ly * jx)) / (ll * h)) < Math.Sin(o.CollinearDeg * Math.PI / 180);
                    if (h < o.MinLegMm || straightOn)
                    {
                        e[0] = target.XMm;
                        e[1] = target.YMm;
                    }
                    else
                    {
                        pts.Add(new[] { target.XMm, target.YMm, runZ });
                    }
                }

                if (shares)
                {
                    end.Kind = "shares_drop";
                    AddFlag(p, "shares_drop");
                    return;
                }

                end.Kind = "device";
                end.DropMm = runZ - target.ZMm;
                if (Math.Abs(end.DropMm) < o.MinDropMm)
                {
                    AddFlag(p, "drop_too_short");
                    return;
                }
                pts.Add(new[] { target.XMm, target.YMm, target.ZMm });
                p.Drops++;
            }
            finally
            {
                if (atStart) pts.Reverse();
            }
        }

        /// <summary>The nearest target within reach of an end: the smaller of the distance to its
        /// point and the distance to its drawn symbol's edge. Ambiguous when another one is within
        /// AmbiguousMm of that distance.</summary>
        private static DropTarget? Nearest(IReadOnlyList<DropTarget> targets, double x, double y, RunPlanOptions o,
                                           out bool ambiguous)
        {
            ambiguous = false;
            DropTarget? best = null;
            double bestD = double.MaxValue;
            var near = new List<double>();
            foreach (DropTarget t in targets)
            {
                double d = Math.Sqrt(Sq(t.XMm - x) + Sq(t.YMm - y));
                if (t.DrawnHalfMm > 0)
                {
                    double edge = Math.Sqrt(Sq(t.DrawnXMm - x) + Sq(t.DrawnYMm - y)) - t.DrawnHalfMm;
                    d = Math.Min(d, Math.Max(0, edge));
                }
                if (d > o.DropReachMm) continue;
                near.Add(d);
                if (d < bestD)
                {
                    bestD = d;
                    best = t;
                }
            }
            ambiguous = near.Count(d => d <= bestD + o.AmbiguousMm) > 1;
            return best;
        }

        internal static double Length3(IReadOnlyList<double[]> points)
        {
            double total = 0;
            for (int i = 0; i + 1 < points.Count; i++)
                total += Math.Sqrt(Sq(points[i + 1][0] - points[i][0]) + Sq(points[i + 1][1] - points[i][1]) +
                                   Sq(points[i + 1][2] - points[i][2]));
            return total;
        }

        private static void AddFlag(PlannedRun p, string flag)
        {
            if (!p.Flags.Contains(flag)) p.Flags.Add(flag);
        }

        private static double Sq(double v) => v * v;
    }
}
