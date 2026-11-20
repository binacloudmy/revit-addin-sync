#nullable enable
// RunCompare - phase 2 of the CAD electrical to BIM work (the conduit exit check).
//
// How much of a drafter's conduit and cable tray lies along the runs route_conduit made, and
// the reverse: each segment is walked in short steps, and a step counts as covered when it
// lies within the tolerance of a segment of the other set. Reads the "runs" list that
// dump_electrical_reference writes with include_runs. In plan (x, y) by default; with 3D, Z
// counts too.
//
// Pure: System, Linq and System.Text.Json, so it is linked into Tests and the headless CLI.
// It must also compile on net48.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One straight conduit or cable tray segment, model mm.</summary>
    internal sealed class RunSegment
    {
        public long Id;
        /// <summary>"conduit" | "cable_tray"</summary>
        public string Kind = "conduit";
        public string Type = "";
        public double X1, Y1, Z1, X2, Y2, Z2;

        public double Length(bool in3d) =>
            Math.Sqrt(((X2 - X1) * (X2 - X1)) + ((Y2 - Y1) * (Y2 - Y1)) + (in3d ? (Z2 - Z1) * (Z2 - Z1) : 0));
    }

    internal sealed class RunCompareResult
    {
        public double ToleranceMm;
        public bool In3d;
        public int ReferenceSegments, CreatedSegments;
        public double ReferenceLengthMm, CreatedLengthMm;
        /// <summary>Reference length lying within the tolerance of a created segment.</summary>
        public double ReferenceCoveredMm;
        /// <summary>Created length lying within the tolerance of a reference segment.</summary>
        public double CreatedCoveredMm;

        public double ReferenceCoveredShare => ReferenceLengthMm > 0 ? ReferenceCoveredMm / ReferenceLengthMm : 0;
        public double CreatedCoveredShare => CreatedLengthMm > 0 ? CreatedCoveredMm / CreatedLengthMm : 0;
        /// <summary>Created length along no reference segment.</summary>
        public double ExtraLengthMm => CreatedLengthMm - CreatedCoveredMm;

        public Dictionary<string, object?> ToWire() => new Dictionary<string, object?>
        {
            ["tolerance_mm"] = ToleranceMm,
            ["in_3d"] = In3d,
            ["reference_segments"] = ReferenceSegments,
            ["created_segments"] = CreatedSegments,
            ["reference_length_m"] = Math.Round(ReferenceLengthMm / 1000, 2),
            ["created_length_m"] = Math.Round(CreatedLengthMm / 1000, 2),
            ["reference_covered_share"] = Math.Round(ReferenceCoveredShare, 4),
            ["created_covered_share"] = Math.Round(CreatedCoveredShare, 4),
            ["extra_length_m"] = Math.Round(ExtraLengthMm / 1000, 2),
        };
    }

    internal static class RunCompare
    {
        /// <summary>Steps along a segment are at most this long, mm.</summary>
        public const double StepMm = 25;

        /// <summary>The segments of a dump's "runs" list, of one kind when given.</summary>
        public static List<RunSegment> Read(JsonElement root, string? kind = null)
        {
            var segments = new List<RunSegment>();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("runs", out JsonElement runs) ||
                runs.ValueKind != JsonValueKind.Array)
                return segments;

            foreach (JsonElement row in runs.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                string rowKind = row.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(kind) && !string.Equals(rowKind, kind, StringComparison.OrdinalIgnoreCase)) continue;
                double[]? a = Xyz(row, "start_mm"), b = Xyz(row, "end_mm");
                if (a == null || b == null) continue;
                segments.Add(new RunSegment
                {
                    Id = row.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long n) ? n : 0,
                    Kind = rowKind,
                    Type = row.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                    X1 = a[0], Y1 = a[1], Z1 = a[2], X2 = b[0], Y2 = b[1], Z2 = b[2],
                });
            }
            return segments;
        }

        public static RunCompareResult Compare(IReadOnlyList<RunSegment> reference, IReadOnlyList<RunSegment> created,
                                               double toleranceMm = 100, bool in3d = false)
        {
            var result = new RunCompareResult
            {
                ToleranceMm = toleranceMm,
                In3d = in3d,
                ReferenceSegments = reference.Count,
                CreatedSegments = created.Count,
                ReferenceLengthMm = reference.Sum(s => s.Length(in3d)),
                CreatedLengthMm = created.Sum(s => s.Length(in3d)),
            };
            result.ReferenceCoveredMm = Covered(reference, created, toleranceMm, in3d);
            result.CreatedCoveredMm = Covered(created, reference, toleranceMm, in3d);
            return result;
        }

        public static string Summary(RunCompareResult r) =>
            string.Format(CultureInfo.InvariantCulture,
                "reference {0} segments, {1:0.00} m; created {2} segments, {3:0.00} m ({4})\n" +
                "reference covered within {5:0} mm: {6:0.00} m ({7:0.0}%)\n" +
                "created along the reference: {8:0.00} m ({9:0.0}%); extra {10:0.00} m\n",
                r.ReferenceSegments, r.ReferenceLengthMm / 1000, r.CreatedSegments, r.CreatedLengthMm / 1000,
                r.In3d ? "3D" : "plan", r.ToleranceMm, r.ReferenceCoveredMm / 1000, r.ReferenceCoveredShare * 100,
                r.CreatedCoveredMm / 1000, r.CreatedCoveredShare * 100, r.ExtraLengthMm / 1000);

        /// <summary>The length of <paramref name="from"/> lying within the tolerance of a segment of
        /// <paramref name="to"/>, by steps of at most StepMm.</summary>
        private static double Covered(IReadOnlyList<RunSegment> from, IReadOnlyList<RunSegment> to, double tol, bool in3d)
        {
            if (from.Count == 0 || to.Count == 0) return 0;
            const double cell = 1000;
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < to.Count; i++)
            {
                RunSegment s = to[i];
                long i0 = Cell(Math.Min(s.X1, s.X2) - tol, cell), i1 = Cell(Math.Max(s.X1, s.X2) + tol, cell);
                long j0 = Cell(Math.Min(s.Y1, s.Y2) - tol, cell), j1 = Cell(Math.Max(s.Y1, s.Y2) + tol, cell);
                for (long x = i0; x <= i1; x++)
                {
                    for (long y = j0; y <= j1; y++)
                    {
                        long key = Key(x, y);
                        if (!grid.TryGetValue(key, out List<int>? list)) grid[key] = list = new List<int>();
                        list.Add(i);
                    }
                }
            }

            double covered = 0;
            foreach (RunSegment s in from)
            {
                double length = s.Length(in3d);
                if (length <= 0) continue;
                int steps = Math.Max(1, (int)Math.Ceiling(length / StepMm));
                double piece = length / steps;
                for (int k = 0; k < steps; k++)
                {
                    double f = (k + 0.5) / steps;
                    double x = s.X1 + ((s.X2 - s.X1) * f), y = s.Y1 + ((s.Y2 - s.Y1) * f), z = s.Z1 + ((s.Z2 - s.Z1) * f);
                    if (!grid.TryGetValue(Key(Cell(x, cell), Cell(y, cell)), out List<int>? near)) continue;
                    if (near.Any(i => Distance(x, y, z, to[i], in3d) <= tol)) covered += piece;
                }
            }
            return covered;
        }

        private static double Distance(double x, double y, double z, RunSegment s, bool in3d)
        {
            double dx = s.X2 - s.X1, dy = s.Y2 - s.Y1, dz = in3d ? s.Z2 - s.Z1 : 0;
            double l2 = (dx * dx) + (dy * dy) + (dz * dz);
            double t = l2 > 0 ? (((x - s.X1) * dx) + ((y - s.Y1) * dy) + (in3d ? (z - s.Z1) * dz : 0)) / l2 : 0;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            double px = s.X1 + (t * dx) - x, py = s.Y1 + (t * dy) - y, pz = in3d ? s.Z1 + (t * dz) - z : 0;
            return Math.Sqrt((px * px) + (py * py) + (pz * pz));
        }

        private static long Cell(double v, double cell) => (long)Math.Floor(v / cell);

        private static long Key(long i, long j) => (i << 32) ^ (j & 0xffffffffL);

        private static double[]? Xyz(JsonElement row, string name)
        {
            if (!row.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array) return null;
            var nums = new List<double>();
            foreach (JsonElement n in v.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.Number) nums.Add(n.GetDouble());
            }
            if (nums.Count < 2) return null;
            return new[] { nums[0], nums[1], nums.Count > 2 ? nums[2] : 0 };
        }
    }
}
