// BinaVibe.Spatial — spatial edit planner, Revit-free
// (bina-ai R2 Task 23, family A: move / copy / rotate / delete).
//
// Before any transaction: which elements change and where they end up, which
// are skipped and why (pinned, grouped, datum), and the risks the drafter must
// see (hosted dependents that move with — or die with — the host). After the
// commit: positions re-read and compared within a tolerance, absence checked
// for delete, new ids for copy. All in millimetres, project coordinates.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Spatial
{
    public readonly record struct Vec(double X, double Y, double Z)
    {
        public Vec Plus(Vec o) => new(X + o.X, Y + o.Y, Z + o.Z);
        public double DistanceTo(Vec o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
        public List<double> ToList() => new() { Math.Round(X, 1), Math.Round(Y, 1), Math.Round(Z, 1) };
    }

    public enum SpatialOp { Move, Copy, Rotate, Delete }

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
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public Vec From { get; init; }
        public Vec To { get; init; }
        public int Dependents { get; init; }
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

        private SpatialPlan(SpatialOp op, List<SpatialChange> changes, Dictionary<string, int> skipped, List<SpatialRisk> risks,
                            int matched, Vec axis, double angle, Vec vector)
        { Op = op; Changes = changes; Skipped = skipped; Risks = risks; Matched = matched; Axis = axis; AngleDeg = angle; Vector = vector; }

        public static SpatialPlan Build(IEnumerable<SpatialRow> rows, SpatialOp op, Vec? vector, double angleDeg, Vec? axis)
        {
            var list = rows.ToList();
            var vec = vector ?? new Vec(0, 0, 0);
            var skipped = new Dictionary<string, int> { ["pinned"] = 0, ["grouped"] = 0, ["datum"] = 0 };
            var changes = new List<SpatialChange>();
            var risks = new List<SpatialRisk>();

            // rotation axis: caller's, else centroid of the eligible elements
            var eligible = list.Where(r => !r.IsDatum && !r.Pinned && !r.Grouped).ToList();
            Vec ax = axis ?? (eligible.Count > 0
                ? new Vec(eligible.Average(r => r.X), eligible.Average(r => r.Y), 0)
                : new Vec(0, 0, 0));
            double rad = angleDeg * Math.PI / 180.0;

            foreach (var r in list)
            {
                if (r.IsDatum) { skipped["datum"]++; continue; }
                if (op != SpatialOp.Delete && r.Pinned) { skipped["pinned"]++; continue; }
                if (op != SpatialOp.Delete && r.Grouped) { skipped["grouped"]++; continue; }

                Vec to = op switch
                {
                    SpatialOp.Move => r.Position.Plus(vec),
                    SpatialOp.Copy => r.Position.Plus(vec),
                    SpatialOp.Rotate => RotateAbout(r.Position, ax, rad),
                    _ => r.Position,
                };
                changes.Add(new SpatialChange { Id = r.Id, Name = r.Name, From = r.Position, To = to, Dependents = r.Dependents });
                if (r.Dependents > 0)
                {
                    risks.Add(op == SpatialOp.Delete
                        ? new SpatialRisk { Id = r.Id, Kind = "dependents_deleted", Count = r.Dependents, Note = $"{r.Dependents} hosted element(s) will be deleted too" }
                        : new SpatialRisk { Id = r.Id, Kind = "hosted_dependents", Count = r.Dependents, Note = $"{r.Dependents} hosted element(s) move with it" });
                }
            }
            return new SpatialPlan(op, changes, skipped, risks, list.Count, ax, angleDeg, vec);
        }

        private static Vec RotateAbout(Vec p, Vec axis, double rad)
        {
            double dx = p.X - axis.X, dy = p.Y - axis.Y;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            // Revit rotates counter-clockwise for a positive angle about +Z;
            // the planner mirrors that convention with y negated for +90° from (0,0) about (500,0) → (500,-500).
            double rx = axis.X + dx * c + dy * s;
            double ry = axis.Y - dx * s + dy * c;
            return new Vec(rx, ry, p.Z);
        }

        public Dictionary<string, object?> ToPreview(int cap = 200) => new()
        {
            ["ok"] = true,
            ["dry_run"] = true,
            ["op"] = Op.ToString().ToLowerInvariant(),
            ["matched"] = Matched,
            ["would_change"] = Changes.Count,
            ["preview"] = Changes.Take(cap).Select(c => (object)new Dictionary<string, object?>
            {
                ["id"] = c.Id, ["name"] = c.Name,
                ["from_mm"] = c.From.ToList(), ["to_mm"] = c.To.ToList(),
                ["dependents"] = c.Dependents,
            }).ToList(),
            ["preview_truncated"] = Changes.Count > cap,
            ["skipped"] = Skipped.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
            ["risks"] = Risks.Take(cap).Select(r => (object)new Dictionary<string, object?>
                { ["id"] = r.Id, ["kind"] = r.Kind, ["count"] = r.Count, ["note"] = r.Note }).ToList(),
            ["axis_mm"] = Op == SpatialOp.Rotate ? Axis.ToList() : null,
            ["nothing"] = Changes.Count == 0,
            ["headline"] = $"{Changes.Count} of {Matched} would {Op.ToString().ToLowerInvariant()} (nothing changed yet)",
        };
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
