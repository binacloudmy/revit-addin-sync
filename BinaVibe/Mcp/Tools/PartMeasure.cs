// PartMeasure — one part, measured. The addin's half of "expected vs actual".
// The backend computed every number in `expected` (design_parts.py); this
// file must NEVER invent a tolerance or default — read them from the part,
// so the two sides cannot drift apart.
//
// `expected` is MEASURABLE KEYS ONLY (see `Recognized` below). Anything else
// the backend wants to say about a part rides in a sibling `info` dict, and a
// key this file does not recognize caps the part at "unverified" — the two
// sides agreeing on a vocabulary is what stops "ok" meaning "I ignored it".
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

        /// <summary>Every key this file knows how to MEASURE (or that tells it
        /// how to measure). The backend's `expected` may carry nothing else:
        /// design_parts.py puts narration — a roof's `kind`, the walls the
        /// doors are hosted in — in a sibling `info` dict instead.
        ///
        /// Why this list is enforced below rather than ignored: an unknown key
        /// used to fall through silently, so a part could be reported "ok"
        /// while the one thing the backend actually cared about was never
        /// checked. A check nobody ran is not a pass.</summary>
        private static readonly HashSet<string> Recognized =
            new HashSet<string>(StringComparer.Ordinal) {
                "count", "bbox_mm", "open_ends", "pitch_deg", "status",
                "measure", "tolerance_mm", "tolerance_deg", "tolerance_z_mm" };

        public static PartResult Measure(Document doc, string partId,
                                         JsonElement expected,
                                         IReadOnlyList<ElementId> owned)
        {
            var result = MeasureCore(doc, partId, expected, owned);
            var unknown = UnknownKeys(expected);
            if (unknown.Count == 0) return result;

            // At BEST unverified — never ok. A part that failed the checks it
            // could run stays failed; one that passed them still carries a key
            // nothing measured, so it has not been verified.
            if (result.Status == "ok") result.Status = "unverified";
            result.Measured = (string.IsNullOrEmpty(result.Measured)
                                   ? "" : result.Measured + " ")
                + $"unrecognized expected key(s): {string.Join(", ", unknown)} — "
                + "nothing here measures them, so this part is not verified";
            return result;
        }

        private static List<string> UnknownKeys(JsonElement expected)
        {
            var unknown = new List<string>();
            if (expected.ValueKind != JsonValueKind.Object) return unknown;
            foreach (var p in expected.EnumerateObject())
                if (!Recognized.Contains(p.Name)) unknown.Add(p.Name);
            unknown.Sort(StringComparer.Ordinal);
            return unknown;
        }

        private static PartResult MeasureCore(Document doc, string partId,
                                              JsonElement expected,
                                              IReadOnlyList<ElementId> owned)
        {
            if (expected.TryGetProperty("status", out var st)
                && st.GetString() == "not_implemented")
                return new PartResult { Status = "unverified",
                                        Predicted = "not_implemented" };

            var checks = new List<(bool ok, string pred, string meas)>();
            double tolMm = Num(expected, "tolerance_mm") ?? 50;
            // Measurement HINTS, shipped by the backend with the prediction.
            // They exist because a solid bounding box answers a different
            // question for each element type: a wall's solid is offset half its
            // thickness from the centreline the solver placed, a slab's z-min is
            // its (type-dependent) thickness, and a roof's z spans a ridge the
            // plan extents say nothing about. Absent hints, nothing changes.
            string mode = expected.TryGetProperty("measure", out var mv)
                          && mv.ValueKind == JsonValueKind.String
                ? (mv.GetString() ?? "").Trim().ToLowerInvariant() : "";
            double? tolZMm = Num(expected, "tolerance_z_mm");

            if (expected.TryGetProperty("count", out var cnt))
            {
                int want = cnt.GetInt32();
                checks.Add((owned.Count == want, $"count={want}", $"count={owned.Count}"));
            }
            if (expected.TryGetProperty("bbox_mm", out var bb))
            {
                var (min, max) = ReadBox(bb);

                // Precedence: an explicit `measure` outranks `tolerance_z_mm`, so
                // "centerline" ignores it (a wall's z comes from its own level and
                // height, not from a roof's ridge slack) and "top" compares z-max
                // against `tolerance_mm`; `tolerance_z_mm` selects the roof rule
                // only when no `measure` was sent.
                if (mode == "centerline")
                {
                    // Walls: the solver placed CENTRELINES, so measure
                    // centrelines. Comparing its prediction against the solid
                    // makes every wall fail by half a wall thickness.
                    var cl = CenterlineBox(doc, owned);
                    if (cl == null)
                        return new PartResult { Status = "unverified",
                            Predicted = Fmt(min, max),
                            Measured = "no wall centreline to measure" };
                    checks.Add((Within(cl.Value.Min, cl.Value.Max, min, max, tolMm),
                                Fmt(min, max), Fmt(cl.Value.Min, cl.Value.Max)));
                }
                else
                {
                    var actual = UnionBox(doc, owned);
                    if (actual == null)
                        return new PartResult { Status = "failed",
                            Predicted = Fmt(min, max), Measured = "no geometry" };

                    bool ok;
                    if (mode == "top")
                    {
                        // Slabs: the prediction is the TOP surface. z-min is the
                        // slab type's thickness, which the backend cannot know
                        // and must not be judged on — so only the top is checked.
                        double t = tolMm / FT;
                        ok = actual.Min.X >= min.X - t && actual.Min.Y >= min.Y - t
                          && actual.Max.X <= max.X + t && actual.Max.Y <= max.Y + t
                          && Math.Abs(actual.Max.Z - max.Z) <= t;
                    }
                    else if (tolZMm.HasValue)
                    {
                        // Roofs: plan extents are tight, height is not — a ridge
                        // depends on a pitch the type may override.
                        double t = tolMm / FT, tz = tolZMm.Value / FT;
                        ok = actual.Min.X >= min.X - t && actual.Min.Y >= min.Y - t
                          && actual.Max.X <= max.X + t && actual.Max.Y <= max.Y + t
                          && actual.Min.Z >= min.Z - tz && actual.Max.Z <= max.Z + tz;
                    }
                    else
                    {
                        ok = Within(actual.Min, actual.Max, min, max, tolMm);
                    }
                    checks.Add((ok, Fmt(min, max), FmtBox(actual)));
                }
            }
            if (expected.TryGetProperty("open_ends", out var oe))
            {
                int want = oe.GetInt32();
                var where = new List<string>();
                int got = CountOpenEnds(doc, owned, where);
                // Name WHERE — "open_ends=1" without a coordinate sent the
                // 2026-08-11 smoke's repair round in blind. The location is
                // the repair.
                var locs = where.Count > 0 ? $" at [{string.Join("; ", where)}]" : "";
                checks.Add((got <= want, $"open_ends={want}", $"open_ends={got}{locs}"));
            }
            bool unmeasurable = false;
            if (expected.TryGetProperty("pitch_deg", out var pd))
            {
                double want = pd.GetDouble();
                double tolDeg = Num(expected, "tolerance_deg") ?? 1;
                double? got = MeasurePitch(doc, owned);
                if (got == null)
                {
                    // NOT an early return. On 2026-08-11 an extrusion-fallback
                    // roof landed DISPLACED; its bbox check had already failed,
                    // but this early-returned "unverified" and threw that
                    // verdict away — the one visible defect in the screenshot
                    // was the one the scorecard did not report. Unmeasurable
                    // pitch degrades ok -> unverified; it never pardons a
                    // failed bbox.
                    unmeasurable = true;
                    checks.Add((true, $"pitch={want}", "pitch unmeasurable"));
                }
                else
                {
                    checks.Add((Math.Abs(got.Value - want) <= tolDeg,
                                $"pitch={want}", $"pitch={got:F1}"));
                }
            }
            if (checks.Count == 0)
                return new PartResult { Status = "unverified",
                                        Predicted = expected.ToString() };
            var failedAny = checks.Any(c => !c.ok);
            return new PartResult
            {
                Status = failedAny ? "failed" : (unmeasurable ? "unverified" : "ok"),
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

        private static bool Within(XYZ aMin, XYZ aMax, XYZ min, XYZ max, double tolMm)
        {
            double t = tolMm / FT;
            return aMin.X >= min.X - t && aMin.Y >= min.Y - t && aMin.Z >= min.Z - t
                && aMax.X <= max.X + t && aMax.Y <= max.Y + t && aMax.Z <= max.Z + t;
        }

        /// <summary>The box the WALL CENTRELINES occupy: plan XY from every
        /// LocationCurve endpoint, z from each wall's own base (level elevation +
        /// base offset) to its top.
        ///
        /// Top is read from the top CONSTRAINT when there is one, because
        /// Unconnected Height is stale on a top-constrained wall (same trap
        /// Inspectors.cs documents for duplicate detection) — falling back to
        /// Unconnected Height only for a genuinely unconnected wall.
        ///
        /// Returns null when nothing here is a wall with a location curve: the
        /// caller reports that as UNVERIFIED, never as a pass.</summary>
        private static (XYZ Min, XYZ Max)? CenterlineBox(Document doc, IReadOnlyList<ElementId> ids)
        {
            double minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
            bool any = false;
            foreach (var id in ids)
            {
                if (doc.GetElement(id) is not Wall w) continue;
                if (w.Location is not LocationCurve lc || lc.Curve == null) continue;

                double baseElev = 0;
                if (w.LevelId.Value != ElementId.InvalidElementId.Value
                    && doc.GetElement(w.LevelId) is Level bl) baseElev = bl.Elevation;
                baseElev += w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;

                double top = baseElev;
                var topId = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId();
                if (topId != null && topId.Value != ElementId.InvalidElementId.Value
                    && doc.GetElement(topId) is Level tl)
                    top = tl.Elevation + (w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0);
                else
                    top = baseElev + (w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0);
                // PROFILE walls (gable ends) answer 0 to the height params —
                // their top follows the roof line. The solid bbox knows; use
                // its z when it exceeds what the params claim.
                var bb = w.get_BoundingBox(null);
                if (bb != null)
                {
                    if (bb.Max.Z > top) top = bb.Max.Z;
                    if (bb.Min.Z < baseElev) baseElev = bb.Min.Z;
                }

                foreach (var p in new[] { lc.Curve.GetEndPoint(0), lc.Curve.GetEndPoint(1) })
                {
                    if (!any)
                    {
                        minX = maxX = p.X; minY = maxY = p.Y;
                        minZ = Math.Min(baseElev, top); maxZ = Math.Max(baseElev, top);
                        any = true;
                        continue;
                    }
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                    minZ = Math.Min(minZ, Math.Min(baseElev, top));
                    maxZ = Math.Max(maxZ, Math.Max(baseElev, top));
                }
            }
            if (!any) return null;
            return (new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        private static int CountOpenEnds(Document doc, IReadOnlyList<ElementId> ids,
                                         List<string>? where = null)
        {
            int open = 0;
            foreach (var id in ids)
            {
                if (doc.GetElement(id) is not Wall w) continue;
                for (int end = 0; end <= 1; end++)
                    if (w.Location is LocationCurve lc
                        && !JoinedAtEnd(doc, w, lc, end, ids))
                    {
                        open++;
                        var p = lc.Curve.GetEndPoint(end);
                        where?.Add($"({p.X * FT:F0},{p.Y * FT:F0})mm");
                    }
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
