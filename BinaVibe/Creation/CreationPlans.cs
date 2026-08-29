// BinaVibe.Creation — Revit-free planners for the creation families
// (bina-ai R2 Task 25: levels / grid / wall / room / door).
//
// Each plan answers, before any transaction, exactly what the tool would
// make and why it might refuse: which level names already exist and where
// the new ones land, whether a grid name is taken, the resolved wall
// length / top constraint, whether a door's insertion point lies along its
// host wall. All lengths in millimetres; the caller converts.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BinaVibe.Creation
{
    public sealed class CreationRisk
    {
        public string Kind { get; init; } = "";
        public string Note { get; init; } = "";
    }

    public sealed class LevelRow
    {
        public string Name { get; init; } = "";
        public double ElevationMm { get; init; }
        public bool Exists { get; init; }
    }

    /// <summary>create_levels_batch: N levels above a base at a fixed floor-to-floor.</summary>
    public sealed class LevelsPlan
    {
        public IReadOnlyList<LevelRow> Levels { get; }
        public double BaseElevationMm { get; }
        public int WouldCreate => Levels.Count(l => !l.Exists);
        public int SkippedExisting => Levels.Count(l => l.Exists);
        public IReadOnlyList<CreationRisk> Risks { get; }

        private LevelsPlan(List<LevelRow> rows, double baseMm, List<CreationRisk> risks) { Levels = rows; BaseElevationMm = baseMm; Risks = risks; }

        public static LevelsPlan Build(IEnumerable<(string name, double elevationMm)> existing, double baseElevationMm,
                                       int count, double floorToFloorMm, string prefix, int startIndex)
        {
            var ex = existing.ToList();
            var names = new HashSet<string>(ex.Select(e => e.name), StringComparer.OrdinalIgnoreCase);
            var rows = new List<LevelRow>();
            var risks = new List<CreationRisk>();
            if (count <= 0) risks.Add(new CreationRisk { Kind = "invalid_count", Note = "count must be positive" });
            if (floorToFloorMm <= 0) risks.Add(new CreationRisk { Kind = "invalid_spacing", Note = "floor-to-floor must be positive" });
            for (int i = 0; i < Math.Max(0, count); i++)
            {
                var name = $"{prefix}{startIndex + i}";
                var elev = baseElevationMm + floorToFloorMm * (i + 1);
                var exists = names.Contains(name);
                rows.Add(new LevelRow { Name = name, ElevationMm = Math.Round(elev, 3), Exists = exists });
                var clash = ex.FirstOrDefault(e => !string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase) && Math.Abs(e.elevationMm - elev) <= 1.0);
                if (!exists && clash.name != null)
                    risks.Add(new CreationRisk { Kind = "elevation_collision", Note = $"{name} at {elev:0} mm sits on existing level {clash.name}" });
            }
            return new LevelsPlan(rows, baseElevationMm, risks);
        }

        public Dictionary<string, object?> ToPreview(string baseLevel) => new()
        {
            ["ok"] = true, ["dry_run"] = true, ["base_level"] = baseLevel, ["base_elevation_mm"] = Math.Round(BaseElevationMm),
            ["would_create"] = WouldCreate, ["skipped_existing"] = SkippedExisting,
            ["levels"] = Levels.Select(l => (object)new Dictionary<string, object?> { ["name"] = l.Name, ["elevation_mm"] = Math.Round(l.ElevationMm), ["exists"] = l.Exists }).ToList(),
            ["risks"] = Risks.Select(r => (object)new Dictionary<string, object?> { ["kind"] = r.Kind, ["note"] = r.Note }).ToList(),
        };
    }

    /// <summary>create_level / create_grid: one named datum; refuse a taken name.</summary>
    public sealed class DatumPlan
    {
        public string Name { get; }
        public bool Exists { get; }
        public long? ExistingId { get; }
        public double LengthMm { get; }
        public IReadOnlyList<CreationRisk> Risks { get; }
        public int WouldCreate => Exists || Risks.Count > 0 ? 0 : 1;

        private DatumPlan(string name, bool exists, long? id, double len, List<CreationRisk> risks) { Name = name; Exists = exists; ExistingId = id; LengthMm = len; Risks = risks; }

        public static DatumPlan ForLevel(IEnumerable<(string name, long id, double elevationMm)> existing, string name, double elevationMm)
        {
            var ex = existing.ToList();
            var same = ex.FirstOrDefault(e => string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));
            var risks = new List<CreationRisk>();
            var clash = ex.FirstOrDefault(e => Math.Abs(e.elevationMm - elevationMm) <= 1.0);
            if (same.name == null && clash.name != null)
                risks.Add(new CreationRisk { Kind = "elevation_collision", Note = $"{name} at {elevationMm:0} mm sits on existing level {clash.name}" });
            return new DatumPlan(name, same.name != null, same.name != null ? same.id : null, 0, risks);
        }

        public static DatumPlan ForGrid(IEnumerable<(string name, long id)> existing, string name, (double x, double y) startMm, (double x, double y) endMm)
        {
            var same = existing.FirstOrDefault(e => string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));
            var len = Math.Sqrt(Math.Pow(endMm.x - startMm.x, 2) + Math.Pow(endMm.y - startMm.y, 2));
            var risks = new List<CreationRisk>();
            if (len < 1.0) risks.Add(new CreationRisk { Kind = "zero_length", Note = "start equals end" });
            return new DatumPlan(name, same.name != null, same.name != null ? same.id : null, Math.Round(len, 3), risks);
        }
    }

    /// <summary>create_wall: resolved level / top / type and the measured length.</summary>
    public sealed class WallPlan
    {
        public double LengthMm { get; }
        public string Level { get; }
        public string? TopLevel { get; }
        public double? HeightMm { get; }
        public string HeightMode => TopLevel != null ? "level_to_level" : "unconnected";
        public string? TypeName { get; }
        public IReadOnlyList<CreationRisk> Risks { get; }
        public int WouldCreate => Risks.Any(r => r.Kind == "zero_length") ? 0 : 1;

        private WallPlan(double len, string level, string? top, double? h, string? type, List<CreationRisk> risks)
        { LengthMm = len; Level = level; TopLevel = top; HeightMm = h; TypeName = type; Risks = risks; }

        /// <param name="levels">name + elevation (mm), any order.</param>
        public static WallPlan Build((double x, double y, double z) startMm, (double x, double y, double z) endMm,
                                     IEnumerable<(string name, double elevationMm)> levels, string level,
                                     string? topLevel, double? heightMm, string? typeName)
        {
            var lv = levels.ToList();
            var baseLv = lv.FirstOrDefault(l => string.Equals(l.name, level, StringComparison.OrdinalIgnoreCase));
            var len = Math.Sqrt(Math.Pow(endMm.x - startMm.x, 2) + Math.Pow(endMm.y - startMm.y, 2) + Math.Pow(endMm.z - startMm.z, 2));
            var risks = new List<CreationRisk>();
            if (len < 1.0) risks.Add(new CreationRisk { Kind = "zero_length", Note = "start equals end" });
            string? top = null;
            double? h = heightMm;
            if (!string.IsNullOrEmpty(topLevel)) top = topLevel;
            else if (heightMm == null && baseLv.name != null)
            {
                var above = lv.Where(l => l.elevationMm > baseLv.elevationMm + 1e-6).OrderBy(l => l.elevationMm).FirstOrDefault();
                if (above.name != null) top = above.name;
                else h = 3000.0;
            }
            if (top == null && h == null) h = 3000.0;
            if (top != null && baseLv.name != null)
            {
                var t = lv.First(l => string.Equals(l.name, top, StringComparison.OrdinalIgnoreCase));
                if (t.elevationMm <= baseLv.elevationMm + 1e-6)
                    risks.Add(new CreationRisk { Kind = "top_below_base", Note = $"top level {top} is not above {level}" });
            }
            return new WallPlan(Math.Round(len, 3), baseLv.name ?? level, top, top != null ? null : h, typeName, risks);
        }
    }

    /// <summary>place_door: where along the host the point lands, and whether it fits.</summary>
    public sealed class DoorPlan
    {
        public double WallLengthMm { get; }
        public double OffsetAlongMm { get; }
        public double OffsetFromLineMm { get; }
        public double? TypeWidthMm { get; }
        public bool Fits { get; }
        public IReadOnlyList<CreationRisk> Risks { get; }
        public int WouldCreate => Fits ? 1 : 0;

        private DoorPlan(double wl, double along, double off, double? w, bool fits, List<CreationRisk> risks)
        { WallLengthMm = wl; OffsetAlongMm = along; OffsetFromLineMm = off; TypeWidthMm = w; Fits = fits; Risks = risks; }

        public static DoorPlan Build((double x, double y) wallStartMm, (double x, double y) wallEndMm,
                                     (double x, double y) locationMm, double? typeWidthMm, double toleranceMm = 500.0)
        {
            var dx = wallEndMm.x - wallStartMm.x; var dy = wallEndMm.y - wallStartMm.y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            var risks = new List<CreationRisk>();
            if (len < 1.0) { risks.Add(new CreationRisk { Kind = "zero_length", Note = "host wall has no length" }); return new DoorPlan(0, 0, 0, typeWidthMm, false, risks); }
            var ux = dx / len; var uy = dy / len;
            var px = locationMm.x - wallStartMm.x; var py = locationMm.y - wallStartMm.y;
            var along = px * ux + py * uy;
            var off = Math.Abs(px * -uy + py * ux);
            var half = (typeWidthMm ?? 0) / 2.0;
            var fits = along - half >= -1.0 && along + half <= len + 1.0;
            if (!fits) risks.Add(new CreationRisk { Kind = "outside_host", Note = $"{along:0} mm along a {len:0} mm wall" + (typeWidthMm != null ? $" (type {typeWidthMm:0} mm wide)" : "") });
            if (off > toleranceMm) risks.Add(new CreationRisk { Kind = "off_wall_line", Note = $"point is {off:0} mm off the wall line — it will be projected onto the wall" });
            return new DoorPlan(Math.Round(len, 3), Math.Round(along, 3), Math.Round(off, 3), typeWidthMm, fits, risks);
        }
    }

    public static class CreationVerify
    {
        /// <summary>Post-commit check for create_levels_batch: each level's re-read elevation within 1 mm.</summary>
        public static Dictionary<string, object?> Levels(IEnumerable<(long id, double expectedMm, double actualMm)> rows)
        {
            var list = rows.ToList();
            var mism = list.Where(r => Math.Abs(r.expectedMm - r.actualMm) > 1.0)
                .Select(r => (object)new Dictionary<string, object?> { ["id"] = r.id, ["expected_mm"] = Math.Round(r.expectedMm), ["actual_mm"] = Math.Round(r.actualMm) }).ToList();
            return new() { ["checked"] = list.Count, ["matches"] = list.Count - mism.Count, ["mismatches"] = mism };
        }
    }
}
