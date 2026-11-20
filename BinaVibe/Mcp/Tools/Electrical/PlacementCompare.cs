#nullable enable
// PlacementCompare - the exit check of phase 1 of the CAD electrical to BIM work.
//
// The issue's rule (AI-390): "≥ 90% of sockets placed within 50mm of a hand-modelled reference
// floor on staging, with every miss explained by a flag (not silent)." So the check first lays
// the devices a drafter modelled (the reference) against the ones placed from the drawing, one
// to one, nearest first in plan, and counts the reference devices with a placed device within
// 50 mm. Every other reference device gets a miss code, the first that applies:
//   near_<N>mm          a placed device within 500 mm (moved along the wall or similar)
//   plan_flag:<flag>    the nearest plan point within 500 mm carries an exclusion flag
//   dropped:<reason>    the nearest dropped drawing symbol within 500 mm, and why it was dropped
//   not_in_drawing      no drawn symbol of any class within 1000 mm: the drafter modelled a
//                       socket the drawing does not show (needs a plan that lists every symbol)
//   unexplained         none of these; the rule requires none.
// A placed device with no reference within 50 mm is near_reference (a reference within 500 mm)
// or not_in_reference.
//
// Diagnostic only, not the issue's rule: a drafter slides sockets along a wall, so each match
// (up to the pass limits' reach) is split across the wall (along the placed point's facing) and
// along it, and passes when it is on the right face, not too far along, and at the right height.
//
// Pure: reads the JSON that dump_electrical_reference and plan_cad_electrical write, so the
// headless CLI runs it without Revit. Millimetres. Must compile on net48.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>One device of a dump: the reference model's, or the placed one's.</summary>
    internal sealed class RefDevice
    {
        public long Id;
        public string Category = "";
        public string FamilyType = "";
        public string Level = "";
        /// <summary>wall | ceiling | face | none | another host's category.</summary>
        public string HostKind = "";
        public double X, Y, Z;
        /// <summary>Unit plan facing; both zero when the dump had none.</summary>
        public double FacingX, FacingY;

        public bool HasFacing => (FacingX * FacingX) + (FacingY * FacingY) > 1e-6;
    }

    /// <summary>When a match passes the exit check, mm.</summary>
    internal sealed class PassLimits
    {
        /// <summary>Across the wall: the right wall and the right face.</summary>
        public double AcrossMm = 50;
        /// <summary>Along the wall: how far a drafter's shift may go.</summary>
        public double AlongMm = 500;
        /// <summary>Height, absolute.</summary>
        public double DzMm = 150;

        /// <summary>How far apart two devices may be and still be matched.</summary>
        public double ReachMm => Math.Max(PlacementCompare.ExplainMm, Math.Sqrt((AcrossMm * AcrossMm) + (AlongMm * AlongMm)));
    }

    /// <summary>A drawn instance the plan placed nothing for, and why.</summary>
    internal sealed class DroppedInstance
    {
        public string Block = "";
        /// <summary>The mapping row's role, or "unmapped"; empty in an older plan.</summary>
        public string Class = "";
        public string Reason = "";
        public double X, Y;
    }

    /// <summary>A placed device with no reference device within 50 mm, and why.</summary>
    internal sealed class ExtraRow
    {
        public RefDevice Placed = new RefDevice();
        /// <summary>near_reference | not_in_reference</summary>
        public string Code = "";
        /// <summary>The nearest reference device, when within 500 mm.</summary>
        public double? DistanceMm;
    }

    /// <summary>A Revit link's placement: origin in mm, unit basis vectors.</summary>
    internal sealed class LinkTransform
    {
        public string Name = "";
        public double OX, OY, OZ;
        public double[] BX = { 1, 0, 0 }, BY = { 0, 1, 0 }, BZ = { 0, 0, 1 };

        public void Apply(double x, double y, double z, out double hx, out double hy, out double hz)
        {
            hx = OX + (BX[0] * x) + (BY[0] * y) + (BZ[0] * z);
            hy = OY + (BX[1] * x) + (BY[1] * y) + (BZ[1] * z);
            hz = OZ + (BX[2] * x) + (BY[2] * y) + (BZ[2] * z);
        }
    }

    internal sealed class CompareRow
    {
        public RefDevice Reference = new RefDevice();
        public RefDevice? Placed;
        /// <summary>hit | near | far | miss</summary>
        public string Outcome = "miss";
        public double? DistanceMm, DzMm;
        /// <summary>The plan offset across the wall (along the facing) and along it, absolute.</summary>
        public double? AcrossMm, AlongMm;
        /// <summary>"facing", or "axis" when no facing was known (the smaller axis is across).</summary>
        public string? Split;
        public bool? HostAgrees;
        /// <summary>Null within 50 mm; else the miss code: near_&lt;N&gt;mm, plan_flag:&lt;flag&gt;,
        /// dropped:&lt;reason&gt;, not_in_drawing or unexplained. Never blank on a miss.</summary>
        public string? Explanation;
        /// <summary>On a near_ miss: the placed device and its plan offset across and along.</summary>
        public long? NearPlacedId;
        public double? NearAcrossMm, NearAlongMm;

        /// <summary>The issue's rule: a placed device within 50 mm, one to one.</summary>
        public bool Within => Outcome == "hit";
    }

    internal sealed class CompareResult
    {
        public List<CompareRow> Rows = new List<CompareRow>();
        /// <summary>Placed devices left unmatched within the diagnostic reach.</summary>
        public List<RefDevice> Extra = new List<RefDevice>();
        /// <summary>Every placed device with no reference device within 50 mm, with its code.</summary>
        public List<ExtraRow> ExtraRows = new List<ExtraRow>();
        public double ToleranceMm;
        public PassLimits Limits = new PassLimits();
        /// <summary>The diagnostic shares' denominator, the sockets drawn on the floor's plan,
        /// when the caller knows it (the plan's points); else the placed devices.</summary>
        public int? Drawn;
        /// <summary>A plan was given: plan_flag and dropped can explain a miss.</summary>
        public bool PlanGiven;
        /// <summary>The plan lists every drawn symbol: not_in_drawing can explain a miss.</summary>
        public bool CensusComplete;

        /// <summary>The issue's required share of reference devices within 50 mm.</summary>
        public const double RequiredShare = 0.9;

        /// <summary>The issue's metric: reference devices with a placed device within 50 mm.</summary>
        public int Within => Rows.Count(r => r.Within);
        public int IssueMisses => Rows.Count - Within;
        public double IssueShare => Rows.Count == 0 ? 0.0 : (double)Within / Rows.Count;
        public bool SharePasses => Rows.Count > 0 && IssueShare >= RequiredShare - 1e-9;
        /// <summary>The exit gate: the share holds and every miss is explained.</summary>
        public bool GatePasses => SharePasses && Unexplained == 0;

        /// <summary>A miss code's family: every near_&lt;N&gt;mm counts as near_&lt;N&gt;mm.</summary>
        public static string CodeFamily(string code) => code.StartsWith("near_", StringComparison.Ordinal) ? "near_<N>mm" : code;

        /// <summary>The miss codes by family, most first.</summary>
        public List<KeyValuePair<string, int>> MissCodes() =>
            Rows.Where(r => !r.Within).GroupBy(r => CodeFamily(r.Explanation ?? "unexplained"), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).ToList();

        public List<KeyValuePair<string, int>> ExtraCodes() =>
            ExtraRows.GroupBy(e => e.Code, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).ToList();

        public int Hits => Rows.Count(r => r.Outcome == "hit");
        public int Near => Rows.Count(r => r.Outcome == "near");
        public int Far => Rows.Count(r => r.Outcome == "far");
        public int Misses => Rows.Count(r => r.Outcome == "miss");
        public int Unexplained => Rows.Count(r => !r.Within && (r.Explanation ?? "unexplained") == "unexplained");
        public int Matched => Rows.Count(r => r.Placed != null);

        public bool AcrossOk(CompareRow r) => r.AcrossMm.HasValue && r.AcrossMm.Value <= Limits.AcrossMm;
        public bool AlongOk(CompareRow r) => r.AlongMm.HasValue && r.AlongMm.Value <= Limits.AlongMm;
        public bool HeightOk(CompareRow r) => r.DzMm.HasValue && Math.Abs(r.DzMm.Value) <= Limits.DzMm;
        public bool Passes(CompareRow r) => AcrossOk(r) && AlongOk(r) && HeightOk(r);

        public int AcrossOkCount => Rows.Count(AcrossOk);
        public int AlongOkCount => Rows.Count(AlongOk);
        public int HeightOkCount => Rows.Count(HeightOk);
        public int PassCount => Rows.Count(Passes);

        /// <summary>Drawn when known, else the placed devices (matched plus extra).</summary>
        public int Denominator => Drawn ?? (Matched + Extra.Count);

        public Dictionary<string, object?> ToWire()
        {
            List<CompareRow> matched = Rows.Where(r => r.Placed != null).ToList();
            List<double> dz = matched.Where(r => r.DzMm.HasValue).Select(r => Math.Abs(r.DzMm!.Value)).ToList();
            int denominator = Denominator;
            var diagnostic = new Dictionary<string, object?>
            {
                ["note"] = "not the issue's rule: the across/along/height split of the matches within the reach",
                ["reach_mm"] = Math.Round(Limits.ReachMm, 1),
                ["hits"] = Hits,
                ["near"] = Near,
                ["far"] = Far,
                ["unmatched"] = Misses,
                ["limits"] = new Dictionary<string, object?>
                {
                    ["across_mm"] = Limits.AcrossMm,
                    ["along_mm"] = Limits.AlongMm,
                    ["dz_mm"] = Limits.DzMm,
                },
                ["matched"] = matched.Count,
                ["across_ok"] = AcrossOkCount,
                ["along_ok"] = AlongOkCount,
                ["height_ok"] = HeightOkCount,
                ["pass"] = PassCount,
                ["denominator"] = denominator,
                ["denominator_is"] = Drawn.HasValue ? "drawn" : "placed",
                ["pass_share"] = denominator == 0 ? 0.0 : Math.Round((double)PassCount / denominator, 3),
                ["across_ok_share"] = denominator == 0 ? 0.0 : Math.Round((double)AcrossOkCount / denominator, 3),
                ["height"] = new Dictionary<string, object?>
                {
                    ["matched"] = dz.Count,
                    ["mean_abs_dz_mm"] = dz.Count == 0 ? (double?)null : Math.Round(dz.Average(), 1),
                    ["max_abs_dz_mm"] = dz.Count == 0 ? (double?)null : Math.Round(dz.Max(), 1),
                },
                ["host_agreement"] = new Dictionary<string, object?>
                {
                    ["matched"] = matched.Count,
                    ["agree"] = matched.Count(r => r.HostAgrees == true),
                },
                ["unmatched_placements"] = Extra.Count,
            };
            return new Dictionary<string, object?>
            {
                ["issue"] = new Dictionary<string, object?>
                {
                    ["rule"] = PlacementCompare.IssueRule,
                    ["tolerance_mm"] = ToleranceMm,
                    ["reference"] = Rows.Count,
                    ["within_tolerance"] = Within,
                    ["share"] = Math.Round(IssueShare, 3),
                    ["required_share"] = RequiredShare,
                    ["share_passes"] = SharePasses,
                    ["misses"] = IssueMisses,
                    ["unexplained"] = Unexplained,
                    ["gate_passes"] = GatePasses,
                },
                ["miss_codes"] = MissCodes().ToDictionary(k => k.Key, k => (object?)k.Value),
                ["explainers"] = new Dictionary<string, object?>
                {
                    ["plan"] = PlanGiven,
                    ["every_drawn_symbol"] = CensusComplete,
                },
                ["extra_placements"] = new Dictionary<string, object?>
                {
                    ["count"] = ExtraRows.Count,
                    ["codes"] = ExtraCodes().ToDictionary(k => k.Key, k => (object?)k.Value),
                },
                ["diagnostic"] = diagnostic,
                ["rows"] = Rows.Select(r => (object?)new Dictionary<string, object?>
                {
                    ["ref_id"] = r.Reference.Id,
                    ["within_tolerance"] = r.Within,
                    ["miss_code"] = r.Within ? null : r.Explanation ?? "unexplained",
                    ["near_placed_id"] = r.NearPlacedId,
                    ["near_across_mm"] = r.NearAcrossMm.HasValue ? Math.Round(r.NearAcrossMm.Value, 1) : (double?)null,
                    ["near_along_mm"] = r.NearAlongMm.HasValue ? Math.Round(r.NearAlongMm.Value, 1) : (double?)null,
                    ["outcome"] = r.Outcome,
                    ["distance_mm"] = r.DistanceMm.HasValue ? Math.Round(r.DistanceMm.Value, 1) : (double?)null,
                    ["placed_id"] = r.Placed?.Id,
                    ["across_mm"] = r.AcrossMm.HasValue ? Math.Round(r.AcrossMm.Value, 1) : (double?)null,
                    ["along_mm"] = r.AlongMm.HasValue ? Math.Round(r.AlongMm.Value, 1) : (double?)null,
                    ["split"] = r.Split,
                    ["pass"] = r.Placed != null ? Passes(r) : (bool?)null,
                    ["dz_mm"] = r.DzMm.HasValue ? Math.Round(r.DzMm.Value, 1) : (double?)null,
                    ["host_agrees"] = r.HostAgrees,
                }).ToList(),
                ["extra_rows"] = ExtraRows.Select(e => (object?)new Dictionary<string, object?>
                {
                    ["placed_id"] = e.Placed.Id,
                    ["code"] = e.Code,
                    ["nearest_reference_mm"] = e.DistanceMm.HasValue ? Math.Round(e.DistanceMm.Value, 1) : (double?)null,
                }).ToList(),
            };
        }
    }

    internal static class PlacementCompare
    {
        /// <summary>The issue's rule, verbatim (AI-390).</summary>
        public const string IssueRule =
            "≥ 90% of sockets placed within 50mm of a hand-modelled reference floor on staging, with every miss explained by a flag (not silent).";

        /// <summary>The diagnostic outcome "near" (a match within this) against "far".</summary>
        public const double NearMm = 300;

        /// <summary>How far a placed device, plan point or dropped symbol explains a miss.</summary>
        public const double ExplainMm = 500;

        /// <summary>With no drawn symbol of any class this close, the drawing does not show the
        /// reference device.</summary>
        public const double CensusMm = 1000;

        /// <summary>Plan flags that explain a miss: those that exclude a point, and above_wall_top.</summary>
        public static bool FlagExplainsMiss(string flag) =>
            flag == "above_wall_top" || (CadSymbolResolve.FlagExcludes.TryGetValue(flag, out bool x) && x);

        /// <summary>
        /// Matches reference devices to placed ones one to one, by ascending plan distance, so
        /// every pair within <paramref name="toleranceMm"/> is taken before any pair beyond it.
        /// <paramref name="refLink"/>, when given, carries the reference into the placed model's
        /// coordinates first (the reference model is linked there). <paramref name="censusComplete"/>
        /// says the plan points and dropped symbols list every symbol drawn on the plan, so a
        /// miss far from all of them is not_in_drawing.
        /// </summary>
        public static CompareResult Compare(IReadOnlyList<RefDevice> reference, IReadOnlyList<RefDevice> placed,
                                            IReadOnlyList<PlannedPoint> planPoints, IReadOnlyList<DroppedInstance> dropped,
                                            double toleranceMm = 50, LinkTransform? refLink = null, PassLimits? limits = null,
                                            bool censusComplete = false)
        {
            limits = limits ?? new PassLimits();
            List<RefDevice> refs = reference.Select(d => refLink == null ? d : Moved(d, refLink)).ToList();

            double reach = limits.ReachMm;
            var pairs = new List<(int R, int P, double D)>();
            for (int r = 0; r < refs.Count; r++)
            {
                for (int p = 0; p < placed.Count; p++)
                {
                    double d = Distance(refs[r].X, refs[r].Y, placed[p].X, placed[p].Y);
                    if (d <= reach) pairs.Add((r, p, d));
                }
            }

            var refTaken = new int[refs.Count];
            var placedTaken = new bool[placed.Count];
            for (int i = 0; i < refTaken.Length; i++) refTaken[i] = -1;
            var distance = new double[refs.Count];
            foreach ((int r, int p, double d) in pairs.OrderBy(x => x.D).ThenBy(x => x.R).ThenBy(x => x.P))
            {
                if (refTaken[r] >= 0 || placedTaken[p]) continue;
                refTaken[r] = p;
                placedTaken[p] = true;
                distance[r] = d;
            }

            var result = new CompareResult
            {
                ToleranceMm = toleranceMm,
                Limits = limits,
                PlanGiven = planPoints.Count > 0 || dropped.Count > 0 || censusComplete,
                CensusComplete = censusComplete,
            };
            for (int r = 0; r < refs.Count; r++)
            {
                var row = new CompareRow { Reference = refs[r] };
                if (refTaken[r] >= 0)
                {
                    RefDevice match = placed[refTaken[r]];
                    row.Placed = match;
                    row.DistanceMm = distance[r];
                    row.Outcome = distance[r] <= toleranceMm ? "hit" : distance[r] <= NearMm ? "near" : "far";
                    row.DzMm = match.Z - refs[r].Z;
                    bool known = FacingFor(match, refs[r], planPoints, toleranceMm, out double fx, out double fy);
                    Split(match.X - refs[r].X, match.Y - refs[r].Y, known, fx, fy, out double across, out double along);
                    row.AcrossMm = across;
                    row.AlongMm = along;
                    row.Split = known ? "facing" : "axis";
                    row.HostAgrees = string.Equals(match.HostKind, refs[r].HostKind, StringComparison.OrdinalIgnoreCase);
                }
                if (!row.Within) Explain(row, placed, planPoints, dropped, toleranceMm, censusComplete);
                result.Rows.Add(row);
            }
            var placedWithin = new bool[placed.Count];
            for (int r = 0; r < refs.Count; r++)
            {
                if (result.Rows[r].Within) placedWithin[refTaken[r]] = true;
            }
            for (int p = 0; p < placed.Count; p++)
            {
                if (!placedTaken[p]) result.Extra.Add(placed[p]);
                if (placedWithin[p]) continue;
                double? nearest = null;
                foreach (RefDevice d in refs)
                {
                    double dist = Distance(d.X, d.Y, placed[p].X, placed[p].Y);
                    if (dist <= ExplainMm && (!nearest.HasValue || dist < nearest.Value)) nearest = dist;
                }
                result.ExtraRows.Add(new ExtraRow
                {
                    Placed = placed[p],
                    Code = nearest.HasValue ? "near_reference" : "not_in_reference",
                    DistanceMm = nearest,
                });
            }
            return result;
        }

        /// <summary>
        /// Splits a plan offset into its part across the wall (along the facing) and along the
        /// wall, both absolute. With no facing, the larger axis component is taken as along
        /// and the smaller as across: walls mostly run on the axes.
        /// </summary>
        public static void Split(double dx, double dy, bool hasFacing, double fx, double fy, out double across, out double along)
        {
            double len = Math.Sqrt((fx * fx) + (fy * fy));
            if (hasFacing && len > 1e-9)
            {
                fx /= len;
                fy /= len;
                across = Math.Abs((dx * fx) + (dy * fy));
                along = Math.Abs((dx * -fy) + (dy * fx));
                return;
            }
            across = Math.Min(Math.Abs(dx), Math.Abs(dy));
            along = Math.Max(Math.Abs(dx), Math.Abs(dy));
        }

        /// <summary>The facing a match is split along: the planned point's (the plan point
        /// within the hit tolerance of the placed device), else the placed device's own, else
        /// the reference device's. False when none has one.</summary>
        private static bool FacingFor(RefDevice placed, RefDevice reference, IReadOnlyList<PlannedPoint> planPoints,
                                      double toleranceMm, out double fx, out double fy)
        {
            PlannedPoint? nearest = null;
            double best = toleranceMm;
            foreach (PlannedPoint p in planPoints)
            {
                if ((p.FacingDx * p.FacingDx) + (p.FacingDy * p.FacingDy) <= 1e-6) continue;
                double d = Distance(placed.X, placed.Y, p.XMm, p.YMm);
                if (d > best) continue;
                best = d;
                nearest = p;
            }
            if (nearest != null)
            {
                fx = nearest.FacingDx;
                fy = nearest.FacingDy;
                return true;
            }
            RefDevice? own = placed.HasFacing ? placed : reference.HasFacing ? reference : null;
            fx = own?.FacingX ?? 0;
            fy = own?.FacingY ?? 0;
            return own != null;
        }

        /// <summary>
        /// A miss's code, the first that applies: near_&lt;N&gt;mm (its own match beyond the
        /// tolerance, else the nearest placed device, within 500 mm), plan_flag:&lt;flag&gt; (the
        /// nearest plan point within 500 mm carries a flag that explains it), dropped:&lt;reason&gt;
        /// (the nearest dropped symbol within 500 mm), not_in_drawing (no plan point or dropped
        /// symbol within 1000 mm, when the plan lists every symbol), else unexplained.
        /// </summary>
        private static void Explain(CompareRow row, IReadOnlyList<RefDevice> placed, IReadOnlyList<PlannedPoint> planPoints,
                                    IReadOnlyList<DroppedInstance> dropped, double toleranceMm, bool censusComplete)
        {
            RefDevice device = row.Reference;
            RefDevice? near = null;
            double nearMm = 0;
            if (row.Placed != null && row.DistanceMm.HasValue && row.DistanceMm.Value <= ExplainMm)
            {
                near = row.Placed;
                nearMm = row.DistanceMm.Value;
            }
            else
            {
                double best = ExplainMm;
                foreach (RefDevice p in placed)
                {
                    double d = Distance(device.X, device.Y, p.X, p.Y);
                    if (d > best) continue;
                    best = d;
                    near = p;
                }
                nearMm = best;
            }
            if (near != null)
            {
                bool known = FacingFor(near, device, planPoints, toleranceMm, out double fx, out double fy);
                Split(near.X - device.X, near.Y - device.Y, known, fx, fy, out double across, out double along);
                row.NearPlacedId = near.Id;
                row.NearAcrossMm = across;
                row.NearAlongMm = along;
                row.Explanation = "near_" + Math.Round(nearMm).ToString("0", CultureInfo.InvariantCulture) + "mm";
                return;
            }

            PlannedPoint? point = null;
            double pointMm = double.MaxValue;
            foreach (PlannedPoint p in planPoints)
            {
                double d = Distance(device.X, device.Y, p.XMm, p.YMm);
                if (d >= pointMm) continue;
                pointMm = d;
                point = p;
            }
            if (point != null && pointMm <= ExplainMm)
            {
                string? flag = point.Flags.FirstOrDefault(FlagExplainsMiss);
                if (flag == null && point.Excluded) flag = "excluded";
                if (flag != null)
                {
                    row.Explanation = "plan_flag:" + flag;
                    return;
                }
            }

            DroppedInstance? drop = null;
            double dropMm = double.MaxValue;
            foreach (DroppedInstance x in dropped)
            {
                double d = Distance(device.X, device.Y, x.X, x.Y);
                if (d >= dropMm) continue;
                dropMm = d;
                drop = x;
            }
            if (drop != null && dropMm <= ExplainMm)
            {
                row.Explanation = "dropped:" + (drop.Reason.Length > 0 ? drop.Reason : "no_reason");
                return;
            }

            bool drawnNearby = pointMm <= CensusMm || dropMm <= CensusMm;
            row.Explanation = censusComplete && !drawnNearby ? "not_in_drawing" : "unexplained";
        }

        private static RefDevice Moved(RefDevice d, LinkTransform t)
        {
            t.Apply(d.X, d.Y, d.Z, out double x, out double y, out double z);
            return new RefDevice
            {
                Id = d.Id,
                Category = d.Category,
                FamilyType = d.FamilyType,
                Level = d.Level,
                HostKind = d.HostKind,
                X = x,
                Y = y,
                Z = z,
                // A direction turns with the link's basis and does not move with its origin.
                FacingX = (t.BX[0] * d.FacingX) + (t.BY[0] * d.FacingY),
                FacingY = (t.BX[1] * d.FacingX) + (t.BY[1] * d.FacingY),
            };
        }

        private static double Distance(double x1, double y1, double x2, double y2) =>
            Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

        // ---------------------------------------------------------------- reading the dumps

        /// <summary>The devices of a dump_electrical_reference file, those whose family type
        /// contains <paramref name="typeFilter"/> (any case) when it is given.</summary>
        public static List<RefDevice> ReadDevices(JsonElement root, string? typeFilter = null)
        {
            var devices = new List<RefDevice>();
            if (!root.TryGetProperty("devices", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return devices;
            foreach (JsonElement d in list.EnumerateArray())
            {
                var device = new RefDevice
                {
                    Id = d.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                    Category = Text(d, "category"),
                    FamilyType = Text(d, "family_type"),
                    Level = Text(d, "level"),
                    HostKind = Text(d, "host_kind"),
                };
                double[] xyz = Numbers(d, "xyz_mm");
                if (xyz.Length < 3) continue;
                device.X = xyz[0];
                device.Y = xyz[1];
                device.Z = xyz[2];
                double[] facing = Numbers(d, "facing");
                if (facing.Length >= 2)
                {
                    device.FacingX = facing[0];
                    device.FacingY = facing[1];
                }
                if (!string.IsNullOrEmpty(typeFilter) &&
                    device.FamilyType.IndexOf(typeFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                devices.Add(device);
            }
            return devices;
        }

        /// <summary>The links of a dump: name, origin_mm, basis_x/y/z.</summary>
        public static List<LinkTransform> ReadLinks(JsonElement root)
        {
            var links = new List<LinkTransform>();
            if (!root.TryGetProperty("links", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return links;
            foreach (JsonElement l in list.EnumerateArray())
            {
                double[] o = Numbers(l, "origin_mm"), bx = Numbers(l, "basis_x"), by = Numbers(l, "basis_y"), bz = Numbers(l, "basis_z");
                if (o.Length < 3 || bx.Length < 3 || by.Length < 3 || bz.Length < 3) continue;
                links.Add(new LinkTransform { Name = Text(l, "name"), OX = o[0], OY = o[1], OZ = o[2], BX = bx, BY = by, BZ = bz });
            }
            return links;
        }

        /// <summary>The points and dropped instances of a plan_cad_electrical result made with
        /// include_points. True when the plan says they list every symbol drawn on it
        /// (dropped_lists_every_symbol), so a miss can be not_in_drawing.</summary>
        public static bool ReadPlan(JsonElement root, List<PlannedPoint> points, List<DroppedInstance> dropped)
        {
            if (root.TryGetProperty("points", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement p in list.EnumerateArray())
                {
                    double[] xyz = Numbers(p, "xyz_mm");
                    if (xyz.Length < 2) continue;
                    var point = new PlannedPoint
                    {
                        Index = p.TryGetProperty("index", out JsonElement i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0,
                        Role = Text(p, "role"),
                        SymbolKey = Text(p, "block"),
                        RoomName = Text(p, "room"),
                        Host = Text(p, "host"),
                        XMm = xyz[0],
                        YMm = xyz[1],
                        ZMm = xyz.Length > 2 ? xyz[2] : 0,
                        Excluded = p.TryGetProperty("excluded", out JsonElement e) && e.ValueKind == JsonValueKind.True,
                        Source = "cad",
                    };
                    double[] facing = Numbers(p, "facing");
                    if (facing.Length >= 2)
                    {
                        point.FacingDx = facing[0];
                        point.FacingDy = facing[1];
                    }
                    if (p.TryGetProperty("flags", out JsonElement flags) && flags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement f in flags.EnumerateArray())
                        {
                            if (f.ValueKind == JsonValueKind.String) point.Flags.Add(f.GetString() ?? "");
                        }
                    }
                    points.Add(point);
                }
            }

            if (root.TryGetProperty("dropped", out JsonElement drops) && drops.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement d in drops.EnumerateArray())
                {
                    double[] xyz = Numbers(d, "xyz_mm");
                    if (xyz.Length < 2) continue;
                    dropped.Add(new DroppedInstance
                    {
                        Block = Text(d, "block"), Class = Text(d, "class"), Reason = Text(d, "reason"), X = xyz[0], Y = xyz[1],
                    });
                }
            }
            return root.TryGetProperty("dropped_lists_every_symbol", out JsonElement every) && every.ValueKind == JsonValueKind.True;
        }

        private static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static double[] Numbers(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array) return new double[0];
            var values = new List<double>();
            foreach (JsonElement n in v.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.Number) values.Add(n.GetDouble());
                else return new double[0];
            }
            return values.ToArray();
        }

        /// <summary>The issue's metric and the miss codes first, then the diagnostic, for the CLI.</summary>
        public static string Summary(CompareResult r)
        {
            var c = CultureInfo.InvariantCulture;
            string tol = r.ToleranceMm.ToString(c);
            string required = (100 * CompareResult.RequiredShare).ToString("0", c);
            var lines = new List<string>
            {
                "issue rule: " + IssueRule,
                "reference " + r.Rows.Count.ToString(c) + ", within " + tol + " mm " + Share(r.Within, r.Rows.Count) +
                ": " + (r.SharePasses ? "PASS" : "FAIL") + " (needs >= " + required + "%)",
                "misses " + r.IssueMisses.ToString(c) + ", unexplained " + r.Unexplained.ToString(c) + ": " +
                (r.Unexplained == 0 ? "PASS" : "FAIL") + " (needs 0)",
                "exit gate: " + (r.GatePasses ? "PASS" : "FAIL"),
            };
            if (!r.PlanGiven)
                lines.Add("note: no plan given, so plan_flag, dropped and not_in_drawing cannot explain a miss");
            else if (!r.CensusComplete)
                lines.Add("note: the plan does not list every drawn symbol (dropped_lists_every_symbol), so not_in_drawing cannot explain a miss");

            List<CompareRow> misses = r.Rows.Where(x => !x.Within).ToList();
            if (misses.Count > 0)
            {
                lines.Add("miss codes:");
                foreach (KeyValuePair<string, int> g in r.MissCodes())
                    lines.Add("  " + g.Key + ": " + g.Value.ToString(c));
                lines.Add("misses:");
                foreach (CompareRow m in misses)
                {
                    string line = "  ref " + m.Reference.Id.ToString(c) + ": " + (m.Explanation ?? "unexplained");
                    if (m.NearPlacedId.HasValue)
                        line += " (placed " + m.NearPlacedId.Value.ToString(c) + ", across " + Mm(m.NearAcrossMm) +
                                ", along " + Mm(m.NearAlongMm) + ")";
                    lines.Add(line);
                }
            }

            lines.Add("extra placements " + r.ExtraRows.Count.ToString(c) + " (no reference within " + tol + " mm):");
            foreach (KeyValuePair<string, int> g in r.ExtraCodes())
                lines.Add("  " + g.Key + ": " + g.Value.ToString(c));

            lines.Add("diagnostic (not the issue's rule): matches within " + Math.Round(r.Limits.ReachMm).ToString(c) +
                      " mm split across and along the wall");
            lines.Add("  hits " + r.Hits.ToString(c) + ", near " + r.Near.ToString(c) + ", far " + r.Far.ToString(c) +
                      ", unmatched " + r.Misses.ToString(c));
            lines.Add("  limits: across <= " + r.Limits.AcrossMm.ToString(c) + " mm, along <= " + r.Limits.AlongMm.ToString(c) +
                      " mm, |dz| <= " + r.Limits.DzMm.ToString(c) + " mm");
            lines.Add("  matched " + r.Matched.ToString(c) + ": across_ok " + r.AcrossOkCount.ToString(c) + ", along_ok " +
                      r.AlongOkCount.ToString(c) + ", height_ok " + r.HeightOkCount.ToString(c) + ", pass " +
                      r.PassCount.ToString(c));
            int n = r.Denominator;
            lines.Add("  of " + n.ToString(c) + " " + (r.Drawn.HasValue ? "drawn" : "placed") + ": pass " +
                      Share(r.PassCount, n) + ", across_ok " + Share(r.AcrossOkCount, n));
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static string Mm(double? mm) =>
            mm.HasValue ? Math.Round(mm.Value).ToString("0", CultureInfo.InvariantCulture) + " mm" : "-";

        private static string Share(int count, int of) =>
            count.ToString(CultureInfo.InvariantCulture) + " (" +
            (of == 0 ? 0.0 : 100.0 * count / of).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
    }
}
