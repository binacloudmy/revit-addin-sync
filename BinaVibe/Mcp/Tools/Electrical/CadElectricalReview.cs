#nullable enable
// CadElectricalReview - phase 1 of the CAD electrical to BIM work (spec 2026-11-17 §7).
//
// What the drafter sees before and after placing a drawing's devices: per floor, how many
// were drawn, how many will be placed and why the rest will not, room by room. Counts, names
// and flags only; never a coordinate. Also the slim census, the census cut down to what the
// mapping step needs so it fits one tool result.
//
// Pure: plain dictionaries and lists with snake_case keys, the same wire style as
// ElectricalCensusResult.ToWire. Must compile on net48.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BinaVibe.Mcp.Tools.Electrical
{
    /// <summary>A block class the plan places nothing of, and why.</summary>
    internal sealed class UnmappedClass
    {
        public string Block = "";
        public int Instances;
        /// <summary>role_unknown | no_family_type | no_mount_height | not_in_mapping</summary>
        public string Reason = "";
    }

    internal static class CadElectricalReview
    {
        /// <summary>Flagged items listed per flag; the count is always whole.</summary>
        public const int MaxFlaggedItems = 50;

        private const string NoRoom = "(no room)";

        /// <summary>
        /// The review card of one drawing plan: headline, alignment, rooms (room x role,
        /// drawn against will place), flagged points by flag with the flag's default, unmapped
        /// classes, family types and notes. <paramref name="otherPlanInstances"/> counts the
        /// mapped instances that sit in the drawing's other plans, which this plan leaves out.
        /// </summary>
        public static Dictionary<string, object?> PlanReview(SocketPlan plan, IReadOnlyList<UnmappedClass> unmapped,
                                                             int otherPlanInstances, IEnumerable<string>? notes = null)
        {
            List<PlannedPoint> points = plan.Points;
            int drawn = points.Count;
            int place = points.Count(p => !p.Excluded);

            string level = plan.Alignment?.Level ?? points.Select(p => p.LevelName).FirstOrDefault(n => n.Length > 0) ?? "";
            string headline = (level.Length > 0 ? level + ": " : "") +
                              Count(drawn) + " drawn, " + Count(place) + " will be placed, " +
                              Count(drawn - place) + " excluded.";
            if (plan.Alignment != null) headline += " Alignment " + Round2(plan.Alignment.Score) + ".";

            var rooms = points
                .GroupBy(p => (Room: p.RoomName.Length > 0 ? p.RoomName : NoRoom, p.Role))
                .OrderBy(g => g.Key.Room == NoRoom ? 1 : 0)
                .ThenBy(g => g.Key.Room, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Role, StringComparer.Ordinal)
                .Select(g => (object?)new Dictionary<string, object?>
                {
                    ["room"] = g.Key.Room,
                    ["role"] = g.Key.Role,
                    ["drawn"] = g.Count(),
                    ["will_place"] = g.Count(p => !p.Excluded),
                })
                .ToList();

            var flagged = points
                .SelectMany(p => p.Flags.Select(flag => (Flag: flag, Point: p)))
                .GroupBy(x => x.Flag, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object?)new Dictionary<string, object?>
                {
                    ["flag"] = g.Key,
                    ["default"] = CadSymbolResolve.FlagExcludes.TryGetValue(g.Key, out bool excludes) && excludes ? "excluded" : "kept",
                    ["count"] = g.Count(),
                    ["items"] = g.Take(MaxFlaggedItems).Select(x => (object?)new Dictionary<string, object?>
                    {
                        ["index"] = x.Point.Index,
                        ["room"] = x.Point.RoomName.Length > 0 ? x.Point.RoomName : NoRoom,
                        ["role"] = x.Point.Role,
                        ["block"] = x.Point.SymbolKey,
                    }).ToList(),
                    ["truncated"] = g.Count() > MaxFlaggedItems,
                })
                .ToList();

            var familyTypes = points
                .Where(p => !p.Excluded)
                .GroupBy(p => p.FamilyType ?? "", StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object?)new Dictionary<string, object?>
                {
                    ["family_type"] = g.Key.Length > 0 ? g.Key : null,
                    ["will_place"] = g.Count(),
                })
                .ToList();

            var allNotes = new List<object?>();
            if (notes != null) allNotes.AddRange(notes.Select(n => (object?)n));
            if (otherPlanInstances > 0)
            {
                allNotes.Add(Count(otherPlanInstances) + " mapped instances sit in the drawing's other plans and are not in this one");
            }

            return new Dictionary<string, object?>
            {
                ["headline"] = headline,
                ["alignment"] = Alignment(plan.Alignment),
                ["drawn"] = drawn,
                ["will_place"] = place,
                ["excluded"] = drawn - place,
                ["rooms"] = rooms,
                ["flagged"] = flagged,
                ["unmapped_classes"] = unmapped
                    .OrderByDescending(u => u.Instances)
                    .ThenBy(u => u.Block, StringComparer.Ordinal)
                    .Select(u => (object?)new Dictionary<string, object?>
                    {
                        ["block"] = u.Block,
                        ["instances"] = u.Instances,
                        ["reason"] = u.Reason,
                    })
                    .ToList(),
                ["family_types"] = familyTypes,
                ["notes"] = allNotes,
            };
        }

        /// <summary>What a placement leaves for the drafter to look at: points whose facing could
        /// not be corrected or whose elevation could not be set, and placed points flagged
        /// no_room, wet_room or provisional_alignment.</summary>
        public static Dictionary<string, object?> PlaceReview(IReadOnlyList<PlannedPoint> placed, int facingUncorrected,
                                                              int elevationUnset)
        {
            int noRoom = placed.Count(p => p.Flags.Contains("no_room"));
            int wet = placed.Count(p => p.Flags.Contains("wet_room"));
            int provisional = placed.Count(p => p.Flags.Contains("provisional_alignment"));
            int needs = placed.Count(p => p.Flags.Contains("no_room") || p.Flags.Contains("wet_room") ||
                                          p.Flags.Contains("provisional_alignment"));

            return new Dictionary<string, object?>
            {
                ["headline"] = Count(placed.Count) + " placed; " + Count(needs + facingUncorrected + elevationUnset) +
                               " things to check.",
                ["needs_review"] = new Dictionary<string, object?>
                {
                    ["facing_uncorrected"] = facingUncorrected,
                    ["elevation_unset"] = elevationUnset,
                    ["no_room"] = noRoom,
                    ["wet_room"] = wet,
                    ["provisional_alignment"] = provisional,
                },
            };
        }

        /// <summary>
        /// The census cut to fit one tool result: at most <paramref name="maxBlocks"/> classes,
        /// symbol-sized non-container ones first (those are what gets mapped), and at most
        /// <paramref name="maxLayers"/> layers, each shrunk to its name, inserts, length, long
        /// open polylines and wall hint. Drawn from the uncapped lists when the census has them.
        /// A block entry is the census's, less its false flags and empty lists.
        /// </summary>
        public static Dictionary<string, object?> SlimCensus(ElectricalCensusResult census, int maxBlocks = 80, int maxLayers = 60)
        {
            double symbolMax = new CensusOptions().SymbolMaxExtentMm;
            List<CensusBlockClass> allBlocks = census.AllBlocks ?? census.Blocks;
            List<CensusBlockClass> blocks = allBlocks
                .Select((block, order) => (Block: block, Order: order))
                .OrderBy(x => !x.Block.IsContainer && Math.Max(x.Block.WidthMm, x.Block.HeightMm) <= symbolMax ? 0 : 1)
                .ThenBy(x => x.Order)
                .Take(maxBlocks)
                .Select(x => x.Block)
                .ToList();

            var cut = new ElectricalCensusResult
            {
                Drawing = census.Drawing,
                Blocks = blocks,
                Plans = census.Plans,
                Legend = census.Legend,
                Totals = census.Totals,
                PlansTruncated = census.PlansTruncated,
                LegendTruncated = census.LegendTruncated,
                SymbolsAreExploded = census.SymbolsAreExploded,
                TimingsSeconds = census.TimingsSeconds,
            };
            Dictionary<string, object?> wire = cut.ToWire();

            // A block's false flags and empty lists are left out: most classes have them all.
            foreach (object? row in (List<object?>)wire["blocks"]!)
            {
                var block = (Dictionary<string, object?>)row!;
                foreach (string flag in new[] { "anonymous", "is_container", "unresolved_xref" })
                {
                    if (block[flag] is bool set && !set) block.Remove(flag);
                }
                foreach (string list in new[] { "record_names", "attributes", "near_texts" })
                {
                    if (block[list] is List<object?> items && items.Count == 0) block.Remove(list);
                }
            }

            List<CensusLayer> allLayers = census.AllLayers ?? census.Layers;
            wire["blocks_truncated"] = census.BlocksTruncated || allBlocks.Count > blocks.Count;
            wire["layers"] = allLayers.Take(maxLayers).Select(layer => (object?)new Dictionary<string, object?>
            {
                ["layer"] = layer.Name,
                ["inserts"] = layer.Inserts,
                ["length_m"] = Math.Round(layer.LengthM, 1),
                ["open_polylines_over_1m"] = layer.OpenPolylinesOver1m,
                ["pairs_like_walls"] = layer.PairsLikeWalls,
            }).ToList();
            wire["layers_truncated"] = census.LayersTruncated || allLayers.Count > maxLayers;
            wire.Remove("timings_s");
            return wire;
        }

        private static Dictionary<string, object?>? Alignment(PlanAlignment? a) =>
            a == null
                ? null
                : new Dictionary<string, object?>
                {
                    ["level"] = a.Level,
                    ["cluster"] = a.Cluster,
                    ["rotation_deg"] = Math.Round(a.RotationDeg, 2),
                    ["score"] = Math.Round(a.Score, 3),
                    ["outcome"] = a.Outcome,
                    ["method"] = a.Method,
                };

        private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Round2(double value) => Math.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);
    }
}
