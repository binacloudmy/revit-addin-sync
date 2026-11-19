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

    /// <summary>A run layer the plan routes nothing on, and why.</summary>
    internal sealed class UnplannedLayer
    {
        public string Layer = "";
        /// <summary>no_conduit_type | no_tray_type | no_diameter | no_tray_size | no_run_height | too_many_pieces</summary>
        public string Reason = "";
        public int Pieces;
        public double LengthMm;
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
        /// mapped instances that sit in the drawing's other plans, which this plan leaves out;
        /// <paramref name="outsideFootprint"/> the mapped instances of this plan's cluster that lie
        /// outside the level's footprint once placed (a count and a note, never a flag: they are
        /// not points).
        /// </summary>
        public static Dictionary<string, object?> PlanReview(SocketPlan plan, IReadOnlyList<UnmappedClass> unmapped,
                                                             int otherPlanInstances, IEnumerable<string>? notes = null,
                                                             int outsideFootprint = 0,
                                                             Dictionary<string, object?>? conduit = null)
        {
            List<PlannedPoint> points = plan.Points;
            int drawn = points.Count;
            int place = points.Count(p => !p.Excluded);

            string level = plan.Alignment?.Level ?? points.Select(p => p.LevelName).FirstOrDefault(n => n.Length > 0) ?? "";
            string headline = (level.Length > 0 ? level + ": " : "") +
                              Count(drawn) + " drawn, " + Count(place) + " will be placed, " +
                              Count(drawn - place) + " excluded.";
            if (plan.Alignment != null) headline += " Alignment " + Round2(plan.Alignment.Score) + ".";
            if (conduit != null) headline += ConduitSentence(plan, conduit);

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
            if (outsideFootprint > 0)
            {
                allNotes.Add(Count(outsideFootprint) + " mapped instances in this plan's part of the drawing fall outside " +
                             "the level's walls once aligned and are not placed: devices outdoors, or drawing that " +
                             "belongs to another plan");
            }

            var review = new Dictionary<string, object?>
            {
                ["headline"] = headline,
                ["alignment"] = Alignment(plan.Alignment),
                ["drawn"] = drawn,
                ["will_place"] = place,
                ["excluded"] = drawn - place,
                ["outside_model_footprint"] = outsideFootprint,
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
            if (conduit != null) review["conduit"] = conduit;
            return review;
        }

        /// <summary>
        /// The conduit block of a plan's review: how many runs, how long, how many drops, and
        /// what to look at (open ends, junctions, flagged runs by flag), per layer; the run
        /// layers that plan nothing and why; short runs and free arcs left out. Counts, names and
        /// indices only, never a coordinate.
        /// <paramref name="status"/> is planned | needs_run_height | none.
        /// </summary>
        public static Dictionary<string, object?> ConduitReview(SocketPlan plan, IReadOnlyList<UnplannedLayer> unplanned,
                                                                int tooShort, int freeArcs, string status,
                                                                int piecesOutside = 0)
        {
            List<PlannedRun> runs = plan.Runs;
            // A closed loop has no ends.
            List<PlannedRun> conduit = runs.Where(r => r.Kind == "conduit" && !r.Flags.Contains("closed_loop")).ToList();

            var byLayer = runs
                .GroupBy(r => r.Layer, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    PlannedRun first = g.First();
                    return (object?)new Dictionary<string, object?>
                    {
                        ["layer"] = g.Key,
                        ["kind"] = first.Kind,
                        ["type"] = first.TypeName,
                        ["diameter_mm"] = first.DiameterMm,
                        ["width_mm"] = first.WidthMm,
                        ["height_mm"] = first.HeightMm,
                        ["run_height_mm"] = first.RunHeightMm,
                        ["runs"] = g.Count(),
                        ["will_route"] = g.Count(r => !r.Excluded),
                        ["length_m"] = Math.Round(g.Sum(r => r.LengthMm) / 1000, 1),
                    };
                })
                .ToList();

            var flagged = runs
                .SelectMany(r => r.Flags.Select(flag => (Flag: flag, Run: r)))
                .GroupBy(x => x.Flag, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (object?)new Dictionary<string, object?>
                {
                    ["flag"] = g.Key,
                    ["default"] = CadRunPlan.FlagExcludes.TryGetValue(g.Key, out bool excludes) && excludes ? "excluded" : "kept",
                    ["count"] = g.Count(),
                    ["runs"] = g.Take(MaxFlaggedItems).Select(x => (object?)x.Run.Index).ToList(),
                    ["truncated"] = g.Count() > MaxFlaggedItems,
                })
                .ToList();

            return new Dictionary<string, object?>
            {
                ["status"] = status,
                ["runs"] = runs.Count,
                ["will_route"] = runs.Count(r => !r.Excluded),
                ["total_length_m"] = Math.Round(runs.Sum(r => r.LengthMm) / 1000, 1),
                ["drops"] = runs.Sum(r => r.Drops),
                ["open_ends"] = conduit.Count(r => r.Start.Kind == "open") + conduit.Count(r => r.End.Kind == "open"),
                ["junctions"] = runs.Count(r => r.Start.Kind == "junction") + runs.Count(r => r.End.Kind == "junction"),
                ["by_layer"] = byLayer,
                ["flagged"] = flagged,
                ["unplanned_layers"] = unplanned
                    .OrderBy(u => u.Layer, StringComparer.Ordinal)
                    .Select(u => (object?)new Dictionary<string, object?>
                    {
                        ["layer"] = u.Layer,
                        ["reason"] = u.Reason,
                        ["pieces"] = u.Pieces,
                        ["length_m"] = Math.Round(u.LengthMm / 1000, 1),
                    })
                    .ToList(),
                ["dropped_short"] = tooShort,
                ["free_arcs"] = freeArcs,
                ["pieces_outside_footprint"] = piecesOutside,
            };
        }

        /// <summary>The headline's conduit sentence.</summary>
        private static string ConduitSentence(SocketPlan plan, Dictionary<string, object?> conduit)
        {
            string status = conduit.TryGetValue("status", out object? s) ? s as string ?? "" : "";
            if (status == "none") return "";
            List<PlannedRun> runs = plan.Runs;
            int needHeight = conduit.TryGetValue("unplanned_layers", out object? u) && u is List<object?> list
                ? list.Count(row => row is Dictionary<string, object?> d && d.TryGetValue("reason", out object? r) &&
                                    (r as string) == "no_run_height")
                : 0;

            string sentence = "";
            if (runs.Count > 0 || needHeight == 0)
            {
                int openEnds = conduit.TryGetValue("open_ends", out object? o) && o is int n ? n : 0;
                bool trays = runs.Any(r => r.Kind == "cable_tray"), conduits = runs.Any(r => r.Kind != "cable_tray");
                string label = trays ? (conduits ? "Conduit and trunking" : "Trunking") : "Conduit";
                sentence = " " + label + ": " + Count(runs.Count) + " runs, " +
                           Math.Round(runs.Sum(r => r.LengthMm) / 1000, 1).ToString("0.0", CultureInfo.InvariantCulture) + " m, " +
                           Count(runs.Sum(r => r.Drops)) + " drops, " + Count(openEnds) + " open ends.";
            }
            if (needHeight > 0)
            {
                sentence += (sentence.Length == 0 ? " Conduit: run" : " Run") + " height needed for " + Count(needHeight) +
                            " layer" + (needHeight == 1 ? "" : "s") + ".";
            }
            return sentence;
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
                    ["score_uncropped"] = Math.Round(a.ScoreUncropped, 3),
                    ["inside_share"] = Math.Round(a.InsideShare, 3),
                    ["model_coverage"] = Math.Round(a.ModelCoverage, 3),
                    ["outcome"] = a.Outcome,
                    ["method"] = a.Method,
                    ["pick"] = a.Pick >= 0 ? a.Pick : (int?)null,
                };

        private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Round2(double value) => Math.Round(value, 2).ToString("0.00", CultureInfo.InvariantCulture);
    }
}
