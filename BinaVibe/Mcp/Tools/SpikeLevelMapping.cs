// SpikeLevelMapping — measures what Revit ACTUALLY does when model elements
// are copied out of one document into another whose levels differ.
//
// Why this exists: the exemplar ("seed-and-mutate") plan depends on opening a
// JKR standard .rvt and copying its walls/floors/roofs into the drafter's open
// project. LoadFromRvtContainer already does the cross-document copy for TYPE
// elements with a null transform, and that works in production. Nobody knows
// what happens to LEVEL-HOSTED MODEL elements when the target document has
// different levels — the API docs are silent, and the community answer
// ("set Level and Offset after the copy") describes same-document copies.
//
// Three outcomes are possible and they lead to three different designs:
//   A. Revit copies the source levels in as new Level elements  -> we must
//      dedupe levels by elevation afterwards, or the target grows a level per
//      exemplar.
//   B. Revit maps to an existing target level (by elevation? by name?)   -> we
//      must pre-create matching levels before copying, and the match rule is
//      the thing to discover.
//   C. Revit refuses                                            -> exemplar
//      elements must be rebuilt rather than copied, and the whole plan changes.
//
// This tool does not guess. It records the before/after state and reports it.
//
// NON-DESTRUCTIVE BY DEFAULT: the transaction rolls back unless commit=true.
// A rolled-back transaction still lets us read everything the copy produced,
// because the reads happen before the rollback.
//
// Delete this file once the answer is recorded in the exemplar design doc.
// It is a measuring instrument, not a feature.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BinaVibe.Mcp.Tools
{
    /// <summary>Answers "use the destination type" so the copy proceeds instead
    /// of blocking on a modal dialog. Records nothing — the caller compares
    /// type names before and after to see what was absorbed.</summary>
    internal sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            => DuplicateTypeAction.UseDestinationTypes;
    }

    internal sealed class AbortOnDuplicateTypes : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
            => DuplicateTypeAction.Abort;
    }

    internal static class SpikeLevelMapping
    {
        private const double FT = 304.8;

        /// <summary>
        /// args: {
        ///   source_path:      string   — full path to the exemplar .rvt
        ///   source_level?:    string   — level whose walls to copy; default = lowest level carrying walls
        ///   dx_mm?, dy_mm?:   double   — translation applied to the copy (default 0,0)
        ///   duplicate_types?: "destination" | "abort"   (default "destination")
        ///   commit?:          bool     — keep the result (default false = roll back)
        ///   include_hosted?:  bool     — also copy doors/windows hosted in those walls (default true)
        /// }
        /// </summary>
        public static Dictionary<string, object?> Run(UIApplication app, JsonElement args)
        {
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("no active document");

            var sourcePath = ArgsHelp.GetString(args, "source_path")
                ?? throw new ArgumentException("missing source_path");
            if (!File.Exists(sourcePath))
                throw new ArgumentException($"source_path not found: {sourcePath}");

            var wantLevel = ArgsHelp.GetString(args, "source_level");
            var dx = (ArgsHelp.GetDouble(args, "dx_mm") ?? 0) / FT;
            var dy = (ArgsHelp.GetDouble(args, "dy_mm") ?? 0) / FT;
            var abortOnDup = string.Equals(ArgsHelp.GetString(args, "duplicate_types"),
                                           "abort", StringComparison.OrdinalIgnoreCase);
            var commit = ArgsHelp.GetBool(args, "commit") ?? false;
            var includeHosted = ArgsHelp.GetBool(args, "include_hosted") ?? true;

            var report = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["source_file"] = Path.GetFileName(sourcePath),
                ["committed"] = false,
                ["duplicate_type_policy"] = abortOnDup ? "abort" : "destination",
            };

            // ── Target state BEFORE ────────────────────────────────────────
            report["target_levels_before"] = LevelsOf(doc);
            var levelIdsBefore = new HashSet<long>(
                new FilteredElementCollector(doc).OfClass(typeof(Level))
                    .Select(e => (long)e.Id.Value));
            var wallTypesBefore = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(WallType))
                    .Cast<WallType>().Select(t => t.Name));

            Document? sourceDoc = null;
            try
            {
                sourceDoc = app.Application.OpenDocumentFile(sourcePath);
                report["source_levels"] = LevelsOf(sourceDoc);

                // ── Choose the element set ─────────────────────────────────
                var walls = new FilteredElementCollector(sourceDoc)
                    .OfClass(typeof(Wall)).Cast<Wall>()
                    .Where(w => w.LevelId != ElementId.InvalidElementId)
                    .ToList();
                if (walls.Count == 0)
                    throw new InvalidOperationException("exemplar has no level-hosted walls to copy");

                Level? pick = null;
                if (!string.IsNullOrWhiteSpace(wantLevel))
                {
                    pick = new FilteredElementCollector(sourceDoc).OfClass(typeof(Level))
                        .Cast<Level>().FirstOrDefault(l =>
                            string.Equals(l.Name, wantLevel, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"source has no level '{wantLevel}'");
                }
                else
                {
                    // Lowest level that actually carries walls — an exemplar's
                    // topmost level is often empty and would copy nothing.
                    pick = walls
                        .Select(w => sourceDoc.GetElement(w.LevelId) as Level)
                        .Where(l => l != null).Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .FirstOrDefault();
                }
                if (pick == null) throw new InvalidOperationException("could not resolve a source level");

                report["source_level_used"] = new Dictionary<string, object?>
                {
                    ["name"] = pick.Name,
                    ["elevation_mm"] = Math.Round(pick.Elevation * FT, 1),
                };

                var picked = walls.Where(w => w.LevelId == pick.Id).ToList();
                var ids = picked.Select(w => w.Id).ToList();

                if (includeHosted)
                {
                    // Hosted openings are the second unknown: a door copied
                    // without its host, or with a host that lands on a
                    // different level, is where a silent corruption would hide.
                    var hostIds = new HashSet<ElementId>(ids);
                    var hosted = new FilteredElementCollector(sourceDoc)
                        .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .Where(fi => fi.Host != null && hostIds.Contains(fi.Host.Id))
                        .Select(fi => fi.Id).ToList();
                    ids.AddRange(hosted);
                    report["hosted_included"] = hosted.Count;
                }

                report["source_elements_copied"] = ids.Count;
                report["source_walls_before_copy"] = picked.Select(w => WallFacts(sourceDoc, w)).ToList();

                // ── The measurement ────────────────────────────────────────
                var opts = new CopyPasteOptions();
                opts.SetDuplicateTypeNamesHandler(
                    abortOnDup ? (IDuplicateTypeNamesHandler)new AbortOnDuplicateTypes()
                               : new UseDestinationTypes());
                var transform = Transform.CreateTranslation(new XYZ(dx, dy, 0));

                using var tx = new Transaction(doc, "BINA spike: cross-doc level mapping");
                TxGuard.StartSwallowing(tx);
                try
                {
                    var newIds = ElementTransformUtils.CopyElements(
                        sourceDoc, ids, doc, transform, opts);
                    doc.Regenerate();

                    report["copied_count"] = newIds.Count;

                    // Q: did NEW levels appear in the target?
                    var levelsAfter = new FilteredElementCollector(doc).OfClass(typeof(Level))
                        .Cast<Level>().ToList();
                    var added = levelsAfter.Where(l => !levelIdsBefore.Contains((long)l.Id.Value))
                        .Select(l => new Dictionary<string, object?>
                        {
                            ["name"] = l.Name,
                            ["elevation_mm"] = Math.Round(l.Elevation * FT, 1),
                        }).ToList();
                    report["levels_created_by_copy"] = added;
                    report["target_levels_after"] = LevelsOf(doc);

                    // Q: what level did each copied wall land on, and did its
                    // top constraint survive or fall back to a fixed height?
                    // CopyElements is documented to return ids of elements that
                    // may since have been deleted (hosted elements especially),
                    // so every lookup here must tolerate a null. OfType<T>()
                    // drops nulls; the count difference is itself a finding.
                    var resolved = newIds.Select(id => doc.GetElement(id)).ToList();
                    report["returned_ids_that_no_longer_resolve"] =
                        resolved.Count(e => e == null);

                    var copiedWalls = resolved
                        .OfType<Wall>()
                        .Select(w => WallFacts(doc, w))
                        .ToList();
                    report["copied_walls_after"] = copiedWalls;

                    // Q: which wall types were absorbed vs newly created?
                    var wallTypesAfter = new FilteredElementCollector(doc).OfClass(typeof(WallType))
                        .Cast<WallType>().Select(t => t.Name).ToList();
                    report["wall_types_added"] = wallTypesAfter.Where(n => !wallTypesBefore.Contains(n)).ToList();

                    // Q: did hosted openings survive the trip?
                    var copiedHosted = resolved
                        .OfType<FamilyInstance>()
                        .Select(fi => new Dictionary<string, object?>
                        {
                            ["id"] = (long)fi.Id.Value,
                            ["symbol"] = fi.Symbol?.Name,
                            ["has_host"] = fi.Host != null,
                            ["host_id"] = fi.Host != null ? (object)(long)fi.Host.Id.Value : null,
                        }).ToList();
                    report["copied_hosted_after"] = copiedHosted;
                    report["hosted_orphaned"] = copiedHosted.Count(h => !(bool)h["has_host"]!);

                    report["ok"] = true;
                    report["verdict"] = Verdict(added.Count, copiedWalls);

                    if (commit) { TxGuard.CommitOrThrow(tx); report["committed"] = true; }
                    else { tx.RollBack(); }
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    report["ok"] = false;
                    report["verdict"] = "REFUSED — copy threw; exemplar elements cannot be copied as-is";
                    report["error"] = $"{ex.GetType().Name}: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                report["ok"] = false;
                report["error"] = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (sourceDoc != null) { try { sourceDoc.Close(false); } catch { } }
            }

            return report;
        }

        /// <summary>The one-line answer, so the result is readable without
        /// cross-referencing three arrays.</summary>
        private static string Verdict(int levelsCreated, List<Dictionary<string, object?>> walls)
        {
            if (walls.Count == 0) return "NO WALLS LANDED — nothing to conclude; check the element set";
            var lost = walls.Count(w => string.Equals(w["top_constraint"] as string, "Unconnected",
                                                      StringComparison.OrdinalIgnoreCase));
            var levelPart = levelsCreated > 0
                ? $"A — Revit created {levelsCreated} new level(s); dedupe by elevation is required"
                : "B — no new levels; walls were mapped onto existing target levels";
            var topPart = lost > 0
                ? $" ⚠ {lost}/{walls.Count} walls lost their top constraint (now unconnected height) — level edits will NOT propagate"
                : " top constraints survived";
            return levelPart + "." + topPart;
        }

        private static List<Dictionary<string, object?>> LevelsOf(Document d) =>
            new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new Dictionary<string, object?>
                {
                    ["name"] = l.Name,
                    ["elevation_mm"] = Math.Round(l.Elevation * FT, 1),
                }).ToList();

        /// <summary>Base level, top constraint and height of one wall — the
        /// facts that decide whether a copied exemplar is still editable.
        /// Top constraint matters because the modeling recipes require walls
        /// constrained to the level above; an unconnected-height wall ignores
        /// level moves, so a copy that silently unconstrains them breaks
        /// modify_level_stack for every seeded building.</summary>
        private static Dictionary<string, object?> WallFacts(Document d, Wall w)
        {
            string LevelName(BuiltInParameter bip)
            {
                var p = w.get_Parameter(bip);
                if (p == null) return "(none)";
                var id = p.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "Unconnected";
                return (d.GetElement(id) as Level)?.Name ?? $"(id {(long)id.Value})";
            }

            // Phase is recorded because CopyElements is documented to rewrite
            // PhaseCreated/PhaseDemolished on the copies. An exemplar that
            // lands in "Existing" instead of "New Construction" is invisible in
            // the drafter's default view and wrong in every schedule — a
            // failure that looks like "the copy did nothing".
            string PhaseName(BuiltInParameter bip)
            {
                var p = w.get_Parameter(bip);
                var id = p?.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return "(none)";
                return d.GetElement(id)?.Name ?? $"(id {(long)id.Value})";
            }

            return new Dictionary<string, object?>
            {
                ["id"] = (long)w.Id.Value,
                ["type"] = w.WallType?.Name,
                ["base_level"] = LevelName(BuiltInParameter.WALL_BASE_CONSTRAINT),
                ["top_constraint"] = LevelName(BuiltInParameter.WALL_HEIGHT_TYPE),
                ["phase_created"] = PhaseName(BuiltInParameter.PHASE_CREATED),
                ["phase_demolished"] = PhaseName(BuiltInParameter.PHASE_DEMOLISHED),
                ["base_offset_mm"] = Math.Round(
                    (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) * FT, 1),
                ["unconnected_height_mm"] = Math.Round(
                    (w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0) * FT, 1),
            };
        }
    }
}
