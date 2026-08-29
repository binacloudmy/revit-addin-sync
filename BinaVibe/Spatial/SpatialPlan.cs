// BinaVibe.Spatial — spatial edit planner, Revit-free
// (bina-ai R2 Task 23: family A move / copy / rotate / delete; family B mirror / align / array).
//
// Before any transaction: which elements change and where they end up, which
// are skipped and why (pinned, grouped, datum, already aligned), and the risks
// the drafter must see (hosted dependents, invalid array). After the commit:
// positions re-read and compared within a tolerance, absence checked for
// delete, new ids for copy / mirror-copy / array. All in millimetres.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Spatial
{
    public readonly record struct Vec(double X, double Y, double Z)
    {
        public Vec Plus(Vec o) => new(X + o.X, Y + o.Y, Z + o.Z);
        public Vec Times(double k) => new(X * k, Y * k, Z * k);
        public double DistanceTo(Vec o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
        public List<double> ToList() => new() { Math.Round(X, 1), Math.Round(Y, 1), Math.Round(Z, 1) };
    }

    public enum SpatialOp { Move, Copy, Rotate, Delete, Mirror, Align, Array }

    public sealed class SpatialParams
    {
        public Vec? Vector { get; init; }          // move / copy / array spacing
        public double AngleDeg { get; init; }      // rotate
        public Vec? Axis { get; init; }            // rotate axis (null = centroid)
        public string? MirrorAxis { get; init; }   // "x" | "y": plane normal
        public double MirrorAtMm { get; init; }    // plane position along that axis
        public bool Copy { get; init; } = true;    // mirror: keep originals
        public string? AlignAxis { get; init; }    // "x" | "y": coordinate that becomes AlignAtMm
        public double? AlignAtMm { get; init; }
        public string? AlignEdge { get; init; }    // left | right | top | bottom | center (selection's own extreme)
        public int Count { get; init; }            // array: total instances including the source
    }

    public sealed class SpatialRow
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public double X { get; init; }
        public double Y { get; init; }
        public double Z { get; init; }
        public bool Pinned { get; init; }
        public bool Grouped { get; init; }
        public bool IsDatum { get; init; }
        public int Dependents { get; init; }
        public Vec Position => new(X, Y, Z);
    }

    public sealed class SpatialChange
    {
        public long Id { get; init; }            // source element
        public string Name { get; init; } = "";
        public Vec From { get; init; }
        public Vec To { get; init; }
        public int Dependents { get; init; }
        public int CopyIndex { get; init; }      // 0 = the element itself moves; k>0 = k-th copy
    }

    public sealed class SpatialRisk
    {
        public long Id { get; init; }
        public string Kind { get; init; } = "";
        public int Count { get; init; }
        public string Note { get; init; } = "";
    }

    public sealed class SpatialPlan
    {
        public SpatialOp Op { get; }
        public IReadOnlyList<SpatialChange> Changes { get; }
        public IReadOnlyDictionary<string, int> Skipped { get; }
        public IReadOnlyList<SpatialRisk> Risks { get; }
        public int Matched { get; }
        public Vec Axis { get; }
        public double AngleDeg { get; }
        public Vec Vector { get; }
        public bool CreatesCopies { get; }
        public int CopiesPerSource { get; }
        public double AlignTargetMm { get; }
        public string? AlignAxis { get; }
        public string? MirrorAxis { get; }
        public double MirrorAtMm { get; }

        private SpatialPlan(SpatialOp op, List<SpatialChange> changes, Dictionary<string, int> skipped, List<SpatialRisk> risks,
                            int matched, Vec axis, double angle, Vec vector, bool copies, int perSource,
                            double alignAt, string? alignAxis, string? mirrorAxis, double mirrorAt)
        {
            Op = op; Changes = changes; Skipped = skipped; Risks = risks; Matched = matched; Axis = axis; AngleDeg = angle; Vector = vector;
            CreatesCopies = copies; CopiesPerSource = perSource; AlignTargetMm = alignAt; AlignAxis = alignAxis; MirrorAxis = mirrorAxis; MirrorAtMm = mirrorAt;
        }

        /// <summary>Family A signature (kept for callers and tests).</summary>
        public static SpatialPlan Build(IEnumerable<SpatialRow> rows, SpatialOp op, Vec? vector, double angleDeg, Vec? axis)
            => Build(rows, op, new SpatialParams { Vector = vector, AngleDeg = angleDeg, Axis = axis });

        public static SpatialPlan Build(IEnumerable<SpatialRow> rows, SpatialOp op, SpatialParams p)
        {
            var list = rows.ToList();
            var vec = p.Vector ?? new Vec(0, 0, 0);
            var skipped = new Dictionary<string, int> { ["pinned"] = 0, ["grouped"] = 0, ["datum"] = 0 };
            var changes = new List<SpatialChange>();
            var risks = new List<SpatialRisk>();
            bool copies = op == SpatialOp.Copy || op == SpatialOp.Array || (op == SpatialOp.Mirror && p.Copy);
            int perSource = 0;

            var eligible = list.Where(r => !r.IsDatum && !r.Pinned && !r.Grouped).ToList();
            Vec ax = p.Axis ?? (eligible.Count > 0 ? new Vec(eligible.Average(r => r.X), eligible.Average(r => r.Y), 0) : new Vec(0, 0, 0));
            double rad = p.AngleDeg * Math.PI / 180.0;

            // align target: explicit coordinate, or the selection's own extreme
            string? alignAxis = p.AlignAxis;
            double alignAt = p.AlignAtMm ?? 0;
            if (op == SpatialOp.Align && p.AlignAtMm == null && !string.IsNullOrEmpty(p.AlignEdge) && eligible.Count > 0)
            {
                switch (p.AlignEdge!.ToLowerInvariant())
                {
                    case "left": alignAxis = "x"; alignAt = eligible.Min(r => r.X); break;
                    case "right": alignAxis = "x"; alignAt = eligible.Max(r => r.X); break;
                    case "bottom": alignAxis = "y"; alignAt = eligible.Min(r => r.Y); break;
                    case "top": alignAxis = "y"; alignAt = eligible.Max(r => r.Y); break;
                    case "center": case "centre": alignAxis = "x"; alignAt = eligible.Average(r => r.X); break;
                }
            }
            if (op == SpatialOp.Align) skipped["unchanged"] = 0;
            if (op == SpatialOp.Array)
            {
                perSource = Math.Max(0, p.Count - 1);
                if (p.Count < 2 || vec.DistanceTo(new Vec(0, 0, 0)) < 0.001)
                {
                    risks.Add(new SpatialRisk { Kind = "invalid_array", Note = "array needs count ≥ 2 and a non-zero spacing" });
                    return new SpatialPlan(op, changes, skipped, risks, list.Count, ax, p.AngleDeg, vec, true, perSource, alignAt, alignAxis, p.MirrorAxis, p.MirrorAtMm);
                }
            }

            foreach (var r in list)
            {
                if (r.IsDatum) { skipped["datum"]++; continue; }
                if (op != SpatialOp.Delete && r.Pinned) { skipped["pinned"]++; continue; }
                if (op != SpatialOp.Delete && r.Grouped) { skipped["grouped"]++; continue; }

                switch (op)
                {
                    case SpatialOp.Move:
                    case SpatialOp.Copy:
                        changes.Add(Change(r, r.Position.Plus(vec), 0)); break;
                    case SpatialOp.Rotate:
                        changes.Add(Change(r, RotateAbout(r.Position, ax, rad), 0)); break;
                    case SpatialOp.Delete:
                        changes.Add(Change(r, r.Position, 0)); break;
                    case SpatialOp.Mirror:
                    {
                        var to = string.Equals(p.MirrorAxis, "y", StringComparison.OrdinalIgnoreCase)
                            ? new Vec(r.X, 2 * p.MirrorAtMm - r.Y, r.Z)
                            : new Vec(2 * p.MirrorAtMm - r.X, r.Y, r.Z);
                        changes.Add(Change(r, to, p.Copy ? 1 : 0)); break;
                    }
                    case SpatialOp.Align:
                    {
                        var to = alignAxis == "y" ? new Vec(r.X, alignAt, r.Z) : new Vec(alignAt, r.Y, r.Z);
                        if (to.DistanceTo(r.Position) < 0.5) { skipped["unchanged"]++; continue; }
                        changes.Add(Change(r, to, 0)); break;
                    }
                    case SpatialOp.Array:
                        for (int k = 1; k <= perSource; k++) changes.Add(Change(r, r.Position.Plus(vec.Times(k)), k));
                        break;
                }
                if (r.Dependents > 0 && op != SpatialOp.Copy && op != SpatialOp.Array && !(op == SpatialOp.Mirror && p.Copy))
                {
                    risks.Add(op == SpatialOp.Delete
                        ? new SpatialRisk { Id = r.Id, Kind = "dependents_deleted", Count = r.Dependents, Note = $"{r.Dependents} hosted element(s) will be deleted too" }
                        : new SpatialRisk { Id = r.Id, Kind = "hosted_dependents", Count = r.Dependents, Note = $"{r.Dependents} hosted element(s) move with it" });
                }
            }
            return new SpatialPlan(op, changes, skipped, risks, list.Count, ax, p.AngleDeg, vec, copies, perSource, alignAt, alignAxis, p.MirrorAxis, p.MirrorAtMm);
        }

        private static SpatialChange Change(SpatialRow r, Vec to, int copyIndex) =>
            new() { Id = r.Id, Name = r.Name, From = r.Position, To = to, Dependents = r.Dependents, CopyIndex = copyIndex };

        private static Vec RotateAbout(Vec p, Vec axis, double rad)
        {
            double dx = p.X - axis.X, dy = p.Y - axis.Y;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double rx = axis.X + dx * c + dy * s;
            double ry = axis.Y - dx * s + dy * c;
            return new Vec(rx, ry, p.Z);
        }

        public Dictionary<string, object?> ToPreview(int cap = 200)
        {
            var d = new Dictionary<string, object?>
            {
                ["ok"] = !Risks.Any(r => r.Kind == "invalid_array"),
                ["dry_run"] = true,
                ["op"] = Op.ToString().ToLowerInvariant(),
                ["matched"] = Matched,
                ["would_change"] = Changes.Count,
                ["preview"] = Changes.Take(cap).Select(c => (object)new Dictionary<string, object?>
                {
                    ["id"] = c.Id, ["name"] = c.Name, ["from_mm"] = c.From.ToList(), ["to_mm"] = c.To.ToList(),
                    ["dependents"] = c.Dependents, ["copy_index"] = c.CopyIndex,
                }).ToList(),
                ["preview_truncated"] = Changes.Count > cap,
                ["skipped"] = Skipped.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                ["risks"] = Risks.Take(cap).Select(r => (object)new Dictionary<string, object?>
                    { ["id"] = r.Id, ["kind"] = r.Kind, ["count"] = r.Count, ["note"] = r.Note }).ToList(),
                ["axis_mm"] = Op == SpatialOp.Rotate ? Axis.ToList() : null,
                ["creates_copies"] = CreatesCopies,
                ["nothing"] = Changes.Count == 0,
                ["headline"] = $"{Changes.Count} change(s) across {Matched} element(s) would {Op.ToString().ToLowerInvariant()} (nothing changed yet)",
            };
            if (Op == SpatialOp.Array) d["copies_per_source"] = CopiesPerSource;
            if (Op == SpatialOp.Align) d["target"] = new Dictionary<string, object?> { ["axis"] = AlignAxis, ["at_mm"] = Math.Round(AlignTargetMm, 1) };
            if (Op == SpatialOp.Mirror) d["plane"] = new Dictionary<string, object?> { ["axis"] = MirrorAxis, ["at_mm"] = Math.Round(MirrorAtMm, 1), ["copy"] = CreatesCopies };
            if (Risks.Any(r => r.Kind == "invalid_array")) d["error"] = Risks.First(r => r.Kind == "invalid_array").Note;
            return d;
        }
    }

    public static class SpatialVerification
    {
        public static Dictionary<string, object?> Positions(IReadOnlyDictionary<long, Vec> expected, Func<long, Vec?> readBack, double toleranceMm)
        {
            int matches = 0;
            var mismatches = new List<object>();
            foreach (var kv in expected)
            {
                var actual = readBack(kv.Key);
                if (actual.HasValue && actual.Value.DistanceTo(kv.Value) <= toleranceMm) { matches++; continue; }
                if (mismatches.Count < 50)
                    mismatches.Add(new Dictionary<string, object?>
                    { ["id"] = kv.Key, ["expected_mm"] = kv.Value.ToList(), ["actual_mm"] = actual?.ToList() });
            }
            return new() { ["checked"] = expected.Count, ["matches"] = matches, ["mismatches"] = mismatches };
        }

        public static Dictionary<string, object?> Absent(IEnumerable<long> deletedIds, Func<long, bool> stillExists)
        {
            var ids = deletedIds.ToList();
            int matches = 0;
            var mismatches = new List<object>();
            foreach (var id in ids)
            {
                if (!stillExists(id)) { matches++; continue; }
                if (mismatches.Count < 50) mismatches.Add(new Dictionary<string, object?> { ["id"] = id, ["expected"] = "deleted", ["actual"] = "still exists" });
            }
            return new() { ["checked"] = ids.Count, ["matches"] = matches, ["mismatches"] = mismatches };
        }
    }
}
