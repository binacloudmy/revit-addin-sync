#nullable enable
// PlacementCompare - the exit check of phase 1 of the CAD electrical to BIM work.
//
// Lays the devices a drafter modelled (the reference) against the ones placed from the
// drawing, one to one, nearest first: a hit within 50 mm, near within 300 mm, far when matched
// further out (up to the pass limits' reach), else a miss.
// A miss is explained by what the plan said about that spot (the nearest plan point's flag,
// or the nearest dropped instance's reason); a miss nothing explains is what the check is for.
//
// A drafter slides sockets along a wall, so each match's plan offset is also split across the
// wall (along the placed point's facing) and along it. A match passes when it is on the right
// face (across), not too far along, and at the right height.
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
        public double ReachMm => Math.Max(PlacementCompare.NearMm, Math.Sqrt((AcrossMm * AcrossMm) + (AlongMm * AlongMm)));
    }

    /// <summary>A drawn instance the plan placed nothing for, and why.</summary>
    internal sealed class DroppedInstance
    {
        public string Block = "";
        public string Reason = "";
        public double X, Y;
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
        /// <summary>On a miss: "flag:&lt;flag&gt;", "dropped:&lt;reason&gt;" or "unexplained".</summary>
        public string? Explanation;
    }

    internal sealed class CompareResult
    {
        public List<CompareRow> Rows = new List<CompareRow>();
        public List<RefDevice> Extra = new List<RefDevice>();
        public double ToleranceMm;
        public PassLimits Limits = new PassLimits();
        /// <summary>The exit check's denominator, the sockets drawn on the floor's plan, when the
        /// caller knows it (the plan's points); else the placed devices.</summary>
        public int? Drawn;

        public int Hits => Rows.Count(r => r.Outcome == "hit");
        public int Near => Rows.Count(r => r.Outcome == "near");
        public int Far => Rows.Count(r => r.Outcome == "far");
        public int Misses => Rows.Count(r => r.Outcome == "miss");
        public int Unexplained => Rows.Count(r => r.Explanation == "unexplained");
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
            return new Dictionary<string, object?>
            {
                ["tolerance_mm"] = ToleranceMm,
                ["reference"] = Rows.Count,
                ["hits"] = Hits,
                ["near"] = Near,
                ["far"] = Far,
                ["misses"] = Misses,
                ["unexplained"] = Unexplained,
                ["hit_rate"] = Rows.Count == 0 ? 0.0 : Math.Round((double)Hits / Rows.Count, 3),
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
                ["explained_by"] = Rows.Where(r => r.Explanation != null)
                    .GroupBy(r => r.Explanation!, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => (object?)g.Count()),
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
                ["extra_placements"] = Extra.Count,
                ["rows"] = Rows.Select(r => (object?)new Dictionary<string, object?>
                {
                    ["ref_id"] = r.Reference.Id,
                    ["outcome"] = r.Outcome,
                    ["distance_mm"] = r.DistanceMm.HasValue ? Math.Round(r.DistanceMm.Value, 1) : (double?)null,
                    ["placed_id"] = r.Placed?.Id,
                    ["across_mm"] = r.AcrossMm.HasValue ? Math.Round(r.AcrossMm.Value, 1) : (double?)null,
                    ["along_mm"] = r.AlongMm.HasValue ? Math.Round(r.AlongMm.Value, 1) : (double?)null,
                    ["split"] = r.Split,
                    ["pass"] = r.Placed != null ? Passes(r) : (bool?)null,
                    ["dz_mm"] = r.DzMm.HasValue ? Math.Round(r.DzMm.Value, 1) : (double?)null,
                    ["host_agrees"] = r.HostAgrees,
                    ["explanation"] = r.Explanation,
                }).ToList(),
                ["extra_ids"] = Extra.Select(d => (object?)d.Id).ToList(),
            };
        }
    }

    internal static class PlacementCompare
    {
        /// <summary>Beyond this a reference device has no counterpart, and a plan point or
        /// dropped instance does not explain it.</summary>
        public const double NearMm = 300;

        /// <summary>
        /// Matches reference devices to placed ones one to one, by ascending plan distance.
        /// <paramref name="refLink"/>, when given, carries the reference into the placed model's
        /// coordinates first (the reference model is linked there).
        /// </summary>
        public static CompareResult Compare(IReadOnlyList<RefDevice> reference, IReadOnlyList<RefDevice> placed,
                                            IReadOnlyList<PlannedPoint> planPoints, IReadOnlyList<DroppedInstance> dropped,
                                            double toleranceMm = 50, LinkTransform? refLink = null, PassLimits? limits = null)
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

            var result = new CompareResult { ToleranceMm = toleranceMm, Limits = limits };
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
                else
                {
                    row.Explanation = Explain(refs[r], planPoints, dropped);
                }
                result.Rows.Add(row);
            }
            for (int p = 0; p < placed.Count; p++)
            {
                if (!placedTaken[p]) result.Extra.Add(placed[p]);
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

        /// <summary>The nearest plan point's flag (one that excludes, if it has one), else the
        /// nearest dropped instance's reason, else unexplained.</summary>
        private static string Explain(RefDevice device, IReadOnlyList<PlannedPoint> planPoints, IReadOnlyList<DroppedInstance> dropped)
        {
            PlannedPoint? point = null;
            double best = NearMm;
            foreach (PlannedPoint p in planPoints)
            {
                double d = Distance(device.X, device.Y, p.XMm, p.YMm);
                if (d > best) continue;
                best = d;
                point = p;
            }
            if (point != null && point.Flags.Count > 0)
            {
                string flag = point.Flags.FirstOrDefault(f => CadSymbolResolve.FlagExcludes.TryGetValue(f, out bool x) && x)
                              ?? point.Flags[0];
                return "flag:" + flag;
            }

            DroppedInstance? drop = null;
            best = NearMm;
            foreach (DroppedInstance x in dropped)
            {
                double d = Distance(device.X, device.Y, x.X, x.Y);
                if (d > best) continue;
                best = d;
                drop = x;
            }
            return drop != null ? "dropped:" + drop.Reason : "unexplained";
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
        /// include_points.</summary>
        public static void ReadPlan(JsonElement root, List<PlannedPoint> points, List<DroppedInstance> dropped)
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
                    dropped.Add(new DroppedInstance { Block = Text(d, "block"), Reason = Text(d, "reason"), X = xyz[0], Y = xyz[1] });
                }
            }
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

        /// <summary>One line per count, for the CLI.</summary>
        public static string Summary(CompareResult r)
        {
            var c = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "reference " + r.Rows.Count.ToString(c) + ", hits " + r.Hits.ToString(c) + ", near " + r.Near.ToString(c) +
                ", far " + r.Far.ToString(c) + ", misses " + r.Misses.ToString(c) + ", unexplained " +
                r.Unexplained.ToString(c) + ", extra placements " + r.Extra.Count.ToString(c),
            };
            foreach (var g in r.Rows.Where(x => x.Explanation != null).GroupBy(x => x.Explanation!, StringComparer.Ordinal)
                         .OrderByDescending(g => g.Count()))
            {
                lines.Add("  miss " + g.Key + ": " + g.Count().ToString(c));
            }
            lines.Add("limits: across <= " + r.Limits.AcrossMm.ToString(c) + " mm, along <= " + r.Limits.AlongMm.ToString(c) +
                      " mm, |dz| <= " + r.Limits.DzMm.ToString(c) + " mm");
            lines.Add("matched " + r.Matched.ToString(c) + ": across_ok " + r.AcrossOkCount.ToString(c) + ", along_ok " +
                      r.AlongOkCount.ToString(c) + ", height_ok " + r.HeightOkCount.ToString(c) + ", pass " +
                      r.PassCount.ToString(c));
            int n = r.Denominator;
            lines.Add("of " + n.ToString(c) + " " + (r.Drawn.HasValue ? "drawn" : "placed") + ": pass " +
                      Share(r.PassCount, n) + ", across_ok " + Share(r.AcrossOkCount, n));
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        private static string Share(int count, int of) =>
            count.ToString(CultureInfo.InvariantCulture) + " (" +
            (of == 0 ? 0.0 : 100.0 * count / of).ToString("0.0", CultureInfo.InvariantCulture) + "%)";
    }
}
