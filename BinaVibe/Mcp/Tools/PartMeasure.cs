// PartMeasure — one part, measured. The addin's half of "expected vs actual".
// The backend computed every number in `expected` (design_parts.py); this
// file must NEVER invent a tolerance or default — read them from the part,
// so the two sides cannot drift apart.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;

namespace BinaVibe.Mcp.Tools
{
    internal sealed class PartResult
    {
        public string Status = "unverified";   // ok | failed | blocked | unverified
        public string Predicted = "";
        public string Measured = "";
    }

    internal static class PartMeasure
    {
        private const double FT = 304.8;

        public static PartResult Measure(Document doc, string partId,
                                         JsonElement expected,
                                         IReadOnlyList<ElementId> owned)
        {
            if (expected.TryGetProperty("status", out var st)
                && st.GetString() == "not_implemented")
                return new PartResult { Status = "unverified",
                                        Predicted = "not_implemented" };

            var checks = new List<(bool ok, string pred, string meas)>();
            double tolMm = Num(expected, "tolerance_mm") ?? 50;

            if (expected.TryGetProperty("count", out var cnt))
            {
                int want = cnt.GetInt32();
                checks.Add((owned.Count == want, $"count={want}", $"count={owned.Count}"));
            }
            if (expected.TryGetProperty("bbox_mm", out var bb))
            {
                var (min, max) = ReadBox(bb);
                var actual = UnionBox(doc, owned);
                if (actual == null)
                    return new PartResult { Status = "failed",
                        Predicted = Fmt(min, max), Measured = "no geometry" };
                bool ok = Within(actual, min, max, tolMm);
                checks.Add((ok, Fmt(min, max), FmtBox(actual)));
            }
            if (expected.TryGetProperty("open_ends", out var oe))
            {
                int want = oe.GetInt32();
                int got = CountOpenEnds(doc, owned);
                checks.Add((got <= want, $"open_ends={want}", $"open_ends={got}"));
            }
            if (expected.TryGetProperty("pitch_deg", out var pd))
            {
                double want = pd.GetDouble();
                double tolDeg = Num(expected, "tolerance_deg") ?? 1;
                double? got = MeasurePitch(doc, owned);
                if (got == null)
                    return new PartResult { Status = "unverified",
                        Predicted = $"pitch={want}", Measured = "pitch unmeasurable" };
                checks.Add((Math.Abs(got.Value - want) <= tolDeg,
                            $"pitch={want}", $"pitch={got:F1}"));
            }
            if (checks.Count == 0)
                return new PartResult { Status = "unverified",
                                        Predicted = expected.ToString() };
            return new PartResult
            {
                Status = checks.All(c => c.ok) ? "ok" : "failed",
                Predicted = string.Join(" ", checks.Select(c => c.pred)),
                Measured = string.Join(" ", checks.Select(c => c.meas)),
            };
        }

        private static double? Num(JsonElement o, string k) =>
            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : (double?)null;

        private static (XYZ min, XYZ max) ReadBox(JsonElement bb)
        {
            var pts = bb.EnumerateArray().Select(p =>
                p.EnumerateArray().Select(x => x.GetDouble() / FT).ToArray()).ToArray();
            return (new XYZ(pts[0][0], pts[0][1], pts[0][2]),
                    new XYZ(pts[1][0], pts[1][1], pts[1][2]));
        }

        private static BoundingBoxXYZ? UnionBox(Document doc, IReadOnlyList<ElementId> ids)
        {
            BoundingBoxXYZ? u = null;
            foreach (var id in ids)
            {
                var bb = doc.GetElement(id)?.get_BoundingBox(null);
                if (bb == null) continue;
                if (u == null) u = new BoundingBoxXYZ { Min = bb.Min, Max = bb.Max };
                else
                {
                    u.Min = new XYZ(Math.Min(u.Min.X, bb.Min.X),
                                    Math.Min(u.Min.Y, bb.Min.Y),
                                    Math.Min(u.Min.Z, bb.Min.Z));
                    u.Max = new XYZ(Math.Max(u.Max.X, bb.Max.X),
                                    Math.Max(u.Max.Y, bb.Max.Y),
                                    Math.Max(u.Max.Z, bb.Max.Z));
                }
            }
            return u;
        }

        private static bool Within(BoundingBoxXYZ a, XYZ min, XYZ max, double tolMm)
        {
            double t = tolMm / FT;
            return a.Min.X >= min.X - t && a.Min.Y >= min.Y - t && a.Min.Z >= min.Z - t
                && a.Max.X <= max.X + t && a.Max.Y <= max.Y + t && a.Max.Z <= max.Z + t;
        }

        private static int CountOpenEnds(Document doc, IReadOnlyList<ElementId> ids)
        {
            int open = 0;
            foreach (var id in ids)
            {
                if (doc.GetElement(id) is not Wall w) continue;
                for (int end = 0; end <= 1; end++)
                    if (w.Location is LocationCurve lc
                        && !JoinedAtEnd(doc, w, lc, end, ids)) open++;
            }
            return open;
        }

        private static bool JoinedAtEnd(Document doc, Wall w, LocationCurve lc,
                                        int end, IReadOnlyList<ElementId> all)
        {
            var p = lc.Curve.GetEndPoint(end);
            const double touch = 50 / FT;               // 50mm — same family as tolMm
            foreach (var id in all)
            {
                if (id == w.Id) continue;
                if (doc.GetElement(id) is Wall other
                    && other.Location is LocationCurve olc
                    && olc.Curve.Distance(p) < touch) return true;
            }
            // exterior walls also close an end — check every wall in the model
            var others = new FilteredElementCollector(doc).OfClass(typeof(Wall));
            foreach (Wall other in others)
            {
                if (other.Id == w.Id) continue;
                if (other.Location is LocationCurve olc
                    && olc.Curve.Distance(p) < touch) return true;
            }
            return false;
        }

        private static double? MeasurePitch(Document doc, IReadOnlyList<ElementId> ids)
        {
            foreach (var id in ids)
            {
                if (doc.GetElement(id) is not FootPrintRoof roof) continue;
                var slope = roof.get_Parameter(BuiltInParameter.ROOF_SLOPE);
                if (slope != null && slope.HasValue)
                    return Math.Atan(slope.AsDouble()) * 180.0 / Math.PI;
            }
            return null;
        }

        private static string Fmt(XYZ min, XYZ max) =>
            $"bbox=({min.X * FT:F0},{min.Y * FT:F0},{min.Z * FT:F0})-" +
            $"({max.X * FT:F0},{max.Y * FT:F0},{max.Z * FT:F0})mm";

        private static string FmtBox(BoundingBoxXYZ b) => Fmt(b.Min, b.Max);
    }
}
